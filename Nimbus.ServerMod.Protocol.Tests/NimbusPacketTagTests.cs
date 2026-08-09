using System.Reflection;
using ProtoBuf;
using ProtoBuf.Meta;
using Xunit;
using Legacy = Nimbus.ServerMod.Protocol.Tests.Legacy;

namespace Nimbus.ServerMod.Protocol.Tests;

/// <summary>
/// The wire format of the "nimbus" channel, pinned. Every field number the seamless packets
/// use was handed out by protobuf-net from the member names, so the numbering lived nowhere
/// a reader could see it and moved whenever a member was added. It is written down in the
/// contracts now, and what is checked here is that writing it down changed nothing: the same
/// values still serialize to the same bytes, and a payload produced by the old declarations
/// still reads correctly under the new ones.
///
/// LegacyImplicitPackets.cs holds the old declarations verbatim and stays frozen, so it goes
/// on speaking what a client built before this change speaks.
/// </summary>
public class NimbusPacketTagTests
{
    /// <summary>Every tag in the protocol, by hand. Nothing derives this from the code under
    /// test, so a renumbering has to break it.</summary>
    public static TheoryData<Type, string, int> PinnedTags => new()
    {
        { typeof(NimbusClientHello), nameof(NimbusClientHello.ProtocolVersion), 1 },
        { typeof(NimbusClientHello), nameof(NimbusClientHello.SupportsSeamlessTransfers), 2 },

        { typeof(NimbusSeamlessPrepare), nameof(NimbusSeamlessPrepare.Reason), 1 },
        { typeof(NimbusSeamlessPrepare), nameof(NimbusSeamlessPrepare.TargetServerId), 2 },
        { typeof(NimbusSeamlessPrepare), nameof(NimbusSeamlessPrepare.TransferId), 3 },

        { typeof(NimbusSeamlessCommit), nameof(NimbusSeamlessCommit.TransferId), 1 },

        { typeof(NimbusSeamlessReady), nameof(NimbusSeamlessReady.TransferId), 1 },

        { typeof(NimbusSeamlessAbort), nameof(NimbusSeamlessAbort.Message), 1 },
        { typeof(NimbusSeamlessAbort), nameof(NimbusSeamlessAbort.TransferId), 2 },
    };

    [Theory]
    [MemberData(nameof(PinnedTags))]
    public void EveryMemberSitsOnTheNumberItIsWrittenDownAs(Type contract, string member, int tag)
    {
        Assert.Equal(tag, TagsOf(contract)[member]);
    }

    /// <summary>The pairs of declarations to hold against each other, old shape and pinned
    /// shape, matched by name.</summary>
    public static TheoryData<Type, Type> ContractPairs => new()
    {
        { typeof(Legacy.NimbusClientHello), typeof(NimbusClientHello) },
        { typeof(Legacy.NimbusSeamlessPrepare), typeof(NimbusSeamlessPrepare) },
        { typeof(Legacy.NimbusSeamlessCommit), typeof(NimbusSeamlessCommit) },
        { typeof(Legacy.NimbusSeamlessReady), typeof(NimbusSeamlessReady) },
        { typeof(Legacy.NimbusSeamlessAbort), typeof(NimbusSeamlessAbort) },
    };

    [Theory]
    [MemberData(nameof(ContractPairs))]
    public void TheNumbersAreTheOnesProtobufWasAlreadyHandingOut(Type legacy, Type pinned)
    {
        Assert.Equal(TagsOf(legacy), TagsOf(pinned));
    }

    /// <summary>
    /// The property this whole change rests on. Both declarations get the same values, and the
    /// bytes have to come out the same, including the shapes where a member is left at its
    /// default and protobuf-net drops it from the payload entirely.
    /// </summary>
    [Theory]
    [MemberData(nameof(Payloads))]
    public void TheSameValuesSerializeToTheSameBytesUnderBothContracts(
        string _, Type legacy, Type pinned, IDictionary<string, object?> values)
    {
        Assert.Equal(
            Serialize(Populate(legacy, values)),
            Serialize(Populate(pinned, values)));
    }

