namespace COMPEL.Services.Proxy;

// TODO: The Proxy Validates Datagram Length, The Per-Challenge Packet Quota, And Duplicate Counters, And Scores Abuse Per Source; It Does Not Yet Validate The Watermarks
// TODO: The Reference Proxy Also Checks A Constant Per-Region Watermark And A Dynamic CRC32C One, Which Together Are Its Anti-Cheat Signal; Adding Them Needs A Region Setting COMPEL Has No Equivalent For, And Carries A Higher False-Positive Cost Than The Checks Above
/// <summary>
///     The managed, cross-platform proxy. When enabled, it runs a UDP relay per instance for both the game and voice ports, forwarding the public ports (offset by <see cref="PortPlan.ProxyPublicOffset"/>) to the local server ports.
///     Heroes Of Newerth clients throttle their own traffic on the public port range until the proxy authenticates them, so each forwarder issues a challenge to every session on creation and this service renews those challenges periodically.
/// </summary>
public sealed class UDPProxyService : BackgroundService
{
    internal static readonly TimeSpan IdleSessionTimeout = TimeSpan.FromMinutes(2);

    // "MAX_IDLE_TIME": A Session That Has Never Authenticated Is Swept Far Sooner Than One Carrying A Real Match, Because Any Datagram From A Novel Source Creates One And The Repeat Above Then Transmits To It Every Second
    // Eviction Only Runs On A Rotating Pass, So The Effective Unauthenticated Lifetime Is Fifteen To Twenty-Five Seconds Rather Than Exactly Fifteen
    // This Timeout Is What Makes Repeating To Every Session Affordable, Not What Bounds It: The Reference's Own Bound Also Includes "MAX_GAME_CONNECTIONS" (24), "MAX_GAME_CONNECTIONS_PER_IP" (10), And Refusing New Connections While Its Own Under-Attack Indicator Is Over Threshold
    internal static readonly TimeSpan UnauthenticatedSessionTimeout = TimeSpan.FromSeconds(15);

    // Renewed Well Within The Client's Authentication Window So A Session Never Lapses Back To The Throttled, Unauthenticated State Between Renewals
    internal static readonly TimeSpan ChallengeRenewalInterval = TimeSpan.FromSeconds(10);

    // The Maintenance Pass Runs Far More Often Than A Rotation, Because The Reference Re-Sends The Current Challenge About Once A Second So That One Lost Challenge Datagram Cannot Leave A Client Throttled
    internal static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(1);

    // Derived Rather Than Written Down Twice, So The Renewal Interval Stays What It Says It Is If Either Value Changes
    internal static readonly int MaintenancePassesPerRotation = (int) Math.Max(1, ChallengeRenewalInterval.Ticks / MaintenanceInterval.Ticks);

    private readonly MatchServerManagerOptions options;
    private readonly PortPlan ports;
    private readonly ILogger<UDPProxyService> logger;

    private readonly List<UDPForwarder> forwarders = new ();

    // One Clock For The Score Drain, Both Idle Timeouts And The Unknown-Challenge Grace, So Everything Time-Dependent In The Proxy Measures From The Same Place
    private readonly TimeProvider timeProvider = TimeProvider.System;

    // Shared Across Every Forwarder So A Single Source's Score Is The Same Regardless Of Which Public Port It Sends To; Not "IDisposable" And So Never Disposed Alongside The Forwarders
    private readonly ViolationScoreContainer scoreContainer;

    // Shared Across Every Forwarder So Attack Pressure On Any Public Port Activates The Under-Attack Protection Globally
    private readonly AttackIndicatorContainer attackIndicator;

    // Completes With TRUE Once The Proxy Is Usable (Disabled, Or At Least One Forwarder Bound) And FALSE When The Proxy Is Enabled But No Forwarder Could Bind, So The Supervisor Can Refuse To Launch The Manager Rather Than Advertise Unreachable Public Ports
    private readonly TaskCompletionSource<bool> ready = new (TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile bool running;
    private int failedForwarderCount;
    private long droppedDatagramCount;
    private int maintenancePassesSinceRotation;

    public UDPProxyService(IOptions<MatchServerManagerOptions> options, PortPlan ports, ILogger<UDPProxyService> logger)
    {
        this.options = options.Value;
        this.ports = ports;
        this.logger = logger;

        scoreContainer = new ViolationScoreContainer(timeProvider);
        attackIndicator = new AttackIndicatorContainer(timeProvider);
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
    ///     The number of client datagrams the proxy has refused to relay, across every forwarder, as at the last maintenance pass.
    /// </summary>
    public long DroppedDatagramCount => Volatile.Read(ref droppedDatagramCount);

    /// <summary>
    ///     Whether the proxy is currently under attack based on the live decaying attack indicator.
    /// </summary>
    public bool IsUnderAttack => attackIndicator.IsUnderAttack;

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
            TryAddForwarder(ports.PublicGameStart + instance, ports.LocalGameStart + instance, ProxyForwarderKind.Game);
            TryAddForwarder(ports.PublicVoiceStart + instance, ports.LocalVoiceStart + instance, ProxyForwarderKind.Voice);
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

    private void TryAddForwarder(int publicPort, int localPort, ProxyForwarderKind kind)
    {
        try
        {
            forwarders.Add(new UDPForwarder(publicPort, localPort, kind, ChallengeRenewalInterval, scoreContainer, attackIndicator, timeProvider, logger));
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
            try { await Task.Delay(MaintenanceInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            scoreContainer.Drain();
            attackIndicator.Drain();

            bool rotating = ++maintenancePassesSinceRotation >= MaintenancePassesPerRotation;

            if (rotating)
                maintenancePassesSinceRotation = 0;

            long droppedDatagrams = 0;

            foreach (UDPForwarder forwarder in forwarders)
            {
                // A Rotation Sends The New Challenge Itself, So There Is Nothing To Repeat On That Pass
                if (rotating)
                {
                    // Eviction Runs First: A Session Evicted Immediately After Being Sent A Challenge Would Have Its Replacement Rejected, Because A Recreated Session's Floor Starts Fresh And Would Stamp The Same Wall-Clock Second, Which The Client Refuses As Not Strictly Greater
                    forwarder.EvictIdleSessions(IdleSessionTimeout, UnauthenticatedSessionTimeout);
                    forwarder.RotateChallenges();
                }

                else
                {
                    forwarder.RepeatChallenges();
                }

                droppedDatagrams += forwarder.DroppedDatagramCount;
            }

            Volatile.Write(ref droppedDatagramCount, droppedDatagrams);

            if (attackIndicator.IsUnderAttack)
                logger.LogWarning("The Proxy Is Currently Under Attack (Indicator Score: {Score})", attackIndicator.Score);
        }
    }
}
