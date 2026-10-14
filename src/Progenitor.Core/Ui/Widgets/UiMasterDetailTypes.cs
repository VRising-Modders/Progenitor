using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7b: one entry of a master-detail list (<see cref="UiWidgets.MasterDetail"/>): a belt, a category, a profile.
/// <see cref="Id"/> is the caller's stable key; two entries with the same id are refused.
/// </summary>
public sealed class UiMasterEntry
{
    /// <summary>An entry with a stable <paramref name="id"/> and the caption <paramref name="title"/>.</summary>
    public UiMasterEntry(string id, string title)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("An entry needs an id.", nameof(id));
        Id = id;
        Title = title ?? "";
    }

    /// <summary>The caller's stable key. Never shown.</summary>
    public string Id { get; }

    /// <summary>The caption in the list, and what the search matches first.</summary>
    public string Title { get; set; }

    /// <summary>A short muted note after the caption (<c>3 chests · plot 2</c>); empty for none. The search matches it.</summary>
    public string Note { get; set; } = "";

    /// <summary>A short count after the note in the client gold, right-aligned in tabular figures (<c>12</c>, at most three characters: <c>99+</c>); empty for none.</summary>
    public string Badge { get; set; } = "";

    /// <summary>A <c>UnityEngine.Sprite</c> for the entry's icon, or null.</summary>
    public object? Icon { get; set; }

    /// <summary>A game item's prefab id whose own inventory icon the entry shows when <see cref="Icon"/> is null; 0 for none.</summary>
    public int ItemPrefab { get; set; }

    /// <summary>Hover text for the entry (a tooltip); empty for none.</summary>
    public string Tooltip { get; set; } = "";

    /// <summary>More words the search matches (an alias, a member name); never shown.</summary>
    public IReadOnlyList<string> Keywords { get; set; } = Array.Empty<string>();

    /// <summary>
    /// A glossary term the caption explains (<see cref="UiTerms.Define(string, string, string)"/> on the panel's plugin):
    /// the caption gets the help marker, and hovering the entry shows the term's explanation instead of
    /// <see cref="Tooltip"/>. Empty for none.
    /// </summary>
    public string Term { get; set; } = "";

    /// <inheritdoc />
    public override string ToString() => Id + ":" + Title;
}

/// <summary>P7b: a button on a detail line (<see cref="UiDetailLine.Title"/>, <see cref="UiDetailLine.Actions"/>).</summary>
public sealed class UiDetailButton
{
    /// <summary>A button raising <see cref="IUiMasterDetail.OnAction"/> with <paramref name="id"/>, captioned <paramref name="caption"/>.</summary>
    public UiDetailButton(string id, string caption)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("A button needs an id.", nameof(id));
        Id = id;
        Caption = caption ?? "";
    }

    /// <summary>The action id the event carries (<see cref="UiDetailAction.ActionId"/>).</summary>
    public string Id { get; }

    /// <summary>The caption: a verb or a short verb phrase (<c>Rename</c>, <c>Locate all</c>).</summary>
    public string Caption { get; }

    /// <summary>False draws the button grayed; it raises nothing and the controller passes it over.</summary>
    public bool Enabled { get; set; } = true;

    /// <inheritdoc />
    public override string ToString() => Id + ":" + Caption;
}

/// <summary>P7b: what a detail line is (<see cref="UiDetailLine"/>).</summary>
public enum UiDetailLineKind
{
    /// <summary>An empty line (space between blocks).</summary>
    Blank = 0,

    /// <summary>The entry's name with its buttons at the right (<c>Ore in [Rename]</c>).</summary>
    Title = 1,

    /// <summary>A line of text, muted unless a tone is given (<c>3 chests · 2 plots</c>).</summary>
    Text = 2,

    /// <summary>A section heading in the gold (<c>Used by (4)</c>).</summary>
    Heading = 3,

    /// <summary>A link that raises <see cref="IUiMasterDetail.OnAction"/> with <see cref="UiDetailAction.Open"/>, with an optional muted note.</summary>
    Link = 4,

    /// <summary>A row of buttons (<c>[Locate all] [Delete]</c>).</summary>
    Actions = 5,

    /// <summary>An ordered member list: one line per member, reordered by drag or arrows, removed, expanded.</summary>
    Members = 6,
}

