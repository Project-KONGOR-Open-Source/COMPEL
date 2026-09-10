namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies one challenge's packet allowance: that a counter within the quota is admitted once, that the quota is enforced, and that a repeated counter is rejected as a duplicate.
/// </summary>
public sealed class ChallengeWindowTests
{
    [Test]
    public async Task A_Counter_Within_The_Quota_Is_Admitted()
    {
        ChallengeWindow window = new (challenge: 42, quota: 10);

        bool admitted = window.TryAdmit(0, out ChallengeAdmission admission);

        using (Assert.Multiple())
        {
            await Assert.That(admitted).IsTrue();
            await Assert.That(admission).IsEqualTo(ChallengeAdmission.Admitted);
        }
    }

    [Test]
    public async Task A_Counter_At_Or_Above_The_Quota_Is_Over_Quota()
    {
        ChallengeWindow window = new (challenge: 42, quota: 10);

        using (Assert.Multiple())
        {
            await Assert.That(window.TryAdmit(10, out ChallengeAdmission atQuota)).IsFalse();
            await Assert.That(atQuota).IsEqualTo(ChallengeAdmission.OverQuota);

            await Assert.That(window.TryAdmit(9999, out ChallengeAdmission farAbove)).IsFalse();
            await Assert.That(farAbove).IsEqualTo(ChallengeAdmission.OverQuota);
        }
    }

    // A Counter Beyond The Quota Must Be Refused Before It Is Used To Index The Duplicate Store, Which Is Sized To The Quota
    [Test]
    public async Task The_Maximum_Possible_Counter_Does_Not_Index_Out_Of_Range()
    {
        ChallengeWindow window = new (challenge: 42, quota: 10);

        bool admitted = window.TryAdmit(ushort.MaxValue, out ChallengeAdmission admission);

        using (Assert.Multiple())
        {
            await Assert.That(admitted).IsFalse();
            await Assert.That(admission).IsEqualTo(ChallengeAdmission.OverQuota);
        }
    }

    [Test]
    public async Task A_Repeated_Counter_Is_A_Duplicate()
    {
        ChallengeWindow window = new (challenge: 42, quota: 10);

        window.TryAdmit(3, out _);

        bool admitted = window.TryAdmit(3, out ChallengeAdmission admission);

        using (Assert.Multiple())
        {
            await Assert.That(admitted).IsFalse();
            await Assert.That(admission).IsEqualTo(ChallengeAdmission.Duplicate);
        }
    }

    [Test]
    public async Task Every_Counter_Below_The_Quota_Is_Admitted_Exactly_Once()
    {
        const ushort quota = 64;

        ChallengeWindow window = new (challenge: 1, quota: quota);

        using (Assert.Multiple())
        {
            for (ushort counter = 0; counter < quota; counter++)
                await Assert.That(window.TryAdmit(counter, out _)).IsTrue();

            for (ushort counter = 0; counter < quota; counter++)
                await Assert.That(window.TryAdmit(counter, out _)).IsFalse();
        }
    }

    // The Same Counter Under A New Challenge Is Legitimate, Because The Client Restarts Its Counter When It Accepts One
    [Test]
    public async Task A_New_Window_Admits_A_Counter_The_Previous_One_Consumed()
    {
        ChallengeWindow first = new (challenge: 1, quota: 10);
        first.TryAdmit(5, out _);

        ChallengeWindow second = new (challenge: 2, quota: 10);

        await Assert.That(second.TryAdmit(5, out _)).IsTrue();
    }
}
