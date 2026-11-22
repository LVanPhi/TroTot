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

    /// <summary>US-902: the poster learns the outcome of a moderation decision; neutral wording, link to the listing page.</summary>
    public static EmailMessage ListingDecision(string to, string fullName, string title, ListingNotice notice, string? reason, string link)
    {
        var (subject, what) = notice switch
        {
            ListingNotice.Approved => ("Tin của bạn đã được duyệt", "đã được cán bộ xác minh duyệt và đang hiển thị 90 ngày"),
            ListingNotice.Rejected => ("Tin của bạn chưa được duyệt", "chưa được duyệt"),
            ListingNotice.Hidden => ("Tin của bạn đang tạm ẩn", "đang được tạm ẩn"),
            ListingNotice.AppealAccepted => ("Kháng nghị được chấp nhận", "đã được hiển thị lại sau khi xem xét kháng nghị"),
            _ => ("Kết quả kháng nghị", "giữ nguyên quyết định sau khi xem xét kháng nghị"),
        };
        var because = reason is null ? "" : $"\nLý do / ghi chú của cán bộ: {reason}\n";
        var appeal = notice is ListingNotice.Rejected or ListingNotice.Hidden
            ? "\nBạn có thể sửa tin hoặc gửi kháng nghị trong 7 ngày tại trang quản lý tin.\n"
            : "";
        return new(to, $"{subject} – Trọ & Việc Sạch",
            $"""
            Chào {fullName},

            Tin "{title}" {what}.
            {because}{appeal}
            Xem tin: {link}

            Trọ & Việc Sạch
            """);
    }
}

public static class RentalEmailTemplates
{
    /// <summary>BR-27: sent to every invited email; the text does not say whether an account exists.</summary>
    public static EmailMessage LeaseInvite(string to, string landlordName, string place, string link, DateTime expiresAtUtc) => new(
        to,
        "Lời mời xác nhận hợp đồng thuê – Trọ & Việc Sạch",
        $"""
        Chào bạn,

        {landlordName} mời bạn xác nhận hợp đồng thuê {place}.

        Mở liên kết dưới đây, đăng nhập hoặc đăng ký bằng đúng địa chỉ email này, rồi đọc kỹ hợp đồng trước khi xác nhận:
        {link}

        Lời mời hết hạn lúc {Vietnam(expiresAtUtc)}. Nếu bạn không biết về hợp đồng này, hãy bỏ qua email.
        Trọ & Việc Sạch không nhận hay giữ tiền cọc, tiền thuê của bạn.

        Trọ & Việc Sạch
        """);

    public static EmailMessage LeaseCode(string to, string fullName, string code) => new(
        to,
        "Mã xác nhận hợp đồng thuê – Trọ & Việc Sạch",
        $"""
        Chào {fullName},

        Mã xác nhận hợp đồng của bạn là: {code}

        Mã có hiệu lực 10 phút. Chỉ nhập mã khi bạn đã đọc và đồng ý toàn bộ nội dung hợp đồng.
        Không đưa mã này cho người khác, kể cả chủ trọ.

        Trọ & Việc Sạch
        """);

    public static EmailMessage LeaseActivated(string to, string fullName, string place, string sha256Hex, string link) => new(
        to,
        "Hợp đồng thuê đã có hiệu lực – Trọ & Việc Sạch",
        $"""
        Chào {fullName},

        Mọi bên đã xác nhận hợp đồng thuê {place}. Hợp đồng có hiệu lực.
        Mã băm nội dung (để đối chiếu bản in): {sha256Hex}

        Xem hợp đồng: {link}

        Trọ & Việc Sạch
        """);

    private static string Vietnam(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc),
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")).ToString("HH:mm dd/MM/yyyy");
}

public enum ListingNotice
{
    Approved,
    Rejected,
    Hidden,
    AppealAccepted,
    AppealRejected,
}
