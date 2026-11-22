using Dapper;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace TroViec.Api.Realtime;

/// <summary>Pushed to group scan:{scanId} when a quick check finishes; the client then GETs /scans/{scanId}.</summary>
public sealed record ScanUpdated(Guid ScanId, string Status);

/// <summary>
/// /hubs/scans (docs/04-api.md, SignalR). Anonymous; Watch only joins groups of quick_check scans (listing screenings
/// must not leak their AI result), at most 10 groups per connection. No sensitive data is sent through the hub.
/// </summary>
public sealed class ScanHub(NpgsqlDataSource db) : Hub
{
    public const string Path = "/hubs/scans";
    public const int MaxGroupsPerConnection = 10;
    private const string WatchedKey = "watched";

    public static string GroupName(Guid scanId) => $"scan:{scanId}";

    public async Task Watch(string scanId)
    {
        if (!Guid.TryParse(scanId, out var id)) throw new HubException("scan.not_found");

        var watched = Context.Items.TryGetValue(WatchedKey, out var value) ? (HashSet<Guid>)value! : [];
        if (watched.Contains(id)) return;
        if (watched.Count >= MaxGroupsPerConnection) throw new HubException("scan.watch_limit");

        await using var connection = await db.OpenConnectionAsync(Context.ConnectionAborted);
        var isQuickCheck = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM app.scans WHERE public_id = @Id AND source = 'quick_check')",
            new { Id = id }, cancellationToken: Context.ConnectionAborted));
        if (!isQuickCheck) throw new HubException("scan.not_found");

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(id), Context.ConnectionAborted);
        watched.Add(id);
        Context.Items[WatchedKey] = watched;
    }
}
