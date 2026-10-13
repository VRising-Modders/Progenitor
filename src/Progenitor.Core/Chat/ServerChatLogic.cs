using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Chat;

/// <summary>
/// CHAT4 server side (docs/chat.md, "Server settings (as built)"): the game-free rules behind the Server tab's Chat group —
/// the word filter, the Discord relay's formatting, escaping and rate limit, the webhook address check, and the mapping
/// from the server's synced switches to <see cref="ChatPolicy"/>. Nothing here touches the game or the network.
/// </summary>
internal static class ServerChatText
{
    /// <summary>What the sender of a blocked line is told (a System line to them only).</summary>
    internal const string BlockedNotice = "Your message was not sent: it contains a word this server filters.";

    /// <summary>What a filtered word becomes with the Replace action.</summary>
    internal const string Mask = "***";
}

/// <summary>The word filter's action (<c>[ChatFilter] Action</c>).</summary>
internal enum ChatFilterAction
{
    /// <summary>Each filtered word becomes <see cref="ServerChatText.Mask"/>; the line is still sent.</summary>
    Replace = 0,

    /// <summary>The line is not sent; only its sender is told why.</summary>
    Block = 1,
}

/// <summary>What the filter decided for one line.</summary>
internal readonly struct ChatFilterOutcome
{
    internal ChatFilterOutcome(bool matched, bool block, string text)
    {
        Matched = matched;
        Block = block;
        Text = text ?? "";
    }

    /// <summary>A filtered word was found.</summary>
    internal bool Matched { get; }

    /// <summary>The line must not be sent.</summary>
    internal bool Block { get; }

    /// <summary>The line to send: unchanged when nothing matched, masked for Replace.</summary>
    internal string Text { get; }
}

/// <summary>
/// The server's word filter: case-insensitive whole words (or phrases). A match must not touch a letter or digit on
/// either side, so <c>ass</c> does not match <c>class</c>. Game-free.
/// </summary>
internal static class ChatWordFilter
{
    /// <summary>The most words the list keeps.</summary>
    internal const int MaxWords = 200;

    /// <summary>The longest word or phrase.</summary>
    internal const int MaxWordChars = 64;

    /// <summary>The longest stored list (the admin row's field and the server's check).</summary>
    internal const int MaxListChars = 2000;

    internal const string ActionReplace = "replace";
    internal const string ActionBlock = "block";

    internal static readonly IReadOnlyList<string> ActionWords = new[] { ActionReplace, ActionBlock };

    /// <summary>
    /// The words of <paramref name="list"/> in the order typed, each once (ignoring case): separated by commas, semicolons or
    /// line breaks, trimmed, inner runs of spaces made one. Over-long words and words past <see cref="MaxWords"/> are left out.
    /// </summary>
    internal static List<string> Parse(string? list)
    {
        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in (list ?? "").Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string word = CollapseSpaces(raw.Trim());
            if (word.Length == 0 || word.Length > MaxWordChars) continue;
            if (!HasWordChar(word)) continue;
            if (!seen.Add(word)) continue;
            words.Add(word);
            if (words.Count >= MaxWords) break;
        }

