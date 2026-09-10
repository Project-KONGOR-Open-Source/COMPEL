namespace COMPEL.Services.Proxy;

/// <summary>
///     The challenges one client session may currently use: the challenge most recently issued to it, and the one that replaced.
///     Both are retained because a challenge is renewed while the client is sending, so datagrams already in flight carry the previous challenge and a counter that has not restarted.
///     Matching only the newest challenge would drop those datagrams on every renewal.
/// </summary>
internal sealed class SessionChallengeState
{
    private readonly Lock stateLock = new ();

    private ChallengeWindow? current;
    private ChallengeWindow? previous;

    /// <summary>
    ///     Records a newly issued challenge, retaining the one it replaces.
    /// </summary>
    internal void Rotate(uint challenge, ushort quota)
    {
        lock (stateLock)
        {
            previous = current;
            current = new ChallengeWindow(challenge, quota);
        }
    }

    /// <summary>
    ///     The window for the challenge the client echoed, or <see langword="null"/> when it echoed neither of the two the session holds.
    /// </summary>
    internal ChallengeWindow? Match(uint challenge)
    {
        lock (stateLock)
        {
            if (current is not null && current.Challenge == challenge)
                return current;

            return previous is not null && previous.Challenge == challenge ? previous : null;
        }
    }
}
