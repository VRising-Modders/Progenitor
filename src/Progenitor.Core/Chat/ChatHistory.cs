using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Chat;

/// <summary>
/// One saved chat line as plain values, copied from a <see cref="ChatRecord"/> on the main thread so the history file
/// can be written on a background thread while the log keeps changing. Game-free.
/// </summary>
internal readonly struct ChatHistoryLine
{
    internal ChatHistoryLine(DateTime timeUtc, ChatKind kind, bool fromSelf, string user, string text, string full, int repeat,
        ChatLinkSource source)
    {
        TimeUtc = timeUtc;
        Kind = kind;
        FromSelf = fromSelf;
        User = user ?? "";
        Text = text ?? "";
        Full = full ?? "";
        Repeat = repeat;
        Source = source;
    }

    internal DateTime TimeUtc { get; }
    internal ChatKind Kind { get; }
    internal bool FromSelf { get; }
    internal string User { get; }
    internal string Text { get; }
    internal string Full { get; }
    internal int Repeat { get; }
    internal ChatLinkSource Source { get; }

    internal static ChatHistoryLine From(ChatRecord r) =>
        new(r.TimeUtc, r.Kind, r.FromSelf, r.User, r.Text, r.Full, r.Repeat, r.Source);
}

/// <summary>
/// The per-server history file (<c>progenitor-cache/chat/&lt;server&gt;.jsonl</c>): one JSON object per line, keys
/// <c>t</c> (time, ISO 8601 UTC), <c>k</c> (kind), <c>s</c> (sent by this player), <c>u</c> (speaker), <c>x</c>
/// (text), <c>f</c> (the game's formatted line), <c>n</c> (repeat count), <c>p</c> (source). Game-free.
///
/// <para><b>Writing.</b> Streamed with one reused JSON writer (no per-line dictionary or string), into
/// <c>&lt;file&gt;.tmp</c>, then moved over the file in one step, so a crash or a full disk mid-write leaves the
/// previous file whole.</para>
/// </summary>
internal static class ChatHistoryFile
{
    private static readonly byte[] NewLine = { (byte)'\n' };

