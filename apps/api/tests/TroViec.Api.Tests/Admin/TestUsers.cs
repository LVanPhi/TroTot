using System.Text.Json;
using Dapper;
using Npgsql;
using TroViec.Api.Tests.Auth;

namespace TroViec.Api.Tests.Admin;

public sealed record TestUser(long Id, string Email, string Token, string RefreshCookie);

/// <summary>Creates signed-in users with exact roles (set in the database, as an admin would) for authorization tests.</summary>
public static class TestUsers
{
    public static async Task<TestUser> CreateAsync(AuthClient client, InfraFixture infra, string[] roles, bool phoneVerified = true)
    {
        var email = await client.RegisterVerifiedAsync(accountType: "owner");
        var id = await UserIdAsync(infra, email);
        await using (var connection = new NpgsqlConnection(infra.ApiConnectionString))
        {
            await connection.ExecuteAsync("DELETE FROM app.user_roles WHERE user_id = @Id", new { Id = id });
            await connection.ExecuteAsync(
                """
                INSERT INTO app.user_roles (user_id, role_id, granter_id)
                SELECT @Id, r.id, app.system_user_id() FROM app.roles r WHERE r.code = ANY(@Roles)
                """, new { Id = id, Roles = roles });
            if (phoneVerified)
                await connection.ExecuteAsync("UPDATE app.users SET phone_e164 = @Phone, phone_verified_at = now() WHERE id = @Id",
                    new { Phone = $"+849{Random.Shared.Next(10_000_000, 99_999_999)}", Id = id });
        }
        var (token, refresh, _) = await client.LoginAsync(email);
        return new TestUser(id, email, token, refresh);
    }

    public static async Task<long> UserIdAsync(InfraFixture infra, string email)
    {
        await using var connection = new NpgsqlConnection(infra.ApiConnectionString);
        return await connection.ExecuteScalarAsync<long>("SELECT id FROM app.users WHERE lower(email) = lower(@Email)", new { Email = email });
    }

    public static IReadOnlyList<string> PermissionsIn(string accessToken) =>
        JsonDocument.Parse(AuthClient.Base64UrlDecode(accessToken.Split('.')[1])).RootElement
            .GetProperty("perm").EnumerateArray().Select(p => p.GetString()!).ToList();
}
