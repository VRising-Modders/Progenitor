using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Units;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// ctx.Units aura, game-free half: the <see cref="UnitAura"/> enum, the aura table
/// (<see cref="UnitAuraTable"/>: clone ids, sources and server strip lists), range validation, and
/// the buff-to-aura mapping <c>GetAura</c> and the one-aura rule use. The live half
/// (<c>UnitAuras.cs</c>) registers, strips and applies the clones.
/// </summary>
public class UnitAuraTests
{
    /// <summary>Stand-in for <c>PrefabGUID.CalculateGuidHash(row.Id)</c>: distinct per row.</summary>
    private static int FakeGuid(UnitAuraRow row) => 1000 + (int)row.Aura;

    // ---- enum and validation --------------------------------------------------------------------

    [Fact]
    public void Enum_values_are_pinned()
    {
        Assert.Equal(0, (int)UnitAura.None);
        Assert.Equal(1, (int)UnitAura.Elite);
        Assert.Equal(default, UnitAura.None);
    }

    [Fact]
    public void Every_named_value_is_defined_and_has_a_row_except_None()
    {
        foreach (UnitAura aura in Enum.GetValues(typeof(UnitAura)))
        {
            Assert.True(UnitAuraTable.IsDefined(aura));
            UnitAuraTable.Validate(aura, "aura");
            Assert.Equal(aura != UnitAura.None, UnitAuraTable.TryGet(aura, out _));
        }
        Assert.Equal(Enum.GetValues(typeof(UnitAura)).Length - 1, UnitAuraTable.Rows.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    public void Undefined_values_are_refused(int value)
    {
        var aura = (UnitAura)value;
        Assert.False(UnitAuraTable.IsDefined(aura));
        Assert.False(UnitAuraTable.TryGet(aura, out _));
        var e = Assert.Throws<ArgumentOutOfRangeException>(() => UnitAuraTable.Validate(aura, "aura"));
        Assert.Equal("aura", e.ParamName);
        Assert.Contains(value.ToString(System.Globalization.CultureInfo.InvariantCulture), e.Message);
        Assert.Contains("Elite = 1", UnitAuraTable.RangeMessage(aura));
    }

    [Fact]
    public void Spawn_option_defaults_to_no_aura()
    {
        Assert.Null(new UnitSpawnOptions().Aura);
        Assert.Equal(UnitAura.Elite, new UnitSpawnOptions { Aura = UnitAura.Elite }.Aura);
    }

    // ---- the table ------------------------------------------------------------------------------

    [Fact]
    public void Elite_row_is_the_inert_level_aura_clone()
    {
        Assert.True(UnitAuraTable.TryGet(UnitAura.Elite, out var row));
        // The id is saved with every unit that wears the aura (through its GUID): never change it.
        Assert.Equal("progenitor.core.aura.elite", row.Id);
        Assert.Equal(UnitAuraTable.EliteId, row.Id);
        Assert.Equal("Shared_Affix_LevelAura_Self", row.Source);
        Assert.Equal(-2104035188, row.SourceGuid);
    }

    [Fact]
    public void Elite_strip_list_removes_the_level_aura_gameplay()
    {
        UnitAuraTable.TryGet(UnitAura.Elite, out var row);
        // Seen on the source's server prefab live (2026-09-25): the 1 s tick spawning the 15 m +8 level
        // trigger, its event mapping and listeners, and the Affixes tag.
        foreach (string name in new[] { "CreateGameplayEventsOnTick", "GameplayEventIdMapping", "SpawnPrefabOnGameplayEvent", "GameplayEventListeners", "Affixes" })
        {
            Assert.Contains(name, row.Strip);
            Assert.Contains(name, row.ObservedOnSource);
        }
        Assert.Contains("ModifyUnitLevelBuff", row.Strip);
        // The visual sequence is client-side and must survive: never on a strip list.
        Assert.DoesNotContain("BuffSequence", row.Strip);
        Assert.DoesNotContain("Buff", row.Strip);
    }

    [Fact]
    public void Rows_are_consistent()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var auras = new HashSet<UnitAura>();
        foreach (var row in UnitAuraTable.Rows)
        {
            Assert.NotEqual(UnitAura.None, row.Aura);
            Assert.True(auras.Add(row.Aura), "two rows for " + row.Aura);
            Assert.True(ids.Add(row.Id), "two rows with id " + row.Id);
            Assert.StartsWith("progenitor.core.aura.", row.Id, StringComparison.Ordinal);
            Assert.InRange(row.Id.Length, 1, 63); // the registry's id limit
            Assert.False(string.IsNullOrWhiteSpace(row.Source));
            Assert.NotEmpty(row.Strip);
            Assert.Equal(row.Strip.Length, row.Strip.Distinct(StringComparer.Ordinal).Count());
            foreach (string name in row.Strip)
                Assert.Contains(name, UnitAuraTable.KnownStripComponents);
            foreach (string name in row.ObservedOnSource)
                Assert.Contains(name, row.Strip);
        }
    }

    // ---- reading and replacing ------------------------------------------------------------------

    [Fact]
    public void FromBuffs_finds_the_aura_among_other_buffs()
    {
        int elite = FakeGuid(UnitAuraTable.Rows[0]);
        Assert.Equal(UnitAura.None, UnitAuraTable.FromBuffs(new[] { 5, 6, 7 }, FakeGuid));
        Assert.Equal(UnitAura.None, UnitAuraTable.FromBuffs(Array.Empty<int>(), FakeGuid));
        Assert.Equal(UnitAura.None, UnitAuraTable.FromBuffs(null!, FakeGuid));
        Assert.Equal(UnitAura.Elite, UnitAuraTable.FromBuffs(new[] { 5, elite, 7 }, FakeGuid));
        // The vanilla source buff is not the aura: it carries the +8 level gameplay.
        Assert.Equal(UnitAura.None, UnitAuraTable.FromBuffs(new[] { -2104035188 }, FakeGuid));
    }

    [Fact]
    public void ToRemove_lists_every_other_aura_once()
    {
        int elite = FakeGuid(UnitAuraTable.Rows[0]);
        Assert.Empty(UnitAuraTable.ToRemove(new[] { 5, elite }, UnitAura.Elite, FakeGuid));
        Assert.Equal(new List<int> { elite }, UnitAuraTable.ToRemove(new[] { elite, 5, elite }, UnitAura.None, FakeGuid));
        Assert.Empty(UnitAuraTable.ToRemove(new[] { 5, 6 }, UnitAura.None, FakeGuid));
        Assert.Empty(UnitAuraTable.ToRemove(null!, UnitAura.None, FakeGuid));
    }
}
