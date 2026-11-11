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
}
