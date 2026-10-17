using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// CHECKLIST1: one option of a checklist's filter dropdown (<see cref="UiChecklistOptions.Filters"/>). An entry is listed
/// under the filter when its <see cref="UiChecklistEntry.Filters"/> names <see cref="Id"/>.
/// </summary>
public sealed class UiChecklistFilter
{
    /// <summary>A filter option with the stable <paramref name="id"/> and the caption <paramref name="label"/>.</summary>
    public UiChecklistFilter(string id, string label)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A filter needs an id.", nameof(id));
        Id = id.Trim();
        Label = string.IsNullOrWhiteSpace(label) ? Id : label.Trim();
    }

    private UiChecklistFilter(string label)
    {
        Id = "";
        Label = label;
    }

    /// <summary>The option that lists every entry: its id is empty.</summary>
    internal static UiChecklistFilter All(string? label) => new(string.IsNullOrWhiteSpace(label) ? "All" : label!.Trim());

    /// <summary>The caller's stable key: what <see cref="UiChecklistEntry.Filters"/> and <see cref="IUiChecklist.Filter"/> name (empty: the All option).</summary>
    public string Id { get; }

    /// <summary>The caption on the dropdown bar and its chip.</summary>
    public string Label { get; }

    /// <inheritdoc />
    public override string ToString() => Id + ":" + Label;
}

/// <summary>
/// CHECKLIST1: one row of a checklist (<see cref="UiWidgets.Checklist"/>): a chest, a station, a belt — anything the caller
/// can name. <see cref="Id"/> is the caller's stable key: the checked set follows it across filtering, searching, sorting
/// and <see cref="IUiChecklist.SetEntries"/>. Two entries with the same id are refused. The checklist reads an entry when it is handed over (a snapshot): change it, then call <see cref="IUiChecklist.SetEntries"/> again.
/// </summary>
public sealed class UiChecklistEntry
{
    /// <summary>The column key whose cell an entry without its own cell fills with <see cref="Label"/>.</summary>
    public const string LabelColumn = "label";

    /// <summary>The column key whose cell an entry without its own cell fills with <see cref="Note"/>.</summary>
    public const string NoteColumn = "note";

    private readonly Dictionary<string, UiTableCell> _cells = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _keywords = Array.Empty<string>();
    private IReadOnlyList<string> _filters = Array.Empty<string>();

    /// <summary>An entry with the stable <paramref name="id"/> and the caption <paramref name="label"/>.</summary>
    public UiChecklistEntry(string id, string label)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("An entry needs an id.", nameof(id));
        Id = id;
        Label = label ?? "";
    }

    /// <summary>The caller's stable key. Never shown.</summary>
    public string Id { get; }

    /// <summary>The caption: the <see cref="LabelColumn"/> column's text, and what the search matches first.</summary>
    public string Label { get; set; }

    /// <summary>The section heading the entry is listed under (<c>Storage</c>, <c>Stations</c>); empty for none.</summary>
    public string Section { get; set; } = "";

    /// <summary>A short note (<c>near the gate</c>): the <see cref="NoteColumn"/> column's text, and searched. Empty for none.</summary>
    public string Note { get; set; } = "";

    /// <summary>More words the search matches (a prefab name, a nickname); never shown.</summary>
    public IReadOnlyList<string> Keywords
    {
        get => _keywords;
        set => _keywords = value ?? Array.Empty<string>();
    }

    /// <summary>
    /// The filter ids (<see cref="UiChecklistFilter.Id"/>) the entry is listed under. The "All" option lists every entry
    /// whatever its tags.
    /// </summary>
    public IReadOnlyList<string> Filters
    {
        get => _filters;
        set => _filters = value ?? Array.Empty<string>();
    }

    /// <summary>The row's icon: a <c>UnityEngine.Sprite</c>, or null. <see cref="ItemPrefab"/> and <see cref="BuildingPrefab"/> show the game's.</summary>
    public object? Icon { get; set; }

    /// <summary>A game item's prefab id whose inventory icon the row shows when <see cref="Icon"/> is null; 0 for none.</summary>
    public int ItemPrefab { get; set; }

    /// <summary>
    /// A castle building's prefab id (a chest, a station) whose build-menu icon the row shows when <see cref="Icon"/> is null
    /// and <see cref="ItemPrefab"/> is 0 (<see cref="UiTableRow.BuildingPrefab"/>'s rule: an aligned blank until the game has
    /// that building's blueprint icon loaded); 0 for none.
    /// </summary>
    public int BuildingPrefab { get; set; }

    /// <summary>
    /// Why the entry cannot be checked now (<c>Not your clan</c>); empty when it can. The row is drawn grayed, cannot be
    /// checked (by a click, a range, Select shown or <see cref="IUiChecklist.Select"/>), and shows the reason when hovered.
    /// </summary>
    public string DisabledReason { get; set; } = "";

    /// <summary>Whether <see cref="DisabledReason"/> is set.</summary>
    public bool Disabled => !string.IsNullOrWhiteSpace(DisabledReason);

    /// <summary>Hover text for an entry that can be checked; empty for none.</summary>
    public string Tooltip { get; set; } = "";

    /// <summary>The entry's own cells by column key. A column with no cell draws blank (the label and note columns excepted).</summary>
    public IReadOnlyDictionary<string, UiTableCell> Cells => _cells;

    /// <summary>The entry's own cell of column <paramref name="column"/>, or null. Setting null removes it.</summary>
    public UiTableCell? this[string column]
    {
        get => column != null && _cells.TryGetValue(column, out var c) ? c : null;
        set
        {
            if (column == null) throw new ArgumentNullException(nameof(column));
            if (value == null) _cells.Remove(column);
            else _cells[column] = value;
        }
    }

    /// <summary>Set column <paramref name="column"/>'s cell; returns the entry (for initializers).</summary>
    public UiChecklistEntry Set(string column, UiTableCell cell)
    {
        this[column] = cell ?? throw new ArgumentNullException(nameof(cell));
        return this;
    }

    /// <summary>Set a text cell.</summary>
    public UiChecklistEntry Text(string column, string text, LabelTone tone = LabelTone.Normal) =>
        Set(column, new UiTableCell(text) { Tone = tone });

    /// <summary>Set a number cell: <paramref name="value"/> sorts; <paramref name="text"/> is drawn when given (<c>12 m</c>).</summary>
    public UiChecklistEntry Number(string column, double? value, string? text = null) =>
        Set(column, UiTableCell.Number(value, text));

    /// <inheritdoc />
    public override string ToString() => Id + ":" + Label;
}