        return words;
    }

    /// <summary>The list as stored: <see cref="Parse"/> joined with <c>", "</c>.</summary>
    internal static string Normalize(string? list) => string.Join(", ", Parse(list));

    /// <summary>Null when <paramref name="list"/> can be stored, else why not.</summary>
    internal static string? Check(string? list)
    {
        string text = list ?? "";
        if (text.Length > MaxListChars) return "At most " + MaxListChars.ToString(CultureInfo.InvariantCulture) + " characters.";
        foreach (string raw in text.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string word = raw.Trim();
            if (word.Length > MaxWordChars)
                return "A word or phrase is at most " + MaxWordChars.ToString(CultureInfo.InvariantCulture) + " characters.";
        }

        return null;
    }

    /// <summary>The action word as stored (<c>replace</c> or <c>block</c>); anything else reads as Replace.</summary>
    internal static ChatFilterAction ParseAction(string? word) =>
        string.Equals((word ?? "").Trim(), ActionBlock, StringComparison.OrdinalIgnoreCase) ? ChatFilterAction.Block : ChatFilterAction.Replace;

    /// <summary>True when <paramref name="text"/> holds any of <paramref name="words"/> as a whole word.</summary>
    internal static bool Matches(string? text, IReadOnlyList<string>? words)
    {
        if (string.IsNullOrEmpty(text) || words == null) return false;
        foreach (string word in words)
            if (FindWhole(text!, word, 0) >= 0) return true;
        return false;
    }

    /// <summary><paramref name="text"/> with every whole-word match of <paramref name="words"/> replaced by <see cref="ServerChatText.Mask"/>.</summary>
    internal static string Mask(string? text, IReadOnlyList<string>? words)
    {
        string result = text ?? "";
        if (result.Length == 0 || words == null) return result;
        // Longer entries first, so a phrase is masked whole before one of its words is.
        var ordered = new List<string>(words);
        ordered.Sort((a, b) => b.Length.CompareTo(a.Length));
        foreach (string word in ordered)
        {
            if (string.IsNullOrEmpty(word)) continue;
            int at = 0;
            while (at <= result.Length)
            {
                int found = FindWhole(result, word, at);
                if (found < 0) break;
                result = result.Substring(0, found) + ServerChatText.Mask + result.Substring(found + word.Length);
                at = found + ServerChatText.Mask.Length;
            }
        }

        return result;
    }

    /// <summary>The filter's decision for one line.</summary>
    internal static ChatFilterOutcome Decide(string? text, IReadOnlyList<string>? words, ChatFilterAction action)
    {
        string t = text ?? "";
        if (!Matches(t, words)) return new ChatFilterOutcome(false, false, t);
        return action == ChatFilterAction.Block
            ? new ChatFilterOutcome(true, true, t)
            : new ChatFilterOutcome(true, false, Mask(t, words));
    }

    /// <summary>The first whole-word, case-insensitive match of <paramref name="word"/> at or after <paramref name="start"/>, or -1.</summary>
    internal static int FindWhole(string text, string word, int start)
    {
        if (string.IsNullOrEmpty(word) || string.IsNullOrEmpty(text)) return -1;
        int at = start < 0 ? 0 : start;
        while (at <= text.Length - word.Length)
        {
            int found = text.IndexOf(word, at, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return -1;
            bool before = found == 0 || !IsWordChar(text[found - 1]) || !IsWordChar(word[0]);
            int end = found + word.Length;
            bool after = end >= text.Length || !IsWordChar(text[end]) || !IsWordChar(word[word.Length - 1]);
            if (before && after) return found;
            at = found + 1;
        }

        return -1;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool HasWordChar(string word)
    {
        foreach (char c in word)
            if (IsWordChar(c)) return true;
        return false;
    }

    private static string CollapseSpaces(string s)
    {
        if (s.IndexOf("  ", StringComparison.Ordinal) < 0 && s.IndexOf('\t') < 0) return s;
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c in s)
        {
            bool ws = c == ' ' || c == '\t';
            if (ws)
            {
                if (!space) sb.Append(' ');
                space = true;
            }
            else
            {
                sb.Append(c);
                space = false;
            }
        }

        return sb.ToString();
    }
}

/// <summary>The chat channels the Discord relay can post (one switch each on the Server tab).</summary>
internal enum ChatRelayChannel
{
    None = 0,
    Global = 1,
    Local = 2,
    Clan = 3,
    Whisper = 4,
    System = 5,
}

/// <summary>
/// The Discord relay's text rules: the channel of a game chat type, the line, mention escaping, the 2000-character cut,
/// the JSON body and the webhook address check. Game-free.
/// </summary>
internal static class ChatRelayFormat
{
    /// <summary>Discord's longest message content.</summary>
    internal const int MaxContentChars = 2000;

