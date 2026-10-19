using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Progenitor.Bootstrap;

/// <summary>
/// The game-free half of the queued console (<c>QueuedConsoleLogListener</c>): a bounded, thread-safe line queue
/// between the thread that logs (usually the game's main thread) and the one background thread that writes to the
/// BepInEx console window.
///
/// <para>BepInEx's own console listener writes each line synchronously: two or three console calls to set and reset the
/// color plus one write, on the thread that logged. When the console host is slow to take output (a busy machine, a
/// terminal that redraws), every log line from every mod stalls the server frame for as long as the console needs,
/// and longer lines cost more. Here the logging thread only formats the line and adds it to this queue.</para>
///
/// <para>Bounds: at most <see cref="MaxLines"/> lines and <see cref="MaxChars"/> characters wait; a line that does not fit
/// is dropped and counted, and the writer prints one "dropped" notice before the next line it writes. A line longer
/// than <see cref="MaxLineChars"/> is shortened for the console only; the disk log and the bridge ring keep it whole.
/// The writer takes at most <see cref="BatchLines"/> lines or <see cref="BatchChars"/> characters per batch, and joins
/// consecutive lines of one color into a single write.</para>
/// </summary>
internal sealed class ConsoleLogQueue
{
    internal const int DefaultMaxLines = 4000;
    internal const int DefaultMaxChars = 1024 * 1024;
    internal const int DefaultMaxLineChars = 2000;
    internal const int DefaultBatchLines = 256;
    internal const int DefaultBatchChars = 64 * 1024;

    internal readonly struct Line
    {
        public Line(ConsoleColor color, string text) { Color = color; Text = text; }
        public ConsoleColor Color { get; }
        public string Text { get; }
    }

    /// <summary>One run of same-colored lines, joined with newlines, for a single console write.</summary>
    internal readonly struct Run
    {
        public Run(ConsoleColor color, string text, int lines) { Color = color; Text = text; Lines = lines; }
        public ConsoleColor Color { get; }
        public string Text { get; }
        public int Lines { get; }
    }

    /// <summary>Counters since the queue was made. All are totals; a reader subtracts two snapshots for a window.</summary>
    internal struct Stats
    {
        public long Enqueued;
        public long Dropped;
        public long Truncated;
        public long Written;
        public long WriteTicks;
        public long MaxWriteTicks;
        public long EnqueueTicks;
        public long MaxEnqueueTicks;
        public long MaxPending;
        public long Pending;
    }

    private readonly object _gate = new();
    private readonly Queue<Line> _lines = new();
    private long _chars;
    private long _droppedSinceNotice;
    private Stats _stats;
    private readonly AutoResetEvent _signal = new(false);

    internal ConsoleLogQueue(
        int maxLines = DefaultMaxLines, int maxChars = DefaultMaxChars, int maxLineChars = DefaultMaxLineChars,
        int batchLines = DefaultBatchLines, int batchChars = DefaultBatchChars)
    {
        MaxLines = Math.Max(1, maxLines);
        MaxChars = Math.Max(1, maxChars);
        MaxLineChars = Math.Max(16, maxLineChars);
        BatchLines = Math.Max(1, batchLines);
        BatchChars = Math.Max(1, batchChars);
    }

    internal int MaxLines { get; }
    internal int MaxChars { get; }
    internal int MaxLineChars { get; }
    internal int BatchLines { get; }
    internal int BatchChars { get; }

    /// <summary>Lines waiting for the writer.</summary>
    internal int Pending
    {
        get { lock (_gate) return _lines.Count; }
    }

    /// <summary>
    /// The console text of one log line, exactly as BepInEx's console listener prints it
    /// (<c>[Level  :    Source] data</c>), with the data shortened to <paramref name="maxLineChars"/> characters.
    /// </summary>
    internal static string Format(string level, string source, string? data, int maxLineChars, out bool truncated)
    {
        string body = Shorten(data ?? "", maxLineChars, out truncated);
        return "[" + (level ?? "").PadRight(7) + ":" + (source ?? "").PadLeft(10) + "] " + body;
    }

    /// <summary>
    /// <paramref name="text"/> cut to <paramref name="maxChars"/> characters plus a note of how much was left out.
    /// Never splits a surrogate pair.
    /// </summary>
    internal static string Shorten(string text, int maxChars, out bool truncated)
    {
        truncated = false;
        if (text == null) return "";
        if (maxChars <= 0 || text.Length <= maxChars) return text;
        int cut = maxChars;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        truncated = true;
        return text.Substring(0, cut) + TruncatedSuffix(text.Length - cut);
    }

    internal static string TruncatedSuffix(int omitted) =>
        " ... [" + omitted.ToString(CultureInfo.InvariantCulture) + " more characters; the whole line is in LogOutput.log]";

    internal static string DroppedNotice(long dropped) =>
        "[Progenitor: " + dropped.ToString(CultureInfo.InvariantCulture)
        + " console line(s) dropped because the console could not keep up; they are in LogOutput.log]";

