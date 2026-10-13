using System;
using Progenitor.Bootstrap;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free round-trip and refuse paths for the <c>PrfI</c> key-table order packet.
/// </summary>
public class ItemKeyLedgerPacketTests
{
    private const int Base = 1083;
    private const int Capacity = 2048;
    private const int Ember = 480926629;
    private const int Cinder = -650563057;

    [Fact]
    public void EventId_is_PrfI()
    {
        Assert.Equal(0x50726649, KeyLedgerPacket.EventId);
        Assert.Equal(ReceiveSlotPolicy.PrefabKeyIndex, KeyLedgerPacket.EventId);
        Assert.True(ReceiveSlotPolicy.IsOwnedEventId(KeyLedgerPacket.EventId));
        Assert.NotEqual(0x50726644, KeyLedgerPacket.EventId); // PrfD
        Assert.NotEqual(0x5072664B, KeyLedgerPacket.EventId); // PrfK
        Assert.NotEqual(PrefabEditWire.EventId, KeyLedgerPacket.EventId);
    }

    [Fact]
    public void RoundTrip_preserves_order_ids_and_hash()
    {
        var rows = new[]
        {
            new KeyLedgerRow(Ember, "sample.ember", "mod.a", false),
            new KeyLedgerRow(Cinder, "sample.cinder", "mod.b", false),
        };
        var dest = new byte[KeyLedgerPacket.MaxPacketBytes];
        Assert.True(KeyLedgerPacket.TryWrite(dest, Base, Capacity, rows, out int written, out var error));
        Assert.Equal("", error);
        Assert.True(written > KeyLedgerPacket.HeaderBytes);

        Assert.True(KeyLedgerPacket.TryRead(dest, written, Capacity, out int baseLength, out var read, out error));
        Assert.Equal("", error);
        Assert.Equal(Base, baseLength);
        Assert.Equal(2, read.Length);
        Assert.Equal(Ember, read[0].GuidHash);
        Assert.Equal("sample.ember", read[0].Id);
        Assert.Equal(Cinder, read[1].GuidHash);
        Assert.Equal("sample.cinder", read[1].Id);
        Assert.Equal(
            KeyLedgerPacket.HashOrder(new[] { Ember, Cinder }),
            KeyLedgerPacket.HashOrder(new[] { read[0].GuidHash, read[1].GuidHash }));
    }

    [Fact]
    public void Empty_tail_round_trips()
    {
        var dest = new byte[KeyLedgerPacket.MaxPacketBytes];
        Assert.True(KeyLedgerPacket.TryWrite(
            dest, Base, Capacity, Array.Empty<KeyLedgerRow>(), out int written, out var error));
        Assert.Equal(KeyLedgerPacket.HeaderBytes, written);
        Assert.True(KeyLedgerPacket.TryRead(dest, written, Capacity, out int baseLength, out var rows, out error));
        Assert.Equal(Base, baseLength);
        Assert.Empty(rows);
        Assert.Equal(0, KeyLedgerPacket.HashOrder(Array.Empty<int>()));
    }

    [Fact]
    public void Hash_is_order_sensitive()
    {
        Assert.NotEqual(
            KeyLedgerPacket.HashOrder(new[] { Ember, Cinder }),
            KeyLedgerPacket.HashOrder(new[] { Cinder, Ember }));
    }

    [Fact]
    public void Tampered_hash_refuses_the_whole_packet()
    {
        var rows = new[] { new KeyLedgerRow(Ember, "sample.ember", "mod.a", false) };
        var dest = new byte[KeyLedgerPacket.MaxPacketBytes];
        Assert.True(KeyLedgerPacket.TryWrite(dest, Base, Capacity, rows, out int written, out _));
        dest[8] ^= 0xFF;
        Assert.False(KeyLedgerPacket.TryRead(dest, written, Capacity, out _, out _, out var error));
        Assert.Contains("order hash", error);
    }

    [Fact]
    public void Truncated_payload_is_refused()
    {
        Assert.False(KeyLedgerPacket.TryRead(new byte[4], 4, Capacity, out _, out _, out var error));
        Assert.Contains("header", error);
    }

    [Fact]
    public void Duplicate_guid_is_refused_on_write()
    {
        var rows = new[]
        {
            new KeyLedgerRow(Ember, "sample.ember", "mod.a", false),
            new KeyLedgerRow(Ember, "sample.ember.dup", "mod.b", false),
        };
        var dest = new byte[KeyLedgerPacket.MaxPacketBytes];
        Assert.False(KeyLedgerPacket.TryWrite(dest, Base, Capacity, rows, out _, out var error));
        Assert.Contains("already held", error);
    }

