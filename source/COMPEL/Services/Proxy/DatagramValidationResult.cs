namespace COMPEL.Services.Proxy;

/// <summary>
///     Represents the outcome of evaluating an incoming datagram against the proxy's length, challenge and counter rules.
/// </summary>
internal readonly struct DatagramValidationResult
{
    public bool IsAdmitted { get; }

    public bool IsAuthenticatedChallenge { get; }

    public int ViolationWeight { get; }

    public string? DropReason { get; }

    private DatagramValidationResult(bool isAdmitted, bool isAuthenticatedChallenge, int violationWeight, string? dropReason)
    {
        IsAdmitted = isAdmitted;
        IsAuthenticatedChallenge = isAuthenticatedChallenge;
        ViolationWeight = violationWeight;
        DropReason = dropReason;
    }

    public static DatagramValidationResult Admitted(bool isAuthenticatedChallenge)
        => new (true, isAuthenticatedChallenge, 0, null);

    public static DatagramValidationResult Dropped(int violationWeight, string dropReason)
        => new (false, false, violationWeight, dropReason);
}
