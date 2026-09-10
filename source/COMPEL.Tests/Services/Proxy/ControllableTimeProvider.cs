namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     A time provider whose clock only moves when a test moves it, so behaviour proportional to elapsed time can be asserted exactly rather than waited for.
/// </summary>
internal sealed class ControllableTimeProvider : TimeProvider
{
    private long timestamp;

    /// <summary>
    ///     Timestamps are counted in ticks, so an advance of a given interval moves the clock by exactly that interval.
    /// </summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => timestamp;

    internal void Advance(TimeSpan interval) => timestamp += interval.Ticks;
}
