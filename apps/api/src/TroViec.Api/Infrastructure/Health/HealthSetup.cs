using Amazon.S3;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using StackExchange.Redis;

namespace TroViec.Api.Infrastructure.Health;

/// <summary>
/// /health/live: process is up. /health/ready: PostgreSQL, Redis and S3 reachable (docs/04-api.md, Sức khỏe hệ thống).
/// Responses are the plain "Healthy"/"Unhealthy" text; no dependency details leak out.
/// </summary>
public static class HealthSetup
{
    private const string ReadyTag = "ready";

    public static IServiceCollection AddHealth(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: [ReadyTag])
            .AddCheck<RedisHealthCheck>("redis", tags: [ReadyTag])
            .AddCheck<S3HealthCheck>("s3", tags: [ReadyTag]);
        return services;
    }

    public static void MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains(ReadyTag) }).AllowAnonymous();
    }
}

internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("postgres unreachable");
        }
    }
}

internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        try
        {
            await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("redis unreachable");
        }
    }
}

internal sealed class S3HealthCheck(IAmazonS3 s3) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        try
        {
            // Authenticated call: also catches a wrong access key, not just a dead endpoint.
            await s3.ListBucketsAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is AmazonS3Exception or HttpRequestException or TaskCanceledException
                                       or Amazon.Runtime.AmazonServiceException)
        {
            return HealthCheckResult.Unhealthy("s3 unreachable");
        }
    }
}
