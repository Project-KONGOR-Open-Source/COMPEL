namespace COMPEL.Services.Proxy;

/// <summary>
///     Recognises the engine's connectionless reconnect query, which asks a match server how long a player's slot in its match can still be reclaimed, and the match server's reply to it.
///     The request is the one the chat server sends to verify a match lock: the query (the packet header, the query command, the match ID, the account ID, and the connection ID) at the start of the datagram and again after the watermark area, so that a match server finds it whether or not it expects a watermark.
///     The reply is the packet header, the reply command, and the slot time remaining, either bare or after the watermark area.
///     Only the two IDs vary, so every other byte of the request must match exactly, and anything that does not is left to the ordinary path.
/// </summary>
internal static class ReconnectQuery
{
    // "WATERMARK_LEN_TOTAL" Plus "ENHANCED_WATERMARK_LEN_TOTAL", After Which A Match Server Which Expects A Watermark Reads Its Copy Of The Query
    private const int WatermarkLength = 40;

    // The Packet Header (A "ushort" Connection ID And A Flags Byte), The Command, The "uint" Match ID And Account ID, And The "ushort" Connection ID
    private const int QueryLength = 14;

    // The Packet Header, The Command, And The "uint" Slot Time Remaining
    private const int ReplyLength = 8;

    private const int FlagsOffset = 2;
    private const int CommandOffset = 3;
    private const int ConnectionIDOffset = 12;

    // "PACKET_NORMAL"
    private const byte NormalPacketFlag = 0x01;

    // "NETCMD_RECONNECT_INFO_REQUEST"
    private const byte RequestCommand = 0xCC;

    // "NETCMD_RECONNECT_INFO_RESPONSE"
    private const byte ReplyCommand = 0x6F;

    /// <summary>
    ///     The length of the request, which carries the query twice.
    /// </summary>
    internal const int RequestLength = WatermarkLength + QueryLength;

    /// <summary>
    ///     The length of the longer of the two replies, the one which follows the watermark area.
    /// </summary>
    internal const int WatermarkedReplyLength = WatermarkLength + ReplyLength;

    /// <summary>
    ///     Whether <paramref name="datagram"/> is the reconnect query, byte for byte apart from the match ID and the account ID, which must be the same in both copies.
    /// </summary>
    internal static bool IsRequest(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length is not RequestLength)
            return false;

        ReadOnlySpan<byte> query = datagram[..QueryLength];

        // Everything Between The Two Copies Is Zero, Including Where A Client Datagram Carries Its Challenge And Counter
        if (query.SequenceEqual(datagram[WatermarkLength..]) is false || datagram[QueryLength..WatermarkLength].ContainsAnyExcept((byte)0))
            return false;

        return query[0] is 0 && query[1] is 0
            && query[FlagsOffset] is NormalPacketFlag
            && query[CommandOffset] is RequestCommand
            && query[ConnectionIDOffset] is 0 && query[ConnectionIDOffset + 1] is 0;
    }

    /// <summary>
    ///     Whether <paramref name="datagram"/> has the shape of a reply to the reconnect query, exactly as the chat server reads one: either length, with the reply command where that length places it.
    /// </summary>
    internal static bool IsReply(ReadOnlySpan<byte> datagram)
    {
        int commandOffset = datagram.Length switch
        {
            ReplyLength            => CommandOffset,
            WatermarkedReplyLength => WatermarkLength + CommandOffset,
            _                      => -1
        };

        return commandOffset >= 0 && datagram[commandOffset] is ReplyCommand;
    }
}
