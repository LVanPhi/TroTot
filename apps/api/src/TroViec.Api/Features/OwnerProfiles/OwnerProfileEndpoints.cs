using System.Security.Cryptography;
using Dapper;
using FluentValidation;
using Microsoft.Extensions.Options;
using Npgsql;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Crypto;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Api.Infrastructure.Storage;

namespace TroViec.Api.Features.OwnerProfiles;

/// <summary>/api/v1/me/owner-profile and /me/owner-documents (US-501, US-502, US-504; docs/04-api.md).</summary>
public static partial class OwnerProfileEndpoints
{
    public const int MaxDocumentBytes = 10 * 1024 * 1024;

    public sealed record ProfileRequest(string OwnerType, string? BusinessName, string? TaxCode);

    public sealed record ProfileResponse(string OwnerType, string? BusinessName, string? TaxCode, DateTime? VerifiedUntil);

    public sealed record DocumentItem(long Id, string DocType, string Status, string MimeType, int ByteSize,
        DateTime CreatedAt, DateTime? ReviewedAt, DateTime? ErasedAt);

    public sealed record CreatedDocument(long Id);

    public static readonly string[] DocTypes = ["id_card", "business_registration", "property_proof", "authorization_letter", "other"];

    public sealed partial class ProfileValidator : AbstractValidator<ProfileRequest>
    {
        public ProfileValidator()
        {
            RuleFor(r => r.OwnerType).Must(t => t is "individual" or "business")
                .WithMessage("Chọn cá nhân hoặc doanh nghiệp");
            RuleFor(r => r.BusinessName).MaximumLength(200)
                .Must(n => n is null || n.Trim().Length > 0).WithMessage("Tên doanh nghiệp không được để trống");
            RuleFor(r => r.BusinessName).NotEmpty().When(r => r.OwnerType == "business")
                .WithMessage("Doanh nghiệp cần tên");
            RuleFor(r => r.TaxCode).NotEmpty().When(r => r.OwnerType == "business")
                .WithMessage("Doanh nghiệp cần mã số thuế");
            RuleFor(r => r.TaxCode).Matches(TaxCode()).When(r => r.TaxCode is not null)
                .WithMessage("Mã số thuế gồm 10 số hoặc 10-3 số");
        }

        [System.Text.RegularExpressions.GeneratedRegex("^[0-9]{10}(-[0-9]{3})?$")]
        private static partial System.Text.RegularExpressions.Regex TaxCode();
    }

