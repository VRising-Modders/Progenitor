using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7: the game-free state of an ordered list: the items, which drawers are open, and a drag in flight. The handle
/// (<see cref="UiOrderedListHandle"/>) draws it; everything that decides an order is here and tested
/// (<c>UiOrderedListTests</c>).
///
/// <para><b>Drag.</b> The rows are slots that never move; a drag rewrites them. While a row is held, the slot under the
/// pointer is the drop target and the slots show the order the list would have after the drop (the held item already in
/// the target slot, the others closed up around it), so the player sees the result before letting go. The target is
/// decided from the slots' fixed centers (<see cref="DropIndex"/>), never from the moving content, so it cannot
/// oscillate.</para>
/// </summary>
internal sealed class UiOrderedListModel
{
    /// <summary>Rows made when the caller does not ask for a capacity and the list starts short.</summary>
    internal const int DefaultCapacity = 8;

    /// <summary>The most rows one list makes (each row is a few game objects; a longer list wants a picker).</summary>
    internal const int MaxCapacity = 64;

    private readonly List<UiListItem> _items = new();
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    internal UiOrderedListModel(IEnumerable<UiListItem>? items, int capacity)
    {
        SetItems(items);
        Capacity = CapacityFor(capacity, _items.Count);
    }

    /// <summary>The rows a list makes for a requested capacity (0: automatic) and its first item count.</summary>
    internal static int CapacityFor(int requested, int initialCount)
    {
        if (requested > 0) return Math.Min(requested, MaxCapacity);
        return Math.Min(Math.Max(initialCount, DefaultCapacity), MaxCapacity);
    }

    /// <summary>The drawer's line count for a requested value, clamped to 1..6.</summary>
    internal static int DetailLinesFor(int requested) => requested < 1 ? 1 : requested > 6 ? 6 : requested;

    internal int Capacity { get; }

    internal IReadOnlyList<UiListItem> Items => _items;

    internal int Count => _items.Count;

    /// <summary>How many items have a row (the rest are past the capacity).</summary>
    internal int Drawn => Math.Min(_items.Count, Capacity);

    internal int Overflow => Math.Max(0, _items.Count - Capacity);

    /// <summary>Replace every item. Duplicate ids are refused (the whole call throws, nothing changes).</summary>
    internal void SetItems(IEnumerable<UiListItem>? items)
    {
        var next = new List<UiListItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (items != null)
        {
            foreach (var item in items)
            {
                if (item == null) continue;
                if (!seen.Add(item.Id)) throw new ArgumentException("Two list items share the id '" + item.Id + "'.", nameof(items));
                next.Add(item);
            }
        }

        CancelDrag();
        _items.Clear();
        _items.AddRange(next);
        _expanded.RemoveWhere(id => !seen.Contains(id) || !HasDetail(Find(id)));
    }

    internal int IndexOf(string? id)
    {
        if (id == null) return -1;
        for (int i = 0; i < _items.Count; i++)
            if (string.Equals(_items[i].Id, id, StringComparison.Ordinal)) return i;
        return -1;
    }

    internal UiListItem? Find(string? id)
    {
        int i = IndexOf(id);
        return i < 0 ? null : _items[i];
    }

    internal bool CanMoveUp(int index) => index > 0 && index < _items.Count;

    internal bool CanMoveDown(int index) => index >= 0 && index < _items.Count - 1;

    /// <summary>Move the item at <paramref name="from"/> to <paramref name="to"/> (clamped). False when nothing moved.</summary>
    internal bool Move(int from, int to, out UiListReorder change)
    {
        change = default;
        if (from < 0 || from >= _items.Count) return false;
        if (to < 0) to = 0;
        if (to > _items.Count - 1) to = _items.Count - 1;
        if (to == from) return false;
        var item = _items[from];
        _items.RemoveAt(from);
        _items.Insert(to, item);
        change = new UiListReorder(item.Id, from, to, Order());
        return true;
    }

