using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// Bounds handling in the shared protobuf reader. A length-delimited field carries its length as a
/// varint and nothing stops a hostile client from putting a value with bit 31 set in there, so the
/// reader has to compare that length against what is left in the buffer while it is still wide. A
/// narrowing cast taken before the comparison turns 0x80000000 into int.MinValue, which reads as
/// comfortably in bounds and leaves the slice or the advance that follows to blow up. Every case
/// below asserts a clean false, and the ones that pass through the two parsers assert it from the
/// outside, where a session actually feeds frames in.
///
/// Frames are built with the independent encoder in ProtoWire rather than with VsWire, so the
/// reader is measured against the wire format and not against its own writer.
/// </summary>
public class VsWireTests
{
    // (int)0x80000000 is int.MinValue, so pos + (int)len goes negative and any bounds check taken
    // after the cast waves it through.
    private const ulong HighBitLength = 0x80000000UL;

    /// <summary>The value half of a length-delimited field: the length varint, then the bytes that
    /// are really there. The declared length is a separate argument so a test can make it lie.</summary>
    private static byte[] LengthDelimitedValue(ulong declaredLength, byte[] content)
    {
        var value = new MemoryStream();
        ProtoWire.WriteVarint(value, declaredLength);
        value.Write(content);
        return value.ToArray();
    }

    private static byte[] LengthDelimitedField(int fieldNumber, ulong declaredLength, byte[] content)
    {
        var field = new MemoryStream();
        ProtoWire.WriteTag(field, fieldNumber, 2);
        ProtoWire.WriteVarint(field, declaredLength);
        field.Write(content);
        return field.ToArray();
    }

    // ---- SkipField ----

    [Fact]
    public void SkipField_RejectsALengthWithTheHighBitSet()
    {
        byte[] buf = LengthDelimitedValue(HighBitLength, [1, 2, 3]);
        int pos = 0;

        Assert.False(VsWire.SkipField(buf, ref pos, wireType: 2));
    }

    [Fact]
    public void SkipField_RejectsALengthThatWouldWrapTheWholeRange()
    {
        byte[] buf = LengthDelimitedValue(ulong.MaxValue, [1, 2, 3]);
        int pos = 0;

        Assert.False(VsWire.SkipField(buf, ref pos, wireType: 2));
    }

    [Fact]
    public void SkipField_RejectsAnHonestLengthThatOverrunsByOne()
    {
        byte[] buf = LengthDelimitedValue(4, [1, 2, 3]);
        int pos = 0;

        Assert.False(VsWire.SkipField(buf, ref pos, wireType: 2));
    }

    [Fact]
    public void SkipField_AcceptsALengthThatReachesExactlyTheEnd()
    {
        byte[] buf = LengthDelimitedValue(3, [1, 2, 3]);
        int pos = 0;

        Assert.True(VsWire.SkipField(buf, ref pos, wireType: 2));
        Assert.Equal(buf.Length, pos);
    }

    [Fact]
    public void SkipField_LandsOnTheNextFieldAfterAnOrdinaryValue()
    {
        var buf = new MemoryStream();
        ProtoWire.WriteVarint(buf, 3uL);
        buf.Write([1, 2, 3]);
        int tail = (int)buf.Length;
        ProtoWire.WriteTag(buf, 7, 0);
        ProtoWire.WriteVarint(buf, 42uL);
        int pos = 0;

        Assert.True(VsWire.SkipField(buf.ToArray(), ref pos, wireType: 2));
        Assert.Equal(tail, pos);
    }

    // ---- TryFindNestedField ----

    [Fact]
    public void TryFindNestedField_RejectsALengthWithTheHighBitSetOnTheFieldItWants()
    {
        byte[] body = LengthDelimitedField(2, HighBitLength, [1, 2, 3]);

        Assert.False(VsWire.TryFindNestedField(body, fieldNumber: 2, out _));
    }

    [Fact]
    public void TryFindNestedField_RejectsALengthWithTheHighBitSetOnAFieldItSkips()
    {
        // The decoy is skipped rather than returned, which is how a bad length inside SkipField
        // gets to matter: it drives pos negative and the next pass over the loop indexes there.
        var body = new MemoryStream();
        body.Write(LengthDelimitedField(1, HighBitLength, [1, 2, 3]));
        ProtoWire.WriteBytes(body, 2, [9, 9]);

        Assert.False(VsWire.TryFindNestedField(body.ToArray(), fieldNumber: 2, out _));
    }

