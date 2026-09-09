namespace COMPEL.Services.ContentBroker;

/// <summary>
///     Keeps the local match server distribution synchronised with the CDN.
///     On startup it performs an initial synchronisation (retried with backoff, or skipped if a local copy already exists, or skipped entirely when <see cref="CDNOptions.Synchronisation"/> is disabled) and exposes <see cref="SynchroniseNow"/> for the control plane to trigger a re-synchronisation on demand.
///     The supervisor awaits <see cref="WaitUntilReady"/> before launching the manager.
/// </summary>
public sealed class DistributionSynchronisationService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly CDNOptions options;
    private readonly ILogger<DistributionSynchronisationService> logger;
    private readonly SemaphoreSlim gate = new (1, 1);
    private readonly TaskCompletionSource ready = new (TaskCreationOptions.RunContinuationsAsynchronously);

    public string InstallationDirectory { get; }

    public string Variant { get; }

    public string? DistributionVersion { get; private set; }

    public string SynchronisationState { get; private set; } = "Pending";

    /// <summary>
    ///     Whether a synchronisation is currently rewriting the installation directory. The supervisor consults this so it never launches the manager against a half-rewritten distribution.
    /// </summary>
    public bool IsSynchronising => synchronising;

    private volatile bool synchronising;

    public DistributionSynchronisationService(IOptions<CDNOptions> options, ILogger<DistributionSynchronisationService> logger)
    {
        this.options = options.Value;
        this.logger = logger;

        Variant = ResolveServerVariant(this.options);

        InstallationDirectory = ResolveInstallationDirectory(this.options.InstallationDirectory);
    }

    // The Distribution Installs Alongside The COMPEL Executable By Default (An Empty Configured Directory), So It Sits Beside The Binary Rather Than In A Peer Folder. A Relative Path Is Resolved Against The Executable's Directory, And A Fully Qualified Path Is Honoured As-Is.
    public static string ResolveInstallationDirectory(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return AppContext.BaseDirectory;

        return Path.IsPathFullyQualified(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
    }

    /// <summary>
    ///     The path to the manager executable within the installed distribution.
    /// </summary>
    public string ManagerExecutablePath => Path.Combine(InstallationDirectory, HeroesOfNewerthExecutable.FileName);

    /// <summary>
    ///     Completes once the distribution has been synchronised, or once an existing local copy has been accepted when the CDN is unreachable or synchronisation is disabled. The supervisor awaits this before launching the manager.
    /// </summary>
    public Task WaitUntilReady(CancellationToken cancellationToken) => ready.Task.WaitAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The Initial Synchronisation Can Be Disabled For Development And Testing; The Existing Local Distribution Is Used, And On-Demand Synchronisation Via The Control Plane Still Works
        if (options.Synchronisation is false)
        {
            logger.LogInformation("SKIP: Synchronisation Skipped (Manual Override)");

            SynchronisationState = "Disabled";

            ready.TrySetResult();

            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }

            return;
        }

        // Mirroring The Launcher's Location Guard, A Development Environment Is Never Synchronised; The Mirror's Deletion Pass Would Otherwise Remove Development Artefacts That Are Not Part Of The Distribution
        if (LocationGuard.AssessLocationSafety(InstallationDirectory).Verdict is not LocationSafetyVerdict.Safe)
        {
            logger.LogInformation("SKIP: Synchronisation Skipped (Development Environment)");

            SynchronisationState = "Skipped (Development Environment)";

            ready.TrySetResult();

            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }

            return;
        }

        while (stoppingToken.IsCancellationRequested is false)
        {
            try
            {
                SynchronisationSummary summary = await SynchroniseNow(stoppingToken).ConfigureAwait(false);

                // A Synchronisation That Reports No Exception Can Still Have Failed To Fetch Individual Files, Which Would Leave A Mixed-Version Tree; Only Stop Retrying Once Every File Transferred And The Manager Executable Is Present
                if (summary.FilesFailed is 0 && File.Exists(ManagerExecutablePath))
                    break;

                logger.LogWarning("Retrying Synchronisation In {Seconds} Seconds", RetryDelay.TotalSeconds);
            }

            catch (OperationCanceledException)
            {
                return;
            }

            catch (Exception)
            {
                // The Failure Itself Was Logged By "SynchroniseNow"; If A Previous Synchronisation Already Installed The Manager, The Launch Proceeds With It Rather Than Blocking On A Transient CDN Outage
                if (File.Exists(ManagerExecutablePath))
                {
                    logger.LogWarning("Proceeding With The Existing Local Distribution At {InstallationDirectory}", InstallationDirectory);

                    ready.TrySetResult();

                    break;
                }

                logger.LogWarning("No Local Distribution Is Present; Retrying Synchronisation In {Seconds} Seconds", RetryDelay.TotalSeconds);
            }

            try { await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        // Remain Alive So The Control Plane Can Resolve This Singleton For On-Demand Synchronisations And Status Reporting
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    ///     Fetches the manifest and synchronises the installation directory, logging each step in WILLOWMAKER's vocabulary. Safe to call concurrently; calls are serialised.
    ///     Every failure is logged here before it propagates, so the start-up loop and the control plane only decide what to do next.
    /// </summary>
    public async Task<SynchronisationSummary> SynchroniseNow(CancellationToken cancellationToken)
    {
        // Mirroring The Startup Path, A Location That Is Not Safe To Mirror Into (A Development Environment) Is Never Synchronised, Including On Demand Via The Control Plane
        if (LocationGuard.AssessLocationSafety(InstallationDirectory).Verdict is not LocationSafetyVerdict.Safe)
        {
            logger.LogInformation("SKIP: Synchronisation Skipped (Development Environment)");

            return new SynchronisationSummary(0, 0, 0, 0, 0, []);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        synchronising = true;

        try
        {
            SynchronisationState = "Synchronising";

            logger.LogInformation(@"INIT: Fetching Manifest For Variant ""{Variant}"" From CDN", Variant);

            Manifest manifest = await ContentBroker.FetchManifest(Variant, options.Host, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("INIT: Manifest Version {Version} Lists {Count} File(s)", manifest.Version, manifest.Files.Count);

            DistributionVersion = manifest.Version;

            // Reported Synchronously So The Per-File Lines Land In The Log In The Order The Broker Raises Them, With The Completion Line Last
            SynchronousProgress<SynchronisationEvent> progress = new (LogSynchronisationEvent);

            SynchronisationSummary summary = await ContentBroker.Synchronise(manifest, Variant, InstallationDirectory, options.Host, options.ParallelTransfers, progress, cancellationToken).ConfigureAwait(false);

            SynchronisationState = summary.FilesFailed is 0 ? "Up To Date" : $"Completed With {summary.FilesFailed} Failure(s)";

            if (summary.FilesFailed > 0)
                logger.LogWarning("FAIL: {Failures} File(s) Failed To Be Transferred", summary.FilesFailed);

            // Release Consumers Only Once Every File Transferred And The Manager Executable Is Present; A Synchronisation That Failed To Fetch Some Files Would Leave A Mixed-Version Tree, So The Manager Must Not Be Launched Against It
            if (summary.FilesFailed is 0 && File.Exists(ManagerExecutablePath))
                ready.TrySetResult();

            return summary;
        }

        catch (HttpRequestException exception)
        {
            string statusCode = exception.StatusCode is not null
                ? $"{(int) exception.StatusCode} ({exception.StatusCode})"
                : "Unknown Status Code";

            logger.LogError("FAIL: CDN Unreachable :: HTTP {StatusCode}", statusCode);

            SynchronisationState = "CDN Unreachable; Synchronisation Aborted";

            throw;
        }

        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("FAIL: {ExceptionType} :: {Message}", exception.GetType().Name, exception.Message);

            SynchronisationState = $"Failed: {exception.Message}";

            throw;
        }

        finally
        {
            synchronising = false;

            gate.Release();
        }
    }

    private void LogSynchronisationEvent(SynchronisationEvent synchronisationEvent)
    {
        switch (synchronisationEvent.Kind)
        {
            case SynchronisationEventKind.PlanReady:
                logger.LogInformation("PLAN: {Plan}", synchronisationEvent.Detail);
                break;

            case SynchronisationEventKind.Downloaded:
                logger.LogInformation("PULL: {Path}", synchronisationEvent.Detail);
                break;

            case SynchronisationEventKind.Deleted:
                logger.LogInformation("NUKE: {Path}", synchronisationEvent.Detail);
                break;

            case SynchronisationEventKind.Skipped:
                logger.LogInformation("SKIP: {Path}", synchronisationEvent.Detail);
                break;

            case SynchronisationEventKind.DownloadFailed:
            case SynchronisationEventKind.DeletionFailed:
                logger.LogWarning("FAIL: {Detail}", synchronisationEvent.Detail);
                break;

            case SynchronisationEventKind.Completed:
                logger.LogInformation("DONE: {Summary}", synchronisationEvent.Detail);
                break;
        }
    }

    /// <summary>
    ///     Returns the distribution variant code that matches the current operating system.
    /// </summary>
    public static string ResolveServerVariant(CDNOptions options) =>
          OperatingSystem.IsWindows() ? options.WindowsVariant
        : OperatingSystem.IsLinux()   ? options.LinuxVariant
        : throw new PlatformNotSupportedException("COMPEL Hosts Match Servers On Windows And Linux Only");
}
