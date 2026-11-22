using Dapper;
using Npgsql;
using TroViec.Api.Infrastructure.Auth;

namespace TroViec.Api.Features.Moderation;

/// <summary>US-609: the calling moderator's own decisions and document openings, newest first.</summary>
public static class AuditEndpoints
{
    public sealed record AuditItem(string Kind, DateTime At, string Target, string? Detail);

    public static void MapAudit(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/moderation/audit/me", MineAsync).WithTags("Moderation")
            .RequireAuthorization(Permissions.AuditViewOwn).Produces<List<AuditItem>>();

    public static async Task<IResult> MineAsync(HttpContext http, NpgsqlDataSource db, int? limit, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var items = await connection.QueryAsync<AuditItem>(new CommandDefinition(
            """
            SELECT kind, at, target, detail FROM (
                SELECT 'listing_' || t.to_status::text AS kind, t.created_at AS at, l.public_id::text AS target, t.reason AS detail
                FROM app.listing_transitions t JOIN app.listings l ON l.id = t.listing_id
                WHERE t.actor_id = @Me AND l.poster_id <> @Me
                UNION ALL
                SELECT 'document_opened', d.accessed_at, 'document:' || d.owner_document_id, d.purpose
                FROM app.document_access_logs d WHERE d.viewer_id = @Me
                UNION ALL
                SELECT 'document_' || o.status::text, o.reviewed_at, 'document:' || o.id, NULL
                FROM app.owner_documents o WHERE o.reviewer_id = @Me
                UNION ALL
                SELECT 'report_' || r.status::text, r.resolved_at, 'report:' || r.id, r.resolution_note
                FROM app.reports r WHERE r.resolver_id = @Me
                UNION ALL
                SELECT 'appeal_' || a.status::text, a.decided_at, 'appeal:' || a.id, a.decision_note
                FROM app.appeals a WHERE a.decider_id = @Me
                UNION ALL
                -- reviews has no hidden_at column: the time shown is when the review was written.
                SELECT 'review_hidden', rv.created_at, 'review:' || rv.id, rv.hidden_reason
                FROM app.reviews rv WHERE rv.hider_id = @Me
            ) x
            ORDER BY at DESC
            LIMIT @Limit
            """, new { Me = CurrentUser.Id(http.User), Limit = Math.Clamp(limit ?? 100, 1, 500) }, cancellationToken: ct));
        return Results.Ok(items.ToList());
    }
}
