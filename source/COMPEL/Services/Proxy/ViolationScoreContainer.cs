namespace COMPEL.Services.Proxy;

/// <summary>
///     Scores abusive behaviour per source address and reports when a source's score is high enough to act upon.
///     Every datagram costs its source an arrival score, while specific violations accumulate a violation score.
///     To prevent a spoofed source from silencing a legitimate player, actioning a source (refusing all its traffic) depends on its sustained arrival rate alone.
///     Violation weights accumulate for logging, attack indicator scoring, and diagnostics without gating whether valid traffic from that source is relayed.
/// </summary>
internal sealed class ViolationScoreContainer(TimeProvider timeProvider)
{
    // "BAN_THRESHOLD": The Score Above Which A Source Is Acted Upon; Named For The Decision Rather Than Today's Action, Which Is A Drop
    internal const int ActionableThreshold = 4000;

    // "MAX_WARN_COUNT": Where This Implementation Saturates Accumulation. The Reference Applies This Bound Only To Its Above-Threshold Escalation And Lets Its Own Count Climb Unbounded; Saturating Keeps The Arithmetic In Range And Bounds The Worst Case To Roughly Two And A Half Minutes Of Drain At A Standstill
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

    // "WARN_CHALLENGE": A Challenge The Session Never Issued, Which The Reference Treats Separately From A Client That Has Not Been Challenged Yet
    internal const int ChallengeViolationWeight = 100;

    // The Reference Drains Only Once More Than This Much Time Has Passed, So A Pass That Runs Early Returns Without Advancing Its Mark Rather Than Draining A Partial Amount And Discarding The Remainder
    private static readonly TimeSpan MinimumDrainInterval = TimeSpan.FromMilliseconds(900);

    private readonly ConcurrentDictionary<IPEndPoint, SourceScore> scores = new ();

    private long lastDrainTimestamp = timeProvider.GetTimestamp();

    /// <summary>
    ///     How many sources currently carry a score. Exposed for tests and diagnostics; a source drained to zero is no longer counted.
    /// </summary>
    internal int TrackedSourceCount => scores.Count;

    /// <summary>
    ///     Charges <paramref name="source"/> for the arrival of one datagram, whatever it contains.
    ///     Called exactly once per datagram, before any check, so that a flood carrying no detectable violation is still scored.
    /// </summary>
    internal void ChargeArrival(IPEndPoint source)
        => scores.AddOrUpdate(
            source,
            static _ => new SourceScore(PacketScore, 0),
            static (_, existing) => new SourceScore(Math.Min(existing.ArrivalScore + PacketScore, MaximumViolationScore), existing.ViolationScore));

    /// <summary>
    ///     Charges <paramref name="weight"/> against <paramref name="source"/> for a specific violation, on top of the arrival already charged for the same datagram.
    ///     Violation weights accumulate for diagnostics and attack indicators, but do not gate whether valid traffic is relayed.
    /// </summary>
    internal void ChargeViolation(IPEndPoint source, int weight)
        => scores.AddOrUpdate(
            source,
            static (_, violationWeight) => new SourceScore(0, Math.Min(violationWeight, MaximumViolationScore)),
            static (_, existing, violationWeight) => new SourceScore(existing.ArrivalScore, Math.Min(existing.ViolationScore + violationWeight, MaximumViolationScore)),
            weight);

    /// <summary>
    ///     Whether <paramref name="source"/>'s sustained arrival rate is within <see cref="ActionableThreshold"/>.
    ///     Driven solely by arrival score so spoofed violation datagrams cannot silence a player.
    /// </summary>
    internal bool IsWithinAllowance(IPEndPoint source) => ScoreArrival(source) <= ActionableThreshold;

    /// <summary>
    ///     The current arrival score for <paramref name="source"/>, or zero if it carries none.
    /// </summary>
    internal int ScoreArrival(IPEndPoint source) => scores.TryGetValue(source, out SourceScore score) ? score.ArrivalScore : 0;

    /// <summary>
    ///     The current total score (arrival + violation) for <paramref name="source"/>, or zero if it carries none, clamped to <see cref="MaximumViolationScore"/>.
    /// </summary>
    internal int Score(IPEndPoint source) => scores.TryGetValue(source, out SourceScore score) ? Math.Min(score.ArrivalScore + score.ViolationScore, MaximumViolationScore) : 0;

    /// <summary>
    ///     Removes score from every tracked source in proportion to the time elapsed since the last drain, flooring at zero, and forgets any source that reaches it so an address which has stopped misbehaving is not tracked for the life of the process.
    /// </summary>
    internal void Drain()
    {
        long now = timeProvider.GetTimestamp();
        TimeSpan elapsed = timeProvider.GetElapsedTime(lastDrainTimestamp, now);

        if (elapsed <= MinimumDrainInterval)
            return;

        lastDrainTimestamp = now;

        double drainAmount = Math.Min(elapsed.TotalSeconds * EstimatedPacketsPerSecond, MaximumViolationScore);

        foreach (KeyValuePair<IPEndPoint, SourceScore> entry in scores)
        {
            int remainingArrival = entry.Value.ArrivalScore > drainAmount ? (int)(entry.Value.ArrivalScore - drainAmount) : 0;
            int remainingViolation = entry.Value.ViolationScore > drainAmount ? (int)(entry.Value.ViolationScore - drainAmount) : 0;

            if (remainingArrival is 0 && remainingViolation is 0)
                scores.TryRemove(entry);
            else
                scores.TryUpdate(entry.Key, new SourceScore(remainingArrival, remainingViolation), entry.Value);
        }
    }

    private readonly struct SourceScore(int arrivalScore, int violationScore)
    {
        public int ArrivalScore { get; } = arrivalScore;
        public int ViolationScore { get; } = violationScore;
    }
}
