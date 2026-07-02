namespace COMPEL.Services.Proxy;

/// <summary>
///     A bidirectional UDP relay for a single public port. Datagrams from a client are forwarded to the local server port; the server's replies are relayed back to the originating client.
///     A dedicated upstream socket per client preserves the server's per-client addressing, mirroring how the native proxy mapped each public port to its local server port.
///     Heroes Of Newerth clients throttle their own traffic on the public (20000-29999) port range until the proxy authenticates them with a challenge, so the forwarder issues a challenge to each session on creation and renews it periodically.
/// </summary>
internal sealed class UDPForwarder : IDisposable
{
    private const int DatagramBufferSize = 65535;

    // The Challenge Packet's Leading Watermark Bytes, Which The Client Skips Before Reading The Control Payload: WATERMARK_LEN_TOTAL (20) Plus ENHANCED_WATERMARK_LEN_TOTAL (20).
    private const int WatermarkPrefixLength = 40;

    // Identifies A Proxy Control Packet (PACKET_PROXY, Bit 6) And The Challenge Sub-Type Within It.
    private const byte ProxyPacketFlag = 0x40;
    private const byte ChallengePacketType = 0x00;

    // The Window (Seconds) The Client Treats Itself As Authenticated After A Challenge, And The Per-Challenge Packet Counters It Is Granted. The Counters Are Maximised Because The Proxy Does Not Perform The Native Build's Rate-Based Cheat Detection; Renewal Well Within The Window Keeps The Client Authenticated Continuously.
    private const ushort ChallengeExpirySeconds = 60;
    private const ushort ChallengeMaximumCounter = ushort.MaxValue;
    private const ushort ChallengeMaximumGameCommandCounter = ushort.MaxValue;

    private readonly IPEndPoint serverEndPoint;
    private readonly ILogger logger;
    private readonly Socket frontSocket;
    private readonly ConcurrentDictionary<IPEndPoint, ClientSession> sessions = new ();
    private readonly Lock sessionsLock = new ();

    // The Client Accepts A Renewed Challenge Only When Its Value Differs From The Previous One And Its Timestamp Is Strictly Greater, So A Single Monotonically-Increasing Sequence Drives Both Fields.
    private long challengeSequence;

    public int PublicPort { get; }

    public int LocalPort { get; }

    public UDPForwarder(int publicPort, int localPort, ILogger logger)
    {
        PublicPort = publicPort;
        LocalPort = localPort;
        serverEndPoint = new IPEndPoint(IPAddress.Loopback, localPort);
        this.logger = logger;

        frontSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        DisableConnectionResetReporting(frontSocket);
        frontSocket.Bind(new IPEndPoint(IPAddress.Any, publicPort));
    }

    // On Windows A UDP Socket Reports A Received ICMP Port-Unreachable As A Connection-Reset Error On The Next Socket Operation. Disabling It (SIO_UDP_CONNRESET) Stops A Momentarily-Down Server From Killing The Relay With Spurious Exceptions. The Control Code Does Not Exist On Other Platforms.
    private static void DisableConnectionResetReporting(Socket socket)
    {
        if (OperatingSystem.IsWindows() is false)
            return;

        const int windowsUDPConnectionResetControlCode = unchecked((int)0x9800000C);

        socket.IOControl(windowsUDPConnectionResetControlCode, [ 0x00, 0x00, 0x00, 0x00 ], null);
    }

    public async Task Run(CancellationToken stoppingToken)
    {
        byte[] buffer = new byte[DatagramBufferSize];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);

