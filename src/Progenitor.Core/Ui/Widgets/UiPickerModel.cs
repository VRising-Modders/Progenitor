using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>What one drawn picker line is.</summary>
internal enum UiPickerLineKind
{
    Section = 0,
    Entry = 1,
}

/// <summary>One drawn picker line: a section heading or an entry.</summary>
internal readonly struct UiPickerLine
{
    internal UiPickerLine(UiPickerLineKind kind, string text, UiPickerEntry? entry)
    {
        Kind = kind;
        Text = text ?? "";
        Entry = entry;
    }

    internal UiPickerLineKind Kind { get; }

    internal string Text { get; }

    internal UiPickerEntry? Entry { get; }

    public override string ToString() => Kind == UiPickerLineKind.Section ? "# " + Text : Text;
}

/// <summary>
/// P7: the game-free picker: matching, ranking, sections, the autofill suggestion, the windowed lines and the selection
/// (<c>UiPickerTests</c>).
///
/// <para><b>Matching.</b> Case- and accent-insensitive on the label and the keywords. Rank, best first: the label equals
/// the query; the label starts with it; a word of the label starts with it; every query word starts some label word; the
/// label contains it; a keyword matches. An entry that matches nothing is not listed. An empty query lists everything in
/// the caller's order.</para>
///
/// <para><b>Order.</b> Sections keep the order of their first entry in the caller's list; within a section, rank then the
/// caller's order. Grayed entries are listed (so the player learns why) but after the pickable ones of their rank.</para>
/// </summary>
internal sealed class UiPickerModel
{
    internal const int MinRows = 4;
    internal const int MaxRows = 30;

    private readonly List<UiPickerEntry> _entries = new();

    /// <summary>Each entry's folded label, label words and keywords, made once in <see cref="SetEntries"/>, not per keystroke.</summary>
    private readonly List<FoldedEntry> _folded = new();
    private readonly Dictionary<UiPickerEntry, FoldedEntry> _foldedBy = new(ReferenceEqualityComparer.Instance);

    /// <summary>An entry's search text, folded once.</summary>
    internal sealed class FoldedEntry
    {
        internal FoldedEntry(UiPickerEntry entry)
        {
            Label = Fold(entry.Label);
            Words = SplitWords(Label);
            var keys = new List<string>();
            foreach (var k in entry.Keywords ?? Array.Empty<string>())
            {
                string f = Fold(k);
                if (f.Length > 0) keys.Add(f);
            }

            Keywords = keys.ToArray();
        }

        internal string Label { get; }

        internal string[] Words { get; }

        internal string[] Keywords { get; }
    }
    private readonly List<string> _selected = new();
    private List<UiPickerEntry> _matches = new();
    private string _query = "";

    internal UiPickerModel(IEnumerable<UiPickerEntry>? entries, bool multi, int rows, IEnumerable<string>? selected)
    {
        Multi = multi;
        Rows = RowsFor(rows);
        SetEntries(entries);
        if (selected != null)
            foreach (var id in selected) Select(id, true);
    }

    internal static int RowsFor(int requested) => requested < MinRows ? MinRows : requested > MaxRows ? MaxRows : requested;

    internal bool Multi { get; }

    /// <summary>Lines drawn at once (sections included).</summary>
    internal int Rows { get; }

    internal IReadOnlyList<UiPickerEntry> Entries => _entries;

    internal IReadOnlyList<UiPickerEntry> Matches => _matches;

    internal IReadOnlyList<string> Selected => _selected;

    internal string Query
    {
        get => _query;
        set
        {
            _query = value ?? "";
            Refilter();
        }
    }

    internal void SetEntries(IEnumerable<UiPickerEntry>? entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var next = new List<UiPickerEntry>();
        if (entries != null)
        {
            foreach (var e in entries)
            {
                if (e == null) continue;
                if (!seen.Add(e.Id)) throw new ArgumentException("Two picker entries share the id '" + e.Id + "'.", nameof(entries));
                next.Add(e);
            }
        }

        _entries.Clear();
        _entries.AddRange(next);
        _folded.Clear();
        _foldedBy.Clear();
        foreach (var e in next)
        {
            var f = new FoldedEntry(e);
            _folded.Add(f);
            _foldedBy[e] = f;
        }
        _selected.RemoveAll(id => !seen.Contains(id) || Find(id)?.Disabled == true);
        Refilter();
    }

