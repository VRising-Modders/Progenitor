using System;
using System.Linq;
using Progenitor.Content;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

public sealed class ItemScaleProfileTests
{
    [Theory]
    [InlineData(256, false)]
    [InlineData(1024, false)]
    [InlineData(10000, false)]
    [InlineData(16383, false)]
    [InlineData(16384, true)]
    public void Experimental_capability_requires_full_configured_profile(int limit, bool advertised)
    {
        var caps = Handshake.CapabilitiesForPrefabLimit(limit);
        Assert.Equal(advertised, (caps & Handshake.Capability.ItemCapacity16384) != 0);
    }

    [Theory]
    [InlineData(256, 256, true)]
    [InlineData(1024, 1024, true)]
    [InlineData(1024, 1025, false)]
    [InlineData(1024, 10000, false)]
    [InlineData(16383, 10000, false)]
    [InlineData(16384, 10000, true)]
    [InlineData(16384, 16384, true)]
    public void Admission_requires_the_actual_profile_capability(int clientLimit, int serverLimit, bool accepted)
    {
        Assert.Equal(accepted, Handshake.SupportsPrefabLimit(Handshake.CapabilitiesForPrefabLimit(clientLimit), serverLimit));
    }

    [Fact]
    public void Configured_cap_clamps_to_16384_and_preserves_default_256()
    {
        int previous = PrefabSyncBudget.Cap;
        try
        {
            Assert.Equal(256, PrefabSyncBudget.DefaultMaxPerJoin);
            PrefabSyncBudget.Cap = 10000;
            Assert.Equal(10000, PrefabSyncBudget.Cap);
            PrefabSyncBudget.Cap = int.MaxValue;
            Assert.Equal(16384, PrefabSyncBudget.Cap);
            PrefabSyncBudget.Cap = 0;
            Assert.Equal(1, PrefabSyncBudget.Cap);
        }
        finally { PrefabSyncBudget.Cap = previous; }
    }

    [Theory]
    [InlineData(10000)]
    [InlineData(16384)]
    public void Maximum_length_ids_round_trip_through_bounded_chunk_transport(int count)
    {
        var rows = Enumerable.Range(1, count).Select(i => new KeyLedgerRow(i,
            new string('a', 57) + i.ToString("D6"), "", false)).ToArray();
        var payload = new byte[KeyLedgerPacket.EncodedSize(rows)];
        Assert.True(KeyLedgerPacket.TryWrite(payload, 100, 32768, rows, out int written, out var error), error);
        Assert.Equal(12 + count * 68, written);
        Assert.True(KeyLedgerChunks.TrySplit(payload, 1, out var chunks, out error), error);
        var assembler = new KeyLedgerChunkAssembler();
        byte[]? completed = null;
        foreach (var chunk in chunks)
        {
            Assert.InRange(chunk.Length, 13, 15 * 1024);
            Assert.True(assembler.TryAccept(chunk, chunk.Length, out completed, out error), error);
        }
        Assert.NotNull(completed);
        Assert.Equal(payload, completed);
        Assert.True(KeyLedgerPacket.TryRead(completed!, completed!.Length, 32768, out int baseLength, out var decoded, out error), error);
        Assert.Equal(100, baseLength);
        Assert.Equal(count, decoded.Length);
        Assert.Equal(rows[count - 1].Id, decoded[count - 1].Id);
    }

    [Fact]
    public void Experimental_limit_does_not_bypass_native_capacity_or_identity_budget()
    {
        var proposed = Enumerable.Range(1, 10000).Select(i => new DataPackItemIdentity("item" + i, i)).ToArray();
        var none = Array.Empty<DataPackItemIdentity>();
        Assert.True(DataPackAdmissionRules.TryValidate(proposed, none, none, Array.Empty<KeyLedgerRow>(),
            100, 16384, out var error, maxIdentities: 16384), error);
        Assert.False(DataPackAdmissionRules.TryValidate(proposed, none, none, Array.Empty<KeyLedgerRow>(),
            100, 10000, out error, maxIdentities: 16384));
        Assert.Contains("capacity", error);
        Assert.False(DataPackAdmissionRules.TryValidate(proposed, none, none, Array.Empty<KeyLedgerRow>(),
            100, 16384, out error, maxIdentities: 1024));
        Assert.Contains("distinct client prefab identities", error);
    }

    [Fact]
    public void Ledger_bounds_check_headroom_without_integer_overflow()
    {
        var rows = new[] { new KeyLedgerRow(1, "a", "", false), new KeyLedgerRow(2, "b", "", false) };
        Assert.False(KeyLedgerPacket.TryValidateBounds(int.MaxValue - 1, int.MaxValue, rows, out _));
        var bytes = new byte[KeyLedgerPacket.EncodedSize(rows)];
        Assert.True(KeyLedgerPacket.TryWrite(bytes, 1, 100, rows, out int length, out _));
        BitConverter.GetBytes(int.MaxValue - 1).CopyTo(bytes, 0);
        Assert.False(KeyLedgerPacket.TryRead(bytes, length, int.MaxValue, out _, out _, out _));
    }
}
