using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;

namespace TroViec.Api.Tests.Auth;

/// <summary>Drives the auth endpoints like a browser would and reads emails from Mailpit.</summary>
public sealed partial class AuthClient(HttpClient http, InfraFixture infra)
{
    public const string Password = "Correct-Horse-Battery-42";
    private static readonly HttpClient Mailpit = new();

    /// <summary>Each AuthClient acts from its own random IP so IP limits never collide between tests.</summary>
    public string Ip { get; } = $"10.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

    public static string NewStudentEmail() => $"sv-{Guid.NewGuid():N}@{ApiFactory.StudentDomain}";
    public static string NewOwnerEmail() => $"owner-{Guid.NewGuid():N}@gmail.test";

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null, string? cookie = null, bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Add(ApiFactory.TestIpHeader, Ip);
        if (csrf) request.Headers.Add("X-Requested-With", "troviec");
        if (cookie is not null) request.Headers.Add("Cookie", $"tv_refresh={cookie}");
        return http.SendAsync(request);
    }

    /// <summary>Authenticated call with a bearer token (JSON body optional).</summary>
    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string accessToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add(ApiFactory.TestIpHeader, Ip);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return http.SendAsync(request);
    }

    /// <summary>Registers, verifies and signs in; returns the access token and email.</summary>
    public async Task<(string Token, string Email)> SignedInAsync(string accountType = "student", string? email = null)
    {
        email = await RegisterVerifiedAsync(email, accountType);
        return ((await LoginAsync(email)).AccessToken, email);
    }

    public Task<HttpResponseMessage> RegisterAsync(string email, string accountType = "student", string password = Password) =>
        PostAsync("/api/v1/auth/register", new { email, password, fullName = "Nguyễn Văn A", accountType });

    /// <summary>Registers, reads the code from Mailpit and verifies the email.</summary>
    public async Task<string> RegisterVerifiedAsync(string? email = null, string accountType = "student")
    {
        email ??= accountType == "student" ? NewStudentEmail() : NewOwnerEmail();
        (await RegisterAsync(email, accountType)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var code = await LatestCodeAsync(email);
        (await PostAsync("/api/v1/auth/email/verify", new { email, code })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return email;
    }

    public async Task<(string AccessToken, string RefreshCookie, JsonElement Body)> LoginAsync(string email, string password = Password)
    {
        var response = await PostAsync("/api/v1/auth/login", new { email, password });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        return (body.GetProperty("accessToken").GetString()!, RefreshCookieFrom(response)!, body);
    }

    public Task<HttpResponseMessage> WhoAmIAsync(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ApiFactory.WhoAmIPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return http.SendAsync(request);
    }

    public static string? RefreshCookieFrom(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Where(v => v.StartsWith("tv_refresh=", StringComparison.Ordinal))
                .Select(v => v["tv_refresh=".Length..v.IndexOf(';')])
                .FirstOrDefault(v => v.Length > 0)
            : null;

    public static string? SetCookieHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith("tv_refresh=", StringComparison.Ordinal))
            : null;

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("code").GetString();

    /// <summary>Text bodies of every email sent to <paramref name="email"/>, newest first.</summary>
    public async Task<IReadOnlyList<string>> EmailsToAsync(string email)
    {
        var search = await Mailpit.GetFromJsonAsync<JsonElement>(
            new Uri(infra.MailpitApi, $"api/v1/search?query={Uri.EscapeDataString($"to:\"{email}\"")}"));
        var bodies = new List<string>();
        foreach (var message in search.GetProperty("messages").EnumerateArray())
        {
            var full = await Mailpit.GetFromJsonAsync<JsonElement>(
                new Uri(infra.MailpitApi, $"api/v1/message/{message.GetProperty("ID").GetString()}"));
            bodies.Add(full.GetProperty("Text").GetString() ?? "");
        }
        return bodies;
    }

    /// <summary>The 6-digit code from the newest verification email (waits briefly for delivery).</summary>
    public async Task<string> LatestCodeAsync(string email) => SixDigits().Match(await LatestEmailAsync(email)).Value;

    public async Task<string> LatestEmailAsync(string email)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var emails = await EmailsToAsync(email);
            if (emails.Count > 0) return emails[0];
            await Task.Delay(100);
        }
        throw new InvalidOperationException("no email arrived");
    }

    public static string ResetTokenFrom(string emailBody) => ResetLink().Match(emailBody).Groups[1].Value;

    public static string Base64UrlDecode(string segment)
    {
        var s = segment.Replace('-', '+').Replace('_', '/');
        return Encoding.UTF8.GetString(Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')));
    }

    [GeneratedRegex(@"\b\d{6}\b")]
    private static partial Regex SixDigits();

    [GeneratedRegex(@"dat-lai-mat-khau\?token=([A-Za-z0-9_\-%]+)")]
    private static partial Regex ResetLink();
}
