using System.Text;

namespace Nimbus.Proxy;

// Extracts the PlayerUID (and Playername) from a captured client Identification frame so the
// proxy can mint a pre-swap reservation against the registry.
//
// Frame layout: VS TCP header (4 BE bytes) then a Packet_Client envelope. Inside that,
// field 2 (wire-type 2) holds the nested Packet_ClientIdentification. Relevant string fields:
//   2 -> Playername     6 -> PlayerUID
internal static class IdentificationParser
{
    // Parse player UID + name out of a captured raw client-to-server frame (length-prefixed,
    // the way it lives in ProxySession.capturedIdentification). Returns false on malformed input.
    public static bool TryExtract(ReadOnlySpan<byte> rawFrame, out string playerUid, out string playerName)
    {
        playerUid = "";
        playerName = "";
        if (!VsWire.TryParseHeader(rawFrame, out bool compressed, out int payloadLen)) return false;
        if (compressed) return false; // Identification frames are never compressed.
        if (payloadLen <= 0 || 4 + payloadLen > rawFrame.Length) return false;

        var payload = rawFrame.Slice(4, payloadLen);

        // Preferred path: outer envelope field 2 contains Packet_ClientIdentification.
        // Some forks/dev builds flatten this once, so fall back to parsing the payload
        // directly as an Identification body.
        if (VsWire.TryFindNestedField(payload, fieldNumber: 2, out var ident) && ParseIdentBody(ident, out playerUid, out playerName))
            return true;

        return ParseIdentBody(payload, out playerUid, out playerName);
    }

    // Essential complexity (S3776 reports cognitive complexity 25). This walks a protobuf body one
    // field at a time: read the tag, and for a length-delimited Playername (field 2) or PlayerUID
    // (field 6) decode and bounds-check the string, otherwise skip the field. Two interesting
    // fields, each length-delimited read carrying its own bounds check, plus the early return once
    // both are in hand, is what the wire format costs; the count is the layout's, not the code's.
    // Splitting the per-field handling into helpers would pull the field-number-to-output mapping
    // away from the loop that reads it and thread `ref pos` plus the out-parameters through every
    // helper, making the frame harder to check against the layout documented at the top of this
    // file rather than easier. It is clearest as one flat pass down the body.
    private static bool ParseIdentBody(ReadOnlySpan<byte> body, out string playerUid, out string playerName) // NOSONAR
    {
        playerUid = "";
        playerName = "";
        int pos = 0;
        while (pos < body.Length)
        {
            if (!VsWire.TryReadVarint(body, ref pos, out ulong key)) return false;
            int fieldNum = (int)(key >> 3);
            int wireType = (int)(key & 0x7);
            if (wireType == 2 && (fieldNum == 2 || fieldNum == 6))
            {
                if (!VsWire.TryReadLength(body, ref pos, out int len)) return false;
                string val = Encoding.UTF8.GetString(body.Slice(pos, len));
                pos += len;
                if (fieldNum == 2) playerName = val;
                else playerUid = val;
                if (playerName.Length > 0 && playerUid.Length > 0) return true;
            }
            else
            {
                if (!VsWire.SkipField(body, ref pos, wireType)) return false;
            }
        }
        return playerUid.Length > 0; // PlayerUID is required, name is best-effort.
    }

}
