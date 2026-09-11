namespace COMPEL.Services.Proxy;

/// <summary>
///     The challenges one client session may currently use: the several most recently issued to it, and the window a client uses before it has accepted any.
///     Several are retained because a challenge is renewed while the client is sending, so datagrams already in flight carry an earlier challenge and a counter that has not restarted, and because a client whose renewals are lost keeps using the last one it accepted.
///     Matching only the newest challenge would drop those datagrams on every renewal; matching only two would strand a client that lost a pair of consecutive renewals.
/// </summary>
internal sealed class SessionChallengeState
{
    // Zero Marks A Client That Has Not Accepted A Challenge Yet. "SendChallenge" Never Issues It, So It Can Never Collide With A Real Challenge
    internal const uint UnauthenticatedChallenge = 0;

    // "KEEP_CHALLENGES": How Many Issued Challenges Stay Valid. At The Renewal Interval This Is A Minute Of History, Against The Reference's Effective Thirty-Six Seconds (Six Challenges At Its Own Effective Six-Second Refresh, Not Its Nominal Five)
    internal const int RetainedChallengeCount = 6;

    // "CLEAR_UNAUTHENTICATED": Renewals Between Resets Of The Pre-Authentication Window, So Its Small Total Is A Recurring Allowance Rather Than A Once-Per-Session Budget
    // The Reference Counts Its Own Five-Second Challenge Refreshes And Resets On Every Fourth, So Roughly Every Twenty-Four Seconds; Three Ten-Second Renewals Is Thirty, Deliberately A Little Stricter On A Path That Relays What It Admits
    // The Quantity That Matters Is The Rate The Allowance Is Handed Out At, Not The Number Of Rotations, So This Is Not An Off-By-One Against The Reference's Count Of Refreshes
    internal const int UnauthenticatedResetRotations = 3;

    private readonly Lock stateLock = new ();

    // Newest First, Bounded By "RetainedChallengeCount". A List Rather Than A Queue Because It Is Searched On The Datagram Path And Six Elements Search Faster Than They Hash
    private readonly List<ChallengeWindow> retained = new (RetainedChallengeCount);

    private ChallengeWindow unauthenticated = NewUnauthenticatedWindow();

    private int rotationsSinceUnauthenticatedReset;

    /// <summary>
    ///     Records a newly issued challenge, retaining the most recent <see cref="RetainedChallengeCount"/> of them and discarding the oldest.
    ///     The caller must not issue a challenge equal to one already retained: a repeat would build a fresh window for that value and silently discard the counters the client has already consumed under it.
    ///     A monotonic issuer satisfies this on its own, but a random one would not, so the reference checks a new challenge against its whole retained history before accepting it.
    /// </summary>
    internal void Rotate(uint challenge, ushort quota)
    {
        lock (stateLock)
        {
            retained.Insert(0, new ChallengeWindow(challenge, quota));

            if (retained.Count > RetainedChallengeCount)
                retained.RemoveAt(retained.Count - 1);

            if (++rotationsSinceUnauthenticatedReset < UnauthenticatedResetRotations)
                return;

            rotationsSinceUnauthenticatedReset = 0;
            unauthenticated = NewUnauthenticatedWindow();
        }
    }

    /// <summary>
    ///     The most recently issued challenge, or <see langword="null"/> when none has been issued to this session yet.
    /// </summary>
    internal ChallengeWindow? Current
    {
        get
        {
            lock (stateLock)
                return retained.Count is 0 ? null : retained[0];
        }
    }

    /// <summary>
    ///     The window for the challenge the client echoed, or <see langword="null"/> when it echoed a non-zero challenge this session never issued or no longer retains.
    ///     <see cref="UnauthenticatedChallenge"/> always matches, because a client that has not been challenged yet has nothing else to echo.
    /// </summary>
    internal ChallengeWindow? Match(uint challenge)
    {
        lock (stateLock)
        {
            if (challenge is UnauthenticatedChallenge)
                return unauthenticated;

            foreach (ChallengeWindow window in retained)
                if (window.Challenge == challenge)
                    return window;

            return null;
        }
    }

    private static ChallengeWindow NewUnauthenticatedWindow() => new (UnauthenticatedChallenge, ChallengeQuota.UnauthenticatedPacketQuota);
}
