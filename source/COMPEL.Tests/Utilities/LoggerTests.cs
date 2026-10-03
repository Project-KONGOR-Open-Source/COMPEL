namespace COMPEL.Tests.Utilities;

/// <summary>
///     Verifies the log file layout: a session header opens each session, sessions are separated by a blank line, and entries carry a timestamp and a fixed-width category.
/// </summary>
public sealed class LoggerTests
{
    private const string SessionMarker = "COMPEL Session Started At";

    private static string TemporaryLogPath() => Path.Combine(Path.GetTempPath(), $"compel-log-{Guid.NewGuid():N}.log");

    [Test]
    public async Task A_Fresh_Log_File_Opens_With_A_Session_Header()
    {
        string path = TemporaryLogPath();

        try
        {
            _ = new Logger(path);

            string content = await File.ReadAllTextAsync(path);

            using (Assert.Multiple())
            {
                await Assert.That(content.StartsWith('▝')).IsTrue();
                await Assert.That(content.Contains(SessionMarker)).IsTrue();
            }
        }

        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_Second_Session_Is_Separated_From_The_First_By_A_Blank_Line()
    {
        string path = TemporaryLogPath();

        try
        {
            _ = new Logger(path);
            _ = new Logger(path);

            string content = await File.ReadAllTextAsync(path);

            int markerCount = (content.Length - content.Replace(SessionMarker, string.Empty).Length) / SessionMarker.Length;

            using (Assert.Multiple())
            {
                await Assert.That(markerCount).IsEqualTo(2);
                await Assert.That(content.Contains(Environment.NewLine + Environment.NewLine + "▝")).IsTrue();
            }
        }

        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task An_Entry_Carries_A_Timestamp_And_Its_Category()
    {
        string path = TemporaryLogPath();

        try
        {
            Logger logger = new (path);

            logger.Log(LogCategory.Synchronise, "PLAN: 1 To Download");

            string[] lines = await File.ReadAllLinesAsync(path);

            await Assert.That(Regex.IsMatch(lines[^1], @"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+[+-]\d{2}:\d{2}\] \[SYNCHRONISE\] PLAN: 1 To Download$")).IsTrue();
        }

        finally
        {
            File.Delete(path);
        }
    }
}
