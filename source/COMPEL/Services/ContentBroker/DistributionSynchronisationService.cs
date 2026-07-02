namespace COMPEL.Services.ContentBroker;

/// <summary>
///     Keeps the local match server distribution synchronised with the CDN.
///     On startup it performs an initial synchronisation (retried with backoff, or skipped if a local copy already exists, or skipped entirely when <see cref="CDNOptions.Synchronisation"/> is disabled) and exposes <see cref="SynchroniseNow"/> for the control plane to trigger a re-synchronisation on demand.
///     The supervisor awaits <see cref="WaitUntilReady"/> before launching the manager.
/// </summary>
public sealed class DistributionSynchronisationService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    // COMPEL Installs The Distribution Alongside Its Own Executable, So Its Own Files (The Binary, "COMPEL.json", "COMPEL.log", "COMPEL.lock", And Any Build Artefacts) Are Protected From Being Overwritten Or Deleted By The Mirror, Regardless Of What The Manifest Declares.
    private static readonly string[] OwnFileProtectionPatterns = [ "COMPEL*" ];

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

    private long announcedTotalBytes;
    private long lastProgressLogTicks;

    public DistributionSynchronisationService(IOptions<CDNOptions> options, ILogger<DistributionSynchronisationService> logger)
    {
        this.options = options.Value;
        this.logger = logger;

        Variant = ResolveServerVariant(this.options);

        InstallationDirectory = ResolveInstallationDirectory(this.options.InstallationDirectory);
    }

    // The Distribution Installs Alongside The COMPEL Executable By Default (An Empty Configured Directory), So It Sits Beside The Binary Rather Than In A Peer Folder. A Relative Path Is Resolved Against The Executable's Directory, And A Fully Qualified Path Is Honoured As-Is.
    private static string ResolveInstallationDirectory(string configured)
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
        // The Initial Synchronisation Can Be Disabled For Development And Testing: The Existing Local Distribution Is Used, And On-Demand Synchronisation Via The Control Plane Still Works.
        if (options.Synchronisation is false)
        {
            logger.LogInformation("Initial CDN Synchronisation Is Disabled; Proceeding With The Existing Local Distribution");

            SynchronisationState = "Disabled";

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

                // A Synchronisation That Reports No Exception Can Still Have Failed To Fetch Individual Files, Which Would Leave A Mixed-Version Tree; Only Stop Retrying Once Every File Transferred And The Manager Executable Is Present.
                if (summary.FilesFailed is 0 && File.Exists(ManagerExecutablePath))
                    break;

                logger.LogWarning("Synchronisation Did Not Fully Complete ({Failures} File(s) Failed); Retrying In {Seconds} Seconds", summary.FilesFailed, RetryDelay.TotalSeconds);
            }

            catch (OperationCanceledException)
            {
                return;
            }

            catch (Exception exception)
            {
                logger.LogError(exception, "Distribution Synchronisation Failed");

                // If A Previous Synchronisation Already Installed The Manager, Proceed With It Rather Than Blocking The Manager Launch On A Transient CDN Outage.
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

        // Remain Alive So The Control Plane Can Resolve This Singleton For On-Demand Synchronisations And Status Reporting.
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    ///     Fetches the manifest and synchronises the installation directory. Safe to call concurrently; calls are serialised.
    /// </summary>
    public async Task<SynchronisationSummary> SynchroniseNow(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        synchronising = true;

        try
        {
            SynchronisationState = "Synchronising";

            logger.LogInformation("Synchronising Match Server Distribution {Variant} From {Host} Into {Directory}", Variant, options.Host, InstallationDirectory);

            Manifest manifest = await ContentBroker.FetchManifest(Variant, options.Host, cancellationToken).ConfigureAwait(false);

            DistributionVersion = manifest.Version;

            Progress<SynchronisationEvent> progress = new (LogSynchronisationEvent);

            SynchronisationSummary summary = await ContentBroker.Synchronise(manifest, Variant, InstallationDirectory, options.Host, options.ParallelTransfers, OwnFileProtectionPatterns, progress, cancellationToken).ConfigureAwait(false);

            SynchronisationState = summary.FilesFailed is 0 ? "Up To Date" : $"Completed With {summary.FilesFailed} Failure(s)";

            // Release Consumers Only Once Every File Transferred And The Manager Executable Is Present. A Synchronisation That Failed To Fetch Some Files Would Leave A Mixed-Version Tree, So The Manager Must Not Be Launched Against It.
            if (summary.FilesFailed is 0 && File.Exists(ManagerExecutablePath))
                ready.TrySetResult();

            return summary;
        }

        catch (Exception exception) when (exception is not OperationCanceledException)
        {
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
                announcedTotalBytes = synchronisationEvent.Plan?.TotalBytesToDownload ?? 0;
                lastProgressLogTicks = 0;
                logger.LogInformation("Synchronisation Plan: {Plan}", synchronisationEvent.Detail);
                break;

            case SynchronisationEventKind.ProgressUpdated:
                LogProgress(synchronisationEvent.Size);
                break;

            case SynchronisationEventKind.Downloaded:
                logger.LogDebug("Downloaded {Path} ({Size:N0} Bytes)", synchronisationEvent.Detail, synchronisationEvent.Size);
                break;

            case SynchronisationEventKind.Deleted:
                logger.LogDebug("Deleted {Path}", synchronisationEvent.Detail);
                break;

            case SynchronisationEventKind.DownloadFailed:
            case SynchronisationEventKind.DeletionFailed:
                logger.LogWarning("{Detail}", synchronisationEvent.Detail);
                break;

            case SynchronisationEventKind.Completed:
                logger.LogInformation("Synchronisation Complete: {Summary}", synchronisationEvent.Detail);
                break;
        }
    }

    // Logs A Throttled Progress Line During A Long Initial Synchronisation So The Operator Sees Movement Between The Plan And The Completion Lines, Without Flooding The Log.
    private void LogProgress(long bytesDownloaded)
    {
        if (announcedTotalBytes <= 0)
            return;

        long now = Environment.TickCount64;

        if (lastProgressLogTicks is not 0 && now - lastProgressLogTicks < 10_000)
            return;

        lastProgressLogTicks = now;

        double percentage = Math.Min(100.0, bytesDownloaded * 100.0 / announcedTotalBytes);

        logger.LogInformation("Synchronising: {Downloaded:N0} Of {Total:N0} Bytes ({Percentage:F0}%)", bytesDownloaded, announcedTotalBytes, percentage);
    }

    /// <summary>
    ///     Returns the distribution variant code that matches the current operating system.
    /// </summary>
    public static string ResolveServerVariant(CDNOptions options) =>
          OperatingSystem.IsWindows() ? options.WindowsVariant
        : OperatingSystem.IsLinux()   ? options.LinuxVariant
        : throw new PlatformNotSupportedException("COMPEL Hosts Match Servers On Windows And Linux Only");
}
