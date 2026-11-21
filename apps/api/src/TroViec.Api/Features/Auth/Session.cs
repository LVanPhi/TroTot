using Npgsql;
using TroViec.Api.Features.Me;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Auth;

/// <summary>POST /auth/refresh and /auth/logout: authenticated by the tv_refresh cookie, CSRF header required.</summary>
public static class Session
{
    public sealed record RefreshResponse(string AccessToken, int ExpiresIn);

    public static async Task<IResult> RefreshAsync(
        HttpContext http,
        NpgsqlDataSource db,
        TokenService tokens,
        RefreshTokenStore refreshTokens,
        UserVersionStore versions,
        CancellationToken ct)
    {
        AuthEndpoints.RequireCsrfHeader(http.Request);
        if (http.Request.Cookies[AuthEndpoints.RefreshCookie] is not { Length: > 0 } presented)
            throw new ProblemException("auth.refresh_invalid");

        var result = await refreshTokens.RotateAsync(presented);
        switch (result.Outcome)
        {
            case RefreshOutcome.Reused:
                // US-103 AC2: a replayed token revokes its whole family.
                AuthEndpoints.ClearRefreshCookie(http.Response);
                throw new ProblemException("auth.refresh_reused");
            case RefreshOutcome.Invalid:
                AuthEndpoints.ClearRefreshCookie(http.Response);
                throw new ProblemException("auth.refresh_invalid");
        }

        await using var connection = await db.OpenConnectionAsync(ct);
        var user = await AuthUsers.ByIdAsync(connection, result.UserId, ct);
        if (user is not { Status: "active" })
        {
            await refreshTokens.RevokeAllAsync(result.UserId);
            AuthEndpoints.ClearRefreshCookie(http.Response);
            throw new ProblemException("auth.refresh_invalid");
        }

        // Permissions are reloaded on every refresh, so a role change takes effect without signing in again.
        var permissions = await MeQueries.PermissionsAsync(connection, user.Id, ct);
        var access = tokens.Create(user.Id, permissions, await versions.GetAsync(user.Id));
        AuthEndpoints.SetRefreshCookie(http.Response, result.NewToken!, refreshTokens.Lifetime);
        return Results.Ok(new RefreshResponse(access.Token, access.ExpiresIn));
    }

    public static async Task<IResult> LogoutAsync(HttpContext http, RefreshTokenStore refreshTokens)
    {
        AuthEndpoints.RequireCsrfHeader(http.Request);
        if (http.Request.Cookies[AuthEndpoints.RefreshCookie] is { Length: > 0 } presented)
            await refreshTokens.RevokeFamilyOfAsync(presented);
        AuthEndpoints.ClearRefreshCookie(http.Response);
        return Results.NoContent();
    }
}
