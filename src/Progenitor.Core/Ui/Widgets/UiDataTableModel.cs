using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>P7b: what one body line of a data table is.</summary>
internal enum UiTableLineKind
{
    /// <summary>A data row.</summary>
    Row = 0,

    /// <summary>One line of an expanded row's drawer.</summary>
    Detail = 1,

    /// <summary>A group's header (its name and row count; folds the group).</summary>
    GroupHeader = 2,

    /// <summary>A group's subtotal row.</summary>
    Subtotal = 3,

    /// <summary>The grand total row (each row counted once).</summary>
    Total = 4,
}

/// <summary>P7b: one body line of a data table: its kind, its row (rows and drawer lines) and its group.</summary>
internal readonly struct UiTableLine
{
    internal UiTableLine(UiTableLineKind kind, UiTableRow? row, string group, string text = "")
    {
        Kind = kind;
        Row = row;
        Group = group ?? "";
        Text = text ?? "";
    }

    internal UiTableLineKind Kind { get; }

    internal UiTableRow? Row { get; }

    /// <summary>The group the line is under ("" while ungrouped).</summary>
    internal string Group { get; }

    /// <summary>A drawer line's text.</summary>
    internal string Text { get; }

    public override string ToString() => Kind + (Row != null ? ":" + Row.Id : "") + (Group.Length > 0 ? "@" + Group : "");
}

/// <summary>
/// P7b: the data table's state, game-free: rows, sort, hidden columns, selection, drawers, grouping, the flattened body
/// lines, subtotals and pending edits. The handle draws it; the tests drive it directly.
///
/// <para>Rules: the sort is stable (ties keep the caller's order); empty cells sort after every value in both directions
/// for a <see cref="UiTableColumn.NullsLast"/> column; a cell with a <see cref="UiTableCell.Value"/> sorts by it, otherwise
/// by its text (case-insensitive, then exact). Groups are listed by name (the "other" group last); the sort applies within
/// each group. Selection, drawers and edits are by row id, so a re-sort, a regroup or <see cref="SetRows"/> keeps them.</para>
/// </summary>
internal sealed class UiDataTableModel
{
    private readonly List<UiTableColumn> _columns;
    private readonly Dictionary<string, UiTableColumn> _byKey = new(StringComparer.Ordinal);
    private List<UiTableRow> _rows = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<UiTableSortKey> _sort = new();
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
    private readonly List<string> _selected = new();
    private readonly HashSet<string> _selectedSet = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UiTableEdit> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rejected = new(StringComparer.Ordinal);
    private readonly Func<double> _clock;
    private List<UiTableLine>? _lines;
    private List<string> _groups = new();
    private string? _anchor;

    internal UiDataTableModel(IEnumerable<UiTableColumn> columns, UiDataTableOptions options)
    {
        Options = options ?? new UiDataTableOptions();
        _columns = new List<UiTableColumn>();
        foreach (var c in columns ?? throw new ArgumentNullException(nameof(columns)))
        {
            if (c == null) continue;
            if (_byKey.ContainsKey(c.Key)) throw new ArgumentException("Two columns have the key '" + c.Key + "'.", nameof(columns));
            if (c.Kind == UiTableColumnKind.Actions && UiDataTableLayout.ActionsColumn(_columns) != null)
                throw new ArgumentException("A table has at most one actions column ('" + c.Key + "' is the second).", nameof(columns));
            if (c.Kind == UiTableColumnKind.Choice)
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var choice in c.Choices)
                    if (choice != null && !ids.Add(choice.Id))
                        throw new ArgumentException("Column '" + c.Key + "' has two choices with the id '" + choice.Id + "'.", nameof(columns));
            }

