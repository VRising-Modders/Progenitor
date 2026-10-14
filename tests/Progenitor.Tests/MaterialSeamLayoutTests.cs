using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Progenitor.Bootstrap;
using Progenitor.Materials;
using Xunit;

namespace Progenitor.Tests;

public class MaterialSeamLayoutTests
{
    [Fact]
    public void Bootstrap_and_Core_agree_on_the_row_stride()
    {
        Assert.Equal(Layout.InventoryBufferStride, MaterialSeamPolicy.InventoryBufferStride);
        Assert.Equal(24, Layout.InventoryBufferStride);
        Assert.Equal(0xC, Layout.InventoryBufferItemTypeOffset);
        Assert.Equal(0x10, Layout.InventoryBufferAmountOffset);
    }

    private static byte[] Rows(params (int item, int amount, int entityIndex)[] rows)
    {
        var buf = new byte[rows.Length * MaterialRows.Stride];
        for (int i = 0; i < rows.Length; i++)
        {
            var r = buf.AsSpan(i * MaterialRows.Stride, MaterialRows.Stride);
            BinaryPrimitives.WriteInt32LittleEndian(r.Slice(0), rows[i].entityIndex);
            BinaryPrimitives.WriteInt32LittleEndian(r.Slice(4), rows[i].entityIndex == 0 ? 0 : 1);
            BinaryPrimitives.WriteInt32LittleEndian(r.Slice(Layout.InventoryBufferItemTypeOffset), rows[i].item);
            BinaryPrimitives.WriteInt32LittleEndian(r.Slice(Layout.InventoryBufferAmountOffset), rows[i].amount);
            BinaryPrimitives.WriteInt32LittleEndian(r.Slice(0x14), 7); // MaxAmountOverride
        }
        return buf;
    }

    [Fact]
    public void Sum_adds_rows_per_item_and_skips_empty_slots()
    {
        var rows = Rows((100, 5, 0), (0, 0, 0), (100, 7, 0), (200, 1, 42));
        var sums = new Dictionary<int, long>();
        MaterialRows.Sum(rows, 4, sums);
        Assert.Equal(12, sums[100]);
        Assert.Equal(1, sums[200]);
        Assert.Equal(2, sums.Count);
    }

    [Fact]
    public void Compose_copies_bag_rows_verbatim_and_appends_plain_pool_rows()
    {
        var bag = Rows((100, 5, 0), (200, 1, 42));
        var output = new byte[MaterialRows.Bytes(5)];
        output.AsSpan().Fill(0xEE);
        int n = MaterialRows.Compose(output, bag, 2, new[] { new MaterialLine(100, 30), new MaterialLine(300, 4) });
        Assert.Equal(4, n);
        Assert.True(output.AsSpan(0, bag.Length).SequenceEqual(bag)); // item entity 42 kept
        var pool = output.AsSpan(2 * MaterialRows.Stride, MaterialRows.Stride);
        Assert.Equal(0, BinaryPrimitives.ReadInt64LittleEndian(pool)); // no item entity
        Assert.Equal(100, BinaryPrimitives.ReadInt32LittleEndian(pool.Slice(Layout.InventoryBufferItemTypeOffset)));
        Assert.Equal(30, BinaryPrimitives.ReadInt32LittleEndian(pool.Slice(Layout.InventoryBufferAmountOffset)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(pool.Slice(0x14)));
        var sums = new Dictionary<int, long>();
        MaterialRows.Sum(output, n, sums);
        Assert.Equal(35, sums[100]);
        Assert.Equal(4, sums[300]);
    }

    [Fact]
    public void Compose_refuses_a_buffer_that_is_too_small()
    {
        var bag = Rows((100, 5, 0));
        Assert.Throws<ArgumentException>(() =>
            MaterialRows.Compose(new byte[MaterialRows.Bytes(1)], bag, 1, new[] { new MaterialLine(1, 1) }));
    }

    [Theory]
    [InlineData(-1, 10, false, MaterialSeamPolicy.CountDecision.KeepVanilla)]
    [InlineData(4, 10, false, MaterialSeamPolicy.CountDecision.UseComposed)]
    [InlineData(10, 10, false, MaterialSeamPolicy.CountDecision.UseComposed)]
    [InlineData(11, 10, false, MaterialSeamPolicy.CountDecision.GrowAndRetry)]
    [InlineData(11, 10, true, MaterialSeamPolicy.CountDecision.KeepVanilla)]
    public void Count_seam_decisions(int composed, int capacity, bool retried, MaterialSeamPolicy.CountDecision expected) =>
        Assert.Equal(expected, MaterialSeamPolicy.DecideCount(composed, capacity, retried));

    [Fact]
    public void Initial_capacity_covers_the_game_rows_with_room_for_the_pool()
    {
        Assert.True(MaterialSeamPolicy.InitialCapacity(0, 0) >= 16);
        Assert.True(MaterialSeamPolicy.InitialCapacity(30, 10) >= 40 + 64);
    }

    [Fact]
    public void The_opt_in_is_named_in_the_disabled_line()
    {
        string line = MaterialSeamPolicy.FormatState(false, true, "off", "off");
        Assert.Contains("[Materials] NativeSeams = true", line);
        Assert.StartsWith("materials:", line);
        Assert.Contains("pay=", MaterialSeamPolicy.FormatState(true, true, "armed@0x1", "armed@0x2"));
        Assert.DoesNotContain("pay=", MaterialSeamPolicy.FormatState(true, false, "armed@0x1", "off"));
    }

    [Fact]
    public void Interop_field_names_select_the_castle_heart_overloads()
    {
        Assert.Contains("_EntityManager_Entity_Entity_Boolean_Boolean_0", MaterialSeamPolicy.CountField);
        Assert.Contains("_EntityManager_Entity_Entity_PrefabGUID_Int32_byref_Int32_byref_Entity_", MaterialSeamPolicy.RemoveField);
        Assert.DoesNotContain("MapZoneCollection", MaterialSeamPolicy.CountField + MaterialSeamPolicy.RemoveField);
    }
}
