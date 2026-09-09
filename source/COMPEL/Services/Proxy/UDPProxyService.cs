namespace COMPEL.Services.Proxy;

// TODO: This Proxy Performs The Transport, Port Remapping, And Client Authentication Only; It Does Not Detect Cheaters Or Ban Anyone
// The Native Proxy's Detection Heuristics Lived In A Closed Binary And Are Not Reproduced, And The Previous Firewall And Ban-List Mechanism Was Removed As Ineffective
// A Future Redesign Is Expected To Introduce A Different Enforcement Approach, Likely Not A Static Ban List, At Which Point A Hook To Drop Or Block Traffic Per Source Can Be Reintroduced
/// <summary>
///     The managed, cross-platform proxy. When enabled, it runs a UDP relay per instance for both the game and voice ports, forwarding the public ports (offset by <see cref="PortPlan.ProxyPublicOffset"/>) to the local server ports.
///     Heroes Of Newerth clients throttle their own traffic on the public port range until the proxy authenticates them, so each forwarder issues a challenge to every session on creation and this service renews those challenges periodically.
/// </summary>
public sealed class UDPProxyService : BackgroundService
{
    private static readonly TimeSpan IdleSessionTimeout = TimeSpan.FromMinutes(2);

    // Renewed Well Within The Client's Authentication Window So A Session Never Lapses Back To The Throttled, Unauthenticated State Between Renewals
    private static readonly TimeSpan ChallengeRenewalInterval = TimeSpan.FromSeconds(10);

    private readonly MatchServerManagerOptions options;
    private readonly PortPlan ports;
    private readonly ILogger<UDPProxyService> logger;

    private readonly List<UDPForwarder> forwarders = new ();

    // Completes With TRUE Once The Proxy Is Usable (Disabled, Or At Least One Forwarder Bound) And FALSE When The Proxy Is Enabled But No Forwarder Could Bind, So The Supervisor Can Refuse To Launch The Manager Rather Than Advertise Unreachable Public Ports
    private readonly TaskCompletionSource<bool> ready = new (TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile bool running;
    private int failedForwarderCount;

    public UDPProxyService(IOptions<MatchServerManagerOptions> options, PortPlan ports, ILogger<UDPProxyService> logger)
    {
        this.options = options.Value;
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
            forwarders.Add(new UDPForwarder(publicPort, localPort, logger));
        }

        catch (Exception exception)
        {
            Interlocked.Increment(ref failedForwarderCount);

            logger.LogError(exception, "Failed To Bind The {Kind} Proxy On Public Port {PublicPort}", kind, publicPort);
        }
    }

    private async Task RunMaintenanceLoop(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested is false)
        {
            try { await Task.Delay(ChallengeRenewalInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            foreach (UDPForwarder forwarder in forwarders)
            {
                forwarder.ChallengeActiveSessions();
                forwarder.EvictIdleSessions(IdleSessionTimeout);
            }
        }
    }
}
