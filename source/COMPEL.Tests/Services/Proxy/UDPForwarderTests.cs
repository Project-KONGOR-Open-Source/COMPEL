namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Exercises the proxy forwarder over loopback: that a valid datagram is relayed to the server and back, that a datagram too short to validate is dropped instead, that a client is issued a challenge, that each renewal carries a strictly greater value, that a repeated counter under one challenge is dropped as a duplicate, that a counter at the quota is dropped, that a challenge the proxy never issued is dropped and charged, that an unauthenticated counter at the total is dropped, and that an actioned source is refused whatever it sends and before its datagram is otherwise read.
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

        using UDPForwarder forwarder = new (publicPort, localPort, ProxyForwarderKind.Game, TimeSpan.FromSeconds(10), container, NullLogger.Instance);

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

                forwarder.ChallengeActiveSessions();

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
    public async Task Each_Challenge_Renewal_Carries_A_Strictly_Greater_Value()
    {
        int publicPort = FreeUDPPort();
        int localPort = FreeUDPPort();

        ViolationScoreContainer container = new (TimeProvider.System);

        using UDPForwarder forwarder = new (publicPort, localPort, ProxyForwarderKind.Game, TimeSpan.FromSeconds(10), container, NullLogger.Instance);

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

            forwarder.ChallengeActiveSessions();
            uint firstValue = await ReadOneChallengeValue(forwarder, client);

            forwarder.ChallengeActiveSessions();
            uint secondValue = await ReadOneChallengeValue(forwarder, client);

            using (Assert.Multiple())
            {
                await Assert.That(firstValue).IsNotEqualTo((uint)0);
                await Assert.That(secondValue > firstValue).IsTrue();
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

    // A Challenge The Proxy Never Issued Is Not The Same As A Client That Has Not Been Challenged Yet: The Reference Charges It Separately And Drops It, Rather Than Admitting It Under The Unauthenticated Allowance
    [Test]
    public async Task A_Challenge_The_Proxy_Never_Issued_Is_Dropped()
    {
        await using ForwarderProbe probe = new ();

        await probe.Establish();

        int scoreBefore = probe.Scores.Score(probe.ClientEndPoint);

        await Assert.That(await probe.Refuses(GameDatagram(challenge: 0xDEADBEEF, counter: 1))).IsTrue();

        // The Forwarder Must Charge The Violation, Not Merely Drop It: Without The Charge, A Source Below The Expected Rate Never Becomes Actioned And The Recovery-Depends-On-Rate Contract Silently Stops Holding
        await Assert.That(probe.Scores.Score(probe.ClientEndPoint)).IsGreaterThanOrEqualTo(scoreBefore + ViolationScoreContainer.ChallengeViolationWeight);
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

        // Driven Over The Threshold Directly, So This Test Is About The Ordering Rather Than About Accumulating A Score
        while (probe.Scores.IsWithinAllowance(probe.ClientEndPoint))
            probe.Scores.ChargeViolation(probe.ClientEndPoint, ViolationScoreContainer.TooShortViolationWeight);

        await Assert.That(await probe.Refuses(GameDatagram(challenge, counter: 1))).IsTrue();
    }

    // Pins The Order Rather Than The Outcome: An Actioned Source's Short Datagram Must Be Refused By The Allowance Check Before The Length Guard Can Charge It
    [Test]
    public async Task An_Actioned_Source_Is_Refused_Before_The_Length_Guard_Charges_It()
    {
        await using ForwarderProbe probe = new ();

        await probe.Establish();

        while (probe.Scores.IsWithinAllowance(probe.ClientEndPoint))
            probe.Scores.ChargeViolation(probe.ClientEndPoint, ViolationScoreContainer.RateLimitViolationWeight);

        int scoreBefore = probe.Scores.Score(probe.ClientEndPoint);

        await Assert.That(await probe.Refuses(new byte[8])).IsTrue();

        // Exactly The Arrival And Nothing Else: If The Arrival Were Charged After The Allowance Check The Score Would Not Move, And If The Length Guard Ran First It Would Rise By "TooShortViolationWeight" As Well
        await Assert.That(probe.Scores.Score(probe.ClientEndPoint)).IsEqualTo(scoreBefore + ViolationScoreContainer.PacketScore);
    }

    private static bool IsChallenge(byte[] datagram)
        => datagram.Length >= 58 && datagram[40] is 0xFF && datagram[41] is 0xFF && (datagram[42] & 0x40) is not 0 && datagram[43] is 0x00;

    private static uint ChallengeValue(byte[] datagram) => BinaryPrimitives.ReadUInt32LittleEndian(datagram.AsSpan(40 + 14));

    private static async Task<uint> ReadOneChallengeValue(UDPForwarder forwarder, Socket client)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            (byte[] Payload, EndPoint Sender)? datagram = await TryReceive(client);

            if (datagram is not null && IsChallenge(datagram.Value.Payload))
                return ChallengeValue(datagram.Value.Payload);

            // Nothing Usable Arrived (Idle Timeout Or A Dropped Challenge); Re-Issue And Try Again
            forwarder.ChallengeActiveSessions();
        }

        throw new InvalidOperationException("No Challenge Packet Was Received");
    }

    private static async Task DrainUntilIdle(Socket socket)
    {
        while (await TryReceive(socket) is not null)
        {
        }
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

        internal ForwarderProbe()
        {
            int publicPort = FreeUDPPort();

            server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            server.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            int localPort = server.LocalEndPoint is IPEndPoint boundServer ? boundServer.Port : throw new InvalidOperationException("Could Not Determine The Bound UDP Port");

            Scores = new ViolationScoreContainer(TimeProvider.System);
            Forwarder = new UDPForwarder(publicPort, localPort, ProxyForwarderKind.Game, TimeSpan.FromSeconds(10), Scores, NullLogger.Instance);

            run = Forwarder.Run(lifetime.Token);

            client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            publicEndPoint = new IPEndPoint(IPAddress.Loopback, publicPort);
        }

        internal UDPForwarder Forwarder { get; }

        internal ViolationScoreContainer Scores { get; }

        internal IPEndPoint ClientEndPoint => client.LocalEndPoint is IPEndPoint boundClient ? boundClient : throw new InvalidOperationException("Could Not Determine The Client Endpoint");

        /// <summary>
        ///     Sends the datagram and reports whether it reached the server.
        /// </summary>
        internal async Task<bool> Relays(byte[] datagram)
        {
            await client.SendToAsync(datagram, SocketFlags.None, publicEndPoint);

            return await TryReceive(server) is not null;
        }

        /// <summary>
        ///     Sends the datagram and reports whether the forwarder refused it: that it did not reach the server, and that the forwarder counted a refusal.
        ///     Both halves matter, because a receive timeout on its own is also what a dead receive loop looks like.
        /// </summary>
        internal async Task<bool> Refuses(byte[] datagram)
        {
            int refusedBefore = Forwarder.DroppedDatagramCount;

            bool relayed = await Relays(datagram);

            return relayed is false && Forwarder.DroppedDatagramCount > refusedBefore;
        }

        /// <summary>
        ///     Establishes the session, which is what causes a challenge to be issued, and returns the challenge the forwarder issued.
        /// </summary>
        internal async Task<uint> Establish()
        {
            await Relays(GameDatagram(SessionChallengeState.UnauthenticatedChallenge, counter: 0));

            return await ReadOneChallengeValue(Forwarder, client);
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
