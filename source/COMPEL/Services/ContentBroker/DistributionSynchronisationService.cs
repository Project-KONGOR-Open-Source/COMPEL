namespace COMPEL.Services.ContentBroker;

/// <summary>
///     Keeps the local match server distribution synchronised with the CDN.
///     On startup it performs an initial synchronisation (retried with backoff, or skipped if a local copy already exists, or skipped entirely when <see cref="CDNOptions.Synchronisation"/> is disabled) and exposes <see cref="SynchroniseNow"/> for the control plane to trigger a re-synchronisation on demand.
///     The supervisor awaits <see cref="WaitUntilReady"/> before launching the manager.
/// </summary>
public sealed class DistributionSynchronisationService : BackgroundService
{
    /// <summary>
    ///     Stands in for the distribution version until it has been resolved from the manifest, which cannot happen while synchronisation is disabled or the CDN is unreachable.
    ///     It carries the same four full-stop-separated components a real version does, so a consumer that splits the field is not caught out by an absent value, while remaining impossible to mistake for a real version.
    /// </summary>
    public const string UnknownDistributionVersion = "?.?.?.?";

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan VersionRecoveryDelay = TimeSpan.FromMinutes(1);

    private readonly CDNOptions options;
    private readonly ILogger<DistributionSynchronisationService> logger;
    private readonly SemaphoreSlim gate = new (1, 1);
    private readonly TaskCompletionSource ready = new (TaskCreationOptions.RunContinuationsAsynchronously);

    public string InstallationDirectory { get; }

    public string Variant { get; }

    public string DistributionVersion { get; private set; } = UnknownDistributionVersion;

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

    // The Distribution Installs Alongside The COMPEL Executable By Default (An Empty Configured Directory), So It Sits Beside The Binary Rather Than In A Peer Folder
    // A Relative Path Is Resolved Against The Executable's Directory, And A Fully Qualified Path Is Honoured As-Is
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

            // The CDN Is Not Contacted At All Here, Not Even For The Version: Synchronisation Is Switched Off, So The Operator Has Asked For The Local Distribution To Be Used As-Is
            // The Version Therefore Stays At Its Placeholder Until An On-Demand Synchronisation Resolves It, Which Also Keeps Start-Up Immediate On A Host With No Network
            logger.LogInformation("INIT: The Distribution Version Is Not Resolved While Synchronisation Is Disabled; Pongs Will Advertise {Version}", UnknownDistributionVersion);

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