/// <summary>CHECKLIST1: what a checklist offers (<see cref="UiWidgets.Checklist"/>). Every option has the common default.</summary>
public sealed class UiChecklistOptions
{
    /// <summary>
    /// The columns after the check box and the icon, in order (<see cref="UiTableColumn"/>: text or number, width, sortable).
    /// Entries fill them by key. A column keyed <see cref="UiChecklistEntry.LabelColumn"/> shows each entry's label and one
    /// keyed <see cref="UiChecklistEntry.NoteColumn"/> its note; without a label column a <see cref="LabelCaption"/> column is
    /// put first. Null: the label column alone.
    /// </summary>
    public IReadOnlyList<UiTableColumn>? Columns { get; set; }

    /// <summary>The caption of the label column the checklist adds when <see cref="Columns"/> has none.</summary>
    public string LabelCaption { get; set; } = "Name";

    /// <summary>The sort when the checklist is made (the mod's default); empty keeps the caller's order. A header click changes it.</summary>
    public IReadOnlyList<UiTableSortKey>? Sort { get; set; }

    /// <summary>The filter dropdown's options, after the "All" option. Null or empty: no dropdown.</summary>
    public IReadOnlyList<UiChecklistFilter>? Filters { get; set; }

    /// <summary>The filter id selected when the checklist is made; empty for "All" (or the first filter when there is no "All").</summary>
    public string DefaultFilter { get; set; } = "";

    /// <summary>The caption of the first option, which lists every entry. Empty: no such option (one filter always applies).</summary>
    public string AllFilterLabel { get; set; } = "All";

    /// <summary>The caption before the filter dropdown's bar (the compact dropdown's caption). Empty for none.</summary>
    public string FilterCaption { get; set; } = "Show";

    /// <summary>The search field's hint while it is empty.</summary>
    public string Placeholder { get; set; } = "Search";

    /// <summary>The ids checked at the start (disabled and unknown ids are skipped).</summary>
    public IEnumerable<string>? Selected { get; set; }

    /// <summary>
    /// The Confirm button's caption, with <c>{0}</c> for the checked count (<c>Add {0}</c>). The button is gray and does
    /// nothing while nothing is checked.
    /// </summary>
    public string ConfirmFormat { get; set; } = "Confirm ({0})";

    /// <summary>The Cancel button's caption.</summary>
    public string CancelCaption { get; set; } = "Cancel";

    /// <summary>Draw the Cancel and Confirm buttons under the list. On by default; off for a checklist the mod acts on itself.</summary>
    public bool Buttons { get; set; } = true;

