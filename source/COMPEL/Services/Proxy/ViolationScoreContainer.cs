namespace COMPEL.Services.Proxy;

/// <summary>
///     Scores abusive behaviour per source address and reports when a source has exhausted its allowance.
///     Violations consume a source's allowance and the allowance refills when <see cref="Replenish"/> is called, which reproduces the reference proxy's accumulate-and-decay model: a source behaving normally never runs out, while one misbehaving does and recovers only once it stops.
///     No state is persisted and nothing is attributed to an account, because at this layer there is only an address and a datagram.
/// </summary>
internal sealed class ViolationScoreContainer : IDisposable
{
    // "BAN_THRESHOLD": The Score At Which A Source Is Acted Upon; Named For The Decision Rather Than Today's Action, Which Is A Drop
    internal const int ActionableThreshold = 4000;

    // "ESTIMATED_PACKETS_PER_SECOND": How Much Of A Source's Allowance Is Restored By Each Replenishment
    internal const int EstimatedPacketsPerSecond = 140;

    // "WARN_TOO_SHORT"
    internal const int TooShortViolationWeight = 200;

    // "WARN_UNAUTHENTICATED"
    internal const int UnauthenticatedViolationWeight = 200;

    // "WARN_LIMIT"
    internal const int RateLimitViolationWeight = 100;

    // "WARN_DUPE"
    internal const int DuplicateViolationWeight = 30;

    // One Limiter Per Source, Rather Than A "PartitionedRateLimiter", Because The Partitioned Wrapper Offers No Way To Drive Replenishment And Its Factory Has No Time Provider Overload
    private readonly ConcurrentDictionary<IPEndPoint, TokenBucketRateLimiter> limiters = new ();

    /// <summary>
    ///     Charges <paramref name="weight"/> against <paramref name="source"/>. The outcome is read separately through <see cref="IsWithinAllowance"/>, because a charge is recorded whether or not the source still has room.
    /// </summary>
    internal void Charge(IPEndPoint source, int weight)
    {
        using RateLimitLease lease = For(source).AttemptAcquire(weight);
    }

    /// <summary>
    ///     Whether <paramref name="source"/> is still within <see cref="ActionableThreshold"/>. Consumes nothing, so it is safe to call for every datagram.
    /// </summary>
    internal bool IsWithinAllowance(IPEndPoint source) => For(source).GetStatistics()?.CurrentAvailablePermits > 0;

    /// <summary>
    ///     Restores <see cref="EstimatedPacketsPerSecond"/> of allowance to every tracked source, and forgets any source whose allowance is fully restored so an idle address is not tracked indefinitely.
    ///     Called once per second by the proxy's maintenance loop, matching the reference proxy's housekeeping pass.
    /// </summary>
    internal void Replenish()
    {
        foreach (KeyValuePair<IPEndPoint, TokenBucketRateLimiter> entry in limiters)
        {
            long availablePermits = entry.Value.GetStatistics()?.CurrentAvailablePermits ?? ActionableThreshold;
            long restoredPermits = Math.Min(availablePermits + EstimatedPacketsPerSecond, ActionableThreshold);

            // A Source Whose Allowance Is Fully Restored Is Forgotten, So An Address That Has Stopped Misbehaving Is Not Tracked For The Life Of The Process
            if (restoredPermits >= ActionableThreshold)
            {
                limiters.TryRemove(entry.Key, out _);

                continue;
            }

            // "TokenBucketRateLimiter.TryReplenish" Scales The Amount It Restores By The Real Time Elapsed Since It Was Last Called, So It Cannot Be Used To Restore A Fixed Amount Each Time This Method Runs
            // A Freshly-Constructed Limiter Is Brought Down To The Target Balance Instead, Which Restores Exactly "EstimatedPacketsPerSecond" Regardless Of How Promptly The Maintenance Loop Runs
            // The Limiter Being Replaced Is Deliberately Not Disposed Here: A Datagram Thread May Already Hold The Same Reference, And Disposing It Underneath That Thread Would Throw On The Hot Path
            // Dropping The Reference Leaks Nothing, Because Automatic Replenishment Is Off And The Limiter Owns No Timer
            limiters[entry.Key] = Restored((int)restoredPermits);
        }
    }

    private TokenBucketRateLimiter For(IPEndPoint source) => limiters.GetOrAdd(source, _ => NewLimiter());

    private static TokenBucketRateLimiter NewLimiter() => new (new TokenBucketRateLimiterOptions
    {
        TokenLimit = ActionableThreshold,
        TokensPerPeriod = EstimatedPacketsPerSecond,

        // Replenishment Is Driven By The Maintenance Loop So The Drain Is Deterministic And Testable Without Waiting On Wall-Clock Time
        AutoReplenishment = false,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),

        // The Datagram Path Must Never Wait, So Nothing Is Queued
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
    });

    /// <summary>
    ///     A freshly-constructed limiter brought down to <paramref name="availablePermits"/> of remaining allowance.
    ///     A limiter always starts full and offers no way to set its starting balance directly, so the balance is reached by charging off the difference immediately after construction.
    /// </summary>
    private static TokenBucketRateLimiter Restored(int availablePermits)
    {
        TokenBucketRateLimiter limiter = NewLimiter();

        using RateLimitLease lease = limiter.AttemptAcquire(ActionableThreshold - availablePermits);

        return limiter;
    }

    public void Dispose()
    {
        foreach (TokenBucketRateLimiter limiter in limiters.Values)
            limiter.Dispose();

        limiters.Clear();
    }
}
