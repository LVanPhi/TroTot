using Dapper;
using FluentValidation;
using Npgsql;
using TroViec.Api.Domain;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Admin;

/// <summary>
/// US-801 and US-104 AC6: /api/v1/admin/users, permission user.manage. The system account is never listed or
/// changed; anonymized accounts are listed but not changed. Logs carry ids only (AGENTS.md rule 13).
/// </summary>
public static class AdminUsers
{
    public sealed record UserItem(
        long Id, string? Email, string FullName, string? Phone, string Status,
        bool EmailVerified, bool PhoneVerified, DateTime CreatedAt, string[] Roles);

    public sealed record RoleRequest(string RoleCode);

    public sealed record PhoneRequest(string Phone);

    public sealed class RoleValidator : AbstractValidator<RoleRequest>
    {
        public RoleValidator() => RuleFor(r => r.RoleCode).NotEmpty().MaximumLength(30);
    }

    public sealed class PhoneValidator : AbstractValidator<PhoneRequest>
    {
        public PhoneValidator() => RuleFor(r => r.Phone)
            .Must(p => PhoneNumber.TryParse(p, out _)).WithMessage("Số điện thoại di động Việt Nam không hợp lệ");
    }

    public static void MapAdminUsers(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/v1/admin/users").WithTags("Admin").RequireAuthorization(Permissions.UserManage);
        users.MapGet("/", SearchAsync).Produces<Paged<UserItem>>();
        users.MapPost("/{id:long}/roles", AddRoleAsync).Validate<RoleRequest>();
        users.MapDelete("/{id:long}/roles/{roleCode}", RemoveRoleAsync);
        users.MapPost("/{id:long}/lock", LockAsync);
        users.MapPost("/{id:long}/unlock", UnlockAsync);
        users.MapPost("/{id:long}/phone", VerifyPhoneAsync).Validate<PhoneRequest>();
    }

    public static async Task<IResult> SearchAsync(
        string? q, string? role, int? page, int? pageSize, NpgsqlDataSource db, CancellationToken ct)
    {
        var (p, size, offset) = Paging.Normalize(page, pageSize);
        var pattern = string.IsNullOrWhiteSpace(q) ? null : Paging.LikeContains(q.Trim());
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<(long Id, string? Email, string FullName, string? Phone, string Status,
            bool EmailVerified, bool PhoneVerified, DateTime CreatedAt, string[] Roles, long Total)>(new CommandDefinition(
            """
            SELECT u.id, u.email, u.full_name, u.phone_e164, u.status::text,
                   u.email_verified_at IS NOT NULL, u.phone_verified_at IS NOT NULL, u.created_at,
                   COALESCE(array_agg(r.code ORDER BY r.code) FILTER (WHERE r.code IS NOT NULL), '{}') AS roles,
                   count(*) OVER () AS total
            FROM app.users u
            LEFT JOIN app.user_roles ur ON ur.user_id = u.id
            LEFT JOIN app.roles r ON r.id = ur.role_id
            WHERE u.id <> app.system_user_id()
              AND (@Pattern::text IS NULL OR u.email ILIKE @Pattern OR u.full_name ILIKE @Pattern)
              AND (@Role::text IS NULL OR EXISTS (
                    SELECT 1 FROM app.user_roles ur2 JOIN app.roles r2 ON r2.id = ur2.role_id
                    WHERE ur2.user_id = u.id AND r2.code = @Role))
            GROUP BY u.id
            ORDER BY u.created_at DESC, u.id DESC
            LIMIT @PageSize OFFSET @Offset
            """,
            new { Pattern = pattern, Role = string.IsNullOrWhiteSpace(role) ? null : role, PageSize = size, Offset = offset },
            cancellationToken: ct))).ToList();

