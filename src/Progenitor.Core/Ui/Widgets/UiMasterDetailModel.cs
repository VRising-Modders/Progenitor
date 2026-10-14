using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>What one master line is.</summary>
internal enum UiMasterLineKind
{
    Entry = 0,

    /// <summary>The new-entry action line (<see cref="UiMasterDetailOptions.NewCaption"/>).</summary>
    New = 1,

    /// <summary>The empty state, or "No match for …".</summary>
    Empty = 2,
}

/// <summary>One drawn master line.</summary>
internal readonly struct UiMasterLine
{
    internal UiMasterLine(UiMasterLineKind kind, UiMasterEntry? entry, string text)
    {
        Kind = kind;
        Entry = entry;
        Text = text ?? "";
    }

    internal UiMasterLineKind Kind { get; }

    internal UiMasterEntry? Entry { get; }

    internal string Text { get; }

    public override string ToString() => Kind == UiMasterLineKind.Entry ? Entry!.Title : "[" + Kind + "] " + Text;
}

/// <summary>What one drawn detail line is.</summary>
internal enum UiDetailSlotKind
{
    /// <summary>A caller line other than the member list (title, text, heading, link, actions, blank).</summary>
    Line = 0,

    /// <summary>One member of the member list.</summary>
    Member = 1,

    /// <summary>A line of an expanded member's drawer.</summary>
    Drawer = 2,

    /// <summary>A muted line: the member list's empty text, or the detail's empty text.</summary>
    Empty = 3,
}

/// <summary>One drawn detail line.</summary>
internal readonly struct UiDetailSlot
{
    internal UiDetailSlot(UiDetailSlotKind kind, UiDetailLine? line, UiListItem? member, int memberIndex, string text)
    {
        Kind = kind;
        Line = line;
        Member = member;
        MemberIndex = memberIndex;
        Text = text ?? "";
    }

    internal UiDetailSlotKind Kind { get; }

    /// <summary>The caller's line (also the members line for member, drawer and member-empty slots).</summary>
    internal UiDetailLine? Line { get; }

    internal UiListItem? Member { get; }

    /// <summary>A member slot's index in the drawn order (the drag preview while a drag runs); -1 otherwise.</summary>
    internal int MemberIndex { get; }

    internal string Text { get; }

    public override string ToString() => Kind switch
    {
        UiDetailSlotKind.Member => "member " + Member!.Id,
        UiDetailSlotKind.Drawer => "  " + Text,
        UiDetailSlotKind.Empty => "(" + Text + ")",
        _ => Line!.ToString(),
    };
}

/// <summary>One tab's state: its entries, selection, search, detail, member list and both scroll positions.</summary>
internal sealed class UiMasterDetailPage
{
    internal readonly List<UiMasterEntry> Entries = new();
    internal readonly Dictionary<UiMasterEntry, UiPickerEntry> Proxies = new(ReferenceEqualityComparer.Instance);
    internal readonly List<UiDetailLine> Detail = new();
    internal readonly UiOrderedListModel Members = new(null, UiOrderedListModel.MaxCapacity);
    internal UiDetailLine? MembersLine;
    internal string? SelectedId;
    internal string? DetailFor;
    internal string Query = "";
    internal int MasterTop;
    internal int DetailTop;
    internal string NewCaption = "";
    internal string EmptyText = "";
    internal string DetailEmptyText = "";
}

/// <summary>
/// P7b: the game-free master-detail state (<c>UiMasterDetailTests</c>): per tab the entries and their search filter, the
/// selection, the detail lines flattened into drawn lines (the member list one line per member, an open drawer one more
/// per line of its text), and the two scroll windows. The handle (<see cref="UiMasterDetailHandle"/>) draws it.
///
/// <para><b>Rows.</b> The widget makes <see cref="Rows"/> rows. The master list draws in <see cref="MasterSlots"/> of them
/// (the first row is the search field when there is one), the detail in all of them. Each half scrolls by itself:
/// <see cref="ScrollMaster"/> and <see cref="ScrollDetail"/> clamp to the lines there are.</para>
///
/// <para><b>Search.</b> The picker's matching (<see cref="UiPickerModel.Rank(UiPickerEntry, string)"/>: case- and
/// accent-insensitive, label words, keywords) on the title, with the note and the keywords as keywords. The master list
/// keeps the caller's order while filtering, so entries never jump around as the player types.</para>
/// </summary>
internal sealed class UiMasterDetailModel
{
    /// <summary>The most lines an open member drawer takes.</summary>
    internal const int DrawerLines = 3;

