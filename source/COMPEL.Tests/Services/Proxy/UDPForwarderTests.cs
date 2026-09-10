namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Exercises the proxy forwarder over loopback: that a valid datagram is relayed to the server and back, that a datagram too short to validate is dropped instead, that a client is issued a challenge, and that each renewal carries a strictly greater value.
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
        // "PublicPort" Is Whatever Was Passed In Rather Than The Port The Socket Ended Up Bound To, So It Must Be A Real Port Chosen Up Front, Exactly As The Other Tests In This File Do
        int publicPort = FreeUDPPort();

        ViolationScoreContainer container = new (TimeProvider.System);

        using Socket server = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        // The Null-Forgiving Operator Is Banned, And This File Already Has The Idiom For This
        int localPort = server.LocalEndPoint is IPEndPoint bound ? bound.Port : throw new InvalidOperationException("Could Not Determine The Bound UDP Port");

        using UDPForwarder forwarder = new (publicPort, localPort, ProxyForwarderKind.Game, TimeSpan.FromSeconds(10), container, NullLogger.Instance);

        using CancellationTokenSource cancellation = new (TimeSpan.FromSeconds(5));

        _ = forwarder.Run(cancellation.Token);

        using Socket client = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.SendTo(new byte[8], new IPEndPoint(IPAddress.Loopback, publicPort));

        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellation.Token);

        using (Assert.Multiple())
        {
            await Assert.That(server.Available).IsEqualTo(0);
            await Assert.That(forwarder.DroppedDatagramCount).IsGreaterThan(0);
        }
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
}
