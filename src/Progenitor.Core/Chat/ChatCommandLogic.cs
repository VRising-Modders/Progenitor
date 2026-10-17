using System;
using System.Collections.Generic;

namespace Progenitor.Chat;

/// <summary>
/// CHAT2: the chat commands Progenitor adds to the game's chat box, game-free. The live half is
/// <c>ChatWindow</c>.
///
/// <para><b>What the game already has</b> (its chat command parser, client, 2026-10-07): <c>/w</c> and
/// <c>/whisper</c> with an exact character name (otherwise "No vampire with that name found"), <c>/reply</c>,
/// the channel switches (<c>/local</c>, <c>/global</c>, <c>/allies</c>, <c>/a</c>, <c>/team</c>, <c>/clan</c>,
/// <c>/system</c>) and the clan, kill, stats and pvp commands. It has no <c>/r</c> and no name completion.</para>
///
/// <list type="bullet">
/// <item><b><c>/r</c></b>: typing <c>/r </c> at the start of the line becomes <c>/reply </c>, the game's own reply
/// command, so the game's reply mode and its "No recent whispers" message apply unchanged.</item>
/// <item><b>Name completion</b>: while the line is <c>/w </c> or <c>/whisper </c> followed by part of a name and
/// nothing after it, the completion key (Tab by default) fills in the first online player whose name starts with
/// it (ignoring case), then a space; pressing it again cycles through the other matches.</item>
/// </list>
/// </summary>
internal static class ChatCommandLogic
{
    internal const string ReplyShort = "/r ";
    internal const string ReplyLong = "/reply ";

    /// <summary><c>/r message</c> becomes <c>/reply message</c>; anything else is left alone.</summary>
    internal static bool TryExpandReply(string? text, out string expanded) => TryExpandReply(text, ReplyLong, out expanded);

    /// <summary>
    /// CHAT3: <c>/r message</c> becomes <paramref name="replyLine"/> followed by the message
    /// (<see cref="ChatWhisperMemory.ReplyLine"/>: <c>/w partner </c>, or <c>/reply </c>); anything else is left alone.
    /// </summary>
    internal static bool TryExpandReply(string? text, string replyLine, out string expanded)
    {
        expanded = text ?? "";
        if (text == null || text.Length < ReplyShort.Length) return false;
        if (!text.StartsWith(ReplyShort, StringComparison.OrdinalIgnoreCase)) return false;
        expanded = (string.IsNullOrEmpty(replyLine) ? ReplyLong : replyLine) + text.Substring(ReplyShort.Length);
        return true;
    }

    /// <summary>
    /// The whisper command and the part of a name typed so far, when the line is <c>/w</c> or <c>/whisper</c>, one
    /// space, then a name being typed with nothing after it. Tab characters are ignored (an input field may have
    /// taken the completion key as text).
    /// </summary>
    private static readonly string[] WhisperCommands = { "/whisper ", "/w " };