    internal static void Write(Stream stream, IReadOnlyList<ChatHistoryLine> lines)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (lines == null || lines.Count == 0) return;
        using var w = new Utf8JsonWriter(stream);
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            w.WriteStartObject();
            w.WriteString("t", l.TimeUtc.ToString("o", CultureInfo.InvariantCulture));
            w.WriteNumber("k", (int)l.Kind);
            w.WriteBoolean("s", l.FromSelf);
            w.WriteString("u", l.User);
            w.WriteString("x", l.Text);
            w.WriteString("f", l.Full);
            w.WriteNumber("n", l.Repeat);
            w.WriteNumber("p", (int)l.Source);
            w.WriteEndObject();
            w.Flush();
            stream.Write(NewLine, 0, 1);
            w.Reset(stream);
        }
    }

    /// <summary>The lines as the file's text.</summary>
    internal static string ToText(IReadOnlyList<ChatHistoryLine> lines)
    {
        using var ms = new MemoryStream();
        Write(ms, lines);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>Write <paramref name="lines"/> to <c>path.tmp</c>, then replace <paramref name="path"/> with it in one move.</summary>
    internal static void WriteAtomic(string path, IReadOnlyList<ChatHistoryLine> lines)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("A path is required.", nameof(path));
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            Write(fs, lines);
            // On disk before the move, so a crash right after it never leaves an empty or partial file in its place.
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// When the chat history is saved, game-free: a few seconds after chat goes quiet (<see cref="QuietSeconds"/>), and at
/// least every <see cref="MaxDelaySeconds"/> while it keeps going, never on every line. Leaving the server and a Core
/// unload save at once. Times are seconds on any steady clock.
/// </summary>
internal sealed class ChatSaveSchedule
{
    internal const double QuietSeconds = 5;
    internal const double MaxDelaySeconds = 30;

    private double _firstDirty = double.NaN;
    private double _lastDirty;

    internal bool Dirty => !double.IsNaN(_firstDirty);

    internal void MarkDirty(double now)
    {
        if (!Dirty) _firstDirty = now;
        _lastDirty = now;
    }

    internal bool Due(double now) =>
        Dirty && (now - _lastDirty >= QuietSeconds || now - _firstDirty >= MaxDelaySeconds);

    internal void Saved() => _firstDirty = double.NaN;
}

/// <summary>
/// Which server's history file the chat log belongs to, game-free. A world's join opens it (<see cref="Open"/>, the
/// server's host); leaving the server closes it (<see cref="Close"/>) and that leave is the one that writes the file.
/// After the close nothing writes it again: not the next world's load, not a Core unload at the title, so a history file
/// changed or restored while at the title is what the next join reads. A Core swap in a world writes without closing
/// (<see cref="SaveHost"/>), so the next generation continues the same session. A join that reuses the world it left
/// (the client took a new connection since the close, <see cref="RejoinedAt"/>) starts a new log.
/// </summary>
internal sealed class ChatHistorySession
{
    /// <summary>This world's history was opened (read or found missing); the next world opens it again.</summary>
    internal bool Opened { get; private set; }

    /// <summary>Closed by a leave: the log is the previous server's and is never written again.</summary>
    internal bool Closed { get; private set; }

    /// <summary>The server's host the history is keyed by; empty when there is none (a local game, or no history kept).</summary>
    internal string Host { get; private set; } = "";

    /// <summary>The host whose file a save writes now, or empty: nothing is written before the open or after the close.</summary>
    internal string SaveHost => Opened && !Closed ? Host : "";

    /// <summary>
    /// Open the history when it may be: only while the client holds a connection (connecting or connected) and the
    /// server's host is known. At the title or the disconnect screen a surviving client world still remembers the last
    /// server (a Core swap there must not reopen that server's file under its host), and a join whose host is not known
    /// yet would open with none and never save; both wait. True when it opened now.
    /// </summary>
    internal bool TryOpen(bool connectionHeld, string? host)
    {
        if (Opened || !MayOpen(connectionHeld, host)) return false;
        Open(host);
        return true;
    }

    /// <summary>Whether a history may open: a held connection to a server whose host is known.</summary>
    internal static bool MayOpen(bool connectionHeld, string? host) => connectionHeld && !string.IsNullOrWhiteSpace(host);

    internal void Open(string? host)
    {
        Opened = true;
        Closed = false;
        Host = host ?? "";
    }

    /// <summary>The client's connection serial when the history was closed.</summary>
    internal int ClosedSerial { get; private set; }

    /// <summary>
    /// Leaving the server: the host to write one last time (empty when nothing is open or it was already closed), then
    /// closed. <paramref name="serial"/> is the serial of the client's connection that ended; a second notice of the same
    /// leave (the connection's error, then the world ending) writes nothing.
    /// </summary>
    internal string Close(int serial = 0)
    {
        string host = SaveHost;
        if (Opened)
        {
            Closed = true;
            ClosedSerial = serial;
        }

        return host;
    }

    /// <summary>The history was closed by a leave and the client has taken a new connection since.</summary>
    internal bool RejoinedAt(int serial) => Closed && serial != ClosedSerial;

    /// <summary>A new world (or an unload): nothing open until the next <see cref="Open"/>.</summary>
    internal void Reset()
    {
        Opened = false;
        Closed = false;
        ClosedSerial = 0;
        Host = "";
    }
}

/// <summary>
/// <c>[Chat] HistoryLines</c>'s default was raised from 500 to 5000 on 2026-10-17, game-free. BepInEx writes a default
/// into the file the first time it is bound, so a saved 500 cannot tell the old default from a chosen 500: once, when
/// <c>[Chat] HistoryDefaultRaised</c> is not yet set, a saved 500 becomes 5000; any other saved value is kept. The
/// marker is then set, so a 500 chosen afterwards stays.
/// </summary>
internal static class ChatHistorySetting
{
    internal const int OldDefault = 500;
    internal const string RaisedKey = "HistoryDefaultRaised";

    /// <summary>The value to keep: the new default for an untouched old default, else the saved value.</summary>
    internal static int Upgrade(int saved, bool alreadyRaised) =>
        !alreadyRaised && saved == OldDefault ? ChatLog.DefaultCapacity : saved;
}

/// <summary>
/// Which part of a tab's lines the chat view draws, game-free. The view draws at most <see cref="Size"/> lines; reading
/// further back pages the window in steps of <see cref="Page"/> lines, so the whole history can be read while only one
/// window of lines is ever built as UI.
///
/// <para><b>Following</b> (the default) shows the newest lines and takes each new one. Scrolling up away from the
/// bottom <see cref="Pin"/>s the window to the newest line drawn, so new lines no longer move what is being read.
/// Reaching the top pages back (<see cref="PageBack"/>); reaching the bottom of an older window jumps straight to the
/// newest lines (<see cref="Follow"/>), however far back it was. Switching tabs follows again.</para>
/// </summary>
internal sealed class ChatScrollWindow
{
    internal ChatScrollWindow(int size, int page)
    {
        Size = Math.Max(1, size);
        Page = Math.Clamp(page, 1, Math.Max(1, Size - 1));
    }

    internal int Size { get; }
    internal int Page { get; }

    /// <summary>The line number of the newest line drawn while pinned; null while following the newest line.</summary>
    internal long? EndSeq { get; private set; }

    internal bool Following => EndSeq == null;

    internal void Follow() => EndSeq = null;

    internal void Pin(long seq) => EndSeq = seq;

    /// <summary>The first index and count of the lines drawn, in <paramref name="lines"/> (oldest first).</summary>
    internal (int Start, int Count) Resolve(IReadOnlyList<ChatRecord> lines)
    {
        int n = lines.Count;
        if (n == 0) return (0, 0);
        int end;
        if (EndSeq == null) end = n - 1;
        else
        {
            end = ChatLog.IndexAtOrBefore(lines, EndSeq.Value);
            // The pinned line and everything before it were dropped (capacity): the oldest window left.
            if (end < 0) end = Math.Min(n, Size) - 1;
        }

        int count = Math.Min(Size, end + 1);
        return (end + 1 - count, count);
    }

    /// <summary>Older lines exist: move the window back by up to a page. False when it already starts at the oldest line.</summary>
    internal bool PageBack(IReadOnlyList<ChatRecord> lines)
    {
        var (start, count) = Resolve(lines);
        if (start <= 0 || count == 0) return false;
        int end = start + count - 1 - Math.Min(Page, start);
        EndSeq = lines[end].Seq;
        return true;
    }
}

/// <summary>
/// The fewest sibling moves that put the chat view's line objects in a new order, game-free. A new line usually reuses
/// the slot of the line that scrolled off the top: one move to the end, instead of setting every line's text and
/// index again. Each move is (item, index) with the engine's final-index meaning (the item ends at that index).
/// </summary>
internal static class ChatSlotOrder
{
    /// <summary>
    /// The moves that turn <paramref name="current"/> into <paramref name="wanted"/> (the same items in another order),
    /// applied to <paramref name="current"/> as they are planned. Items kept in place are a longest increasing run of
    /// their old positions; every other item moves once, right after its new predecessor.
    /// </summary>
    internal static List<(T Item, int Index)> Plan<T>(List<T> current, IReadOnlyList<T> wanted) where T : class
    {
        var moves = new List<(T, int)>();
        int n = wanted.Count;
        if (n == 0) return moves;
        var position = new Dictionary<T, int>(current.Count, ReferenceEqualityComparer.Instance as IEqualityComparer<T>);
        for (int i = 0; i < current.Count; i++) position[current[i]] = i;
        var pos = new int[n];
        for (int i = 0; i < n; i++)
            pos[i] = position.TryGetValue(wanted[i], out int p) ? p : throw new ArgumentException("An item is not in the current order.");

        bool[] keep = LongestIncreasing(pos);
        for (int i = 0; i < n; i++)
        {
            if (keep[i]) continue;
            var item = wanted[i];
            current.Remove(item);
            int at = i == 0 ? 0 : IndexOf(current, wanted[i - 1]) + 1;
            current.Insert(at, item);
            moves.Add((item, at));
        }

        return moves;
    }

    private static int IndexOf<T>(List<T> list, T item) where T : class
    {
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], item)) return i;
        return -1;
    }

    /// <summary>Marks one longest strictly increasing subsequence of <paramref name="a"/> (patience sorting, n log n).</summary>
    private static bool[] LongestIncreasing(int[] a)
    {
        int n = a.Length;
        var tails = new int[n];
        var prev = new int[n];
        int len = 0;
        for (int i = 0; i < n; i++)
        {
            int lo = 0, hi = len;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (a[tails[mid]] < a[i]) lo = mid + 1;
                else hi = mid;
            }

            prev[i] = lo > 0 ? tails[lo - 1] : -1;
            tails[lo] = i;
            if (lo == len) len++;
        }

        var keep = new bool[n];
        for (int k = len > 0 ? tails[len - 1] : -1; k >= 0; k = prev[k]) keep[k] = true;
        return keep;
    }
}

