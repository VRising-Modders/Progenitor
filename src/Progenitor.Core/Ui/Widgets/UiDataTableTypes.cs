using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>P7b: what a data table column holds, which decides how it sorts and draws.</summary>
public enum UiTableColumnKind
{
    /// <summary>Words: sorted A to Z (case-insensitive), drawn in the body face.</summary>
    Text = 0,

    /// <summary>A number: sorted by <see cref="UiTableCell.Value"/>, drawn in tabular figures, right-justified.</summary>
    Number = 1,

    /// <summary>
    /// A column of small icon buttons per row: each row's <see cref="UiTableRow.Actions"/>, raising
    /// <see cref="IUiDataTable.OnRowAction"/>. Never sorts, holds no cells, is always shown (<see cref="UiTableColumn.Hideable"/> is ignored: the column chooser never offers it), and has
    /// room for <see cref="UiTableColumn.ActionSlots"/> buttons. One per table.
    /// </summary>
    Actions = 2,
}

/// <summary>The face of a row action button (<see cref="UiRowAction"/>): a drawn mark in the kit's glyph-button gold.</summary>
public enum UiRowActionIcon
{
    /// <summary>A target ring with four ticks: find or locate the row's thing.</summary>
    Locate = 0,

    /// <summary>A triangle pointing up: move the row up.</summary>
    Up = 1,

    /// <summary>A triangle pointing down: move the row down.</summary>
    Down = 2,

    /// <summary>A cross: remove the row.</summary>
    Remove = 3,

    /// <summary>A storage chest: open or show the row's container.</summary>
    Chest = 4,

    /// <summary>A check mark: confirm or mark the row.</summary>
    Check = 5,
}

/// <summary>
/// One small icon button on a data table row (<see cref="UiTableRow.Actions"/>), drawn in the table's
/// <see cref="UiTableColumnKind.Actions"/> column. A click (or A on a controller) raises
/// <see cref="IUiDataTable.OnRowAction"/> with the row's id and <see cref="Id"/>; a disabled one is drawn faint and does
/// nothing. Hovering it shows <see cref="Tooltip"/>.
/// </summary>
public sealed class UiRowAction
{
    /// <summary>An action <paramref name="id"/> (the caller's name, raised with the click) drawn as <paramref name="icon"/>.</summary>
    public UiRowAction(string id, UiRowActionIcon icon, string tooltip = "", bool enabled = true)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A row action needs an id.", nameof(id));
        Id = id.Trim();
        Icon = icon;
        Tooltip = tooltip ?? "";
        Enabled = enabled;
    }

    /// <summary>The caller's name for the action (<c>locate</c>, <c>up</c>, <c>remove</c>). Never shown.</summary>
    public string Id { get; }

    /// <summary>The button's face.</summary>
    public UiRowActionIcon Icon { get; }

    /// <summary>Hover text (<c>Move up</c>); empty for none. Shown for a disabled button too.</summary>
    public string Tooltip { get; set; }

    /// <summary>False: drawn faint and a click does nothing (the first row's up, the last row's down).</summary>
    public bool Enabled { get; set; }

    /// <inheritdoc />
    public override string ToString() => Id + (Enabled ? "" : " (disabled)");
}

/// <summary>P7b: what a column's subtotal and total rows show (<see cref="UiTableColumn.Subtotal"/>).</summary>
public enum UiTableSubtotal
{
    /// <summary>Nothing: the column's subtotal cells are blank.</summary>
    None = 0,

    /// <summary>The sum of the rows' values.</summary>
    Sum = 1,

    /// <summary>The smallest value.</summary>
    Min = 2,

    /// <summary>The largest value.</summary>
    Max = 3,

    /// <summary>How many rows have a value.</summary>
    Count = 4,
}

