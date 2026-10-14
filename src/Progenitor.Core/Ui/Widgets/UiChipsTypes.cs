using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7b (experimental): one chip of a chip set (<see cref="UiWidgets.Chips"/>). <see cref="Id"/> is the caller's stable
/// key: events name it, and two chips with the same id are refused. Everything else is presentation.
/// </summary>
public sealed class UiChip
{
    /// <summary>A chip with a stable <paramref name="id"/> and the caption <paramref name="label"/>.</summary>
    public UiChip(string id, string label)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("A chip needs an id.", nameof(id));
        Id = id;
        Label = label ?? "";
    }

    /// <summary>The caller's stable key. Never shown.</summary>
    public string Id { get; }

    /// <summary>The chip's caption.</summary>
    public string Label { get; set; }

    /// <summary>A short note drawn after the caption (an item's stock, a count); empty for none.</summary>
    public string Note { get; set; } = "";

    /// <summary>
    /// The chip's icon: a <c>UnityEngine.Sprite</c> (a game item's own icon, see <see cref="ItemPrefab"/>), or null. A chip
    /// with an icon or an item prefab is an item chip: the icon sits in the kit's icon well before the caption.
    /// </summary>
    public object? Icon { get; set; }

    /// <summary>
    /// A game item's prefab id (<c>PrefabGUID.GuidHash</c>) whose own inventory icon the chip shows when <see cref="Icon"/>
    /// is null; 0 for none. Resolved on the client from the game's item data.
    /// </summary>
    public int ItemPrefab { get; set; }

    /// <summary>False draws no remove button on this chip (the set's <see cref="UiChipsOptions.Remove"/> must be on too).</summary>
    public bool Removable { get; set; } = true;

    /// <summary>The chip starts selected (drawn on the gold plate). The set keeps the state from then on.</summary>
    public bool Selected { get; set; }

    /// <summary>Hover text for the chip (a tooltip, under the chip's caption as its title); empty for none.</summary>
    public string Tooltip { get; set; } = "";

    /// <summary>
    /// P7b (experimental): a term of the plugin's glossary (<see cref="UiTerms.Define(string, string, string)"/>) this chip's
    /// caption explains: the caption gets the circle-i marker and hovering the chip shows the term's explanation (in place
    /// of <see cref="Tooltip"/>). Empty for none.
    /// </summary>
    public string Term { get; set; } = "";

    /// <summary>True when the chip shows an icon well (<see cref="Icon"/> or <see cref="ItemPrefab"/>).</summary>
    public bool IsItem => Icon != null || ItemPrefab != 0;

    /// <inheritdoc />
    public override string ToString() => Id + ":" + Label;
}

/// <summary>P7b (experimental): what a chip set offers. Every option has the common default.</summary>
public sealed class UiChipsOptions
{
    /// <summary>
    /// The most lines the chips wrap onto (1 to 8; 3 by default). The set keeps its place on the page, so its line rows
    /// are made once; chips that do not fit are counted on a <c>+N more</c> chip (<see cref="IUiChips.OnMore"/>).
    /// </summary>
    public int MaxLines { get; set; } = 3;

    /// <summary>The most chips on one line (2 to 16; 8 by default), whatever the row's width.</summary>
    public int MaxPerLine { get; set; } = 8;

    /// <summary>
    /// A click flips the chip's selected state (combinable filters) before <see cref="IUiChips.OnClick"/> is raised. Off
    /// by default: a click only raises the event (open the belt, filter to the category).
    /// </summary>
    public bool Toggle { get; set; }

    /// <summary>Draw the remove button on removable chips. On by default.</summary>
    public bool Remove { get; set; } = true;

    /// <summary>Draw an add chip after the last chip; its press raises <see cref="IUiChips.OnAdd"/>. Off by default.</summary>
    public bool Add { get; set; }

    /// <summary>The add chip's caption (<c>+</c> by default; <c>+ Add belt</c> reads better on a wide page).</summary>
    public string AddText { get; set; } = "+";

    /// <summary>Hover text for the add chip; empty for none.</summary>
    public string AddTooltip { get; set; } = "";

    /// <summary>The muted text drawn while the set has no chips (beside the add chip, when there is one).</summary>
    public string EmptyText { get; set; } = "None yet.";

    /// <summary>
    /// The width, in panel units, the chips are packed for. 0 (the default) measures the row the set is drawn in and
    /// packs again when it changes; until the row is first measured it packs for the narrowest checked row (560).
    /// </summary>
    public float RowWidth { get; set; }
}

/// <summary>
/// P7b (experimental): a set of chips on a panel that wrap onto lines: item chips (a game item's icon, a caption, a
/// remove button) and plain chips (a caption, optionally removable), plus an optional add chip. Drawn as the Install
/// tab's filter chips (the game's popup button; a selected chip on the gold plate). The set changes itself and then tells
/// the plugin (<see cref="OnRemove"/>); call <see cref="SetChips"/> to replace or undo. Lines are rewritten in place.
/// </summary>
public interface IUiChips : IUiHandle
{
    /// <summary>The chips in order (all of them, also those counted on the <c>+N more</c> chip).</summary>
    IReadOnlyList<UiChip> Chips { get; }

    /// <summary>How many chips are drawn now.</summary>
    int Shown { get; }

    /// <summary>How many chips did not fit and are counted on the <c>+N more</c> chip.</summary>
    int Overflow { get; }

    /// <summary>The most chips the lines can hold (<see cref="UiChipsOptions.MaxLines"/> × <see cref="UiChipsOptions.MaxPerLine"/>).</summary>
    int Capacity { get; }

    /// <summary>The ids of the selected chips, in chip order.</summary>
    IReadOnlyList<string> SelectedIds { get; }

    /// <summary>Replace every chip. No event. Selection is taken from each chip's <see cref="UiChip.Selected"/>.</summary>
    void SetChips(IEnumerable<UiChip> chips);

    /// <summary>Remove chip <paramref name="id"/>; raises <see cref="OnRemove"/>. False when it is not in the set.</summary>
    bool Remove(string id);

    /// <summary>Select or clear chip <paramref name="id"/>. No event. False when it is not in the set or nothing changed.</summary>
    bool SetSelected(string id, bool selected);

    /// <summary>Whether chip <paramref name="id"/> is selected.</summary>
    bool IsSelected(string id);

    /// <summary>After a chip was pressed (in <see cref="UiChipsOptions.Toggle"/> mode, after its selection flipped).</summary>
    IDisposable OnClick(Action<UiChip> handler);

    /// <summary>After a chip was removed, by its remove button or <see cref="Remove"/>.</summary>
    IDisposable OnRemove(Action<UiChip> handler);

    /// <summary>The add chip was pressed.</summary>
    IDisposable OnAdd(Action handler);

    /// <summary>The <c>+N more</c> chip was pressed: the chips it counts, in order.</summary>
    IDisposable OnMore(Action<IReadOnlyList<UiChip>> handler);

    /// <summary>
    /// Readback: <c>{kind: chips, key, chips [{id, label, selected, removable, item, line, slot}], shown, overflow,
    /// capacity, rowWidth, lines, add, more}</c>; a chip that is not drawn has line and slot -1.
    /// </summary>
    Dictionary<string, object?> Describe();
}
