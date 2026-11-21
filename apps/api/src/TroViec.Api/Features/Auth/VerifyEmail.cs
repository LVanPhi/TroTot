using Dapper;
using FluentValidation;
using Npgsql;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Notifications;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Auth;

/// <summary>US-101 AC3: correct code sets email_verified_at; 5 wrong attempts cancel the code.</summary>
public static class VerifyEmail
{
    public sealed record Request(string Email, string Code);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.Email).NotEmpty().MaximumLength(254);
            RuleFor(r => r.Code).Matches("^[0-9]{6}$").WithMessage("Mã gồm 6 chữ số");
        }
    }

    public static async Task<IResult> HandleAsync(Request request, NpgsqlDataSource db, OneTimeCodes codes, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var user = await AuthUsers.ByEmailAsync(connection, request.Email, ct);
        if (user is null || user.Status != "active") throw new ProblemException("auth.code_invalid");
        if (user.EmailVerifiedAt is not null) return Results.NoContent();

        switch (await codes.CheckEmailCodeAsync(user.Id, request.Code))
        {
            case CodeCheck.AttemptsExceeded:
                throw new ProblemException("auth.code_attempts_exceeded");
            case CodeCheck.Invalid:
                throw new ProblemException("auth.code_invalid");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.users SET email_verified_at = now() WHERE id = @Id AND email_verified_at IS NULL",
            new { user.Id }, cancellationToken: ct));
        return Results.NoContent();
    }
}

/// <summary>Always 204, so the response never reveals whether an email is registered (docs/04-api.md).</summary>
public static class ResendEmail
{
    public sealed record Request(string Email);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator() => RuleFor(r => r.Email).NotEmpty().MaximumLength(254);
    }

    public static async Task<IResult> HandleAsync(
        Request request, HttpContext http, NpgsqlDataSource db, OneTimeCodes codes, Throttles throttles,
        IEmailSender email, CancellationToken ct)
    {
        // 3/hour/email and 10/hour/IP; over the limit still answers 204 but sends nothing.
        if (!await throttles.TryHitAsync("resend:email", request.Email, 3, TimeSpan.FromHours(1))
            | !await throttles.TryHitAsync("resend:ip", AuthSetup.ClientIp(http), 10, TimeSpan.FromHours(1)))
            return Results.NoContent();

        await using var connection = await db.OpenConnectionAsync(ct);
        var user = await AuthUsers.ByEmailAsync(connection, request.Email, ct);
        if (user is { Status: "active", EmailVerifiedAt: null, Email: not null })
        {
            var code = await codes.NewEmailCodeAsync(user.Id);
            await email.SendAsync(EmailTemplates.VerificationCode(user.Email, user.FullName, code), ct);
        }
        return Results.NoContent();
    }
}
