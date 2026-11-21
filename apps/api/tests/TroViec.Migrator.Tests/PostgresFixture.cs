using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using Npgsql;
using Testcontainers.PostgreSql;

namespace TroViec.Migrator.Tests;

/// <summary>
/// One PostgreSQL container built from infra/postgres/Dockerfile (same image as dev/prod),
/// initialised by db/init/00_init.sh and migrated once as app_migrator.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string MigratorPassword = "test-migrator";

    private IFutureDockerImage? _image;
    private PostgreSqlContainer? _container;

    public string MigratorConnectionString { get; private set; } = "";
    public string MigrationsDirectory { get; } = Path.Combine(RepoRoot.Find(), "db", "migrations");
    public IReadOnlyList<string> FirstRunScripts { get; private set; } = [];

    public async ValueTask InitializeAsync()
    {
        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(RepoRoot.Find())
            .WithDockerfile("infra/postgres/Dockerfile")
            .WithName("troviec-postgres-test:latest")
            .WithCleanUp(false)
            .Build();
        await _image.CreateAsync();

        _container = new PostgreSqlBuilder(_image.FullName)
            .WithDatabase("troviec")
            .WithUsername("postgres")
            .WithPassword("test-superuser")
            .WithEnvironment("APP_MIGRATOR_PASSWORD", MigratorPassword)
            .WithEnvironment("APP_API_PASSWORD", "test-api")
            .WithEnvironment("APP_WORKER_PASSWORD", "test-worker")
            .WithEnvironment("APP_MAINTENANCE_PASSWORD", "test-maintenance")
            .WithEnvironment("APP_REPORTING_PASSWORD", "test-reporting")
            .WithEnvironment("APP_RESEARCH_PASSWORD", "test-research")
            .Build();
        await _container.StartAsync();

        MigratorConnectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Username = "app_migrator",
            Password = MigratorPassword,
        }.ConnectionString;

        FirstRunScripts = MigrationRunner.Run(MigratorConnectionString, MigrationsDirectory, _ => { });
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