/// <summary>
/// P7b: one line of a master-detail widget's detail side, built with the static factories. The widget draws the lines in
/// order into its fixed rows; a <see cref="UiDetailLineKind.Members"/> line becomes one line per member.
/// </summary>
public sealed class UiDetailLine
{
    private UiDetailLine(UiDetailLineKind kind, string id, string text)
    {
        Kind = kind;
        Id = id ?? "";
        Text = text ?? "";
    }

    /// <summary>What the line is.</summary>
    public UiDetailLineKind Kind { get; }

    /// <summary>The line's id: the event's <see cref="UiDetailAction.LineId"/>.</summary>
    public string Id { get; }

    /// <summary>The line's words (the title, the text, the heading, the link caption).</summary>
    public string Text { get; }

    /// <summary>A muted note after a link's caption; empty for none.</summary>
    public string Note { get; private set; } = "";

    /// <summary>A heading's glossary term (its caption gets the help marker and explains it when hovered); empty for none.</summary>
    public string Term { get; private set; } = "";

    /// <summary>A text line's tone (<see cref="LabelTone.Muted"/> by default).</summary>
    public LabelTone Tone { get; private set; } = LabelTone.Muted;

    /// <summary>The line's buttons (a title or an action line); empty otherwise.</summary>
    public IReadOnlyList<UiDetailButton> Buttons { get; private set; } = Array.Empty<UiDetailButton>();

    /// <summary>A members line's items, in order; empty otherwise.</summary>
    public IReadOnlyList<UiListItem> Members { get; private set; } = Array.Empty<UiListItem>();

    /// <summary>A members line: the grip and the up and down buttons are shown.</summary>
    public bool Reorder { get; private set; }

    /// <summary>A members line: the remove button is shown.</summary>
    public bool Remove { get; private set; }

    /// <summary>A members line: the muted line drawn while it has no member.</summary>
    public string EmptyText { get; private set; } = "";

    /// <summary>The most buttons one line carries.</summary>
    public const int MaxButtons = 3;

    /// <summary>The entry's name with up to <see cref="MaxButtons"/> buttons at the line's right (line id <c>title</c>).</summary>
    public static UiDetailLine Title(string text, params UiDetailButton[] buttons) =>
        new(UiDetailLineKind.Title, "title", text) { Buttons = CheckButtons(buttons) };

    /// <summary>A line of text in <paramref name="tone"/> (muted by default). One line: a longer text ends in an ellipsis.</summary>
    public static UiDetailLine Line(string text, LabelTone tone = LabelTone.Muted) =>
        new(UiDetailLineKind.Text, "", text) { Tone = tone };

    /// <summary>
    /// A gold section heading; <paramref name="count"/> 0 or more is appended as <c>(n)</c>. <paramref name="term"/> marks it
    /// with a glossary term of the panel's plugin (<see cref="UiTerms"/>); empty for none.
    /// </summary>
    public static UiDetailLine Heading(string text, int count = -1, string term = "") =>
        new(UiDetailLineKind.Heading, "", count >= 0 ? (text ?? "") + " (" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : text)
        {
            Term = (term ?? "").Trim(),
        };

    /// <summary>A link with the line id <paramref name="id"/>; a click raises <see cref="UiDetailAction.Open"/>.</summary>
    public static UiDetailLine Link(string id, string text, string note = "")
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("A link needs an id.", nameof(id));
        return new UiDetailLine(UiDetailLineKind.Link, id, text) { Note = note ?? "" };
    }

    /// <summary>A row of up to <see cref="MaxButtons"/> buttons from the detail side's left edge (line id <c>actions</c>).</summary>
    public static UiDetailLine Actions(params UiDetailButton[] buttons) =>
        new(UiDetailLineKind.Actions, "actions", "") { Buttons = CheckButtons(buttons) };

    /// <summary>
    /// The ordered member list (line id <c>members</c>; one per detail). Reorder by the grip or the arrows, remove with
    /// the button, expand a member with a <see cref="UiListItem.Detail"/> to read it. The widget changes the list itself and
    /// raises <see cref="IUiMasterDetail.OnReorder"/> / <see cref="IUiMasterDetail.OnRemove"/>.
    /// </summary>
    public static UiDetailLine MemberList(IEnumerable<UiListItem> items, bool reorder = true, bool remove = true, string emptyText = "No members yet.")
    {
        var list = new List<UiListItem>();
        if (items != null)
            foreach (var i in items)
                if (i != null) list.Add(i);
        return new UiDetailLine(UiDetailLineKind.Members, "members", "")
        {
            Members = list,
            Reorder = reorder,
            Remove = remove,
            EmptyText = emptyText ?? "",
        };
    }

    /// <summary>An empty line.</summary>
    public static UiDetailLine Blank() => new(UiDetailLineKind.Blank, "", "");

    private static IReadOnlyList<UiDetailButton> CheckButtons(UiDetailButton[]? buttons)
    {
        var list = new List<UiDetailButton>();
        if (buttons != null)
            foreach (var b in buttons)
                if (b != null) list.Add(b);
        if (list.Count > MaxButtons) throw new ArgumentException("A detail line carries at most " + MaxButtons + " buttons.", nameof(buttons));
        return list;
    }

    /// <inheritdoc />
    public override string ToString() => Kind + (Id.Length > 0 ? "#" + Id : "") + ":" + Text;
}

