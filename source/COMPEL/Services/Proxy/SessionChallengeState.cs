namespace COMPEL.Services.Proxy;

/// <summary>
///     The challenges one forwarder currently retains: the several most recently issued across all sessions on this forwarder, and the window a client uses before it has accepted any.
///     Retained challenges are held per forwarder rather than per session because the client keys challenges by destination IP address and port, so multiple sessions sharing a forwarder all echo the latest challenge issued on that forwarder.
/// </summary>
internal sealed class SessionChallengeState
{
    // Zero Marks A Client That Has Not Accepted A Challenge Yet. "SendChallenge" Never Issues It, So It Can Never Collide With A Real Challenge
    internal const uint UnauthenticatedChallenge = 0;

    // "KEEP_CHALLENGES": How Many Issued Challenges Stay Valid. At The Renewal Interval This Is A Minute Of History, Against The Reference's Effective Thirty-Six Seconds
    internal const int RetainedChallengeCount = 6;

    // "CLEAR_UNAUTHENTICATED": Renewals Between Resets Of The Pre-Authentication Window, So Its Small Total Is A Recurring Allowance Rather Than A Once-Per-Session Budget
    internal const int UnauthenticatedResetRotations = 3;

    private static readonly IPEndPoint DefaultTestEndPoint = new (IPAddress.Loopback, 0);

    private readonly Lock stateLock = new ();

    // Newest First, Bounded By "RetainedChallengeCount"
    private readonly List<RetainedChallenge> retained = new (RetainedChallengeCount);

    private RetainedChallenge unauthenticated = new (UnauthenticatedChallenge, ChallengeQuota.UnauthenticatedPacketQuota);

    private int rotationsSinceUnauthenticatedReset;

    /// <summary>
    ///     The most recently issued challenge, or <see langword="null"/> when none has been issued on this forwarder yet.
    /// </summary>
    internal uint? CurrentChallenge
    {
        get
        {
            lock (stateLock)
            {
                return retained.Count is 0 ? null : retained[0].Challenge;
            }
        }
    }

    /// <summary>
    ///     Records a newly issued challenge, retaining the most recent <see cref="RetainedChallengeCount"/> of them and discarding the oldest.
    /// </summary>
    internal void Rotate(uint challenge, ushort quota)
    {
        lock (stateLock)
        {
            retained.Insert(0, new RetainedChallenge(challenge, quota));

            if (retained.Count > RetainedChallengeCount)
                retained.RemoveAt(retained.Count - 1);

            if (++rotationsSinceUnauthenticatedReset < UnauthenticatedResetRotations)
                return;

            rotationsSinceUnauthenticatedReset = 0;
            unauthenticated = new RetainedChallenge(UnauthenticatedChallenge, ChallengeQuota.UnauthenticatedPacketQuota);
        }
    }

    /// <summary>
    ///     The window for the challenge the client echoed, or <see langword="null"/> when it echoed a non-zero challenge this forwarder never issued or no longer retains.
    /// </summary>
    internal ChallengeWindow? Match(uint challenge, IPEndPoint? endpoint = null)
    {
        IPEndPoint key = endpoint ?? DefaultTestEndPoint;

        lock (stateLock)
        {
            if (challenge is UnauthenticatedChallenge)
                return unauthenticated.GetWindow(key);

            foreach (RetainedChallenge item in retained)
            {
                if (item.Challenge == challenge)
                    return item.GetWindow(key);
            }

            return null;
        }
    }

    private sealed class RetainedChallenge
    {
        private readonly Lock windowLock = new ();
        private readonly Dictionary<IPEndPoint, ChallengeWindow> windows = new ();

        internal RetainedChallenge(uint challenge, ushort quota)
        {
            Challenge = challenge;
            Quota = quota;
        }

        internal uint Challenge { get; }

        internal ushort Quota { get; }

        internal ChallengeWindow GetWindow(IPEndPoint endpoint)
        {
            lock (windowLock)
            {
                if (windows.TryGetValue(endpoint, out ChallengeWindow? existing) is false)
                {
                    existing = new ChallengeWindow(Challenge, Quota);
                    windows[endpoint] = existing;
                }

                return existing;
            }
        }
    }
}
