namespace COMPEL.Services.Proxy;

/// <summary>
///     A firewall controller used when operating-system firewall integration is unavailable: the process is unprivileged, or the platform's firewall integration is not implemented.
///     Bans are still enforced at the application layer by the proxy dropping banned datagrams.
/// </summary>
public sealed class NoOperationFirewallController : IFirewallController
{
    public void BlockAddress(IPAddress address)
    {
    }

    public void ClearBlockedAddresses()
    {
    }
}
