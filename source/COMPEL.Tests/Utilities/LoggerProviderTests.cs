namespace COMPEL.Tests.Utilities;

/// <summary>
///     Verifies that the hosted services' logger calls land in the log under their mapped category, with an exception recorded as its type and message.
/// </summary>
public sealed class LoggerProviderTests
{
    private static string TemporaryLogPath() => Path.Combine(Path.GetTempPath(), $"compel-log-{Guid.NewGuid():N}.log");

    [Test]
    public async Task A_Hosted_Service_Logger_Writes_Under_Its_Mapped_Category()
    {
        string path = TemporaryLogPath();

        try
        {
            Logger logger = new (path);

            using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(new LoggerProvider(logger)));

            factory.CreateLogger<UDPProxyService>().LogInformation("Proxy Forwarding {Instances} Instance(s)", 2);

            string[] lines = await File.ReadAllLinesAsync(path);

            await Assert.That(lines[^1].EndsWith("] [PROXY______] Proxy Forwarding 2 Instance(s)")).IsTrue();
        }

        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task An_Exception_Is_Appended_As_Its_Type_And_Message()
    {
        string path = TemporaryLogPath();

        try
        {
            Logger logger = new (path);

            using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(new LoggerProvider(logger)));

            factory.CreateLogger<MaintenanceService>().LogWarning(new IOException("Disc Full"), "Replay Cleanup Failed");

            string[] lines = await File.ReadAllLinesAsync(path);

            await Assert.That(lines[^1].EndsWith("] [MAINTENANCE] Replay Cleanup Failed :: IOException :: Disc Full")).IsTrue();
        }

        finally
        {
            File.Delete(path);
        }
    }
}
