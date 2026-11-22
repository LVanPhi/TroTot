using Dapper;
using FluentValidation;
using Npgsql;
using TroViec.Api.Features.Listings;
using TroViec.Api.Features.OwnerProfiles;
using TroViec.Api.Features.Scans;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Messaging;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Api.Realtime;

namespace TroViec.Api.Features.Moderation;

public sealed record DecisionRequest(int Version, string? Reason);

/// <summary>reject and hide need a reason (US-603, US-604); approve and restore take only the version.</summary>
public sealed class DecisionValidator : AbstractValidator<DecisionRequest>
{
    public DecisionValidator()
    {
        RuleFor(r => r.Version).GreaterThan(0).WithMessage("Thiếu phiên bản tin");
        RuleFor(r => r.Reason).MaximumLength(500).WithMessage("Lý do tối đa 500 ký tự");
    }
}

/// <summary>Moderation queue, review screen and decisions (US-601–604; docs/04-api.md, Kiểm duyệt).</summary>
public static class ListingModerationEndpoints
{
    public static readonly TimeSpan PublishPeriod = TimeSpan.FromDays(90);

    public sealed record QueueItem(Guid ListingId, string Title, string ListingType, int? Score, string? RiskLevel, int FlagCount,
        DateTime QueuedAt);

    public sealed record Report(long Id, string Category, string? Description, string Status, DateTime CreatedAt);

    public sealed record Poster(long UserId, string FullName, string? Phone, DateTime MemberSince,
        OwnerProfileEndpoints.ProfileResponse? OwnerProfile, IReadOnlyList<OwnerProfileEndpoints.DocumentItem> Documents);

    public sealed record ReviewScreen(ListingReadEndpoints.Detail Listing, GetScan.ListingScanResult? Screening,
        IReadOnlyList<ListingReadEndpoints.HistoryItem> History, IReadOnlyList<Report> Reports, Poster Poster);

    public sealed record DecisionResponse(string Status, int Version);

    public static void MapListingModeration(this IEndpointRouteBuilder app)
    {
        var mod = app.MapGroup("/api/v1/moderation").WithTags("Moderation").RequireAuthorization(Permissions.ListingModerate);
        mod.MapGet("/queue", QueueAsync).Produces<Paged<QueueItem>>();
        mod.MapGet("/listings/{listingId:guid}", ReviewScreenAsync).Produces<ReviewScreen>();
        mod.MapPost("/listings/{listingId:guid}/approve", ApproveAsync).Validate<DecisionRequest>().Produces<DecisionResponse>();
        mod.MapPost("/listings/{listingId:guid}/reject", RejectAsync).Validate<DecisionRequest>().Produces<DecisionResponse>();
        mod.MapPost("/listings/{listingId:guid}/hide", HideAsync).Validate<DecisionRequest>().Produces<DecisionResponse>();
        mod.MapPost("/listings/{listingId:guid}/restore", RestoreAsync).Validate<DecisionRequest>().Produces<DecisionResponse>();
        app.MapHub<ModerationHub>(ModerationHub.Path).RequireAuthorization(Permissions.ListingModerate);
    }

