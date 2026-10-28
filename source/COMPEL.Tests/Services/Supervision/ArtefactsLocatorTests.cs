namespace COMPEL.Tests.Services.Supervision;

/// <summary>
///     Verifies where the artefacts locator resolves the child process's profile and artefacts directories on each platform.
/// </summary>
public sealed class ArtefactsLocatorTests
{
    private static ArtefactsLocator Locator(string runtimeArtefactsPath)
        => new (Options.Create(new MatchServerManagerOptions { RuntimeArtefactsPath = runtimeArtefactsPath }));

    [Test]
    public async Task The_Default_Alias_Places_Artefacts_In_The_Heroes_Of_Newerth_Root_Of_The_User_Profile()
    {
        ArtefactsLocator locator = Locator("DEFAULT");

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Heroes Of Newerth Keeps Its Root In "HON" Beneath The Profile On Windows And In ".HON" On Linux, For Every Role
        string root = OperatingSystem.IsWindows() ? "HON" : ".HON";

        using (Assert.Multiple())
        {
            await Assert.That(locator.ProfileDirectory).IsEqualTo(profile);
            await Assert.That(locator.ArtefactsDirectory).IsEqualTo(Path.Combine(profile, root));
        }
    }

    [Test]
    public async Task A_Literal_Path_Is_Used_As_The_Profile_Directory_On_Every_Platform()
    {
        string profile = Path.Combine(Path.GetTempPath(), "HON Server");

        ArtefactsLocator locator = Locator(profile);

        using (Assert.Multiple())
        {
            await Assert.That(locator.ProfileDirectory).IsEqualTo(profile);
            await Assert.That(locator.ArtefactsDirectory).IsEqualTo(Path.Combine(profile, OperatingSystem.IsWindows() ? "HON" : ".HON"));
        }
    }

    [Test]
    public async Task The_Replays_Directory_Is_The_Match_Servers_Own_Beneath_The_Root()
    {
        ArtefactsLocator locator = Locator("DEFAULT");

        // The Root Also Holds The Game Client's And The Editors' Folders On A Machine That Plays As Well As Hosts, So Only The Match Servers' Replays Are Swept
        await Assert.That(locator.ReplaysDirectory).IsEqualTo(Path.Combine(locator.ArtefactsDirectory, "server", "replays"));
    }
}
