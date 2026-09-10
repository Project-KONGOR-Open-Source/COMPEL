namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies the per-source violation score: that ordinary traffic never reaches the actionable threshold, that abusive traffic does, and that a source recovers as its score drains.
/// </summary>
public sealed class ViolationScoreContainerTests
{
    private static IPEndPoint Source(int port = 40000) => new (IPAddress.Parse("203.0.113.5"), port);

    // The Most Important Test In The Suite: A Client At The Expected Packet Rate Must Never Be Actioned, Because A False Positive Drops A Legitimate Player Mid-Match
    [Test]
    public async Task A_Source_At_The_Expected_Rate_Is_Never_Actioned()
    {
        using ViolationScoreContainer container = new ();

        bool everRefused = false;

        // Sixty Seconds Of Traffic At The Rate The Drain Is Sized For, Charged One Rate-Limit Weight Per Second
        for (int second = 0; second < 60; second++)
        {
            container.Charge(Source(), ViolationScoreContainer.RateLimitViolationWeight);

            if (container.IsWithinAllowance(Source()) is false)
                everRefused = true;

            container.Replenish();
        }

        await Assert.That(everRefused).IsFalse();
    }

    [Test]
    public async Task A_Source_Well_Above_The_Threshold_Is_Actioned()
    {
        using ViolationScoreContainer container = new ();

        bool refused = false;

        // Nothing Is Replenished, So The Allowance Is Exhausted
        for (int attempt = 0; attempt < 200; attempt++)
        {
            container.Charge(Source(), ViolationScoreContainer.RateLimitViolationWeight);

            if (container.IsWithinAllowance(Source()) is false)
            {
                refused = true;

                break;
            }
        }

        await Assert.That(refused).IsTrue();
    }

    [Test]
    public async Task An_Actioned_Source_Recovers_After_Draining()
    {
        using ViolationScoreContainer container = new ();

        while (container.IsWithinAllowance(Source()))
            container.Charge(Source(), ViolationScoreContainer.RateLimitViolationWeight);

        // A Minute Of Drain At The Configured Rate Is Far More Than The Threshold, So The Source Must Be Clear Again
        for (int second = 0; second < 60; second++)
            container.Replenish();

        await Assert.That(container.IsWithinAllowance(Source())).IsTrue();
    }

    [Test]
    public async Task Sources_Are_Scored_Independently()
    {
        using ViolationScoreContainer container = new ();

        while (container.IsWithinAllowance(Source(40000)))
            container.Charge(Source(40000), ViolationScoreContainer.RateLimitViolationWeight);

        await Assert.That(container.IsWithinAllowance(Source(40001))).IsTrue();
    }

    // A Weight Above The Threshold Would Throw Rather Than Refuse, So No Weight May Ever Exceed It
    [Test]
    public async Task Every_Weight_Is_Below_The_Actionable_Threshold()
    {
        int[] weights =
        [
            ViolationScoreContainer.TooShortViolationWeight,
            ViolationScoreContainer.UnauthenticatedViolationWeight,
            ViolationScoreContainer.RateLimitViolationWeight,
            ViolationScoreContainer.DuplicateViolationWeight
        ];

        using (Assert.Multiple())
        {
            foreach (int weight in weights)
            {
                await Assert.That(weight).IsGreaterThan(0);
                await Assert.That(weight).IsLessThan(ViolationScoreContainer.ActionableThreshold);
            }
        }
    }

    [Test]
    public async Task A_Single_Violation_Of_Any_Weight_Never_Actions_A_Source()
    {
        using ViolationScoreContainer container = new ();

        using (Assert.Multiple())
        {
            container.Charge(Source(41000), ViolationScoreContainer.TooShortViolationWeight);
            container.Charge(Source(41001), ViolationScoreContainer.UnauthenticatedViolationWeight);
            container.Charge(Source(41002), ViolationScoreContainer.RateLimitViolationWeight);
            container.Charge(Source(41003), ViolationScoreContainer.DuplicateViolationWeight);

            await Assert.That(container.IsWithinAllowance(Source(41000))).IsTrue();
            await Assert.That(container.IsWithinAllowance(Source(41001))).IsTrue();
            await Assert.That(container.IsWithinAllowance(Source(41002))).IsTrue();
            await Assert.That(container.IsWithinAllowance(Source(41003))).IsTrue();
        }
    }
}
