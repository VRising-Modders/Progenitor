using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Progenitor.Ui;

/// <summary>
/// HOSTS1 round 4 (user report 2026-09-30: "it takes a second to switch" from System to Mods, with a
/// 364-508 ms hitch on every open, almost all outside the Core tick). One open is timed from the moment
/// the workspace starts to open from closed until it is first shown, with named spans for the work
/// inside (the backdrop build and its texture, the index, the strip and its tab donors, the list, the
/// content pane, the artwork donors, the first layout). One summary line per open:
/// <c>Mods workspace: open timing total=…ms frames=… spans=[name=ms …] unaccounted=…ms</c>, where
/// <c>unaccounted</c> is the open's wall time the spans do not cover (Unity's own canvas and mesh
/// rebuilds on the frames in between). Readback: <c>mods_ui status.openTiming</c>. Game-free: the clock
/// and the frame counter are seams.
/// </summary>
internal static class WorkspaceOpenTiming
{
    internal const string LogPrefix = "Mods workspace: open timing ";

    private static readonly object Gate = new();
    private static readonly List<(string Name, double Ms)> Spans = new();
    private static readonly List<(string Name, double Ms, int Frames)> Phases = new();
    private static long _phaseTicks;
    private static int _phaseFrame;
    private static long _startTicks;
    private static int _startFrame;
    private static bool _running;

    /// <summary>The high-resolution clock (Stopwatch ticks); a seam for tests.</summary>
    internal static Func<long> Ticks { get; set; } = Stopwatch.GetTimestamp;

    /// <summary>Ticks per second of <see cref="Ticks"/>.</summary>
    internal static long Frequency { get; set; } = Stopwatch.Frequency;

    /// <summary>The frame counter; set by the runtime (Time.frameCount), 0 in tests.</summary>
    internal static Func<int> Frame { get; set; } = () => 0;

    /// <summary>The last summary line (empty before the first).</summary>
    internal static string LastLine { get; private set; } = "";

    internal static double LastTotalMs { get; private set; }

    internal static bool Running
    {
        get { lock (Gate) return _running; }
    }

    /// <summary>An open from closed starts: the clock runs until <see cref="Finish"/>.</summary>
    internal static void Begin()
    {
        lock (Gate)
        {
            Spans.Clear();
            Phases.Clear();
            Detail.Clear();
            _startTicks = SafeTicks();
            _startFrame = SafeFrame();
            _phaseTicks = _startTicks;
            _phaseFrame = _startFrame;
            _running = true;
        }
    }

    private static int _suppress;

    /// <summary>
    /// Round 10: a readback (the bridge's status read during an open) is recorded as one span under
    /// <paramref name="name"/>; the spans inside it are not added to the open's own (round 9's entry-facts
    /// 554 ms against a 190 ms list build was mostly the after-open status read deciding every row again).
    /// </summary>
    internal static T Readback<T>(string name, Func<T> work)
    {
        if (!Running) return work();
        long start = SafeTicks();
        lock (Gate) _suppress++;
        try { return work(); }
        finally
        {
            lock (Gate) _suppress--;
            Add(name, start);
        }
    }

    /// <summary>Time one named piece of the open; outside an open it runs the work untimed.</summary>
    internal static void Span(string name, Action work)
    {
        if (!Running)
        {
            work();
            return;
        }

        long start = SafeTicks();
        try { work(); }
        finally { Add(name, start); }
    }

    /// <summary>Time one named piece of the open that returns a value.</summary>
    internal static T Span<T>(string name, Func<T> work)
    {
        if (!Running) return work();
        long start = SafeTicks();
        try { return work(); }
        finally { Add(name, start); }
    }

    /// <summary>
    /// Round 11: also record the last span under <paramref name="name"/> (a per-pane copy of show-layout,
    /// for example); not counted twice in <c>unaccounted</c>.
    /// </summary>
    internal static void NoteLast(string name)
    {
        lock (Gate)
        {
            if (!_running || _suppress > 0 || Spans.Count == 0) return;
            Detail.Add((name, Spans[Spans.Count - 1].Ms));
        }
    }

    private static readonly List<(string Name, double Ms)> Detail = new();

    /// <summary>Round 11: a measured detail (nested in a span, so outside the span sums).</summary>
    internal static void AddDetail(string name, long startTicks)
    {
        long now = SafeTicks();
        lock (Gate)
        {
            if (!_running || _suppress > 0) return;
            Detail.Add((name, Ms(now - startTicks)));
        }
    }

