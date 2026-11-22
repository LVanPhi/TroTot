using Dapper;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace TroViec.Api.Realtime;

public sealed record QueueChanged(int PendingCount);

public sealed record ListingUpdated(Guid ListingId, string Status, int Version);

/// <summary>/hubs/moderation (docs/04-api.md, SignalR): listing.moderate only; the server pushes, clients call nothing.</summary>
public sealed class ModerationHub : Hub
{
    public const string Path = "/hubs/moderation";
}

/// <summary>Pushes moderation events. Failures are logged and swallowed: the database change already committed.</summary>
public sealed class ModerationNotifier(IHubContext<ModerationHub> hub, NpgsqlDataSource db, ILogger<ModerationNotifier> logger)
{
    public async Task QueueChangedAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await db.OpenConnectionAsync(ct);
            var pending = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM app.listings WHERE status = 'pending_review'", cancellationToken: ct));
            await hub.Clients.All.SendAsync(nameof(QueueChanged), new QueueChanged(pending), ct);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            logger.LogWarning("QueueChanged not sent: {ExceptionType}", ex.GetType().Name);
        }
    }

    public async Task ListingUpdatedAsync(Guid listingId, string status, int version, CancellationToken ct)
    {
        await hub.Clients.All.SendAsync(nameof(ListingUpdated), new ListingUpdated(listingId, status, version), ct);
        await QueueChangedAsync(ct);
    }
}
