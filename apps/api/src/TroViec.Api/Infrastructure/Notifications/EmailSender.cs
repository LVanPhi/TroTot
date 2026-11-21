using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace TroViec.Api.Infrastructure.Notifications;

public sealed record EmailMessage(string To, string Subject, string TextBody);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>Smtp__* (dev: Mailpit localhost:1025, production: Brevo smtp-relay.brevo.com:587).</summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "Tro & Viec Sach <no-reply@example.com>";
}

/// <summary>MailKit sender. Logs only that a message type was sent, never the recipient (AGENTS.md rule 13).</summary>
public sealed class SmtpEmailSender(Microsoft.Extensions.Options.IOptions<SmtpOptions> options) : IEmailSender
{
    private readonly SmtpOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(_options.From));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.TextBody };

        using var client = new SmtpClient();
        // Port 587 negotiates STARTTLS; Mailpit on 1025 has no TLS.
        await client.ConnectAsync(_options.Host, _options.Port, SecureSocketOptions.Auto, ct);
        if (_options.User.Length > 0)
            await client.AuthenticateAsync(_options.User, _options.Password, ct);
        await client.SendAsync(new MailKit.SendRequest(mime), ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}

/// <summary>Vietnamese email texts. Kept here so wording is reviewed in one place.</summary>
public static class EmailTemplates
{
    public static EmailMessage VerificationCode(string to, string fullName, string code) => new(
        to,
        "Mã xác thực email Trọ & Việc Sạch",
        $"""
        Chào {fullName},

        Mã xác thực email của bạn là: {code}

        Mã có hiệu lực trong 10 phút. Nếu bạn không đăng ký tài khoản, hãy bỏ qua email này.

        Trọ & Việc Sạch
        """);

    public static EmailMessage PasswordReset(string to, string fullName, string link) => new(
        to,
        "Đặt lại mật khẩu Trọ & Việc Sạch",
        $"""
        Chào {fullName},

        Mở liên kết dưới đây để đặt mật khẩu mới (hiệu lực 30 phút, dùng một lần):
        {link}

        Nếu bạn không yêu cầu, hãy bỏ qua email này; mật khẩu hiện tại vẫn giữ nguyên.

        Trọ & Việc Sạch
        """);
}
