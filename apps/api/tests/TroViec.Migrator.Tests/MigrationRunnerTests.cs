using Dapper;
using Npgsql;
using Shouldly;

namespace TroViec.Migrator.Tests;

[Collection(nameof(PostgresCollection))]
public sealed class MigrationRunnerTests(PostgresFixture db)
{
    [Fact]
    public void Run_EmptyDatabase_AppliesAllScriptsInOrder()
    {
        var expected = Directory.GetFiles(db.MigrationsDirectory, "*.sql")
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();

        db.FirstRunScripts.ShouldBe(expected!);
    }

    [Fact]
    public async Task Run_EmptyDatabase_Creates42TablesJournalAnd3MaterializedViews()
    {
        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);

        var appTables = (await connection.QueryAsync<string>(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'app'")).ToList();
        var matViews = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM pg_matviews");

        appTables.ShouldContain("schema_versions");
        appTables.Count(t => t != "schema_versions").ShouldBe(42);
        matViews.ShouldBe(3);
    }

    [Fact]
    public void Run_SecondTime_AppliesNothing()
    {
        var executed = MigrationRunner.Run(db.MigratorConnectionString, db.MigrationsDirectory, _ => { });

        executed.ShouldBeEmpty();
    }
}
