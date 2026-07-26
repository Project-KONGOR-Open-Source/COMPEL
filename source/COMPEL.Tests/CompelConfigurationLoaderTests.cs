namespace COMPEL.Tests;

/// <summary>
///     Verifies that the configuration file round-trips its values, tolerates a null setting by falling back to its default, and reports malformed JSON as a clean error.
/// </summary>
public sealed class CompelConfigurationLoaderTests
{
    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"compel-configuration-{Guid.NewGuid():N}.json");

    [Test]
    public async Task A_Generated_Default_File_Loads_Back_With_Its_Default_Values()
    {
        string path = TemporaryPath();

        try
        {
            CompelConfigurationLoader.CreateDefault(path);

            CompelConfigurationFile file = CompelConfigurationLoader.Load(path);

            using (Assert.Multiple())
            {
                await Assert.That(file.UseProxy.Value).IsTrue();
                await Assert.That(file.RuntimeArtefactsPath.Value).IsEqualTo("DEFAULT");
                await Assert.That(file.AuthenticationToken.Value).IsEqualTo("...");
                await Assert.That(file.ControlPlanePort.Value).IsEqualTo(8080);
                await Assert.That(file.IdleTarget.Value).IsEqualTo(1);
            }
        }

        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_Setting_Explicitly_Set_To_Null_Falls_Back_To_Its_Default()
    {
        string path = TemporaryPath();

        try
        {
            File.WriteAllText(path, """{ "UserName": null }""");

            CompelConfigurationFile file = CompelConfigurationLoader.Load(path);

            using (Assert.Multiple())
            {
                await Assert.That(file.UserName).IsNotNull();
                await Assert.That(file.UserName.Value).IsEqualTo("USERNAME");
            }
        }

        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Malformed_JSON_Is_Reported_As_An_Invalid_Operation_Rather_Than_A_Raw_Parse_Error()
    {
        string path = TemporaryPath();

        try
        {
            File.WriteAllText(path, "{ this is not valid json");

            bool threw = false;

            try
            {
                CompelConfigurationLoader.Load(path);
            }

            catch (InvalidOperationException)
            {
                threw = true;
            }

            await Assert.That(threw).IsTrue();
        }

        finally
        {
            File.Delete(path);
        }
    }
}
