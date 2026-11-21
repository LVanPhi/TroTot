using System.Net;
using Shouldly;
using TroViec.Api.Tests.Auth;

namespace TroViec.Api.Tests.Scans;

/// <summary>/meta reference data (fixture imports db/seed/administrative_units/sample.csv).</summary>
[Collection(nameof(InfraCollection))]
public sealed class MetaTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(infra, overrides: new Dictionary<string, string?>
    {
        ["Campuses:Json"] = """[{"code":"main","name":"Cơ sở chính","lat":21.0537,"lng":105.7353}]""",
    });

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private async Task<(HttpResponseMessage Response, System.Text.Json.JsonElement Body)> GetAsync(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path, TestContext.Current.CancellationToken);
        return (response, await AuthClient.ReadJsonAsync(response));
    }

    [Fact]
    public async Task Provinces_AreListedAndCachedForAnHour()
    {
        var (response, body) = await GetAsync("/api/v1/meta/provinces");

        response.Headers.CacheControl!.ToString().ShouldBe("public, max-age=3600");
        body.EnumerateArray().Select(p => p.GetProperty("code").GetString()).ShouldBe(["91", "90"]);   // ordered by name: "Tỉnh Mẫu Hai" < "Tỉnh Mẫu Một"
    }

    [Fact]
    public async Task CommunesOfProvince_IncludeProvinceName_UnknownProvinceIs404()
    {
        var (_, body) = await GetAsync("/api/v1/meta/provinces/90/communes");
        var (missing, _) = await GetAsync("/api/v1/meta/provinces/99/communes");

        body.GetArrayLength().ShouldBe(2);
        body[0].GetProperty("provinceName").GetString().ShouldBe("Tỉnh Mẫu Một");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("phuong mau a", "90001")]   // without diacritics
    [InlineData("Phường Mẫu C", "91001")]
    public async Task CommuneSearch_MatchesWithOrWithoutAccents(string q, string expectedCode)
    {
        var (_, body) = await GetAsync($"/api/v1/meta/communes?q={Uri.EscapeDataString(q)}");

        body[0].GetProperty("code").GetString().ShouldBe(expectedCode);
    }

    [Fact]
    public async Task CommuneSearch_TooShort_ReturnsEmpty()
    {
        var (_, body) = await GetAsync("/api/v1/meta/communes?q=a");

        body.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Amenities_OnlyActive()
    {
        var (_, body) = await GetAsync("/api/v1/meta/amenities");

        var codes = body.EnumerateArray().Select(a => a.GetProperty("code").GetString()).ToList();
        codes.ShouldContain("wifi");
        codes.ShouldNotContain("private_bathroom");   // deactivated by 0003
    }

    [Fact]
    public async Task Criteria_AndCampuses_AreReturned()
    {
        var (_, criteria) = await GetAsync("/api/v1/meta/criteria");
        var (_, campuses) = await GetAsync("/api/v1/meta/campuses");

        criteria.GetArrayLength().ShouldBeGreaterThan(0);
        criteria[0].TryGetProperty("guidance", out _).ShouldBeTrue();
        campuses[0].GetProperty("lat").GetDouble().ShouldBe(21.0537);
    }
}
