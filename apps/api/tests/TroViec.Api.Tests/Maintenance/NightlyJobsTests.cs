using System.Collections.Concurrent;
using System.Net;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using TroViec.Api.Tests.Admin;
using TroViec.Api.Tests.Auth;
using TroViec.Maintenance;

namespace TroViec.Api.Tests.Maintenance;

/// <summary>T3.11: nightly jobs as app_maintenance (US-901, D18, D19) and decision emails (US-902).</summary>
[Collection(nameof(InfraCollection))]
public sealed class NightlyJobsTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private sealed class FakeStorage(Func<string, bool>? fail = null) : IObjectDeleter
    {
        public ConcurrentBag<string> Deleted { get; } = [];

        public Task DeleteAsync(string bucket, string key, CancellationToken ct)
        {
            if (fail?.Invoke(key) == true) throw new IOException("storage down");
            Deleted.Add($"{bucket}/{key}");
            return Task.CompletedTask;
        }
    }

    private NightlyJobs Jobs(IObjectDeleter storage, out NpgsqlDataSource db)
    {
        db = NpgsqlDataSource.Create(infra.MaintenanceConnectionString);
        return new NightlyJobs(db, storage, "scan-images", NullLogger<NightlyJobs>.Instance);
    }

    private async Task<T> Sql<T>(string sql, object? args = null)
    {
        await using var c = new NpgsqlConnection(infra.MigratorConnectionString);
        return (await c.ExecuteScalarAsync<T>(sql, args))!;
    }

    private async Task<long> ListingAsync(string status, string expires, string lastTransitionAgo, string? via = null)
    {
        var owner = await TestUsers.CreateAsync(new AuthClient(_factory.CreateClient(), infra), infra, ["owner"]);
        return await Sql<long>(
            $"""
            WITH l AS (INSERT INTO app.listings (poster_id, listing_type, title, description, ward_code, street_address, location, status, expires_at)
                       VALUES (@Owner, 'room', 'Tin job đêm', 'Phòng trọ khép kín gần trường, có điều hòa và wifi', '90001', 'Số 1',
                               ST_SetSRID(ST_MakePoint(105.7, 21.0), 4326)::geography, @Status::app.listing_status, now() + interval '{expires}')
                       RETURNING id),
                 t AS (INSERT INTO app.listing_transitions (listing_id, from_status, to_status, actor_id, reason, created_at)
                       SELECT id, @Via::app.listing_status, @Status::app.listing_status, app.system_user_id(),
                              CASE WHEN @Status = 'hidden' THEN 'Ẩn thử' END, now() - interval '{lastTransitionAgo}' FROM l)
            SELECT id FROM l
            """, new { Owner = owner.Id, Status = status, Via = via ?? "pending_review" });
    }

    private Task<string> StatusAsync(long id) => Sql<string>("SELECT status::text FROM app.listings WHERE id = @Id", new { Id = id });

    [Fact]
    public async Task ExpireListings_VerifiedPastExpiry_BecomesExpiredWithSystemTransition()   // D19
    {
        var expired = await ListingAsync("verified", "-1 hour", "1 day");
        var fresh = await ListingAsync("verified", "30 days", "1 day");

        await Jobs(new FakeStorage(), out var db).ExpireListingsAsync(CancellationToken.None);
        await db.DisposeAsync();

        (await StatusAsync(expired)).ShouldBe("expired");
        (await StatusAsync(fresh)).ShouldBe("verified");
        (await Sql<bool>(
            "SELECT actor_id = app.system_user_id() FROM app.listing_transitions WHERE listing_id = @Id AND to_status = 'expired'",
            new { Id = expired })).ShouldBeTrue();
        (await Sql<int>("SELECT version FROM app.listings WHERE id = @Id", new { Id = expired })).ShouldBe(2);
    }

    [Fact]
    public async Task RemoveHidden_AfterSevenDaysWithoutOpenAppeal_ButNotWithOne()
    {
        var stale = await ListingAsync("hidden", "30 days", "8 days", "verified");
        var recent = await ListingAsync("hidden", "30 days", "2 days", "verified");
        // The appeal was filed while the decision was fresh (the trigger refuses older ones); the decision is then
        // back-dated in test data only, to reach the 7-day boundary without waiting.
        var appealed = await ListingAsync("hidden", "30 days", "1 day", "verified");
        await Sql<int>(
            "INSERT INTO app.appeals (listing_id, listing_transition_id, reason) SELECT listing_id, id, 'Đã sửa' FROM app.listing_transitions WHERE listing_id = @Id",
            new { Id = appealed });
        await Sql<int>("UPDATE app.listing_transitions SET created_at = now() - interval '8 days' WHERE listing_id = @Id", new { Id = appealed });

        await Jobs(new FakeStorage(), out var db).RemoveStaleHiddenAsync(CancellationToken.None);
        await db.DisposeAsync();

        (await StatusAsync(stale)).ShouldBe("removed");
        (await StatusAsync(recent)).ShouldBe("hidden");
        (await StatusAsync(appealed)).ShouldBe("hidden");
        (await Sql<string>("SELECT reason FROM app.listing_transitions WHERE listing_id = @Id AND to_status = 'removed'", new { Id = stale }))
            .ShouldBe("Quá hạn kháng nghị 7 ngày");
    }

    [Fact]
    public async Task DeleteExpiredScans_FilesThenRows_KeepsReportedScansAndIdentifierStats()   // D18
    {
        var reporter = await TestUsers.CreateAsync(new AuthClient(_factory.CreateClient(), infra), infra, ["student"]);
        async Task<long> Scan(string key)
        {
            var id = await Sql<long>(
                """
                INSERT INTO app.scans (source, input_text, input_sha256, expires_at, status, completed_at)
                VALUES ('quick_check', 'x', sha256(gen_random_uuid()::text::bytea), now() - interval '1 hour', 'completed', now() - interval '31 days')
                RETURNING id
                """);
            await Sql<int>("INSERT INTO app.scan_images (scan_id, image_no, object_key, sha256) VALUES (@Id, 1, @Key, sha256(@Key::bytea))",
                new { Id = id, Key = key });
            return id;
        }
        var old = await Scan($"t/{Guid.NewGuid():N}/1.webp");
        var reported = await Scan($"t/{Guid.NewGuid():N}/1.webp");
        await Sql<int>("INSERT INTO app.reports (reporter_id, scan_id, category) VALUES (@R, @S, 'scam')", new { R = reporter.Id, S = reported });
        var identifier = await Sql<long>(
            """
            WITH i AS (INSERT INTO app.identifiers (kind, value_hmac, hint) VALUES ('phone', sha256(gen_random_uuid()::text::bytea), '•••111') RETURNING id),
                 st AS (INSERT INTO app.identifier_stats (identifier_id, confirmed_report_count, last_confirmed_at) SELECT id, 2, now() FROM i)
            INSERT INTO app.scan_identifiers (scan_id, identifier_id) SELECT @S, id FROM i RETURNING identifier_id
            """, new { S = old });
        var storage = new FakeStorage();

        await Jobs(storage, out var db).DeleteExpiredScansAsync(CancellationToken.None);
        await db.DisposeAsync();

        (await Sql<int>("SELECT count(*)::int FROM app.scans WHERE id = @Id", new { Id = old })).ShouldBe(0);
        (await Sql<int>("SELECT count(*)::int FROM app.scans WHERE id = @Id", new { Id = reported })).ShouldBe(1);
        storage.Deleted.ShouldContain(d => d.StartsWith("scan-images/t/"));
        (await Sql<int>("SELECT confirmed_report_count FROM app.identifier_stats WHERE identifier_id = @Id", new { Id = identifier })).ShouldBe(2);
    }

    [Fact]
    public async Task DeleteExpiredScans_FileDeleteFails_RowIsKeptForNextNight_OtherJobsStillRun()
    {
        var key = $"t/{Guid.NewGuid():N}/1.webp";
        var scan = await Sql<long>(
            """
            INSERT INTO app.scans (source, input_text, input_sha256, expires_at, status, completed_at)
            VALUES ('quick_check', 'x', sha256(gen_random_uuid()::text::bytea), now() - interval '1 hour', 'completed', now() - interval '31 days')
            RETURNING id
            """);
        await Sql<int>("INSERT INTO app.scan_images (scan_id, image_no, object_key, sha256) VALUES (@Id, 1, @Key, sha256(@Key::bytea))", new { Id = scan, Key = key });

        var results = await Jobs(new FakeStorage(k => k == key), out var db).RunAsync(CancellationToken.None);
        await db.DisposeAsync();

        (await Sql<int>("SELECT count(*)::int FROM app.scans WHERE id = @Id", new { Id = scan })).ShouldBe(1);
        results.Select(r => r.Name).ShouldBe(["expire_listings", "remove_hidden_listings", "delete_expired_scans", "purge_outbox", "refresh_stats"]);
        results.ShouldAllBe(r => r.Ok);   // refresh_stats also proves the MAINTAIN grant works for app_maintenance
    }

    [Fact]
    public async Task PurgeOutbox_PublishedMoreThanSevenDaysAgo()
    {
        var oldId = await Sql<long>("INSERT INTO app.outbox_messages (listing_id, published_at) SELECT id, now() - interval '8 days' FROM app.listings LIMIT 1 RETURNING id");
        var pending = await Sql<long>("INSERT INTO app.outbox_messages (listing_id) SELECT id FROM app.listings LIMIT 1 RETURNING id");

        await Jobs(new FakeStorage(), out var db).PurgeOutboxAsync(CancellationToken.None);
        await db.DisposeAsync();

        (await Sql<int>("SELECT count(*)::int FROM app.outbox_messages WHERE id = @Id", new { Id = oldId })).ShouldBe(0);
        (await Sql<int>("SELECT count(*)::int FROM app.outbox_messages WHERE id = @Id", new { Id = pending })).ShouldBe(1);
        await Sql<int>("DELETE FROM app.outbox_messages WHERE id = @Id", new { Id = pending });
    }

    [Fact]
    public async Task ApproveAndReject_EmailThePoster()   // US-902
    {
        var client = new AuthClient(_factory.CreateClient(), infra);
        var poster = await TestUsers.CreateAsync(client, infra, ["owner"]);
        var moderator = await TestUsers.CreateAsync(client, infra, ["moderator"]);
        async Task<Guid> Pending() => await Sql<Guid>(
            """
            WITH l AS (INSERT INTO app.listings (poster_id, listing_type, title, description, ward_code, street_address, location, status)
                       VALUES (@Owner, 'room', 'Tin chờ email', 'Phòng trọ khép kín gần trường, có điều hòa và wifi', '90001', 'Số 1',
                               ST_SetSRID(ST_MakePoint(105.7, 21.0), 4326)::geography, 'pending_review') RETURNING id, public_id),
                 t AS (INSERT INTO app.listing_transitions (listing_id, from_status, to_status, actor_id) SELECT id, 'screening', 'pending_review', app.system_user_id() FROM l)
            SELECT public_id FROM l
            """, new { Owner = poster.Id });

        (await client.SendAsync(HttpMethod.Post, $"/api/v1/moderation/listings/{await Pending()}/approve", moderator.Token, new { version = 1 }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(HttpMethod.Post, $"/api/v1/moderation/listings/{await Pending()}/reject", moderator.Token, new { version = 1, reason = "Thiếu ảnh phòng" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Bodies of the poster's emails (registration code, approval, rejection).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        IReadOnlyList<string> bodies = [];
        while (DateTime.UtcNow < deadline && bodies.Count < 3)
        {
            bodies = await client.EmailsToAsync(poster.Email);
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
        bodies.ShouldContain(b => b.Contains("đã được cán bộ xác minh duyệt"));
        bodies.ShouldContain(b => b.Contains("chưa được duyệt") && b.Contains("Thiếu ảnh phòng") && b.Contains("kháng nghị trong 7 ngày"));
    }
}