/// <summary>
/// P7b: one column of a data table (<see cref="UiWidgets.DataTable"/>). <see cref="Key"/> is the caller's stable name: rows
/// address their cells by it, and the sort, the hidden set and edits name it.
/// </summary>
public sealed class UiTableColumn
{
    /// <summary>A column named <paramref name="key"/> with the header caption <paramref name="caption"/>.</summary>
    public UiTableColumn(string key, string caption, UiTableColumnKind kind = UiTableColumnKind.Text)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A column needs a key.", nameof(key));
        Key = key.Trim();
        Caption = caption ?? "";
        Kind = kind;
        if (kind == UiTableColumnKind.Actions)
        {
            Sortable = false;
            Hideable = false;
        }
    }

    /// <summary>The caller's stable name. Never shown.</summary>
    public string Key { get; }

    /// <summary>The header caption, drawn in the game's small caps.</summary>
    public string Caption { get; }

    /// <summary>Text, number or the row actions.</summary>
    public UiTableColumnKind Kind { get; }

    /// <summary>
    /// An <see cref="UiTableColumnKind.Actions"/> column: how many icon buttons each row has room for (1 to 6; 4 by
    /// default). The column is exactly that wide (28 units a button, 8 between); <see cref="Width"/> is ignored, and a
    /// caption longer than the buttons is cut. Give every row the same actions in the same order and disable those that
    /// do not apply, so each icon stays in its own column from row to row.
    /// </summary>
    public int ActionSlots { get; set; } = 4;

    /// <summary>
    /// The column's width in UI units, or 0 for the fill column (takes what the row has left, never under 120). The table
    /// widens a column whose caption and sort mark would not fit. With no fill column, the first text column fills.
    /// </summary>
    public float Width { get; set; }

    /// <summary>Clicking the header sorts by this column. On by default (off, and ignored, on an actions column).</summary>
    public bool Sortable { get; set; } = true;

    /// <summary>
    /// The player may hide the column (the column chooser offers it), and a row too narrow for every column drops it
    /// (rightmost first). False: always shown, never dropped. On by default.
    /// </summary>
    public bool Hideable { get; set; } = true;

    /// <summary>Hidden when the table is made (a hideable column only).</summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// A number column the player edits in place: each cell is the game's value box. A commit raises
    /// <see cref="IUiDataTable.OnEdit"/>; the cell waits for the answer. Ignored on a text column.
    /// </summary>
    public bool Editable { get; set; }

    /// <summary>Hover text for the header caption (a sortable column); empty for none.</summary>
    public string Tooltip { get; set; } = "";

    /// <summary>
    /// A term (<see cref="UiTerms"/>) the caption explains: the circle-i marker after the caption, and hovering a
    /// sortable caption shows the term's explanation (it wins over <see cref="Tooltip"/>). Define the term with
    /// <see cref="UiTerms.Define(string, string, string)"/> on the plugin's id. Empty for none.
    /// </summary>
    public string Term { get; set; } = "";

    /// <summary>
    /// Empty cells (no value) sort after every value in both directions ("unlimited last"). On by default; off sorts
    /// them as the smallest value.
    /// </summary>
    public bool NullsLast { get; set; } = true;

    /// <summary>The .NET number format a value without its own text is drawn with (invariant culture). <c>#,0.##</c> by default.</summary>
    public string Format { get; set; } = "#,0.##";

    /// <summary>What a number cell with no value shows (<c>—</c>: unlimited, none).</summary>
    public string NullText { get; set; } = "—";

    /// <summary>What the column's subtotal and grand total cells show. <see cref="UiTableSubtotal.None"/> by default.</summary>
    public UiTableSubtotal Subtotal { get; set; }

    /// <inheritdoc />
    public override string ToString() => Key;
}

/// <summary>
/// P7b: one cell of a data table row: its display text, its sort value, its tone and whether it is inherited. Make one
/// with <see cref="Of"/> (text) or <see cref="Number"/>.
/// </summary>
public sealed class UiTableCell
{
    /// <summary>A text cell.</summary>
    public UiTableCell(string text) => Text = text ?? "";

    /// <summary>A text cell.</summary>
    public static UiTableCell Of(string text) => new(text);

    /// <summary>
    /// A number cell: <paramref name="value"/> sorts and totals; <paramref name="text"/> is drawn when given, otherwise the
    /// value in the column's <see cref="UiTableColumn.Format"/> (or its <see cref="UiTableColumn.NullText"/> for null).
    /// </summary>
    public static UiTableCell Number(double? value, string? text = null) => new(text ?? "") { Value = value };

