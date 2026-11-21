using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Shouldly;

namespace TroViec.Api.Tests.Auth;

/// <summary>US-103: login, rotating refresh tokens with reuse detection, logout, lockout, CSRF header.</summary>
[Collection(nameof(InfraCollection))]
public sealed class LoginAndSessionTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private AuthClient Client() => new(_factory.CreateClient(), infra);

    [Fact]
    public async Task Login_VerifiedStudent_ReturnsJwtWithClaimsAndStrictRefreshCookie()
    {
        var client = Client();
        var email = await client.RegisterVerifiedAsync();

        var response = await client.PostAsync("/api/v1/auth/login", new { email, password = AuthClient.Password });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await AuthClient.ReadJsonAsync(response);
        body.GetProperty("expiresIn").GetInt32().ShouldBe(900);
        body.GetProperty("user").GetProperty("roles")[0].GetString().ShouldBe("student");
        body.GetProperty("user").GetProperty("posting").GetProperty("missing")[0].GetString().ShouldBe("phone_verification");

        var payload = JsonDocument.Parse(AuthClient.Base64UrlDecode(body.GetProperty("accessToken").GetString()!.Split('.')[1])).RootElement;
        payload.GetProperty("sub").GetString().ShouldNotBeNullOrEmpty();
        payload.GetProperty("ver").GetInt64().ShouldBe(0);
        payload.GetProperty("perm").EnumerateArray().Select(p => p.GetString()).ShouldContain("scan.create");
        (payload.GetProperty("exp").GetInt64() - payload.GetProperty("iat").GetInt64()).ShouldBe(900);

        var cookie = AuthClient.SetCookieHeader(response)!.ToLowerInvariant();
        cookie.ShouldContain("httponly");
        cookie.ShouldContain("secure");
        cookie.ShouldContain("samesite=strict");
        cookie.ShouldContain("path=/api/v1/auth");
        cookie.ShouldContain("max-age=2592000");
    }

    [Fact]
    public async Task Login_AccessToken_IsAcceptedByBearerAuth()
    {
        var client = Client();
        var (token, _, _) = await client.LoginAsync(await client.RegisterVerifiedAsync());

        (await client.WhoAmIAsync(token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.WhoAmIAsync(token + "x")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_EmailNotVerified_Returns403EmailNotVerified()
    {
        var client = Client();
        var email = AuthClient.NewStudentEmail();
        await client.RegisterAsync(email);

        var response = await client.PostAsync("/api/v1/auth/login", new { email, password = AuthClient.Password });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("auth.email_not_verified");
    }

    [Fact]
    public async Task Login_WrongPasswordOrUnknownEmail_Returns401InvalidCredentials()
    {
        var client = Client();
        var email = await client.RegisterVerifiedAsync();

        var wrong = await client.PostAsync("/api/v1/auth/login", new { email, password = "Wrong-Password-123" });
        var unknown = await client.PostAsync("/api/v1/auth/login", new { email = AuthClient.NewStudentEmail(), password = AuthClient.Password });

        (await AuthClient.ProblemCodeAsync(wrong)).ShouldBe("auth.invalid_credentials");
        (await AuthClient.ProblemCodeAsync(unknown)).ShouldBe("auth.invalid_credentials");
    }

    [Fact]
    public async Task Login_FiveWrongPasswords_LocksAccountEvenFromAnotherIp()
    {
        var attacker = Client();
        var email = await attacker.RegisterVerifiedAsync();
        for (var i = 0; i < 5; i++)
            await attacker.PostAsync("/api/v1/auth/login", new { email, password = "Wrong-Password-123" });

        var owner = Client();  // different IP, correct password
        var response = await owner.PostAsync("/api/v1/auth/login", new { email, password = AuthClient.Password });

        response.StatusCode.ShouldBe((HttpStatusCode)423);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("auth.locked_temporarily");
    }

    [Theory]
    [InlineData("locked")]
    public async Task Login_AccountLocked_Returns403AccountLocked(string status)
    {
        var client = Client();
        var email = await client.RegisterVerifiedAsync();
        await using (var connection = new NpgsqlConnection(infra.ApiConnectionString))
            await connection.ExecuteAsync("UPDATE app.users SET status = @Status::app.user_status WHERE lower(email) = lower(@Email)",
                new { Status = status, Email = email });

        var response = await client.PostAsync("/api/v1/auth/login", new { email, password = AuthClient.Password });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("auth.account_locked");
    }

    [Fact]
    public async Task Refresh_ValidCookie_RotatesTokenAndIssuesNewAccessToken()
    {
        var client = Client();
        var (_, cookie, _) = await client.LoginAsync(await client.RegisterVerifiedAsync());

        var response = await client.PostAsync("/api/v1/auth/refresh", cookie: cookie);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rotated = AuthClient.RefreshCookieFrom(response);
        rotated.ShouldNotBeNull();
        rotated.ShouldNotBe(cookie);
        var token = (await AuthClient.ReadJsonAsync(response)).GetProperty("accessToken").GetString()!;
        (await client.WhoAmIAsync(token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_OldTokenReused_Returns401ReusedAndRevokesWholeFamily()
    {
        var client = Client();
        var (_, first, _) = await client.LoginAsync(await client.RegisterVerifiedAsync());
        var second = AuthClient.RefreshCookieFrom(await client.PostAsync("/api/v1/auth/refresh", cookie: first))!;

        var replay = await client.PostAsync("/api/v1/auth/refresh", cookie: first);
        var afterRevoke = await client.PostAsync("/api/v1/auth/refresh", cookie: second);

        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthClient.ProblemCodeAsync(replay)).ShouldBe("auth.refresh_reused");
        afterRevoke.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthClient.ProblemCodeAsync(afterRevoke)).ShouldBe("auth.refresh_invalid");
    }

    [Theory]
    [InlineData("/api/v1/auth/refresh")]
    [InlineData("/api/v1/auth/logout")]
    public async Task CookieEndpoints_WithoutCsrfHeader_Return403(string path)
    {
        var client = Client();
        var (_, cookie, _) = await client.LoginAsync(await client.RegisterVerifiedAsync());

        var response = await client.PostAsync(path, cookie: cookie, csrf: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("auth.forbidden");
    }

    [Fact]
    public async Task Logout_ThenRefresh_Returns401()
    {
        var client = Client();
        var (_, cookie, _) = await client.LoginAsync(await client.RegisterVerifiedAsync());

        (await client.PostAsync("/api/v1/auth/logout", cookie: cookie)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var response = await client.PostAsync("/api/v1/auth/refresh", cookie: cookie);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_WithoutCookie_Returns401RefreshInvalid()
    {
        var response = await Client().PostAsync("/api/v1/auth/refresh");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("auth.refresh_invalid");
    }
}
