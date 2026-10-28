namespace COMPEL.Tests.Services.Supervision;

/// <summary>
///     Verifies the configuration file that COMPEL writes for the manager and its match servers before every launch.
/// </summary>
public sealed class CustomConfigurationTests
{
    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"COMPEL Custom Configuration {Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        return directory;
    }

    [Test]
    public async Task The_File_Holds_The_Settings_Every_Process_Shares()
    {
        string installation = CreateTemporaryDirectory();

        try
        {
            string path = CustomConfiguration.Write(installation);

            string content = await File.ReadAllTextAsync(path);

            using (Assert.Multiple())
            {
                await Assert.That(path).IsEqualTo(Path.Combine(installation, "KONGOR", "configuration", "autoexec.cfg"));
                await Assert.That(content).Contains("set sys_dumpOnFatal true");
                await Assert.That(content).Contains("set con_verbose true");
                await Assert.That(content).DoesNotContain("setsave");
            }
        }

        finally
        {
            Directory.Delete(installation, recursive: true);
        }
    }

    [Test]
    public async Task An_Edited_File_Is_Rewritten()
    {
        string installation = CreateTemporaryDirectory();

        try
        {
            string path = CustomConfiguration.Write(installation);

            await File.WriteAllTextAsync(path, "set svr_port 1");

            CustomConfiguration.Write(installation);

            await Assert.That(await File.ReadAllTextAsync(path)).DoesNotContain("svr_port");
        }

        finally
        {
            Directory.Delete(installation, recursive: true);
        }
    }
}
