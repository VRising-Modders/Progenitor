using System;
using System.Runtime.InteropServices;
using Progenitor.Content;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

public sealed class ItemStatPacketTests
{
    [Fact]
    public void PacketBudgetStopsFloodAndRecoversInNextMonotonicWindow()
    {
        long now = 0;
        var budget = new ItemStatSyncBudget(() => now);
        for (int i = 0; i < ItemStatSyncBudget.DefaultMaxPacketsPerWindow; i++)
        {
            Assert.True(budget.TryAdmit(out var report));
            Assert.False(report);
        }
        Assert.False(budget.TryAdmit(out var firstReport));
        Assert.True(firstReport);
        Assert.False(budget.TryAdmit(out var repeatedReport));
        Assert.False(repeatedReport);
        now = ItemStatSyncBudget.WindowMilliseconds - 1;
        Assert.False(budget.TryAdmit(out _));
        now++;
        Assert.True(budget.TryAdmit(out var nextReport));
        budget.Reset();
        Assert.True(budget.TryAdmit(out _));
        Assert.False(nextReport);
    }

    [Fact]
    public void UpdatedCoreAdvertisesDistinctStatCapability()
    {
        Assert.Equal(131072u, (uint)Handshake.Capability.ItemStatSync);
        Assert.True((Handshake.SelfCapabilities & Handshake.Capability.ItemStatSync) != 0);
        var row = Handshake.Compare.CreateCapabilityRecord(Handshake.Capability.ItemStatSync);
        Assert.Equal(Handshake.Capability.ItemStatSync, Handshake.Compare.ReadCapabilityBlob(row.Version));
    }

    private static ItemStatPacket ValidPacket() => new()
    {
        Version = ItemStatPacket.CurrentVersion,
        NewGuid = 1001,
        SourceGuid = -2002,
        MaxHealth = 100,
        PhysicalPower = 100,
        SpellPower = 100,
    };

    [Fact]
    public void WireLayoutIs24BytesAndRoundTrips()
    {
        Assert.Equal(24, Marshal.SizeOf<ItemStatPacket>());
        Assert.Equal(12, Marshal.OffsetOf<ItemStatPacket>(nameof(ItemStatPacket.MaxHealth)).ToInt32());
        var storage = Marshal.AllocHGlobal(24);
        try
        {
            var packet = ValidPacket();
            Marshal.StructureToPtr(packet, storage, false);
            var decoded = Marshal.PtrToStructure<ItemStatPacket>(storage);
            Assert.Equal(packet.NewGuid, decoded.NewGuid);
            Assert.Equal(packet.SourceGuid, decoded.SourceGuid);
            Assert.True(decoded.TryBonuses(out var bonuses));
            Assert.Equal(100, bonuses.MaxHealth);
            Assert.Equal(100, bonuses.PhysicalPower);
            Assert.Equal(100, bonuses.SpellPower);
        }
        finally { Marshal.FreeHGlobal(storage); }
    }

    [Fact]
    public void ZeroPacketClearsAllBonuses()
    {
        var packet = ValidPacket();
        packet.MaxHealth = packet.PhysicalPower = packet.SpellPower = 0;
        Assert.True(packet.TryBonuses(out var bonuses));
        Assert.Equal(0, bonuses.MaxHealth);
        Assert.Equal(0, bonuses.PhysicalPower);
        Assert.Equal(0, bonuses.SpellPower);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(10001f)]
    [InlineData(-10001f)]
    public void InvalidStatInAnySlotIsRejected(float invalid)
    {
        var packet = ValidPacket();
        packet.MaxHealth = invalid;
        Assert.False(packet.TryBonuses(out _));
        packet = ValidPacket();
        packet.PhysicalPower = invalid;
        Assert.False(packet.TryBonuses(out _));
        packet = ValidPacket();
        packet.SpellPower = invalid;
        Assert.False(packet.TryBonuses(out _));
    }

    [Theory]
    [InlineData(0, -2002)]
    [InlineData(1001, 0)]
    [InlineData(1001, 1001)]
    public void InvalidIdentityIsRejected(int target, int source)
    {
        var packet = ValidPacket();
        packet.NewGuid = target;
        packet.SourceGuid = source;
        Assert.False(packet.TryBonuses(out _));
    }

    [Fact]
    public void UnknownVersionIsRejectedAndBoundaryValuesAccepted()
    {
        var packet = ValidPacket();
        packet.Version++;
        Assert.False(packet.TryBonuses(out _));
        packet.Version = ItemStatPacket.CurrentVersion;
        packet.MaxHealth = -10000;
        packet.PhysicalPower = packet.SpellPower = 10000;
        Assert.True(packet.TryBonuses(out _));
    }
}
