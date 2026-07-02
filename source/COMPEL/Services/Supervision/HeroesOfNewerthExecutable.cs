namespace COMPEL.Services.Supervision;

/// <summary>
///     Resolves the Heroes Of Newerth match server executable name for the current operating system.
///     The same executable runs the manager (with the "-manager" flag) and the dedicated server instances it spawns.
///     On Linux this is the dedicated server build ("hon-x86_64-server"), not the client build ("hon-x86_64"): only the server build writes its artefacts to the fixed location and runs without the client's X11 dependencies.
/// </summary>
public static class HeroesOfNewerthExecutable
{
    public static string FileName =>
          OperatingSystem.IsWindows() ? "hon_x64.exe"
        : OperatingSystem.IsLinux()   ? "hon-x86_64-server"
        : throw new PlatformNotSupportedException("COMPEL Hosts Match Servers On Windows And Linux Only");
}
