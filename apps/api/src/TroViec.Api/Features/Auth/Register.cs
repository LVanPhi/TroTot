using Dapper;
using FluentValidation;
using Npgsql;
using TroViec.Api.Infrastructure.Auth;
using TroViec.Api.Infrastructure.Notifications;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Security;

namespace TroViec.Api.Features.Auth;

/// <summary>US-101, US-102: student (school email, BR-13) or owner account; sends a 6-digit email code.</summary>
public static class Register
{
    public sealed record Request(string Email, string Password, string FullName, string AccountType);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.Email).NotEmpty().MaximumLength(254).EmailAddress().WithMessage("Email không hợp lệ");
            RuleFor(r => r.Password).NotEmpty().MaximumLength(256);
            RuleFor(r => r.FullName).NotEmpty().MaximumLength(100).WithMessage("Họ tên từ 1 đến 100 ký tự");
            // renter: người thuê không phải sinh viên (docs/12-rental.md, Phân quyền); any verified email.
            RuleFor(r => r.AccountType).Must(t => t is "student" or "owner" or "renter")
                .WithMessage("Loại tài khoản là student, owner hoặc renter");
        }
    }

    public static async Task<IResult> HandleAsync(
        Request request,
        NpgsqlDataSource db,
        StudentEmailDomains studentDomains,
        OneTimeCodes codes,
        IEmailSender email,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (PasswordPolicy.Validate(request.Password) is not null) throw new ProblemException("auth.weak_password");
        if (request.AccountType == "student" && !studentDomains.Contains(request.Email))
            throw new ProblemException("auth.student_email_domain");

        var emailAddress = request.Email.Trim();
        var fullName = request.FullName.Trim();
        long userId;
        await using (var connection = await db.OpenConnectionAsync(ct))
        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            // Duplicate email → 23505 uq_users_email → 409 auth.email_taken (PgErrorMapper).
            userId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO app.users (email, password_hash, full_name)
                VALUES (@Email, @PasswordHash, @FullName)
                RETURNING id
                """,
                new { Email = emailAddress, PasswordHash = PasswordHasher.Hash(request.Password), FullName = fullName },
                tx, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO app.user_roles (user_id, role_id, granter_id)
                SELECT @UserId, r.id, app.system_user_id() FROM app.roles r WHERE r.code = @Role
                """,
                new { UserId = userId, Role = request.AccountType }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }

        var code = await codes.NewEmailCodeAsync(userId);
        await email.SendAsync(EmailTemplates.VerificationCode(emailAddress, fullName, code), ct);
        loggers.CreateLogger(nameof(Register)).LogInformation("Registered user {UserId}", userId);
        return Results.StatusCode(StatusCodes.Status201Created);
    }
}
