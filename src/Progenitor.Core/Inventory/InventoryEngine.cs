using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Inventory;

/// <summary>Item facts the engine needs, from the item's <c>ItemData</c>.</summary>
internal readonly struct InventoryItemInfo
{
    /// <summary>Construct item facts.</summary>
    public InventoryItemInfo(int maxAmount, long category, int itemType)
    {
        MaxAmount = maxAmount;
        Category = category;
        ItemType = itemType;
    }

    /// <summary><c>ItemData.MaxAmount</c>: the stack size.</summary>
    public int MaxAmount { get; }

    /// <summary><c>ItemData.ItemCategory</c> flags.</summary>
    public long Category { get; }

    /// <summary><c>ItemData.ItemType</c> (0 is stackable).</summary>
    public int ItemType { get; }
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

        bool called = _backend.Add(container, item, amount, out string detail);
        Touched?.Invoke(container);
        if (!_backend.TryRead(container, _a, out _, out why))
            return Inconsistent(item, amount, 0, "could not read the container back after the add: " + why, 1);
        int after = InventoryMath.Count(_a, item);
        int done = after - before;
        if (done < 0 || done > amount)
        {
            string text = "add read-back out of range: before=" + before + " after=" + after + " requested=" + amount;
            _logError?.Invoke("Inventory: " + text + " container=" + container + " item=" + item);
            return Inconsistent(item, amount, Math.Max(0, done), text, 1, targetBefore: before, targetAfter: after);
        }

        return Finish(item, amount, done, called ? "" : detail, steps: 1, targetBefore: before, targetAfter: after,
            shortfall: "only " + done + " of " + amount + " fit");
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

    internal InventoryResult Transfer(InventoryRef from, InventoryRef to, int item, int amount)
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

        int srcBefore = InventoryMath.Count(_a, item);
        int dstBefore = InventoryMath.Count(_b, item);
        if (srcBefore == 0)
            return Nothing(item, amount, "the source holds none of this item", srcBefore, srcBefore, dstBefore, dstBefore);
        if (!InventoryMath.IsAllowed(restriction, item, info.Category))
            return Nothing(item, amount, "the target refuses this item (" + restriction + ")", srcBefore, srcBefore, dstBefore, dstBefore);
        if (InventoryMath.FreeSpace(_b, item, info.MaxAmount, true) <= 0)
            return Nothing(item, amount, "the target is full for this item", srcBefore, srcBefore, dstBefore, dstBefore);

        // The source slots holding the item, captured before the first move changes the buffer.
        var plan = TransferPlanner.SourceSlots(_a, item);
        int want = Math.Min(amount, srcBefore);
        int total = srcBefore + dstBefore;
        int src = srcBefore, dst = dstBefore, steps = 0, compensated = 0;
        string stop = "";

        for (int p = 0; p < plan.Count && dst - dstBefore < want; p++)
        {
            int step = Math.Min(want - (dst - dstBefore), plan[p].Amount);
            if (step <= 0) break;
            int code = _backend.MoveSlot(from, plan[p].Slot, to, step, out string detail);
            steps++;
            Touched?.Invoke(from);
            Touched?.Invoke(to);

            if (!ReadPair(from, to, item, out int srcNow, out int dstNow, out why))
                return Inconsistent(item, amount, Math.Max(0, dst - dstBefore),
                    "could not read the containers back after a move: " + why, steps, srcBefore, src, dstBefore, dst, compensated);

            int imbalance = TransferPlanner.Imbalance(total, srcNow, dstNow);
            if (imbalance != 0)
            {
                compensated += Compensate(from, to, item, total);
                if (!ReadPair(from, to, item, out srcNow, out dstNow, out why)
                    || TransferPlanner.Imbalance(total, srcNow, dstNow) != 0)
                {
                    string text = "the total of item " + item + " changed and could not be restored: before "
                        + srcBefore + "+" + dstBefore + ", now " + srcNow + "+" + dstNow;
                    _logError?.Invoke("Inventory: transfer " + from + " -> " + to + ": " + text);
                    return Inconsistent(item, amount, Math.Max(0, dstNow - dstBefore), text, steps,
                        srcBefore, srcNow, dstBefore, dstNow, compensated);
                }
            }

            int movedThisStep = dstNow - dst;
            src = srcNow;
            dst = dstNow;
            if (movedThisStep <= 0 || TransferPlanner.StopAfter(code, movedThisStep, step))
            {
                stop = movedThisStep <= 0
                    ? "the game moved nothing (" + detail + ")"
                    : "the target filled up (" + detail + ")";
                break;
            }
        }

        int moved = dst - dstBefore;
        string shortfall = moved < want
            ? (stop.Length > 0 ? stop : "moved " + moved + " of " + want)
            : "the source held only " + srcBefore;
        return Finish(item, amount, moved, "", steps, srcBefore, src, dstBefore, dst, shortfall, compensated);
    }

    internal InventoryTransferAllResult TransferAll(InventoryRef from, InventoryRef to, Func<int, bool>? filter)
    {
        if (!Check(from, out string why) || !Check(to, out why))
            return new InventoryTransferAllResult { Ok = false, Reason = why };
        if (SameInventory(from, to))
            return new InventoryTransferAllResult { Ok = false, Reason = "source and target are the same inventory" };
        if (!_backend.TryRead(from, _a, out _, out why))
            return new InventoryTransferAllResult { Ok = false, Reason = "source: " + why };

        var items = InventoryMath.DistinctItems(_a, _a.Count, filter);
        var counts = new int[items.Count];
        for (int i = 0; i < items.Count; i++) counts[i] = InventoryMath.Count(_a, items[i]);

        var rows = new List<InventoryResult>(items.Count);
        int moved = 0, left = 0;
        bool ok = true;
        for (int i = 0; i < items.Count; i++)
        {
            var r = Transfer(from, to, items[i], Math.Min(counts[i], MaxAmount));
            rows.Add(r);
            moved += r.Done;
            left += r.Outcome == InventoryOutcome.Refused ? counts[i] : r.SourceAfter;
            if (r.Outcome == InventoryOutcome.Inconsistent) { ok = false; break; }
        }

        return new InventoryTransferAllResult { Ok = ok, Moved = moved, Left = left, PerItem = rows };
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>
    /// Restore the total after a move that did not conserve it. Units that vanished are added
    /// back to the source, then to the target if the source refuses them; units that appeared are
    /// removed from the target, then from the source. Returns how many units the step restored.
    /// </summary>
    private int Compensate(InventoryRef from, InventoryRef to, int item, int total)
    {
        if (!ReadPair(from, to, item, out int s, out int d, out _)) return 0;
        int first = TransferPlanner.Imbalance(total, s, d);
        int now = first;
        for (int attempt = 0; attempt < 2 && now != 0; attempt++)
        {
            if (now > 0)
                _backend.Add(attempt == 0 ? from : to, item, now, out _);
            else
                _backend.Remove(attempt == 0 ? to : from, item, -now, out _);
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

    private static bool SameInventory(InventoryRef a, InventoryRef b) =>
        a.OwnerIndex == b.OwnerIndex && a.OwnerVersion == b.OwnerVersion && a.Instance == b.Instance;

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
        int sourceBefore = 0, int sourceAfter = 0, int targetBefore = 0, int targetAfter = 0, int compensated = 0) =>
        new()
        {
            Outcome = InventoryOutcome.Inconsistent, Item = item, Requested = requested, Done = done, Reason = reason,
            Steps = steps, SourceBefore = sourceBefore, SourceAfter = sourceAfter,
            TargetBefore = targetBefore, TargetAfter = targetAfter, Compensated = compensated,
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
    public static List<InventorySlot> SourceSlots(IReadOnlyList<InventorySlot> slots, int item)
    {
        var rows = new List<InventorySlot>();
        if (slots == null) return rows;
        for (int i = 0; i < slots.Count; i++)
            if (slots[i].Item == item && slots[i].Amount > 0)
                rows.Add(slots[i]);
        return rows;
    }

    /// <summary>
    /// How far the total drifted: positive when units vanished, negative when units appeared.
    /// 0 is the only acceptable value after any move.
    /// </summary>
    public static int Imbalance(int totalBefore, int sourceNow, int targetNow) =>
        totalBefore - (sourceNow + targetNow);

    /// <summary>
    /// True when the transfer should not try another slot: the game reported a partial or
    /// failed move, or fewer units moved than were asked for this step (the target is full).
    /// </summary>
    public static bool StopAfter(int gameCode, int movedThisStep, int askedThisStep) =>
        gameCode != 0 || movedThisStep < askedThisStep;
}