    private readonly UiMasterDetailPage[] _pages;

    internal UiMasterDetailModel(int tabs, int rows, bool search, UiMasterDetailOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        _pages = new UiMasterDetailPage[Math.Max(1, tabs)];
        for (int i = 0; i < _pages.Length; i++)
            _pages[i] = new UiMasterDetailPage
            {
                NewCaption = options.NewCaption ?? "",
                EmptyText = options.EmptyText ?? "",
                DetailEmptyText = options.DetailEmptyText ?? "",
            };
        Rows = UiMasterDetailLayout.RowsFor(rows);
        Search = search;
    }

    internal int Rows { get; }

    internal bool Search { get; }

    internal int TabCount => _pages.Length;

    /// <summary>The rows the master list draws entries in (all but the search row).</summary>
    internal int MasterSlots => Search ? Rows - 1 : Rows;

    /// <summary>The rows the detail draws in.</summary>
    internal int DetailSlots => Rows;

    internal int Tab { get; private set; }

    internal UiMasterDetailPage Page => _pages[Tab];

    internal UiMasterDetailPage PageAt(int tab) => _pages[ClampTab(tab)];

    internal int ClampTab(int tab) => tab < 0 ? 0 : tab >= _pages.Length ? _pages.Length - 1 : tab;

    /// <summary>Show tab <paramref name="tab"/>. False when it was shown already.</summary>
    internal bool SetTab(int tab)
    {
        int t = ClampTab(tab);
        if (t == Tab) return false;
        Page.Members.CancelDrag();
        Tab = t;
        return true;
    }

    // ---- entries and selection -----------------------------------------------------------------

    /// <summary>Replace a tab's entries. Duplicate ids are refused (the whole call throws, nothing changes).</summary>
    internal void SetEntries(int tab, IEnumerable<UiMasterEntry>? entries)
    {
        var page = PageAt(tab);
        var next = new List<UiMasterEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (entries != null)
        {
            foreach (var e in entries)
            {
                if (e == null) continue;
                if (!seen.Add(e.Id)) throw new ArgumentException("Two master entries share the id '" + e.Id + "'.", nameof(entries));
                next.Add(e);
            }
        }

        page.Entries.Clear();
        page.Entries.AddRange(next);
        page.Proxies.Clear();
        foreach (var e in next) page.Proxies[e] = ProxyOf(e);
        if (page.SelectedId != null && !seen.Contains(page.SelectedId)) page.SelectedId = null;
        ClampMaster(page);
    }

    private static UiPickerEntry ProxyOf(UiMasterEntry e)
    {
        var keys = new List<string>();
        if (!string.IsNullOrWhiteSpace(e.Note)) keys.Add(e.Note);
        foreach (var k in e.Keywords ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(k)) keys.Add(k);
        return new UiPickerEntry(e.Id, e.Title) { Keywords = keys };
    }

    internal UiMasterEntry? Find(UiMasterDetailPage page, string? id)
    {
        if (id == null) return null;
        foreach (var e in page.Entries)
            if (string.Equals(e.Id, id, StringComparison.Ordinal)) return e;
        return null;
    }

    /// <summary>Select <paramref name="id"/> on the current tab (null: none). False for an unknown id or no change.</summary>
    internal bool Select(string? id)
    {
        var page = Page;
        if (id != null && Find(page, id) == null) return false;
        if (string.Equals(page.SelectedId, id, StringComparison.Ordinal)) return false;
        page.SelectedId = id;
        RevealSelected(page);
        return true;
    }

    /// <summary>Set the current tab's query. False when it did not change.</summary>
    internal bool SetQuery(string? query)
    {
        var page = Page;
        string q = query ?? "";
        if (string.Equals(page.Query, q, StringComparison.Ordinal)) return false;
        page.Query = q;
        page.MasterTop = 0;
        return true;
    }

