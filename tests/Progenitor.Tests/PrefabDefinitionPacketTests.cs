using System;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the cloned-item definition packet: four ints then three 64-byte
/// ASCII fields (id, name, description). Linked to <c>PrefabWire</c>; does not load game types.
/// </summary>
public class PrefabDefinitionPacketTests
{
    [Fact]
    public void PacketSize_is_four_ints_and_three_ascii_slots()
    {
        Assert.Equal(4, PrefabWire.PacketIntCount);
        Assert.Equal(3, PrefabWire.PacketAsciiFieldCount);
        Assert.Equal(16, PrefabWire.PacketAsciiOffset);
        Assert.Equal(16 + (3 * PrefabWire.AsciiCapacity), PrefabWire.PacketSize);
        Assert.Equal(208, PrefabWire.PacketSize);
    }

    [Fact]
    public void DescriptionKey_is_id_dot_desc()
    {
        Assert.Equal("sample.ember.desc", PrefabWire.DescriptionKey("sample.ember"));
        Assert.Equal(".desc", PrefabWire.DescriptionKeySuffix);
    }

    [Fact]
    public void DescriptionKey_rejects_empty()
    {
        Assert.Throws<ArgumentException>(() => PrefabWire.DescriptionKey(""));
        Assert.Throws<ArgumentException>(() => PrefabWire.DescriptionKey(null!));
    }

    [Theory]
    [InlineData(null, "incoming", "incoming")]
    [InlineData("", "incoming", "incoming")]
    [InlineData("kept", "incoming", "kept")]
    [InlineData("kept", null, "kept")]
    [InlineData("kept", "", "kept")]
    [InlineData(null, null, null)]
    [InlineData("", "", "")]
    public void FillEmpty_only_writes_when_existing_is_blank(string? existing, string? incoming, string? expected)
    {
        Assert.Equal(expected, PrefabWire.FillEmpty(existing, incoming));
    }

    [Fact]
    public void RoundTrip_writes_id_name_and_description()
    {
        var dest = new byte[PrefabWire.PacketSize];
        Assert.True(PrefabWire.TryWriteDefinitionPacket(
            dest,
            newGuid: 802060347,
            sourceGuid: -257494203,
            maxAmount: 50,
            id: "sample.ember",
            name: "Ember Shard",
            description: "A shard that still holds a little heat.",
            out var error));
        Assert.Equal("", error);

        Assert.True(PrefabWire.TryReadDefinitionPacket(
            dest,
            out var newGuid,
            out var sourceGuid,
            out var maxAmount,
            out var kind,
            out var id,
            out var name,
            out var description,
            out error));
        Assert.Equal("", error);
        Assert.Equal(802060347, newGuid);
        Assert.Equal(-257494203, sourceGuid);
        Assert.Equal(50, maxAmount);
        Assert.Equal((int)PrefabKind.Item, kind);
        Assert.Equal("sample.ember", id);
        Assert.Equal("Ember Shard", name);
        Assert.Equal("A shard that still holds a little heat.", description);
    }

    [Fact]
    public void RoundTrip_buff_writes_kind_one_and_zero_max_amount()
    {
        var dest = new byte[PrefabWire.PacketSize];
        Assert.True(PrefabWire.TryWriteDefinitionPacket(
            dest,
            newGuid: 111,
            sourceGuid: 222,
            maxAmount: 0,
            kind: (int)PrefabKind.Buff,
            id: "sample.nodrain",
            name: "No Drain",
            description: null,
            out var error));
        Assert.Equal("", error);

        Assert.True(PrefabWire.TryReadDefinitionPacket(
            dest,
            out var newGuid,
            out var sourceGuid,
            out var maxAmount,
            out var kind,
            out var id,
            out var name,
            out var description,
            out error));
        Assert.Equal("", error);
        Assert.Equal(111, newGuid);
        Assert.Equal(222, sourceGuid);
        Assert.Equal(0, maxAmount);
        Assert.Equal((int)PrefabKind.Buff, kind);
        Assert.Equal("sample.nodrain", id);
        Assert.Equal("No Drain", name);
        Assert.Equal("", description);
        Assert.Equal(PrefabKind.Buff, PrefabWire.KindFromWire(kind));
    }

    [Fact]
    public void Write_rejects_unknown_kind()
    {
        var dest = new byte[PrefabWire.PacketSize];
        Assert.False(PrefabWire.TryWriteDefinitionPacket(
            dest, 1, 2, 3, kind: 99, id: "id", name: "n", description: "d", out var error));
        Assert.Contains("PrefabKind", error);
    }

