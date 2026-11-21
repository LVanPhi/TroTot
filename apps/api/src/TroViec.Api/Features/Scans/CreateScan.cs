using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Messaging;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Api.Infrastructure.RateLimiting;
using TroViec.Api.Infrastructure.Storage;

namespace TroViec.Api.Features.Scans;

/// <summary>
/// US-201: POST /api/v1/scans (multipart). Guests or callers with scan.create. Images go through ImagePipeline into the
/// scan-images bucket; scans + scan_images + outbox_messages are written in one transaction; the quota is consumed
/// only once the scan exists (202). The AI result is not awaited.
/// </summary>
public static partial class CreateScan
{
    public const int MaxTextLength = 10_000;
    public const int MaxImages = 5;
    public const long MaxRequestBytes = 30L * 1024 * 1024;

    public sealed record Response(Guid ScanId, string Status);

    public static async Task<IResult> HandleAsync(
        HttpContext http,
        NpgsqlDataSource db,
        IObjectStorage storage,
        IOptions<StorageBuckets> buckets,
        QuotaService quota,
        OutboxSignal outbox,
        TimeProvider clock,
        CancellationToken ct)
    {
        long? userId = http.User.Identity?.IsAuthenticated == true ? CurrentUser.Id(http.User) : null;
        if (userId is not null && !http.User.HasClaim(TokenService.PermissionClaim, Permissions.ScanCreate))
            throw new ProblemException("auth.forbidden");

        var ip = AuthSetup.ClientIp(http);
        var status = await quota.GetAsync(userId, ip);
        if (status.Remaining <= 0)
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling((status.ResetAt - clock.GetUtcNow()).TotalSeconds));
            throw new ProblemException("scan.quota_exceeded",
                extensions: new Dictionary<string, object?> { ["resetAt"] = status.ResetAt },
                headers: new Dictionary<string, string> { ["Retry-After"] = retryAfter.ToString(CultureInfo.InvariantCulture) });
        }

        if (!http.Request.HasFormContentType) throw new ProblemException("scan.input_required");
        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // Empty or malformed multipart body: nothing usable was sent.
            throw new ProblemException("scan.input_required");
        }
        var input = Parse(form);
        await using var connection = await db.OpenConnectionAsync(ct);
        if (input.ClaimedWardCode is { } ward && !await IsCommuneAsync(connection, ward, ct))
            throw Validation("claimedWardCode", "Phường/xã không tồn tại");

        var processed = new List<ProcessedImage>();
        foreach (var file in input.Images)
        {
            await using var stream = file.OpenReadStream();
            processed.Add(await ImagePipeline.ProcessAsync(stream, ct));
        }

        var scanPublicId = Guid.NewGuid();
        var bucket = buckets.Value.ScanImages;
        var keys = processed.Select((_, i) => ObjectKeys.ScanImage(scanPublicId, i + 1)).ToList();
        for (var i = 0; i < processed.Count; i++)
            await storage.PutAsync(bucket, keys[i], processed[i].Webp, "image/webp", ct);

        try
        {
            await using var tx = await connection.BeginTransactionAsync(ct);
            var scanId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO app.scans (public_id, source, requester_id, input_text, claimed_rent_vnd, claimed_ward_code,
                                       input_sha256, expires_at)
                VALUES (@PublicId, 'quick_check', @UserId, @Text, @Rent, @Ward, @Sha, now() + interval '30 days')
                RETURNING id
                """,
                new
                {
                    PublicId = scanPublicId, UserId = userId, input.Text, Rent = input.ClaimedRentVnd,
                    Ward = input.ClaimedWardCode, Sha = InputSha256(input, processed),
                }, tx, cancellationToken: ct));
            for (var i = 0; i < processed.Count; i++)
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO app.scan_images (scan_id, image_no, object_key, sha256) VALUES (@ScanId, @No, @Key, @Sha)",
                    new { ScanId = scanId, No = (short)(i + 1), Key = keys[i], Sha = processed[i].Sha256 }, tx, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO app.outbox_messages (scan_id) VALUES (@ScanId)", new { ScanId = scanId }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            foreach (var key in keys) await storage.DeleteAsync(bucket, key, CancellationToken.None);
            throw;
        }

        outbox.Notify();
        await quota.ConsumeAsync(userId, ip);
        return Results.Accepted($"/api/v1/scans/{scanPublicId}", new Response(scanPublicId, "queued"));
    }

    public sealed record Input(string? Text, int? ClaimedRentVnd, string? ClaimedWardCode, IReadOnlyList<IFormFile> Images);

    /// <summary>US-201 AC1-2: text ≤ 10,000 and/or ≤ 5 images (at least one), optional rent and ward.</summary>
    public static Input Parse(IFormCollection form)
    {
        var text = form["text"].ToString();
        text = string.IsNullOrWhiteSpace(text) ? null : text;
        if (text is { Length: > MaxTextLength }) throw Validation("text", "Nội dung tối đa 10.000 ký tự");

        var images = form.Files.Where(f => f.Name == "images").ToList();
        if (images.Count > MaxImages) throw new ProblemException("image.too_many");
        if (text is null && images.Count == 0) throw new ProblemException("scan.input_required");

        int? rent = null;
        if (form["claimedRentVnd"].ToString() is { Length: > 0 } rawRent)
        {
            if (!int.TryParse(rawRent, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 100_000 or > 100_000_000)
                throw Validation("claimedRentVnd", "Giá thuê từ 100.000 đến 100.000.000 đồng");
            rent = value;
        }

        string? ward = null;
        if (form["claimedWardCode"].ToString() is { Length: > 0 } rawWard)
        {
            if (!WardCode().IsMatch(rawWard)) throw Validation("claimedWardCode", "Mã phường/xã gồm 5 chữ số");
            ward = rawWard;
        }
        return new Input(text, rent, ward, images);
    }

    /// <summary>
    /// docs/05-ai-worker.md: SHA-256 (UTF-8) of normalize(text) \n rent \n ward \n hex sha256 of each image in order,
    /// where normalize = Unicode NFC, collapse whitespace, trim (same as the worker's step 3).
    /// </summary>
    public static byte[] InputSha256(Input input, IReadOnlyList<ProcessedImage> images)
    {
        var builder = new StringBuilder()
            .Append(Normalize(input.Text ?? "")).Append('\n')
            .Append(input.ClaimedRentVnd?.ToString(CultureInfo.InvariantCulture) ?? "").Append('\n')
            .Append(input.ClaimedWardCode ?? "").Append('\n')
            .AppendJoin("", images.Select(i => Convert.ToHexStringLower(i.Sha256)));
        return SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    public static string Normalize(string text) => Whitespace().Replace(text.Normalize(NormalizationForm.FormC), " ").Trim();

    private static Task<bool> IsCommuneAsync(NpgsqlConnection connection, string code, CancellationToken ct) =>
        connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM app.administrative_units WHERE code = @Code AND unit_level = 'commune')",
            new { Code = code }, cancellationToken: ct));

    private static ProblemException Validation(string field, string message) =>
        new("validation.failed", errors: new Dictionary<string, string[]> { [field] = [message] });

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("^[0-9]{5}$")]
    private static partial Regex WardCode();
}
