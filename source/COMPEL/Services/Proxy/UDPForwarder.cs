namespace COMPEL.Services.Proxy;

/// <summary>
///     A bidirectional UDP relay for a single public port. It validates each client datagram's length, challenge and counter, scores abusive sources and drops them, and relays only what passes, while the server's replies are relayed back unexamined.
///     A dedicated upstream socket per client preserves the server's per-client addressing, mirroring how the native proxy mapped each public port to its local server port.
///     Heroes Of Newerth clients throttle their own traffic on the public (20000-29999) port range until the proxy authenticates them with a challenge, so the forwarder issues a challenge to each session on creation and renews it periodically.
/// </summary>
internal sealed class UDPForwarder : IDisposable
{
    private const int DatagramBufferSize = 65535;

    // The Challenge Packet's Leading Watermark Bytes, Which The Client Skips Before Reading The Control Payload: "WATERMARK_LEN_TOTAL" Plus "ENHANCED_WATERMARK_LEN_TOTAL"
    private const int WatermarkPrefixLength = 40;

    // Identifies A Proxy Control Packet ("PACKET_PROXY", Bit 6) And The Challenge Sub-Type Within It
    private const byte ProxyPacketFlag = 0x40;
    private const byte ChallengePacketType = 0x00;

    // The Window (Seconds) The Client Treats Itself As Authenticated After A Challenge
    private const ushort ChallengeExpirySeconds = 60;

    // No Reference "#define" To Cite: The Reference Bounds Its Own Maps With A Bare Literal Of A Thousand. Reporting Stops At The Bound Rather Than Clearing, Because Clearing Would Un-Throttle Every Source Already Reported And Turn A Flood Into A Log Flood
    private const int ReportedDropLimit = 1000;

    private readonly IPEndPoint serverEndPoint;
    private readonly ProxyForwarderKind kind;
    private readonly ViolationScoreContainer scoreContainer;
    private readonly ILogger logger;
    private readonly Socket frontSocket;
    private readonly ConcurrentDictionary<IPEndPoint, ClientSession> sessions = new ();
    private readonly ConcurrentDictionary<IPEndPoint, bool> reportedDrops = new ();
    private readonly Lock sessionsLock = new ();

    // The Sequence Behind Each Issued Challenge Value, Which Must Differ From The Previous One The Client Was Sent; The Timestamp Is Not Derived From It And Comes From The Clock Instead
    private long challengeSequence;

    private long lastIssuedTimestamp;

    private readonly ushort packetQuota;
    private readonly ushort gameCommandQuota;

    private long droppedDatagramCount;
    private int reportedDropCount;

    public int PublicPort { get; }

    public int LocalPort { get; }

    public long DroppedDatagramCount => Volatile.Read(ref droppedDatagramCount);

    public UDPForwarder(int publicPort, int localPort, ProxyForwarderKind kind, TimeSpan challengeRenewalInterval, ViolationScoreContainer scoreContainer, ILogger logger)
    {
        PublicPort = publicPort;
        LocalPort = localPort;
        serverEndPoint = new IPEndPoint(IPAddress.Loopback, localPort);
        this.kind = kind;
        this.scoreContainer = scoreContainer;
        this.logger = logger;

        // The Interval Is Not Stored: It Is Only Needed To Derive The Two Quotas, Which Are Fixed For The Life Of The Forwarder
        packetQuota = ChallengeQuota.ForKind(kind, challengeRenewalInterval);
        gameCommandQuota = ChallengeQuota.GameCommandForInterval(challengeRenewalInterval);

        frontSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        DisableConnectionResetReporting(frontSocket);
        frontSocket.Bind(new IPEndPoint(IPAddress.Any, publicPort));
    }

    // On Windows A UDP Socket Reports A Received ICMP Port-Unreachable As A Connection-Reset Error On The Next Socket Operation
    // Disabling It (SIO_UDP_CONNRESET) Stops A Momentarily-Down Server From Killing The Relay With Spurious Exceptions
    // The Control Code Does Not Exist On Other Platforms
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
            catch (Exception exception)
            {
                // Counted, Charged And Throttled Like Any Other Refusal. Unthrottled And Unscored, This Path Disabled The Abuse Protection For New Sources At Exactly The Moment The Proxy Was Being Exhausted, And Logged Once Per Datagram
                // Only The Arrival Is Charged And No Violation Weight, Because A Session Can Fail To Open For Reasons That Are Not The Client's Fault
                scoreContainer.ChargeArrival(client);

                if (Drop(client, "Session Creation Failed"))
                    logger.LogDebug(exception, "Failed To Create Proxy Session For {Client}", client);

                continue;
            }

