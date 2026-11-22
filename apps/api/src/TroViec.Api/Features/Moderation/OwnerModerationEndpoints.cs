using System.Security.Cryptography;
using Dapper;
using FluentValidation;
using Microsoft.Extensions.Options;
using Npgsql;
using TroViec.Api.Features.OwnerProfiles;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Crypto;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Api.Infrastructure.Storage;

namespace TroViec.Api.Features.Moderation;

/// <summary>
/// Owner profiles and documents for moderators (US-503, US-605, BR-10, BR-11, BR-19; docs/04-api.md) and the
/// signed document link GET /files/documents/{token} (docs/02-architecture.md, Mở giấy tờ xác minh).
/// </summary>
public static class OwnerModerationEndpoints
{
    public sealed record ProfileListItem(long UserId, string FullName, string OwnerType, string? BusinessName,
        DateTime? VerifiedUntil, int PendingDocuments, int Documents);

    public sealed record ProfileDetail(long UserId, string FullName, string OwnerType, string? BusinessName, string? TaxCode,
        DateTime? VerifiedUntil, IReadOnlyList<OwnerProfileEndpoints.DocumentItem> Documents);

    public sealed record ReviewRequest(string Decision);

    public sealed record ReviewResponse(long Id, string Status);

    public sealed record AccessRequest(string Purpose);

    public sealed record AccessResponse(string Url, DateTimeOffset ExpiresAt);

    public sealed record VerifyResponse(DateTimeOffset VerifiedUntil);

    public sealed class ReviewValidator : AbstractValidator<ReviewRequest>
    {
        public ReviewValidator() => RuleFor(r => r.Decision).Must(d => d is "accepted" or "rejected")
            .WithMessage("Chọn chấp nhận hoặc từ chối");
    }

    public sealed class AccessValidator : AbstractValidator<AccessRequest>
    {
        public AccessValidator() => RuleFor(r => r.Purpose).NotEmpty().MaximumLength(200)
            .Must(p => p.Trim().Length > 0).WithMessage("Nhập mục đích mở giấy tờ (tối đa 200 ký tự)");
    }

    public static void MapOwnerModeration(this IEndpointRouteBuilder app)
    {
        var mod = app.MapGroup("/api/v1/moderation").WithTags("Moderation");
        mod.MapGet("/owner-profiles", ListAsync).RequireAuthorization(Permissions.OwnerVerify).Produces<Paged<ProfileListItem>>();
        mod.MapGet("/owner-profiles/{userId:long}", DetailAsync).RequireAuthorization(Permissions.OwnerVerify).Produces<ProfileDetail>();
        mod.MapPost("/owner-profiles/{userId:long}/verify", VerifyAsync).RequireAuthorization(Permissions.OwnerVerify)
            .Produces<VerifyResponse>();
        mod.MapPost("/owner-documents/{id:long}/review", ReviewAsync).RequireAuthorization(Permissions.OwnerVerify)
            .Validate<ReviewRequest>().Produces<ReviewResponse>();
        mod.MapPost("/owner-documents/{id:long}/access", AccessAsync).RequireAuthorization(Permissions.DocumentView)
            .Validate<AccessRequest>().Produces<AccessResponse>();

        // The token is the authorization (5 minutes, bound to the viewer); opened in a new tab, so no bearer header.
        app.MapGet("/api/v1/files/documents/{token}", OpenAsync).WithTags("Moderation").AllowAnonymous();
    }

