namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies that the engine's reconnect query and the match server's reply to it are recognised, and that a datagram differing from the request in length or in any fixed byte is not.
///     The request is pinned as literal bytes, as the chat server sends it, rather than built from the recogniser's own constants, so that a change to the recognised shape cannot pass unnoticed.
/// </summary>
public sealed class ReconnectQueryTests
{
    // The Packet Header (A Zero Connection ID And The Normal Packet Flag), The Query Command, Match ID 0x01020304, Account ID 0x0A0B0C0D, And A Zero Connection ID, All Little-Endian
    private static readonly byte[] Query = [ 0x00, 0x00, 0x01, 0xCC, 0x04, 0x03, 0x02, 0x01, 0x0D, 0x0C, 0x0B, 0x0A, 0x00, 0x00 ];

    /// <summary>
    ///     The request as the chat server sends it: the query at the start of the datagram and again after the watermark area, with zeroes between.
    /// </summary>
    internal static byte[] Request()
    {
        byte[] request = new byte[54];

        Query.CopyTo(request, 0);
        Query.CopyTo(request, 40);

        return request;
    }

    /// <summary>
    ///     The reply as a match server without a watermark sends it: the packet header, the reply command, and the slot time remaining.
    /// </summary>
    internal static byte[] Reply(uint slotTimeRemaining)
    {
        byte[] reply = new byte[8];

        reply[2] = 0x01;
        reply[3] = 0x6F;

        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), slotTimeRemaining);

        return reply;
    }

    [Test]
    public async Task The_Request_Is_Recognised()
    {
        await Assert.That(ReconnectQuery.IsRequest(Request())).IsTrue();
    }

    [Test]
    public async Task A_Request_For_Another_Match_And_Account_Is_Recognised()
    {
        byte[] request = Request();

        foreach (int offset in (int[])[ 0, 40 ])
        {
            BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(offset + 4), 987654);
            BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(offset + 8), 123456);
        }

        await Assert.That(ReconnectQuery.IsRequest(request)).IsTrue();
    }

    // Every Byte Other Than The Two IDs Is Fixed, Including The Zeroes Where A Client Datagram Carries Its Challenge And Counter, So Changing Any One Of Them Must Leave The Datagram To The Ordinary Path
    [Test]
    public async Task A_Request_Differing_In_Any_Fixed_Byte_Is_Not_Recognised()
    {
        List<int> recognised = new ();

        for (int offset = 0; offset < Request().Length; offset++)
        {
            if (offset is (>= 4 and < 12) or (>= 44 and < 52))
                continue;

            byte[] request = Request();

            request[offset] ^= 0xFF;

            if (ReconnectQuery.IsRequest(request))
                recognised.Add(offset);
        }

        await Assert.That(recognised).IsEmpty();
    }

    [Test]
    public async Task A_Request_Whose_Two_Copies_Differ_Is_Not_Recognised()
    {
        byte[] request = Request();

        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(40 + 4), 987654);

        await Assert.That(ReconnectQuery.IsRequest(request)).IsFalse();
    }

    // Fourteen Bytes Is The Bare Query, Without The Watermark Area Or The Second Copy
    [Test]
    [Arguments(0)]
    [Arguments(14)]
    [Arguments(53)]
    [Arguments(55)]
    public async Task A_Request_Of_Another_Length_Is_Not_Recognised(int length)
    {
        byte[] datagram = new byte[length];

        Request().AsSpan(0, Math.Min(length, Request().Length)).CopyTo(datagram);

        await Assert.That(ReconnectQuery.IsRequest(datagram)).IsFalse();
    }

    [Test]
    public async Task The_Reply_Is_Recognised_With_And_Without_A_Watermark()
    {
        byte[] bare = Reply(240000);
        byte[] watermarked = new byte[40 + bare.Length];

        bare.CopyTo(watermarked, 40);

        using (Assert.Multiple())
        {
            await Assert.That(ReconnectQuery.IsReply(bare)).IsTrue();
            await Assert.That(ReconnectQuery.IsReply(watermarked)).IsTrue();
        }
    }

    [Test]
    [Arguments(8, 3, 0x66)]
    [Arguments(48, 43, 0x66)]
    [Arguments(7, 3, 0x6F)]
    [Arguments(9, 3, 0x6F)]
    [Arguments(47, 43, 0x6F)]
    [Arguments(49, 43, 0x6F)]
    public async Task A_Datagram_Of_Another_Length_Or_Command_Is_Not_A_Reply(int length, int commandOffset, int command)
    {
        byte[] datagram = new byte[length];

        datagram[commandOffset] = (byte)command;

        await Assert.That(ReconnectQuery.IsReply(datagram)).IsFalse();
    }

    // A Reply Is Relayed To Whatever Source Sent The Request, Which Need Not Be The Source It Claims To Be, So No Recognised Reply May Be Larger Than The Request That Earns It
    [Test]
    public async Task No_Datagram_Longer_Than_The_Request_Is_Recognised_As_A_Reply()
    {
        List<int> recognised = new ();

        for (int length = Request().Length + 1; length <= 1500; length++)
        {
            byte[] datagram = new byte[length];

            datagram[3] = 0x6F;
            datagram[43] = 0x6F;

            if (ReconnectQuery.IsReply(datagram))
                recognised.Add(length);
        }

        await Assert.That(recognised).IsEmpty();
    }
}
