namespace COMPEL.Services.Proxy;

/// <summary>
///     Tracks global proxy attack pressure using a decaying weighted counter.
///     The reference proxy resets its "under_attack_indicator" to zero every five minutes (main.cpp:1248-1253).
///     COMPEL uses an elapsed-proportional decaying counter so that the under-attack indicator rises immediately when a burst occurs and clears within seconds after the burst ends, avoiding up to five minutes of latency in both directions.
/// </summary>
internal sealed class AttackIndicatorContainer(TimeProvider timeProvider)
{
    // "UNDER_ATTACK_THRESHOLD": The Number Of Weighted Attack Events Above Which The Proxy Considers Itself Under Attack
    internal const int UnderAttackThreshold = 1000;

    // Relative Weights Ported From Reference main.cpp (main.cpp:1714, :1719, :584)
    internal const int NovelEndpointAttackWeight = 1;
    internal const int CapRefusalAttackWeight = 10;
    internal const int ValidationDropAttackWeight = 100;

    // Maximum Score Saturates Accumulation To Keep Arithmetic Bounded
    internal const int MaximumAttackScore = 20000;

    // Rate At Which Score Drains Per Second After A Burst Stops
    internal const int DrainScorePerSecond = 200;

    private static readonly TimeSpan MinimumDrainInterval = TimeSpan.FromMilliseconds(900);

    private int score;
    private long lastDrainTimestamp = timeProvider.GetTimestamp();

    public int Score => Volatile.Read(ref score);

    public bool IsUnderAttack => Score > UnderAttackThreshold;

    public void Charge(int weight)
    {
        int current;
        int updated;

        do
        {
            current = Volatile.Read(ref score);
            updated = Math.Min(current + weight, MaximumAttackScore);
        }
        while (Interlocked.CompareExchange(ref score, updated, current) != current);
    }

    public void Drain()
    {
        long now = timeProvider.GetTimestamp();
        TimeSpan elapsed = timeProvider.GetElapsedTime(lastDrainTimestamp, now);

        if (elapsed <= MinimumDrainInterval)
            return;

        lastDrainTimestamp = now;

        double drainAmount = Math.Min(elapsed.TotalSeconds * DrainScorePerSecond, MaximumAttackScore);

        int current;
        int updated;

        do
        {
            current = Volatile.Read(ref score);
            updated = current > drainAmount ? (int)(current - drainAmount) : 0;
        }
        while (Interlocked.CompareExchange(ref score, updated, current) != current);
    }
}
