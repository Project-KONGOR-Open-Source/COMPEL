namespace COMPEL.Tests;

/// <summary>
///     Verifies the log-file session header: the banner opens a fresh file, and each subsequent session is separated by its own marker without the banner being repeated.
/// </summary>
public sealed class BannerTests
{
    private const string SessionMarker = "COMPEL Session Started At";

    private static string TemporaryLogPath() => Path.Combine(Path.GetTempPath(), $"compel-log-{Guid.NewGuid():N}.log");

    [Test]
    public async Task A_Fresh_Log_File_Opens_With_The_Banner_And_A_Session_Marker()
    {
        string path = TemporaryLogPath();

        try
        {
            Banner.WriteToLogFile(path);

            string content = await File.ReadAllTextAsync(path);

            using (Assert.Multiple())
            {
                // The Banner Art Is The Only Content Containing Backslashes.
                await Assert.That(content.Contains('\\')).IsTrue();
                await Assert.That(content.Contains(SessionMarker)).IsTrue();
            }
        }

        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_Second_Session_Is_Separated_By_Its_Own_Marker_Without_Repeating_The_Banner()
    {
        string path = TemporaryLogPath();

        try
        {
            Banner.WriteToLogFile(path);
            Banner.WriteToLogFile(path);

            string content = await File.ReadAllTextAsync(path);

            int markerCount = (content.Length - content.Replace(SessionMarker, string.Empty).Length) / SessionMarker.Length;

            int firstMarker = content.IndexOf(SessionMarker, StringComparison.Ordinal);
            string afterFirstSession = content[(firstMarker + SessionMarker.Length)..];

            using (Assert.Multiple())
            {
                await Assert.That(markerCount).IsEqualTo(2);

                // The Banner (The Only Content With Backslashes) Must Not Appear Again After The First Session.
                await Assert.That(afterFirstSession.Contains('\\')).IsFalse();
            }
        }

        finally
        {
            File.Delete(path);
        }
    }
}
