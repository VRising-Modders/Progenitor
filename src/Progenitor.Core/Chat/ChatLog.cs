using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Progenitor.Chat;

/// <summary>
/// CHAT5: a chat line's kind. The first eight follow the game's <c>ServerChatMessageType</c> numbers (Global 1, Region
/// 2, Team 3, WhisperFrom 4, WhisperTo 5, System 6, Lore 7, Local 8); <see cref="Session"/> is Progenitor's own
/// divider line ("Joined … · 21:04").
/// </summary>
internal enum ChatKind
{
    Unknown = 0,
    Global = 1,
    Region = 2,
    Team = 3,
    WhisperFrom = 4,
    WhisperTo = 5,
    System = 6,
    Lore = 7,
    Local = 8,
    Session = 100,
}

/// <summary>CHAT5: one line of Progenitor's chat log. Game-free.</summary>
internal sealed class ChatRecord
{
    internal long Seq { get; set; }

    /// <summary>When the line arrived (UTC).</summary>
    internal DateTime TimeUtc { get; set; }

    internal ChatKind Kind { get; set; }

    /// <summary>Sent by this player.</summary>
    internal bool FromSelf { get; set; }

    /// <summary>The speaking character's name, or empty (system lines, markers).</summary>
    internal string User { get; set; } = "";

    /// <summary>The message as typed (plain text).</summary>
    internal string Text { get; set; } = "";

    /// <summary>
    /// The game's formatted line (rich text with the channel tag, colored name and the message inside
    /// <c>&lt;noparse&gt;</c>), or empty when the line was not drawn by the game (restored history keeps it; markers
    /// have none).
    /// </summary>
    internal string Full { get; set; } = "";

    /// <summary>How many times the same line arrived in a row (repeat collapse), at least 1.</summary>
    internal int Repeat { get; set; } = 1;

    /// <summary>The line mentions this player (their name, a watched word, or <c>@all</c> from an admin).</summary>
    internal bool Mention { get; set; }

    /// <summary>Restored from an earlier session (drawn dimmer; never mentions or sounds again).</summary>
    internal bool Restored { get; set; }

    /// <summary>A note for this session only ("Copied the line"): drawn, never saved.</summary>
    internal bool Transient { get; set; }

    /// <summary>
    /// Who wrote the line, from the game's event (a System or lore line with no sender is the server's), never from its
    /// text. Decides whether its mod links are trusted (<see cref="ChatLinks"/>). Player by default.
    /// </summary>
    internal ChatLinkSource Source { get; set; }

    /// <summary>Raised each time the line changes in place (a repeat, a joined part); a drawn copy older than it is redrawn.</summary>
    internal int Rev { get; set; }

    // ---- the drawn text, cached (ChatView): a line is formatted once, not on every redraw -----------------

    /// <summary>The formatted line last drawn, for <see cref="RenderKey"/> and <see cref="RenderRev"/>; null: not drawn yet.</summary>
    internal string? RenderText { get; set; }

    /// <summary>The links of <see cref="RenderText"/>.</summary>
    internal List<ChatSpan>? RenderSpans { get; set; }

    /// <summary>The display settings <see cref="RenderText"/> was formatted with.</summary>
    internal int RenderKey { get; set; }

    /// <summary>The <see cref="Rev"/> <see cref="RenderText"/> was formatted at.</summary>
    internal int RenderRev { get; set; } = -1;

    /// <summary>Whether <see cref="RenderText"/> still fits this line and these settings.</summary>
    internal bool RenderFresh(int key) => RenderText != null && RenderRev == Rev && RenderKey == key;

    /// <summary>
    /// The provenance rule: a System or lore line whose sender network id is empty is the server's (or this client's game);
    /// any line with a sender, any other channel, is a player's. The server fills the sender in from the connection, and
    /// forwards a player's System line only from an admin, with the admin as the sender.
    /// </summary>
    internal static ChatLinkSource SourceOf(ChatKind kind, bool hasSender) =>
        !hasSender && (kind == ChatKind.System || kind == ChatKind.Lore) ? ChatLinkSource.Server : ChatLinkSource.Player;
}

