namespace COMPEL.Services.Supervision;

/// <summary>
///     Resolves where the match server manager and its server instances write their runtime artefacts (replays and logs).
///     On Windows, Heroes Of Newerth derives its "Documents/Heroes of Newerth x64" tree from the home directory it is given, so COMPEL sets the child process's home (USERPROFILE) to the resolved <see cref="ProfileDirectory"/> and the configured runtime artefacts path takes effect.
///     On Linux, the server build of Heroes Of Newerth writes to a fixed location (<see cref="LinuxServerArtefactsDirectory"/>) regardless of the home directory, so the configured runtime artefacts path does not apply there and <see cref="ArtefactsDirectory"/> reports that fixed location.
/// </summary>
public sealed class ArtefactsLocator
{
    // The Leaf Beneath The Documents Folder Is Determined By Heroes Of Newerth Itself; COMPEL Only Controls The Home Directory The Tree Hangs From (On Windows).
    private const string HeroesOfNewerthDirectoryName = "Heroes of Newerth x64";

    // The Linux Server Build Of Heroes Of Newerth Writes Its Runtime Artefacts Beneath This Fixed Directory, Ignoring The Home Directory, So The Home Redirect And The Configured Runtime Artefacts Path Have No Effect On Linux.
    private const string LinuxServerArtefactsDirectory = "/opt/hon/config";

    private readonly MatchServerManagerOptions options;

    public ArtefactsLocator(IOptions<MatchServerManagerOptions> options) => this.options = options.Value;

    /// <summary>
    ///     The directory to set as the child process's home. On Windows this steers where Heroes Of Newerth writes its "Documents/Heroes of Newerth x64" tree, so the configured runtime artefacts path takes effect. On Linux the server build writes to a fixed location regardless, so the real user profile is used and the configured path does not apply.
    /// </summary>
    public string ProfileDirectory => OperatingSystem.IsWindows()
        ? ResolveConfiguredProfileDirectory()
        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    ///     The directory beneath which Heroes Of Newerth writes its runtime artefacts, used by the maintenance loop to clean up old replays. On Windows it is derived from the configured runtime artefacts path; on Linux it is the server build's fixed location.
    /// </summary>
    public string ArtefactsDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(ProfileDirectory, "Documents", HeroesOfNewerthDirectoryName)
        : LinuxServerArtefactsDirectory;

    /// <summary>
    ///     Whether the configured runtime artefacts path is honoured on the current platform. It applies on Windows; on Linux the server build writes to a fixed location and the setting is ignored.
    /// </summary>
    public bool RuntimeArtefactsPathApplies => OperatingSystem.IsWindows();

    private string ResolveConfiguredProfileDirectory() => options.RuntimeArtefactsPath.ToUpperInvariant() switch
    {
        "DEFAULT" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        _         => options.RuntimeArtefactsPath
    };
}