/// <summary>
/// The join-time history read and the lines that arrive during it, game-free. While the file is read on a background
/// thread (<see cref="Begin"/>), new lines are held (<see cref="TryHold"/>); <see cref="Finish"/> then puts, in order,
/// the restored lines, the <c>Joined</c> divider (unless the saved history continues this session) and the held lines.
///
/// <para><b>Same session after a Core swap.</b> History saved by an unload ends in a <c>Joined</c> divider with no
/// <c>Left</c> after it: the session goes on, no new divider is added, and the game's own lines read again at attach
/// (<c>fromAttach</c>) that are already among the last <see cref="TailLines"/> saved lines are skipped
/// (<see cref="IsDuplicate"/>).</para>
/// </summary>
internal sealed class ChatRestore
{
    internal const int TailLines = 60;

    private readonly HashSet<string> _tail = new(StringComparer.Ordinal);
    private readonly List<(ChatRecord Record, bool FromAttach)> _held = new();

    /// <summary>The history is being read; lines are held.</summary>
    internal bool Reading { get; private set; }

    /// <summary>The saved history continues this session (no new divider; attach lines deduplicated).</summary>
    internal bool Continued { get; private set; }

    internal int Held => _held.Count;

    internal void Begin() => Reading = true;

    /// <summary>Hold a line while the history is read; false when nothing is being read (commit it now).</summary>
    internal bool TryHold(ChatRecord record, bool fromAttach)
    {
        if (!Reading) return false;
        _held.Add((record, fromAttach));
        return true;
    }