    internal UiPickerEntry? Find(string? id)
    {
        if (id == null) return null;
        foreach (var e in _entries)
            if (string.Equals(e.Id, id, StringComparison.Ordinal)) return e;
        return null;
    }

    internal bool IsSelected(string? id) => id != null && _selected.Contains(id);

    /// <summary>Select or unselect. Single mode: selecting replaces the selection. False when nothing changed.</summary>
    internal bool Select(string? id, bool selected)
    {
        var entry = Find(id);
        if (entry == null) return false;
        if (!selected) return _selected.Remove(entry.Id);
        if (entry.Disabled || _selected.Contains(entry.Id)) return false;
        if (!Multi) _selected.Clear();
        _selected.Add(entry.Id);
        return true;
    }

    /// <summary>A click on an entry: single mode selects it, multi mode flips it. False when nothing changed.</summary>
    internal bool Click(string? id)
    {
        var entry = Find(id);
        if (entry == null || entry.Disabled) return false;
        if (Multi && _selected.Contains(entry.Id)) return Select(entry.Id, false);
        return Select(entry.Id, true);
    }

    /// <summary>
    /// P7b, multi mode: select every pickable match of the query that is not selected yet, in the order listed. Returns how
    /// many were added (0 in single mode).
    /// </summary>
    internal int SelectMatches()
    {
        if (!Multi) return 0;
        int added = 0;
        foreach (var e in _matches)
        {
            if (e.Disabled || _selected.Contains(e.Id)) continue;
            _selected.Add(e.Id);
            added++;
        }

        return added;
    }

    /// <summary>P7b, multi mode: some pickable match is not selected (the Select all link has work to do).</summary>
    internal bool CanSelectMatches()
    {
        if (!Multi) return false;
        foreach (var e in _matches)
            if (!e.Disabled && !_selected.Contains(e.Id)) return true;
        return false;
    }

    /// <summary>P7b, multi mode: the selected count for the status line (<c>3 selected.</c>), empty with none or in single mode.</summary>
    internal string SelectionStatus() => !Multi || _selected.Count == 0 ? "" : _selected.Count + " selected.";

    internal bool ClearSelection()
    {
        if (_selected.Count == 0) return false;
        _selected.Clear();
        return true;
    }

    internal List<UiPickerEntry> SelectedEntries()
    {
        var list = new List<UiPickerEntry>(_selected.Count);
        foreach (var id in _selected)
        {
            var e = Find(id);
            if (e != null) list.Add(e);
        }

        return list;
    }

    // ---- Matching ----------------------------------------------------------------------------

