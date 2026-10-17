using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Chat;

/// <summary>What kind of chat link was clicked.</summary>
public enum ChatLinkKind
{
    /// <summary>A web address (<c>https://…</c>, <c>www.…</c>). <see cref="ChatLinkClick.Target"/> is the URL.</summary>
    Web = 0,

    /// <summary>A Thunderstore mod link (<c>[mod:Namespace-Name@1.2.3]</c>). Target is the package with an optional <c>@version</c>.</summary>
    Package = 1,

    /// <summary>A location link (<c>[loc:x,z]</c>). Target is <c>x,z</c>.</summary>
    Location = 2,

    /// <summary>An item link (<c>[item:id]</c>). Target is the prefab id; <see cref="ChatLinkClick.ItemPrefab"/> has it as a number.</summary>
    Item = 3,

    /// <summary>A mod-owned link (<c>[mod:owner:type:payload|text]</c>), built with <see cref="ChatLinks.ModLink"/>.</summary>
    Mod = 4,
}

/// <summary>
/// Who wrote the chat line a link is on. Decided from the game's own event fields, never from the text: see
/// <see cref="ChatLinks"/>.
/// </summary>
public enum ChatLinkSource
{
    /// <summary>A player typed the line (any player, admins and this player included). Treat its links as untrusted input.</summary>
    Player = 0,

    /// <summary>The server sent it with no speaker: the game's own notices, Progenitor and server mods; or this client's game.</summary>
    Server = 1,
}

/// <summary>The chat channel a line arrived on (the game's message types).</summary>
public enum ChatChannel
{
    /// <summary>Not one of the game's channels.</summary>
    Unknown = 0,
    /// <summary>Global chat.</summary>
    Global = 1,
    /// <summary>Region chat (the game sends none).</summary>
    Region = 2,
    /// <summary>Clan chat.</summary>
    Clan = 3,
    /// <summary>A whisper this player received.</summary>
    WhisperFrom = 4,
    /// <summary>A whisper this player sent.</summary>
    WhisperTo = 5,
    /// <summary>System lines: server notices, admin announcements, mod replies.</summary>
    System = 6,
    /// <summary>The game's lore lines.</summary>
    Lore = 7,
    /// <summary>Local chat (48 m around the speaker).</summary>
    Local = 8,
}

/// <summary>
/// A chat link the player clicked, as a <see cref="ChatLinkHandler"/> sees it. Client only.
/// </summary>
public sealed class ChatLinkClick
{
    internal ChatLinkClick(ChatLinkKind kind, string target, string text, ChatLinkSource source, string sender,
        bool fromSelf, ChatChannel channel, string line, string ns = "", string linkType = "")
    {
        Kind = kind;
        Target = target ?? "";
        Text = text ?? "";
        Source = source;
        Sender = sender ?? "";
        FromSelf = fromSelf;
        Channel = channel;
        Line = line ?? "";
        Owner = ns ?? "";
        LinkType = linkType ?? "";
        if (kind == ChatLinkKind.Item && int.TryParse(Target, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int id))
            ItemPrefab = id;
    }

    /// <summary>What kind of link it is.</summary>
    public ChatLinkKind Kind { get; }

    /// <summary>
    /// The link's target: the URL, the package (<c>Namespace-Name[@version]</c>), <c>x,z</c>, the item's prefab id, or for
    /// a <see cref="ChatLinkKind.Mod"/> link its payload (unescaped).
    /// </summary>
    public string Target { get; }

    /// <summary>The text the chat line shows for the link.</summary>
    public string Text { get; }

    /// <summary>For an <see cref="ChatLinkKind.Mod"/> link, its owner (lowercase); empty otherwise.</summary>
    public string Owner { get; }

    /// <summary>For an <see cref="ChatLinkKind.Mod"/> link, its type (<c>chest</c>, lowercase); empty otherwise.</summary>
    public string LinkType { get; }

