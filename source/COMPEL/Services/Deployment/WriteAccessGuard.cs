namespace COMPEL.Services.Deployment;

/// <summary>
///     Verifies that COMPEL can write to the directories it depends on writing to, before anything that needs them is started.
///     This is the precondition COMPEL actually has: it mirrors the match server distribution into its installation directory, writes its own log and lock files beside its executable, and replaces those files during a self-update.
/// </summary>
public static class WriteAccessGuard
{
    /// <summary>
    ///     Whether <paramref name="directory"/> can be written to.
    ///     The answer is established by creating and removing a uniquely-named probe file rather than by inspecting permissions, so ownership, access control lists, and read-only mounts are all accounted for without platform-specific code.
    ///     A directory that does not exist yet is created, because the location the Linux match server writes to is fixed and may legitimately be absent on a first run.
    /// </summary>
    public static bool CanWriteTo(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            string probePath = Path.Combine(directory, $"{DeploymentManifest.ApplicationName}.{Guid.NewGuid():N}.probe");

            using (FileStream probe = new (probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                probe.WriteByte(0);

            File.Delete(probePath);

            return true;
        }

        catch (IOException)
        {
            return false;
        }

        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Returns the first directory COMPEL must be able to write to but cannot, or <see langword="null"/> when every one of them is writable.
    ///     On Linux the match server writes its runtime artefacts to a fixed location regardless of the home directory it is given, so that location is checked as well as the installation directory.
    /// </summary>
    public static string? FindUnwritableDirectory(string installationDirectory)
    {
        if (CanWriteTo(installationDirectory) is false)
            return installationDirectory;

        if (OperatingSystem.IsLinux() && CanWriteTo(ArtefactsLocator.LinuxServerArtefactsDirectory) is false)
            return ArtefactsLocator.LinuxServerArtefactsDirectory;

        return null;
    }
}
