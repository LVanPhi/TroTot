using FluentValidation;
using TroViec.Api.Infrastructure.Notifications;

namespace TroViec.Api.Infrastructure.Config;

public static class AppServices
{
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SmtpOptions>(configuration.GetSection("Smtp"));
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        return services;
    }

    /// <summary>Registers every FluentValidation validator in this assembly (one per request record).</summary>
    public static IServiceCollection AddValidators(this IServiceCollection services)
    {
        var validators = typeof(AppServices).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>))
                .Select(i => (Service: i, Implementation: t)));
        foreach (var (service, implementation) in validators)
            services.AddSingleton(service, implementation);
        return services;
    }

    /// <summary>
    /// Cors__AllowedOrigins (comma-separated). Needed in dev where web (3000) and api (8080) are different origins;
    /// credentials allowed so the refresh cookie is sent. Production is same-origin through Caddy.
    /// </summary>
    public static IServiceCollection AddCorsForSite(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCors();
        // Read when options are first built, so configuration added after registration is honoured.
        services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>()
            .Configure<IConfiguration>((options, config) =>
            {
                var origins = (config["Cors:AllowedOrigins"] ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                options.AddDefaultPolicy(policy => policy
                    .WithOrigins(origins)
                    .AllowCredentials()
                    .AllowAnyHeader()
                    .AllowAnyMethod());
            });
        return services;
    }
}
