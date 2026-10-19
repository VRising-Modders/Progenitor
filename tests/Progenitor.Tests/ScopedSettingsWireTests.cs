using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

public sealed class ScopedSettingsWireTests
{
    private static ScopedSettingsPacket Packet(byte kind) => new()
    {
        Kind = kind,
        Generation = Guid.Parse("b24a9251-7a14-46cc-a233-a551e891d432"),
        RequestId = kind >= 5 ? Guid.Parse("c325181f-1a49-4d37-a76a-812e1907a905") : Guid.Empty,
        Count = kind == 1 || kind == 3 ? 512 : 0,
        CanEdit = kind == 1,
        ServerName = kind == 1 ? "Server 雪" : "",
        Row = kind == 2 ? Row() : null,
        Id = kind >= 4 ? "mod/General/Size" : "",
        Type = kind == 5 ? "System.Int32" : "",
        Expected = kind == 5 ? "1" : "",
        Value = kind >= 4 ? "2" : "",
        Personal = kind >= 4,
        Success = kind == 6,
    };

    private static ScopedSettingRecord Row() => new()
    {
        Id = "mod/General/Size", PluginId = "mod", ModName = "A mod 雪", Version = "1.0",
        Section = "General", Key = "Size", Help = "Help\nSecond line", Type = "System.Int32",
        Default = "1", Value = "2", Personal = true, Editable = true,
        Choices = new[] { "1", "2", "雪" }, Min = "0", Max = "10", MaxLength = 1024,
    };

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void Every_kind_roundtrips_and_rejects_wrong_direction_truncation_and_suffix(int kind)
    {
        var original = Packet((byte)kind);
        var bytes = ScopedSettingsWire.Encode(original);
        Assert.True(ScopedSettingsWire.TryDecode(bytes, kind != 5, out var decoded));
        Assert.Equal(original.Kind, decoded.Kind);
        Assert.Equal(original.Generation, decoded.Generation);
        Assert.Equal(original.RequestId, decoded.RequestId);
        Assert.Equal(original.Count, decoded.Count);
        Assert.Equal(original.CanEdit, decoded.CanEdit);
        Assert.Equal(original.ServerName, decoded.ServerName);
        Assert.Equal(original.Id, decoded.Id);
        Assert.Equal(original.Type, decoded.Type);
        Assert.Equal(original.Expected, decoded.Expected);
        Assert.Equal(original.Value, decoded.Value);
        Assert.Equal(original.Error, decoded.Error);
        Assert.Equal(original.Personal, decoded.Personal);
        Assert.Equal(original.Success, decoded.Success);
        if (original.Row != null)
        {
            var row = Assert.IsType<ScopedSettingRecord>(decoded.Row);
            Assert.Equal(original.Row.Id, row.Id);
            Assert.Equal(original.Row.PluginId, row.PluginId);
            Assert.Equal(original.Row.ModName, row.ModName);
            Assert.Equal(original.Row.Version, row.Version);
            Assert.Equal(original.Row.Section, row.Section);
            Assert.Equal(original.Row.Key, row.Key);
            Assert.Equal(original.Row.Help, row.Help);
            Assert.Equal(original.Row.Type, row.Type);
            Assert.Equal(original.Row.Default, row.Default);
            Assert.Equal(original.Row.Value, row.Value);
            Assert.Equal(original.Row.Personal, row.Personal);
            Assert.Equal(original.Row.Editable, row.Editable);
            Assert.Equal(original.Row.Choices, row.Choices);
            Assert.Equal(original.Row.Min, row.Min);
            Assert.Equal(original.Row.Max, row.Max);
            Assert.Equal(original.Row.MaxLength, row.MaxLength);
        }
        Assert.Equal(bytes, ScopedSettingsWire.Encode(decoded));
        Assert.False(ScopedSettingsWire.TryDecode(bytes, kind == 5, out var wrongDirection));
        Assert.Null(wrongDirection);
        for (int i = 0; i < bytes.Length; i++)
        {
            Assert.False(ScopedSettingsWire.TryDecode(bytes.AsSpan(0, i), kind != 5, out var partial));
            Assert.Null(partial);
        }
        Assert.False(ScopedSettingsWire.TryDecode(bytes.Concat(new byte[1]).ToArray(), kind != 5, out var trailing));
        Assert.Null(trailing);
    }

    [Fact]
    public void Rejects_unknown_version_kind_empty_guids_and_noncanonical_booleans()
    {
        var bytes = ScopedSettingsWire.Encode(Packet(1));
        bytes[0] = 3; // versions 1 and 2 decode; anything else is refused
        Assert.False(ScopedSettingsWire.TryDecode(bytes, true, out _));
        bytes[0] = 1; bytes[1] = 7;
        Assert.False(ScopedSettingsWire.TryDecode(bytes, true, out _));
        bytes[1] = 0;
        Assert.False(ScopedSettingsWire.TryDecode(bytes, true, out _));
        bytes[1] = 1; bytes[22] = 2;
        Assert.False(ScopedSettingsWire.TryDecode(bytes, true, out _));
        bytes[22] = 1; Array.Clear(bytes, 2, 16);
        Assert.False(ScopedSettingsWire.TryDecode(bytes, true, out _));
        bytes = ScopedSettingsWire.Encode(Packet(5));
        Array.Clear(bytes, 18, 16);
        Assert.False(ScopedSettingsWire.TryDecode(bytes, false, out _));
    }

