using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Infrastructure.Crypto;

/// <summary>
/// Owner documents at rest (docs/07-security.md, Mã hóa và khóa): AES-256-GCM, random 96-bit nonce per file,
/// stored as nonce ‖ ciphertext ‖ tag. Keys come from Crypto__DocumentKeys (JSON {"k1": "base64 32 bytes"}) and
/// Crypto__ActiveDocumentKeyId; owner_documents.key_id records the key used, so old files still open after rotation.
/// </summary>
public sealed class DocumentCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly Dictionary<string, byte[]> _keys;

    public DocumentCipher(string keysJson, string activeKeyId)
    {
        Dictionary<string, string>? raw;
        try { raw = JsonSerializer.Deserialize<Dictionary<string, string>>(keysJson); }
        catch (JsonException) { throw new InvalidOperationException("Crypto__DocumentKeys must be a JSON object of base64 keys"); }
        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (id, value) in raw ?? [])
        {
            byte[] key;
            try { key = Convert.FromBase64String(value); }
            catch (FormatException) { throw new InvalidOperationException($"Crypto__DocumentKeys[{id}] must be base64"); }
            if (key.Length != 32) throw new InvalidOperationException($"Crypto__DocumentKeys[{id}] must decode to 32 bytes");
            _keys[id] = key;
        }
        if (!_keys.ContainsKey(activeKeyId))
            throw new InvalidOperationException("Crypto__ActiveDocumentKeyId must name a key in Crypto__DocumentKeys");
        ActiveKeyId = activeKeyId;
    }

    public string ActiveKeyId { get; }

    public byte[] Encrypt(byte[] plain) => Encrypt(plain, null);

    /// <summary>
    /// With associated data the ciphertext is bound to its context (e.g. "payout-account:{ownerId}"): copied to
    /// another row it no longer decrypts (docs/12-rental.md, Bảo mật và riêng tư).
    /// </summary>
    public byte[] Encrypt(byte[] plain, byte[]? associatedData)
    {
        var blob = new byte[NonceSize + plain.Length + TagSize];
        var nonce = blob.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_keys[ActiveKeyId], TagSize);
        aes.Encrypt(nonce, plain, blob.AsSpan(NonceSize, plain.Length), blob.AsSpan(NonceSize + plain.Length), associatedData);
        return blob;
    }

    /// <summary>Throws CryptographicException when the blob was altered or the key is wrong.</summary>
    public byte[] Decrypt(string keyId, byte[] blob) => Decrypt(keyId, blob, null);

    /// <summary>associatedData must equal the value given to Encrypt.</summary>
    public byte[] Decrypt(string keyId, byte[] blob, byte[]? associatedData)
    {
        if (!_keys.TryGetValue(keyId, out var key)) throw new CryptographicException($"unknown document key {keyId}");
        if (blob.Length < NonceSize + TagSize) throw new CryptographicException("document blob too short");
        var plain = new byte[blob.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize, plain.Length), blob.AsSpan(NonceSize + plain.Length), plain,
            associatedData);
        return plain;
    }
}

/// <summary>
/// Links for opening a document (docs/07-security.md): token = base64url(docId ‖ viewerId ‖ exp ‖ HMAC-SHA256),
/// valid 5 minutes, keyed by Crypto__SignedUrlKey. The link can be reopened until it expires (BR-10).
/// </summary>
public sealed class SignedUrlService(byte[] key, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const int PayloadSize = 24;
    private const int MacSize = 32;

    public static SignedUrlService FromBase64(string base64Key, TimeProvider clock)
    {
        byte[] key;
        try { key = Convert.FromBase64String(base64Key); }
        catch (FormatException) { throw new InvalidOperationException("Crypto__SignedUrlKey must be base64"); }
        if (key.Length < 32) throw new InvalidOperationException("Crypto__SignedUrlKey must decode to at least 32 bytes");
        return new SignedUrlService(key, clock);
    }

    public (string Token, DateTimeOffset ExpiresAt) Create(long documentId, long viewerId)
    {
        var expires = clock.GetUtcNow() + Lifetime;
        var bytes = new byte[PayloadSize + MacSize];
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(0, 8), documentId);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(8, 8), viewerId);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(16, 8), expires.ToUnixTimeSeconds());
        HMACSHA256.HashData(key, bytes.AsSpan(0, PayloadSize), bytes.AsSpan(PayloadSize));
        return (Base64Url.Encode(bytes), DateTimeOffset.FromUnixTimeSeconds(expires.ToUnixTimeSeconds()));
    }

    /// <summary>(documentId, viewerId) of a valid, unexpired token; otherwise 403 document.token_invalid.</summary>
    public (long DocumentId, long ViewerId) Validate(string token)
    {
        var bytes = Base64Url.TryDecode(token);
        if (bytes is not { Length: PayloadSize + MacSize }) throw new ProblemException("document.token_invalid");
        var expected = HMACSHA256.HashData(key, bytes.AsSpan(0, PayloadSize));
        if (!CryptographicOperations.FixedTimeEquals(expected, bytes.AsSpan(PayloadSize)))
            throw new ProblemException("document.token_invalid");
        var expires = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(16, 8));
        if (clock.GetUtcNow().ToUnixTimeSeconds() >= expires) throw new ProblemException("document.token_invalid");
        return (BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0, 8)), BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(8, 8)));
    }
}

internal static class Base64Url
{
    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? TryDecode(string text)
    {
        if (text.Length > 200) return null;
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }
}

public static class DocumentCryptoSetup
{
    /// <summary>Read lazily: test configuration is applied after services are registered.</summary>
    public static IServiceCollection AddDocumentCrypto(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            return new DocumentCipher(
                config["Crypto:DocumentKeys"] ?? throw new InvalidOperationException("Crypto__DocumentKeys is not set"),
                config["Crypto:ActiveDocumentKeyId"] ?? throw new InvalidOperationException("Crypto__ActiveDocumentKeyId is not set"));
        });
        services.AddSingleton(sp => SignedUrlService.FromBase64(
            sp.GetRequiredService<IConfiguration>()["Crypto:SignedUrlKey"] ?? throw new InvalidOperationException("Crypto__SignedUrlKey is not set"),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
