using Dapper;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Npgsql;
using Shouldly;
using StackExchange.Redis;
using TroViec.Api.Features.Listings;
using TroViec.Api.Infrastructure.Messaging;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Api.Realtime;
using TroViec.Api.Tests.Admin;
using TroViec.Api.Tests.Auth;

namespace TroViec.Api.Tests.Scans;

/// <summary>T2.5: scan.completed consumer, ScanHub, screening → pending_review.</summary>
[Collection(nameof(InfraCollection))]
public sealed class ScanResultTests(InfraFixture infra) : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private async Task<HubConnection> ConnectAsync()
    {
        _ = _factory.Server; // start the host
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, ScanHub.Path), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();
        await connection.StartAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private async Task PublishAsync(object payload)
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(infra.RedisConnectionString);
        await redis.GetDatabase().StreamAddAsync(Streams.ScanCompleted, Streams.Entry("scan.completed", payload));
    }

    private async Task<(long Id, Guid PublicId)> InsertQuickCheckAsync()
    {
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        return await connection.QuerySingleAsync<(long, Guid)>(
            """
            INSERT INTO app.scans (source, input_text, input_sha256, expires_at)
            VALUES ('quick_check', 'phòng trọ', sha256(convert_to(gen_random_uuid()::text, 'UTF8')), now() + interval '30 days')
            RETURNING id, public_id
            """);
    }

    private async Task<long> InsertListingAsync(string status)
    {
        var owner = await TestUsers.CreateAsync(new AuthClient(_factory.CreateClient(), infra), infra, ["owner"]);
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO app.listings (poster_id, listing_type, title, description, ward_code, street_address, location, status)
            VALUES (@Owner, 'room', 'Phòng thử', 'Phòng trọ khép kín gần trường, có điều hòa và wifi', '90001', 'Số 1',
                    ST_SetSRID(ST_MakePoint(105.7, 21.0), 4326)::geography, @Status::app.listing_status)
            RETURNING id
            """, new { Owner = owner.Id, Status = status });
    }

    private async Task<long> InsertListingScanAsync(long listingId, int secondsAgo)
    {
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO app.scans (source, listing_id, input_text, input_sha256, status, completed_at, created_at)
            VALUES ('listing', @ListingId, 'Phòng thử', sha256(convert_to(gen_random_uuid()::text, 'UTF8')),
                    'completed', now(), now() - make_interval(secs => @Ago))
            RETURNING id
            """, new { ListingId = listingId, Ago = secondsAgo });
    }

    private async Task<(string Status, int Transitions)> ListingStateAsync(long listingId)
    {
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        return await connection.QuerySingleAsync<(string, int)>(
            """
            SELECT status::text, (SELECT count(*)::int FROM app.listing_transitions t
                                  WHERE t.listing_id = l.id AND t.to_status = 'pending_review')
            FROM app.listings l WHERE l.id = @Id
            """, new { Id = listingId });
    }

    private async Task WaitUntilAcknowledgedAsync()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(infra.RedisConnectionString);
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var info = await redis.GetDatabase().StreamGroupInfoAsync(Streams.ScanCompleted);
            var api = info.SingleOrDefault(g => g.Name == ScanCompletedConsumer.Group);
            if (api.Name is not null && api.PendingMessageCount == 0 && api.LastDeliveredId == (await LastIdAsync(redis)))
                return;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        throw new ShouldAssertException("scan.completed entries were not acknowledged in time");
    }

    private static async Task<string?> LastIdAsync(ConnectionMultiplexer redis)
    {
        var last = await redis.GetDatabase().StreamRangeAsync(Streams.ScanCompleted, "-", "+", count: 1, messageOrder: Order.Descending);
        return last.Length == 0 ? null : last[0].Id.ToString();
    }

    [Fact]
    public async Task Completed_QuickCheck_WatcherReceivesScanUpdated()
    {
        var scan = await InsertQuickCheckAsync();
        await using var hub = await ConnectAsync();
        var received = new TaskCompletionSource<ScanUpdated>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<ScanUpdated>(nameof(ScanUpdated), m => received.TrySetResult(m));
        await hub.InvokeAsync("Watch", scan.PublicId.ToString(), TestContext.Current.CancellationToken);

        await PublishAsync(new { scanId = scan.Id, publicId = scan.PublicId, status = "completed", source = "quick_check", listingId = (long?)null });

        var message = await received.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        message.ShouldBe(new ScanUpdated(scan.PublicId, "completed"));
        await WaitUntilAcknowledgedAsync();
    }

    [Fact]
    public async Task Watch_ListingScanOrUnknownId_IsRejected()
    {
        var listingId = await InsertListingAsync("screening");
        await InsertListingScanAsync(listingId, 0);
        Guid listingScan;
        await using (var connection = new NpgsqlConnection(infra.MigratorConnectionString))
            listingScan = await connection.ExecuteScalarAsync<Guid>("SELECT public_id FROM app.scans WHERE listing_id = @Id", new { Id = listingId });
        await using var hub = await ConnectAsync();

        foreach (var id in new[] { listingScan.ToString(), Guid.NewGuid().ToString(), "not-a-guid" })
        {
            var error = await Should.ThrowAsync<HubException>(() => hub.InvokeAsync("Watch", id, TestContext.Current.CancellationToken));
            error.Message.ShouldContain("scan.not_found");
        }
    }

    [Fact]
    public async Task Watch_MoreThanTenScans_IsRejected()
    {
        await using var hub = await ConnectAsync();
        for (var i = 0; i < ScanHub.MaxGroupsPerConnection; i++)
            await hub.InvokeAsync("Watch", (await InsertQuickCheckAsync()).PublicId.ToString(), TestContext.Current.CancellationToken);

        var eleventh = (await InsertQuickCheckAsync()).PublicId.ToString();
        var error = await Should.ThrowAsync<HubException>(() => hub.InvokeAsync("Watch", eleventh, TestContext.Current.CancellationToken));
        error.Message.ShouldContain("scan.watch_limit");
    }

    [Fact]
    public async Task Completed_LatestListingScan_MovesToPendingReviewOnceEvenWhenReplayed()
    {
        _ = _factory.Server;
        var listingId = await InsertListingAsync("screening");
        var scanId = await InsertListingScanAsync(listingId, 0);
        var payload = new { scanId, publicId = Guid.NewGuid(), status = "completed", source = "listing", listingId };

        await PublishAsync(payload);
        await PublishAsync(payload);   // worker replay (docs/05-ai-worker.md, Idempotent)
        await WaitUntilAcknowledgedAsync();

        (await ListingStateAsync(listingId)).ShouldBe(("pending_review", 1));
    }

    [Fact]
    public async Task Completed_OlderListingScan_DoesNotMoveListingScreenedAgain()
    {
        _ = _factory.Server;
        var listingId = await InsertListingAsync("screening");
        var oldScan = await InsertListingScanAsync(listingId, 600);
        await InsertListingScanAsync(listingId, 0);   // the listing was edited and sent for screening again

        await PublishAsync(new { scanId = oldScan, publicId = Guid.NewGuid(), status = "completed", source = "listing", listingId });
        await WaitUntilAcknowledgedAsync();

        (await ListingStateAsync(listingId)).ShouldBe(("screening", 0));
    }

    [Fact]
    public async Task Completed_MalformedEntry_IsAcknowledged()
    {
        _ = _factory.Server;
        using var redis = await ConnectionMultiplexer.ConnectAsync(infra.RedisConnectionString);
        await redis.GetDatabase().StreamAddAsync(Streams.ScanCompleted, [new("type", "scan.completed"), new("payload", "{not json")]);

        await WaitUntilAcknowledgedAsync();
    }

    // ---- ListingTransitionService ---------------------------------------------------------------

    [Fact]
    public async Task ListingLifecycle_IsAllowed_MatchesDatabaseFunction()
    {
        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        foreach (var from in Enum.GetValues<ListingStatus>())
        foreach (var to in Enum.GetValues<ListingStatus>())
        {
            var db = await connection.ExecuteScalarAsync<bool>(
                "SELECT app.listing_transition_allowed(@From::app.listing_status, @To::app.listing_status)",
                new { From = Infrastructure.Db.DbEnum.ToDb(from), To = Infrastructure.Db.DbEnum.ToDb(to) });
            ListingLifecycle.IsAllowed(from, to).ShouldBe(db, $"{from} → {to}");
        }
    }

    private async Task<ProblemException?> TryTransitionAsync(long listingId, int version, ListingStatus from, ListingStatus to,
        long? actorId = null, TransitionRules? rules = null)
    {
        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var actor = actorId ?? await connection.ExecuteScalarAsync<long>("SELECT app.system_user_id()", transaction: tx);
        try
        {
            await new ListingTransitionService().TransitionAsync(connection, tx, listingId, version, from, to, actor,
                reason: null, rules: rules, ct: TestContext.Current.CancellationToken);
            await tx.CommitAsync(TestContext.Current.CancellationToken);
            return null;
        }
        catch (ProblemException ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task Transition_WrongVersionOrEdge_Throws()
    {
        var listingId = await InsertListingAsync("screening");

        (await TryTransitionAsync(listingId, 5, ListingStatus.Screening, ListingStatus.PendingReview))!.Code.ShouldBe("listing.version_conflict");
        (await TryTransitionAsync(listingId, 1, ListingStatus.Screening, ListingStatus.Verified))!.Code.ShouldBe("listing.invalid_transition");
        (await TryTransitionAsync(listingId, 1, ListingStatus.Screening, ListingStatus.PendingReview)).ShouldBeNull();
        (await ListingStateAsync(listingId)).ShouldBe(("pending_review", 1));
    }

    [Fact]
    public async Task Transition_OpenAppeal_OnlyTheAppealDecisionMayMoveListing()
    {
        var listingId = await InsertListingAsync("rejected");
        await using (var connection = new NpgsqlConnection(infra.MigratorConnectionString))
            await connection.ExecuteAsync(
                """
                WITH t AS (
                  INSERT INTO app.listing_transitions (listing_id, from_status, to_status, actor_id, reason)
                  VALUES (@Id, 'pending_review', 'rejected', app.system_user_id(), 'Thiếu ảnh') RETURNING id)
                INSERT INTO app.appeals (listing_id, listing_transition_id, reason) SELECT @Id, t.id, 'Đã bổ sung' FROM t
                """, new { Id = listingId });

        (await TryTransitionAsync(listingId, 1, ListingStatus.Rejected, ListingStatus.Draft))!.Code.ShouldBe("appeal.pending");
        (await TryTransitionAsync(listingId, 1, ListingStatus.Rejected, ListingStatus.Draft, rules: new(ResolvesAppeal: true))).ShouldBeNull();
    }

    [Fact]
    public async Task Transition_ModeratorIsPoster_ThrowsConflictOfInterest()
    {
        var listingId = await InsertListingAsync("pending_review");
        long posterId;
        await using (var connection = new NpgsqlConnection(infra.MigratorConnectionString))
            posterId = await connection.ExecuteScalarAsync<long>("SELECT poster_id FROM app.listings WHERE id = @Id", new { Id = listingId });

        var error = await TryTransitionAsync(listingId, 1, ListingStatus.PendingReview, ListingStatus.Rejected, posterId,
            new TransitionRules(ModeratorAction: true));

        error!.Code.ShouldBe("moderation.conflict_of_interest");
    }
}
