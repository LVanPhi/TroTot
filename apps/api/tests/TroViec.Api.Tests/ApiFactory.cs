using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TroViec.Api.Tests;

/// <summary>Hosts the real Program against the fixture's containers. Overrides win over any repo .env.</summary>
public sealed class ApiFactory(InfraFixture infra, string environment = "Development",
    IDictionary<string, string?>? overrides = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration(config =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Main"] = infra.ApiConnectionString,
                ["ConnectionStrings:Redis"] = infra.RedisConnectionString,
                ["S3:Endpoint"] = infra.S3Endpoint,
                ["S3:Region"] = "garage",
                ["S3:AccessKey"] = infra.S3AccessKey,
                ["S3:SecretKey"] = infra.S3SecretKey,
            };
            foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
                settings[key] = value;
            config.AddInMemoryCollection(settings);
        });
    }
}
