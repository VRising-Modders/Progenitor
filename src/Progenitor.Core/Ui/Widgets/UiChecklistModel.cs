using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>CHECKLIST1: what one body line of a checklist is.</summary>
internal enum UiChecklistLineKind
{
    /// <summary>A section heading (its name and counts; folds the section).</summary>
    Section = 0,

    /// <summary>An entry's row.</summary>
    Entry = 1,
}

/// <summary>CHECKLIST1: one body line of a checklist: a section heading or an entry.</summary>
internal readonly struct UiChecklistLine
{
    internal UiChecklistLine(UiChecklistLineKind kind, string section, UiChecklistEntry? entry)
    {
        Kind = kind;
        Section = section ?? "";
        Entry = entry;
    }

    internal UiChecklistLineKind Kind { get; }

    /// <summary>The section the line belongs to (the heading's name; empty when the list has no headings).</summary>
    internal string Section { get; }

    internal UiChecklistEntry? Entry { get; }

    public override string ToString() => Kind == UiChecklistLineKind.Section ? "# " + Section : Entry!.Id;
}

/// <summary>
/// CHECKLIST1: the checklist's state, game-free: entries, the search, the filter, the sort, folded sections, the checked
/// set and the flattened body lines. The handle draws it; <c>UiChecklistTests</c> drives it directly.
///
/// <para><b>Shown.</b> An entry is shown when the filter lists it (the All option lists everything; another filter lists
/// the entries whose <see cref="UiChecklistEntry.Filters"/> name it) and the search matches it: every word of the query
/// (case- and accent-insensitive, <see cref="UiPickerModel.Fold"/>) appears in its label, note, section, keywords or a text
/// cell. Entries under a folded heading are not shown (the heading stays, with its count).</para>
///
/// <para><b>Order.</b> Sections keep the order of their first entry in the caller's list (whatever the filter); within a
/// section the sort applies (stable: ties keep the caller's order; <see cref="UiDataTableModel.CompareCells"/>'s rules).
/// Entries without a section while others have one sit under <see cref="UiChecklistOptions.OtherSection"/>.</para>
///
/// <para><b>Checked set.</b> By id, in check order, independent of what is shown: the filter, the search, the sort, a fold
/// and <see cref="SetEntries"/> never change it, except that ids that leave or become disabled drop out. Only enabled
/// entries are checked. A click toggles one entry and makes it the anchor; a Shift-click sets every shown, enabled entry
/// from the anchor's line to the clicked one to the clicked entry's new state.</para>
/// </summary>
internal sealed class UiChecklistModel
{
    private readonly List<UiTableColumn> _columns = new();
    private readonly Dictionary<string, UiTableColumn> _byKey = new(StringComparer.Ordinal);
    private readonly List<UiChecklistFilter> _filters = new();
    private List<UiChecklistEntry> _entries = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    /// <summary>Each entry's folded search text and its cells per column, snapshot at <see cref="SetEntries"/>.</summary>
    private string[] _hay = Array.Empty<string>();
    private UiTableCell?[][] _cells = Array.Empty<UiTableCell?[]>();
    private readonly Dictionary<string, int> _sectionOrder = new(StringComparer.Ordinal);
    private bool _anySection;

    private readonly List<UiTableSortKey> _sort = new();
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly List<string> _selected = new();
    private readonly HashSet<string> _selectedSet = new(StringComparer.Ordinal);
    private string _query = "";
    private string[] _queryWords = Array.Empty<string>();
    private string _filter = "";
    private string? _anchor;

    private List<UiChecklistLine>? _lines;
    private List<UiChecklistEntry>? _shown;
    private List<string>? _sections;
    private readonly Dictionary<string, int> _matchedIn = new(StringComparer.Ordinal);
    private int _matched;

