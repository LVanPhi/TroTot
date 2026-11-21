namespace TroViec.Api.Infrastructure.Auth;

/// <summary>Jwt__* (docs/08-deployment.md). SigningKey is base64 of at least 32 random bytes.</summary>
public sealed class JwtOptions
{
    public string SigningKey { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;

    public byte[] SigningKeyBytes()
    {
        byte[] key;
        try { key = Convert.FromBase64String(SigningKey); }
        catch (FormatException) { throw new InvalidOperationException("Jwt__SigningKey must be base64"); }
        return key.Length >= 32 ? key : throw new InvalidOperationException("Jwt__SigningKey must decode to at least 32 bytes");
    }
}

/// <summary>Auth__StudentEmailDomains: comma-separated school email domains (BR-13).</summary>
public sealed class StudentEmailDomains(string? configured)
{
    private readonly HashSet<string> _domains = (configured ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(d => d.ToLowerInvariant())
        .ToHashSet();

    public bool Contains(string email)
    {
        var at = email.LastIndexOf('@');
        return at > 0 && _domains.Contains(email[(at + 1)..].ToLowerInvariant());
    }
}

/// <summary>App__SiteUrl: public web origin used in emailed links (e.g. password reset).</summary>
public sealed record SiteUrl(string Value)
{
    public string Link(string pathAndQuery) => Value.TrimEnd('/') + pathAndQuery;
}
