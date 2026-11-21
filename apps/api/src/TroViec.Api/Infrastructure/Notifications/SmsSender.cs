namespace TroViec.Api.Infrastructure.Notifications;

public interface ISmsSender
{
    /// <summary>False when no SMS provider is configured (Sms__Provider=none).</summary>
    bool CanSend { get; }

    Task SendOtpAsync(long userId, string phoneE164, string code, CancellationToken ct);
}

/// <summary>
/// Development only: writes the OTP to the log with the user id, never the phone number (US-104 AC5).
/// The API refuses to start with this sender in Production (see AddSms).
/// </summary>
public sealed class ConsoleSmsSender(ILogger<ConsoleSmsSender> logger) : ISmsSender
{
    public bool CanSend => true;

    public Task SendOtpAsync(long userId, string phoneE164, string code, CancellationToken ct)
    {
        logger.LogWarning("DEV SMS: OTP {Code} for user {UserId}", code, userId);
        return Task.CompletedTask;
    }
}

/// <summary>Sms__Provider=none: no SMS budget; an admin verifies phone numbers by hand (US-104 AC6).</summary>
public sealed class NoSmsSender : ISmsSender
{
    public bool CanSend => false;

    public Task SendOtpAsync(long userId, string phoneE164, string code, CancellationToken ct) =>
        throw new InvalidOperationException("No SMS provider configured");
}

public static class SmsSetup
{
    /// <summary>
    /// Picks the sender from Sms__Provider when first resolved (so later configuration is honoured).
    /// Program resolves it right after Build(), which makes a bad value stop the API at startup.
    /// </summary>
    public static IServiceCollection AddSms(this IServiceCollection services)
    {
        services.AddSingleton<ISmsSender>(sp =>
        {
            var provider = sp.GetRequiredService<IConfiguration>()["Sms:Provider"] ?? "none";
            var environment = sp.GetRequiredService<IHostEnvironment>();
            return provider switch
            {
                "console" when environment.IsProduction() => throw new InvalidOperationException(
                    "Sms__Provider=console is not allowed in Production (it logs OTP codes)"),
                "console" => new ConsoleSmsSender(sp.GetRequiredService<ILogger<ConsoleSmsSender>>()),
                "none" => new NoSmsSender(),
                _ => throw new InvalidOperationException($"Unknown Sms__Provider '{provider}' (expected console or none)"),
            };
        });
        return services;
    }
}
