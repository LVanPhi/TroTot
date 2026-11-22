using Dapper;
using FluentValidation;
using Npgsql;
using TroViec.Api.Features.Auth;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Security;

namespace TroViec.Api.Features.Me;

/// <summary>/api/v1/me: the caller's own account. RequireAuthorization() without a permission (AGENTS.md rule 11).</summary>
public static class MeEndpoints
{
    public sealed record UpdateRequest(string FullName);

    public sealed record DeleteRequest(string Password);

    public sealed class UpdateValidator : AbstractValidator<UpdateRequest>
    {
        public UpdateValidator() => RuleFor(r => r.FullName).NotEmpty().MaximumLength(100)
            .Must(n => n.Trim().Length > 0).WithMessage("Họ tên từ 1 đến 100 ký tự");
    }

    public sealed class DeleteValidator : AbstractValidator<DeleteRequest>
    {
        public DeleteValidator() => RuleFor(r => r.Password).NotEmpty().MaximumLength(256);
    }

    public static void MapMe(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me").WithTags("Me").RequireAuthorization();
        me.MapGet("/", GetAsync).Produces<MeResponse>();
        me.MapPatch("/", UpdateAsync).Validate<UpdateRequest>().Produces<MeResponse>();
        me.MapDelete("/", DeleteAsync).Validate<DeleteRequest>();
    }

    public static async Task<IResult> GetAsync(HttpContext http, NpgsqlDataSource db, StudentEmailDomains studentDomains, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var me = await MeQueries.GetAsync(connection, CurrentUser.Id(http.User), studentDomains, ct)
            ?? throw new ProblemException("auth.unauthenticated");
        return Results.Ok(me);
    }

    public static async Task<IResult> UpdateAsync(
        UpdateRequest request, HttpContext http, NpgsqlDataSource db, StudentEmailDomains studentDomains, CancellationToken ct)
    {
        var userId = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.users SET full_name = @FullName WHERE id = @Id AND status = 'active'",
            new { FullName = request.FullName.Trim(), Id = userId }, cancellationToken: ct));
        return Results.Ok(await MeQueries.GetAsync(connection, userId, studentDomains, ct));
    }

    /// <summary>
    /// US-106 AC1: confirm with the password, then anonymize in one transaction and end every session.
    /// AC4 holds because nothing is deleted: user_roles and moderation history keep pointing at this row.
    /// </summary>
    public static async Task<IResult> DeleteAsync(
        [Microsoft.AspNetCore.Mvc.FromBody] DeleteRequest request, HttpContext http, NpgsqlDataSource db, RefreshTokenStore refreshTokens,
        UserVersionStore versions, Infrastructure.Storage.IObjectStorage storage,
        Microsoft.Extensions.Options.IOptions<Infrastructure.Storage.StorageBuckets> buckets, CancellationToken ct)
    {
        var userId = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        var user = await AuthUsers.ByIdAsync(connection, userId, ct);
        if (user is not { Status: "active" } || !PasswordHasher.Verify(request.Password, user.PasswordHash))
            throw new ProblemException("auth.invalid_credentials");

        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            // TODO(T3.3): US-106 AC2 — move every listing not yet removed to 'removed' through ListingTransitionService
            // ("Người dùng xóa tài khoản"), and answer 409 me.listing_in_screening while one is still screening.
            // US-106 AC3: delete owner document files, keep the rows with object_key NULL + erased_at.
            var documents = (await connection.QueryAsync<(long Id, string? ObjectKey)>(new CommandDefinition(
                "SELECT id, object_key FROM app.owner_documents WHERE owner_id = @Id AND erased_at IS NULL",
                new { Id = userId }, tx, cancellationToken: ct))).ToList();
            await OwnerProfiles.OwnerProfileEndpoints.EraseAsync(connection, tx, storage, buckets.Value.OwnerDocuments, documents, ct);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE app.users
                SET email = NULL, email_verified_at = NULL,
                    phone_e164 = NULL, phone_verified_at = NULL,
                    password_hash = '!', full_name = 'Người dùng đã xóa',
                    status = 'anonymized', anonymized_at = now()
                WHERE id = @Id
                """, new { Id = userId }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }

        await refreshTokens.RevokeAllAsync(userId);
        await versions.BumpAsync(userId);
        AuthEndpoints.ClearRefreshCookie(http.Response);
        return Results.NoContent();
    }
}