    [Fact]
    public void TryFindNestedField_RejectsAnHonestLengthThatOverrunsByOne()
    {
        byte[] body = LengthDelimitedField(2, 4, [1, 2, 3]);

        Assert.False(VsWire.TryFindNestedField(body, fieldNumber: 2, out _));
    }

    [Fact]
    public void TryFindNestedField_AcceptsALengthThatReachesExactlyTheEnd()
    {
        byte[] body = LengthDelimitedField(2, 3, [1, 2, 3]);

        Assert.True(VsWire.TryFindNestedField(body, fieldNumber: 2, out var nested));
        Assert.Equal<byte>([1, 2, 3], nested.ToArray());
    }

    [Fact]
    public void TryFindNestedField_ReturnsTheContentsOfAnOrdinaryNestedField()
    {
        var body = new MemoryStream();
        ProtoWire.WriteBytes(body, 1, [7, 7, 7]);
        ProtoWire.WriteBytes(body, 2, [1, 2, 3, 4]);

        Assert.True(VsWire.TryFindNestedField(body.ToArray(), fieldNumber: 2, out var nested));
        Assert.Equal<byte>([1, 2, 3, 4], nested.ToArray());
    }

    // ---- through the parsers, the way a session feeds them ----

    [Fact]
    public void IdentificationParser_ReturnsFalseOnAHighBitLengthInsideTheIdentBody()
    {
        var ident = new MemoryStream();
        ident.Write(LengthDelimitedField(6, HighBitLength, "uid-123"u8.ToArray())); // PlayerUID
        var envelope = new MemoryStream();
        ProtoWire.WriteBytes(envelope, 2, ident.ToArray());

        Assert.False(IdentificationParser.TryExtract(ProtoWire.Frame(envelope.ToArray()), out _, out _));
    }

    [Fact]
    public void IdentificationParser_ReturnsFalseOnAHighBitLengthOnTheEnvelopeItself()
    {
        var envelope = new MemoryStream();
        envelope.Write(LengthDelimitedField(2, HighBitLength, [1, 2, 3]));

        Assert.False(IdentificationParser.TryExtract(ProtoWire.Frame(envelope.ToArray()), out _, out _));
    }

    [Fact]
    public void IdentificationParser_StillReadsAnOrdinaryFrame()
    {
        Assert.True(IdentificationParser.TryExtract(ClientFrames.Identification("uid-123", "alice"), out string uid, out string name));
        Assert.Equal("uid-123", uid);
        Assert.Equal("alice", name);
    }

    [Fact]
    public void ChatlineParser_ReturnsFalseOnAHighBitLengthInsideTheChatlineBody()
    {
        var chatline = new MemoryStream();
        chatline.Write(LengthDelimitedField(1, HighBitLength, "hello"u8.ToArray())); // Message
        var envelope = new MemoryStream();
        ProtoWire.WriteBytes(envelope, 4, chatline.ToArray());

        Assert.False(ChatlineParser.TryExtract(ProtoWire.Frame(envelope.ToArray()), out _, out _));
    }

    [Fact]
    public void ChatlineParser_ReturnsFalseOnAHighBitLengthOnTheEnvelopeItself()
    {
        var envelope = new MemoryStream();
        envelope.Write(LengthDelimitedField(4, HighBitLength, [1, 2, 3]));

        Assert.False(ChatlineParser.TryExtract(ProtoWire.Frame(envelope.ToArray()), out _, out _));
    }

    [Fact]
    public void ChatlineParser_StillReadsAnOrdinaryFrame()
    {
        var chatline = new MemoryStream();
        ProtoWire.WriteString(chatline, 1, "hello world");
        ProtoWire.WriteTag(chatline, 2, 0);
        ProtoWire.WriteVarint(chatline, 4uL);
        var envelope = new MemoryStream();
        ProtoWire.WriteBytes(envelope, 4, chatline.ToArray());

        Assert.True(ChatlineParser.TryExtract(ProtoWire.Frame(envelope.ToArray()), out string message, out int groupId));
        Assert.Equal("hello world", message);
        Assert.Equal(4, groupId);
    }
}
