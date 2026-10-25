namespace COMPEL.Services.Proxy;

/// <summary>
///     Tells a public source address from a loopback, private, link-local, or shared one, which no genuine client on the internet arrives from.
///     An IPv4-mapped IPv6 address is classified as the IPv4 address it maps.
/// </summary>
internal static class SourceAddress
{
    // Private (RFC 1918), Link-Local (RFC 3927), And Shared, Which Carrier-Grade NAT And Tunnels Use (RFC 6598); "IPAddress" Recognises Loopback And The IPv6 Ranges Itself
    private static readonly IPNetwork[] NonPublicIPv4Networks =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("100.64.0.0/10")
    ];

    /// <summary>
    ///     Whether <paramref name="address"/> lies outside the loopback, private, link-local, and shared ranges, which is where every genuine client on the internet arrives from.
    /// </summary>
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || address.IsIPv6UniqueLocal || address.IsIPv6LinkLocal)
            return false;

        foreach (IPNetwork network in NonPublicIPv4Networks)
        {
            if (network.Contains(address))
                return false;
        }

        return true;
    }
}
