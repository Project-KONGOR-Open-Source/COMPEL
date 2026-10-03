namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     A time provider whose clock only moves when a test moves it, so behaviour proportional to elapsed time can be asserted exactly rather than waited for.
/// </summary>
internal sealed class ControllableTimeProvider : TimeProvider
{
    // An Arbitrary Fixed Point The Wall Clock Is Measured From, Late Enough That A Unix Timestamp Taken From It Is Plausible To Anything Reading One
    private static readonly DateTimeOffset Epoch = new (2026, 01, 01, 00, 00, 00, TimeSpan.Zero);

    private long timestamp;

    /// <summary>
    ///     Timestamps are counted in ticks, so an advance of a given interval moves the clock by exactly that interval.
    /// </summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => timestamp;

    /// <summary>
    ///     The wall clock advances with the monotonic one, from a fixed epoch, so a test that moves the clock cannot leave code reading the wall clock out of step with code measuring elapsed time.
    /// </summary>
    public override DateTimeOffset GetUtcNow() => Epoch.AddTicks(timestamp);

    internal void Advance(TimeSpan interval) => timestamp += interval.Ticks;
}
