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

    // MAX_GAME_CONNECTIONS / MAX_VOICE_CONNECTIONS
    private const int MaxSessionsPerForwarder = 24;

    // MAX_GAME_CONNECTIONS_PER_IP / MAX_VOICE_CONNECTIONS_PER_IP
    private const int MaxSessionsPerAddress = 10;

    // A Session That Has Never Had A Datagram Admitted May Belong To A Client Still Echoing A Challenge Issued To An Earlier Session, Because The Client Keys Its Challenge On Our Public Port Rather Than Its Own Source Port
    // The Grace Is Bounded Both Ways: It Ends At The First Admitted Datagram, And It Expires Regardless, So A Source That Never Authenticates Does Not Keep It
    internal static readonly TimeSpan UnknownChallengeGrace = TimeSpan.FromSeconds(30);

    private readonly IPEndPoint serverEndPoint;
    private readonly ProxyForwarderKind kind;
    private readonly ViolationScoreContainer scoreContainer;
    private readonly AttackIndicatorContainer attackIndicator;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly Socket frontSocket;
    private readonly ConcurrentDictionary<IPEndPoint, ClientSession> sessions = new ();
    private readonly ConcurrentDictionary<IPEndPoint, bool> reportedDrops = new ();
    private readonly SessionChallengeState challenges = new ();
    private readonly Lock sessionsLock = new ();
    private readonly ushort packetQuota;
    private readonly ushort gameCommandQuota;

    private long droppedDatagramCount;
    private int reportedDropCount;

    public int PublicPort { get; }

    public int LocalPort { get; }

    public long DroppedDatagramCount => Volatile.Read(ref droppedDatagramCount);

    public UDPForwarder(int publicPort, int localPort, ProxyForwarderKind kind, TimeSpan challengeRenewalInterval, ViolationScoreContainer scoreContainer, AttackIndicatorContainer attackIndicator, TimeProvider timeProvider, ILogger logger)
    {
        PublicPort = publicPort;
        LocalPort = localPort;
        serverEndPoint = new IPEndPoint(IPAddress.Loopback, localPort);
        this.kind = kind;
        this.scoreContainer = scoreContainer;
        this.attackIndicator = attackIndicator;
        this.timeProvider = timeProvider;
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

            SessionAdmissionResult admissionResult = TryGetOrCreateSession(client, stoppingToken, out ClientSession? session, out bool created);

            if (admissionResult is not SessionAdmissionResult.Admitted || session is null)
            {
                // Counted, Charged And Throttled Like Any Other Refusal. Unthrottled And Unscored, This Path Disabled The Abuse Protection For New Sources At Exactly The Moment The Proxy Was Being Exhausted, And Logged Once Per Datagram
                // Only The Arrival Is Charged And No Violation Weight, Because A Session Can Fail To Open For Reasons That Are Not The Client's Fault
                scoreContainer.ChargeArrival(client);

                if (Drop(client, "Session Creation Failed"))
                    logger.LogDebug("Failed To Create Proxy Session For {Client} ({Reason})", client, admissionResult);

                continue;
            }

            // Authenticate A New Client Immediately So It Does Not Exhaust Its Unauthenticated Packet Budget Waiting For The First Periodic Renewal
            if (created)
                SendChallenge(client, session);

            // Every Datagram Costs Its Source, Whatever It Turns Out To Contain, Which Is What The Drain Rate Is Calibrated Against; The Reference Does This First As Well
            scoreContainer.ChargeArrival(client);

            // A Source Already Over The Threshold Is Refused Before Any Field Of Its Datagram Is Read. By This Point The Session Exists And Its Challenge Has Been Issued, Because A Client Must Be Challenged Before It Can Send Anything Valid
            if (scoreContainer.IsWithinAllowance(client) is false)
            {
                Drop(client, "Actioned");

                continue;
            }

            ReadOnlySpan<byte> datagram = buffer.AsSpan(0, result.ReceivedBytes);

            DatagramValidationResult outcome = DatagramValidator.Validate(
                datagram,
                kind,
                challenges,
                client,
                session.IsWithinUnknownChallengeGrace);

            if (outcome.IsAdmitted is false)
            {
                if (outcome.ViolationWeight > 0)
                    Drop(client, outcome.ViolationWeight, outcome.DropReason!);
                else
                    Drop(client, outcome.DropReason!);

                continue;
            }

            // A Matched Non-Zero Challenge Means The Client Has Demonstrably Accepted One Of Ours, So It No Longer Needs The Benefit Of The Doubt
            if (outcome.IsAuthenticatedChallenge)
                session.MarkAuthenticated();

            // Refreshed Only Now, So That Refused Traffic Never Extends A Session's Life; The Reference Does The Same, Which Is What Makes Its Idle Timeout Effective Against A Source Sending Nothing But Refused Datagrams
            session.Touch();

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
        uint sequence = NextChallengeSequence();
        challenges.Rotate(sequence, packetQuota);

        foreach (KeyValuePair<IPEndPoint, ClientSession> pair in sessions)
        {
            uint issuedTimestamp = pair.Value.NextIssuedTimestamp();

            Volatile.Write(ref pair.Value.IssuedTimestamp, issuedTimestamp);
            TransmitChallenge(pair.Key, sequence, issuedTimestamp);
        }
    }

    /// <summary>
    ///     Re-sends each session's current challenge without issuing a new one.
    ///     The reference does this about once a second, so that a single lost challenge datagram cannot leave a client holding an allowance sized for less time than it must now cover.
    ///     A client ignores a repeat of the challenge it already holds, so this is free of side effects for one that received the original.
    /// </summary>
    public void RepeatChallenges()
    {
        if (challenges.CurrentChallenge is uint current)
        {
            foreach (KeyValuePair<IPEndPoint, ClientSession> pair in sessions)
                TransmitChallenge(pair.Key, current, Volatile.Read(ref pair.Value.IssuedTimestamp));
        }
    }

    private void SendChallenge(IPEndPoint client, ClientSession session)
    {
        uint? current = challenges.CurrentChallenge;

        if (current is null)
        {
            uint sequence = NextChallengeSequence();
            challenges.Rotate(sequence, packetQuota);
            current = sequence;
        }

        uint issuedTimestamp = session.NextIssuedTimestamp();

        Volatile.Write(ref session.IssuedTimestamp, issuedTimestamp);

        TransmitChallenge(client, current.Value, issuedTimestamp);
    }

    private uint NextChallengeSequence()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        uint sequence;

        do
        {
            RandomNumberGenerator.Fill(bytes);
            sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
        while (challenges.ContainsChallenge(sequence));

        return sequence;
    }

    private void TransmitChallenge(IPEndPoint client, uint challenge, uint serverCreationTimestamp)
    {
        // The Client Accepts A Replacement Only When This Timestamp Is Strictly Greater Than The One It Holds, And It Keys What It Holds On Our Public Port, Which A Restart Does Not Change
        // So It Comes From The Wall Clock And Advances Only When The Challenge Value Does: Advancing It On Every Transmit Ran It Hours Ahead Of The Clock, And A Restart Then Issued Timestamps The Client Rejected As Old
        byte[] packet = BuildChallengePacket(serverCreationTimestamp, challenge);

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
        attackIndicator.Charge(AttackIndicatorContainer.ValidationDropAttackWeight);

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

    private void ReleaseDropReport(IPEndPoint client)
    {
        if (reportedDrops.TryRemove(client, out _))
            Interlocked.Decrement(ref reportedDropCount);
    }

    private enum SessionAdmissionResult
    {
        Admitted,
        UnderAttack,
        ForwarderCapReached,
        AddressCapReached,
        CreationFailed
    }

    private SessionAdmissionResult TryGetOrCreateSession(IPEndPoint client, CancellationToken stoppingToken, out ClientSession? session, out bool created)
    {
        if (sessions.TryGetValue(client, out session))
        {
            created = false;

            return SessionAdmissionResult.Admitted;
        }

        lock (sessionsLock)
        {
            if (sessions.TryGetValue(client, out session))
            {
                created = false;

                return SessionAdmissionResult.Admitted;
            }

            if (attackIndicator.IsUnderAttack)
            {
                session = null;
                created = false;

                return SessionAdmissionResult.UnderAttack;
            }

            if (sessions.Count >= MaxSessionsPerForwarder)
            {
                attackIndicator.Charge(AttackIndicatorContainer.CapRefusalAttackWeight);

                session = null;
                created = false;

                return SessionAdmissionResult.ForwarderCapReached;
            }

            int sessionCountForAddress = 0;
            foreach (KeyValuePair<IPEndPoint, ClientSession> pair in sessions)
            {
                if (pair.Key.Address.Equals(client.Address))
                    sessionCountForAddress++;
            }

            if (sessionCountForAddress >= MaxSessionsPerAddress)
            {
                attackIndicator.Charge(AttackIndicatorContainer.CapRefusalAttackWeight);

                session = null;
                created = false;

                return SessionAdmissionResult.AddressCapReached;
            }

            Socket upstreamSocket;
            try
            {
                upstreamSocket = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                DisableConnectionResetReporting(upstreamSocket);
                upstreamSocket.Connect(serverEndPoint);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Failed To Open Upstream Proxy Socket For {Client}", client);

                session = null;
                created = false;

                return SessionAdmissionResult.CreationFailed;
            }

            attackIndicator.Charge(AttackIndicatorContainer.NovelEndpointAttackWeight);

            session = new (upstreamSocket, stoppingToken, timeProvider);
            sessions[client] = session;

            _ = PumpServerToClient(client, session);

            created = true;

            return SessionAdmissionResult.Admitted;
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
                    {
                        removed.Dispose();

                        ReleaseDropReport(client);
                    }
            }
        }
    }

    public void EvictIdleSessions(TimeSpan idleTimeout, TimeSpan unauthenticatedTimeout)
    {
        long now = timeProvider.GetTimestamp();

        // Sweep Under The Same Lock That Guards Session Creation So An Idle Session Can Never Be Removed And Disposed While A Datagram For The Same Client Is Concurrently Creating A Replacement, Which Would Otherwise Leak Whichever Session Lost The Race
        lock (sessionsLock)
        {
            foreach (KeyValuePair<IPEndPoint, ClientSession> pair in sessions)
            {
                TimeSpan timeout = pair.Value.HasAuthenticated ? idleTimeout : unauthenticatedTimeout;

                if (timeProvider.GetElapsedTime(Volatile.Read(ref pair.Value.LastActivityTimestamp), now) < timeout)
                    continue;

                if (sessions.TryRemove(pair.Key, out ClientSession? removed))
                {
                    removed.Dispose();

                    ReleaseDropReport(pair.Key);
                }
            }
        }

        // A Report Entry Can Outlive Its Session: The Creation-Failure Path Never Had One, And A Datagram Can Be Refused For A Session Evicted Underneath It
        // Reclaiming Them Here Bounds The Budget Without Releasing A Throttle That Is Still Doing Its Job, Which Is What Releasing On The Failure Path Did
        foreach (KeyValuePair<IPEndPoint, bool> report in reportedDrops)
            if (sessions.ContainsKey(report.Key) is false)
                ReleaseDropReport(report.Key);
    }

    public void Dispose()
    {
        frontSocket.Dispose();

        foreach (ClientSession session in sessions.Values)
            session.Dispose();

        sessions.Clear();
    }

    // Every Clock This Class Reads Comes From The Injected Provider Rather Than "Environment.TickCount64" Or The Wall Clock, Which Is What Lets A Test Reach The Unknown-Challenge Grace's Bound And Both Idle Timeouts Without Waiting Out The Real Interval
    private sealed class ClientSession : IDisposable
    {
        public Socket UpstreamSocket { get; }

        public CancellationTokenSource Cancellation { get; }

        // A Provider Timestamp Rather Than Milliseconds, So Only "TimeProvider.GetElapsedTime" Can Interpret It
        public long LastActivityTimestamp;

        public uint IssuedTimestamp;

        private readonly TimeProvider timeProvider;

        private readonly long createdTimestamp;

        private long lastIssuedTimestamp;

        private bool authenticated;

        private int disposed;

        public ClientSession(Socket upstreamSocket, CancellationToken stoppingToken, TimeProvider timeProvider)
        {
            UpstreamSocket = upstreamSocket;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            this.timeProvider = timeProvider;
            createdTimestamp = timeProvider.GetTimestamp();

            Touch();
        }

        public bool HasAuthenticated => Volatile.Read(ref authenticated);

        public bool IsWithinUnknownChallengeGrace
            => HasAuthenticated is false && timeProvider.GetElapsedTime(createdTimestamp) < UnknownChallengeGrace;

        public void MarkAuthenticated()
        {
            if (Volatile.Read(ref authenticated) is false)
                Volatile.Write(ref authenticated, true);
        }

        public void Touch() => Volatile.Write(ref LastActivityTimestamp, timeProvider.GetTimestamp());

        /// <summary>
        ///     The timestamp to stamp into a newly issued challenge: the wall clock, floored so it never repeats or decreases for this session.
        ///     The floor is what lets a challenge issued inside the same second as its predecessor still be accepted, and it is per session so that transmitting to other sessions cannot run it ahead of the clock.
        /// </summary>
        public uint NextIssuedTimestamp()
        {
            long now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            long monotonic = Math.Max(Interlocked.Read(ref lastIssuedTimestamp) + 1, now);

            Interlocked.Exchange(ref lastIssuedTimestamp, monotonic);

            return (uint)monotonic;
        }

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
