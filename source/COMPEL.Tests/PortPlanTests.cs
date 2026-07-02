namespace COMPEL.Tests;

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
}
