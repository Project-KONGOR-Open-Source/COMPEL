namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies the datagram validation pipeline: length enforcement, challenge matching, counter admission, and grace period handling.
/// </summary>
public sealed class DatagramValidatorTests
{
    private static readonly IPEndPoint ClientEndPoint = new (IPAddress.Parse("127.0.0.1"), 54321);

    [Test]
    public async Task Datagram_Shorter_Than_Minimum_Length_Is_Dropped_As_Too_Short()
    {
        byte[] shortDatagram = new byte[10];

        SessionChallengeState challenges = new ();

        DatagramValidationResult outcome = DatagramValidator.Validate
        (
            shortDatagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: false
        );

        using (Assert.Multiple())
        {
            await Assert.That(outcome.IsAdmitted).IsFalse();
            await Assert.That(outcome.DropReason).IsEqualTo("Too Short");

            int weight = outcome.ViolationWeight;
            int expected = ViolationScoreContainer.TooShortViolationWeight;

            await Assert.That(weight).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Datagram_With_Unknown_Challenge_Within_Grace_Is_Dropped_With_Zero_Violation_Weight()
    {
        byte[] datagram = BuildDatagram(challenge: 9999, counter: 0);

        SessionChallengeState challenges = new ();

        DatagramValidationResult outcome = DatagramValidator.Validate
        (
            datagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: true
        );

        using (Assert.Multiple())
        {
            await Assert.That(outcome.IsAdmitted).IsFalse();
            await Assert.That(outcome.DropReason).IsEqualTo("Unknown Challenge Within Grace");

            int weight = outcome.ViolationWeight;

            await Assert.That(weight).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Datagram_With_Unknown_Challenge_Outside_Grace_Is_Dropped_With_Challenge_Violation_Weight()
    {
        byte[] datagram = BuildDatagram(challenge: 9999, counter: 0);

        SessionChallengeState challenges = new ();

        DatagramValidationResult outcome = DatagramValidator.Validate
        (
            datagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: false
        );

        using (Assert.Multiple())
        {
            await Assert.That(outcome.IsAdmitted).IsFalse();
            await Assert.That(outcome.DropReason).IsEqualTo("Unknown Challenge");

            int weight = outcome.ViolationWeight;
            int expected = ViolationScoreContainer.ChallengeViolationWeight;

            await Assert.That(weight).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Datagram_With_Unauthenticated_Challenge_Over_Quota_Is_Dropped_As_Unauthenticated()
    {
        byte[] datagram = BuildDatagram(challenge: SessionChallengeState.UnauthenticatedChallenge, counter: ChallengeQuota.UnauthenticatedPacketQuota);

        SessionChallengeState challenges = new ();

        DatagramValidationResult outcome = DatagramValidator.Validate
        (
            datagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: false
        );

        using (Assert.Multiple())
        {
            await Assert.That(outcome.IsAdmitted).IsFalse();
            await Assert.That(outcome.DropReason).IsEqualTo("Unauthenticated");

            int weight = outcome.ViolationWeight;
            int expected = ViolationScoreContainer.UnauthenticatedViolationWeight;

            await Assert.That(weight).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Datagram_With_Duplicate_Counter_Is_Dropped_As_Duplicate()
    {
        const uint challenge = 12345;

        SessionChallengeState challenges = new ();

        challenges.Rotate(challenge, quota: 100);

        byte[] datagram = BuildDatagram(challenge, counter: 5);

        DatagramValidationResult firstPass = DatagramValidator.Validate
        (
            datagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: false
        );

        DatagramValidationResult secondPass = DatagramValidator.Validate
        (
            datagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: false
        );

        using (Assert.Multiple())
        {
            await Assert.That(firstPass.IsAdmitted).IsTrue();

            await Assert.That(secondPass.IsAdmitted).IsFalse();
            await Assert.That(secondPass.DropReason).IsEqualTo("Duplicate");

            int weight = secondPass.ViolationWeight;
            int expected = ViolationScoreContainer.DuplicateViolationWeight;

            await Assert.That(weight).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Datagram_With_Authenticated_Challenge_Over_Quota_Is_Dropped_As_Over_Quota()
    {
        const uint challenge = 12345;

        SessionChallengeState challenges = new ();

        challenges.Rotate(challenge, quota: 10);

        byte[] datagram = BuildDatagram(challenge, counter: 10);

        DatagramValidationResult outcome = DatagramValidator.Validate
        (
            datagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: false
        );

        using (Assert.Multiple())
        {
            await Assert.That(outcome.IsAdmitted).IsFalse();
            await Assert.That(outcome.DropReason).IsEqualTo("Over Quota");

            int weight = outcome.ViolationWeight;
            int expected = ViolationScoreContainer.RateLimitViolationWeight;

            await Assert.That(weight).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Valid_Unauthenticated_Datagram_Is_Admitted_Without_Authenticating()
    {
        byte[] datagram = BuildDatagram(challenge: SessionChallengeState.UnauthenticatedChallenge, counter: 0);

        SessionChallengeState challenges = new ();

        DatagramValidationResult outcome = DatagramValidator.Validate
        (
            datagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: false
        );

        using (Assert.Multiple())
        {
            await Assert.That(outcome.IsAdmitted).IsTrue();
            await Assert.That(outcome.IsAuthenticatedChallenge).IsFalse();
        }
    }

    [Test]
    public async Task Valid_Authenticated_Datagram_Is_Admitted_And_Marks_Authenticated()
    {
        const uint challenge = 54321;

        SessionChallengeState challenges = new ();

        challenges.Rotate(challenge, quota: 100);

        byte[] datagram = BuildDatagram(challenge, counter: 0);

        DatagramValidationResult outcome = DatagramValidator.Validate
        (
            datagram,
            ProxyForwarderKind.Game,
            challenges,
            ClientEndPoint,
            isWithinUnknownChallengeGrace: false
        );

        using (Assert.Multiple())
        {
            await Assert.That(outcome.IsAdmitted).IsTrue();
            await Assert.That(outcome.IsAuthenticatedChallenge).IsTrue();
        }
    }

    private static byte[] BuildDatagram(uint challenge, ushort counter)
    {
        byte[] buffer = new byte[43];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(ClientPacketReader.ChallengeOffset), challenge);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(ClientPacketReader.CounterOffset), counter);

        return buffer;
    }
}