    /// <summary>The longest stored webhook address.</summary>
    internal const int MaxUrlChars = 512;

    /// <summary>Inserted after every <c>@</c>, so no line can ping <c>@everyone</c>, <c>@here</c>, a user or a role.</summary>
    internal const char ZeroWidthSpace = '​';

    /// <summary>
    /// The relay channel of the game's inbound <c>ChatMessageType</c> value (0 Global, 1 Region, 2 Team, 3 Whisper,
    /// 4 Local, 5 System). Region has no recipients in the game and is never relayed.
    /// </summary>
    internal static ChatRelayChannel ChannelOf(int chatMessageType) => chatMessageType switch
    {
        0 => ChatRelayChannel.Global,
        2 => ChatRelayChannel.Clan,
        3 => ChatRelayChannel.Whisper,
        4 => ChatRelayChannel.Local,
        5 => ChatRelayChannel.System,
        _ => ChatRelayChannel.None,
    };

    /// <summary>
    /// The relayed line: <c>Name: message</c> for Global; other channels lead with their name in brackets
    /// (<c>[Local] Name: message</c>); a line without a speaker (a server notice) is <c>[System] message</c>.
    /// </summary>
    internal static string Line(string? name, string? text, ChatRelayChannel channel)
    {
        string body = OneLine(text);
        string who = OneLine(name);
        string tag = channel switch
        {
            ChatRelayChannel.Local => "[Local] ",
            ChatRelayChannel.Clan => "[Clan] ",
            ChatRelayChannel.Whisper => "[Whisper] ",
            ChatRelayChannel.System => "[System] ",
            _ => "",
        };
        return who.Length == 0 ? tag + body : tag + who + ": " + body;
    }

    /// <summary>Every <c>@</c> gets a zero-width space after it (already-escaped ones are left alone).</summary>
    internal static string EscapeMentions(string? text)
    {
        string t = text ?? "";
        if (t.IndexOf('@') < 0) return t;
        var sb = new StringBuilder(t.Length + 8);
        for (int i = 0; i < t.Length; i++)
        {
            sb.Append(t[i]);
            if (t[i] == '@' && (i + 1 >= t.Length || t[i + 1] != ZeroWidthSpace)) sb.Append(ZeroWidthSpace);
        }

        return sb.ToString();
    }

    /// <summary>Escaped, then cut to <see cref="MaxContentChars"/> (an ellipsis marks a cut; a surrogate pair is never split).</summary>
    internal static string Content(string? line)
    {
        string t = EscapeMentions(line);
        if (t.Length <= MaxContentChars) return t;
        int cut = MaxContentChars - 1;
        if (char.IsHighSurrogate(t[cut - 1])) cut--;
        return t.Substring(0, cut) + "…";
    }

    /// <summary>The webhook body: <c>{"content": ..., "allowed_mentions": {"parse": []}}</c>.</summary>
    internal static string Json(string content)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("content", content ?? "");
            w.WriteStartObject("allowed_mentions");
            w.WriteStartArray("parse");
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Null when <paramref name="url"/> is a Discord webhook address Progenitor posts to (empty clears it), else why not:
    /// <c>https</c>, host <c>discord.com</c> or <c>discordapp.com</c> (also their <c>ptb.</c> and <c>canary.</c> hosts), a path
    /// under <c>/api/webhooks/</c>, no user name or password, the default port.
    /// </summary>
    internal static string? CheckWebhookUrl(string? url)
    {
        string u = (url ?? "").Trim();
        if (u.Length == 0) return null;
        const string error = "Give a Discord webhook address: https://discord.com/api/webhooks/...";
        if (u.Length > MaxUrlChars) return "At most " + MaxUrlChars.ToString(CultureInfo.InvariantCulture) + " characters.";
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) return error;
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return error;
        if (uri.UserInfo.Length > 0 || !uri.IsDefaultPort) return error;
        string host = uri.Host.ToLowerInvariant();
        bool discord = host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com"
            or "ptb.discordapp.com" or "canary.discordapp.com";
        if (!discord) return error;
        if (!uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase)) return error;
        if (uri.AbsolutePath.Length <= "/api/webhooks/".Length) return error;
        return null;
    }

    /// <summary>True when <paramref name="url"/> is set and passes <see cref="CheckWebhookUrl"/>.</summary>
    internal static bool UsableWebhookUrl(string? url) => (url ?? "").Trim().Length > 0 && CheckWebhookUrl(url) == null;

    /// <summary>The address as logs and the admin channel may show it: <c>set</c> or empty, never any part of it.</summary>
    internal static string Redact(string? url) => (url ?? "").Trim().Length > 0 ? "set" : "";

    private static string OneLine(string? text)
    {
        string t = (text ?? "").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        return t.Trim();
    }
}

