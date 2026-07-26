namespace COMPEL.Services;

/// <summary>
///     Refuses to launch COMPEL from a directory whose contents do not match either an existing Heroes Of Newerth match server installation or a fresh COMPEL-only deployment.
///     Prevents the content synchronisation process from deleting unrelated user files when the operator has accidentally placed COMPEL in a personal directory.
/// </summary>
public static class LocationGuard
{
    /// <summary>
    ///     The maximum number of foreign entries to surface in user-facing displays.
    ///     Lists longer than this cap are truncated and the remaining count is summarised with a trailing sentinel produced by <see cref="ApplyForeignEntriesDisplayCap"/>.
    /// </summary>
    public const int MaximumCountOfForeignEntriesToDisplay = 25;

    /// <summary>
    ///     The outcome of a location safety assessment.
    ///     <paramref name="Verdict"/> classifies the directory.
    ///     <paramref name="Reason"/> is a short, human-readable justification suitable for logging.
    ///     <paramref name="ForeignEntries"/> lists the top-level entries that caused an <see cref="LocationSafetyVerdict.Unsafe"/> verdict (empty for every other verdict).
    /// </summary>
    public sealed record Result(LocationSafetyVerdict Verdict, string Reason, IReadOnlyList<string> ForeignEntries);

    /// <summary>
    ///     Truncates <paramref name="foreignEntries"/> to at most <see cref="MaximumCountOfForeignEntriesToDisplay"/> items.
    ///     When the input exceeds the cap, a trailing summary entry of the form <c>"... and N more"</c> is appended in place of the omitted names.
    /// </summary>
    public static IReadOnlyList<string> ApplyForeignEntriesDisplayCap(IReadOnlyList<string> foreignEntries)
    {
        if (foreignEntries.Count <= MaximumCountOfForeignEntriesToDisplay)
            return foreignEntries;

        int remaining = foreignEntries.Count - MaximumCountOfForeignEntriesToDisplay;

        return [.. foreignEntries.Take(MaximumCountOfForeignEntriesToDisplay), $"... and {remaining} more"];
    }

    /// <summary>
    ///     Evaluates the supplied directory against the safety criteria, in order:
    ///     1) Development build → DEVELOPMENT ENVIRONMENT. The application is allowed to run, but operations that modify the directory are skipped.
    ///     2) Heroes Of Newerth match server executable present at the top level → SAFE. The directory is an existing installation.
    ///     3) The directory contains only the COMPEL distribution files and ignored runtime artefacts → SAFE. The directory is a fresh deployment ready for first-time synchronisation.
    ///     4) Otherwise → UNSAFE. The directory contains foreign content that the synchronisation process would put at risk.
    /// </summary>
    public static Result AssessLocationSafety(string directory)
    {
        if (DeploymentManifest.IsDevelopmentBuild)
            return new Result(LocationSafetyVerdict.DevelopmentEnvironment, "Development Environment Detected", []);

        string gameExecutableName = HeroesOfNewerthExecutable.FileName;

        if (File.Exists(Path.Combine(directory, gameExecutableName)))
            return new Result(LocationSafetyVerdict.Safe, $@"Heroes Of Newerth Directory (""{gameExecutableName}"" Is Present)", []);

        IReadOnlyList<string> foreignEntries = EnumerateForeignEntries(directory);

        if (foreignEntries.Count is 0)
            return new Result(LocationSafetyVerdict.Safe, $"Baseline {DeploymentManifest.ApplicationName} Directory", []);

        return new Result(LocationSafetyVerdict.Unsafe, "Foreign Entries Detected", foreignEntries);
    }

    /// <summary>
    ///     Returns the top-level entries in <paramref name="directory"/> that are neither part of COMPEL's distribution payload (the executable) nor on the ignore list (runtime artefacts such as the configuration, log, and lock files).
    ///     Each entry is reported as a path relative to <paramref name="directory"/>, which for the top-level enumeration is the entry's bare name.
    ///     Subdirectory entries carry a trailing <c>/**</c> suffix so the calling display and the log can distinguish them from same-named files.
    ///     The enumeration is configured to surface hidden and system entries as well, so that no foreign content can slip past the guard by virtue of an attribute flag.
    ///     Subdirectories are always treated as foreign, regardless of their name, because the recognised list is defined in terms of distribution files.
    ///     An empty subdirectory tree (no files at any depth) is omitted from the foreign-entries list because removing it cannot cause data loss.
    /// </summary>
    internal static IReadOnlyList<string> EnumerateForeignEntries(string directory)
    {
        HashSet<string> recognisedNames = new (StringComparer.OrdinalIgnoreCase) { DeploymentManifest.ApplicationExecutableFileName };

        foreach (string ignoredFileName in DeploymentManifest.IgnoredFileNames)
            recognisedNames.Add(ignoredFileName);

        EnumerationOptions topLevelEnumerationOptions = new ()
        {
            AttributesToSkip      = FileAttributes.None,
            RecurseSubdirectories = false
        };

        EnumerationOptions recursiveEnumerationOptions = new ()
        {
            AttributesToSkip      = FileAttributes.None,
            RecurseSubdirectories = true
        };

        List<string> foreignEntries = [];

        foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", topLevelEnumerationOptions))
        {
            if (entry is FileInfo)
            {
                if (recognisedNames.Contains(entry.Name) is false)
                    foreignEntries.Add(Path.GetRelativePath(directory, entry.FullName));
            }

            else if (Directory.EnumerateFiles(entry.FullName, "*", recursiveEnumerationOptions).Any())
            {
                foreignEntries.Add($"{Path.GetRelativePath(directory, entry.FullName)}/**");
            }
        }

        return foreignEntries;
    }
}
