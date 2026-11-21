using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace TroViec.Security;

/// <summary>
/// Argon2id with OWASP minimum parameters (docs/07-security.md), stored as a PHC string:
/// $argon2id$v=19$m=19456,t=2,p=1$&lt;salt&gt;$&lt;hash&gt; (unpadded base64).
/// Shared by the API (sign-up, sign-in, password reset) and the Migrator (bootstrap-admin).
/// </summary>
public static class PasswordHasher
{
    private const int MemoryKib = 19456;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Prefix = "$argon2id$v=19$m=19456,t=2,p=1$";

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(password, salt, MemoryKib, Iterations, Parallelism, HashBytes);
        return Prefix + ToB64(salt) + "$" + ToB64(hash);
    }

    public static bool Verify(string password, string phc)
    {
        // "", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash
        var parts = phc.Split('$');
        if (parts.Length != 6 || parts[1] != "argon2id" || parts[2] != "v=19") return false;

        var parameters = parts[3].Split(',')
            .Select(p => p.Split('='))
            .ToDictionary(p => p[0], p => int.Parse(p[1], CultureInfo.InvariantCulture));
        var salt = FromB64(parts[4]);
        var expected = FromB64(parts[5]);
        var actual = Compute(password, salt, parameters["m"], parameters["t"], parameters["p"], expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Compute(string password, byte[] salt, int memoryKib, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(length);
    }

    private static string ToB64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] FromB64(string value) =>
        Convert.FromBase64String(value.PadRight(value.Length + (4 - value.Length % 4) % 4, '='));
}
