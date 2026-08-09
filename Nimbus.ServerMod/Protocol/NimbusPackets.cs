namespace Nimbus.ServerMod;

using ProtoBuf;

// Wire contract for the "nimbus" channel. Every tag below is pinned deliberately.
//
// These packets used to be declared with [ProtoContract(ImplicitFields =
// ImplicitFields.AllPublic)], which lets protobuf-net hand out the field numbers itself.
// It does that by sorting the public members by name with an ordinal comparison and
// numbering them from 1, so the numbering is a function of the member names rather than
// of anything a reader can see. Add one property and every member that sorts after it
// moves up a slot: on NimbusSeamlessPrepare an ExpiresInSeconds would have taken tag 1
// and pushed Reason, TargetServerId and TransferId to 2, 3 and 4. Nothing in the diff
// would say so, and any client still parsing the old numbering would silently read
// garbage.
//
// The numbers here are exactly the ones the implicit assignment was already producing, so
// this is a no-op on the wire. They are frozen at those values now, which is why they do
// not run in declaration order.
//
// Adding a member: give it the next free number in that contract and never touch or
// reorder an existing one. A member without a [ProtoMember] is not serialized at all
// under an explicit contract, so the attribute is not optional. NimbusPacketTagTests
// fails if one is missing.

[ProtoContract]
public sealed class NimbusClientHello
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
    [ProtoMember(2)] public bool SupportsSeamlessTransfers { get; set; }
}

[ProtoContract]
public sealed class NimbusSeamlessPrepare
{
    [ProtoMember(3)] public string TransferId { get; set; } = "";
    [ProtoMember(2)] public string TargetServerId { get; set; } = "";
    [ProtoMember(1)] public string Reason { get; set; } = "";
}

[ProtoContract]
public sealed class NimbusSeamlessCommit
{
    [ProtoMember(1)] public string TransferId { get; set; } = "";
}

[ProtoContract]
public sealed class NimbusSeamlessReady
{
    [ProtoMember(1)] public string TransferId { get; set; } = "";
}

[ProtoContract]
public sealed class NimbusSeamlessAbort
{
    [ProtoMember(2)] public string TransferId { get; set; } = "";
    [ProtoMember(1)] public string Message { get; set; } = "";
}
