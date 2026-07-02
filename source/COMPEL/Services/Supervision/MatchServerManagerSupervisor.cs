namespace COMPEL.Services.Supervision;

/// <summary>
///     Launches and supervises the Heroes Of Newerth match server manager process.
///     It waits for the distribution to be synchronised, resolves the server address, then keeps the manager running, restarting it (with backoff) if it exits unexpectedly or fails to start in the first place.
///     The control plane drives <see cref="RequestStart"/>, <see cref="RequestStop"/>, and <see cref="RequestRestart"/>; on shutdown the manager and its spawned servers are stopped.
/// </summary>
public sealed class MatchServerManagerSupervisor : BackgroundService
{
    private static readonly TimeSpan RestartBackoff = TimeSpan.FromSeconds(5);

    private readonly MatchServerManagerOptions options;
    private readonly DistributionSynchronisationService distribution;
    private readonly UDPProxyService proxy;
    private readonly AddressResolver addressResolver;
    private readonly ArtefactsLocator artefacts;
    private readonly ILogger<MatchServerManagerSupervisor> logger;

    private readonly SemaphoreSlim reconcileSignal = new (0, int.MaxValue);
    private readonly SemaphoreSlim lifecycleGate = new (1, 1);

    private volatile bool desiredRunning = true;
    private volatile bool managerRunning;
    private Process? managerProcess;
    private long lastAttemptTicks;

    public MatchServerManagerSupervisor(IOptions<MatchServerManagerOptions> options, DistributionSynchronisationService distribution, UDPProxyService proxy, AddressResolver addressResolver, ArtefactsLocator artefacts, PortPlan ports, ILogger<MatchServerManagerSupervisor> logger)
    {
        this.options = options.Value;
        this.distribution = distribution;
        this.proxy = proxy;
        this.addressResolver = addressResolver;
        this.artefacts = artefacts;
        this.logger = logger;

        Ports = ports;
    }

    public PortPlan Ports { get; }

    public string? ServerAddress { get; private set; }

    // Backed By A Volatile Flag Maintained By The Launch, Exit, And Stop Paths Rather Than Reading "Process.HasExited" Live: The Control Plane And Health Checks Read This From Other Threads, And Touching A "Process" Instance That The Launch Or Stop Path Is Concurrently Disposing Would Throw.
    public bool IsRunning => managerRunning;

    /// <summary>
    ///     Whether the manager is intended to be running. Distinct from <see cref="IsRunning"/>: a crashed manager reports <see cref="IsRunning"/> as <see langword="false"/> while this stays <see langword="true"/> because the reconcile loop will relaunch it. The control plane requires this to be <see langword="false"/> before a synchronisation is accepted.
    /// </summary>
    public bool DesiredRunning => desiredRunning;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        KillOrphanedProcesses();

        logger.LogInformation("Waiting For The Match Server Distribution To Be Ready");

