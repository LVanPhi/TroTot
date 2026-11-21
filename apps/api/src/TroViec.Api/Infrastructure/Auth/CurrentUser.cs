using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Infrastructure.Auth;

public static class CurrentUser
{
    /// <summary>The caller's user id from the "sub" claim of a validated access token.</summary>
    public static long Id(ClaimsPrincipal user) =>
        long.TryParse(user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new ProblemException("auth.unauthenticated");
}