/// <summary>
/// The Discord relay's queue and rate limit (game-free; the caller supplies the clock). At most one request in flight,
/// at most one start per <see cref="MinIntervalSeconds"/>; lines wait in a queue of at most <see cref="Capacity"/>,
/// the oldest dropped first. A request carries as many waiting lines as fit in one message. Thread-safe.
/// </summary>
internal sealed class ChatRelayQueue
{
    internal const int Capacity = 50;
    internal const double MinIntervalSeconds = 1.0;

    /// <summary>The longest wait a <c>429</c>'s retry-after is honored for.</summary>
    internal const double MaxRetryAfterSeconds = 60.0;

    private readonly object _gate = new();
    private readonly LinkedList<string> _lines = new();
    private double _nextStart = double.NegativeInfinity;
    private bool _inFlight;
    private List<string>? _sending;

    /// <summary>Lines dropped because the queue was full, since the last <see cref="TakeDropped"/>.</summary>
    private int _dropped;

    internal int Count
    {
        get { lock (_gate) return _lines.Count; }
    }

    internal bool InFlight
    {
        get { lock (_gate) return _inFlight; }
    }

    /// <summary>Queue one line (already formatted); returns true when an older line was dropped to make room.</summary>
    internal bool Enqueue(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        lock (_gate)
        {
            _lines.AddLast(ChatRelayFormat.Content(line));
            if (_lines.Count <= Capacity) return false;
            _lines.RemoveFirst();
            _dropped++;
            return true;
        }
    }

    /// <summary>
    /// The content of the next request, or null when one is in flight, the rate limit has not passed or nothing waits.
    /// The lines taken are joined with line breaks up to <see cref="ChatRelayFormat.MaxContentChars"/>.
    /// </summary>
    internal string? TakeBatch(double now)
    {
        lock (_gate)
        {
            if (_inFlight || now < _nextStart || _lines.Count == 0) return null;
            var batch = new List<string>();
            int length = 0;
            while (_lines.Count > 0)
            {
                string next = _lines.First!.Value;
                int add = (batch.Count == 0 ? 0 : 1) + next.Length;
                if (batch.Count > 0 && length + add > ChatRelayFormat.MaxContentChars) break;
                batch.Add(next);
                length += add;
                _lines.RemoveFirst();
            }

            _inFlight = true;
            _sending = batch;
            _nextStart = now + MinIntervalSeconds;
            return string.Join("\n", batch);
        }
    }

    /// <summary>
    /// The request started by <see cref="TakeBatch"/> ended. <paramref name="retryAfterSeconds"/> above 0 (a <c>429</c>) puts
    /// its lines back at the front and waits that long (at most <see cref="MaxRetryAfterSeconds"/>); any other failure drops them.
    /// </summary>
    internal void Completed(double now, bool ok, double retryAfterSeconds = 0)
    {
        lock (_gate)
        {
            _inFlight = false;
            var sent = _sending;
            _sending = null;
            if (ok || retryAfterSeconds <= 0 || sent == null) return;
            double wait = Math.Min(retryAfterSeconds, MaxRetryAfterSeconds);
            if (now + wait > _nextStart) _nextStart = now + wait;
            for (int i = sent.Count - 1; i >= 0; i--) _lines.AddFirst(sent[i]);
            while (_lines.Count > Capacity)
            {
                _lines.RemoveFirst();
                _dropped++;
            }
        }
    }

