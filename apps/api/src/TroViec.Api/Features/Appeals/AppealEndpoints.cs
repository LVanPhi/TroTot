using Dapper;
using FluentValidation;
using Npgsql;
using TroViec.Api.Features.Listings;
using TroViec.Api.Features.Moderation;
using TroViec.Api.Features.Reports;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Messaging;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Api.Realtime;

namespace TroViec.Api.Features.Appeals;

public sealed record AppealRequest(string Reason);

public sealed class AppealValidator : AbstractValidator<AppealRequest>
{
    public AppealValidator() => RuleFor(r => r.Reason).Must(r => r is not null && r.Trim().Length is >= 1 and <= 1000)
        .WithMessage("Lý do kháng nghị từ 1 đến 1.000 ký tự");
}

/// <summary>Appeals (US-701, US-702, US-607; BR-04). The trigger check_appeal is the last line of defence.</summary>
public static class AppealEndpoints
{
    public sealed record Created(long Id);

    public sealed record AppealItem(long Id, Guid ListingId, string ListingTitle, string DecisionStatus, string? DecisionReason,
        string Reason, string Status, string? DecisionNote, DateTime CreatedAt, DateTime? DecidedAt);

    public sealed record Decision(long Id, string Status, string ListingStatus, int ListingVersion);

    private sealed record AppealRow(long Id, long ListingId, Guid ListingPublicId, long PosterId, string Status, long DeciderOfDecision,
        string DecisionStatus, string ListingStatus, int Version);

    public static void MapAppeals(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/listings/{listingId:guid}/appeals", CreateAsync).WithTags("Listings")
            .RequireAuthorization(Permissions.ListingAppeal).Validate<AppealRequest>().Produces<Created>(StatusCodes.Status201Created);
        app.MapGet("/api/v1/me/appeals", MineAsync).WithTags("Me")
            .RequireAuthorization(Permissions.ListingAppeal).Produces<List<AppealItem>>();

        var mod = app.MapGroup("/api/v1/moderation/appeals").WithTags("Moderation").RequireAuthorization(Permissions.AppealResolve);
        mod.MapGet("/", ListAsync).Produces<Paged<AppealItem>>();
        mod.MapPost("/{id:long}/accept", AcceptAsync).Validate<NoteRequest>().Produces<Decision>();
        mod.MapPost("/{id:long}/reject", RejectAsync).Validate<NoteRequest>().Produces<Decision>();
    }

