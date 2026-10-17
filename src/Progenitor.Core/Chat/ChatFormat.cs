using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Ui;

namespace Progenitor.Chat;

/// <summary>CHAT5: a clickable part of a chat message.</summary>
internal readonly struct ChatSpan
{
    internal ChatSpan(ChatSpanKind kind, int start, int length, string target, string? display = null)
    {
        Kind = kind;
        Start = start;
        Length = length;
        Target = target ?? "";
        Display = display;
    }

    internal ChatSpanKind Kind { get; }
    internal int Start { get; }
    internal int Length { get; }

    /// <summary>
    /// The URL (web link), the package id with an optional <c>@version</c> (mod link), <c>x,z</c> (location) or the
    /// item's prefab id (item link).
    /// </summary>
    internal string Target { get; }

    /// <summary>What the line shows instead of the token text (a location's coordinates, an item's name); null: the text.</summary>
    internal string? Display { get; }
}

internal enum ChatSpanKind
{
    Url = 0,
    Mod = 1,
    Location = 2,
    Item = 3,
}

/// <summary>
/// CHAT8: location and item tokens in chat, game-free. <c>[loc:x,z]</c> (world coordinates, whole numbers) and
/// <c>[item:id]</c> (the item's prefab id). Players without Progenitor see the token as written.
/// </summary>
internal static class ChatTokens
{
    internal const int MaxPerLine = 8;

    internal static string Location(float x, float z) =>
        "[loc:" + ((int)Math.Round(x)).ToString(CultureInfo.InvariantCulture) + "," + ((int)Math.Round(z)).ToString(CultureInfo.InvariantCulture) + "]";

    internal static string Item(int prefabId) => "[item:" + prefabId.ToString(CultureInfo.InvariantCulture) + "]";

    internal static bool TryParseLocation(string target, out int x, out int z)
    {
        x = z = 0;
        int comma = target.IndexOf(',');
        return comma > 0
            && int.TryParse(target.Substring(0, comma), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out x)
            && int.TryParse(target.Substring(comma + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out z)
            && Math.Abs(x) <= 100000 && Math.Abs(z) <= 100000;
    }

    /// <summary>Location and item tokens in <paramref name="text"/> (start, length, kind, target), strict.</summary>
    internal static List<(int Start, int Length, ChatSpanKind Kind, string Target)> Find(string? text)
    {
        var found = new List<(int, int, ChatSpanKind, string)>();
        if (string.IsNullOrEmpty(text)) return found;
        int at = 0;
        while (found.Count < MaxPerLine)
        {
            int open = text!.IndexOf('[', at);
            if (open < 0) break;
            int close = text.IndexOf(']', open + 1);
            if (close < 0) break;
            string body = text.Substring(open + 1, close - open - 1);
            if (body.StartsWith("loc:", StringComparison.Ordinal) && TryParseLocation(body.Substring(4), out _, out _))
                found.Add((open, close - open + 1, ChatSpanKind.Location, body.Substring(4)));
            else if (body.StartsWith("item:", StringComparison.Ordinal) && body.Length <= 17
                && int.TryParse(body.Substring(5), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int id) && id != 0)
                found.Add((open, close - open + 1, ChatSpanKind.Item, id.ToString(CultureInfo.InvariantCulture)));
            at = close + 1;
        }

        return found;
    }

    /// <summary>A compass direction from a world-space offset (x east, z north): north, north-east, and so on.</summary>
    internal static string Direction(float dx, float dz)
    {
        if (Math.Abs(dx) < 0.5f && Math.Abs(dz) < 0.5f) return "here";
        double deg = Math.Atan2(dx, dz) * 180.0 / Math.PI;
        if (deg < 0) deg += 360.0;
        string[] names = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
        return names[(int)Math.Round(deg / 45.0) % 8];
    }
}

/// <summary>
/// CHAT5: how Progenitor's chat view draws a <see cref="ChatRecord"/>, game-free.
///
/// <para><b>Built on the game's own line.</b> A line the game drew keeps its channel tag and colored name exactly
/// (<see cref="ChatRecord.Full"/>, everything before <c>&lt;noparse&gt;</c>); the message itself is redrawn from the
/// plain text with every character escaped (<see cref="UiRichText.Literal"/>), so a player still cannot add formatting,
/// and links become <c>&lt;link="u:n"&gt;</c> / <c>&lt;link="m:n"&gt;</c> regions in the link color. A line the game did
/// not draw (a divider, an old saved line without the game's text) is built as <c>[Channel] Name: message</c>.</para>
/// </summary>
internal static class ChatFormat
{
    internal const string MutedHex = "#9A9A9A";

