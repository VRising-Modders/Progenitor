using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Progenitor.Dye;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Core's Dye all (src/Progenitor.Core/Dye, a sample until 2026-10-08), its game-free halves: the press-and-hold
/// machine, the slot and index planner, the ring math, the settings, the former mod's cfg import and the
/// stand-down rule. Plus Core's pointer-watch registry the swatch watches are swept through.
/// </summary>
public class DyeAllTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "progenitor-dyeall-" + Guid.NewGuid().ToString("N"));

    public DyeAllTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    // ---- hold machine ---------------------------------------------------------------------------

    [Fact]
    public void Hold_charges_then_fires_once_and_waits_for_release()
    {
        var m = new DyeHoldMachine();
        Assert.True(m.Press(swatch: 3, primary: true, isColor: true, enabled: true, holdSeconds: 1.0, now: 10.0));
        var half = m.Tick(10.5, targetVisible: true, enabled: true);
        Assert.Equal(HoldPhase.Charging, half.Phase);
        Assert.Equal(0.5f, half.Progress, 3);
        Assert.False(half.Fire);

        var done = m.Tick(11.0, true, true);
        Assert.True(done.Fire);
        Assert.Equal(1f, done.Progress);
        Assert.Equal(HoldPhase.Completed, done.Phase);

        var after = m.Tick(12.0, true, true);
        Assert.False(after.Fire);
        Assert.Equal(HoldPhase.Completed, after.Phase);

        m.Release(3);
        var idle = m.Tick(12.1, true, true);
        Assert.Equal(HoldPhase.Idle, idle.Phase);
        Assert.Equal(HoldCancel.None, idle.Cancelled);
        Assert.False(idle.Fire);
    }

    [Fact]
    public void Early_release_cancels_without_firing()
    {
        var m = new DyeHoldMachine();
        m.Press(1, true, true, true, 1.0, 0.0);
        m.Tick(0.4, true, true);
        m.Release(1);
        var step = m.Tick(0.5, true, true);
        Assert.Equal(HoldPhase.Idle, step.Phase);
        Assert.Equal(HoldCancel.Released, step.Cancelled);
        Assert.False(step.Fire);
        // A later tick past the hold time never fires the canceled hold.
        Assert.False(m.Tick(5.0, true, true).Fire);
        Assert.Equal(HoldCancel.None, m.Tick(5.1, true, true).Cancelled);
    }

    [Fact]
    public void Panel_closing_mid_hold_cancels()
    {
        var m = new DyeHoldMachine();
        m.Press(1, true, true, true, 1.0, 0.0);
        var step = m.Tick(0.6, targetVisible: false, enabled: true);
        Assert.Equal(HoldCancel.Closed, step.Cancelled);
        Assert.False(step.Fire);
        Assert.False(m.Tick(2.0, true, true).Fire);
    }

    [Fact]
    public void Leaving_the_swatch_or_disabling_cancels()
    {
        var m = new DyeHoldMachine();
        m.Press(1, true, true, true, 1.0, 0.0);
        m.Leave(2); // another swatch: ignored
        Assert.Equal(HoldPhase.Charging, m.Phase);
        m.Leave(1);
        Assert.Equal(HoldCancel.Left, m.Tick(0.2, true, true).Cancelled);

        m.Press(1, true, true, true, 1.0, 1.0);
        Assert.Equal(HoldCancel.Disabled, m.Tick(1.5, true, enabled: false).Cancelled);

        m.Press(1, true, true, true, 1.0, 2.0);
        m.Cancel(HoldCancel.Unloaded);
        Assert.Equal(HoldCancel.Unloaded, m.Tick(2.1, true, true).Cancelled);
    }

    [Fact]
    public void Only_a_primary_press_on_a_color_swatch_while_enabled_starts_a_hold()
    {
        var m = new DyeHoldMachine();
        Assert.False(m.Press(1, primary: false, isColor: true, enabled: true, 1.0, 0));
        Assert.False(m.Press(1, true, isColor: false, enabled: true, 1.0, 0)); // the X hide/show cell, or empty
        Assert.False(m.Press(1, true, true, enabled: false, 1.0, 0));
        Assert.Equal(HoldPhase.Idle, m.Phase);
        Assert.True(m.Press(1, true, true, true, 1.0, 0));
        Assert.False(m.Press(2, true, true, true, 1.0, 0.1)); // one hold at a time
        Assert.Equal(1, m.Swatch);
    }

    [Fact]
    public void Leaving_after_completion_keeps_the_hold_until_release()
    {
        var m = new DyeHoldMachine();
        m.Press(1, true, true, true, 0.5, 0);
        Assert.True(m.Tick(0.5, true, true).Fire);
        m.Leave(1);
        Assert.Equal(HoldPhase.Completed, m.Phase);
        m.Release(1);
        Assert.Equal(HoldPhase.Idle, m.Phase);
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.0, DyeHoldMachine.MinHoldSeconds)]
    [InlineData(99.0, DyeHoldMachine.MaxHoldSeconds)]
    [InlineData(double.NaN, DyeHoldMachine.DefaultHoldSeconds)]
    [InlineData(double.PositiveInfinity, DyeHoldMachine.DefaultHoldSeconds)]
    public void Hold_time_is_clamped(double configured, double expected)
    {
        Assert.Equal(expected, DyeHoldMachine.ClampHold(configured));
        var m = new DyeHoldMachine();
        m.Press(1, true, true, true, configured, 0);
        Assert.Equal(expected, m.HoldSeconds);
    }

    // ---- planner ---------------------------------------------------------------------------------

    private static DyeItem Dyeable(int index, byte active = 0, byte colors = 20, IReadOnlyList<DyePaletteKey>? palettes = null,
        bool hidden = false) =>
        new(index, 1, present: true, hasTransmog: true, active, colors, palettes, hidden);

    private static DyeItem Plain(int index) => new(index, 1, present: true, hasTransmog: false, 0, 0);

    private static List<DyeSlot> Slots(params (int type, DyeItem stat, DyeItem cosmetic)[] filled)
    {
        var slots = new List<DyeSlot>();
        foreach (int type in DyeEquipmentTypes.Dyeable)
        {
            var match = filled.FirstOrDefault(f => f.type == type);
            slots.Add(match.type == type ? new DyeSlot(type, match.stat, match.cosmetic) : new DyeSlot(type, DyeItem.None, DyeItem.None));
        }

        return slots;
    }

    [Fact]
    public void Only_the_five_picker_slots_are_dyeable_and_headgear_is_not_among_them()
    {
        Assert.Equal(new[] { 1, 5, 4, 9, 8 }, DyeEquipmentTypes.Dyeable);
        Assert.DoesNotContain(0, DyeEquipmentTypes.Dyeable);   // headgear
        Assert.DoesNotContain(10, DyeEquipmentTypes.Dyeable);  // bag
        Assert.DoesNotContain(3, DyeEquipmentTypes.Dyeable);   // magic source (ring)
        Assert.Equal(12, DyeEquipmentTypes.CosmeticOf(DyeEquipmentTypes.Chest));
        Assert.Equal(16, DyeEquipmentTypes.CosmeticOf(DyeEquipmentTypes.Gloves));
        Assert.Equal(-1, DyeEquipmentTypes.CosmeticOf(0));
    }

    [Fact]
    public void Plan_dyes_every_dyeable_worn_piece_and_skips_empty_and_plain_ones()
    {
        var slots = Slots(
            (DyeEquipmentTypes.Chest, Dyeable(10), DyeItem.None),
            (DyeEquipmentTypes.Legs, Dyeable(11), DyeItem.None),
            (DyeEquipmentTypes.Gloves, Plain(12), DyeItem.None));
        var plan = DyePlanner.Plan(slots, new DyeColor(4, null));

        Assert.Equal(new[] { (1, 4), (5, 4) }, plan.Targets.Select(t => (t.EquipmentType, (int)t.NewIndex)).ToArray());
        Assert.Contains(plan.Skipped, s => s.EquipmentType == DyeEquipmentTypes.Gloves && s.Reason == DyeSkipReason.NotDyeable);
        Assert.Contains(plan.Skipped, s => s.EquipmentType == DyeEquipmentTypes.Footgear && s.Reason == DyeSkipReason.Empty);
        Assert.Contains(plan.Skipped, s => s.EquipmentType == DyeEquipmentTypes.Cloak && s.Reason == DyeSkipReason.Empty);
        Assert.False(plan.MatchedByPalette);
    }

    [Fact]
    public void A_transmog_is_dyed_with_its_cosmetic_type_and_the_covered_piece_with_its_stat_type()
    {
        var slots = Slots((DyeEquipmentTypes.Chest, Dyeable(10), Dyeable(20)));
        var plan = DyePlanner.Plan(slots, new DyeColor(2, null), includeCovered: true);
        // The covered piece goes first: the server re-keys the shown (transmog) slot from whichever item an
        // event names, so the transmog's own event must be the last word on what is shown.
        Assert.Equal(new[] { (1, 10), (12, 20) }, plan.Targets.Select(t => (t.EquipmentType, t.Item.Index)).ToArray());
        Assert.DoesNotContain(plan.Targets, t => t.Restore);

        var visibleOnly = DyePlanner.Plan(slots, new DyeColor(2, null), includeCovered: false);
        Assert.Equal(new[] { (12, 20) }, visibleOnly.Targets.Select(t => (t.EquipmentType, t.Item.Index)).ToArray());
    }

    [Fact]
    public void A_transmog_that_keeps_its_color_is_sent_its_own_index_after_the_covered_piece()
    {
        // Already that color: the covered chest is dyed, then the transmog is sent its current index back.
        var already = DyePlanner.Plan(Slots((DyeEquipmentTypes.Chest, Dyeable(10, active: 0), Dyeable(20, active: 2))),
            new DyeColor(2, null));
        Assert.Equal(new[] { (1, 10, 2, false), (12, 20, 2, true) },
            already.Targets.Select(t => (t.EquipmentType, t.Item.Index, (int)t.NewIndex, t.Restore)).ToArray());
        Assert.Contains(already.Skipped, s => s.EquipmentType == DyeEquipmentTypes.CosmeticChest && s.Reason == DyeSkipReason.AlreadyThatColor);
        Assert.Contains("chest (transmog) kept at 2", already.Describe(), StringComparison.Ordinal);

        // Index past the transmog's colors: the gear still takes it; the transmog keeps index 1.
        var shortList = DyePlanner.Plan(Slots((DyeEquipmentTypes.Legs, Dyeable(11, colors: 20), Dyeable(21, active: 1, colors: 4))),
            new DyeColor(9, null));
        Assert.Equal(new[] { (5, 9, false), (14, 1, true) },
            shortList.Targets.Select(t => (t.EquipmentType, (int)t.NewIndex, t.Restore)).ToArray());

        // Palette missing on the transmog (palette matching on): restored too.
        var source = new[] { new DyePaletteKey(111, 0, 10) };
        var other = new[] { new DyePaletteKey(222, 0, 10) };
        var missing = DyePlanner.Plan(Slots((DyeEquipmentTypes.Gloves, Dyeable(12, colors: 10, palettes: source),
            Dyeable(22, active: 3, colors: 10, palettes: other))), new DyeColor(5, source));
        Assert.Equal(new[] { (9, 5, false), (16, 3, true) },
            missing.Targets.Select(t => (t.EquipmentType, (int)t.NewIndex, t.Restore)).ToArray());
    }

    [Fact]
    public void A_covered_piece_is_left_alone_when_the_transmog_could_not_be_put_back()
    {
        // The transmog has no colors: no event can restore its shown color.
        var plain = DyePlanner.Plan(Slots((DyeEquipmentTypes.Cloak, Dyeable(13), Plain(23))), new DyeColor(4, null));
        Assert.Empty(plain.Targets);
        Assert.Contains(plain.Skipped, s => s.EquipmentType == DyeEquipmentTypes.Cloak && s.Reason == DyeSkipReason.TransmogWouldChange);
        Assert.Contains(plain.Skipped, s => s.EquipmentType == DyeEquipmentTypes.CosmeticCloak && s.Reason == DyeSkipReason.NotDyeable);

        // The transmog is hidden and keeps its color: a restore would show it again.
        var hidden = DyePlanner.Plan(Slots((DyeEquipmentTypes.Chest, Dyeable(10), Dyeable(20, active: 4, hidden: true))),
            new DyeColor(4, null));
        Assert.Empty(hidden.Targets);
        Assert.Contains(hidden.Skipped, s => s.EquipmentType == DyeEquipmentTypes.Chest && s.Reason == DyeSkipReason.TransmogWouldChange);

        // A hidden transmog that is dyed anyway (its own swatch event shows it, as the game's click does).
        var dyed = DyePlanner.Plan(Slots((DyeEquipmentTypes.Chest, Dyeable(10), Dyeable(20, active: 1, hidden: true))),
            new DyeColor(4, null));
        Assert.Equal(new[] { (1, 10, 4, false), (12, 20, 4, false) },
            dyed.Targets.Select(t => (t.EquipmentType, t.Item.Index, (int)t.NewIndex, t.Restore)).ToArray());
    }

    [Fact]
    public void A_hidden_gear_piece_under_a_transmog_is_left_alone_and_the_transmog_is_still_dyed()
    {
        // The player hid the chest gear, then put a transmog on: the gear's own event would un-hide it.
        var plan = DyePlanner.Plan(Slots((DyeEquipmentTypes.Chest, Dyeable(10, hidden: true), Dyeable(20, active: 1))),
            new DyeColor(4, null));
        var target = Assert.Single(plan.Targets);
        Assert.Equal((DyeEquipmentTypes.CosmeticChest, 20, 4, false), (target.EquipmentType, target.Item.Index, (int)target.NewIndex, target.Restore));
        Assert.Contains(plan.Skipped, s => s.EquipmentType == DyeEquipmentTypes.Chest && s.Reason == DyeSkipReason.CoveredHidden);

        // Hidden gear and a transmog that keeps its color: nothing at all.
        var none = DyePlanner.Plan(Slots((DyeEquipmentTypes.Chest, Dyeable(10, hidden: true), Dyeable(20, active: 4))),
            new DyeColor(4, null));
        Assert.Empty(none.Targets);
        Assert.Contains(none.Skipped, s => s.EquipmentType == DyeEquipmentTypes.Chest && s.Reason == DyeSkipReason.CoveredHidden);
    }

    [Fact]
    public void A_gear_piece_skipped_for_its_own_reason_leaves_only_the_transmog_event()
    {
        // Index past the gear's colors.
        var range = DyePlanner.Plan(Slots((DyeEquipmentTypes.Legs, Dyeable(11, colors: 4), Dyeable(21, active: 0, colors: 20))),
            new DyeColor(9, null));
        var t1 = Assert.Single(range.Targets);
        Assert.Equal((DyeEquipmentTypes.CosmeticLegs, 9, false), (t1.EquipmentType, (int)t1.NewIndex, t1.Restore));
        Assert.Contains(range.Skipped, s => s.EquipmentType == DyeEquipmentTypes.Legs && s.Reason == DyeSkipReason.IndexOutOfRange);

        // Palette missing on the gear (palette matching on).
        var source = new[] { new DyePaletteKey(111, 0, 10) };
        var other = new[] { new DyePaletteKey(222, 0, 10) };
        var missing = DyePlanner.Plan(Slots((DyeEquipmentTypes.Gloves, Dyeable(12, colors: 10, palettes: other),
            Dyeable(22, active: 0, colors: 10, palettes: source))), new DyeColor(5, source));
        var t2 = Assert.Single(missing.Targets);
        Assert.Equal((DyeEquipmentTypes.CosmeticGloves, 5, false), (t2.EquipmentType, (int)t2.NewIndex, t2.Restore));
        Assert.Contains(missing.Skipped, s => s.EquipmentType == DyeEquipmentTypes.Gloves && s.Reason == DyeSkipReason.PaletteMissing);
    }

    [Fact]
    public void A_hidden_transmog_without_covered_pieces_follows_the_games_click()
    {
        // Dyed: the transmog's own event, as a click on that swatch would send.
        var dyed = DyePlanner.Plan(Slots((DyeEquipmentTypes.Chest, Dyeable(10), Dyeable(20, active: 1, hidden: true))),
            new DyeColor(4, null), includeCovered: false);
        var target = Assert.Single(dyed.Targets);
        Assert.Equal((DyeEquipmentTypes.CosmeticChest, 20, 4, false), (target.EquipmentType, target.Item.Index, (int)target.NewIndex, target.Restore));

        // Already that color: nothing, and no restore (no gear event went first).
        var same = DyePlanner.Plan(Slots((DyeEquipmentTypes.Chest, Dyeable(10), Dyeable(20, active: 4, hidden: true))),
            new DyeColor(4, null), includeCovered: false);
        Assert.Empty(same.Targets);
    }

    [Fact]
    public void A_transmog_still_waiting_for_sync_counts_as_covering_its_slot()
    {
        // The server already sees the transmog (SlotId set); a gear event would re-key it from the gear's palettes.
        foreach (bool includeCovered in new[] { true, false })
        {
            var plan = DyePlanner.Plan(Slots((DyeEquipmentTypes.Chest, Dyeable(10), DyeItem.Pending())),
                new DyeColor(4, null), includeCovered);
            Assert.Empty(plan.Targets);
            Assert.Contains(plan.Skipped, s => s.EquipmentType == DyeEquipmentTypes.CosmeticChest && s.Reason == DyeSkipReason.TransmogNotSynced);
        }

        Assert.True(DyeItem.Pending().Slotted);
        Assert.False(DyeItem.Pending().Present);
        Assert.False(DyeItem.None.Slotted);
        Assert.True(Dyeable(1).Slotted);
    }

    [Fact]
    public void A_covered_piece_that_needs_nothing_sends_only_the_transmog()
    {
        var plan = DyePlanner.Plan(Slots((DyeEquipmentTypes.Footgear, Dyeable(14, active: 6), Dyeable(24, active: 1))),
            new DyeColor(6, null));
        var target = Assert.Single(plan.Targets);
        Assert.Equal((DyeEquipmentTypes.CosmeticFootgear, false), (target.EquipmentType, target.Restore));
        Assert.Contains(plan.Skipped, s => s.EquipmentType == DyeEquipmentTypes.Footgear && s.Reason == DyeSkipReason.AlreadyThatColor);

        // Both already that color: nothing at all is sent.
        var none = DyePlanner.Plan(Slots((DyeEquipmentTypes.Footgear, Dyeable(14, active: 6), Dyeable(24, active: 6))),
            new DyeColor(6, null));
        Assert.Empty(none.Targets);
    }

    [Fact]
    public void A_transmog_over_an_empty_stat_slot_is_still_dyed()
    {
        var slots = Slots((DyeEquipmentTypes.Cloak, DyeItem.None, Dyeable(30)));
        var plan = DyePlanner.Plan(slots, new DyeColor(1, null));
        var target = Assert.Single(plan.Targets);
        Assert.Equal(DyeEquipmentTypes.CosmeticCloak, target.EquipmentType);
    }

    [Fact]
    public void Choose_follows_the_picker_rule()
    {
        Assert.True(DyePlanner.Choose(new DyeSlot(5, Dyeable(1), Dyeable(2)), out int t1, out var i1));
        Assert.Equal((14, 2), (t1, i1.Index));
        Assert.True(DyePlanner.Choose(new DyeSlot(5, Dyeable(1), DyeItem.None), out int t2, out var i2));
        Assert.Equal((5, 1), (t2, i2.Index));
        Assert.False(DyePlanner.Choose(new DyeSlot(5, DyeItem.None, DyeItem.None), out _, out _));
    }

    [Fact]
    public void Indexes_the_server_would_refuse_and_unchanged_pieces_are_skipped()
    {
        var slots = Slots(
            (DyeEquipmentTypes.Chest, Dyeable(10, colors: 4), DyeItem.None),
            (DyeEquipmentTypes.Legs, Dyeable(11, active: 6), DyeItem.None),
            (DyeEquipmentTypes.Footgear, Dyeable(12, colors: 7), DyeItem.None));
        var plan = DyePlanner.Plan(slots, new DyeColor(6, null));

        Assert.Equal(new[] { DyeEquipmentTypes.Footgear }, plan.Targets.Select(t => t.EquipmentType).ToArray());
        Assert.Contains(plan.Skipped, s => s.EquipmentType == DyeEquipmentTypes.Chest && s.Reason == DyeSkipReason.IndexOutOfRange);
        Assert.Contains(plan.Skipped, s => s.EquipmentType == DyeEquipmentTypes.Legs && s.Reason == DyeSkipReason.AlreadyThatColor);
    }

    [Fact]
    public void With_palettes_a_color_keeps_its_palette_across_pieces()
    {
        const int basic = 111, dlc = 222;
        var source = new[] { new DyePaletteKey(basic, 0, 10), new DyePaletteKey(dlc, 10, 5) };
        var shifted = new[] { new DyePaletteKey(dlc, 0, 5), new DyePaletteKey(basic, 5, 10) };
        var basicOnly = new[] { new DyePaletteKey(basic, 0, 10) };
        var slots = Slots(
            (DyeEquipmentTypes.Chest, Dyeable(10, colors: 15, palettes: source), DyeItem.None),
            (DyeEquipmentTypes.Legs, Dyeable(11, colors: 15, palettes: shifted), DyeItem.None),
            (DyeEquipmentTypes.Gloves, Dyeable(12, colors: 10, palettes: basicOnly), DyeItem.None),
            (DyeEquipmentTypes.Cloak, Dyeable(13, colors: 15), DyeItem.None)); // palettes unread: raw index

        // Index 12 on the chest is the DLC palette's third color.
        var plan = DyePlanner.Plan(slots, new DyeColor(12, source));
        Assert.True(plan.MatchedByPalette);
        var byType = plan.Targets.ToDictionary(t => t.EquipmentType, t => (int)t.NewIndex);
        Assert.Equal(12, byType[DyeEquipmentTypes.Chest]);
        Assert.Equal(2, byType[DyeEquipmentTypes.Legs]);
        Assert.Equal(12, byType[DyeEquipmentTypes.Cloak]);
        Assert.False(byType.ContainsKey(DyeEquipmentTypes.Gloves));
        Assert.Contains(plan.Skipped, s => s.EquipmentType == DyeEquipmentTypes.Gloves && s.Reason == DyeSkipReason.PaletteMissing);
    }

    [Fact]
    public void A_target_whose_palettes_could_not_be_read_gets_the_raw_index()
    {
        var source = new[] { new DyePaletteKey(111, 0, 10), new DyePaletteKey(222, 10, 5) };
        var slots = Slots(
            (DyeEquipmentTypes.Chest, Dyeable(10, colors: 15, palettes: source), DyeItem.None),
            (DyeEquipmentTypes.Legs, Dyeable(11, colors: 15, palettes: null), DyeItem.None));
        var plan = DyePlanner.Plan(slots, new DyeColor(12, source));
        var legs = Assert.Single(plan.Targets, t => t.EquipmentType == DyeEquipmentTypes.Legs);
        Assert.Equal(12, legs.NewIndex);
    }

    [Fact]
    public void A_new_press_after_a_cancel_in_the_same_frame_starts_cleanly()
    {
        var m = new DyeHoldMachine();
        m.Press(1, true, true, true, 1.0, 0.0);
        m.Release(1);
        Assert.True(m.Press(2, true, true, true, 1.0, 0.1));
        var step = m.Tick(0.6, true, true);
        Assert.Equal(HoldPhase.Charging, step.Phase);
        Assert.Equal(2, m.Swatch);
        Assert.True(m.Tick(1.1, true, true).Fire);
    }

    [Fact]
    public void Locate_finds_the_palette_and_offset_of_an_index()
    {
        var keys = new[] { new DyePaletteKey(1, 0, 3), new DyePaletteKey(2, 3, 4) };
        Assert.True(DyePlanner.TryLocate(keys, 4, out var key, out int offset));
        Assert.Equal((2, 1), (key.AssetGuid, offset));
        Assert.False(DyePlanner.TryLocate(keys, 7, out _, out _));
    }

    [Fact]
    public void Describe_names_targets_and_skips()
    {
        var plan = DyePlanner.Plan(Slots((DyeEquipmentTypes.Chest, Dyeable(1), DyeItem.None)), new DyeColor(3, null));
        string line = plan.Describe();
        Assert.Contains("chest->3", line, StringComparison.Ordinal);
        Assert.Contains("boots skipped (Empty)", line, StringComparison.Ordinal);
    }

    // ---- ring math and blob bounds ---------------------------------------------------------------

    [Fact]
    public void Drawn_ring_is_opaque_in_the_band_and_clear_at_the_center_and_corner()
    {
        int n = HoldRingMath.TextureSize;
        Assert.Equal(0f, HoldRingMath.Alpha(n / 2, n / 2, n));
        Assert.Equal(0f, HoldRingMath.Alpha(0, 0, n));
        float mid = (n / 2f - 1f) * (1f + HoldRingMath.InnerFraction) / 2f;
        Assert.Equal(1f, HoldRingMath.Alpha(n / 2 + (int)mid, n / 2, n), 3);
        Assert.Equal(0f, HoldRingMath.Alpha(0, 0, 0));
    }

    [Fact]
    public void Pulse_grows_and_fades_then_ends()
    {
        Assert.Equal((1f, 1f, false), HoldRingMath.Pulse(0));
        var half = HoldRingMath.Pulse(HoldRingMath.PulseSeconds / 2);
        Assert.InRange(half.Scale, 1.1f, 1.25f);
        Assert.InRange(half.Alpha, 0.4f, 0.6f);
        Assert.True(HoldRingMath.Pulse(HoldRingMath.PulseSeconds).Done);
    }

    [Theory]
    [InlineData(64, 0, 8, 3, 8, true)]    // header + three rows inside the blob
    [InlineData(31, 0, 8, 3, 8, false)]   // one byte short
    [InlineData(64, 0, 8, 0, 8, true)]    // an empty list is fine
    [InlineData(64, 0, 4, 1, 8, false)]   // rows cannot overlap the {offset, length} header
    [InlineData(4096, 0, 8, 65, 8, false)] // more palettes than any item has: a bad read
    [InlineData(0, 0, 8, 1, 8, false)]
    [InlineData(64, 0, 8, -1, 8, false)]
    public void Palette_array_must_fit_its_blob(int blobLength, int arrayAt, int relative, int count, int stride, bool fits) =>
        Assert.Equal(fits, DyeRing.PaletteArrayFits(blobLength, arrayAt, relative, count, stride));

    // ---- settings --------------------------------------------------------------------------------

    private sealed class ShimBinder : IDyeAllBinder
    {
        private readonly ConfigFile _file;

        internal ShimBinder(ConfigFile file) => _file = file;

        public ConfigEntry<bool> Flag(string section, string key, bool value, string description) =>
            _file.Bind(section, key, value, description);
    }

    private DyeAllEntries Entries() =>
        DyeAllEntries.Bind(new ShimBinder(new ConfigFile(Path.Combine(_dir, "dyeall.cfg"), saveOnInit: true)));

    [Fact]
    public void Settings_default_to_on_one_second_and_palette_matching_off()
    {
        var e = Entries();
        Assert.True(e.Enabled.Value);
        Assert.Equal(1.0, e.HoldTime);
        Assert.False(e.MatchPalettes.Value); // experimental memory read stays off by default
    }

    [Fact]
    public void Settings_live_in_the_DyeAll_section_of_Cores_ui_cfg()
    {
        var file = new ConfigFile(Path.Combine(_dir, "progenitor.ui.cfg"), saveOnInit: true);
        DyeAllEntries.Bind(new ShimBinder(file));
        foreach (string key in new[]
                 {
                     DyeAllEntries.EnabledKey, DyeAllEntries.MatchPalettesKey, DyeAllEntries.LegacyImportedKey,
                 })
            Assert.True(file.ContainsKey(new ConfigDefinition("DyeAll", key)), key);
    }

    [Fact]
    public void Progenitors_page_draws_only_the_switch_under_Equipment_and_keeps_the_rest_in_the_file()
    {
        var page = new SettingsPageBuilder(SettingPlaces.PluginId);
        ProgenitorPage.BuildYourSettings(page);
        var titles = page.Sections.Select(s => s.Title).ToArray();
        int equipment = Array.IndexOf(titles, ProgenitorPage.EquipmentTitle);
        Assert.True(equipment > Array.IndexOf(titles, ProgenitorPage.BuildMenuTitle));
        Assert.True(equipment < Array.IndexOf(titles, ProgenitorPage.ChatTitle));
        var refs = page.Sections[equipment].Items.Select(i => i.RefSection + "/" + i.RefKey).ToArray();
        Assert.Equal(new[] { "DyeAll/Enabled" }, refs);
        Assert.Equal("Dye all on hold", page.Sections[equipment].Items[0].Options.Label);
        Assert.Equal(SettingPlaces.Equipment, SettingPlaces.Find("progenitor/DyeAll/Enabled")!.Home);
        Assert.Null(SettingPlaces.Find("progenitor/DyeAll/IncludeCovered"));
        Assert.Null(SettingPlaces.Find("progenitor/DyeAll/HoldSeconds"));
        foreach (string key in new[] { "MatchPalettes", "LegacySettingsImported" })
            Assert.Equal(SettingPlaces.CfgOnly, SettingPlaces.Find("progenitor/DyeAll/" + key)!.Home);
        Assert.Null(SettingPlaces.Find("progenitor/DyeAll/TestColorIndex"));
        Assert.Null(SettingPlaces.Find("progenitor/Keybinds/DyeAllTestColor"));
    }

    // ---- the former Dye all mod -------------------------------------------------------------------

    private const string OldCfg =
        "## Settings file was created by plugin Dye all v1.0.0\n"
        + "## Plugin GUID: progenitor.dyeall\r\n\r\n"
        + "[Dye all]\r\n\r\n"
        + "## Hold a color swatch in Character > Equipment to dye every worn piece.\r\n"
        + "# Setting type: Boolean\r\n# Default value: true\r\n"
        + "Enabled = false\r\n\r\n"
        + "# Acceptable value range: From 0.3 to 5\r\n"
        + "HoldSeconds = 2.5\r\n\r\n"
        + "IncludeCovered = false\r\n"
        + "MatchPalettes = true\r\n\r\n"
        + "[Keybinds]\r\n\r\n"
        + "DyeAllTestColor = F7 + Ctrl + Shift\r\n\r\n"
        + "[Test]\r\n\r\n"
        + "ColorIndex = 12\r\n";

    [Fact]
    public void The_old_cfg_is_parsed_by_section_and_key_and_its_keybind_is_left_behind()
    {
        var v = DyeAllLegacy.Parse(OldCfg);
        Assert.False(v.Enabled);
        Assert.True(v.MatchPalettes);
        Assert.Equal(2, v.Count); // hold, covered, the test color and the old keybind are not settings anymore
    }

    [Fact]
    public void A_bad_or_missing_old_value_is_left_out_and_the_parse_ignores_the_locale()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var v = DyeAllLegacy.Parse("[Dye all]\nEnabled = maybe\nHoldSeconds = 1.5\nMatchPalettes = true\n[Other]\nIncludeCovered = false\n");
            Assert.Null(v.Enabled);
            Assert.True(v.MatchPalettes);
            Assert.Equal(1, v.Count); // HoldSeconds and a covered flag in another section are ignored
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }

        Assert.Equal(0, DyeAllLegacy.Parse(null).Count);
        Assert.Equal(0, DyeAllLegacy.Parse("").Count);
    }

    [Fact]
    public void Import_copies_once_clamps_and_never_changes_the_old_file()
    {
        string old = Path.Combine(_dir, DyeAllLegacy.CfgFileName);
        File.WriteAllText(old, OldCfg.Replace("HoldSeconds = 2.5", "HoldSeconds = 60").Replace("ColorIndex = 12", "ColorIndex = 999"));
        var stamp = File.GetLastWriteTimeUtc(old);
        string text = File.ReadAllText(old);
        var e = Entries();

        string? line = DyeAllLegacy.ImportOnce(_dir, e);
        Assert.Contains("copied 2 setting(s)", line);
        Assert.False(e.Enabled.Value);
        Assert.Equal(DyeHoldMachine.DefaultHoldSeconds, e.HoldTime);
        Assert.True(e.MatchPalettes.Value);
        Assert.True(e.LegacyImported.Value);

        // Once: a later change of Core's own value survives the next install.
        e.Enabled.Value = true;
        Assert.Null(DyeAllLegacy.ImportOnce(_dir, e));
        Assert.True(e.Enabled.Value);

        Assert.True(File.Exists(old));
        Assert.Equal(text, File.ReadAllText(old));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(old));
    }

    [Fact]
    public void Import_without_an_old_file_keeps_the_defaults_and_marks_itself_done()
    {
        var e = Entries();
        string? line = DyeAllLegacy.ImportOnce(_dir, e);
        Assert.Contains("no " + DyeAllLegacy.CfgFileName, line);
        Assert.True(e.Enabled.Value);
        Assert.Equal(DyeHoldMachine.DefaultHoldSeconds, e.HoldTime);
        Assert.True(e.LegacyImported.Value);
        Assert.False(File.Exists(Path.Combine(_dir, DyeAllLegacy.CfgFileName)));
    }

    [Theory]
    [InlineData(true, true, false, true)]    // the registry saw it load: stand down
    [InlineData(true, false, true, false)]   // turned off or unloaded (the registry knows): Core's copy runs
    [InlineData(false, false, true, true)]   // not in the registry yet, but the chainloader has it: stand down
    [InlineData(false, false, false, false)] // not installed: Core's copy runs
    public void Core_stands_down_while_the_old_mod_runs(bool registryKnows, bool registryRunning, bool chainloaderHas, bool standDown) =>
        Assert.Equal(standDown, DyeAllLegacy.OldModRunning(registryKnows, registryRunning, chainloaderHas));

    // ---- when the picker is looked for --------------------------------------------------------------

    [Theory]
    [InlineData(false, false, true, 1, (int)DyeScanStep.Release)]   // switched off: release every watch, search nothing
    [InlineData(true, true, true, 1, (int)DyeScanStep.Release)]     // standing down for the former mod
    [InlineData(true, false, false, 0, (int)DyeScanStep.Pause)]     // title screen or loading: no client world
    [InlineData(true, false, true, 0, (int)DyeScanStep.Pause)]      // in game, no HUD menu open
    [InlineData(true, false, true, 1, (int)DyeScanStep.Scan)]       // the character menu (the picker's home)
    [InlineData(true, false, true, 3, (int)DyeScanStep.Scan)]       // a container menu: the inventory sub-menu is shared
    [InlineData(true, false, true, -1, (int)DyeScanStep.Scan)]      // menu type unreadable: keep working (logged once)
    public void The_picker_is_searched_for_only_while_on_and_a_menu_is_open(
        bool enabled, bool standingDown, bool world, int menu, int expected) =>
        Assert.Equal(expected, (int)DyeScanGate.Decide(enabled, standingDown, world, menu));

    // 2026-10-08 regression: the first container found was the interaction menu's (inactive while Character >
    // Equipment shows), and because one was known the search never ran again, so no swatch was ever watched.
    [Theory]
    [InlineData(0, 10.0, 8.0, true)]    // nothing known (or every known one is gone): search
    [InlineData(0, 10.0, 12.0, false)]  // ... but at most every two seconds
    [InlineData(1, 10.0, 8.0, false)]   // a known picker is showing: list its cells, no scene search
    [InlineData(2, 10.0, 8.0, false)]
    public void The_scene_is_searched_again_while_no_known_picker_is_active(int knownActive, double now, double nextFind, bool find) =>
        Assert.Equal(find, DyePickerSearch.ShouldFind(knownActive, now, nextFind));

    [Fact]
    public void Knowing_only_an_inactive_picker_does_not_stop_the_search()
    {
        // Two containers known, the interaction menu's and a stale one, neither active: the character menu's must still be looked for.
        Assert.True(DyePickerSearch.ShouldFind(knownActive: 0, now: 100.0, nextFind: 98.0));
    }

    [Fact]
    public void The_old_mods_page_says_it_is_part_of_Progenitor_now()
    {
        Assert.Equal("Now part of Progenitor; you can uninstall this mod", DyeAllLegacy.NoteFor("progenitor.dyeall"));
        Assert.Equal(DyeAllLegacy.ProvenanceNote, DyeAllLegacy.NoteFor(" Progenitor.DyeAll "));
        Assert.Null(DyeAllLegacy.NoteFor("progenitor"));
        Assert.Null(DyeAllLegacy.NoteFor(null));
        Assert.Equal("Now part of Progenitor; you can uninstall this mod.", ModPageCopy.ProvenanceBlock(DyeAllLegacy.NoteFor("progenitor.dyeall"), null, ""));
    }

    // ---- palette hint ---------------------------------------------------------------------------------

    [Fact]
    public void Tip_reads_like_the_games_hold_entries()
    {
        // A hold is always 1 s (PR #35), so the tip never names a time.
        Assert.Equal("Hold", DyeHint.Interaction);
        Assert.Equal("Set All", DyeHint.Action);
        Assert.Equal("Hold [LMB] Set All", DyeHint.Text);
    }

    [Theory]
    [InlineData(false, true, true, true, true)]    // loaded, enabled, palette with a color: shown
    [InlineData(false, false, true, true, false)]  // feature off
    [InlineData(false, true, false, true, false)]  // panel closed / palette hidden
    [InlineData(false, true, true, false, false)]  // palette shows no color swatch (no slot selected)
    [InlineData(true, true, true, true, false)]    // unloaded (or Core swap): never shown again
    public void Hint_shows_only_while_loaded_enabled_and_the_palette_shows_a_color(
        bool stopped, bool enabled, bool paletteShown, bool hasColor, bool expected) =>
        Assert.Equal(expected, DyeHint.ShouldShow(stopped, enabled, paletteShown, hasColor));

    // The inventory key-hint row as read live on 2026-10-08 (pooled entries in sibling order).
    private static DyeHintDonor[] Row(bool setColorActive, bool editActive) => new[]
    {
        new DyeHintDonor(false, "Hold", true, "X"),                       // console-only entry
        new DyeHintDonor(setColorActive, "Set Color", true, "Icon_LMB1"),
        new DyeHintDonor(true, "Drop", true, "KeyboardGlyphs_Smaller_32"),
        new DyeHintDonor(editActive, "Edit", true, "Icon_LMB1"),
        new DyeHintDonor(true, "Sort", false, "Icon_LMB1"),
    };

    [Fact]
    public void Tip_is_copied_from_the_games_set_color_entry()
    {
        Assert.Equal(1, DyeHint.PickDonor(Row(setColorActive: true, editActive: true)));
        Assert.Equal(0, DyeHint.PickDonor(new[] { new DyeHintDonor(true, " set color ", true, "Icon_LMB1") }));
    }

    [Fact]
    public void Tip_falls_back_to_a_shown_mouse_entry_then_to_a_hidden_one()
    {
        // Another language: no "Set Color" caption, so a shown entry with the mouse glyph lends the style.
        var row = new[]
        {
            new DyeHintDonor(true, "Ablegen", true, "KeyboardGlyphs_Smaller_32"),
            new DyeHintDonor(true, "Farbe festlegen", true, "Icon_LMB1"),
        };
        Assert.Equal(1, DyeHint.PickDonor(row));
        Assert.Equal(1, DyeHint.PickDonor(Row(setColorActive: false, editActive: false))); // hidden Set Color beats hidden others
        Assert.Equal(3, DyeHint.PickDonor(Row(setColorActive: false, editActive: true)));  // a shown mouse entry beats a hidden one
    }

    [Fact]
    public void Tip_never_copies_an_entry_without_the_mouse_glyph()
    {
        Assert.Equal(-1, DyeHint.PickDonor(new[] { new DyeHintDonor(true, "Drop", true, "KeyboardGlyphs_Smaller_32") }));
        Assert.Equal(-1, DyeHint.PickDonor(Array.Empty<DyeHintDonor>()));
        Assert.Equal(-1, DyeHint.PickDonor(null!));
    }

    [Theory]
    [InlineData(1, 2, 2)]  // already right after the donor
    [InlineData(1, 5, 2)]  // after it, further down: move up to just after it
    [InlineData(3, 0, 3)]  // before it: moving it out shifts the donor left, so the donor's old index is right after it
    public void Tip_sits_right_after_its_donor(int donor, int tip, int expected) =>
        Assert.Equal(expected, DyeHint.SiblingAfter(donor, tip));

    // ---- Core pointer-watch registry ---------------------------------------------------------------

    private sealed class Counted : IDisposable
    {
        internal int Disposed;

        public void Dispose() => Disposed++;
    }

    [Fact]
    public void Pointer_watches_are_swept_by_owner_and_on_clear()
    {
        UiPointerWatchRegistry.Clear();
        var a = new object();
        var b = new object();
        var wa = new Counted();
        var wb = new Counted();
        var wb2 = new Counted();
        UiPointerWatchRegistry.Add(a, wa);
        UiPointerWatchRegistry.Add(b, wb);
        UiPointerWatchRegistry.Add(b, wb2);

        Assert.Equal(1, UiPointerWatchRegistry.RemoveAll(a));
        Assert.Equal(1, wa.Disposed);
        Assert.Equal(0, wb.Disposed);

        UiPointerWatchRegistry.Forget(wb2);
        Assert.Equal(1, UiPointerWatchRegistry.Clear());
        Assert.Equal(1, wb.Disposed);
        Assert.Equal(0, wb2.Disposed);
        Assert.Equal(0, UiPointerWatchRegistry.Count);
    }

    [Fact]
    public void An_unowned_pointer_watch_is_swept_with_the_plugin_that_declared_its_handler()
    {
        // PINS1: only the unload sweep of a reloadable plugin matches by the handler's assembly.
        using var plugin = UnloadingPluginStandIn.Load();
        UiPointerWatchRegistry.Clear();
        var unowned = new Counted();
        var library = new Counted();
        var coreHandler = new Counted();
        UiPointerWatchRegistry.Add(null, unowned, plugin.Callback);
        UiPointerWatchRegistry.Add(null, library, new Action(GC.Collect));
        UiPointerWatchRegistry.Add(null, coreHandler, new Action(() => { })); // declared in "Core": never matched by assembly
        Assert.Null(UiPointerWatchRegistry.SourceName(new Action(() => { })));

        // A live RemoveAll (no sweep) keeps the plugin's own unowned watch.
        Assert.Equal(0, UiPointerWatchRegistry.RemoveAll(plugin.Owner));
        // A library owner never matches a library callback, even inside a sweep.
        using (PluginDelegateSource.UnloadSweep(this))
            Assert.Equal(0, UiPointerWatchRegistry.RemoveAll(this));
        var bcl = new object();
        using (PluginDelegateSource.UnloadSweep(bcl))
            Assert.Equal(0, UiPointerWatchRegistry.RemoveAll(bcl));

        using (PluginDelegateSource.UnloadSweep(plugin.Owner))
            Assert.Equal(1, UiPointerWatchRegistry.RemoveAll(plugin.Owner));
        Assert.Equal(1, unowned.Disposed);
        Assert.Equal(0, library.Disposed);
        Assert.Equal(0, coreHandler.Disposed);
        Assert.Equal(2, UiPointerWatchRegistry.Clear());
    }

    [Fact]
    public void Pointer_event_reports_kind_and_primary_button()
    {
        var e = new UiPointerEvent(UiPointerEventKind.Down, primaryHeld: true);
        Assert.Equal(UiPointerEventKind.Down, e.Kind);
        Assert.True(e.PrimaryHeld);
        Assert.Equal("Down (primary)", e.ToString());
    }
}