    /// <summary>A line read again at attach that the restored history already has.</summary>
    internal bool IsDuplicate(ChatRecord record, bool fromAttach) =>
        fromAttach && Continued && _tail.Contains(Key(record.Kind, record.User, record.Text));

    /// <summary>
    /// Apply the read (<paramref name="restored"/> null or empty: none), then the divider, then the held lines through
    /// <paramref name="commit"/> (which may skip one, <see cref="IsDuplicate"/>).
    /// </summary>
    internal void Finish(ChatLog log, List<ChatRecord>? restored, string serverName, DateTime localNow, DateTime utcNow,
        Action<ChatRecord, bool> commit)
    {
        Reading = false;
        if (restored != null && restored.Count > 0)
        {
            _tail.Clear();
            for (int i = Math.Max(0, restored.Count - TailLines); i < restored.Count; i++)
                if (restored[i].Kind != ChatKind.Session) _tail.Add(Key(restored[i].Kind, restored[i].User, restored[i].Text));
            Continued = LastDividerIsJoined(restored);
            log.Restore(restored);
        }

        if (!Continued) log.AddMarker(ChatLog.JoinedText(serverName, localNow), utcNow);
        var held = _held.ToArray();
        _held.Clear();
        foreach (var (record, fromAttach) in held) commit(record, fromAttach);
    }

    /// <summary>A read that will not be applied: drop it and the held lines.</summary>
    internal void Abandon()
    {
        Reading = false;
        _held.Clear();
    }

    /// <summary>A new server: forget everything.</summary>
    internal void Reset()
    {
        Abandon();
        Continued = false;
        _tail.Clear();
    }

    private static string Key(ChatKind kind, string user, string text) => ((int)kind) + "|" + user + "|" + text;

    private static bool LastDividerIsJoined(List<ChatRecord> restored)
    {
        for (int i = restored.Count - 1; i >= 0; i--)
        {
            if (restored[i].Kind != ChatKind.Session) continue;
            return restored[i].Text.StartsWith("Joined ", StringComparison.Ordinal);
        }

        return false;
    }
}