    /// <summary>The mention highlight behind a line (TMP <c>&lt;mark&gt;</c>: gold at low alpha).</summary>
    internal const string MentionMark = "#E8B33F40";

    /// <summary>
    /// Finds mod-link tokens in a message (start, length, package, version). Null until the mod-link module installs
    /// its finder; then <c>[mod:Namespace-Name@1.2.3]</c> becomes a link.
    /// </summary>
    internal static Func<string, IEnumerable<(int Start, int Length, string Package, string? Version)>>? ModTokenFinder { get; set; }

    /// <summary>CHAT8: an item's display name by prefab id, or null when unknown (the token then shows as written).</summary>
    internal static Func<int, string?>? ItemNameResolver { get; set; }

    internal sealed class Options
    {
        internal bool Timestamps { get; init; }
        internal bool Links { get; init; } = true;
        internal bool ModLinks { get; init; } = true;
        internal bool Mentions { get; init; } = true;
        internal bool Use24Hour { get; init; } = true;
    }

    /// <summary>The clickable parts of <paramref name="text"/>, in order, never overlapping.</summary>
    internal static List<ChatSpan> Spans(string? text, Options options)
    {
        var spans = new List<ChatSpan>();
        if (string.IsNullOrEmpty(text)) return spans;
        if (options.ModLinks && ModTokenFinder != null)
        {
            try
            {
                foreach (var (start, length, package, version) in ModTokenFinder(text!))
                {
                    if (start < 0 || length <= 0 || start + length > text!.Length) continue;
                    spans.Add(new ChatSpan(ChatSpanKind.Mod, start, length,
                        string.IsNullOrEmpty(version) ? package : package + "@" + version));
                }
            }
            catch
            {
                // A finder that throws makes no mod links on this line.
            }
        }

        foreach (var (start, length, kind, target) in ChatTokens.Find(text))
        {
            if (Overlaps(spans, start, length)) continue;
            string? display = null;
            if (kind == ChatSpanKind.Location && ChatTokens.TryParseLocation(target, out int lx, out int lz))
                display = "[" + lx.ToString(CultureInfo.InvariantCulture) + ", " + lz.ToString(CultureInfo.InvariantCulture) + "]";
            else if (kind == ChatSpanKind.Item)
            {
                string? name = null;
                try { name = ItemNameResolver?.Invoke(int.Parse(target, CultureInfo.InvariantCulture)); } catch { name = null; }
                if (!string.IsNullOrWhiteSpace(name)) display = "[" + name + "]";
            }

            spans.Add(new ChatSpan(kind, start, length, target, display));
        }

        if (options.Links)
        {
            foreach (var link in UiLinks.Find(text))
            {
                if (Overlaps(spans, link.Start, link.Length)) continue;
                spans.Add(new ChatSpan(ChatSpanKind.Url, link.Start, link.Length, link.Url));
            }
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        return spans;
    }

    private static bool Overlaps(List<ChatSpan> spans, int start, int length)
    {
        foreach (var s in spans)
            if (start < s.Start + s.Length && s.Start < start + length) return true;
        return false;
    }

    /// <summary>The TMP text for one line, and its links (link ids are <c>u:n</c> / <c>m:n</c>, n the span's index).</summary>
    internal static string Render(ChatRecord r, Options options, out List<ChatSpan> spans, DateTime? localTime = null)
    {
        spans = new List<ChatSpan>();
        var sb = new StringBuilder(64 + (r.Full?.Length ?? 0) + r.Text.Length * 2);
        if (r.Kind == ChatKind.Session)
        {
            sb.Append("<color=").Append(MutedHex).Append(">— ").Append(UiRichText.Literal(r.Text)).Append(" —</color>");
            return sb.ToString();
        }

        // System and lore lines: the game draws them as rich text (a server or mod line may color itself), with no
        // <noparse>; draw them the same way, links wrapped where their text appears as written.
        bool rich = (r.Kind == ChatKind.System || r.Kind == ChatKind.Lore)
            && (r.Full ?? "").IndexOf("<noparse>", StringComparison.Ordinal) < 0;
        string prefix = rich ? "" : Prefix(r);
        if (options.Timestamps && !StartsWithTime(rich ? (r.Full ?? r.Text) : prefix))
        {
            var t = localTime ?? r.TimeUtc.ToLocalTime();
            sb.Append("<color=").Append(MutedHex).Append(">[")
              .Append(t.ToString(options.Use24Hour ? "HH:mm" : "h:mm", CultureInfo.InvariantCulture))
              .Append("]</color> ");
        }

        if (rich)
        {
            string body = (r.Full ?? "").Length > 0 ? r.Full! : (ChannelLabel(r.Kind).Length > 0 ? "[" + ChannelLabel(r.Kind) + "] " : "") + r.Text;
            spans = RichSpans(body, options);
            AppendRich(sb, body, spans);
        }
        else
        {
            sb.Append(prefix);
            spans = Spans(r.Text, options);
            AppendMessage(sb, r.Text, spans);
        }
        if (r.Repeat > 1)
            sb.Append(" <color=").Append(MutedHex).Append(">×").Append(r.Repeat.ToString(CultureInfo.InvariantCulture)).Append("</color>");

        string line = sb.ToString();
        if (options.Mentions && r.Mention && !r.Restored) line = "<mark=" + MentionMark + ">" + line + "</mark>";
        if (r.Restored) line = "<alpha=#99>" + line;
        return line;
    }

    /// <summary>
    /// What comes before the message: the game's own part of its line (before <c>&lt;noparse&gt;</c>), or for a line the
    /// game drew without one, the line minus its message; else a built <c>[Channel] Name: </c>.
    /// </summary>
    internal static string Prefix(ChatRecord r)
    {
        string full = r.Full ?? "";
        int np = full.IndexOf("<noparse>", StringComparison.Ordinal);
        if (np >= 0) return full.Substring(0, np);
        if (full.Length > 0 && r.Text.Length > 0 && full.EndsWith(r.Text, StringComparison.Ordinal))
            return UiRichText.Literal(full.Substring(0, full.Length - r.Text.Length));
        string channel = ChannelLabel(r.Kind);
        string who = r.User.Length > 0 ? UiRichText.Literal(r.User) + ": " : "";
        return (channel.Length > 0 ? "[" + channel + "] " : "") + who;
    }

    internal static string ChannelLabel(ChatKind kind) => kind switch
    {
        ChatKind.Global => "Global",
        ChatKind.Region => "Region",
        ChatKind.Team => "Clan",
        ChatKind.WhisperFrom or ChatKind.WhisperTo => "Whisper",
        ChatKind.System => "System",
        ChatKind.Lore => "",
        ChatKind.Local => "Local",
        _ => "",
    };

    private static bool StartsWithTime(string prefix)
    {
        // The game's own timestamp option draws "[12:50]" first (inside a color tag or not).
        string plain = StripTags(prefix).TrimStart();
        return plain.Length >= 7 && plain[0] == '[' && char.IsDigit(plain[1]) && plain.IndexOf(':') is > 1 and < 4 && plain.IndexOf(']') is > 3 and < 9;
    }

    private static string StripTags(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool inTag = false;
        foreach (char c in s)
        {
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag) sb.Append(c);
        }

        return sb.ToString();
    }

