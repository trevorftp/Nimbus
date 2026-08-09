namespace Nimbus.ServerMod.Protocol.Tests.Legacy;

using ProtoBuf;

// The packet declarations exactly as they stood before the tags were pinned, member for
// member and attribute for attribute, kept here as the reference the pinned contracts are
// measured against. Nothing outside the test assembly uses them.
//
// Frozen on purpose: this is the shape a client built against the old numbering still
// speaks, so it has to keep saying what the wire used to look like even after the real
// declarations grow new members. Do not add fields here to match a change on the other
// side. If a new packet type is added over there, add its pre-existing shape here only if
// it also existed before this change, which for anything new it did not.

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public sealed class NimbusClientHello
{
    public int ProtocolVersion { get; set; }
    public bool SupportsSeamlessTransfers { get; set; }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public sealed class NimbusSeamlessPrepare
{
    public string TransferId { get; set; } = "";
    public string TargetServerId { get; set; } = "";
    public string Reason { get; set; } = "";
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public sealed class NimbusSeamlessCommit
{
    public string TransferId { get; set; } = "";
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public sealed class NimbusSeamlessReady
{
    public string TransferId { get; set; } = "";
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public sealed class NimbusSeamlessAbort
{
    public string TransferId { get; set; } = "";
    public string Message { get; set; } = "";
}
