using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TroViec.Maintenance;

/// <summary>Deletes stored objects; implemented with S3 in production and in memory in tests.</summary>
public interface IObjectDeleter
{
    /// <summary>Idempotent: a missing object is not an error.</summary>
    Task DeleteAsync(string bucket, string key, CancellationToken ct);
}

public sealed record JobResult(string Name, bool Ok, long Count, string? Error);

/// <summary>
/// US-901, docs/03-database.md (Job đêm). Five jobs in this order, each in its own transaction; a failing job is logged and
/// the next ones still run. The two bulk listing updates are the only change of listings.status outside
/// ListingTransitionService (AGENTS.md rule 9): they still bump version and write listing_transitions in the same statement.
/// </summary>
public sealed class NightlyJobs(NpgsqlDataSource db, IObjectDeleter storage, string scanBucket, ILogger<NightlyJobs> logger)
{
    public async Task<IReadOnlyList<JobResult>> RunAsync(CancellationToken ct)
    {
        var results = new List<JobResult>
        {
            await RunOneAsync("expire_listings", ExpireListingsAsync, ct),
            await RunOneAsync("remove_hidden_listings", RemoveStaleHiddenAsync, ct),
            await RunOneAsync("delete_expired_scans", DeleteExpiredScansAsync, ct),
            await RunOneAsync("purge_outbox", PurgeOutboxAsync, ct),
            await RunOneAsync("refresh_stats", RefreshStatsAsync, ct),
            await RunOneAsync("cancel_expired_lease_invites", CancelExpiredLeaseInvitesAsync, ct),
            await RunOneAsync("end_finished_leases", EndFinishedLeasesAsync, ct),
        };
        return results;
    }

    private async Task<JobResult> RunOneAsync(string name, Func<CancellationToken, Task<long>> job, CancellationToken ct)
    {
        try
        {
            var count = await job(ct);
            logger.LogInformation("Job {Job} done: {Count}", name, count);
            return new JobResult(name, true, count, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Job {Job} failed: {ExceptionType}", name, ex.GetType().Name);
            return new JobResult(name, false, 0, ex.GetType().Name);
        }
    }

    // Room status after lease jobs: occupied with an active lease, reserved with one awaiting signatures, else vacant
    // (same rule as LeaseStore.RecomputeRoomStatusAsync in the API).
    private const string RecomputeRooms = """
        UPDATE app.rooms r
        SET status = CASE
                WHEN EXISTS (SELECT 1 FROM app.leases l WHERE l.room_id = r.id AND l.status = 'active') THEN 'occupied'::app.room_status
                WHEN EXISTS (SELECT 1 FROM app.leases l WHERE l.room_id = r.id AND l.status = 'awaiting_signatures') THEN 'reserved'::app.room_status
                ELSE 'vacant'::app.room_status END,
            version = r.version + 1
        WHERE r.id = ANY(@Rooms) AND r.status IN ('reserved', 'occupied')
        """;

    /// <summary>BR-26: leases still awaiting signatures after the 7-day invite → cancelled, room freed.</summary>
    public async Task<long> CancelExpiredLeaseInvitesAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var rooms = (await connection.QueryAsync<long>(new CommandDefinition(
            """
            UPDATE app.leases SET status = 'cancelled', cancelled_at = now(), version = version + 1
            WHERE status = 'awaiting_signatures' AND invite_expires_at < now()
            RETURNING room_id
            """, transaction: tx, cancellationToken: ct))).ToArray();
        await connection.ExecuteAsync(new CommandDefinition(RecomputeRooms, new { Rooms = rooms }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return rooms.Length;
    }

    /// <summary>BR-33: active leases whose end_date (Vietnam calendar) has come → ended, room freed.</summary>
    public async Task<long> EndFinishedLeasesAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var rooms = (await connection.QueryAsync<long>(new CommandDefinition(
            """
            UPDATE app.leases SET status = 'ended', ended_at = now(), version = version + 1
            WHERE status = 'active' AND end_date <= (now() AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
            RETURNING room_id
            """, transaction: tx, cancellationToken: ct))).ToArray();
        await connection.ExecuteAsync(new CommandDefinition(RecomputeRooms, new { Rooms = rooms }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return rooms.Length;
    }

    /// <summary>1. verified listings past expires_at → expired (D19).</summary>
    public async Task<long> ExpireListingsAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            WITH u AS (
                UPDATE app.listings SET status = 'expired', version = version + 1
                WHERE status = 'verified' AND expires_at < now()
                RETURNING id)
            INSERT INTO app.listing_transitions (listing_id, from_status, to_status, actor_id)
            SELECT id, 'verified', 'expired', app.system_user_id() FROM u
            """, cancellationToken: ct));
    }

    /// <summary>2. hidden for more than 7 days without an open appeal → removed (BR-04).</summary>
    public async Task<long> RemoveStaleHiddenAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            WITH h AS (
                SELECT l.id FROM app.listings l
                JOIN LATERAL (SELECT t.created_at FROM app.listing_transitions t
                              WHERE t.listing_id = l.id ORDER BY t.id DESC LIMIT 1) last ON true
                WHERE l.status = 'hidden' AND last.created_at < now() - interval '7 days'
                  AND NOT EXISTS (SELECT 1 FROM app.appeals a WHERE a.listing_id = l.id AND a.status = 'open')),
            u AS (UPDATE app.listings l SET status = 'removed', version = version + 1
                  FROM h WHERE l.id = h.id RETURNING l.id)
            INSERT INTO app.listing_transitions (listing_id, from_status, to_status, actor_id, reason)
            SELECT id, 'hidden', 'removed', app.system_user_id(), 'Quá hạn kháng nghị 7 ngày' FROM u
            """, cancellationToken: ct));
    }