            // Authenticate A New Client Immediately So It Does Not Exhaust Its Unauthenticated Packet Budget Waiting For The First Periodic Renewal
            if (created)
                SendChallenge(client, session);

            session.Touch();

            // Every Datagram Costs Its Source, Whatever It Turns Out To Contain, Which Is What The Drain Rate Is Calibrated Against; The Reference Does This First As Well
            scoreContainer.ChargeArrival(client);

            // A Source Already Over The Threshold Is Refused Before Any Field Of Its Datagram Is Read. By This Point The Session Exists, Its Challenge Has Been Issued And Its Idle Timer Has Been Refreshed, Because A Client Must Be Challenged Before It Can Send Anything Valid
            if (scoreContainer.IsWithinAllowance(client) is false)
            {
                Drop(client, "Actioned");

                continue;
            }

            ReadOnlySpan<byte> datagram = buffer.AsSpan(0, result.ReceivedBytes);

            // The Length Guard Runs Before Any Field Is Read So None Is Ever Read Out Of Range
            if (ClientPacketReader.TryRead(datagram, kind, out uint challenge, out ushort counter) is false)
            {
                Drop(client, ViolationScoreContainer.TooShortViolationWeight, "Too Short");

                continue;
            }

            ChallengeWindow? window = session.Challenges.Match(challenge);

            // A Non-Zero Challenge This Session Never Issued Or No Longer Retains. The Reference Treats This Separately From A Client That Has Not Been Challenged Yet, Which Echoes Zero And Matches The Session's Unauthenticated Window
            if (window is null)
            {
                // Refused Either Way, So This Is Not A Relay Path; What The Grace Suppresses Is Only The Violation Weight, Which At Ordinary Game Rates Would Cross The Threshold In Well Under A Second
                // TODO: The Reference Keys Its Retained Challenges On The Challenge Value Globally With A Per-Address Inner Map, So A Client Whose Source Port Changes Is Matched Immediately And Never Refused At All; Holding Them Per Forwarder Rather Than Per Session Would Remove The Need For This Grace
                if (session.IsWithinUnknownChallengeGrace)
                    Drop(client, "Unknown Challenge Within Grace");

                else
                    Drop(client, ViolationScoreContainer.ChallengeViolationWeight, "Unknown Challenge");

                continue;
            }

            // The Quota Is Checked Before The Counter Indexes The Seen Set, Because The Counter Arrives From The Client
            if (window.TryAdmit(counter, out ChallengeAdmission admission) is false)
            {
                // Constant Reasons Rather Than "admission.ToString()", Which Would Allocate On Every Dropped Datagram Whether Or Not The Drop Is Logged, And A Flood Is Made Entirely Of Dropped Datagrams
                if (admission is ChallengeAdmission.Duplicate)
                    Drop(client, ViolationScoreContainer.DuplicateViolationWeight, "Duplicate");

                // A Client That Has Not Accepted A Challenge Yet Is Held To A Small Total Rather Than A Rate, And The Reference Weights Exceeding That Total More Heavily Than An Ordinary Rate Limit
                else if (challenge is SessionChallengeState.UnauthenticatedChallenge)
                    Drop(client, ViolationScoreContainer.UnauthenticatedViolationWeight, "Unauthenticated");

                else
                    Drop(client, ViolationScoreContainer.RateLimitViolationWeight, "Over Quota");

                continue;
            }

            // A Matched Non-Zero Challenge Means The Client Has Demonstrably Accepted One Of Ours, So It No Longer Needs The Benefit Of The Doubt
            // Matching Only The Unauthenticated Window Proves Nothing, Because A Client That Has Accepted No Challenge At All Echoes Zero
            if (challenge is not SessionChallengeState.UnauthenticatedChallenge)
                session.MarkAuthenticated();

