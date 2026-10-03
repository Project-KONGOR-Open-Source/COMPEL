namespace COMPEL.Tests.Services.Ping;

/// <summary>
///     Verifies the byte layout of the master-server pong template: the unreliable flag and pong message type at their fixed offsets, and the server name and version at the offsets the client expects.
/// </summary>
public sealed class UDPPingResponderTests
{
    [Test]
    public async Task The_Response_Template_Places_The_Markers_Name_And_Version_At_The_Expected_Offsets()
    {
        const string serverName = "KONGOR ARENA";
        const string version = "4.10.1";

        byte[] response = UDPPingResponder.BuildResponseTemplate(serverName, version);

        byte[] serverNameBytes = Encoding.UTF8.GetBytes(serverName);
        byte[] versionBytes = Encoding.UTF8.GetBytes(version);

        using (Assert.Multiple())
        {
            await Assert.That(response[42]).IsEqualTo((byte)0x01);
            await Assert.That(response[43]).IsEqualTo((byte)0x66);
            await Assert.That(response.Length).IsEqualTo(69 + serverNameBytes.Length + versionBytes.Length);
            await Assert.That(response.Skip(46).Take(serverNameBytes.Length).SequenceEqual(serverNameBytes)).IsTrue();
            await Assert.That(response.Skip(50 + serverNameBytes.Length).Take(versionBytes.Length).SequenceEqual(versionBytes)).IsTrue();
        }
    }

    // The Version Field Is A Variable-Length String Followed By A Constant Number Of Trailing Bytes, So A Longer Version Grows The Packet Rather Than Being Cropped To Fit It
    [Test]
    public async Task A_Version_Longer_Than_Twelve_Bytes_Is_Not_Cropped()
    {
        const string serverName = "KONGOR ARENA";
        const string version = "4.10.1.20260617";

        byte[] response = UDPPingResponder.BuildResponseTemplate(serverName, version);

        byte[] serverNameBytes = Encoding.UTF8.GetBytes(serverName);
        byte[] versionBytes = Encoding.UTF8.GetBytes(version);

        using (Assert.Multiple())
        {
            await Assert.That(versionBytes.Length).IsGreaterThan(12);
            await Assert.That(response.Length).IsEqualTo(69 + serverNameBytes.Length + versionBytes.Length);
            await Assert.That(response.Skip(50 + serverNameBytes.Length).Take(versionBytes.Length).SequenceEqual(versionBytes)).IsTrue();
        }
    }

    [Test]
    public async Task An_Unresolved_Version_Is_Advertised_As_The_Placeholder()
    {
        const string serverName = "Server";

        byte[] response = UDPPingResponder.BuildResponseTemplate(serverName, DistributionSynchronisationService.UnknownDistributionVersion);

        byte[] serverNameBytes = Encoding.UTF8.GetBytes(serverName);
        byte[] versionBytes = Encoding.UTF8.GetBytes(DistributionSynchronisationService.UnknownDistributionVersion);

        using (Assert.Multiple())
        {
            await Assert.That(response.Length).IsEqualTo(69 + serverNameBytes.Length + versionBytes.Length);
            await Assert.That(response.Skip(50 + serverNameBytes.Length).Take(versionBytes.Length).SequenceEqual(versionBytes)).IsTrue();
        }
    }
}
