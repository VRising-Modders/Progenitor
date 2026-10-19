using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Inventory;

/// <summary>Item facts the engine needs, from the item's <c>ItemData</c>.</summary>
internal readonly struct InventoryItemInfo
{
    /// <summary>Construct item facts.</summary>
    public InventoryItemInfo(int maxAmount, long category, int itemType, bool hasInstanceEntity = false)
    {
        MaxAmount = maxAmount;
        Category = category;
        ItemType = itemType;
        HasInstanceEntity = hasInstanceEntity;
    }

    /// <summary><c>ItemData.MaxAmount</c>: the stack size.</summary>
    public int MaxAmount { get; }

    /// <summary><c>ItemData.ItemCategory</c> flags.</summary>
    public long Category { get; }

    /// <summary><c>ItemData.ItemType</c> (0 is stackable).</summary>
    public int ItemType { get; }

    /// <summary>
    /// <c>ItemData.Entity</c> is set in the item table row: the game's inventory add gives each slot
    /// of this item its own item entity (see <see cref="Progenitor.Items.ItemInstanceRule"/>).
    /// </summary>
    public bool HasInstanceEntity { get; }
}

/// <summary>
/// What the engine needs from the game. The server implementation is
/// <c>UnityInventoryBackend</c>, the only file of the module that touches Unity or ProjectM
/// types; tests use an in-memory fake. Every method returns false or a failure code instead of
/// throwing into the caller.
/// </summary>
internal interface IInventoryBackend
{
    /// <summary>True on the server with a live world.</summary>
    bool IsServer { get; }

    /// <summary>The world-load epoch now.</summary>
    int CurrentEpoch { get; }

    /// <summary>
    /// Copy the inventory's slots into <paramref name="into"/> (cleared first) and its
    /// restriction. False with a reason when the owner is gone or has no such inventory.
    /// </summary>
    bool TryRead(InventoryRef container, List<InventorySlot> into, out InventoryRestriction restriction, out string reason);

    /// <summary>Item facts from the item table. False when the item is not an item the server knows.</summary>
    bool TryItemInfo(int item, out InventoryItemInfo info);

    /// <summary>
    /// Move up to <paramref name="maxAmount"/> from one source slot into the target, with the
    /// game's own slot move. Returns the game's result code (0 complete, 1 partial, other =
    /// failed) or -1 when the call itself failed; <paramref name="detail"/> names it.
    /// </summary>
    int MoveSlot(InventoryRef from, int slot, InventoryRef to, int maxAmount, out string detail);

    /// <summary>Add with the game's server add. False when the call failed outright.</summary>
    bool Add(InventoryRef container, int item, int amount, out string detail);

    /// <summary>
    /// Remove with the game's server removal, from this inventory only. The engine never asks
    /// for more than the inventory holds, so the game's fallback to worn equipment is never reached.
    /// </summary>
    bool Remove(InventoryRef container, int item, int amount, out string detail);

    /// <summary>
    /// The entity that actually holds the slots, as one number. Two references that resolve to
    /// the same key are the same inventory (a character and its bag entity, a station and its
    /// input entity). False when unresolvable.
    /// </summary>
    bool TryInventoryKey(InventoryRef container, out long key);

    /// <summary>
    /// How many equipment slots of the inventory owner hold <paramref name="item"/> (a player's
    /// <c>Equipment</c>, a servant's <c>ServantEquipment</c>); -1 when the owner has neither or
    /// it could not be read.
    /// </summary>
    int EquippedCount(InventoryRef container, int item);

    /// <summary>
    /// A content fingerprint of the inventory (<see cref="InventoryMath.Fingerprint"/> over item
    /// and amount) without item-table lookups. False when unreadable.
    /// </summary>
    bool TryFingerprint(InventoryRef container, out ulong fingerprint);
}

