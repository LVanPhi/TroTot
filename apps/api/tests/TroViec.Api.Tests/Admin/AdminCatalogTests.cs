using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Shouldly;
using TroViec.Api.Tests.Auth;

namespace TroViec.Api.Tests.Admin;

/// <summary>T3.10: criteria (US-802), model versions (US-803), statistics (US-804), full audit (US-805).</summary>
[Collection(nameof(InfraCollection))]
public sealed class AdminCatalogTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private AuthClient Client() => new(_factory.CreateClient(), infra);

    private Task<TestUser> Admin() => TestUsers.CreateAsync(Client(), infra, ["admin"]);

    private async Task<T> Db<T>(string sql, object? args = null)
    {
        await using var c = new NpgsqlConnection(infra.MigratorConnectionString);
        return (await c.ExecuteScalarAsync<T>(sql, args))!;
    }

    [Fact]
    public async Task Criteria_CreateUpdateDeactivate_CodeUniqueAndImmutable()
    {
        var admin = await Admin();
        var code = "test_" + new string(Guid.NewGuid().ToString("N").Where(char.IsLetter).Take(8).ToArray());
        var create = new { code, name = "Đòi chuyển khoản qua ví lạ", guidance = "Tin yêu cầu chuyển tiền qua ví điện tử không rõ", scope = "both", legalBasis = (string?)null };

        var created = await Client().SendAsync(HttpMethod.Post, "/api/v1/admin/criteria", admin.Token, create);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await AuthClient.ReadJsonAsync(created)).GetProperty("id").GetInt32();
        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Post, "/api/v1/admin/criteria", admin.Token, create))).ShouldBe("criteria.code_taken");
        (await Client().SendAsync(HttpMethod.Post, "/api/v1/admin/criteria", admin.Token, new { code = "Bad-Code", name = "x", guidance = "y", scope = "both" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var updated = await Client().SendAsync(HttpMethod.Put, $"/api/v1/admin/criteria/{id}", admin.Token,
            new { name = "Đòi chuyển tiền qua ví lạ", guidance = "Hướng dẫn mới", scope = "room", legalBasis = "Điều 174 BLHS", isActive = false });
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await AuthClient.ReadJsonAsync(updated);
        (body.GetProperty("code").GetString(), body.GetProperty("scope").GetString(), body.GetProperty("isActive").GetBoolean()).ShouldBe((code, "room", false));

        // Inactive criteria disappear from the public list but stay in the admin list (no delete).
        var publicList = await AuthClient.ReadJsonAsync(await _factory.CreateClient().GetAsync("/api/v1/meta/criteria", TestContext.Current.CancellationToken));
        publicList.EnumerateArray().Select(c => c.GetProperty("code").GetString()).ShouldNotContain(code);
        var adminList = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, "/api/v1/admin/criteria", admin.Token));
        adminList.EnumerateArray().Select(c => c.GetProperty("code").GetString()).ShouldContain(code);
    }

    [Fact]
    public async Task ModelVersions_AggregatorShowsThresholds_ActivateSwitchesInOneTransaction_UnknownAggregatorRefused()
    {
        var admin = await Admin();
        var list = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, "/api/v1/admin/model-versions", admin.Token));
        var agg = list.EnumerateArray().Single(m => m.GetProperty("component").GetString() == "aggregator" && m.GetProperty("isActive").GetBoolean());
        (agg.GetProperty("thresholds").GetProperty("caution").GetInt32(), agg.GetProperty("thresholds").GetProperty("high").GetInt32()).ShouldBe((40, 70));

        var unknownAgg = await Db<short>("INSERT INTO app.model_versions (component, name, version) VALUES ('aggregator', 'weighted-rules', @V) RETURNING id",
            new { V = "agg-x" + Guid.NewGuid().ToString("N")[..6] });
        (await AuthClient.ProblemCodeAsync(await Client().SendAsync(HttpMethod.Post, $"/api/v1/admin/model-versions/{unknownAgg}/activate", admin.Token)))
            .ShouldBe("model.not_implemented");

        var oldOcr = await Db<short>("SELECT id FROM app.model_versions WHERE component = 'ocr' AND is_active");
        var newOcr = await Db<short>("INSERT INTO app.model_versions (component, name, version) VALUES ('ocr', 'tesseract-vie', @V) RETURNING id",
            new { V = "t" + Guid.NewGuid().ToString("N")[..6] });
        try
        {
            var activated = await Client().SendAsync(HttpMethod.Post, $"/api/v1/admin/model-versions/{newOcr}/activate", admin.Token);
            activated.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await Db<int>("SELECT count(*)::int FROM app.model_versions WHERE component = 'ocr' AND is_active")).ShouldBe(1);
            (await Db<bool>("SELECT is_active FROM app.model_versions WHERE id = @Id", new { Id = oldOcr })).ShouldBeFalse();
        }
        finally
        {
            (await Client().SendAsync(HttpMethod.Post, $"/api/v1/admin/model-versions/{oldOcr}/activate", admin.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        (await Db<bool>("SELECT is_active FROM app.model_versions WHERE id = @Id", new { Id = oldOcr })).ShouldBeTrue();
    }

    [Fact]
    public async Task Stats_ReadFromRefreshedViews()
    {
        var admin = await Admin();
        await using (var c = new NpgsqlConnection(infra.MigratorConnectionString))
        {
            await c.ExecuteAsync(
                """
                WITH s AS (INSERT INTO app.scans (source, input_text, input_sha256, expires_at, status, completed_at)
                           VALUES ('quick_check', 'x', sha256(gen_random_uuid()::text::bytea), now() + interval '30 days', 'completed', now())
                           RETURNING id)
                INSERT INTO app.assessments (scan_id, aggregator_model_id, score, risk_level, text_signal)
                SELECT s.id, (SELECT id FROM app.model_versions WHERE component = 'aggregator' AND is_active), 85, 'high', 0.9 FROM s
                """);
            await c.ExecuteAsync("REFRESH MATERIALIZED VIEW reporting.daily_scans; REFRESH MATERIALIZED VIEW reporting.risk_distribution; REFRESH MATERIALIZED VIEW reporting.daily_moderation;");
        }

        var stats = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, "/api/v1/admin/stats", admin.Token));

        var today = stats.GetProperty("scans").EnumerateArray().Where(d => d.GetProperty("source").GetString() == "quick_check").ToList();
        today.ShouldNotBeEmpty();
        today.Sum(d => d.GetProperty("highCount").GetInt64()).ShouldBeGreaterThanOrEqualTo(1);
        stats.GetProperty("risk").EnumerateArray().Any(b => b.GetProperty("scoreBucket").GetInt32() == 80).ShouldBeTrue();
        (await Client().SendAsync(HttpMethod.Get, "/api/v1/admin/stats?from=2026-12-31&to=2026-01-01", admin.Token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Audit_TransitionsAndDocumentAccess_FilterByListingAndViewer()
    {
        var admin = await Admin();
        var owner = await TestUsers.CreateAsync(Client(), infra, ["owner"]);
        var listing = await Db<Guid>(
            """
            WITH l AS (INSERT INTO app.listings (poster_id, listing_type, title, description, ward_code, street_address, location)
                       VALUES (@Owner, 'room', 'Tin nhật ký', 'Phòng trọ khép kín gần trường, có điều hòa và wifi', '90001', 'Số 1',
                               ST_SetSRID(ST_MakePoint(105.7, 21.0), 4326)::geography) RETURNING id, public_id),
                 t AS (INSERT INTO app.listing_transitions (listing_id, from_status, to_status, actor_id) SELECT id, NULL, 'draft', @Owner FROM l)
            SELECT public_id FROM l
            """, new { Owner = owner.Id });

        var transitions = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, $"/api/v1/admin/audit/transitions?listingId={listing}", admin.Token));
        var item = transitions.GetProperty("items").EnumerateArray().Single();
        (item.GetProperty("toStatus").GetString(), item.GetProperty("actorId").GetInt64()).ShouldBe(("draft", owner.Id));

        var docs = await AuthClient.ReadJsonAsync(await Client().SendAsync(HttpMethod.Get, $"/api/v1/admin/audit/document-access?viewerId={owner.Id}", admin.Token));
        docs.GetProperty("total").GetInt64().ShouldBe(0);

        var moderator = await TestUsers.CreateAsync(Client(), infra, ["moderator"]);
        (await Client().SendAsync(HttpMethod.Get, "/api/v1/admin/audit/transitions", moderator.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _ = JsonValueKind.Null;
    }
}