    /// <summary>
    /// US-701: against the latest decision of the listing; the trigger enforces "rejected/hidden", "still in effect" and
    /// "within 7 days"; one appeal per decision (appeal.already_used) and one open per listing (appeal.already_open).
    /// </summary>
    public static async Task<IResult> CreateAsync(Guid listingId, AppealRequest request, HttpContext http, NpgsqlDataSource db,
        CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var listing = await ListingAccess.OwnedAsync(connection, tx, listingId, CurrentUser.Id(http.User), ct);
        var transition = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT id FROM app.listing_transitions WHERE listing_id = @Id ORDER BY id DESC LIMIT 1",
            new { listing.Id }, tx, cancellationToken: ct));
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO app.appeals (listing_id, listing_transition_id, reason) VALUES (@ListingId, @Transition, @Reason) RETURNING id",
            new { ListingId = listing.Id, Transition = transition, Reason = request.Reason.Trim() }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return Results.Created($"/api/v1/me/appeals/{id}", new Created(id));
    }

    public static async Task<IResult> MineAsync(HttpContext http, NpgsqlDataSource db, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return Results.Ok(await QueryAsync(connection, "l.poster_id = @UserId", new { UserId = CurrentUser.Id(http.User) }, ct));
    }

    public static async Task<IResult> ListAsync(NpgsqlDataSource db, string? status, int? page, int? pageSize, CancellationToken ct)
    {
        if (status is not (null or "open" or "accepted" or "rejected"))
            throw new ProblemException("validation.failed", errors: new Dictionary<string, string[]> { ["status"] = ["Trạng thái không hợp lệ"] });
        var (p, size, offset) = Paging.Normalize(page, pageSize);
        await using var connection = await db.OpenConnectionAsync(ct);
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM app.appeals a WHERE @Status::text IS NULL OR a.status = @Status::app.appeal_status",
            new { Status = status }, cancellationToken: ct));
        var items = await QueryAsync(connection, "(@Status::text IS NULL OR a.status = @Status::app.appeal_status)",
            new { Status = status }, ct, $"LIMIT {size} OFFSET {offset}");
        return Results.Ok(new Paged<AppealItem>(items, p, size, total));
    }

    /// <summary>US-607 accept: listing back to verified; from rejected also a new 90-day expiry and an embedding outbox row.</summary>
    public static Task<IResult> AcceptAsync(long id, NoteRequest request, HttpContext http, NpgsqlDataSource db,
        ListingTransitionService transitions, OutboxSignal outbox, ModerationNotifier notifier, TimeProvider clock, CancellationToken ct) =>
        DecideAsync(id, request, accept: true, http, db, transitions, outbox, notifier, clock, ct);

    /// <summary>US-607 reject: a hidden listing becomes removed (reason = note); a rejected one stays rejected.</summary>
    public static Task<IResult> RejectAsync(long id, NoteRequest request, HttpContext http, NpgsqlDataSource db,
        ListingTransitionService transitions, OutboxSignal outbox, ModerationNotifier notifier, TimeProvider clock, CancellationToken ct) =>
        DecideAsync(id, request, accept: false, http, db, transitions, outbox, notifier, clock, ct);

    private static async Task<IResult> DecideAsync(long id, NoteRequest request, bool accept, HttpContext http, NpgsqlDataSource db,
        ListingTransitionService transitions, OutboxSignal outbox, ModerationNotifier notifier, TimeProvider clock, CancellationToken ct)
    {
        var moderator = CurrentUser.Id(http.User);
        var note = request.Note.Trim();
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var appeal = await connection.QuerySingleOrDefaultAsync<AppealRow>(new CommandDefinition(
            """
            SELECT a.id, a.listing_id, l.public_id AS listing_public_id, l.poster_id, a.status::text AS status,
                   t.actor_id AS decider_of_decision, t.to_status::text AS decision_status, l.status::text AS listing_status, l.version
            FROM app.appeals a
            JOIN app.listings l ON l.id = a.listing_id
            JOIN app.listing_transitions t ON t.id = a.listing_transition_id
            WHERE a.id = @Id
            FOR UPDATE OF a, l
            """, new { Id = id }, tx, cancellationToken: ct)) ?? throw new ProblemException("resource.not_found");

        // Checked here before the trigger (BR-04, BR-19), so the caller gets the precise code.
        if (appeal.PosterId == moderator) throw new ProblemException("moderation.conflict_of_interest");
        if (appeal.DeciderOfDecision == moderator) throw new ProblemException("appeal.decider_conflict");
        if (appeal.Status != "open") throw new ProblemException("appeal.already_resolved");

        var from = DbEnum.FromDb<ListingStatus>(appeal.ListingStatus);
        var rules = new TransitionRules(ResolvesAppeal: true, ModeratorAction: true);
        var listingStatus = appeal.ListingStatus;
        var version = appeal.Version;
        if (accept)
        {
            var fromRejected = from == ListingStatus.Rejected;
            await transitions.TransitionAsync(connection, tx, appeal.ListingId, version, from, ListingStatus.Verified, moderator, note,
                fromRejected ? clock.GetUtcNow().UtcDateTime + ListingModerationEndpoints.PublishPeriod : null, rules, ct);
            if (fromRejected)
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO app.outbox_messages (listing_id) VALUES (@Id)", new { Id = appeal.ListingId }, tx, cancellationToken: ct));
            (listingStatus, version) = ("verified", version + 1);
        }
        else if (from == ListingStatus.Hidden)
        {
            await transitions.TransitionAsync(connection, tx, appeal.ListingId, version, from, ListingStatus.Removed, moderator, note,
                rules: rules, ct: ct);
            (listingStatus, version) = ("removed", version + 1);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE app.appeals SET status = @Status::app.appeal_status, decider_id = @Moderator, decided_at = now(), decision_note = @Note
            WHERE id = @Id
            """, new { Id = id, Status = accept ? "accepted" : "rejected", Moderator = moderator, Note = note }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        outbox.Notify();
        if (version != appeal.Version) await notifier.ListingUpdatedAsync(appeal.ListingPublicId, listingStatus, version, ct);
        await http.RequestServices.GetRequiredService<Infrastructure.Notifications.ListingMailer>().NotifyAsync(appeal.ListingPublicId,
            accept ? Infrastructure.Notifications.ListingNotice.AppealAccepted : Infrastructure.Notifications.ListingNotice.AppealRejected, note, ct);
        return Results.Ok(new Decision(id, accept ? "accepted" : "rejected", listingStatus, version));
    }

    private static async Task<List<AppealItem>> QueryAsync(NpgsqlConnection connection, string where, object args, CancellationToken ct,
        string tail = "") =>
        (await connection.QueryAsync<AppealItem>(new CommandDefinition(
            $"""
            SELECT a.id, l.public_id AS listing_id, l.title AS listing_title, t.to_status::text AS decision_status,
                   t.reason AS decision_reason, a.reason, a.status::text AS status, a.decision_note, a.created_at, a.decided_at
            FROM app.appeals a
            JOIN app.listings l ON l.id = a.listing_id
            JOIN app.listing_transitions t ON t.id = a.listing_transition_id
            WHERE {where}
            ORDER BY a.created_at DESC, a.id DESC
            {tail}
            """, args, cancellationToken: ct))).ToList();
}
