using System.Security.Cryptography;
using System.Text;

namespace TroViec.Api.Infrastructure.Auth;

/// <summary>Random tokens, numeric codes and hashes shared by refresh tokens, email codes and reset links.</summary>
public static class Secrets
{
    /// <summary>256-bit random token, base64url without padding.</summary>
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Six-digit code from a CSPRNG, zero-padded ("004213").</summary>
    public static string NewSixDigitCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>SHA-256 hex; stored instead of the secret itself so a Redis dump does not leak usable tokens.</summary>
    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Constant-time comparison of two hex hashes.</summary>
    public static bool HashesEqual(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