    /// <summary>
    /// Each row gets a <see cref="PreviewCaption"/> button that raises <see cref="IUiChecklist.OnPreview"/>, as does a
    /// right-click on the row. Off by default. (A double-click is two clicks: two flips.)
    /// </summary>
    public bool Preview { get; set; }

    /// <summary>The preview button's caption.</summary>
    public string PreviewCaption { get; set; } = "View";

    /// <summary>A group heading (an entry's <see cref="UiChecklistEntry.Section"/>) folds and unfolds its rows (a click on it). On by default.</summary>
    public bool CollapsibleGroups { get; set; } = true;

    /// <summary>The group of entries with no <see cref="UiChecklistEntry.Section"/> while other entries name one (the data table's rule).</summary>
    public string OtherGroup { get; set; } = "Other";

    /// <summary>The slot rows the body draws (4 to 30; 12 by default). More rows scroll; only these rows are ever built.</summary>
    public int VisibleRows { get; set; } = 12;

    /// <summary>
    /// The list takes all the height its panel has left (<see cref="UiDataTableOptions.FillHeight"/>'s rule): it draws as
    /// many rows (4 to 30) as fit and scrolls inside the window. Off by default.
    /// </summary>
    public bool FillHeight { get; set; }

    /// <summary>The muted line drawn while the checklist has no entries.</summary>
    public string EmptyText { get; set; } = "Nothing to choose from.";
}

/// <summary>
/// CHECKLIST1: a filterable multi-select checklist (<see cref="UiWidgets.Checklist"/>) for long lists: a search box and a
/// filter dropdown on one row, a sortable header, rows with the game's checkbox, an icon and the mod's columns, section
/// headings that fold, a status line with the checked count and <b>Select shown</b> / <b>Clear</b>, and Cancel and
/// Confirm. The checked set is by id and survives filtering, searching, sorting, <see cref="SetEntries"/>, hiding and
/// <see cref="Suspend"/>. Every change the player makes is applied and then raised; every setter applies without an event
/// unless it says so.
/// </summary>
public interface IUiChecklist : IUiHandle
{
    /// <summary>The columns after the check box and the icon, in order (the label column included).</summary>
    IReadOnlyList<UiTableColumn> Columns { get; }

    /// <summary>The entries in the caller's order.</summary>
    IReadOnlyList<UiChecklistEntry> Entries { get; }

    /// <summary>
    /// Replace every entry (a refresh). No event. Checked ids that remain (and can be checked) stay checked; others drop
    /// out silently. The search, the filter, the sort and folded groups are kept, and the scroll keeps the entry that was
    /// at the top (by id; clamped when it left). The entries are read here (a snapshot): an entry changed afterwards shows
    /// its change only after the next <see cref="SetEntries"/>.
    /// </summary>
    void SetEntries(IEnumerable<UiChecklistEntry> entries);

    /// <summary>The search text. Setting it filters as typing does (and rewrites the field). No event.</summary>
    string Query { get; set; }

    /// <summary>The filter options, the "All" option (id empty) first when there is one.</summary>
    IReadOnlyList<UiChecklistFilter> Filters { get; }

    /// <summary>The selected filter's id (empty: All). Setting an unknown id is ignored. Raises <see cref="OnFilterChanged"/>.</summary>
    string Filter { get; set; }

    /// <summary>After the filter changed (to persist it per player).</summary>
    IDisposable OnFilterChanged(Action<string> handler);

    /// <summary>The filter's options are open under the search row.</summary>
    bool FilterOpen { get; set; }

    /// <summary>
    /// The entries the filter and the search show now, in display order, without those under a folded section heading.
    /// This is what <see cref="SelectShown"/> acts on.
    /// </summary>
    IReadOnlyList<UiChecklistEntry> Shown { get; }

    /// <summary>The sort keys, first key first; empty: the caller's order. Setting it re-sorts and raises <see cref="OnSortChanged"/>.</summary>
    IReadOnlyList<UiTableSortKey> Sort { get; set; }

    /// <summary>A header click: sort by <paramref name="column"/> (again: flip), or with <paramref name="add"/> add it as the next key.</summary>
    bool SortBy(string column, bool add = false);

    /// <summary>After the sort changed.</summary>
    IDisposable OnSortChanged(Action<IReadOnlyList<UiTableSortKey>> handler);

    /// <summary>The checked ids, in the order they were checked (hidden ones included).</summary>
    IReadOnlyList<string> Selected { get; }

    /// <summary>Whether <paramref name="id"/> is checked.</summary>
    bool IsSelected(string id);

