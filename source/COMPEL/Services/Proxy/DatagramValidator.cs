namespace COMPEL.Services.Proxy;

/// <summary>
///     Evaluates incoming client datagrams against length requirements, challenge matching, and counter rate limits.
/// </summary>
internal static class DatagramValidator
{
    /// <summary>
    ///     Validates a datagram and returns whether it should be admitted or dropped, along with any violation weight and reason.
    /// </summary>
    public static DatagramValidationResult Validate(ReadOnlySpan<byte> datagram, ProxyForwarderKind kind, SessionChallengeState challenges, IPEndPoint client, bool isWithinUnknownChallengeGrace)
    {
        if (ClientPacketReader.TryRead(datagram, kind, out uint challenge, out ushort counter) is false)
            return DatagramValidationResult.Dropped(ViolationScoreContainer.TooShortViolationWeight, "Too Short");

        ChallengeWindow? window = challenges.Match(challenge, client);

        if (window is null)
        {
            if (isWithinUnknownChallengeGrace)
                return DatagramValidationResult.Dropped(0, "Unknown Challenge Within Grace");

            return DatagramValidationResult.Dropped(ViolationScoreContainer.ChallengeViolationWeight, "Unknown Challenge");
        }

        if (window.TryAdmit(counter, out ChallengeAdmission admission) is false)
        {
            if (admission is ChallengeAdmission.Duplicate)
                return DatagramValidationResult.Dropped(ViolationScoreContainer.DuplicateViolationWeight, "Duplicate");

            if (challenge is SessionChallengeState.UnauthenticatedChallenge)
                return DatagramValidationResult.Dropped(ViolationScoreContainer.UnauthenticatedViolationWeight, "Unauthenticated");

            return DatagramValidationResult.Dropped(ViolationScoreContainer.RateLimitViolationWeight, "Over Quota");
        }

        bool isAuthenticatedChallenge = challenge is not SessionChallengeState.UnauthenticatedChallenge;

        return DatagramValidationResult.Admitted(isAuthenticatedChallenge);
    }
}
