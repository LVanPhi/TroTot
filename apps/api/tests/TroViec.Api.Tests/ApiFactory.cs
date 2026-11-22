using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TroViec.Api.Tests;

/// <summary>Hosts the real Program against the fixture's containers. Overrides win over any repo .env.</summary>
public sealed class ApiFactory(InfraFixture infra, string environment = "Development",
    IDictionary<string, string?>? overrides = null) : WebApplicationFactory<Program>
{
    public const string StudentDomain = "sv.test.edu.vn";
    public const string SiteUrl = "http://site.test";

    /// <summary>Test-only header: sets the client IP so IP-based limits do not leak between tests.</summary>
    public const string TestIpHeader = "X-Test-Ip";

    /// <summary>Test-only endpoint: 200 with the user id when the bearer token is accepted, 401 otherwise.</summary>
    public const string WhoAmIPath = "/test/whoami";

    /// <summary>When true, SMS codes are captured in <see cref="Sms"/> instead of going to the console sender.</summary>
    public bool CaptureSms { get; init; } = true;

    public CapturingSmsSender Sms { get; } = new();

    /// <summary>When set, replaces TimeProvider.System (e.g. to expire a 5-minute document link).</summary>
    public MutableClock? Clock { get; init; }

    public static string TestKey(int seed) => Convert.ToBase64String(Enumerable.Range(seed, 32).Select(i => (byte)i).ToArray());

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
                ["Jwt:SigningKey"] = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 7)).ToArray()),
                ["Jwt:Issuer"] = "https://test.troviec",
                ["Jwt:Audience"] = "https://test.troviec",
                ["Jwt:AccessTokenMinutes"] = "15",
                ["Jwt:RefreshTokenDays"] = "30",
                ["Auth:StudentEmailDomains"] = StudentDomain,
                ["App:SiteUrl"] = SiteUrl,
                ["Smtp:Host"] = infra.SmtpHost,
                ["Smtp:Port"] = infra.SmtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Smtp:User"] = "",
                ["Smtp:From"] = "Tro & Viec Sach <no-reply@test.troviec>",
                ["Cors:AllowedOrigins"] = SiteUrl,
                ["Sms:Provider"] = "console",
                ["Crypto:DocumentKeys"] = $"{{\"k1\":\"{TestKey(11)}\",\"k2\":\"{TestKey(12)}\"}}",
                ["Crypto:ActiveDocumentKeyId"] = "k1",
                ["Crypto:SignedUrlKey"] = TestKey(13),
            };
            foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
                settings[key] = value;
            config.AddInMemoryCollection(settings);
        });
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, TestHooks>());
        if (Clock is not null)
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
        if (CaptureSms)
            builder.ConfigureTestServices(services => services.AddSingleton<TroViec.Api.Infrastructure.Notifications.ISmsSender>(Sms));
    }

    private sealed class TestHooks : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[TestIpHeader], out var ip))
                    context.Connection.RemoteIpAddress = ip;

                if (context.Request.Path == WhoAmIPath)
                {
                    var result = await context.AuthenticateAsync();
                    context.Response.StatusCode = result.Succeeded ? 200 : 401;
                    if (result.Succeeded) await context.Response.WriteAsync(result.Principal!.Identity!.Name ?? "");
                    return;
                }
                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
