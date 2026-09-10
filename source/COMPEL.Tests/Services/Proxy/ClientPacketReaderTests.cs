namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies the length guard and the field offsets used to read a client datagram, both taken from the reference proxy.
/// </summary>
public sealed class ClientPacketReaderTests
{
    private static byte[] BuildDatagram(uint challenge, ushort counter, int length)
    {
        byte[] datagram = new byte[length];

        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(28), challenge);
        BinaryPrimitives.WriteUInt16LittleEndian(datagram.AsSpan(32), counter);

        return datagram;
    }

    [Test]
    public async Task The_Minimum_Length_Differs_By_Kind()
    {
        using (Assert.Multiple())
        {
            await Assert.That(ClientPacketReader.MinimumLength(ProxyForwarderKind.Game)).IsEqualTo(43);
            await Assert.That(ClientPacketReader.MinimumLength(ProxyForwarderKind.Voice)).IsEqualTo(41);
        }
    }

    [Test]
    public async Task The_Challenge_And_Counter_Are_Read_From_Their_Reference_Offsets()
    {
        byte[] datagram = BuildDatagram(challenge: 0x11223344, counter: 4321, length: 64);

        bool read = ClientPacketReader.TryRead(datagram, ProxyForwarderKind.Game, out uint challenge, out ushort counter);

        using (Assert.Multiple())
        {
            await Assert.That(read).IsTrue();
            await Assert.That(challenge).IsEqualTo(0x11223344u);
            await Assert.That(counter).IsEqualTo((ushort)4321);
        }
    }

    // The Guard Exists So No Field Is Ever Read Out Of Bounds: A Datagram One Byte Short Of The Minimum Must Be Refused Outright
    [Test]
    public async Task A_Datagram_One_Byte_Below_The_Minimum_Is_Refused()
    {
        foreach (ProxyForwarderKind kind in (ProxyForwarderKind[])[ ProxyForwarderKind.Game, ProxyForwarderKind.Voice ])
        {
            byte[] datagram = new byte[ClientPacketReader.MinimumLength(kind) - 1];

            bool read = ClientPacketReader.TryRead(datagram, kind, out uint challenge, out ushort counter);

            using (Assert.Multiple())
            {
                await Assert.That(read).IsFalse();
                await Assert.That(challenge).IsEqualTo(0u);
                await Assert.That(counter).IsEqualTo((ushort)0);
            }
        }
    }

    [Test]
    public async Task An_Empty_Datagram_Is_Refused_Without_Throwing()
    {
        bool read = ClientPacketReader.TryRead([], ProxyForwarderKind.Game, out _, out _);

        await Assert.That(read).IsFalse();
    }

    [Test]
    public async Task A_Datagram_Exactly_At_The_Minimum_Is_Accepted()
    {
        byte[] datagram = BuildDatagram(challenge: 7, counter: 9, length: ClientPacketReader.MinimumLength(ProxyForwarderKind.Game));

        bool read = ClientPacketReader.TryRead(datagram, ProxyForwarderKind.Game, out uint challenge, out ushort counter);

        using (Assert.Multiple())
        {
            await Assert.That(read).IsTrue();
            await Assert.That(challenge).IsEqualTo(7u);
            await Assert.That(counter).IsEqualTo((ushort)9);
        }
    }
}
