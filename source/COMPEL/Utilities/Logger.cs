namespace COMPEL.Utilities;

/// <summary>
///     Writes timestamped, categorised entries to the console and to the log file on disc, in the format WILLOWMAKER uses.
///     Constructing the logger appends a session header, preceded by a blank line when the file already has content, so successive sessions read as distinct blocks.
/// </summary>
public sealed class Logger
{
    private string FilePath { get; }
    private Lock FileLock { get; } = new ();

    private bool fileWriteFailureReported;

    public Logger(string filePath)
    {
        FilePath = filePath;

        bool hasExistingContent = File.Exists(FilePath) && new FileInfo(FilePath).Length > 0;

        string sessionSeparator = hasExistingContent ? Environment.NewLine : string.Empty;

        File.AppendAllText(FilePath, sessionSeparator + $"▝▚▞▚▞▚▞▚▖ {DeploymentManifest.ApplicationName} Session Started At {DateTime.Now:O} ▗▞▚▞▚▞▚▞▘" + Environment.NewLine);
    }

    /// <summary>
    ///     Formats a timestamped log entry and writes it to the console and to the log file.
    ///     The file write is best-effort: a locked or full log file degrades logging rather than throwing into the hosted service that called it, which would stop the host.
    /// </summary>
    public void Log(string category, string message)
    {
        string entry = $"[{DateTime.Now:O}] [{category}] {message}";

        lock (FileLock)
        {
            Console.WriteLine(entry);

            try
            {
                File.AppendAllText(FilePath, entry + Environment.NewLine);
            }

            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Reported Once Per Session So A Persistently Unwritable Log File Does Not Fill The Console With The Same Failure
                if (fileWriteFailureReported is false)
                {
                    fileWriteFailureReported = true;

                    Console.WriteLine($@"COMPEL Could Not Write To Its Log File ""{FilePath}"": {exception.Message}");
                }
            }
        }
    }
}