    private static void AppendMessage(StringBuilder sb, string text, List<ChatSpan> spans)
    {
        int at = 0;
        string color = UiLinks.ColorHex;
        for (int n = 0; n < spans.Count; n++)
        {
            var s = spans[n];
            if (s.Start < at) continue;
            sb.Append(UiRichText.Literal(text.Substring(at, s.Start - at)));
            sb.Append("<link=\"").Append(LinkPrefix(s.Kind)).Append(n.ToString(CultureInfo.InvariantCulture))
              .Append("\"><color=").Append(color).Append("><u>")
              .Append(UiRichText.Literal(s.Display ?? text.Substring(s.Start, s.Length)))
              .Append("</u></color></link>");
            at = s.Start + s.Length;
        }

        sb.Append(UiRichText.Literal(text.Substring(at)));
    }

    /// <summary>
    /// The links in a rich-text line: found in its visible text, kept only where they sit outside tags in the line itself
    /// (a link inside a tag's attribute is not wrapped).
    /// </summary>
    private static List<ChatSpan> RichSpans(string body, Options options)
    {
        var found = Spans(body, options);
        var kept = new List<ChatSpan>();
        foreach (var s in found)
            if (!InsideTag(body, s.Start) && body.IndexOf('<', s.Start, s.Length) < 0) kept.Add(s);
        return kept;
    }

    private static bool InsideTag(string text, int at)
    {
        int open = text.LastIndexOf('<', Math.Max(0, at - 1));
        if (open < 0) return false;
        int close = text.LastIndexOf('>', Math.Max(0, at - 1));
        return close < open;
    }

