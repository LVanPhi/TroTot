using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace TroViec.Api.Infrastructure.Auth;

/// <summary>
/// uver:{userId} in Redis. Bumped on password reset, lock, role change, account deletion so every access token
/// carrying an older "ver" is rejected (docs/07-security.md). Reads are cached 30 s; a bump in this process clears
/// its own cache entry so the change is immediate here and within 30 s on other API instances.
/// </summary>
public sealed class UserVersionStore(IConnectionMultiplexer redis, IMemoryCache cache)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    public async Task<long> GetAsync(long userId)
    {
        if (cache.TryGetValue(CacheKey(userId), out long cached)) return cached;
        var value = await redis.GetDatabase().StringGetAsync(Key(userId));
        var version = value.HasValue ? (long)value : 0;
        cache.Set(CacheKey(userId), version, CacheFor);
        return version;
    }

    public async Task<long> BumpAsync(long userId)
    {
        var version = await redis.GetDatabase().StringIncrementAsync(Key(userId));
        cache.Remove(CacheKey(userId));
        return version;
    }

    private static string Key(long userId) => $"uver:{userId}";
    private static string CacheKey(long userId) => $"uver-cache:{userId}";
}
