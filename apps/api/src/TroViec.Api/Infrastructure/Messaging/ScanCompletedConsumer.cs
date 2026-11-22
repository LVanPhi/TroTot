using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
using StackExchange.Redis;
using TroViec.Api.Features.Listings;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Api.Realtime;

namespace TroViec.Api.Infrastructure.Messaging;

/// <summary>Payload of scan.completed (docs/05-ai-worker.md, Hợp đồng hàng đợi). Ids are internal ids.</summary>
public sealed record ScanCompletedEvent(long ScanId, Guid PublicId, string Status, string Source, long? ListingId);

/// <summary>
/// What the API does with one scan.completed event. Idempotent: the worker may replay an event (docs/05-ai-worker.md,
/// Idempotent), and Redis may redeliver one.
/// </summary>
public sealed class ScanCompletedHandler(
    NpgsqlDataSource db,
    IHubContext<ScanHub> hub,
    ListingTransitionService transitions,
    ModerationNotifier moderation,
    ILogger<ScanCompletedHandler> logger)
{
    public async Task HandleAsync(ScanCompletedEvent evt, CancellationToken ct)
    {
        if (evt.ListingId is { } listingId)
            await AdvanceListingAsync(evt.ScanId, listingId, ct);
        else if (evt.Source == "quick_check")
            await hub.Clients.Group(ScanHub.GroupName(evt.PublicId))
                .SendAsync(nameof(ScanUpdated), new ScanUpdated(evt.PublicId, evt.Status), ct);
    }

    /// <summary>
    /// screening → pending_review, but only for the listing's latest scan while the listing is still screening:
    /// an old or replayed event must not move a listing that is being screened again (docs/02-architecture.md, bước 4).
    /// </summary>
    private async Task AdvanceListingAsync(long scanId, long listingId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var listing = await connection.QuerySingleOrDefaultAsync<(string Status, int Version, long? LatestScanId)?>(
            new CommandDefinition(
                """
                SELECT l.status::text, l.version,
                       (SELECT s.id FROM app.scans s WHERE s.listing_id = l.id ORDER BY s.created_at DESC, s.id DESC LIMIT 1)
                FROM app.listings l WHERE l.id = @ListingId
                FOR UPDATE OF l
                """, new { ListingId = listingId }, tx, cancellationToken: ct));

        if (listing is not { Status: "screening" } current || current.LatestScanId != scanId)
        {
            logger.LogInformation("scan.completed ignored for listing {ListingId}, scan {ScanId}", listingId, scanId);
            return;
        }

        var systemUser = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("SELECT app.system_user_id()", transaction: tx, cancellationToken: ct));
        await transitions.TransitionAsync(connection, tx, listingId, current.Version, ListingStatus.Screening,
            ListingStatus.PendingReview, systemUser, reason: null, ct: ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Listing {ListingId} screened by scan {ScanId}, now pending_review", listingId, scanId);
        await moderation.QueueChangedAsync(ct);
    }
}

/// <summary>
/// Reads troviec:scan-completed with consumer group "api" (docs/02-architecture.md, Hợp đồng hàng đợi). An entry is
/// XACKed after it is handled; when handling fails (database down) it stays pending and is claimed again later.
/// Malformed entries are logged by id and acknowledged so they cannot block the stream.
/// </summary>
public sealed class ScanCompletedConsumer(
    IConnectionMultiplexer redis,
    ScanCompletedHandler handler,
    ILogger<ScanCompletedConsumer> logger) : BackgroundService
{
    public const string Group = "api";
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RetryPendingAfter = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _consumer = $"api-{Environment.MachineName}-{Guid.NewGuid():N}"[..40];
    private bool _groupReady;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextReclaim = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            try
            {
                if (DateTimeOffset.UtcNow >= nextReclaim)
                {
                    handled += await ReclaimPendingAsync(stoppingToken);
                    nextReclaim = DateTimeOffset.UtcNow + RetryPendingAfter;
                }
                handled += await ReadNewAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                _groupReady = false;
                logger.LogWarning("scan-completed consumer waiting for Redis: {ExceptionType}", ex.GetType().Name);
            }

            if (handled == 0)
            {
                try { await Task.Delay(IdleDelay, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Reads up to 20 new entries for this consumer and handles them; returns how many were read.</summary>
    public async Task<int> ReadNewAsync(CancellationToken ct)
    {
        var stream = redis.GetDatabase();
        await EnsureGroupAsync(stream);
        var entries = await stream.StreamReadGroupAsync(Streams.ScanCompleted, Group, _consumer, ">", count: 20);
        foreach (var entry in entries) await HandleEntryAsync(stream, entry, ct);
        return entries.Length;
    }

    /// <summary>Entries delivered to any api consumer but not acknowledged for 30 s are taken over and retried.</summary>
    public async Task<int> ReclaimPendingAsync(CancellationToken ct)
    {
        var stream = redis.GetDatabase();
        await EnsureGroupAsync(stream);
        var claimed = await stream.StreamAutoClaimAsync(Streams.ScanCompleted, Group, _consumer,
            (long)RetryPendingAfter.TotalMilliseconds, "0-0", count: 20);
        foreach (var entry in claimed.ClaimedEntries) await HandleEntryAsync(stream, entry, ct);
        return claimed.ClaimedEntries.Length;
    }

    private async Task HandleEntryAsync(IDatabase stream, StreamEntry entry, CancellationToken ct)
    {
        ScanCompletedEvent? evt = null;
        try
        {
            evt = JsonSerializer.Deserialize<ScanCompletedEvent>((string)entry["payload"]!, Json);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentNullException or NotSupportedException) { }

        if (evt is null)
        {
            logger.LogWarning("Malformed scan.completed entry {MessageId} acknowledged", entry.Id.ToString());
            await stream.StreamAcknowledgeAsync(Streams.ScanCompleted, Group, entry.Id);
            return;
        }

        try
        {
            await handler.HandleAsync(evt, ct);
        }
        catch (ProblemException ex) when (ex.Code is "listing.version_conflict" or "appeal.pending" or "listing.invalid_transition")
        {
            // The listing changed under us; nothing left to do for this event.
            logger.LogInformation("scan.completed for scan {ScanId} skipped: {Code}", evt.ScanId, ex.Code);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            logger.LogWarning("scan.completed for scan {ScanId} left pending: {ExceptionType}", evt.ScanId, ex.GetType().Name);
            return;
        }

        await stream.StreamAcknowledgeAsync(Streams.ScanCompleted, Group, entry.Id);
    }

    private async Task EnsureGroupAsync(IDatabase stream)
    {
        if (_groupReady) return;
        try
        {
            await stream.StreamCreateConsumerGroupAsync(Streams.ScanCompleted, Group, "0-0", createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal)) { }
        _groupReady = true;
    }
}