    /// <summary>Remove <paramref name="id"/>. False when it is not in the list.</summary>
    internal bool Remove(string? id, out UiListItem? removed, out int index)
    {
        index = IndexOf(id);
        removed = null;
        if (index < 0) return false;
        CancelDrag();
        removed = _items[index];
        _items.RemoveAt(index);
        _expanded.Remove(removed.Id);
        return true;
    }

    internal static bool HasDetail(UiListItem? item) => item != null && !string.IsNullOrWhiteSpace(item.Detail);

    internal bool IsExpanded(string? id) => id != null && _expanded.Contains(id);

    /// <summary>Open or close a drawer. False when the item has none or nothing changed.</summary>
    internal bool SetExpanded(string? id, bool expanded)
    {
        var item = Find(id);
        if (item == null) return false;
        if (expanded && !HasDetail(item)) return false;
        return expanded ? _expanded.Add(item.Id) : _expanded.Remove(item.Id);
    }

    internal IReadOnlyList<string> Order()
    {
        var order = new string[_items.Count];
        for (int i = 0; i < order.Length; i++) order[i] = _items[i].Id;
        return order;
    }

    // ---- Drag ------------------------------------------------------------------------------

    /// <summary>The held item's index, or -1.</summary>
    internal int DragFrom { get; private set; } = -1;

    /// <summary>The slot the held item would drop into, or -1.</summary>
    internal int DragTarget { get; private set; } = -1;

    internal bool Dragging => DragFrom >= 0;

    /// <summary>Hold the item in slot <paramref name="index"/>. False for an empty slot or while a drag runs.</summary>
    internal bool BeginDrag(int index)
    {
        if (Dragging || index < 0 || index >= Drawn) return false;
        DragFrom = index;
        DragTarget = index;
        return true;
    }

    /// <summary>
    /// The drop slot for a pointer at <paramref name="pointerY"/> (screen pixels, up is larger) over slots whose vertical
    /// centers are <paramref name="slotCenters"/> (top slot first): the number of slots whose center is above the pointer,
    /// clamped to the drawn slots. NaN centers (an unreadable row) are skipped; with none readable, -1.
    /// </summary>
    internal static int DropIndex(float pointerY, IReadOnlyList<float> slotCenters)
    {
        if (slotCenters == null || slotCenters.Count == 0 || float.IsNaN(pointerY) || float.IsInfinity(pointerY)) return -1;
        int above = 0;
        int readable = 0;
        for (int i = 0; i < slotCenters.Count; i++)
        {
            float c = slotCenters[i];
            if (float.IsNaN(c) || float.IsInfinity(c)) continue;
            readable++;
            if (c > pointerY) above++;
        }

        if (readable == 0) return -1;
        return Math.Min(above, slotCenters.Count - 1);
    }

    /// <summary>Move the drop target. True when it changed (the slots need a repaint).</summary>
    internal bool DragTo(int target)
    {
        if (!Dragging || target < 0) return false;
        if (target > Drawn - 1) target = Drawn - 1;
        if (target == DragTarget) return false;
        DragTarget = target;
        return true;
    }

    /// <summary>Let go: the held item moves to the target. False (and nothing changes) when it lands where it started.</summary>
    internal bool EndDrag(out UiListReorder change)
    {
        change = default;
        if (!Dragging) return false;
        int from = DragFrom;
        int to = DragTarget;
        DragFrom = DragTarget = -1;
        return Move(from, to, out change);
    }

    /// <summary>Drop the drag without moving anything.</summary>
    internal void CancelDrag() => DragFrom = DragTarget = -1;

    /// <summary>
    /// The items in slot order as drawn now: the list itself, or during a drag the order the drop would leave.
    /// </summary>
    internal IReadOnlyList<UiListItem> Display()
    {
        if (!Dragging || DragTarget == DragFrom) return _items;
        var view = new List<UiListItem>(_items);
        var held = view[DragFrom];
        view.RemoveAt(DragFrom);
        view.Insert(DragTarget, held);
        return view;
    }
}