    internal static bool TryParseWhisperName(string? text, out string command, out string partial)
    {
        command = "";
        partial = "";
        if (string.IsNullOrEmpty(text) || text[0] != '/') return false;
        string line = text.IndexOf('\t') >= 0 ? text.Replace("\t", "") : text;
        foreach (var c in WhisperCommands)
        {
            if (!line.StartsWith(c, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = line.Substring(c.Length);
            if (rest.IndexOf(' ') >= 0) return false;
            command = line.Substring(0, c.Length);
            partial = rest;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The online names that complete <paramref name="partial"/>: a case-insensitive prefix match, without
    /// <paramref name="self"/>, without repeats, sorted case-insensitively. An empty partial matches everyone.
    /// </summary>
    internal static List<string> Matches(string partial, IEnumerable<string> names, string? self)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOf(' ') >= 0) continue;
            if (self != null && string.Equals(name, self, StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.StartsWith(partial ?? "", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(name)) result.Add(name);
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}

/// <summary>
/// CHAT3: who <c>/r</c> answers. The game's <c>/reply</c> answers only the last whisper received, and it keeps no
/// record of whom you whispered (its whisper target is cleared after sending; a sent whisper's chat entry names you
/// as the user), so <c>/r</c> after your own whisper, including one to yourself ("Note to Self"), said "There are
/// no whispers to reply!". This keeps the latest conversation partner in either direction: a received whisper's
/// sender, or the name you whispered when your sent whisper appears. <c>/r</c> becomes <c>/w partner </c>, or the
/// game's <c>/reply </c> while there is no partner yet.
/// </summary>
internal sealed class ChatWhisperMemory
{
    private string _draftTarget = "";

    /// <summary>The latest conversation partner, or empty.</summary>
    internal string Partner { get; private set; } = "";

    /// <summary>
    /// The line being typed: <c>/w name message</c> (or <c>/whisper</c>) names the target of the whisper about to be
    /// sent. A line that is not a whisper leaves the last draft target as it was (the game clears the line on send).
    /// </summary>
    internal void NoteDraft(string? text)
    {
        if (TryWhisperTarget(text, out string name)) _draftTarget = name;
    }

    /// <summary>The game's whisper mode is on with this target (typed after <c>/w name </c> switched the mode).</summary>
    internal void NoteWhisperMode(string? target)
    {
        if (!string.IsNullOrWhiteSpace(target)) _draftTarget = target!.Trim();
    }

    /// <summary>A whisper you received from <paramref name="sender"/>.</summary>
    internal void NoteReceived(string? sender)
    {
        if (!string.IsNullOrWhiteSpace(sender)) Partner = sender!.Trim();
    }

    /// <summary>A whisper you sent appeared in chat: its target is the one you typed.</summary>
    internal void NoteSent()
    {
        if (_draftTarget.Length > 0) Partner = _draftTarget;
    }

    /// <summary>The line <c>/r </c> becomes.</summary>
    internal string ReplyLine() => Partner.Length > 0 ? "/w " + Partner + " " : ChatCommandLogic.ReplyLong;

    internal void Reset()
    {
        _draftTarget = "";
        Partner = "";
    }

    /// <summary><c>/w name</c> followed by a space (the name is complete), with or without a message after it.</summary>
    internal static bool TryWhisperTarget(string? text, out string name)
    {
        name = "";
        if (string.IsNullOrEmpty(text) || text[0] != '/') return false;
        foreach (var c in new[] { "/whisper ", "/w " })
        {
            if (!text.StartsWith(c, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = text.Substring(c.Length);
            int space = rest.IndexOf(' ');
            if (space <= 0) return false;
            name = rest.Substring(0, space);
            return true;
        }

        return false;
    }
}

/// <summary>
/// CHAT7: sent-line recall. Up brings back the previous line you sent (commands included), Down the next; past the
/// newest, Down gives back what you were typing. The newest line is last; a line equal to the one before it is not
/// stored twice. Game-free; the view persists <see cref="Lines"/>.
/// </summary>
internal sealed class ChatRecall
{
    internal const int MaxLines = 50;
    internal const int MaxLineLength = 1000;

    private readonly List<string> _lines = new();
    private int _cursor = -1;
    private string _draft = "";

    internal IReadOnlyList<string> Lines => _lines;

    /// <summary>A line was sent: remember it and end any browsing.</summary>
    internal void Push(string? line)
    {
        _cursor = -1;
        _draft = "";
        string t = (line ?? "").Trim();
        if (t.Length == 0 || t.Length > MaxLineLength) return;
        if (_lines.Count > 0 && string.Equals(_lines[_lines.Count - 1], t, StringComparison.Ordinal)) return;
        _lines.Add(t);
        if (_lines.Count > MaxLines) _lines.RemoveAt(0);
    }

    /// <summary>The line before the one shown (Up), or null when there is none.</summary>
    internal string? Previous(string? current)
    {
        if (_lines.Count == 0) return null;
        if (_cursor < 0)
        {
            _draft = current ?? "";
            _cursor = _lines.Count - 1;
        }
        else if (_cursor > 0) _cursor--;
        else return null;
        return _lines[_cursor];
    }

    /// <summary>The line after the one shown (Down); past the newest, the line being typed before browsing.</summary>
    internal string? Next()
    {
        if (_cursor < 0) return null;
        if (_cursor < _lines.Count - 1) return _lines[++_cursor];
        _cursor = -1;
        return _draft;
    }

    internal void EndBrowsing() => _cursor = -1;

    internal void Load(IEnumerable<string>? lines)
    {
        _lines.Clear();
        _cursor = -1;
        if (lines == null) return;
        foreach (var l in lines) Push(l);
        _cursor = -1;
    }
}

/// <summary>CHAT7: the players this player muted in Progenitor's chat view, by character name. Game-free.</summary>
internal static class ChatMute
{
    internal const int MaxNames = 100;

    internal static List<string> Parse(string? setting)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(setting)) return names;
        foreach (var n in setting!.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = n.Trim();
            if (t.Length == 0 || t.Length > 32 || Contains(names, t)) continue;
            names.Add(t);
            if (names.Count >= MaxNames) break;
        }

        return names;
    }

    internal static bool Contains(IReadOnlyList<string> names, string? user)
    {
        if (string.IsNullOrEmpty(user)) return false;
        foreach (var n in names) if (string.Equals(n, user, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The setting after muting or unmuting <paramref name="user"/>.</summary>
    internal static string Toggle(string? setting, string user)
    {
        var names = Parse(setting);
        int at = names.FindIndex(n => string.Equals(n, user, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) names.RemoveAt(at);
        else if (!string.IsNullOrWhiteSpace(user) && names.Count < MaxNames) names.Add(user.Trim());
        return string.Join(", ", names);
    }
}

/// <summary>
/// CHAT8: <c>/loc </c> and <c>/item name </c> at the start of the chat line become link tokens as they are typed:
/// <c>/loc </c> → <c>[loc:x,z] </c> at this player's position; <c>/item Item_Weapon_Sword_T01_Bone </c> →
/// <c>[item:id] </c> when the prefab name is an item. <c>/loc</c> alone (nothing after it) is expanded when the line is
/// sent instead (<see cref="TryExpandOnSend"/>), so a longer word such as <c>/local</c> is never touched while it is typed.
/// Game-free; the client fills <see cref="Position"/> and <see cref="ItemIdByPrefabName"/>.
/// </summary>
internal static class ChatInputTokens
{
    internal const string LocCommand = "/loc ";
    internal const string LocWord = "/loc";
    internal const string ItemCommand = "/item ";

    /// <summary>
    /// On send (Enter): a line that is exactly <c>/loc</c> (any case, trailing spaces ignored) becomes <c>[loc:x,z]</c>
    /// at this player's position. Anything else, <c>/local</c> included, is left alone.
    /// </summary>
    internal static bool TryExpandOnSend(string? text, out string expanded)
    {
        expanded = text ?? "";
        if (string.IsNullOrEmpty(text) || Position == null) return false;
        if (!string.Equals(text!.TrimEnd(), LocWord, StringComparison.OrdinalIgnoreCase)) return false;
        var (ok, x, z) = Position();
        if (!ok) return false;
        expanded = ChatTokens.Location(x, z);
        return true;
    }

    internal static Func<(bool Ok, float X, float Z)>? Position { get; set; }
    internal static Func<string, int>? ItemIdByPrefabName { get; set; }

    internal static bool TryExpand(string? text, out string expanded)
    {
        expanded = text ?? "";
        if (string.IsNullOrEmpty(text) || text[0] != '/') return false;
        if (text.StartsWith(LocCommand, StringComparison.OrdinalIgnoreCase) && Position != null)
        {
            var (ok, x, z) = Position();
            if (!ok) return false;
            expanded = ChatTokens.Location(x, z) + " " + text.Substring(LocCommand.Length);
            return true;
        }

        if (text.StartsWith(ItemCommand, StringComparison.OrdinalIgnoreCase) && ItemIdByPrefabName != null)
        {
            string rest = text.Substring(ItemCommand.Length);
            int space = rest.IndexOf(' ');
            if (space <= 0) return false;
            int id = ItemIdByPrefabName(rest.Substring(0, space));
            if (id == 0) return false;
            expanded = ChatTokens.Item(id) + rest.Substring(space);
            return true;
        }

        return false;
    }
}

/// <summary>
/// CHAT9: long messages, game-free. A chat line travels in the game's 512-byte text (509 bytes of UTF-8), so a longer
/// message goes as parts: the first ends with <see cref="MoreMark"/>, each later one starts with
/// <see cref="ContinueMark"/> (and all but the last end with <see cref="MoreMark"/>). Progenitor clients join the parts
/// back into one line; other players read the parts in order.
/// </summary>
internal static class ChatLongMessage
{
    internal const string MoreMark = " …";
    internal const string ContinueMark = "↪ ";

    /// <summary>The most UTF-8 bytes one part carries, markers included (under the game's 509 with room to spare).</summary>
    internal const int PartBytes = 480;

    /// <summary>Seconds within which a continuation joins the line before it.</summary>
    internal const double JoinWindowSeconds = 10;

    internal static int Bytes(string text) => System.Text.Encoding.UTF8.GetByteCount(text ?? "");

    /// <summary>Whether <paramref name="text"/> is too long for one chat line.</summary>
    internal static bool IsLong(string? text) => !string.IsNullOrEmpty(text) && Bytes(text!) > PartBytes;

    /// <summary>
    /// The parts to send for <paramref name="text"/>, cut at spaces where possible, the whole message capped at
    /// <paramref name="maxChars"/> characters.
    /// </summary>
    internal static List<string> Split(string text, int maxChars)
    {
        var parts = new List<string>();
        if (string.IsNullOrEmpty(text)) return parts;
        string rest = text.Length > maxChars ? text.Substring(0, maxChars) : text;
        bool first = true;
        while (rest.Length > 0)
        {
            string lead = first ? "" : ContinueMark;
            int budget = PartBytes - Bytes(lead) - Bytes(MoreMark);
            int take = rest.Length;
            while (take > 0 && Bytes(rest.Substring(0, take)) > budget) take--;
            if (take < rest.Length)
            {
                int space = rest.LastIndexOf(' ', Math.Max(0, take - 1), Math.Max(1, take / 2));
                if (space > 0) take = space;
                if (take > 0 && char.IsHighSurrogate(rest[take - 1])) take--;
            }

            if (take <= 0) take = Math.Min(rest.Length, 1);
            string piece = rest.Substring(0, take).TrimEnd();
            rest = rest.Substring(take).TrimStart();
            parts.Add(lead + piece + (rest.Length > 0 ? MoreMark : ""));
            first = false;
        }

        return parts;
    }

    /// <summary>A part that continues the line before it.</summary>
    internal static bool IsContinuation(string? text) =>
        text != null && text.StartsWith(ContinueMark, StringComparison.Ordinal);

    /// <summary>A line that says more is coming.</summary>
    internal static bool ExpectsMore(string? text) =>
        text != null && text.EndsWith(MoreMark, StringComparison.Ordinal);

    /// <summary>The line before joined with a continuation part.</summary>
    internal static string Join(string before, string part)
    {
        string head = ExpectsMore(before) ? before.Substring(0, before.Length - MoreMark.Length) : before;
        string tail = IsContinuation(part) ? part.Substring(ContinueMark.Length) : part;
        return head + " " + tail;
    }
}

/// <summary>
/// CHAT2: one completion session. The first press completes the typed part; a press on the line the last press
/// produced cycles to the next match of the same typed part; anything else the player types starts over.
/// </summary>
internal sealed class ChatNameCompleter
{
    private string _command = "";
    private string _partial = "";
    private string _lastResult = "";
    private int _index = -1;

    /// <summary>The new line for a completion press on <paramref name="text"/>, or null when there is nothing to complete.</summary>
    internal string? Complete(string? text, IEnumerable<string> online, string? self)
    {
        string current = (text ?? "").Replace("\t", "");
        bool cycling = _index >= 0 && current.Length > 0 && string.Equals(current, _lastResult, StringComparison.Ordinal);
        if (!cycling)
        {
            if (!ChatCommandLogic.TryParseWhisperName(current, out _command, out _partial))
            {
                Reset();
                return null;
            }

            _index = -1;
        }

        var matches = ChatCommandLogic.Matches(_partial, online, self);
        if (matches.Count == 0)
        {
            Reset();
            return null;
        }

        _index = (_index + 1) % matches.Count;
        _lastResult = _command + matches[_index] + " ";
        return _lastResult;
    }

    /// <summary>
    /// Whether a completion press on <paramref name="text"/> would produce a line: cycling a line the last press
    /// produced, or a name being typed that some online player's name starts with.
    /// </summary>
    internal bool WouldComplete(string? text, IEnumerable<string> online, string? self)
    {
        string current = (text ?? "").Replace("\t", "");
        string partial;
        if (_index >= 0 && string.Equals(current, _lastResult, StringComparison.Ordinal)) partial = _partial;
        else if (!ChatCommandLogic.TryParseWhisperName(current, out _, out partial)) return false;
        return ChatCommandLogic.Matches(partial, online, self).Count > 0;
    }

    internal void Reset()
    {
        _command = "";
        _partial = "";
        _lastResult = "";
        _index = -1;
    }

    /// <summary>
    /// Whether a completion press would act on <paramref name="text"/> (the line is a name being typed, or the
    /// line the last press produced). While true the game's own use of the key (cycle the channel) is held back.
    /// </summary>
    internal bool WouldAct(string? text)
    {
        string current = (text ?? "").Replace("\t", "");
        if (_index >= 0 && string.Equals(current, _lastResult, StringComparison.Ordinal)) return true;
        return ChatCommandLogic.TryParseWhisperName(current, out _, out _);
    }
}

/// <summary>A mod the <c>[mod:</c> suggestions can offer: its Thunderstore package, the name shown, and whether it is installed.</summary>
internal sealed class ChatModCandidate
{
    internal ChatModCandidate(string package, string label, bool installed)
    {
        Package = package ?? "";
        Label = string.IsNullOrWhiteSpace(label) ? Package : label.Trim();
        Installed = installed;
    }

    internal string Package { get; }
    internal string Label { get; }
    internal bool Installed { get; }
}

/// <summary>
/// CHAT11: typing <c>[mod:</c> and part of a mod's name suggests mods above the line; the completion key puts the first in
/// as a finished link token (<c>[mod:Namespace-Name] </c>) and cycles on the next press. Game-free.
/// </summary>
internal static class ChatModSuggest
{
    internal const string Open = "[mod:";
    internal const int MaxPartial = 40;

    /// <summary>The unfinished <c>[mod:</c> at the end of the line: where it starts and what follows it.</summary>
    internal static bool TryParse(string? text, out int start, out string partial)
    {
        start = -1;
        partial = "";
        if (string.IsNullOrEmpty(text)) return false;
        int i = text!.LastIndexOf(Open, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        string rest = text.Substring(i + Open.Length);
        if (rest.IndexOf(']') >= 0 || rest.Length > MaxPartial) return false;
        start = i;
        partial = rest;
        return true;
    }

    /// <summary>
    /// The candidates that match <paramref name="partial"/> (name or package, ignoring case; all when empty), each package
    /// once: names that start with it first, then installed mods, then by name.
    /// </summary>
    internal static List<ChatModCandidate> Rank(string partial, IEnumerable<ChatModCandidate> candidates, int max)
    {
        string p = (partial ?? "").Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hits = new List<(ChatModCandidate C, int Score)>();
        foreach (var c in candidates)
        {
            if (c == null || c.Package.Length == 0 || !seen.Add(c.Package)) continue;
            string name = NameOf(c.Package);
            bool starts = p.Length == 0 || c.Label.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(p, StringComparison.OrdinalIgnoreCase);
            bool contains = starts || c.Label.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0
                || c.Package.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!contains) continue;
            hits.Add((c, (starts ? 0 : 2) + (c.Installed ? 0 : 1)));
        }

        hits.Sort((a, b) =>
        {
            int s = a.Score.CompareTo(b.Score);
            return s != 0 ? s : string.Compare(a.C.Label, b.C.Label, StringComparison.OrdinalIgnoreCase);
        });
        var result = new List<ChatModCandidate>();
        foreach (var h in hits)
        {
            if (result.Count >= max) break;
            result.Add(h.C);
        }

        return result;
    }

    /// <summary>The line with the unfinished token replaced by a finished one and a space after it.</summary>
    internal static string Complete(string text, int start, string package) =>
        text.Substring(0, start) + Open + package + "] ";

    /// <summary>The name part of <c>Namespace-Name</c>.</summary>
    private static string NameOf(string package)
    {
        int dash = package.IndexOf('-');
        return dash >= 0 ? package.Substring(dash + 1) : package;
    }
}
