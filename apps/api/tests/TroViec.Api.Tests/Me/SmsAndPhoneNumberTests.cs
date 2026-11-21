using System.Net;
using Microsoft.Extensions.Logging;
using Shouldly;
using TroViec.Api.Domain;
using TroViec.Api.Infrastructure.Notifications;
using TroViec.Api.Tests.Auth;

namespace TroViec.Api.Tests.Me;

public sealed class PhoneNumberTests
{
    [Theory]
    [InlineData("0912345678", "+84912345678")]
    [InlineData("0912 345 678", "+84912345678")]
    [InlineData("0912.345.678", "+84912345678")]
    [InlineData("+84 912-345-678", "+84912345678")]
    [InlineData("84912345678", "+84912345678")]
    [InlineData("0387654321", "+84387654321")]
    public void TryParse_VietnameseMobile_NormalisesToE164(string input, string expected)
    {
        PhoneNumber.TryParse(input, out var phone).ShouldBeTrue();
        phone!.E164.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0212345678")]
    [InlineData("091234567")]
    [InlineData("09123456789")]
    [InlineData("+14155550100")]
    [InlineData("09a2345678")]
    public void TryParse_NotAVietnameseMobile_ReturnsFalse(string input) =>
        PhoneNumber.TryParse(input, out _).ShouldBeFalse();
}

public sealed class ConsoleSmsSenderTests
{
    [Fact]
    public async Task SendOtp_LogsCodeAndUserIdButNeverThePhoneNumber()
    {
        var logger = new RecordingLogger();

        await new ConsoleSmsSender(logger).SendOtpAsync(42, "+84912345678", "123456", CancellationToken.None);

        var line = logger.Lines.ShouldHaveSingleItem();
        line.ShouldContain("123456");
        line.ShouldContain("42");
        line.ShouldNotContain("912345678");
    }

    private sealed class RecordingLogger : ILogger<ConsoleSmsSender>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + " " + string.Join(' ', (state as IEnumerable<KeyValuePair<string, object?>>)?.Select(kv => kv.Value) ?? []));
    }
}

[Collection(nameof(InfraCollection))]
public sealed class SmsProviderTests(InfraFixture infra)
{
    [Fact]
    public async Task Startup_ConsoleSmsInProduction_RefusesToStart()
    {
        await using var factory = new ApiFactory(infra, environment: "Production") { CaptureSms = false };

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("Sms__Provider=console");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Otp_NoSmsProvider_Returns503SmsUnavailable()
    {
        await using var factory = new ApiFactory(infra, overrides: new Dictionary<string, string?> { ["Sms:Provider"] = "none" })
        {
            CaptureSms = false,
        };
        var client = new AuthClient(factory.CreateClient(), infra);
        var (token, _) = await client.SignedInAsync();

        var response = await client.SendAsync(HttpMethod.Post, "/api/v1/auth/phone/otp", token, new { phone = "0912345678" });

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await AuthClient.ProblemCodeAsync(response)).ShouldBe("phone.sms_unavailable");
    }
}
