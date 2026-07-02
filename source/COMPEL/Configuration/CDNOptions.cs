namespace COMPEL.Configuration;

/// <summary>
///     Configures the content delivery network from which the match server distribution is synchronised, and the local directory into which it is installed.
/// </summary>
public sealed class CDNOptions
{
    /// <summary>
    ///     The base URL of the content delivery network. Per-variant manifests are fetched from "{Host}/{variant}/manifest.json".
    /// </summary>
    public string Host { get; set; } = "https://cdn.kongor.net/";

    /// <summary>
    ///     The distribution variant code for the Windows match server.
    /// </summary>
    public string WindowsVariant { get; set; } = "was";

    /// <summary>
    ///     The distribution variant code for the Linux match server.
    /// </summary>
    public string LinuxVariant { get; set; } = "las";

    /// <summary>
    ///     The maximum number of files to download concurrently during a synchronisation.
    /// </summary>
    public int ParallelTransfers { get; set; } = 8;

    /// <summary>
    ///     The directory into which the match server distribution is installed and from which the manager executable is launched. Empty (the default) installs the distribution alongside the COMPEL executable; a relative path is resolved against the executable's directory, and a fully qualified path is used as-is.
    /// </summary>
    public string InstallationDirectory { get; set; } = string.Empty;

    /// <summary>
    ///     Whether to synchronise the distribution from the CDN on startup. When <see langword="false"/>, the initial synchronisation is skipped and the existing local distribution is used; on-demand synchronisation via the control plane is unaffected.
    /// </summary>
    public bool Synchronisation { get; set; } = true;
}
