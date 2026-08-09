using System.Text;

namespace Nimbus.Proxy;

// Extracts what a player typed out of a sniffed client-to-server Chatline frame, so plugins can
// observe chat without the proxy interpreting or forging any of it.
//
// Frame layout: VS TCP header (4 BE bytes) then a Packet_Client envelope whose field 4 holds a
// nested Packet_ChatLine. Inside that (from Packet_ChatLine in VintagestoryLib):
//   1 -> Message (string)   2 -> Groupid (int)   3 -> ChatType (int)   4 -> Data (string)
internal static class ChatlineParser
{
    // Field number of Packet_ChatLine inside Packet_Client. Matches the "Chatline" tag 34
    // (4 << 3 | 2) in PacketDispatch.ClientTags.
    private const int ChatlineEnvelopeField = 4;

    private const int MessageField = 1;
    private const int GroupIdField = 2;

    public static bool TryExtract(ReadOnlySpan<byte> rawFrame, out string message, out int groupId)
    {
        message = "";
        groupId = 0;

        if (!VsWire.TryParseHeader(rawFrame, out bool compressed, out int payloadLen)) return false;
        if (compressed) return false; // chat frames are far below the compression threshold
        if (payloadLen <= 0 || 4 + payloadLen > rawFrame.Length) return false;

        var payload = rawFrame.Slice(4, payloadLen);

        // Same shape as IdentificationParser: prefer the nested body, fall back to a flattened
        // payload so a fork that serializes one level shallower still parses.
        if (VsWire.TryFindNestedField(payload, ChatlineEnvelopeField, out var body) &&
            ParseChatlineBody(body, out message, out groupId))
            return true;

        return ParseChatlineBody(payload, out message, out groupId);
    }

    // Essential complexity (S3776 reports cognitive complexity 21). This walks a protobuf body one
    // field at a time: read the tag, and either decode the one length-delimited field we want
    // (Message), the one varint field we want (Groupid), or skip anything else. Each field is its
    // own branch and each length-delimited read carries its own bounds check because that is what
    // the wire format is; the count is the layout's, not the code's. Splitting the per-field
    // handling into helpers would move the field-number-to-output mapping away from the loop that
    // reads it and force every helper to thread `ref pos` and the out-parameters, making the frame
    // harder to check against the layout documented at the top of this file rather than easier. It
    // is clearest as one flat pass down the body.
    private static bool ParseChatlineBody(ReadOnlySpan<byte> body, out string message, out int groupId) // NOSONAR
    {
        message = "";
        groupId = 0;
        bool sawMessage = false;

        int pos = 0;
        while (pos < body.Length)
        {
            if (!VsWire.TryReadVarint(body, ref pos, out ulong key)) return false;
            int fieldNum = (int)(key >> 3);
            int wireType = (int)(key & 0x7);

            if (fieldNum == MessageField && wireType == 2)
            {
                if (!VsWire.TryReadLength(body, ref pos, out int len)) return false;
                message = Encoding.UTF8.GetString(body.Slice(pos, len));
                pos += len;
                sawMessage = true;
                continue;
            }

            if (fieldNum == GroupIdField && wireType == 0)
            {
                if (!VsWire.TryReadVarint(body, ref pos, out ulong value)) return false;
                groupId = (int)value;
                continue;
            }

            if (!VsWire.SkipField(body, ref pos, wireType)) return false;
        }

        // An empty message is not worth an event, and is what a mis-parsed frame looks like.
        return sawMessage && message.Length > 0;
    }
}
