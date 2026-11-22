using Dapper;
using Npgsql;
using Shouldly;
using TroViec.Security;

namespace TroViec.Migrator.Tests;

/// <summary>T4.2: db/seed/demo is idempotent and gives verified accounts for every role plus 20 rooms and 10 jobs.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class DemoSeederTests(PostgresFixture db)
{
    private static string SeedFile => Path.Combine(RepoRoot.Find(), "db", "seed", "demo", "demo.sql");

    [Fact]
    public async Task SeedDemo_TwiceGivesTheSameData_AccountsVerifiedWithRolesAndPassword()
    {
        await using (var c = new NpgsqlConnection(db.MigratorConnectionString))
            await c.ExecuteAsync(
                """
                INSERT INTO app.administrative_units (code, name, unit_level, parent_code) VALUES
                    ('88', 'Tỉnh Demo', 'province', NULL), ('88001', 'Phường Demo', 'commune', '88')
                ON CONFLICT DO NOTHING
                """);

        for (var run = 0; run < 2; run++)
            await DemoSeeder.RunAsync(db.MigratorConnectionString, SeedFile, DemoSeeder.DefaultPassword, "sv.demo.edu.vn", _ => { },
                TestContext.Current.CancellationToken);

        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        var users = (await connection.QueryAsync<(string Email, string Hash, bool EmailOk, bool PhoneOk, string Roles)>(
            """
            SELECT u.email, u.password_hash, u.email_verified_at IS NOT NULL, u.phone_verified_at IS NOT NULL,
                   string_agg(r.code, ',' ORDER BY r.code)
            FROM app.users u JOIN app.user_roles ur ON ur.user_id = u.id JOIN app.roles r ON r.id = ur.role_id
            WHERE u.email LIKE '%demo%'
            GROUP BY u.id
            """)).ToList();
        users.Count.ShouldBe(7);
        users.ShouldAllBe(u => u.EmailOk && u.PhoneOk);
        users.Select(u => u.Roles).Distinct().OrderBy(r => r).ShouldBe(["admin", "moderator", "owner", "student"]);
        PasswordHasher.Verify(DemoSeeder.DefaultPassword, users[0].Hash).ShouldBeTrue();

        var listings = await connection.QuerySingleAsync<(long Rooms, long Jobs, long Verified)>(
            """
            SELECT count(*) FILTER (WHERE l.listing_type = 'room'), count(*) FILTER (WHERE l.listing_type = 'job'),
                   count(*) FILTER (WHERE l.status = 'verified' AND l.expires_at > now())
            FROM app.listings l JOIN app.users u ON u.id = l.poster_id WHERE u.email LIKE 'chutro%@demo.troviec.test'
            """);
        listings.ShouldBe((20L, 10L, 30L));
        (await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM app.owner_profiles op JOIN app.users u ON u.id = op.user_id WHERE u.email LIKE 'chutro%' AND op.verified_until > now()"))
            .ShouldBe(2);
    }
}
