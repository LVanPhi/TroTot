using TroViec.Security;
using Dapper;
using Npgsql;
using Shouldly;

namespace TroViec.Migrator.Tests;

[Collection(nameof(PostgresCollection))]
public sealed class AdminBootstrapperTests(PostgresFixture db)
{
    private const string Password = "Correct-Horse-Battery-42";

    [Fact]
    public async Task RunAsync_CalledTwice_CreatesOneVerifiedAdmin()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = $"admin-{Guid.NewGuid():N}@example.com";

        var first = await AdminBootstrapper.RunAsync(db.MigratorConnectionString, email, Password, _ => { }, ct);
        var second = await AdminBootstrapper.RunAsync(db.MigratorConnectionString, email.ToUpperInvariant(), Password, _ => { }, ct);

        first.Created.ShouldBeTrue();
        second.Created.ShouldBeFalse();
        second.UserId.ShouldBe(first.UserId);

        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        var row = await connection.QuerySingleAsync<(int Count, bool Verified, string Hash, string Roles, bool GrantedBySystem)>(
            """
            SELECT count(*) OVER ()::int, u.email_verified_at IS NOT NULL, u.password_hash,
                   (SELECT string_agg(r.code, ',') FROM app.user_roles ur JOIN app.roles r ON r.id = ur.role_id
                    WHERE ur.user_id = u.id),
                   (SELECT bool_and(ur.granter_id = app.system_user_id()) FROM app.user_roles ur WHERE ur.user_id = u.id)
            FROM app.users u WHERE lower(u.email) = lower(@Email)
            """, new { Email = email });

        row.Count.ShouldBe(1);
        row.Verified.ShouldBeTrue();
        row.Roles.ShouldBe("admin");
        row.GrantedBySystem.ShouldBeTrue();
        PasswordHasher.Verify(Password, row.Hash).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_WeakPassword_ThrowsAndCreatesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = $"weak-{Guid.NewGuid():N}@example.com";

        await Should.ThrowAsync<MigratorException>(
            () => AdminBootstrapper.RunAsync(db.MigratorConnectionString, email, "short", _ => { }, ct));

        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM app.users WHERE email = @Email", new { Email = email });
        count.ShouldBe(0);
    }
}
