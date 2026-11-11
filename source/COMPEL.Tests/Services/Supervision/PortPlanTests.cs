namespace COMPEL.Tests.Services.Supervision;

/// <summary>
///     Verifies the port arithmetic that the supervisor, proxy, and ping responder all depend on.
/// </summary>
public sealed class PortPlanTests
{
    [Test]
    public async Task Without_Proxy_The_Public_Ports_Equal_The_Local_Ports()
    {
        PortPlan plan = new (instances: 3, offset: 0, useProxy: false);

        using (Assert.Multiple())
        {
            await Assert.That(plan.LocalGameStart).IsEqualTo(11235);
            await Assert.That(plan.LocalGameEnd).IsEqualTo(11237);
            await Assert.That(plan.LocalVoiceStart).IsEqualTo(11435);
            await Assert.That(plan.LocalVoiceEnd).IsEqualTo(11437);
            await Assert.That(plan.PublicGameStart).IsEqualTo(11235);
            await Assert.That(plan.PublicVoiceStart).IsEqualTo(11435);
            await Assert.That(plan.PingPort).IsEqualTo(11234);
        }
    }

    [Test]
    public async Task With_Proxy_The_Public_Ports_Are_Offset_By_10000_Above_The_Local_Ports()
    {
        PortPlan plan = new (instances: 2, offset: 0, useProxy: true);

        using (Assert.Multiple())
        {
            await Assert.That(plan.LocalGameStart).IsEqualTo(11235);
            await Assert.That(plan.PublicGameStart).IsEqualTo(21235);
            await Assert.That(plan.PublicGameEnd).IsEqualTo(21236);
            await Assert.That(plan.PublicVoiceStart).IsEqualTo(21435);
            await Assert.That(plan.PingPort).IsEqualTo(21234);
        }
    }

    [Test]
    public async Task The_Offset_Shifts_Every_Port_By_The_Same_Amount()
    {
        PortPlan plan = new (instances: 1, offset: 5, useProxy: false);

        using (Assert.Multiple())
        {
            await Assert.That(plan.LocalGameStart).IsEqualTo(11240);
            await Assert.That(plan.LocalGameEnd).IsEqualTo(11240);
            await Assert.That(plan.LocalVoiceStart).IsEqualTo(11440);
            await Assert.That(plan.PingPort).IsEqualTo(11239);
        }
    }

    [Test]
    public async Task The_Manager_Port_Moves_With_The_Offset()
    {
        using (Assert.Multiple())
        {
            await Assert.That(new PortPlan(instances: 1, offset: 0, useProxy: false).ManagerPort).IsEqualTo(1135);
            await Assert.That(new PortPlan(instances: 1, offset: 7, useProxy: false).ManagerPort).IsEqualTo(1142);
        }
    }

    [Test]
    public async Task The_Manager_Port_Never_Meets_A_Game_Voice_Or_Ping_Port()
    {
        MatchServerManagerOptionsValidator validator = new ();

        int checkedPlans = 0;
        int collisions = 0;

        foreach (bool useProxy in new[] { false, true })
        {
            for (int instances = 1; instances <= 100; instances++)
            {
                for (int offset = 0; offset <= PortPlan.PortRangeWindow; offset++)
                {
                    MatchServerManagerOptions options = new ()
                    {
                        UserName             = "KONGOR",
                        Password             = "secret",
                        Instances            = instances,
                        WarmInstancesTarget  = 0,
                        Gateway              = "kongor.net",
                        Location             = "EU",
                        ServerNamePrefix     = "KONGOR ARENA",
                        UseProxy             = useProxy,
                        PortRangeOffset      = offset,
                        RuntimeArtefactsPath = "DEFAULT"
                    };

                    // Only The Port Window Decides Which Plans Are Accepted Here, Since The Processor Count Of This Machine Is Not Under Test
                    if (validator.Validate(null, options).Failures?.Any(failure => failure.Contains("Bleed Outside")) ?? false)
                        continue;

                    PortPlan plan = new (options);

                    // Every Plan Is Compared With Every Other Plan, As Two Installations On One Machine Share Nothing But The Port Space
                    foreach (int otherOffset in new[] { 0, offset, PortPlan.PortRangeWindow })
                    {
                        PortPlan other = new (instances, otherOffset, useProxy);

                        if (plan.ManagerPort >= other.LocalGameStart && plan.ManagerPort <= other.PublicGameEnd)
                            collisions++;

                        if (plan.ManagerPort >= other.LocalVoiceStart && plan.ManagerPort <= other.PublicVoiceEnd)
                            collisions++;

                        if (plan.ManagerPort == other.PingPort)
                            collisions++;
                    }

                    checkedPlans++;
                }
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(checkedPlans).IsGreaterThan(0);
            await Assert.That(collisions).IsEqualTo(0);
        }
    }
}
