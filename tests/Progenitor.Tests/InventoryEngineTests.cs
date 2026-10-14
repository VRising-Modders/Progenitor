using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Inventory;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of <c>ctx.Inventory</c>'s engine: the admission rule, slot math, add,
/// remove, the atomic transfer (with its conservation check and compensating step), TransferAll
/// and the fingerprint watch. The fake backend models the game's server add and slot move as
/// the game's own server code: stack onto matching slots below capacity, then
/// fill empty slots; a move adds into the target and leaves what did not fit in the source slot.
/// </summary>
public class InventoryEngineTests
{
    internal const int IronOre = -1000; // placeholder item ids
    internal const int Copper = -1001;
    internal const int Essence = -1002;
    internal const int Sword = -1003;

    internal const long CatMineral = 0x4000000;
    internal const long CatBloodEssence = 0x80;
    internal const long CatJewel = 0x800000;
    internal const long CatSilver = 0x400;

    internal static FakeInventoryBackend NewBackend()
    {
        var b = new FakeInventoryBackend();
        b.Items[IronOre] = new InventoryItemInfo(250, CatMineral, 0);
        b.Items[Copper] = new InventoryItemInfo(100, CatMineral, 0);
        b.Items[Essence] = new InventoryItemInfo(500, CatBloodEssence, 0);
        b.Items[Sword] = new InventoryItemInfo(1, 1, 2);
        return b;
    }

    // ---- rules ----------------------------------------------------------------------------------

    [Fact]
    public void Restriction_follows_the_game_rule()
    {
        // A treasury-style container: category Silver|Jewel.
        var treasury = new InventoryRestriction(0, CatSilver | CatJewel);
        Assert.False(InventoryMath.IsAllowed(treasury, Essence, CatBloodEssence));
        Assert.True(InventoryMath.IsAllowed(treasury, 5, CatJewel));
        // Type-only restriction (heart fuel style): only that item.
        var only = new InventoryRestriction(Essence, 0);
        Assert.True(InventoryMath.IsAllowed(only, Essence, CatBloodEssence));
        Assert.False(InventoryMath.IsAllowed(only, IronOre, CatMineral));
        // Both set: both must hold.
        var both = new InventoryRestriction(Essence, CatMineral);
        Assert.False(InventoryMath.IsAllowed(both, Essence, CatBloodEssence));
        Assert.True(InventoryMath.IsAllowed(InventoryRestriction.None, IronOre, CatMineral));
    }

    [Fact]
    public void FreeSpace_counts_partial_stacks_empty_slots_and_overrides()
    {
        var slots = new[]
        {
            new InventorySlot(0, IronOre, 200, 250, 0, false),   // 50 free
            new InventorySlot(1, Copper, 10, 100, 0, false),     // other item
            new InventorySlot(2, 0, 0, 0, 0, false),             // 250 free
            new InventorySlot(3, 0, 0, 0, 1000, false),          // override: 1000 free
            new InventorySlot(4, IronOre, 250, 250, 0, false),   // full
        };
        Assert.Equal(50 + 250 + 1000, InventoryMath.FreeSpace(slots, IronOre, 250, true));
        Assert.Equal(0, InventoryMath.FreeSpace(slots, IronOre, 250, false));
        Assert.Equal(450, InventoryMath.Count(slots, IronOre));
    }

    [Fact]
    public void Fingerprint_moves_with_contents_and_never_returns_zero()
    {
        var a = new List<InventorySlot> { new(0, IronOre, 5, 250, 0, false) };
        var b = new List<InventorySlot> { new(0, IronOre, 6, 250, 0, false) };
        Assert.NotEqual(InventoryMath.Fingerprint(a, 1), InventoryMath.Fingerprint(b, 1));
        Assert.Equal(InventoryMath.Fingerprint(a, 1), InventoryMath.Fingerprint(a.ToList(), 1));
        Assert.NotEqual(0UL, InventoryMath.Fingerprint(Array.Empty<InventorySlot>(), 0));
    }

    // ---- add / remove ---------------------------------------------------------------------------

    [Fact]
    public void TryAdd_partial_reports_the_remainder()
    {
        var b = NewBackend();
        var chest = b.AddContainer(1, 2);
        b.Put(chest, 0, IronOre, 200);
        var engine = new InventoryEngine(b);
        var r = engine.TryAdd(chest, IronOre, 500);
        Assert.Equal(InventoryOutcome.Partial, r.Outcome);
        Assert.Equal(300, r.Done); // 50 on the stack + 250 in the empty slot
        Assert.Equal(200, r.Remainder);
        Assert.Equal(200, r.TargetBefore);
        Assert.Equal(500, r.TargetAfter);
    }