            _byKey[c.Key] = c;
            _columns.Add(c);
            if (c.Hidden && UiDataTableLayout.Hideable(c)) _hidden.Add(c.Key);
        }

        if (_columns.Count == 0) throw new ArgumentException("A table needs at least one column.", nameof(columns));
        Grouped = Options.Grouped;
        if (Options.Sort != null) SetSort(Options.Sort);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _clock = Options.Clock ?? (() => watch.Elapsed.TotalSeconds);
    }

    internal UiDataTableOptions Options { get; }

    internal IReadOnlyList<UiTableColumn> Columns => _columns;

    internal UiTableColumn? Column(string? key) => key != null && _byKey.TryGetValue(key, out var c) ? c : null;

    internal IReadOnlyList<UiTableRow> Rows => _rows;

    internal UiTableRow? Find(string? id) => id != null && _index.TryGetValue(id, out int i) ? _rows[i] : null;

    internal double Now => _clock();

    // ---- Rows --------------------------------------------------------------------------------

    /// <summary>Replace every row. Duplicate ids are refused (nothing changes). Selection and drawers drop vanished ids.</summary>
    internal void SetRows(IEnumerable<UiTableRow>? rows)
    {
        var next = new List<UiTableRow>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in rows ?? Array.Empty<UiTableRow>())
        {
            if (r == null) continue;
            if (index.ContainsKey(r.Id)) throw new ArgumentException("Two rows have the id '" + r.Id + "'.", nameof(rows));
            index[r.Id] = next.Count;
            next.Add(r);
        }

        _rows = next;
        _index.Clear();
        foreach (var p in index) _index[p.Key] = p.Value;
        bool selectionChanged = false;
        for (int i = _selected.Count - 1; i >= 0; i--)
        {
            if (_index.ContainsKey(_selected[i])) continue;
            _selectedSet.Remove(_selected[i]);
            _selected.RemoveAt(i);
            selectionChanged = true;
        }

        if (_anchor != null && !_index.ContainsKey(_anchor)) _anchor = null;
        _expanded.RemoveWhere(id => !_index.ContainsKey(id) || !HasDetail(_rows[_index[id]]));
        foreach (var key in new List<string>(_rejected.Keys))
            if (!_index.ContainsKey(key.Substring(0, key.IndexOf('\u001f')))) _rejected.Remove(key);
        // An edit of a row that left the table is dropped: no spinner, no count in the footer, and a late answer has no
        // effect (Complete refuses an edit that is not pending).
        List<UiTableEdit>? gone = null;
        foreach (var e in _pending.Values)
            if (!_index.ContainsKey(e.RowId)) (gone ??= new List<UiTableEdit>()).Add(e);
        if (gone != null)
        {
            foreach (var e in gone)
            {
                _pending.Remove(EditKey(e.RowId, e.Column));
                e.State = UiTableEditState.Rejected;
                e.Reason = RowGoneReason;
            }
        }

        SelectionDroppedOnRefresh = selectionChanged;
        Invalidate();
    }

    /// <summary>The reason a waiting edit is dropped with when its row leaves the table (not shown: the row is gone).</summary>
    internal const string RowGoneReason = "The row left the table.";

    /// <summary>The last <see cref="SetRows"/> dropped selected ids (the table raises no event for it).</summary>
    internal bool SelectionDroppedOnRefresh { get; private set; }

    /// <summary>Drop the cached body lines, group members and subtotals (rows, sort, grouping, folding or a value changed).</summary>
    internal void Invalidate()
    {
        _lines = null;
        _members = null;
        _aggregates.Clear();
    }

    private Dictionary<string, List<UiTableRow>>? _members;
    private readonly Dictionary<string, double?> _aggregates = new(StringComparer.Ordinal);

    // ---- Sort --------------------------------------------------------------------------------

    internal IReadOnlyList<UiTableSortKey> Sort => _sort;

    /// <summary>Replace the sort keys (unknown and unsortable columns and repeats are skipped). True when it changed.</summary>
    internal bool SetSort(IEnumerable<UiTableSortKey>? keys)
    {
        var next = new List<UiTableSortKey>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in keys ?? Array.Empty<UiTableSortKey>())
        {
            var c = Column(k.Column);
            if (c == null || !Sorts(c) || !seen.Add(c.Key)) continue;
            next.Add(new UiTableSortKey(c.Key, k.Ascending));
        }

        if (SameSort(next)) return false;
        _sort.Clear();
        _sort.AddRange(next);
        Invalidate();
        return true;
    }

    private bool SameSort(List<UiTableSortKey> next)
    {
        if (next.Count != _sort.Count) return false;
        for (int i = 0; i < next.Count; i++)
            if (!next[i].Equals(_sort[i])) return false;
        return true;
    }

    /// <summary>
    /// A header click. Plain: sort by <paramref name="column"/> alone, ascending; when it already is the only key, flip it.
    /// <paramref name="add"/> (Shift): append it as the next key, ascending; when it is already a key, flip that key in place.
    /// A plain click on a column that is one of several keys makes it the only key, keeping its direction flipped.
    /// </summary>
    internal bool ClickSort(string column, bool add)
    {
        var c = Column(column);
        if (c == null || !Sorts(c)) return false;
        int at = _sort.FindIndex(k => string.Equals(k.Column, c.Key, StringComparison.Ordinal));
        var next = new List<UiTableSortKey>(_sort);
        if (add)
        {
            if (at >= 0) next[at] = new UiTableSortKey(c.Key, !next[at].Ascending);
            else next.Add(new UiTableSortKey(c.Key, DefaultAscending(c)));
        }
        else
        {
            bool asc = at >= 0 ? !next[at].Ascending : DefaultAscending(c);
            next.Clear();
            next.Add(new UiTableSortKey(c.Key, asc));
        }

        return SetSort(next);
    }

    /// <summary>Whether a column sorts: <see cref="UiTableColumn.Sortable"/>, never an actions column.</summary>
    internal static bool Sorts(UiTableColumn c) => c.Sortable && c.Kind != UiTableColumnKind.Actions;

    /// <summary>
    /// Row <paramref name="rowId"/>'s action <paramref name="actionId"/> when it may be pressed now (known and enabled), or
    /// null.
    /// </summary>
    internal UiRowAction? PressableAction(string? rowId, string? actionId)
    {
        var action = Find(rowId)?.FindAction(actionId);
        return action is { Enabled: true } ? action : null;
    }

    /// <summary>A first click sorts A to Z, smallest first.</summary>
    private static bool DefaultAscending(UiTableColumn c) => true;

    /// <summary>The rows in sort order (the caller's order when there is no sort). Stable.</summary>
    internal List<UiTableRow> Sorted()
    {
        var list = new List<UiTableRow>(_rows);
        if (_sort.Count == 0) return list;
        var keys = new List<(UiTableColumn Column, bool Asc)>(_sort.Count);
        foreach (var k in _sort)
        {
            var c = Column(k.Column);
            if (c != null) keys.Add((c, k.Ascending));
        }

        var decorated = new KeyValuePair<int, UiTableRow>[list.Count];
        for (int i = 0; i < list.Count; i++) decorated[i] = new KeyValuePair<int, UiTableRow>(_index[list[i].Id], list[i]);
        Array.Sort(decorated, (a, b) =>
        {
            foreach (var (col, asc) in keys)
            {
                int r = CompareCells(a.Value[col.Key], b.Value[col.Key], col, asc);
                if (r != 0) return r;
            }

            return a.Key.CompareTo(b.Key);
        });
        for (int i = 0; i < decorated.Length; i++) list[i] = decorated[i].Value;
        return list;
    }

    /// <summary>A cell is empty for sorting: a number column's cell without a value, a text cell without text or value.</summary>
    internal static bool IsEmpty(UiTableCell? cell, UiTableColumn column)
    {
        if (cell == null) return true;
        // A choice sorts by its option's place; no option (or one the column does not have) is empty.
        if (column.Kind == UiTableColumnKind.Choice) return column.ChoiceIndex(cell.Choice) < 0;
        if (cell.Value.HasValue) return double.IsNaN(cell.Value.Value);
        if (column.Kind == UiTableColumnKind.Number) return true;
        return string.IsNullOrEmpty(cell.SortText ?? cell.Text);
    }

    /// <summary>The comparison one sort key makes, direction and the empty-cell rule included.</summary>
    internal static int CompareCells(UiTableCell? a, UiTableCell? b, UiTableColumn column, bool ascending)
    {
        bool ea = IsEmpty(a, column), eb = IsEmpty(b, column);
        if (ea && eb) return 0;
        if (ea || eb)
        {
            if (column.NullsLast) return ea ? 1 : -1;
            int n = ea ? -1 : 1;
            return ascending ? n : -n;
        }

        int r;
        if (column.Kind == UiTableColumnKind.Choice) r = column.ChoiceIndex(a!.Choice).CompareTo(column.ChoiceIndex(b!.Choice));
        else if (a!.Value.HasValue && b!.Value.HasValue) r = a.Value.Value.CompareTo(b.Value.Value);
        else if (a.Value.HasValue != b!.Value.HasValue) r = a.Value.HasValue ? -1 : 1; // numbers before words
        else
        {
            string sa = a.SortText ?? a.Text, sb = b.SortText ?? b.Text;
            r = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
            if (r == 0) r = string.CompareOrdinal(sa, sb);
        }

        return ascending ? r : -r;
    }

    // ---- Columns -----------------------------------------------------------------------------

    internal IReadOnlyCollection<string> Hidden => _hidden;

    /// <summary>The hidden keys in column order.</summary>
    internal List<string> HiddenInOrder()
    {
        var list = new List<string>();
        foreach (var c in _columns) if (_hidden.Contains(c.Key)) list.Add(c.Key);
        return list;
    }

    internal bool IsHidden(string column) => _hidden.Contains(column);

    /// <summary>Show or hide one column. Always-shown columns and unknown keys refuse. True when it changed.</summary>
    internal bool SetVisible(string column, bool visible)
    {
        var c = Column(column);
        if (c == null || !UiDataTableLayout.Hideable(c)) return false;
        return visible ? _hidden.Remove(c.Key) : _hidden.Add(c.Key);
    }

    /// <summary>Hide exactly <paramref name="columns"/> (hideable ones). True when the set changed.</summary>
    internal bool SetHidden(IEnumerable<string>? columns)
    {
        var next = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in columns ?? Array.Empty<string>())
        {
            var c = Column(k);
            if (c != null && UiDataTableLayout.Hideable(c)) next.Add(c.Key);
        }

        if (next.SetEquals(_hidden)) return false;
        _hidden.Clear();
        _hidden.UnionWith(next);
        return true;
    }

    /// <summary>The columns the chooser offers: every hideable one, in column order.</summary>
    internal List<UiTableColumn> ChooserColumns()
    {
        var list = new List<UiTableColumn>();
        foreach (var c in _columns) if (UiDataTableLayout.Hideable(c)) list.Add(c);
        return list;
    }

    // ---- Grouping and lines --------------------------------------------------------------------

    private bool _grouped;

    internal bool Grouped
    {
        get => _grouped;
        set
        {
            if (_grouped == value) return;
            _grouped = value;
            Invalidate();
        }
    }

    internal IReadOnlyList<string> Groups
    {
        get
        {
            Lines();
            return _groups;
        }
    }

    internal bool IsCollapsed(string group) => group != null && _collapsed.Contains(group);

    internal bool SetCollapsed(string group, bool collapsed)
    {
        if (string.IsNullOrEmpty(group)) return false;
        bool changed = collapsed ? _collapsed.Add(group) : _collapsed.Remove(group);
        if (changed) Invalidate();
        return changed;
    }

    /// <summary>The groups a row sits under (the "other" group when it names none), each once.</summary>
    internal List<string> GroupsOf(UiTableRow row)
    {
        var list = new List<string>();
        foreach (var g in row.Groups)
        {
            if (string.IsNullOrWhiteSpace(g)) continue;
            string name = g.Trim();
            if (!list.Contains(name)) list.Add(name);
        }

        if (list.Count == 0) list.Add(OtherGroup);
        return list;
    }

    private string OtherGroup => string.IsNullOrWhiteSpace(Options.OtherGroup) ? "Other" : Options.OtherGroup;

    internal bool AnySubtotal()
    {
        foreach (var c in _columns) if (c.Subtotal != UiTableSubtotal.None) return true;
        return false;
    }

    /// <summary>Whether the table draws a grand total line.</summary>
    internal bool HasTotal => Options.TotalRow && AnySubtotal() && _rows.Count > 0;

    /// <summary>The flattened body: what each line of the scroll window draws. Cached until something changes.</summary>
    internal IReadOnlyList<UiTableLine> Lines()
    {
        if (_lines != null) return _lines;
        var lines = new List<UiTableLine>();
        var sorted = Sorted();
        _groups = new List<string>();
        if (!_grouped)
        {
            foreach (var row in sorted) AddRow(lines, row, "");
        }
        else
        {
            var members = Members(sorted);

            var names = new List<string>(members.Keys);
            string other = OtherGroup;
            names.Sort((a, b) =>
            {
                bool ao = string.Equals(a, other, StringComparison.Ordinal), bo = string.Equals(b, other, StringComparison.Ordinal);
                if (ao != bo) return ao ? 1 : -1;
                int r = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                return r != 0 ? r : string.CompareOrdinal(a, b);
            });
            _groups = names;
            bool subtotals = AnySubtotal();
            foreach (var g in names)
            {
                lines.Add(new UiTableLine(UiTableLineKind.GroupHeader, null, g));
                if (!_collapsed.Contains(g))
                    foreach (var row in members[g]) AddRow(lines, row, g);
                if (subtotals) lines.Add(new UiTableLine(UiTableLineKind.Subtotal, null, g));
            }
        }

        if (HasTotal) lines.Add(new UiTableLine(UiTableLineKind.Total, null, ""));
        _lines = lines;
        return lines;
    }

    private void AddRow(List<UiTableLine> lines, UiTableRow row, string group)
    {
        lines.Add(new UiTableLine(UiTableLineKind.Row, row, group));
        if (!_expanded.Contains(row.Id)) return;
        foreach (var text in DetailLines(row, Options.DetailLines))
            lines.Add(new UiTableLine(UiTableLineKind.Detail, row, group, text));
    }

    /// <summary>
    /// A drawer's lines: the detail split at newlines, empty lines dropped, at most <paramref name="max"/> (1 to 8); when
    /// some did not fit the last one says how many more.
    /// </summary>
    internal static List<string> DetailLines(UiTableRow row, int max)
    {
        max = max < 1 ? 1 : max > 8 ? 8 : max;
        var all = new List<string>();
        foreach (var raw in (row.Detail ?? "").Replace("\r", "").Split('\n'))
        {
            string t = raw.Trim();
            if (t.Length > 0) all.Add(t);
        }

        if (all.Count <= max) return all;
        var lines = all.GetRange(0, max - 1);
        int more = all.Count - (max - 1);
        lines.Add(more + " more lines");
        return lines;
    }

    internal static bool HasDetail(UiTableRow row) => !string.IsNullOrWhiteSpace(row.Detail);

    /// <summary>The first line drawing row <paramref name="id"/>, or -1.</summary>
    internal int LineOf(string id)
    {
        var lines = Lines();
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Kind == UiTableLineKind.Row && string.Equals(lines[i].Row!.Id, id, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>How many lines are data rows (each occurrence), and the 1-based ordinals of the first and last of them in [from, to).</summary>
    internal int RowLines(int from, int to, out int first, out int last)
    {
        var lines = Lines();
        first = 0;
        last = 0;
        int n = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind != UiTableLineKind.Row) continue;
            n++;
            if (i < from || i >= to) continue;
            if (first == 0) first = n;
            last = n;
        }

        return n;
    }

    // ---- Subtotals -----------------------------------------------------------------------------

    /// <summary>Column <paramref name="column"/>'s subtotal over <paramref name="rows"/> (each counted once), or null.</summary>
    internal static double? Aggregate(IEnumerable<UiTableRow> rows, UiTableColumn column)
    {
        if (column.Subtotal == UiTableSubtotal.None) return null;
        double sum = 0;
        double? min = null, max = null;
        int count = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!seen.Add(r.Id)) continue;
            var v = r[column.Key]?.Value;
            if (v == null || double.IsNaN(v.Value)) continue;
            count++;
            sum += v.Value;
            min = min == null ? v : Math.Min(min.Value, v.Value);
            max = max == null ? v : Math.Max(max.Value, v.Value);
        }

        return column.Subtotal switch
        {
            UiTableSubtotal.Sum => count == 0 ? (double?)null : sum,
            UiTableSubtotal.Min => min,
            UiTableSubtotal.Max => max,
            UiTableSubtotal.Count => count,
            _ => null,
        };
    }

    /// <summary>The rows under group <paramref name="group"/> (folded or not), in the current sort. Cached with the lines.</summary>
    internal IReadOnlyList<UiTableRow> RowsIn(string group)
    {
        var members = _members ?? Members(Sorted());
        return members.TryGetValue(group ?? "", out var list) ? list : Array.Empty<UiTableRow>();
    }

    /// <summary>Every group's rows in sort order; cached until <see cref="Invalidate"/>.</summary>
    private Dictionary<string, List<UiTableRow>> Members(List<UiTableRow> sorted)
    {
        if (_members != null) return _members;
        var members = new Dictionary<string, List<UiTableRow>>(StringComparer.Ordinal);
        foreach (var row in sorted)
        {
            foreach (var g in GroupsOf(row))
            {
                if (!members.TryGetValue(g, out var list)) members[g] = list = new List<UiTableRow>();
                list.Add(row);
            }
        }

        _members = members;
        return members;
    }

    /// <summary>
    /// Column <paramref name="column"/>'s subtotal over group <paramref name="group"/>, or its grand total over every row
    /// (each once) when <paramref name="group"/> is null. Cached until <see cref="Invalidate"/>, so a scroll over a
    /// grouped table never adds a group up again.
    /// </summary>
    internal double? SubtotalOf(string? group, UiTableColumn column)
    {
        if (column.Subtotal == UiTableSubtotal.None) return null;
        string key = (group == null ? "\u0001" : group) + "\u001f" + column.Key;
        if (_aggregates.TryGetValue(key, out var cached)) return cached;
        var value = Aggregate(group == null ? _rows : RowsIn(group), column);
        _aggregates[key] = value;
        AggregateComputations++;
        return value;
    }

    /// <summary>Tests: how many subtotals were computed (not served from the cache).</summary>
    internal int AggregateComputations { get; private set; }

    /// <summary>The value text a column draws: the cell's own text, else the value in the column's format, else the null text.</summary>
    internal static string Display(UiTableCell? cell, UiTableColumn column)
    {
        if (cell == null) return "";
        if (column.Kind == UiTableColumnKind.Choice) return column.FindChoice(cell.Choice)?.Text ?? cell.Text;
        if (cell.Text.Length > 0) return cell.Text;
        // A text column's value only sorts (a status by severity): an empty text stays empty.
        return column.Kind == UiTableColumnKind.Number ? FormatValue(cell.Value, column) : "";
    }

    internal static string FormatValue(double? value, UiTableColumn column)
    {
        if (value == null || double.IsNaN(value.Value)) return column.NullText ?? "";
        try { return value.Value.ToString(string.IsNullOrEmpty(column.Format) ? "#,0.##" : column.Format, CultureInfo.InvariantCulture); }
        catch (FormatException) { return value.Value.ToString(CultureInfo.InvariantCulture); }
    }

    // ---- Selection -----------------------------------------------------------------------------

    internal IReadOnlyList<string> Selected => _selected;

    internal bool IsSelected(string id) => _selectedSet.Contains(id);

    internal string? Anchor => _anchor;

    /// <summary>
    /// A click on line <paramref name="line"/> (a row) with <paramref name="mods"/>. Plain: that row alone. Ctrl (multi): add or
    /// remove it. Shift (multi): the rows from the anchor's line to this one, in body order (Ctrl+Shift adds them). The
    /// anchor is the last row clicked without Shift. True when the selection changed.
    /// </summary>
    internal bool Click(int line, UiClickModifiers mods)
    {
        var lines = Lines();
        if (!Options.Selectable || line < 0 || line >= lines.Count || lines[line].Kind != UiTableLineKind.Row) return false;
        string id = lines[line].Row!.Id;
        bool multi = Options.MultiSelect;
        if (multi && (mods & UiClickModifiers.Shift) != 0 && _anchor != null)
        {
            int from = NearestLineOf(_anchor, line);
            if (from >= 0)
            {
                var range = new List<string>();
                int a = Math.Min(from, line), b = Math.Max(from, line);
                for (int i = a; i <= b; i++)
                {
                    if (lines[i].Kind != UiTableLineKind.Row) continue;
                    string rid = lines[i].Row!.Id;
                    if (!range.Contains(rid)) range.Add(rid);
                }

                if ((mods & UiClickModifiers.Ctrl) != 0)
                {
                    var union = new List<string>(_selected);
                    foreach (var r in range) if (!union.Contains(r)) union.Add(r);
                    return Replace(union);
                }

                return Replace(range);
            }
        }

        _anchor = id;
        if (multi && (mods & UiClickModifiers.Ctrl) != 0) return Toggle(id);
        return Replace(new List<string> { id });
    }

    /// <summary>The check cell (and Ctrl-click): add or remove one row; it becomes the anchor. True when it changed.</summary>
    internal bool Toggle(string id)
    {
        if (!Options.Selectable || Find(id) == null) return false;
        _anchor = id;
        if (!Options.MultiSelect)
            return _selectedSet.Contains(id) ? Replace(new List<string>()) : Replace(new List<string> { id });
        if (_selectedSet.Remove(id))
        {
            _selected.Remove(id);
            return true;
        }

        _selectedSet.Add(id);
        _selected.Add(id);
        return true;
    }

    /// <summary>Select exactly <paramref name="ids"/> (known ones; one in single mode). True when it changed.</summary>
    internal bool Replace(IEnumerable<string> ids)
    {
        var next = new List<string>();
        foreach (var id in ids ?? Array.Empty<string>())
        {
            if (id == null || Find(id) == null || next.Contains(id)) continue;
            next.Add(id);
            if (!Options.MultiSelect) break;
        }

        bool same = next.Count == _selected.Count;
        for (int i = 0; same && i < next.Count; i++) same = string.Equals(next[i], _selected[i], StringComparison.Ordinal);
        if (same) return false;
        _selected.Clear();
        _selected.AddRange(next);
        _selectedSet.Clear();
        _selectedSet.UnionWith(next);
        return true;
    }

    /// <summary>The line of row <paramref name="id"/> nearest line <paramref name="near"/> (a row in two groups has two).</summary>
    private int NearestLineOf(string id, int near)
    {
        var lines = Lines();
        int best = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind != UiTableLineKind.Row || !string.Equals(lines[i].Row!.Id, id, StringComparison.Ordinal)) continue;
            if (best < 0 || Math.Abs(i - near) < Math.Abs(best - near)) best = i;
        }

        return best;
    }

    // ---- Drawers ------------------------------------------------------------------------------

    internal bool IsExpanded(string id) => _expanded.Contains(id);

    internal IReadOnlyCollection<string> Expanded => _expanded;

    internal bool SetExpanded(string id, bool expanded)
    {
        var row = Find(id);
        if (row == null || !HasDetail(row)) return false;
        bool changed = expanded ? _expanded.Add(id) : _expanded.Remove(id);
        if (changed) Invalidate();
        return changed;
    }

    // ---- Edits --------------------------------------------------------------------------------

    internal static string EditKey(string rowId, string column) => rowId + "\u001f" + column;

    internal IReadOnlyCollection<UiTableEdit> Pending => _pending.Values;

    internal UiTableEdit? PendingAt(string rowId, string column) =>
        _pending.TryGetValue(EditKey(rowId, column), out var e) ? e : null;

    internal string? RejectedAt(string rowId, string column) =>
        _rejected.TryGetValue(EditKey(rowId, column), out var r) ? r : null;

    /// <summary>The last refusal's reason (an answer, a timeout, a bad value); empty when none or cleared.</summary>
    internal string Reason { get; set; } = "";

    /// <summary>Whether row <paramref name="row"/>'s cell in <paramref name="column"/> takes typing now.</summary>
    internal bool CanEdit(UiTableRow row, UiTableColumn column) =>
        column.Editable && column.Kind == UiTableColumnKind.Number && !row.Locked && row[column.Key]?.Locked != true
        && PendingAt(row.Id, column.Key) == null;

    /// <summary>
    /// Whether row <paramref name="row"/>'s cell in choice column <paramref name="column"/> takes a click now: an editable
    /// choice column with options, the row and cell not locked, no answer awaited.
    /// </summary>
    internal bool CanChoose(UiTableRow row, UiTableColumn column) =>
        column.Editable && column.Kind == UiTableColumnKind.Choice && column.Choices.Count > 0 && !row.Locked
        && row[column.Key]?.Locked != true && PendingAt(row.Id, column.Key) == null;

    /// <summary>
    /// The option a click on a choice cell asks for: the one after <paramref name="current"/> in the column's order, the
    /// last wrapping to the first; the first when the cell has no option (or one the column does not have). Null when the
    /// column has no options.
    /// </summary>
    internal static UiTableChoice? NextChoice(UiTableColumn column, string? current)
    {
        var all = column.Choices;
        if (all.Count == 0) return null;
        int at = column.ChoiceIndex(current);
        for (int step = 1; step <= all.Count; step++)
        {
            var next = all[((at < 0 ? -1 : at) + step) % all.Count];
            if (next != null) return next;
        }

        return null;
    }

    /// <summary>
    /// Start an edit of <paramref name="rowId"/>'s <paramref name="column"/> with the typed <paramref name="text"/> (on a
    /// choice column: an option id, empty for the next one). Null when nothing changed (the same value) or it is refused;
    /// <paramref name="refusal"/> then says why (empty for no change).
    /// </summary>
    internal UiTableEdit? Begin(string rowId, string column, string? text, Func<UiTableEdit, bool, string?, bool> complete, out string refusal)
    {
        refusal = "";
        var row = Find(rowId);
        var col = Column(column);
        if (row == null || col == null)
        {
            refusal = "That cell is not in the table.";
            return null;
        }

        if (col.Kind == UiTableColumnKind.Choice) return BeginChoice(row, col, text, complete, out refusal);

        if (!col.Editable || col.Kind != UiTableColumnKind.Number)
        {
            refusal = col.Caption + " cannot be edited here.";
            return null;
        }

        if (PendingAt(rowId, col.Key) != null)
        {
            refusal = "Waiting for the server to answer the last change.";
            return null;
        }

        if (row.Locked || row[col.Key]?.Locked == true)
        {
            refusal = "You cannot change " + col.Caption + " here.";
            return null;
        }

        var cell = row[col.Key];
        string typed = (text ?? "").Trim();
        bool clear = typed.Length == 0;
        double? value = null;
        if (!clear)
        {
            if (!TryParse(typed, out double parsed))
            {
                refusal = "\"" + typed + "\" is not a number.";
                return null;
            }

            value = parsed;
        }

        bool inherited = cell?.Inherited == true;
        if (clear && (cell == null || inherited)) return null; // already inherited
        if (!clear && !inherited && cell?.Value is double old && old.Equals(value!.Value)) return null;
        var edit = new UiTableEdit(rowId, col.Key, cell?.Value, value, clear, typed, Now, complete);
        _pending[EditKey(rowId, col.Key)] = edit;
        _rejected.Remove(EditKey(rowId, col.Key));
        return edit;
    }

    /// <summary>A click on a choice cell (or <see cref="Begin"/> with an option id): the same refusals as a typed edit.</summary>
    private UiTableEdit? BeginChoice(UiTableRow row, UiTableColumn col, string? text, Func<UiTableEdit, bool, string?, bool> complete, out string refusal)
    {
        refusal = "";
        if (!col.Editable || col.Choices.Count == 0)
        {
            refusal = col.Caption + " cannot be edited here.";
            return null;
        }

        if (PendingAt(row.Id, col.Key) != null)
        {
            refusal = "Waiting for the server to answer the last change.";
            return null;
        }

        if (row.Locked || row[col.Key]?.Locked == true)
        {
            refusal = "You cannot change " + col.Caption + " here.";
            return null;
        }

        var cell = row[col.Key];
        string? current = cell?.Choice;
        string wanted = (text ?? "").Trim();
        UiTableChoice? next;
        if (wanted.Length == 0) next = NextChoice(col, current);
        else
        {
            next = col.FindChoice(wanted);
            if (next == null)
            {
                refusal = "\"" + wanted + "\" is not one of the choices.";
                return null;
            }
        }

        if (next == null || string.Equals(next.Id, current, StringComparison.Ordinal)) return null;
        int oldIndex = col.ChoiceIndex(current);
        var edit = new UiTableEdit(row.Id, col.Key, oldIndex < 0 ? null : oldIndex, col.ChoiceIndex(next.Id), false, next.Id, Now, complete)
        {
            OldChoice = oldIndex < 0 ? null : current,
            NewChoice = next.Id,
        };
        _pending[EditKey(row.Id, col.Key)] = edit;
        _rejected.Remove(EditKey(row.Id, col.Key));
        return edit;
    }

    /// <summary>A typed number: invariant (<c>1,250.5</c>) first, then the player's culture.</summary>
    internal static bool TryParse(string text, out double value)
    {
        const NumberStyles styles = NumberStyles.Float | NumberStyles.AllowThousands;
        if (double.TryParse(text, styles, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value)) return true;
        return double.TryParse(text, styles, CultureInfo.CurrentCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>
    /// Answer an edit. Resolved: the row's cell takes the new value (<paramref name="text"/> its display when given).
    /// Rejected: the cell keeps its value and the reason is kept for the row's tooltip and the status line. False when the
    /// edit is not pending.
    /// </summary>
    internal bool Complete(UiTableEdit edit, bool resolved, string? text)
    {
        if (edit == null || edit.State != UiTableEditState.Pending) return false;
        string key = EditKey(edit.RowId, edit.Column);
        if (!_pending.TryGetValue(key, out var live) || !ReferenceEquals(live, edit)) return false;
        _pending.Remove(key);
        var col = Column(edit.Column);
        var row = Find(edit.RowId);
        if (resolved)
        {
            edit.State = UiTableEditState.Resolved;
            if (row != null && col != null)
            {
                var old = row[col.Key];
                var cell = old?.Copy() ?? UiTableCell.Number(null);
                if (edit.NewChoice != null)
                {
                    // A choice takes its option; the option's own words and tone are drawn, so a display is not used.
                    cell.Choice = edit.NewChoice;
                    cell.Inherited = false;
                }
                else if (edit.Clear)
                {
                    cell.Inherited = true;
                    if (text != null) cell.Text = text;
                }
                else
                {
                    cell.Value = edit.NewValue;
                    cell.Inherited = false;
                    cell.Text = text ?? "";
                }

                row[col.Key] = cell;
                // The value moved: its subtotals (and, when sorted by it, the order) are stale.
                if (IsSortedBy(col.Key)) Invalidate();
                else _aggregates.Clear();
            }
        }
        else
        {
            edit.State = UiTableEditState.Rejected;
            edit.Reason = text ?? "";
            _rejected[key] = edit.Reason;
            Reason = (col != null && row != null ? CaptionOf(row) + ", " + col.Caption + ": " : "") + edit.Reason;
        }

        return true;
    }

    private bool IsSortedBy(string column)
    {
        foreach (var k in _sort) if (string.Equals(k.Column, column, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The row's name: its first column's text, else its id.</summary>
    internal string CaptionOf(UiTableRow row)
    {
        var first = row[_columns[0].Key];
        string t = first == null ? "" : Display(first, _columns[0]);
        return t.Length > 0 ? t : row.Id;
    }

    private readonly List<UiTableEdit> _late = new();

    /// <summary>Whether edits time out at all (<see cref="UiDataTableOptions.EditTimeout"/> above zero).</summary>
    internal bool Expires => Options.EditTimeout > TimeSpan.Zero;

    /// <summary>The text a timed-out edit is rejected with.</summary>
    internal const string TimeoutReason = "No answer from the server.";

    /// <summary>Reject every edit older than the timeout. Returns how many.</summary>
    internal int Expire()
    {
        var timeout = Options.EditTimeout;
        if (timeout <= TimeSpan.Zero || _pending.Count == 0) return 0;
        double now = Now;
        _late.Clear();
        foreach (var e in _pending.Values)
            if (now - e.Started >= timeout.TotalSeconds) _late.Add(e);
        if (_late.Count == 0) return 0;
        int n = _late.Count;
        foreach (var e in _late.ToArray()) Complete(e, false, TimeoutReason);
        _late.Clear();
        return n;
    }
}
