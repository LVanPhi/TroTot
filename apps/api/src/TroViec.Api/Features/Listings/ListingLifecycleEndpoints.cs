using System.Security.Cryptography;
using System.Text;
using Dapper;
using FluentValidation;
using Npgsql;
using TroViec.Api.Features.Me;
using TroViec.Api.Features.Scans;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Crypto;
using TroViec.Api.Infrastructure.Messaging;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Listings;

public sealed record VersionRequest(int Version);

public sealed record RemoveRequest(int Version, string? Reason);

public sealed record LifecycleResponse(string Status, int Version);

public sealed class VersionRequestValidator : AbstractValidator<VersionRequest>
{
    public VersionRequestValidator() => RuleFor(r => r.Version).GreaterThan(0).WithMessage("Thiếu phiên bản tin");
}

public sealed class RemoveRequestValidator : AbstractValidator<RemoveRequest>
{
    public RemoveRequestValidator()
    {
        RuleFor(r => r.Version).GreaterThan(0).WithMessage("Thiếu phiên bản tin");
        RuleFor(r => r.Reason).MaximumLength(500).WithMessage("Lý do tối đa 500 ký tự");
    }
}

/// <summary>Poster status changes (US-304–306; docs/02-architecture.md, Gửi tin đi duyệt). All go through ListingTransitionService.</summary>
public static class ListingLifecycleEndpoints
{
    public const string DefaultRemoveReason = "Người đăng gỡ";
    public static readonly TimeSpan RenewWindow = TimeSpan.FromDays(7);

    /// <summary>draft → screening, with a new listing scan (US-304).</summary>
    public static Task<IResult> SubmitAsync(Guid listingId, VersionRequest request, HttpContext http, NpgsqlDataSource db,
        ListingTransitionService transitions, IdentifierHasher hasher, StudentEmailDomains domains, OutboxSignal outbox, CancellationToken ct) =>
        ScreenAsync(listingId, request.Version, renew: false, http, db, transitions, hasher, domains, outbox, ct);

    /// <summary>verified with ≤ 7 days left, or expired → screening; BR-02 checked again (US-305, BR-03).</summary>
    public static Task<IResult> RenewAsync(Guid listingId, VersionRequest request, HttpContext http, NpgsqlDataSource db,
        ListingTransitionService transitions, IdentifierHasher hasher, StudentEmailDomains domains, OutboxSignal outbox, CancellationToken ct) =>
        ScreenAsync(listingId, request.Version, renew: true, http, db, transitions, hasher, domains, outbox, ct);

    /// <summary>verified / rejected / expired → draft so the poster can edit (US-307).</summary>
    public static async Task<IResult> UnpublishAsync(Guid listingId, VersionRequest request, HttpContext http, NpgsqlDataSource db,
        ListingTransitionService transitions, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var listing = await ListingAccess.OwnedAsync(connection, tx, listingId, CurrentUser.Id(http.User), ct);
        // The service answers appeal.pending first, then listing.invalid_transition for any source without a → draft edge.
        await transitions.TransitionAsync(connection, tx, listing.Id, request.Version, Status(listing), ListingStatus.Draft, listing.PosterId,
            reason: null, ct: ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new LifecycleResponse("draft", request.Version + 1));
    }

    /// <summary>Any status except screening (and not with an open appeal) → removed (US-306).</summary>
    public static async Task<IResult> RemoveAsync(Guid listingId, RemoveRequest request, HttpContext http, NpgsqlDataSource db,
        ListingTransitionService transitions, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var listing = await ListingAccess.OwnedAsync(connection, tx, listingId, CurrentUser.Id(http.User), ct);
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? DefaultRemoveReason : request.Reason.Trim();
        await transitions.TransitionAsync(connection, tx, listing.Id, request.Version, Status(listing), ListingStatus.Removed,
            listing.PosterId, reason, ct: ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new LifecycleResponse("removed", request.Version + 1));
    }

