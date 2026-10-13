using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Bootstrap;
using Xunit;

using Mode = Progenitor.Bootstrap.SaveSafetyPolicy.Mode;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the world-save load guard (job FSAVE; research <c>save-safety.md</c>,
/// job RD3). The Harmony prefix on
/// <c>LoadPersistenceSystemV2.CopyDataFromPrefabs(DeserializationJobData)</c>
/// needs a live server; the decisions and the operator-facing log text do not,
/// and they are what a regression would silently change.
/// </summary>
public class SaveSafetyTests
{
    // ------------------------------------------------------------ config resolution

    [Fact]
    public void Defaults_drop_unknown_prefabs_and_do_not_fail_the_load()
    {
        Assert.Equal(
            Mode.Drop,
            SaveSafetyPolicy.ResolveMode(
                SaveSafetyPolicy.DropUnknownPrefabsDefault,
                SaveSafetyPolicy.FailLoadInsteadDefault));
    }

    [Fact]
    public void The_default_mode_is_only_reachable_through_the_caps()
    {
        // R29 finding 1: Drop being the default is only defensible because the defaults refuse a
        // blast radius that looks like a broken lookup rather than a missing mod. A load in which
        // every root missed must never drop, at the shipped defaults, with a healthy lookup.
        var everythingMissed = Defaults(dropEntities: 40_000, scanned: 40_000);
        Assert.Equal(SaveSafetyPolicy.Refusal.TooManyEntities, everythingMissed);
        Assert.False(SaveSafetyPolicy.MayDrop(everythingMissed));

        // ... and the handful a missing mod actually costs still goes through.
        Assert.Equal(SaveSafetyPolicy.Refusal.None, Defaults(dropEntities: 2, scanned: 40_000));
    }

    private static SaveSafetyPolicy.Refusal Defaults(int dropEntities, int scanned) =>
        SaveSafetyPolicy.EvaluateBlastRadius(
            lookupCount: 42_000,
            minLookupCount: SaveSafetyPolicy.MinPrefabLookupCountDefault,
            vanillaProbeResolved: true,
            dropEntities: dropEntities,
            scanned: scanned,
            maxDropEntities: SaveSafetyPolicy.MaxDropEntitiesDefault,
            maxDropPercent: SaveSafetyPolicy.MaxDropPercentDefault);

    [Theory]
    [InlineData(true, false, Mode.Drop)]
    [InlineData(false, false, Mode.Off)]
    // FailLoadInstead is the operator's explicit "I want the vanilla abort", so it wins over the
    // default-true DropUnknownPrefabs; otherwise setting it alone would do nothing.
    [InlineData(true, true, Mode.FailLoad)]
    [InlineData(false, true, Mode.FailLoad)]
    public void ResolveMode_lets_FailLoadInstead_win(bool drop, bool fail, Mode expected)
    {
        Assert.Equal(expected, SaveSafetyPolicy.ResolveMode(drop, fail));
    }

    [Fact]
    public void Only_Off_skips_the_scan_and_only_Drop_mutates()
    {
        Assert.False(SaveSafetyPolicy.ModeScans(Mode.Off));
        Assert.True(SaveSafetyPolicy.ModeScans(Mode.Drop));
        Assert.True(SaveSafetyPolicy.ModeScans(Mode.FailLoad));

        Assert.False(SaveSafetyPolicy.ModeDrops(Mode.Off));
        Assert.True(SaveSafetyPolicy.ModeDrops(Mode.Drop));
        Assert.False(SaveSafetyPolicy.ModeDrops(Mode.FailLoad));
    }

    [Fact]
    public void Config_metadata_is_the_documented_section_and_keys()
    {
        Assert.Equal("SaveSafety", SaveSafetyPolicy.ConfigSection);
        Assert.Equal("DropUnknownPrefabs", SaveSafetyPolicy.DropUnknownPrefabsKey);
        Assert.Equal("FailLoadInstead", SaveSafetyPolicy.FailLoadInsteadKey);
        Assert.Contains("autosave", SaveSafetyPolicy.DropUnknownPrefabsDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DropUnknownPrefabs", SaveSafetyPolicy.FailLoadInsteadDescription, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ the loader's predicate

    [Theory]
    // hasParent, resolved, expected "this is the entity the loader would throw on"
    [InlineData(false, false, true)]   // root + unknown guid -> the throwing case
    [InlineData(false, true, false)]   // root + known guid   -> kept
    [InlineData(true, false, false)]   // child + unknown     -> the loader skips it, so do we
    [InlineData(true, true, false)]    // child + known       -> kept
    public void IsUnknownRoot_matches_the_loaders_own_predicate(bool hasParent, bool resolved, bool expected)
    {
        Assert.Equal(expected, SaveSafetyPolicy.IsUnknownRoot(hasParent, resolved));
    }

    [Fact]
    public void A_child_entity_with_an_unknown_guid_is_never_dropped()
    {
        // CopyDataFromPrefabs skips entities that have Unity.Transforms.Parent, so an
        // unresolvable child is not what aborts the load — dropping it would delete part of a
        // prefab that does resolve.
        Assert.False(SaveSafetyPolicy.ShouldDrop(Mode.Drop, hasParent: true, resolvedInPrefabMap: false));
        Assert.True(SaveSafetyPolicy.ShouldKeep(Mode.Drop, hasParent: true, resolvedInPrefabMap: false));
    }

    [Fact]
    public void Drop_mode_drops_only_the_unresolvable_root()
    {
        Assert.True(SaveSafetyPolicy.ShouldDrop(Mode.Drop, hasParent: false, resolvedInPrefabMap: false));
        Assert.False(SaveSafetyPolicy.ShouldDrop(Mode.Drop, hasParent: false, resolvedInPrefabMap: true));
    }

    [Fact]
    public void Off_and_FailLoad_never_drop_anything()
    {
        foreach (var mode in new[] { Mode.Off, Mode.FailLoad })
        {
            Assert.False(SaveSafetyPolicy.ShouldDrop(mode, hasParent: false, resolvedInPrefabMap: false));
            Assert.True(SaveSafetyPolicy.ShouldKeep(mode, hasParent: false, resolvedInPrefabMap: false));
        }
    }

    [Fact]
    public void ShouldKeep_is_the_exact_negation_of_ShouldDrop()
    {
        foreach (var mode in new[] { Mode.Off, Mode.Drop, Mode.FailLoad })
        {
            foreach (bool parent in new[] { false, true })
            {
                foreach (bool resolved in new[] { false, true })
                {
                    Assert.Equal(
                        !SaveSafetyPolicy.ShouldDrop(mode, parent, resolved),
                        SaveSafetyPolicy.ShouldKeep(mode, parent, resolved));
                }
            }
        }
    }

    // ------------------------------------------------------------ tally

    [Fact]
    public void Tally_counts_entities_per_guid_and_reports_guid_and_entity_totals()
    {
        var tally = new SaveSafetyPolicy.UnknownPrefabTally();
        Assert.True(tally.IsEmpty);

        tally.Add(650563057);
        tally.Add(650563057);
        tally.Add(480926629);

        Assert.False(tally.IsEmpty);
        Assert.Equal(2, tally.GuidCount);
        Assert.Equal(3, tally.EntityCount);
        Assert.Equal(2, tally.CountFor(650563057));
        Assert.Equal(1, tally.CountFor(480926629));
        Assert.Equal(0, tally.CountFor(-1));
    }

    [Fact]
    public void Tally_orders_guids_ascending_so_two_loads_log_the_same_order()
    {
        var tally = new SaveSafetyPolicy.UnknownPrefabTally();
        tally.Add(650563057);
        tally.Add(-257494203);
        tally.Add(480926629);

        Assert.Equal(new[] { -257494203, 480926629, 650563057 }, tally.Guids());
    }

    [Fact]
    public void Tally_emits_one_line_per_guid_not_one_per_entity()
    {
        var tally = new SaveSafetyPolicy.UnknownPrefabTally();
        tally.Add(650563057);
        tally.Add(650563057);
        tally.Add(480926629);

        var lines = tally.FormatLines(Mode.Drop);
        Assert.Equal(2, lines.Length);
        Assert.Equal(SaveSafetyPolicy.FormatDropped(1, 480926629), lines[0]);
        Assert.Equal(SaveSafetyPolicy.FormatDropped(2, 650563057), lines[1]);
    }

    [Fact]
    public void Tally_uses_the_report_only_wording_in_FailLoad_mode()
    {
        var tally = new SaveSafetyPolicy.UnknownPrefabTally();
        tally.Add(650563057);

        var lines = tally.FormatLines(Mode.FailLoad);
        Assert.Single(lines);
        Assert.Equal(SaveSafetyPolicy.FormatWouldDrop(1, 650563057), lines[0]);
        Assert.DoesNotContain("dropped", lines[0], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ log formatting

    [Fact]
    public void The_drop_line_is_the_exact_agreed_wording()
    {
        Assert.Equal(
            "SaveSafety: dropped 2 entity(ies) with unknown prefab guid 650563057 (mod missing?) so the load can continue",
            SaveSafetyPolicy.FormatDropped(2, 650563057));
    }

    [Fact]
    public void The_drop_line_prints_a_negative_guid_hash_verbatim()
    {
        // PrefabGUID._Value is a signed int32 and vanilla guids are routinely negative
        // (Item_Ingredient_Crystal is -257494203); the log must be greppable against the save scan.
        Assert.Contains("guid -257494203 ", SaveSafetyPolicy.FormatDropped(1, -257494203), StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_names_entities_children_guids_and_the_scanned_roots()
    {
        // R29 finding 9: the operator must be told the real total, not just the roots.
        string line = SaveSafetyPolicy.FormatSummary(entities: 5, children: 2, guids: 2, scanned: 41000, elapsedMs: 812);
        Assert.Contains("5 entity(ies)", line, StringComparison.Ordinal);
        Assert.Contains("2 of them children of a dropped root", line, StringComparison.Ordinal);
        Assert.Contains("2 unknown prefab guid(s)", line, StringComparison.Ordinal);
        Assert.Contains("41000 entity(ies) scanned", line, StringComparison.Ordinal);
        Assert.Contains("812 ms", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_load_reports_the_scan_and_says_nothing_was_dropped()
    {
        string line = SaveSafetyPolicy.FormatClean(41000, 812);
        Assert.Contains("41000", line, StringComparison.Ordinal);
        Assert.Contains("812 ms", line, StringComparison.Ordinal);
        Assert.Contains("nothing dropped", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_autosave_warning_names_the_save_and_the_data_loss()
    {
        string line = SaveSafetyPolicy.FormatAutosaveWarning(3, 2, "test1");
        Assert.Contains("save 'test1'", line, StringComparison.Ordinal);
        Assert.Contains("NEXT AUTOSAVE WILL NOT CONTAIN THEM", line, StringComparison.Ordinal);
        Assert.Contains("3 entity(ies)", line, StringComparison.Ordinal);
        Assert.Contains("2 unknown prefab guid(s)", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unknown_save_name_still_produces_a_readable_warning(string? saveName)
    {
        Assert.Equal("this world (save name unknown)", SaveSafetyPolicy.DescribeSave(saveName));
        Assert.Contains(
            "this world (save name unknown)",
            SaveSafetyPolicy.FormatAutosaveWarning(1, 1, saveName),
            StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeSave_trims_the_captured_name()
    {
        Assert.Equal("save 'test1'", SaveSafetyPolicy.DescribeSave("  test1 "));
    }

    [Fact]
    public void FailLoad_summary_and_warning_say_nothing_was_dropped_and_tell_the_operator_to_stop()
    {
        string summary = SaveSafetyPolicy.FormatFailLoadSummary(3, 2, 41000);
        Assert.Contains("Nothing was dropped", summary, StringComparison.Ordinal);
        Assert.Contains("PersistenceV2 - Load Persistence Failed", summary, StringComparison.Ordinal);

        string warning = SaveSafetyPolicy.FormatFailLoadWarning(3, 2, "test1");
        Assert.Contains("save 'test1'", warning, StringComparison.Ordinal);
        Assert.Contains("Stop it now.", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bound_line_says_what_each_mode_does()
    {
        Assert.Contains("armed", SaveSafetyPolicy.FormatBound(Mode.Drop), StringComparison.Ordinal);
        Assert.Contains("report-only", SaveSafetyPolicy.FormatBound(Mode.FailLoad), StringComparison.Ordinal);
        Assert.Contains("disabled", SaveSafetyPolicy.FormatBound(Mode.Off), StringComparison.Ordinal);
    }

    [Fact]
    public void A_guid_this_process_cloned_is_called_out_as_a_progenitor_problem()
    {
        string line = SaveSafetyPolicy.FormatKnownModGuid(650563057);
        Assert.Contains("650563057", line, StringComparison.Ordinal);
        Assert.Contains("clone set", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Inventory_slots_are_documented_as_untouched()
    {
        // §1.7: InventoryBuffer is buffer data, never an entity, so it never reaches the
        // CopyDataFromPrefabs loop; RepairBrokenInventoryItemsSystem skips an unknown item guid.
        Assert.Contains("kept verbatim", SaveSafetyPolicy.InventorySlotsNote, StringComparison.Ordinal);
        Assert.Contains("RepairBrokenInventoryItemsSystem", SaveSafetyPolicy.InventorySlotsNote, StringComparison.Ordinal);
        Assert.Contains("not touched", SaveSafetyPolicy.InventorySlotsNote, StringComparison.Ordinal);
    }

    [Fact]
    public void A_layout_mismatch_refuses_to_edit_rather_than_writing_at_the_wrong_offset()
    {
        string line = SaveSafetyPolicy.FormatLayoutRefused(120, 7);
        Assert.Contains("refusing to edit", line, StringComparison.Ordinal);
        Assert.Contains("120", line, StringComparison.Ordinal);
        Assert.Contains("7", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Pin_diag_line_has_the_grep_prefix_and_the_counters()
    {
        string line = SaveSafetyPolicy.FormatPins(armed: true, Mode.Drop, loads: 2, droppedEntities: 3, droppedGuids: 1, saveName: "test1");
        Assert.StartsWith("pin-diag SaveSafety ", line, StringComparison.Ordinal);
        Assert.Contains("armed=True", line, StringComparison.Ordinal);
        Assert.Contains("mode=Drop", line, StringComparison.Ordinal);
        Assert.Contains("loads=2", line, StringComparison.Ordinal);
        Assert.Contains("droppedEntities=3", line, StringComparison.Ordinal);
        Assert.Contains("droppedGuids=1", line, StringComparison.Ordinal);
        Assert.Contains("save=test1", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Pin_diag_line_tolerates_an_unresolved_save_name()
    {
        Assert.Contains(
            "save=?",
            SaveSafetyPolicy.FormatPins(false, Mode.Off, 0, 0, 0, null),
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ blast radius (FSAVE2, R29 finding 1)

    [Fact]
    public void An_empty_prefab_lookup_refuses_every_drop()
    {
        // The scenario that can empty a world: the map exists but nothing is registered, so every
        // root misses. Vanilla throws here; the guard must let it.
        Assert.Equal(
            SaveSafetyPolicy.Refusal.LookupEmpty,
            SaveSafetyPolicy.EvaluateBlastRadius(
                lookupCount: 0, minLookupCount: 5000, vanillaProbeResolved: false,
                dropEntities: 1, scanned: 1, maxDropEntities: 5000, maxDropPercent: 0.5));
    }

    [Fact]
    public void A_lookup_below_the_floor_refuses_even_a_single_drop()
    {
        Assert.Equal(
            SaveSafetyPolicy.Refusal.LookupTooSmall,
            SaveSafetyPolicy.EvaluateBlastRadius(
                lookupCount: 4999, minLookupCount: 5000, vanillaProbeResolved: true,
                dropEntities: 1, scanned: 40000, maxDropEntities: 5000, maxDropPercent: 0.5));

        Assert.Equal(
            SaveSafetyPolicy.Refusal.None,
            SaveSafetyPolicy.EvaluateBlastRadius(
                lookupCount: 5000, minLookupCount: 5000, vanillaProbeResolved: true,
                dropEntities: 1, scanned: 40000, maxDropEntities: 5000, maxDropPercent: 0.5));
    }

    [Fact]
    public void A_populated_lookup_that_cannot_resolve_a_known_vanilla_guid_is_not_trusted()
    {
        Assert.Equal(
            SaveSafetyPolicy.Refusal.LookupProbeFailed,
            SaveSafetyPolicy.EvaluateBlastRadius(
                lookupCount: 42000, minLookupCount: 5000, vanillaProbeResolved: false,
                dropEntities: 1, scanned: 40000, maxDropEntities: 5000, maxDropPercent: 0.5));
    }

    [Fact]
    public void The_probe_set_is_not_empty_and_carries_the_documented_crystal_guid()
    {
        Assert.NotEmpty(SaveSafetyPolicy.KnownVanillaGuids);
        Assert.Contains(-257494203, SaveSafetyPolicy.KnownVanillaGuids);
    }

    [Theory]
    // dropEntities, maxDropEntities, expected
    [InlineData(5000, 5000, SaveSafetyPolicy.Refusal.None)]
    [InlineData(5001, 5000, SaveSafetyPolicy.Refusal.TooManyEntities)]
    [InlineData(1, 0, SaveSafetyPolicy.Refusal.TooManyEntities)]
    public void The_absolute_cap_is_inclusive_and_zero_disables_dropping(
        int dropEntities, int maxDropEntities, SaveSafetyPolicy.Refusal expected)
    {
        Assert.Equal(
            expected,
            SaveSafetyPolicy.EvaluateBlastRadius(
                lookupCount: 42000, minLookupCount: 5000, vanillaProbeResolved: true,
                dropEntities: dropEntities, scanned: 10_000_000,
                maxDropEntities: maxDropEntities, maxDropPercent: 100));
    }

    [Theory]
    // 0.5% of 40000 is 200: at the line it passes, one over it refuses.
    [InlineData(200, 40000, SaveSafetyPolicy.Refusal.None)]
    [InlineData(201, 40000, SaveSafetyPolicy.Refusal.RatioTooHigh)]
    // A tiny save is protected by the ratio even though the absolute cap is nowhere near.
    [InlineData(2, 100, SaveSafetyPolicy.Refusal.RatioTooHigh)]
    public void The_ratio_cap_refuses_a_miss_rate_that_looks_like_a_broken_lookup(
        int dropEntities, int scanned, SaveSafetyPolicy.Refusal expected)
    {
        Assert.Equal(
            expected,
            SaveSafetyPolicy.EvaluateBlastRadius(
                lookupCount: 42000, minLookupCount: 5000, vanillaProbeResolved: true,
                dropEntities: dropEntities, scanned: scanned,
                maxDropEntities: int.MaxValue, maxDropPercent: 0.5));
    }

    [Fact]
    public void Dropping_without_having_scanned_anything_is_refused()
    {
        Assert.Equal(
            SaveSafetyPolicy.Refusal.RatioTooHigh,
            SaveSafetyPolicy.EvaluateBlastRadius(
                lookupCount: 42000, minLookupCount: 5000, vanillaProbeResolved: true,
                dropEntities: 1, scanned: 0, maxDropEntities: 5000, maxDropPercent: 100));
    }

    [Fact]
    public void A_clean_load_never_refuses_because_there_is_nothing_to_refuse()
    {
        // Nothing to drop short-circuits before any of the liveness tests, so an unpopulated
        // lookup on a save with no unknown guids is still a clean load, not a refusal.
        Assert.Equal(
            SaveSafetyPolicy.Refusal.None,
            SaveSafetyPolicy.EvaluateBlastRadius(
                lookupCount: 0, minLookupCount: 5000, vanillaProbeResolved: false,
                dropEntities: 0, scanned: 0, maxDropEntities: 0, maxDropPercent: 0));
    }

    [Theory]
    [InlineData(-1d, 0d)]
    [InlineData(0d, 0d)]
    [InlineData(0.5d, 0.5d)]
    [InlineData(101d, 100d)]
    [InlineData(double.NaN, 0d)]
    public void MaxDropPercent_is_clamped_into_zero_to_one_hundred(double raw, double expected)
    {
        Assert.Equal(expected, SaveSafetyPolicy.ClampMaxDropPercent(raw));
    }

    [Fact]
    public void The_lookup_floor_can_never_be_configured_down_to_zero()
    {
        Assert.Equal(1, SaveSafetyPolicy.ClampMinPrefabLookupCount(0));
        Assert.Equal(1, SaveSafetyPolicy.ClampMinPrefabLookupCount(-9));
        Assert.Equal(0, SaveSafetyPolicy.ClampMaxDropEntities(-9));
    }

    [Fact]
    public void The_refusal_line_is_the_agreed_wording_and_names_the_cap_that_fired()
    {
        string line = SaveSafetyPolicy.FormatRefusedToDrop(
            SaveSafetyPolicy.Refusal.RatioTooHigh,
            drops: 39000, scanned: 40000, lookupCount: 42000,
            minLookupCount: 5000, maxDropEntities: 5000, maxDropPercent: 0.5);

        Assert.StartsWith("SaveSafety: refusing to drop 39000 of 40000 entities (", line, StringComparison.Ordinal);
        Assert.EndsWith("); falling back to the vanilla load failure", line, StringComparison.Ordinal);
        Assert.Contains("SaveSafety.MaxDropPercent=0.5%", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_refusal_reason_explains_itself_with_the_numbers_that_caused_it()
    {
        Assert.Contains("Count()=0", SaveSafetyPolicy.DescribeRefusal(
            SaveSafetyPolicy.Refusal.LookupEmpty, 0, 5000, 5000, 0.5), StringComparison.Ordinal);
        Assert.Contains("SaveSafety.MinPrefabLookupCount=5000", SaveSafetyPolicy.DescribeRefusal(
            SaveSafetyPolicy.Refusal.LookupTooSmall, 12, 5000, 5000, 0.5), StringComparison.Ordinal);
        Assert.Contains("no known vanilla prefab guid resolved", SaveSafetyPolicy.DescribeRefusal(
            SaveSafetyPolicy.Refusal.LookupProbeFailed, 42000, 5000, 5000, 0.5), StringComparison.Ordinal);
        Assert.Contains("SaveSafety.MaxDropEntities=5000", SaveSafetyPolicy.DescribeRefusal(
            SaveSafetyPolicy.Refusal.TooManyEntities, 42000, 5000, 5000, 0.5), StringComparison.Ordinal);
    }

    [Fact]
    public void The_config_descriptions_say_the_vanilla_outcome_is_a_failed_load_not_a_silent_loss()
    {
        Assert.Contains("MaxDropPercent", SaveSafetyPolicy.DropUnknownPrefabsDescription, StringComparison.Ordinal);
        Assert.Contains("vanilla load failure", SaveSafetyPolicy.MaxDropPercentDescription, StringComparison.Ordinal);
        Assert.Contains("vanilla load failure", SaveSafetyPolicy.MaxDropEntitiesDescription, StringComparison.Ordinal);
        Assert.Contains("children of a dropped root", SaveSafetyPolicy.MaxDropEntitiesDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not built yet", SaveSafetyPolicy.MinPrefabLookupCountDescription, StringComparison.Ordinal);
        Assert.Contains("MaxDropPercent", SaveSafetyPolicy.FormatBound(Mode.Drop), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ lockstep compaction (R29 finding 2)

    [Fact]
    public void Compaction_preserves_order_and_moves_both_arrays_in_lockstep()
    {
        // NewEntities / OldEntities are paired by ordinal in SetupOldToNewEntityRemappingHashMap.
        // Removing index 1 and 3 must leave 0,2,4 in that order in *both*.
        var news = new[] { 10, 11, 12, 13, 14 };
        var olds = new[] { 90, 91, 92, 93, 94 };
        var remove = new[] { false, true, false, true, false };

        int length = SaveSafetyPolicy.CompactInOrder(
            news.Length,
            i => remove[i],
            (from, to) => { news[to] = news[from]; olds[to] = olds[from]; });

        Assert.Equal(3, length);
        Assert.Equal(new[] { 10, 12, 14 }, news[..length]);
        Assert.Equal(new[] { 90, 92, 94 }, olds[..length]);
    }

    [Fact]
    public void Compaction_is_not_swap_back_the_tail_never_jumps_to_the_front()
    {
        // The swap-back this replaces would produce 10,14,12 here. Order is the whole point.
        var news = new[] { 10, 11, 12, 13, 14 };
        int length = SaveSafetyPolicy.CompactInOrder(
            news.Length,
            i => i == 1 || i == 3,
            (from, to) => news[to] = news[from]);

        Assert.Equal(new[] { 10, 12, 14 }, news[..length]);
        Assert.NotEqual(14, news[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void Compacting_nothing_touches_nothing_and_compacting_everything_empties_the_list(int length)
    {
        int moves = 0;
        Assert.Equal(length, SaveSafetyPolicy.CompactInOrder(length, _ => false, (_, _) => moves++));
        Assert.Equal(0, moves);

        Assert.Equal(0, SaveSafetyPolicy.CompactInOrder(length, _ => true, (_, _) => moves++));
        Assert.Equal(0, moves);
    }

    [Fact]
    public void A_leading_removal_shifts_every_survivor_down_by_one()
    {
        var values = new[] { 1, 2, 3 };
        int length = SaveSafetyPolicy.CompactInOrder(3, i => i == 0, (from, to) => values[to] = values[from]);
        Assert.Equal(2, length);
        Assert.Equal(new[] { 2, 3 }, values[..length]);
    }

    // ------------------------------------------------------------ abort on throw (R29 finding 17)

    [Fact]
    public void A_throw_partway_through_the_edit_propagates_so_the_caller_can_roll_back()
    {
        var news = new[] { 10, 11, 12, 13, 14 };
        var snapshot = (int[])news.Clone();

        Assert.Throws<InvalidOperationException>(() => SaveSafetyPolicy.CompactInOrder(
            news.Length,
            i => i == 1 || i == 3,
            (from, to) =>
            {
                if (to == 2) throw new InvalidOperationException("layout moved");
                news[to] = news[from];
            }));

        // The half-applied array is not what the loader must see; the guard restores it verbatim
        // and destroys nothing, which is the vanilla failed load.
        Assert.NotEqual(snapshot, news);
        Array.Copy(snapshot, news, snapshot.Length);
        Assert.Equal(snapshot, news);
    }

    [Fact]
    public void The_rollback_line_says_nothing_was_destroyed()
    {
        string line = SaveSafetyPolicy.FormatRestoredAfterThrow(3, "layout moved");
        Assert.Contains("rolled back across 3 archetype(s)", line, StringComparison.Ordinal);
        Assert.Contains("nothing was destroyed", line, StringComparison.Ordinal);
        Assert.Contains("layout moved", line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_abort_before_any_mutation_names_the_reason_and_falls_back_to_vanilla()
    {
        string line = SaveSafetyPolicy.FormatAbortedBeforeMutation("layout could not be validated");
        Assert.Contains("aborted before changing anything", line, StringComparison.Ordinal);
        Assert.Contains("layout could not be validated", line, StringComparison.Ordinal);
        Assert.Contains("vanilla load failure", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_whose_postfix_never_ran_is_reported_and_not_carried_into_the_next_load()
    {
        string line = SaveSafetyPolicy.FormatPlanAbandoned(4);
        Assert.Contains("4 entity(ies)", line, StringComparison.Ordinal);
        Assert.Contains("nothing was destroyed", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_retire_line_reports_what_happened_not_what_was_planned()
    {
        // R29 residual 21.6: the counters and the log are charged from the destroys.
        string partial = SaveSafetyPolicy.FormatRetired(retired: 2, planned: 5, rawDestroyed: 1, failed: 2);
        Assert.Contains("retired 2 of 5 planned", partial, StringComparison.Ordinal);
        Assert.Contains("1 fell back to DestroyEntity", partial, StringComparison.Ordinal);
        Assert.Contains("2 could not be destroyed", partial, StringComparison.Ordinal);

        string clean = SaveSafetyPolicy.FormatRetired(retired: 5, planned: 5, rawDestroyed: 0, failed: 0);
        Assert.Contains("ModificationCleanupSystem", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("fell back", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("could not", clean, StringComparison.Ordinal);
    }

    [Fact]
    public void The_plan_line_repeats_the_mode_and_says_the_edit_is_lockstep_and_ordered()
    {
        // R29 residual 21.7: FormatBound is printed at patch time, the mode is re-read per load.
        string line = SaveSafetyPolicy.FormatPlan(Mode.Drop, entities: 5, children: 2, guids: 1, scanned: 40000);
        Assert.Contains("mode=Drop", line, StringComparison.Ordinal);
        Assert.Contains("5 entity(ies) (2 child(ren))", line, StringComparison.Ordinal);
        Assert.Contains("lockstep, order preserved", line, StringComparison.Ordinal);
        Assert.Contains("after CopyDataFromPrefabs returns", line, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ doomed children (R29 finding 9)

    [Fact]
    public void A_child_of_a_dropped_root_is_dropped_with_it_and_so_is_the_whole_chain()
    {
        //  1 (doomed root) <- 2 <- 3      4 (fine root) <- 5
        var parents = new Dictionary<int, int> { [2] = 1, [3] = 2, [5] = 4 };
        bool Lookup(int child, out int parent) => parents.TryGetValue(child, out parent);

        var doomed = new HashSet<int> { 1 };
        int added = SaveSafetyPolicy.CollectDoomedDescendants(
            new[] { 1, 2, 3, 4, 5 }, Lookup, doomed, SaveSafetyPolicy.MaxParentChainDepth);

        Assert.Equal(2, added);
        Assert.Equal(new[] { 1, 2, 3 }, doomed.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void A_child_of_a_resolvable_root_is_still_left_alone()
    {
        var parents = new Dictionary<int, int> { [2] = 1 };
        bool Lookup(int child, out int parent) => parents.TryGetValue(child, out parent);

        var doomed = new HashSet<int>();
        Assert.Equal(0, SaveSafetyPolicy.CollectDoomedDescendants(
            new[] { 1, 2 }, Lookup, doomed, SaveSafetyPolicy.MaxParentChainDepth));
        Assert.Empty(doomed);
    }

    [Fact]
    public void A_parent_cycle_terminates_instead_of_hanging_the_load()
    {
        var parents = new Dictionary<int, int> { [1] = 2, [2] = 3, [3] = 1 };
        bool Lookup(int child, out int parent) => parents.TryGetValue(child, out parent);

        var doomed = new HashSet<int>();
        Assert.Equal(0, SaveSafetyPolicy.CollectDoomedDescendants(
            new[] { 1, 2, 3 }, Lookup, doomed, SaveSafetyPolicy.MaxParentChainDepth));
        Assert.Empty(doomed);
    }

    [Fact]
    public void A_chain_longer_than_the_depth_bound_is_abandoned_rather_than_walked_forever()
    {
        var parents = new Dictionary<int, int>();
        for (int i = 1; i <= 200; i++) parents[i] = i + 1;   // 1 -> 2 -> ... -> 201
        bool Lookup(int child, out int parent) => parents.TryGetValue(child, out parent);

        var doomed = new HashSet<int> { 201 };
        SaveSafetyPolicy.CollectDoomedDescendants(new[] { 1 }, Lookup, doomed, maxDepth: 8);

        Assert.Single(doomed);
        Assert.Contains(201, doomed);
    }

    [Fact]
    public void Children_are_counted_so_the_caps_and_the_operator_see_the_real_total()
    {
        // Two unresolvable roots plus three descendants is five entities, and it is five that the
        // cap must be evaluated against — not two (R29 finding 9's invisibility complaint).
        var parents = new Dictionary<int, int> { [3] = 1, [4] = 3, [5] = 2 };
        bool Lookup(int child, out int parent) => parents.TryGetValue(child, out parent);

        var doomed = new HashSet<int> { 1, 2 };
        SaveSafetyPolicy.CollectDoomedDescendants(
            new[] { 1, 2, 3, 4, 5 }, Lookup, doomed, SaveSafetyPolicy.MaxParentChainDepth);

        Assert.Equal(5, doomed.Count);
        Assert.Equal(
            SaveSafetyPolicy.Refusal.TooManyEntities,
            SaveSafetyPolicy.EvaluateBlastRadius(
                lookupCount: 42000, minLookupCount: 5000, vanillaProbeResolved: true,
                dropEntities: doomed.Count, scanned: 40000, maxDropEntities: 4, maxDropPercent: 100));
    }

    // ------------------------------------------------------------ site registration

    [Fact]
    public void The_guard_site_is_bootstrap_owned_and_not_a_core_content_site()
    {
        Assert.Equal("LoadPersistenceSystemV2.CopyDataFromPrefabs", HarmonySlotPolicy.CopyDataFromPrefabs);

        // Not in AllSites / ContentSites: Diagnostics.SkipCoreHarmony must never empty this guard,
        // and Core has no fallback patch for it.
        Assert.DoesNotContain(HarmonySlotPolicy.CopyDataFromPrefabs, HarmonySlotPolicy.AllSites);
        Assert.DoesNotContain(HarmonySlotPolicy.CopyDataFromPrefabs, HarmonySlotPolicy.ContentSites);
        Assert.DoesNotContain(HarmonySlotPolicy.CopyDataFromPrefabs, HarmonySlotPolicy.PluginSites);

        // No slot, so no plugin or Core may bind it.
        Assert.False(HarmonySlotPolicy.IsKnownSlot(HarmonySlotPolicy.CopyDataFromPrefabs));
        Assert.False(HarmonySlotPolicy.IsPluginSlot(HarmonySlotPolicy.CopyDataFromPrefabs));

        // CanonicalSite passes it through unchanged (it is its own site).
        Assert.Equal(
            HarmonySlotPolicy.CopyDataFromPrefabs,
            HarmonySlotPolicy.CanonicalSite(HarmonySlotPolicy.CopyDataFromPrefabs));
    }

    [Fact]
    public void The_guard_site_is_distinct_from_the_existing_load_seam()
    {
        // DeserializeLoadFile is the apply gate (runs once per archetype file, before the
        // entities are checked); CopyDataFromPrefabs is the single-shot guard after them all.
        Assert.NotEqual(HarmonySlotPolicy.DeserializeLoadFile, HarmonySlotPolicy.CopyDataFromPrefabs);
    }
}
