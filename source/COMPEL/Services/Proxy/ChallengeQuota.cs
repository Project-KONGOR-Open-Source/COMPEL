namespace COMPEL.Services.Proxy;

/// <summary>
///     The per-challenge packet quotas the proxy advertises to a client and enforces against it.
///     Only the rates are stated; the quota itself is derived from the rate and the challenge renewal interval, so the advertised and enforced values cannot drift apart when the interval changes.
/// </summary>
internal static class ChallengeQuota
{
    // "CHALLENGE_MAX_CTR" (720) Over The Reference's Nominal Five-Second "CHALLENGE_REFRESH_TIME". The Reference's Own Counter Only Refreshes Every Sixth Pass, So Its Effective Rate Is 120 A Second Rather Than 144
    // A Compliant Client Self-Limits Below This Rate Rather Than At It, And That Self-Limited Rate Must Still Stay Under The Drain: COMPEL's Margin There Is Narrower Than The Reference's Because This Ceiling Sits Above The Reference's Own Effective Rate
    internal const int GamePacketsPerSecond = 144;

    // "CHALLENGE_MAX_GAME_CMD_CTR" (40) Over The Reference's Nominal Five-Second Refresh; Effectively About 6.7 A Second Rather Than 8, For The Same Reason As "GamePacketsPerSecond"
    internal const int GameCommandPacketsPerSecond = 8;

    // "CHALLENGE_MAX_CTR_VOICE" (50) Over The Reference's Nominal Five-Second Refresh; Effectively About 8.3 A Second Rather Than 10, For The Same Reason As "GamePacketsPerSecond"
    internal const int VoicePacketsPerSecond = 10;

    // "MAX_CTR_UNAUTHENTICATED": A Total For The Whole Unauthenticated State Rather Than A Rate, So It Is Not Derived
    internal const ushort UnauthenticatedPacketQuota = 100;

    /// <summary>
    ///     The number of packets a client may send within one challenge at the supplied rate, clamped to the unsigned sixteen-bit field the quota is advertised in.
    /// </summary>
    internal static ushort Derive(int ratePerSecond, TimeSpan renewalInterval)
    {
        double quota = ratePerSecond * renewalInterval.TotalSeconds;

        return quota >= ushort.MaxValue ? ushort.MaxValue : (ushort)Math.Ceiling(quota);
    }

    /// <summary>
    ///     The total packet quota for the supplied forwarder kind.
    /// </summary>
    internal static ushort ForKind(ProxyForwarderKind kind, TimeSpan renewalInterval) => kind switch
    {
        ProxyForwarderKind.Voice => Derive(VoicePacketsPerSecond, renewalInterval),
        ProxyForwarderKind.Game  => Derive(GamePacketsPerSecond, renewalInterval),
        _                        => throw new ArgumentOutOfRangeException(nameof(kind), @$"Unsupported Forwarder Kind ""{kind}""")
    };

    /// <summary>
    ///     The game command packet quota, which the reference advertises separately from the total.
    /// </summary>
    internal static ushort GameCommandForInterval(TimeSpan renewalInterval) => Derive(GameCommandPacketsPerSecond, renewalInterval);
}