    [Fact]
    public void Rejects_bad_utf8_and_declared_lengths_before_allocating_text()
    {
        var bytes = ScopedSettingsWire.Encode(Packet(1));
        bytes[25] = 0xff;
        Assert.False(ScopedSettingsWire.TryDecode(bytes, true, out _));
        bytes = ScopedSettingsWire.Encode(Packet(1));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(23), 4097);
        Assert.False(ScopedSettingsWire.TryDecode(bytes, true, out _));
        var packet = Packet(1);
        packet.ServerName = "\ud800";
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(packet));
        packet.ServerName = new string('雪', 1366);
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(packet));
    }

    [Theory]
    [InlineData(-1)] [InlineData(513)] [InlineData(int.MaxValue)]
    public void Rejects_invalid_counts_on_both_paths(int count)
    {
        foreach (byte kind in new byte[] { 1, 3 })
        {
            var p = Packet(kind);
            var bytes = ScopedSettingsWire.Encode(p);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), count);
            Assert.False(ScopedSettingsWire.TryDecode(bytes, true, out _));
            p.Count = count;
            Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(p));
        }
    }

    [Fact]
    public void Rejects_oversized_total_even_when_individual_fields_fit()
    {
        Assert.False(ScopedSettingsWire.TryDecode(new byte[15361], true, out _));
        var p = Packet(2);
        p.Row!.Choices = Enumerable.Repeat(new string('a', 4096), 4).ToArray();
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(p));
    }

    [Fact]
    public void Exact_packet_and_field_limits_are_inclusive()
    {
        var p = Packet(2);
        p.Row!.Choices = new[] { new string('a', 4096), new string('b', 4096), new string('c', 4096), "" };
        int available = ScopedSettingsWire.MaxPacketBytes - ScopedSettingsWire.Encode(p).Length;
        p.Row.Choices[3] = new string('d', available);
        var bytes = ScopedSettingsWire.Encode(p);
        Assert.Equal(ScopedSettingsWire.MaxPacketBytes, bytes.Length);
        Assert.True(ScopedSettingsWire.TryDecode(bytes, true, out _));
        p.Row.Choices[3] += "d";
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(p));
    }

    [Fact]
    public void Choices_bounds_nullable_bounds_and_opaque_types()
    {
        var p = Packet(2);
        p.Row!.Min = null; p.Row.Max = "";
        p.Row.Type = "Unknown.Type, Never.Load.This.Assembly";
        p.Row.Choices = Enumerable.Repeat("", 128).ToArray();
        var bytes = ScopedSettingsWire.Encode(p);
        Assert.True(ScopedSettingsWire.TryDecode(bytes, true, out var decoded));
        Assert.Null(decoded.Row!.Min);
        Assert.Equal("", decoded.Row.Max);
        Assert.Equal(p.Row.Type, decoded.Row.Type);
        Assert.Equal(128, decoded.Row.Choices.Length);
        int choicesOffset = 18 + new[] { p.Row.Id, p.Row.PluginId, p.Row.ModName, p.Row.Version,
            p.Row.Section, p.Row.Key, p.Row.Help, p.Row.Type, p.Row.Default, p.Row.Value }
            .Sum(s => 2 + Encoding.UTF8.GetByteCount(s)) + 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(choicesOffset), 129);
        Assert.False(ScopedSettingsWire.TryDecode(bytes, true, out _));
        p.Row.Choices = Enumerable.Repeat("", 129).ToArray();
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(p));
    }

    [Fact]
    public void Rejects_mismatched_or_ambiguous_identifiers_on_both_paths()
    {
        var p = Packet(2);
        var bytes = ScopedSettingsWire.Encode(p);
        bytes[20] = (byte)'x'; // Change only the Id, preserving its otherwise valid structure.
        Assert.False(ScopedSettingsWire.TryDecode(bytes, true, out _));
        p.Row!.Id = "other/General/Size";
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(p));
        p.Row.Id = "mod/General/sub/Size"; p.Row.Section = "General/sub";
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(p));
        p = Packet(5); p.Id = "mod//Size";
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(p));
    }

    [Fact]
    public void Rejects_missing_required_and_unexpected_fields()
    {
        var p = Packet(1); p.Row = Row();
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(p));
        p = Packet(2); p.Row = null;
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(p));
        p = Packet(3); p.RequestId = Guid.NewGuid();
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(p));
        p = Packet(4); p.Expected = "not allowed";
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(p));
        p = Packet(5); p.Type = "";
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(p));
        p = Packet(6); p.Error = "failure";
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(p));
        p.Success = false;
        Assert.True(ScopedSettingsWire.TryDecode(ScopedSettingsWire.Encode(p), true, out var failure));
        Assert.Equal("failure", failure.Error);
        Assert.False(failure.Success);
        p = Packet(2); p.Row!.Choices = null!;
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(p));
        p = Packet(1); p.ServerName = null!;
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(p));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(4097)]
    public void Rejects_invalid_max_length(int value)
    {
        var p = Packet(2);
        var bytes = ScopedSettingsWire.Encode(p);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), value);
        Assert.False(ScopedSettingsWire.TryDecode(bytes, true, out _));
        p.Row!.MaxLength = value;
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(p));
    }

    [Fact]
    public void Arbitrary_bounded_input_does_not_throw_or_publish_partial_packets()
    {
        var random = new Random(1639);
        for (int i = 0; i < 2000; i++)
        {
            var bytes = new byte[random.Next(18, 15362)];
            random.NextBytes(bytes);
            bytes[0] = 1; bytes[1] = (byte)random.Next(1, 7);
            if (!ScopedSettingsWire.TryDecode(bytes, bytes[1] != 5, out var packet)) Assert.Null(packet);
        }
    }
}
