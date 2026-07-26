namespace COMPEL.Services;

/// <summary>
///     Single source of truth for product-name string literals, deployment file naming conventions, and the platform-specific executable name that COMPEL ships as.
/// </summary>
public static class DeploymentManifest
{
    public const string ApplicationName            = "COMPEL";
    public const string ConfigurationFileName      = "COMPEL.json";
    public const string LogFileName                = "COMPEL.log";
    public const string LockFileName               = "COMPEL.lock";
    public const string UpdateArchiveFileName      = "COMPEL.update.zip";
    public const string UpdateExtractDirectoryName = "COMPEL.update";

    /// <summary>
    ///     The name of the COMPEL executable on the current platform.
    /// </summary>
    public static string ApplicationExecutableFileName =>
          OperatingSystem.IsWindows() ? $"{ApplicationName}.exe"
        : OperatingSystem.IsLinux()   ? ApplicationName
        : throw new PlatformNotSupportedException("COMPEL Hosts Match Servers On Windows And Linux Only");

    /// <summary>
    ///     File names that the location guard ignores when assessing a baseline deployment directory.
    ///     These are not part of the distribution payload and the guard does not require their presence; their presence simply does not classify the directory as unsafe.
    /// </summary>
    public static string[] IgnoredFileNames => [ ConfigurationFileName, LogFileName, LockFileName ];

    /// <summary>
    ///     Indicates whether the current build is a development build (any configuration other than Release) rather than a release build (the Release configuration, which is how releases are published).
    /// </summary>
    public static bool IsDevelopmentBuild =>
    # if DEVELOPMENT
    true;
    # else
    false;
    # endif
}
