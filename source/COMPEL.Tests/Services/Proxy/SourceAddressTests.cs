namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies that loopback, private, link-local, and shared addresses are classified as non-public, at both ends of each range, and that the addresses just outside each range are classified as public.
///     It also verifies that an IPv4-mapped IPv6 address is classified as the IPv4 address it maps.
/// </summary>
public sealed class SourceAddressTests
{
    [Test]
    [Arguments("127.0.0.0")]
    [Arguments("127.255.255.255")]
    [Arguments("::1")]
    [Arguments("10.0.0.0")]
    [Arguments("10.255.255.255")]
    [Arguments("172.16.0.0")]
    [Arguments("172.31.255.255")]
    [Arguments("192.168.0.0")]
    [Arguments("192.168.255.255")]
    [Arguments("fc00::")]
    [Arguments("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [Arguments("169.254.0.0")]
    [Arguments("169.254.255.255")]
    [Arguments("fe80::")]
    [Arguments("febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [Arguments("100.64.0.0")]
    [Arguments("100.127.255.255")]
    public async Task A_Loopback_Private_Link_Local_Or_Shared_Address_Is_Not_Public(string address)
    {
        await Assert.That(SourceAddress.IsPublic(IPAddress.Parse(address))).IsFalse();
    }

    [Test]
    [Arguments("126.255.255.255")]
    [Arguments("128.0.0.0")]
    [Arguments("9.255.255.255")]
    [Arguments("11.0.0.0")]
    [Arguments("172.15.255.255")]
    [Arguments("172.32.0.0")]
    [Arguments("192.167.255.255")]
    [Arguments("192.169.0.0")]
    [Arguments("fbff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [Arguments("fe00::")]
    [Arguments("169.253.255.255")]
    [Arguments("169.255.0.0")]
    [Arguments("fe7f:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [Arguments("100.63.255.255")]
    [Arguments("100.128.0.0")]
    [Arguments("203.0.113.1")]
    [Arguments("2001:db8::1")]
    public async Task An_Address_Outside_Those_Ranges_Is_Public(string address)
    {
        await Assert.That(SourceAddress.IsPublic(IPAddress.Parse(address))).IsTrue();
    }

    // A Dual-Mode Socket Presents An IPv4 Source In This Form, Which Matches None Of The IPv4 Ranges Unless It Is Mapped Back
    [Test]
    [Arguments("::ffff:127.0.0.1", false)]
    [Arguments("::ffff:10.1.2.3", false)]
    [Arguments("::ffff:172.16.1.2", false)]
    [Arguments("::ffff:192.168.1.2", false)]
    [Arguments("::ffff:169.254.1.2", false)]
    [Arguments("::ffff:100.64.1.2", false)]
    [Arguments("::ffff:172.32.0.0", true)]
    [Arguments("::ffff:203.0.113.1", true)]
    public async Task An_IPv4_Mapped_Address_Is_Classified_As_The_IPv4_Address_It_Maps(string address, bool isPublic)
    {
        await Assert.That(SourceAddress.IsPublic(IPAddress.Parse(address))).IsEqualTo(isPublic);
    }
}
