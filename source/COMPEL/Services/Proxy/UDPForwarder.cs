namespace COMPEL.Services.Proxy;

/// <summary>
///     A bidirectional UDP relay for a single public port. Datagrams from a client are forwarded to the local server port; the server's replies are relayed back to the originating client.
///     A dedicated upstream socket per client preserves the server's per-client addressing, mirroring how the native proxy mapped each public port to its local server port.
/// </summary>
internal sealed class UDPForwarder : IDisposable
{
    private const int DatagramBufferSize = 65535;

    private readonly IPEndPoint serverEndPoint;
    private readonly Func<IPAddress, bool> isBanned;
    private readonly ILogger logger;
    private readonly Socket frontSocket;
    private readonly ConcurrentDictionary<IPEndPoint, ClientSession> sessions = new ();
    private readonly Lock sessionsLock = new ();

    public int PublicPort { get; }

    public int LocalPort { get; }

    public UDPForwarder(int publicPort, int localPort, Func<IPAddress, bool> isBanned, ILogger logger)
    {
        PublicPort = publicPort;
        LocalPort = localPort;
        serverEndPoint = new IPEndPoint(IPAddress.Loopback, localPort);
        this.isBanned = isBanned;
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

            // Application-Layer Ban Enforcement: Banned Sources Are Dropped Regardless Of Whether A Firewall Rule Also Exists.
            if (isBanned(client.Address))
                continue;

            ClientSession session;

            try { session = GetOrCreateSession(client, stoppingToken); }
            catch (Exception exception) { logger.LogDebug(exception, "Failed To Create Proxy Session For {Client}", client); continue; }

            session.Touch();

            try { await session.UpstreamSocket.SendAsync(buffer.AsMemory(0, result.ReceivedBytes), SocketFlags.None, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception exception) { logger.LogDebug(exception, "Failed To Forward Datagram To Server For {Client}", client); }
        }
    }

    private ClientSession GetOrCreateSession(IPEndPoint client, CancellationToken stoppingToken)
    {
        if (sessions.TryGetValue(client, out ClientSession? existing))
            return existing;

        lock (sessionsLock)
        {
            if (sessions.TryGetValue(client, out existing))
                return existing;

            Socket upstreamSocket = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            DisableConnectionResetReporting(upstreamSocket);
            upstreamSocket.Connect(serverEndPoint);

            ClientSession created = new (upstreamSocket, stoppingToken);
            sessions[client] = created;

            _ = PumpServerToClient(client, created);

            return created;
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

                // Application-Layer Ban Enforcement On The Reply Path: A Client Banned After Its Session Was Established Must Not Keep Receiving Relayed Server Traffic.
                // The Check Precedes "Touch" So A Banned Client's Session Is Left To Age Out And Be Evicted Rather Than Being Kept Alive Indefinitely By The Server's Continued Replies.
                if (isBanned(client.Address))
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
