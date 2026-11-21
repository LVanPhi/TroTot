using Dapper;
using Npgsql;
using TroViec.Api.Infrastructure.Auth;

namespace TroViec.Api.Features.Me;

public sealed record PostingStatus(bool Allowed, IReadOnlyList<string> Missing);

/// <summary>GET /me shape (docs/04-api.md). Also returned as "user" by POST /auth/login.</summary>
public sealed record MeResponse(
    string Email,
    string FullName,
    string? Phone,
    bool EmailVerified,
    bool PhoneVerified,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    PostingStatus Posting);

public static class MeQueries
{
    private sealed record UserRow(
        string Email, string FullName, string? PhoneE164, DateTime? EmailVerifiedAt, DateTime? PhoneVerifiedAt,
        bool OwnerVerified);

    public static async Task<IReadOnlyList<string>> PermissionsAsync(NpgsqlConnection connection, long userId, CancellationToken ct) =>
        (await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT DISTINCT p.code
            FROM app.user_roles ur
            JOIN app.role_permissions rp ON rp.role_id = ur.role_id
            JOIN app.permissions p ON p.id = rp.permission_id
            WHERE ur.user_id = @UserId
            ORDER BY p.code
            """, new { UserId = userId }, cancellationToken: ct))).ToList();

    public static async Task<MeResponse?> GetAsync(
        NpgsqlConnection connection, long userId, StudentEmailDomains studentDomains, CancellationToken ct)
    {
        var user = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            """
            SELECT u.email, u.full_name, u.phone_e164, u.email_verified_at, u.phone_verified_at,
                   EXISTS (SELECT 1 FROM app.owner_profiles op
                           WHERE op.user_id = u.id AND op.verified_until > now()) AS owner_verified
            FROM app.users u
            WHERE u.id = @UserId AND u.status <> 'anonymized'
            """, new { UserId = userId }, cancellationToken: ct));
        if (user is null) return null;

        var roles = (await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT r.code FROM app.user_roles ur JOIN app.roles r ON r.id = ur.role_id
            WHERE ur.user_id = @UserId ORDER BY r.code
            """, new { UserId = userId }, cancellationToken: ct))).ToList();
        var permissions = await PermissionsAsync(connection, userId, ct);

        return new MeResponse(
            user.Email, user.FullName, user.PhoneE164,
            user.EmailVerifiedAt is not null, user.PhoneVerifiedAt is not null,
            roles, permissions,
            Posting(user, studentDomains));
    }

    /// <summary>
    /// BR-02, by data not by role name: verified phone AND (verified school email OR owner profile verified now).
    /// </summary>
    private static PostingStatus Posting(UserRow user, StudentEmailDomains studentDomains)
    {
        var missing = new List<string>();
        if (user.PhoneVerifiedAt is null) missing.Add("phone_verification");

        var schoolEmail = studentDomains.Contains(user.Email);
        var identityOk = (schoolEmail && user.EmailVerifiedAt is not null) || user.OwnerVerified;
        if (!identityOk) missing.Add(schoolEmail ? "email_verification" : "owner_verification");

        return new PostingStatus(missing.Count == 0, missing);
    }
}
