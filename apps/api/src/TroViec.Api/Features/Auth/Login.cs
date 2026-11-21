using FluentValidation;
using Npgsql;
using TroViec.Api.Features.Me;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Security;

namespace TroViec.Api.Features.Auth;

/// <summary>US-103: access token in the body, refresh token in the tv_refresh cookie.</summary>
public static class Login
{
    public sealed record Request(string Email, string Password);

    public sealed record Response(string AccessToken, int ExpiresIn, MeResponse User);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.Email).NotEmpty().MaximumLength(254);
            RuleFor(r => r.Password).NotEmpty().MaximumLength(256);
        }
    }

    // Verified against when the email is unknown, so both paths cost one Argon2id run (no timing oracle).
    private static readonly Lazy<string> DummyHash = new(() => PasswordHasher.Hash(Guid.NewGuid().ToString()));

    public static async Task<IResult> HandleAsync(
        Request request,
        HttpContext http,
        NpgsqlDataSource db,
        Throttles throttles,
        TokenService tokens,
        RefreshTokenStore refreshTokens,
        UserVersionStore versions,
        StudentEmailDomains studentDomains,
        CancellationToken ct)
    {
        var ip = AuthSetup.ClientIp(http);
        await using var connection = await db.OpenConnectionAsync(ct);
        var user = await AuthUsers.ByEmailAsync(connection, request.Email, ct);

        if (await throttles.IsLoginLockedAsync(user?.Id, ip)) throw new ProblemException("auth.locked_temporarily");

        var passwordOk = PasswordHasher.Verify(request.Password, user?.PasswordHash is { Length: > 1 } h ? h : DummyHash.Value);
        if (user is null || !passwordOk)
        {
            await throttles.RecordLoginFailureAsync(user?.Id, ip);
            throw new ProblemException("auth.invalid_credentials");
        }

        // US-103 AC4: locked or anonymized accounts cannot sign in.
        if (user.Status != "active") throw new ProblemException("auth.account_locked");
        if (user.EmailVerifiedAt is null) throw new ProblemException("auth.email_not_verified");

        await throttles.ClearLoginFailuresAsync(user.Id);
        var me = await MeQueries.GetAsync(connection, user.Id, studentDomains, ct)
            ?? throw new ProblemException("auth.account_locked");
        var access = tokens.Create(user.Id, me.Permissions, await versions.GetAsync(user.Id));
        AuthEndpoints.SetRefreshCookie(http.Response, await refreshTokens.IssueAsync(user.Id), refreshTokens.Lifetime);
        return Results.Ok(new Response(access.Token, access.ExpiresIn, me));
    }
}
