using System.Net;
using Dapper;
using Npgsql;
using Shouldly;
using TroViec.Api.Tests.Auth;

namespace TroViec.Api.Tests.Me;

/// <summary>US-104 and BR-12: phone OTP, pending number only in Redis, one number per account.</summary>
[Collection(nameof(InfraCollection))]
public sealed class PhoneTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private AuthClient Client() => new(_factory.CreateClient(), infra);

    private static string NewPhone() => $"09{Random.Shared.Next(10_000_000, 99_999_999)}";

    private static string E164(string local) => "+84" + local[1..];

    [Fact]
    public async Task Otp_ThenVerify_StoresNormalisedNumberOnlyAfterVerification()
    {
        var client = Client();
        var (token, email) = await client.SignedInAsync();
        var local = NewPhone();
        var typed = $"{local[..4]} {local[4..7]} {local[7..]}";   // "09xx xxx xxx"

        (await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/otp", token, new { phone = typed })).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        (await PhoneOfAsync(email)).ShouldBe((null, false));    // BR-12: nothing reserved in users yet

        var code = _factory.Sms.LastCodeFor(E164(local));
        var verify = await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/verify", token, new { phone = "+84" + local[1..], code });

        verify.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await PhoneOfAsync(email)).ShouldBe((E164(local), true));
    }

    [Fact]
    public async Task Verify_FiveWrongCodes_CancelsCode()
    {
        var client = Client();
        var (token, email) = await client.SignedInAsync();
        var phone = NewPhone();
        await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/otp", token, new { phone });
        var code = _factory.Sms.LastCodeFor(E164(phone));
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < 4; i++)
            await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/verify", token, new { phone, code = wrong });
        var fifth = await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/verify", token, new { phone, code = wrong });
        var correctAfter = await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/verify", token, new { phone, code });

        (await AuthClient.ProblemCodeAsync(fifth)).ShouldBe("auth.code_attempts_exceeded");
        (await AuthClient.ProblemCodeAsync(correctAfter)).ShouldBe("auth.code_invalid");
        (await PhoneOfAsync(email)).ShouldBe((null, false));
    }

    [Fact]
    public async Task Verify_CodeForAnotherNumber_Returns422CodeInvalid()
    {
        var client = Client();
        var (token, _) = await client.SignedInAsync();
        var phone = NewPhone();
        await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/otp", token, new { phone });

        var response = await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/verify", token,
            new { phone = NewPhone(), code = _factory.Sms.LastCodeFor(E164(phone)) });

        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("auth.code_invalid");
    }

    [Fact]
    public async Task Otp_FourthTimeInAnHourForSameNumber_Returns429()
    {
        var client = Client();
        var (token, _) = await client.SignedInAsync();
        var phone = NewPhone();

        for (var i = 0; i < 3; i++)
            (await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/otp", token, new { phone })).StatusCode
                .ShouldBe(HttpStatusCode.NoContent);
        var fourth = await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/otp", token, new { phone });

        fourth.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await AuthClient.ProblemCodeAsync(fourth)).ShouldBe("rate_limited");
    }

    [Fact]
    public async Task Otp_NumberVerifiedByAnotherAccount_Returns409PhoneTaken()
    {
        var client = Client();
        var phone = NewPhone();
        await VerifyPhoneAsync(client, (await client.SignedInAsync()).Token, phone);
        var (otherToken, _) = await client.SignedInAsync();

        var response = await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/otp", otherToken, new { phone });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("auth.phone_taken");
    }

    [Fact]
    public async Task Verify_NumberTakenWhileCodePending_Returns409PhoneTaken()
    {
        var client = Client();
        var phone = NewPhone();
        var (slowToken, slowEmail) = await client.SignedInAsync();
        await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/otp", slowToken, new { phone });
        var slowCode = _factory.Sms.LastCodeFor(E164(phone));
        await VerifyPhoneAsync(client, (await client.SignedInAsync()).Token, phone);   // someone else wins

        var response = await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/verify", slowToken, new { phone, code = slowCode });

        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("auth.phone_taken");
        (await PhoneOfAsync(slowEmail)).ShouldBe((null, false));
    }

    [Theory]
    [InlineData("0212345678")]   // landline
    [InlineData("091234567")]    // too short
    [InlineData("+1 415 555 0100")]
    public async Task Otp_NotAVietnameseMobile_Returns400(string phone)
    {
        var client = Client();
        var (token, _) = await client.SignedInAsync();

        var response = await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/otp", token, new { phone });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AuthClient.ReadJsonAsync(response)).GetProperty("errors").TryGetProperty("phone", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Otp_WithoutToken_Returns401()
    {
        var response = await Client().PostAsync("/api/v1/auth/phone/otp", new { phone = NewPhone() });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task VerifyPhoneAsync(AuthClient client, string token, string phone)
    {
        await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/otp", token, new { phone });
        (await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/verify", token,
            new { phone, code = _factory.Sms.LastCodeFor(E164(phone)) })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private async Task<(string? Phone, bool Verified)> PhoneOfAsync(string email)
    {
        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        return await connection.QuerySingleAsync<(string?, bool)>(
            "SELECT phone_e164, phone_verified_at IS NOT NULL FROM app.users WHERE lower(email) = lower(@Email)",
            new { Email = email });
    }
}