    [Fact]
    public void TryAdd_refuses_unknown_items_restricted_containers_stale_refs_and_the_client()
    {
        var b = NewBackend();
        var treasury = b.AddContainer(1, 4, new InventoryRestriction(0, CatSilver | CatJewel));
        var engine = new InventoryEngine(b);
        Assert.Equal(InventoryOutcome.Refused, engine.TryAdd(treasury, 12345, 1).Outcome);
        var refused = engine.TryAdd(treasury, Essence, 10);
        Assert.Equal(InventoryOutcome.Nothing, refused.Outcome);
        Assert.Contains("refuses", refused.Reason);
        Assert.Equal(InventoryOutcome.Refused, engine.TryAdd(treasury, IronOre, 0).Outcome);

        b.Epoch = 2; // a save reload
        var stale = engine.TryAdd(treasury, IronOre, 1);
        Assert.Equal(InventoryOutcome.Refused, stale.Outcome);
        Assert.Contains("stale", stale.Reason);

        b.Epoch = 1;
        b.Server = false;
        Assert.Contains("server-side", engine.TryAdd(treasury, IronOre, 1).Reason);
    }

    [Fact]
    public void TryRemove_never_asks_for_more_than_the_inventory_holds()
    {
        var b = NewBackend();
        var bag = b.AddContainer(7, 3);
        b.Put(bag, 1, IronOre, 30);
        var engine = new InventoryEngine(b);
        var r = engine.TryRemove(bag, IronOre, 100);
        Assert.Equal(InventoryOutcome.Partial, r.Outcome);
        Assert.Equal(30, r.Done);
        Assert.Equal(30, b.LastRemoveAsk); // clamped: the game's equipment fallback is never reached
        Assert.Equal(InventoryOutcome.Nothing, engine.TryRemove(bag, IronOre, 1).Outcome);
    }

    // ---- transfer -------------------------------------------------------------------------------

    [Fact]
    public void Transfer_moves_50_iron_ore_between_two_chests_and_reports_counts()
    {
        var b = NewBackend();
        var a = b.AddContainer(10, 4);
        var c = b.AddContainer(11, 4);
        b.Put(a, 0, IronOre, 30);
        b.Put(a, 2, IronOre, 40);
        b.Put(c, 0, IronOre, 245);
        var r = new InventoryEngine(b).Transfer(a, c, IronOre, 50);
        Assert.Equal(InventoryOutcome.Complete, r.Outcome);
        Assert.Equal(50, r.Done);
        Assert.Equal(70, r.SourceBefore);
        Assert.Equal(20, r.SourceAfter);
        Assert.Equal(245, r.TargetBefore);
        Assert.Equal(295, r.TargetAfter);
        Assert.Equal(r.SourceBefore + r.TargetBefore, r.SourceAfter + r.TargetAfter);
        Assert.Equal(2, r.Steps);
        Assert.Equal(0, r.Compensated);
    }

    [Fact]
    public void Transfer_stops_partial_when_the_target_fills_and_conserves_the_total()
    {
        var b = NewBackend();
        var a = b.AddContainer(10, 2);
        var c = b.AddContainer(11, 1);
        b.Put(a, 0, IronOre, 250);
        b.Put(a, 1, IronOre, 250);
        b.Put(c, 0, IronOre, 200);
        var r = new InventoryEngine(b).Transfer(a, c, IronOre, 300);
        Assert.Equal(InventoryOutcome.Partial, r.Outcome);
        Assert.Equal(50, r.Done);
        Assert.Equal(500 + 200, r.SourceAfter + r.TargetAfter);
    }

