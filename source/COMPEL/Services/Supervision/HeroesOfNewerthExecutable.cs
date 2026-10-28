namespace COMPEL.Services.Supervision;

/// <summary>
///     Resolves the Heroes Of Newerth match server executable name for the current operating system.
///     The same executable runs the manager (with the "-manager" flag) and the dedicated server instances it spawns.
///     The match server distribution's build of it runs without the game client's X11 dependencies on Linux; its name carries no architecture and no role.
/// </summary>
public static class HeroesOfNewerthExecutable
{
    public static string FileName =>
          OperatingSystem.IsWindows() ? "hon.exe"
        : OperatingSystem.IsLinux()   ? "hon"
        : throw new PlatformNotSupportedException("COMPEL Hosts Match Servers On Windows And Linux Only");

    /// <summary>
    ///     Whether the manager, launched from <paramref name="directory"/>, can spawn server instances that receive the configuration it passes them.
    ///     On Windows the manager spawns each instance with an unquoted executable path, which Heroes Of Newerth only parses correctly when that path contains a whitespace character.
    ///     Without one, every instance loses the flags and the configuration payload the manager passes it, starts as a game client rather than a dedicated server, and never binds its game port.
    ///     On Linux the manager quotes the path it spawns, so any directory works.
    /// </summary>
    public static bool CanLaunchInstancesFrom(string directory)
        => OperatingSystem.IsWindows() is false || Path.Combine(directory, FileName).Any(char.IsWhiteSpace);
}