            try { await session.UpstreamSocket.SendAsync(buffer.AsMemory(0, result.ReceivedBytes), SocketFlags.None, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception exception) { logger.LogDebug(exception, "Failed To Forward Datagram To Server For {Client}", client); }
        }
    }

    /// <summary>
    ///     Issues a fresh challenge to every active session, which is what resets each client's packet counter for the next window.
    /// </summary>
    public void RotateChallenges()
    {
        foreach (KeyValuePair<IPEndPoint, ClientSession> pair in sessions)
            SendChallenge(pair.Key, pair.Value);
    }

    /// <summary>
    ///     Re-sends each session's current challenge without issuing a new one.
    ///     The reference does this about once a second, so that a single lost challenge datagram cannot leave a client holding an allowance sized for less time than it must now cover.
    ///     A client ignores a repeat of the challenge it already holds, so this is free of side effects for one that received the original.
    /// </summary>
    public void RepeatChallenges()
    {
        foreach (KeyValuePair<IPEndPoint, ClientSession> pair in sessions)
        {
            // Only A Session That Has Authenticated Needs This: The Repeat Exists For A Client That Lost A Rotation Challenge Mid-Window, Which Is Authenticated By Definition
            // Repeating To Every Session Instead Turns One Spoofed Datagram Into A Challenge Every Second Aimed At Whatever Address It Named, For As Long As The Session Lives
            // TODO: The Session Table Itself Has No Cap, So A Spoofed-Source Flood Still Buys A Socket, A Pump Task And A Two-Minute Lifetime Per Datagram; The Reference Bounds This With "MAX_GAME_CONNECTIONS" And "MAX_GAME_CONNECTIONS_PER_IP", Which COMPEL Would Need A Policy For
            if (pair.Value.HasAuthenticated is false)
                continue;

            if (pair.Value.Challenges.Current is ChallengeWindow current)
                TransmitChallenge(pair.Key, current.Challenge);
        }
    }

    private void SendChallenge(IPEndPoint client, ClientSession session)
    {
        uint sequence = unchecked((uint)Interlocked.Increment(ref challengeSequence));

        // The Value Must Be Non-Zero, As Zero Marks An Unauthenticated Session On The Client; Skip It On The Rare Wrap-Around
        if (sequence is 0)
            sequence = unchecked((uint)Interlocked.Increment(ref challengeSequence));

        // Rotation Must Happen Before The Challenge Is Sent, So A Reply Arriving The Instant After Send Is Already Matched
        session.Challenges.Rotate(sequence, packetQuota);

        TransmitChallenge(client, sequence);
    }

    private void TransmitChallenge(IPEndPoint client, uint challenge)
    {
        // The Client's Gate Is A Strict Comparison It Never Re-Bases, So A Clock That Steps Backwards Would Have It Reject Every Replacement Until The Clock Caught Up
        // Never Issuing A Timestamp Below The Last One Keeps The Sequence Monotonic Across A Backwards Step While Still Being Wall-Clock Derived, So A Restart Still Issues A Greater Value Than A Previous Run
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long monotonic = Math.Max(Interlocked.Read(ref lastIssuedTimestamp) + 1, now);

        Interlocked.Exchange(ref lastIssuedTimestamp, monotonic);

        byte[] packet = BuildChallengePacket((uint)monotonic, challenge);

        // The Challenge Must Originate From This (Front) Socket So Its Source Address And Port Match The Endpoint The Client Sends Its Game Traffic To, Which Is How The Client Keys The Authenticated Session
        try { frontSocket.SendTo(packet, SocketFlags.None, client); }
        catch (Exception exception) { logger.LogDebug(exception, "Failed To Send Challenge To {Client}", client); }
    }

    private byte[] BuildChallengePacket(uint serverCreationTimestamp, uint value)
    {
        byte[] packet = new byte[WatermarkPrefixLength + 18];

        // The First Forty Bytes Are The Watermark Prefix The Client Skips Unread And Are Left Zeroed
        Span<byte> payload = packet.AsSpan(WatermarkPrefixLength);

        payload[0] = 0xFF;
        payload[1] = 0xFF;
        payload[2] = ProxyPacketFlag;
        payload[3] = ChallengePacketType;

        BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], serverCreationTimestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[8..], ChallengeExpirySeconds);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[10..], packetQuota);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[12..], gameCommandQuota);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[14..], value);

        return packet;
    }

    private void Drop(IPEndPoint client, int weight, string reason)
    {
        scoreContainer.ChargeViolation(client, weight);

        Drop(client, reason);
    }

    /// <summary>
    ///     Counts a refused datagram and logs the reason once per source endpoint, so that a flood from a single source cannot become a log flood.
    ///     Returns whether this was the first refusal reported for the source, so a caller with more detail can log it under the same throttle.
    /// </summary>
    private bool Drop(IPEndPoint client, string reason)
    {
        Interlocked.Increment(ref droppedDatagramCount);

        if (Volatile.Read(ref reportedDropCount) >= ReportedDropLimit || reportedDrops.TryAdd(client, true) is false)
            return false;

        Interlocked.Increment(ref reportedDropCount);

        logger.LogWarning("Dropped A Datagram From {Client} On Public Port {Port} ({Reason}); Further Drops From This Source Are Not Logged", client, PublicPort, reason);

        return true;
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

                // A Connected UDP Socket Surfaces An ICMP Port-Unreachable (For Example While The Server Instance Is Briefly Down Between Restarts) As A Connection-Reset Or Connection-Refused Socket Error
                // This Is Transient, So The Pump Keeps Running Rather Than Tearing The Session Down And Leaving The Client Permanently Unable To Receive Server Traffic
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
            // Remove This Session So The Client's Next Datagram Transparently Creates A Fresh One, Rather Than Reusing A Pump That Has Stopped Relaying
            // The Reference Check Ensures A Newer Session For The Same Client (Created After A Race With Eviction) Is Never Removed
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

        // Sweep Under The Same Lock That Guards Session Creation So An Idle Session Can Never Be Removed And Disposed While A Datagram For The Same Client Is Concurrently Creating A Replacement, Which Would Otherwise Leak Whichever Session Lost The Race
        lock (sessionsLock)
        {
            foreach (KeyValuePair<IPEndPoint, ClientSession> pair in sessions)
            {
                if (Volatile.Read(ref pair.Value.LastActivityTicks) > cutoff)
                    continue;

                if (sessions.TryRemove(pair.Key, out ClientSession? removed))
                {
                    removed.Dispose();

                    if (reportedDrops.TryRemove(pair.Key, out _))
                        Interlocked.Decrement(ref reportedDropCount);
                }
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

        // Rotation And Matching Live In "SessionChallengeState" So The Renewal Grace Is Testable Outside This Private Class
        public SessionChallengeState Challenges { get; } = new ();

        public long LastActivityTicks;

        // A Session That Has Never Had A Datagram Admitted May Belong To A Client Still Echoing A Challenge Issued To An Earlier Session, Because The Client Keys Its Challenge On Our Public Port Rather Than Its Own Source Port
        // The Grace Is Bounded Both Ways: It Ends At The First Admitted Datagram, And It Expires Regardless, So A Source That Never Authenticates Does Not Keep It
        private static readonly long UnknownChallengeGraceMilliseconds = (long)TimeSpan.FromSeconds(30).TotalMilliseconds;

        private readonly long createdTicks = Environment.TickCount64;

        private bool authenticated;

        private int disposed;

        public ClientSession(Socket upstreamSocket, CancellationToken stoppingToken)
        {
            UpstreamSocket = upstreamSocket;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            Touch();
        }

        public bool HasAuthenticated => Volatile.Read(ref authenticated);

        public bool IsWithinUnknownChallengeGrace
            => HasAuthenticated is false && Environment.TickCount64 - createdTicks < UnknownChallengeGraceMilliseconds;

        public void MarkAuthenticated()
        {
            if (Volatile.Read(ref authenticated) is false)
                Volatile.Write(ref authenticated, true);
        }

        public void Touch() => Volatile.Write(ref LastActivityTicks, Environment.TickCount64);

        public void Dispose()
        {
            // Idempotent: The Recycle, Eviction, And Shutdown Paths Can All Reach A Session, So Disposal Runs Exactly Once Rather Than Cancelling An Already-Disposed Token Source
            if (Interlocked.Exchange(ref disposed, 1) is not 0)
                return;

            // Cancel First So The Server-To-Client Pump Stops Awaiting Its Socket Before It Is Torn Down
            Cancellation.Cancel();
            Cancellation.Dispose();
            UpstreamSocket.Dispose();
        }
    }
}