/// <summary>
/// The inventory operations behind <c>ctx.Inventory</c>, written against
/// <see cref="IInventoryBackend"/> so the whole algorithm — including the transfer's
/// conservation check and compensating step — is tested without the game.
///
/// <para><b>Transfer.</b> The move is the game's own slot move
/// (<c>InventoryUtilitiesServer.TryMoveItem</c>), one source slot at a time, never more than
/// the remaining amount per step. That call adds into the target first, clears the source slot
/// only when the add succeeded, and puts any part that did not fit back into the slot it came
/// from, so it neither duplicates nor destroys by itself and it keeps an item's own entity
/// (durability, stats) instead of recreating it. After every step the engine reads both
/// containers back and checks that the source lost exactly what the target gained. When it did
/// not, the compensating step restores the total: a shortfall is added back to the source (then
/// to the target if the source refuses), a surplus is removed from the target. If the total
/// still does not match, the transfer stops with <see cref="InventoryOutcome.Inconsistent"/>
/// and logs both counts.</para>
///
/// <para>Main thread only; the scratch buffers are shared.</para>
/// </summary>
internal sealed class InventoryEngine
{
    private readonly IInventoryBackend _backend;
    private readonly Action<string>? _logError;
    private readonly List<InventorySlot> _a = new();
    private readonly List<InventorySlot> _b = new();

    /// <summary>Called after every write with each container it touched (watches use it).</summary>
    internal Action<InventoryRef>? Touched;

    internal InventoryEngine(IInventoryBackend backend, Action<string>? logError = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logError = logError;
    }

    internal IInventoryBackend Backend => _backend;

    /// <summary>Largest amount a single call accepts.</summary>
    internal const int MaxAmount = 1_000_000;

    // ---- reads --------------------------------------------------------------------------------

    internal InventoryAcceptance DescribeAcceptance(InventoryRef container)
    {
        var snapshot = Read(container);
        return new InventoryAcceptance(snapshot.Restriction, snapshot.Reason);
    }

    internal InventorySnapshot Read(InventoryRef container)
    {
        if (!Check(container, out string why))
            return new InventorySnapshot(container, Array.Empty<InventorySlot>(), InventoryRestriction.None, why);
        if (!_backend.TryRead(container, _a, out var restriction, out why))
            return new InventorySnapshot(container, Array.Empty<InventorySlot>(), InventoryRestriction.None, why);
        return new InventorySnapshot(container, _a.ToArray(), restriction);
    }

