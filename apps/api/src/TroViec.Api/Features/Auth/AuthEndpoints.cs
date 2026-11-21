using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Auth;

/// <summary>/api/v1/auth (docs/04-api.md, Xác thực). All anonymous: refresh/logout authenticate with the cookie.</summary>
public static class AuthEndpoints
{
    public const string RefreshCookie = "tv_refresh";
    public const string CookiePath = "/api/v1/auth";
    public const string CsrfHeader = "X-Requested-With";
    public const string CsrfValue = "troviec";

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth").WithTags("Auth").AllowAnonymous();

        auth.MapPost("/register", Register.HandleAsync).Validate<Register.Request>()
            .RequireRateLimiting(AuthSetup.RegisterLimit);
        auth.MapPost("/email/verify", VerifyEmail.HandleAsync).Validate<VerifyEmail.Request>();
        auth.MapPost("/email/resend", ResendEmail.HandleAsync).Validate<ResendEmail.Request>();
        auth.MapPost("/login", Login.HandleAsync).Validate<Login.Request>()
            .RequireRateLimiting(AuthSetup.LoginLimit);
        auth.MapPost("/refresh", Session.RefreshAsync);
        auth.MapPost("/logout", Session.LogoutAsync);
        auth.MapPost("/password/forgot", PasswordReset.ForgotAsync).Validate<PasswordReset.ForgotRequest>();
        auth.MapPost("/password/reset", PasswordReset.ResetAsync).Validate<PasswordReset.ResetRequest>();
    }

    /// <summary>tv_refresh; HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth (US-103 AC1).</summary>
    public static void SetRefreshCookie(HttpResponse response, string token, TimeSpan lifetime) =>
        response.Cookies.Append(RefreshCookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
            MaxAge = lifetime,
        });

    public static void ClearRefreshCookie(HttpResponse response) =>
        response.Cookies.Delete(RefreshCookie, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = CookiePath,
        });

    /// <summary>CSRF guard for the two cookie-authenticated endpoints (docs/07-security.md).</summary>
    public static void RequireCsrfHeader(HttpRequest request)
    {
        if (request.Headers[CsrfHeader] != CsrfValue) throw new ProblemException("auth.forbidden");
    }
}
