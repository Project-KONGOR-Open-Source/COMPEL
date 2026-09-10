namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies that a client is still admitted immediately after its challenge is renewed, using the challenge it echoed rather than only the newest one.
/// </summary>
public sealed class SessionChallengeStateTests
{
    // Without This, Every Renewal Would Drop The Datagrams Already In Flight: A False Positive For Every Player On Every Rotation
    [Test]
    public async Task A_Datagram_Echoing_The_Previous_Challenge_Is_Still_Matched()
    {
        SessionChallengeState state = new ();

        state.Rotate(challenge: 100, quota: 64);
        state.Rotate(challenge: 200, quota: 64);

        using (Assert.Multiple())
        {
            await Assert.That(state.Match(200)).IsNotNull();
            await Assert.That(state.Match(100)).IsNotNull();
        }
    }

    // The Previous Window Keeps Its Own Consumed Counters, So A Client Mid-Rotation Is Judged Against The Window It Was Actually Using
    [Test]
    public async Task The_Previous_Window_Retains_Its_Own_Consumed_Counters()
    {
        SessionChallengeState state = new ();

        state.Rotate(challenge: 100, quota: 64);

        ChallengeWindow? first = state.Match(100);
        first?.TryAdmit(7, out _);

        state.Rotate(challenge: 200, quota: 64);

        using (Assert.Multiple())
        {
            await Assert.That(state.Match(100)?.TryAdmit(7, out _)).IsFalse();
            await Assert.That(state.Match(200)?.TryAdmit(7, out _)).IsTrue();
        }
    }

    [Test]
    public async Task A_Challenge_Older_Than_The_Previous_One_Is_Not_Matched()
    {
        SessionChallengeState state = new ();

        state.Rotate(challenge: 100, quota: 64);
        state.Rotate(challenge: 200, quota: 64);
        state.Rotate(challenge: 300, quota: 64);

        await Assert.That(state.Match(100)).IsNull();
    }

    [Test]
    public async Task An_Unknown_Challenge_Is_Not_Matched()
    {
        SessionChallengeState state = new ();

        state.Rotate(challenge: 100, quota: 64);

        using (Assert.Multiple())
        {
            await Assert.That(state.Match(999)).IsNull();
            await Assert.That(state.Match(0)).IsNull();
        }
    }

    [Test]
    public async Task No_Challenge_Is_Matched_Before_The_First_Rotation()
    {
        SessionChallengeState state = new ();

        await Assert.That(state.Match(100)).IsNull();
    }
}
