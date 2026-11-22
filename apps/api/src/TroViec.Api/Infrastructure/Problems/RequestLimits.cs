using Microsoft.AspNetCore.Http.Features;


namespace TroViec.Api.Infrastructure.Problems;

/// <summary>
/// Body size limits per endpoint (docs/07-security.md, Dữ liệu vào): 1 MB for JSON by default; upload endpoints declare
/// their own limit with RequestSizeLimitAttribute (scan images 30 MB, listing photo ~5 MB, document ~10 MB).
/// A declared Content-Length over the limit is refused before the body is read (413 request.too_large); Kestrel also
/// applies the same limit to chunked bodies through IHttpMaxRequestBodySizeFeature.
/// </summary>
public static class RequestLimits
{
    public const long DefaultMaxBytes = 1024 * 1024;

    public static IApplicationBuilder UseRequestLimits(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var limit = context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>()?.MaxRequestBodySize ?? DefaultMaxBytes;
        if (context.Request.ContentLength is { } length && length > limit)
            throw new ProblemException("request.too_large");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = limit;
        await next(context);
    });
}
