using System.Net;
using Shouldly;

namespace TroViec.Api.Tests.Auth;

/// <summary>Dev web (another origin) may call the API with credentials; other origins get no CORS grant.</summary>
[Collection(nameof(InfraCollection))]
public sealed class CorsTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData(ApiFactory.SiteUrl, true)]
    [InlineData("http://evil.test", false)]
    public async Task Preflight_Refresh_AllowsOnlyConfiguredOriginWithCredentials(string origin, bool allowed)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/refresh");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "x-requested-with");

        var response = await _factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowOrigin).ShouldBe(allowed);
        if (allowed)
        {
            allowOrigin!.Single().ShouldBe(origin);
            response.Headers.GetValues("Access-Control-Allow-Credentials").Single().ShouldBe("true");
        }
    }
}
