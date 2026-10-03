namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies the decaying weighted attack indicator: that an attack burst raises the indicator immediately, that the indicator clears once the burst stops, and that relative weights match the reference order.
/// </summary>
public sealed class AttackIndicatorContainerTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    [Test]
    public async Task An_Attack_Burst_Raises_The_Under_Attack_Indicator_Immediately_Within_The_Maintenance_Pass()
    {
        ControllableTimeProvider clock = new ();
        AttackIndicatorContainer container = new (clock);

        await Assert.That(container.IsUnderAttack).IsFalse();

        // 11 Validation Drops At Weight 100 Push Score To 1100, Crossing UnderAttackThreshold (1000) Immediately Without Waiting 5 Minutes
        for (int index = 0; index < 11; index++)
            container.Charge(AttackIndicatorContainer.ValidationDropAttackWeight);

        await Assert.That(container.IsUnderAttack).IsTrue();
    }

    [Test]
    public async Task The_Under_Attack_Indicator_Clears_Once_The_Burst_Stops_And_Time_Elapses()
    {
        ControllableTimeProvider clock = new ();
        AttackIndicatorContainer container = new (clock);

        for (int index = 0; index < 11; index++)
            container.Charge(AttackIndicatorContainer.ValidationDropAttackWeight);

        await Assert.That(container.IsUnderAttack).IsTrue();

        // 6 Seconds Of Drain At 200 Score/Second Drains 1200 Score, Returning Below 1000
        for (int second = 0; second < 6; second++)
        {
            clock.Advance(OneSecond);
            container.Drain();
        }

        await Assert.That(container.IsUnderAttack).IsFalse();
    }

    [Test]
    public async Task Validation_Drop_Weight_Is_Greater_Than_Cap_Refusal_Weight()
    {
        int validationWeight = AttackIndicatorContainer.ValidationDropAttackWeight;
        int capRefusalWeight = AttackIndicatorContainer.CapRefusalAttackWeight;
        int novelEndpointWeight = AttackIndicatorContainer.NovelEndpointAttackWeight;

        using (Assert.Multiple())
        {
            await Assert.That(validationWeight).IsGreaterThan(capRefusalWeight);
            await Assert.That(capRefusalWeight).IsGreaterThan(novelEndpointWeight);
        }
    }
}