    /// <summary>What the cell shows. Empty on a number cell: the formatted <see cref="Value"/>.</summary>
    public string Text { get; set; }

    /// <summary>
    /// The number the cell sorts and totals by. On a text column a value makes the column sort by it (a status sorted by
    /// severity); null sorts by the text.
    /// </summary>
    public double? Value { get; set; }

    /// <summary>Text to sort by in place of <see cref="Text"/> (a name without its prefix); null for the text.</summary>
    public string? SortText { get; set; }

    /// <summary>The text's tone (the framework's status colors).</summary>
    public LabelTone Tone { get; set; }

    /// <summary>
    /// The value is inherited (from a category, a default), not set on this row: drawn muted. In an editable cell it is
    /// the field's gray hint; typing sets the row's own value, and clearing the field returns to the inherited one.
    /// </summary>
    public bool Inherited { get; set; }

    /// <summary>
    /// Where an <see cref="Inherited"/> value comes from, in words (<c>category Ore</c>, <c>the castle default</c>): the
    /// row's tooltip reads <c>Reserve 200: inherited from category Ore</c>. Empty: <c>inherited</c>.
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>The player may not edit this cell (a guest's view). Drawn as plain text.</summary>
    public bool Locked { get; set; }

    internal UiTableCell Copy() => new(Text)
    {
        Value = Value,
        SortText = SortText,
        Tone = Tone,
        Inherited = Inherited,
        Source = Source,
        Locked = Locked,
    };

    /// <inheritdoc />
    public override string ToString() => Text.Length > 0 ? Text : Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
}

/// <summary>
/// P7b: one row of a data table. <see cref="Id"/> is the caller's stable key: selection, expansion and edits follow it
/// across sorting, grouping and <see cref="IUiDataTable.SetRows"/>. Two rows with the same id are refused.
/// </summary>
public sealed class UiTableRow
{
    private readonly Dictionary<string, UiTableCell> _cells = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _groups = Array.Empty<string>();

    /// <summary>A row with the stable <paramref name="id"/>.</summary>
    public UiTableRow(string id)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("A row needs an id.", nameof(id));
        Id = id;
    }

    /// <summary>The caller's stable key. Never shown.</summary>
    public string Id { get; }

    /// <summary>The row's cells by column key. A column with no cell draws blank and sorts as empty.</summary>
    public IReadOnlyDictionary<string, UiTableCell> Cells => _cells;

    /// <summary>The cell of column <paramref name="column"/>, or null. Setting null removes it.</summary>
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

    /// <summary>Set column <paramref name="column"/>'s cell; returns the row (for initializers).</summary>
    public UiTableRow Set(string column, UiTableCell cell)
    {
        this[column] = cell ?? throw new ArgumentNullException(nameof(cell));
        return this;
    }

    /// <summary>Set a text cell.</summary>
    public UiTableRow Text(string column, string text, LabelTone tone = LabelTone.Normal) =>
        Set(column, new UiTableCell(text) { Tone = tone });

    /// <summary>
    /// Set a number cell (see <see cref="UiTableCell.Number"/>); <paramref name="inherited"/> with its
    /// <paramref name="source"/> (<see cref="UiTableCell.Source"/>).
    /// </summary>
    public UiTableRow Number(string column, double? value, string? text = null, bool inherited = false, string source = "") =>
        Set(column, new UiTableCell(text ?? "") { Value = value, Inherited = inherited, Source = source ?? "" });

    /// <summary>
    /// The row's icon: a <c>UnityEngine.Sprite</c>, or null. <see cref="ItemPrefab"/> shows a game item's own icon instead.
    /// </summary>
    public object? Icon { get; set; }

    /// <summary>A game item's prefab id whose inventory icon the row shows when <see cref="Icon"/> is null; 0 for none.</summary>
    public int ItemPrefab { get; set; }

    /// <summary>
    /// HUB1: a castle building's prefab id (a chest, a station, a prison cell) whose icon from the game's build menu the row
    /// shows when <see cref="Icon"/> is null and <see cref="ItemPrefab"/> is 0; 0 for none: the building's own blueprint
    /// icon. Until the game has that icon loaded, the row keeps an empty, aligned icon place (<see cref="UiBuildingIcons"/>).
    /// </summary>
    public int BuildingPrefab { get; set; }

