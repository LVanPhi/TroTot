using System.Globalization;
using StackExchange.Redis;

namespace TroViec.Api.Infrastructure.RateLimiting;

/// <summary>Quota__GuestScansPerDay / Quota__UserScansPerDay (BR-05).</summary>
public sealed class QuotaOptions
{
    public int GuestScansPerDay { get; set; } = 5;
    public int UserScansPerDay { get; set; } = 50;
}

public sealed record QuotaStatus(int Limit, int Remaining, DateTimeOffset ResetAt);

/// <summary>
/// Daily scan quota in Redis: quota:scan:{ip:x|user:n}:{yyyyMMdd}, the day in Asia/Ho_Chi_Minh (docs/04-api.md, Hạn mức).
/// Only scans that were actually created (202) are counted. If Redis is down the quota fails open (logged): a scan
/// must still be accepted and wait in the outbox (docs/09-testing.md, Outbox).
/// </summary>
public sealed class QuotaService(
    IConnectionMultiplexer redis,
    Microsoft.Extensions.Options.IOptions<QuotaOptions> options,
    TimeProvider clock,
    ILogger<QuotaService> logger)
{
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    public async Task<QuotaStatus> GetAsync(long? userId, string ip)
    {
        var (limit, key, resetAt) = Describe(userId, ip);
        try
        {
            var used = (long?)await redis.GetDatabase().StringGetAsync(key) ?? 0;
            return new QuotaStatus(limit, (int)Math.Max(0, limit - used), resetAt);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning("Quota read skipped: Redis unavailable");
            return new QuotaStatus(limit, limit, resetAt);
        }
    }

    /// <summary>Counts one created scan.</summary>
    public async Task ConsumeAsync(long? userId, string ip)
    {
        var (_, key, resetAt) = Describe(userId, ip);
        try
        {
            var db = redis.GetDatabase();
            var used = await db.StringIncrementAsync(key);
            if (used == 1) await db.KeyExpireAsync(key, resetAt - clock.GetUtcNow() + TimeSpan.FromMinutes(5));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning("Quota not counted: Redis unavailable");
        }
    }

    private (int Limit, string Key, DateTimeOffset ResetAt) Describe(long? userId, string ip)
    {
        var now = clock.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, Vietnam);
        var nextMidnightLocal = new DateTimeOffset(local.Date.AddDays(1), local.Offset);
        var subject = userId is { } id ? $"user:{id.ToString(CultureInfo.InvariantCulture)}" : $"ip:{ip}";
        var limit = userId is null ? options.Value.GuestScansPerDay : options.Value.UserScansPerDay;
        return (limit, $"quota:scan:{subject}:{local:yyyyMMdd}", nextMidnightLocal.ToUniversalTime());
    }
}
