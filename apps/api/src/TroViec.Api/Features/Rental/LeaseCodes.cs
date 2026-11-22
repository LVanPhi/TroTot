using StackExchange.Redis;
using TroViec.Api.Infrastructure.Auth;

namespace TroViec.Api.Features.Rental;

/// <summary>
/// One-time codes for confirming a lease (BR-25): lotp:{leaseId}:{userId} hash { h = sha256(code), tries },
/// TTL 10 minutes, cancelled after 5 wrong attempts. Same scheme as the email verification code.
/// </summary>
public sealed class LeaseCodes(IConnectionMultiplexer redis)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private static string Key(long leaseId, long userId) => $"lotp:{leaseId}:{userId}";

    public async Task<string> NewAsync(long leaseId, long userId)
    {
        var code = Secrets.NewSixDigitCode();
        var db = redis.GetDatabase();
        var key = Key(leaseId, userId);
        await db.KeyDeleteAsync(key);
        await db.HashSetAsync(key, [new HashEntry("h", Secrets.Hash(code)), new HashEntry("tries", 0)]);
        await db.KeyExpireAsync(key, Lifetime);
        return code;
    }

    public async Task<CodeCheck> CheckAsync(long leaseId, long userId, string code)
    {
        var db = redis.GetDatabase();
        var key = Key(leaseId, userId);
        var stored = await db.HashGetAsync(key, "h");
        if (!stored.HasValue) return CodeCheck.Invalid;
        if (Secrets.HashesEqual(stored!, Secrets.Hash(code)))
        {
            await db.KeyDeleteAsync(key);
            return CodeCheck.Valid;
        }
        var tries = await db.HashIncrementAsync(key, "tries");
        if (tries < OneTimeCodes.MaxWrongAttempts) return CodeCheck.Invalid;
        await db.KeyDeleteAsync(key);
        return CodeCheck.AttemptsExceeded;
    }
}
