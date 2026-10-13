using System;
using Progenitor.Abilities;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The game-free rules behind <c>ctx.Abilities.SetSlot</c>: which slots are on the bar, the clone
/// id, and the <c>ReplaceAbilityOnSlotBuff</c> element bytes. No game.
/// </summary>
public class AbilitySlotPolicyTests
{
    [Fact]
    public void Bar_slots_are_zero_to_eight()
    {
        Assert.True(AbilitySlotPolicy.IsBarSlot(0));
        Assert.True(AbilitySlotPolicy.IsBarSlot(8));
        Assert.False(AbilitySlotPolicy.IsBarSlot(-1));
        Assert.False(AbilitySlotPolicy.IsBarSlot(9));
    }

    [Fact]
    public void Element_size_is_the_measured_stride()
    {
        // CopyCooldown's byte at 0x24, rounded up to the blob reference's 8-byte alignment; the
        // research measured 40 on a live element.
        Assert.Equal(40, AbilitySlotPolicy.ElementSize);
    }

    [Fact]
    public void Element_carries_slot_group_and_priority_and_nothing_else()
    {
        var bytes = new byte[AbilitySlotPolicy.ElementSize];
        Array.Fill(bytes, (byte)0xCD);
        AbilitySlotPolicy.WriteElement(bytes, slot: 5, abilityGroupGuid: -1204819086);

        Assert.Equal(5, BitConverter.ToInt32(bytes, 0x4));
        Assert.Equal(-1204819086, BitConverter.ToInt32(bytes, 0xC));
        Assert.Equal(AbilitySlotPolicy.Priority, BitConverter.ToInt32(bytes, 0x10));
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i >= 0x4 && i < 0x8 || i >= 0xC && i < 0x14) continue;
            Assert.True(bytes[i] == 0, "byte 0x" + i.ToString("X") + " should be zero");
        }
    }

    [Fact]
    public void Element_refuses_a_short_span()
    {
        Assert.Throws<ArgumentException>(() => AbilitySlotPolicy.WriteElement(new byte[16], 5, 1));
    }

    [Fact]
    public void Clone_ids_are_stable_and_distinct_per_plugin()
    {
        Assert.Equal("progenitor.abilityslot.mod.a.5.m1204819086", AbilitySlotPolicy.CloneId("mod.a", 5, -1204819086));
        Assert.Equal("progenitor.abilityslot.mod.a.7.2138402840", AbilitySlotPolicy.CloneId("mod.a", 7, 2138402840));
        Assert.NotEqual(AbilitySlotPolicy.CloneId("mod.a", 5, 1), AbilitySlotPolicy.CloneId("mod.a", 6, 1));
        Assert.NotEqual(AbilitySlotPolicy.CloneId("mod.a", 5, 1), AbilitySlotPolicy.CloneId("mod.b", 5, 1));
        Assert.Equal("progenitor.abilityslot.mod.a.0.m2147483648", AbilitySlotPolicy.CloneId("mod.a", 0, int.MinValue));
        Assert.Throws<ArgumentException>(() => AbilitySlotPolicy.CloneId(" ", 5, 1));
    }
}
