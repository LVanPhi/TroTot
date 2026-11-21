using StackExchange.Redis;

namespace TroViec.Api.Infrastructure.Auth;

/// <summary>
/// Redis fixed-window counters for limits that must be keyed by something other than IP (email, account),
/// plus the login lockout (US-103 AC3: 5 wrong passwords in 15 minutes → locked for 15 minutes).
/// </summary>
public sealed class Throttles(IConnectionMultiplexer redis)
{
    public const int LoginMaxFailures = 5;
    public static readonly TimeSpan LoginWindow = TimeSpan.FromMinutes(15);

    /// <summary>Counts one hit; false once the subject has used up <paramref name="limit"/> in the window.</summary>
    public async Task<bool> TryHitAsync(string name, string subject, int limit, TimeSpan window)
    {
        var db = redis.GetDatabase();
        var key = $"rl:{name}:{Secrets.Hash(subject.ToLowerInvariant())}";
        var count = await db.StringIncrementAsync(key);
        if (count == 1) await db.KeyExpireAsync(key, window);
        return count <= limit;
    }

    public async Task<bool> IsLoginLockedAsync(long? userId, string ip)
    {
        var db = redis.GetDatabase();
        if (await db.KeyExistsAsync(LockKey("ip", ip))) return true;
        return userId is { } id && await db.KeyExistsAsync(LockKey("acct", id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>Records a wrong password for the account (when known) and the IP; locks either at 5 failures.</summary>
    public async Task RecordLoginFailureAsync(long? userId, string ip)
    {
        await CountFailureAsync("ip", ip);
        if (userId is { } id) await CountFailureAsync("acct", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task ClearLoginFailuresAsync(long userId)
    {
        var id = userId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await redis.GetDatabase().KeyDeleteAsync([FailKey("acct", id), LockKey("acct", id)]);
    }

    private async Task CountFailureAsync(string scope, string subject)
    {
        var db = redis.GetDatabase();
        var failKey = FailKey(scope, subject);
        var failures = await db.StringIncrementAsync(failKey);
        if (failures == 1) await db.KeyExpireAsync(failKey, LoginWindow);
        if (failures >= LoginMaxFailures)
        {
            await db.StringSetAsync(LockKey(scope, subject), 1, LoginWindow);
            await db.KeyDeleteAsync(failKey);
        }
    }

    private static string FailKey(string scope, string subject) => $"login:fail:{scope}:{Secrets.Hash(subject)}";
    private static string LockKey(string scope, string subject) => $"login:lock:{scope}:{Secrets.Hash(subject)}";
}
