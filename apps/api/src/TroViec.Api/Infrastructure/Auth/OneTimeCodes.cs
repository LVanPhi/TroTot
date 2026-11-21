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

    public static readonly TimeSpan PhoneCodeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// potp:{userId} hash { phone, h = sha256(code), tries } TTL 5 min. The pending number lives only here until
    /// it is verified, so nobody can reserve someone else's number in app.users (BR-12, US-104 AC4).
    /// </summary>
    public async Task<string> NewPhoneCodeAsync(long userId, string phoneE164)
    {
        var code = Secrets.NewSixDigitCode();
        var db = redis.GetDatabase();
        var key = PhoneKey(userId);
        await db.KeyDeleteAsync(key);
        await db.HashSetAsync(key, [new HashEntry("phone", phoneE164), new HashEntry("h", Secrets.Hash(code)), new HashEntry("tries", 0)]);
        await db.KeyExpireAsync(key, PhoneCodeLifetime);
        return code;
    }

    /// <summary>Valid only for the number the code was sent to; 5 wrong codes cancel it (US-104 AC2).</summary>
    public async Task<CodeCheck> CheckPhoneCodeAsync(long userId, string phoneE164, string code)
    {
        var db = redis.GetDatabase();
        var key = PhoneKey(userId);
        var stored = await db.HashGetAsync(key, ["phone", "h"]);
        if (!stored[0].HasValue || stored[0] != phoneE164) return CodeCheck.Invalid;

        if (Secrets.HashesEqual(stored[1]!, Secrets.Hash(code)))
        {
            await db.KeyDeleteAsync(key);
            return CodeCheck.Valid;
        }

        var tries = await db.HashIncrementAsync(key, "tries");
        if (tries < MaxWrongAttempts) return CodeCheck.Invalid;
        await db.KeyDeleteAsync(key);
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
    private static string PhoneKey(long userId) => $"potp:{userId}";
}
