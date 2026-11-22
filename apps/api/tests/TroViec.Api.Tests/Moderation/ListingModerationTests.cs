using System.Net;
using Dapper;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Npgsql;
using Shouldly;
using TroViec.Api.Realtime;
using TroViec.Api.Tests.Admin;
using TroViec.Api.Tests.Auth;

namespace TroViec.Api.Tests.Moderation;

/// <summary>T3.4: queue, review screen, approve/reject/hide/restore, ModerationHub (US-601–604, BR-19).</summary>
[Collection(nameof(InfraCollection))]
public sealed class ListingModerationTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private AuthClient Client() => new(_factory.CreateClient(), infra);

    private Task<TestUser> ModeratorAsync() => TestUsers.CreateAsync(Client(), infra, ["moderator", "owner"]);

    private async Task<T> Db<T>(Func<NpgsqlConnection, Task<T>> work)
    {
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        return await work(connection);
    }

    /// <summary>A listing in <paramref name="status"/> whose last transition reached it, optionally with a scored screening.</summary>
    private async Task<(Guid Id, long InternalId)> ListingAsync(long posterId, string status = "pending_review", short? score = null,
        string? via = null) =>
        await Db(async c =>
        {
            var (id, publicId) = await c.QuerySingleAsync<(long, Guid)>(
                """
                INSERT INTO app.listings (poster_id, listing_type, title, description, ward_code, street_address, location, status, expires_at)
                VALUES (@Poster, 'room', 'Phòng chờ duyệt', 'Phòng trọ khép kín gần trường, có điều hòa và wifi', '90001', 'Số 1',
                        ST_SetSRID(ST_MakePoint(105.7, 21.0), 4326)::geography, @Status::app.listing_status,
                        CASE WHEN @Status IN ('verified', 'hidden') THEN now() + interval '30 days' END)
                RETURNING id, public_id
                """, new { Poster = posterId, Status = status });
            await c.ExecuteAsync(
                """
                INSERT INTO app.listing_transitions (listing_id, from_status, to_status, actor_id, reason)
                VALUES (@Id, @From::app.listing_status, @Status::app.listing_status, app.system_user_id(), @Reason)
                """, new { Id = id, Status = status, From = via ?? "screening", Reason = status is "hidden" or "rejected" ? "Lý do" : null });
            if (score is { } s)
                await c.ExecuteAsync(
                    """
                    WITH sc AS (INSERT INTO app.scans (source, listing_id, input_text, input_sha256, status, completed_at)
                                VALUES ('listing', @Id, 'x', sha256(gen_random_uuid()::text::bytea), 'completed', now()) RETURNING id)
                    INSERT INTO app.assessments (scan_id, aggregator_model_id, score, risk_level, text_signal)
                    SELECT sc.id, (SELECT id FROM app.model_versions WHERE component = 'aggregator' AND is_active), @Score,
                           CASE WHEN @Score >= 70 THEN 'high' WHEN @Score >= 40 THEN 'caution' ELSE 'low' END::app.risk_level, 0.5
                    FROM sc
                    """, new { Id = id, Score = s });
            return (publicId, id);
        });

    private Task<HttpResponseMessage> DecideAsync(TestUser user, Guid id, string action, int version = 1, string? reason = null) =>
        Client().SendAsync(HttpMethod.Post, $"/api/v1/moderation/listings/{id}/{action}", user.Token, new { version, reason });

    [Fact]
    public async Task Queue_AiFailuresFirst_ThenHighestScore()
    {
        var poster = await TestUsers.CreateAsync(Client(), infra, ["owner"]);
        var low = await ListingAsync(poster.Id, score: 20);
        var failed = await ListingAsync(poster.Id);
        var high = await ListingAsync(poster.Id, score: 90);
        var moderator = await ModeratorAsync();

        var queue = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, "/api/v1/moderation/queue?pageSize=50", moderator.Token));

        var ours = queue.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("listingId").GetGuid())
            .Where(g => g == low.Id || g == failed.Id || g == high.Id).ToList();
        ours.ShouldBe([failed.Id, high.Id, low.Id]);
    }

    [Fact]
    public async Task ReviewScreen_HasAiResultHistoryWithNamesAndPoster()
    {
        var poster = await TestUsers.CreateAsync(Client(), infra, ["owner"]);
        var (id, _) = await ListingAsync(poster.Id, score: 75);
        var moderator = await ModeratorAsync();

        var screen = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, $"/api/v1/moderation/listings/{id}", moderator.Token));

        screen.GetProperty("listing").GetProperty("status").GetString().ShouldBe("pending_review");
        screen.GetProperty("screening").GetProperty("assessment").GetProperty("score").GetInt32().ShouldBe(75);
        screen.GetProperty("history").EnumerateArray().Single().GetProperty("actor").GetString().ShouldNotBe("Cán bộ xác minh");
        screen.GetProperty("poster").GetProperty("userId").GetInt64().ShouldBe(poster.Id);
        screen.GetProperty("poster").GetProperty("phone").GetString().ShouldStartWith("+849");
    }

    [Fact]
    public async Task Approve_SetsNinetyDaysAndEmbeddingOutbox_SecondConcurrentApproveGets409()
    {
        var poster = await TestUsers.CreateAsync(Client(), infra, ["owner"]);
        var (id, internalId) = await ListingAsync(poster.Id, score: 10);
        var first = await ModeratorAsync();
        var second = await ModeratorAsync();

        var responses = await Task.WhenAll(DecideAsync(first, id, "approve"), DecideAsync(second, id, "approve"));

        responses.Select(r => r.StatusCode).OrderBy(s => s).ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await AuthClient.ProblemCodeAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict))).ShouldBe("listing.version_conflict");
        var (status, days, outbox) = await Db(c => c.QuerySingleAsync<(string, double, int)>(
            """
            SELECT status::text, extract(epoch FROM expires_at - now()) / 86400,
                   (SELECT count(*)::int FROM app.outbox_messages o WHERE o.listing_id = l.id)
            FROM app.listings l WHERE l.id = @Id
            """, new { Id = internalId }));
        (status, outbox).ShouldBe(("verified", 1));
        days.ShouldBeInRange(89.9, 90.1);
    }

    [Fact]
    public async Task RejectAndHide_NeedReason_OwnListingIsConflictOfInterest()
    {
        var poster = await TestUsers.CreateAsync(Client(), infra, ["owner"]);
        var moderator = await ModeratorAsync();
        var (pending, _) = await ListingAsync(poster.Id);
        var (published, _) = await ListingAsync(poster.Id, "verified", via: "pending_review");

        (await DecideAsync(moderator, pending, "reject")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DecideAsync(moderator, published, "hide", reason: "  ")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DecideAsync(moderator, pending, "reject", reason: "Ảnh không đúng phòng")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DecideAsync(moderator, published, "hide", reason: "Nhiều báo cáo")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var (own, _) = await ListingAsync(moderator.Id);
        (await AuthClient.ProblemCodeAsync(await DecideAsync(moderator, own, "approve"))).ShouldBe("moderation.conflict_of_interest");
    }

    [Fact]
    public async Task Restore_WithOpenAppeal_IsAppealPending_OtherwiseBackToVerified()
    {
        var poster = await TestUsers.CreateAsync(Client(), infra, ["owner"]);
        var moderator = await ModeratorAsync();
        var (hidden, hiddenId) = await ListingAsync(poster.Id, "hidden", via: "verified");
        var (appealed, appealedId) = await ListingAsync(poster.Id, "hidden", via: "verified");
        await Db(c => c.ExecuteAsync(
            """
            INSERT INTO app.appeals (listing_id, listing_transition_id, reason)
            SELECT listing_id, id, 'Đã sửa' FROM app.listing_transitions WHERE listing_id = @Id
            """, new { Id = appealedId }));

        (await AuthClient.ProblemCodeAsync(await DecideAsync(moderator, appealed, "restore"))).ShouldBe("appeal.pending");
        var ok = await DecideAsync(moderator, hidden, "restore");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AuthClient.ReadJsonAsync(ok)).GetProperty("status").GetString().ShouldBe("verified");
        _ = hiddenId;
    }

    [Fact]
    public async Task Hub_ModeratorReceivesListingUpdated_StudentCannotConnect()
    {
        var poster = await TestUsers.CreateAsync(Client(), infra, ["owner"]);
        var (id, _) = await ListingAsync(poster.Id);
        var moderator = await ModeratorAsync();
        var received = new TaskCompletionSource<ListingUpdated>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = Connect(moderator.Token);
        hub.On<ListingUpdated>(nameof(ListingUpdated), u => { if (u.ListingId == id) received.TrySetResult(u); });
        await hub.StartAsync(TestContext.Current.CancellationToken);

        (await DecideAsync(moderator, id, "approve")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBe(new ListingUpdated(id, "verified", 2));
        var (studentToken, _) = await Client().SignedInAsync("student");
        await using var denied = Connect(studentToken);
        await Should.ThrowAsync<Exception>(() => denied.StartAsync(TestContext.Current.CancellationToken));
    }

    private HubConnection Connect(string token) => new HubConnectionBuilder()
        .WithUrl(new Uri(_factory.Server.BaseAddress, ModerationHub.Path), options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            options.AccessTokenProvider = () => Task.FromResult<string?>(token);
        })
        .Build();
}