/// <summary>P7b: a press on a master-detail widget: a detail button, a link, or the master list's new-entry line.</summary>
public readonly struct UiDetailAction
{
    /// <summary>The action id of a link line's click.</summary>
    public const string Open = "open";

    /// <summary>The action id of the master list's new-entry line (<see cref="UiMasterDetailOptions.NewCaption"/>).</summary>
    public const string New = "new";

    /// <summary>The line id of the master list's new-entry line.</summary>
    public const string MasterLine = "master";

    /// <summary>Make the event (tests and Core; plugins receive it).</summary>
    public UiDetailAction(int tab, string entryId, string lineId, string actionId)
    {
        Tab = tab;
        EntryId = entryId ?? "";
        LineId = lineId ?? "";
        ActionId = actionId ?? "";
    }

    /// <summary>The tab it happened on (0 without tabs).</summary>
    public int Tab { get; }

    /// <summary>The selected entry's id then; empty when none was selected.</summary>
    public string EntryId { get; }

    /// <summary>The line's id (<see cref="UiDetailLine.Id"/>; <see cref="MasterLine"/> for the new-entry line).</summary>
    public string LineId { get; }

    /// <summary>The button's id, <see cref="Open"/> for a link, <see cref="New"/> for the new-entry line.</summary>
    public string ActionId { get; }

    /// <inheritdoc />
    public override string ToString() => "tab " + Tab + " " + (EntryId.Length > 0 ? EntryId : "-") + " " + LineId + "/" + ActionId;
}

/// <summary>P7b: what a master-detail widget offers. Every option has a usable default.</summary>
public sealed class UiMasterDetailOptions
{
    /// <summary>Tab captions above the list (<c>Belts</c>, <c>Categories</c>); null or empty for no tab row.</summary>
    public IReadOnlyList<string>? Tabs { get; set; }

    /// <summary>
    /// The rows the widget makes (6 to 30; 14 by default). They are made once and rewritten in place; a longer list or
    /// detail scrolls inside its own half.
    /// </summary>
    public int Rows { get; set; } = 14;

    /// <summary>
    /// The share of the row the master list's caption and note take (0.2 to 0.45; 0.32 by default), clamped so the
    /// caption never drops under 120 units (see docs/ui.md, master-detail layout rules).
    /// </summary>
    public float MasterFraction { get; set; } = UiMasterDetailLayout.DefaultFraction;

    /// <summary>A search field at the top of the master list. On by default.</summary>
    public bool Search { get; set; } = true;

    /// <summary>The search field's hint while it is empty.</summary>
    public string SearchPlaceholder { get; set; } = "Search";

    /// <summary>The new-entry line at the end of the master list (<c>+ New belt</c>); empty for none.</summary>
    public string NewCaption { get; set; } = "";

    /// <summary>The master list's line while it has no entry (short: it is one line of the list).</summary>
    public string EmptyText { get; set; } = "Nothing here yet.";

    /// <summary>The detail side while nothing is selected and no detail is set; each <c>\n</c> starts a line.</summary>
    public string DetailEmptyText { get; set; } = "Select an entry to see it here.";
}

/// <summary>
/// P7b: a master-detail widget: an optional tab row, a list of entries on the left with a search field, and the selected
/// entry's detail on the right (a title with buttons, text, headings, links, action buttons and an ordered member list).
/// The caller fills the detail on <see cref="OnSelected"/> with <see cref="SetDetail(IEnumerable{UiDetailLine})"/>.
/// Rows are made once and rewritten in place; each half scrolls by itself.
/// </summary>
public interface IUiMasterDetail : IUiHandle
{
    /// <summary>The tab row (<see cref="UiMasterDetailOptions.Tabs"/>), or null when there is none. Each tab keeps its own entries, selection, search and detail.</summary>
    IUiTabs? Tabs { get; }

