using Dapper;
using Npgsql;
using TroViec.Api.Infrastructure.Db;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Listings;

/// <summary>Mirrors app.listing_status (0001_init.sql). DbEnum maps PendingReview ↔ "pending_review".</summary>
public enum ListingStatus
{
    Draft,
    Screening,
    PendingReview,
    Verified,
    Rejected,
    Hidden,
    Removed,
    Expired,
}

/// <summary>
/// Allowed edges of the listing life cycle (docs/01-requirements.md, Vòng đời tin). Same table as the database function
/// app.listing_transition_allowed (0003_review_fixes.sql), which stays the last line of defence.
/// </summary>
public static class ListingLifecycle
{
    private static readonly HashSet<(ListingStatus From, ListingStatus To)> Edges =
    [
        (ListingStatus.Draft, ListingStatus.Screening),
        (ListingStatus.Screening, ListingStatus.PendingReview),
        (ListingStatus.PendingReview, ListingStatus.Verified),
        (ListingStatus.PendingReview, ListingStatus.Rejected),
        (ListingStatus.Rejected, ListingStatus.Draft),
        (ListingStatus.Rejected, ListingStatus.Verified),
        (ListingStatus.Verified, ListingStatus.Hidden),
        (ListingStatus.Verified, ListingStatus.Expired),
        (ListingStatus.Verified, ListingStatus.Screening),
        (ListingStatus.Verified, ListingStatus.Draft),
        (ListingStatus.Hidden, ListingStatus.Verified),
        (ListingStatus.Expired, ListingStatus.Screening),
        (ListingStatus.Expired, ListingStatus.Draft),
    ];

    public static bool IsAllowed(ListingStatus from, ListingStatus to) =>
        to == ListingStatus.Removed
            ? from is not (ListingStatus.Removed or ListingStatus.Screening)
            : Edges.Contains((from, to));
}

/// <summary>Extra checks for a transition that are not in the database.</summary>
/// <param name="ResolvesAppeal">True only for the edge made by deciding the listing's open appeal.</param>
/// <param name="ModeratorAction">True for moderation actions: the actor must not be the poster (BR-19).</param>
public sealed record TransitionRules(bool ResolvesAppeal = false, bool ModeratorAction = false);

/// <summary>
/// The ONLY place that writes listings.status (AGENTS.md rule 9, docs/03-database.md, Đổi trạng thái tin):
/// optimistic concurrency on version + a listing_transitions row in the caller's transaction.
/// </summary>
public sealed class ListingTransitionService
{
    public async Task TransitionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long listingId,
        int expectedVersion, ListingStatus from, ListingStatus to, long actorId, string? reason,
        DateTime? newExpiresAt = null, TransitionRules? rules = null, CancellationToken ct = default)
    {
        rules ??= new TransitionRules();

        var guard = await conn.QuerySingleOrDefaultAsync<(long PosterId, bool OpenAppeal)?>(new CommandDefinition(
            """
            SELECT l.poster_id,
                   EXISTS (SELECT 1 FROM app.appeals a WHERE a.listing_id = l.id AND a.status = 'open')
            FROM app.listings l WHERE l.id = @ListingId
            """, new { ListingId = listingId }, tx, cancellationToken: ct));
        if (guard is null) throw new ProblemException("listing.not_found");
        if (guard.Value.OpenAppeal && !rules.ResolvesAppeal) throw new ProblemException("appeal.pending");
        if (rules.ModeratorAction && guard.Value.PosterId == actorId)
            throw new ProblemException("moderation.conflict_of_interest");
        // After the appeal check: with an open appeal every other change answers appeal.pending (BR-04).
        if (!ListingLifecycle.IsAllowed(from, to)) throw new ProblemException("listing.invalid_transition");

        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE app.listings
            SET status = @To::app.listing_status,
                version = version + 1,
                expires_at = COALESCE(@NewExpiresAt, expires_at)
            WHERE id = @ListingId
              AND version = @ExpectedVersion
              AND status = @From::app.listing_status
            """, new
            {
                ListingId = listingId, ExpectedVersion = expectedVersion,
                From = DbEnum.ToDb(from), To = DbEnum.ToDb(to), NewExpiresAt = newExpiresAt,
            }, tx, cancellationToken: ct));
        if (rows == 0) throw new ProblemException("listing.version_conflict");

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO app.listing_transitions (listing_id, from_status, to_status, actor_id, reason)
            VALUES (@ListingId, @From::app.listing_status, @To::app.listing_status, @ActorId, @Reason)
            """, new { ListingId = listingId, From = DbEnum.ToDb(from), To = DbEnum.ToDb(to), ActorId = actorId, Reason = reason },
            tx, cancellationToken: ct));
    }
}
