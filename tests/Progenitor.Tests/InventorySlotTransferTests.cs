using System.Collections.Generic;
using System.Linq;
using Progenitor.Inventory;
using Progenitor.Sample;
using Xunit;
using static Progenitor.Tests.InventoryEngineTests;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of <c>ctx.Inventory.TransferSlot</c> and the source slot filter on
/// <c>Transfer</c> / <c>TransferAll</c>: validation, the conservation guarantees they share with
/// <c>Transfer</c>, and that skipped slots (a player's action bar) are never drawn from.
/// </summary>
public class InventorySlotTransferTests
{
    // ---- the filter ----------------------------------------------------------------------------

    [Fact]
    public void Filter_default_allows_every_slot_and_skip_action_bar_skips_zero_to_seven()
    {
        Assert.True(InventorySlotFilter.All.IsAll);
        Assert.True(default(InventorySlotFilter).Allows(0));
        Assert.True(InventorySlotFilter.All.Allows(35));

        var bar = InventorySlotFilter.SkipActionBar;
        Assert.False(bar.IsAll);
        Assert.Equal(8, InventorySlotFilter.ActionBarSlots);
        for (int s = 0; s < 8; s++) Assert.False(bar.Allows(s));
        Assert.True(bar.Allows(8));
        Assert.True(bar.Allows(35));
        Assert.Equal("skip 0..7", bar.ToString());
        Assert.Equal(InventorySlotFilter.SkipSlots(0, 8), bar);
    }

    [Fact]
    public void Filter_skip_slots_ignores_empty_or_negative_ranges()
    {
        Assert.True(InventorySlotFilter.SkipSlots(3, 0).IsAll);
        Assert.True(InventorySlotFilter.SkipSlots(-1, 4).IsAll);
        Assert.True(InventorySlotFilter.SkipSlots(3, -2).IsAll);
        var mid = InventorySlotFilter.SkipSlots(10, 2);
        Assert.True(mid.Allows(9));
        Assert.False(mid.Allows(10));
        Assert.False(mid.Allows(11));
        Assert.True(mid.Allows(12));
        Assert.True(InventorySlotFilter.SkipSlots(5, int.MaxValue).Allows(4));
        Assert.False(InventorySlotFilter.SkipSlots(5, int.MaxValue).Allows(int.MaxValue));
        Assert.Equal("all", InventorySlotFilter.All.ToString());
    }

    // ---- TransferSlot --------------------------------------------------------------------------

    [Fact]
    public void TransferSlot_moves_only_from_the_named_slot_and_conserves_the_total()
    {
        var b = NewBackend();
        var bag = b.AddContainer(20, 12);
        var chest = b.AddContainer(21, 4);
        b.Put(bag, 2, IronOre, 30);   // action bar slot, untouched
        b.Put(bag, 9, IronOre, 40);
        var r = new InventoryEngine(b).TransferSlot(bag, 9, chest, 25);
        Assert.Equal(InventoryOutcome.Complete, r.Outcome);
        Assert.Equal(IronOre, r.Item);
        Assert.Equal(25, r.Done);
        Assert.Equal(70, r.SourceBefore);   // whole-container counts
        Assert.Equal(45, r.SourceAfter);
        Assert.Equal(0, r.TargetBefore);
        Assert.Equal(25, r.TargetAfter);
        Assert.Equal(r.SourceBefore + r.TargetBefore, r.SourceAfter + r.TargetAfter);
        var slots = b.Slots(bag);
        Assert.Equal(30, slots[2].Amount);
        Assert.Equal(15, slots[9].Amount);
        Assert.Equal(1, r.Steps);
    }

    [Fact]
    public void TransferSlot_asking_more_than_the_slot_holds_moves_the_slot_and_reports_partial()
    {
        var b = NewBackend();
        var bag = b.AddContainer(20, 12);
        var chest = b.AddContainer(21, 4);
        b.Put(bag, 3, IronOre, 10);
        b.Put(bag, 4, IronOre, 10);
        var r = new InventoryEngine(b).TransferSlot(bag, 3, chest, 50);
        Assert.Equal(InventoryOutcome.Partial, r.Outcome);
        Assert.Equal(10, r.Done);
        Assert.Equal(10, b.Slots(bag)[4].Amount); // the other stack of the same item stays
        Assert.Equal(20, b.CountOf(bag, IronOre) + b.CountOf(chest, IronOre));
    }