    /// <summary>Add a piece measured by the caller from <paramref name="startTicks"/> to now.</summary>
    internal static void Add(string name, long startTicks)
    {
        long now = SafeTicks();
        lock (Gate)
        {
            if (!_running || _suppress > 0) return;
            Spans.Add((name, Ms(now - startTicks)));
        }
    }

    /// <summary>
    /// Round 6: end a phase of the open (wall time and frames since the previous phase or the start), once
    /// per name per open. The phases cover the whole open, so the time the spans miss is placed: the rest of
    /// the synchronous open call, Unity's end of frame and render before our next tick, and the frames until
    /// the workspace is shown.
    /// </summary>
    internal static void Phase(string name)
    {
        long now = SafeTicks();
        int frame = SafeFrame();
        lock (Gate)
        {
            if (!_running) return;
            foreach (var p in Phases) if (string.Equals(p.Name, name, StringComparison.Ordinal)) return;
            Phases.Add((name, Ms(now - _phaseTicks), Math.Max(0, frame - _phaseFrame)));
            _phaseTicks = now;
            _phaseFrame = frame;
        }
    }

    /// <summary>The workspace is shown: the summary line, or null when no open was being timed.</summary>
    internal static string? Finish(string why)
    {
        long now = SafeTicks();
        int frame = SafeFrame();
        lock (Gate)
        {
            if (!_running) return null;
            _running = false;
            double total = Ms(now - _startTicks);
            double covered = 0;
            var sb = new StringBuilder();
            sb.Append(LogPrefix).Append("total=").Append(Fmt(total)).Append("ms frames=").Append(Math.Max(0, frame - _startFrame));
            sb.Append(" spans=[");
            // Same-named spans are summed, in first-seen order.
            var order = new List<string>();
            var sums = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (name, ms) in Spans)
            {
                if (!sums.ContainsKey(name)) { order.Add(name); sums[name] = 0; }
                sums[name] += ms;
            }

            for (int i = 0; i < order.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(order[i]).Append('=').Append(Fmt(sums[order[i]]));
                covered += sums[order[i]];
            }

            sb.Append("] unaccounted=").Append(Fmt(Math.Max(0, total - covered))).Append("ms");
            if (Detail.Count > 0)
            {
                // Round 11: per-pane shows (already inside show-layout).
                var dorder = new List<string>();
                var dsums = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var (name, ms) in Detail)
                {
                    if (!dsums.ContainsKey(name)) { dorder.Add(name); dsums[name] = 0; }
                    dsums[name] += ms;
                }

                sb.Append(" detail=[");
                for (int i = 0; i < dorder.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(dorder[i]).Append('=').Append(Fmt(dsums[dorder[i]]));
                }

                sb.Append(']');
            }
            if (Phases.Count > 0)
            {
                // The last phase (to the show) is closed here.
                Phases.Add(("to-show", Ms(now - _phaseTicks), Math.Max(0, frame - _phaseFrame)));
                sb.Append(" phases=[");
                for (int i = 0; i < Phases.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(Phases[i].Name).Append('=').Append(Fmt(Phases[i].Ms)).Append('/').Append(Phases[i].Frames).Append('f');
                }

                sb.Append(']');
            }

            sb.Append(" end=").Append(why ?? "");
            LastLine = sb.ToString();
            LastTotalMs = total;
            return LastLine;
        }
    }

    /// <summary>Stop without a line (the open failed or was canceled).</summary>
    internal static void Cancel()
    {
        lock (Gate) _running = false;
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Spans.Clear();
            Phases.Clear();
            Detail.Clear();
            _running = false;
            _suppress = 0;
        }

        LastLine = "";
        LastTotalMs = 0;
        Ticks = Stopwatch.GetTimestamp;
        Frequency = Stopwatch.Frequency;
        Frame = () => 0;
    }

    private static double Ms(long ticks) => Frequency > 0 ? ticks * 1000.0 / Frequency : 0;

    private static string Fmt(double ms) => Math.Round(ms, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static long SafeTicks()
    {
        try { return Ticks(); }
        catch { return 0; }
    }

    private static int SafeFrame()
    {
        try { return Frame(); }
        catch { return 0; }
    }
}
