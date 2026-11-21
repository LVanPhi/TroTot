using System.Security.Cryptography;
using System.Text;

namespace TroViec.Api.Infrastructure.Crypto;

/// <summary>
/// HMAC-SHA256 of identifiers (phone, bank_account, url), shared with the worker (pipeline/identifiers.py):
/// key = bytes decoded from base64 Crypto__IdentifierHmacKey (= IDENTIFIER_HMAC_KEY), input = "{kind}:{normalized}".
/// Both sides keep a common test vector so a change on one side cannot make hashes diverge silently.
/// </summary>
public sealed class IdentifierHasher
{
    private readonly byte[] _key;

    public IdentifierHasher(string base64Key)
    {
        try { _key = Convert.FromBase64String(base64Key); }
        catch (FormatException) { throw new InvalidOperationException("Crypto__IdentifierHmacKey must be base64"); }
        if (_key.Length < 32) throw new InvalidOperationException("Crypto__IdentifierHmacKey must decode to at least 32 bytes");
    }

    public byte[] Hash(string kind, string normalized) =>
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{kind}:{normalized}"));
}
