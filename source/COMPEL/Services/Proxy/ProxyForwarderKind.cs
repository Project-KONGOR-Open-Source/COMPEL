namespace COMPEL.Services.Proxy;

/// <summary>
///     Which traffic a forwarder relays. The two kinds are quoted differently, because the reference proxy grants a voice client far fewer packets than a game client.
/// </summary>
internal enum ProxyForwarderKind
{
    Game,
    Voice
}
