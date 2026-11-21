using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.HttpOverrides;
using StackExchange.Redis;

namespace TroViec.Api.Infrastructure.Config;

public static class ServiceSetup
{
    /// <summary>Redis connects lazily (abortConnect=false) so the API starts even if Redis is briefly down.</summary>
    public static IServiceCollection AddRedis(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(
                configuration.GetConnectionString("Redis") ?? throw new InvalidOperationException("ConnectionStrings__Redis is not set"));
            options.AbortOnConnectFail = false;
            options.ConnectTimeout = 2000;
            options.SyncTimeout = 2000;
            options.AsyncTimeout = 2000;
            return ConnectionMultiplexer.Connect(options);
        });
        return services;
    }

    /// <summary>S3 client for Garage: path-style addressing, custom endpoint and region from S3__*.</summary>
    public static IServiceCollection AddObjectStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAmazonS3>(_ =>
        {
            var s3 = configuration.GetSection("S3");
            var config = new AmazonS3Config
            {
                ServiceURL = s3["Endpoint"] ?? throw new InvalidOperationException("S3__Endpoint is not set"),
                AuthenticationRegion = s3["Region"] ?? "garage",
                ForcePathStyle = true,
                Timeout = TimeSpan.FromSeconds(3),
                MaxErrorRetry = 0,
            };
            return new AmazonS3Client(new BasicAWSCredentials(s3["AccessKey"], s3["SecretKey"]), config);
        });
        return services;
    }

    /// <summary>
    /// Trust X-Forwarded-For/-Proto only from ForwardedHeaders__KnownNetworks (Caddy and web on the Docker network),
    /// so rate limits see the real client IP (docs/04-api.md, Hạn mức).
    /// </summary>
    public static IServiceCollection AddTrustedProxies(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Caddy → web → api adds two hops; untrusted hops stop processing, so no fixed limit is needed.
            options.ForwardLimit = null;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var cidr in ParseNetworks(configuration["ForwardedHeaders:KnownNetworks"]))
                options.KnownIPNetworks.Add(cidr);
        });
        return services;
    }

    internal static IEnumerable<System.Net.IPNetwork> ParseNetworks(string? value) =>
        (value ?? "127.0.0.1/32,::1/128")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(System.Net.IPNetwork.Parse);
}
