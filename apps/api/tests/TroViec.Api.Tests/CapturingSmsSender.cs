using System.Collections.Concurrent;
using TroViec.Api.Infrastructure.Notifications;

namespace TroViec.Api.Tests;

/// <summary>Test double: remembers the last OTP sent to each number.</summary>
public sealed class CapturingSmsSender : ISmsSender
{
    private readonly ConcurrentDictionary<string, string> _lastCode = new();
    private int _sent;

    public bool CanSend => true;

    public int SentCount => _sent;

    public Task SendOtpAsync(long userId, string phoneE164, string code, CancellationToken ct)
    {
        _lastCode[phoneE164] = code;
        Interlocked.Increment(ref _sent);
        return Task.CompletedTask;
    }

    public string LastCodeFor(string phoneE164) => _lastCode[phoneE164];
}
