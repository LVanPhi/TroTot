using System.Globalization;
using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using TroViec.Api.Features.Search;
using TroViec.Api.Tests.Admin;
using TroViec.Api.Tests.Auth;

namespace TroViec.Api.Tests.Search;

/// <summary>T3.6: GET /listings filters, GET /listings/search modes (filter, fulltext, hybrid), public detail (US-401–403).</summary>
[Collection(nameof(InfraCollection))]
public sealed class SearchTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly FakeEmbeddings _embeddings = new();
    private ApiFactory? _factory;

    /// <summary>Embedding down by default (no service at Embed:BaseUrl); tests that need vectors set _embeddings.Vector.</summary>
    private ApiFactory Factory => _factory ??= new ApiFactory(infra, overrides: new Dictionary<string, string?>
    {
        ["Campuses:Json"] = """[{"code":"T","name":"Cơ sở thử","lat":11.5,"lng":106.5}]""",
    })
    { TestServices = s => s.AddSingleton<IEmbeddingClient>(_embeddings) };

    public ValueTask DisposeAsync() => _factory?.DisposeAsync() ?? ValueTask.CompletedTask;

    private sealed class FakeEmbeddings : IEmbeddingClient
    {
        public QueryVector? Vector { get; set; }

        public Task<QueryVector?> EmbedAsync(string text, CancellationToken ct) => Task.FromResult(Vector);
    }

    /// <summary>Each test searches around its own point, so listings of other tests never show up.</summary>
    private readonly (double Lat, double Lng) _here = (9 + Random.Shared.NextDouble() * 2, 104 + Random.Shared.NextDouble() * 2);

    private static float[] Unit(int i)
    {
        var v = new float[1024];
        v[i] = 1;
        return v;
    }

    private async Task<long> TextEmbeddingModelAsync()
    {
        await using var c = new NpgsqlConnection(infra.MigratorConnectionString);
        return await c.ExecuteScalarAsync<long>("SELECT id FROM app.model_versions WHERE component = 'text_embedding' AND is_active");
    }

    private async Task<(Guid Id, long InternalId)> RoomAsync(long poster, string title, int rent, bool selfContained, string[] amenities,
        string status = "verified", string expires = "30 days", double offsetM = 100, float[]? vector = null, long? model = null)
    {
        await using var c = new NpgsqlConnection(infra.MigratorConnectionString);
        var (id, publicId) = await c.QuerySingleAsync<(long, Guid)>(
            $"""
            INSERT INTO app.listings (poster_id, listing_type, title, description, ward_code, street_address, location, status, expires_at)
            VALUES (@Poster, 'room', @Title, 'Mô tả đủ dài cho tin phòng trọ thử nghiệm tìm kiếm', '90001', 'Số 1',
                    ST_SetSRID(ST_MakePoint(@Lng, @Lat), 4326)::geography, @Status::app.listing_status, now() + interval '{expires}')
            RETURNING id, public_id
            """, new { Poster = poster, Title = title, Status = status, Lat = _here.Lat + offsetM / 111_000, Lng = _here.Lng });
        await c.ExecuteAsync("INSERT INTO app.room_listings (listing_id, monthly_rent_vnd, area_m2, is_self_contained) VALUES (@Id, @Rent, 20, @Self)",
            new { Id = id, Rent = rent, Self = selfContained });
        await c.ExecuteAsync("INSERT INTO app.room_amenities (listing_id, amenity_id) SELECT @Id, id FROM app.amenities WHERE code = ANY(@Codes)",
            new { Id = id, Codes = amenities });
        if (vector is not null)
            await c.ExecuteAsync("INSERT INTO app.listing_embeddings (listing_id, embedding, model_version_id) VALUES (@Id, @V::vector, @M)",
                new { Id = id, V = "[" + string.Join(',', vector.Select(x => x.ToString(CultureInfo.InvariantCulture))) + "]", M = (short)model!.Value });
        return (publicId, id);
    }

    private string Near => $"lat={_here.Lat.ToString(CultureInfo.InvariantCulture)}&lng={_here.Lng.ToString(CultureInfo.InvariantCulture)}&radiusM=1000";

    private async Task<JsonElement> GetAsync(string path, string? token = null) =>
        await AuthClient.ReadJsonAsync(token is null
            ? await Factory.CreateClient().GetAsync(path, TestContext.Current.CancellationToken)
            : await new AuthClient(Factory.CreateClient(), infra).SendAsync(HttpMethod.Get, path, token));

    private static List<Guid> Ids(JsonElement body) =>
        body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("listingId").GetGuid()).ToList();

    [Fact]
    public async Task List_FiltersAndSort_ExpiredAndUnpublishedNeverShown()
    {
        var poster = await TestUsers.CreateAsync(new AuthClient(Factory.CreateClient(), infra), infra, ["owner"]);
        var cheap = await RoomAsync(poster.Id, "Phòng rẻ", 1_800_000, true, ["parking"], offsetM: 300);
        var dear = await RoomAsync(poster.Id, "Phòng đắt", 4_000_000, false, ["wifi"], offsetM: 100);
        var expired = await RoomAsync(poster.Id, "Phòng hết hạn", 1_500_000, true, ["parking"], expires: "-1 day");
        var hidden = await RoomAsync(poster.Id, "Phòng bị ẩn", 1_500_000, true, ["parking"], status: "hidden");

        Ids(await GetAsync($"/api/v1/listings?{Near}")).ShouldBe([dear.Id, cheap.Id]);   // by distance
        Ids(await GetAsync($"/api/v1/listings?{Near}&sort=rent_asc")).ShouldBe([cheap.Id, dear.Id]);
        Ids(await GetAsync($"/api/v1/listings?{Near}&maxRent=2500000&selfContained=true&amenities=parking")).ShouldBe([cheap.Id]);
        Ids(await GetAsync($"/api/v1/listings?{Near}&type=job")).ShouldBeEmpty();
        var all = Ids(await GetAsync($"/api/v1/listings?{Near}"));
        all.ShouldNotContain(expired.Id);
        all.ShouldNotContain(hidden.Id);
        (await Factory.CreateClient().GetAsync($"/api/v1/listings?{Near}&sort=title", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_WithoutCoordinates_SearchesAroundFirstCampus()
    {
        var body = await GetAsync("/api/v1/listings?radiusM=100");
        body.GetProperty("items").ValueKind.ShouldBe(JsonValueKind.Array);   // the campus (11.5, 106.5) is used, no 400
    }

    [Fact]
    public async Task Search_SentenceOnlyFilters_IsFilterModeWithAppliedFilters()
    {
        var poster = await TestUsers.CreateAsync(new AuthClient(Factory.CreateClient(), infra), infra, ["owner"]);
        var match = await RoomAsync(poster.Id, "Phòng trọ", 2_300_000, true, ["parking", "wifi"]);
        await RoomAsync(poster.Id, "Phòng trọ", 2_300_000, false, ["parking"]);
        await RoomAsync(poster.Id, "Phòng trọ", 2_900_000, true, ["parking"]);

        var body = await GetAsync($"/api/v1/listings/search?q={Uri.EscapeDataString("phòng khép kín dưới 2,5 triệu có chỗ để xe")}&{Near}");

        body.GetProperty("mode").GetString().ShouldBe("filter");
        var applied = body.GetProperty("appliedFilters");
        (applied.GetProperty("type").GetString(), applied.GetProperty("maxRent").GetInt32(), applied.GetProperty("selfContained").GetBoolean())
            .ShouldBe(("room", 2_500_000, true));
        applied.GetProperty("amenities").EnumerateArray().Select(a => a.GetString()).ShouldBe(["parking"]);
        Ids(body).ShouldBe([match.Id]);
    }

    [Fact]
    public async Task Search_EmbeddingDown_FallsBackToFulltext()
    {
        var poster = await TestUsers.CreateAsync(new AuthClient(Factory.CreateClient(), infra), infra, ["owner"]);
        var quiet = await RoomAsync(poster.Id, "Phòng yên tĩnh view hồ", 2_000_000, false, []);
        await RoomAsync(poster.Id, "Phòng gần chợ ồn ào", 2_000_000, false, []);

        var body = await GetAsync($"/api/v1/listings/search?q={Uri.EscapeDataString("phòng yên tĩnh")}&{Near}");

        body.GetProperty("mode").GetString().ShouldBe("fulltext");
        Ids(body).First().ShouldBe(quiet.Id);
    }

    [Fact]
    public async Task Search_Hybrid_UsesOnlyVectorsOfTheReturnedModel()
    {
        var poster = await TestUsers.CreateAsync(new AuthClient(Factory.CreateClient(), infra), infra, ["owner"]);
        var model = await TextEmbeddingModelAsync();
        long other;
        await using (var c = new NpgsqlConnection(infra.MigratorConnectionString))
            other = await c.ExecuteScalarAsync<long>(
                "INSERT INTO app.model_versions (component, name, version) VALUES ('text_embedding', 'old/model', @V) RETURNING id",
                new { V = Guid.NewGuid().ToString("N")[..8] });
        // Neither title contains the query words: only the vector branch can find them.
        var semantic = await RoomAsync(poster.Id, "Chỗ ở cho sinh viên", 2_000_000, false, [], vector: Unit(7), model: model);
        var otherModel = await RoomAsync(poster.Id, "Chỗ ở khác", 2_000_000, false, [], vector: Unit(7), model: other);
        _embeddings.Vector = new QueryVector(Unit(7), (int)model);

        var body = await GetAsync($"/api/v1/listings/search?q={Uri.EscapeDataString("nơi ở thoáng đãng")}&{Near}");

        body.GetProperty("mode").GetString().ShouldBe("hybrid");
        var ids = Ids(body);
        ids.ShouldContain(semantic.Id);
        ids.ShouldNotContain(otherModel.Id);
    }

    [Fact]
    public async Task Detail_PhoneOnlyWhenSignedIn_ReviewSummaryAndPoster()
    {
        var poster = await TestUsers.CreateAsync(new AuthClient(Factory.CreateClient(), infra), infra, ["owner"]);
        var listing = await RoomAsync(poster.Id, "Phòng chi tiết", 2_000_000, true, []);
        var student = await TestUsers.CreateAsync(new AuthClient(Factory.CreateClient(), infra), infra, ["student"]);

        var guest = await GetAsync($"/api/v1/listings/{listing.Id}");
        guest.GetProperty("contactPhone").ValueKind.ShouldBe(JsonValueKind.Null);
        guest.GetProperty("isSaved").ValueKind.ShouldBe(JsonValueKind.Null);
        guest.GetProperty("reviewSummary").GetProperty("count").GetInt32().ShouldBe(0);
        guest.GetProperty("poster").GetProperty("isBusiness").GetBoolean().ShouldBeFalse();

        var signedIn = await GetAsync($"/api/v1/listings/{listing.Id}", student.Token);
        signedIn.GetProperty("contactPhone").GetString()!.ShouldStartWith("+849");
        signedIn.GetProperty("isSaved").GetBoolean().ShouldBeFalse();
    }
}
