namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Exercises the proxy forwarder over loopback: that a valid datagram is relayed to the server and back, that a datagram too short to validate is dropped instead, that a client is issued a challenge, that each renewal carries a strictly greater value, that a repeated counter under one challenge is dropped as a duplicate, that a counter at the quota is dropped, that a challenge the proxy never issued is dropped and charged, that an unauthenticated counter at the total is dropped, and that an actioned source is refused whatever it sends and before its datagram is otherwise read.
///     It also exercises the engine's reconnect query, which is relayed without a session and under its own bounds, and whose reply alone is relayed back.
///     This validates the challenge wire format (which cannot be tested against a live client here) end to end against the real forwarder.
///     The tests run serially and tolerate the occasional loopback datagram drop by retrying, so they do not flake under load.
/// </summary>
[NotInParallel]
public sealed class UDPForwarderTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(2);

    [Test]
    public async Task A_Datagram_Is_Relayed_To_The_Server_And_The_Reply_Is_Relayed_Back_To_The_Client()
    {
        int publicPort = FreeUDPPort();
        int localPort = FreeUDPPort();

        using Socket server = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        server.Bind(new IPEndPoint(IPAddress.Loopback, localPort));

        ViolationScoreContainer container = new (TimeProvider.System);
        AttackIndicatorContainer attackIndicator = new (TimeProvider.System);

        using UDPForwarder forwarder = new (publicPort, localPort, ProxyForwarderKind.Game, TimeSpan.FromSeconds(10), container, attackIndicator, TimeProvider.System, NullLogger.Instance);

        using CancellationTokenSource lifetime = new ();

        Task run = forwarder.Run(lifetime.Token);

        try
        {
            using Socket client = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            IPEndPoint publicEndPoint = new (IPAddress.Loopback, publicPort);

            // The Proxy Is No Longer A Transparent Relay: A Game Datagram Shorter Than The Reader's Minimum Is Dropped Unread, So The Probe Must Be Long Enough To Be Judged On Its Contents Rather Than Its Length
            // Its Challenge Field Is Left Zero, Which Is The Unauthenticated Case: Zero Never Matches An Issued Challenge Because "SendChallenge" Skips It
            byte[] pong = Encoding.UTF8.GetBytes("PONG");

            bool relayedToServer = false;
            bool relayedToClient = false;
            bool challenged = false;

            // Loopback UDP Can Occasionally Drop A Datagram, So The Exchange Is Retried; Each Attempt Only Needs To Observe The Behaviours Not Already Seen
            // Forcing A Challenge After The Session Exists Recovers A Dropped Initial Challenge
            for (int attempt = 0; attempt < 4 && (relayedToServer is false || relayedToClient is false || challenged is false); attempt++)
            {
                // Each Attempt Carries A Fresh Counter, Because The Unauthenticated Window Admits Any Counter Only Once And A Byte-Identical Retry Would Be Refused As A Duplicate
                byte[] hello = new byte[ClientPacketReader.MinimumLength(ProxyForwarderKind.Game)];

                Encoding.UTF8.GetBytes("HELLO").CopyTo(hello, 0);
                BinaryPrimitives.WriteUInt16LittleEndian(hello.AsSpan(ClientPacketReader.CounterOffset), (ushort)attempt);

                await client.SendToAsync(hello, SocketFlags.None, publicEndPoint);

                (byte[] Payload, EndPoint Sender)? fromServer = await TryReceive(server);

                if (fromServer is null)
                    continue;

                relayedToServer |= fromServer.Value.Payload.SequenceEqual(hello);

                await server.SendToAsync(pong, SocketFlags.None, fromServer.Value.Sender);

                forwarder.RotateChallenges();

                while (relayedToClient is false || challenged is false)
                {
                    (byte[] Payload, EndPoint Sender)? datagram = await TryReceive(client);

                    if (datagram is null)
                        break;

                    if (IsChallenge(datagram.Value.Payload))
                        challenged = true;

                    else if (datagram.Value.Payload.SequenceEqual(pong))
                        relayedToClient = true;
                }
            }

            using (Assert.Multiple())
            {
                await Assert.That(relayedToServer).IsTrue();
                await Assert.That(relayedToClient).IsTrue();
                await Assert.That(challenged).IsTrue();
            }
        }

        finally
        {
            await lifetime.CancelAsync();

            try { await run; } catch (Exception) { }
        }
    }

    [Test]
    public async Task Successive_Challenges_Are_Not_Consecutive()
    {
        int publicPort = FreeUDPPort();
        int localPort = FreeUDPPort();

        ViolationScoreContainer container = new (TimeProvider.System);
        AttackIndicatorContainer attackIndicator = new (TimeProvider.System);

        using UDPForwarder forwarder = new (publicPort, localPort, ProxyForwarderKind.Game, TimeSpan.FromSeconds(10), container, attackIndicator, TimeProvider.System, NullLogger.Instance);

        using CancellationTokenSource lifetime = new ();

        Task run = forwarder.Run(lifetime.Token);

        try
        {
            using Socket client = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            // Establishing A Session Triggers The Initial Challenge; This Datagram Is Deliberately Too Short And Is Dropped, Because A Client Sending Nothing Valid Must Still Be Challenged Or It Could Never Learn A Challenge To Echo
            await client.SendToAsync(Encoding.UTF8.GetBytes("HELLO"), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, publicPort));

            // Flush Any Challenges Buffered From Session Creation So The Two Values Compared Below Are Read In Issue Order
            await DrainUntilIdle(client);

            forwarder.RotateChallenges();

            uint firstValue = await ReadOneChallengeValue(forwarder, client);

            forwarder.RotateChallenges();

            uint secondValue = await ReadOneChallengeValue(forwarder, client);

            using (Assert.Multiple())
            {
                await Assert.That(firstValue).IsNotEqualTo((uint)0);
                await Assert.That(secondValue).IsNotEqualTo((uint)0);
                await Assert.That(secondValue).IsNotEqualTo(firstValue + 1);
            }
        }

        finally
        {
            await lifetime.CancelAsync();

            try { await run; } catch (Exception) { }
        }
    }

    // A Datagram Below The Minimum Length Must Never Reach The Server, Because The Reader Refuses To Read Its Fields
    [Test]
    public async Task A_Short_Datagram_Is_Dropped_Rather_Than_Relayed()
    {
        await using ForwarderProbe probe = new ();

        await Assert.That(await probe.Refuses(new byte[8])).IsTrue();
    }

    [Test]
    public async Task A_Datagram_Echoing_An_Issued_Challenge_Is_Relayed()
    {
        await using ForwarderProbe probe = new ();

        uint challenge = await probe.Establish();

        await Assert.That(await probe.Relays(GameDatagram(challenge, counter: 0))).IsTrue();
    }

    // The Duplicate Check Is What Stops A Captured Datagram Being Replayed, And Nothing Above The Forwarder Exercises It
    [Test]
    public async Task A_Repeated_Counter_Under_One_Challenge_Is_Dropped()
    {
        await using ForwarderProbe probe = new ();

        uint challenge = await probe.Establish();

        using (Assert.Multiple())
        {
            await Assert.That(await probe.Relays(GameDatagram(challenge, counter: 5))).IsTrue();
            await Assert.That(await probe.Refuses(GameDatagram(challenge, counter: 5))).IsTrue();
        }
    }

    [Test]
    public async Task A_Counter_At_The_Quota_Is_Dropped()
    {
        await using ForwarderProbe probe = new ();

        uint challenge = await probe.Establish();

        ushort quota = ChallengeQuota.ForKind(ProxyForwarderKind.Game, TimeSpan.FromSeconds(10));

        await Assert.That(await probe.Refuses(GameDatagram(challenge, quota))).IsTrue();
    }

    // The Enforced Quota Comes From A Field And The Advertised One From The Wire, So A Shared Derivation Does Not Protect The Encoding: Transposing These Two Writes Would Silently Hold A Client To The Game-Command Quota For All Its Traffic
    [Test]
    public async Task The_Advertised_Quotas_Match_What_Is_Enforced()
    {
        await using ForwarderProbe probe = new ();

        await probe.Relays(GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0));

        byte[] challenge = await ReadOneChallengePacket(probe.Forwarder, probe.Client);

        ushort expectedPacketQuota = ChallengeQuota.ForKind(ProxyForwarderKind.Game, TimeSpan.FromSeconds(10));
        ushort expectedGameCommandQuota = ChallengeQuota.GameCommandForInterval(TimeSpan.FromSeconds(10));

        using (Assert.Multiple())
        {
            await Assert.That(ChallengePacketQuota(challenge)).IsEqualTo(expectedPacketQuota);
            await Assert.That(ChallengeGameCommandQuota(challenge)).IsEqualTo(expectedGameCommandQuota);
        }
    }

    // A Challenge The Proxy Never Issued Is Refused Whether Or Not The Session Is Still Within Its Grace; What The Grace Changes Is The Charge, Which The Two Grace Tests Cover
    [Test]
    public async Task A_Challenge_The_Proxy_Never_Issued_Is_Dropped()
    {
        await using ForwarderProbe probe = new ();

        await probe.Establish();

        await Assert.That(await probe.Refuses(GameDatagram(challenge: 0xDEADBEEF, counter: 1))).IsTrue();
    }

    [Test]
    public async Task An_Unauthenticated_Counter_At_The_Total_Is_Dropped()
    {
        await using ForwarderProbe probe = new ();

        await probe.Establish();

        await Assert.That(await probe.Refuses(GameDatagram(SessionChallengeState.UnauthenticatedChallenge, ChallengeQuota.UnauthenticatedPacketQuota))).IsTrue();
    }

    // The Allowance Is Checked Before The Challenge Is Matched, So An Actioned Source Is Refused Whatever It Sends
    [Test]
    public async Task An_Actioned_Source_Has_Even_A_Valid_Datagram_Dropped()
    {
        await using ForwarderProbe probe = new ();

        uint challenge = await probe.Establish();

        while (probe.Scores.IsWithinAllowance(probe.ClientEndPoint))
            probe.Scores.ChargeArrival(probe.ClientEndPoint);

        await Assert.That(await probe.Refuses(GameDatagram(challenge, counter: 1))).IsTrue();
    }

    // Pins The Order Rather Than The Outcome: An Actioned Source's Short Datagram Must Be Refused By The Allowance Check Before The Length Guard Can Charge It
    [Test]
    public async Task An_Actioned_Source_Is_Refused_Before_The_Length_Guard_Charges_It()
    {
        await using ForwarderProbe probe = new ();

        await probe.Establish();

        while (probe.Scores.IsWithinAllowance(probe.ClientEndPoint))
            probe.Scores.ChargeArrival(probe.ClientEndPoint);

        int scoreBefore = probe.Scores.Score(probe.ClientEndPoint);

        await Assert.That(await probe.Refuses(new byte[8])).IsTrue();

        // Exactly The Arrival And Nothing Else: If The Arrival Were Charged After The Allowance Check The Score Would Not Move, And If The Length Guard Ran First It Would Rise By "TooShortViolationWeight" As Well
        await Assert.That(probe.Scores.Score(probe.ClientEndPoint)).IsEqualTo(scoreBefore + ViolationScoreContainer.PacketScore);
    }

    // The Client Accepts A Replacement Challenge Only If Its Timestamp Is Strictly Greater Than The One It Holds, And It Keys That On Our Public Port, Which A COMPEL Restart Does Not Change
    // A Counter That Restarts At Zero Therefore Issues Timestamps The Client Rejects As Old, So This Must Come From The Wall Clock
    [Test]
    public async Task The_Challenge_Timestamp_Comes_From_The_Wall_Clock()
    {
        await using ForwarderProbe probe = new ();

        uint before = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await Assert.That(await probe.Relays(GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0))).IsTrue();

        uint timestamp = await ReadOneChallengeTimestamp(probe.Forwarder, probe.Client);

        uint after = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using (Assert.Multiple())
        {
            await Assert.That(timestamp).IsGreaterThanOrEqualTo(before);
            await Assert.That(timestamp).IsLessThanOrEqualTo(after);
        }
    }

    [Test]
    public async Task Rotate_Challenges_In_Same_Second_Produces_Strictly_Increasing_Timestamp_For_Session()
    {
        ControllableTimeProvider clock = new ();

        await using ForwarderProbe probe = new (clock);

        await Assert.That(await probe.Relays(GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0))).IsTrue();

        uint initialTimestamp = await ReadOneChallengeTimestamp(probe.Forwarder, probe.Client);

        // Do Not Advance Clock: Rotation Happens Within The Same Unix Second
        probe.Forwarder.RotateChallenges();

        uint rotatedTimestamp = await ReadOneChallengeTimestamp(probe.Forwarder, probe.Client);

        await Assert.That(rotatedTimestamp).IsGreaterThan(initialTimestamp);
    }

    // The Mark Advanced On Every Transmit Rather Than Per Challenge, So It Ran Ahead Of The Clock At One Per Session Per Second And A Restart Then Issued Timestamps The Client Rejected As Old
    // One Challenge From A Fresh Forwarder Cannot See That, Which Is Why The Original Test Passed Against It
    // A Second Session Is What Distinguishes A Per-Session Floor From A Forwarder-Level One That Merely Advances On Value Rather Than On Transmit; One Session Alone Cannot Tell The Two Apart
    [Test]
    public async Task The_Challenge_Timestamp_Does_Not_Drift_Ahead_Of_The_Clock()
    {
        await using ForwarderProbe probe = new ();

        uint issued = await probe.Establish();

        await Assert.That(await probe.Relays(GameDatagram(issued, counter: 0))).IsTrue();

        // A Second, Independent Session On The Same Forwarder And Public Port
        using Socket otherClient = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        otherClient.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        await otherClient.SendToAsync(GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, probe.Forwarder.PublicPort));

        // Far More Transmits Than Wall-Clock Seconds Will Pass During Them
        for (int repeat = 0; repeat < 50; repeat++)
            probe.Forwarder.RepeatChallenges();

        await DrainUntilIdle(probe.Client);
        await DrainUntilIdle(otherClient);

        probe.Forwarder.RotateChallenges();

        uint timestamp = await ReadOneChallengeTimestamp(probe.Forwarder, probe.Client);
        uint ceiling = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2;

        await Assert.That(timestamp).IsLessThanOrEqualTo(ceiling);
    }

    [Test]
    public async Task Rotated_And_Repeated_Challenges_Are_Accepted_By_Client_Challenge_Store()
    {
        await using ForwarderProbe probe = new ();

        uint initialChallenge = await probe.Establish();

        using (Assert.Multiple())
        {
            await Assert.That(probe.ClientChallenges.TryGetChallenge(probe.PublicEndPoint, out uint storedInitial, out _)).IsTrue();
            await Assert.That(storedInitial).IsEqualTo(initialChallenge);
        }

        // A Rotation Emits A New Challenge Packet With Strictly Greater Monotonic Timestamp
        probe.Forwarder.RotateChallenges();

        uint rotatedChallenge = await probe.ReceiveAndStoreChallenge();

        using (Assert.Multiple())
        {
            await Assert.That(probe.ClientChallenges.TryGetChallenge(probe.PublicEndPoint, out uint storedRotated, out _)).IsTrue();
            await Assert.That(storedRotated).IsEqualTo(rotatedChallenge);
            await Assert.That(storedRotated).IsNotEqualTo(initialChallenge);
        }

        // A Repeat Re-Sends The Current Challenge With Same Timestamp, Which The Store Ignores Without Failing
        probe.Forwarder.RepeatChallenges();

        byte[] repeatPacket = await ReadOneChallengePacket(probe.Forwarder, probe.Client);
        bool repeatAccepted = probe.ClientChallenges.TryProcessChallengePacket(probe.PublicEndPoint, repeatPacket);

        using (Assert.Multiple())
        {
            // A Repeat Has Same Timestamp So Client Retains Held Challenge Without Updating Or Throwing
            await Assert.That(repeatAccepted).IsFalse();
            await Assert.That(probe.ClientChallenges.TryGetChallenge(probe.PublicEndPoint, out uint storedAfterRepeat, out _)).IsTrue();
            await Assert.That(storedAfterRepeat).IsEqualTo(rotatedChallenge);
        }
    }

    // A Lost Challenge Must Be Retried Long Before The Next Rotation, Because The Client Enforces The Advertised Quota Itself And Goes Silent Rather Than Over-Sending
    [Test]
    public async Task A_Repeated_Challenge_Carries_The_Same_Value()
    {
        await using ForwarderProbe probe = new ();

        uint issued = await probe.Establish();

        // Authenticating First Keeps This Test's Setup Independent Of Whether A Session Has Authenticated, Since The Repeat Now Reaches Every Session Regardless
        await Assert.That(await probe.Relays(GameDatagram(issued, counter: 0))).IsTrue();

        await DrainUntilIdle(probe.Client);

        // Retried Through "RepeatChallenges" Rather Than "ReadOneChallengeValue", Which Rotates On A Miss And Would Hand Back A Different Value, Failing This For The Wrong Reason
        uint repeated = 0;

        for (int attempt = 0; attempt < 10 && repeated is 0; attempt++)
        {
            probe.Forwarder.RepeatChallenges();

            (byte[] Payload, EndPoint Sender)? datagram = await TryReceive(probe.Client);

            if (datagram is not null && IsChallenge(datagram.Value.Payload))
                repeated = ChallengeValue(datagram.Value.Payload);
        }

        await Assert.That(repeated).IsEqualTo(issued);
    }

    // The Repeat Exists So One Lost Challenge Cannot Leave A Client Throttled, So It Must Reach A Session Whose Only Challenge Was The One That Was Lost
    [Test]
    public async Task A_Session_That_Has_Not_Authenticated_Still_Receives_A_Repeat()
    {
        await using ForwarderProbe probe = new ();

        // One Datagram Creates The Session And Triggers Its Only Challenge; Nothing Authenticates It
        await probe.Relays(GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0));

        await DrainUntilIdle(probe.Client);

        probe.Forwarder.RepeatChallenges();

        (byte[] Payload, EndPoint Sender)? datagram = await TryReceive(probe.Client);

        await Assert.That(datagram is not null && IsChallenge(datagram.Value.Payload)).IsTrue();
    }

    // A Client Whose Source Port Changes Keeps Echoing The Challenge It Holds, Because It Keys That On Our Public Port; The New Session Knows Nothing Of It, So Charging For It Would Refuse A Well-Behaved Player
    [Test]
    public async Task A_Session_That_Has_Not_Authenticated_Is_Not_Charged_For_An_Unknown_Challenge()
    {
        await using ForwarderProbe probe = new ();

        int scoreBefore = probe.Scores.Score(probe.ClientEndPoint);

        // A Counter Partway Through A Window, As A Client Mid-Session Would Carry
        await Assert.That(await probe.Refuses(GameDatagram(challenge: 0xDEADBEEF, counter: 600))).IsTrue();

        // Refused, But Charged Only The Arrival: The Violation Weight Would Cross The Threshold In Well Under A Second At Ordinary Game Rates
        await Assert.That(probe.Scores.Score(probe.ClientEndPoint)).IsEqualTo(scoreBefore + ViolationScoreContainer.PacketScore);
    }

    [Test]
    public async Task A_Session_That_Has_Authenticated_Is_Charged_For_An_Unknown_Challenge()
    {
        await using ForwarderProbe probe = new ();

        uint issued = await probe.Establish();

        // "Establish" Echoes Challenge Zero, Which Does Not End The Grace; Admitting A Datagram Under An ISSUED Challenge Is What Proves The Client Is In Sync
        await Assert.That(await probe.Relays(GameDatagram(issued, counter: 0))).IsTrue();

        int scoreBefore = probe.Scores.Score(probe.ClientEndPoint);

        await Assert.That(await probe.Refuses(GameDatagram(challenge: 0xDEADBEEF, counter: 1))).IsTrue();

        await Assert.That(probe.Scores.Score(probe.ClientEndPoint)).IsGreaterThanOrEqualTo(scoreBefore + ViolationScoreContainer.ChallengeViolationWeight);
    }

    // Pinned As Literals Deliberately: The Behavioural Tests Below Advance The Clock By Whatever These Constants Say, So On Their Own They Would Still Pass If A Constant Were Widened To Infinity
    // Asserting The Values Here Is What Makes Changing One A Deliberate Act With A Failing Test Behind It
    [Test]
    public async Task The_Grace_And_Both_Idle_Timeouts_Are_The_Durations_They_Are_Documented_As()
    {
        using (Assert.Multiple())
        {
            await Assert.That(UDPForwarder.UnknownChallengeGrace).IsEqualTo(TimeSpan.FromSeconds(30));
            await Assert.That(UDPProxyService.UnauthenticatedSessionTimeout).IsEqualTo(TimeSpan.FromSeconds(15));
            await Assert.That(UDPProxyService.IdleSessionTimeout).IsEqualTo(TimeSpan.FromMinutes(2));
        }
    }

    // The Grace Has To End, Or A Hostile Source Keeps The Violation Weight Suppressed For As Long As It Cares To Send: The Bound Is The Only Limit On That, And Until The Clock Was Injectable No Test Could Reach It
    [Test]
    public async Task A_Session_That_Has_Not_Authenticated_Is_Charged_For_An_Unknown_Challenge_Once_The_Grace_Has_Expired()
    {
        ControllableTimeProvider clock = new ();

        await using ForwarderProbe probe = new (clock);

        // One Datagram Creates The Session; Echoing Zero Does Not Authenticate It, So Only The Grace's Expiry Can Change The Charge Below
        await probe.Relays(GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0));

        clock.Advance(UDPForwarder.UnknownChallengeGrace);

        int scoreBefore = probe.Scores.Score(probe.ClientEndPoint);

        await Assert.That(await probe.Refuses(GameDatagram(challenge: 0xDEADBEEF, counter: 600))).IsTrue();

        await Assert.That(probe.Scores.Score(probe.ClientEndPoint)).IsEqualTo(scoreBefore + ViolationScoreContainer.PacketScore + ViolationScoreContainer.ChallengeViolationWeight);
    }

    [Test]
    public async Task A_Session_That_Has_Not_Authenticated_Is_Evicted_Once_The_Unauthenticated_Timeout_Has_Elapsed()
    {
        ControllableTimeProvider clock = new ();

        await using ForwarderProbe probe = new (clock);

        await probe.Relays(GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0));

        await DrainUntilIdle(probe.Client);

        clock.Advance(UDPProxyService.UnauthenticatedSessionTimeout);

        probe.Forwarder.EvictIdleSessions(UDPProxyService.IdleSessionTimeout, UDPProxyService.UnauthenticatedSessionTimeout);

        probe.Forwarder.RepeatChallenges();

        // A Repeat Reaches Every Session Holding A Challenge, So Nothing Arriving Is What Proves The Session Is Gone
        await Assert.That(await TryReceive(probe.Client)).IsNull();
    }

    [Test]
    public async Task A_Session_That_Has_Not_Authenticated_Survives_Until_The_Unauthenticated_Timeout()
    {
        ControllableTimeProvider clock = new ();

        await using ForwarderProbe probe = new (clock);

        await probe.Relays(GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0));

        await DrainUntilIdle(probe.Client);

        clock.Advance(UDPProxyService.UnauthenticatedSessionTimeout - TimeSpan.FromSeconds(1));

        probe.Forwarder.EvictIdleSessions(UDPProxyService.IdleSessionTimeout, UDPProxyService.UnauthenticatedSessionTimeout);

        probe.Forwarder.RepeatChallenges();

        (byte[] Payload, EndPoint Sender)? datagram = await TryReceive(probe.Client);

        await Assert.That(datagram is not null && IsChallenge(datagram.Value.Payload)).IsTrue();
    }

    // The Shorter Timeout Applies Only Until A Session Authenticates, Which Is What Keeps It From Sweeping A Real Match That Has Gone Quiet
    [Test]
    public async Task A_Session_That_Has_Authenticated_Survives_The_Unauthenticated_Timeout()
    {
        ControllableTimeProvider clock = new ();

        await using ForwarderProbe probe = new (clock);

        uint issued = await probe.Establish();

        // Admitting A Datagram Under An Issued Challenge Is What Marks The Session Authenticated; Echoing Zero Would Not
        await Assert.That(await probe.Relays(GameDatagram(issued, counter: 0))).IsTrue();

        await DrainUntilIdle(probe.Client);

        clock.Advance(UDPProxyService.UnauthenticatedSessionTimeout);

        probe.Forwarder.EvictIdleSessions(UDPProxyService.IdleSessionTimeout, UDPProxyService.UnauthenticatedSessionTimeout);

        probe.Forwarder.RepeatChallenges();

        (byte[] Payload, EndPoint Sender)? datagram = await TryReceive(probe.Client);

        await Assert.That(datagram is not null && IsChallenge(datagram.Value.Payload)).IsTrue();
    }

    [Test]
    public async Task A_Session_That_Has_Authenticated_Is_Evicted_Once_The_Idle_Timeout_Has_Elapsed()
    {
        ControllableTimeProvider clock = new ();

        await using ForwarderProbe probe = new (clock);

        uint issued = await probe.Establish();

        await Assert.That(await probe.Relays(GameDatagram(issued, counter: 0))).IsTrue();

        await DrainUntilIdle(probe.Client);

        clock.Advance(UDPProxyService.IdleSessionTimeout);

        probe.Forwarder.EvictIdleSessions(UDPProxyService.IdleSessionTimeout, UDPProxyService.UnauthenticatedSessionTimeout);

        probe.Forwarder.RepeatChallenges();

        await Assert.That(await TryReceive(probe.Client)).IsNull();
    }

    // Two sessions on one forwarder share the destination public port.
    // When challenges rotate, each session is sent the forwarder's latest retained challenge.
    // A client storing challenges by destination port accepts the last challenge received for that destination port, so any session on that port echoing that challenge must be admitted.
    [Test]
    public async Task A_Rotated_Challenge_Issued_To_One_Session_Is_Admitted_When_Echoed_By_Another_Session_On_The_Same_Forwarder()
    {
        await using ForwarderProbe probe = new ();

        uint session1FirstChallenge = await probe.Establish();

        await Assert.That(await probe.Relays(GameDatagram(session1FirstChallenge, counter: 0))).IsTrue();

        using Socket client2 = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        client2.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        IPEndPoint publicEndPoint = new (IPAddress.Loopback, probe.Forwarder.PublicPort);

        await client2.SendToAsync(GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0), SocketFlags.None, publicEndPoint);

        await DrainUntilIdle(probe.Client);
        await DrainUntilIdle(client2);
        await DrainUntilIdle(probe.Server);

        probe.Forwarder.RotateChallenges();

        uint challenge2 = await ReadOneChallengeValue(probe.Forwarder, client2);

        // Session 1 echoes challenge2 (which was issued during rotation to Session 2).
        // Since both sessions share the forwarder, this challenge must be recognized as retained by the forwarder.
        await Assert.That(await probe.Relays(GameDatagram(challenge2, counter: 10))).IsTrue();
    }

    [Test]
    public async Task A_Forwarder_At_Its_Total_Session_Cap_Refuses_A_Novel_Session()
    {
        await using ForwarderProbe probe = new ();

        List<Socket> clients = new ();
        try
        {
            for (int ipIndex = 1; ipIndex <= 3; ipIndex++)
            {
                IPAddress ipAddress = IPAddress.Parse($"127.0.0.{ipIndex}");

                for (int sessionIndex = 0; sessionIndex < 8; sessionIndex++)
                {
                    Socket clientSocket = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                    clientSocket.Bind(new IPEndPoint(ipAddress, 0));
                    clients.Add(clientSocket);

                    byte[] datagram = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: (ushort)sessionIndex);

                    await Assert.That(await probe.RelaysFrom(clientSocket, datagram)).IsTrue();
                }
            }

            using Socket client25 = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            client25.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.4"), 0));

            byte[] overflowDatagram = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0);

            await Assert.That(await probe.RefusesFrom(client25, overflowDatagram)).IsTrue();
        }

        finally
        {
            foreach (Socket clientSocket in clients)
                clientSocket.Dispose();
        }
    }

    // Documentation Addresses, Which Are Public To The Classification But Reach No One
    [Test]
    public async Task A_Forwarder_At_Its_Per_Address_Session_Cap_Refuses_A_Novel_Session_From_That_Public_Address_While_Admitting_Another_Address()
    {
        await using ForwarderProbe probe = new ();

        IPAddress publicAddress = IPAddress.Parse("203.0.113.1");

        int admitted = 0;

        for (int sessionIndex = 0; sessionIndex < UDPForwarder.MaxSessionsPerAddress; sessionIndex++)
        {
            if (Admit(probe.Forwarder, publicAddress, sessionIndex) is UDPForwarder.SessionAdmissionResult.Admitted)
                admitted++;
        }

        using (Assert.Multiple())
        {
            await Assert.That(admitted).IsEqualTo(UDPForwarder.MaxSessionsPerAddress);
            await Assert.That(Admit(probe.Forwarder, publicAddress, UDPForwarder.MaxSessionsPerAddress)).IsEqualTo(UDPForwarder.SessionAdmissionResult.AddressCapReached);
            await Assert.That(Admit(probe.Forwarder, IPAddress.Parse("198.51.100.1"), 0)).IsEqualTo(UDPForwarder.SessionAdmissionResult.Admitted);
        }
    }

    // Behind A Relay Every Player Arrives Under The Relay's Address, So A Source Only A Relay Or The Local Network Can Present Is Held To The Forwarder's Cap Alone
    [Test]
    public async Task A_Loopback_Source_Opens_Sessions_Beyond_The_Per_Address_Cap_Until_The_Forwarder_Cap()
    {
        await using ForwarderProbe probe = new ();

        List<Socket> clients = new ();

        try
        {
            for (int sessionIndex = 0; sessionIndex < UDPForwarder.MaxSessionsPerForwarder; sessionIndex++)
            {
                Socket clientSocket = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                clientSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                clients.Add(clientSocket);

                byte[] datagram = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: (ushort)sessionIndex);

                await Assert.That(await probe.RelaysFrom(clientSocket, datagram)).IsTrue();
            }

            using Socket overflowClient = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            overflowClient.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            byte[] overflowDatagram = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0);

            await Assert.That(await probe.RefusesFrom(overflowClient, overflowDatagram)).IsTrue();
        }

        finally
        {
            foreach (Socket clientSocket in clients)
                clientSocket.Dispose();
        }
    }

    // Only Loopback Can Be Bound Here, So The Other Ranges Are Admitted Without A Datagram
    [Test]
    [Arguments("10.1.2.3")]
    [Arguments("172.16.1.2")]
    [Arguments("192.168.1.2")]
    [Arguments("169.254.1.2")]
    [Arguments("100.64.1.2")]
    [Arguments("::ffff:127.0.0.1")]
    [Arguments("::ffff:192.168.1.2")]
    public async Task A_Non_Public_Source_Opens_Sessions_Beyond_The_Per_Address_Cap_Until_The_Forwarder_Cap(string address)
    {
        await using ForwarderProbe probe = new ();

        IPAddress source = IPAddress.Parse(address);

        int admitted = 0;

        for (int sessionIndex = 0; sessionIndex < UDPForwarder.MaxSessionsPerForwarder; sessionIndex++)
        {
            if (Admit(probe.Forwarder, source, sessionIndex) is UDPForwarder.SessionAdmissionResult.Admitted)
                admitted++;
        }

        using (Assert.Multiple())
        {
            await Assert.That(admitted).IsEqualTo(UDPForwarder.MaxSessionsPerForwarder);
            await Assert.That(Admit(probe.Forwarder, source, UDPForwarder.MaxSessionsPerForwarder)).IsEqualTo(UDPForwarder.SessionAdmissionResult.ForwarderCapReached);
        }
    }

    // Loopback Is Not Held To The Per-Address Cap, So The Slot Filled Here Is The Forwarder's
    [Test]
    public async Task An_Evicted_Session_Releases_Its_Session_Slot()
    {
        ControllableTimeProvider clock = new ();

        await using ForwarderProbe probe = new (clock);

        List<Socket> clients = new ();

        try
        {
            for (int sessionIndex = 0; sessionIndex < UDPForwarder.MaxSessionsPerForwarder; sessionIndex++)
            {
                Socket clientSocket = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                clientSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                clients.Add(clientSocket);

                byte[] datagram = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: (ushort)sessionIndex);

                await Assert.That(await probe.RelaysFrom(clientSocket, datagram)).IsTrue();
            }

            using Socket overflowClient = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            overflowClient.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            byte[] overflowDatagram = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0);

            await Assert.That(await probe.RefusesFrom(overflowClient, overflowDatagram)).IsTrue();

            clock.Advance(UDPProxyService.UnauthenticatedSessionTimeout);
            probe.Forwarder.EvictIdleSessions(UDPProxyService.IdleSessionTimeout, UDPProxyService.UnauthenticatedSessionTimeout);

            await Assert.That(await probe.RelaysFrom(overflowClient, overflowDatagram)).IsTrue();
        }

        finally
        {
            foreach (Socket clientSocket in clients)
                clientSocket.Dispose();
        }
    }

    [Test]
    public async Task When_The_Under_Attack_Indicator_Is_Active_A_Novel_Session_Is_Refused()
    {
        await using ForwarderProbe probe = new ();

        probe.AttackIndicator.Charge(AttackIndicatorContainer.UnderAttackThreshold + 100);

        using Socket newClient = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        newClient.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        byte[] datagram = GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0);

        await Assert.That(await probe.RefusesFrom(newClient, datagram)).IsTrue();
    }

    [Test]
    public async Task When_The_Under_Attack_Indicator_Is_Active_An_Existing_Session_Continues_To_Relay()
    {
        await using ForwarderProbe probe = new ();

        uint challenge = await probe.Establish();

        await Assert.That(await probe.Relays(GameDatagram(challenge, counter: 0))).IsTrue();

        probe.AttackIndicator.Charge(AttackIndicatorContainer.UnderAttackThreshold + 100);

        await Assert.That(await probe.Relays(GameDatagram(challenge, counter: 1))).IsTrue();
    }

    [Test]
    public async Task Forty_Spoofed_Violation_Datagrams_Do_Not_Refuse_A_Victims_Valid_Traffic()
    {
        await using ForwarderProbe probe = new ();

        uint challenge = await probe.Establish();

        await Assert.That(await probe.Relays(GameDatagram(challenge, counter: 0))).IsTrue();

        for (int index = 0; index < 40; index++)
            await probe.Refuses(GameDatagram(challenge: 0xDEADBEEF, counter: (ushort)(index + 10)));

        await Assert.That(await probe.Relays(GameDatagram(challenge, counter: 1))).IsTrue();
    }

    // The Chat Server Verifies A Match Lock With The Reconnect Query From A Fresh Source Each Time, So As A Session It Would Be Challenged, Charged, And Counted Like A Player
    [Test]
    public async Task A_Reconnect_Query_Is_Answered_Without_A_Session_Or_A_Charge()
    {
        await using ForwarderProbe probe = new ();

        await Assert.That(await probe.AnswersQueryFrom(probe.Client, ReconnectQueryTests.Reply(240000))).IsTrue();

        // A Session Is Challenged When It Is Created And Again By Every Repeat, So Nothing Further Arriving Is What Proves There Is None
        probe.Forwarder.RepeatChallenges();

        using (Assert.Multiple())
        {
            await Assert.That(await TryReceive(probe.Client)).IsNull();
            await Assert.That(probe.Scores.Score(probe.ClientEndPoint)).IsEqualTo(0);
            await Assert.That(probe.AttackIndicator.Score).IsEqualTo(0);
            await Assert.That(probe.Forwarder.DroppedDatagramCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task A_Reconnect_Query_Is_Answered_At_The_Session_Caps()
    {
        await using ForwarderProbe probe = new ();

        List<Socket> players = new ();

        try
        {
            // The Players Bring The Forwarder To Its Total; Their Loopback Addresses Are Not Held To The Per-Address Cap
            for (int index = 0; index < 24; index++)
            {
                Socket player = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                player.Bind(new IPEndPoint(IPAddress.Parse($"127.0.0.{index / 10 + 1}"), 0));
                players.Add(player);

                await Assert.That(await probe.RelaysFrom(player, GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: (ushort)index))).IsTrue();
            }

            using Socket novelPlayer = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            novelPlayer.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            await Assert.That(await probe.RefusesFrom(novelPlayer, GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0))).IsTrue();

            int attackScoreBefore = probe.AttackIndicator.Score;

            using Socket chatServer = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            chatServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            using (Assert.Multiple())
            {
                await Assert.That(await probe.AnswersQueryFrom(chatServer, ReconnectQueryTests.Reply(240000))).IsTrue();
                await Assert.That(probe.AttackIndicator.Score).IsEqualTo(attackScoreBefore);
            }
        }

        finally
        {
            foreach (Socket player in players)
                player.Dispose();
        }
    }

    [Test]
    public async Task A_Reconnect_Query_Is_Answered_While_Under_Attack()
    {
        await using ForwarderProbe probe = new ();

        probe.AttackIndicator.Charge(AttackIndicatorContainer.UnderAttackThreshold + 100);

        int attackScoreBefore = probe.AttackIndicator.Score;

        using (Assert.Multiple())
        {
            await Assert.That(await probe.AnswersQueryFrom(probe.Client, ReconnectQueryTests.Reply(240000))).IsTrue();
            await Assert.That(probe.AttackIndicator.Score).IsEqualTo(attackScoreBefore);
        }
    }

    // The Chat Server Resends The Same Bytes From The Same Source When An Attempt Goes Unanswered, Which A Session Would Refuse As A Duplicate Counter
    [Test]
    public async Task Every_Attempt_Of_A_Reconnect_Query_Is_Forwarded()
    {
        await using ForwarderProbe probe = new ();

        List<EndPoint> upstreams = new ();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (await probe.ForwardsQueryFrom(probe.Client) is EndPoint upstream)
                upstreams.Add(upstream);
        }

        await Assert.That(upstreams.Count).IsEqualTo(3);

        await Assert.That(await probe.RelaysReply(upstreams[^1], ReconnectQueryTests.Reply(240000), probe.Client)).IsTrue();
    }

    // Each Changes One Fixed Byte: The Header's Connection ID, The Flags, The Command, The Query's Connection ID, Where A Client Datagram Carries Its Challenge And Its Counter, And The Second Copy's Command And Match ID
    // A Fresh Source Being Challenged Is What Shows The Datagram Went Down The Ordinary Path
    [Test]
    [Arguments(0)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(12)]
    [Arguments(28)]
    [Arguments(32)]
    [Arguments(43)]
    [Arguments(44)]
    public async Task A_Near_Miss_Of_The_Reconnect_Query_Takes_The_Session_Path(int offset)
    {
        await using ForwarderProbe probe = new ();

        byte[] nearMiss = ReconnectQueryTests.Request();

        nearMiss[offset] ^= 0xFF;

        await probe.Client.SendToAsync(nearMiss, SocketFlags.None, probe.PublicEndPoint);

        (byte[] Payload, EndPoint Sender)? datagram = await TryReceive(probe.Client);

        await Assert.That(datagram is not null && IsChallenge(datagram.Value.Payload)).IsTrue();
    }

    [Test]
    [Arguments(53)]
    [Arguments(55)]
    public async Task A_Reconnect_Query_Of_Another_Length_Takes_The_Session_Path(int length)
    {
        await using ForwarderProbe probe = new ();

        byte[] nearMiss = new byte[length];

        ReconnectQueryTests.Request().AsSpan(0, Math.Min(length, ReconnectQueryTests.Request().Length)).CopyTo(nearMiss);

        await probe.Client.SendToAsync(nearMiss, SocketFlags.None, probe.PublicEndPoint);

        (byte[] Payload, EndPoint Sender)? datagram = await TryReceive(probe.Client);

        await Assert.That(datagram is not null && IsChallenge(datagram.Value.Payload)).IsTrue();
    }

    // The Match Server Answers The Query Only On Its Game Port, So A Voice Port Has No Reason To Relay It Outside A Session
    [Test]
    public async Task A_Reconnect_Query_To_A_Voice_Port_Takes_The_Session_Path()
    {
        await using ForwarderProbe probe = new (kind: ProxyForwarderKind.Voice);

        await probe.Client.SendToAsync(ReconnectQueryTests.Request(), SocketFlags.None, probe.PublicEndPoint);

        (byte[] Payload, EndPoint Sender)? datagram = await TryReceive(probe.Client);

        await Assert.That(datagram is not null && IsChallenge(datagram.Value.Payload)).IsTrue();
    }

    [Test]
    public async Task Reconnect_Queries_Over_The_Rate_Limit_Are_Dropped_And_Counted()
    {
        ControllableTimeProvider clock = new ();

        await using ForwarderProbe probe = new (clock);

        byte[] request = ReconnectQueryTests.Request();

        // The Clock Stands Still, So Every Attempt Falls In The Same Window; Twice The Window's Count Still Exceeds It If A Few Datagrams Are Lost On The Way In
        int sent = UDPForwarder.ReconnectQueriesPerWindow * 2;

        for (int attempt = 0; attempt < sent; attempt++)
            await probe.Client.SendToAsync(request, SocketFlags.None, probe.PublicEndPoint);

        int forwarded = 0;

        while (await TryReceive(probe.Server) is not null)
            forwarded++;

        long dropped = probe.Forwarder.DroppedDatagramCount;

        using (Assert.Multiple())
        {
            await Assert.That(forwarded).IsEqualTo(UDPForwarder.ReconnectQueriesPerWindow);
            await Assert.That(dropped).IsGreaterThan(0);
            await Assert.That(forwarded + dropped).IsLessThanOrEqualTo(sent);
            await Assert.That(probe.Scores.Score(probe.ClientEndPoint)).IsEqualTo(0);
            await Assert.That(probe.AttackIndicator.Score).IsEqualTo(0);
        }

        clock.Advance(UDPForwarder.ReconnectQueryWindow);

        await Assert.That(await probe.Relays(request)).IsTrue();
    }

    [Test]
    public async Task Reconnect_Queries_From_New_Sources_Beyond_The_Pending_Bound_Are_Dropped_Until_One_Expires()
    {
        ControllableTimeProvider clock = new ();

        await using ForwarderProbe probe = new (clock);

        byte[] request = ReconnectQueryTests.Request();

        List<Socket> sources = new ();

        try
        {
            int forwarded = 0;

            for (int index = 0; index < UDPForwarder.MaximumPendingReconnectQueries; index++)
            {
                // A Fresh Window For Every Query, So That Only The Pending Bound Can Refuse One; Nothing Expires, Because Only Eviction Expires A Pending Query
                clock.Advance(UDPForwarder.ReconnectQueryWindow);

                Socket source = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                source.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                sources.Add(source);

                if (await probe.RelaysFrom(source, request))
                    forwarded++;
            }

            await Assert.That(forwarded).IsEqualTo(UDPForwarder.MaximumPendingReconnectQueries);

            clock.Advance(UDPForwarder.ReconnectQueryWindow);

            using Socket newSource = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            newSource.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            using (Assert.Multiple())
            {
                await Assert.That(await probe.RefusesFrom(newSource, request)).IsTrue();

                // A Retry Uses The Entry Its Source Already Holds, So The Bound Does Not Refuse It
                await Assert.That(await probe.RelaysFrom(sources[^1], request)).IsTrue();
            }

            clock.Advance(UDPForwarder.PendingReconnectQueryTimeout);

            probe.Forwarder.EvictExpiredReconnectQueries();

            await Assert.That(await probe.RelaysFrom(newSource, request)).IsTrue();
        }

        finally
        {
            foreach (Socket source in sources)
                source.Dispose();
        }
    }

    [Test]
    public async Task A_Server_Datagram_That_Is_Not_A_Reconnect_Reply_Is_Not_Relayed()
    {
        await using ForwarderProbe probe = new ();

        EndPoint upstream = await probe.ForwardsQueryFrom(probe.Client) ?? throw new InvalidOperationException("The Reconnect Query Was Not Forwarded");

        byte[] otherCommand = ReconnectQueryTests.Reply(240000);

        otherCommand[3] = 0x66;

        await probe.Server.SendToAsync(otherCommand, SocketFlags.None, upstream);
        await probe.Server.SendToAsync(new byte[ReconnectQuery.RequestLength], SocketFlags.None, upstream);

        await Assert.That(await TryReceive(probe.Client)).IsNull();

        // Neither Answered The Query, So It Is Still Pending And Its Reply Is Relayed
        await Assert.That(await probe.RelaysReply(upstream, ReconnectQueryTests.Reply(240000), probe.Client)).IsTrue();
    }

    [Test]
    public async Task A_Second_Reply_To_One_Reconnect_Query_Is_Not_Relayed()
    {
        await using ForwarderProbe probe = new ();

        EndPoint upstream = await probe.ForwardsQueryFrom(probe.Client) ?? throw new InvalidOperationException("The Reconnect Query Was Not Forwarded");

        byte[] reply = ReconnectQueryTests.Reply(240000);

        using (Assert.Multiple())
        {
            await Assert.That(await probe.RelaysReply(upstream, reply, probe.Client)).IsTrue();
            await Assert.That(await probe.RelaysReply(upstream, reply, probe.Client)).IsFalse();
        }
    }

    [Test]
    public async Task A_Reconnect_Reply_After_The_Query_Has_Expired_Is_Not_Relayed()
    {
        ControllableTimeProvider clock = new ();

        await using ForwarderProbe probe = new (clock);

        EndPoint upstream = await probe.ForwardsQueryFrom(probe.Client) ?? throw new InvalidOperationException("The Reconnect Query Was Not Forwarded");

        clock.Advance(UDPForwarder.PendingReconnectQueryTimeout);

        probe.Forwarder.EvictExpiredReconnectQueries();

        await Assert.That(await probe.RelaysReply(upstream, ReconnectQueryTests.Reply(240000), probe.Client)).IsFalse();
    }

    // Forged From The Player's Own Address, And More Than Both Bounds Admit Within One Window
    [Test]
    public async Task A_Flood_Of_Reconnect_Queries_Leaves_Players_Unaffected()
    {
        ControllableTimeProvider clock = new ();

        await using ForwarderProbe probe = new (clock);

        uint challenge = await probe.Establish();

        await Assert.That(await probe.Relays(GameDatagram(challenge, counter: 0))).IsTrue();

        int attackScoreBefore = probe.AttackIndicator.Score;
        int playerScoreBefore = probe.Scores.Score(probe.ClientEndPoint);
        long droppedBefore = probe.Forwarder.DroppedDatagramCount;

        int floodSize = UDPForwarder.ReconnectQueriesPerWindow + UDPForwarder.MaximumPendingReconnectQueries;

        List<Socket> forgers = new ();

        try
        {
            for (int index = 0; index < floodSize; index++)
            {
                Socket forger = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                forger.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                forgers.Add(forger);

                await forger.SendToAsync(ReconnectQueryTests.Request(), SocketFlags.None, probe.PublicEndPoint);
            }

            int forwarded = 0;

            while (await TryReceive(probe.Server) is not null)
                forwarded++;

            long dropped = probe.Forwarder.DroppedDatagramCount - droppedBefore;

            // Bounded Rather Than Exact For The Drops, Because A Forged Datagram Lost On The Way In Is Neither Forwarded Nor Dropped; The Flood Exceeds The Bounds By Enough That A Few Such Losses Cannot Leave Them Unreached
            using (Assert.Multiple())
            {
                await Assert.That(forwarded).IsEqualTo(Math.Min(UDPForwarder.ReconnectQueriesPerWindow, UDPForwarder.MaximumPendingReconnectQueries));
                await Assert.That(dropped).IsGreaterThan(0);
                await Assert.That(forwarded + dropped).IsLessThanOrEqualTo(floodSize);
                await Assert.That(probe.AttackIndicator.Score).IsEqualTo(attackScoreBefore);
                await Assert.That(probe.Scores.Score(probe.ClientEndPoint)).IsEqualTo(playerScoreBefore);
            }

            // The Player Still Relays, And A New Player From The Same Address Is Still Admitted, So The Flood Took No Session And No Share Of A Cap
            using Socket newPlayer = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            newPlayer.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            using (Assert.Multiple())
            {
                await Assert.That(await probe.Relays(GameDatagram(challenge, counter: 1))).IsTrue();
                await Assert.That(await probe.RelaysFrom(newPlayer, GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0))).IsTrue();
            }
        }

        finally
        {
            foreach (Socket forger in forgers)
                forger.Dispose();
        }
    }

    // Pinned As Literals For The Same Reason As The Timeouts Above: The Behavioural Tests Scale With Whatever These Say, So Only This Makes Widening One A Deliberate Act
    [Test]
    public async Task The_Reconnect_Query_Bounds_Are_The_Sizes_They_Were_Chosen_As()
    {
        int queriesPerWindow = UDPForwarder.ReconnectQueriesPerWindow;
        int maximumPendingQueries = UDPForwarder.MaximumPendingReconnectQueries;

        using (Assert.Multiple())
        {
            await Assert.That(queriesPerWindow).IsEqualTo(30);
            await Assert.That(UDPForwarder.ReconnectQueryWindow).IsEqualTo(TimeSpan.FromSeconds(1));
            await Assert.That(maximumPendingQueries).IsEqualTo(30);
            await Assert.That(UDPForwarder.PendingReconnectQueryTimeout).IsEqualTo(TimeSpan.FromSeconds(2));
        }
    }

    private static bool IsChallenge(byte[] datagram)
        => datagram.Length >= 58 && datagram[40] is 0xFF && datagram[41] is 0xFF && (datagram[42] & 0x40) is not 0 && datagram[43] is 0x00;

    private static uint ChallengeValue(byte[] datagram) => BinaryPrimitives.ReadUInt32LittleEndian(datagram.AsSpan(40 + 14));

    private static uint ChallengeTimestamp(byte[] datagram) => BinaryPrimitives.ReadUInt32LittleEndian(datagram.AsSpan(40 + 4));

    private static ushort ChallengePacketQuota(byte[] datagram) => BinaryPrimitives.ReadUInt16LittleEndian(datagram.AsSpan(40 + 10));

    private static ushort ChallengeGameCommandQuota(byte[] datagram) => BinaryPrimitives.ReadUInt16LittleEndian(datagram.AsSpan(40 + 12));

    private static async Task<byte[]> ReadOneChallengePacket(UDPForwarder forwarder, Socket client)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            (byte[] Payload, EndPoint Sender)? datagram = await TryReceive(client);

            if (datagram is not null && IsChallenge(datagram.Value.Payload))
                return datagram.Value.Payload;

            // Nothing Usable Arrived (Idle Timeout Or A Dropped Challenge); Re-Issue And Try Again
            forwarder.RotateChallenges();
        }

        throw new InvalidOperationException("No Challenge Packet Was Received");
    }

    private static async Task<uint> ReadOneChallengeValue(UDPForwarder forwarder, Socket client)
        => ChallengeValue(await ReadOneChallengePacket(forwarder, client));

    private static async Task<uint> ReadOneChallengeTimestamp(UDPForwarder forwarder, Socket client)
        => ChallengeTimestamp(await ReadOneChallengePacket(forwarder, client));

    // Opens A Session For A Source That Cannot Be Bound Here, Without A Datagram And So Without A Challenge; The Server Behind The Probe Never Answers One, So Nothing Is Sent To The Source, And Its Port Only Tells The Sessions Apart
    private static UDPForwarder.SessionAdmissionResult Admit(UDPForwarder forwarder, IPAddress address, int sessionIndex)
        => forwarder.TryGetOrCreateSession(new IPEndPoint(address, sessionIndex + 1), CancellationToken.None, out _, out _);

    private static async Task DrainUntilIdle(Socket socket)
    {
        while (await TryReceive(socket) is not null) { }
    }

    private static async Task<(byte[] Payload, EndPoint Sender)?> TryReceive(Socket socket)
    {
        byte[] buffer = new byte[65535];

        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);

        using CancellationTokenSource timeout = new (ReceiveTimeout);

        try
        {
            SocketReceiveFromResult result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, sender, timeout.Token);

            return (buffer[..result.ReceivedBytes], result.RemoteEndPoint);
        }

        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static int FreeUDPPort()
    {
        using Socket probe = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return probe.LocalEndPoint is IPEndPoint endpoint ? endpoint.Port : throw new InvalidOperationException("Could Not Determine A Free UDP Port");
    }

    private static byte[] GameDatagram(uint challenge, ushort counter)
    {
        byte[] datagram = new byte[ClientPacketReader.MinimumLength(ProxyForwarderKind.Game)];

        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(ClientPacketReader.ChallengeOffset), challenge);
        BinaryPrimitives.WriteUInt16LittleEndian(datagram.AsSpan(ClientPacketReader.CounterOffset), counter);

        return datagram;
    }

    /// <summary>
    ///     A forwarder on loopback with a server behind it and a client in front, so the tests above can assert what does and does not reach the server.
    ///     A negative assertion costs a receive timeout, so these tests are deliberately few and each asserts one branch of the pipeline.
    /// </summary>
    private sealed class ForwarderProbe : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new ();
        private readonly Task run;
        private readonly Socket server;
        private readonly Socket client;
        private readonly IPEndPoint publicEndPoint;

        /// <summary>
        ///     Supply <paramref name="timeProvider"/> to drive the forwarder's grace and idle timeouts from a test-controlled clock; the tests that assert real wall-clock behaviour omit it and get the system clock.
        ///     Supply <paramref name="kind"/> to probe a voice forwarder rather than a game one.
        /// </summary>
        internal ForwarderProbe(TimeProvider? timeProvider = null, ProxyForwarderKind kind = ProxyForwarderKind.Game)
        {
            TimeProvider clock = timeProvider ?? TimeProvider.System;

            int publicPort = FreeUDPPort();

            server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            server.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            int localPort = server.LocalEndPoint is IPEndPoint boundServer ? boundServer.Port : throw new InvalidOperationException("Could Not Determine The Bound UDP Port");

            Scores = new ViolationScoreContainer(clock);
            AttackIndicator = new AttackIndicatorContainer(clock);
            Forwarder = new UDPForwarder(publicPort, localPort, kind, TimeSpan.FromSeconds(10), Scores, AttackIndicator, clock, NullLogger.Instance);

            run = Forwarder.Run(lifetime.Token);

            client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            publicEndPoint = new IPEndPoint(IPAddress.Loopback, publicPort);
        }

        internal UDPForwarder Forwarder { get; }

        internal ViolationScoreContainer Scores { get; }

        internal AttackIndicatorContainer AttackIndicator { get; }

        internal ClientChallengeStoreDouble ClientChallenges { get; } = new ();

        internal Socket Server => server;

        internal Socket Client => client;

        internal IPEndPoint PublicEndPoint => publicEndPoint;

        internal IPEndPoint ClientEndPoint => client.LocalEndPoint is IPEndPoint boundClient ? boundClient : throw new InvalidOperationException("Could Not Determine The Client Endpoint");

        internal async Task<bool> Relays(byte[] datagram) => await RelaysFrom(client, datagram);

        internal async Task<bool> RelaysFrom(Socket senderSocket, byte[] datagram)
        {
            await senderSocket.SendToAsync(datagram, SocketFlags.None, publicEndPoint);

            return await TryReceive(server) is not null;
        }

        internal async Task<bool> Refuses(byte[] datagram) => await RefusesFrom(client, datagram);

        internal async Task<bool> RefusesFrom(Socket senderSocket, byte[] datagram)
        {
            long refusedBefore = Forwarder.DroppedDatagramCount;

            bool relayed = await RelaysFrom(senderSocket, datagram);

            return relayed is false && Forwarder.DroppedDatagramCount > refusedBefore;
        }

        /// <summary>
        ///     Sends the reconnect query from <paramref name="senderSocket"/>, and returns the endpoint the server received it from, or <see langword="null"/> when it did not arrive intact.
        /// </summary>
        internal async Task<EndPoint?> ForwardsQueryFrom(Socket senderSocket)
        {
            byte[] request = ReconnectQueryTests.Request();

            await senderSocket.SendToAsync(request, SocketFlags.None, publicEndPoint);

            (byte[] Payload, EndPoint Sender)? received = await TryReceive(server);

            return received is not null && received.Value.Payload.SequenceEqual(request) ? received.Value.Sender : null;
        }

        /// <summary>
        ///     Has the server send <paramref name="reply"/> to <paramref name="upstream"/>, and returns whether the next datagram <paramref name="senderSocket"/> receives is that reply, from the public port.
        /// </summary>
        internal async Task<bool> RelaysReply(EndPoint upstream, byte[] reply, Socket senderSocket)
        {
            await server.SendToAsync(reply, SocketFlags.None, upstream);

            (byte[] Payload, EndPoint Sender)? received = await TryReceive(senderSocket);

            return received is not null && received.Value.Payload.SequenceEqual(reply) && received.Value.Sender.Equals(publicEndPoint);
        }

        /// <summary>
        ///     Sends the reconnect query from <paramref name="senderSocket"/>, has the server answer it with <paramref name="reply"/>, and returns whether the answer is the first datagram to reach the sender.
        /// </summary>
        internal async Task<bool> AnswersQueryFrom(Socket senderSocket, byte[] reply)
            => await ForwardsQueryFrom(senderSocket) is EndPoint upstream && await RelaysReply(upstream, reply, senderSocket);

        /// <summary>
        ///     Establishes the session, which is what causes a challenge to be issued, and returns the challenge the forwarder issued.
        /// </summary>
        internal async Task<uint> Establish()
        {
            await Relays(GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0));

            byte[] packet = await ReadOneChallengePacket(Forwarder, client);

            ClientChallenges.TryProcessChallengePacket(publicEndPoint, packet);

            return ChallengeValue(packet);
        }

        /// <summary>
        ///     Reads one challenge packet emitted to the probe client and processes it through the client challenge store double.
        /// </summary>
        internal async Task<uint> ReceiveAndStoreChallenge()
        {
            byte[] packet = await ReadOneChallengePacket(Forwarder, client);

            ClientChallenges.TryProcessChallengePacket(publicEndPoint, packet);

            return ChallengeValue(packet);
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();

            // A Faulted Receive Loop Must Fail The Test Rather Than Be Swallowed: Every Assertion Here Is A Negative, And A Dead Loop Satisfies A Negative Just As Well As A Correct Refusal Does
            try { await run; }
            catch (OperationCanceledException) { }

            client.Dispose();
            Forwarder.Dispose();
            server.Dispose();
            lifetime.Dispose();
        }
    }
}