    /// <summary>The page's entries matching its query, in the caller's order.</summary>
    internal List<UiMasterEntry> Matches(UiMasterDetailPage page)
    {
        string q = UiPickerModel.Fold(page.Query);
        var list = new List<UiMasterEntry>(page.Entries.Count);
        foreach (var e in page.Entries)
        {
            if (q.Length > 0)
            {
                var proxy = page.Proxies.TryGetValue(e, out var p) ? p : ProxyOf(e);
                if (UiPickerModel.Rank(proxy, q) < 0) continue;
            }

            list.Add(e);
        }

        return list;
    }

    /// <summary>Every master line of the page: the matches, the empty state, then the new-entry line.</summary>
    internal List<UiMasterLine> MasterLines(UiMasterDetailPage page)
    {
        var matches = Matches(page);
        var lines = new List<UiMasterLine>(matches.Count + 2);
        foreach (var e in matches) lines.Add(new UiMasterLine(UiMasterLineKind.Entry, e, e.Title));
        if (matches.Count == 0)
        {
            string empty = page.Entries.Count == 0 ? page.EmptyText : "No match for \"" + page.Query.Trim() + "\".";
            if (empty.Length > 0) lines.Add(new UiMasterLine(UiMasterLineKind.Empty, null, empty));
        }

        if (page.NewCaption.Length > 0) lines.Add(new UiMasterLine(UiMasterLineKind.New, null, page.NewCaption));
        return lines;
    }

    /// <summary>The master lines drawn now: the window of <see cref="MasterSlots"/> lines from <see cref="UiMasterDetailPage.MasterTop"/>.</summary>
    internal List<UiMasterLine> MasterWindow(UiMasterDetailPage page)
    {
        ClampMaster(page);
        var all = MasterLines(page);
        var window = new List<UiMasterLine>(MasterSlots);
        for (int i = page.MasterTop; i < all.Count && window.Count < MasterSlots; i++) window.Add(all[i]);
        return window;
    }

