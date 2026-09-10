namespace COMPEL.Services.Proxy;

/// <summary>
///     Reads the fields the proxy validates out of a client datagram, and refuses any datagram too short to contain them.
///     The offsets and the per-kind minimum lengths are those of the reference proxy, which drops a datagram before touching any field so that a short one cannot cause an out-of-range read.
/// </summary>
internal static class ClientPacketReader
{
    // The Watermark Prefix Occupies The Leading Forty Bytes; The Fields Below Sit Within Its Enhanced Half
    private const int ChallengeOffset = 28;
    private const int CounterOffset = 32;

    // "40 bytes Watermark + 2 bytes connection + 1 bytes packet type" For Game Traffic, Two Fewer For Voice
    private const int GameMinimumLength = 43;
    private const int VoiceMinimumLength = 41;

    /// <summary>
    ///     The shortest datagram of the supplied kind the proxy will consider.
    /// </summary>
    internal static int MinimumLength(ProxyForwarderKind kind) => kind switch
    {
        ProxyForwarderKind.Voice => VoiceMinimumLength,
        ProxyForwarderKind.Game  => GameMinimumLength,
        _                        => throw new ArgumentOutOfRangeException(nameof(kind), @$"Unsupported Forwarder Kind ""{kind}""")
    };

    /// <summary>
    ///     Reads the challenge the client echoed and the counter it stamped into this datagram.
    ///     Returns <see langword="false"/> without reading anything when the datagram is shorter than the minimum for its kind.
    /// </summary>
    internal static bool TryRead(ReadOnlySpan<byte> datagram, ProxyForwarderKind kind, out uint challenge, out ushort counter)
    {
        challenge = 0;
        counter = 0;

        if (datagram.Length < MinimumLength(kind))
            return false;

        challenge = BinaryPrimitives.ReadUInt32LittleEndian(datagram[ChallengeOffset..]);
        counter = BinaryPrimitives.ReadUInt16LittleEndian(datagram[CounterOffset..]);

        return true;
    }
}
