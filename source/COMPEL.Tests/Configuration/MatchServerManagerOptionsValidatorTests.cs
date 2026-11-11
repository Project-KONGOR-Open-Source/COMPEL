namespace COMPEL.Tests.Configuration;

/// <summary>
///     Verifies the start-up validation of the host-facing options: the placeholder-credential guard, the refusal of a NUL character and of a user name outside the account name characters, the supported locations and artefacts-path alias, and the port-range-offset boundary.
/// </summary>
public sealed class MatchServerManagerOptionsValidatorTests
{
    private static MatchServerManagerOptions ValidOptions() => new ()
    {
        UserName             = "KONGOR",
        Password             = "secret",
        Instances            = 1,
        WarmInstancesTarget  = 1,
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
    public async Task A_Password_With_Spaces_Quotes_And_Semicolons_Is_Accepted()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.Password = @"open ""sesame""; \ $";

        await Assert.That(Validate(options).Succeeded).IsTrue();
    }

    [Test]
    public async Task A_Server_Name_With_Any_Character_Is_Accepted()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.ServerNamePrefix = @"Ünïcødé 名前 ""Arena""; \ $";

        await Assert.That(Validate(options).Succeeded).IsTrue();
    }

    [Test]
    [Arguments("UserName")]
    [Arguments("Password")]
    [Arguments("ServerNamePrefix")]
    public async Task A_NUL_Character_Is_Refused_In_Every_Value(string setting)
    {
        MatchServerManagerOptions options = ValidOptions();

        switch (setting)
        {
            case "UserName":         options.UserName = "KON\0GOR"; break;
            case "Password":         options.Password = "sec\0ret"; break;
            default:                 options.ServerNamePrefix = "KONGOR\0ARENA"; break;
        }

        await Assert.That(Validate(options).Failed).IsTrue();
    }

    [Test]
    [Arguments("Name With Space")]
    [Arguments("name.dot")]
    public async Task A_User_Name_Outside_NEXUS_Account_Name_Characters_Is_Refused(string userName)
    {
        MatchServerManagerOptions options = ValidOptions();
        options.UserName = userName;

        await Assert.That(Validate(options).Failed).IsTrue();
    }

    [Test]
    public async Task A_User_Name_With_Letters_Digits_Underscores_And_Hyphens_Is_Accepted()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.UserName = "Host_01-A";

        await Assert.That(Validate(options).Succeeded).IsTrue();
    }

    [Test]
    public async Task A_Configuration_Too_Long_For_The_Console_Is_Refused()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.ServerNamePrefix = new string('A', 2000);

        ValidateOptionsResult result = Validate(options);

        using (Assert.Multiple())
        {
            await Assert.That(result.Failed).IsTrue();
            await Assert.That(result.FailureMessage?.Contains(ConsoleEscaping.CommandLimit.ToString()) is true).IsTrue();
        }
    }

    private static MatchServerManagerOptions OptionsWithPayloadLength(int payloadLength)
    {
        MatchServerManagerOptions options = ValidOptions();
        options.ServerNamePrefix = "A";

        string[] arguments = ManagerArguments.Build(options, new PortPlan(options), "255.255.255.255", AddressResolver.MasterServerHostAndPortFor(options.Gateway));
        int baseLength = arguments[Array.IndexOf(arguments, "-execute") + 1].Length - 2;

        options.ServerNamePrefix = new string('A', payloadLength - baseLength + 1);

        return options;
    }

    [Test]
    public async Task A_Payload_One_Character_Below_The_Console_Limit_Is_Accepted()
    {
        await Assert.That(Validate(OptionsWithPayloadLength(ConsoleEscaping.CommandLimit - 1)).Succeeded).IsTrue();
    }

    [Test]
    public async Task A_Payload_Exactly_At_The_Console_Limit_Is_Refused()
    {
        await Assert.That(Validate(OptionsWithPayloadLength(ConsoleEscaping.CommandLimit)).Failed).IsTrue();
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

    [Test]
    public async Task A_Negative_Warm_Instances_Target_Is_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.WarmInstancesTarget = -1;

        await Assert.That(Validate(options).Failed).IsTrue();
    }

    [Test]
    public async Task A_Warm_Instances_Target_Greater_Than_The_Instance_Count_Is_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.Instances = 1;
        options.WarmInstancesTarget = 2;

        await Assert.That(Validate(options).Failed).IsTrue();
    }

    [Test]
    public async Task A_Zero_Warm_Instances_Target_Is_Accepted()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.WarmInstancesTarget = 0;

        await Assert.That(Validate(options).Succeeded).IsTrue();
    }
}
