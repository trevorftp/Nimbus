using System.Text;

namespace Nimbus.Proxy;

// Shared Vintage Story wire-format primitives. One home for the two things every
// protocol class was hand-rolling separately:
//
//   * the VS TCP frame header, four big-endian bytes, where bit 31 carries the
//     zlib-compressed flag and bits 30 down to 0 carry the payload length
//   * minimal protobuf writing (varints, tags, length-delimited fields), enough to
//     forge the handful of vanilla packets the proxy fabricates
//   * minimal protobuf reading (varints, field skipping, nested-field lookup), enough
//     for the parsers that pull player identity and chat text out of sniffed frames
//
// Byte-for-byte identical to the previous per-class implementations. The wire tests
// in Nimbus.Proxy.Tests decode the produced frames with an independent reader.
internal static class VsWire
{
    public const int MaxFrameSize = 256 * 1024 * 1024; // VS uses a 128 MB MaxPacketSize

    // ---- frame header ----

    // Parses the 4-byte header. Returns false when fewer than 4 bytes are available.
    public static bool TryParseHeader(ReadOnlySpan<byte> bytes, out bool compressed, out int payloadLength)
    {
        compressed = false;
        payloadLength = 0;
        if (bytes.Length < 4) return false;
        uint header = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
        compressed = (header & 0x80000000u) != 0;
        payloadLength = (int)(header & 0x7FFFFFFFu);
        return true;
    }

    // Wraps a payload in an uncompressed VS frame (header + payload).
    public static byte[] WrapFrame(byte[] payload)
    {
        int len = payload.Length;
        var frame = new byte[4 + len];
        frame[0] = (byte)((len >> 24) & 0x7F);
        frame[1] = (byte)((len >> 16) & 0xFF);
        frame[2] = (byte)((len >> 8) & 0xFF);
        frame[3] = (byte)(len & 0xFF);
        Buffer.BlockCopy(payload, 0, frame, 4, len);
        return frame;
    }

    // ---- protobuf writing ----

    public static void WriteVarint(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }

    public static void WriteTag(Stream s, int fieldNumber, int wireType)
        => WriteVarint(s, (ulong)((fieldNumber << 3) | wireType));

    public static void WriteVarintField(Stream s, int fieldNumber, ulong value)
    {
        WriteTag(s, fieldNumber, 0);
        WriteVarint(s, value);
    }

    public static void WriteBytesField(Stream s, int fieldNumber, ReadOnlySpan<byte> value)
    {
        WriteTag(s, fieldNumber, 2);
        WriteVarint(s, (ulong)value.Length);
        s.Write(value);
    }

    public static void WriteStringField(Stream s, int fieldNumber, string value)
        => WriteBytesField(s, fieldNumber, Encoding.UTF8.GetBytes(value));

    // --- Reading ---

    public static bool TryReadVarint(ReadOnlySpan<byte> buf, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < buf.Length)
        {
            byte b = buf[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 63) return false;
        }
        return false;
    }

    // Reads the length varint that opens a length-delimited field and checks it against the bytes
    // actually left in the buffer, handing back a length the caller can slice or advance by.
    //
    // The comparison happens in ulong on purpose. Narrowing first and comparing afterwards turns a
    // length with bit 31 set into a negative int, which reads as comfortably in bounds and leaves
    // the slice or the advance that follows to throw on a frame the parser should simply have
    // refused. Past the check the value is known to fit in what remains of the buffer, so it fits
    // in an int and the cast on the way out cannot lose anything.
    public static bool TryReadLength(ReadOnlySpan<byte> buf, ref int pos, out int len)
    {
        len = 0;
        if (!TryReadVarint(buf, ref pos, out ulong raw)) return false;
        // TryReadVarint leaves pos between 0 and buf.Length, so the subtraction is non-negative.
        if (raw > (ulong)(buf.Length - pos)) return false;
        len = (int)raw;
        return true;
    }

    // Advances past the value of a field whose key was just read. False on truncated input or
    // wire types the proxy never needs (groups).
    public static bool SkipField(ReadOnlySpan<byte> buf, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case 0: // varint
                return TryReadVarint(buf, ref pos, out _);
            case 1: // 64-bit
                if (pos + 8 > buf.Length) return false;
                pos += 8;
                return true;
            case 2: // length-delimited
                if (!TryReadLength(buf, ref pos, out int len)) return false;
                pos += len;
                return true;
            case 5: // 32-bit
                if (pos + 4 > buf.Length) return false;
                pos += 4;
                return true;
            default:
                return false; // groups (3, 4) and unknown wire types
        }
    }

    // Finds a length-delimited field by number and hands back its contents, for pulling a
    // nested message out of a packet envelope.
    public static bool TryFindNestedField(ReadOnlySpan<byte> body, int fieldNumber, out ReadOnlySpan<byte> nested)
    {
        nested = default;
        int pos = 0;
        while (pos < body.Length)
        {
            if (!TryReadVarint(body, ref pos, out ulong key)) return false;
            int field = (int)(key >> 3);
            int wireType = (int)(key & 0x7);
            if (field == fieldNumber && wireType == 2)
            {
                if (!TryReadLength(body, ref pos, out int len)) return false;
                nested = body.Slice(pos, len);
                return true;
            }
            if (!SkipField(body, ref pos, wireType)) return false;
        }
        return false;
    }

    // Envelope helper for forged server packets: Packet_Server.Id (field 90) as a varint,
    // then the nested body under its own field number, wrapped in a frame.
    public static byte[] BuildServerPacketFrame(int packetId, int bodyField, byte[] body)
    {
        var env = new MemoryStream();
        WriteVarintField(env, 90, (ulong)packetId);
        WriteBytesField(env, bodyField, body);
        return WrapFrame(env.ToArray());
    }
}
