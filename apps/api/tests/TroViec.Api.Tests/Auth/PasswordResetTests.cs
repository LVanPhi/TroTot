using System.Net;
using Shouldly;

namespace TroViec.Api.Tests.Auth;

/// <summary>US-105: forgot password link, single use, ends every session and invalidates access tokens at once.</summary>
[Collection(nameof(InfraCollection))]
public sealed class PasswordResetTests(InfraFixture infra) : IAsyncDisposable
{
    private const string NewPassword = "Brand-New-Secret-2026";
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private AuthClient Client() => new(_factory.CreateClient(), infra);

    [Fact]
    public async Task Forgot_UnknownEmail_Returns204AndSendsNothing()
    {
        var client = Client();
        var email = AuthClient.NewStudentEmail();

        (await client.PostAsync("/api/v1/auth/password/forgot", new { email })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.EmailsToAsync(email)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Forgot_KnownEmail_EmailsLinkToSiteResetPage()
    {
        var client = Client();
        var email = await client.RegisterVerifiedAsync();

        await client.PostAsync("/api/v1/auth/password/forgot", new { email });

        var body = await client.LatestEmailAsync(email);
        body.ShouldContain($"{ApiFactory.SiteUrl}/dat-lai-mat-khau?token=");
        AuthClient.ResetTokenFrom(body).ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Reset_ValidToken_ChangesPasswordRevokesSessionsAndOldAccessTokens()
    {
        var client = Client();
        var email = await client.RegisterVerifiedAsync();
        var (oldAccess, oldRefresh, _) = await client.LoginAsync(email);
        (await client.WhoAmIAsync(oldAccess)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = await ResetTokenAsync(client, email);

        var reset = await client.PostAsync("/api/v1/auth/password/reset", new { token, newPassword = NewPassword });

        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.WhoAmIAsync(oldAccess)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsync("/api/v1/auth/refresh", cookie: oldRefresh)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsync("/api/v1/auth/login", new { email, password = AuthClient.Password })).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        var (newAccess, _, _) = await client.LoginAsync(email, NewPassword);
        (await client.WhoAmIAsync(newAccess)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reset_TokenUsedTwice_SecondTimeReturns422CodeInvalid()
    {
        var client = Client();
        var email = await client.RegisterVerifiedAsync();
        var token = await ResetTokenAsync(client, email);
        await client.PostAsync("/api/v1/auth/password/reset", new { token, newPassword = NewPassword });

        var again = await client.PostAsync("/api/v1/auth/password/reset", new { token, newPassword = NewPassword + "x" });

        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await AuthClient.ProblemCodeAsync(again)).ShouldBe("auth.code_invalid");
    }

    [Fact]
    public async Task Reset_WeakPassword_Returns422AndKeepsTokenUsable()
    {
        var client = Client();
        var email = await client.RegisterVerifiedAsync();
        var token = await ResetTokenAsync(client, email);

        var weak = await client.PostAsync("/api/v1/auth/password/reset", new { token, newPassword = "password" });
        var strong = await client.PostAsync("/api/v1/auth/password/reset", new { token, newPassword = NewPassword });

        (await AuthClient.ProblemCodeAsync(weak)).ShouldBe("auth.weak_password");
        strong.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task<string> ResetTokenAsync(AuthClient client, string email)
    {
        await client.PostAsync("/api/v1/auth/password/forgot", new { email });
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var reset = (await client.EmailsToAsync(email)).FirstOrDefault(b => b.Contains("dat-lai-mat-khau", StringComparison.Ordinal));
            if (reset is not null) return Uri.UnescapeDataString(AuthClient.ResetTokenFrom(reset));
            await Task.Delay(100);
        }
        throw new InvalidOperationException("no reset email");
    }
}
