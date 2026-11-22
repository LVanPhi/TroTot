using Dapper;
using Npgsql;
using TroViec.Api.Infrastructure.Auth;

namespace TroViec.Api.Infrastructure.Notifications;

/// <summary>
/// US-902: emails the poster after a decision has been committed. Best effort: a mail failure never undoes or fails the
/// decision (logged by listing id only, AGENTS.md rule 13). Anonymized or locked accounts get nothing.
/// </summary>
public sealed class ListingMailer(NpgsqlDataSource db, IEmailSender email, SiteUrl site, ILogger<ListingMailer> logger)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task NotifyAsync(Guid listingId, ListingNotice notice, string? reason, CancellationToken ct)
    {
        try
        {
            await using var connection = await db.OpenConnectionAsync(ct);
            var poster = await connection.QuerySingleOrDefaultAsync<(string? Email, string FullName, string Title)?>(new CommandDefinition(
                """
                SELECT u.email, u.full_name, l.title
                FROM app.listings l JOIN app.users u ON u.id = l.poster_id
                WHERE l.public_id = @Id AND u.status = 'active' AND u.email_verified_at IS NOT NULL
                """, new { Id = listingId }, cancellationToken: ct));
            if (poster is not { Email: { } to } p) return;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            var link = $"{site.Value.TrimEnd('/')}/toi/tin-cua-toi/{listingId}";
            await email.SendAsync(EmailTemplates.ListingDecision(to, p.FullName, p.Title, notice, reason, link), cts.Token);
            logger.LogInformation("Listing notice {Notice} sent for listing {ListingId}", notice, listingId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Listing notice {Notice} not sent for listing {ListingId}: {ExceptionType}", notice, listingId, ex.GetType().Name);
        }
    }
}