    /// <summary>The dropped count since the last call, and resets it.</summary>
    internal int TakeDropped()
    {
        lock (_gate)
        {
            int n = _dropped;
            _dropped = 0;
            return n;
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            _sending = null;
            _inFlight = false;
            _dropped = 0;
            _nextStart = double.NegativeInfinity;
        }
    }
}

/// <summary>Once-per-interval gate for a repeated warning (the relay's failures: one line a minute). Thread-safe.</summary>
internal sealed class ChatLogThrottle
{
    private readonly double _every;
    private readonly object _gate = new();
    private double _last = double.NegativeInfinity;
    private int _suppressed;

    internal ChatLogThrottle(double everySeconds) => _every = everySeconds;

    /// <summary>True when a line may be written now; <paramref name="suppressed"/> is how many were held back since the last one.</summary>
    internal bool ShouldLog(double now, out int suppressed)
    {
        lock (_gate)
        {
            if (now - _last >= _every || now < _last)
            {
                _last = now;
                suppressed = _suppressed;
                _suppressed = 0;
                return true;
            }

            _suppressed++;
            suppressed = 0;
            return false;
        }
    }
}

/// <summary>The result of one webhook request (the HTTP sender's answer).</summary>
internal readonly struct ChatRelayResult
{
    internal ChatRelayResult(bool ok, int status, double retryAfterSeconds, string error)
    {
        Ok = ok;
        Status = status;
        RetryAfterSeconds = retryAfterSeconds;
        Error = error ?? "";
    }

    internal bool Ok { get; }

    /// <summary>The HTTP status, or 0 when no answer came.</summary>
    internal int Status { get; }

    /// <summary>A <c>429</c>'s wait in seconds, else 0.</summary>
    internal double RetryAfterSeconds { get; }

    /// <summary>Why it failed (never the address).</summary>
    internal string Error { get; }
}

/// <summary>Posts one webhook body. The live sender is an <c>HttpClient</c>; tests use their own.</summary>
internal interface IChatRelaySender : IDisposable
{
    System.Threading.Tasks.Task<ChatRelayResult> PostAsync(string url, string json, System.Threading.CancellationToken cancel);
}

/// <summary>The server's chat switches (Server tab → Chat) mapped to the policy clients read. Game-free.</summary>
internal static class ServerChatPolicy
{
    /// <summary>
    /// The policy from the server's values; <paramref name="fromServer"/> false (no Progenitor server, its values not yet
    /// arrived, or a server older than these rows) answers <see cref="ChatPolicy.Defaults"/>. The length is clamped to
    /// <see cref="ChatPolicy.MinLongMessageChars"/>..<see cref="ChatPolicy.MaxLongMessageCharsCap"/>.
    /// </summary>
    internal static ChatPolicy From(bool fromServer, bool allowLinks, bool allowModLinks, bool allowAtAll, bool allowLongMessages, int maxLongMessageChars)
    {
        if (!fromServer) return ChatPolicy.Defaults;
        return new ChatPolicy
        {
            AllowLinks = allowLinks,
            AllowModLinks = allowModLinks,
            AllowAtAll = allowAtAll,
            AllowLongMessages = allowLongMessages,
            MaxLongMessageChars = ChatPolicy.ClampLongMessage(maxLongMessageChars),
        };
    }

    /// <summary>A text that changes when any field of <paramref name="p"/> does (the client swaps the policy only then).</summary>
    internal static string Fingerprint(ChatPolicy p) =>
        (p.AllowLinks ? "L" : "l") + (p.AllowModLinks ? "M" : "m") + (p.AllowAtAll ? "A" : "a") + (p.AllowLongMessages ? "G" : "g")
        + p.MaxLongMessageChars.ToString(CultureInfo.InvariantCulture);
}