    public static void MapOwnerProfiles(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me").WithTags("Me").RequireAuthorization(Permissions.ListingCreate);
        me.MapGet("/owner-profile", GetProfileAsync).Produces<ProfileResponse>();
        me.MapPut("/owner-profile", PutProfileAsync).Validate<ProfileRequest>().Produces<ProfileResponse>();
        me.MapGet("/owner-documents", ListDocumentsAsync).Produces<List<DocumentItem>>();
        me.MapPost("/owner-documents", UploadDocumentAsync)
            .DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxDocumentBytes + 64 * 1024))
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<CreatedDocument>(StatusCodes.Status201Created);
        me.MapDelete("/owner-documents/{id:long}", DeleteDocumentAsync);
    }

    public static async Task<IResult> GetProfileAsync(HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var profile = await ProfileAsync(connection, CurrentUser.Id(http.User), null, ct);
        return profile is null ? throw new ProblemException("resource.not_found") : Results.Ok(profile);
    }

    /// <summary>
    /// Create or update. Changing the type, business name or tax code clears a verification, because the moderator
    /// verified the previous identity (assumption recorded in docs/04-api.md).
    /// </summary>
    public static async Task<IResult> PutProfileAsync(ProfileRequest request, HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        var userId = CurrentUser.Id(http.User);
        var business = request.OwnerType == "business";
        await using var connection = await db.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO app.owner_profiles (user_id, owner_type, business_name, tax_code)
            VALUES (@UserId, @OwnerType::app.owner_type, @BusinessName, @TaxCode)
            ON CONFLICT (user_id) DO UPDATE SET
                owner_type = EXCLUDED.owner_type,
                business_name = EXCLUDED.business_name,
                tax_code = EXCLUDED.tax_code,
                verified_until = CASE WHEN (owner_profiles.owner_type, owner_profiles.business_name, owner_profiles.tax_code)
                                           IS NOT DISTINCT FROM (EXCLUDED.owner_type, EXCLUDED.business_name, EXCLUDED.tax_code)
                                      THEN owner_profiles.verified_until END,
                verifier_id = CASE WHEN (owner_profiles.owner_type, owner_profiles.business_name, owner_profiles.tax_code)
                                        IS NOT DISTINCT FROM (EXCLUDED.owner_type, EXCLUDED.business_name, EXCLUDED.tax_code)
                                   THEN owner_profiles.verifier_id END
            """, new
            {
                UserId = userId,
                request.OwnerType,
                BusinessName = business ? request.BusinessName!.Trim() : NullIfBlank(request.BusinessName),
                TaxCode = NullIfBlank(request.TaxCode),
            }, cancellationToken: ct));
        return Results.Ok(await ProfileAsync(connection, userId, null, ct));
    }

    public static async Task<IResult> ListDocumentsAsync(HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return Results.Ok(await DocumentsAsync(connection, CurrentUser.Id(http.User), ct));
    }

    /// <summary>US-502: consent, type by content, ≤ 10 MB plain, duplicate by SHA-256, AES-256-GCM before storing.</summary>
    public static async Task<IResult> UploadDocumentAsync(
        HttpContext http, NpgsqlDataSource db, IObjectStorage storage, IOptions<StorageBuckets> buckets,
        DocumentCipher cipher, ILoggerFactory loggers, CancellationToken ct)
    {
        var userId = CurrentUser.Id(http.User);
        if (!http.Request.HasFormContentType) throw Validation("file", "Chọn tệp giấy tờ");
        IFormCollection form;
        try { form = await http.Request.ReadFormAsync(ct); }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { throw Validation("file", "Chọn tệp giấy tờ"); }

        if (!string.Equals(form["consent"].ToString(), "true", StringComparison.OrdinalIgnoreCase))
            throw new ProblemException("document.consent_required");
        var docType = form["docType"].ToString();
        if (!DocTypes.Contains(docType)) throw Validation("docType", "Chọn loại giấy tờ");
        if (form.Files.GetFile("file") is not { } file || file.Length == 0) throw Validation("file", "Chọn tệp giấy tờ");
        if (file.Length > MaxDocumentBytes) throw new ProblemException("document.too_large");

        var plain = new byte[file.Length];
        await using (var stream = file.OpenReadStream()) await stream.ReadExactlyAsync(plain, ct);
        var mime = DetectMime(plain) ?? throw new ProblemException("document.unsupported_type");
        var sha = SHA256.HashData(plain);

        await using var connection = await db.OpenConnectionAsync(ct);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM app.owner_profiles WHERE user_id = @Id)", new { Id = userId }, cancellationToken: ct)))
            throw new ProblemException("owner.profile_required");

        var holder = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT owner_id FROM app.owner_documents WHERE sha256 = @Sha AND erased_at IS NULL", new { Sha = sha }, cancellationToken: ct));
        if (holder is { } other)
        {
            if (other != userId)
                loggers.CreateLogger("OwnerDocuments").LogWarning(
                    "Owner document reused by another account: uploader {UserId}, holder {OtherUserId}", userId, other);
            throw new ProblemException("document.duplicate");
        }

        var bucket = buckets.Value.OwnerDocuments;
        var key = ObjectKeys.OwnerDocument(userId);
        await storage.PutAsync(bucket, key, cipher.Encrypt(plain), "application/octet-stream", ct);
        try
        {
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO app.owner_documents (owner_id, doc_type, object_key, sha256, mime_type, byte_size, key_id, consent_at)
                VALUES (@UserId, @DocType::app.document_type, @Key, @Sha, @Mime, @Size, @KeyId, now())
                RETURNING id
                """, new { UserId = userId, DocType = docType, Key = key, Sha = sha, Mime = mime, Size = plain.Length, KeyId = cipher.ActiveKeyId },
                cancellationToken: ct));
            return Results.Created($"/api/v1/me/owner-documents/{id}", new CreatedDocument(id));
        }
        catch
        {
            await storage.DeleteAsync(bucket, key, CancellationToken.None);
            throw;
        }
    }

    /// <summary>US-504: delete the file, keep the row with erased_at. Someone else's document → 404.</summary>
    public static async Task<IResult> DeleteDocumentAsync(
        long id, HttpContext http, NpgsqlDataSource db, IObjectStorage storage, IOptions<StorageBuckets> buckets, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var key = await connection.QuerySingleOrDefaultAsync<(long Id, string? ObjectKey)?>(new CommandDefinition(
            "SELECT id, object_key FROM app.owner_documents WHERE id = @Id AND owner_id = @UserId",
            new { Id = id, UserId = CurrentUser.Id(http.User) }, cancellationToken: ct))
            ?? throw new ProblemException("resource.not_found");
        await EraseAsync(connection, null, storage, buckets.Value.OwnerDocuments, [key], ct);
        return Results.NoContent();
    }

    /// <summary>Deletes the files, then marks the rows erased (also used by DELETE /me, US-106 AC3).</summary>
    public static async Task EraseAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, IObjectStorage storage, string bucket,
        IReadOnlyList<(long Id, string? ObjectKey)> documents, CancellationToken ct)
    {
        var live = documents.Where(d => d.ObjectKey is not null).ToList();
        foreach (var doc in live) await storage.DeleteAsync(bucket, doc.ObjectKey!, ct);
        if (live.Count == 0) return;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.owner_documents SET object_key = NULL, erased_at = now() WHERE id = ANY(@Ids) AND erased_at IS NULL",
            new { Ids = live.Select(d => d.Id).ToArray() }, tx, cancellationToken: ct));
    }

    public static async Task<ProfileResponse?> ProfileAsync(NpgsqlConnection connection, long userId, NpgsqlTransaction? tx, CancellationToken ct) =>
        await connection.QuerySingleOrDefaultAsync<ProfileResponse>(new CommandDefinition(
            """
            SELECT owner_type::text AS OwnerType, business_name AS BusinessName, tax_code AS TaxCode, verified_until AS VerifiedUntil
            FROM app.owner_profiles WHERE user_id = @UserId
            """, new { UserId = userId }, tx, cancellationToken: ct));

    public static async Task<List<DocumentItem>> DocumentsAsync(NpgsqlConnection connection, long ownerId, CancellationToken ct) =>
        (await connection.QueryAsync<DocumentItem>(new CommandDefinition(
            """
            SELECT id AS Id, doc_type::text AS DocType, status::text AS Status, mime_type AS MimeType, byte_size AS ByteSize,
                   created_at AS CreatedAt, reviewed_at AS ReviewedAt, erased_at AS ErasedAt
            FROM app.owner_documents WHERE owner_id = @OwnerId ORDER BY id
            """, new { OwnerId = ownerId }, cancellationToken: ct))).ToList();

    /// <summary>Type from the file's first bytes, never from its name or the client's Content-Type.</summary>
    public static string? DetectMime(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (b.Length >= 5 && b[..5].SequenceEqual("%PDF-"u8)) return "application/pdf";
        return null;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static ProblemException Validation(string field, string message) =>
        new("validation.failed", errors: new Dictionary<string, string[]> { [field] = [message] });
}