    private static async Task<IResult> ScreenAsync(Guid listingId, int version, bool renew, HttpContext http, NpgsqlDataSource db,
        ListingTransitionService transitions, IdentifierHasher hasher, StudentEmailDomains domains, OutboxSignal outbox, CancellationToken ct)
    {
        var userId = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var listing = await ListingAccess.OwnedAsync(connection, tx, listingId, userId, ct);
        var facts = await connection.QuerySingleAsync<(string Title, string Description, DateTime? ExpiresAt, int Images, string? Phone)>(
            new CommandDefinition(
                """
                SELECT l.title, l.description, l.expires_at,
                       (SELECT count(*)::int FROM app.listing_images i WHERE i.listing_id = l.id),
                       u.phone_e164
                FROM app.listings l JOIN app.users u ON u.id = l.poster_id
                WHERE l.id = @Id
                """, new { listing.Id }, tx, cancellationToken: ct));

        var from = Status(listing);
        if (renew)
        {
            var dueSoon = from == ListingStatus.Verified && facts.ExpiresAt is { } exp && exp - DateTime.UtcNow <= RenewWindow;
            if (!(dueSoon || from == ListingStatus.Expired))
                throw new ProblemException(from == ListingStatus.Verified ? "listing.renew_too_early" : "listing.invalid_transition");
        }
        else if (from != ListingStatus.Draft)
        {
            throw new ProblemException("listing.invalid_transition");
        }

        var me = await MeQueries.GetAsync(connection, userId, domains, ct) ?? throw new ProblemException("auth.unauthenticated");
        if (!me.Posting.Allowed)
            throw new ProblemException("listing.poster_not_verified",
                extensions: new Dictionary<string, object?> { ["missing"] = me.Posting.Missing });
        if (listing.ListingType == "room" && facts.Images == 0) throw new ProblemException("listing.images_required");

        await transitions.TransitionAsync(connection, tx, listing.Id, version, from, ListingStatus.Screening, userId, reason: null, ct: ct);
        var inputText = $"{facts.Title}\n{facts.Description}";
        var scanId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO app.scans (source, listing_id, requester_id, input_text, input_sha256)
            VALUES ('listing', @ListingId, @UserId, @Text, @Sha)
            RETURNING id
            """, new
            {
                ListingId = listing.Id, UserId = userId, Text = inputText,
                Sha = SHA256.HashData(Encoding.UTF8.GetBytes(CreateScan.Normalize(inputText))),
            }, tx, cancellationToken: ct));
        if (facts.Phone is { } phone) await LinkPhoneAsync(connection, tx, scanId, phone, hasher, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO app.outbox_messages (scan_id) VALUES (@ScanId)", new { ScanId = scanId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        outbox.Notify();
        return Results.Accepted($"/api/v1/listings/{listingId}", new LifecycleResponse("screening", version + 1));
    }

    /// <summary>The poster's verified phone as a scan identifier (same HMAC and hint as the worker, docs/05-ai-worker.md step 4).</summary>
    private static async Task LinkPhoneAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long scanId, string phoneE164,
        IdentifierHasher hasher, CancellationToken ct)
    {
        var digest = hasher.Hash("phone", phoneE164);
        var identifierId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            WITH ins AS (
                INSERT INTO app.identifiers (kind, value_hmac, hint) VALUES ('phone', @Digest, @Hint)
                ON CONFLICT (kind, value_hmac) DO NOTHING RETURNING id)
            SELECT id FROM ins
            UNION ALL
            SELECT id FROM app.identifiers WHERE kind = 'phone' AND value_hmac = @Digest
            LIMIT 1
            """, new { Digest = digest, Hint = "•••" + phoneE164[^3..] }, tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO app.scan_identifiers (scan_id, identifier_id) VALUES (@ScanId, @IdentifierId) ON CONFLICT DO NOTHING",
            new { ScanId = scanId, IdentifierId = identifierId }, tx, cancellationToken: ct));
    }

    private static ListingStatus Status(OwnedListing listing) => Infrastructure.Db.DbEnum.FromDb<ListingStatus>(listing.Status);
}