    /// <summary>
    /// The groups the row sits under when the table is grouped (<see cref="IUiDataTable.Grouped"/>). A row in several groups
    /// appears under each; the grand total counts it once. Empty: the table's "other" group.
    /// </summary>
    public IReadOnlyList<string> Groups
    {
        get => _groups;
        set => _groups = value ?? Array.Empty<string>();
    }

    /// <summary>
    /// The expanded row's drawer: one line of the body per text line (separated by <c>\n</c>), up to
    /// <see cref="UiDataTableOptions.DetailLines"/>. Empty: the row has no drawer.
    /// </summary>
    public string Detail { get; set; } = "";

    /// <summary>Hover text for the row; empty for none.</summary>
    public string Tooltip { get; set; } = "";

    /// <summary>Every editable cell of the row is locked (a guest's view).</summary>
    public bool Locked { get; set; }

    private IReadOnlyList<UiRowAction> _actions = Array.Empty<UiRowAction>();

    /// <summary>
    /// The row's icon buttons, drawn left to right in the table's <see cref="UiTableColumnKind.Actions"/> column (as many
    /// as its <see cref="UiTableColumn.ActionSlots"/>). Read at every draw: after changing an action's
    /// <see cref="UiRowAction.Enabled"/>, call <see cref="IUiDataTable.SetRows"/> to redraw. Empty: none.
    /// </summary>
    public IReadOnlyList<UiRowAction> Actions
    {
        get => _actions;
        set => _actions = value ?? Array.Empty<UiRowAction>();
    }

    /// <summary>Add an icon button (see <see cref="UiRowAction"/>) after the row's others; returns the row (for initializers).</summary>
    public UiTableRow Action(string id, UiRowActionIcon icon, string tooltip = "", bool enabled = true)
    {
        _actions = new List<UiRowAction>(_actions) { new UiRowAction(id, icon, tooltip, enabled) };
        return this;
    }

    /// <summary>The action <paramref name="id"/> of the row, or null.</summary>
    internal UiRowAction? FindAction(string? id)
    {
        if (id == null) return null;
        foreach (var a in _actions)
            if (a != null && string.Equals(a.Id, id, StringComparison.Ordinal)) return a;
        return null;
    }

    /// <inheritdoc />
    public override string ToString() => Id;
}

/// <summary>P7b: one key of a data table's sort: a column and a direction.</summary>
public readonly struct UiTableSortKey : IEquatable<UiTableSortKey>
{
    /// <summary>Sort by <paramref name="column"/>, ascending (A to Z, smallest first) or descending.</summary>
    public UiTableSortKey(string column, bool ascending = true)
    {
        Column = column ?? "";
        Ascending = ascending;
    }

    /// <summary>The column key.</summary>
    public string Column { get; }

    /// <summary>A to Z, smallest first.</summary>
    public bool Ascending { get; }

    /// <inheritdoc />
    public bool Equals(UiTableSortKey other) => string.Equals(Column, other.Column, StringComparison.Ordinal) && Ascending == other.Ascending;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is UiTableSortKey k && Equals(k);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Column) * 2 + (Ascending ? 1 : 0);

    /// <inheritdoc />
    public override string ToString() => Column + (Ascending ? " asc" : " desc");
}

/// <summary>P7b: where an inline edit stands.</summary>
public enum UiTableEditState
{
    /// <summary>Raised; the cell waits (spinner, locked).</summary>
    Pending = 0,

    /// <summary>The caller accepted it: the cell shows the new value.</summary>
    Resolved = 1,

    /// <summary>The caller refused it, or the timeout passed: the old value is back and the reason is shown.</summary>
    Rejected = 2,
}

/// <summary>
/// P7b: an inline edit of a number cell (<see cref="IUiDataTable.OnEdit"/>). The cell shows a spinner and takes no input
/// until the caller answers with <see cref="Resolve"/> or <see cref="Reject"/>; with no answer within
/// <see cref="UiDataTableOptions.EditTimeout"/> the table rejects it itself ("No answer from the server"). Answer on the
/// main thread. A second answer is ignored.
/// </summary>
public sealed class UiTableEdit
{
    private readonly Func<UiTableEdit, bool, string?, bool> _complete;

