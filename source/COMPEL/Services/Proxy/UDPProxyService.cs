namespace COMPEL.Services.Proxy;

/// <summary>
///     The managed, cross-platform anti-cheat / anti-DDoS proxy. When enabled, it runs a UDP relay per instance for both the game and voice ports, forwarding the public ports (offset by <see cref="PortPlan.ProxyPublicOffset"/>) to the local server ports.
///     It enforces a ban list by dropping banned datagrams and, where firewall integration is available, blocking the source; the ban list is cleared periodically so blocks expire, faithfully reproducing the legacy proxy manager's twelve-hour firewall cleaner.
///     The original proxy's cheater-detection heuristics lived in a closed binary and are not reproduced; the transport, port remapping, ban enforcement, and firewall integration are. Detection is a deliberate future extension via <see cref="BanAddress"/>.
/// </summary>
public sealed class UDPProxyService : BackgroundService
{
    private static readonly TimeSpan IdleSessionTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FirewallCleanupInterval = TimeSpan.FromHours(12);

    private readonly MatchServerManagerOptions options;
    private readonly IFirewallController firewall;
    private readonly PortPlan ports;
    private readonly ILogger<UDPProxyService> logger;

    private readonly ConcurrentDictionary<IPAddress, byte> bannedAddresses = new ();
    private readonly List<UDPForwarder> forwarders = new ();

    // Guards "bannedAddresses" Together With The Firewall Controller So The Periodic Clear In "RunMaintenanceLoop" And A Concurrent "BanAddress" Call Can Never Leave The Two Stores Disagreeing With Each Other.
    private readonly Lock banLock = new ();

    // Completes With TRUE Once The Proxy Is Usable (Disabled, Or At Least One Forwarder Bound) And FALSE When The Proxy Is Enabled But No Forwarder Could Bind, So The Supervisor Can Refuse To Launch The Manager Rather Than Advertise Unreachable Public Ports.
    private readonly TaskCompletionSource<bool> ready = new (TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile bool running;
    private int failedForwarderCount;

    public UDPProxyService(IOptions<MatchServerManagerOptions> options, IFirewallController firewall, PortPlan ports, ILogger<UDPProxyService> logger)
    {
        this.options = options.Value;
        this.firewall = firewall;
        this.ports = ports;
        this.logger = logger;
    }

    public bool IsRunning => running;

    /// <summary>
    ///     Completes once the proxy has finished its bind attempt: <see langword="true"/> when the proxy is disabled or at least one forwarder bound, and <see langword="false"/> when the proxy is enabled but no forwarder could bind. The supervisor awaits this before launching the manager so it does not advertise public ports nothing is listening on.
    /// </summary>
    public Task<bool> WaitUntilReady(CancellationToken cancellationToken) => ready.Task.WaitAsync(cancellationToken);

    /// <summary>
    ///     The number of game/voice ports that failed to bind on startup. A non-zero value means the proxy is running in a degraded state: some instances have no working proxy at all even though <see cref="IsRunning"/> is <see langword="true"/>.
    /// </summary>
    public int FailedForwarderCount => Volatile.Read(ref failedForwarderCount);

    /// <summary>
    ///     Bans a source address: subsequent datagrams from it are dropped, and a firewall rule blocks it where firewall integration is available.
    /// </summary>
    public void BanAddress(IPAddress address)
    {
        lock (banLock)
        {
            if (bannedAddresses.TryAdd(address, 0) is false)
                return;

            firewall.BlockAddress(address);
        }

        logger.LogWarning("Banned Source Address {Address}", address);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.UseProxy is false)
        {
            logger.LogInformation("Proxy Is Disabled; The Servers' Local Ports Are Public");

            ready.TrySetResult(true);

            return;
        }

        for (int instance = 0; instance < ports.Instances; instance++)
        {
            TryAddForwarder(ports.PublicGameStart + instance, ports.LocalGameStart + instance, "Game");
            TryAddForwarder(ports.PublicVoiceStart + instance, ports.LocalVoiceStart + instance, "Voice");
        }

        if (forwarders.Count is 0)
        {
            logger.LogError("No Proxy Forwarders Could Be Started");

            ready.TrySetResult(false);

            return;
        }

        running = true;

        ready.TrySetResult(true);

        logger.LogInformation
        (
            "Proxy Forwarding {Instances} Instance(s): Public Game {PublicGameStart}-{PublicGameEnd} And Voice {PublicVoiceStart}-{PublicVoiceEnd} To Local Game {LocalGameStart}-{LocalGameEnd} And Voice {LocalVoiceStart}-{LocalVoiceEnd}",
            ports.Instances, ports.PublicGameStart, ports.PublicGameEnd, ports.PublicVoiceStart, ports.PublicVoiceEnd, ports.LocalGameStart, ports.LocalGameEnd, ports.LocalVoiceStart, ports.LocalVoiceEnd
        );

        List<Task> tasks = forwarders.Select(forwarder => forwarder.Run(stoppingToken)).ToList();

        tasks.Add(RunMaintenanceLoop(stoppingToken));

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        catch (OperationCanceledException)
        {
        }

        finally
        {
            running = false;

            foreach (UDPForwarder forwarder in forwarders)
                forwarder.Dispose();

            forwarders.Clear();
        }
    }

    private void TryAddForwarder(int publicPort, int localPort, string kind)
    {
        try
        {
            forwarders.Add(new UDPForwarder(publicPort, localPort, IsBanned, logger));
        }

        catch (Exception exception)
        {
            Interlocked.Increment(ref failedForwarderCount);

            logger.LogError(exception, "Failed To Bind The {Kind} Proxy On Public Port {PublicPort}", kind, publicPort);
        }
    }

    private bool IsBanned(IPAddress address) => bannedAddresses.ContainsKey(address);

    private async Task RunMaintenanceLoop(CancellationToken stoppingToken)
    {
        // Clear Any Stale Firewall Block Rule Left Over From A Previous Run, As The Legacy Proxy Manager Did On Startup.
        lock (banLock) { firewall.ClearBlockedAddresses(); }

        long lastFirewallCleanupTicks = Environment.TickCount64;

        while (stoppingToken.IsCancellationRequested is false)
        {
            try { await Task.Delay(IdleSweepInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            foreach (UDPForwarder forwarder in forwarders)
                forwarder.EvictIdleSessions(IdleSessionTimeout);

            if (Environment.TickCount64 - lastFirewallCleanupTicks < FirewallCleanupInterval.TotalMilliseconds)
                continue;

            // Bans Expire Periodically So A Transient Block Does Not Persist Indefinitely. Locked Together With "BanAddress" So The Firewall And The In-Memory Ban List Never Disagree.
            lock (banLock)
            {
                firewall.ClearBlockedAddresses();
                bannedAddresses.Clear();
            }

            lastFirewallCleanupTicks = Environment.TickCount64;
        }
    }
}