        try { await distribution.WaitUntilReady(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        // Resolve The Advertised Address, Retrying With Backoff Rather Than Giving Up: A Transient Failure (For Example A DNS Hiccup Or A Public-IP Lookup Timing Out) At Startup Must Not Permanently Disable The Supervisor While COMPEL Keeps Running And Reporting Success.
        while (stoppingToken.IsCancellationRequested is false)
        {
            try
            {
                ServerAddress = await addressResolver.ResolveServerAddress(stoppingToken).ConfigureAwait(false);

                break;
            }

            catch (OperationCanceledException)
            {
                return;
            }

            catch (Exception exception)
            {
                logger.LogError(exception, "Unable To Resolve The Server Address; Retrying In {Seconds} Second(s)", RestartBackoff.TotalSeconds);

                try { await Task.Delay(RestartBackoff, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        if (stoppingToken.IsCancellationRequested)
            return;

        await PingMasterServer(stoppingToken).ConfigureAwait(false);

        LogPortAllocation();

        // When The Proxy Is Enabled The Manager Advertises Public Ports (Local + 10000) That Only Work If The Proxy Bound Them. If No Forwarder Could Bind, Launching The Manager Would Register Unreachable Public Ports With The Master Server, So The Launch Is Refused Instead.
        if (options.UseProxy)
        {
            bool proxyReady;

            try { proxyReady = await proxy.WaitUntilReady(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (proxyReady is false)
            {
                logger.LogError("The Proxy Is Enabled But No Public Port Could Be Bound; The Manager Will Not Be Launched");

                return;
            }
        }

        // Event-Driven Reconcile Loop: Each Pass Brings The Process State Into Line With The Desired State, Then Waits For The Next Change (A Process Exit Or A Control-Plane Request).
        // Whenever The Manager Should Be Running But Isn't, The Wait Is Bounded By "RestartBackoff" So A Launch Failure (E.G. A Missing Executable) Retries Automatically Instead Of Stalling Forever With No Signal To Wake It.
        while (stoppingToken.IsCancellationRequested is false)
        {
            await Reconcile(stoppingToken).ConfigureAwait(false);

            TimeSpan wakeTimeout = desiredRunning && IsRunning is false ? RestartBackoff : Timeout.InfiniteTimeSpan;

            try { await reconcileSignal.WaitAsync(wakeTimeout, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        StopProcess();
    }

    private async Task Reconcile(CancellationToken stoppingToken)
    {
        await lifecycleGate.WaitAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            if (desiredRunning && IsRunning is false)
            {
                // Never Launch While A Synchronisation Is Rewriting The Installation Directory: The Servers Would Hold Files The Synchronisation Is Replacing. The Reconcile Loop Retries After The Backoff. This Is Not Counted As A Launch Attempt, So The Backoff Is Not Consumed While Waiting.
                if (distribution.IsSynchronising)
                    return;

                long elapsed = Environment.TickCount64 - lastAttemptTicks;

                if (lastAttemptTicks is not 0 && elapsed < RestartBackoff.TotalMilliseconds)
                {
                    TimeSpan wait = RestartBackoff - TimeSpan.FromMilliseconds(elapsed);

                    logger.LogWarning("Restarting The Match Server Manager In {Seconds:F0} Second(s)", wait.TotalSeconds);

                    try { await Task.Delay(wait, stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }

                // Recorded Before The Attempt, Not Only On Success, So A Launch That Throws Still Enforces The Backoff On The Next Attempt Rather Than Retrying In A Tight Loop.
                lastAttemptTicks = Environment.TickCount64;

                LaunchProcess();
            }

            else if (desiredRunning is false && IsRunning)
            {
                StopProcess();
            }
        }

        catch (Exception exception)
        {
            logger.LogError(exception, "Failed To Reconcile The Match Server Manager State");
        }

        finally
        {
            lifecycleGate.Release();
        }
    }

    private void LaunchProcess()
    {
        string executable = distribution.ManagerExecutablePath;

        if (File.Exists(executable) is false)
            throw new FileNotFoundException($@"Manager Executable Was Not Found At ""{executable}""");

        // The Synchronised Server Binary Arrives Without The Unix Execute Bit, So It Is Set Here Before Every Launch (On Windows This Is A No-Op).
        EnsureExecutable(executable);

        string address = ServerAddress ?? throw new InvalidOperationException("The Server Address Has Not Been Resolved");

        // Dispose The Previous Exited Process Object Before Replacing It.
        Process? previous = managerProcess;

        if (previous is not null)
        {
            previous.Exited -= OnProcessExited;
            previous.Dispose();
        }

        // Best-Effort: Heroes Of Newerth Creates Its Own Artefacts Directory On Launch, So A Failure To Pre-Create It (For Example A Permissions Issue On The Fixed Linux Location) Must Not Block The Launch.
        try { Directory.CreateDirectory(artefacts.ArtefactsDirectory); }
        catch (Exception exception) { logger.LogDebug(exception, "Could Not Pre-Create The Artefacts Directory {Directory}", artefacts.ArtefactsDirectory); }

        // Sweep Before Every Launch, Not Only At Startup: A Previous Manager's Spawned Servers Can Be Reparented (For Example To The Init Process) And So Escape "Process.Kill(entireProcessTree)", Leaving Them Holding The Ports This Launch Is About To Bind.
        KillOrphanedProcesses();

        string[] arguments = ManagerArguments.Build(options, Ports, address, addressResolver.MasterServerHostAndPort);

        ProcessStartInfo startInfo = new (executable)
        {
            WorkingDirectory = distribution.InstallationDirectory,
            UseShellExecute = false
        };

        // Windows Receives The Command Line Verbatim, So Heroes Of Newerth Sees The Literal Quotes Around The "-execute" Payload. On Linux ".NET" Strips Grouping Quotes From A Joined String, So Each Argument Is Passed Individually Via "ArgumentList" And The Payload Retains Its Own Literal Quotes When Heroes Of Newerth Rejoins Its Command Line.
        if (OperatingSystem.IsWindows())
            startInfo.Arguments = string.Join(' ', arguments);
        else
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

        // Heroes Of Newerth Derives Its Documents Tree From The Home Directory, So The Child's Home Is Redirected To The Resolved Artefacts Location.
        if (OperatingSystem.IsWindows())
            startInfo.EnvironmentVariables["USERPROFILE"] = artefacts.ProfileDirectory;
        else
            startInfo.EnvironmentVariables["HOME"] = artefacts.ProfileDirectory;

        Process process = new () { StartInfo = startInfo, EnableRaisingEvents = true };

        process.Exited += OnProcessExited;

        if (process.Start() is false)
            throw new InvalidOperationException("The Match Server Manager Process Failed To Start");

        managerProcess = process;
        managerRunning = true;

        logger.LogInformation("Launched The Match Server Manager (Process {ProcessID})", process.Id);

        // If The Process Exited Between "Start" And Now, The "Exited" Event May Have Already Run And Cleared The Running Flag Before This Method Set It. Re-Check So An Instantly-Exiting Manager Does Not Leave The State Stuck Reporting Running With No Live Process, And Wake The Reconcile Loop To Apply The Backoff And Relaunch.
        if (process.HasExited)
        {
            managerRunning = false;

            reconcileSignal.Release();
        }
    }

    /// <summary>
    ///     Ensures the executable carries the Unix execute bit before it is launched. This is a no-op on Windows, where the concept does not apply.
    /// </summary>
    private void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            UnixFileMode currentMode = File.GetUnixFileMode(path);
            UnixFileMode executableMode = currentMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

            if (executableMode != currentMode)
                File.SetUnixFileMode(path, executableMode);
        }

        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could Not Set The Execute Bit On {Path}", path);
        }
    }

    /// <summary>
    ///     Kills any match server manager process left over from a previous, uncleanly-terminated run (a crash, a forced kill, a host reboot), identified by matching its executable path against this installation's manager executable, so it cannot keep holding the ports the new manager is about to bind.
    /// </summary>
    private void KillOrphanedProcesses()
    {
        string executableName = Path.GetFileNameWithoutExtension(HeroesOfNewerthExecutable.FileName);

        // Linux Exposes The Process Name Via "/proc/[pid]/comm", Which Is Truncated To 15 Characters, So The Lookup Name Is Truncated To Match; The Executable-Path Comparison Below Still Confirms The Process Identity.
        if (OperatingSystem.IsLinux() && executableName.Length > 15)
            executableName = executableName[..15];

        string executablePath = distribution.ManagerExecutablePath;

        foreach (Process process in Process.GetProcessesByName(executableName))
        {
            try
            {
                string? modulePath = process.MainModule?.FileName;

                if (string.Equals(modulePath, executablePath, StringComparison.OrdinalIgnoreCase) is false)
                    continue;

                logger.LogWarning("Killing An Orphaned Match Server Process (Process {ProcessID}) Left Over From A Previous Run", process.Id);

                process.Kill(entireProcessTree: true);
            }

            catch (Exception exception)
            {
                logger.LogDebug(exception, "Failed To Inspect Or Kill A Potential Orphan Process (Process {ProcessID})", process.Id);
            }

            finally
            {
                process.Dispose();
            }
        }
    }

    private void StopProcess()
    {
        managerRunning = false;

        Process? process = Interlocked.Exchange(ref managerProcess, null);

        if (process is null)
            return;

        process.Exited -= OnProcessExited;

        try
        {
            if (process.HasExited is false)
            {
                logger.LogInformation("Stopping The Match Server Manager (Process {ProcessID})", process.Id);

                process.Kill(entireProcessTree: true);
            }
        }

        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed To Stop The Match Server Manager Cleanly");
        }

        finally
        {
            process.Dispose();
        }
    }

    private void OnProcessExited(object? sender, EventArgs eventArguments)
    {
        managerRunning = false;

        reconcileSignal.Release();
    }

    /// <summary>
    ///     Requests that the manager be running.
    /// </summary>
    public void RequestStart()
    {
        desiredRunning = true;

        reconcileSignal.Release();
    }

    /// <summary>
    ///     Requests that the manager be stopped and kept stopped.
    /// </summary>
    public void RequestStop()
    {
        desiredRunning = false;

        reconcileSignal.Release();
    }

    /// <summary>
    ///     Requests that the manager be restarted. Killing the running process triggers the reconcile loop to relaunch it.
    ///     Acquires the same lifecycle gate as <see cref="Reconcile"/> so this cannot race a concurrent launch or stop for the same process.
    /// </summary>
    public async Task RequestRestart(CancellationToken cancellationToken)
    {
        desiredRunning = true;

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Process? process = managerProcess;

            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }

        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed To Signal A Restart To The Match Server Manager");
        }

        finally
        {
            lifecycleGate.Release();
        }

        reconcileSignal.Release();
    }

    private async Task PingMasterServer(CancellationToken cancellationToken)
    {
        string host = addressResolver.MasterServerHost;

        try
        {
            using System.Net.NetworkInformation.Ping ping = new ();

            PingReply reply = await ping.SendPingAsync(host, TimeSpan.FromSeconds(5), cancellationToken: cancellationToken).ConfigureAwait(false);

            if (reply.Status is IPStatus.Success)
                logger.LogInformation("Master Server {Host} Is Reachable ({RoundtripTime} ms)", host, reply.RoundtripTime);

            else
                logger.LogWarning("Master Server {Host} Ping Returned {Status}; Proceeding Anyway", host, reply.Status);
        }

        catch (Exception exception)
        {
            logger.LogWarning(exception, "Master Server {Host} Could Not Be Pinged; Proceeding Anyway", host);
        }
    }

    private void LogPortAllocation()
    {
        logger.LogInformation
        (
            "Match Server Manager Configured: {Instances} Instance(s), Server Address {ServerAddress}, Master Server {MasterServer}",
            options.Instances, ServerAddress, addressResolver.MasterServerHostAndPort
        );

        logger.LogInformation
        (
            "Port Allocation: Game {LocalGameStart}-{LocalGameEnd}, Voice {LocalVoiceStart}-{LocalVoiceEnd}, Public Game {PublicGameStart}-{PublicGameEnd}, Public Voice {PublicVoiceStart}-{PublicVoiceEnd}, Ping {PingPort}{ProxyNote}",
            Ports.LocalGameStart, Ports.LocalGameEnd, Ports.LocalVoiceStart, Ports.LocalVoiceEnd,
            Ports.PublicGameStart, Ports.PublicGameEnd, Ports.PublicVoiceStart, Ports.PublicVoiceEnd,
            Ports.PingPort, Ports.UseProxy ? " (Proxy Enabled)" : string.Empty
        );

        logger.LogInformation("Runtime Artefacts Directory: {Directory}", artefacts.ArtefactsDirectory);

        if (artefacts.RuntimeArtefactsPathApplies is false)
            logger.LogInformation(@"The ""RuntimeArtefactsPath"" Setting Does Not Apply On This Platform; The Heroes Of Newerth Server Build Writes To A Fixed Location");
    }
}
