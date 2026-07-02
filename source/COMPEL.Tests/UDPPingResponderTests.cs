namespace COMPEL.Tests;

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

    [Test]
    public async Task A_Missing_Version_Produces_A_Template_With_No_Version_Bytes()
    {
        byte[] response = UDPPingResponder.BuildResponseTemplate("Server", version: null);

        byte[] serverNameBytes = Encoding.UTF8.GetBytes("Server");

        await Assert.That(response.Length).IsEqualTo(69 + serverNameBytes.Length);
    }
}