            // Synchronisation Is Still Enabled Here; Only The Mirroring Is Skipped, So The Version Is Worth Resolving
            // It Is Resolved After The Ready Gate Rather Than Before It, So An Unreachable CDN Delays Nothing; The Ping Responder Rebuilds Its Template As Soon As The Version Arrives
            try { await ResolveDistributionVersionWithoutSynchronising(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

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

                // With No Failed Files The Only Reason Left To Retry Is A Distribution That Does Not Contain The Manager Executable, Which The Per-File Lines Cannot Show
                if (summary.FilesFailed is 0)
                    logger.LogWarning(@"FAIL: The Manager Executable ""{Executable}"" Is Not Part Of The Synchronised Distribution", ManagerExecutablePath);

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

                    // The Manifest Fetch Is What Failed When The Version Is Still Unknown, So It Is Retried In The Background Rather Than Left Unknown For The Life Of The Process
                    if (DistributionVersion == UnknownDistributionVersion)
                        await RecoverDistributionVersion(stoppingToken).ConfigureAwait(false);

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
            {
                logger.LogWarning("FAIL: {Failures} File(s) Failed To Be Transferred", summary.FilesFailed);

                // The Lock Scan Is Diagnostic Only, So A Failure Inside It Must Not Turn A Partial Synchronisation Into An Exception That The Start-Up Loop Treats As A Reason To Proceed With The Existing Distribution
                try { LogLockingProcesses(summary.Failures); }
                catch (Exception exception) { logger.LogWarning("FAIL: The Lock Scan Failed :: {ExceptionType} :: {Message}", exception.GetType().Name, exception.Message); }
            }

            // Release Consumers Only Once Every File Transferred And The Manager Executable Is Present; A Synchronisation That Failed To Fetch Some Files Would Leave A Mixed-Version Tree, So The Manager Must Not Be Launched Against It
            if (summary.FilesFailed is 0 && File.Exists(ManagerExecutablePath))
                ready.TrySetResult();

            return summary;
        }

        catch (HttpRequestException exception)
        {
            // A DNS, Connection, Or TLS Failure Carries No HTTP Status, So The Exception Message Is Appended In That Case To Keep The Cause In The Log
            string statusCode = exception.StatusCode is not null
                ? $"{(int) exception.StatusCode} ({exception.StatusCode})"
                : $"Unknown Status Code :: {exception.Message}";

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

    /// <summary>
    ///     Fetches only the manifest so the distribution version is known when the files themselves are not synchronised.
    ///     The ping responder advertises that version, and clients discard any server whose version does not match their own, so an unknown version makes this host unselectable.
    /// </summary>
    private async Task ResolveDistributionVersionWithoutSynchronising(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation(@"INIT: Fetching Manifest For Variant ""{Variant}"" From CDN", Variant);

            Manifest manifest = await ContentBroker.FetchManifest(Variant, options.Host, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("INIT: Manifest Version {Version} Lists {Count} File(s)", manifest.Version, manifest.Files.Count);

            DistributionVersion = manifest.Version;
        }

        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("FAIL: {ExceptionType} :: {Message} :: Pongs Will Advertise No Version And Clients Will Not List This Server", exception.GetType().Name, exception.Message);
        }
    }

    /// <summary>
    ///     Retries the manifest fetch until the distribution version is known, for the case where the fetch failed and the existing local distribution was accepted.
    ///     Only the manifest is fetched, never the files, because the manager may already be running against this installation by the time this runs.
    ///     Each failed attempt is logged at debug level so a long outage does not flood the log; the ping responder rebuilds its template as soon as the version arrives.
    /// </summary>
    private async Task RecoverDistributionVersion(CancellationToken stoppingToken)
    {
        logger.LogWarning("FAIL: The Distribution Version Is Unknown; Pongs Will Advertise {Version} Until The Manifest Can Be Fetched", UnknownDistributionVersion);

        while (stoppingToken.IsCancellationRequested is false && DistributionVersion == UnknownDistributionVersion)
        {
            try { await Task.Delay(VersionRecoveryDelay, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            try
            {
                Manifest manifest = await ContentBroker.FetchManifest(Variant, options.Host, stoppingToken).ConfigureAwait(false);

                DistributionVersion = manifest.Version;

                logger.LogInformation("INIT: Manifest Version {Version} Was Recovered Without Synchronising", manifest.Version);
            }

            catch (OperationCanceledException)
            {
                return;
            }

            catch (Exception exception)
            {
                logger.LogDebug("FAIL: {ExceptionType} :: {Message}", exception.GetType().Name, exception.Message);
            }
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

    // The Launcher Shows The Processes Holding Failed Files In A Dialog Grouped By Application; The Console Equivalent Is One Line Per Application Naming The Files It Holds
    private void LogLockingProcesses(IReadOnlyList<SynchronisationFailure> failures)
    {
        const string unidentifiedProcessGroup = "Unidentified Process";

        Dictionary<string, LockGroup> groups = new (StringComparer.OrdinalIgnoreCase);

        LockGroup GroupFor(string applicationName)
        {
            if (groups.TryGetValue(applicationName, out LockGroup? group) is false)
            {
                group = new LockGroup();

                groups.Add(applicationName, group);
            }

            return group;
        }

        foreach (SynchronisationFailure failure in failures)
        {
            string absolutePath = Path.IsPathRooted(failure.Path)
                ? failure.Path
                : Path.GetFullPath(Path.Combine(InstallationDirectory, failure.Path));

            if (File.Exists(absolutePath) is false)
                continue;

            string displayPath = Path.GetRelativePath(InstallationDirectory, absolutePath);

            List<FileLockingProcess> lockingProcesses = FileLockDetector.GetLockingProcesses(absolutePath);

            if (lockingProcesses.Count is 0)
            {
                // No Locking Process Was Identified, So The File Is Only Surfaced When It Is Genuinely Still Locked; This Filters Out Failures Caused By Other Reasons (Such As A Hash Mismatch Or An Unreachable CDN) While Still Reporting A Lock Whose Owner Could Not Be Determined
                if (FileIsLocked(absolutePath))
                    GroupFor(unidentifiedProcessGroup).FilePaths.Add(displayPath);

                continue;
            }

            foreach (FileLockingProcess lockingProcess in lockingProcesses)
            {
                // Every Instance Of The Same Executable Is Collapsed Into One Group Keyed By Its Application Name; Its Distinct Process IDs Are Counted So The Operator Knows How Many Instances Need To Be Closed
                LockGroup group = GroupFor(lockingProcess.ApplicationName);

                group.ProcessIDs.Add(lockingProcess.ProcessID);
                group.FilePaths.Add(displayPath);
            }
        }

        foreach ((string applicationName, LockGroup group) in groups)
        {
            string processName = group.ProcessIDs.Count > 1
                ? $"{applicationName} ({group.ProcessIDs.Count} Processes)"
                : applicationName;

            logger.LogWarning("FAIL: {ProcessName} Holds {Files}", processName, string.Join(", ", group.FilePaths));
        }
    }

    private static bool FileIsLocked(string path)
    {
        try
        {
            // Opening With No Sharing Fails When Any Other Handle To The File Is Already Open, Which Is The Defining Symptom Of A Lock Held By Another Process; Read Access Is Requested So The Read-Only Attribute Does Not Interfere
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);

            return false;
        }

        catch (IOException)
        {
            return true;
        }

        catch
        {
            // Swallowed Deliberately: An Inability To Open The File For Reasons Other Than Sharing (Such As Insufficient Permissions) Must Not Be Misreported As A Lock
            return false;
        }
    }

    /// <summary>
    ///     Accumulates the distinct locking process IDs and the locked file paths for a single application while the locking processes are being scanned.
    /// </summary>
    private sealed class LockGroup
    {
        public HashSet<int> ProcessIDs { get; } = [];

        public HashSet<string> FilePaths { get; } = new (StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns the distribution variant code that matches the current operating system.
    /// </summary>
    public static string ResolveServerVariant(CDNOptions options) =>
          OperatingSystem.IsWindows() ? options.WindowsVariant
        : OperatingSystem.IsLinux()   ? options.LinuxVariant
        : throw new PlatformNotSupportedException("COMPEL Hosts Match Servers On Windows And Linux Only");
}
