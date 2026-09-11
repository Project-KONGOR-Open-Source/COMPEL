namespace COMPEL.Services.Proxy;

/// <summary>
///     Why a datagram was or was not admitted under a challenge.
/// </summary>
internal enum ChallengeAdmission
{
    Admitted,
    OverQuota,
    Duplicate
}

/// <summary>
///     One challenge's packet allowance for one client.
///     The client stamps an increasing counter into each datagram and restarts it when it accepts a new challenge, so each window admits every counter below its quota exactly once.
///     The quota is checked before the counter is used to index the seen set, because the counter arrives from the client and may be any value the field can hold.
/// </summary>
internal sealed class ChallengeWindow
{
    // The Check And The Record Below Must Not Be Separable, Or Two Datagrams Carrying The Same Counter Could Both Be Admitted; The Forwarder's Single Receive Loop Makes That Unlikely Rather Than Impossible, And An Uncontended Lock Costs Nothing Against A Datagram's Other Work
    private readonly Lock admissionLock = new ();

    // A Byte Per Admissible Counter Rather Than A Bit, Because Indexing Beats Masking On This Path And The Whole Window Is Under One And A Half Kilobytes At The Configured Quota; The Largest Value "Derive" Can Return Is "ushort.MaxValue", Which Is 64 Kilobytes
    private readonly bool[] seen;

    internal ChallengeWindow(uint challenge, ushort quota)
    {
        Challenge = challenge;
        Quota = quota;
        seen = new bool[quota];
    }

    internal uint Challenge { get; }

    private ushort Quota { get; }

    /// <summary>
    ///     Admits <paramref name="counter"/> if it is within the quota and has not been seen under this challenge before.
    /// </summary>
    internal bool TryAdmit(ushort counter, out ChallengeAdmission admission)
    {
        if (counter >= Quota)
        {
            admission = ChallengeAdmission.OverQuota;

            return false;
        }

        lock (admissionLock)
        {
            if (seen[counter])
            {
                admission = ChallengeAdmission.Duplicate;

                return false;
            }

            seen[counter] = true;
        }

        admission = ChallengeAdmission.Admitted;

        return true;
    }
}
