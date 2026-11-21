using Dapper;
using FluentValidation;
using Npgsql;
using TroViec.Api.Domain;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Notifications;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Features.Auth;

/// <summary>US-104: verify a Vietnamese mobile number with a 6-digit SMS code (5 min, 5 wrong attempts).</summary>
public static class Phone
{
    public sealed record OtpRequest(string Phone);

    public sealed record VerifyRequest(string Phone, string Code);

    public sealed class OtpValidator : AbstractValidator<OtpRequest>
    {
        public OtpValidator() => RuleFor(r => r.Phone)
            .Must(p => PhoneNumber.TryParse(p, out _)).WithMessage("Số điện thoại di động Việt Nam không hợp lệ");
    }

    public sealed class VerifyValidator : AbstractValidator<VerifyRequest>
    {
        public VerifyValidator()
        {
            RuleFor(r => r.Phone).Must(p => PhoneNumber.TryParse(p, out _))
                .WithMessage("Số điện thoại di động Việt Nam không hợp lệ");
            RuleFor(r => r.Code).Matches("^[0-9]{6}$").WithMessage("Mã gồm 6 chữ số");
        }
    }

    public static async Task<IResult> SendOtpAsync(
        OtpRequest request, HttpContext http, NpgsqlDataSource db, OneTimeCodes codes, Throttles throttles,
        ISmsSender sms, CancellationToken ct)
    {
        var userId = CurrentUser.Id(http.User);
        PhoneNumber.TryParse(request.Phone, out var phone);
        if (!sms.CanSend) throw new ProblemException("phone.sms_unavailable");

        await using (var connection = await db.OpenConnectionAsync(ct))
        {
            if (await TakenByOtherAsync(connection, phone!.E164, userId, ct)) throw new ProblemException("auth.phone_taken");
        }

        // 3/hour/number and 10/day/account (docs/04-api.md, Hạn mức).
        if (!await throttles.TryHitAsync("otp:phone", phone.E164, 3, TimeSpan.FromHours(1))
            || !await throttles.TryHitAsync("otp:user", userId.ToString(System.Globalization.CultureInfo.InvariantCulture), 10, TimeSpan.FromDays(1)))
            throw new ProblemException("rate_limited");

        var code = await codes.NewPhoneCodeAsync(userId, phone.E164);
        await sms.SendOtpAsync(userId, phone.E164, code, ct);
        return Results.NoContent();
    }

    public static async Task<IResult> VerifyAsync(
        VerifyRequest request, HttpContext http, NpgsqlDataSource db, OneTimeCodes codes, CancellationToken ct)
    {
        var userId = CurrentUser.Id(http.User);
        PhoneNumber.TryParse(request.Phone, out var phone);

        await using var connection = await db.OpenConnectionAsync(ct);
        // Checked again here: another account may have verified the same number since the code was sent.
        if (await TakenByOtherAsync(connection, phone!.E164, userId, ct)) throw new ProblemException("auth.phone_taken");

        switch (await codes.CheckPhoneCodeAsync(userId, phone.E164, request.Code))
        {
            case CodeCheck.AttemptsExceeded:
                throw new ProblemException("auth.code_attempts_exceeded");
            case CodeCheck.Invalid:
                throw new ProblemException("auth.code_invalid");
        }

        // A race with another account still ends as 23505 uq_users_phone_e164 → 409 auth.phone_taken.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE app.users SET phone_e164 = @Phone, phone_verified_at = now() WHERE id = @Id",
            new { Phone = phone.E164, Id = userId }, cancellationToken: ct));
        return Results.NoContent();
    }

    private static Task<bool> TakenByOtherAsync(NpgsqlConnection connection, string phoneE164, long userId, CancellationToken ct) =>
        connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM app.users WHERE phone_e164 = @Phone AND id <> @UserId)",
            new { Phone = phoneE164, UserId = userId }, cancellationToken: ct));
}
