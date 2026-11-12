namespace COMPEL.Tests.Services.Supervision;

/// <summary>
///     Verifies the description of a match server manager exit that is logged when the match server manager stops while it is meant to be running.
/// </summary>
public sealed class ManagerExitTests
{
    [Test]
    public async Task A_Port_Exit_Names_The_Port()
    {
        PortPlan ports = new (instances: 1, offset: 7, useProxy: false);

        string description = ManagerExit.Describe(ManagerExit.PortUnavailable, ports);

        using (Assert.Multiple())
        {
            await Assert.That(description.Contains("1142")).IsTrue();
            await Assert.That(description.Contains("Port")).IsTrue();
        }
    }

    [Test]
    public async Task Any_Other_Exit_Names_Its_Code()
    {
        PortPlan ports = new (instances: 1, offset: 0, useProxy: false);

        string description = ManagerExit.Describe(3, ports);

        using (Assert.Multiple())
        {
            await Assert.That(description.Contains("3")).IsTrue();
            await Assert.That(description.Contains("1135")).IsFalse();
        }
    }
}