    [Fact]
    public void TransferSlot_validates_amount_slot_range_empty_slot_and_same_inventory()
    {
        var b = NewBackend();
        var bag = b.AddContainer(20, 12);
        var chest = b.AddContainer(21, 4);
        b.Put(bag, 0, IronOre, 10);
        var e = new InventoryEngine(b);

        Assert.Equal(InventoryOutcome.Refused, e.TransferSlot(bag, 0, chest, 0).Outcome);
        Assert.Equal(InventoryOutcome.Refused, e.TransferSlot(bag, 0, chest, -5).Outcome);

        var low = e.TransferSlot(bag, -1, chest, 1);
        Assert.Equal(InventoryOutcome.Refused, low.Outcome);
        Assert.Contains("out of range", low.Reason);
        var high = e.TransferSlot(bag, 12, chest, 1);
        Assert.Equal(InventoryOutcome.Refused, high.Outcome);
        Assert.Contains("12 slots", high.Reason);

        var empty = e.TransferSlot(bag, 5, chest, 1);
        Assert.Equal(InventoryOutcome.Nothing, empty.Outcome);
        Assert.Equal(0, empty.Item);
        Assert.Contains("empty", empty.Reason);

        Assert.Equal(InventoryOutcome.Refused, e.TransferSlot(bag, 0, bag, 1).Outcome);
        b.Aliases[(21, 1, 0)] = 777;
        b.Aliases[(20, 1, 0)] = 777; // a character and its own bag entity
        Assert.Equal(InventoryOutcome.Refused, e.TransferSlot(bag, 0, chest, 1).Outcome);
        b.Aliases.Clear();

        b.Epoch = 2;
        var stale = e.TransferSlot(bag, 0, chest, 1);
        Assert.Equal(InventoryOutcome.Refused, stale.Outcome);
        Assert.Contains("stale", stale.Reason);
        b.Epoch = 1;

        Assert.Equal(10, b.CountOf(bag, IronOre)); // nothing moved by any refusal
        Assert.Equal(0, b.CountOf(chest, IronOre));
    }

    [Fact]
    public void TransferSlot_restores_an_interchangeable_item_after_a_lossy_move()
    {
        var b = NewBackend();
        var bag = b.AddContainer(20, 12);
        var chest = b.AddContainer(21, 4);
        b.Put(bag, 8, IronOre, 100);
        b.LoseOnMove = 7;
        var r = new InventoryEngine(b).TransferSlot(bag, 8, chest, 50);
        Assert.True(r.Ok);
        Assert.Equal(7, r.Compensated);
        Assert.Equal(100, b.CountOf(bag, IronOre) + b.CountOf(chest, IronOre));
    }

    [Fact]
    public void TransferSlot_stops_inconsistent_for_gear_and_never_recreates_a_copy()
    {
        var b = NewBackend();
        var bag = b.AddContainer(20, 12);
        var chest = b.AddContainer(21, 4);
        b.Put(bag, 1, Sword, 1, hasEntity: true);
        b.LoseOnMove = 1;
        var errors = new List<string>();
        var r = new InventoryEngine(b, errors.Add).TransferSlot(bag, 1, chest, 1);
        Assert.Equal(InventoryOutcome.Inconsistent, r.Outcome);
        Assert.Equal(0, b.Adds);
        Assert.Single(errors);
    }

    [Fact]
    public void TransferSlot_counts_gear_the_target_owner_equips_as_moved()
    {
        var b = NewBackend();
        var chest = b.AddContainer(21, 4);
        var servant = b.AddContainer(50, 4);
        b.FreeEquipSlots[(50, 1)] = new List<int> { Sword };
        b.Put(chest, 2, Sword, 1, hasEntity: true);
        var r = new InventoryEngine(b).TransferSlot(chest, 2, servant, 1);
        Assert.Equal(InventoryOutcome.Complete, r.Outcome);
        Assert.Equal(1, r.Equipped);
        Assert.Equal(0, b.CountOf(chest, Sword));
    }

    // ---- the filter on Transfer and TransferAll ------------------------------------------------

    [Fact]
    public void Transfer_with_skip_action_bar_never_draws_from_slots_zero_to_seven()
    {
        var b = NewBackend();
        var bag = b.AddContainer(20, 12);
        var chest = b.AddContainer(21, 4);
        b.Put(bag, 0, IronOre, 50);   // action bar
        b.Put(bag, 7, IronOre, 50);   // action bar
        b.Put(bag, 10, IronOre, 30);
        var e = new InventoryEngine(b);
        var r = e.Transfer(bag, chest, IronOre, 100, InventorySlotFilter.SkipActionBar);
        Assert.Equal(InventoryOutcome.Partial, r.Outcome);
        Assert.Equal(30, r.Done);
        Assert.Contains("allowed source slots", r.Reason);
        Assert.Equal(130, r.SourceBefore);
        Assert.Equal(100, r.SourceAfter);
        var slots = b.Slots(bag);
        Assert.Equal(50, slots[0].Amount);
        Assert.Equal(50, slots[7].Amount);
        Assert.True(slots[10].IsEmpty);

        // Only on the bar now: nothing to do, and the reason says why.
        var none = e.Transfer(bag, chest, IronOre, 10, InventorySlotFilter.SkipActionBar);
        Assert.Equal(InventoryOutcome.Nothing, none.Outcome);
        Assert.Contains("skips", none.Reason);

        // Without a filter, Transfer may take from the bar.
        var all = e.Transfer(bag, chest, IronOre, 10);
        Assert.Equal(10, all.Done);
        Assert.Equal(130, b.CountOf(bag, IronOre) + b.CountOf(chest, IronOre));
    }