    /// <summary>
    /// What a client that has not been rebuilt actually does: it puts the old numbering on the
    /// wire, and the server has to read it back into the right members.
    /// </summary>
    [Theory]
    [MemberData(nameof(Payloads))]
    public void APayloadWrittenByTheOldContractReadsBackUnderTheNewOne(
        string _, Type legacy, Type pinned, IDictionary<string, object?> values)
    {
        var read = Deserialize(pinned, Serialize(Populate(legacy, values)));

        foreach (var (member, expected) in values)
            Assert.Equal(expected, pinned.GetProperty(member)!.GetValue(read));
    }

    /// <summary>And the other direction, for a server talking to a client still on the old
    /// numbering.</summary>
    [Theory]
    [MemberData(nameof(Payloads))]
    public void APayloadWrittenByTheNewContractReadsBackUnderTheOldOne(
        string _, Type legacy, Type pinned, IDictionary<string, object?> values)
    {
        var read = Deserialize(legacy, Serialize(Populate(pinned, values)));

        foreach (var (member, expected) in values)
            Assert.Equal(expected, legacy.GetProperty(member)!.GetValue(read));
    }

    /// <summary>
    /// Byte for byte, against a payload copied out of a run of the old declarations. The
    /// comparison above only says the two sides agree with each other, which they would go on
    /// doing if somebody edited both. These are what the wire looked like on the day the tags
    /// were pinned, and nothing in the repository can move them.
    /// </summary>
    [Theory]
    [MemberData(nameof(Goldens))]
    public void TheBytesAreTheOnesTheWireCarriedBeforeAnyOfThis(
        string expectedHex, Type pinned, IDictionary<string, object?> values)
    {
        Assert.Equal(expectedHex, Convert.ToHexString(Serialize(Populate(pinned, values))));
    }

    public static TheoryData<string, Type, IDictionary<string, object?>> Goldens => new()
    {
        {
            "08071001", typeof(NimbusClientHello),
            new Dictionary<string, object?>
            {
                ["ProtocolVersion"] = 7,
                ["SupportsSeamlessTransfers"] = true,
            }
        },
        {
            "0A0377687912037372761A03746964", typeof(NimbusSeamlessPrepare),
            new Dictionary<string, object?>
            {
                ["TransferId"] = "tid",
                ["TargetServerId"] = "srv",
                ["Reason"] = "why",
            }
        },
        {
            "0A03746964", typeof(NimbusSeamlessCommit),
            new Dictionary<string, object?> { ["TransferId"] = "tid" }
        },
        {
            "0A03746964", typeof(NimbusSeamlessReady),
            new Dictionary<string, object?> { ["TransferId"] = "tid" }
        },
        {
            "0A036D73671203746964", typeof(NimbusSeamlessAbort),
            new Dictionary<string, object?>
            {
                ["TransferId"] = "tid",
                ["Message"] = "msg",
            }
        },
    };

    /// <summary>
    /// The attribute is what carries the number now, so a member that goes without one is not
    /// on the wire at all and nothing else would say so. This is the test the comment in
    /// NimbusPackets.cs points at.
    /// </summary>
    [Theory]
    [MemberData(nameof(Contracts))]
    public void NoMemberOfAPacketGoesWithoutATag(Type contract)
    {
        var untagged = SerializableMembers(contract)
            .Where(p => p.GetCustomAttribute<ProtoMemberAttribute>() is null)
            .Select(p => p.Name)
            .ToArray();

        Assert.Empty(untagged);
    }

    [Theory]
    [MemberData(nameof(Contracts))]
    public void NoTwoMembersOfAPacketShareANumber(Type contract)
    {
        var tags = TagsOf(contract);

        Assert.Equal(tags.Count, tags.Values.Distinct().Count());
    }

    /// <summary>
    /// ImplicitFields is what was handing out the numbers, and it does not stop once tags are
    /// written down: with it back in place a new untagged member is given a number of
    /// protobuf-net's choosing, which lands on top of an existing tag and takes the packet down
    /// at serialization time. The contracts have to stay explicit.
    /// </summary>
    [Theory]
    [MemberData(nameof(Contracts))]
    public void NoPacketLetsProtobufChooseNumbersForItself(Type contract)
    {
        var contractAttribute = contract.GetCustomAttribute<ProtoContractAttribute>()!;

        Assert.Equal(ImplicitFields.None, contractAttribute.ImplicitFields);
    }

