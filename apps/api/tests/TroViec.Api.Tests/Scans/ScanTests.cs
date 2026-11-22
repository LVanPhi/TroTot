using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using StackExchange.Redis;
using TroViec.Api.Infrastructure.Messaging;
using TroViec.Api.Infrastructure.Storage;
using TroViec.Api.Tests.Admin;
using TroViec.Api.Tests.Auth;

namespace TroViec.Api.Tests.Scans;

/// <summary>US-201, US-204, US-205 (API side), BR-05, BR-07, BR-20, outbox.</summary>
[Collection(nameof(InfraCollection))]
public sealed class ScanTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private HttpClient Http() => _factory.CreateClient();

    private static async Task<Guid> ScanIdAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await AuthClient.ReadJsonAsync(response)).GetProperty("scanId").GetGuid();
    }

    [Fact]
    public async Task Create_TextOnly_Returns202QueuedAndWritesScanAndOutbox()
    {
        var response = await ScanClient.PostAsync(Http(), ScanClient.NewIp(), text: "Cho thuê phòng giá rẻ, chuyển cọc trước");

        var scanId = await ScanIdAsync(response);
        (await AuthClient.ReadJsonAsync(response)).GetProperty("status").GetString().ShouldBe("queued");
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/scans/{scanId}");
        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        var row = await connection.QuerySingleAsync<(string Source, string Status, double DaysLeft, int Outbox)>(
            """
            SELECT s.source::text, s.status::text, extract(epoch FROM s.expires_at - now()) / 86400,
                   (SELECT count(*)::int FROM app.outbox_messages o WHERE o.scan_id = s.id)
            FROM app.scans s WHERE s.public_id = @Id
            """, new { Id = scanId });
        (row.Source, row.Status, row.Outbox).ShouldBe(("quick_check", "queued", 1));
        row.DaysLeft.ShouldBeInRange(29.9, 30.1);   // US-201 AC5
    }

    [Fact]
    public async Task Create_Scan_ReachesRedisStreamWithInternalScanId()
    {
        var scanId = await ScanIdAsync(await ScanClient.PostAsync(Http(), ScanClient.NewIp(), text: "phòng trọ " + Guid.NewGuid()));
        long internalId;
        await using (var connection = new NpgsqlConnection(infra.ApiConnectionString))
            internalId = await connection.ExecuteScalarAsync<long>("SELECT id FROM app.scans WHERE public_id = @Id", new { Id = scanId });

        using var redis = await ConnectionMultiplexer.ConnectAsync(infra.RedisConnectionString);
        StreamEntry? entry = null;
        for (var i = 0; i < 50 && entry is null; i++)
        {
            entry = (await redis.GetDatabase().StreamRangeAsync(Streams.ScanRequested))
                .Cast<StreamEntry?>()
                .FirstOrDefault(e => JsonDocument.Parse((string)e!.Value["payload"]!).RootElement.GetProperty("scanId").GetInt64() == internalId);
            if (entry is null) await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        entry.ShouldNotBeNull();
        ((string)entry.Value["type"]!).ShouldBe("scan.requested");
    }

    [Fact]
    public async Task Create_WithImages_StoresWebpUnderServerKeysAndRows()
    {
        var scanId = await ScanIdAsync(await ScanClient.PostAsync(Http(), ScanClient.NewIp(), images: [ScanClient.Png(), ScanClient.Png(64, 48)]));

        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        var keys = (await connection.QueryAsync<string>(
            "SELECT si.object_key FROM app.scan_images si JOIN app.scans s ON s.id = si.scan_id WHERE s.public_id = @Id ORDER BY si.image_no",
            new { Id = scanId })).ToList();
        keys.ShouldBe([$"{scanId}/1.webp", $"{scanId}/2.webp"]);
        var storage = _factory.Services.GetRequiredService<IObjectStorage>();
        var bucket = _factory.Services.GetRequiredService<IOptions<StorageBuckets>>().Value.ScanImages;
        var stored = await storage.GetAsync(bucket, keys[0], TestContext.Current.CancellationToken);
        stored![8..12].ShouldBe("WEBP"u8.ToArray());
    }

    [Theory]
    [InlineData(null, 0, 422, "scan.input_required")]
    [InlineData(null, 6, 422, "image.too_many")]
    public async Task Create_InvalidInput_RejectedWithCode(string? text, int images, int status, string code)
    {
        var response = await ScanClient.PostAsync(Http(), ScanClient.NewIp(), text: text, images: Enumerable.Range(0, images).Select(_ => ScanClient.Png()));

        ((int)response.StatusCode).ShouldBe(status);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe(code);
    }

    [Theory]
    [InlineData("text", "x", null, null)]
    [InlineData("claimedRentVnd", "phòng", "50000", null)]
    [InlineData("claimedWardCode", "phòng", null, "99999")]
    [InlineData("claimedWardCode", "phòng", null, "90")]   // a province, not a commune
    public async Task Create_BadField_Returns400WithFieldError(string field, string text, string? rent, string? ward)
    {
        if (field == "text") text = new string('a', 10_001);

        var response = await ScanClient.PostAsync(Http(), ScanClient.NewIp(), text: text, rent: rent, ward: ward);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AuthClient.ReadJsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Create_ValidRentAndWard_AreStored()
    {
        var scanId = await ScanIdAsync(await ScanClient.PostAsync(Http(), ScanClient.NewIp(), text: "phòng 2tr", rent: "2000000", ward: "90001"));

        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        (await connection.QuerySingleAsync<(int, string)>(
            "SELECT claimed_rent_vnd, claimed_ward_code FROM app.scans WHERE public_id = @Id", new { Id = scanId }))
            .ShouldBe((2_000_000, "90001"));
    }

    [Fact]
    public async Task Create_SixthGuestScanSameDay_Returns429WithRetryAfter_AndRejectedRequestsDoNotCount()
    {
        var http = Http();
        var ip = ScanClient.NewIp();
        (await ScanClient.PostAsync(http, ip)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);   // not counted
        (await ScanClient.PostAsync(http, ip, images: [[0x25, 0x50, 0x44, 0x46]])).StatusCode
            .ShouldBe(HttpStatusCode.UnsupportedMediaType);                                              // not counted
        for (var i = 0; i < 5; i++) await ScanIdAsync(await ScanClient.PostAsync(http, ip, text: $"tin {i}"));

        var sixth = await ScanClient.PostAsync(http, ip, text: "tin 6");

        sixth.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await AuthClient.ProblemCodeAsync(sixth)).ShouldBe("scan.quota_exceeded");
        int.Parse(sixth.Headers.GetValues("Retry-After").Single()).ShouldBeInRange(1, 86_400);
        var quota = await AuthClient.ReadJsonAsync(await QuotaAsync(http, ip));
        (quota.GetProperty("limit").GetInt32(), quota.GetProperty("remaining").GetInt32()).ShouldBe((5, 0));
    }

    [Fact]
    public async Task Quota_SignedInUser_UsesAccountLimit()
    {
        var client = new AuthClient(Http(), infra);
        var (token, _) = await client.SignedInAsync();

        var quota = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get, "/api/v1/scans/quota", token));

        (quota.GetProperty("limit").GetInt32(), quota.GetProperty("remaining").GetInt32()).ShouldBe((50, 50));
    }

    [Fact]
    public async Task Get_QueuedScan_ReturnsNullAssessment()
    {
        var scanId = await ScanIdAsync(await ScanClient.PostAsync(Http(), ScanClient.NewIp(), text: "phòng"));

        var body = await AuthClient.ReadJsonAsync(await Http().GetAsync($"/api/v1/scans/{scanId}", TestContext.Current.CancellationToken));

        body.GetProperty("status").GetString().ShouldBe("queued");
        body.GetProperty("assessment").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("canReport").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Get_CompletedScan_ReturnsFullAssessmentShape()
    {
        var scanId = await ScanIdAsync(await ScanClient.PostAsync(Http(), ScanClient.NewIp(), text: "cọc trước", rent: "1000000", ward: "90001"));
        await CompleteWithResultAsync(scanId);

        var a = (await AuthClient.ReadJsonAsync(await Http().GetAsync($"/api/v1/scans/{scanId}", TestContext.Current.CancellationToken)))
            .GetProperty("assessment");

        (a.GetProperty("score").GetInt32(), a.GetProperty("riskLevel").GetString()).ShouldBe((98, "high"));
        a.GetProperty("signals").GetProperty("text").GetDouble().ShouldBe(0.9);
        a.GetProperty("signals").GetProperty("image").ValueKind.ShouldBe(JsonValueKind.Null);
        a.GetProperty("flags")[0].GetProperty("evidence").GetString().ShouldBe("chuyển cọc giữ chỗ ngay, liên hệ [SĐT]");
        a.GetProperty("imageMatches").GetArrayLength().ShouldBe(0);
        var price = a.GetProperty("price");
        (price.GetProperty("rentVnd").GetInt32(), price.GetProperty("rentSource").GetString(), price.GetProperty("medianVnd").GetInt32())
            .ShouldBe((1_000_000, "claimed", 2_600_000));
        var community = a.GetProperty("community")[0];
        (community.GetProperty("kind").GetString(), community.GetProperty("hint").GetString(), community.GetProperty("confirmedReports").GetInt32())
            .ShouldBe(("phone", "•••321", 3));
    }

    [Fact]
    public async Task ListingScreeningScan_IsNotServedByScansEndpoints()
    {
        var listingScanId = await InsertListingScreeningScanAsync();
        var client = new AuthClient(Http(), infra);
        var (token, _) = await client.SignedInAsync();

        var get = await Http().GetAsync($"/api/v1/scans/{listingScanId}", TestContext.Current.CancellationToken);
        var report = await client.SendAsync(HttpMethod.Post, $"/api/v1/scans/{listingScanId}/reports", token, new { category = "scam" });

        (get.StatusCode, await AuthClient.ProblemCodeAsync(get)).ShouldBe((HttpStatusCode.NotFound, "scan.not_found"));
        (report.StatusCode, await AuthClient.ProblemCodeAsync(report)).ShouldBe((HttpStatusCode.NotFound, "scan.not_found"));
    }

    [Fact]
    public async Task Report_KeepsScanOneYear_AndSecondOpenReportIs409()
    {
        var client = new AuthClient(Http(), infra);
        var (token, _) = await client.SignedInAsync();
        var scanId = await ScanIdAsync(await ScanClient.PostAsync(Http(), ScanClient.NewIp(), text: "phòng", token: token));

        var first = await client.SendAsync(HttpMethod.Post, $"/api/v1/scans/{scanId}/reports", token, new { category = "scam", description = "Đã mất cọc" });
        var second = await client.SendAsync(HttpMethod.Post, $"/api/v1/scans/{scanId}/reports", token, new { category = "scam" });

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await AuthClient.ReadJsonAsync(first)).GetProperty("id").GetInt64().ShouldBeGreaterThan(0);
        (second.StatusCode, await AuthClient.ProblemCodeAsync(second)).ShouldBe((HttpStatusCode.Conflict, "report.already_open"));
        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        (await connection.ExecuteScalarAsync<double>(
            "SELECT extract(epoch FROM expires_at - now()) / 86400 FROM app.scans WHERE public_id = @Id", new { Id = scanId }))
            .ShouldBeInRange(364, 367);
    }

    [Fact]
    public async Task MyScans_ListsOnlyOwnQuickChecks_NewestFirst()
    {
        var client = new AuthClient(Http(), infra);
        var me = await TestUsers.CreateAsync(client, infra, ["student"]);
        var other = await TestUsers.CreateAsync(client, infra, ["student"]);
        var first = await ScanIdAsync(await ScanClient.PostAsync(Http(), ScanClient.NewIp(), text: "một", token: me.Token));
        var second = await ScanIdAsync(await ScanClient.PostAsync(Http(), ScanClient.NewIp(), text: "hai", token: me.Token));
        await ScanIdAsync(await ScanClient.PostAsync(Http(), ScanClient.NewIp(), text: "khác", token: other.Token));

        var page = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get, "/api/v1/me/scans", me.Token));

        page.GetProperty("total").GetInt64().ShouldBe(2);
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("scanId").GetGuid()).ShouldBe([second, first]);
    }

    [Fact]
    public async Task Create_WhileRedisIsDown_Still202_AndRelayPublishesOnceRedisIsBack()
    {
        Guid scanId;
        await using (var down = new ApiFactory(infra, overrides: new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = "127.0.0.1:1,connectTimeout=200" }))
            scanId = await ScanIdAsync(await ScanClient.PostAsync(down.CreateClient(), ScanClient.NewIp(), text: "redis tắt"));

        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        var outboxId = await connection.ExecuteScalarAsync<long>(
            "SELECT o.id FROM app.outbox_messages o JOIN app.scans s ON s.id = o.scan_id WHERE s.public_id = @Id", new { Id = scanId });
        (await connection.ExecuteScalarAsync<DateTime?>("SELECT published_at FROM app.outbox_messages WHERE id = @Id", new { Id = outboxId }))
            .ShouldBeNull();

        // A factory with a working Redis starts its relay; it (or this explicit call) publishes the waiting row.
        DateTime? publishedAt = null;
        for (var i = 0; i < 50 && publishedAt is null; i++)
        {
            await _factory.Services.GetRequiredService<OutboxRelay>().PublishPendingAsync(TestContext.Current.CancellationToken);
            publishedAt = await connection.ExecuteScalarAsync<DateTime?>(
                "SELECT published_at FROM app.outbox_messages WHERE id = @Id", new { Id = outboxId });
            if (publishedAt is null) await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        publishedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Relay_ScanStillQueuedFiveMinutesAfterPublishing_IsPublishedAgain()
    {
        var scanId = await ScanIdAsync(await ScanClient.PostAsync(Http(), ScanClient.NewIp(), text: "bị mất trong Redis"));
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        await connection.ExecuteAsync(
            """
            UPDATE app.outbox_messages o SET published_at = now() - interval '6 minutes'
            FROM app.scans s WHERE o.scan_id = s.id AND s.public_id = @Id
            """, new { Id = scanId });

        var requeued = await _factory.Services.GetRequiredService<OutboxRelay>().RequeueStaleAsync(TestContext.Current.CancellationToken);

        requeued.ShouldBeGreaterThanOrEqualTo(1);
        (await connection.ExecuteScalarAsync<DateTime?>(
            "SELECT o.published_at FROM app.outbox_messages o JOIN app.scans s ON s.id = o.scan_id WHERE s.public_id = @Id",
            new { Id = scanId })).ShouldBeNull();
    }

    private static Task<HttpResponseMessage> QuotaAsync(HttpClient http, string ip)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/scans/quota");
        request.Headers.Add(ApiFactory.TestIpHeader, ip);
        return http.SendAsync(request);
    }

    /// <summary>Writes what the worker would (as app_migrator, which owns the tables) so the read side can be checked.</summary>
    private async Task CompleteWithResultAsync(Guid scanId)
    {
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        await connection.ExecuteAsync(
            """
            WITH s AS (SELECT id FROM app.scans WHERE public_id = @Id),
                 agg AS (SELECT id FROM app.model_versions WHERE component = 'aggregator' AND is_active),
                 a AS (
                   INSERT INTO app.assessments (scan_id, aggregator_model_id, score, risk_level, text_signal, price_signal,
                                                community_signal, price_median_vnd, price_sample_size, price_vnd)
                   SELECT s.id, agg.id, 98, 'high', 0.9, 0.95, 0.78, 2600000, 14, 1000000 FROM s, agg RETURNING id),
                 f AS (
                   INSERT INTO app.assessment_flags (assessment_id, criterion_id, evidence_text)
                   SELECT a.id, (SELECT min(id) FROM app.criteria), 'chuyển cọc giữ chỗ ngay, liên hệ [SĐT]' FROM a),
                 i AS (
                   INSERT INTO app.identifiers (kind, value_hmac, hint)
                   VALUES ('phone', sha256(convert_to(gen_random_uuid()::text, 'UTF8')), '•••321') RETURNING id),
                 st AS (
                   INSERT INTO app.identifier_stats (identifier_id, confirmed_report_count, last_confirmed_at)
                   SELECT i.id, 3, now() FROM i)
            INSERT INTO app.scan_identifiers (scan_id, identifier_id) SELECT s.id, i.id FROM s, i;
            UPDATE app.scans SET status = 'completed', completed_at = now() WHERE public_id = @Id;
            """, new { Id = scanId });
    }

    private async Task<Guid> InsertListingScreeningScanAsync()
    {
        var client = new AuthClient(Http(), infra);
        var owner = await TestUsers.CreateAsync(client, infra, ["owner"]);
        await using var connection = new NpgsqlConnection(infra.MigratorConnectionString);
        return await connection.ExecuteScalarAsync<Guid>(
            """
            WITH l AS (
              INSERT INTO app.listings (poster_id, listing_type, title, description, ward_code, street_address, location)
              VALUES (@Owner, 'room', 'Phòng thử', 'Phòng trọ khép kín gần trường, có điều hòa và wifi', '90001', 'Số 1', ST_SetSRID(ST_MakePoint(105.7, 21.0), 4326)::geography)
              RETURNING id)
            INSERT INTO app.scans (source, listing_id, input_text, input_sha256)
            SELECT 'listing', l.id, 'Phòng thử', sha256(convert_to(gen_random_uuid()::text, 'UTF8')) FROM l
            RETURNING public_id
            """, new { Owner = owner.Id });
    }
}