    internal UiChecklistModel(UiChecklistOptions? options)
    {
        Options = options ?? new UiChecklistOptions();
        var given = Options.Columns ?? Array.Empty<UiTableColumn>();
        bool hasLabel = false;
        foreach (var c in given)
            if (c != null && string.Equals(c.Key, UiChecklistEntry.LabelColumn, StringComparison.Ordinal)) hasLabel = true;
        if (!hasLabel)
            AddColumn(new UiTableColumn(UiChecklistEntry.LabelColumn, string.IsNullOrWhiteSpace(Options.LabelCaption) ? "Name" : Options.LabelCaption)
            {
                Hideable = false,
            });
        foreach (var c in given)
        {
            if (c == null) continue;
            if (_byKey.ContainsKey(c.Key)) throw new ArgumentException("Two columns have the key '" + c.Key + "'.", nameof(options));
            AddColumn(c);
        }

        if (!string.IsNullOrWhiteSpace(Options.AllFilterLabel) || (Options.Filters?.Count ?? 0) == 0)
            _filters.Add(AllOption);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Options.Filters ?? Array.Empty<UiChecklistFilter>())
        {
            if (f == null) continue;
            if (!seen.Add(f.Id)) throw new ArgumentException("Two filters have the id '" + f.Id + "'.", nameof(options));
            _filters.Add(f);
        }

