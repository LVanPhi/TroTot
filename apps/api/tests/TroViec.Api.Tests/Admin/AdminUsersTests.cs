using System.Net;
using Dapper;
using Npgsql;
using Shouldly;
using TroViec.Api.Tests.Auth;

namespace TroViec.Api.Tests.Admin;

/// <summary>US-801 and US-104 AC6: user search, roles, lock/unlock, manual phone verification.</summary>
[Collection(nameof(InfraCollection))]
public sealed class AdminUsersTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private AuthClient Client() => new(_factory.CreateClient(), infra);

    private async Task<(AuthClient Client, TestUser Admin)> AdminAsync()
    {
        var client = Client();
        return (client, await TestUsers.CreateAsync(client, infra, ["admin"]));
    }

    [Fact]
    public async Task Search_ByEmail_FindsUserAndNeverReturnsSystemAccount()
    {
        var (client, admin) = await AdminAsync();
        var target = await TestUsers.CreateAsync(client, infra, ["student"]);

        var found = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get,
            $"/api/v1/admin/users?q={Uri.EscapeDataString(target.Email.ToUpperInvariant())}", admin.Token));
        var all = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get, "/api/v1/admin/users?q=system&pageSize=50", admin.Token));

        found.GetProperty("total").GetInt64().ShouldBe(1);
        var item = found.GetProperty("items")[0];
        item.GetProperty("id").GetInt64().ShouldBe(target.Id);
        item.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe(["student"]);
        all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("email").GetString())
            .ShouldNotContain("system@troviec.invalid");
    }

    [Fact]
    public async Task Search_ByRoleWithPaging_ReturnsOnlyThatRole()
    {
        var (client, admin) = await AdminAsync();
        await TestUsers.CreateAsync(client, infra, ["moderator"]);

        var page = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get, "/api/v1/admin/users?role=moderator&pageSize=1", admin.Token));

        page.GetProperty("pageSize").GetInt32().ShouldBe(1);
        page.GetProperty("items").GetArrayLength().ShouldBe(1);
        page.GetProperty("items")[0].GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldContain("moderator");
    }

    [Fact]
    public async Task AddRole_ThenRefresh_IssuesTokenWithNewPermissionsAndRejectsOldToken()
    {
        var (client, admin) = await AdminAsync();
        var target = await TestUsers.CreateAsync(client, infra, ["student"]);
        TestUsers.PermissionsIn(target.Token).ShouldNotContain("listing.moderate");

        (await client.SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{target.Id}/roles", admin.Token, new { roleCode = "moderator" }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.WhoAmIAsync(target.Token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);  // ver bumped
        var refreshed = await client.PostAsync("/api/v1/auth/refresh", cookie: target.RefreshCookie);    // refresh still valid
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var newToken = (await AuthClient.ReadJsonAsync(refreshed)).GetProperty("accessToken").GetString()!;
        TestUsers.PermissionsIn(newToken).ShouldContain("listing.moderate");
        (await client.WhoAmIAsync(newToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RemoveRole_RemovesPermissionsOnNextRefresh()
    {
        var (client, admin) = await AdminAsync();
        var target = await TestUsers.CreateAsync(client, infra, ["student", "moderator"]);

        (await client.SendAsync(HttpMethod.Delete, $"/api/v1/admin/users/{target.Id}/roles/moderator", admin.Token)).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        var refreshed = await client.PostAsync("/api/v1/auth/refresh", cookie: target.RefreshCookie);
        var newToken = (await AuthClient.ReadJsonAsync(refreshed)).GetProperty("accessToken").GetString()!;
        TestUsers.PermissionsIn(newToken).ShouldNotContain("listing.moderate");
        TestUsers.PermissionsIn(newToken).ShouldContain("listing.save");
    }

    [Fact]
    public async Task RemoveRole_OwnAdmin_Returns409()
    {
        var (client, admin) = await AdminAsync();

        var response = await client.SendAsync(HttpMethod.Delete, $"/api/v1/admin/users/{admin.Id}/roles/admin", admin.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("admin.cannot_remove_own_admin");
    }

    [Fact]
    public async Task AddRole_UnknownRole_Returns400()
    {
        var (client, admin) = await AdminAsync();
        var target = await TestUsers.CreateAsync(client, infra, ["student"]);

        var response = await client.SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{target.Id}/roles", admin.Token, new { roleCode = "superuser" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AuthClient.ReadJsonAsync(response)).GetProperty("errors").TryGetProperty("roleCode", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Lock_EndsEverySessionAtOnceAndBlocksLogin_UnlockRestores()
    {
        var (client, admin) = await AdminAsync();
        var target = await TestUsers.CreateAsync(client, infra, ["student"]);
        (await client.WhoAmIAsync(target.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{target.Id}/lock", admin.Token)).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        (await client.WhoAmIAsync(target.Token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsync("/api/v1/auth/refresh", cookie: target.RefreshCookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthClient.ProblemCodeAsync(await client.PostAsync("/api/v1/auth/login",
            new { email = target.Email, password = AuthClient.Password }))).ShouldBe("auth.account_locked");

        (await client.SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{target.Id}/unlock", admin.Token)).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        (await client.LoginAsync(target.Email)).AccessToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Lock_Self_Returns409()
    {
        var (client, admin) = await AdminAsync();

        var response = await client.SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{admin.Id}/lock", admin.Token);

        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("admin.cannot_lock_self");
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("unlock")]
    [InlineData("roles")]
    [InlineData("phone")]
    public async Task AnyAction_OnSystemAccount_Returns404(string action)
    {
        var (client, admin) = await AdminAsync();
        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        var systemId = await connection.ExecuteScalarAsync<long>("SELECT app.system_user_id()");
        object? body = action switch { "roles" => new { roleCode = "admin" }, "phone" => new { phone = "0912345678" }, _ => null };

        var response = await client.SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{systemId}/{action}", admin.Token, body);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("resource.not_found");
    }

    [Fact]
    public async Task VerifyPhone_Manually_SetsNumberAndCountsForPosting()
    {
        var (client, admin) = await AdminAsync();
        var target = await TestUsers.CreateAsync(client, infra, ["student"], phoneVerified: false);
        var phone = $"09{Random.Shared.Next(10_000_000, 99_999_999)}";

        (await client.SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{target.Id}/phone", admin.Token, new { phone })).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        var me = await AuthClient.ReadJsonAsync(await client.SendAsync(HttpMethod.Get, "/api/v1/me", target.Token));
        me.GetProperty("phone").GetString().ShouldBe("+84" + phone[1..]);
        me.GetProperty("phoneVerified").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task VerifyPhone_NumberOfAnotherAccount_Returns409PhoneTaken()
    {
        var (client, admin) = await AdminAsync();
        var first = await TestUsers.CreateAsync(client, infra, ["student"], phoneVerified: false);
        var second = await TestUsers.CreateAsync(client, infra, ["student"], phoneVerified: false);
        var phone = $"09{Random.Shared.Next(10_000_000, 99_999_999)}";
        await client.SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{first.Id}/phone", admin.Token, new { phone });

        var response = await client.SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{second.Id}/phone", admin.Token, new { phone });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("auth.phone_taken");
    }
}