    [Fact]
    public void Headroom_overflow_is_refused_on_write()
    {
        var rows = new[]
        {
            new KeyLedgerRow(Ember, "sample.ember", "mod.a", false),
            new KeyLedgerRow(Cinder, "sample.cinder", "mod.b", false),
        };
        var dest = new byte[KeyLedgerPacket.MaxPacketBytes];
        Assert.False(KeyLedgerPacket.TryWrite(dest, 2047, Capacity, rows, out _, out var error));
        Assert.Contains("would exceed capacity", error);
    }

    [Fact]
    public void Truncated_row_is_refused()
    {
        var rows = new[] { new KeyLedgerRow(Ember, "sample.ember", "mod.a", false) };
        var dest = new byte[KeyLedgerPacket.MaxPacketBytes];
        Assert.True(KeyLedgerPacket.TryWrite(dest, Base, Capacity, rows, out int written, out _));
        Assert.False(KeyLedgerPacket.TryRead(dest, written - 1, Capacity, out _, out _, out var error));
        Assert.Contains("truncated", error);
    }

    [Fact]
    public void Trailing_bytes_are_refused()
    {
        var rows = new[] { new KeyLedgerRow(Ember, "sample.ember", "mod.a", false) };
        var dest = new byte[KeyLedgerPacket.MaxPacketBytes];
        Assert.True(KeyLedgerPacket.TryWrite(dest, Base, Capacity, rows, out int written, out _));
        dest[written] = 0xFF;
        Assert.False(KeyLedgerPacket.TryRead(dest, written + 1, Capacity, out _, out _, out var error));
        Assert.Contains("trailing", error);
    }

    [Fact]
    public void Count_above_MaxRows_is_refused()
    {
        var dest = new byte[KeyLedgerPacket.HeaderBytes];
        dest[0] = unchecked((byte)Base);
        dest[1] = (byte)(Base >> 8);
        dest[2] = (byte)(Base >> 16);
        dest[3] = (byte)(Base >> 24);
        int count = KeyLedgerPacket.MaxRows + 1;
        dest[4] = (byte)count;
        dest[5] = (byte)(count >> 8);
        dest[6] = (byte)(count >> 16);
        dest[7] = (byte)(count >> 24);
        Assert.False(KeyLedgerPacket.TryRead(dest, dest.Length, Capacity, out _, out _, out var error));
        Assert.Contains("MaxRows", error);
    }

    [Fact]
    public void Encoded_ledger_larger_than_transport_packet_requires_larger_destination()
    {
        var rows = new KeyLedgerRow[300];
        var id = new string('a', KeyLedgerPacket.MaxIdBytes);
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new KeyLedgerRow(Ember + i, id, "mod.a", false);
        Assert.True(KeyLedgerPacket.EncodedSize(rows) > KeyLedgerPacket.MaxPacketBytes);
        var dest = new byte[KeyLedgerPacket.MaxPacketBytes];
        Assert.False(KeyLedgerPacket.TryWrite(dest, Base, Capacity, rows, out _, out var error));
        Assert.Contains("destination", error);
    }

    [Fact]
    public void Oversized_id_length_on_read_is_refused()
    {
        var dest = new byte[KeyLedgerPacket.HeaderBytes + sizeof(int) + 1];
        dest[0] = unchecked((byte)Base);
        dest[1] = (byte)(Base >> 8);
        dest[2] = (byte)(Base >> 16);
        dest[3] = (byte)(Base >> 24);
        dest[4] = 1;
        dest[KeyLedgerPacket.HeaderBytes + sizeof(int)] = unchecked((byte)(KeyLedgerPacket.MaxIdBytes + 1));
        Assert.False(KeyLedgerPacket.TryRead(dest, dest.Length, Capacity, out _, out _, out var error));
        Assert.Contains("id length", error);
    }

    [Fact]
    public void Non_ascii_id_is_refused()
    {
        var rows = new[] { new KeyLedgerRow(Ember, "émber", "mod.a", false) };
        var dest = new byte[KeyLedgerPacket.MaxPacketBytes];
        Assert.False(KeyLedgerPacket.TryWrite(dest, Base, Capacity, rows, out _, out var error));
        Assert.Contains("ASCII", error);
    }
}
