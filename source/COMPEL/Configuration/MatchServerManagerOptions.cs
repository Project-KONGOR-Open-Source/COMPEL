namespace COMPEL.Configuration;

/// <summary>
///     The host-supplied configuration that governs how COMPEL launches and supervises the Heroes Of Newerth match server manager.
///     These are the cross-platform equivalents of the keys that the legacy COMPEL stored in its "COMPEL.JSON" file.
/// </summary>
public sealed class MatchServerManagerOptions
{
    /// <summary>
    ///     The name of the Project KONGOR user which will host the game servers.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    ///     The password of the Project KONGOR user which will host the game servers.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    ///     The number of server instances to spawn. Must be between one and the number of logical processors.
    /// </summary>
    public int Instances { get; set; } = 1;

    /// <summary>
    ///     The entry point for game servers and the server manager: "localhost" for local development, "PUBLIC" to auto-detect the public IP address, a LAN or public IP address, or a host name to resolve.
    /// </summary>
    public string Gateway { get; set; } = "kongor.net";

    /// <summary>
    ///     The server region. In order for the server to be TMM-compatible, only the values "USW", "USE", "EU", "AU", "BR", "RU", "SEA", and "NEWERTH" are valid.
    /// </summary>
    public string Location { get; set; } = "NEWERTH";

    /// <summary>
    ///     The base name of the game server instances. The server manager appends the one-based index of each instance to this base name.
    /// </summary>
    public string ServerNamePrefix { get; set; } = "KONGOR ARENA";

    /// <summary>
    ///     Whether to run the proxy in front of the game servers. The proxy exposes public ports offset by <see cref="PortPlan.ProxyPublicOffset"/>, forwards them to the local server ports, and authenticates clients with the challenge protocol they require on that port range.
    /// </summary>
    public bool UseProxy { get; set; } = true;

    /// <summary>
    ///     The offset from the start of the valid game and voice port ranges at which the ports used at runtime begin.
    /// </summary>
    public int PortRangeOffset { get; set; }

    /// <summary>
    ///     The base directory beneath which the match server manager and servers write their runtime artefacts (replays and logs). This value is either the alias "DEFAULT" or a fully qualified path used as the base profile directory.
    ///     "DEFAULT" places the artefacts beneath the host account's profile (its "Documents/Heroes of Newerth x64" tree), which is where Heroes Of Newerth writes everything else.
    ///     This applies on Windows only; the Linux server build writes to a fixed location and ignores this setting.
    /// </summary>
    public string RuntimeArtefactsPath { get; set; } = "DEFAULT";
}