    [Fact]
    public void Transfer_refuses_same_inventory_and_does_nothing_for_full_or_refusing_targets()
    {
        var b = NewBackend();
        var a = b.AddContainer(10, 2);
        var full = b.AddContainer(11, 1);
        var heart = b.AddContainer(12, 2, new InventoryRestriction(Essence, 0));
        b.Put(a, 0, IronOre, 10);
        b.Put(full, 0, Copper, 100);
        var e = new InventoryEngine(b);
        Assert.Equal(InventoryOutcome.Refused, e.Transfer(a, a, IronOre, 1).Outcome);
        Assert.Equal(InventoryOutcome.Nothing, e.Transfer(a, full, IronOre, 5).Outcome);
        Assert.Equal(InventoryOutcome.Nothing, e.Transfer(a, heart, IronOre, 5).Outcome);
        Assert.Equal(10, b.CountOf(a, IronOre));
        // Station input and output are two inventories of one owner: allowed.
        var input = new InventoryRef(30, 1, 0, 1);
        var output = new InventoryRef(30, 1, 1, 1);
        b.AddContainer(input, 2);
        b.AddContainer(output, 2);
        b.Put(input, 0, Copper, 9);
        Assert.Equal(9, e.Transfer(input, output, Copper, 9).Done);
    }

    [Fact]
    public void Transfer_compensates_a_move_that_destroys_units()
    {
        var b = NewBackend();
        var a = b.AddContainer(10, 4);
        var c = b.AddContainer(11, 4);
        b.Put(a, 0, IronOre, 100);
        b.LoseOnMove = 7; // the next move destroys 7 units
        var r = new InventoryEngine(b).Transfer(a, c, IronOre, 50);
        Assert.True(r.Ok);
        Assert.Equal(7, r.Compensated);
        Assert.Equal(100, b.CountOf(a, IronOre) + b.CountOf(c, IronOre));
        Assert.Contains("compensated", r.Reason);
    }

    [Fact]
    public void Transfer_compensates_a_move_that_duplicates_units()
    {
        var b = NewBackend();
        var a = b.AddContainer(10, 4);
        var c = b.AddContainer(11, 4);
        b.Put(a, 0, IronOre, 100);
        b.DuplicateOnMove = 5;
        var r = new InventoryEngine(b).Transfer(a, c, IronOre, 50);
        Assert.True(r.Ok);
        Assert.Equal(5, r.Compensated);
        Assert.Equal(100, b.CountOf(a, IronOre) + b.CountOf(c, IronOre));
    }

    [Fact]
    public void Transfer_reports_inconsistent_when_compensation_cannot_restore_the_total()
    {
        var b = NewBackend();
        var a = b.AddContainer(10, 4);
        var c = b.AddContainer(11, 4);
        b.Put(a, 0, IronOre, 100);
        b.LoseOnMove = 7;
        b.AddFails = true;
        var errors = new List<string>();
        var r = new InventoryEngine(b, errors.Add).Transfer(a, c, IronOre, 50);
        Assert.Equal(InventoryOutcome.Inconsistent, r.Outcome);
        Assert.False(r.Ok);
        Assert.Single(errors);
    }

    [Fact]
    public void Transfer_keeps_item_entities_by_moving_slots()
    {
        var b = NewBackend();
        var a = b.AddContainer(10, 2);
        var c = b.AddContainer(11, 2);
        b.Put(a, 1, Sword, 1, hasEntity: true);
        var r = new InventoryEngine(b).Transfer(a, c, Sword, 1);
        Assert.Equal(InventoryOutcome.Complete, r.Outcome);
        Assert.True(b.Slots(c).Single(s => s.Item == Sword).HasItemEntity);
        Assert.Equal(0, b.Adds); // no recreation through the add path
    }

    [Fact]
    public void TransferAll_moves_each_item_type_through_the_filter()
    {
        var b = NewBackend();
        var a = b.AddContainer(10, 4);
        var c = b.AddContainer(11, 4);
        b.Put(a, 0, IronOre, 10);
        b.Put(a, 1, Copper, 20);
        b.Put(a, 2, IronOre, 5);
        var e = new InventoryEngine(b);
        var only = e.TransferAll(a, c, item => item == Copper);
        Assert.True(only.Ok);
        Assert.Equal(20, only.Moved);
        Assert.Single(only.PerItem);
        var rest = e.TransferAll(a, c, null);
        Assert.Equal(15, rest.Moved);
        Assert.Equal(0, rest.Left);
        Assert.Equal(35, b.CountOf(c, IronOre) + b.CountOf(c, Copper));
    }

    [Fact]
    public void Writes_poke_watches_on_the_touched_containers()
    {
        var b = NewBackend();
        var a = b.AddContainer(10, 2);
        var c = b.AddContainer(11, 2);
        b.Put(a, 0, IronOre, 5);
        var e = new InventoryEngine(b);
        var touched = new List<InventoryRef>();
        e.Touched = touched.Add;
        e.Transfer(a, c, IronOre, 5);
        Assert.Contains(a, touched);
        Assert.Contains(c, touched);
    }

