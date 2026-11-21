using System.Net.Http.Headers;
using SkiaSharp;
using TroViec.Api.Tests.Storage;

namespace TroViec.Api.Tests.Scans;

/// <summary>Builds POST /api/v1/scans multipart requests from a given client IP.</summary>
public static class ScanClient
{
    public static string NewIp() => $"10.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

    public static Task<HttpResponseMessage> PostAsync(
        HttpClient http, string ip, string? text = null, IEnumerable<byte[]>? images = null,
        string? rent = null, string? ward = null, string? token = null)
    {
        var form = new MultipartFormDataContent();
        if (text is not null) form.Add(new StringContent(text), "text");
        if (rent is not null) form.Add(new StringContent(rent), "claimedRentVnd");
        if (ward is not null) form.Add(new StringContent(ward), "claimedWardCode");
        var n = 0;
        foreach (var image in images ?? [])
        {
            var part = new ByteArrayContent(image);
            part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");   // ignored by the API on purpose
            form.Add(part, "images", $"photo{++n}.jpg");
        }
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scans") { Content = form };
        request.Headers.Add(ApiFactory.TestIpHeader, ip);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http.SendAsync(request);
    }

    public static byte[] Png(int w = 32, int h = 24) => TestImages.Encode(w, h, SKEncodedImageFormat.Png);
}