    /// <summary>Lower case with diacritics removed and runs of white space folded to one space.</summary>
    internal static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string decomposed = text!.Trim().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        bool space = false;
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(c))
            {
                if (!space) sb.Append(' ');
                space = true;
                continue;
            }

            space = false;
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>How well <paramref name="entry"/> matches the folded query (lower is better), or -1 for no match.</summary>
    internal static int Rank(UiPickerEntry entry, string foldedQuery) => Rank(new FoldedEntry(entry), foldedQuery, SplitWords(foldedQuery));

    /// <summary><see cref="Rank(UiPickerEntry, string)"/> over text folded once (the per-keystroke path).</summary>
    internal static int Rank(FoldedEntry entry, string foldedQuery, string[] queryWords)
    {
        if (foldedQuery.Length == 0) return 0;
        string label = entry.Label;
        if (label == foldedQuery) return 0;
        if (label.StartsWith(foldedQuery, StringComparison.Ordinal)) return 1;
        var words = entry.Words;
        foreach (var w in words)
            if (w.StartsWith(foldedQuery, StringComparison.Ordinal)) return 2;
        if (queryWords.Length > 1)
        {
            bool all = true;
            foreach (var q in queryWords)
            {
                bool hit = false;
                foreach (var w in words)
                    if (w.StartsWith(q, StringComparison.Ordinal)) { hit = true; break; }
                if (!hit) { all = false; break; }
            }

            if (all) return 3;
        }

        if (label.Contains(foldedQuery, StringComparison.Ordinal)) return 4;
        foreach (var fk in entry.Keywords)
            if (fk.Contains(foldedQuery, StringComparison.Ordinal)) return 5;

        return -1;
    }

    private static string[] SplitWords(string folded) =>
        folded.Split(new[] { ' ', '-', '_', '(', ')', '/', ',', '.' }, StringSplitOptions.RemoveEmptyEntries);

    private void Refilter()
    {
        string q = Fold(_query);
        var queryWords = SplitWords(q);
        var sectionOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        var ranked = new List<(UiPickerEntry Entry, int Section, int Rank, int Index)>();
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            string section = e.Section ?? "";
            if (!sectionOrder.TryGetValue(section, out int s))
            {
                s = sectionOrder.Count;
                sectionOrder[section] = s;
            }

            int r = Rank(_folded[i], q, queryWords);
            if (r < 0) continue;
            ranked.Add((e, s, r, i));
        }

        ranked.Sort((a, b) =>
        {
            int c = a.Section.CompareTo(b.Section);
            if (c != 0) return c;
            c = a.Rank.CompareTo(b.Rank);
            if (c != 0) return c;
            c = a.Entry.Disabled.CompareTo(b.Entry.Disabled);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });
        var list = new List<UiPickerEntry>(ranked.Count);
        foreach (var r in ranked) list.Add(r.Entry);
        _matches = list;
    }

    /// <summary>
    /// The autofill completion: the label of the best-ranked pickable match whose folded label starts with the folded
    /// query, when it is longer than the query; else empty. Nothing for an empty query.
    /// </summary>
    internal string Suggestion
    {
        get
        {
            string q = Fold(_query);
            if (q.Length == 0) return "";
            UiPickerEntry? best = null;
            int bestRank = int.MaxValue;
            foreach (var e in _matches)
            {
                if (e.Disabled) continue;
                var folded = _foldedBy.TryGetValue(e, out var cached) ? cached : new FoldedEntry(e);
                string label = folded.Label;
                if (!label.StartsWith(q, StringComparison.Ordinal) || label.Length <= q.Length) continue;
                int r = Rank(folded, q, SplitWords(q));
                if (r < bestRank)
                {
                    best = e;
                    bestRank = r;
                }
            }

            return best?.Label ?? "";
        }
    }

    /// <summary>
    /// The lines drawn now: a section heading before the first match of each named section, then the matches, cut to
    /// <see cref="Rows"/> lines. A heading is never the last line drawn (it moves out with its first entry).
    /// <paramref name="hidden"/> is how many matches did not fit.
    /// </summary>
    internal List<UiPickerLine> Lines(out int hidden)
    {
        var lines = new List<UiPickerLine>(Rows);
        string? section = null;
        int shown = 0;
        foreach (var e in _matches)
        {
            string s = e.Section ?? "";
            bool heading = s.Length > 0 && !string.Equals(s, section, StringComparison.Ordinal);
            int need = heading ? 2 : 1;
            if (lines.Count + need > Rows) break;
            if (heading) lines.Add(new UiPickerLine(UiPickerLineKind.Section, s, null));
            section = s;
            lines.Add(new UiPickerLine(UiPickerLineKind.Entry, e.Label, e));
            shown++;
        }

        hidden = _matches.Count - shown;
        return lines;
    }

    /// <summary>The status line under the search field: the match count, or why nothing is listed.</summary>
    internal string Status(int hidden)
    {
        if (_entries.Count == 0) return "Nothing to pick from.";
        if (_matches.Count == 0) return "No match for \"" + _query.Trim() + "\".";
        if (Fold(_query).Length == 0) return _matches.Count == 1 ? "1 entry." : _matches.Count + " entries.";
        string count = _matches.Count == 1 ? "1 match." : _matches.Count + " matches.";
        return hidden > 0 ? count + " Keep typing to narrow them down." : count;
    }
}
