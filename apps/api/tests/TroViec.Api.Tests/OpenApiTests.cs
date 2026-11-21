using System.Net;
using System.Text.Json;
using Shouldly;

namespace TroViec.Api.Tests;

[Collection(nameof(InfraCollection))]
public sealed class OpenApiTests(InfraFixture infra)
{
    [Fact]
    public async Task OpenApiDocument_Development_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(infra);

        var response = await factory.CreateClient().GetAsync("/openapi/v1.json", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        doc.RootElement.GetProperty("openapi").GetString().ShouldNotBeNull();
    }

    [Fact]
    public async Task OpenApiDocument_Production_Returns404Problem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(infra, environment: "Production");

        var response = await factory.CreateClient().GetAsync("/openapi/v1.json", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        doc.RootElement.GetProperty("code").GetString().ShouldBe("resource.not_found");
        doc.RootElement.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
    }
}