    /// <summary>A rich line as written, with its links wrapped (their own text is not markup: it holds no tag).</summary>
    private static void AppendRich(StringBuilder sb, string body, List<ChatSpan> spans)
    {
        int at = 0;
        string color = UiLinks.ColorHex;
        for (int n = 0; n < spans.Count; n++)
        {
            var s = spans[n];
            if (s.Start < at) continue;
            sb.Append(body, at, s.Start - at);
            sb.Append("<link=\"").Append(LinkPrefix(s.Kind)).Append(n.ToString(CultureInfo.InvariantCulture))
              .Append("\"><color=").Append(color).Append("><u>")
              .Append(UiRichText.Literal(s.Display ?? body.Substring(s.Start, s.Length)))
              .Append("</u></color></link>");
            at = s.Start + s.Length;
        }

        sb.Append(body, at, body.Length - at);
    }

    /// <summary>The span a TMP link id names (<c>u:2</c>), or null.</summary>
    internal static ChatSpan? SpanForLink(string? linkId, List<ChatSpan> spans)
    {
        if (string.IsNullOrEmpty(linkId) || linkId!.Length < 3 || linkId[1] != ':') return null;
        if (!int.TryParse(linkId.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return null;
        if (n < 0 || n >= spans.Count) return null;
        var s = spans[n];
        if (!linkId.StartsWith(LinkPrefix(s.Kind), StringComparison.Ordinal)) return null;
        return s;
    }

    private static string LinkPrefix(ChatSpanKind kind) => kind switch
    {
        ChatSpanKind.Mod => "m:",
        ChatSpanKind.Location => "l:",
        ChatSpanKind.Item => "i:",
        _ => "u:",
    };

    /// <summary>The line as plain text, for Copy (no tags, the game's timestamp kept).</summary>
    internal static string PlainText(ChatRecord r)
    {
        if (r.Kind == ChatKind.Session) return "— " + r.Text + " —";
        bool rich = (r.Kind == ChatKind.System || r.Kind == ChatKind.Lore)
            && (r.Full ?? "").IndexOf("<noparse>", StringComparison.Ordinal) < 0;
        string line = rich ? StripTags((r.Full ?? "").Length > 0 ? r.Full! : r.Text) : StripTags(Prefix(r)) + r.Text;
        return line + (r.Repeat > 1 ? " ×" + r.Repeat.ToString(CultureInfo.InvariantCulture) : "");
    }
}

/// <summary>
/// CHAT6: the tab row's words, game-free: the menu glyph and the tab editor's "Show messages from" lines. The row
/// itself (CHAT12) is drawn by <c>ChatTabs</c> from <see cref="ChatTabRow"/>.
/// </summary>
internal static class ChatTabStrip
{
    /// <summary>The menu button: a plain glyph every TMP font in the game has (the gear is missing from some).</summary>
    internal const string MenuGlyph = "≡";

    /// <summary>The "Show messages from" choices, in the order the tab editor lists them.</summary>
    internal static readonly (ChatScope Scope, string Label)[] ScopeChoices =
    {
        (ChatScope.Global, "Global"),
        (ChatScope.Local, "Local"),
        (ChatScope.Clan, "Clan"),
        (ChatScope.Whispers, "Whispers"),
        (ChatScope.SystemAndMods, "System and mods"),
        (ChatScope.Mentions, "Mentions of me"),
    };

    /// <summary>A scope choice's line in the editor: a check mark when on.</summary>
    internal static string ScopeLine(ChatScope scope, (ChatScope Scope, string Label) choice) =>
        ((scope & choice.Scope) != 0 ? "✓ " : "   ") + choice.Label;
}

/// <summary>CHAT5: whether a chat line mentions this player. Game-free.</summary>
internal static class ChatMentions
{
    internal const string AtAll = "@all";

    /// <summary>
    /// A mention: this player's name, a watched word, or (when the server allows it) <c>@all</c> from an admin. Whole
    /// words, ignoring case. Your own line can mention you too (highlighted, with the sound), so a sender sees it worked.
    /// </summary>
    internal static bool IsMention(string? text, string? ownName, IReadOnlyList<string>? watchWords,
        bool senderIsAdmin, bool allowAtAll)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (!string.IsNullOrWhiteSpace(ownName) && ContainsWord(text!, ownName!.Trim())) return true;
        if (allowAtAll && senderIsAdmin && ContainsWord(text!, AtAll)) return true;
        if (watchWords != null)
        {
            foreach (var w in watchWords)
                if (!string.IsNullOrWhiteSpace(w) && ContainsWord(text!, w.Trim())) return true;
        }

        return false;
    }

    /// <summary>The words list from a setting: commas or spaces between words.</summary>
    internal static List<string> ParseWords(string? setting)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(setting)) return words;
        foreach (var w in setting!.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = w.Trim();
            if (t.Length >= 2 && t.Length <= 32 && !words.Contains(t)) words.Add(t);
            if (words.Count >= 20) break;
        }

