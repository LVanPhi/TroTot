using System.Globalization;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace TroViec.Api.Infrastructure.Auth;

public enum RefreshOutcome { Rotated, Invalid, Reused }

public sealed record RefreshResult(RefreshOutcome Outcome, long UserId = 0, string? NewToken = null);

/// <summary>
/// Rotating refresh tokens in Redis (docs/07-security.md, Phiên đăng nhập). Only SHA-256 hashes are stored:
///   rt:{hash}       → "userId:familyId" (TTL = refresh lifetime)
///   rtf:{familyId}  → set of hashes in the family
///   rtusr:{userId}  → set of the user's family ids (to revoke all sessions)
///   rtu:{hash}      → "userId:familyId" tombstone of an already-used token (reuse detection)
/// </summary>
public sealed class RefreshTokenStore(IConnectionMultiplexer redis, IOptions<JwtOptions> options)
{
    private readonly TimeSpan _lifetime = TimeSpan.FromDays(options.Value.RefreshTokenDays);

    public TimeSpan Lifetime => _lifetime;

    /// <summary>Starts a new family (a new sign-in) and returns its first token.</summary>
    public Task<string> IssueAsync(long userId) => AddToFamilyAsync(userId, Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Atomically consumes the token (GETDEL). Valid → new token in the same family. Unknown but tombstoned →
    /// someone replayed an old token: the whole family is revoked. Otherwise invalid.
    /// </summary>
    public async Task<RefreshResult> RotateAsync(string token)
    {
        var db = redis.GetDatabase();
        var hash = Secrets.Hash(token);
        var value = await db.StringGetDeleteAsync($"rt:{hash}");
        if (value.HasValue)
        {
            var (userId, familyId) = Parse(value!);
            await db.StringSetAsync($"rtu:{hash}", value, _lifetime);
            await db.SetRemoveAsync($"rtf:{familyId}", hash);
            return new RefreshResult(RefreshOutcome.Rotated, userId, await AddToFamilyAsync(userId, familyId));
        }

        var tombstone = await db.StringGetAsync($"rtu:{hash}");
        if (!tombstone.HasValue) return new RefreshResult(RefreshOutcome.Invalid);

        var (reusedBy, reusedFamily) = Parse(tombstone!);
        await RevokeFamilyAsync(reusedBy, reusedFamily);
        return new RefreshResult(RefreshOutcome.Reused, reusedBy);
    }

    /// <summary>Logout: revoke the family the presented token belongs to (live or already rotated).</summary>
    public async Task RevokeFamilyOfAsync(string token)
    {
        var db = redis.GetDatabase();
        var hash = Secrets.Hash(token);
        var value = await db.StringGetAsync($"rt:{hash}");
        if (!value.HasValue) value = await db.StringGetAsync($"rtu:{hash}");
        if (!value.HasValue) return;
        var (userId, familyId) = Parse(value!);
        await RevokeFamilyAsync(userId, familyId);
    }

    /// <summary>Password reset, lock, deletion: every session of the user ends.</summary>
    public async Task RevokeAllAsync(long userId)
    {
        var db = redis.GetDatabase();
        foreach (var familyId in await db.SetMembersAsync($"rtusr:{userId}"))
            await RevokeFamilyAsync(userId, familyId!);
        await db.KeyDeleteAsync($"rtusr:{userId}");
    }

    private async Task<string> AddToFamilyAsync(long userId, string familyId)
    {
        var db = redis.GetDatabase();
        var token = Secrets.NewToken();
        var hash = Secrets.Hash(token);
        var tx = db.CreateTransaction();
        _ = tx.StringSetAsync($"rt:{hash}", $"{userId}:{familyId}", _lifetime);
        _ = tx.SetAddAsync($"rtf:{familyId}", hash);
        _ = tx.KeyExpireAsync($"rtf:{familyId}", _lifetime);
        _ = tx.SetAddAsync($"rtusr:{userId}", familyId);
        _ = tx.KeyExpireAsync($"rtusr:{userId}", _lifetime);
        await tx.ExecuteAsync();
        return token;
    }

    private async Task RevokeFamilyAsync(long userId, string familyId)
    {
        var db = redis.GetDatabase();
        var hashes = await db.SetMembersAsync($"rtf:{familyId}");
        var keys = hashes.Select(h => (RedisKey)$"rt:{h}").Append($"rtf:{familyId}").ToArray();
        await db.KeyDeleteAsync(keys);
        await db.SetRemoveAsync($"rtusr:{userId}", familyId);
    }

    private static (long UserId, string FamilyId) Parse(string value)
    {
        var parts = value.Split(':', 2);
        return (long.Parse(parts[0], CultureInfo.InvariantCulture), parts[1]);
    }
}
