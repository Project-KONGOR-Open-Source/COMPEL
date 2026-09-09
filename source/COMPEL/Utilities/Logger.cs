namespace COMPEL.Utilities;

/// <summary>
///     Writes timestamped, categorised entries to the console and to the log file on disc, in the format WILLOWMAKER uses.
///     Constructing the logger appends a session header, preceded by a blank line when the file already has content, so successive sessions read as distinct blocks.
/// </summary>
public sealed class Logger
{
    private string FilePath { get; }
    private Lock FileLock { get; } = new ();

    public Logger(string filePath)
    {
        FilePath = filePath;

        bool hasExistingContent = File.Exists(FilePath) && new FileInfo(FilePath).Length > 0;

        string sessionSeparator = hasExistingContent ? Environment.NewLine : string.Empty;

        File.AppendAllText(FilePath, sessionSeparator + $"▝▚▞▚▞▚▞▚▖ {DeploymentManifest.ApplicationName} Session Started At {DateTime.Now:O} ▗▞▚▞▚▞▚▞▘" + Environment.NewLine);
    }

    /// <summary>
    ///     Formats a timestamped log entry and writes it to the console and to the log file.
    /// </summary>
    public void Log(string category, string message)
    {
        string entry = $"[{DateTime.Now:O}] [{category}] {message}";

        lock (FileLock)
        {
            Console.WriteLine(entry);

            File.AppendAllText(FilePath, entry + Environment.NewLine);
        }
    }
}
