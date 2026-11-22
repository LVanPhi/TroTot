using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TroViec.Api.Features.Search;

namespace TroViec.Api.Tests.Search;

/// <summary>Any embedding failure yields null (search falls back to full-text), never an error for the user.</summary>
public sealed class EmbeddingClientTests
{
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return answer(request, ct);
        }
    }

    private static (EmbeddingClient Client, Stub Stub) Make(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer,
        string timeoutMs = "800")
    {
        var stub = new Stub(answer);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Embed:BaseUrl"] = "http://embed.test:8001",
            ["Embed:InternalToken"] = "tok",
            ["Embed:TimeoutMs"] = timeoutMs,
        }).Build();
        return (new EmbeddingClient(new HttpClient(stub), config, NullLogger<EmbeddingClient>.Instance), stub);
    }

    [Fact]
    public async Task Ok_ReturnsVectorAndModelVersion_SendsToken()
    {
        var (client, stub) = Make((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"model":"BAAI/bge-m3","modelVersionId":3,"dim":2,"vectors":[[0.6,0.8]]}""", Encoding.UTF8, "application/json"),
        }));

        var result = await client.EmbedAsync("phòng", TestContext.Current.CancellationToken);

        result!.ModelVersionId.ShouldBe(3);
        result.Vector.ShouldBe([0.6f, 0.8f]);
        stub.Last!.Headers.GetValues("X-Internal-Token").Single().ShouldBe("tok");
        stub.Last.RequestUri!.AbsolutePath.ShouldBe("/internal/embed");
    }

    [Fact]
    public async Task ServiceUnavailable_IsNull()
    {
        var (client, _) = Make((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        (await client.EmbedAsync("phòng", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ConnectionRefused_IsNull()
    {
        var (client, _) = Make((_, _) => throw new HttpRequestException("refused"));
        (await client.EmbedAsync("phòng", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task SlowerThanTimeout_IsNull()
    {
        var (client, _) = Make(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, timeoutMs: "50");
        (await client.EmbedAsync("phòng", TestContext.Current.CancellationToken)).ShouldBeNull();
    }
}