    /// <summary>Fill <paramref name="buffer"/>; returns the slot count (which may exceed the buffer), or -1 when unreadable.</summary>
    internal int ReadInto(InventoryRef container, InventorySlot[] buffer)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (!Check(container, out _) || !_backend.TryRead(container, _a, out _, out _))
            return -1;
        int n = Math.Min(buffer.Length, _a.Count);
        for (int i = 0; i < n; i++) buffer[i] = _a[i];
        return _a.Count;
    }

    internal bool TryCount(InventoryRef container, int item, out int count, out string reason)
    {
        count = 0;
        if (!Check(container, out reason)) return false;
        if (!_backend.TryRead(container, _a, out _, out reason)) return false;
        count = InventoryMath.Count(_a, item);
        return true;
    }

    internal int Count(InventoryRef container, int item) =>
        TryCount(container, item, out int n, out _) ? n : 0;

    internal int CountMany(IEnumerable<InventoryRef> containers, int item)
    {
        if (containers == null) return 0;
        long total = 0;
        foreach (var c in containers)
            if (TryCount(c, item, out int n, out _)) total += n;
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    internal int FreeSpace(InventoryRef container, int item) =>
        TryFreeSpace(container, item, out int free, out _) ? free : 0;

    internal bool TryFreeSpace(InventoryRef container, int item, out int free, out string reason)
    {
        free = 0;
        if (!Check(container, out reason)) return false;
        if (!_backend.TryItemInfo(item, out var info))
        {
            reason = UnknownItem(item);
            return false;
        }

        if (!_backend.TryRead(container, _a, out var restriction, out reason)) return false;
        free = InventoryMath.FreeSpace(_a, item, info.MaxAmount, InventoryMath.IsAllowed(restriction, item, info.Category));
        return true;
    }

    // ---- writes -------------------------------------------------------------------------------

    internal InventoryResult TryAdd(InventoryRef container, int item, int amount)
    {
        if (!ValidAmount(amount, out string why) || !Check(container, out why))
            return InventoryResult.Refuse(item, amount, why);
        if (!_backend.TryItemInfo(item, out var info))
            return InventoryResult.Refuse(item, amount, UnknownItem(item));
        if (!_backend.TryRead(container, _a, out var restriction, out why))
            return InventoryResult.Refuse(item, amount, why);

        int before = InventoryMath.Count(_a, item);
        if (!InventoryMath.IsAllowed(restriction, item, info.Category))
            return Nothing(item, amount, "the container refuses this item (" + restriction + ")", targetBefore: before);
        if (InventoryMath.FreeSpace(_a, item, info.MaxAmount, true) <= 0)
            return Nothing(item, amount, "the container is full for this item", targetBefore: before);

        // An add the game equips onto the owner leaves the inventory unchanged: count it too, so a
        // caller never reads "nothing fit" for an item that was handed out (and retries).
        int eqBefore = _backend.EquippedCount(container, item);
        bool called = _backend.Add(container, item, amount, out string detail);
        Touched?.Invoke(container);
        if (!_backend.TryRead(container, _a, out _, out why))
            return Inconsistent(item, amount, 0, "could not read the container back after the add: " + why, 1);
        int after = InventoryMath.Count(_a, item);
        int equipped = 0;
        if (eqBefore >= 0)
        {
            int e = _backend.EquippedCount(container, item);
            if (e >= 0) equipped = Math.Max(0, e - eqBefore);
        }

        int done = after - before + equipped;
        if (done < 0 || done > amount)
        {
            string text = "add read-back out of range: before=" + before + " after=" + after + " equipped=" + equipped + " requested=" + amount;
            _logError?.Invoke("Inventory: " + text + " container=" + container + " item=" + item);
            return Inconsistent(item, amount, Math.Max(0, done), text, 1, targetBefore: before, targetAfter: after, equipped: equipped);
        }

        var added = Finish(item, amount, done, called ? "" : detail, steps: 1, targetBefore: before, targetAfter: after,
            shortfall: "only " + done + " of " + amount + " fit");
        return equipped == 0 ? added : added.WithEquipped(equipped);
    }

    internal InventoryResult TryRemove(InventoryRef container, int item, int amount)
    {
        if (!ValidAmount(amount, out string why) || !Check(container, out why))
            return InventoryResult.Refuse(item, amount, why);
        if (item == 0) return InventoryResult.Refuse(item, amount, "item 0 is not an item");
        if (!_backend.TryRead(container, _a, out _, out why))
            return InventoryResult.Refuse(item, amount, why);

        int before = InventoryMath.Count(_a, item);
        if (before == 0)
            return Nothing(item, amount, "the container holds none of this item", sourceBefore: 0);

        int ask = Math.Min(amount, before);
        bool called = _backend.Remove(container, item, ask, out string detail);
        Touched?.Invoke(container);
        if (!_backend.TryRead(container, _a, out _, out why))
            return Inconsistent(item, amount, 0, "could not read the container back after the removal: " + why, 1);
        int after = InventoryMath.Count(_a, item);
        int done = before - after;
        if (done < 0 || done > ask)
        {
            string text = "removal read-back out of range: before=" + before + " after=" + after + " asked=" + ask;
            _logError?.Invoke("Inventory: " + text + " container=" + container + " item=" + item);
            return Inconsistent(item, amount, Math.Max(0, done), text, 1, sourceBefore: before, sourceAfter: after);
        }

        return Finish(item, amount, done, called ? "" : detail, steps: 1, sourceBefore: before, sourceAfter: after,
            shortfall: done < ask ? "the game removed " + done + " of " + ask : "the container held only " + before);
    }

    internal InventoryResult Transfer(InventoryRef from, InventoryRef to, int item, int amount) =>
        TransferCore(from, to, item, amount, InventorySlotFilter.All);

    internal InventoryResult Transfer(InventoryRef from, InventoryRef to, int item, int amount, InventorySlotFilter sourceSlots) =>
        TransferCore(from, to, item, amount, sourceSlots);

    /// <summary>
    /// Move up to <paramref name="amount"/> from one slot of the source: the slot's item, through
    /// the same transfer as <see cref="Transfer(InventoryRef, InventoryRef, int, int)"/> restricted
    /// to that slot, so the conservation check, restore step and equipped accounting are the same.
    /// </summary>
    internal InventoryResult TransferSlot(InventoryRef from, int slot, InventoryRef to, int amount)
    {
        if (!ValidAmount(amount, out string why) || !Check(from, out why) || !Check(to, out why))
            return InventoryResult.Refuse(0, amount, why);
        if (SameInventory(from, to))
            return InventoryResult.Refuse(0, amount, "source and target are the same inventory");
        if (!_backend.TryRead(from, _a, out _, out why))
            return InventoryResult.Refuse(0, amount, "source: " + why);
        if (slot < 0 || slot >= _a.Count)
            return InventoryResult.Refuse(0, amount, "slot " + slot.ToString(CultureInfo.InvariantCulture)
                + " is out of range: the source has " + _a.Count.ToString(CultureInfo.InvariantCulture) + " slots");
        var row = _a[slot];
        if (row.IsEmpty || row.Amount <= 0)
            return Nothing(0, amount, "slot " + slot.ToString(CultureInfo.InvariantCulture) + " of the source is empty");
        return TransferCore(from, to, row.Item, amount, InventorySlotFilter.Only(slot));
    }

    private InventoryResult TransferCore(InventoryRef from, InventoryRef to, int item, int amount, InventorySlotFilter sourceSlots)
    {
        if (!ValidAmount(amount, out string why) || !Check(from, out why) || !Check(to, out why))
            return InventoryResult.Refuse(item, amount, why);
        if (SameInventory(from, to))
            return InventoryResult.Refuse(item, amount, "source and target are the same inventory");
        if (!_backend.TryItemInfo(item, out var info))
            return InventoryResult.Refuse(item, amount, UnknownItem(item));
        if (!_backend.TryRead(from, _a, out _, out why))
            return InventoryResult.Refuse(item, amount, "source: " + why);
        if (!_backend.TryRead(to, _b, out var restriction, out why))
            return InventoryResult.Refuse(item, amount, "target: " + why);

        // Counts are whole containers (conservation is checked on the totals); the move draws only
        // from the slots the filter allows.
        int srcBefore = InventoryMath.Count(_a, item);
        int dstBefore = InventoryMath.Count(_b, item);
        int available = TransferPlanner.CountAllowed(_a, item, sourceSlots);
        if (available == 0)
            return Nothing(item, amount, srcBefore == 0 ? "the source holds none of this item"
                : "the source holds this item only in slots the filter skips (" + sourceSlots + ")",
                srcBefore, srcBefore, dstBefore, dstBefore);
        if (!InventoryMath.IsAllowed(restriction, item, info.Category))
            return Nothing(item, amount, "the target refuses this item (" + restriction + ")", srcBefore, srcBefore, dstBefore, dstBefore);
        if (InventoryMath.FreeSpace(_b, item, info.MaxAmount, true) <= 0)
            return Nothing(item, amount, "the target is full for this item", srcBefore, srcBefore, dstBefore, dstBefore);

        // The source slots holding the item, captured before the first move changes the buffer.
        var plan = TransferPlanner.SourceSlots(_a, item, sourceSlots);
        // Only interchangeable units may ever be added back or taken away by the restore step.
        bool plain = TransferPlanner.IsPlain(info.ItemType, _a, _b, item);
        // The game's move equips gear onto a target owner with a free matching slot (a player, a
        // servant); those units left the inventory for the owner's equipment and count as moved.
        int eqBefore = _backend.EquippedCount(to, item);
        int want = Math.Min(amount, available);
        int total = srcBefore + dstBefore;
        int src = srcBefore, dst = dstBefore, equipped = 0, steps = 0, compensated = 0;
        string stop = "";

        for (int p = 0; p < plan.Count && (dst - dstBefore) + equipped < want; p++)
        {
            int step = Math.Min(want - ((dst - dstBefore) + equipped), plan[p].Amount);
            if (step <= 0) break;
            int code = _backend.MoveSlot(from, plan[p].Slot, to, step, out string detail);
            steps++;
            Touched?.Invoke(from);
            Touched?.Invoke(to);

            if (!ReadPair(from, to, item, out int srcNow, out int dstNow, out why))
                return Inconsistent(item, amount, Math.Max(0, dst - dstBefore + equipped),
                    "could not read the containers back after a move: " + why, steps, srcBefore, src, dstBefore, dst, compensated, equipped);

            int eqNow = equipped;
            if (eqBefore >= 0)
            {
                int e = _backend.EquippedCount(to, item);
                if (e >= 0) eqNow = Math.Max(0, e - eqBefore);
            }

            int imbalance = TransferPlanner.Imbalance(total, srcNow, dstNow + eqNow);
            if (imbalance != 0)
            {
                if (!plain)
                {
                    string text = "the total of item " + item + " changed and the item is not interchangeable, so nothing was "
                        + "added or removed to restore it: before " + srcBefore + "+" + dstBefore + ", now " + srcNow + "+" + dstNow
                        + (eqNow > 0 ? " (+" + eqNow + " equipped)" : "");
                    _logError?.Invoke("Inventory: transfer " + from + " -> " + to + ": " + text);
                    return Inconsistent(item, amount, Math.Max(0, dstNow - dstBefore + eqNow), text, steps,
                        srcBefore, srcNow, dstBefore, dstNow, compensated, eqNow);
                }

                compensated += Compensate(from, to, item, total - eqNow);
                if (!ReadPair(from, to, item, out srcNow, out dstNow, out why)
                    || TransferPlanner.Imbalance(total, srcNow, dstNow + eqNow) != 0)
                {
                    string text = "the total of item " + item + " changed and could not be restored: before "
                        + srcBefore + "+" + dstBefore + ", now " + srcNow + "+" + dstNow;
                    _logError?.Invoke("Inventory: transfer " + from + " -> " + to + ": " + text);
                    return Inconsistent(item, amount, Math.Max(0, dstNow - dstBefore + eqNow), text, steps,
                        srcBefore, srcNow, dstBefore, dstNow, compensated, eqNow);
                }
            }

            int movedThisStep = (dstNow - dst) + (eqNow - equipped);
            src = srcNow;
            dst = dstNow;
            equipped = eqNow;
            if (movedThisStep <= 0 || TransferPlanner.StopAfter(code, movedThisStep, step))
            {
                stop = movedThisStep <= 0
                    ? "the game moved nothing (" + detail + ")"
                    : "the target filled up (" + detail + ")";
                break;
            }
        }

        int moved = (dst - dstBefore) + equipped;
        string shortfall = moved < want
            ? (stop.Length > 0 ? stop : "moved " + moved + " of " + want)
            : sourceSlots.IsAll ? "the source held only " + srcBefore
            : "the allowed source slots (" + sourceSlots + ") held only " + available;
        var result = Finish(item, amount, moved, "", steps, srcBefore, src, dstBefore, dst, shortfall, compensated);
        return equipped == 0 ? result : result.WithEquipped(equipped);
    }

    internal InventoryTransferAllResult TransferAll(InventoryRef from, InventoryRef to, Func<int, bool>? filter) =>
        TransferAll(from, to, filter, InventorySlotFilter.All);

    internal InventoryTransferAllResult TransferAll(InventoryRef from, InventoryRef to, Func<int, bool>? filter, InventorySlotFilter sourceSlots)
    {
        if (!Check(from, out string why) || !Check(to, out why))
            return new InventoryTransferAllResult { Ok = false, Reason = why };
        if (SameInventory(from, to))
            return new InventoryTransferAllResult { Ok = false, Reason = "source and target are the same inventory" };
        if (!_backend.TryRead(from, _a, out _, out why))
            return new InventoryTransferAllResult { Ok = false, Reason = "source: " + why };

        // Copy first: the filter is plugin code and may itself read inventories (the shared buffer).
        // Only the slots the slot filter allows take part: the item list and counts come from them.
        var snapshot = TransferPlanner.AllowedSlots(_a, sourceSlots);
        var items = InventoryMath.DistinctItems(snapshot, snapshot.Length, filter);
        var counts = new int[items.Count];
        for (int i = 0; i < items.Count; i++) counts[i] = InventoryMath.Count(snapshot, items[i]);

        var rows = new List<InventoryResult>(items.Count);
        int moved = 0, left = 0;
        bool ok = true;
        for (int i = 0; i < items.Count; i++)
        {
            var r = TransferCore(from, to, items[i], Math.Min(counts[i], MaxAmount), sourceSlots);
            rows.Add(r);
            moved += r.Done;
            if (r.Outcome == InventoryOutcome.Refused) left += counts[i];
            else if (sourceSlots.IsAll) left += r.SourceAfter;
            // Filtered: what was asked for minus what moved, counted from before the move. Units in
            // skipped slots were never asked for; units the restore step put back may sit in any
            // slot, a skipped one included, so a read of the allowed slots afterwards would miss them.
            else left += Math.Max(0, counts[i] - r.Done);
            if (r.Outcome == InventoryOutcome.Inconsistent) { ok = false; break; }
        }

        return new InventoryTransferAllResult { Ok = ok, Moved = moved, Left = left, PerItem = rows };
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>
    /// Restore the total of an interchangeable (plain) item after a move that did not conserve
    /// it. Units that vanished are added back to the source only (if that does not restore the
    /// total, the transfer stops as Inconsistent); units that appeared are removed from the
    /// target, then from the source, never more than that side holds. Returns how many units the step restored. Never called for an
    /// item with its own entity: guessing which copy to recreate or delete is refused instead.
    /// </summary>
    private int Compensate(InventoryRef from, InventoryRef to, int item, int total)
    {
        if (!ReadPair(from, to, item, out int s, out int d, out _)) return 0;
        int first = TransferPlanner.Imbalance(total, s, d);
        int now = first;
        for (int attempt = 0; attempt < 2 && now != 0; attempt++)
        {
            if (now > 0)
            {
                // Units vanished: put them back into the source only. A second add elsewhere
                // could double up if the game's add had already dropped a remainder somewhere.
                if (attempt > 0) break;
                _backend.Add(from, item, now, out _);
            }
            else
            {
                // Never ask for more than that inventory holds: past its own count the game's
                // removal falls back to the owner's worn equipment.
                var side = attempt == 0 ? to : from;
                int ask = Math.Min(-now, attempt == 0 ? d : s);
                if (ask > 0) _backend.Remove(side, item, ask, out _);
            }
            Touched?.Invoke(from);
            Touched?.Invoke(to);
            if (!ReadPair(from, to, item, out s, out d, out _)) break;
            now = TransferPlanner.Imbalance(total, s, d);
        }

        return Math.Abs(first) - Math.Abs(now);
    }

    private bool ReadPair(InventoryRef from, InventoryRef to, int item, out int src, out int dst, out string reason)
    {
        src = dst = 0;
        if (!_backend.TryRead(from, _a, out _, out reason)) { reason = "source: " + reason; return false; }
        src = InventoryMath.Count(_a, item);
        if (!_backend.TryRead(to, _b, out _, out reason)) { reason = "target: " + reason; return false; }
        dst = InventoryMath.Count(_b, item);
        return true;
    }

    private bool Check(InventoryRef container, out string reason)
    {
        if (!_backend.IsServer) { reason = "inventories are server-side; this call ran on the client"; return false; }
        if (container.IsNone) { reason = "no container (InventoryRef.None)"; return false; }
        int epoch = _backend.CurrentEpoch;
        if (container.IsStaleIn(epoch))
        {
            reason = "stale container reference " + container + ": taken in world epoch " + container.Epoch
                + ", the world is now in epoch " + epoch + ". Resolve it again (persist a ContainerId).";
            return false;
        }

        if (container.Instance < 0 || container.Instance > 7) { reason = "inventory instance " + container.Instance + " is out of range"; return false; }
        reason = "";
        return true;
    }

    /// <summary>
    /// Same inventory by what the references resolve to, not by how they are spelled: moving
    /// from a character to its own bag entity would let the game's add stack onto the very slot
    /// it then clears. An unresolvable side counts as the same, so the call refuses.
    /// </summary>
    private bool SameInventory(InventoryRef a, InventoryRef b)
    {
        if (a.OwnerIndex == b.OwnerIndex && a.OwnerVersion == b.OwnerVersion && a.Instance == b.Instance) return true;
        if (!_backend.TryInventoryKey(a, out long ka) || !_backend.TryInventoryKey(b, out long kb)) return true;
        return ka == kb;
    }

    private static bool ValidAmount(int amount, out string reason)
    {
        if (amount < 1 || amount > MaxAmount)
        {
            reason = "amount must be from 1 to " + MaxAmount.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        reason = "";
        return true;
    }

    private static string UnknownItem(int item) =>
        "item " + item.ToString(CultureInfo.InvariantCulture) + " is not in the server item table";

    private static InventoryResult Nothing(int item, int requested, string reason,
        int sourceBefore = 0, int sourceAfter = 0, int targetBefore = 0, int targetAfter = 0) =>
        new()
        {
            Outcome = InventoryOutcome.Nothing, Item = item, Requested = requested, Reason = reason,
            SourceBefore = sourceBefore, SourceAfter = sourceAfter == 0 ? sourceBefore : sourceAfter,
            TargetBefore = targetBefore, TargetAfter = targetAfter == 0 ? targetBefore : targetAfter,
        };

    private static InventoryResult Inconsistent(int item, int requested, int done, string reason, int steps,
        int sourceBefore = 0, int sourceAfter = 0, int targetBefore = 0, int targetAfter = 0, int compensated = 0, int equipped = 0) =>
        new()
        {
            Outcome = InventoryOutcome.Inconsistent, Item = item, Requested = requested, Done = done, Reason = reason,
            Steps = steps, SourceBefore = sourceBefore, SourceAfter = sourceAfter,
            TargetBefore = targetBefore, TargetAfter = targetAfter, Compensated = compensated, Equipped = equipped,
        };

    private static InventoryResult Finish(int item, int requested, int done, string detail, int steps,
        int sourceBefore = 0, int sourceAfter = 0, int targetBefore = 0, int targetAfter = 0,
        string shortfall = "", int compensated = 0)
    {
        var outcome = done >= requested ? InventoryOutcome.Complete
            : done > 0 ? InventoryOutcome.Partial
            : InventoryOutcome.Nothing;
        string reason = outcome == InventoryOutcome.Complete ? "" : shortfall;
        if (detail.Length > 0) reason = reason.Length == 0 ? detail : reason + "; " + detail;
        if (compensated != 0)
            reason = (reason.Length == 0 ? "" : reason + "; ") + "compensated " + compensated + " after a move that did not conserve the total";
        return new InventoryResult
        {
            Outcome = outcome, Item = item, Requested = requested, Done = done, Reason = reason, Steps = steps,
            SourceBefore = sourceBefore, SourceAfter = sourceAfter, TargetBefore = targetBefore, TargetAfter = targetAfter,
            Compensated = compensated,
        };
    }
}

/// <summary>Pure pieces of the transfer: which slots to draw from, the conservation check, when to stop.</summary>
internal static class TransferPlanner
{
    /// <summary>The source slots holding <paramref name="item"/>, in buffer order.</summary>
    public static List<InventorySlot> SourceSlots(IReadOnlyList<InventorySlot> slots, int item) =>
        SourceSlots(slots, item, InventorySlotFilter.All);

    /// <summary>The source slots holding <paramref name="item"/> that <paramref name="filter"/> allows, in buffer order.</summary>
    public static List<InventorySlot> SourceSlots(IReadOnlyList<InventorySlot> slots, int item, InventorySlotFilter filter)
    {
        var rows = new List<InventorySlot>();
        if (slots == null) return rows;
        for (int i = 0; i < slots.Count; i++)
            if (slots[i].Item == item && slots[i].Amount > 0 && filter.Allows(slots[i].Slot))
                rows.Add(slots[i]);
        return rows;
    }

    /// <summary>Total of <paramref name="item"/> in the slots <paramref name="filter"/> allows.</summary>
    public static int CountAllowed(IReadOnlyList<InventorySlot> slots, int item, InventorySlotFilter filter)
    {
        if (slots == null || item == 0) return 0;
        long n = 0;
        for (int i = 0; i < slots.Count; i++)
            if (slots[i].Item == item && slots[i].Amount > 0 && filter.Allows(slots[i].Slot))
                n += slots[i].Amount;
        return n > int.MaxValue ? int.MaxValue : (int)n;
    }

    /// <summary>A copy of the slots <paramref name="filter"/> allows, in buffer order.</summary>
    public static InventorySlot[] AllowedSlots(IReadOnlyList<InventorySlot> slots, InventorySlotFilter filter)
    {
        if (slots == null) return Array.Empty<InventorySlot>();
        var rows = new List<InventorySlot>(slots.Count);
        for (int i = 0; i < slots.Count; i++)
            if (filter.Allows(slots[i].Slot)) rows.Add(slots[i]);
        return rows.ToArray();
    }

    /// <summary>
    /// How far the total drifted: positive when units vanished, negative when units appeared.
    /// 0 is the only acceptable value after any move.
    /// </summary>
    public static int Imbalance(int totalBefore, int sourceNow, int targetNow) =>
        totalBefore - (sourceNow + targetNow);

    /// <summary>
    /// True when one unit of <paramref name="item"/> is as good as another, so the restore step may
    /// add or remove units: a stackable or consumable item type (0 or 1) with no slot in either
    /// container carrying its own item entity. Gear and anything with per-copy state is never
    /// recreated or deleted to balance a count.
    /// </summary>
    public static bool IsPlain(int itemType, IReadOnlyList<InventorySlot> source, IReadOnlyList<InventorySlot> target, int item)
    {
        if (itemType != 0 && itemType != 1) return false;
        for (int i = 0; i < source.Count; i++)
            if (source[i].Item == item && source[i].HasItemEntity) return false;
        for (int i = 0; i < target.Count; i++)
            if (target[i].Item == item && target[i].HasItemEntity) return false;
        return true;
    }

    /// <summary>
    /// True when the transfer should not try another slot: the game reported a partial or
    /// failed move, or fewer units moved than were asked for this step (the target is full).
    /// </summary>
    public static bool StopAfter(int gameCode, int movedThisStep, int askedThisStep) =>
        gameCode != 0 || movedThisStep < askedThisStep;
}