    internal UiTableEdit(
        string rowId, string column, double? oldValue, double? newValue, bool clear, string text, double started,
        Func<UiTableEdit, bool, string?, bool> complete)
    {
        RowId = rowId;
        Column = column;
        OldValue = oldValue;
        NewValue = newValue;
        Clear = clear;
        Text = text;
        Started = started;
        _complete = complete;
    }

    /// <summary>The edited row's id.</summary>
    public string RowId { get; }

    /// <summary>The edited column's key.</summary>
    public string Column { get; }

    /// <summary>The value before the edit (null: none).</summary>
    public double? OldValue { get; }

    /// <summary>The value the player entered; null when <see cref="Clear"/>.</summary>
    public double? NewValue { get; }

    /// <summary>The player cleared the field: return the cell to its inherited value (remove the row's own value).</summary>
    public bool Clear { get; }

    /// <summary>The text the player committed, trimmed.</summary>
    public string Text { get; }

    /// <summary>Where the edit stands now.</summary>
    public UiTableEditState State { get; internal set; }

    /// <summary>The reason a rejected edit gave; empty otherwise.</summary>
    public string Reason { get; internal set; } = "";

    internal double Started { get; }

    /// <summary>
    /// Accept the edit: the cell shows <paramref name="display"/> when given, otherwise the new value in the column's
    /// format (after a <see cref="Clear"/>, the old text, muted, until the next <see cref="IUiDataTable.SetRows"/>).
    /// False when the edit was already answered.
    /// </summary>
    public bool Resolve(string? display = null) => _complete(this, true, display);

    /// <summary>Refuse the edit: the old value is back, and <paramref name="reason"/> shows under the table and as the row's tooltip.</summary>
    public bool Reject(string reason) => _complete(this, false, string.IsNullOrWhiteSpace(reason) ? "The change was refused." : reason);

    /// <inheritdoc />
    public override string ToString() => RowId + "/" + Column + " " + (Clear ? "clear" : Text) + " " + State;
}

/// <summary>P7b: what a data table offers. Every option has the common default.</summary>
public sealed class UiDataTableOptions
{
    /// <summary>The slot rows the body draws (4 to 30; 12 by default). More rows scroll; only these rows are ever built.</summary>
    public int VisibleRows { get; set; } = 12;

    /// <summary>
    /// HUB1: the table takes all the height its panel has left: its body draws as many rows (4 to 30) as fit the panel's
    /// tallest frame with every other row on it, so the window grows to its height cap and stops there, and the table
    /// scrolls inside it (the panel itself never scrolls for it). The count follows the panel (a tab that shows more
    /// rows above the table, a resized screen). <see cref="VisibleRows"/> is the count until the first layout is read.
    /// Off by default.
    /// </summary>
    public bool FillHeight { get; set; }

    /// <summary>
    /// The fewest body rows the table draws (0 to 30, at most the rows in use; 0 by default): with fewer lines, blank
    /// filler rows below them keep the table's height, so a tab with two rows is as tall as one with ten. A filler row
    /// takes no click, selection, tooltip or hover wash, has no check cell, and the navigation passes over it. Scrolling
    /// past <see cref="VisibleRows"/> is unchanged.
    /// </summary>
    public int MinRows { get; set; }

    /// <summary>Rows can be selected (click). On by default.</summary>
    public bool Selectable { get; set; } = true;

    /// <summary>
    /// Several rows can be selected: Ctrl-click adds or removes one, Shift-click selects the range from the last click, and
    /// a check cell on each row toggles it (reachable with a controller). Off: one row at a time.
    /// </summary>
    public bool MultiSelect { get; set; }

    /// <summary>Group the rows when the table is made (<see cref="IUiDataTable.Grouped"/>).</summary>
    public bool Grouped { get; set; }

    /// <summary>The group a row with no <see cref="UiTableRow.Groups"/> sits under, listed last.</summary>
    public string OtherGroup { get; set; } = "Other";

