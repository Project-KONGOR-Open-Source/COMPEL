namespace COMPEL.Services;

/// <summary>
///     Checks for a newer COMPEL release before the host starts, and offers to self-update when the console is interactive.
///     Declining, timing out, running non-interactively, and every failure path all let startup continue; an accepted update exits the process into the update script and never returns.
/// </summary>
internal static class UpdateGate
{
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The console equivalent of the launcher's update dialog: the operator is prompted with <c>[U]pdate / [N]ot Now</c> and a countdown, with any key other than <c>U</c>, the countdown elapsing, or a non-interactive console all treated as "Not Now".
    /// </summary>
    public static async Task CheckForUpdates(SingleInstanceGuard singleInstanceGuard)
    {
        Console.WriteLine($"Current Version: {VersionChecker.CurrentVersionDisplay}");
        Console.WriteLine("Checking For Updates ...");

        VersionCheckResult result;

        try
        {
            result = await VersionChecker.CheckForLatestVersion();
        }

        catch (HttpRequestException httpException)
        {
            string statusCode = httpException.StatusCode is not null
                ? $"{(int) httpException.StatusCode} ({httpException.StatusCode})"
                : "Unknown Status Code";

            Console.WriteLine($"The COMPEL Releases Repository Is Not Reachable: HTTP {statusCode}");

            return;
        }

        catch (Exception exception)
        {
            Console.WriteLine($"The COMPEL Releases Repository Is Not Reachable: {exception.GetType().Name}");

            return;
        }

        if (result.IsUpdateAvailable is false || result.LatestVersion is null)
        {
            Console.WriteLine("COMPEL Is Up To Date");

            return;
        }

        string latestVersionDisplay = $"v{result.LatestVersion.Major}.{result.LatestVersion.Minor}.{result.LatestVersion.Build}";

        Console.WriteLine($"Update Available: {latestVersionDisplay}");

        // A Non-Interactive Console (For Example A Service Manager Or A Redirected Pipeline) Cannot Answer A Prompt, So The Update Is Only Announced And Startup Continues
        if (Console.IsInputRedirected)
            return;

        if (PromptForUpdate(latestVersionDisplay) is false)
        {
            Console.WriteLine("Update Skipped By User");

            return;
        }

        Console.WriteLine("Update Accepted By User");

        if (result.DownloadURL is null)
        {
            Console.WriteLine($"No Downloadable Asset Found; Get The Update From {result.ReleasePageURL}");

            return;
        }

        try
        {
            Console.WriteLine($"Downloading Update From {result.DownloadURL} ...");

            IProgress<double>? downloadProgress = Console.IsOutputRedirected
                ? null
                : new SynchronousProgress<double>(percent => Console.Write($"\rDownloading {latestVersionDisplay}: {(int) percent,3}%"));

            string archivePath = await VersionChecker.DownloadUpdate(result.DownloadURL, downloadProgress);

            if (Console.IsOutputRedirected is false)
                Console.WriteLine($"\rDownloading {latestVersionDisplay}: 100%");

            Console.WriteLine("Restarting Into The Update Script ...");

            // The Lock Is Released Before The Process Exits So The Relaunched Instance Never Races Against Operating-System Handle Cleanup
            singleInstanceGuard.Dispose();

            VersionChecker.ApplyUpdateAndRestart(archivePath);
        }

        catch (Exception exception)
        {
            Console.WriteLine($"Update Failed: {exception.Message}");
        }
    }

    // Reads A Single Decision Key With A Visible Countdown, Defaulting To "Not Now" When The Countdown Elapses So An Unattended Terminal Never Blocks Startup
    private static bool PromptForUpdate(string latestVersionDisplay)
    {
        long deadline = Environment.TickCount64 + (long) PromptTimeout.TotalMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            int secondsRemaining = (int) Math.Ceiling((deadline - Environment.TickCount64) / 1000.0);

            Console.Write($"\rCOMPEL {latestVersionDisplay} Is Available. [U]pdate / [N]ot Now ({secondsRemaining,2}s) ");

            if (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                Console.WriteLine();

                return key.Key is ConsoleKey.U;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(100));
        }

        Console.WriteLine();

        return false;
    }

    // "Progress<T>" Posts Its Callbacks Asynchronously, Which Could Interleave A Stale Percentage With The Lines Printed After The Download Completes, So The Renderer Reports Synchronously Instead
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
