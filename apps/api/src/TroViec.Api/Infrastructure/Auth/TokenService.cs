using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace TroViec.Api.Infrastructure.Auth;

public sealed record AccessToken(string Token, int ExpiresIn);

/// <summary>HS256 access tokens: sub (user id), perm (one claim per permission), ver (uver at issue time).</summary>
public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    public const string PermissionClaim = "perm";
    public const string VersionClaim = "ver";

    private readonly JwtOptions _options = options.Value;

    public AccessToken Create(long userId, IEnumerable<string> permissions, long version)
    {
        var now = clock.GetUtcNow();
        var lifetime = TimeSpan.FromMinutes(_options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(VersionClaim, version.ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
        };
        claims.AddRange(permissions.Select(p => new Claim(PermissionClaim, p)));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + lifetime).UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(_options.SigningKeyBytes()), SecurityAlgorithms.HmacSha256),
        };
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        return new AccessToken(handler.WriteToken(handler.CreateToken(descriptor)), (int)lifetime.TotalSeconds);
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidIssuer = _options.Issuer,
        ValidAudience = _options.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(_options.SigningKeyBytes()),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = JwtRegisteredClaimNames.Sub,
    };
}
