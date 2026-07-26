namespace COMPEL.Tests;

/// <summary>
///     Verifies the version-string parsing used by the self-update version check.
/// </summary>
public sealed class VersionCheckerTests
{
    [Test]
    public async Task A_Version_String_With_A_Leading_V_Prefix_Is_Parsed()
    {
        bool parsed = VersionChecker.TryParseNumericVersion("v1.2.3", out Version? version);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(version).IsEqualTo(new Version(1, 2, 3));
        }
    }

    [Test]
    public async Task A_Version_String_Without_A_Prefix_Is_Parsed()
    {
        bool parsed = VersionChecker.TryParseNumericVersion("1.2.3", out Version? version);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(version).IsEqualTo(new Version(1, 2, 3));
        }
    }

    [Test]
    public async Task A_Pre_Release_Suffix_Is_Discarded()
    {
        bool parsed = VersionChecker.TryParseNumericVersion("v1.2.3-rc1", out Version? version);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(version).IsEqualTo(new Version(1, 2, 3));
        }
    }

    [Test]
    public async Task Build_Metadata_Is_Discarded()
    {
        bool parsed = VersionChecker.TryParseNumericVersion("1.2.3+abc123", out Version? version);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsTrue();
            await Assert.That(version).IsEqualTo(new Version(1, 2, 3));
        }
    }

    [Test]
    public async Task An_Invalid_Version_String_Is_Rejected()
    {
        using (Assert.Multiple())
        {
            await Assert.That(VersionChecker.TryParseNumericVersion("garbage", out Version? _)).IsFalse();
            await Assert.That(VersionChecker.TryParseNumericVersion(string.Empty, out Version? _)).IsFalse();
            await Assert.That(VersionChecker.TryParseNumericVersion("v1.2", out Version? _)).IsFalse();
            await Assert.That(VersionChecker.TryParseNumericVersion("version 1.2.3", out Version? _)).IsFalse();
        }
    }

    [Test]
    public async Task Parsed_Versions_Compare_Numerically()
    {
        VersionChecker.TryParseNumericVersion("v2.1.0", out Version? newer);
        VersionChecker.TryParseNumericVersion("v2.0.0", out Version? older);
        VersionChecker.TryParseNumericVersion("v10.0.0", out Version? doubleDigit);
        VersionChecker.TryParseNumericVersion("v9.9.9", out Version? singleDigit);

        using (Assert.Multiple())
        {
            await Assert.That(newer > older).IsTrue();
            await Assert.That(doubleDigit > singleDigit).IsTrue();
        }
    }

    [Test]
    public async Task The_Current_Version_Display_Is_The_V_Prefixed_Numeric_Version()
        => await Assert.That(VersionChecker.CurrentVersionDisplay).IsEqualTo($"v{VersionChecker.CurrentVersion.Major}.{VersionChecker.CurrentVersion.Minor}.{VersionChecker.CurrentVersion.Build}");
}
