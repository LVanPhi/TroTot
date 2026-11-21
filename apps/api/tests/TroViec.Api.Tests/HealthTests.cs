using System.Net;
using Shouldly;

namespace TroViec.Api.Tests;

[Collection(nameof(InfraCollection))]
public sealed class HealthTests(InfraFixture infra)
{
    [Fact]
    public async Task Live_Always_Returns200()
    {
        await using var factory = new ApiFactory(infra);
        var response = await factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_AllDependenciesUp_Returns200()
    {
        await using var factory = new ApiFactory(infra);
        var response = await factory.CreateClient().GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("ConnectionStrings:Redis", "127.0.0.1:1")]
    [InlineData("ConnectionStrings:Main", "Host=127.0.0.1;Port=1;Database=troviec;Username=app_api;Password=x;Timeout=2")]
    [InlineData("S3:SecretKey", "wrong-secret")]
    public async Task Ready_DependencyBroken_Returns503WithoutDetails(string key, string value)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(infra, overrides: new Dictionary<string, string?> { [key] = value });

        var response = await factory.CreateClient().GetAsync("/health/ready", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        body.ShouldBe("Unhealthy");
    }
}