    public static async Task<IResult> ListAsync(NpgsqlDataSource db, bool? pending, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size, offset) = Paging.Normalize(page, pageSize);
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<(long UserId, string FullName, string OwnerType, string? BusinessName,
            DateTime? VerifiedUntil, int Pending, int Documents, long Total)>(new CommandDefinition(
            """
            SELECT p.user_id, u.full_name, p.owner_type::text, p.business_name, p.verified_until,
                   count(d.id) FILTER (WHERE d.status = 'pending' AND d.erased_at IS NULL)::int,
                   count(d.id)::int,
                   count(*) OVER ()
            FROM app.owner_profiles p
            JOIN app.users u ON u.id = p.user_id
            LEFT JOIN app.owner_documents d ON d.owner_id = p.user_id
            WHERE u.status = 'active'
            GROUP BY p.user_id, u.full_name
            HAVING NOT @Pending OR count(d.id) FILTER (WHERE d.status = 'pending' AND d.erased_at IS NULL) > 0
            ORDER BY min(d.created_at) FILTER (WHERE d.status = 'pending' AND d.erased_at IS NULL) NULLS LAST, p.user_id
            LIMIT @Size OFFSET @Offset
            """, new { Pending = pending ?? false, Size = size, Offset = offset }, cancellationToken: ct))).ToList();
        var items = rows.Select(r => new ProfileListItem(r.UserId, r.FullName, r.OwnerType, r.BusinessName, r.VerifiedUntil,
            r.Pending, r.Documents)).ToList();
        return Results.Ok(new Paged<ProfileListItem>(items, p, size, rows.Count > 0 ? rows[0].Total : 0));
    }

    public static async Task<IResult> DetailAsync(long userId, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var head = await connection.QuerySingleOrDefaultAsync<(string FullName, string OwnerType, string? BusinessName,
            string? TaxCode, DateTime? VerifiedUntil)?>(new CommandDefinition(
            """
            SELECT u.full_name, p.owner_type::text, p.business_name, p.tax_code, p.verified_until
            FROM app.owner_profiles p JOIN app.users u ON u.id = p.user_id WHERE p.user_id = @UserId
            """, new { UserId = userId }, cancellationToken: ct)) ?? throw new ProblemException("resource.not_found");
        var docs = await OwnerProfileEndpoints.DocumentsAsync(connection, userId, ct);
        var h = head;
        return Results.Ok(new ProfileDetail(userId, h.FullName, h.OwnerType, h.BusinessName, h.TaxCode, h.VerifiedUntil, docs));
    }

    /// <summary>US-503: accept or reject one document; not one's own (BR-19); an erased file → 410.</summary>
    public static async Task<IResult> ReviewAsync(long id, ReviewRequest request, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        var moderator = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        var doc = await DocumentAsync(connection, id, ct);
        if (doc.OwnerId == moderator) throw new ProblemException("moderation.conflict_of_interest");
        if (doc.Erased) throw new ProblemException("document.erased");
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE app.owner_documents SET status = @Decision::app.document_status, reviewer_id = @Moderator, reviewed_at = now()
            WHERE id = @Id
            """, new { Id = id, request.Decision, Moderator = moderator }, cancellationToken: ct));
        return Results.Ok(new ReviewResponse(id, request.Decision));
    }

    /// <summary>US-605 / BR-10: purpose required; every link issued writes one document_access_logs row.</summary>
    public static async Task<IResult> AccessAsync(long id, AccessRequest request, HttpContext http, NpgsqlDataSource db,
        SignedUrlService links, CancellationToken ct)
    {
        var viewer = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        var doc = await DocumentAsync(connection, id, ct);
        if (doc.OwnerId == viewer) throw new ProblemException("moderation.conflict_of_interest");
        if (doc.Erased) throw new ProblemException("document.erased");
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO app.document_access_logs (owner_document_id, viewer_id, purpose) VALUES (@Id, @Viewer, @Purpose)",
            new { Id = id, Viewer = viewer, Purpose = request.Purpose.Trim() }, cancellationToken: ct));
        var (token, expiresAt) = links.Create(id, viewer);
        return Results.Ok(new AccessResponse($"/api/v1/files/documents/{token}", expiresAt));
    }

    /// <summary>BR-11: verified_until = now + 12 months; needs an accepted document still on file; not one's own.</summary>
    public static async Task<IResult> VerifyAsync(long userId, HttpContext http, NpgsqlDataSource db, TimeProvider clock, CancellationToken ct)
    {
        var moderator = CurrentUser.Id(http.User);
        if (userId == moderator) throw new ProblemException("moderation.conflict_of_interest");
        await using var connection = await db.OpenConnectionAsync(ct);
        var accepted = await connection.ExecuteScalarAsync<bool?>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM app.owner_documents d
                           WHERE d.owner_id = p.user_id AND d.status = 'accepted' AND d.erased_at IS NULL)
            FROM app.owner_profiles p WHERE p.user_id = @UserId
            """, new { UserId = userId }, cancellationToken: ct)) ?? throw new ProblemException("resource.not_found");
        if (!accepted) throw new ProblemException("owner.no_accepted_document");

        var until = clock.GetUtcNow().AddMonths(12);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.owner_profiles SET verified_until = @Until, verifier_id = @Moderator WHERE user_id = @UserId",
            new { Until = until.UtcDateTime, Moderator = moderator, UserId = userId }, cancellationToken: ct));
        return Results.Ok(new VerifyResponse(until));
    }

    /// <summary>Decrypts and returns the file: no-store, inline, nosniff. Bad or expired token → 403; erased → 410.</summary>
    public static async Task<IResult> OpenAsync(string token, HttpContext http, NpgsqlDataSource db, SignedUrlService links,
        IObjectStorage storage, IOptions<StorageBuckets> buckets, DocumentCipher cipher, CancellationToken ct)
    {
        var (documentId, _) = links.Validate(token);
        await using var connection = await db.OpenConnectionAsync(ct);
        var doc = await connection.QuerySingleOrDefaultAsync<(string? ObjectKey, string MimeType, string KeyId)?>(new CommandDefinition(
            "SELECT object_key, mime_type, key_id FROM app.owner_documents WHERE id = @Id",
            new { Id = documentId }, cancellationToken: ct)) ?? throw new ProblemException("document.token_invalid");
        if (doc.ObjectKey is null) throw new ProblemException("document.erased");
        var blob = await storage.GetAsync(buckets.Value.OwnerDocuments, doc.ObjectKey, ct)
            ?? throw new ProblemException("document.erased");

        byte[] plain;
        try { plain = cipher.Decrypt(doc.KeyId, blob); }
        catch (CryptographicException) { throw new InvalidOperationException($"owner document {documentId} failed to decrypt"); }

        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.ContentDisposition = "inline";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Bytes(plain, doc.MimeType);
    }

    private static async Task<(long OwnerId, bool Erased)> DocumentAsync(NpgsqlConnection connection, long id, CancellationToken ct) =>
        await connection.QuerySingleOrDefaultAsync<(long OwnerId, bool Erased)?>(new CommandDefinition(
            "SELECT owner_id, erased_at IS NOT NULL FROM app.owner_documents WHERE id = @Id", new { Id = id }, cancellationToken: ct))
        ?? throw new ProblemException("resource.not_found");
}
