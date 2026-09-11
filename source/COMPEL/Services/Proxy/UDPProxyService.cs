namespace COMPEL.Services.Proxy;

// TODO: The Proxy Validates Datagram Length, The Per-Challenge Packet Quota, And Duplicate Counters, And Scores Abuse Per Source; It Does Not Yet Validate The Watermarks
// TODO: The Reference Proxy Also Checks A Constant Per-Region Watermark And A Dynamic CRC32C One, Which Together Are Its Anti-Cheat Signal; Adding Them Needs A Region Setting COMPEL Has No Equivalent For, And Carries A Higher False-Positive Cost Than The Checks Above
// TODO: Challenge Values Are A Monotonic Counter Rather Than The Reference's Cryptographically Random One, So They Are Guessable; A Source That Guesses One Is Held To The Per-Challenge Quota Instead Of The Much Smaller Unauthenticated One, And Watermark Validation Would Depend On Them Being Unpredictable
// TODO: The Challenge Value And The Creation Timestamp No Longer Share One Value In "BuildChallengePacket", So Making The Challenge Cryptographically Random Is Now A Self-Contained Change Rather Than One That Also Requires Separating Them First
/// <summary>
///     The managed, cross-platform proxy. When enabled, it runs a UDP relay per instance for both the game and voice ports, forwarding the public ports (offset by <see cref="PortPlan.ProxyPublicOffset"/>) to the local server ports.
///     Heroes Of Newerth clients throttle their own traffic on the public port range until the proxy authenticates them, so each forwarder issues a challenge to every session on creation and this service renews those challenges periodically.
/// </summary>
public sealed class UDPProxyService : BackgroundService
{
    private static readonly TimeSpan IdleSessionTimeout = TimeSpan.FromMinutes(2);

    // Renewed Well Within The Client's Authentication Window So A Session Never Lapses Back To The Throttled, Unauthenticated State Between Renewals
    internal static readonly TimeSpan ChallengeRenewalInterval = TimeSpan.FromSeconds(10);

    // The Maintenance Pass Runs Far More Often Than A Rotation, Because The Reference Re-Sends The Current Challenge About Once A Second So That One Lost Challenge Datagram Cannot Leave A Client Throttled
    internal static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(1);

    // Derived Rather Than Written Down Twice, So The Renewal Interval Stays What It Says It Is If Either Value Changes
    internal static readonly int MaintenancePassesPerRotation = (int) Math.Max(1, ChallengeRenewalInterval.Ticks / MaintenanceInterval.Ticks);

    // "UNDER_ATTACK_THRESHOLD": Refusals Within One Window Above Which The Proxy Reports Itself Under Attack
    private const int UnderAttackThreshold = 1000;

    // The Reference Resets Its Indicator Every Five Minutes Of Its Own Housekeeping Tick; Derived From The Interval Rather Than Written Down Twice, So It Stays Five Minutes If The Interval Changes
    internal static readonly int UnderAttackWindowPasses = (int) Math.Max(1, TimeSpan.FromMinutes(5).Ticks / MaintenanceInterval.Ticks);

    private readonly MatchServerManagerOptions options;
    private readonly PortPlan ports;
    private readonly ILogger<UDPProxyService> logger;

    private readonly List<UDPForwarder> forwarders = new ();

    // Shared Across Every Forwarder So A Single Source's Score Is The Same Regardless Of Which Public Port It Sends To; Not "IDisposable" And So Never Disposed Alongside The Forwarders
    private readonly ViolationScoreContainer scoreContainer = new (TimeProvider.System);

    // Completes With TRUE Once The Proxy Is Usable (Disabled, Or At Least One Forwarder Bound) And FALSE When The Proxy Is Enabled But No Forwarder Could Bind, So The Supervisor Can Refuse To Launch The Manager Rather Than Advertise Unreachable Public Ports
    private readonly TaskCompletionSource<bool> ready = new (TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile bool running;
    private int failedForwarderCount;
    private long droppedDatagramCount;
    private int maintenancePassesThisWindow;
    private int maintenancePassesSinceRotation;
    private long droppedDatagramsAtWindowStart;
    private bool isUnderAttack;

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

    /// <summary>
    ///     The number of client datagrams the proxy has refused to relay, across every forwarder, as at the last maintenance pass.
    /// </summary>
    public long DroppedDatagramCount => Volatile.Read(ref droppedDatagramCount);

    /// <summary>
    ///     Whether the proxy refused more datagrams in the last completed window than the under-attack threshold allows.
    /// </summary>
    public bool IsUnderAttack => Volatile.Read(ref isUnderAttack);

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
            forwarders.Add(new UDPForwarder(publicPort, localPort, kind, ChallengeRenewalInterval, scoreContainer, logger));
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

            bool rotating = ++maintenancePassesSinceRotation >= MaintenancePassesPerRotation;

            if (rotating)
                maintenancePassesSinceRotation = 0;

            long droppedDatagrams = 0;

            foreach (UDPForwarder forwarder in forwarders)
            {
                // A Rotation Sends The New Challenge Itself, So There Is Nothing To Repeat On That Pass
                if (rotating)
                {
                    forwarder.RotateChallenges();
                    forwarder.EvictIdleSessions(IdleSessionTimeout);
                }

                else
                {
                    forwarder.RepeatChallenges();
                }

                droppedDatagrams += forwarder.DroppedDatagramCount;
            }

            Volatile.Write(ref droppedDatagramCount, droppedDatagrams);

            if (++maintenancePassesThisWindow >= UnderAttackWindowPasses)
            {
                maintenancePassesThisWindow = 0;

                // The Sum From This Pass Is Reused Rather Than Read Back Through The Property, Which Would Volatile-Read The Value Just Written From It
                long droppedThisWindow = droppedDatagrams - droppedDatagramsAtWindowStart;

                droppedDatagramsAtWindowStart = droppedDatagrams;

                bool underAttack = droppedThisWindow > UnderAttackThreshold;

                Volatile.Write(ref isUnderAttack, underAttack);

                if (underAttack)
                    logger.LogWarning("The Proxy Refused {DroppedDatagrams} Datagram(s) In The Last Window, Which Exceeds The Under-Attack Threshold Of {Threshold}", droppedThisWindow, UnderAttackThreshold);
            }
        }
    }
}