    /// <summary>Every packet declared in NimbusPackets.cs, found rather than listed, so a type
    /// added later is held to the same rules without anyone remembering to add it.</summary>
    public static TheoryData<Type> Contracts
    {
        get
        {
            var data = new TheoryData<Type>();
            foreach (var t in typeof(NimbusClientHello).Assembly.GetTypes()
                         .Where(t => t.Namespace == "Nimbus.ServerMod")
                         .Where(t => t.GetCustomAttribute<ProtoContractAttribute>() is not null)
                         .OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                data.Add(t);
            }

            Assert.NotEmpty(data);
            return data;
        }
    }

    /// <summary>
    /// Values to push through both declarations. Empty strings and zeros are in here on
    /// purpose: protobuf-net leaves a member at its default out of the payload, so the
    /// all-defaults cases are the ones where a byte comparison has the least to work with and
    /// a numbering mistake could hide.
    /// </summary>
    public static TheoryData<string, Type, Type, IDictionary<string, object?>> Payloads
    {
        get
        {
            var data = new TheoryData<string, Type, Type, IDictionary<string, object?>>();

            void Add(string label, Type legacy, Type pinned, IDictionary<string, object?> values)
                => data.Add(label, legacy, pinned, values);

            Add("hello/defaults", typeof(Legacy.NimbusClientHello), typeof(NimbusClientHello),
                new Dictionary<string, object?>
                {
                    ["ProtocolVersion"] = 0,
                    ["SupportsSeamlessTransfers"] = false,
                });
            Add("hello/current", typeof(Legacy.NimbusClientHello), typeof(NimbusClientHello),
                new Dictionary<string, object?>
                {
                    ["ProtocolVersion"] = 1,
                    ["SupportsSeamlessTransfers"] = true,
                });
            Add("hello/version-only", typeof(Legacy.NimbusClientHello), typeof(NimbusClientHello),
                new Dictionary<string, object?>
                {
                    ["ProtocolVersion"] = 7,
                    ["SupportsSeamlessTransfers"] = false,
                });
            Add("hello/negative-version", typeof(Legacy.NimbusClientHello), typeof(NimbusClientHello),
                new Dictionary<string, object?>
                {
                    ["ProtocolVersion"] = -1,
                    ["SupportsSeamlessTransfers"] = true,
                });
            Add("hello/max-version", typeof(Legacy.NimbusClientHello), typeof(NimbusClientHello),
                new Dictionary<string, object?>
                {
                    ["ProtocolVersion"] = int.MaxValue,
                    ["SupportsSeamlessTransfers"] = true,
                });

            Add("prepare/empty", typeof(Legacy.NimbusSeamlessPrepare), typeof(NimbusSeamlessPrepare),
                new Dictionary<string, object?>
                {
                    ["TransferId"] = "",
                    ["TargetServerId"] = "",
                    ["Reason"] = "",
                });
            Add("prepare/typical", typeof(Legacy.NimbusSeamlessPrepare), typeof(NimbusSeamlessPrepare),
                new Dictionary<string, object?>
                {
                    ["TransferId"] = "b3f1c0d2-7a44-4e9b-9c21-5f0e8a1d6b3c",
                    ["TargetServerId"] = "survival-02",
                    ["Reason"] = "operator transfer",
                });
            // The three members are the ones that would have been renumbered, so a payload with
            // only one of them set is where a shifted tag shows up hardest.
            Add("prepare/transfer-id-only", typeof(Legacy.NimbusSeamlessPrepare), typeof(NimbusSeamlessPrepare),
                new Dictionary<string, object?>
                {
                    ["TransferId"] = "tid",
                    ["TargetServerId"] = "",
                    ["Reason"] = "",
                });
            Add("prepare/target-only", typeof(Legacy.NimbusSeamlessPrepare), typeof(NimbusSeamlessPrepare),
                new Dictionary<string, object?>
                {
                    ["TransferId"] = "",
                    ["TargetServerId"] = "survival-02",
                    ["Reason"] = "",
                });
            Add("prepare/reason-only", typeof(Legacy.NimbusSeamlessPrepare), typeof(NimbusSeamlessPrepare),
                new Dictionary<string, object?>
                {
                    ["TransferId"] = "",
                    ["TargetServerId"] = "",
                    ["Reason"] = "shutting down",
                });
            Add("prepare/non-ascii", typeof(Legacy.NimbusSeamlessPrepare), typeof(NimbusSeamlessPrepare),
                new Dictionary<string, object?>
                {
                    ["TransferId"] = "id-éèê",
                    ["TargetServerId"] = "serveur-créatif",
                    ["Reason"] = "maintenance prévue, redémarrage",
                });
            Add("prepare/long", typeof(Legacy.NimbusSeamlessPrepare), typeof(NimbusSeamlessPrepare),
                new Dictionary<string, object?>
                {
                    ["TransferId"] = new string('t', 300),
                    ["TargetServerId"] = new string('s', 300),
                    ["Reason"] = new string('r', 300),
                });

            Add("commit/empty", typeof(Legacy.NimbusSeamlessCommit), typeof(NimbusSeamlessCommit),
                new Dictionary<string, object?> { ["TransferId"] = "" });
            Add("commit/typical", typeof(Legacy.NimbusSeamlessCommit), typeof(NimbusSeamlessCommit),
                new Dictionary<string, object?> { ["TransferId"] = "b3f1c0d2-7a44-4e9b-9c21-5f0e8a1d6b3c" });

            Add("ready/empty", typeof(Legacy.NimbusSeamlessReady), typeof(NimbusSeamlessReady),
                new Dictionary<string, object?> { ["TransferId"] = "" });
            Add("ready/typical", typeof(Legacy.NimbusSeamlessReady), typeof(NimbusSeamlessReady),
                new Dictionary<string, object?> { ["TransferId"] = "b3f1c0d2-7a44-4e9b-9c21-5f0e8a1d6b3c" });

            Add("abort/empty", typeof(Legacy.NimbusSeamlessAbort), typeof(NimbusSeamlessAbort),
                new Dictionary<string, object?>
                {
                    ["TransferId"] = "",
                    ["Message"] = "",
                });
            Add("abort/typical", typeof(Legacy.NimbusSeamlessAbort), typeof(NimbusSeamlessAbort),
                new Dictionary<string, object?>
                {
                    ["TransferId"] = "b3f1c0d2-7a44-4e9b-9c21-5f0e8a1d6b3c",
                    ["Message"] = "target refused the reservation",
                });
            Add("abort/message-only", typeof(Legacy.NimbusSeamlessAbort), typeof(NimbusSeamlessAbort),
                new Dictionary<string, object?>
                {
                    ["TransferId"] = "",
                    ["Message"] = "target refused the reservation",
                });
            Add("abort/transfer-id-only", typeof(Legacy.NimbusSeamlessAbort), typeof(NimbusSeamlessAbort),
                new Dictionary<string, object?>
                {
                    ["TransferId"] = "tid",
                    ["Message"] = "",
                });

            return data;
        }
    }

    private static SortedDictionary<string, int> TagsOf(Type contract)
    {
        var tags = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var field in RuntimeTypeModel.Default[contract].GetFields())
            tags[field.Name] = field.FieldNumber;

        return tags;
    }

    private static IEnumerable<PropertyInfo> SerializableMembers(Type contract) =>
        contract.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite);

    private static object Populate(Type contract, IDictionary<string, object?> values)
    {
        var instance = Activator.CreateInstance(contract)!;
        foreach (var (member, value) in values)
            contract.GetProperty(member)!.SetValue(instance, value);

        return instance;
    }

    private static byte[] Serialize(object packet)
    {
        using var buffer = new MemoryStream();
        Serializer.NonGeneric.Serialize(buffer, packet);

        return buffer.ToArray();
    }

    private static object Deserialize(Type contract, byte[] payload)
    {
        using var buffer = new MemoryStream(payload);

        return Serializer.NonGeneric.Deserialize(contract, buffer);
    }
}