    // ---- watches --------------------------------------------------------------------------------

    [Fact]
    public void Watch_reports_changes_by_fingerprint_and_gone_once()
    {
        var b = NewBackend();
        var a = b.AddContainer(10, 2);
        var hub = new InventoryWatchHub(b);
        var seen = new List<InventoryChange>();
        var w = hub.Add(a, seen.Add, TimeSpan.FromMilliseconds(500), nowMs: 0);
        hub.Tick(600);
        Assert.Empty(seen); // unchanged
        b.Put(a, 0, IronOre, 3);
        hub.Tick(700); // not due yet
        Assert.Empty(seen);
        hub.Tick(1200);
        Assert.Single(seen);
        Assert.Equal(InventoryChangeKind.Changed, seen[0].Kind);

        b.Put(a, 0, IronOre, 4);
        w.Poke();
        hub.Tick(1201);
        Assert.Equal(2, seen.Count);

        b.Remove(a);
        hub.Tick(5000);
        Assert.Equal(InventoryChangeKind.Gone, seen[^1].Kind);
        Assert.False(w.Active);
        hub.Tick(10000);
        Assert.Equal(3, seen.Count);
        Assert.Equal(0, hub.Count);
    }

    [Fact]
    public void Watch_goes_gone_after_a_reload_and_stops_when_disposed()
    {
        var b = NewBackend();
        var a = b.AddContainer(10, 2);
        var hub = new InventoryWatchHub(b);
        var seen = new List<InventoryChange>();
        var w1 = hub.Add(a, seen.Add, null, 0);
        var w2 = hub.Add(a, _ => throw new InvalidOperationException("boom"), null, 0);
        w2.Dispose();
        b.Epoch = 2;
        hub.Tick(1000);
        Assert.Single(seen);
        Assert.Equal(InventoryChangeKind.Gone, seen[0].Kind);
        Assert.False(w1.Active);
    }

    [Fact]
    public void Watch_isolates_a_throwing_handler()
    {
        var b = NewBackend();
        var a = b.AddContainer(10, 2);
        var warnings = new List<string>();
        var hub = new InventoryWatchHub(b, warnings.Add);
        hub.Add(a, _ => throw new InvalidOperationException("boom"), null, 0);
        int ok = 0;
        hub.Add(a, _ => ok++, null, 0);
        b.Put(a, 0, IronOre, 1);
        hub.Tick(1000);
        Assert.Equal(1, ok);
        Assert.Single(warnings);
    }
}

/// <summary>In-memory inventories with the game's add and move rules, plus fault injection.</summary>
internal sealed class FakeInventoryBackend : IInventoryBackend
{
    private readonly Dictionary<(int, int, int), Box> _boxes = new();

    public bool Server { get; set; } = true;
    public int Epoch { get; set; } = 1;
    public Dictionary<int, InventoryItemInfo> Items { get; } = new();
    public int LoseOnMove { get; set; }
    public int DuplicateOnMove { get; set; }
    public bool AddFails { get; set; }
    public int Adds { get; private set; }
    public int LastRemoveAsk { get; private set; }

    public bool IsServer => Server;
    public int CurrentEpoch => Epoch;

    private sealed class Box
    {
        internal (int Item, int Amount, bool Entity)[] Slots = Array.Empty<(int, int, bool)>();
        internal InventoryRestriction Restriction;
    }

    public InventoryRef AddContainer(int owner, int slots, InventoryRestriction restriction = default) =>
        AddContainer(new InventoryRef(owner, 1, 0, Epoch), slots, restriction);

    public InventoryRef AddContainer(InventoryRef r, int slots, InventoryRestriction restriction = default)
    {
        _boxes[(r.OwnerIndex, r.OwnerVersion, r.Instance)] = new Box { Slots = new (int, int, bool)[slots], Restriction = restriction };
        return r;
    }

    public void Remove(InventoryRef r) => _boxes.Remove((r.OwnerIndex, r.OwnerVersion, r.Instance));

    public void Put(InventoryRef r, int slot, int item, int amount, bool hasEntity = false) =>
        Get(r)!.Slots[slot] = (item, amount, hasEntity);

    public int CountOf(InventoryRef r, int item) => Get(r)!.Slots.Where(s => s.Item == item).Sum(s => s.Amount);

