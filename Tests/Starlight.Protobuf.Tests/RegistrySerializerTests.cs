using Google.Protobuf;
using Starlight.Game.Protocol;
using Starlight.Game.Protocol.V66;
using Starlight.Protobuf.Inspection;
using Xunit;

namespace Starlight.Protobuf.Tests;

/// <summary>
/// Validates the generated fast path: per-version serializers and the
/// <see cref="V66ProtocolRegistry"/> dispatcher. Confirms emitted bytes use the
/// obfuscated version field numbers (not the canonical base structural ones),
/// round-trip cleanly, and that the registry metadata is correct.
/// </summary>
public sealed class RegistrySerializerTests
{
    private static readonly V66ProtocolRegistry Registry = new();

    [Fact]
    public void Serialize_UsesVersionFieldNumbers_NotBaseStructuralOnes()
    {
        // In V66, NCNPDMHMHGA.uid is wire field 10 -> tag (10<<3)|0 = 0x50.
        var message = new NCNPDMHMHGA { Uid = 150 };

        var bytes = Registry.Serialize(message);

        byte[] expected = [0x50, 0x96, 0x01]; // tag 0x50, value 150 (varint 0x96 0x01)
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void Serialize_OmitsProto3DefaultValues()
    {
        var bytes = Registry.Serialize(new NCNPDMHMHGA());
        Assert.Empty(bytes);
    }

    [Fact]
    public void RoundTrip_Map_PreservesEntries()
    {
        var original = new NCNPDMHMHGA
        {
            Uid = 7,
            OpenStateMap = { [1] = 2, [3] = 4 },
        };

        var bytes = Registry.Serialize(original);
        var restored = (NCNPDMHMHGA) Registry.Deserialize(NCNPDMHMHGA.CmdId, new CodedInputStream(bytes));

        Assert.Equal(7u, restored.Uid);
        Assert.Equal(2u, restored.OpenStateMap[1]);
        Assert.Equal(4u, restored.OpenStateMap[3]);
        Assert.Equal(2, restored.OpenStateMap.Count);
    }

    [Fact]
    public void RoundTrip_PackedRepeatedInt32()
    {
        // option_list is wire field 5 -> length-delimited tag (5<<3)|2 = 0x2A.
        var original = new IEMHBJDLKHL { OptionList = { 1, 2, 3 } };

        var bytes = Registry.Serialize(original);

        byte[] expected = [0x2A, 0x03, 0x01, 0x02, 0x03]; // tag, length 3, three 1-byte varints
        Assert.Equal(expected, bytes);

        var restored = (IEMHBJDLKHL) Registry.Deserialize(IEMHBJDLKHL.CmdId, new CodedInputStream(bytes));
        Assert.Equal(new[] { 1, 2, 3 }, restored.OptionList);
    }

    [Fact]
    public void RoundTrip_GetPlayerTokenReq_PreservesCanonicalFields()
    {
        var original = new GetPlayerTokenReq
        {
            PsnId = "psn",
            Ticket = "tkt",
            OnlineId = "online",
            AccountUid = "acct",
            AccountToken = "token",
            AuthkeyVer = 3,
            PlatformType = 2,
            SignType = 1,
            IsGuest = true,
            KeyId = 99,
            Uid = 12345,
        };

        var bytes = Registry.Serialize(original);
        var restored = (GetPlayerTokenReq) Registry.Deserialize(GetPlayerTokenReq.CmdId, new CodedInputStream(bytes));

        Assert.Equal(original.PsnId, restored.PsnId);
        Assert.Equal(original.Ticket, restored.Ticket);
        Assert.Equal(original.OnlineId, restored.OnlineId);
        Assert.Equal(original.AccountUid, restored.AccountUid);
        Assert.Equal(original.AccountToken, restored.AccountToken);
        Assert.Equal(original.AuthkeyVer, restored.AuthkeyVer);
        Assert.Equal(original.PlatformType, restored.PlatformType);
        Assert.Equal(original.SignType, restored.SignType);
        Assert.Equal(original.IsGuest, restored.IsGuest);
        Assert.Equal(original.KeyId, restored.KeyId);
        Assert.Equal(original.Uid, restored.Uid);
    }

    [Fact]
    public void Deserialize_CapturesUnknownVersionFields()
    {
        // Field 1824 (HADOFGGLMDB) exists in the V66 dump but has no canonical
        // counterpart, so on read it must be captured (never discarded) while the
        // known field still deserializes.
        var output = new MemoryStream();
        var cos = new CodedOutputStream(output);
        cos.WriteTag(1824, WireFormat.WireType.LengthDelimited);
        cos.WriteString("obfuscated");
        cos.WriteTag(9, WireFormat.WireType.LengthDelimited); // psn_id
        cos.WriteString("psn");
        cos.Flush();

        var restored = (GetPlayerTokenReq) Registry.Deserialize(
            GetPlayerTokenReq.CmdId, new CodedInputStream(output.ToArray()));

        Assert.Equal("psn", restored.PsnId);

        Assert.NotNull(restored.UnknownFields);
        var unknown = Assert.Single(restored.UnknownFields!.Fields);
        Assert.Equal(1824, unknown.FieldNumber);
        Assert.Equal(WireFormat.WireType.LengthDelimited, unknown.WireType);
        Assert.Equal("obfuscated", System.Text.Encoding.UTF8.GetString(unknown.Data));
    }

    [Fact]
    public void Inspect_RendersKnownAndUnknownFields()
    {
        var output = new MemoryStream();
        var cos = new CodedOutputStream(output);
        cos.WriteTag(1824, WireFormat.WireType.LengthDelimited);
        cos.WriteString("obfuscated");
        cos.WriteTag(9, WireFormat.WireType.LengthDelimited); // psn_id
        cos.WriteString("psn");
        cos.Flush();

        var restored = (GetPlayerTokenReq) Registry.Deserialize(
            GetPlayerTokenReq.CmdId, new CodedInputStream(output.ToArray()));

        var json = ProtocolInspector.ToJson(restored);

        Assert.Contains("\"psnId\":\"psn\"", json);
        Assert.Contains("\"_unknown\":[", json);
        Assert.Contains("\"field\":1824", json);
        Assert.Contains($"\"data\":\"{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("obfuscated"))}\"", json);
    }

    [Fact]
    public void GetCmdId_ResolvesByMessageType()
    {
        Assert.Equal(6301, Registry.GetCmdId(new NCNPDMHMHGA()));
        Assert.Equal(28757, Registry.GetCmdId(new GetPlayerTokenReq()));
        Assert.Equal(29783, Registry.GetCmdId(new IEMHBJDLKHL()));
    }

    [Fact]
    public void Create_ConstructsCorrectPocoType()
    {
        Assert.IsType<NCNPDMHMHGA>(Registry.Create(6301));
        Assert.IsType<GetPlayerTokenReq>(Registry.Create(28757));
        Assert.IsType<IEMHBJDLKHL>(Registry.Create(29783));
    }

    [Fact]
    public void Registry_ExposesVersionAndKnownFirst()
    {
        Assert.Equal("V66", Registry.Version);
        Assert.Contains(28757, Registry.KnownFirst);
    }
}