    [Fact]
    public void RoundTrip_empty_name_and_description_stay_empty()
    {
        var dest = new byte[PrefabWire.PacketSize];
        Assert.True(PrefabWire.TryWriteDefinitionPacket(
            dest, 1, 2, 3, "ember", name: null, description: "", out var error));
        Assert.Equal("", error);

        Assert.True(PrefabWire.TryReadDefinitionPacket(
            dest, out _, out _, out _, out var id, out var name, out var description, out error));
        Assert.Equal("ember", id);
        Assert.Equal("", name);
        Assert.Equal("", description);
    }

    [Fact]
    public void RoundTrip_accepts_63_byte_description()
    {
        var text = new string('D', PrefabWire.AsciiMaxChars);
        var dest = new byte[PrefabWire.PacketSize];
        Assert.True(PrefabWire.TryWriteDefinitionPacket(
            dest, 1, 2, 3, "id", name: "n", description: text, out var error));
        Assert.Equal("", error);

        Assert.True(PrefabWire.TryReadDefinitionPacket(
            dest, out _, out _, out _, out _, out _, out var description, out error));
        Assert.Equal(text, description);
    }

    [Fact]
    public void Write_rejects_64_byte_description()
    {
        var dest = new byte[PrefabWire.PacketSize];
        Assert.False(PrefabWire.TryWriteDefinitionPacket(
            dest, 1, 2, 3, "id", name: "n", description: new string('D', PrefabWire.AsciiCapacity), out var error));
        Assert.Contains("1–63", error);
    }

    [Fact]
    public void Write_rejects_non_ascii_description()
    {
        var dest = new byte[PrefabWire.PacketSize];
        Assert.False(PrefabWire.TryWriteDefinitionPacket(
            dest, 1, 2, 3, "id", name: "n", description: "émber heat", out var error));
        Assert.Contains("not ASCII", error);
    }

    [Fact]
    public void Write_rejects_empty_id_even_when_description_is_valid()
    {
        var dest = new byte[PrefabWire.PacketSize];
        Assert.False(PrefabWire.TryWriteDefinitionPacket(
            dest, 1, 2, 3, id: "", name: "n", description: "ok", out var error));
        Assert.Contains("1–63", error);
    }

    [Fact]
    public void Write_rejects_overlong_name_the_same_way_as_description()
    {
        var dest = new byte[PrefabWire.PacketSize];
        Assert.False(PrefabWire.TryWriteDefinitionPacket(
            dest, 1, 2, 3, "id", name: new string('N', 64), description: "ok", out var error));
        Assert.Contains("1–63", error);
    }

    [Fact]
    public void Write_rejects_buffer_shorter_than_packet_size()
    {
        var dest = new byte[PrefabWire.PacketSize - 1];
        Assert.False(PrefabWire.TryWriteDefinitionPacket(
            dest, 1, 2, 3, "id", "n", "d", out var error));
        Assert.Contains("PacketSize", error);
    }

    [Fact]
    public void Read_rejects_buffer_shorter_than_packet_size()
    {
        Assert.False(PrefabWire.TryReadDefinitionPacket(
            new byte[10],
            out _, out _, out _, out _, out _, out _, out var error));
        Assert.Contains("PacketSize", error);
    }

    [Fact]
    public void Ascii_slots_are_nul_padded_so_a_shorter_string_does_not_leak()
    {
        var dest = new byte[PrefabWire.PacketSize];
        Assert.True(PrefabWire.TryWriteDefinitionPacket(
            dest, 1, 2, 3, "id", "long-name-here", "long-description-here", out _));
        Assert.True(PrefabWire.TryWriteDefinitionPacket(
            dest, 1, 2, 3, "id", "n", "d", out _));

        Assert.True(PrefabWire.TryReadDefinitionPacket(
            dest, out _, out _, out _, out _, out var name, out var description, out _));
        Assert.Equal("n", name);
        Assert.Equal("d", description);

        int nameOffset = PrefabWire.PacketAsciiOffset + PrefabWire.AsciiCapacity;
        int descOffset = PrefabWire.PacketAsciiOffset + (2 * PrefabWire.AsciiCapacity);
        for (int i = 1; i < PrefabWire.AsciiCapacity; i++)
        {
            Assert.Equal(0, dest[nameOffset + i]);
            Assert.Equal(0, dest[descOffset + i]);
        }
    }

    [Fact]
    public void TryValidateOptionalAscii_accepts_a_description_and_rejects_overlong()
    {
        Assert.True(PrefabWire.TryValidateOptionalAscii("A shard of heat.", "description", out var error));
        Assert.Equal("", error);
        Assert.False(PrefabWire.TryValidateOptionalAscii(new string('D', 64), "description", out error));
        Assert.Contains("1–63", error);
    }
}