/// <summary>
/// CHAT5: Progenitor's chat log for one server: what the chat view draws, filtered per tab. Game-free.
///
/// <para><b>Repeat collapse.</b> The same line (same kind, speaker and text) arriving again within
/// <see cref="CollapseWindowSeconds"/> of the last one raises its count instead of adding a line ("×3").</para>
///
/// <para><b>Capacity.</b> The oldest lines go first once <see cref="Capacity"/> is reached (a ring, so dropping one is
/// constant time); a lower capacity set later drops the oldest at once, a higher one keeps more from then on.</para>
///
/// <para><b>Tab filters.</b> Each tab asked for keeps its own list of the lines it shows, kept up to date as lines
/// arrive, repeat, join or drop: a new line is checked once per kept tab, never the whole log again. A tab is
/// scanned in full only the first time it is asked for, after its channels were edited, and after a restore or
/// clear. <see cref="FilterChecks"/> counts the checks (the performance tests hold it to one per tab per line).</para>
/// </summary>
internal sealed class ChatLog
{
    /// <summary>The default of <c>[Chat] HistoryLines</c> since 2026-10-17 (it was 500 before; see <see cref="ChatHistorySetting"/>).</summary>
    internal const int DefaultCapacity = 5000;
    internal const int MinCapacity = 50;
    internal const int MaxCapacity = 5000;
    internal const double CollapseWindowSeconds = 60;

    /// <summary>Tab filters kept at once (the tab row's six, plus room for edited copies); the least recently used goes.</summary>
    internal const int MaxFilters = ChatTab.MaxTabs + 2;

    private readonly ChatRing<ChatRecord> _records = new();
    private readonly List<TabFilter> _filters = new();
    private long _seq;
    private int _capacity;
    private long _useClock;

    private sealed class TabFilter
    {
        internal ChatTab Tab = null!;
        internal ChatScope Scope;
        internal readonly ChatRing<ChatRecord> Matches = new();
        internal long LastUsed;
    }

    internal ChatLog(int capacity = DefaultCapacity) => _capacity = ClampCapacity(capacity);

    /// <summary>Lines kept; setting it lower drops the oldest lines at once.</summary>
    internal int Capacity
    {
        get => _capacity;
        set
        {
            int next = ClampCapacity(value);
            if (next == _capacity) return;
            _capacity = next;
            if (_records.Count > _capacity)
            {
                Trim();
                Version++;
            }
        }
    }

    /// <summary>Raised count of changes; the view redraws when it moves.</summary>
    internal int Version { get; private set; }

    internal IReadOnlyList<ChatRecord> Records => _records;

    /// <summary>How many times a tab's filter was asked about one line (for the performance tests).</summary>
    internal long FilterChecks { get; private set; }

    internal static int ClampCapacity(int n) => n < MinCapacity ? MinCapacity : n > MaxCapacity ? MaxCapacity : n;

    /// <summary>Add a line, or raise the last line's count when it repeats. Returns the record shown.</summary>
    internal ChatRecord Add(ChatRecord record, bool collapse)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        if (collapse && record.Kind != ChatKind.Session && _records.Count > 0)
        {
            var last = _records.Last!;
            if (!last.Restored && last.Kind == record.Kind && last.FromSelf == record.FromSelf && last.Source == record.Source
                && string.Equals(last.User, record.User, StringComparison.Ordinal)
                && string.Equals(last.Text, record.Text, StringComparison.Ordinal)
                && (record.TimeUtc - last.TimeUtc).TotalSeconds <= CollapseWindowSeconds)
            {
                last.Repeat++;
                last.TimeUtc = record.TimeUtc;
                last.Mention |= record.Mention;
                Changed(last);
                return last;
            }
        }

        record.Seq = ++_seq;
        if (record.Repeat < 1) record.Repeat = 1;
        _records.PushBack(record);
        foreach (var f in _filters)
        {
            if (Stale(f)) continue;
            FilterChecks++;
            if (f.Tab.Shows(record)) f.Matches.PushBack(record);
        }

