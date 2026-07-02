namespace COMPEL.Tests;

/// <summary>
///     Verifies the gateway-to-address logic and the master server endpoint derivation, for the branches that do not require network access.
/// </summary>
public sealed class AddressResolverTests
{
    private static AddressResolver Resolver(string gateway)
        => new (Options.Create(new MatchServerManagerOptions { Gateway = gateway }), NullLogger<AddressResolver>.Instance);

    [Test]
    public async Task A_Localhost_Gateway_Resolves_To_The_Loopback_Address()
    {
        string address = await Resolver("localhost").ResolveServerAddress();

        await Assert.That(address).IsEqualTo("127.0.0.1");
    }

    [Test]
    public async Task An_IPv4_Literal_Gateway_Is_Used_Directly()
    {
        string address = await Resolver("203.0.113.5").ResolveServerAddress();

        await Assert.That(address).IsEqualTo("203.0.113.5");
    }

    [Test]
    public async Task An_IPv6_Literal_Gateway_Is_Rejected()
    {
        bool threw = false;

        try
        {
            await Resolver("2001:db8::1").ResolveServerAddress();
        }

        catch (InvalidOperationException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task The_Master_Server_Endpoint_Uses_The_Local_Port_For_A_Localhost_Gateway()
    {
        await Assert.That(Resolver("localhost").MasterServerHostAndPort).IsEqualTo("127.0.0.1:5555");
    }

    [Test]
    public async Task The_Master_Server_Endpoint_Uses_The_Public_Gateway_Without_A_Port_Otherwise()
    {
        await Assert.That(Resolver("kongor.net").MasterServerHostAndPort).IsEqualTo("api.kongor.net");
    }
}
