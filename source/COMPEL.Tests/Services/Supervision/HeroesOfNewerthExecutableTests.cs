namespace COMPEL.Tests.Services.Supervision;

/// <summary>
///     Verifies the match server executable name.
/// </summary>
public sealed class HeroesOfNewerthExecutableTests
{
    [Test]
    public async Task The_Executable_Has_No_Architecture_In_Its_Name()
    {
        await Assert.That(HeroesOfNewerthExecutable.FileName).IsEqualTo(OperatingSystem.IsWindows() ? "hon.exe" : "hon");
    }
}