    public List<InventorySlot> Slots(InventoryRef r)
    {
        var list = new List<InventorySlot>();
        TryRead(r, list, out _, out _);
        return list;
    }

    private Box? Get(InventoryRef r) => _boxes.TryGetValue((r.OwnerIndex, r.OwnerVersion, r.Instance), out var b) ? b : null;

    public bool TryRead(InventoryRef container, List<InventorySlot> into, out InventoryRestriction restriction, out string reason)
    {
        into.Clear();
        restriction = default;
        var box = Get(container);
        if (box == null) { reason = "gone"; return false; }
        for (int i = 0; i < box.Slots.Length; i++)
        {
            var s = box.Slots[i];
            int max = s.Item == 0 ? 0 : (Items.TryGetValue(s.Item, out var info) ? info.MaxAmount : 0);
            into.Add(new InventorySlot(i, s.Item, s.Amount, max, 0, s.Entity));
        }

        restriction = box.Restriction;
        reason = "";
        return true;
    }

    public bool TryItemInfo(int item, out InventoryItemInfo info) => Items.TryGetValue(item, out info);

    /// <summary>The game's add: stack onto matching slots below capacity, then empty slots. Returns the amount added.</summary>
    private int AddInto(Box box, int item, int amount, bool entity)
    {
        if (!Items.TryGetValue(item, out var info) || !InventoryMath.IsAllowed(box.Restriction, item, info.Category)) return 0;
        int left = amount;
        for (int i = 0; i < box.Slots.Length && left > 0; i++)
        {
            var s = box.Slots[i];
            if (s.Item != item || s.Amount >= info.MaxAmount) continue;
            int n = Math.Min(left, info.MaxAmount - s.Amount);
            box.Slots[i] = (item, s.Amount + n, s.Entity);
            left -= n;
        }

        for (int i = 0; i < box.Slots.Length && left > 0; i++)
        {
            if (box.Slots[i].Item != 0) continue;
            int n = Math.Min(left, info.MaxAmount);
            box.Slots[i] = (item, n, entity);
            left -= n;
        }

        return amount - left;
    }

    public int MoveSlot(InventoryRef from, int slot, InventoryRef to, int maxAmount, out string detail)
    {
        var src = Get(from);
        var dst = Get(to);
        if (src == null || dst == null) { detail = "missing"; return 14; }
        var s = src.Slots[slot];
        if (s.Item == 0) { detail = "Failed_NoItemAtFromIndex"; return 7; }
        int ask = maxAmount < 1 ? s.Amount : Math.Min(maxAmount, s.Amount);
        int added = AddInto(dst, s.Item, ask, s.Entity);
        if (added == 0) { detail = "Failed"; return 14; }
        int left = s.Amount - added;
        src.Slots[slot] = left > 0 ? (s.Item, left, s.Entity) : (0, 0, false);

        if (LoseOnMove > 0)
        {
            // Destroy units on the target side.
            for (int i = 0; i < dst.Slots.Length; i++)
                if (dst.Slots[i].Item == s.Item && dst.Slots[i].Amount >= LoseOnMove)
                {
                    dst.Slots[i] = (s.Item, dst.Slots[i].Amount - LoseOnMove, dst.Slots[i].Entity);
                    break;
                }
            LoseOnMove = 0;
        }

        if (DuplicateOnMove > 0)
        {
            AddInto(dst, s.Item, DuplicateOnMove, false);
            DuplicateOnMove = 0;
        }

        detail = added == ask ? "Success_Complete" : "Success_Partial";
        return added == ask ? 0 : 1;
    }

    public bool Add(InventoryRef container, int item, int amount, out string detail)
    {
        Adds++;
        detail = "";
        if (AddFails) { detail = "Failed_InventoryFull"; return false; }
        var box = Get(container);
        if (box == null) { detail = "missing"; return false; }
        AddInto(box, item, amount, false);
        return true;
    }

    public bool Remove(InventoryRef container, int item, int amount, out string detail)
    {
        LastRemoveAsk = amount;
        detail = "";
        var box = Get(container);
        if (box == null) return false;
        int left = amount;
        for (int i = box.Slots.Length - 1; i >= 0 && left > 0; i--)
        {
            var s = box.Slots[i];
            if (s.Item != item) continue;
            int n = Math.Min(left, s.Amount);
            box.Slots[i] = s.Amount - n > 0 ? (item, s.Amount - n, s.Entity) : (0, 0, false);
            left -= n;
        }

        return true;
    }
}
