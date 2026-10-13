using System;
using System.Collections.Generic;
#if !PROGENITOR_TESTS
using Progenitor.Networking;
#endif

namespace Progenitor.Chat;

/// <summary>One server chat command, as the chat box's command hint line shows it.</summary>
internal sealed class ChatCommandHint
{
    public ChatCommandHint(string command, string usage, string help)
    {
        Command = command ?? "";
        Usage = usage ?? "";
        Help = help ?? "";
    }

    /// <summary>The exact text to type, prefix included (<c>.help</c>, <c>.p some.mod tool run</c>).</summary>
    public string Command { get; }

    /// <summary>The usage of every form this player may run, joined with <c> | </c>; may be empty.</summary>
    public string Usage { get; }

    /// <summary>One line of help; may be empty.</summary>
    public string Help { get; }
}

/// <summary>
/// Chat command hints, client side: the joined server's chat commands this player can run, fetched once per connection
/// over the <c>'PrCh'</c> channel (<see cref="ChatCommandHintsWire"/>) and matched against the chat line. The state
/// machine and the matching are game-free; only <see cref="Install"/> and the packet handler touch the game.
///
/// <para>Nothing is ever sent before the server's hello: a server without Progenitor, or with an older Progenitor that
/// does not have the channel, never sends one, so <see cref="Available"/> stays false and there are no hints. After a
/// hello, the first <see cref="RequestIfNeeded"/> asks for the catalog; a later hello with a new stamp (the server's
/// command list changed) makes the next call ask again. Requests are at least <see cref="MinRequestIntervalSeconds"/>
/// apart, time out after <see cref="TimeoutSeconds"/>, and stop after <see cref="MaxAttemptsPerStamp"/> unanswered
/// tries until the next hello. Everything is cleared when the connection ends and when Core unloads. Main thread.</para>
/// </summary>
internal static class ChatCommandHints
{
    internal const double MinRequestIntervalSeconds = 5;
    internal const double TimeoutSeconds = 10;
    internal const int MaxAttemptsPerStamp = 3;

    private static int? _helloStamp;
    private static List<ChatCommandHint>? _catalog;
    private static int _nextNonce;
    private static int _attempts;
    private static double _lastRequestAt = double.NegativeInfinity;
    private static Pending? _pending;

    /// <summary>Sends a request packet to the server; false when it could not be sent. Set by <see cref="Install"/>.</summary>
    internal static Func<byte[], bool>? Send;

    /// <summary>Seconds on a monotonic clock.</summary>
    internal static Func<double> Now = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>True when a catalog from the joined server is held (possibly an empty one).</summary>
    internal static bool Available => _catalog != null;

    /// <summary>True once the joined server said it has the channel.</summary>
    internal static bool ServerHasHints => _helloStamp.HasValue;

    /// <summary>How many commands the held catalog lists.</summary>
    internal static int Count => _catalog?.Count ?? 0;

    /// <summary>
    /// Client: ask the server for the catalog when it has the channel and none is held for its current stamp. Cheap to
    /// call every frame while the chat line starts with the command prefix.
    /// </summary>
    internal static void RequestIfNeeded()
    {
        if (!_helloStamp.HasValue || Send == null) return;
        if (_catalog != null && _catalogStamp == _helloStamp.Value) return;
        double now = Now();
        if (_pending != null)
        {
            if (now < _pending.Deadline) return;
            _pending = null;
        }

        if (_attempts >= MaxAttemptsPerStamp) return;
        if (now - _lastRequestAt < MinRequestIntervalSeconds) return;

        _lastRequestAt = now;
        _attempts++;
        int nonce = ++_nextNonce;
        if (_nextNonce == int.MaxValue) _nextNonce = 0;
        bool sent;
        try { sent = Send(ChatCommandHintsWire.EncodeRequest(nonce)); }
        catch { sent = false; }
        if (sent) _pending = new Pending(nonce, now + TimeoutSeconds);
    }

    private static int _catalogStamp;

    /// <summary>The held catalog's commands that match <paramref name="typed"/>, best first, at most <paramref name="max"/>.</summary>
    internal static IReadOnlyList<ChatCommandHint> Match(string typed, int max) =>
        _catalog == null ? Array.Empty<ChatCommandHint>() : MatchIn(_catalog, typed, max);

