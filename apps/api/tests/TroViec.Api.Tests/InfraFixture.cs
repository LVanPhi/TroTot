using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using TroViec.Migrator;

namespace TroViec.Api.Tests;

/// <summary>
/// Real dependencies for API tests: PostgreSQL from infra/postgres/Dockerfile migrated by TroViec.Migrator,
/// Redis 8, and a one-node Garage with an initialised layout and an access key.
/// </summary>
public sealed class InfraFixture : IAsyncLifetime
{
    private const string ApiPassword = "test-api";
    private const string MigratorPassword = "test-migrator";

    private PostgreSqlContainer? _postgres;
    private RedisContainer? _redis;
    private IContainer? _garage;

    public string ApiConnectionString { get; private set; } = "";
    public string RedisConnectionString { get; private set; } = "";
    public string S3Endpoint { get; private set; } = "";
    public string S3AccessKey { get; private set; } = "";
    public string S3SecretKey { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var repoRoot = RepoRoot.Find();
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(repoRoot)
            .WithDockerfile("infra/postgres/Dockerfile")
            .WithName("troviec-postgres-test:latest")
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync();

        _postgres = new PostgreSqlBuilder(image.FullName)
            .WithDatabase("troviec").WithUsername("postgres").WithPassword("test-superuser")
            .WithEnvironment("APP_MIGRATOR_PASSWORD", MigratorPassword)
            .WithEnvironment("APP_API_PASSWORD", ApiPassword)
            .WithEnvironment("APP_WORKER_PASSWORD", "test-worker")
            .WithEnvironment("APP_MAINTENANCE_PASSWORD", "test-maintenance")
            .WithEnvironment("APP_REPORTING_PASSWORD", "test-reporting")
            .WithEnvironment("APP_RESEARCH_PASSWORD", "test-research")
            .Build();
        _redis = new RedisBuilder("redis:8-alpine").Build();
        _garage = new ContainerBuilder("dxflrs/garage:v2.4.1")
            .WithEnvironment("GARAGE_RPC_SECRET", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)))
            .WithResourceMapping(
                await File.ReadAllBytesAsync(Path.Combine(repoRoot, "infra", "garage", "garage.toml")),
                FilePath.Of("/etc/garage.toml"))
            .WithPortBinding(3900, assignRandomHostPort: true)
            // The image has no shell, so shell-based port waits never finish; GarageAsync retries until the daemon answers.
            .Build();

        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _garage.StartAsync());

        var admin = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString());
        MigrationRunner.Run(
            new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Username = "app_migrator", Password = MigratorPassword }.ConnectionString,
            Path.Combine(repoRoot, "db", "migrations"),
            _ => { });
        ApiConnectionString =
            new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Username = "app_api", Password = ApiPassword }.ConnectionString;
        RedisConnectionString = _redis.GetConnectionString();

        await InitGarageAsync();
        S3Endpoint = $"http://{_garage.Hostname}:{_garage.GetMappedPublicPort(3900)}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
        if (_redis is not null) await _redis.DisposeAsync();
        if (_garage is not null) await _garage.DisposeAsync();
    }

    /// <summary>Same steps as infra/garage/init.sh, reduced to what the health check needs: layout + one key.</summary>
    private async Task InitGarageAsync()
    {
        var nodeId = (await GarageAsync("node", "id", "-q")).Trim().Split('@')[0];
        await GarageAsync("layout", "assign", "-z", "dc1", "-c", "1G", nodeId);
        await GarageAsync("layout", "apply", "--version", "1");
        await GarageAsync("key", "create", "troviec-app");
        var info = await GarageAsync("key", "info", "--show-secret", "troviec-app");
        S3AccessKey = ValueAfter(info, "Key ID");
        S3SecretKey = ValueAfter(info, "Secret key");
    }

    private async Task<string> GarageAsync(params string[] args)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await _garage!.ExecAsync(["/garage", .. args]);
            if (result.ExitCode == 0) return result.Stdout;
            if (attempt == 20) throw new InvalidOperationException($"garage {string.Join(' ', args)} failed: {result.Stderr}");
            await Task.Delay(500);
        }
    }

    private static string ValueAfter(string text, string label)
    {
        var line = text.Split('\n').First(l => l.Contains(label, StringComparison.OrdinalIgnoreCase));
        return line.Split(':', 2)[1].Trim();
    }
}

[CollectionDefinition(nameof(InfraCollection))]
public sealed class InfraCollection : ICollectionFixture<InfraFixture>;
