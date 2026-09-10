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

    // Six Retained Challenges Is Sixty Seconds Of History At The Renewal Interval, So Losing Several Consecutive Renewals Cannot Strand A Client Whose Counter Has Legitimately Run Past The Unauthenticated Total
    [Test]
    public async Task Every_Retained_Challenge_Is_Still_Matched()
    {
        SessionChallengeState state = new ();

        for (uint challenge = 1; challenge <= SessionChallengeState.RetainedChallengeCount; challenge++)
            state.Rotate(challenge, quota: 64);

        using (Assert.Multiple())
        {
            for (uint challenge = 1; challenge <= SessionChallengeState.RetainedChallengeCount; challenge++)
                await Assert.That(state.Match(challenge)).IsNotNull();
        }
    }

    [Test]
    public async Task A_Challenge_Older_Than_The_Retained_History_Is_Not_Matched()
    {
        SessionChallengeState state = new ();

        for (uint challenge = 1; challenge <= SessionChallengeState.RetainedChallengeCount + 1; challenge++)
            state.Rotate(challenge, quota: 64);

        using (Assert.Multiple())
        {
            await Assert.That(state.Match(1)).IsNull();
            await Assert.That(state.Match(2)).IsNotNull();
        }
    }

    // The Reference Runs Its Duplicate Check Over Challenge Zero Exactly As Over An Issued Challenge, So It Must Be A Window Rather Than A Bare Ceiling
    [Test]
    public async Task The_Unauthenticated_Challenge_Is_Always_Matched_And_Detects_A_Duplicate()
    {
        SessionChallengeState state = new ();

        ChallengeWindow? before = state.Match(SessionChallengeState.UnauthenticatedChallenge);

        state.Rotate(challenge: 100, quota: 64);

        ChallengeWindow? after = state.Match(SessionChallengeState.UnauthenticatedChallenge);

        using (Assert.Multiple())
        {
            // It Is Available Before Any Challenge Has Been Issued, Because That Is Exactly When A Client Uses It
            await Assert.That(before).IsNotNull();
            await Assert.That(after).IsNotNull();

            await Assert.That(after?.TryAdmit(7, out _)).IsTrue();
            await Assert.That(after?.TryAdmit(7, out _)).IsFalse();
        }
    }

    [Test]
    public async Task The_Unauthenticated_Window_Is_Reset_Periodically()
    {
        SessionChallengeState state = new ();

        state.Match(SessionChallengeState.UnauthenticatedChallenge)?.TryAdmit(7, out _);

        for (uint rotation = 1; rotation <= SessionChallengeState.UnauthenticatedResetRotations; rotation++)
            state.Rotate(rotation, quota: 64);

        // Without The Reset Its Small Total Would Be A Once-Per-Session Budget, So A Long-Lived Session Could Never Use It Again
        await Assert.That(state.Match(SessionChallengeState.UnauthenticatedChallenge)?.TryAdmit(7, out _)).IsTrue();
    }

    [Test]
    public async Task The_Unauthenticated_Window_Survives_A_Rotation_That_Does_Not_Reset_It()
    {
        SessionChallengeState state = new ();

        state.Match(SessionChallengeState.UnauthenticatedChallenge)?.TryAdmit(7, out _);

        state.Rotate(challenge: 100, quota: 64);

        await Assert.That(state.Match(SessionChallengeState.UnauthenticatedChallenge)?.TryAdmit(7, out _)).IsFalse();
    }

    [Test]
    public async Task An_Unknown_Challenge_Is_Not_Matched()
    {
        SessionChallengeState state = new ();

        state.Rotate(challenge: 100, quota: 64);

        await Assert.That(state.Match(999)).IsNull();
    }

    [Test]
    public async Task No_Issued_Challenge_Is_Matched_Before_The_First_Rotation()
    {
        SessionChallengeState state = new ();

        await Assert.That(state.Match(100)).IsNull();
    }
}
