using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Shouldly;
using TroViec.Api.Tests.Auth;

namespace TroViec.Api.Tests.Me;

/// <summary>GET/PATCH/DELETE /me: BR-02 posting status and US-106 AC1/AC4 anonymization.</summary>
[Collection(nameof(InfraCollection))]
public sealed class MeTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private AuthClient Client() => new(_factory.CreateClient(), infra);

    [Fact]
    public async Task Get_SignedIn_ReturnsOwnAccount()
    {
        var client = Client();
        var (token, email) = await client.SignedInAsync();

        var body = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get, "/api/v1/me", token));

        body.GetProperty("email").GetString().ShouldBe(email);
        body.GetProperty("emailVerified").GetBoolean().ShouldBeTrue();
        body.GetProperty("phoneVerified").GetBoolean().ShouldBeFalse();
        body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe(["student"]);
    }

    [Fact]
    public async Task Get_WithoutToken_Returns401Problem()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/me", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("auth.unauthenticated");
    }

    // BR-02: verified phone AND (verified school email OR owner profile verified now). By data, not role.
    [Theory]
    [InlineData("student", false, null, new[] { "phone_verification" })]
    [InlineData("student", true, null, new string[0])]
    [InlineData("owner", false, null, new[] { "phone_verification", "owner_verification" })]
    [InlineData("owner", true, null, new[] { "owner_verification" })]
    [InlineData("owner", true, "valid", new string[0])]
    [InlineData("owner", true, "expired", new[] { "owner_verification" })]
    public async Task Get_PostingStatus_FollowsBr02(string accountType, bool phoneVerified, string? ownerProfile, string[] missing)
    {
        var client = Client();
        var (token, email) = await client.SignedInAsync(accountType);
        await using (var connection = new NpgsqlConnection(infra.ApiConnectionString))
        {
            if (phoneVerified)
                await connection.ExecuteAsync(
                    "UPDATE app.users SET phone_e164 = @Phone, phone_verified_at = now() WHERE lower(email) = lower(@Email)",
                    new { Phone = $"+849{Random.Shared.Next(10_000_000, 99_999_999)}", Email = email });
            if (ownerProfile is not null)
                await connection.ExecuteAsync(
                    """
                    INSERT INTO app.owner_profiles (user_id, owner_type, verified_until, verifier_id)
                    SELECT id, 'individual', now() + (CASE WHEN @Valid THEN 1 ELSE -1 END) * interval '30 days', app.system_user_id()
                    FROM app.users WHERE lower(email) = lower(@Email)
                    """, new { Valid = ownerProfile == "valid", Email = email });
        }

        var posting = (await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get, "/api/v1/me", token))).GetProperty("posting");

        posting.GetProperty("allowed").GetBoolean().ShouldBe(missing.Length == 0);
        posting.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldBe(missing);
    }

    [Fact]
    public async Task Patch_FullName_UpdatesAndReturnsMe()
    {
        var client = Client();
        var (token, _) = await client.SignedInAsync();

        var response = await client.SendAsync(HttpMethod.Patch, "/api/v1/me", token, new { fullName = "  Trần Thị B  " });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AuthClient.ReadJsonAsync(response)).GetProperty("fullName").GetString().ShouldBe("Trần Thị B");
    }

    [Fact]
    public async Task Delete_CorrectPassword_AnonymizesAccountAndEndsEverySession()
    {
        var client = Client();
        var email = await client.RegisterVerifiedAsync();
        var (token, refresh, _) = await client.LoginAsync(email);
        var userId = long.Parse(JsonDocument.Parse(AuthClient.Base64UrlDecode(token.Split('.')[1])).RootElement.GetProperty("sub").GetString()!);
        await using (var connection = new NpgsqlConnection(infra.ApiConnectionString))
            await connection.ExecuteAsync("UPDATE app.users SET phone_e164 = @Phone, phone_verified_at = now() WHERE id = @Id",
                new { Phone = $"+849{Random.Shared.Next(10_000_000, 99_999_999)}", Id = userId });

        var response = await client.SendAsync(HttpMethod.Delete, "/api/v1/me", token, new { password = AuthClient.Password });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var connection = new NpgsqlConnection(infra.ApiConnectionString))
        {
            var row = await connection.QuerySingleAsync<(string? Email, bool EmailVerified, string? Phone, bool PhoneVerified,
                string Hash, string FullName, string Status, bool Anonymized, int Roles)>(
                """
                SELECT email, email_verified_at IS NOT NULL, phone_e164, phone_verified_at IS NOT NULL,
                       password_hash, full_name, status::text, anonymized_at IS NOT NULL,
                       (SELECT count(*)::int FROM app.user_roles WHERE user_id = u.id)
                FROM app.users u WHERE id = @Id
                """, new { Id = userId });
            row.ShouldBe((null, false, null, false, "!", "Người dùng đã xóa", "anonymized", true, 1));  // AC1; AC4: role row kept
        }
        (await client.WhoAmIAsync(token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsync("/api/v1/auth/refresh", cookie: refresh)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsync("/api/v1/auth/login", new { email, password = AuthClient.Password })).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Delete_WrongPassword_Returns401AndKeepsAccount()
    {
        var client = Client();
        var (token, email) = await client.SignedInAsync();

        var response = await client.SendAsync(HttpMethod.Delete, "/api/v1/me", token, new { password = "Not-My-Password-1" });

        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("auth.invalid_credentials");
        (await client.WhoAmIAsync(token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        (await connection.ExecuteScalarAsync<string>("SELECT status::text FROM app.users WHERE lower(email) = lower(@Email)",
            new { Email = email })).ShouldBe("active");
    }
}
