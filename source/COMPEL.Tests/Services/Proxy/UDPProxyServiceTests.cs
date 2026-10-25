namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies the derived cadences of the proxy's maintenance loop, which no behavioural test reaches and whose breakage is silent.
///     It also verifies that the maintenance loop expires pending reconnect queries, which only a running service exercises.
/// </summary>
[NotInParallel]
public sealed class UDPProxyServiceTests
{
    // A Rotation Is What Resets A Client's Counter And What Ages The Retained Challenge History, So Its Cadence Is Load-Bearing Well Beyond The Challenge Itself
    [Test]
    public async Task The_Rotation_Cadence_Is_The_Challenge_Renewal_Interval()
    {
        TimeSpan rotation = UDPProxyService.MaintenanceInterval * UDPProxyService.MaintenancePassesPerRotation;

        await Assert.That(rotation).IsEqualTo(UDPProxyService.ChallengeRenewalInterval);
    }

    [Test]
    public async Task The_Under_Attack_Threshold_Is_One_Thousand()
    {
        int threshold = AttackIndicatorContainer.UnderAttackThreshold;

        await Assert.That(threshold).IsEqualTo(1000);
    }

    [Test]
    public async Task Restarting_A_Disabled_Proxy_Throws_An_Invalid_Operation_Exception()
    {
        MatchServerManagerOptions options = new ()
        {
            UserName  = "KONGOR",
            Password  = "secret",
            Instances = 1,
            UseProxy  = false
        };

        UDPProxyService service = new (Options.Create(options), new PortPlan(options), NullLogger<UDPProxyService>.Instance);

        using (Assert.Multiple())
        {
            await Assert.That(service.IsEnabled).IsFalse();
            await Assert.That(async () => await service.RequestRestart(CancellationToken.None)).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task Restarting_An_Enabled_Proxy_Rebinds_And_Runs_Forwarders()
    {
        MatchServerManagerOptions options = new ()
        {
            UserName        = "KONGOR",
            Password        = "secret",
            Instances       = 1,
            PortRangeOffset = 80,
            UseProxy        = true
        };

        UDPProxyService service = new (Options.Create(options), new PortPlan(options), NullLogger<UDPProxyService>.Instance);

        using CancellationTokenSource lifetime = new ();

        Task executeTask = service.StartAsync(lifetime.Token);

        try
        {
            bool ready = await service.WaitUntilReady(lifetime.Token);

            await Assert.That(ready).IsTrue();
            await Assert.That(service.IsRunning).IsTrue();

            bool restarted = await service.RequestRestart(lifetime.Token);

            await Assert.That(restarted).IsTrue();
            await Assert.That(service.IsRunning).IsTrue();
        }

        finally
        {
            await service.StopAsync(CancellationToken.None);
            await executeTask;
        }
    }

    // Only The Maintenance Pass Expires A Pending Reconnect Query, So Without It A Match Server Which Stopped Answering Would Leave Its Instance's Pending Table Full, And Every Later Verification On That Instance Would Be Refused Until A Restart
    [Test]
    public async Task The_Maintenance_Pass_Evicts_Expired_Reconnect_Queries()
    {
        MatchServerManagerOptions options = new ()
        {
            UserName        = "KONGOR",
            Password        = "secret",
            Instances       = 1,
            PortRangeOffset = 81,
            UseProxy        = true
        };

        PortPlan ports = new (options);

        // Stands In For A Match Server Which Still Receives The Queries But Has Stopped Answering Them
        using Socket matchServer = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        matchServer.Bind(new IPEndPoint(IPAddress.Loopback, ports.LocalGameStart));

        UDPProxyService service = new (Options.Create(options), ports, NullLogger<UDPProxyService>.Instance);

        using CancellationTokenSource lifetime = new ();

        Task executeTask = service.StartAsync(lifetime.Token);

        IPEndPoint publicGameEndPoint = new (IPAddress.Loopback, ports.PublicGameStart);

        byte[] request = ReconnectQueryTests.Request();

        List<Socket> sources = new ();

        try
        {
            await Assert.That(await service.WaitUntilReady(lifetime.Token)).IsTrue();

            // Twice The Bound From Distinct Sources Fills The Pending Table Even If A Few Datagrams Are Lost On The Way In
            for (int index = 0; index < UDPForwarder.MaximumPendingReconnectQueries * 2; index++)
            {
                Socket source = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                source.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                sources.Add(source);

                await source.SendToAsync(request, SocketFlags.None, publicGameEndPoint);
            }

            while (await TryReceive(matchServer) is not null) { }

            long filled = TimeProvider.System.GetTimestamp();

            using Socket verifier = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            verifier.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            await verifier.SendToAsync(request, SocketFlags.None, publicGameEndPoint);

            // The Table Is Full, So A New Source Is Refused Until A Maintenance Pass Evicts The Expired Entries, Which Must Happen Within A Few Passes Of Their Timeout
            await Assert.That(await TryReceive(matchServer)).IsNull();

            TimeSpan deadline = UDPForwarder.PendingReconnectQueryTimeout + UDPProxyService.MaintenanceInterval * 3;

            bool forwarded = false;

            while (forwarded is false && TimeProvider.System.GetElapsedTime(filled) < deadline)
            {
                await verifier.SendToAsync(request, SocketFlags.None, publicGameEndPoint);

                forwarded = await TryReceive(matchServer) is not null;
            }

            await Assert.That(forwarded).IsTrue();
        }

        finally
        {
            foreach (Socket source in sources)
                source.Dispose();

            await service.StopAsync(CancellationToken.None);
            await executeTask;
        }
    }

    // Shorter Than The Pending Timeout, So That Waiting Out A Refusal Cannot Itself Outlast The Entries Being Tested
    private static async Task<byte[]?> TryReceive(Socket socket)
    {
        byte[] buffer = new byte[65535];

        using CancellationTokenSource timeout = new (TimeSpan.FromMilliseconds(500));

        try
        {
            int received = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);

            return buffer[..received];
        }

        catch (OperationCanceledException)
        {
            return null;
        }
    }
}