    /// <summary>Check or uncheck one entry (the picker's <c>Select</c>). False for an unknown or disabled entry, or no change. Raises <see cref="OnSelectionChanged"/>.</summary>
    bool Select(string id, bool selected = true);

    /// <summary>Check exactly <paramref name="ids"/> (unknown and disabled ids skipped). Raises <see cref="OnSelectionChanged"/> when it changed.</summary>
    void SetSelected(IEnumerable<string> ids);

    /// <summary>
    /// Check every entry <see cref="Shown"/> lists that can be checked (the status line's <b>Select shown</b>): never an
    /// entry the filter, the search or a folded section hides. Returns how many were added; one event.
    /// </summary>
    int SelectShown();

    /// <summary>Uncheck everything, shown or not (the status line's <b>Clear</b>). One event when something was checked.</summary>
    void ClearSelection();

    /// <summary>After the checked set changed.</summary>
    IDisposable OnSelectionChanged(Action<IReadOnlyList<string>> handler);

    /// <summary>The group headings in display order (the entries' <see cref="UiChecklistEntry.Section"/>s with a shown entry).</summary>
    IReadOnlyList<string> Groups { get; }

    /// <summary>Fold or unfold group <paramref name="group"/>. False when nothing changed. No event.</summary>
    bool SetGroupCollapsed(string group, bool collapsed);

    /// <summary>Whether group <paramref name="group"/> is folded.</summary>
    bool IsGroupCollapsed(string group);

    /// <summary>The first body line drawn (0-based, clamped). Setting it scrolls; no event.</summary>
    int TopIndex { get; set; }

    /// <summary>How many body lines there are (section headings and rows).</summary>
    int LineCount { get; }

    /// <summary>The slot rows the body draws.</summary>
    int VisibleRows { get; }

    /// <summary>Scroll so entry <paramref name="id"/> is drawn. False when it is not shown.</summary>
    bool ScrollTo(string id);

    /// <summary>The status line (<c>120 of 300 shown · 3 selected (1 not shown)</c>).</summary>
    string StatusText { get; }

    /// <summary>The Confirm button's caption now (<see cref="UiChecklistOptions.ConfirmFormat"/> with the count).</summary>
    string ConfirmText { get; }

    /// <summary>Whether Confirm acts now (something is checked).</summary>
    bool CanConfirm { get; }

    /// <summary>The Confirm button (and Enter on a row): raise <see cref="OnConfirmed"/> with the checked ids. False when nothing is checked.</summary>
    bool Confirm();

    /// <summary>After Confirm, with the checked ids in check order.</summary>
    IDisposable OnConfirmed(Action<IReadOnlyList<string>> handler);

    /// <summary>The Cancel button (and Escape, B on a controller, while a handler is subscribed): raise <see cref="OnCancelled"/>.</summary>
    void Cancel();

    /// <summary>After Cancel. With a handler subscribed, Escape cancels while the checklist's panel is the topmost Escape target (the window the press would close); a window over it closes first.</summary>
    IDisposable OnCancelled(Action handler);

    /// <summary>Raise <see cref="OnPreview"/> for <paramref name="id"/> as the row's preview button does. False for an unknown id.</summary>
    bool Preview(string id);

    /// <summary>
    /// After the player asked to preview an entry: the row's preview button or a right-click on the row (with
    /// <see cref="UiChecklistOptions.Preview"/>). A disabled entry can be previewed.
    /// </summary>
    IDisposable OnPreview(Action<string> handler);

    /// <summary>The pointer entered a row (its entry id), or left the rows (empty).</summary>
    IDisposable OnRowHovered(Action<string> handler);

    /// <summary>
    /// Hide the checklist keeping everything (search, filter, checked rows, sort, folded sections, scroll), while the mod
    /// shows a preview of its own. Escape and Enter are not taken while suspended. Setting <see cref="IUiHandle.Visible"/>
    /// while suspended (a tab switch) shows nothing: it is remembered and applied by <see cref="Resume"/>.
    /// </summary>
    void Suspend();

    /// <summary>
    /// Show the checklist again as it was before <see cref="Suspend"/>. When the controller focus was on one of its rows,
    /// it returns to the previewed row (scrolled into view).
    /// </summary>
    void Resume();

    /// <summary>Whether <see cref="Suspend"/> is in effect.</summary>
    bool Suspended { get; }

    /// <summary>
    /// Readback: <c>{kind: checklist, key, query, filter, filters, sort, columns, top, lines, entries, shown, visibleRows,
    /// slots, selected, groups, collapsed, status, confirm, canConfirm, filterOpen, suspended}</c>.
    /// </summary>
    Dictionary<string, object?> Describe();
}