        return words;
    }

    /// <summary>Whole word, ignoring case: the characters around it are not letters, digits or underscores.</summary>
    internal static bool ContainsWord(string text, string word)
    {
        if (word.Length == 0) return false;
        int at = 0;
        while (true)
        {
            int i = text.IndexOf(word, at, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            bool before = i == 0 || !IsWordChar(text[i - 1]);
            bool after = i + word.Length >= text.Length || !IsWordChar(text[i + word.Length]);
            // "@all" itself starts with a non-word character, so only its end needs a boundary check.
            if ((before || !IsWordChar(word[0])) && after) return true;
            at = i + 1;
        }
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}

/// <summary>
/// CHAT6: the player's own channel colors (the chat menu's <b>Colors</b>): a channel, then a color from a short palette
/// readable on the dark box, or the game's own color back. Saved in <c>[Chat] Colors</c> as
/// <c>Global=#7AB8F5;Clan=#8ED86E</c>; a channel not listed keeps the game's color. Game-free.
/// </summary>
internal static class ChatColors
{
    /// <summary>The channels a player can recolor: the saved key, the menu name and the line kinds it covers.</summary>
    internal static readonly (string Key, string Name, ChatKind[] Kinds)[] Channels =
    {
        ("Global", "Global", new[] { ChatKind.Global, ChatKind.Region }),
        ("Local", "Local", new[] { ChatKind.Local }),
        ("Clan", "Clan", new[] { ChatKind.Team }),
        ("Whispers", "Whispers", new[] { ChatKind.WhisperFrom, ChatKind.WhisperTo }),
        ("System", "System and mods", new[] { ChatKind.System, ChatKind.Lore }),
    };

    internal static readonly (string Name, string Hex)[] Palette =
    {
        ("Gold", "#E8C547"),
        ("Orange", "#F0A050"),
        ("Red", "#F06A6A"),
        ("Pink", "#F08AD0"),
        ("Purple", "#B48CF0"),
        ("Blue", "#7AB8F5"),
        ("Cyan", "#6ED6E0"),
        ("Green", "#8ED86E"),
        ("White", "#FFFFFF"),
        ("Gray", "#B4B4B4"),
    };

    /// <summary>The saved colors: channel key to <c>#RRGGBB</c>. Unknown keys and bad colors are dropped.</summary>
    internal static Dictionary<string, string> Parse(string? saved)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(saved)) return map;
        foreach (var part in saved!.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part.Substring(0, eq).Trim();
            string hex = part.Substring(eq + 1).Trim();
            if (KeyIndex(key) >= 0 && IsHex(hex)) map[Channels[KeyIndex(key)].Key] = hex.ToUpperInvariant();
        }

        return map;
    }

    /// <summary>The saved text with <paramref name="channelKey"/> set to <paramref name="hex"/>, or back to the game's color when null.</summary>
    internal static string Set(string? saved, string channelKey, string? hex)
    {
        var map = Parse(saved);
        int i = KeyIndex(channelKey);
        if (i < 0) return Format(map);
        if (hex != null && IsHex(hex)) map[Channels[i].Key] = hex.ToUpperInvariant();
        else map.Remove(Channels[i].Key);
        return Format(map);
    }

    /// <summary>The player's color for a line kind, or null for the game's.</summary>
    internal static string? HexFor(string? saved, ChatKind kind)
    {
        foreach (var c in Channels)
        {
            if (Array.IndexOf(c.Kinds, kind) < 0) continue;
            return Parse(saved).TryGetValue(c.Key, out var hex) ? hex : null;
        }

        return null;
    }

    /// <summary>The menu's word for a saved color: its palette name, else the hex; "the game's" when none.</summary>
    internal static string Describe(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return "the game's";
        foreach (var p in Palette)
            if (string.Equals(p.Hex, hex, StringComparison.OrdinalIgnoreCase)) return p.Name;
        return hex!;
    }

    private static int KeyIndex(string key)
    {
        for (int i = 0; i < Channels.Length; i++)
            if (string.Equals(Channels[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string Format(Dictionary<string, string> map)
    {
        var sb = new StringBuilder();
        foreach (var c in Channels)
        {
            if (!map.TryGetValue(c.Key, out var hex)) continue;
            if (sb.Length > 0) sb.Append(';');
            sb.Append(c.Key).Append('=').Append(hex);
        }

        return sb.ToString();
    }

    internal static bool IsHex(string? hex)
    {
        if (hex == null || hex.Length != 7 || hex[0] != '#') return false;
        for (int i = 1; i < 7; i++)
            if (!Uri.IsHexDigit(hex[i])) return false;
        return true;
    }
}
