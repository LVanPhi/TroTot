using System.Text.Json;
using Dapper;
using Npgsql;
using StackExchange.Redis;

namespace TroViec.Api.Infrastructure.Messaging;

/// <summary>Redis stream names and entry shape (docs/05-ai-worker.md, Hợp đồng hàng đợi).</summary>
public static class Streams
{
    public const string ScanRequested = "troviec:scan-requested";
    public const string ListingEmbed = "troviec:listing-embed";
    public const string ScanCompleted = "troviec:scan-completed";
    public const int MaxLength = 10_000;

    public static NameValueEntry[] Entry(string type, object payload) =>
        [new("type", type), new("payload", JsonSerializer.Serialize(payload))];
}

/// <summary>Lets a request wake the relay right after its commit instead of waiting for the next poll.</summary>
public sealed class OutboxSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// Transactional outbox → Redis Streams (docs/03-database.md, Outbox relay). Polls every 500 ms (or when signalled),
/// locks unpublished rows with SKIP LOCKED, XADDs them, marks them published. Every minute, scans still queued
/// 5 minutes after publishing are published again (Redis lost them). Duplicates are fine: the worker is idempotent.
/// </summary>
public sealed class OutboxRelay(
    NpgsqlDataSource db,
    IConnectionMultiplexer redis,
    OutboxSignal signal,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RequeueEvery = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextRequeue = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextRequeue)
                {
                    await RequeueStaleAsync(stoppingToken);
                    nextRequeue = DateTimeOffset.UtcNow + RequeueEvery;
                }
                await PublishPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is NpgsqlException or RedisException or TimeoutException)
            {
                // Redis or PostgreSQL briefly down: rows stay unpublished and are retried on the next tick.
                logger.LogWarning("Outbox relay waiting: {ExceptionType}", ex.GetType().Name);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try { await signal.WaitAsync(PollEvery, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Publishes up to 100 pending messages in one transaction; returns how many were sent.</summary>
    public async Task<int> PublishPendingAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var rows = (await connection.QueryAsync<(long Id, long? ScanId, long? ListingId)>(new CommandDefinition(
            """
            SELECT id, scan_id, listing_id FROM app.outbox_messages
            WHERE published_at IS NULL
            ORDER BY id
            LIMIT 100
            FOR UPDATE SKIP LOCKED
            """, transaction: tx, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return 0;

        var stream = redis.GetDatabase();
        foreach (var row in rows)
        {
            if (row.ScanId is { } scanId)
                await stream.StreamAddAsync(Streams.ScanRequested, Streams.Entry("scan.requested", new { scanId }),
                    maxLength: Streams.MaxLength, useApproximateMaxLength: true);
            else
                await stream.StreamAddAsync(Streams.ListingEmbed, Streams.Entry("listing.embed", new { listingId = row.ListingId }),
                    maxLength: Streams.MaxLength, useApproximateMaxLength: true);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.outbox_messages SET published_at = now() WHERE id = ANY(@Ids)",
            new { Ids = rows.Select(r => r.Id).ToArray() }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        logger.LogInformation("Outbox published {Count} message(s)", rows.Count);
        return rows.Count;
    }

    /// <summary>Scans no worker picked up within 5 minutes of publishing go out again.</summary>
    public async Task<int> RequeueStaleAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE app.outbox_messages o SET published_at = NULL
            FROM app.scans s
            WHERE o.scan_id = s.id AND s.status = 'queued'
              AND o.published_at < now() - interval '5 minutes'
            """, cancellationToken: ct));
    }
}

public static class ScanServicesSetup
{
    public static IServiceCollection AddScanServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimiting.QuotaOptions>().Configure<IConfiguration>((o, config) => config.GetSection("Quota").Bind(o));
        services.AddSingleton<RateLimiting.QuotaService>();
        services.AddSingleton<OutboxSignal>();
        services.AddSingleton<OutboxRelay>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxRelay>());
        services.AddSingleton<Features.Listings.ListingTransitionService>();
        services.AddSingleton<ScanCompletedHandler>();
        services.AddSingleton<ScanCompletedConsumer>();
        services.AddHostedService(sp => sp.GetRequiredService<ScanCompletedConsumer>());
        services.AddSignalR();
        return services;
    }
}
