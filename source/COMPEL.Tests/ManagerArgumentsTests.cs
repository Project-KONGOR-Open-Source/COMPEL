namespace COMPEL.Tests;

/// <summary>
///     Verifies the command line built for the Heroes Of Newerth manager: the flag set the legacy COMPEL relied on, the new re-authentication frequency, and the quoting the "-execute" payload requires to survive on both platforms.
/// </summary>
public sealed class ManagerArgumentsTests
{
    private static MatchServerManagerOptions SampleOptions() => new ()
    {
        UserName             = "KONGOR",
        Password             = "secret",
        Instances            = 2,
        WarmInstancesTarget  = 2,
        Gateway              = "kongor.net",
        Location             = "EU",
        ServerNamePrefix     = "KONGOR ARENA",
        UseProxy             = true,
        PortRangeOffset      = 0,
        RuntimeArtefactsPath = "DEFAULT"
    };

    private static string[] Build()
    {
        MatchServerManagerOptions options = SampleOptions();

        return ManagerArguments.Build(options, new PortPlan(options), "1.2.3.4", "api.kongor.net");
    }

    [Test]
    public async Task The_Argument_Vector_Starts_With_The_Manager_And_Master_Server_Flags()
    {
        string[] arguments = Build();

        using (Assert.Multiple())
        {
            await Assert.That(arguments[0]).IsEqualTo("-manager");
            await Assert.That(arguments.Contains("-noconfig")).IsTrue();
            await Assert.That(arguments.Contains("-masterserver")).IsTrue();
            await Assert.That(arguments.Contains("api.kongor.net")).IsTrue();
        }
    }

    [Test]
    public async Task The_Execute_Payload_Carries_The_Legacy_Flags_And_The_New_Reauthentication_Frequency()
    {
        string joined = string.Join(' ', Build());

        using (Assert.Multiple())
        {
            // The Master Login Carries A Trailing Colon So The Manager Can Append A Per-Instance Index.
            await Assert.That(joined.Contains("Set man_masterLogin KONGOR:")).IsTrue();
            await Assert.That(joined.Contains("Set man_startServerPort 11235")).IsTrue();
            await Assert.That(joined.Contains("Set man_endServerPort 11236")).IsTrue();
            await Assert.That(joined.Contains("Set man_enableProxy true")).IsTrue();
            await Assert.That(joined.Contains("Set man_reauthFrequency 30000")).IsTrue();
            await Assert.That(joined.Contains("Set man_idleTarget 2")).IsTrue();
            await Assert.That(joined.Contains("Set host_affinity -1")).IsTrue();
            await Assert.That(joined.Contains("Set svr_location EU")).IsTrue();
            await Assert.That(joined.Contains("Set svr_ip 1.2.3.4")).IsTrue();
        }
    }

    [Test]
    public async Task The_Execute_Payload_Is_A_Single_Argument_Wrapped_In_Literal_Quotes()
    {
        string[] arguments = Build();

        int executeIndex = Array.IndexOf(arguments, "-execute");
        string payload = arguments[executeIndex + 1];

        using (Assert.Multiple())
        {
            await Assert.That(executeIndex).IsGreaterThanOrEqualTo(0);
            await Assert.That(payload.StartsWith('"')).IsTrue();
            await Assert.That(payload.EndsWith('"')).IsTrue();
        }
    }

    [Test]
    public async Task The_Server_Name_Gets_Two_Trailing_Workaround_Tokens()
    {
        string result = ManagerArguments.ServerNameWithWhitespaceWorkaround("KONGOR ARENA");

        await Assert.That(result).IsEqualTo("KONGOR ARENA 0 0");
    }
}
