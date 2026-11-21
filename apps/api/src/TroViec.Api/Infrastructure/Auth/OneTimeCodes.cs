using StackExchange.Redis;

namespace TroViec.Api.Infrastructure.Auth;

public enum CodeCheck { Valid, Invalid, AttemptsExceeded }

/// <summary>
/// Email verification codes and password-reset tokens, stored hashed in Redis.
///   evc:{userId}   hash { h = sha256(code), tries } TTL 10 min, cancelled after 5 wrong attempts (US-101 AC2-3)
///   prt:{sha256}   → userId, TTL 30 min, single use (US-105)
/// </summary>
public sealed class OneTimeCodes(IConnectionMultiplexer redis)
{
    public static readonly TimeSpan EmailCodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromMinutes(30);
    public const int MaxWrongAttempts = 5;

    /// <summary>Creates a fresh code (replacing any previous one) and returns it for the email.</summary>
    public async Task<string> NewEmailCodeAsync(long userId)
    {
        var code = Secrets.NewSixDigitCode();
        var db = redis.GetDatabase();
        var key = EmailKey(userId);
        await db.KeyDeleteAsync(key);
        await db.HashSetAsync(key, [new HashEntry("h", Secrets.Hash(code)), new HashEntry("tries", 0)]);
        await db.KeyExpireAsync(key, EmailCodeLifetime);
        return code;
    }

    public async Task<CodeCheck> CheckEmailCodeAsync(long userId, string code)
    {
        var db = redis.GetDatabase();
        var key = EmailKey(userId);
        var stored = await db.HashGetAsync(key, "h");
        if (!stored.HasValue) return CodeCheck.Invalid;

        if (Secrets.HashesEqual(stored!, Secrets.Hash(code)))
        {
            await db.KeyDeleteAsync(key);
            return CodeCheck.Valid;
        }

        var tries = await db.HashIncrementAsync(key, "tries");
        if (tries < MaxWrongAttempts) return CodeCheck.Invalid;
        await db.KeyDeleteAsync(key);  // cancelled: the user must request a new code
        return CodeCheck.AttemptsExceeded;
    }

    public async Task<string> NewResetTokenAsync(long userId)
    {
        var token = Secrets.NewToken();
        await redis.GetDatabase().StringSetAsync($"prt:{Secrets.Hash(token)}", userId, ResetTokenLifetime);
        return token;
    }

    /// <summary>Consumes a reset token (GETDEL); null when unknown, expired or already used.</summary>
    public async Task<long?> ConsumeResetTokenAsync(string token)
    {
        var value = await redis.GetDatabase().StringGetDeleteAsync($"prt:{Secrets.Hash(token)}");
        return value.HasValue ? (long)value : null;
    }

    private static string EmailKey(long userId) => $"evc:{userId}";
}
