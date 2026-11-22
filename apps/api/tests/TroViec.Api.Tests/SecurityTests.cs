using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using SkiaSharp;
using TroViec.Api.Infrastructure.Storage;
using TroViec.Api.Tests.Auth;
using TroViec.Api.Tests.Scans;
using TroViec.Api.Tests.Storage;

namespace TroViec.Api.Tests;

/// <summary>T4.1: request body limits, access token tampering, polyglot uploads (docs/07-security.md, docs/09-testing.md).</summary>
[Collection(nameof(InfraCollection))]
public sealed class SecurityTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly MutableClock _clock = new();
    private ApiFactory? _factory;

    private ApiFactory Factory => _factory ??= new ApiFactory(infra) { Clock = _clock };

    public ValueTask DisposeAsync() => _factory?.DisposeAsync() ?? ValueTask.CompletedTask;

    private Task<HttpResponseMessage> WhoAmI(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ApiFactory.WhoAmIPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task JsonBodyOverOneMegabyte_Is413BeforeTheHandler()
    {
        var big = new StringContent($$"""{"email":"a@b.c","password":"{{new string('x', 1_100_000)}}"}""", Encoding.UTF8, "application/json");

        var response = await Factory.CreateClient().PostAsync("/api/v1/auth/login", big, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("request.too_large");
    }

    [Fact]
    public async Task UploadEndpoint_KeepsItsOwnLargerLimit()
    {
        // 2 MB is over the JSON default but under the scan upload limit: the request reaches the handler.
        var form = new MultipartFormDataContent { { new ByteArrayContent(new byte[2 * 1024 * 1024]), "images", "a.png" } };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scans") { Content = form };
        request.Headers.Add(ApiFactory.TestIpHeader, ScanClient.NewIp());

        var response = await Factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("image.unsupported_type");
    }

    [Fact]
    public async Task AccessToken_TamperedSignatureExpiredForeignKeyOrAlgNone_AreRejected()
    {
        var (token, _) = await new AuthClient(Factory.CreateClient(), infra).SignedInAsync();
        (await WhoAmI(token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var parts = token.Split('.');
        var flipped = parts[2][..^1] + (parts[2][^1] == 'A' ? 'B' : 'A');
        (await WhoAmI($"{parts[0]}.{parts[1]}.{flipped}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var claims = new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.ToList();
        var foreign = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            "https://test.troviec", "https://test.troviec", claims, expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(new byte[32]), SecurityAlgorithms.HmacSha256)));
        (await WhoAmI(foreign)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        (await WhoAmI($"{header}.{parts[1]}.")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // JwtBearer checks exp against the real clock: issue a token 20 minutes in the past (15-minute lifetime + 30 s skew).
        _clock.Advance(TimeSpan.FromMinutes(-20));
        var (expired, _) = await new AuthClient(Factory.CreateClient(), infra).SignedInAsync();
        (await WhoAmI(expired)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _ = claims.OfType<Claim>();
    }

    [Fact]
    public async Task PolyglotImage_IsReencodedWithoutTheTrailingPayload()
    {
        var png = TestImages.Encode(40, 30, SKEncodedImageFormat.Png);
        var payload = "<script>alert(1)</script><?php system($_GET['c']); ?>"u8.ToArray();
        await using var input = new MemoryStream([.. png, .. payload]);

        var result = await ImagePipeline.ProcessAsync(input, TestContext.Current.CancellationToken);

        Encoding.Latin1.GetString(result.Webp).ShouldNotContain("<script>");
        Encoding.Latin1.GetString(result.Webp).ShouldNotContain("<?php");
        Encoding.ASCII.GetString(result.Webp, 8, 4).ShouldBe("WEBP");
    }
}
