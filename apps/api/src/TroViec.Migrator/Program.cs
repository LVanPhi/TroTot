using TroViec.Migrator;

// Usage: migrate | bootstrap-admin | import-units <csv>
try
{
    DotEnv.LoadFromRepoRoot();
    var command = args.Length > 0 ? args[0] : "migrate";
    // SCRAM only, never Kerberos: skip the GSS probe that logs "libgssapi_krb5.so.2 not found" in the runtime image.
    var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(Settings.Required("ConnectionStrings__Migrator"))
    {
        GssEncryptionMode = Npgsql.GssEncryptionMode.Disable,
    }.ConnectionString;

    switch (command)
    {
        case "migrate":
            // Container images set MIGRATIONS_DIR (no repo checkout there); dev runs find db/migrations from the repo root.
            var scriptsDirectory = Environment.GetEnvironmentVariable("MIGRATIONS_DIR")
                ?? Path.Combine(RepoRoot.Find(), "db", "migrations");
            MigrationRunner.Run(connectionString, scriptsDirectory, Console.WriteLine);
            break;
        case "bootstrap-admin":
            await AdminBootstrapper.RunAsync(
                connectionString,
                Settings.Required("Bootstrap__AdminEmail"),
                Settings.Required("Bootstrap__AdminPassword"),
                Console.WriteLine,
                CancellationToken.None);
            break;
        case "import-units" when args.Length == 2:
            await UnitImporter.ImportFileAsync(connectionString, args[1], Console.WriteLine, CancellationToken.None);
            break;
        default:
            Console.Error.WriteLine("Usage: TroViec.Migrator [migrate | bootstrap-admin | import-units <csv>]");
            return 2;
    }
    return 0;
}
catch (MigratorException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
catch (Npgsql.NpgsqlException ex)
{
    // Connection/auth failures: print the reason only, not the connection string.
    Console.Error.WriteLine($"error: database: {ex.Message}");
    return 1;
}
