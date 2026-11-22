namespace TroViec.Api.Tests;

/// <summary>A TimeProvider tests can move forward.</summary>
public sealed class MutableClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
