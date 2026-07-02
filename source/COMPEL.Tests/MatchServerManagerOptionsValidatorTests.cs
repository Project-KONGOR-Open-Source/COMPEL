namespace COMPEL.Tests;

/// <summary>
///     Verifies the start-up validation of the host-facing options: the placeholder-credential guard, the supported locations and artefacts-path alias, and the port-range-offset boundary.
/// </summary>
public sealed class MatchServerManagerOptionsValidatorTests
{
    private static MatchServerManagerOptions ValidOptions() => new ()
    {
        UserName             = "KONGOR",
        Password             = "secret",
        Instances            = 1,
        Gateway              = "kongor.net",
        Location             = "EU",
        ServerNamePrefix     = "KONGOR ARENA",
        UseProxy             = true,
        PortRangeOffset      = 0,
        RuntimeArtefactsPath = "DEFAULT"
    };

    private static ValidateOptionsResult Validate(MatchServerManagerOptions options)
        => new MatchServerManagerOptionsValidator().Validate(name: null, options);

    [Test]
    public async Task A_Fully_Configured_Set_Of_Options_Passes()
    {
        await Assert.That(Validate(ValidOptions()).Succeeded).IsTrue();
    }

    [Test]
    public async Task The_Default_Placeholder_User_Name_Is_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.UserName = "USERNAME";

        await Assert.That(Validate(options).Failed).IsTrue();
    }

    [Test]
    public async Task The_Default_Placeholder_Password_Is_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.Password = "PASSWORD";

        await Assert.That(Validate(options).Failed).IsTrue();
    }

    [Test]
    public async Task An_Unsupported_Location_Is_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.Location = "MARS";

        await Assert.That(Validate(options).Failed).IsTrue();
    }

    [Test]
    public async Task The_Default_Artefacts_Path_Alias_Is_Accepted_But_A_Legacy_Alias_Is_Not()
    {
        MatchServerManagerOptions accepted = ValidOptions();
        accepted.RuntimeArtefactsPath = "DEFAULT";

        MatchServerManagerOptions rejected = ValidOptions();
        rejected.RuntimeArtefactsPath = "TEMP";

        using (Assert.Multiple())
        {
            await Assert.That(Validate(accepted).Succeeded).IsTrue();
            await Assert.That(Validate(rejected).Failed).IsTrue();
        }
    }

    [Test]
    public async Task A_Port_Range_Offset_That_Bleeds_Outside_The_Window_Is_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.PortRangeOffset = 200;

        await Assert.That(Validate(options).Failed).IsTrue();
    }

    [Test]
    public async Task A_Negative_Port_Range_Offset_Is_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.PortRangeOffset = -1;

        await Assert.That(Validate(options).Failed).IsTrue();
    }
}