    /// <summary>US-601: highest risk first, AI failures (no score) on top, then oldest wait (docs/03-database.md, Hàng đợi duyệt).</summary>
    public static async Task<IResult> QueueAsync(NpgsqlDataSource db, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size, offset) = Paging.Normalize(page, pageSize);
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<(Guid PublicId, string Title, string ListingType, short? Score, string? RiskLevel,
            long FlagCount, DateTime QueuedAt, long Total)>(new CommandDefinition(
            """
            SELECT l.public_id, l.title, l.listing_type::text,
                   a.score, a.risk_level::text,
                   (SELECT count(*) FROM app.assessment_flags f WHERE f.assessment_id = a.id),
                   q.queued_at,
                   count(*) OVER ()
            FROM app.listings l
            JOIN LATERAL (
                SELECT t.created_at AS queued_at FROM app.listing_transitions t
                WHERE t.listing_id = l.id AND t.to_status = 'pending_review'
                ORDER BY t.id DESC LIMIT 1) q ON true
            LEFT JOIN LATERAL (
                SELECT a1.id, a1.score, a1.risk_level
                FROM app.scans s JOIN app.assessments a1 ON a1.scan_id = s.id
                WHERE s.listing_id = l.id
                ORDER BY s.created_at DESC, a1.id DESC LIMIT 1) a ON true
            WHERE l.status = 'pending_review'
            ORDER BY a.score DESC NULLS FIRST, q.queued_at
            LIMIT @Size OFFSET @Offset
            """, new { Size = size, Offset = offset }, cancellationToken: ct))).ToList();
        var items = rows.Select(r => new QueueItem(r.PublicId, r.Title, r.ListingType, r.Score, r.RiskLevel, (int)r.FlagCount, r.QueuedAt)).ToList();
        return Results.Ok(new Paged<QueueItem>(items, p, size, rows.Count > 0 ? rows[0].Total : 0));
    }

    /// <summary>US-602: listing, AI result with evidence, history with names, reports, poster with owner profile and documents.</summary>
    public static async Task<IResult> ReviewScreenAsync(Guid listingId, HttpContext http, NpgsqlDataSource db, MediaUrls urls, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var (detail, id, posterId) = await ListingReadEndpoints.LoadAsync(connection, listingId, urls, http, ct);
        var screening = await GetScan.LatestForListingAsync(connection, id, ct);
        var history = await ListingReadEndpoints.LoadHistoryAsync(connection, id, posterId, showNames: true, ct);
        var reports = (await connection.QueryAsync<Report>(new CommandDefinition(
            """
            SELECT id, category::text AS category, description, status::text AS status, created_at
            FROM app.reports WHERE listing_id = @Id ORDER BY created_at DESC
            """, new { Id = id }, cancellationToken: ct))).ToList();
        var user = await connection.QuerySingleAsync<(string FullName, string? Phone, DateTime CreatedAt)>(new CommandDefinition(
            "SELECT full_name, phone_e164, created_at FROM app.users WHERE id = @Id", new { Id = posterId }, cancellationToken: ct));
        var poster = new Poster(posterId, user.FullName, user.Phone, user.CreatedAt,
            await OwnerProfileEndpoints.ProfileAsync(connection, posterId, null, ct),
            await OwnerProfileEndpoints.DocumentsAsync(connection, posterId, ct));
        return Results.Ok(new ReviewScreen(detail, screening, history, reports, poster));
    }

    /// <summary>pending_review → verified for 90 days, and an outbox row to (re)compute the listing embedding.</summary>
    public static Task<IResult> ApproveAsync(Guid listingId, DecisionRequest request, HttpContext http, NpgsqlDataSource db,
        ListingTransitionService transitions, OutboxSignal outbox, ModerationNotifier notifier, TimeProvider clock, CancellationToken ct) =>
        DecideAsync(listingId, request, ListingStatus.PendingReview, ListingStatus.Verified, reasonRequired: false, http, db, transitions,
            notifier, ct, after: async (connection, tx, id) =>
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO app.outbox_messages (listing_id) VALUES (@Id)", new { Id = id }, tx, cancellationToken: ct));
                outbox.Notify();
            }, newExpiresAt: clock.GetUtcNow().UtcDateTime + PublishPeriod);

    public static Task<IResult> RejectAsync(Guid listingId, DecisionRequest request, HttpContext http, NpgsqlDataSource db,
        ListingTransitionService transitions, ModerationNotifier notifier, CancellationToken ct) =>
        DecideAsync(listingId, request, ListingStatus.PendingReview, ListingStatus.Rejected, reasonRequired: true, http, db, transitions,
            notifier, ct);

    public static Task<IResult> HideAsync(Guid listingId, DecisionRequest request, HttpContext http, NpgsqlDataSource db,
        ListingTransitionService transitions, ModerationNotifier notifier, CancellationToken ct) =>
        DecideAsync(listingId, request, ListingStatus.Verified, ListingStatus.Hidden, reasonRequired: true, http, db, transitions,
            notifier, ct);

    /// <summary>hidden → verified; with an open appeal the service answers appeal.pending (handled through the appeal).</summary>
    public static Task<IResult> RestoreAsync(Guid listingId, DecisionRequest request, HttpContext http, NpgsqlDataSource db,
        ListingTransitionService transitions, ModerationNotifier notifier, CancellationToken ct) =>
        DecideAsync(listingId, request, ListingStatus.Hidden, ListingStatus.Verified, reasonRequired: false, http, db, transitions,
            notifier, ct);

    private static async Task<IResult> DecideAsync(Guid listingId, DecisionRequest request, ListingStatus from, ListingStatus to,
        bool reasonRequired, HttpContext http, NpgsqlDataSource db, ListingTransitionService transitions, ModerationNotifier notifier,
        CancellationToken ct, Func<NpgsqlConnection, NpgsqlTransaction, long, Task>? after = null, DateTime? newExpiresAt = null)
    {
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reasonRequired && reason is null)
            throw new ProblemException("validation.failed", errors: new Dictionary<string, string[]> { ["reason"] = ["Nhập lý do"] });

        var moderator = CurrentUser.Id(http.User);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var listing = await connection.QuerySingleOrDefaultAsync<(long Id, string Status)?>(new CommandDefinition(
            "SELECT id, status::text FROM app.listings WHERE public_id = @Id FOR UPDATE", new { Id = listingId }, tx, cancellationToken: ct))
            ?? throw new ProblemException("listing.not_found");
        if (listing.Status != DbEnum.ToDb(from))
            throw new ProblemException(listing.Status == DbEnum.ToDb(to) ? "listing.version_conflict" : "listing.invalid_transition");

        await transitions.TransitionAsync(connection, tx, listing.Id, request.Version, from, to, moderator, reason, newExpiresAt,
            new TransitionRules(ModeratorAction: true), ct);
        if (after is not null) await after(connection, tx, listing.Id);
        await tx.CommitAsync(ct);

        var version = request.Version + 1;
        await notifier.ListingUpdatedAsync(listingId, DbEnum.ToDb(to), version, ct);
        return Results.Ok(new DecisionResponse(DbEnum.ToDb(to), version));
    }
}
