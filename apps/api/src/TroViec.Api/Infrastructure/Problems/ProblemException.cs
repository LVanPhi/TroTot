namespace TroViec.Api.Infrastructure.Problems;

/// <summary>
/// Business error with a code from docs/04-api.md (Lỗi). Status and Vietnamese title come from <see cref="ErrorCatalog"/>.
/// </summary>
public sealed class ProblemException(
    string code,
    string? detail = null,
    IReadOnlyDictionary<string, string[]>? errors = null,
    IReadOnlyDictionary<string, object?>? extensions = null,
    Exception? inner = null,
    IReadOnlyDictionary<string, string>? headers = null)
    : Exception(code, inner)
{
    public string Code { get; } = code;
    public string? Detail { get; } = detail;
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;
    public IReadOnlyDictionary<string, object?>? Extensions { get; } = extensions;

    /// <summary>Response headers to send with the problem (e.g. Retry-After on 429).</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; } = headers;
}
