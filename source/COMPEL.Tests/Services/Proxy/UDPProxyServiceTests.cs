namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies the derived cadences of the proxy's maintenance loop, which no behavioural test reaches and whose breakage is silent.
/// </summary>
[NotInParallel]
public sealed class UDPProxyServiceTests
{
    // A Rotation Is What Resets A Client's Counter And What Ages The Retained Challenge History, So Its Cadence Is Load-Bearing Well Beyond The Challenge Itself
    [Test]
    public async Task The_Rotation_Cadence_Is_The_Challenge_Renewal_Interval()
    {
        TimeSpan rotation = UDPProxyService.MaintenanceInterval * UDPProxyService.MaintenancePassesPerRotation;

        await Assert.That(rotation).IsEqualTo(UDPProxyService.ChallengeRenewalInterval);
    }

    [Test]
    public async Task The_Under_Attack_Threshold_Is_One_Thousand()
    {
        int threshold = AttackIndicatorContainer.UnderAttackThreshold;

        await Assert.That(threshold).IsEqualTo(1000);
    }

    [Test]
    public async Task Restarting_A_Disabled_Proxy_Throws_An_Invalid_Operation_Exception()
    {
        MatchServerManagerOptions options = new ()
        {
            UserName  = "KONGOR",
            Password  = "secret",
            Instances = 1,
            UseProxy  = false
        };

        UDPProxyService service = new (Options.Create(options), new PortPlan(options), NullLogger<UDPProxyService>.Instance);

        using (Assert.Multiple())
        {
            await Assert.That(service.IsEnabled).IsFalse();
            await Assert.That(async () => await service.RequestRestart(CancellationToken.None)).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task Restarting_An_Enabled_Proxy_Rebinds_And_Runs_Forwarders()
    {
        MatchServerManagerOptions options = new ()
        {
            UserName        = "KONGOR",
            Password        = "secret",
            Instances       = 1,
            PortRangeOffset = 80,
            UseProxy        = true
        };

        UDPProxyService service = new (Options.Create(options), new PortPlan(options), NullLogger<UDPProxyService>.Instance);

        using CancellationTokenSource lifetime = new ();

        Task executeTask = service.StartAsync(lifetime.Token);

        try
        {
            bool ready = await service.WaitUntilReady(lifetime.Token);

            await Assert.That(ready).IsTrue();
            await Assert.That(service.IsRunning).IsTrue();

            bool restarted = await service.RequestRestart(lifetime.Token);

            await Assert.That(restarted).IsTrue();
            await Assert.That(service.IsRunning).IsTrue();
        }

        finally
        {
            await service.StopAsync(CancellationToken.None);
            await executeTask;
        }
    }
}

