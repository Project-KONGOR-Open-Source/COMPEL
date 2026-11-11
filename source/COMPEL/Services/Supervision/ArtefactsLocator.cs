namespace COMPEL.Services.Supervision;

/// <summary>
///     Resolves where the match server manager and its server instances write their runtime artefacts (replays and logs).
///     Heroes Of Newerth keeps its root in "HON" on Windows and in ".HON" on Linux, for every role, and COMPEL passes the resolved <see cref="ArtefactsDirectory"/> to the manager as its user folder, so the configured runtime artefacts path takes effect on both platforms.
///     Within the root, the manager writes to its own "server-manager" folder and its match servers to "server", side by side.
/// </summary>
public sealed class ArtefactsLocator
{
    // The Root's Name Is Determined By Heroes Of Newerth Itself; COMPEL Only Controls The Directory It Hangs From
    private static string RootDirectoryName => OperatingSystem.IsWindows() ? "HON" : ".HON";

    private readonly MatchServerManagerOptions options;

    public ArtefactsLocator(IOptions<MatchServerManagerOptions> options) => this.options = options.Value;

    /// <summary>
    ///     The root in which Heroes Of Newerth writes its runtime artefacts, which COMPEL creates before every launch and passes to the manager as its user folder.
    /// </summary>
    public string ArtefactsDirectory => Path.Combine(options.RuntimeArtefactsPath.ToUpperInvariant() switch
    {
        "DEFAULT" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        _         => options.RuntimeArtefactsPath
    }, RootDirectoryName);

    /// <summary>
    ///     The match servers' replays, which the maintenance loop cleans up. The root also holds the game client's and the editors' folders on a machine that plays as well as hosts, so only these are swept.
    /// </summary>
    public string ReplaysDirectory => Path.Combine(ArtefactsDirectory, "server", "replays");
}