        var items = rows.Select(r => new UserItem(r.Id, r.Email, r.FullName, r.Phone, r.Status,
            r.EmailVerified, r.PhoneVerified, r.CreatedAt, r.Roles)).ToList();
        return Results.Ok(new Paged<UserItem>(items, p, size, rows.Count > 0 ? rows[0].Total : 0));
    }

    /// <summary>Granting only bumps uver: the next refresh issues a token carrying the new permissions.</summary>
    public static async Task<IResult> AddRoleAsync(
        long id, RoleRequest request, HttpContext http, NpgsqlDataSource db, UserVersionStore versions,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var adminId = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        await RequireManageableAsync(connection, id, ct);
        var roleId = await RoleIdAsync(connection, request.RoleCode, ct);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO app.user_roles (user_id, role_id, granter_id) VALUES (@UserId, @RoleId, @AdminId)
            ON CONFLICT (user_id, role_id) DO NOTHING
            """, new { UserId = id, RoleId = roleId, AdminId = adminId }, cancellationToken: ct));
        await versions.BumpAsync(id);
        Log(loggers, "Role granted", adminId, id);
        return Results.NoContent();
    }

    public static async Task<IResult> RemoveRoleAsync(
        long id, string roleCode, HttpContext http, NpgsqlDataSource db, UserVersionStore versions,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var adminId = CurrentUser.Id(http.User);
        if (id == adminId && roleCode == "admin") throw new ProblemException("admin.cannot_remove_own_admin");

        await using var connection = await db.OpenConnectionAsync(ct);
        await RequireManageableAsync(connection, id, ct);
        var roleId = await RoleIdAsync(connection, roleCode, ct);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM app.user_roles WHERE user_id = @UserId AND role_id = @RoleId",
            new { UserId = id, RoleId = roleId }, cancellationToken: ct));
        await versions.BumpAsync(id);
        Log(loggers, "Role revoked", adminId, id);
        return Results.NoContent();
    }

    /// <summary>Locking ends every session at once: refresh families revoked and uver bumped.</summary>
    public static async Task<IResult> LockAsync(
        long id, HttpContext http, NpgsqlDataSource db, RefreshTokenStore refreshTokens, UserVersionStore versions,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var adminId = CurrentUser.Id(http.User);
        if (id == adminId) throw new ProblemException("admin.cannot_lock_self");

        await using var connection = await db.OpenConnectionAsync(ct);
        await RequireManageableAsync(connection, id, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.users SET status = 'locked' WHERE id = @Id AND status = 'active'", new { Id = id }, cancellationToken: ct));
        await refreshTokens.RevokeAllAsync(id);
        await versions.BumpAsync(id);
        Log(loggers, "User locked", adminId, id);
        return Results.NoContent();
    }

    public static async Task<IResult> UnlockAsync(
        long id, HttpContext http, NpgsqlDataSource db, Throttles throttles, ILoggerFactory loggers, CancellationToken ct)
    {
        var adminId = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        await RequireManageableAsync(connection, id, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.users SET status = 'active' WHERE id = @Id AND status = 'locked'", new { Id = id }, cancellationToken: ct));
        await throttles.ClearLoginFailuresAsync(id);
        Log(loggers, "User unlocked", adminId, id);
        return Results.NoContent();
    }

    /// <summary>US-104 AC6: manual verification after the admin has called or met the number's owner.</summary>
    public static async Task<IResult> VerifyPhoneAsync(
        long id, PhoneRequest request, HttpContext http, NpgsqlDataSource db, ILoggerFactory loggers, CancellationToken ct)
    {
        var adminId = CurrentUser.Id(http.User);
        PhoneNumber.TryParse(request.Phone, out var phone);
        await using var connection = await db.OpenConnectionAsync(ct);
        await RequireManageableAsync(connection, id, ct);

        var takenByOther = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM app.users WHERE phone_e164 = @Phone AND id <> @Id)",
            new { Phone = phone!.E164, Id = id }, cancellationToken: ct));
        if (takenByOther) throw new ProblemException("auth.phone_taken");

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.users SET phone_e164 = @Phone, phone_verified_at = now() WHERE id = @Id",
            new { Phone = phone.E164, Id = id }, cancellationToken: ct));
        Log(loggers, "Phone verified manually", adminId, id);
        return Results.NoContent();
    }

    /// <summary>404 for unknown ids, the system account and anonymized accounts (nothing to manage there).</summary>
    private static async Task RequireManageableAsync(NpgsqlConnection connection, long id, CancellationToken ct)
    {
        var manageable = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM app.users
                           WHERE id = @Id AND id <> app.system_user_id() AND status <> 'anonymized')
            """, new { Id = id }, cancellationToken: ct));
        if (!manageable) throw new ProblemException("resource.not_found");
    }

    private static async Task<short> RoleIdAsync(NpgsqlConnection connection, string roleCode, CancellationToken ct) =>
        await connection.QuerySingleOrDefaultAsync<short?>(new CommandDefinition(
            "SELECT id FROM app.roles WHERE code = @Code", new { Code = roleCode }, cancellationToken: ct))
        ?? throw new ProblemException("validation.failed",
            errors: new Dictionary<string, string[]> { ["roleCode"] = ["Vai trò không tồn tại"] });

    private static void Log(ILoggerFactory loggers, string action, long adminId, long userId) =>
        loggers.CreateLogger(nameof(AdminUsers)).LogInformation("{Action} by admin {AdminId} for user {UserId}", action, adminId, userId);
}