    /// <summary>
    /// 3. expired quick checks without an open report (D18): files first, then rows (CASCADE removes images, results,
    /// identifier links and reports; identifier_stats stays). A file that cannot be deleted keeps its scan for tomorrow.
    /// </summary>
    public async Task<long> DeleteExpiredScansAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<(long ScanId, string? Key)>(new CommandDefinition(
            """
            SELECT s.id, si.object_key
            FROM app.scans s LEFT JOIN app.scan_images si ON si.scan_id = s.id
            WHERE s.expires_at < now()
              AND NOT EXISTS (SELECT 1 FROM app.reports r WHERE r.scan_id = s.id AND r.status = 'open')
            ORDER BY s.id
            LIMIT 5000
            """, cancellationToken: ct))).ToList();

        var deletable = new List<long>();
        foreach (var scan in rows.GroupBy(r => r.ScanId))
        {
            try
            {
                foreach (var key in scan.Select(r => r.Key).OfType<string>()) await storage.DeleteAsync(scanBucket, key, ct);
                deletable.Add(scan.Key);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Scan {ScanId} kept: image delete failed ({ExceptionType})", scan.Key, ex.GetType().Name);
            }
        }
        if (deletable.Count == 0) return 0;
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM app.scans s
            WHERE s.id = ANY(@Ids) AND s.expires_at < now()
              AND NOT EXISTS (SELECT 1 FROM app.reports r WHERE r.scan_id = s.id AND r.status = 'open')
            """, new { Ids = deletable.ToArray() }, cancellationToken: ct));
    }

    /// <summary>4. outbox rows published more than 7 days ago.</summary>
    public async Task<long> PurgeOutboxAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM app.outbox_messages WHERE published_at < now() - interval '7 days'", cancellationToken: ct));
    }

    /// <summary>5. statistics views (MAINTAIN privilege, PostgreSQL 17+).</summary>
    public async Task<long> RefreshStatsAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        foreach (var view in new[] { "reporting.daily_scans", "reporting.daily_moderation", "reporting.risk_distribution" })
            await connection.ExecuteAsync(new CommandDefinition($"REFRESH MATERIALIZED VIEW CONCURRENTLY {view}", cancellationToken: ct));
        return 3;
    }
}
