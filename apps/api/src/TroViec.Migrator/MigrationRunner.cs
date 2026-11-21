using DbUp;
using DbUp.Engine;

namespace TroViec.Migrator;

public static class MigrationRunner
{
    /// <summary>Marker for scripts that must run outside a transaction (CREATE INDEX CONCURRENTLY).</summary>
    public const string NoTransactionMarker = "-- dbup:no-transaction";

    /// <summary>
    /// Runs *.sql in file-name order, one transaction per file, journal in app.schema_versions.
    /// Returns the names of scripts executed in this run (empty when already up to date).
    /// </summary>
    public static IReadOnlyList<string> Run(string connectionString, string scriptsDirectory, Action<string> log)
    {
        var scripts = Directory.GetFiles(scriptsDirectory, "*.sql")
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(path => new SqlScript(Path.GetFileName(path), File.ReadAllText(path)))
            .ToList();

        var executed = new List<string>();
        // One upgrader per script so each file can choose its own transaction mode;
        // the shared journal makes DbUp skip files that already ran.
        foreach (var script in scripts)
        {
            var builder = DeployChanges.To
                .PostgresqlDatabase(connectionString)
                .WithScript(script)
                .JournalToPostgresqlTable("app", "schema_versions")
                .WithVariablesDisabled()
                .LogToNowhere();
            builder = script.Contents.Contains(NoTransactionMarker, StringComparison.Ordinal)
                ? builder.WithoutTransaction()
                : builder.WithTransactionPerScript();

            var result = builder.Build().PerformUpgrade();
            if (!result.Successful)
                throw new MigratorException($"migration {script.Name} failed: {result.Error.Message}");

            foreach (var ran in result.Scripts)
            {
                log($"applied {ran.Name}");
                executed.Add(ran.Name);
            }
        }

        log(executed.Count == 0 ? "database is up to date" : $"applied {executed.Count} script(s)");
        return executed;
    }
}