    /// <summary>The tab shown now (0 without tabs).</summary>
    int Tab { get; }

    /// <summary>The current tab's entries, in the caller's order.</summary>
    IReadOnlyList<UiMasterEntry> Entries { get; }

    /// <summary>The current tab's entries matching <see cref="Query"/>, in the caller's order.</summary>
    IReadOnlyList<UiMasterEntry> Matches { get; }

    /// <summary>Replace the current tab's entries. A selected id that remains stays selected; one that left is unselected (no event).</summary>
    void SetEntries(IEnumerable<UiMasterEntry> entries);

    /// <summary>Replace tab <paramref name="tab"/>'s entries (see <see cref="SetEntries(IEnumerable{UiMasterEntry})"/>).</summary>
    void SetEntries(int tab, IEnumerable<UiMasterEntry> entries);

    /// <summary>The current tab's selected entry id, or null.</summary>
    string? SelectedId { get; }

    /// <summary>Select <paramref name="id"/> on the current tab (null: none) and raise <see cref="OnSelected"/>. False for an unknown id or no change.</summary>
    bool Select(string? id);

    /// <summary>The current tab's search text. Setting it filters as typing does and raises <see cref="OnSearch"/>.</summary>
    string Query { get; set; }

    /// <summary>The current tab's detail lines.</summary>
    IReadOnlyList<UiDetailLine> Detail { get; }

    /// <summary>Replace the current tab's detail lines (no event). The member list keeps open drawers for ids that remain.</summary>
    void SetDetail(IEnumerable<UiDetailLine> lines);

    /// <summary>Replace tab <paramref name="tab"/>'s detail lines.</summary>
    void SetDetail(int tab, IEnumerable<UiDetailLine> lines);

    /// <summary>Override tab <paramref name="tab"/>'s new-entry caption, empty-list text and empty-detail text; null keeps one.</summary>
    void SetTexts(int tab, string? newCaption = null, string? emptyText = null, string? detailEmptyText = null);

    /// <summary>The current tab's member list in its current order (empty without a member line).</summary>
    IReadOnlyList<UiListItem> Members { get; }

    /// <summary>Move member <paramref name="id"/> to <paramref name="index"/>; raises <see cref="OnReorder"/>. False when nothing moved.</summary>
    bool MoveMember(string id, int index);

    /// <summary>Remove member <paramref name="id"/>; raises <see cref="OnRemove"/>. False when it is not in the list.</summary>
    bool RemoveMember(string id);

    /// <summary>Open or close member <paramref name="id"/>'s drawer. False when it has none or nothing changed.</summary>
    bool SetMemberExpanded(string id, bool expanded);

    /// <summary>The rows the widget made (<see cref="UiMasterDetailOptions.Rows"/>, clamped).</summary>
    int Rows { get; }

    /// <summary>The first master line shown (the master list's scroll), and the first detail line shown.</summary>
    int MasterTop { get; }

    /// <inheritdoc cref="MasterTop"/>
    int DetailTop { get; }

    /// <summary>Scroll the master list by <paramref name="lines"/> (positive toward the end). False when it did not move.</summary>
    bool ScrollMaster(int lines);

    /// <summary>Scroll the detail by <paramref name="lines"/>. False when it did not move.</summary>
    bool ScrollDetail(int lines);

    /// <summary>After the selection changed (a click, a controller press, <see cref="Select"/>), with the id (empty: none).</summary>
    IDisposable OnSelected(Action<string> handler);

    /// <summary>After a detail button, a link or the new-entry line was pressed.</summary>
    IDisposable OnAction(Action<UiDetailAction> handler);

    /// <summary>After a member moved (a drag, an arrow, <see cref="MoveMember"/>).</summary>
    IDisposable OnReorder(Action<UiListReorder> handler);

    /// <summary>After a member was removed (its button or <see cref="RemoveMember"/>).</summary>
    IDisposable OnRemove(Action<UiListItem> handler);

    /// <summary>After the search text changed (typing or <see cref="Query"/>).</summary>
    IDisposable OnSearch(Action<string> handler);

    /// <summary>Readback: <c>{kind: masterdetail, key, tab, selected, query, masterTop, detailTop, master [..], detail [..], members [..]}</c>.</summary>
    Dictionary<string, object?> Describe();
}