    /// <summary>For an <see cref="ChatLinkKind.Mod"/> link, its payload (the same as <see cref="Target"/>).</summary>
    public string Payload => Kind == ChatLinkKind.Mod ? Target : "";

    /// <summary>For an <see cref="ChatLinkKind.Item"/> link, the item's prefab id; 0 otherwise.</summary>
    public int ItemPrefab { get; }

    /// <summary>Who wrote the line: <see cref="ChatLinkSource.Server"/> lines can be trusted, player lines cannot.</summary>
    public ChatLinkSource Source { get; }

    /// <summary>Shorthand for <c>Source == ChatLinkSource.Server</c>.</summary>
    public bool IsFromServer => Source == ChatLinkSource.Server;

    /// <summary>The speaking player's character name, or empty for a server line.</summary>
    public string Sender { get; }

    /// <summary>This player sent the line.</summary>
    public bool FromSelf { get; }

    /// <summary>The channel the line arrived on.</summary>
    public ChatChannel Channel { get; }

    /// <summary>The line's message text, as written (tokens included).</summary>
    public string Line { get; }
}

/// <summary>
/// A chat link click handler. Return true when the click is handled: later handlers and Progenitor's own action
/// (a dialog, the Mods menu, the browser prompt) are then skipped. Runs on the client's main thread.
/// </summary>
public delegate bool ChatLinkHandler(ChatLinkClick click);

/// <summary>
/// Chat links for mods: build a mod-owned link token, and make player text safe to echo.
///
/// <para><b>The token.</b> <c>[mod:owner:type:payload|text]</c>: <c>owner</c> and <c>type</c> are 1 to 48 and 1 to 32
/// of <c>a-z 0-9 _ . -</c> (lowercase), <c>payload</c> is the mod's own data (at most 160 characters), <c>text</c> what the
/// line shows (at most 80). <c>\</c>, <c>|</c> and <c>]</c> inside the payload or the text are written <c>\\</c>,
/// <c>\|</c>, <c>\]</c>; <see cref="ModLink"/> does that. A Progenitor client draws the token as its text, a link when a
/// mod on that client handles its owner (<c>ctx.Chat.OnModLink</c>), plain text otherwise. A client without Progenitor
/// shows the token as written.</para>
///
/// <para><b>Who wrote the line.</b> Players can type tokens too. A line counts as the server's
/// (<see cref="ChatLinkSource.Server"/>) only when the game says it is a System or lore line with no speaker: the
/// server's chat event carries the sender's network id, which the server fills in from the connection a line came from
/// and a player cannot leave empty, and the game forwards a player's System line only from an admin, with that admin as
/// its sender. Everything else is <see cref="ChatLinkSource.Player"/>. A mod link on a player's line stays as written
/// unless the owning handler accepts player links.</para>
///
/// <para><b>Echoing player text.</b> A server line that repeats what a player typed (an "unknown name 'x'" reply) would
/// carry the player's tokens as the server's. Pass that text through <see cref="Defuse"/> first.</para>
/// </summary>
public static class ChatLinks
{
    /// <summary>
    /// The token for a mod-owned link. <paramref name="owner"/> and <paramref name="type"/> are lowercased and must be
    /// 1 to 48 and 1 to 32 of <c>a-z 0-9 _ . -</c>; <paramref name="payload"/> at most 160 characters with no control
    /// characters, <c>&lt;</c> or <c>&gt;</c>. <paramref name="text"/> is cleaned (control characters become spaces,
    /// <c>&lt;</c> and <c>&gt;</c> become ‹ and ›, at most 80 characters); empty text shows the payload.
    /// Throws <see cref="ArgumentException"/> for an owner, type or payload that is not valid.
    /// </summary>
    public static string ModLink(string owner, string type, string payload, string text) =>
        ChatLinkToken.Format(owner, type, payload, text);

