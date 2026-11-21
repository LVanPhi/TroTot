using Dapper;
using FluentValidation;
using Npgsql;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Notifications;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Security;

namespace TroViec.Api.Features.Auth;

/// <summary>US-105: emailed single-use link (30 min); resetting ends every session and invalidates access tokens.</summary>
public static class PasswordReset
{
    public sealed record ForgotRequest(string Email);

    public sealed record ResetRequest(string Token, string NewPassword);

    public sealed class ForgotValidator : AbstractValidator<ForgotRequest>
    {
        public ForgotValidator() => RuleFor(r => r.Email).NotEmpty().MaximumLength(254);
    }

    public sealed class ResetValidator : AbstractValidator<ResetRequest>
    {
        public ResetValidator()
        {
            RuleFor(r => r.Token).NotEmpty().MaximumLength(100);
            RuleFor(r => r.NewPassword).NotEmpty().MaximumLength(256);
        }
    }

    /// <summary>Always 204 (never reveals whether the email exists); 3/hour/email and 10/hour/IP.</summary>
    public static async Task<IResult> ForgotAsync(
        ForgotRequest request, HttpContext http, NpgsqlDataSource db, OneTimeCodes codes, Throttles throttles,
        IEmailSender email, SiteUrl site, CancellationToken ct)
    {
        if (!await throttles.TryHitAsync("forgot:email", request.Email, 3, TimeSpan.FromHours(1))
            | !await throttles.TryHitAsync("forgot:ip", AuthSetup.ClientIp(http), 10, TimeSpan.FromHours(1)))
            return Results.NoContent();

        await using var connection = await db.OpenConnectionAsync(ct);
        var user = await AuthUsers.ByEmailAsync(connection, request.Email, ct);
        if (user is { Status: "active", Email: not null })
        {
            var token = await codes.NewResetTokenAsync(user.Id);
            var link = site.Link($"/dat-lai-mat-khau?token={Uri.EscapeDataString(token)}");
            await email.SendAsync(EmailTemplates.PasswordReset(user.Email, user.FullName, link), ct);
        }
        return Results.NoContent();
    }

    public static async Task<IResult> ResetAsync(
        ResetRequest request, NpgsqlDataSource db, OneTimeCodes codes, RefreshTokenStore refreshTokens,
        UserVersionStore versions, Throttles throttles, CancellationToken ct)
    {
        // Check the new password first so a weak password does not burn the single-use token.
        if (PasswordPolicy.Validate(request.NewPassword) is not null) throw new ProblemException("auth.weak_password");
        if (await codes.ConsumeResetTokenAsync(request.Token) is not { } userId)
            throw new ProblemException("auth.code_invalid");

        await using var connection = await db.OpenConnectionAsync(ct);
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.users SET password_hash = @Hash WHERE id = @Id AND status = 'active'",
            new { Hash = PasswordHasher.Hash(request.NewPassword), Id = userId }, cancellationToken: ct));
        if (updated == 0) throw new ProblemException("auth.code_invalid");

        await refreshTokens.RevokeAllAsync(userId);
        await versions.BumpAsync(userId);
        await throttles.ClearLoginFailuresAsync(userId);
        return Results.NoContent();
    }
}
