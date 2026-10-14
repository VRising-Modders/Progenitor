using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7: one entry of an ordered list (<see cref="UiWidgets.OrderedList"/>). <see cref="Id"/> is the caller's stable key:
/// events name it, and two items with the same id are refused. Everything else is presentation.
/// </summary>
public sealed class UiListItem
{
    /// <summary>An item with a stable <paramref name="id"/> and the caption <paramref name="title"/>.</summary>
    public UiListItem(string id, string title)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("An item needs an id.", nameof(id));
        Id = id;
        Title = title ?? "";
    }

    /// <summary>The caller's stable key. Never shown.</summary>
    public string Id { get; }

    /// <summary>The row's caption.</summary>
    public string Title { get; set; }

    /// <summary>A short muted note after the caption (a plot, a count); empty for none. Dropped first on a narrow row.</summary>
    public string Note { get; set; } = "";

    /// <summary>The text the row's drawer shows when it is expanded; empty means the row has no drawer.</summary>
    public string Detail { get; set; } = "";

    /// <summary>A count badge at the row's right (<c>"12"</c>); empty for none.</summary>
    public string Badge { get; set; } = "";

    /// <summary>
    /// The row's icon: a <c>UnityEngine.Sprite</c> (a game item's own icon, see <see cref="ItemPrefab"/>), or null for
    /// none. Never a plugin-made texture that the plugin destroys before the list.
    /// </summary>
    public object? Icon { get; set; }

    /// <summary>
    /// A game item's prefab id (<c>PrefabGUID.GuidHash</c>) whose own inventory icon the row shows when
    /// <see cref="Icon"/> is null; 0 for none. Resolved on the client from the game's item data.
    /// </summary>
    public int ItemPrefab { get; set; }

    /// <summary>False keeps the remove button grayed for this row.</summary>
    public bool Removable { get; set; } = true;

    /// <summary>A line of hover text for the row (a tooltip); empty for none.</summary>
    public string Tooltip { get; set; } = "";

    /// <inheritdoc />
    public override string ToString() => Id + ":" + Title;
}

/// <summary>P7: a completed move in an ordered list: which item, from where, to where, and the order now.</summary>
public readonly struct UiListReorder
{
    /// <summary>Make the event (tests and Core; plugins receive it).</summary>
    public UiListReorder(string id, int from, int to, IReadOnlyList<string> order)
    {
        Id = id ?? "";
        From = from;
        To = to;
        Order = order ?? Array.Empty<string>();
    }

    /// <summary>The moved item's id.</summary>
    public string Id { get; }

    /// <summary>Its index before the move.</summary>
    public int From { get; }

    /// <summary>Its index after the move.</summary>
    public int To { get; }

    /// <summary>Every item's id in the new order.</summary>
    public IReadOnlyList<string> Order { get; }

    /// <inheritdoc />
    public override string ToString() => Id + " " + From + "->" + To;
}

/// <summary>P7: what an ordered list offers. Every option has the common default.</summary>
public sealed class UiOrderedListOptions
{
    /// <summary>
    /// The most rows the list can show. The list keeps its place on the page, so its rows are made once; 0 (the default)
    /// makes room for the first items and up to <see cref="UiOrderedListModel.DefaultCapacity"/>. Items past the
    /// capacity are kept and reported by <see cref="IUiOrderedList.Overflow"/> but not drawn.
    /// </summary>
    public int Capacity { get; set; }

    /// <summary>Show the grip and the up and down buttons. On by default.</summary>
    public bool Reorder { get; set; } = true;

    /// <summary>Show the remove button. On by default.</summary>
    public bool Remove { get; set; } = true;

    /// <summary>Lines of text an expanded row's drawer has room for (1 to 6; 2 by default).</summary>
    public int DetailLines { get; set; } = 2;

    /// <summary>The muted line drawn while the list is empty.</summary>
    public string EmptyText { get; set; } = "Nothing here yet.";
}

/// <summary>
/// P7: an ordered list on a panel: drag a row by its grip or use its up and down buttons to reorder, expand a row to read
/// its drawer, remove a row. The list changes itself and then tells the plugin (<see cref="OnReorder"/>,
/// <see cref="OnRemove"/>); call <see cref="SetItems"/> to replace or undo. Rows are rewritten in place, so a move never
/// rebuilds the page or loses its scroll.
/// </summary>
public interface IUiOrderedList : IUiHandle
{
    /// <summary>The items in their current order (all of them, also past the capacity).</summary>
    IReadOnlyList<UiListItem> Items { get; }

    /// <summary>The rows the list can draw (<see cref="UiOrderedListOptions.Capacity"/>).</summary>
    int Capacity { get; }

    /// <summary>How many items are past the capacity and not drawn.</summary>
    int Overflow { get; }

    /// <summary>Replace every item. No event. Expanded drawers stay open for ids that remain.</summary>
    void SetItems(IEnumerable<UiListItem> items);

    /// <summary>Move item <paramref name="id"/> to <paramref name="index"/>; raises <see cref="OnReorder"/>. False when nothing moved.</summary>
    bool Move(string id, int index);

    /// <summary>Remove item <paramref name="id"/>; raises <see cref="OnRemove"/>. False when it is not in the list.</summary>
    bool Remove(string id);

    /// <summary>Open or close item <paramref name="id"/>'s drawer. False when it has none.</summary>
    bool SetExpanded(string id, bool expanded);

    /// <summary>Whether item <paramref name="id"/>'s drawer is open.</summary>
    bool IsExpanded(string id);

    /// <summary>After every move, by a drag, a button or <see cref="Move"/>.</summary>
    IDisposable OnReorder(Action<UiListReorder> handler);

    /// <summary>After a row was removed, by its button or <see cref="Remove"/>.</summary>
    IDisposable OnRemove(Action<UiListItem> handler);

    /// <summary>After a drawer opened (<c>true</c>) or closed.</summary>
    IDisposable OnExpand(Action<UiListItem, bool> handler);

    /// <summary>Readback: <c>{kind: orderedlist, key, items [{id, title, expanded}], capacity, overflow, drag}</c>.</summary>
    Dictionary<string, object?> Describe();
}
