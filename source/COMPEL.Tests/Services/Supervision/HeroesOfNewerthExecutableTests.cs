namespace COMPEL.Tests.Services.Supervision;

/// <summary>
///     Verifies the check that decides whether the manager can spawn server instances from a given installation directory.
/// </summary>
public sealed class HeroesOfNewerthExecutableTests
{
    [Test]
    public async Task A_Directory_Whose_Path_Contains_A_Whitespace_Character_Supports_Instance_Launching()
    {
        await Assert.That(HeroesOfNewerthExecutable.CanLaunchInstancesFrom(@"C:\Games\HoN Match Server")).IsTrue();
    }

    [Test]
    public async Task A_Directory_Whose_Path_Contains_No_Whitespace_Character_Does_Not_Support_Instance_Launching_On_Windows()
    {
        bool supported = HeroesOfNewerthExecutable.CanLaunchInstancesFrom(@"C:\Games\windows-x64-native-aot");

        // The Restriction Is A Windows-Only Consequence Of How The Engine Parses The Command Line Its Manager Builds For Each Instance
        await Assert.That(supported).IsEqualTo(OperatingSystem.IsWindows() is false);
    }

    [Test]
    public async Task The_Whitespace_Character_May_Come_From_Any_Part_Of_The_Path()
    {
        await Assert.That(HeroesOfNewerthExecutable.CanLaunchInstancesFrom(@"C:\Program Files\COMPEL")).IsTrue();
    }
}
