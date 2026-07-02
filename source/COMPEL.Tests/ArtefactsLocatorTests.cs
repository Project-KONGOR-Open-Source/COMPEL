namespace COMPEL.Tests;

/// <summary>
///     Verifies where the artefacts locator resolves the child process's profile and artefacts directories on each platform.
/// </summary>
public sealed class ArtefactsLocatorTests
{
    private static ArtefactsLocator Locator(string runtimeArtefactsPath)
        => new (Options.Create(new MatchServerManagerOptions { RuntimeArtefactsPath = runtimeArtefactsPath }));

    [Test]
    public async Task On_Windows_The_Default_Alias_Places_Artefacts_Beneath_The_User_Profile()
    {
        if (OperatingSystem.IsWindows() is false)
            return;

        ArtefactsLocator locator = Locator("DEFAULT");

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        using (Assert.Multiple())
        {
            await Assert.That(locator.ProfileDirectory).IsEqualTo(profile);
            await Assert.That(locator.ArtefactsDirectory).IsEqualTo(Path.Combine(profile, "Documents", "Heroes of Newerth x64"));
            await Assert.That(locator.RuntimeArtefactsPathApplies).IsTrue();
        }
    }

    [Test]
    public async Task On_Windows_A_Literal_Path_Is_Used_As_The_Profile_Directory()
    {
        if (OperatingSystem.IsWindows() is false)
            return;

        ArtefactsLocator locator = Locator(@"D:\HONServer");

        await Assert.That(locator.ProfileDirectory).IsEqualTo(@"D:\HONServer");
    }

    [Test]
    public async Task On_Linux_The_Artefacts_Directory_Is_The_Fixed_Server_Location_Regardless_Of_Configuration()
    {
        if (OperatingSystem.IsLinux() is false)
            return;

        ArtefactsLocator locator = Locator("DEFAULT");

        using (Assert.Multiple())
        {
            await Assert.That(locator.ArtefactsDirectory).IsEqualTo("/opt/hon/config");
            await Assert.That(locator.RuntimeArtefactsPathApplies).IsFalse();
        }
    }
}