        Trim();
        Version++;
        return record;
    }

    /// <summary>
    /// CHAT9: a long message's next part joins the last line when that line is the same speaker's, on the same channel,
    /// still says more is coming, and arrived within the join window. Returns the joined line, or null (add it as a
    /// line of its own).
    /// </summary>
    internal ChatRecord? TryJoin(ChatRecord part)
    {
        if (part == null || !ChatLongMessage.IsContinuation(part.Text) || _records.Count == 0) return null;
        var last = _records.Last!;
        if (last.Restored || last.Kind != part.Kind || last.FromSelf != part.FromSelf || last.Source != part.Source
            || !string.Equals(last.User, part.User, StringComparison.Ordinal) || !ChatLongMessage.ExpectsMore(last.Text)
            || (part.TimeUtc - last.TimeUtc).TotalSeconds > ChatLongMessage.JoinWindowSeconds) return null;
        last.Text = ChatLongMessage.Join(last.Text, part.Text);
        last.TimeUtc = part.TimeUtc;
        last.Mention |= part.Mention;
        Changed(last);
        return last;
    }

    /// <summary>
    /// The newest line changed in place (a repeat or a joined part): drawn again, and a tab that only now shows it (a
    /// mention arrived with the repeat) takes it. A line never stops showing in a tab, since its kind never changes and
    /// a mention is never taken back.
    /// </summary>
    private void Changed(ChatRecord last)
    {
        last.Rev++;
        foreach (var f in _filters)
        {
            if (Stale(f) || ReferenceEquals(f.Matches.Last, last)) continue;
            FilterChecks++;
            if (f.Tab.Shows(last)) f.Matches.PushBack(last);
        }

        Version++;
    }

    /// <summary>
    /// A tab whose channels were edited since its list was built: it is no longer kept up to date (its scope is marked
    /// unknown) and <see cref="Filtered"/> scans it again on the next read, even when the edit was undone before then.
    /// </summary>
    private static bool Stale(TabFilter f)
    {
        if (f.Scope == f.Tab.Scope) return false;
        f.Scope = Unbuilt;
        return true;
    }

    private const ChatScope Unbuilt = (ChatScope)(-1);

    /// <summary>A divider line: <c>Joined Night Garden · 21:04</c> / <c>Left the server · 23:10</c>.</summary>
    internal ChatRecord AddMarker(string text, DateTime utc) =>
        Add(new ChatRecord { Kind = ChatKind.Session, Text = text ?? "", TimeUtc = utc }, collapse: false);

    internal void Clear()
    {
        _records.Clear();
        _filters.Clear();
        Version++;
    }

    private void Trim()
    {
        while (_records.Count > _capacity)
        {
            var gone = _records.PopFront();
            foreach (var f in _filters)
                if (ReferenceEquals(f.Matches.First, gone)) f.Matches.PopFront();
        }
    }

    /// <summary>
    /// The lines <paramref name="tab"/> shows, oldest first, kept up to date by the log (read it, never keep it across
    /// frames: it is the log's own list). The first call for a tab, or after its channels changed, scans the log once.
    /// </summary>
    internal IReadOnlyList<ChatRecord> Filtered(ChatTab tab)
    {
        if (tab == null) throw new ArgumentNullException(nameof(tab));
        TabFilter? found = null;
        foreach (var f in _filters)
        {
            if (!ReferenceEquals(f.Tab, tab)) continue;
            found = f;
            break;
        }

        if (found == null)
        {
            if (_filters.Count >= MaxFilters)
            {
                int oldest = 0;
                for (int i = 1; i < _filters.Count; i++)
                    if (_filters[i].LastUsed < _filters[oldest].LastUsed) oldest = i;
                _filters.RemoveAt(oldest);
            }

            found = new TabFilter { Tab = tab, Scope = Unbuilt };
            _filters.Add(found);
        }

        if (found.Scope != tab.Scope)
        {
            found.Scope = tab.Scope;
            found.Matches.Clear();
            for (int i = 0; i < _records.Count; i++)
            {
                FilterChecks++;
                if (tab.Shows(_records[i])) found.Matches.PushBack(_records[i]);
            }
        }

        found.LastUsed = ++_useClock;
        return found.Matches;
    }

    /// <summary>The newest <paramref name="max"/> lines a tab shows, oldest first.</summary>
    internal List<ChatRecord> Visible(ChatTab tab, int max)
    {
        var all = Filtered(tab);
        int from = Math.Max(0, all.Count - Math.Max(0, max));
        var result = new List<ChatRecord>(all.Count - from);
        for (int i = from; i < all.Count; i++) result.Add(all[i]);
        return result;
    }

    /// <summary>
    /// The index of the last line in <paramref name="lines"/> (oldest first, as <see cref="Filtered"/> gives them) whose
    /// <see cref="ChatRecord.Seq"/> is at most <paramref name="seq"/>, or -1. A binary search: line numbers only grow.
    /// </summary>
    internal static int IndexAtOrBefore(IReadOnlyList<ChatRecord> lines, long seq)
    {
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (lines[mid].Seq <= seq)
            {
                found = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }

        return found;
    }

    internal static string JoinedText(string server, DateTime local) =>
        "Joined " + (string.IsNullOrWhiteSpace(server) ? "a server" : server.Trim()) + " · " + local.ToString("HH:mm", CultureInfo.InvariantCulture);

    internal static string LeftText(DateTime local) => "Left the server · " + local.ToString("HH:mm", CultureInfo.InvariantCulture);

    // ---- persistence: one JSON object per line -----------------------------------------------------------

    /// <summary>
    /// The newest <paramref name="max"/> saved lines as plain values (no transient notes), taken on the main thread so a
    /// background writer never reads a record the log is still changing (<see cref="ChatHistoryFile"/>).
    /// </summary>
    internal ChatHistoryLine[] Snapshot(int max)
    {
        int from = Math.Max(0, _records.Count - Math.Max(0, max));
        var lines = new List<ChatHistoryLine>(_records.Count - from);
        for (int i = from; i < _records.Count; i++)
        {
            var r = _records[i];
            if (!r.Transient) lines.Add(ChatHistoryLine.From(r));
        }

        return lines.ToArray();
    }

    /// <summary>The log as JSON lines (the newest <paramref name="max"/>), for the per-server history file.</summary>
    internal string Serialize(int max) => ChatHistoryFile.ToText(Snapshot(max));

    /// <summary>Lines saved by <see cref="Serialize"/>; malformed lines are skipped. Restored lines never mention.</summary>
    internal static List<ChatRecord> Deserialize(string? text, int max) =>
        string.IsNullOrEmpty(text) ? new List<ChatRecord>() : Deserialize(Encoding.UTF8.GetBytes(text), max);

    /// <summary>
    /// The history file's bytes as lines (<see cref="Deserialize(string?, int)"/>): read with a forward-only JSON reader,
    /// one line at a time, with no document tree per line.
    /// </summary>
    internal static List<ChatRecord> Deserialize(byte[]? bytes, int max)
    {
        var result = new List<ChatRecord>();
        if (bytes == null || bytes.Length == 0) return result;
        int at = 0;
        // A UTF-8 byte order mark, if an editor added one.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) at = 3;
        while (at < bytes.Length)
        {
            int end = Array.IndexOf(bytes, (byte)'\n', at);
            if (end < 0) end = bytes.Length;
            var line = new ReadOnlySpan<byte>(bytes, at, end - at);
            at = end + 1;
            try
            {
                var r = ReadLine(line);
                if (r != null) result.Add(r);
            }
            catch
            {
                // A damaged line costs that line only.
            }
        }

        if (result.Count > max) result.RemoveRange(0, result.Count - max);
        return result;
    }

    private static ChatRecord? ReadLine(ReadOnlySpan<byte> line)
    {
        var reader = new Utf8JsonReader(line);
        if (!reader.Read()) return null; // blank
        if (reader.TokenType != JsonTokenType.StartObject) return null;
        var r = new ChatRecord { Restored = true };
        bool hasTime = false, hasKind = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            char key = reader.ValueSpan.Length == 1 ? (char)reader.ValueSpan[0] : '\0';
            if (!reader.Read()) return null;
            switch (key)
            {
                case 't':
                    if (!reader.TryGetDateTime(out var t)) return null;
                    r.TimeUtc = t.Kind switch
                    {
                        DateTimeKind.Utc => t,
                        DateTimeKind.Local => t.ToUniversalTime(),
                        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
                    };
                    hasTime = true;
                    break;
                case 'k':
                    r.Kind = (ChatKind)reader.GetInt32();
                    hasKind = true;
                    break;
                case 's':
                    r.FromSelf = reader.TokenType == JsonTokenType.True;
                    break;
                case 'u':
                    r.User = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";
                    break;
                case 'x':
                    r.Text = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";
                    break;
                case 'f':
                    r.Full = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";
                    break;
                case 'n':
                    r.Repeat = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int rep) && rep > 0 ? rep : 1;
                    break;
                case 'p':
                    // Saved before provenance was kept: a player's (its mod links stay as written).
                    r.Source = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int src) && src == (int)ChatLinkSource.Server
                        ? ChatLinkSource.Server : ChatLinkSource.Player;
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject || !hasTime || !hasKind) return null;
        return Enum.IsDefined(typeof(ChatKind), r.Kind) ? r : null;
    }

    /// <summary>
    /// Put restored lines back in front of anything already logged. Every line is numbered again in order, so line
    /// numbers keep growing from the oldest to the newest (<see cref="IndexAtOrBefore"/> relies on it).
    /// </summary>
    internal void Restore(IEnumerable<ChatRecord> restored)
    {
        var all = new List<ChatRecord>(restored);
        for (int i = 0; i < _records.Count; i++) all.Add(_records[i]);
        _records.Clear();
        _filters.Clear();
        _seq = 0;
        foreach (var r in all)
        {
            r.Seq = ++_seq;
            _records.PushBack(r);
        }

        Trim();
        Version++;
    }

    /// <summary>A file-safe name for a server's history (host:port or name, hashed into the name).</summary>
    internal static string FileKey(string? host)
    {
        string h = (host ?? "").Trim().ToLowerInvariant();
        if (h.Length == 0) h = "unknown";
        uint hash = 2166136261;
        foreach (char c in h) hash = (hash ^ c) * 16777619;
        var safe = new StringBuilder();
        foreach (char c in h)
        {
            if (safe.Length >= 32) break;
            safe.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_');
        }

        return safe + "-" + hash.ToString("x8", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// A first-in, first-out ring: constant-time add at the end and drop at the front, indexed oldest first. Game-free.
/// It grows (doubling) when full; it never shrinks.
/// </summary>
internal sealed class ChatRing<T> : IReadOnlyList<T> where T : class
{
    private T?[] _items;
    private int _head;
    private int _count;

    internal ChatRing(int capacity = 16) => _items = new T?[Math.Max(4, capacity)];

    public int Count => _count;

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            int at = _head + index;
            if (at >= _items.Length) at -= _items.Length;
            return _items[at]!;
        }
    }

    internal T? First => _count == 0 ? null : _items[_head];

    internal T? Last => _count == 0 ? null : this[_count - 1];

    internal void PushBack(T item)
    {
        if (_count == _items.Length) Grow();
        int at = _head + _count;
        if (at >= _items.Length) at -= _items.Length;
        _items[at] = item;
        _count++;
    }

    internal T PopFront()
    {
        if (_count == 0) throw new InvalidOperationException("The ring is empty.");
        var item = _items[_head]!;
        _items[_head] = null;
        _head++;
        if (_head == _items.Length) _head = 0;
        _count--;
        return item;
    }

    internal void Clear()
    {
        Array.Clear(_items, 0, _items.Length);
        _head = 0;
        _count = 0;
    }

    private void Grow()
    {
        var next = new T?[_items.Length * 2];
        for (int i = 0; i < _count; i++) next[i] = this[i];
        _items = next;
        _head = 0;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < _count; i++) yield return this[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>CHAT5: what a tab shows. Game-free.</summary>
[Flags]
internal enum ChatScope
{
    None = 0,
    Global = 1,
    Local = 2,
    Clan = 4,
    Whispers = 8,
    SystemAndMods = 16,
    Mentions = 32,
    All = Global | Local | Clan | Whispers | SystemAndMods,
}

/// <summary>CHAT5: one chat tab. <c>All</c> is fixed (never edited or closed, but it can be moved); the rest are the player's.</summary>
internal sealed class ChatTab
{
    internal const string AllName = "All";
    internal const int MaxTabs = 6;
    internal const int MaxNameLength = 16;

    internal string Name { get; set; } = "";

    /// <summary>What the tab shows. Changing it forgets the remembered send channel (CHAT13), which then follows the new channels.</summary>
    internal ChatScope Scope
    {
        get => _scope;
        set
        {
            if (value != _scope) SendTo = null;
            _scope = value;
        }
    }

    private ChatScope _scope;

    internal bool Fixed { get; set; }

    /// <summary>CHAT13: the channel last chosen on this tab, or null for <see cref="ChatTabInput.DefaultFor"/>. Saved.</summary>
    internal ChatSend? SendTo { get; set; }

    /// <summary>CHAT13: the unsent line typed on this tab while another tab is shown. This session only, never saved.</summary>
    internal string Draft { get; set; } = "";

    internal static ChatTab All() => new() { Name = AllName, Scope = ChatScope.All | ChatScope.Mentions, Fixed = true };

    internal static ChatTab Clan() => new() { Name = "Clan", Scope = ChatScope.Clan };

    /// <summary>The presets the + button offers first.</summary>
    internal static IReadOnlyList<ChatTab> Presets() => new[]
    {
        new ChatTab { Name = "Whispers", Scope = ChatScope.Whispers },
        new ChatTab { Name = "System and mods", Scope = ChatScope.SystemAndMods },
        new ChatTab { Name = "Mentions", Scope = ChatScope.Mentions },
        new ChatTab { Name = "Clan", Scope = ChatScope.Clan },
        new ChatTab { Name = "Local", Scope = ChatScope.Local },
    };

    /// <summary>Whether this tab shows <paramref name="r"/>. Session dividers show in every tab.</summary>
    internal bool Shows(ChatRecord r)
    {
        if (r.Kind == ChatKind.Session) return true;
        if ((Scope & ChatScope.Mentions) != 0 && r.Mention) return true;
        ChatScope s = ScopeOf(r.Kind);
        return s != ChatScope.None && (Scope & s) != 0;
    }

    internal static ChatScope ScopeOf(ChatKind kind) => kind switch
    {
        ChatKind.Global or ChatKind.Region => ChatScope.Global,
        ChatKind.Local => ChatScope.Local,
        ChatKind.Team => ChatScope.Clan,
        ChatKind.WhisperFrom or ChatKind.WhisperTo => ChatScope.Whispers,
        ChatKind.System or ChatKind.Lore => ChatScope.SystemAndMods,
        _ => ChatScope.None,
    };

    /// <summary>
    /// The channel typing in this tab sends to before the player chose one (<see cref="ChatTabInput.DefaultFor"/>), as
    /// the game's <c>ChatMessageType</c> number (Global 0, Team 2, Whisper 3, Local 4), or -1 to leave the player's
    /// channel alone (All, Mentions and System tabs).
    /// </summary>
    internal int SendChannel() => ChatTabInput.DefaultFor(this) is ChatSend s ? (int)s : -1;

    /// <summary>CHAT13: the send-channel marker words in <c>[Chat] Tabs</c> (<c>&gt;clan</c>).</summary>
    internal static string SendWord(ChatSend send) => send switch
    {
        ChatSend.Global => "global",
        ChatSend.Clan => "clan",
        ChatSend.Whisper => "whisper",
        ChatSend.Local => "local",
        ChatSend.System => "system",
        _ => "",
    };

    internal static ChatSend? ParseSendWord(string? word)
    {
        foreach (ChatSend s in Enum.GetValues(typeof(ChatSend)))
            if (string.Equals(SendWord(s), (word ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    /// <summary>CHAT13: the marker entry's first character; tab names never contain it (<see cref="Clean"/>).</summary>
    internal const char SendMarker = '>';

    /// <summary>
    /// The tabs as saved in <c>[Chat] Tabs</c>: <c>Name=scope;Name=scope</c>, in the row's order. All is fixed and not
    /// saved with a scope; when the player has moved it away from the front, a bare <see cref="AllName"/> entry marks
    /// its place (<c>Clan=4;All;Trade=1</c>). Builds before tab reordering skip that entry and put All first.
    /// CHAT13: a tab with a remembered send channel is followed by a marker entry, <c>&gt;</c> and the channel's word
    /// (<c>Clan=4;Trade=5;&gt;global;All;&gt;local</c>); All is then written even when first. Older builds skip the
    /// marker (it has no <c>=</c>) and load the tabs as before.
    /// </summary>
    internal static string Save(IReadOnlyList<ChatTab> tabs)
    {
        var parts = new List<string>();
        for (int i = 0; i < tabs.Count; i++)
        {
            var t = tabs[i];
            if (t.Fixed)
            {
                if (i > 0 || t.SendTo != null) parts.Add(AllName);
                if (i == 0 && t.SendTo == null) continue;
            }
            else parts.Add(Clean(t.Name) + "=" + ((int)t.Scope).ToString(CultureInfo.InvariantCulture));

            if (t.SendTo is ChatSend send) parts.Add(SendMarker + SendWord(send));
        }

        return string.Join(";", parts);
    }

    /// <summary>
    /// The saved tabs in their saved order (bad entries skipped, at most <see cref="MaxTabs"/> in all), with All where a
    /// bare <see cref="AllName"/> entry puts it, else first (a config saved before tab reordering).
    /// </summary>
    internal static List<ChatTab> Load(string? saved)
    {
        var tabs = new List<ChatTab>();
        int allAt = -1;
        var all = All();
        // The tab a send marker belongs to: the entry just before it.
        ChatTab? last = null;
        if (saved != null)
        {
            foreach (var raw in saved.Split(';'))
            {
                string part = raw.Trim();
                if (part.Length > 0 && part[0] == SendMarker)
                {
                    // Only a channel the tab may remember (a hand edit or a newer build may say more).
                    if (last != null && ParseSendWord(part.Substring(1)) is ChatSend send && ChatTabInput.CanRemember(last, send)
                        && send != ChatTabInput.DefaultFor(last)) last.SendTo = send;
                    last = null;
                    continue;
                }

                last = null;
                if (tabs.Count >= MaxTabs - 1) break;
                if (allAt < 0 && string.Equals(part, AllName, StringComparison.OrdinalIgnoreCase))
                {
                    allAt = tabs.Count;
                    last = all;
                    continue;
                }

                int eq = part.LastIndexOf('=');
                if (eq <= 0) continue;
                string name = Clean(part.Substring(0, eq));
                if (name.Length == 0 || string.Equals(name, AllName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(part.Substring(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s)) continue;
                var scope = (ChatScope)s & (ChatScope.All | ChatScope.Mentions);
                if (scope == ChatScope.None) continue;
                last = new ChatTab { Name = name, Scope = scope };
                tabs.Add(last);
            }
        }

        tabs.Insert(allAt < 0 ? 0 : Math.Min(allAt, tabs.Count), all);
        return tabs;
    }

    /// <summary>The default saved tabs: Clan.</summary>
    internal static string DefaultSaved => Save(new[] { Clan() });

    internal static string Clean(string? name)
    {
        var sb = new StringBuilder();
        foreach (char c in (name ?? "").Trim())
        {
            if (sb.Length >= MaxNameLength) break;
            if (c == ';' || c == '=' || c == '<' || c == '>' || char.IsControl(c)) continue;
            sb.Append(c);
        }

        return sb.ToString().Trim();
    }
}
