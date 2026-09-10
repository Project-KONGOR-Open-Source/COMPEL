namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies that the advertised and enforced packet quotas are derived from one rate, so they cannot diverge.
/// </summary>
public sealed class ChallengeQuotaTests
{
    [Test]
    public async Task The_Quota_Is_The_Rate_Multiplied_By_The_Renewal_Interval()
    {
        using (Assert.Multiple())
        {
            await Assert.That(ChallengeQuota.Derive(144, TimeSpan.FromSeconds(5))).IsEqualTo((ushort)720);
            await Assert.That(ChallengeQuota.Derive(144, TimeSpan.FromSeconds(10))).IsEqualTo((ushort)1440);
        }
    }

    // The Reference Grants 720 Packets Over A Five-Second Refresh, So A Five-Second Interval Must Reproduce The Reference Exactly
    [Test]
    public async Task A_Five_Second_Interval_Reproduces_The_Reference_Quotas()
    {
        TimeSpan referenceRefresh = TimeSpan.FromSeconds(5);

        using (Assert.Multiple())
        {
            await Assert.That(ChallengeQuota.ForKind(ProxyForwarderKind.Game, referenceRefresh)).IsEqualTo((ushort)720);
            await Assert.That(ChallengeQuota.ForKind(ProxyForwarderKind.Voice, referenceRefresh)).IsEqualTo((ushort)50);
            await Assert.That(ChallengeQuota.GameCommandForInterval(referenceRefresh)).IsEqualTo((ushort)40);
        }
    }

    [Test]
    public async Task Voice_Is_Quoted_Far_Lower_Than_Game()
    {
        TimeSpan interval = TimeSpan.FromSeconds(10);

        await Assert.That(ChallengeQuota.ForKind(ProxyForwarderKind.Voice, interval))
            .IsLessThan(ChallengeQuota.ForKind(ProxyForwarderKind.Game, interval));
    }

    // A Long Interval Must Not Wrap The Unsigned Sixteen-Bit Field The Quota Is Advertised In
    [Test]
    public async Task An_Interval_Large_Enough_To_Overflow_Is_Clamped()
    {
        await Assert.That(ChallengeQuota.Derive(144, TimeSpan.FromHours(1))).IsEqualTo(ushort.MaxValue);
    }

    [Test]
    public async Task No_Rate_Is_Zero_Or_Negative()
    {
        int gamePacketsPerSecond = ChallengeQuota.GamePacketsPerSecond;
        int gameCommandPacketsPerSecond = ChallengeQuota.GameCommandPacketsPerSecond;
        int voicePacketsPerSecond = ChallengeQuota.VoicePacketsPerSecond;

        using (Assert.Multiple())
        {
            await Assert.That(gamePacketsPerSecond).IsGreaterThan(0);
            await Assert.That(gameCommandPacketsPerSecond).IsGreaterThan(0);
            await Assert.That(voicePacketsPerSecond).IsGreaterThan(0);
        }
    }
}
