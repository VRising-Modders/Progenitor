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
/// <para><b>Capacity.</b> The oldest lines go first once <see cref="Capacity"/> is reached; the view draws only the
/// newest lines of the active tab.</para>
/// </summary>
internal sealed class ChatLog
{
    internal const int DefaultCapacity = 500;
    internal const int MinCapacity = 50;
    internal const int MaxCapacity = 5000;
    internal const double CollapseWindowSeconds = 60;

    private readonly List<ChatRecord> _records = new();
    private long _seq;

    internal ChatLog(int capacity = DefaultCapacity) => Capacity = ClampCapacity(capacity);

    internal int Capacity { get; set; }

    /// <summary>Raised count of changes; the view redraws when it moves.</summary>
    internal int Version { get; private set; }

    internal IReadOnlyList<ChatRecord> Records => _records;

    internal static int ClampCapacity(int n) => n < MinCapacity ? MinCapacity : n > MaxCapacity ? MaxCapacity : n;

    /// <summary>Add a line, or raise the last line's count when it repeats. Returns the record shown.</summary>
    internal ChatRecord Add(ChatRecord record, bool collapse)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        if (collapse && record.Kind != ChatKind.Session && _records.Count > 0)
        {
            var last = _records[_records.Count - 1];
            if (!last.Restored && last.Kind == record.Kind && last.FromSelf == record.FromSelf && last.Source == record.Source
                && string.Equals(last.User, record.User, StringComparison.Ordinal)
                && string.Equals(last.Text, record.Text, StringComparison.Ordinal)
                && (record.TimeUtc - last.TimeUtc).TotalSeconds <= CollapseWindowSeconds)
            {
                last.Repeat++;
                last.TimeUtc = record.TimeUtc;
                last.Mention |= record.Mention;
                Version++;
                return last;
            }
        }

        record.Seq = ++_seq;
        if (record.Repeat < 1) record.Repeat = 1;
        _records.Add(record);
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
        var last = _records[_records.Count - 1];
        if (last.Restored || last.Kind != part.Kind || last.FromSelf != part.FromSelf || last.Source != part.Source
            || !string.Equals(last.User, part.User, StringComparison.Ordinal) || !ChatLongMessage.ExpectsMore(last.Text)
            || (part.TimeUtc - last.TimeUtc).TotalSeconds > ChatLongMessage.JoinWindowSeconds) return null;
        last.Text = ChatLongMessage.Join(last.Text, part.Text);
        last.TimeUtc = part.TimeUtc;
        last.Mention |= part.Mention;
        Version++;
        return last;
    }

    /// <summary>A divider line: <c>Joined Night Garden · 21:04</c> / <c>Left the server · 23:10</c>.</summary>
    internal ChatRecord AddMarker(string text, DateTime utc) =>
        Add(new ChatRecord { Kind = ChatKind.Session, Text = text ?? "", TimeUtc = utc }, collapse: false);

    internal void Clear()
    {
        _records.Clear();
        Version++;
    }

    private void Trim()
    {
        int extra = _records.Count - Capacity;
        if (extra > 0) _records.RemoveRange(0, extra);
    }

    /// <summary>The newest <paramref name="max"/> lines a tab shows, oldest first.</summary>
    internal List<ChatRecord> Visible(ChatTab tab, int max)
    {
        var result = new List<ChatRecord>();
        for (int i = _records.Count - 1; i >= 0 && result.Count < max; i--)
        {
            if (tab.Shows(_records[i])) result.Add(_records[i]);
        }

        result.Reverse();
        return result;
    }

    internal static string JoinedText(string server, DateTime local) =>
        "Joined " + (string.IsNullOrWhiteSpace(server) ? "a server" : server.Trim()) + " · " + local.ToString("HH:mm", CultureInfo.InvariantCulture);

    internal static string LeftText(DateTime local) => "Left the server · " + local.ToString("HH:mm", CultureInfo.InvariantCulture);

    // ---- persistence: one JSON object per line -----------------------------------------------------------

    /// <summary>The log as JSON lines (the newest <paramref name="max"/>), for the per-server history file.</summary>
    internal string Serialize(int max)
    {
        var sb = new StringBuilder();
        int from = Math.Max(0, _records.Count - max);
        for (int i = from; i < _records.Count; i++)
        {
            var r = _records[i];
            if (r.Transient) continue;
            sb.Append(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["t"] = r.TimeUtc.ToString("o", CultureInfo.InvariantCulture),
                ["k"] = (int)r.Kind,
                ["s"] = r.FromSelf,
                ["u"] = r.User,
                ["x"] = r.Text,
                ["f"] = r.Full,
                ["n"] = r.Repeat,
                ["p"] = (int)r.Source,
            }));
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Lines saved by <see cref="Serialize"/>; malformed lines are skipped. Restored lines never mention.</summary>
    internal static List<ChatRecord> Deserialize(string? text, int max)
    {
        var result = new List<ChatRecord>();
        if (string.IsNullOrEmpty(text)) return result;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var o = doc.RootElement;
                var r = new ChatRecord
                {
                    TimeUtc = DateTime.Parse(o.GetProperty("t").GetString() ?? "", CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                    Kind = (ChatKind)o.GetProperty("k").GetInt32(),
                    FromSelf = o.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.True,
                    User = o.TryGetProperty("u", out var u) ? u.GetString() ?? "" : "",
                    Text = o.TryGetProperty("x", out var x) ? x.GetString() ?? "" : "",
                    Full = o.TryGetProperty("f", out var f) ? f.GetString() ?? "" : "",
                    Repeat = o.TryGetProperty("n", out var n) && n.TryGetInt32(out int rep) && rep > 0 ? rep : 1,
                    Restored = true,
                    // Saved before provenance was kept: a player's (its mod links stay as written).
                    Source = o.TryGetProperty("p", out var p) && p.TryGetInt32(out int src) && src == (int)ChatLinkSource.Server
                        ? ChatLinkSource.Server : ChatLinkSource.Player,
                };
                if (!Enum.IsDefined(typeof(ChatKind), r.Kind)) continue;
                result.Add(r);
            }
            catch
            {
                // A damaged line costs that line only.
            }
        }

        if (result.Count > max) result.RemoveRange(0, result.Count - max);
        return result;
    }

    /// <summary>Put restored lines back in front of anything already logged.</summary>
    internal void Restore(IEnumerable<ChatRecord> restored)
    {
        var old = new List<ChatRecord>(restored);
        foreach (var r in old) r.Seq = ++_seq;
        _records.InsertRange(0, old);
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
