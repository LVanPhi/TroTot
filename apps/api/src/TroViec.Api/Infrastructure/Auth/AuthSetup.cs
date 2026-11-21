using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Infrastructure.Auth;

public static class AuthSetup
{
    public const string RegisterLimit = "auth-register";
    public const string LoginLimit = "auth-login";

    public static IServiceCollection AddAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection("Jwt"))
            .Validate(o => o.SigningKeyBytes().Length >= 32, "Jwt__SigningKey")
            .Validate(o => o.Issuer.Length > 0 && o.Audience.Length > 0, "Jwt__Issuer and Jwt__Audience are required")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        // Resolved lazily so configuration added after registration (tests, user secrets) is honoured.
        services.AddSingleton(sp => new StudentEmailDomains(sp.GetRequiredService<IConfiguration>()["Auth:StudentEmailDomains"]));
        services.AddSingleton(sp => new SiteUrl(sp.GetRequiredService<IConfiguration>()["App:SiteUrl"] ?? "http://localhost:3000"));
        services.AddSingleton<TokenService>();
        services.AddSingleton<UserVersionStore>();
        services.AddSingleton<RefreshTokenStore>();
        services.AddSingleton<OneTimeCodes>();
        services.AddSingleton<Throttles>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<TokenService>((options, tokens) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = tokens.ValidationParameters();
                options.Events = new JwtBearerEvents { OnTokenValidated = RejectStaleVersionAsync };
            });
        services.AddAuthorization(options => options.AddPermissionPolicies());

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) => throw new ProblemException("rate_limited");
            // docs/04-api.md, Hạn mức. Partitioned by client IP (after ForwardedHeaders).
            options.AddPolicy(RegisterLimit, http => FixedWindowByIp(http, 5, TimeSpan.FromHours(1)));
            options.AddPolicy(LoginLimit, http => FixedWindowByIp(http, 10, TimeSpan.FromMinutes(1)));
        });
        return services;
    }

    public static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Tokens issued before the last uver bump (password reset, lock, role change) stop working at once.</summary>
    private static async Task RejectStaleVersionAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        if (!long.TryParse(principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            || !long.TryParse(principal.FindFirst(TokenService.VersionClaim)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            context.Fail("missing sub or ver");
            return;
        }
        var current = await context.HttpContext.RequestServices.GetRequiredService<UserVersionStore>().GetAsync(userId);
        if (version != current) context.Fail("token version is stale");
    }

    private static RateLimitPartition<string> FixedWindowByIp(HttpContext http, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(ClientIp(http), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            QueueLimit = 0,
        });
}