    /// <summary>The search row's status: the match count, or the visible range while the list scrolls.</summary>
    internal string MasterStatus(UiMasterDetailPage page)
    {
        int total = page.Entries.Count;
        if (total == 0) return "";
        int matches = Matches(page).Count;
        int lines = MasterLines(page).Count;
        if (lines > MasterSlots)
        {
            int first = Math.Min(page.MasterTop + 1, matches);
            int last = Math.Min(page.MasterTop + MasterSlots, matches);
            return first + "–" + last + " of " + matches;
        }

        return UiPickerModel.Fold(page.Query).Length > 0 ? matches + " of " + total : total.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---- detail ---------------------------------------------------------------------------------

    /// <summary>
    /// Replace a tab's detail. One member list at most (a second throws). The member list keeps open drawers for ids that
    /// remain. The detail scrolls back to its top when it now describes another entry than the last call did.
    /// </summary>
    internal void SetDetail(int tab, IEnumerable<UiDetailLine>? lines)
    {
        var page = PageAt(tab);
        var next = new List<UiDetailLine>();
        UiDetailLine? members = null;
        if (lines != null)
        {
            foreach (var l in lines)
            {
                if (l == null) continue;
                if (l.Kind == UiDetailLineKind.Members)
                {
                    if (members != null) throw new ArgumentException("A detail has at most one member list.", nameof(lines));
                    members = l;
                }

                next.Add(l);
            }
        }

        page.Members.SetItems(members?.Members); // throws on duplicate ids before anything changes below
        page.Detail.Clear();
        page.Detail.AddRange(next);
        page.MembersLine = members;
        if (!string.Equals(page.DetailFor, page.SelectedId, StringComparison.Ordinal)) page.DetailTop = 0;
        page.DetailFor = page.SelectedId;
        ClampDetail(page);
    }

    /// <summary>Every detail line of the page, flattened.</summary>
    internal List<UiDetailSlot> DetailLines(UiMasterDetailPage page)
    {
        var slots = new List<UiDetailSlot>(page.Detail.Count + page.Members.Count);
        if (page.Detail.Count == 0)
        {
            foreach (var text in (page.DetailEmptyText ?? "").Replace("\r", "").Split('\n'))
                if (text.Length > 0) slots.Add(new UiDetailSlot(UiDetailSlotKind.Empty, null, null, -1, text));
            return slots;
        }

        foreach (var line in page.Detail)
        {
            if (line.Kind != UiDetailLineKind.Members)
            {
                slots.Add(new UiDetailSlot(UiDetailSlotKind.Line, line, null, -1, line.Text));
                continue;
            }

            var members = page.Members;
            if (members.Count == 0)
            {
                if (line.EmptyText.Length > 0) slots.Add(new UiDetailSlot(UiDetailSlotKind.Empty, line, null, -1, line.EmptyText));
                continue;
            }

            var display = members.Display();
            for (int i = 0; i < display.Count; i++)
            {
                var item = display[i];
                slots.Add(new UiDetailSlot(UiDetailSlotKind.Member, line, item, i, item.Title));
                if (members.Dragging || !members.IsExpanded(item.Id)) continue;
                int n = 0;
                foreach (var text in item.Detail.Replace("\r", "").Split('\n'))
                {
                    if (n >= DrawerLines) break;
                    if (text.Trim().Length == 0) continue;
                    slots.Add(new UiDetailSlot(UiDetailSlotKind.Drawer, line, item, i, text.Trim()));
                    n++;
                }
            }
        }

        return slots;
    }

    /// <summary>The detail lines drawn now.</summary>
    internal List<UiDetailSlot> DetailWindow(UiMasterDetailPage page)
    {
        ClampDetail(page);
        var all = DetailLines(page);
        var window = new List<UiDetailSlot>(DetailSlots);
        for (int i = page.DetailTop; i < all.Count && window.Count < DetailSlots; i++) window.Add(all[i]);
        return window;
    }

    // ---- scrolling ------------------------------------------------------------------------------

    internal int MasterMaxTop(UiMasterDetailPage page) => Math.Max(0, MasterLines(page).Count - MasterSlots);

    internal int DetailMaxTop(UiMasterDetailPage page) => Math.Max(0, DetailLines(page).Count - DetailSlots);

    /// <summary>Move the master window by <paramref name="lines"/> (clamped). False when it did not move.</summary>
    internal bool ScrollMaster(int lines)
    {
        var page = Page;
        int next = Clamp(page.MasterTop + lines, 0, MasterMaxTop(page));
        if (next == page.MasterTop) return false;
        page.MasterTop = next;
        return true;
    }

    /// <summary>Move the detail window by <paramref name="lines"/> (clamped). False when it did not move.</summary>
    internal bool ScrollDetail(int lines)
    {
        var page = Page;
        int next = Clamp(page.DetailTop + lines, 0, DetailMaxTop(page));
        if (next == page.DetailTop) return false;
        page.DetailTop = next;
        return true;
    }

    /// <summary>Put the detail window at <paramref name="position"/> (0 the top, 1 the bottom). False when it did not move.</summary>
    internal bool ScrollDetailTo(float position)
    {
        var page = Page;
        int max = DetailMaxTop(page);
        if (float.IsNaN(position)) return false;
        int next = Clamp((int)Math.Round(Math.Max(0f, Math.Min(1f, position)) * max), 0, max);
        if (next == page.DetailTop) return false;
        page.DetailTop = next;
        return true;
    }

    /// <summary>The detail window as a scroll bar reads it: the visible share and the position.</summary>
    internal (float Size, float Position) DetailBar(UiMasterDetailPage page)
    {
        int total = DetailLines(page).Count;
        if (total <= DetailSlots) return (1f, 0f);
        int max = total - DetailSlots;
        return ((float)DetailSlots / total, max == 0 ? 0f : (float)page.DetailTop / max);
    }

    /// <summary>Scroll the master list so the selected entry is drawn (when it matches the query).</summary>
    internal void RevealSelected(UiMasterDetailPage page)
    {
        if (page.SelectedId == null) return;
        var lines = MasterLines(page);
        int at = -1;
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Entry != null && string.Equals(lines[i].Entry!.Id, page.SelectedId, StringComparison.Ordinal)) { at = i; break; }
        if (at < 0) return;
        if (at < page.MasterTop) page.MasterTop = at;
        else if (at >= page.MasterTop + MasterSlots) page.MasterTop = at - MasterSlots + 1;
        ClampMaster(page);
    }

    private void ClampMaster(UiMasterDetailPage page) => page.MasterTop = Clamp(page.MasterTop, 0, MasterMaxTop(page));

    private void ClampDetail(UiMasterDetailPage page) => page.DetailTop = Clamp(page.DetailTop, 0, DetailMaxTop(page));

    private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
}
