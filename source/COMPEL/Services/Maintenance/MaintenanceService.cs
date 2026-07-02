namespace COMPEL.Services.Maintenance;

/// <summary>
///     Periodically removes the working directories of old replays beneath the artefacts directory, faithfully reproducing the legacy COMPEL's maintenance loop.
///     Unlike the original, the loop is bounded by the host's stopping token rather than running forever.
/// </summary>
public sealed class MaintenanceService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ReplayRetention = TimeSpan.FromMinutes(60);

    private readonly ArtefactsLocator artefacts;
    private readonly ILogger<MaintenanceService> logger;

    public MaintenanceService(ArtefactsLocator artefacts, ILogger<MaintenanceService> logger)
    {
        this.artefacts = artefacts;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested is false)
        {
            try { await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try { CleanUpOldReplays(); }
            catch (Exception exception) { logger.LogWarning(exception, "Replay Cleanup Failed"); }
        }
    }

    private void CleanUpOldReplays()
    {
        string directory = artefacts.ArtefactsDirectory;

        if (Directory.Exists(directory) is false)
            return;

        string[] files = Directory.GetFiles(directory, "*.honreplay", SearchOption.AllDirectories);

        foreach (string file in files)
        {
            if (File.GetLastWriteTime(file).Add(ReplayRetention) > DateTime.Now)
                continue;

            string? parent = Directory.GetParent(file)?.FullName;

            if (parent is null)
                continue;

            // The Replay's Working Directory Shares The File's Name With The "M" Characters Removed, As In The Original Maintenance Loop.
            string replayDirectory = Path.Combine(parent, Path.GetFileNameWithoutExtension(file).Replace("M", string.Empty));

            if (Directory.Exists(replayDirectory) is false)
                continue;

            try { Directory.Delete(replayDirectory, recursive: true); }
            catch (Exception exception) { logger.LogWarning(exception, "Failed To Delete Replay Directory {Directory}", replayDirectory); }
        }
    }
}