    /// <summary>
    /// Game-free matching. The typed line (leading spaces dropped, runs of spaces read as one) matches a command when
    /// <list type="bullet">
    /// <item>the command starts with it, ignoring case (the player is still typing the command); or</item>
    /// <item>it starts with the command and a space (the player is typing that command's arguments).</item>
    /// </list>
    /// Order: commands the line already names (an exact match, or one whose arguments are being typed; the longest,
    /// most specific first), then the rest shortest first, then alphabetically.
    /// </summary>
    internal static List<ChatCommandHint> MatchIn(IReadOnlyList<ChatCommandHint> catalog, string? typed, int max)
    {
        var result = new List<ChatCommandHint>();
        if (catalog == null || max <= 0) return result;
        string line = Normalize(typed);
        if (line.Length == 0) return result;

        var named = new List<ChatCommandHint>();
        var partial = new List<ChatCommandHint>();
        foreach (var hint in catalog)
        {
            if (hint == null || hint.Command.Length == 0) continue;
            string command = hint.Command;
            if (command.StartsWith(line, StringComparison.OrdinalIgnoreCase))
            {
                if (command.Length == line.Length) named.Add(hint);
                else partial.Add(hint);
            }
            else if (line.Length > command.Length && line[command.Length] == ' '
                && line.StartsWith(command, StringComparison.OrdinalIgnoreCase))
            {
                named.Add(hint);
            }
        }

        named.Sort((a, b) =>
        {
            int byLength = b.Command.Length.CompareTo(a.Command.Length);
            return byLength != 0 ? byLength : string.Compare(a.Command, b.Command, StringComparison.OrdinalIgnoreCase);
        });
        partial.Sort((a, b) =>
        {
            int byLength = a.Command.Length.CompareTo(b.Command.Length);
            return byLength != 0 ? byLength : string.Compare(a.Command, b.Command, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var hint in named)
        {
            if (result.Count >= max) return result;
            result.Add(hint);
        }

        foreach (var hint in partial)
        {
            if (result.Count >= max) return result;
            result.Add(hint);
        }

        return result;
    }

    private static string Normalize(string? typed)
    {
        if (string.IsNullOrEmpty(typed)) return "";
        var sb = new System.Text.StringBuilder(typed!.Length);
        bool space = false;
        foreach (char c in typed)
        {
            if (c == ' ' || c == '\t')
            {
                space = sb.Length > 0;
                continue;
            }

            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }

        // A trailing space is kept: ".help " names the command and starts its arguments.
        if (space) sb.Append(' ');
        return sb.ToString();
    }

    /// <summary>A hello from the server: it has the channel, and its command list is at <paramref name="stamp"/>.</summary>
    internal static void OnHello(int stamp)
    {
        if (_helloStamp != stamp) _attempts = 0;
        _helloStamp = stamp;
    }

    /// <summary>
    /// One page of an answer. Pages of a request this client is not waiting for are ignored; a complete set replaces
    /// the held catalog at once (a half-received catalog is never shown).
    /// </summary>
    internal static void OnPage(ChatCommandHintsWire.Page page)
    {
        var pending = _pending;
        if (page == null || pending == null || page.Nonce != pending.Nonce) return;
        if (pending.Pages == null)
        {
            pending.Pages = new List<ChatCommandHint>?[page.Count];
            pending.Stamp = page.Stamp;
        }
        else if (pending.Pages.Length != page.Count || pending.Stamp != page.Stamp)
        {
            _pending = null;
            return;
        }

        if (pending.Pages[page.Index] != null) return;
        pending.Pages[page.Index] = page.Entries;
        pending.Received++;
        if (pending.Received < pending.Pages.Length) return;

        var catalog = new List<ChatCommandHint>();
        foreach (var entries in pending.Pages) catalog.AddRange(entries!);
        _catalog = catalog;
        _catalogStamp = pending.Stamp;
        // The catalog is current as of its own stamp; a later change arrives as a new hello.
        _helloStamp = pending.Stamp;
        _attempts = 0;
        _pending = null;
    }

    /// <summary>Forget the server's channel and catalog (the connection ended, Core unloads, tests).</summary>
    internal static void Reset()
    {
        _helloStamp = null;
        _catalog = null;
        _catalogStamp = 0;
        _pending = null;
        _attempts = 0;
        _lastRequestAt = double.NegativeInfinity;
    }

    private sealed class Pending
    {
        public Pending(int nonce, double deadline)
        {
            Nonce = nonce;
            Deadline = deadline;
        }

        public readonly int Nonce;
        public readonly double Deadline;
        public int Stamp;
        public int Received;
        public List<ChatCommandHint>?[]? Pages;
    }

#if !PROGENITOR_TESTS
    private static bool _installed;

    /// <summary>Client: register the reply handler and the disconnect reset.</summary>
    internal static unsafe void Install()
    {
        if (_installed || Plugin.IsServerProcess) return;
        _installed = true;
        Reset();
        try { CustomEvents.OnReceive(CustomEventPolicy.ChatCommandHintsEventId, OnPacket); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ChatCommandHints: the client channel could not be registered: " + e.Message); }
        Send = bytes => CustomEvents.SendToServer(CustomEventPolicy.ChatCommandHintsEventId, new ReadOnlySpan<byte>(bytes));
        Handshake.ServerContextCleared += OnContextCleared;
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        Handshake.ServerContextCleared -= OnContextCleared;
        try { CustomEvents.RemoveHandler(CustomEventPolicy.ChatCommandHintsEventId); } catch { /* teardown */ }
        Send = null;
        Reset();
    }

    private static void OnContextCleared(string reason) => Reset();

    private static unsafe void OnPacket(int _, byte* payload, int length)
    {
        if (payload == null || length <= 0) return;
        var span = new ReadOnlySpan<byte>(payload, length);
        try
        {
            switch (ChatCommandHintsWire.KindOf(span))
            {
                case ChatCommandHintsWire.KindHello:
                    if (ChatCommandHintsWire.TryDecodeHello(span, out int stamp)) OnHello(stamp);
                    break;
                case ChatCommandHintsWire.KindPage:
                    if (ChatCommandHintsWire.TryDecodePage(span, out var page)) OnPage(page);
                    break;
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ChatCommandHints: packet threw: " + e.Message); }
    }
#endif
}