    /// <summary>
    /// <paramref name="text"/> with every chat link token (<c>[mod:</c>, <c>[item:</c>, <c>[loc:</c>) made plain text, so a
    /// server line that repeats a player's words cannot carry their links as the server's. The token's <c>[</c> becomes
    /// <c>(</c>. Null is empty.
    /// </summary>
    public static string Defuse(string? text) => ChatLinkToken.Defuse(text);
}

/// <summary>
/// The mod-owned link token <c>[mod:owner:type:payload|text]</c>, game-free. Strict: anything that does not parse
/// whole is plain text. Disjoint from the package token <see cref="ModLinkToken"/> (<c>[mod:Ns-Name]</c>), which allows
/// no <c>:</c>.
/// </summary>
internal static class ChatLinkToken
{
    internal const string Prefix = "[mod:";
    internal const int MaxOwner = 48;
    internal const int MaxType = 32;
    internal const int MaxPayload = 160;
    internal const int MaxText = 80;
    internal const int MaxPerLine = 8;

    /// <summary>The longest token as written (every payload and text character escaped).</summary>
    internal const int MaxTokenLength = 5 + MaxOwner + 1 + MaxType + 1 + MaxPayload * 2 + 1 + MaxText * 2 + 1;

    internal readonly struct Token
    {
        internal Token(int start, int length, string owner, string type, string payload, string text)
        {
            Start = start;
            Length = length;
            Owner = owner;
            Type = type;
            Payload = payload;
            Text = text;
        }

        internal int Start { get; }
        internal int Length { get; }
        internal string Owner { get; }
        internal string Type { get; }
        internal string Payload { get; }

        /// <summary>The text to show: the token's own text, else its payload.</summary>
        internal string Text { get; }
    }

    internal static string Format(string owner, string type, string payload, string text)
    {
        string o = (owner ?? "").Trim().ToLowerInvariant();
        string t = (type ?? "").Trim().ToLowerInvariant();
        if (!IsName(o, MaxOwner)) throw new ArgumentException("A link owner is 1 to " + MaxOwner + " of a-z 0-9 _ . -.", nameof(owner));
        if (!IsName(t, MaxType)) throw new ArgumentException("A link type is 1 to " + MaxType + " of a-z 0-9 _ . -.", nameof(type));
        string p = payload ?? "";
        if (p.Length > MaxPayload) throw new ArgumentException("A link payload is at most " + MaxPayload + " characters.", nameof(payload));
        foreach (char c in p)
            if (!IsFreeChar(c)) throw new ArgumentException("A link payload has no control characters, < or >.", nameof(payload));
        string shown = CleanText(text);
        if (shown.Length == 0 && p.Trim().Length == 0)
            throw new ArgumentException("A link needs text to show (or a payload to show instead).", nameof(text));
        var sb = new StringBuilder(Prefix.Length + o.Length + t.Length + p.Length + shown.Length + 8);
        sb.Append(Prefix).Append(o).Append(':').Append(t).Append(':');
        Escape(sb, p);
        if (shown.Length > 0)
        {
            sb.Append('|');
            Escape(sb, shown);
        }

        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>A link's shown text: control characters as spaces, angle brackets as ‹ ›, trimmed, at most <see cref="MaxText"/>.</summary>
    internal static string CleanText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text!.Length);
        foreach (char c in text)
        {
            if (char.IsControl(c)) sb.Append(' ');
            else if (c == '<') sb.Append('‹');
            else if (c == '>') sb.Append('›');
            else sb.Append(c);
        }

        string s = sb.ToString().Trim();
        if (s.Length > MaxText)
        {
            int cut = MaxText - 1;
            if (char.IsHighSurrogate(s[cut - 1])) cut--;
            s = s.Substring(0, cut).TrimEnd() + "…";
        }