    /// <summary>Draw a grand total row at the end when a column has a <see cref="UiTableColumn.Subtotal"/>. On by default.</summary>
    public bool TotalRow { get; set; } = true;

    /// <summary>The most drawer lines an expanded row shows (1 to 8; 4 by default).</summary>
    public int DetailLines { get; set; } = 4;

    /// <summary>How long an edit waits for its answer before the table rejects it. 15 s by default; zero or less: forever.</summary>
    public TimeSpan EditTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Offer the column chooser (a Columns button under the table) when a column is hideable. On by default.</summary>
    public bool ColumnChooser { get; set; } = true;

    /// <summary>The sort when the table is made; empty keeps the caller's order.</summary>
    public IReadOnlyList<UiTableSortKey>? Sort { get; set; }

    /// <summary>The muted line drawn while the table has no rows.</summary>
    public string EmptyText { get; set; } = "Nothing to show.";

    /// <summary>
    /// A row's hover tooltip also repeats each value its text column cuts short (<c>Chest: Large wooden chest of …</c>).
    /// Off by default: the kit's tooltips are asked for (a row's <see cref="UiTableRow.Tooltip"/>, a control's
    /// <c>Tooltip</c>), and an automatic box over the row it repeats is noise; give the column the width of its longest
    /// value instead. A row's own <see cref="UiTableRow.Tooltip"/>, its inherited values and its refused edits always
    /// show.
    /// </summary>
    public bool OverflowTooltips { get; set; }

    /// <summary>Tests: the clock edits time out by, in seconds. Null: the process clock.</summary>
    internal Func<double>? Clock { get; set; }
}

/// <summary>
/// P7b: a data table on a panel (<see cref="UiWidgets.DataTable"/>): sortable columns (multi-key with Shift), columns the
/// player hides, row drawers, multi-select, inline number edits that wait for the caller's answer, grouping with
/// subtotals, and a virtualized body that draws <see cref="UiDataTableOptions.VisibleRows"/> rows however many there are.
/// Every change the player makes is applied and then raised; every setter applies without an event unless it says so.
/// </summary>
public interface IUiDataTable : IUiHandle
{
    /// <summary>The columns, in order.</summary>
    IReadOnlyList<UiTableColumn> Columns { get; }

    /// <summary>The rows in the caller's order.</summary>
    IReadOnlyList<UiTableRow> Rows { get; }

    /// <summary>
    /// Replace every row (a refresh). No event. Selection, drawers and pending edits follow the ids; ids that disappear
    /// drop out of the selection silently; a pending edit's cell keeps its spinner; the scroll position is kept (clamped).
    /// </summary>
    void SetRows(IEnumerable<UiTableRow> rows);

    /// <summary>The sort keys, first key first; empty: the caller's order. Setting it re-sorts and raises <see cref="OnSortChanged"/>.</summary>
    IReadOnlyList<UiTableSortKey> Sort { get; set; }

    /// <summary>
    /// A header click: sort by <paramref name="column"/> alone (again: flip it), or with <paramref name="add"/> (Shift) add
    /// it as the next key (again: flip it). Raises <see cref="OnSortChanged"/>. False for a column that does not sort.
    /// </summary>
    bool SortBy(string column, bool add = false);

    /// <summary>After the sort changed (to persist it per player).</summary>
    IDisposable OnSortChanged(Action<IReadOnlyList<UiTableSortKey>> handler);

    /// <summary>The hidden columns' keys. Setting it shows every other column and raises <see cref="OnColumnsChanged"/>; always-shown columns stay.</summary>
    IReadOnlyList<string> HiddenColumns { get; set; }

    /// <summary>Show or hide one column; raises <see cref="OnColumnsChanged"/>. False when nothing changed or it is always shown.</summary>
    bool SetColumnVisible(string column, bool visible);

    /// <summary>After the hidden set changed: the hidden keys (to persist them per player).</summary>
    IDisposable OnColumnsChanged(Action<IReadOnlyList<string>> handler);

    /// <summary>The column chooser is open.</summary>
    bool ChooserOpen { get; set; }

    /// <summary>The selected row ids, in selection order.</summary>
    IReadOnlyList<string> Selected { get; }

