namespace COMPEL.Services.Proxy;

/// <summary>
///     Blocks and unblocks source addresses at the operating system firewall.
///     The proxy also drops banned datagrams at the application layer, so a no-op firewall controller still enforces bans for traffic that reaches the proxy; firewall integration additionally stops the traffic before it arrives.
/// </summary>
public interface IFirewallController
{
    /// <summary>
    ///     Adds a firewall rule blocking inbound traffic from the given address.
    /// </summary>
    void BlockAddress(IPAddress address);

    /// <summary>
    ///     Removes all firewall rules previously added by <see cref="BlockAddress"/>.
    /// </summary>
    void ClearBlockedAddresses();
}
