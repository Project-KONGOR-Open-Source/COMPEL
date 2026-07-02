namespace COMPEL.Services.Supervision;

/// <summary>
///     Computes the game, voice, and ping port allocation for the match server manager from the instance count, port-range offset, and whether the proxy is enabled.
///     This is the single source of truth for COMPEL's port arithmetic, used by the supervisor, the proxy, the ping responder, and the status endpoint.
/// </summary>
public sealed class PortPlan
{
    /// <summary>
    ///     The first game-server port when the offset is zero.
    /// </summary>
    public const int BaseGamePort = 11235;

    /// <summary>
    ///     The first voice-server port when the offset is zero.
    /// </summary>
    public const int BaseVoicePort = 11435;

    /// <summary>
    ///     The port on which COMPEL answers master-server pings when the offset is zero and the proxy is disabled.
    /// </summary>
    public const int BasePingPort = 11234;

    /// <summary>
    ///     The amount by which the proxy's public ports sit above the local server ports. The resulting 20000-29999 range is where Heroes Of Newerth supports anti-DDoS and anti-cheat protection natively.
    /// </summary>
    public const int ProxyPublicOffset = 10000;

    /// <summary>
    ///     The width of the valid game and voice port windows; the offset plus the instance count must fit within it.
    /// </summary>
    public const int PortRangeWindow = 100;

    public int Instances { get; }

    public int Offset { get; }

    public bool UseProxy { get; }

    public PortPlan(int instances, int offset, bool useProxy)
    {
        Instances = instances;
        Offset = offset;
        UseProxy = useProxy;
    }

    public PortPlan(MatchServerManagerOptions options) : this(options.Instances, options.PortRangeOffset, options.UseProxy)
    {
    }

    // Local Ports: The Ports The Game Servers Actually Bind. The Manager Always Allocates These In The 112xx / 114xx Range, Regardless Of Whether The Proxy Is Enabled.

    public int LocalGameStart => BaseGamePort + Offset;

    public int LocalGameEnd => LocalGameStart + Instances - 1;

    public int LocalVoiceStart => BaseVoicePort + Offset;

    public int LocalVoiceEnd => LocalVoiceStart + Instances - 1;

    // Public Ports: The Ports Clients Connect To. Without The Proxy They Equal The Local Ports; With The Proxy They Are Offset Above Them, And The Proxy Forwards Them Down To The Local Ports.

    public int PublicGameStart => UseProxy ? LocalGameStart + ProxyPublicOffset : LocalGameStart;

    public int PublicGameEnd => UseProxy ? LocalGameEnd + ProxyPublicOffset : LocalGameEnd;

    public int PublicVoiceStart => UseProxy ? LocalVoiceStart + ProxyPublicOffset : LocalVoiceStart;

    public int PublicVoiceEnd => UseProxy ? LocalVoiceEnd + ProxyPublicOffset : LocalVoiceEnd;

    /// <summary>
    ///     The port on which COMPEL answers master-server pings. With the proxy enabled it sits in the public anti-cheat range.
    /// </summary>
    public int PingPort => (UseProxy ? BasePingPort + ProxyPublicOffset : BasePingPort) + Offset;
}