        _filter = _filters[0].Id;
        SetFilter(Options.DefaultFilter);
        if (Options.Sort != null) SetSort(Options.Sort);
    }

    private UiChecklistFilter? _all;

    /// <summary>The All option (id empty, shown as <see cref="UiChecklistOptions.AllFilterLabel"/>).</summary>
    private UiChecklistFilter AllOption => _all ??= UiChecklistFilter.All(Options.AllFilterLabel);

    private void AddColumn(UiTableColumn c)
    {
        _byKey[c.Key] = c;
        _columns.Add(c);
    }

    internal UiChecklistOptions Options { get; }

    internal IReadOnlyList<UiTableColumn> Columns => _columns;

    internal UiTableColumn? Column(string? key) => key != null && _byKey.TryGetValue(key, out var c) ? c : null;

    internal IReadOnlyList<UiChecklistEntry> Entries => _entries;

    internal UiChecklistEntry? Find(string? id) => id != null && _index.TryGetValue(id, out int i) ? _entries[i] : null;

    /// <summary>Whether the filter dropdown is drawn (some filter beyond All).</summary>
    internal bool HasFilters => _filters.Count > 1 || _filters[0] != AllOption;

    internal IReadOnlyList<UiChecklistFilter> Filters => _filters;

    // ---- Entries ---------------------------------------------------------------------------------

    /// <summary>
    /// Replace every entry. Duplicate ids are refused (nothing changes). Checked ids that left, or are now disabled, drop
    /// out (<see cref="SelectionDroppedOnRefresh"/>).
    /// </summary>
    internal void SetEntries(IEnumerable<UiChecklistEntry>? entries)
    {
        var next = new List<UiChecklistEntry>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in entries ?? Array.Empty<UiChecklistEntry>())
        {
            if (e == null) continue;
            if (index.ContainsKey(e.Id)) throw new ArgumentException("Two checklist entries have the id '" + e.Id + "'.", nameof(entries));
            index[e.Id] = next.Count;
            next.Add(e);
        }

        _entries = next;
        _index.Clear();
        foreach (var p in index) _index[p.Key] = p.Value;
        _anySection = false;
        foreach (var e in next)
            if (!string.IsNullOrWhiteSpace(e.Section)) { _anySection = true; break; }
        _sectionOrder.Clear();
        _hay = new string[next.Count];
        _cells = new UiTableCell?[next.Count][];
        for (int i = 0; i < next.Count; i++)
        {
            var e = next[i];
            string section = SectionOf(e);
            if (!_sectionOrder.ContainsKey(section)) _sectionOrder[section] = _sectionOrder.Count;
            var cells = new UiTableCell?[_columns.Count];
            var hay = new System.Text.StringBuilder();
            Append(hay, e.Label);
            Append(hay, e.Note);
            Append(hay, e.Section);
            foreach (var k in e.Keywords) Append(hay, k);
            for (int c = 0; c < _columns.Count; c++)
            {
                var col = _columns[c];
                var cell = e[col.Key];
                if (cell == null && string.Equals(col.Key, UiChecklistEntry.LabelColumn, StringComparison.Ordinal)) cell = UiTableCell.Of(e.Label);
                else if (cell == null && string.Equals(col.Key, UiChecklistEntry.NoteColumn, StringComparison.Ordinal)) cell = UiTableCell.Of(e.Note);
                else if (cell != null && col.Kind == UiTableColumnKind.Text) Append(hay, cell.Text);
                cells[c] = cell;
            }

            _cells[i] = cells;
            _hay[i] = UiPickerModel.Fold(hay.ToString());
        }

        int before = _selected.Count;
        for (int i = _selected.Count - 1; i >= 0; i--)
        {
            var e = Find(_selected[i]);
            if (e != null && !e.Disabled) continue;
            _selectedSet.Remove(_selected[i]);
            _selected.RemoveAt(i);
        }

        SelectionDroppedOnRefresh = _selected.Count != before;
        if (_anchor != null && Find(_anchor) == null) _anchor = null;
        Invalidate();
    }

    private static void Append(System.Text.StringBuilder sb, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (sb.Length > 0) sb.Append('\n');
        sb.Append(text);
    }

    /// <summary>The last <see cref="SetEntries"/> dropped checked ids (no event is raised for it).</summary>
    internal bool SelectionDroppedOnRefresh { get; private set; }

    /// <summary>The section an entry is listed under: its own, the other section while some entry names one, else none.</summary>
    internal string SectionOf(UiChecklistEntry e)
    {
        if (!string.IsNullOrWhiteSpace(e.Section)) return e.Section.Trim();
        return _anySection ? OtherSection : "";
    }

    private string OtherSection => string.IsNullOrWhiteSpace(Options.OtherSection) ? "Other" : Options.OtherSection.Trim();

    /// <summary>The cell an entry draws and sorts by in <paramref name="column"/> (the label and note columns fall back to the entry's).</summary>
    internal UiTableCell? CellOf(UiChecklistEntry entry, UiTableColumn column)
    {
        if (!_index.TryGetValue(entry.Id, out int i) || !ReferenceEquals(_entries[i], entry)) return null;
        int c = _columns.IndexOf(column);
        return c < 0 ? null : _cells[i][c];
    }

    internal void Invalidate()
    {
        _lines = null;
        _shown = null;
        _sections = null;
    }

    // ---- Search and filter --------------------------------------------------------------------------

    internal string Query => _query;

    /// <summary>Set the search text. True when what it matches changed (the folded words).</summary>
    internal bool SetQuery(string? query)
    {
        _query = query ?? "";
        var words = UiPickerModel.Fold(_query).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (SameWords(words, _queryWords)) return false;
        _queryWords = words;
        Invalidate();
        return true;
    }

    private static bool SameWords(string[] a, string[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    internal string Filter => _filter;

    /// <summary>The selected filter option.</summary>
    internal UiChecklistFilter FilterOption
    {
        get
        {
            foreach (var f in _filters) if (string.Equals(IdOf(f), _filter, StringComparison.Ordinal)) return f;
            return _filters[0];
        }
    }

    /// <summary>A filter option's id as <see cref="Filter"/> names it (empty for All).</summary>
    internal static string IdOf(UiChecklistFilter f) => f.Id;

    /// <summary>Select filter <paramref name="id"/> (empty: All). Unknown ids are ignored. True when it changed.</summary>
    internal bool SetFilter(string? id)
    {
        string want = (id ?? "").Trim();
        bool known = false;
        foreach (var f in _filters) if (string.Equals(IdOf(f), want, StringComparison.Ordinal)) known = true;
        if (!known || string.Equals(want, _filter, StringComparison.Ordinal)) return false;
        _filter = want;
        Invalidate();
        return true;
    }

    /// <summary>Whether the filter lists <paramref name="e"/>.</summary>
    internal bool PassesFilter(UiChecklistEntry e)
    {
        if (_filter.Length == 0) return true;
        foreach (var t in e.Filters)
            if (t != null && string.Equals(t.Trim(), _filter, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Whether the search matches entry <paramref name="i"/>: every query word appears in its search text.</summary>
    private bool Matches(int i)
    {
        if (_queryWords.Length == 0) return true;
        string hay = _hay[i];
        foreach (var w in _queryWords)
            if (hay.IndexOf(w, StringComparison.Ordinal) < 0) return false;
        return true;
    }

    // ---- Sort ---------------------------------------------------------------------------------------

    internal IReadOnlyList<UiTableSortKey> Sort => _sort;

    /// <summary>Replace the sort keys (unknown and unsortable columns and repeats skipped). True when it changed.</summary>
    internal bool SetSort(IEnumerable<UiTableSortKey>? keys)
    {
        var next = new List<UiTableSortKey>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in keys ?? Array.Empty<UiTableSortKey>())
        {
            var c = Column(k.Column);
            if (c == null || !c.Sortable || !seen.Add(c.Key)) continue;
            next.Add(new UiTableSortKey(c.Key, k.Ascending));
        }

        bool same = next.Count == _sort.Count;
        for (int i = 0; same && i < next.Count; i++) same = next[i].Equals(_sort[i]);
        if (same) return false;
        _sort.Clear();
        _sort.AddRange(next);
        Invalidate();
        return true;
    }

    /// <summary>A header click (the data table's rule): plain sorts by the column alone (again: flips); add appends or flips it.</summary>
    internal bool ClickSort(string column, bool add)
    {
        var c = Column(column);
        if (c == null || !c.Sortable) return false;
        int at = _sort.FindIndex(k => string.Equals(k.Column, c.Key, StringComparison.Ordinal));
        var next = new List<UiTableSortKey>(_sort);
        if (add)
        {
            if (at >= 0) next[at] = new UiTableSortKey(c.Key, !next[at].Ascending);
            else next.Add(new UiTableSortKey(c.Key, true));
        }
        else
        {
            bool asc = at < 0 || !next[at].Ascending;
            next.Clear();
            next.Add(new UiTableSortKey(c.Key, asc));
        }

        return SetSort(next);
    }

    private int Compare(int a, int b)
    {
        foreach (var k in _sort)
        {
            var col = Column(k.Column);
            if (col == null) continue;
            int c = _columns.IndexOf(col);
            int r = UiDataTableModel.CompareCells(_cells[a][c], _cells[b][c], col, k.Ascending);
            if (r != 0) return r;
        }

        return a.CompareTo(b);
    }

    // ---- Sections and lines -------------------------------------------------------------------------

    internal bool IsCollapsed(string section) => section != null && _collapsed.Contains(section);

    /// <summary>Fold or unfold a section (refused while <see cref="UiChecklistOptions.CollapsibleSections"/> is off). True when it changed.</summary>
    internal bool SetCollapsed(string section, bool collapsed)
    {
        if (string.IsNullOrEmpty(section) || (collapsed && !Options.CollapsibleSections)) return false;
        bool changed = collapsed ? _collapsed.Add(section) : _collapsed.Remove(section);
        if (changed) Invalidate();
        return changed;
    }

    /// <summary>The flattened body: what each line of the scroll window draws. Cached until something changes.</summary>
    internal IReadOnlyList<UiChecklistLine> Lines()
    {
        if (_lines != null) return _lines;
        var bySection = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        _matchedIn.Clear();
        _matched = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (!PassesFilter(e) || !Matches(i)) continue;
            string s = SectionOf(e);
            if (!bySection.TryGetValue(s, out var list)) bySection[s] = list = new List<int>();
            list.Add(i);
            _matched++;
        }

        var names = new List<string>(bySection.Keys);
        names.Sort((a, b) => _sectionOrder[a].CompareTo(_sectionOrder[b]));
        var lines = new List<UiChecklistLine>(_matched + names.Count);
        var shown = new List<UiChecklistEntry>(_matched);
        var sections = new List<string>(names.Count);
        foreach (var name in names)
        {
            var members = bySection[name];
            if (_sort.Count > 0) members.Sort(Compare);
            _matchedIn[name] = members.Count;
            bool heading = name.Length > 0;
            if (heading)
            {
                lines.Add(new UiChecklistLine(UiChecklistLineKind.Section, name, null));
                sections.Add(name);
            }

            if (heading && _collapsed.Contains(name)) continue;
            foreach (int i in members)
            {
                lines.Add(new UiChecklistLine(UiChecklistLineKind.Entry, name, _entries[i]));
                shown.Add(_entries[i]);
            }
        }

        _lines = lines;
        _shown = shown;
        _sections = sections;
        return lines;
    }

    /// <summary>The entries the filter and the search show, in display order, folded sections left out.</summary>
    internal IReadOnlyList<UiChecklistEntry> Shown
    {
        get
        {
            Lines();
            return _shown!;
        }
    }

    internal IReadOnlyList<string> Sections
    {
        get
        {
            Lines();
            return _sections!;
        }
    }

    /// <summary>How many entries the filter and the search match (folded ones included).</summary>
    internal int MatchedCount
    {
        get
        {
            Lines();
            return _matched;
        }
    }

    /// <summary>How many entries of section <paramref name="section"/> the filter and the search match.</summary>
    internal int MatchedIn(string section)
    {
        Lines();
        return _matchedIn.TryGetValue(section ?? "", out int n) ? n : 0;
    }

    /// <summary>How many checked entries section <paramref name="section"/> has (shown or not).</summary>
    internal int CheckedIn(string section)
    {
        int n = 0;
        foreach (var id in _selected)
        {
            var e = Find(id);
            if (e != null && string.Equals(SectionOf(e), section, StringComparison.Ordinal)) n++;
        }

        return n;
    }

    /// <summary>The line drawing entry <paramref name="id"/>, or -1.</summary>
    internal int LineOf(string id)
    {
        var lines = Lines();
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Kind == UiChecklistLineKind.Entry && string.Equals(lines[i].Entry!.Id, id, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>A section heading's words: <c>Storage (124)</c>, and <c> · 3 selected</c> while some are checked.</summary>
    internal string SectionCaption(string section)
    {
        string text = section + " (" + MatchedIn(section).ToString(CultureInfo.InvariantCulture) + ")";
        int n = CheckedIn(section);
        return n > 0 ? text + " · " + n.ToString(CultureInfo.InvariantCulture) + " selected" : text;
    }

    // ---- Checked set --------------------------------------------------------------------------------

    internal IReadOnlyList<string> Selected => _selected;

    internal bool IsChecked(string? id) => id != null && _selectedSet.Contains(id);

    internal string? Anchor => _anchor;

    /// <summary>Check or uncheck one enabled entry. True when it changed.</summary>
    internal bool Set(string? id, bool on)
    {
        var e = Find(id);
        if (e == null) return false;
        if (!on)
        {
            if (!_selectedSet.Remove(e.Id)) return false;
            _selected.Remove(e.Id);
            return true;
        }

        if (e.Disabled || !_selectedSet.Add(e.Id)) return false;
        _selected.Add(e.Id);
        return true;
    }

    /// <summary>Check exactly <paramref name="ids"/> (known, enabled, once each). True when the set changed.</summary>
    internal bool Replace(IEnumerable<string>? ids)
    {
        var next = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids ?? Array.Empty<string>())
        {
            var e = Find(id);
            if (e == null || e.Disabled || !seen.Add(e.Id)) continue;
            next.Add(e.Id);
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

    /// <summary>
    /// A click on body line <paramref name="line"/>. An enabled entry: plain toggles it and makes it the anchor; with
    /// <paramref name="range"/> (Shift) and an anchor still on a line, every enabled entry line from the anchor's to this
    /// one takes this entry's new state (the anchor stays). A disabled entry or a heading changes nothing. True when the
    /// checked set changed.
    /// </summary>
    internal bool Click(int line, bool range)
    {
        var lines = Lines();
        if (line < 0 || line >= lines.Count || lines[line].Kind != UiChecklistLineKind.Entry) return false;
        var entry = lines[line].Entry!;
        if (entry.Disabled) return false;
        int from = range && _anchor != null ? LineOf(_anchor) : -1;
        if (from < 0)
        {
            _anchor = entry.Id;
            return Set(entry.Id, !IsChecked(entry.Id));
        }

        bool on = !IsChecked(entry.Id);
        bool changed = false;
        int a = Math.Min(from, line), b = Math.Max(from, line);
        for (int i = a; i <= b; i++)
        {
            if (lines[i].Kind != UiChecklistLineKind.Entry) continue;
            var e = lines[i].Entry!;
            if (e.Disabled) continue;
            changed |= Set(e.Id, on);
        }

        return changed;
    }

    /// <summary>Check every shown, enabled entry not checked yet. Returns how many were added.</summary>
    internal int SelectShown()
    {
        int added = 0;
        foreach (var e in Shown)
            if (!e.Disabled && Set(e.Id, true)) added++;
        return added;
    }

    /// <summary>Whether <see cref="SelectShown"/> has work to do.</summary>
    internal bool CanSelectShown()
    {
        foreach (var e in Shown)
            if (!e.Disabled && !_selectedSet.Contains(e.Id)) return true;
        return false;
    }

    /// <summary>Uncheck everything. Returns how many were unchecked.</summary>
    internal int Clear()
    {
        int n = _selected.Count;
        _selected.Clear();
        _selectedSet.Clear();
        return n;
    }

    /// <summary>How many checked entries <see cref="Shown"/> does not list (filtered, searched away or folded).</summary>
    internal int CheckedNotShown()
    {
        if (_selected.Count == 0) return 0;
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in Shown) shown.Add(e.Id);
        int n = 0;
        foreach (var id in _selected) if (!shown.Contains(id)) n++;
        return n;
    }

    // ---- Words ----------------------------------------------------------------------------------------

    /// <summary>
    /// The status line: what is shown (<c>300 entries</c>, <c>120 of 300 shown</c>, or why nothing is), then the checked
    /// count and how many of those are not shown (<c>· 3 selected (1 not shown)</c>).
    /// </summary>
    internal string Status()
    {
        int total = _entries.Count;
        if (total == 0) return string.IsNullOrWhiteSpace(Options.EmptyText) ? "Nothing to choose from." : Options.EmptyText;
        int matched = MatchedCount;
        string shown;
        if (matched == 0)
            shown = _queryWords.Length > 0 ? "Nothing matches “" + _query.Trim() + "”" : "Nothing under " + FilterOption.Label;
        else if (matched == total) shown = total == 1 ? "1 entry" : Count(total) + " entries";
        else shown = Count(matched) + " of " + Count(total) + " shown";
        int sel = _selected.Count;
        string selected = sel == 0 ? "none selected" : Count(sel) + " selected";
        int hidden = CheckedNotShown();
        if (hidden > 0) selected += " (" + Count(hidden) + " not shown)";
        return shown + " · " + selected;
    }

    private static string Count(int n) => n.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>The Confirm caption for <paramref name="count"/> checked entries (a broken format reads <c>format (n)</c>).</summary>
    internal string ConfirmCaption(int count) => ConfirmCaption(Options.ConfirmFormat, count);

    internal static string ConfirmCaption(string? format, int count)
    {
        string f = string.IsNullOrWhiteSpace(format) ? "Confirm ({0})" : format!;
        try { return string.Format(CultureInfo.InvariantCulture, f, count); }
        catch (FormatException) { return f + " (" + count.ToString(CultureInfo.InvariantCulture) + ")"; }
    }
}
