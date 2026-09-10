namespace COMPEL.Services.Proxy;

/// <summary>
///     Scores abusive behaviour per source address and reports when a source's score is high enough to act upon.
///     Every datagram costs its source a little, every violation costs its weight on top, and a drain proportional to elapsed time removes score again, which reproduces the reference proxy's accumulate-and-decay model: a source within the expected packet rate never accumulates, while one above it does and recovers only once it stops.
///     No state is persisted and nothing is attributed to an account, because at this layer there is only an address and a datagram.
/// </summary>
internal sealed class ViolationScoreContainer(TimeProvider timeProvider)
{
    // "BAN_THRESHOLD": The Score Above Which A Source Is Acted Upon; Named For The Decision Rather Than Today's Action, Which Is A Drop
    internal const int ActionableThreshold = 4000;

    // "MAX_WARN_COUNT": Where This Implementation Saturates Every Path. The Reference Bounds Only The Above-Threshold Escalation With It And Lets Its Own Count Climb Unbounded; Saturating Instead Keeps The Arithmetic In Range And Bounds The Worst Case To Roughly Two And A Half Minutes Of Drain, At The Cost Of A Flood's Penalty No Longer Growing With Its Duration
    internal const int MaximumViolationScore = 20000;

    // "ESTIMATED_PACKETS_PER_SECOND": The Packet Rate A Client Is Expected To Stay Under, Which Is Both The Score Drained Per Second And The Arrival Rate At Which A Source Breaks Even
    internal const int EstimatedPacketsPerSecond = 140;

    // Every Datagram Costs This Much Before Any Violation Weight, Which Is What Makes The Drain Rate Meaningful: A Source Sending Faster Than "EstimatedPacketsPerSecond" Accumulates Without Violating Anything
    internal const int PacketScore = 1;

    // "WARN_TOO_SHORT"
    internal const int TooShortViolationWeight = 200;

    // "WARN_UNAUTHENTICATED"
    internal const int UnauthenticatedViolationWeight = 200;

    // "WARN_LIMIT"
    internal const int RateLimitViolationWeight = 100;

    // "WARN_DUPE"
    internal const int DuplicateViolationWeight = 30;

    // "WARN_BANNED": Charged For Every Datagram From A Source That Is Already Actioned, Which Is What Drives A Persistent Source Towards "MaximumViolationScore"
    internal const int ActionedViolationWeight = 10;

    // The Reference Drains Only Once At Least This Much Time Has Passed, So A Pass That Runs Early Returns Without Advancing Its Mark Rather Than Draining A Partial Amount And Discarding The Remainder
    private static readonly TimeSpan MinimumDrainInterval = TimeSpan.FromMilliseconds(900);

    private readonly ConcurrentDictionary<IPEndPoint, int> scores = new ();

    private long lastDrainTimestamp = timeProvider.GetTimestamp();

    /// <summary>
    ///     How many sources currently carry a score. Exposed for tests and diagnostics; a source drained to zero is no longer counted.
    /// </summary>
    internal int TrackedSourceCount => scores.Count;

    /// <summary>
    ///     Charges <paramref name="source"/> for the arrival of one datagram, whatever it contains, plus <see cref="ActionedViolationWeight"/> if that leaves it over <see cref="ActionableThreshold"/>.
    ///     Called exactly once per datagram, before any check, so that a flood carrying no detectable violation is still scored.
    /// </summary>
    internal void ChargeArrival(IPEndPoint source) => scores.AddOrUpdate(source, Arrived(0), static (_, score) => Arrived(score));

    /// <summary>
    ///     Charges <paramref name="weight"/> against <paramref name="source"/> for a specific violation, on top of the arrival already charged for the same datagram.
    ///     The outcome is read separately through <see cref="IsWithinAllowance"/>, because the score is recorded whether or not the source was already actionable.
    /// </summary>
    internal void ChargeViolation(IPEndPoint source, int weight)
        => scores.AddOrUpdate(source,

            // Both Factories Are Static And Take The Weight As State, So Charging Allocates No Closure On The Datagram Path
            static (_, violationWeight) => Math.Min(violationWeight, MaximumViolationScore),
            static (_, score, violationWeight) => Math.Min(score + violationWeight, MaximumViolationScore),
            weight);

    /// <summary>
    ///     Whether <paramref name="source"/> is still within <see cref="ActionableThreshold"/>. Records nothing and begins tracking nothing, so it is safe to call for every datagram.
    /// </summary>
    internal bool IsWithinAllowance(IPEndPoint source) => Score(source) <= ActionableThreshold;

    /// <summary>
    ///     The current score for <paramref name="source"/>, or zero if it carries none.
    /// </summary>
    internal int Score(IPEndPoint source) => scores.TryGetValue(source, out int score) ? score : 0;

    /// <summary>
    ///     Removes score from every tracked source in proportion to the time elapsed since the last drain, flooring at zero, and forgets any source that reaches it so an address which has stopped misbehaving is not tracked for the life of the process.
    ///     Only one call may be in progress at a time, which the proxy's single maintenance loop satisfies; the elapsed-time mark is unsynchronised, so concurrent calls would each apply a full drain and would corrupt it.
    /// </summary>
    internal void Drain()
    {
        long now = timeProvider.GetTimestamp();
        TimeSpan elapsed = timeProvider.GetElapsedTime(lastDrainTimestamp, now);

        if (elapsed <= MinimumDrainInterval)
            return;

        lastDrainTimestamp = now;

        // The Amount Stays Fractional And The Subtraction's Result Is Truncated, Which Is What The Reference Does: Its Score Is An Unsigned Integer Assigned From A Float Subtraction, So Each Source Rounds Down And Drains Up To One More Than The Exact Amount Rather Than Up To One Less
        // No Score Can Exceed The Maximum, So Clamping The Amount To It Keeps A Long Pause Between Passes From Overflowing The Conversion Below
        double drainAmount = Math.Min(elapsed.TotalSeconds * EstimatedPacketsPerSecond, MaximumViolationScore);

        foreach (KeyValuePair<IPEndPoint, int> entry in scores)
        {
            int remaining = entry.Value > drainAmount ? (int)(entry.Value - drainAmount) : 0;

            // Both Writes Are Conditional On The Score Not Having Changed Since It Was Read: If A Charge Landed During This Pass, The Source Simply Waits For The Next One, Which Loses A Drain Rather Than A Charge
            if (remaining is 0)
                scores.TryRemove(entry);

            else
                scores.TryUpdate(entry.Key, remaining, entry.Value);
        }
    }

    /// <summary>
    ///     A source's score after one more datagram arrives.
    /// </summary>
    private static int Arrived(int score)
    {
        int arrived = score + PacketScore;

        // A Source Already Over The Threshold Pays Extra For Every Further Datagram, Deliberately: The Reference Does This So That Enforcement Failing Elsewhere Still Leaves A Persistent Source Costed
        if (arrived > ActionableThreshold)
            arrived += ActionedViolationWeight;

        return Math.Min(arrived, MaximumViolationScore);
    }
}
