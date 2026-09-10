namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies the per-source violation score: that a source within the expected packet rate is never actioned, that one above it or violating the checks is, that the score saturates and drains as the reference's does, and that a source which keeps pushing while actioned stays actioned for longer.
/// </summary>
public sealed class ViolationScoreContainerTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static IPEndPoint Source(int port = 40000) => new (IPAddress.Parse("203.0.113.5"), port);

    // The Most Important Test In The Suite: A Client At The Expected Packet Rate Must Never Be Actioned, Because A False Positive Drops A Legitimate Player Mid-Match
    [Test]
    public async Task A_Source_At_The_Expected_Packet_Rate_Is_Never_Actioned()
    {
        ControllableTimeProvider clock = new ();
        ViolationScoreContainer container = new (clock);

        bool everActioned = false;

        // Sixty Seconds Of Traffic At Exactly The Rate The Drain Is Sized For
        for (int second = 0; second < 60; second++)
        {
            for (int packet = 0; packet < ViolationScoreContainer.EstimatedPacketsPerSecond; packet++)
            {
                container.ChargeArrival(Source());

                if (container.IsWithinAllowance(Source()) is false)
                    everActioned = true;
            }

            clock.Advance(OneSecond);
            container.Drain();
        }

        await Assert.That(everActioned).IsFalse();
    }

    // The Per-Packet Cost Exists So That A Flood Carrying No Detectable Violation Is Still Scored
    [Test]
    public async Task A_Source_Above_The_Expected_Packet_Rate_Is_Eventually_Actioned()
    {
        ControllableTimeProvider clock = new ();
        ViolationScoreContainer container = new (clock);

        // Twice The Expected Rate Nets One Drain's Worth Of Score Per Second, So The Threshold Is Crossed In Well Under Two Minutes
        for (int second = 0; second < 120; second++)
        {
            for (int packet = 0; packet < ViolationScoreContainer.EstimatedPacketsPerSecond * 2; packet++)
                container.ChargeArrival(Source());

            clock.Advance(OneSecond);
            container.Drain();
        }

        await Assert.That(container.IsWithinAllowance(Source())).IsFalse();
    }

    [Test]
    public async Task A_Source_Well_Above_The_Threshold_Is_Actioned()
    {
        ViolationScoreContainer container = new (new ControllableTimeProvider());

        bool actioned = false;

        // Nothing Is Drained, So The Score Only Climbs
        for (int attempt = 0; attempt < 200; attempt++)
        {
            container.ChargeArrival(Source());
            container.ChargeViolation(Source(), ViolationScoreContainer.RateLimitViolationWeight);

            if (container.IsWithinAllowance(Source()) is false)
            {
                actioned = true;

                break;
            }
        }

        await Assert.That(actioned).IsTrue();
    }

    // A Regression Test For An All-Or-Nothing Charge: A Weight That Does Not Divide The Threshold Exactly Must Still Accumulate, Or A Source Can Park Just Below The Threshold Indefinitely
    [Test]
    public async Task A_Weight_That_Does_Not_Divide_The_Threshold_Still_Accumulates()
    {
        ViolationScoreContainer container = new (new ControllableTimeProvider());

        for (int attempt = 0; attempt < 400; attempt++)
            container.ChargeViolation(Source(), ViolationScoreContainer.DuplicateViolationWeight);

        using (Assert.Multiple())
        {
            await Assert.That(container.IsWithinAllowance(Source())).IsFalse();
            await Assert.That(container.Score(Source())).IsGreaterThan(ViolationScoreContainer.ActionableThreshold);
        }
    }

    [Test]
    public async Task An_Actioned_Source_Recovers_After_Draining()
    {
        ControllableTimeProvider clock = new ();
        ViolationScoreContainer container = new (clock);

        while (container.IsWithinAllowance(Source()))
            container.ChargeViolation(Source(), ViolationScoreContainer.RateLimitViolationWeight);

        // A Minute Of Drain At The Configured Rate Is Far More Than The Threshold, So A Source That Stops Must Be Clear Again
        for (int second = 0; second < 60; second++)
        {
            clock.Advance(OneSecond);
            container.Drain();
        }

        await Assert.That(container.IsWithinAllowance(Source())).IsTrue();
    }

    // The Escalation Is What Makes A Persistent Source Expensive To Itself, And It Is The Reason The Maximum Is Separate From The Threshold
    [Test]
    public async Task An_Actioned_Source_That_Keeps_Sending_Climbs_Above_The_Threshold()
    {
        ViolationScoreContainer container = new (new ControllableTimeProvider());

        while (container.IsWithinAllowance(Source()))
            container.ChargeViolation(Source(), ViolationScoreContainer.RateLimitViolationWeight);

        int scoreWhenFirstActioned = container.Score(Source());

        for (int packet = 0; packet < 100; packet++)
            container.ChargeArrival(Source());

        // Each Further Packet Costs Its Own Weight Plus The Actioned Weight, So A Hundred Of Them Climb By Far More Than A Hundred
        await Assert.That(container.Score(Source())).IsGreaterThan(scoreWhenFirstActioned + (100 * ViolationScoreContainer.ActionedViolationWeight));
    }

    [Test]
    public async Task The_Score_Saturates_At_The_Maximum()
    {
        ViolationScoreContainer container = new (new ControllableTimeProvider());

        for (int attempt = 0; attempt < 1000; attempt++)
        {
            container.ChargeArrival(Source());
            container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);
        }

        int maximum = ViolationScoreContainer.MaximumViolationScore;

        await Assert.That(container.Score(Source())).IsEqualTo(maximum);
    }

    // Graduated Persistence: The Reason The Two Ceilings Are Separate Is That A Source Which Keeps Pushing Must Take Longer To Recover Than One Which Stops
    [Test]
    public async Task A_Source_That_Kept_Pushing_Takes_Longer_To_Recover_Than_One_That_Stopped()
    {
        ControllableTimeProvider clock = new ();
        ViolationScoreContainer container = new (clock);

        IPEndPoint stopped = Source(40000);
        IPEndPoint persistent = Source(40001);

        IPEndPoint[] sources = [stopped, persistent];

        foreach (IPEndPoint source in sources)
            while (container.IsWithinAllowance(source))
                container.ChargeViolation(source, ViolationScoreContainer.RateLimitViolationWeight);

        // Only The Persistent Source Keeps Sending While Actioned
        for (int packet = 0; packet < 2000; packet++)
            container.ChargeArrival(persistent);

        for (int second = 0; second < 30; second++)
        {
            clock.Advance(OneSecond);
            container.Drain();
        }

        using (Assert.Multiple())
        {
            await Assert.That(container.IsWithinAllowance(stopped)).IsTrue();
            await Assert.That(container.IsWithinAllowance(persistent)).IsFalse();
        }
    }

    [Test]
    public async Task A_Drain_Removes_Score_In_Proportion_To_Elapsed_Time()
    {
        ControllableTimeProvider clock = new ();
        ViolationScoreContainer container = new (clock);

        container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);

        clock.Advance(TimeSpan.FromSeconds(1));
        container.Drain();

        int afterOneSecond = container.Score(Source());

        container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);
        container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);

        clock.Advance(TimeSpan.FromSeconds(2));
        container.Drain();

        using (Assert.Multiple())
        {
            // 200 Charged, 140 Drained
            await Assert.That(afterOneSecond).IsEqualTo(60);

            // 60 Carried Over Plus 400 Charged, Less Two Seconds Of Drain
            await Assert.That(container.Score(Source())).IsEqualTo(180);
        }
    }

    // The Reference Waits For Enough Elapsed Time Rather Than Draining A Partial Amount, And Must Not Discard The Remainder When It Does
    [Test]
    public async Task A_Drain_Before_The_Minimum_Interval_Does_Nothing_And_Keeps_The_Remainder()
    {
        ControllableTimeProvider clock = new ();
        ViolationScoreContainer container = new (clock);

        container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        container.Drain();

        int afterHalfASecond = container.Score(Source());

        clock.Advance(TimeSpan.FromMilliseconds(500));
        container.Drain();

        using (Assert.Multiple())
        {
            await Assert.That(afterHalfASecond).IsEqualTo(ViolationScoreContainer.TooShortViolationWeight);
            await Assert.That(container.Score(Source())).IsEqualTo(60);
        }
    }

    [Test]
    public async Task A_Source_Drained_To_Zero_Is_Forgotten()
    {
        ControllableTimeProvider clock = new ();
        ViolationScoreContainer container = new (clock);

        container.ChargeViolation(Source(), ViolationScoreContainer.DuplicateViolationWeight);

        clock.Advance(OneSecond);
        container.Drain();

        await Assert.That(container.TrackedSourceCount).IsEqualTo(0);
    }

    // Reading A Source's Standing Happens For Every Datagram, So It Must Not Cause The Source To Be Tracked
    [Test]
    public async Task An_Unknown_Source_Is_Within_Allowance_And_Is_Not_Tracked()
    {
        ViolationScoreContainer container = new (new ControllableTimeProvider());

        using (Assert.Multiple())
        {
            await Assert.That(container.IsWithinAllowance(Source())).IsTrue();
            await Assert.That(container.Score(Source())).IsEqualTo(0);
            await Assert.That(container.TrackedSourceCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Sources_Are_Scored_Independently()
    {
        ViolationScoreContainer container = new (new ControllableTimeProvider());

        while (container.IsWithinAllowance(Source(40000)))
            container.ChargeViolation(Source(40000), ViolationScoreContainer.RateLimitViolationWeight);

        await Assert.That(container.IsWithinAllowance(Source(40001))).IsTrue();
    }

    // A Weight At Or Above The Threshold Would Action A Source On A Single Anomaly, Which Is What The Weighting Exists To Prevent
    [Test]
    public async Task Every_Weight_Is_Below_The_Actionable_Threshold()
    {
        int[] weights =
        [
            ViolationScoreContainer.TooShortViolationWeight,
            ViolationScoreContainer.UnauthenticatedViolationWeight,
            ViolationScoreContainer.RateLimitViolationWeight,
            ViolationScoreContainer.DuplicateViolationWeight,
            ViolationScoreContainer.ActionedViolationWeight,
            ViolationScoreContainer.PacketScore
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
    public async Task The_Maximum_Is_Above_The_Actionable_Threshold()
    {
        int maximum = ViolationScoreContainer.MaximumViolationScore;

        await Assert.That(maximum).IsGreaterThan(ViolationScoreContainer.ActionableThreshold);
    }

    [Test]
    public async Task A_Single_Violation_Of_Any_Weight_Never_Actions_A_Source()
    {
        ViolationScoreContainer container = new (new ControllableTimeProvider());

        using (Assert.Multiple())
        {
            container.ChargeViolation(Source(41000), ViolationScoreContainer.TooShortViolationWeight);
            container.ChargeViolation(Source(41001), ViolationScoreContainer.UnauthenticatedViolationWeight);
            container.ChargeViolation(Source(41002), ViolationScoreContainer.RateLimitViolationWeight);
            container.ChargeViolation(Source(41003), ViolationScoreContainer.DuplicateViolationWeight);

            await Assert.That(container.IsWithinAllowance(Source(41000))).IsTrue();
            await Assert.That(container.IsWithinAllowance(Source(41001))).IsTrue();
            await Assert.That(container.IsWithinAllowance(Source(41002))).IsTrue();
            await Assert.That(container.IsWithinAllowance(Source(41003))).IsTrue();
        }
    }
}
