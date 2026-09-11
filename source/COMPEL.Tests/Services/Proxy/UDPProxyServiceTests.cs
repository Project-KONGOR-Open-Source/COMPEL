namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies the derived cadences of the proxy's maintenance loop, which no behavioural test reaches and whose breakage is silent.
/// </summary>
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
}