        while (stoppingToken.IsCancellationRequested is false)
        {
            SocketReceiveFromResult result;

            try { result = await frontSocket.ReceiveFromAsync(buffer, SocketFlags.None, any, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException exception) { logger.LogDebug(exception, "Front Receive Failed On Port {Port}", PublicPort); continue; }

            IPEndPoint client = (IPEndPoint)result.RemoteEndPoint;

            ClientSession session;
            bool created;

            try { session = GetOrCreateSession(client, stoppingToken, out created); }
            catch (Exception exception) { logger.LogDebug(exception, "Failed To Create Proxy Session For {Client}", client); continue; }

            // Authenticate A New Client Immediately So It Does Not Exhaust Its Unauthenticated Packet Budget Waiting For The First Periodic Renewal.
            if (created)
                SendChallenge(client);

            session.Touch();

            try { await session.UpstreamSocket.SendAsync(buffer.AsMemory(0, result.ReceivedBytes), SocketFlags.None, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception exception) { logger.LogDebug(exception, "Failed To Forward Datagram To Server For {Client}", client); }
        }
    }

    /// <summary>
    ///     Sends a fresh challenge to every active session, renewing their authentication before the client's window lapses.
    /// </summary>
    public void ChallengeActiveSessions()
    {
        foreach (IPEndPoint client in sessions.Keys)
            SendChallenge(client);
    }

    private void SendChallenge(IPEndPoint client)
    {
        uint sequence = unchecked((uint)Interlocked.Increment(ref challengeSequence));

        // The Value Must Be Non-Zero, As Zero Marks An Unauthenticated Session On The Client; Skip It On The Rare Wrap-Around.
        if (sequence is 0)
            sequence = unchecked((uint)Interlocked.Increment(ref challengeSequence));

        byte[] packet = BuildChallengePacket(sequence, sequence);

        // The Challenge Must Originate From This (Front) Socket So Its Source Address And Port Match The Endpoint The Client Sends Its Game Traffic To, Which Is How The Client Keys The Authenticated Session.
        try { frontSocket.SendTo(packet, SocketFlags.None, client); }
        catch (Exception exception) { logger.LogDebug(exception, "Failed To Send Challenge To {Client}", client); }
    }

    private static byte[] BuildChallengePacket(uint serverCreationTimestamp, uint value)
    {
        byte[] packet = new byte[WatermarkPrefixLength + 18];

        // The First Forty Bytes Are The Watermark Prefix The Client Skips Unread And Are Left Zeroed.
        Span<byte> payload = packet.AsSpan(WatermarkPrefixLength);

        payload[0] = 0xFF;
        payload[1] = 0xFF;
        payload[2] = ProxyPacketFlag;
        payload[3] = ChallengePacketType;

        BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], serverCreationTimestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[8..], ChallengeExpirySeconds);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[10..], ChallengeMaximumCounter);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[12..], ChallengeMaximumGameCommandCounter);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[14..], value);

        return packet;
    }

    private ClientSession GetOrCreateSession(IPEndPoint client, CancellationToken stoppingToken, out bool created)
    {
        if (sessions.TryGetValue(client, out ClientSession? existing))
        {
            created = false;

            return existing;
        }

        lock (sessionsLock)
        {
            if (sessions.TryGetValue(client, out existing))
            {
                created = false;

                return existing;
            }

            Socket upstreamSocket = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            DisableConnectionResetReporting(upstreamSocket);
            upstreamSocket.Connect(serverEndPoint);

            ClientSession session = new (upstreamSocket, stoppingToken);
            sessions[client] = session;

            _ = PumpServerToClient(client, session);

            created = true;

            return session;
        }
    }

    private async Task PumpServerToClient(IPEndPoint client, ClientSession session)
    {
        byte[] buffer = new byte[DatagramBufferSize];
        CancellationToken cancellationToken = session.Cancellation.Token;

        try
        {
            while (cancellationToken.IsCancellationRequested is false)
            {
                int received;

                try
                {
                    received = await session.UpstreamSocket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                }

                // A Connected UDP Socket Surfaces An ICMP Port-Unreachable (For Example While The Server Instance Is Briefly Down Between Restarts) As A Connection-Reset Or Connection-Refused Socket Error. This Is Transient, So The Pump Keeps Running Rather Than Tearing The Session Down And Leaving The Client Permanently Unable To Receive Server Traffic.
                catch (SocketException exception) when (exception.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
                {
                    continue;
                }

                if (received is 0)
                    continue;

                session.Touch();

                await frontSocket.SendToAsync(buffer.AsMemory(0, received), SocketFlags.None, client, cancellationToken).ConfigureAwait(false);
            }
        }

        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception exception) { logger.LogDebug(exception, "Server-To-Client Relay Ended For {Client}", client); }

        finally
        {
            // Remove This Session So The Client's Next Datagram Transparently Creates A Fresh One, Rather Than Reusing A Pump That Has Stopped Relaying. The Reference Check Ensures A Newer Session For The Same Client (Created After A Race With Eviction) Is Never Removed.
            lock (sessionsLock)
            {
                if (sessions.TryGetValue(client, out ClientSession? current) && ReferenceEquals(current, session))
                    if (sessions.TryRemove(client, out ClientSession? removed))
                        removed.Dispose();
            }
        }
    }

    public void EvictIdleSessions(TimeSpan idleTimeout)
    {
        long cutoff = Environment.TickCount64 - (long)idleTimeout.TotalMilliseconds;

        // Sweep Under The Same Lock That Guards Session Creation So An Idle Session Can Never Be Removed And Disposed While A Datagram For The Same Client Is Concurrently Creating A Replacement, Which Would Otherwise Leak Whichever Session Lost The Race.
        lock (sessionsLock)
        {
            foreach (KeyValuePair<IPEndPoint, ClientSession> pair in sessions)
            {
                if (Volatile.Read(ref pair.Value.LastActivityTicks) > cutoff)
                    continue;

                if (sessions.TryRemove(pair.Key, out ClientSession? removed))
                    removed.Dispose();
            }
        }
    }

    public void Dispose()
    {
        frontSocket.Dispose();

        foreach (ClientSession session in sessions.Values)
            session.Dispose();

        sessions.Clear();
    }

    private sealed class ClientSession : IDisposable
    {
        public Socket UpstreamSocket { get; }

        public CancellationTokenSource Cancellation { get; }

        public long LastActivityTicks;

        private int disposed;

        public ClientSession(Socket upstreamSocket, CancellationToken stoppingToken)
        {
            UpstreamSocket = upstreamSocket;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            Touch();
        }

        public void Touch() => Volatile.Write(ref LastActivityTicks, Environment.TickCount64);

        public void Dispose()
        {
            // Idempotent: The Recycle, Eviction, And Shutdown Paths Can All Reach A Session, So Disposal Runs Exactly Once Rather Than Cancelling An Already-Disposed Token Source.
            if (Interlocked.Exchange(ref disposed, 1) is not 0)
                return;

            // Cancel First So The Server-To-Client Pump Stops Awaiting Its Socket Before It Is Torn Down.
            Cancellation.Cancel();
            Cancellation.Dispose();
            UpstreamSocket.Dispose();
        }
    }
}