        return s;
    }

    private static void Escape(StringBuilder sb, string s)
    {
        foreach (char c in s)
        {
            if (c == '\\' || c == '|' || c == ']') sb.Append('\\');
            sb.Append(c);
        }
    }

    /// <summary>Every valid token in <paramref name="text"/>, in order, at most <see cref="MaxPerLine"/>. Never throws.</summary>
    internal static List<Token> Find(string? text)
    {
        var found = new List<Token>();
        if (string.IsNullOrEmpty(text)) return found;
        int from = 0;
        while (found.Count < MaxPerLine && from < text!.Length)
        {
            int start = text.IndexOf(Prefix, from, StringComparison.Ordinal);
            if (start < 0) break;
            if (TryParseAt(text, start, out var token))
            {
                found.Add(token);
                from = start + token.Length;
            }
            else
            {
                from = start + 1;
            }
        }

        return found;
    }

    /// <summary>Parse one whole token (nothing around it).</summary>
    internal static bool TryParse(string? token, out Token parsed)
    {
        parsed = default;
        return !string.IsNullOrEmpty(token) && TryParseAt(token!, 0, out parsed) && parsed.Length == token!.Length;
    }

    internal static bool TryParseAt(string text, int start, out Token token)
    {
        token = default;
        if (start < 0 || string.CompareOrdinal(text, start, Prefix, 0, Prefix.Length) != 0) return false;
        int i = start + Prefix.Length;
        int limit = Math.Min(text.Length, start + MaxTokenLength);
        if (!ReadName(text, ref i, limit, MaxOwner, out string owner)) return false;
        if (!ReadName(text, ref i, limit, MaxType, out string type)) return false;
        if (!ReadFree(text, ref i, limit, MaxPayload, out string payload, out char stop)) return false;
        string shown = "";
        if (stop == '|')
        {
            if (!ReadFree(text, ref i, limit, MaxText, out shown, out stop) || stop != ']' || shown.Trim().Length == 0) return false;
        }

        string display = shown.Length > 0 ? shown : payload;
        if (display.Trim().Length == 0) return false;
        token = new Token(start, i - start, owner, type, payload, display);
        return true;
    }

    /// <summary>A name up to its <c>:</c> (consumed), lowercased.</summary>
    private static bool ReadName(string text, ref int i, int limit, int max, out string name)
    {
        name = "";
        int begin = i;
        while (i < limit && text[i] != ':')
        {
            if (!IsNameChar(text[i]) || i - begin >= max) return false;
            i++;
        }

        if (i >= limit || i == begin) return false;
        name = text.Substring(begin, i - begin).ToLowerInvariant();
        i++;
        return true;
    }

    /// <summary>Escaped free text up to an unescaped <c>|</c> or <c>]</c> (consumed; <paramref name="stop"/> says which).</summary>
    private static bool ReadFree(string text, ref int i, int limit, int max, out string value, out char stop)
    {
        value = "";
        stop = '\0';
        var sb = new StringBuilder();
        while (i < limit)
        {
            char c = text[i];
            if (c == '\\')
            {
                if (i + 1 >= limit) return false;
                char next = text[i + 1];
                if (next != '\\' && next != '|' && next != ']') return false;
                sb.Append(next);
                i += 2;
            }
            else if (c == '|' || c == ']')
            {
                stop = c;
                i++;
                if (sb.Length > max) return false;
                value = sb.ToString();
                return true;
            }
            else
            {
                if (!IsFreeChar(c)) return false;
                sb.Append(c);
                i++;
            }

            if (sb.Length > max) return false;
        }

        return false;
    }

    internal static bool IsName(string? s, int max)
    {
        if (string.IsNullOrEmpty(s) || s!.Length > max) return false;
        foreach (char c in s)
            if (!IsNameChar(c) || (c >= 'A' && c <= 'Z')) return false;
        return true;
    }

    private static bool IsNameChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-';

    /// <summary>A payload or text character: no control characters and no angle brackets (they would be rich text).</summary>
    private static bool IsFreeChar(char c) => !char.IsControl(c) && c != '<' && c != '>';

    private static readonly string[] Defused = { "[mod:", "[item:", "[loc:" };

    internal static string Defuse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text!.IndexOf('[') < 0) return text ?? "";
        var sb = new StringBuilder(text);
        for (int i = 0; i < sb.Length; i++)
        {
            if (sb[i] != '[') continue;
            foreach (string p in Defused)
            {
                if (i + p.Length > text.Length || string.CompareOrdinal(text, i, p, 0, p.Length) != 0) continue;
                sb[i] = '(';
                break;
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// The client's chat link handlers, game-free. Handlers are owned by plugins and released by their tracked handles.
///
/// <para><b>Order.</b> A <see cref="ChatLinkKind.Mod"/> link goes to its owner's handler first. Then every
/// <c>OnLinkClicked</c> handler runs in the order they were registered (plugin load order). The first that returns true
/// ends it; when none does, Progenitor's own action runs. A handler that throws counts as not handled and is logged once.</para>
/// </summary>
internal static class ChatLinkRegistry
{
    private sealed class Entry
    {
        internal object Owner = null!;
        internal string PluginId = "";
        internal string Namespace = "";
        internal ChatLinkHandler Handler = null!;
        internal bool AcceptPlayerLinks;
        internal bool Warned;
        internal bool Removed;
    }

    private static readonly List<Entry> General = new();
    private static readonly Dictionary<string, Entry> Owners = new(StringComparer.Ordinal);

    /// <summary>Raised on every change, so the chat view redraws (an owned link becomes clickable).</summary>
    internal static int Version { get; private set; }

    internal static int GeneralCount => General.Count;

    /// <summary>Register a handler for every link click. Dispose the result to remove it.</summary>
    internal static IDisposable OnClick(object owner, string pluginId, ChatLinkHandler handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var e = new Entry { Owner = owner, PluginId = pluginId ?? "", Handler = handler };
        General.Add(e);
        Version++;
        return new Handle(e);
    }

    /// <summary>
    /// Claim a mod link owner. The same plugin claiming again replaces its handler (a hot reload); another plugin's
    /// claim is refused with <see cref="InvalidOperationException"/>.
    /// </summary>
    internal static IDisposable OnMod(object owner, string pluginId, string ns, ChatLinkHandler handler, bool acceptPlayerLinks)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        string key = (ns ?? "").Trim().ToLowerInvariant();
        if (!ChatLinkToken.IsName(key, ChatLinkToken.MaxOwner))
            throw new ArgumentException("A link owner is 1 to " + ChatLinkToken.MaxOwner + " of a-z 0-9 _ . -.", nameof(ns));
        if (Owners.TryGetValue(key, out var held) && !string.Equals(held.PluginId, pluginId ?? "", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Chat link owner '" + key + "' is already handled by " + held.PluginId + ".");
        if (held != null) held.Removed = true;
        var e = new Entry { Owner = owner, PluginId = pluginId ?? "", Namespace = key, Handler = handler, AcceptPlayerLinks = acceptPlayerLinks };
        Owners[key] = e;
        Version++;
        return new Handle(e);
    }

    /// <summary>Whether a mod link with this owner is clickable on a line from <paramref name="source"/>.</summary>
    internal static bool Clickable(string ns, ChatLinkSource source)
    {
        if (!Owners.TryGetValue(ns ?? "", out var e)) return false;
        return source == ChatLinkSource.Server || e.AcceptPlayerLinks;
    }

    /// <summary>Whether any plugin handles this owner.</summary>
    internal static bool IsOwned(string ns) => Owners.ContainsKey(ns ?? "");

    /// <summary>Run the handlers for a click in order; true when one handled it.</summary>
    internal static bool Dispatch(ChatLinkClick click)
    {
        if (click == null) return false;
        if (click.Kind == ChatLinkKind.Mod && Owners.TryGetValue(click.Owner, out var owner)
            && (click.Source == ChatLinkSource.Server || owner.AcceptPlayerLinks)
            && Run(owner, click)) return true;
        foreach (var e in General.ToArray())
        {
            if (e.Removed) continue;
            if (Run(e, click)) return true;
        }

        return false;
    }

    private static bool Run(Entry e, ChatLinkClick click)
    {
        try { return e.Handler(click); }
        catch (Exception ex)
        {
            if (!e.Warned)
            {
                e.Warned = true;
                Plugin.Logger?.LogWarning("Chat: a link handler of " + e.PluginId + " threw (logged once): " + ex.Message);
            }

            return false;
        }
    }

    /// <summary>Drop every handler of <paramref name="owner"/> (a plugin context that did not dispose its handles).</summary>
    internal static int RemoveAll(object owner)
    {
        int n = 0;
        foreach (var e in General.ToArray())
            if (ReferenceEquals(e.Owner, owner)) { Remove(e); n++; }
        foreach (var e in new List<Entry>(Owners.Values))
            if (ReferenceEquals(e.Owner, owner)) { Remove(e); n++; }
        return n;
    }

    /// <summary>Core unload: forget everything.</summary>
    internal static void Clear()
    {
        foreach (var e in General) e.Removed = true;
        foreach (var e in Owners.Values) e.Removed = true;
        General.Clear();
        Owners.Clear();
        Version++;
    }

    private static void Remove(Entry e)
    {
        if (e.Removed) return;
        e.Removed = true;
        General.Remove(e);
        if (e.Namespace.Length > 0 && Owners.TryGetValue(e.Namespace, out var held) && ReferenceEquals(held, e))
            Owners.Remove(e.Namespace);
        Version++;
    }

    private sealed class Handle : IDisposable
    {
        private Entry? _entry;
        internal Handle(Entry e) => _entry = e;

        public void Dispose()
        {
            var e = _entry;
            _entry = null;
            if (e != null) Remove(e);
        }
    }
}

/// <summary>
/// What the chat shows for an item link's tooltip and its default click, game-free: the item's name and its
/// description from the game, never its raw prefab id.
/// </summary>
internal static class ChatItemText
{
    internal const string UnknownTitle = "Unknown item";
    internal const string UnknownBody = "This game has no item by that link.";
    internal const string NoDescription = "The game has no description for this item.";

    /// <summary>The title and body for an item, from what the game knows of it (either may be null).</summary>
    internal static (string Title, string Body) Content(string? name, string? description)
    {
        string n = Clean(name);
        string d = Clean(description);
        if (n.Length == 0) return (UnknownTitle, UnknownBody);
        d = DropPlaceholders(d);
        return (n, d.Length > 0 ? d : NoDescription);
    }

    /// <summary>
    /// The game fills a description's <c>{0}</c> placeholders from the item in a slot; a chat link has none, so each
    /// becomes <c>…</c>.
    /// </summary>
    internal static string DropPlaceholders(string text)
    {
        if (text.IndexOf('{') < 0) return text;
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                int close = text.IndexOf('}', i + 1);
                if (close > i + 1 && close - i <= 4 && int.TryParse(text.Substring(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    sb.Append('…');
                    i = close;
                    continue;
                }
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }

    /// <summary>Rich text tags dropped, whitespace runs folded, trimmed.</summary>
    internal static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text!.Length);
        bool inTag = false, space = false;
        foreach (char c in text)
        {
            if (c == '<') { inTag = true; continue; }
            if (inTag) { if (c == '>') inTag = false; continue; }
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (c == '\n' && sb.Length > 0)
                {
                    TrimEnd(sb);
                    if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                    space = true;
                    continue;
                }
                if (!space && sb.Length > 0) sb.Append(' ');
                space = true;
                continue;
            }

            sb.Append(c);
            space = false;
        }

        return sb.ToString().Trim();
    }

    private static void TrimEnd(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
    }
}