    [Fact]
    public void TransferAll_with_skip_action_bar_is_a_quick_stash()
    {
        var b = NewBackend();
        var bag = b.AddContainer(20, 12);
        var chest = b.AddContainer(21, 6);
        b.Put(bag, 0, Sword, 1, hasEntity: true); // wielded on the bar
        b.Put(bag, 1, Essence, 20);               // only on the bar
        b.Put(bag, 3, IronOre, 15);               // on the bar
        b.Put(bag, 9, IronOre, 40);
        b.Put(bag, 11, Copper, 5);
        var all = new InventoryEngine(b).TransferAll(bag, chest, null, InventorySlotFilter.SkipActionBar);
        Assert.True(all.Ok);
        Assert.Equal(45, all.Moved);
        Assert.Equal(0, all.Left); // bar units were never asked for
        Assert.Equal(new[] { IronOre, Copper }, all.PerItem.Select(r => r.Item).ToArray());
        var slots = b.Slots(bag);
        Assert.Equal(Sword, slots[0].Item);
        Assert.Equal(20, slots[1].Amount);
        Assert.Equal(15, slots[3].Amount);
        Assert.Equal(40, b.CountOf(chest, IronOre));
        Assert.Equal(5, b.CountOf(chest, Copper));
        Assert.Equal(0, b.CountOf(chest, Sword));
    }

    [Fact]
    public void TransferAll_left_counts_units_the_restore_step_put_into_a_skipped_slot()
    {
        var b = NewBackend();
        var bag = b.AddContainer(20, 12);
        var chest = b.AddContainer(21, 6);
        b.Put(bag, 3, IronOre, 15);   // on the bar, below the stack size
        b.Put(bag, 9, IronOre, 40);
        b.LoseOnMove = 7;             // the move destroys 7 in the target
        var all = new InventoryEngine(b).TransferAll(bag, chest, null, InventorySlotFilter.SkipActionBar);
        Assert.True(all.Ok);
        var row = Assert.Single(all.PerItem);
        Assert.Equal(7, row.Compensated);
        Assert.Equal(33, all.Moved);
        // The game's add stacked the restored 7 onto the bar slot; they are still left over.
        Assert.Equal(22, b.Slots(bag)[3].Amount);
        Assert.True(b.Slots(bag)[9].IsEmpty);
        Assert.Equal(7, all.Left);
        Assert.Equal(55, b.CountOf(bag, IronOre) + b.CountOf(chest, IronOre));
    }

    [Fact]
    public void TransferAll_without_a_slot_filter_is_unchanged()
    {
        var b = NewBackend();
        var bag = b.AddContainer(20, 12);
        var chest = b.AddContainer(21, 6);
        b.Put(bag, 3, IronOre, 15);
        b.Put(bag, 9, IronOre, 40);
        var all = new InventoryEngine(b).TransferAll(bag, chest, null);
        Assert.Equal(55, all.Moved);
        Assert.Equal(0, all.Left);
        Assert.Equal(0, b.CountOf(bag, IronOre));
    }

    // ---- sample parsing ------------------------------------------------------------------------

    [Fact]
    public void Sample_moveslot_and_keepbar_parse()
    {
        Assert.True(SampleInventory.Handles("moveslot"));
        Assert.True(SampleInventory.TryParseSlot("0", out int s0) && s0 == 0);
        Assert.True(SampleInventory.TryParseSlot(" 12 ", out int s12) && s12 == 12);
        Assert.False(SampleInventory.TryParseSlot("-1", out _));
        Assert.False(SampleInventory.TryParseSlot("x", out _));
        Assert.Equal(InventorySlotFilter.SkipActionBar, SampleInventory.MoveAllSlots("KeepBar"));
        Assert.Equal(InventorySlotFilter.All, SampleInventory.MoveAllSlots(""));
        Assert.Contains("moveslot <from> <slot> <to> <amount>", SampleInventory.UsageReply);
        var r = new InventoryResult { Outcome = InventoryOutcome.Complete, Item = IronOre, Requested = 5, Done = 5, SourceBefore = 9, SourceAfter = 4, TargetAfter = 5 };
        Assert.StartsWith("Moved 5/5 ore. bag slot 9: 9 -> 4", SampleInventory.MoveSlotReply("bag", 9, "#1", "ore", r));
    }
}
