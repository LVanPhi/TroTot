using System.Net.Http.Json;

namespace TroViec.Api.Features.Search;

public sealed record QueryVector(float[] Vector, int ModelVersionId);

/// <summary>Vector for a search query; null whenever the service cannot answer in time (search then uses full-text).</summary>
public interface IEmbeddingClient
{
    Task<QueryVector?> EmbedAsync(string text, CancellationToken ct);
}

/// <summary>POST {Embed__BaseUrl}/internal/embed with X-Internal-Token, timeout Embed__TimeoutMs (800 ms).</summary>
public sealed class EmbeddingClient(HttpClient http, IConfiguration configuration, ILogger<EmbeddingClient> logger) : IEmbeddingClient
{
    private sealed record Request(string[] Texts);

    private sealed record Response(string Model, int ModelVersionId, int Dim, float[][] Vectors);

    public async Task<QueryVector?> EmbedAsync(string text, CancellationToken ct)
    {
        var baseUrl = configuration["Embed:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        var timeout = int.TryParse(configuration["Embed:TimeoutMs"], out var ms) ? ms : 800;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), "/internal/embed"))
            {
                Content = JsonContent.Create(new Request([text[..Math.Min(text.Length, 2000)]])),
            };
            request.Headers.Add("X-Internal-Token", configuration["Embed:InternalToken"] ?? "");
            using var response = await http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Embedding service answered {Status}; using full-text", (int)response.StatusCode);
                return null;
            }
            var body = await response.Content.ReadFromJsonAsync<Response>(cts.Token);
            return body is { Vectors: [var v] } ? new QueryVector(v, body.ModelVersionId) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                       or System.Text.Json.JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            logger.LogWarning("Embedding service unavailable ({ExceptionType}); using full-text", ex.GetType().Name);
            return null;
        }
    }
}