    /// <summary>
    /// Add a formatted line. False when the queue is full: the line is dropped (the disk log still has it) and counted.
    /// Cheap and allocation-free apart from the queue's own growth; safe from any thread.
    /// </summary>
    internal bool Enqueue(ConsoleColor color, string text, bool truncated = false, long formatTicks = 0)
    {
        long start = Stopwatch.GetTimestamp();
        text ??= "";
        bool added;
        lock (_gate)
        {
            if (_lines.Count >= MaxLines || _chars + text.Length > MaxChars)
            {
                _stats.Dropped++;
                _droppedSinceNotice++;
                added = false;
            }
            else
            {
                _lines.Enqueue(new Line(color, text));
                _chars += text.Length;
                _stats.Enqueued++;
                if (truncated) _stats.Truncated++;
                if (_lines.Count > _stats.MaxPending) _stats.MaxPending = _lines.Count;
                added = true;
            }

            long ticks = Stopwatch.GetTimestamp() - start + Math.Max(0, formatTicks);
            _stats.EnqueueTicks += ticks;
            if (ticks > _stats.MaxEnqueueTicks) _stats.MaxEnqueueTicks = ticks;
        }

        if (added) _signal.Set();
        return added;
    }

    /// <summary>Wait until a line is queued or <paramref name="timeoutMs"/> passes. True when there is work.</summary>
    internal bool WaitForWork(int timeoutMs)
    {
        if (Pending > 0 || DroppedSinceNotice > 0) return true;
        _signal.WaitOne(timeoutMs);
        return Pending > 0 || DroppedSinceNotice > 0;
    }

    /// <summary>Wake the writer (shutdown).</summary>
    internal void Wake() => _signal.Set();

    private long DroppedSinceNotice
    {
        get { lock (_gate) return _droppedSinceNotice; }
    }

    /// <summary>
    /// Take up to one batch and join it into same-color runs. A pending "dropped" notice comes first, in
    /// <paramref name="noticeColor"/>. Empty when nothing waits.
    /// </summary>
    internal List<Run> TakeBatch(ConsoleColor noticeColor = ConsoleColor.Yellow)
    {
        var lines = new List<Line>();
        long dropped;
        lock (_gate)
        {
            dropped = _droppedSinceNotice;
            _droppedSinceNotice = 0;
            int chars = 0;
            while (_lines.Count > 0 && lines.Count < BatchLines)
            {
                var next = _lines.Peek();
                if (lines.Count > 0 && chars + next.Text.Length > BatchChars) break;
                _lines.Dequeue();
                _chars -= next.Text.Length;
                chars += next.Text.Length;
                lines.Add(next);
            }
        }

        var runs = new List<Run>();
        if (dropped > 0) runs.Add(new Run(noticeColor, DroppedNotice(dropped) + Environment.NewLine, 0));
        Join(lines, runs);
        return runs;
    }

    /// <summary>Consecutive lines of one color become one run; each line ends with a newline.</summary>
    internal static void Join(List<Line> lines, List<Run> into)
    {
        int i = 0;
        var sb = new StringBuilder();
        while (i < lines.Count)
        {
            var color = lines[i].Color;
            sb.Clear();
            int n = 0;
            while (i < lines.Count && lines[i].Color == color)
            {
                sb.Append(lines[i].Text).Append(Environment.NewLine);
                i++;
                n++;
            }
            into.Add(new Run(color, sb.ToString(), n));
        }
    }

    /// <summary>The writer finished a batch of <paramref name="lines"/> lines in <paramref name="ticks"/> stopwatch ticks.</summary>
    internal void NoteWritten(int lines, long ticks)
    {
        lock (_gate)
        {
            _stats.Written += Math.Max(0, lines);
            if (ticks > 0)
            {
                _stats.WriteTicks += ticks;
                if (ticks > _stats.MaxWriteTicks) _stats.MaxWriteTicks = ticks;
            }
        }
    }

    /// <summary>
    /// The counters now. With <paramref name="resetMaxima"/> the maxima (write, enqueue, pending) start again, so one
    /// reader that snapshots once per window (the hitch summary) sees that window's maxima.
    /// </summary>
    internal Stats Snapshot(bool resetMaxima = false)
    {
        lock (_gate)
        {
            var s = _stats;
            s.Pending = _lines.Count;
            if (resetMaxima)
            {
                _stats.MaxWriteTicks = 0;
                _stats.MaxEnqueueTicks = 0;
                _stats.MaxPending = _lines.Count;
            }
            return s;
        }
    }

    /// <summary>
    /// Flat copy for Core, which reads it by reflection and must not share a type with Bootstrap:
    /// enqueued, dropped, truncated, written, writeTicks, maxWriteTicks, enqueueTicks, maxEnqueueTicks, maxPending,
    /// pending, Stopwatch.Frequency.
    /// </summary>
    internal static long[] ToArray(Stats s) => new[]
    {
        s.Enqueued, s.Dropped, s.Truncated, s.Written, s.WriteTicks, s.MaxWriteTicks,
        s.EnqueueTicks, s.MaxEnqueueTicks, s.MaxPending, s.Pending, Stopwatch.Frequency,
    };
}