    /// <summary>Select exactly <paramref name="ids"/> (unknown ids are skipped); raises <see cref="OnSelectionChanged"/> when it changed.</summary>
    void SetSelected(IEnumerable<string> ids);

    /// <summary>After the selection changed.</summary>
    IDisposable OnSelectionChanged(Action<IReadOnlyList<string>> handler);

    /// <summary>Open or close row <paramref name="id"/>'s drawer; raises <see cref="OnExpand"/>. False when it has none.</summary>
    bool SetExpanded(string id, bool expanded);

    /// <summary>Whether row <paramref name="id"/>'s drawer is open.</summary>
    bool IsExpanded(string id);

    /// <summary>After a drawer opened (<c>true</c>) or closed.</summary>
    IDisposable OnExpand(Action<string, bool> handler);

    /// <summary>Rows fold under group header rows with a subtotal row per group. No event.</summary>
    bool Grouped { get; set; }

    /// <summary>The group names in display order (while grouped).</summary>
    IReadOnlyList<string> Groups { get; }

    /// <summary>Fold or unfold group <paramref name="group"/>. False when nothing changed.</summary>
    bool SetGroupCollapsed(string group, bool collapsed);

    /// <summary>Whether group <paramref name="group"/> is folded.</summary>
    bool IsGroupCollapsed(string group);

    /// <summary>The first body line drawn (0-based, clamped). Setting it scrolls; no event.</summary>
    int TopIndex { get; set; }

    /// <summary>How many body lines there are (rows, group headers, drawer lines, subtotals, the total).</summary>
    int LineCount { get; }

    /// <summary>The slot rows the body draws.</summary>
    int VisibleRows { get; }

    /// <summary>Scroll so row <paramref name="id"/> is drawn. False when it is not in the body (unknown, or in a folded group).</summary>
    bool ScrollTo(string id);

    /// <summary>
    /// After the player committed an edit (Enter, or leaving the field): the cell now waits for
    /// <see cref="UiTableEdit.Resolve"/> or <see cref="UiTableEdit.Reject"/>.
    /// </summary>
    IDisposable OnEdit(Action<UiTableEdit> handler);

    /// <summary>
    /// Commit <paramref name="text"/> into row <paramref name="rowId"/>'s editable cell <paramref name="column"/> as if the
    /// player typed it (a bulk edit over the selection, the bridge): raises <see cref="OnEdit"/> and returns the edit. Empty
    /// text clears to the inherited value. Null when nothing changed or the edit is refused: the reason is in
    /// <see cref="StatusReason"/> (not a number, the cell is locked or already waiting).
    /// </summary>
    UiTableEdit? Edit(string rowId, string column, string text);

    /// <summary>The edits waiting for an answer.</summary>
    IReadOnlyList<UiTableEdit> PendingEdits { get; }

    /// <summary>The footer line ("Showing 13–24 of 600 · 3 selected"; the selected count only with <see cref="UiDataTableOptions.MultiSelect"/>).</summary>
    string StatusText { get; }

    /// <summary>The last refusal's reason (orange line under the table); empty when none.</summary>
    string StatusReason { get; }

    /// <summary>
    /// After the player pressed an enabled row action button (<see cref="UiTableRow.Actions"/>): the row's id and the
    /// action's <see cref="UiRowAction.Id"/>. The table changes nothing itself: move or remove the row and call
    /// <see cref="SetRows"/>.
    /// </summary>
    IDisposable OnRowAction(Action<string, string> handler);

    /// <summary>
    /// Press row <paramref name="rowId"/>'s action <paramref name="actionId"/> as if clicked (the bridge, a keybind): raises
    /// <see cref="OnRowAction"/>. False when the row or the action is unknown, or the action is disabled.
    /// </summary>
    bool PressRowAction(string rowId, string actionId);

    /// <summary>
    /// Readback: <c>{kind: datatable, key, columns, sort, hidden, grouped, top, lines, rows, visibleRows, minRows, fillers,
    /// slots (each with its row's actions), selected, expanded, collapsed, pending, status, reason, chooserOpen}</c>.
    /// </summary>
    Dictionary<string, object?> Describe();
}
