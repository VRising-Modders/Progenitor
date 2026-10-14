using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>
/// Bounded Tick/Relayout stopwatch. Off until a bridge op sets
/// <see cref="Dev.MainThread.UiTickTimingEnabled"/>. Logs at most one Info line per 5 s
/// when a named pass exceeds 50 ms.
/// </summary>
internal static class UiTickTiming
{
    internal const int ThresholdMs = 50;
    internal const int RateLimitMs = 5000;

    internal static bool Enabled
    {
        get => Dev.MainThread.UiTickTimingEnabled;
        set => Dev.MainThread.UiTickTimingEnabled = value;
    }

    internal static Func<long> NowMs = () => Environment.TickCount64;
    internal static Action<string>? LogSink;

    private static long _lastLogMs = long.MinValue;

    internal static bool ShouldLog(bool enabled, long elapsedMs, long nowMs, long lastLogMs) =>
        enabled && elapsedMs > ThresholdMs && (lastLogMs == long.MinValue || nowMs - lastLogMs >= RateLimitMs);

    internal static string Format(string name, long elapsedMs) =>
        "ui tick name=" + name + " ms=" + elapsedMs;

    internal static void Finish(string name, long startTimestamp)
    {
        if (!Enabled) return;
        long elapsedMs = (Stopwatch.GetTimestamp() - startTimestamp) * 1000 / Stopwatch.Frequency;
        long now = NowMs();
        if (!ShouldLog(true, elapsedMs, now, _lastLogMs)) return;
        _lastLogMs = now;
        string line = Format(name, elapsedMs);
        try { LogSink?.Invoke(line); }
        catch { }
        try { Plugin.Logger?.LogInfo(line); }
        catch { }
    }

    internal static void ResetForTests()
    {
        Enabled = false;
        _lastLogMs = long.MinValue;
        NowMs = () => Environment.TickCount64;
        LogSink = null;
    }
}

/// <summary>
/// Always-on, allocation-free frame-phase stamps so a hitch can be named. Per frame it only stores
/// longs and adds to fixed counters; it never writes a line per slow frame (a 2026-10-06 client log
/// had 234 of them in 1,500 lines, drawn by the BepInEx console the game was waiting on).
/// <list type="bullet">
/// <item>A frame is a hitch when its interval or its slowest named phase reaches <see cref="HitchMs"/>.</item>
/// <item>At most every <see cref="SummaryEveryMs"/> one file-only summary
/// (<see cref="FormatSummary"/>): frames, hitches, median and max hitch interval, the slowest
/// Progenitor frame and the top handlers by total ms on hitch frames. No hitch, no line.</item>
/// <item>A single frame whose Progenitor work (a named handler or phase) took
/// <see cref="ImmediateMs"/> or more writes the full <see cref="Format"/> line at once, to the
/// console too, at most once per <see cref="ImmediateRateLimitMs"/>.</item>
/// <item>Progenitor work that runs inside the game's own systems rather than in the frame dispatch
/// (system hook callbacks, material seam gates) is added up per name with <see cref="NoteOutside"/>
/// and folded into the next frame's named phases at <see cref="BeginFrame"/>, so it is judged
/// against the interval it ran in and a hitch it causes is no longer <c>outsideOnly</c>.</item>
/// </list>
/// Does not write a report: a previous recorder stalled the thread it measured.
/// </summary>
internal static class UiFrameCost
{
    internal const int HitchMs = 80;
    internal const int SummaryEveryMs = 30_000;
    internal const int ImmediateMs = 250;
    internal const int ImmediateRateLimitMs = 5_000;

    /// <summary>Hitch intervals kept per window for the median; later hitches still count toward count and max.</summary>
    internal const int MaxSamples = 512;

    /// <summary>Distinct worst-names totalled per window; the rest add up under <c>other</c>.</summary>
    internal const int MaxNames = 16;

    /// <summary>Names the summary lists (largest total first).</summary>
    internal const int TopNames = 4;

    /// <summary>Distinct outside-dispatch names added up per interval; the rest add up under <see cref="OutsideOtherName"/>.</summary>
    internal const int MaxOutsideNames = 32;

    internal const string OutsideOtherName = "outside:other";

    internal static Func<long> NowMs = () => Environment.TickCount64;

    /// <summary>Tests: every line written (summary and immediate), before the logger.</summary>
    internal static Action<string>? LogSink;

    internal static long IntervalMs;
    internal static long OutsideMs;
    internal static long FrameMs;
    internal static long PumpMs;
    internal static long RebuildMs;
    internal static long LayoutMs;
    internal static long FlushDirMs;
    internal static long TickMs;
    internal static long WorstMs;
    internal static string WorstName = "";
    internal static long WorstHandlerMs;
    internal static string WorstHandlerName = "";

    private static long _lastStart;
    private static long _lastEnd;

    // ---- outside-dispatch work since the last BeginFrame (main thread only) ----
    private static int _mainThreadId;
    private static readonly string[] OutsideNames = new string[MaxOutsideNames];
    private static readonly long[] OutsideTicks = new long[MaxOutsideNames];
    private static int _outsideNames;
    private static long _outsideOtherTicks;
    private static long _lastImmediateMs = long.MinValue;

    // ---- the window (reset after each summary) ----
    private static long _windowStartMs = long.MinValue;
    private static int _frames;
    private static int _hitches;
    private static readonly long[] Intervals = new long[MaxSamples];
    private static readonly long[] Scratch = new long[MaxSamples];
    private static int _samples;
    private static long _maxIntervalMs;
    private static long _maxWorstMs;
    private static string _maxWorstName = "";
    private static readonly string[] Names = new string[MaxNames];
    private static readonly long[] NameTotalMs = new long[MaxNames];
    private static readonly int[] NameCount = new int[MaxNames];
    private static int _names;
    private static long _otherTotalMs;
    private static int _otherCount;
    private static int _outsideOnly;

    internal static long TicksToMs(long ticks) =>
        ticks <= 0 ? 0 : ticks * 1000 / Stopwatch.Frequency;

    internal static void BeginFrame()
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
        long now = Stopwatch.GetTimestamp();
        IntervalMs = _lastStart == 0 ? 0 : TicksToMs(now - _lastStart);
        OutsideMs = _lastEnd == 0 ? 0 : TicksToMs(now - _lastEnd);
        _lastStart = now;
        PumpMs = 0;
        RebuildMs = 0;
        LayoutMs = 0;
        FlushDirMs = 0;
        TickMs = 0;
        FrameMs = 0;
        WorstMs = 0;
        WorstName = "";
        WorstHandlerMs = 0;
        WorstHandlerName = "";
        FoldOutside();
    }

    /// <summary>
    /// Progenitor work timed outside the frame dispatch (a system hook callback, a material seam gate).
    /// Adds up per <paramref name="name"/> until the next <see cref="BeginFrame"/>, which makes each
    /// total a named phase of that frame. Allocation-free; ignored off the main thread and before the
    /// first frame. <paramref name="name"/> should be a cached string.
    /// </summary>
    internal static void NoteOutside(string name, long ticks)
    {
        if (ticks <= 0 || _mainThreadId == 0 || Environment.CurrentManagedThreadId != _mainThreadId) return;
        for (int i = 0; i < _outsideNames; i++)
        {
            if (!ReferenceEquals(OutsideNames[i], name) && !string.Equals(OutsideNames[i], name, StringComparison.Ordinal)) continue;
            OutsideTicks[i] += ticks;
            return;
        }

        if (_outsideNames < MaxOutsideNames)
        {
            OutsideNames[_outsideNames] = name;
            OutsideTicks[_outsideNames] = ticks;
            _outsideNames++;
            return;
        }

        _outsideOtherTicks += ticks;
    }

    private static void FoldOutside()
    {
        for (int i = 0; i < _outsideNames; i++)
        {
            NoteWorst(OutsideNames[i], TicksToMs(OutsideTicks[i]));
            OutsideNames[i] = "";
            OutsideTicks[i] = 0;
        }

        _outsideNames = 0;
        if (_outsideOtherTicks > 0) NoteWorst(OutsideOtherName, TicksToMs(_outsideOtherTicks));
        _outsideOtherTicks = 0;
    }

    internal static void EndFrame(long frameStartTimestamp)
    {
        FrameMs = TicksToMs(Stopwatch.GetTimestamp() - frameStartTimestamp);
        _lastEnd = Stopwatch.GetTimestamp();
        Record(NowMs());
    }

    internal static void Note(string name, long startTimestamp)
    {
        long ms = TicksToMs(Stopwatch.GetTimestamp() - startTimestamp);
        switch (name)
        {
            case "pump": PumpMs += ms; break;
            case "rebuild": RebuildMs += ms; break;
            case "layout": LayoutMs += ms; break;
            case "flushDir": FlushDirMs += ms; break;
            case "tick": TickMs += ms; break;
        }

        NoteWorst(name, ms);
    }

    internal static void NoteHandler(string name, long ticks)
    {
        long ms = TicksToMs(ticks);
        if (ms <= WorstHandlerMs) return;
        WorstHandlerMs = ms;
        WorstHandlerName = string.IsNullOrEmpty(name) ? "handler" : name;
        NoteWorst("handler:" + WorstHandlerName, ms);
    }

    /// <summary>A hitch: the frame interval, or the slowest named Progenitor phase, reached <see cref="HitchMs"/>.</summary>
    internal static bool IsHitch(long intervalMs, long worstMs) => intervalMs >= HitchMs || worstMs >= HitchMs;

    /// <summary>One frame's own Progenitor work, with a name, reached <see cref="ImmediateMs"/>, and the last such line is old enough.</summary>
    internal static bool IsImmediate(long worstMs, string? worstName, long nowMs, long lastImmediateMs)
    {
        if (worstMs < ImmediateMs || string.IsNullOrEmpty(worstName)) return false;
        if (lastImmediateMs == long.MinValue) return true;
        return nowMs - lastImmediateMs >= ImmediateRateLimitMs;
    }

    /// <summary>The window is old enough for a summary.</summary>
    internal static bool SummaryDue(long nowMs, long windowStartMs) =>
        windowStartMs != long.MinValue && nowMs - windowStartMs >= SummaryEveryMs;

    /// <summary>
    /// Fold the frame just measured (the current fields) into the window, write the immediate line when
    /// <see cref="IsImmediate"/>, and the summary when <see cref="SummaryDue"/>. Allocation-free unless it writes.
    /// </summary>
    internal static void Record(long nowMs)
    {
        if (_windowStartMs == long.MinValue) _windowStartMs = nowMs;
        _frames++;
        if (IsHitch(IntervalMs, WorstMs))
        {
            _hitches++;
            if (_samples < MaxSamples) Intervals[_samples++] = IntervalMs;
            if (IntervalMs > _maxIntervalMs) _maxIntervalMs = IntervalMs;
            if (WorstMs > _maxWorstMs)
            {
                _maxWorstMs = WorstMs;
                _maxWorstName = WorstName;
            }

            if (WorstMs > 0 && !string.IsNullOrEmpty(WorstName)) AddName(WorstName, WorstMs);
            else _outsideOnly++;
        }

        if (IsImmediate(WorstMs, WorstName, nowMs, _lastImmediateMs))
        {
            _lastImmediateMs = nowMs;
            Write(Format(
                IntervalMs, OutsideMs, FrameMs,
                PumpMs, RebuildMs, LayoutMs, FlushDirMs, TickMs,
                WorstName, WorstMs, WorstHandlerName, WorstHandlerMs), console: true);
        }

        if (!SummaryDue(nowMs, _windowStartMs)) return;
        if (_hitches > 0) Write(FormatWindow(nowMs - _windowStartMs), console: false);
        ResetWindow(nowMs);
    }

    internal static string Format(
        long intervalMs, long outsideMs, long frameMs,
        long pumpMs, long rebuildMs, long layoutMs, long flushDirMs, long tickMs,
        string worstName, long worstMs, string worstHandlerName, long worstHandlerMs)
    {
        return "ui hitch intervalMs=" + intervalMs.ToString(CultureInfo.InvariantCulture)
            + " outsideMs=" + outsideMs.ToString(CultureInfo.InvariantCulture)
            + " frameMs=" + frameMs.ToString(CultureInfo.InvariantCulture)
            + " pumpMs=" + pumpMs.ToString(CultureInfo.InvariantCulture)
            + " rebuildMs=" + rebuildMs.ToString(CultureInfo.InvariantCulture)
            + " layoutMs=" + layoutMs.ToString(CultureInfo.InvariantCulture)
            + " flushDirMs=" + flushDirMs.ToString(CultureInfo.InvariantCulture)
            + " tickMs=" + tickMs.ToString(CultureInfo.InvariantCulture)
            + " worst=" + (string.IsNullOrEmpty(worstName) ? "none" : worstName)
            + " worstMs=" + worstMs.ToString(CultureInfo.InvariantCulture)
            + " handler=" + (string.IsNullOrEmpty(worstHandlerName) ? "none" : worstHandlerName)
            + " handlerMs=" + worstHandlerMs.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <c>ui hitch summary windowS=30 frames=2950 hitches=41 intervalMs=median:85,max:1040 outsideOnly=35
    /// slowest=handler:JoinFlowPanel.OnFrame:211 top=[handler:JoinFlowPanel.OnFrame 420ms/3, …]</c>.
    /// <c>outsideOnly</c> counts hitches with no Progenitor phase or handler over 0 ms (the game's own frame).
    /// <c>top</c> totals the slowest named phase of each hitch frame.
    /// </summary>
    internal static string FormatSummary(
        long windowMs, int frames, int hitches, long medianIntervalMs, long maxIntervalMs, int outsideOnly,
        string slowestName, long slowestMs, IReadOnlyList<KeyValuePair<string, long>> topTotals, IReadOnlyList<int> topCounts)
    {
        var sb = new System.Text.StringBuilder(160);
        sb.Append("ui hitch summary windowS=").Append((windowMs / 1000).ToString(CultureInfo.InvariantCulture))
            .Append(" frames=").Append(frames.ToString(CultureInfo.InvariantCulture))
            .Append(" hitches=").Append(hitches.ToString(CultureInfo.InvariantCulture))
            .Append(" intervalMs=median:").Append(medianIntervalMs.ToString(CultureInfo.InvariantCulture))
            .Append(",max:").Append(maxIntervalMs.ToString(CultureInfo.InvariantCulture))
            .Append(" outsideOnly=").Append(outsideOnly.ToString(CultureInfo.InvariantCulture))
            .Append(" slowest=").Append(string.IsNullOrEmpty(slowestName) ? "none" : slowestName)
            .Append(':').Append(slowestMs.ToString(CultureInfo.InvariantCulture))
            .Append(" top=[");
        for (int i = 0; i < topTotals.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(topTotals[i].Key).Append(' ')
                .Append(topTotals[i].Value.ToString(CultureInfo.InvariantCulture)).Append("ms/")
                .Append((i < topCounts.Count ? topCounts[i] : 0).ToString(CultureInfo.InvariantCulture));
        }

        return sb.Append(']').ToString();
    }

    /// <summary>The median of the first <paramref name="count"/> values (sorted in <paramref name="scratch"/>); 0 when empty.</summary>
    internal static long Median(long[] values, int count, long[] scratch)
    {
        if (count <= 0) return 0;
        Array.Copy(values, scratch, count);
        Array.Sort(scratch, 0, count);
        return count % 2 == 1 ? scratch[count / 2] : (scratch[count / 2 - 1] + scratch[count / 2]) / 2;
    }

    internal static void ResetForTests()
    {
        IntervalMs = 0;
        OutsideMs = 0;
        FrameMs = 0;
        PumpMs = 0;
        RebuildMs = 0;
        LayoutMs = 0;
        FlushDirMs = 0;
        TickMs = 0;
        WorstMs = 0;
        WorstName = "";
        WorstHandlerMs = 0;
        WorstHandlerName = "";
        _lastStart = 0;
        _lastEnd = 0;
        _lastImmediateMs = long.MinValue;
        _mainThreadId = 0;
        for (int i = 0; i < _outsideNames; i++) { OutsideNames[i] = ""; OutsideTicks[i] = 0; }
        _outsideNames = 0;
        _outsideOtherTicks = 0;
        ResetWindow(long.MinValue);
        NowMs = () => Environment.TickCount64;
        LogSink = null;
    }

    private static void NoteWorst(string name, long ms)
    {
        if (ms <= WorstMs) return;
        WorstMs = ms;
        WorstName = name;
    }

    private static void AddName(string name, long ms)
    {
        for (int i = 0; i < _names; i++)
        {
            if (!ReferenceEquals(Names[i], name) && !string.Equals(Names[i], name, StringComparison.Ordinal)) continue;
            NameTotalMs[i] += ms;
            NameCount[i]++;
            return;
        }

        if (_names < MaxNames)
        {
            Names[_names] = name;
            NameTotalMs[_names] = ms;
            NameCount[_names] = 1;
            _names++;
            return;
        }

        _otherTotalMs += ms;
        _otherCount++;
    }

    private static string FormatWindow(long windowMs)
    {
        var order = new List<int>(_names);
        for (int i = 0; i < _names; i++) order.Add(i);
        order.Sort((a, b) => NameTotalMs[b].CompareTo(NameTotalMs[a]));
        var totals = new List<KeyValuePair<string, long>>(TopNames + 1);
        var counts = new List<int>(TopNames + 1);
        for (int i = 0; i < order.Count && i < TopNames; i++)
        {
            totals.Add(new KeyValuePair<string, long>(Names[order[i]], NameTotalMs[order[i]]));
            counts.Add(NameCount[order[i]]);
        }

        if (_otherCount > 0)
        {
            totals.Add(new KeyValuePair<string, long>("other", _otherTotalMs));
            counts.Add(_otherCount);
        }

        return FormatSummary(windowMs, _frames, _hitches, Median(Intervals, _samples, Scratch), _maxIntervalMs, _outsideOnly,
            _maxWorstName, _maxWorstMs, totals, counts);
    }

    private static void ResetWindow(long nowMs)
    {
        _windowStartMs = nowMs;
        _frames = 0;
        _hitches = 0;
        _samples = 0;
        _maxIntervalMs = 0;
        _maxWorstMs = 0;
        _maxWorstName = "";
        for (int i = 0; i < _names; i++) Names[i] = "";
        _names = 0;
        _otherTotalMs = 0;
        _otherCount = 0;
        _outsideOnly = 0;
    }

    private static void Write(string line, bool console)
    {
        try { LogSink?.Invoke(line); }
        catch { }
        // docs/bridge.md, "Logging policy": summaries are file-only; a single slow Progenitor frame reaches the console.
        try
        {
            if (console) Plugin.Logger?.LogInfo(line);
            else Plugin.Logger?.LogDiag(line);
        }
        catch { }
    }
}

/// <summary>
/// Which workspace pane a scroll reading describes. Identity is the sidebar flag,
/// never "leftmost world x" — that reported the content pane's 0 while the list
/// sat at 0.73.
/// </summary>
internal static class WorkspacePaneScroll
{
    internal static float ListPosition(bool aSidebar, float aPosition, bool bSidebar, float bPosition)
    {
        if (aSidebar) return aPosition;
        if (bSidebar) return bPosition;
        return 1f;
    }

    internal static float ContentPosition(bool aSidebar, float aPosition, bool bSidebar, float bPosition)
    {
        if (!aSidebar) return aPosition;
        if (!bSidebar) return bPosition;
        return 1f;
    }

    internal static bool ListOverflow(bool aSidebar, bool aOverflow, bool bSidebar, bool bOverflow)
    {
        if (aSidebar) return aOverflow;
        if (bSidebar) return bOverflow;
        return false;
    }
}

/// <summary>Canvas-space geometry for the Mods workspace. Fonts retain their normal size.</summary>
internal static class MenuPanelLayout
{
    internal const float Gap = 16f;
    internal const float TopInset = 180f;
    internal const float TabLift = 8f;

    /// <summary>
    /// How far the native <c>Divider_Big_04</c> trim's top edge sits above the pane top
    /// (<c>ModsWorkspaceChrome.PlaceHolder</c>). The tab bottoms meet the pane top, so the trim
    /// overlaps them by this much in every host — the in-game look.
    /// </summary>
    internal const float TrimLift = 4f;
    internal const float BottomInset = 40f;

    /// <summary>
    /// Sidebar is min(360, 30% of the inner width after the gap). Detail takes the rest.
    /// Growing the workspace therefore widens the detail pane, not the sidebar.
    /// </summary>
    internal const float SidebarShare = 0.3f;

    internal const float SidebarMaxWidth = 360f;

    internal static MenuPaneGeometry ForCanvas(float width, float height, bool sidebar) =>
        ForFrame(ModsWorkspaceFrame.Fallback(width, height), sidebar);

    internal static MenuPaneGeometry ForCanvas(float width, float height, bool sidebar, MenuPaneMode mode) =>
        ForFrame(ModsWorkspaceFrame.Fallback(width, height), sidebar, mode);

    internal static MenuPaneGeometry ForFrame(in ModsWorkspaceFrame frame, bool sidebar) =>
        ForFrame(frame, sidebar, MenuPaneMode.Split);

    /// <summary>
    /// Pane geometry for the frame. <see cref="MenuPaneMode.Split"/> is the sidebar / detail
    /// pair; <see cref="MenuPaneMode.FullWidth"/> (LIST3, the Install tab) gives the detail
    /// pane the whole frame width, centered on the frame, with no sidebar column. A sidebar
    /// asked for in full-width mode keeps its split geometry (the caller does not build one).
    /// </summary>
    internal static MenuPaneGeometry ForFrame(in ModsWorkspaceFrame frame, bool sidebar, MenuPaneMode mode)
    {
        Split(frame.Width, out float sidebarWidth, out float detailWidth, out float gap);
        float height = frame.Height;
        float y = frame.Y;
        if (StripConsumesFrameTop(frame))
        {
            float reserved = UnityUiRuntimeMetrics.RowHeight - frame.TabLift;
            height = frame.Height - reserved;
            if (height < 1f) height = 1f;
            y = frame.Y - reserved * .5f;
        }

        if (sidebar)
            return new MenuPaneGeometry(sidebarWidth, height, frame.X - (gap + detailWidth) * .5f, y);
        if (mode == MenuPaneMode.FullWidth)
            return new MenuPaneGeometry(Positive(frame.Width, 1276f), height, frame.X, y);
        if (mode is MenuPaneMode.ListWithAside or MenuPaneMode.Aside)
        {
            AsideSplit(frame.Width, out float listWidth, out float asideWidth, out float asideGap);
            return mode == MenuPaneMode.ListWithAside
                ? new MenuPaneGeometry(listWidth, height, frame.X - (asideGap + asideWidth) * .5f, y)
                : new MenuPaneGeometry(asideWidth, height, frame.X + (asideGap + listWidth) * .5f, y);
        }
        return new MenuPaneGeometry(detailWidth, height, frame.X + (gap + sidebarWidth) * .5f, y);
    }

    /// <summary>KEYS3: the description column's share of the frame, its floor and its cap.</summary>
    internal const float AsideShare = 0.22f;

    internal const float AsideMinWidth = 240f;

    internal const float AsideMaxWidth = 400f;

    /// <summary>
    /// KEYS3: the Keybinds tab's split, list on the left and the description column on the right
    /// (the game's Bindings Menu). The column is 22% of the frame after the gap, clamped to
    /// [<see cref="AsideMinWidth"/>, <see cref="AsideMaxWidth"/>] and never more than 40%.
    /// </summary>
    internal static void AsideSplit(float workspace, out float listWidth, out float asideWidth, out float gap)
    {
        workspace = Positive(workspace, 1276f);
        gap = Math.Min(Gap, workspace * .025f);
        float inner = Math.Max(1f, workspace - gap);
        asideWidth = Math.Max(AsideMinWidth, Math.Min(AsideMaxWidth, inner * AsideShare));
        asideWidth = Math.Min(asideWidth, inner * .4f);
        listWidth = Math.Max(1f, inner - asideWidth);
    }

    /// <summary>
    /// Height of a pane's scrolling viewport before pinned chrome: the pane minus the title
    /// band, which a <see cref="MenuPaneMode.FullWidth"/> pane does not draw (the tab strip
    /// already names the tab).
    /// </summary>
    /// <summary>
    /// Row width of the workspace detail pane, from the last resolved frame when there is one and
    /// otherwise the 1920×1080 fallback cards. This is what a kit block whose layout depends on the
    /// pane width (<see cref="HeadBlockLayout.Build"/>) asks when the caller does not pass one.
    /// </summary>
    internal static float DetailRowWidth()
    {
        var last = ModsWorkspaceFrame.Last;
        return last.Width > 1f
            ? ForFrame(last, sidebar: false).RowWidth
            : ForCanvas(1920f, 1080f, sidebar: false).RowWidth;
    }

    internal static float PaneViewportHeight(float paneHeight, MenuPaneMode mode) =>
        MenuPaneModes.Titleless(mode)
            ? Positive(paneHeight, 800f)
            : PanelLayout.ViewportHeightFor(paneHeight);

    /// <summary>
    /// True when the frame is a native ViewParent rect: there is no free band
    /// above it (the game's outer tab bar owns that space), so the strip must
    /// occupy the top of the frame. Fallback / title-ratio frames already leave
    /// <see cref="TopInset"/> free above the panes.
    /// </summary>
    internal static bool StripConsumesFrameTop(in ModsWorkspaceFrame frame) =>
        // Round 8: only when there is no band above the window for the tabs (the collapsed native layout
        // drawn unpinned); the vanilla window (top 128) leaves the 56-unit band Spellbook's tabs use.
        ModsWorkspaceFrame.UsesInGameGeometry(frame.Path) && frame.TabLift < UnityUiRuntimeMetrics.RowHeight;

    /// <summary>Round 8: the strip stands in the band above the window, left-aligned like Spellbook's tabs.</summary>
    internal static bool StripInBand(in ModsWorkspaceFrame frame) =>
        ModsWorkspaceFrame.UsesInGameGeometry(frame.Path) && !StripConsumesFrameTop(frame);

    /// <summary>
    /// One-row tab strip, same width as <paramref name="frame"/>, center-anchored
    /// with it, one catalog row high. On a native-container frame the strip
    /// occupies the top of the frame; on fallback it sits <see cref="Gap"/>
    /// above the pane tops in the unused <see cref="TopInset"/> band.
    /// </summary>
    internal static MenuPaneGeometry ForStrip(in ModsWorkspaceFrame frame)
    {
        float width = Positive(frame.Width, 1276f);
        // Round 13: in the band the tabs are Spellbook's height (rect 48, the frame drawn 40: y 88..128).
        float height = StripInBand(frame) ? WorkspaceHostLayout.TabHeight : UnityUiRuntimeMetrics.RowHeight;
        // TABS2: every host stands the tabs on the artwork pane's top edge — the rect
        // ModsWorkspaceChrome.PlaceHolder draws the artwork and the trim from — so the tab bottoms
        // meet the trim line at the title exactly as in game. On a native frame this is the same
        // y as before (frame top − row + TabLift); on a title/fallback frame it removes the Gap
        // that left the strip floating ~14 px above the frame line.
        float y = ArtworkPaneTop(frame) + height * .5f;
        if (StripInBand(frame))
        {
            // Round 8: in the band above the window, starting where Spellbook's first tab does.
            float inset = WorkspaceHostLayout.StripLeftInset;
            return new MenuPaneGeometry(Math.Max(1f, width - inset), height, frame.X + inset * .5f, y);
        }

        return new MenuPaneGeometry(width, height, frame.X, y);
    }

    /// <summary>
    /// Top edge of the full-width artwork pane for <paramref name="frame"/> — the same geometry
    /// <c>ModsWorkspaceChrome.SyncFrame</c> hands to <c>PlaceHolder</c> (its trim sits
    /// <see cref="TrimLift"/> above this line).
    /// </summary>
    internal static float ArtworkPaneTop(in ModsWorkspaceFrame frame)
    {
        var pane = ForFrame(frame, false, MenuPaneMode.FullWidth);
        return pane.Y + pane.Height * .5f;
    }

    /// <summary>
    /// Title + facts occupy two rows; the primary action sits to their right
    /// and does not add height. Same <see cref="ChromeHeight"/> arithmetic the
    /// sidebar search pin uses, so the viewport inset matches the band.
    /// </summary>
    internal const int PinnedHeaderRows = 2;

    /// <summary>Height of a pinned listing header band inside a detail pane.</summary>
    internal static float PinnedHeaderHeight(int rows = PinnedHeaderRows) =>
        ChromeHeight(rows < 1 ? PinnedHeaderRows : rows);

    /// <summary>
    /// Split a detail pane into a non-scrolling header band at the top and the
    /// scrolling viewport beneath it. The two rects share the seam and do not
    /// overlap. Same center-anchored space as <see cref="ForStrip"/>.
    /// </summary>
    internal static void SplitPinnedHeader(
        in MenuPaneGeometry pane, float headerHeight, out MenuPaneGeometry header, out MenuPaneGeometry viewport)
    {
        float height = Positive(pane.Height, 800f);
        float width = Positive(pane.Width, UnityUiRuntimeMetrics.RowWidth);
        float band = headerHeight;
        if (float.IsNaN(band) || float.IsInfinity(band) || band < 1f)
            band = PinnedHeaderHeight();
        if (band > height - 1f) band = height - 1f;
        float paneTop = pane.Y + height * 0.5f;
        float paneBottom = pane.Y - height * 0.5f;
        header = new MenuPaneGeometry(width, band, pane.X, paneTop - band * 0.5f);
        float viewH = height - band;
        if (viewH < 1f) viewH = 1f;
        viewport = new MenuPaneGeometry(width, viewH, pane.X, paneBottom + viewH * 0.5f);
    }

    /// <summary>
    /// True when <paramref name="header"/> occupies the top of
    /// <paramref name="pane"/> and <paramref name="viewport"/> starts at or
    /// below the header's bottom edge (they may share the seam).
    /// </summary>
    internal static bool HeaderLiesAboveViewport(
        in MenuPaneGeometry pane, in MenuPaneGeometry header, in MenuPaneGeometry viewport)
    {
        const float tol = 0.5f;
        float paneTop = pane.Y + pane.Height * 0.5f;
        float paneBottom = pane.Y - pane.Height * 0.5f;
        float paneLeft = pane.X - pane.Width * 0.5f;
        float paneRight = pane.X + pane.Width * 0.5f;
        float headerTop = header.Y + header.Height * 0.5f;
        float headerBottom = header.Y - header.Height * 0.5f;
        float viewTop = viewport.Y + viewport.Height * 0.5f;
        float viewBottom = viewport.Y - viewport.Height * 0.5f;
        float headerLeft = header.X - header.Width * 0.5f;
        float headerRight = header.X + header.Width * 0.5f;
        float viewLeft = viewport.X - viewport.Width * 0.5f;
        float viewRight = viewport.X + viewport.Width * 0.5f;
        return header.Height > 0f
            && viewport.Height > 0f
            && headerTop <= paneTop + tol
            && Math.Abs(headerTop - paneTop) <= tol
            && headerBottom >= paneBottom - tol
            && viewBottom >= paneBottom - tol
            && viewTop <= headerBottom + tol
            && viewTop >= headerBottom - tol
            && headerLeft >= paneLeft - tol
            && headerRight <= paneRight + tol
            && viewLeft >= paneLeft - tol
            && viewRight <= paneRight + tol
            && headerBottom >= viewTop - tol;
    }

    /// <summary>
    /// Width one header action button wants: room for the longest caption
    /// ("Installed (managed elsewhere)") at <see cref="HeaderButtonFontSize"/>
    /// with the caption's side padding, so the caption sits inside the button
    /// rather than beside it (gen 39, evidence 117).
    /// </summary>
    internal const float HeaderButtonWidth = 320f;

    /// <summary>Caption size inside a header action button; smaller than the 22 px body caption so the long labels fit.</summary>
    internal const float HeaderButtonFontSize = 18f;

    /// <summary>Caption inset inside a header button, both sides together.</summary>
    internal const float HeaderButtonCaptionPad = 16f;

    /// <summary>
    /// KIT1: the icon-only link button (the mock's exit link at the top right of a section). Square,
    /// so it reads as an icon rather than a short caption, and small enough to sit on a heading row.
    /// </summary>
    internal const float IconLinkSize = 30f;

    /// <summary>The link's glyph size inside <see cref="IconLinkSize"/>.</summary>
    internal const float IconLinkFontSize = 20f;

    /// <summary>
    /// The WGL4-safe glyph the link draws until a sprite replaces it. U+00BB
    /// (RIGHT-POINTING DOUBLE ANGLE QUOTATION MARK) is Latin-1, so the vanilla TMP font has it —
    /// unlike the mock's SVG and unlike U+2197, which the font draws as the missing-glyph box.
    /// </summary>
    internal const string IconLinkGlyph = "»";

    /// <summary>
    /// POLISH3 item C: the icon link's own right inset. The link is placed by the generic settings
    /// row's control column, which ends flush with the row — one scrollbar band further right than
    /// everything the mod page right-aligns (HEAD2 gave the head block's status word, action button
    /// and badges <see cref="HeadBlockLayout.RightColumnInset"/>). Live at 3840×1080 that left the
    /// <c>»</c> 12 px right of the status word, half under the pane's scrollbar. Only the icon link
    /// takes this inset: no other pane's control column is moved, because an editor control filling
    /// its column is already inside the row's own padding.
    /// </summary>
    internal static float IconLinkCentreX(in RowColumns columns, float size)
    {
        float square = Math.Max(0f, size);
        return columns.ControlLeft + columns.ControlWidth - HeadBlockLayout.RightColumnInset - (square * .5f);
    }

    /// <summary>POLISH3 item C: the link's right edge, offset from the row center — what a geometry test pins.</summary>
    internal static float IconLinkRight(in RowColumns columns, float size) =>
        IconLinkCentreX(columns, size) + (Math.Max(0f, size) * .5f);

    /// <summary>Reason note under the header buttons: font, line pitch and the wrap cap.</summary>
    internal const float HeaderReasonFontSize = SettingsAlign.NoteFontSize;
    internal const float HeaderReasonLineHeight = 22f;
    internal const int HeaderReasonMaxLines = 3;

    /// <summary>
    /// Left (title + facts) and right (action) columns of a pinned header. The
    /// action column is sized for <paramref name="buttons"/> buttons of
    /// <see cref="HeaderButtonWidth"/> (a secondary Remove sits beside the
    /// primary), capped at the same 45 % share the menu control column gets,
    /// so the title keeps the larger half of the band at every fixture.
    /// </summary>
    internal static void HeaderColumns(
        float rowWidth, int buttons, out float labelWidth, out float labelX, out float actionWidth, out float actionX)
    {
        rowWidth = Positive(rowWidth, UnityUiRuntimeMetrics.RowWidth);
        if (buttons < 1) buttons = 1;
        float gap = Math.Min(PanelLayout.ColumnGap, rowWidth * .05f);
        float wanted = HeaderButtonWidth * buttons + PanelLayout.RowGap * (buttons - 1);
        float cap = Math.Max(1f, (rowWidth - gap) * .45f);
        actionWidth = Math.Min(wanted, cap);
        labelWidth = Math.Max(1f, rowWidth - gap - actionWidth);
        labelX = -(gap + actionWidth) * .5f;
        actionX = (gap + labelWidth) * .5f;
    }

    /// <summary>One-button header columns (the common listing page).</summary>
    internal static void HeaderColumns(
        float rowWidth, out float labelWidth, out float labelX, out float actionWidth, out float actionX) =>
        HeaderColumns(rowWidth, 1, out labelWidth, out labelX, out actionWidth, out actionX);

    /// <summary>
    /// Center x (row-relative, like <see cref="RowColumns"/>) and width of header
    /// button <paramref name="index"/> of <paramref name="count"/>, laid left to
    /// right across the action column with <see cref="PanelLayout.RowGap"/>
    /// between them.
    /// </summary>
    internal static void HeaderButton(float rowWidth, int index, int count, out float x, out float width)
    {
        if (count < 1) count = 1;
        if (index < 0) index = 0;
        if (index >= count) index = count - 1;
        HeaderColumns(rowWidth, count, out _, out _, out float actionWidth, out float actionX);
        float gap = count > 1 ? PanelLayout.RowGap : 0f;
        width = Math.Max(1f, (actionWidth - gap * (count - 1)) / count);
        float left = actionX - actionWidth * .5f;
        x = left + index * (width + gap) + width * .5f;
    }

    /// <summary>
    /// True when <paramref name="caption"/>'s estimated width at
    /// <see cref="HeaderButtonFontSize"/> fits inside a button
    /// <paramref name="buttonWidth"/> wide after <see cref="HeaderButtonCaptionPad"/>.
    /// The live caption still gets TMP ellipsis; this is the game-free check that
    /// the known labels never need it.
    /// </summary>
    internal static bool HeaderCaptionFits(string? caption, float buttonWidth) =>
        string.IsNullOrEmpty(caption)
        || string.Equals(ClipLabel(caption, buttonWidth - HeaderButtonCaptionPad, HeaderButtonFontSize), caption, StringComparison.Ordinal);

    /// <summary>
    /// Estimated wrapped line count of the reason note in a column
    /// <paramref name="width"/> wide, 0 for no text, capped at
    /// <see cref="HeaderReasonMaxLines"/> (the live label gets ellipsis past that).
    /// Pure arithmetic on <see cref="AverageGlyphEm"/>; whole words are kept together.
    /// </summary>
    internal static int HeaderReasonLines(string? text, float width) =>
        WrappedLines(text, Positive(width, HeaderButtonWidth), HeaderReasonFontSize, HeaderReasonMaxLines);

    /// <summary>
    /// Estimated wrapped line count of <paramref name="text"/> in a column <paramref name="width"/>
    /// wide at <paramref name="fontSize"/>: 0 for no text, capped at <paramref name="maxLines"/>
    /// (the live label gets TMP ellipsis past that). Pure arithmetic on <see cref="AverageGlyphEm"/>;
    /// whole words are kept together, a word longer than the line is broken, and an explicit
    /// newline starts a paragraph. KIT1 generalized this out of <see cref="HeaderReasonLines"/> so
    /// the head block's one-liner and the pinned header's reason cannot measure differently.
    /// </summary>
    internal static int WrappedLines(string? text, float width, float fontSize, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (maxLines < 1) maxLines = 1;
        width = Positive(width, HeaderButtonWidth);
        if (float.IsNaN(fontSize) || float.IsInfinity(fontSize) || fontSize <= 0f) fontSize = CaptionFontSize;
        float glyph = fontSize * AverageGlyphEm;
        int perLine = Math.Max(1, (int)(width / glyph));
        int lines = 0;
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            int used = 0;
            lines++;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int need = word.Length + (used > 0 ? 1 : 0);
                if (used > 0 && used + need > perLine)
                {
                    lines++;
                    used = 0;
                    need = word.Length;
                }
                used += need;
                while (used > perLine)
                {
                    lines++;
                    used -= perLine;
                }
            }
            if (lines >= maxLines) return maxLines;
        }
        return Math.Min(maxLines, lines);
    }

    /// <summary>
    /// Header band height for a reason of <paramref name="reasonLines"/> lines:
    /// never less than the two-row band (title + facts) and grows only when the
    /// right column (one button row plus the wrapped reason) needs more.
    /// </summary>
    internal static float PinnedHeaderHeightFor(int reasonLines)
    {
        if (reasonLines < 0) reasonLines = 0;
        if (reasonLines > HeaderReasonMaxLines) reasonLines = HeaderReasonMaxLines;
        float left = PinnedHeaderHeight();
        if (reasonLines == 0) return left;
        float right = PanelLayout.PadTop
            + UnityUiRuntimeMetrics.RowHeight + PanelLayout.RowGap
            + reasonLines * HeaderReasonLineHeight + PanelLayout.RowGap;
        return Math.Max(left, right);
    }

    /// <summary>
    /// One equal-width button of a horizontal strip. <paramref name="index"/> is
    /// 0-based left-to-right; the gap between buttons is the frame's own
    /// <see cref="Gap"/> (capped like <see cref="Split"/>).
    /// </summary>
    internal static MenuPaneGeometry StripButton(in MenuPaneGeometry strip, int index, int count)
    {
        if (count < 1) count = 1;
        if (index < 0) index = 0;
        if (index >= count) index = count - 1;
        // Round 13 (user review against Spellbook): each tab sized to its caption plus the donor's padding,
        // at least Spellbook's (rect 176, the frame drawn 168), with Spellbook's gap (rect 2, drawn 10),
        // left-aligned; scaled with the strip's height, and fitted into the strip when too wide.
        float scale = strip.Height / WorkspaceHostLayout.TabHeight;
        var widths = WorkspaceHostLayout.TabWidths(ModsTabStrip.CaptionsFor(count), strip.Width, scale);
        float gap = WorkspaceHostLayout.TabGap * scale;
        float left = strip.X - strip.Width * .5f;
        for (int i = 0; i < index; i++) left += widths[i] + gap;
        return new MenuPaneGeometry(widths[index], strip.Height, left + widths[index] * .5f, strip.Y);
    }

    /// <summary>
    /// True when <paramref name="strip"/> sits in the frame's top inset, spans
    /// the two panes, and does not overlap them. Title-site / fallback only:
    /// native-container frames have no free inset — use
    /// <see cref="StripLiesInsideFrame"/>.
    /// </summary>
    internal static bool StripLiesInTopInset(in ModsWorkspaceFrame frame, in MenuPaneGeometry strip)
    {
        float paneTop = frame.Y + frame.Height * .5f;
        float insetTop = paneTop + TopInset;
        float stripTop = strip.Y + strip.Height * .5f;
        float stripBottom = strip.Y - strip.Height * .5f;
        float paneLeft = frame.X - frame.Width * .5f;
        float paneRight = frame.X + frame.Width * .5f;
        float stripLeft = strip.X - strip.Width * .5f;
        float stripRight = strip.X + strip.Width * .5f;
        const float tol = 0.5f;
        // Round 8: a strip in the band above the vanilla window starts StripLeftInset in, like Spellbook's.
        float inset = StripInBand(frame) ? WorkspaceHostLayout.StripLeftInset : 0f;
        return strip.Height > 0f
            && strip.Height <= TopInset + tol
            && stripBottom >= paneTop - tol
            && stripTop <= insetTop + tol
            && stripLeft >= paneLeft - tol
            && stripRight <= paneRight + tol
            && Math.Abs(strip.Width - (frame.Width - inset)) <= tol
            && Math.Abs(strip.X - (frame.X + inset * .5f)) <= tol;
    }

    /// <summary>
    /// True when <paramref name="strip"/> occupies the top of the native
    /// container, spans its width, and stays inside the frame (not over the
    /// outer tab bar above ViewParent).
    /// </summary>
    internal static bool StripLiesInsideFrame(in ModsWorkspaceFrame frame, in MenuPaneGeometry strip)
    {
        float frameTop = frame.Y + frame.Height * .5f;
        float frameBottom = frame.Y - frame.Height * .5f;
        float frameLeft = frame.X - frame.Width * .5f;
        float frameRight = frame.X + frame.Width * .5f;
        float stripTop = strip.Y + strip.Height * .5f;
        float stripBottom = strip.Y - strip.Height * .5f;
        float stripLeft = strip.X - strip.Width * .5f;
        float stripRight = strip.X + strip.Width * .5f;
        const float tol = 0.5f;
        return strip.Height > 0f
            && stripTop <= frameTop + frame.TabLift + tol
            && stripBottom >= frameBottom - tol
            && Math.Abs(stripTop - frameTop - frame.TabLift) <= tol
            && stripLeft >= frameLeft - tol
            && stripRight <= frameRight + tol
            && Math.Abs(strip.Width - frame.Width) <= tol
            && Math.Abs(strip.X - frame.X) <= tol;
    }

    /// <summary>
    /// Inner split used by both the fallback cards and a native-sized frame.
    /// Sidebar stays a sidebar: 30% of (workspace − gap), capped at 360.
    /// </summary>
    internal static void Split(float workspace, out float sidebarWidth, out float detailWidth, out float gap)
    {
        workspace = Positive(workspace, 1276f);
        gap = Math.Min(Gap, workspace * .025f);
        sidebarWidth = Math.Min(SidebarMaxWidth, (workspace - gap) * SidebarShare);
        detailWidth = workspace - gap - sidebarWidth;
        if (detailWidth < 1f) detailWidth = 1f;
    }

    internal static RowColumns Columns(float rowWidth, bool hasControl)
    {
        rowWidth = Positive(rowWidth, UnityUiRuntimeMetrics.RowWidth);
        if (!hasControl) return new RowColumns(rowWidth, 0f, 0f, 0f);
        float gap = Math.Min(PanelLayout.ColumnGap, rowWidth * .05f);
        float control = Math.Min(PanelLayout.ControlColumnWidth, (rowWidth - gap) * .45f);
        float label = rowWidth - gap - control;
        return new RowColumns(label, -(gap + control) * .5f,
            control, (gap + label) * .5f);
    }

    /// <summary>
    /// Full-width control, no caption column. The Mods search field spans the
    /// sidebar the way catalog rows do, rather than sitting in the short right-hand box.
    /// </summary>
    internal static RowColumns SearchFieldColumns(float rowWidth)
    {
        rowWidth = Positive(rowWidth, UnityUiRuntimeMetrics.RowWidth);
        return new RowColumns(0f, 0f, rowWidth, 0f);
    }

    /// <summary>
    /// Columns the layout pass must reuse for a menu row. Search uses the full
    /// row width; every other control row keeps the two-column split.
    /// </summary>
    internal static RowColumns ColumnsForMenuRow(float rowWidth, bool hasControl, bool searchField) =>
        searchField ? SearchFieldColumns(rowWidth) : Columns(rowWidth, hasControl);

    /// <summary>Caption font size the sidebar rows use. Width estimates for <see cref="ClipLabel"/> use this.</summary>
    internal const float CaptionFontSize = 18f;

    /// <summary>
    /// Mean glyph width as a fraction of font size. Used only to bound a caption before TMP
    /// ellipsis; a long id that still overflowed the 213 px sidebar at 1280×720 is the reason.
    /// </summary>
    internal const float AverageGlyphEm = 0.55f;

    /// <summary>
    /// Search (pinned above the list) + Overview / refresh + Close Mods (pinned below).
    /// The empty-match label is extra and lives in the scrolling content.
    /// </summary>
    internal const int ListChromeRows = 3;

    /// <summary>Search field, pinned above the scrolling list so it does not move with the rows.</summary>
    internal const int ListTopChromeRows = 1;

    /// <summary>Overview / refresh + Close Mods, pinned below the scrolling list.</summary>
    internal const int ListBottomChromeRows = 2;

    /// <summary>
    /// Truncate <paramref name="text"/> with an ellipsis so its estimated width fits
    /// <paramref name="rowWidth"/>. Short strings are unchanged. Pure arithmetic; TMP ellipsis
    /// is still applied on the live caption.
    /// </summary>
    internal static string ClipLabel(string? text, float rowWidth, float fontSize = CaptionFontSize)
    {
        if (string.IsNullOrEmpty(text)) return "";
        rowWidth = Positive(rowWidth, UnityUiRuntimeMetrics.RowWidth);
        if (float.IsNaN(fontSize) || float.IsInfinity(fontSize) || fontSize <= 0f)
            fontSize = CaptionFontSize;
        float glyph = fontSize * AverageGlyphEm;
        int max = glyph <= 0f ? 1 : Math.Max(1, (int)(rowWidth / glyph));
        if (text.Length <= max) return text;
        if (max == 1) return "\u2026";
        return text.Substring(0, max - 1) + "\u2026";
    }

    /// <summary>
    /// Install filter block under the search box (SEARCH3): <c>Filters · n of m</c>,
    /// <c>Side: Any</c>, <c>Progenitor-supported: off</c>, <c>Current update: on</c> and the
    /// fetch button — five pinned full-width rows, each one <see cref="UnityUiRuntimeMetrics.RowHeight"/>
    /// plus <see cref="PanelLayout.RowGap"/>, laid out by the ordinary row path (no inline cluster).
    /// </summary>
    internal const int InstallFilterRows = 5;

    /// <summary>Install sidebar top chrome: the search field plus <see cref="InstallFilterRows"/>.</summary>
    internal const int InstallTopChromeRows = ListTopChromeRows + InstallFilterRows;

    /// <summary>
    /// Top edge of pinned top row <paramref name="index"/> (0 = the search field) in the
    /// panel's content space: the same arithmetic <c>UnityPanel.PlaceRow</c> uses for a
    /// <see cref="MenuRowPin.Top"/> row whose predecessors each measure one row height.
    /// </summary>
    internal static float PinnedTopRowY(int index) =>
        PanelLayout.HeaderHeight + PanelLayout.PadTop
        + Math.Max(0, index) * (UnityUiRuntimeMetrics.RowHeight + PanelLayout.RowGap);

    /// <summary>True when <paramref name="caption"/> fits one line of <paramref name="rowWidth"/> at <paramref name="fontSize"/> (the <see cref="ClipLabel"/> estimate).</summary>
    internal static bool RowCaptionFits(string? caption, float rowWidth, float fontSize) =>
        string.IsNullOrEmpty(caption)
        || string.Equals(ClipLabel(caption, rowWidth, fontSize), caption, StringComparison.Ordinal);

    /// <summary>Sidebar rows for overflow: chrome, plus one empty-match label when nothing is visible.</summary>
    internal static int SidebarRowCount(int visibleEntries) =>
        ListChromeRows + (visibleEntries <= 0 ? 1 : visibleEntries);

    /// <summary>
    /// Height of the scrolling list viewport after the pinned search (top) and Overview/Close
    /// (bottom) chrome are reserved. Same arithmetic <c>UnityPanel</c> uses to inset the
    /// <c>ScrollRect</c> viewport. The bottom reservation is <see cref="BottomChromeHeight"/>,
    /// which includes <see cref="FooterGap"/> so the last catalog row ends above the footer band
    /// rather than against it.
    /// </summary>
    internal static float ListViewportHeight(float paneHeight)
    {
        float view = PanelLayout.ViewportHeightFor(paneHeight);
        float chrome = ChromeHeight(ListTopChromeRows) + BottomChromeHeight(ListBottomChromeRows);
        float list = view - chrome;
        return list < 0f ? 0f : list;
    }

    /// <summary>
    /// True when <paramref name="rowCount"/> single-line rows would scroll in the list pane
    /// at this canvas size. Same <see cref="PanelLayout.Scrolls"/> rule the live panel uses.
    /// <paramref name="rowCount"/> is chrome-plus-entries (<see cref="SidebarRowCount"/>) so
    /// overflow stays comparable to the pre-pin layout: the pinned chrome still occupies the
    /// pane, it just is no longer inside the scrolling content.
    /// </summary>
    internal static bool ListOverflows(int rowCount, float canvasWidth, float canvasHeight)
    {
        var pane = ForCanvas(canvasWidth, canvasHeight, sidebar: true);
        int n = rowCount < 0 ? 0 : rowCount;
        int matching = n <= ListChromeRows ? 0 : n - ListChromeRows;
        var layout = new PanelLayout();
        int contentRows = matching <= 0 ? 1 : matching;
        for (int i = 0; i < contentRows; i++)
            layout.NextRow(UnityUiRuntimeMetrics.RowHeight);
        return PanelLayout.Scrolls(layout.ContentHeight, ListViewportHeight(pane.Height));
    }

    /// <summary>
    /// How many catalog rows currently intersect the list viewport. This is <c>visibleRows</c>:
    /// the rendered subset, not the filtered catalog count. Chrome (search / Overview / Close)
    /// is pinned outside the viewport and is not counted.
    /// </summary>
    internal static int VisibleCatalogRows(
        int matchingCount, float canvasWidth, float canvasHeight, float normalizedPosition = 1f)
    {
        if (matchingCount <= 0) return 0;
        var pane = ForCanvas(canvasWidth, canvasHeight, sidebar: true);
        float listView = ListViewportHeight(pane.Height);
        var layout = new PanelLayout();
        for (int i = 0; i < matchingCount; i++)
            layout.NextRow(UnityUiRuntimeMetrics.RowHeight);
        float contentHeight = layout.ContentHeight;
        float scrollable = contentHeight > listView ? contentHeight - listView : 0f;
        float npos = normalizedPosition;
        if (float.IsNaN(npos) || float.IsInfinity(npos)) npos = 1f;
        if (npos < 0f) npos = 0f;
        if (npos > 1f) npos = 1f;
        float offset = (1f - npos) * scrollable;
        layout.Reset();
        int visible = 0;
        for (int i = 0; i < matchingCount; i++)
        {
            float top = layout.NextRow(UnityUiRuntimeMetrics.RowHeight);
            if (RowIntersectsViewport(top, UnityUiRuntimeMetrics.RowHeight, offset, listView))
                visible++;
        }

        return visible;
    }

    /// <summary>
    /// A content row is a raycast target at <paramref name="pointY"/> only when that point sits
    /// inside both the row and the viewport. Rows scrolled above the viewport (their bottom at
    /// or above the viewport top) miss — including when they occupy the search field's screen
    /// coordinates.
    /// </summary>
    internal static bool RowIsRaycastTarget(
        float rowTop, float rowHeight, float viewportTop, float viewportHeight, float pointY)
    {
        if (rowHeight <= 0f || viewportHeight <= 0f) return false;
        float rowBottom = rowTop + rowHeight;
        float viewBottom = viewportTop + viewportHeight;
        if (rowBottom <= viewportTop || rowTop >= viewBottom) return false;
        return pointY >= rowTop && pointY < rowBottom
            && pointY >= viewportTop && pointY < viewBottom;
    }

    /// <summary>
    /// One catalog row including the gap below it. Wheel steps are a multiple of this,
    /// not of the total content height.
    /// </summary>
    internal static float RowStep => UnityUiRuntimeMetrics.RowHeight + PanelLayout.RowGap;

    /// <summary>
    /// Rows one mouse-wheel notch should move. Independent of how many rows the list holds.
    /// </summary>
    internal const float WheelRowsPerNotch = 2f;

    /// <summary>
    /// The live mover is the shown-input pump writing <c>content.anchoredPosition</c>.
    /// uGUI's <c>ScrollRect.OnScroll</c> is inert on pump-driven panels
    /// (<see cref="UguiScrollSensitivity"/> is 0) so a notch cannot be applied twice.
    /// </summary>
    internal const string WheelMover = "pump";

    /// <summary>
    /// Units <c>InputSystemUIInputModule</c> historically put on
    /// <c>PointerEventData.scrollDelta</c> per Windows notch. Kept so the zeroed
    /// <see cref="UguiScrollSensitivity"/> stays a relationship rather than a magic 0.
    /// </summary>
    internal const float UguiNotchUnits = 6f;

    /// <summary>
    /// <c>ScrollRect.scrollSensitivity</c> written onto every pump-driven panel. Zero so
    /// <c>OnScroll</c> adds nothing even if the EventSystem delivers the notch after
    /// <c>Plugin.Frame</c>. The pump is the only writer.
    /// </summary>
    internal static float UguiScrollSensitivity => 0f;

    /// <summary>Pixel delta of one notch at <see cref="WheelRowsPerNotch"/> rows.</summary>
    internal static float PumpPixelsPerNotch => RowStep * WheelRowsPerNotch;

    /// <summary>
    /// Next <c>ScrollRect.verticalNormalizedPosition</c> after a mouse-wheel delta. Windows
    /// notches arrive as ±120; values already in lines are used as-is. One notch steps
    /// <see cref="WheelRowsPerNotch"/> rows. Negative delta (toward the user) moves down the
    /// list (position toward 0).
    /// </summary>
    internal static float NextScrollPosition(
        float position, float scrollY, float contentHeight, float viewportHeight, float rowStep)
    {
        float overflow = contentHeight - viewportHeight;
        if (overflow <= 0f) return 1f;
        float ticks = scrollY;
        if (ticks > 20f || ticks < -20f) ticks /= 120f;
        if (rowStep <= 0f) rowStep = RowStep;
        return ClampNormalized(position + ticks * rowStep * WheelRowsPerNotch / overflow);
    }

    /// <summary>
    /// Pixel delta of one wheel event. uGUI is inert on pump-driven panels, so the pump
    /// copy is the one that lands. <paramref name="uguiConsumed"/> true is the double-apply
    /// this helper exists to refuse.
    /// </summary>
    internal static float PixelsForWheelEvent(float uguiPixels, float pumpPixels, bool uguiConsumed) =>
        uguiConsumed ? uguiPixels : pumpPixels;

    /// <summary>
    /// Rows uGUI would move at the written <see cref="UguiScrollSensitivity"/>. Zero: the
    /// pump is the mover. Unclamped pump rows are <see cref="WheelRowsPerNotch"/>.
    /// </summary>
    internal static float UguiRowsPerNotch =>
        UguiScrollSensitivity * UguiNotchUnits / RowStep;

    /// <summary>
    /// Rows one notch actually moves on a list of this size, after overflow clamp.
    /// Zero when the content fits.
    /// </summary>
    internal static float RowsMovedByNotch(float contentHeight, float viewportHeight, float rowStep)
    {
        float overflow = contentHeight - viewportHeight;
        if (overflow <= 0f) return 0f;
        if (rowStep <= 0f) rowStep = RowStep;
        float next = NextScrollPosition(1f, -120f, contentHeight, viewportHeight, rowStep);
        return (1f - next) * overflow / rowStep;
    }

    /// <summary>
    /// True when writing a normalized position against these sizes will not collapse to
    /// Unity's empty-bounds 0/1. Restoring 0.77 before the content height exists is how
    /// a selection jumped the list to the bottom.
    /// </summary>
    internal static bool CanApplyNormalized(float contentHeight, float viewportHeight) =>
        contentHeight > viewportHeight + 0.5f;

    /// <summary>
    /// Top-pivot content <c>anchoredPosition.y</c> for a normalized position: 1 is the top
    /// (y = 0), 0 is the bottom (y = overflow).
    /// </summary>
    internal static float ContentOffsetY(float normalized, float contentHeight, float viewportHeight)
    {
        float overflow = contentHeight - viewportHeight;
        if (overflow <= 0f) return 0f;
        return (1f - ClampNormalized(normalized)) * overflow;
    }

    /// <summary>
    /// INSTALLSIDES2 (user feedback 2026-10-03): the content offset that keeps a row where it was on
    /// screen after the list above or below it changed. <paramref name="rowY"/> is the row's top
    /// measured now, <paramref name="wantedY"/> where it was before, both from the viewport's top
    /// edge (px, downward); the offset moves by the difference and is clamped only to what the
    /// content allows (0 .. content − viewport).
    /// </summary>
    internal static float AnchorOffset(float was, float rowY, float wantedY, float contentHeight, float viewportHeight)
    {
        if (float.IsNaN(was) || float.IsInfinity(was)) was = 0f;
        float next = was + (rowY - wantedY);
        float max = Math.Max(0f, contentHeight - viewportHeight);
        if (float.IsNaN(next)) next = was;
        return next < 0f ? 0f : next > max ? max : next;
    }

    /// <summary>
    /// Job LIST4 item 3. Where the list must scroll to so that an opened row and the drawer
    /// under it are visible. All four inputs and the answer are top-down content pixels: a row
    /// <paramref name="rowTop"/> from the top of the content, a block
    /// <paramref name="blockHeight"/> tall counting the row itself, and a viewport whose top
    /// edge currently sits <paramref name="viewportTop"/> down the content — the same number
    /// <see cref="ContentOffsetY"/> writes.
    ///
    /// <para>The rule: if the whole block already fits inside the window, nothing moves — a
    /// drawer opened at the top of the viewport must not jump. Otherwise the row goes to the
    /// top of the viewport, which is the most of the drawer that can be shown without hiding
    /// the row that owns it, and is clamped to the content's own end so a drawer opened on the
    /// last row does not scroll past the bottom (the drawer is then as visible as the list can
    /// make it).</para>
    /// </summary>
    internal static float RevealOffsetY(
        float rowTop, float blockHeight, float viewportTop, float viewportHeight, float contentHeight, bool toTop = false)
    {
        float maxTop = contentHeight - viewportHeight;
        if (maxTop < 0f) maxTop = 0f;
        if (rowTop < 0f) rowTop = 0f;
        if (blockHeight < 0f) blockHeight = 0f;
        float current = viewportTop < 0f ? 0f : (viewportTop > maxTop ? maxTop : viewportTop);
        bool topVisible = rowTop >= current - 0.5f;
        bool bottomVisible = rowTop + blockHeight <= current + viewportHeight + 0.5f;
        // INSTALLSIDES1: an explicit open always brings the row to the top (still clamped below).
        if (!toTop && topVisible && bottomVisible) return current;
        float wanted = rowTop;
        if (wanted > maxTop) wanted = maxTop;
        return wanted;
    }

    /// <summary>
    /// Job LIST4 item 3, second pass. The same rule as <see cref="RevealOffsetY(float,float,float,float,float)"/>
    /// but measured from what is on screen, because the first pass measured the row in the wrong
    /// frame and scrolled it off the top of the list.
    ///
    /// <para><b>The defect this exists to prevent.</b> A row's <c>anchoredPosition</c> is relative
    /// to the row parent, whose top is the top of the pane frame — the pinned toolbar, filter and
    /// header rows sit in that band above the list viewport. Treating that distance as "how far
    /// down the scrolling content is this row" put the revealed row at the top of the <i>frame</i>
    /// rather than the top of the <i>viewport</i>: live at 3840×1080 the opened row landed at
    /// client y 12 with the viewport starting at y 280, and the chooser row under it at y ≈ −3,
    /// where the chips were genuinely unclickable. The readback was self-consistent
    /// (<c>rowTop 618, viewportTop 618</c>) and still wrong by the pinned band's height.
    ///
    /// <para>Deriving the row's position in content pixels from the live viewport rect instead
    /// cannot make that mistake, whatever the row parent is: the content's top edge is
    /// <paramref name="currentOffsetY"/> above the viewport's own top edge, so the row's offset
    /// down the content is <c>viewportTopY + currentOffsetY − rowTopY</c>. All screen values are
    /// Unity screen space (y up), the space <c>ScreenRectOf</c> answers in.</para>
    /// </summary>
    /// <param name="rowTopEdgeY">
    /// The row's <b>top</b> edge in Unity screen space, which is <c>rect.y + rect.height</c> of a
    /// <c>mods_ui rects</c> row — bottom-origin, y growing upward, so a row further down the
    /// content has a <i>smaller</i> y. Live at 3840×1080 the list viewport spans y 336‥1044
    /// (<c>{y 336, height 708}</c>), the first listing row reads <c>{y 683, h 53}</c> (top 736,
    /// inside), and the twelfth reads <c>{y 73}</c> (top 126, ≈210 px below the viewport's bottom
    /// edge). Revealing that twelfth row must put its top edge on 1044 — <c>rect.y ≈ 991</c> — by
    /// scrolling the content 918 px, not 618.
    /// </param>
    internal static float RevealOffsetFromScreen(
        float rowTopEdgeY,
        float blockBottomY,
        float viewportTopEdgeY,
        float viewportHeight,
        float currentOffsetY,
        float contentHeight,
        out float rowTopInContent) =>
        RevealOffsetFromScreen(rowTopEdgeY, blockBottomY, viewportTopEdgeY, viewportHeight, currentOffsetY, contentHeight, false,
            out rowTopInContent);

    /// <summary>INSTALLSIDES1: as above; <paramref name="toTop"/> always moves the row to the viewport's top.</summary>
    internal static float RevealOffsetFromScreen(
        float rowTopEdgeY,
        float blockBottomY,
        float viewportTopEdgeY,
        float viewportHeight,
        float currentOffsetY,
        float contentHeight,
        bool toTop,
        out float rowTopInContent)
    {
        // Bottom-origin: the content's top edge sits currentOffsetY ABOVE the viewport's top edge,
        // so the row's distance down the content is the drop from that content top to the row's
        // top — a row below the viewport (smaller y) gives a larger distance, which is exactly the
        // offset that brings it up to the viewport's top edge.
        rowTopInContent = viewportTopEdgeY + currentOffsetY - rowTopEdgeY;
        if (rowTopInContent < 0f) rowTopInContent = 0f;
        // Downward in the content is downward on screen, which is a fall in y.
        float blockHeight = rowTopEdgeY - blockBottomY;
        if (blockHeight < 0f) blockHeight = 0f;
        return RevealOffsetY(rowTopInContent, blockHeight, currentOffsetY, viewportHeight, contentHeight, toTop);
    }

    /// <summary>Inverse of <see cref="ContentOffsetY"/>.</summary>
    internal static float NormalizedFromOffset(float offsetY, float contentHeight, float viewportHeight)
    {
        float overflow = contentHeight - viewportHeight;
        if (overflow <= 0f) return 1f;
        return ClampNormalized(1f - offsetY / overflow);
    }

    internal static float ClampNormalized(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return 1f;
        if (value < 0f) return 0f;
        if (value > 1f) return 1f;
        return value;
    }

    /// <summary>
    /// KEYS3 polish: the band pinned top rows take, from their measured heights (active rows only): the
    /// top pad, then each row and the row gap after it. The viewport starts below it, so no pinned row
    /// is drawn over the list. <see cref="TopRowOffset"/> places the rows inside it.
    /// </summary>
    internal static float TopChromeHeight(IReadOnlyList<float> heights)
    {
        if (heights == null || heights.Count == 0) return 0f;
        float sum = PanelLayout.PadTop;
        for (int i = 0; i < heights.Count; i++) sum += Math.Max(0f, heights[i]) + PanelLayout.RowGap;
        return sum;
    }

    /// <summary>
    /// KEYS3 polish (live 2026-09-30: after a jump a list row showed half under the toolbar): the list
    /// viewport's top inset — the title band plus the pinned chrome, and never less than the lowest
    /// pinned top row actually placed (its top and height, from the panel top) plus the row gap. The
    /// viewport is the <c>RectMask2D</c> that clips the list, so a row can only ever draw below it.
    /// </summary>
    internal static float ViewportTopInset(float titleBand, float topChrome, IReadOnlyList<(float Top, float Height)>? placedPinned)
    {
        float inset = Math.Max(0f, titleBand) + Math.Max(0f, topChrome);
        if (placedPinned == null) return inset;
        for (int i = 0; i < placedPinned.Count; i++)
        {
            var p = placedPinned[i];
            if (p.Height <= 0f || float.IsNaN(p.Top) || float.IsNaN(p.Height)) continue;
            inset = Math.Max(inset, p.Top + p.Height + PanelLayout.RowGap);
        }

        return inset;
    }

    /// <summary>KEYS3 polish: where the <paramref name="index"/>th active top row starts, below the title band.</summary>
    internal static float TopRowOffset(IReadOnlyList<float> heights, int index)
    {
        float top = PanelLayout.PadTop;
        for (int i = 0; i < index && heights != null && i < heights.Count; i++) top += Math.Max(0f, heights[i]) + PanelLayout.RowGap;
        return top;
    }

    internal static float ChromeHeight(int rows)
    {
        if (rows <= 0) return 0f;
        return PanelLayout.PadTop + rows * (UnityUiRuntimeMetrics.RowHeight + PanelLayout.RowGap);
    }

    /// <summary>
    /// Empty strip between the list viewport's bottom edge and the footer band. The last
    /// catalog row clips against this gap, not against the band.
    /// </summary>
    internal const float FooterGap = 12f;

    /// <summary>Padding inside the footer band above the first action.</summary>
    internal const float FooterPadTop = 12f;

    /// <summary>Padding inside the footer band below the last action.</summary>
    internal const float FooterPadBottom = 12f;

    /// <summary>Gap between Overview / refresh and Close Mods. Wider than a catalog <see cref="PanelLayout.RowGap"/>.</summary>
    internal const float FooterButtonGap = 10f;

    /// <summary>
    /// Height of the painted footer band (padding + actions). Does not include
    /// <see cref="FooterGap"/>; that sits above the band as unused panel chrome.
    /// </summary>
    internal static float FooterBandHeight(int rows)
    {
        if (rows <= 0) return 0f;
        return FooterPadTop
            + rows * UnityUiRuntimeMetrics.RowHeight
            + (rows - 1) * FooterButtonGap
            + FooterPadBottom;
    }

    /// <summary>
    /// Viewport bottom inset for pinned footer actions: the band plus the gap that keeps
    /// the last catalog row off the band.
    /// </summary>
    internal static float BottomChromeHeight(int rows)
    {
        if (rows <= 0) return 0f;
        return FooterGap + FooterBandHeight(rows);
    }

    private static bool RowIntersectsViewport(float top, float height, float offset, float viewportHeight)
    {
        float y = top - offset;
        return y < viewportHeight && y + height > 0f;
    }

    internal static float Positive(float value, float fallback) =>
        float.IsNaN(value) || float.IsInfinity(value) || value <= 0f ? fallback : value;

    /// <summary>
    /// Column model of a <c>CellRow</c> / <c>CellHeader</c> (LIST1): every cell's center x
    /// (row-relative, like <see cref="RowColumns"/>) and width for a row
    /// <paramref name="rowWidth"/> wide. See <see cref="CellLayout.Resolve"/>.
    /// </summary>
    internal static CellPlacement[] CellColumns(float rowWidth, IReadOnlyList<CellSpec> specs) =>
        CellLayout.Resolve(Positive(rowWidth, UnityUiRuntimeMetrics.RowWidth), specs);

    /// <summary>
    /// The row width an in-place re-placement of a cell row uses (a cell rewritten through <c>SetCell</c>, outside a
    /// panel layout pass): the width the last layout pass placed that row at, so a settings-grid row keeps the grid's
    /// width; the content row width only before the row was ever placed. Live 2026-10-06: the panel's content row width
    /// was used, which outside a layout pass is the full content width, and a drawer header's count refresh after a
    /// value change packed its cells from a left edge wider than the grid row (the fold marker and the start of the
    /// title fell outside it).
    /// </summary>
    internal static float InPlaceCellRowWidth(float placedWidth, float contentRowWidth) =>
        float.IsNaN(placedWidth) || float.IsInfinity(placedWidth) || placedWidth <= 0f ? contentRowWidth : placedWidth;

    /// <summary>
    /// Column model of a whole <see cref="CellRowSpec"/>, which is the one form that honors the
    /// row's <see cref="CellRowSpec.RightInset"/> (HEAD2). Every caller that has the row rather
    /// than only its cells — the runtime's placement pass, the readback's rects and the geometry
    /// tests — must use this overload, or a row with an inset is measured as if it had none.
    /// </summary>
    internal static CellPlacement[] CellColumns(float rowWidth, CellRowSpec? row) =>
        row == null
            ? Array.Empty<CellPlacement>()
            : CellLayout.Resolve(
                Positive(rowWidth, UnityUiRuntimeMetrics.RowWidth), row.Cells, row.RightInset);
}

/// <summary>What a cell of a <see cref="CellRowSpec"/> draws.</summary>
internal enum CellKind
{
    /// <summary>A sprite (listing icon); accepts a sprite handle later and repaints in place.</summary>
    Image = 0,
    /// <summary>Body text.</summary>
    Text = 1,
    /// <summary>Dimmer secondary text (author, note).</summary>
    Muted = 2,
    /// <summary>Tabular figures (version, date, download count).</summary>
    Mono = 3,
    /// <summary>Side badge: fill + rim + short caption from <see cref="MenuPalette.Badge"/>.</summary>
    Badge = 4,
    /// <summary>A real kit button with its own click.</summary>
    Button = 5,
    /// <summary>The expand marker (<see cref="InstallModsCopy.GlyphExpand"/>) that rotates when the row is expanded.</summary>
    Chevron = 6,
    /// <summary>No object; absorbs the remaining width.</summary>
    Spacer = 7,
    /// <summary>A text field (LIST3): <see cref="CellSpec.Text"/> is its value, <see cref="CellSpec.Placeholder"/> the empty hint; edits raise the row's <c>OnChanged</c>.</summary>
    Input = 8,

    /// <summary>
    /// SETTINGS1 stage 2: an on/off switch drawn as a track and a knob. <see cref="CellSpec.Text"/>
    /// is <c>On</c> or <c>Off</c> (the knob sits right when on); the tone colors the track. Not a
    /// button: a switch that can be pressed is a <see cref="Button"/> cell.
    /// </summary>
    Switch = 9,

    /// <summary>
    /// SETTINGS1 stage 3: the Mods list's status diamond — a small square turned 45 degrees, drawn
    /// from images rather than a glyph (the game font has no U+25C6, and a non-WGL4 glyph drew as
    /// TMP's missing-glyph box live). <see cref="CellSpec.Text"/> is <c>filled</c> or <c>hollow</c>;
    /// the tone is its color. See <see cref="ModsListLayout"/>.
    /// </summary>
    Diamond = 10,

    /// <summary>
    /// INSTALLSIDES1: a block of text lines (<see cref="CellSpec.Lines"/>) that scrolls inside the
    /// cell with its own scroll bar — the game's <c>ScrollBar_Bar</c> / <c>ScrollBar_Handle</c> when
    /// they were borrowed. The row must be born as tall as the block (<see cref="CellRowSpec"/>'s
    /// explicit height); the wheel over it scrolls the block, not the list. The Install drawer.
    /// </summary>
    ScrollText = 11,
}

/// <summary>INSTALLSIDES1: what one line of a <see cref="CellKind.ScrollText"/> cell is.</summary>
internal enum CellLineKind
{
    /// <summary>Body text, wrapping.</summary>
    Body = 0,

    /// <summary>A bold, larger line (a README heading).</summary>
    Heading = 1,

    /// <summary>Muted secondary text.</summary>
    Muted = 2,

    /// <summary>A label in the muted color and its value: <see cref="CellLine.Label"/> and <see cref="CellLine.Text"/>.</summary>
    Fact = 3,

    /// <summary>A warning sentence in the warn tone.</summary>
    Warn = 4,

    /// <summary>Code: drawn in the muted color, never re-flowed.</summary>
    Code = 5,
}

/// <summary>INSTALLSIDES1: one line of a <see cref="CellKind.ScrollText"/> cell. Plain text; never rich text.</summary>
internal readonly struct CellLine
{
    internal CellLine(CellLineKind kind, string text, string label = "", string label2 = "", string text2 = "")
    {
        Kind = kind;
        Text = text ?? "";
        Label = label ?? "";
        Label2 = label2 ?? "";
        Text2 = text2 ?? "";
    }

    internal CellLineKind Kind { get; }
    internal string Text { get; }

    /// <summary>A <see cref="CellLineKind.Fact"/>'s label; empty otherwise.</summary>
    internal string Label { get; }

    /// <summary>
    /// INSTALLSIDES2 item 4: a second fact on the same line, drawn from the middle of the block (a
    /// two-column fact grid in the compact drawer). Empty for a one-fact line.
    /// </summary>
    internal string Label2 { get; }

    /// <inheritdoc cref="Label2"/>
    internal string Text2 { get; }

    /// <summary>The line carries a second fact.</summary>
    internal bool HasSecond => Kind == CellLineKind.Fact && Label2.Length > 0;

    /// <summary>The font size the runtime draws the line at.</summary>
    internal float FontSize => Kind switch
    {
        CellLineKind.Heading => CellLayout.ScrollHeadingFontSize,
        CellLineKind.Muted or CellLineKind.Code => CellLayout.ScrollMutedFontSize,
        _ => CellLayout.ScrollBodyFontSize,
    };

    public override string ToString() =>
        Kind != CellLineKind.Fact ? Text
        : HasSecond ? Label + ": " + Text + " | " + Label2 + ": " + Text2
        : Label + ": " + Text;
}

/// <summary>Color treatment of a cell (see <see cref="MenuPalette"/>).</summary>
internal enum CellTone
{
    Normal = 0,
    Muted = 1,
    Gold = 2,
    Server = 3,
    Client = 4,
    Both = 5,
    Warn = 6,
    Ok = 7,
    Pending = 8,
    Disabled = 9,
    /// <summary>KIT1: the mock's red — the leading <c>(!)</c> of a warning line and a sidebar marker.</summary>
    Danger = 10,
    /// <summary>KIT1: the mock's warning-line orange (<c>--orangetx</c>), lighter than <see cref="Warn"/> so a whole sentence stays readable.</summary>
    Orange = 11,

    /// <summary>INSTALLSIDES2 item 1: pale blue — a side waiting on a restart (pending install or removal).</summary>
    Info = 12,

    /// <summary>INSTALLSIDES2 item 1: the light green of an installed side's words (<see cref="MenuPalette.OkText"/>).</summary>
    OkBright = 13,

    /// <summary>INSTALLSIDES2 item 1: the amber of an update or a missing half (<see cref="MenuPalette.PendText"/>).</summary>
    Amber = 14,

    /// <summary>INSTALLSIDES2 live review: a grayed side's words, dimmer than muted (<see cref="MenuPalette.GrayedText"/>).</summary>
    Faint = 15,

    /// <summary>INSTALLFILTERS1: a selected filter chip — filled gold face, bright rim, light text.</summary>
    ChipOn = 16,

    /// <summary>INSTALLFILTERS1: an unselected filter chip — dark face, quiet rim.</summary>
    ChipOff = 17,

    /// <summary>INSTALLFILTERS1: an unselected attention chip (Missing a side / Needs update) with matches: amber rim.</summary>
    ChipAttention = 18,

    /// <summary>JOINLINKS1: a red section's heading words (<see cref="MenuPalette.SectionRedHead"/>).</summary>
    SectionRedHead = 19,

    /// <summary>JOINLINKS1: a red section's heading note (<see cref="MenuPalette.SectionRedNote"/>).</summary>
    SectionRedNote = 20,

    /// <summary>JOINLINKS1: a green section's heading words (<see cref="MenuPalette.SectionGreenHead"/>).</summary>
    SectionGreenHead = 21,

    /// <summary>JOINLINKS1: a green section's heading note (<see cref="MenuPalette.SectionGreenNote"/>).</summary>
    SectionGreenNote = 22,

    /// <summary>
    /// MODPAGE2: a settings row's name at rest, the game's gray (<see cref="GameArt.GameSettingsLook.NameGrey"/>), so a mod
    /// page's facts read in the same color as the settings grid's names beside them.
    /// </summary>
    SettingName = 23,
}

/// <summary>
/// JOINLINKS1: the tinted section band a row belongs to (<see cref="CellRowSpec.Section"/>). Consecutive rows
/// of one section read as one block: the band's fill runs through the gaps between them and a 1 px rim
/// goes round the whole block (<see cref="MenuPalette.SectionBand"/>).
/// </summary>
internal enum CellSection
{
    None = 0,

    /// <summary>The darkened red band (the join mods page's "Required to join").</summary>
    Red = 1,

    /// <summary>The darkened green band (the join mods page's "Recommended by this server").</summary>
    Green = 2,
}

/// <summary>JOINLINKS1: which edges of its section a row carries (the first row the top rim, the last the bottom one).</summary>
[Flags]
internal enum CellSectionEdge
{
    Middle = 0,
    First = 1,
    Last = 2,
}

/// <summary>
/// INSTALLSIDES2 item 1: the Install tab's per-side column a cell belongs to. A row draws one tinted
/// band behind every cell of a column (and the header a tinted band with a top rule), and the
/// column's popup buttons take its tint, so a button visibly belongs to its side.
/// </summary>
internal enum CellColumn
{
    None = 0,

    /// <summary>This client: the cool blue family.</summary>
    Client = 1,

    /// <summary>The picked server: the blood red family.</summary>
    Server = 2,
}

/// <summary>
/// KIT1: color treatment of a kit <i>label</i> — the mod page's status word, an action caption, a
/// warning line. The five colored tones are the ones the Install cell rows already use, so a badge,
/// a cell caption and a page label cannot drift apart (<see cref="LabelTones.ToCellTone"/>);
/// <see cref="Danger"/> is the fixed red of the <c>(!)</c> marker, which is not one of the cell
/// tones an Install row asks for but comes out of the same table
/// (<see cref="MenuPalette.LabelColor"/>) rather than a second constant.
/// </summary>
public enum LabelTone
{
    /// <summary>The ordinary caption color.</summary>
    Normal = 0,

    /// <summary>Gray: secondary text, a hint, a value that does not apply now.</summary>
    Muted = 1,

    /// <summary>Green: done, running, enabled.</summary>
    Ok = 2,

    /// <summary>Yellow: waiting, in progress, needs a restart.</summary>
    Pending = 3,

    /// <summary>Orange: failed or refused, but recoverable (the Install rows' error color).</summary>
    Orange = 4,

    /// <summary>Amber: a warning, something the player should look at.</summary>
    Warn = 5,

    /// <summary>Red: a destructive action's caption or a blocking problem.</summary>
    Danger = 6,
}

/// <summary>Readback names of <see cref="LabelTone"/> and the cell tone each one maps to.</summary>
internal static class LabelTones
{
    internal static string Name(LabelTone tone) => tone switch
    {
        LabelTone.Muted => "muted",
        LabelTone.Ok => "ok",
        LabelTone.Pending => "pending",
        LabelTone.Orange => "orange",
        LabelTone.Warn => "warn",
        LabelTone.Danger => "danger",
        _ => "normal",
    };

    internal static string Name(LabelTone? tone) => tone == null ? "" : Name(tone.Value);

    /// <summary>
    /// The cell tone that paints the same thing, so a status word in a head block and a status
    /// badge in an Install row answer from one palette entry.
    /// </summary>
    internal static CellTone ToCellTone(LabelTone tone) => tone switch
    {
        LabelTone.Muted => CellTone.Muted,
        LabelTone.Ok => CellTone.Ok,
        LabelTone.Pending => CellTone.Pending,
        LabelTone.Orange => CellTone.Orange,
        LabelTone.Warn => CellTone.Warn,
        LabelTone.Danger => CellTone.Danger,
        _ => CellTone.Normal,
    };

    /// <summary>Parse a readback name back to a tone; anything unknown is <see cref="LabelTone.Normal"/>.</summary>
    internal static LabelTone Parse(string? name) => name switch
    {
        "muted" => LabelTone.Muted,
        "ok" => LabelTone.Ok,
        "pending" => LabelTone.Pending,
        "orange" => LabelTone.Orange,
        "warn" => LabelTone.Warn,
        "danger" => LabelTone.Danger,
        _ => LabelTone.Normal,
    };
}

/// <summary>
/// Where a cell sits and how its caption is justified. <see cref="Right"/> cells are packed
/// at the right edge of the row (the mock's Downloads / Update / Install columns).
/// </summary>
internal enum CellAlign
{
    Left = 0,
    Right = 1,
    Center = 2,
}

/// <summary>Overflow of a text cell.</summary>
internal enum CellWrap
{
    /// <summary>One line. Drawn with the same ellipsis clamp as <see cref="Ellipsis"/> (every caption goes through <c>ClampCaptionText</c>); kept for callers that want to say "never wrap".</summary>
    None = 0,
    /// <summary>One line, TMP ellipsis (the default).</summary>
    Ellipsis = 1,
    /// <summary>Two lines (name + one-liner) inside the 53 px row.</summary>
    TwoLine = 2,
    /// <summary>
    /// KIT1: as many lines as the text needs, word-wrapped to the cell's width. The row must be
    /// tall enough for them — a cell row's height never changes after creation, so the caller
    /// sizes the row from <see cref="HeadBlockLayout.WrappedLines"/> and passes it to
    /// <see cref="CellRowSpec"/>. Used for the mod page's one-liner and warning line.
    /// </summary>
    Wrap = 3,

    /// <summary>
    /// RADIOCLICK2: a single glyph (a radio circle) drawn whole and never truncated: TMP's overflow instead of the
    /// ellipsis clamp. Live 2026-10-06: in a 20 px column the clamp replaced the 18 px circle with an ellipsis that
    /// did not fit either, so nothing was drawn.
    /// </summary>
    Glyph = 4,
}

/// <summary>
/// KIT1: where a cell sits in a row taller than the cell. Middle is the default and what every
/// LIST1 row wants; <see cref="Top"/> is the head block's wrapping one-liner and the badge group
/// beside it, which must sit at the top of a grown row rather than float in its middle.
/// </summary>
internal enum CellVAlign
{
    Middle = 0,
    Top = 1,

    /// <summary>SETTINGS1 stage 2: against the bottom of a taller row (a section heading's space above).</summary>
    Bottom = 2,
}

/// <summary>KEYS3: how a cell is drawn (<see cref="CellSpec.Skin"/>).</summary>
internal enum CellSkin
{
    /// <summary>The kit's own look.</summary>
    Kit = 0,

    /// <summary>A button cell drawn as the game's key cap (borrowed sprite; the kit button when none).</summary>
    KeyCap = 1,

    /// <summary>An image cell drawn as a bare sprite: no placeholder fill, no rim (the description ornament).</summary>
    Ornament = 2,

    /// <summary>
    /// KEYS3 polish: a button cell drawn as the game's reset icon (<c>Icon_Reset</c>, borrowed and owned-copied)
    /// with no box; the kit button with its caption when no icon was borrowed.
    /// </summary>
    ResetIcon = 3,

    /// <summary>
    /// DIALOGS2: a button cell drawn as the game's popup button (<c>SmallerButton_*</c>, owned copies, with
    /// its hover, pressed and disabled sprites); the kit button when the art is not ready.
    /// </summary>
    PopupButton = 4,

    /// <summary>
    /// INSTALLSIDES1: a quiet secondary action — a box-less button whose caption is the only thing
    /// drawn, in the muted gold, brightening on hover and dimming when pressed (the Install row's
    /// <c>Install on both</c> shortcut).
    /// </summary>
    Quiet = 5,

    /// <summary>
    /// INSTALLSIDES1: the row's expand/collapse control — the game's dropdown arrow
    /// (<c>DropDownButton_Arrow</c>, owned copy) on a box-less button, pointing right when closed and
    /// down when open (<see cref="IUiCellRow.SetExpanded"/>), brighter on hover and darker pressed. The
    /// caption glyph is drawn only when the arrow was not borrowed.
    /// </summary>
    Expander = 6,

    /// <summary>
    /// INSTALLSIDES1: a dropdown bar — the game's Options dropdown (<c>DropdownButton_Normal</c>,
    /// <c>DropDownButton_Focus</c> on hover, the arrow at the right), the caption left-aligned. The
    /// Install tab's server picker.
    /// </summary>
    Dropdown = 7,

    /// <summary>
    /// VANILLA1: a text field drawn on the game's settings value box (<c>SimpleBox_Normal</c>, 9-sliced,
    /// owned copy) instead of the kit's flat field with a one-pixel outline; the kit field when the
    /// art is not ready.
    /// </summary>
    ValueBox = 8,

    /// <summary>
    /// VANILLA1: a filter chip drawn as the game's popup button (<c>SmallerButton_*</c> with its hover
    /// and pressed swaps). Selected: a warm plate inside the sprite and the bright caption; off: the
    /// bare sprite and a quiet caption; attention: an amber caption. The kit chip when the art is not
    /// ready.
    /// </summary>
    Chip = 9,

    /// <summary>
    /// VANILLA1: an image cell drawn in a quiet well (a faint dark fill and a steel rim) rather than
    /// the kit's opaque placeholder square, so a listing without an icon shows an empty frame, not a
    /// black block.
    /// </summary>
    IconWell = 10,

    /// <summary>
    /// SERVERLINK1: a button cell drawn as a text link: no box, the caption alone in the link blue
    /// (<see cref="MenuPalette.LinkText"/>, the color the kit's web links use), underlined, left-aligned,
    /// brighter on hover and darker pressed. Its whole caption is the click target.
    /// </summary>
    Link = 11,

    /// <summary>
    /// C5: an <see cref="CellKind.Image"/> cell drawn as the loop-arrows glyph (<see cref="LoopGlyph"/>, the Mods list's
    /// Works across versions icon) in <see cref="MenuPalette.Muted"/>: no placeholder fill, no rim. The glyph is
    /// <see cref="CellLayout.LoopGlyphHeight"/> tall at its own aspect, at the right end of the cell and centered
    /// vertically, so the room to its left is the space after the word before it (<see cref="CellSpec.InlineAfter"/>).
    /// </summary>
    LoopGlyph = 12,
}

/// <summary>KEYS3: how a cell row draws its selected state (<see cref="CellRowSpec.Skin"/>).</summary>
internal enum CellRowSkin
{
    /// <summary>The kit's selected fill and gold stripe.</summary>
    Kit = 0,

    /// <summary>The game's settings-entry highlight band (borrowed; the kit's selected fill without the stripe when none).</summary>
    GameEntry = 1,

    /// <summary>A category heading with the game's thin underline under its title (borrowed; none when not found).</summary>
    CategoryHeading = 2,
}

/// <summary>How a cell's width is asked for.</summary>
internal enum CellWidthMode
{
    /// <summary>Fixed pixels.</summary>
    Px = 0,
    /// <summary>A fraction of the row width.</summary>
    Fraction = 1,
    /// <summary>Measured from the caption (<see cref="CellLayout.EstimateTextWidth"/>).</summary>
    Auto = 2,

    /// <summary>
    /// INSTALLCOLS1: takes whatever width the row has left once every other cell is placed, never
    /// less than its floor (<see cref="CellWidth.Value"/>). A row with a fill cell gives its
    /// remainder to the fill cells instead of to its spacers, so there is no dead gap between the
    /// left and right groups. The Install list's Mod column is the one user.
    /// </summary>
    Fill = 3,

    /// <summary>
    /// GRIDROW1: the settings grid's name column (<see cref="ModSettingsRowGrid"/>) for this row's width, so a cell row
    /// added on the grid puts its caption where every settings row's name is and its next cell on the grid's control
    /// column. Put a lead cell <see cref="CellLayout.GridNameLeadWidth"/> wide before the caption.
    /// </summary>
    GridName = 4,
}

/// <summary>A cell width: pixels, a fraction of the row, or measured from the caption.</summary>
internal readonly struct CellWidth
{
    private CellWidth(CellWidthMode mode, float value)
    {
        Mode = mode;
        Value = value;
    }

    internal CellWidthMode Mode { get; }

    /// <summary>Pixels for <see cref="CellWidthMode.Px"/>, 0..1 for <see cref="CellWidthMode.Fraction"/>, unused for auto.</summary>
    internal float Value { get; }

    internal static CellWidth Px(float px) => new(CellWidthMode.Px, px);

    internal static CellWidth Fraction(float fraction) => new(CellWidthMode.Fraction, fraction);

    internal static readonly CellWidth Auto = new(CellWidthMode.Auto, 0f);

    /// <summary>INSTALLCOLS1: the remaining width of the row, never under <paramref name="minPx"/>.</summary>
    internal static CellWidth Fill(float minPx) => new(CellWidthMode.Fill, minPx);

    /// <summary>GRIDROW1: the settings grid's name column (<see cref="CellWidthMode.GridName"/>).</summary>
    internal static readonly CellWidth GridName = new(CellWidthMode.GridName, 0f);

    public override string ToString() => Mode switch
    {
        CellWidthMode.Px => Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "px",
        CellWidthMode.Fraction => Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        CellWidthMode.Fill => "fill(" + Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "px)",
        CellWidthMode.GridName => "grid-name",
        _ => "auto",
    };
}

/// <summary>
/// One cell of a <see cref="CellRowSpec"/>. Mutable so a row can be rewritten in place
/// (<c>SetCell</c>); the column model reads it, never writes it.
/// </summary>
internal sealed class CellSpec
{
    internal CellSpec(CellKind kind, string key)
    {
        Kind = kind;
        Key = key ?? "";
        Width = kind switch
        {
            CellKind.Image => CellWidth.Px(CellLayout.ImageSize),
            CellKind.Chevron => CellWidth.Px(CellLayout.ChevronWidth),
            CellKind.Spacer => CellWidth.Px(0f),
            _ => CellWidth.Auto,
        };
        Align = kind == CellKind.Chevron ? CellAlign.Center : CellAlign.Left;
        Tone = kind == CellKind.Muted ? CellTone.Muted : CellTone.Normal;
        Wrap = CellWrap.Ellipsis;
    }

    internal CellKind Kind { get; }

    /// <summary>Unique within the row: the <c>SetCell</c> and readback address.</summary>
    internal string Key { get; }

    internal string Text { get; set; } = "";

    internal CellWidth Width { get; set; }

    internal CellAlign Align { get; set; }

    internal CellTone Tone { get; set; }

    internal CellWrap Wrap { get; set; }

    /// <summary>Dropped from the right when the row is too narrow (the mock's Author / Released columns).</summary>
    internal bool Optional { get; set; }

    /// <summary>
    /// INSTALLCOLS1: the order an <see cref="Optional"/> cell is dropped in when the row is too
    /// narrow. The lowest positive number goes first; cells left at 0 go after every numbered one,
    /// rightmost first (the behavior every row had before this existed, so a row that never sets
    /// it is unchanged).
    /// </summary>
    internal int DropOrder { get; set; }

    /// <summary>
    /// INSTALLCOLS1, <see cref="CellWrap.TwoLine"/> cells only: a short lead drawn at the start of the
    /// second line in <see cref="NoteLeadTone"/>, followed by the muted note (the Install list's
    /// <c>Built for Gloomrot · </c> before a one-liner). Empty draws the note alone.
    /// </summary>
    internal string NoteLead { get; set; } = "";

    /// <inheritdoc cref="NoteLead"/>
    internal CellTone NoteLeadTone { get; set; } = CellTone.Warn;

    /// <summary>
    /// INSTALLSIDES1, <see cref="CellWrap.TwoLine"/> cells only: width kept free at the right end of
    /// the second line (an overlay cell sits there — the Install row's shortcut over the author line).
    /// </summary>
    internal float NoteReserve { get; set; }

    /// <summary>Hidden cells keep their key but take no width.</summary>
    internal bool Visible { get; set; } = true;

    /// <summary>Floor the model never shrinks the cell below; 0 uses the kind's default.</summary>
    internal float MinWidth { get; set; }

    /// <summary>
    /// P7b: the most a <see cref="CellWidthMode.Fraction"/> or <see cref="CellWidthMode.Auto"/> cell asks for (0: no cap);
    /// never below the cell's floor. The master-detail widget's master column is a clamped fraction of the row.
    /// </summary>
    internal float MaxWidth { get; set; }

    /// <summary>Header only: this column can be sorted by this key (click raises <c>OnSort</c>).</summary>
    internal string? SortKey { get; set; }

    /// <summary><see cref="CellKind.Input"/> only: the hint drawn while the value is empty.</summary>
    internal string Placeholder { get; set; } = "";

    /// <summary>
    /// SEARCHCLEAR1, <see cref="CellKind.Input"/> only: a search box. It draws the clear button
    /// (<see cref="MenuRowStyle.SearchClearName"/>) while it has text, and Escape in it clears the text,
    /// then leaves the box (the Install tab's search).
    /// </summary>
    internal bool Clearable { get; set; }

    /// <summary>DIALOGS1, <see cref="CellKind.Input"/> only: the most characters the field takes (0: no limit).</summary>
    internal int MaxLength { get; set; }

    /// <summary>
    /// TEXTEXPAND1, <see cref="CellKind.Input"/> only: a multi-line editor about this many lines tall
    /// (word-wrapped, Enter starts a line, a vertical scroll bar in the game's art, the wheel scrolls it);
    /// 0 is the one-line field. Give the cell its height with <see cref="HeightOverride"/>.
    /// </summary>
    internal int EditorLines { get; set; }

    /// <summary>TEXTEXPAND1, <see cref="CellKind.Input"/> only: the field takes focus, caret moves, selection and copy, but no edit.</summary>
    internal bool ReadOnly { get; set; }

    /// <summary>
    /// KIT1: caption size in place of the kind's own (<see cref="FontSize"/>), or 0 for the kind's
    /// default. The head block's display title is the reason this exists: 34 px on a
    /// <see cref="CellKind.Text"/> cell whose kind size is 20.
    /// </summary>
    internal float FontSizeOverride { get; set; }

    /// <summary>KIT1: where the cell sits in a row taller than it is. Middle unless asked.</summary>
    internal CellVAlign VAlign { get; set; } = CellVAlign.Middle;

    /// <summary>
    /// SETTINGS1 stage 3: a <see cref="CellKind.Switch"/> cell that can be pressed (the mod page's
    /// on/off switch once it works). It keeps the track-and-knob drawing and gains a click. Ignored
    /// on every other kind (a button is already clickable).
    /// </summary>
    internal bool Clickable { get; set; }

    /// <summary>
    /// KEYS3: how the runtime draws the cell. <see cref="CellSkin.Kit"/> for every cell but the
    /// Keybinds tab's key caps (<see cref="CellSkin.KeyCap"/>, a <see cref="CellKind.Button"/> drawn with
    /// the game's own key-cap sprite when one can be borrowed) and its description ornament
    /// (<see cref="CellSkin.Ornament"/>, an <see cref="CellKind.Image"/> with no placeholder fill or rim).
    /// </summary>
    internal CellSkin Skin { get; set; }

    /// <summary>
    /// KEYS3 polish: a button cell that reports the pointer entering and leaving it
    /// (<see cref="IUiCellRow.OnCellHover"/>), through the same pointer trigger and Bootstrap slot a row uses.
    /// </summary>
    internal bool Hover { get; set; }

    /// <summary>
    /// INSTALLSIDES1: a cell drawn over the right end of another cell (that cell's key) instead of in
    /// a slot of its own. It takes no width from the row, so every other column keeps its place; its
    /// own width is its <see cref="CellWidth.Px"/> ask. Null for every ordinary cell.
    /// </summary>
    internal string? OverlayOf { get; set; }

    /// <summary>
    /// C5, an overlay (<see cref="OverlayOf"/> a <see cref="CellWrap.TwoLine"/> cell) only: drawn inline on its target's
    /// second line, right after this many characters of the note, instead of at the target's right end. The note leaves
    /// the overlay's own width (<see cref="CellWidth.Px"/>) free there, so the rest of the line moves right, and is clipped
    /// that much sooner. When even the text before it does not fit whole, the overlay is hidden and the note is drawn as
    /// it would be without it (<see cref="CellLayout.InlineNoteLine"/>). -1 (the default) for an ordinary overlay.
    /// </summary>
    internal int InlineAfter { get; set; } = -1;

    /// <summary>
    /// P7b chips, an ordinary overlay (<see cref="OverlayOf"/>, not inline) only: drawn at its target's left end instead of
    /// its right (a chip's icon inside the chip art). False for every other overlay.
    /// </summary>
    internal bool OverlayLeft { get; set; }

    /// <summary>P7b chips, an ordinary overlay only: units between the overlay and its target's edge (0: flush).</summary>
    internal float OverlayInset { get; set; }

    /// <summary>
    /// P7b chips, a <see cref="CellSkin.Chip"/> button only: the caption's left and right margins inside the chip in place
    /// of half the button pad each (0: the default), so a caption is centered between an icon overlay and a remove
    /// overlay. A chip without overlays keeps the default and is centered on the whole chip art.
    /// </summary>
    internal float CaptionInsetLeft { get; set; }

    /// <inheritdoc cref="CaptionInsetLeft"/>
    internal float CaptionInsetRight { get; set; }

    /// <summary>INSTALLSIDES1: the height the runtime gives this cell in place of its kind's own (0: the kind's).</summary>
    internal float HeightOverride { get; set; }

    /// <summary>INSTALLSIDES1: a <see cref="CellKind.ScrollText"/> cell's lines.</summary>
    internal IReadOnlyList<CellLine> Lines { get; set; } = Array.Empty<CellLine>();

    /// <summary>
    /// JOINLINKS1: a <see cref="CellKind.Text"/> cell (not <see cref="CellWrap.TwoLine"/>), or the
    /// <see cref="CellLineKind.Body"/> lines of a <see cref="CellKind.ScrollText"/> cell, draws the web links
    /// in its (untrusted, plain) text pale blue and underlined (<see cref="UiLinks"/>) and takes clicks:
    /// <see cref="IUiCellRow.OnClick"/> on the cell, then <see cref="IUiCellRow.LinkAt"/> names the link
    /// under the pointer. Everything else in the text stays escaped. Links are found again on every
    /// write, so a rewritten text (a ticking countdown) keeps them right.
    /// </summary>
    internal bool Linkify { get; set; }

    /// <summary>
    /// JOINDRAFT1: the text is struck through (a staged removal): a <see cref="CellKind.Text"/> or
    /// <see cref="CellKind.Muted"/> cell's whole text, a <see cref="CellWrap.TwoLine"/> cell's first line.
    /// </summary>
    internal bool Strike { get; set; }

    /// <summary>JOINLINKS1: the links this cell draws now, in order (all <see cref="CellLineKind.Body"/> lines, for a scroll block).</summary>
    internal IReadOnlyList<UiTextLink> LinksNow()
    {
        if (!Linkify) return Array.Empty<UiTextLink>();
        if (Kind == CellKind.Text && Wrap != CellWrap.TwoLine) return UiLinks.Find(Text);
        if (Kind != CellKind.ScrollText) return Array.Empty<UiTextLink>();
        var all = new List<UiTextLink>();
        foreach (var line in Lines)
        {
            if (line.Kind != CellLineKind.Body) continue;
            foreach (var l in UiLinks.Find(line.Text))
                all.Add(new UiTextLink(all.Count, l.Start, l.Length, l.Text, l.Url));
        }

        return all;
    }

    /// <summary>
    /// INSTALLSIDES2 item 4: a <see cref="CellKind.ScrollText"/> cell that never scrolls — no scroll
    /// bar, no wheel; its lines are clipped to the cell (the compact drawer, whose text is already
    /// clamped). The mouse wheel over it scrolls the list.
    /// </summary>
    internal bool NoScroll { get; set; }

    /// <summary>INSTALLSIDES2 item 1: the per-side column this cell belongs to (the band and the button tint).</summary>
    internal CellColumn Column { get; set; }

    /// <summary>
    /// VANILLA1: a header caption kept in its own (title) case and drawn in small caps, the way the
    /// game's menu tabs draw <c>Emotes</c> / <c>Vampire Powers</c>, instead of the kit's all-caps
    /// caption. At <see cref="FontSizeOverride"/> when set. False keeps the kit's upper case.
    /// </summary>
    internal bool SmallCaps { get; set; }

    /// <summary>
    /// INSTALLSIDES2 item 2: the cell's side is not used by this mod (or no server is picked): the
    /// column band is drawn grayed (a dim fill) and the words muted.
    /// </summary>
    internal bool Grayed { get; set; }

    /// <summary>
    /// INSTALLSIDES2 item 1: a mark drawn before the cell's words (<c>✓</c>, <c>↑</c>, a lock). The
    /// runtime draws it only when the game's font has every character of it; otherwise the words
    /// stand alone. Empty for none.
    /// </summary>
    internal string Glyph { get; set; } = "";

    /// <summary>
    /// TILES1: the bordered tile this cell belongs to (<see cref="CellTiles"/>; 0: none). The runtime draws one framed
    /// box behind every cell of a tile, from the left of its first cell to the right of its last.
    /// </summary>
    internal int Tile { get; set; }

    /// <summary>Fluent setters for the row builders.</summary>
    internal CellSpec With(string? text = null, CellWidth? width = null, CellAlign? align = null, CellTone? tone = null,
        CellWrap? wrap = null, bool? optional = null, float? minWidth = null, string? sortKey = null, string? placeholder = null,
        float? fontSize = null, CellVAlign? vAlign = null, CellSkin? skin = null)
    {
        if (skin != null) Skin = skin.Value;
        if (text != null) Text = text;
        if (width != null) Width = width.Value;
        if (align != null) Align = align.Value;
        if (tone != null) Tone = tone.Value;
        if (wrap != null) Wrap = wrap.Value;
        if (optional != null) Optional = optional.Value;
        if (minWidth != null) MinWidth = minWidth.Value;
        if (sortKey != null) SortKey = sortKey;
        if (placeholder != null) Placeholder = placeholder;
        if (fontSize != null) FontSizeOverride = fontSize.Value;
        if (vAlign != null) VAlign = vAlign.Value;
        return this;
    }

    /// <summary>Caption font size for the kind (the mock's 19/14/13/12/14 px ladder on the 22 px body), or the explicit <see cref="FontSizeOverride"/>.</summary>
    internal float FontSize => FontSizeOverride > 0f ? FontSizeOverride : KindFontSize;

    private float KindFontSize => Kind switch
    {
        CellKind.Text => Wrap == CellWrap.TwoLine ? CellLayout.TwoLineTitleFontSize : CellLayout.TextFontSize,
        CellKind.Muted => CellLayout.MutedFontSize,
        CellKind.Mono => CellLayout.MonoFontSize,
        CellKind.Badge => CellLayout.BadgeFontSize,
        CellKind.Button => MenuPanelLayout.HeaderButtonFontSize,
        CellKind.Chevron => CellLayout.ChevronFontSize,
        CellKind.Input => CellLayout.InputFontSize,
        _ => CellLayout.TextFontSize,
    };

    public override string ToString() => Kind + ":" + Key + "(" + Width + ")";
}

/// <summary>A full-width row of positioned cells.</summary>
internal sealed class CellRowSpec
{
    internal CellRowSpec(
        string key, IReadOnlyList<CellSpec> cells, bool twoLine = false, float height = 0f, float rightInset = 0f,
        bool band = false, bool plain = false, bool soft = false, bool hover = false, CellRowSkin skin = CellRowSkin.Kit,
        bool joinAbove = false, float indent = 0f, bool separator = false, bool quietHover = false,
        CellSection section = CellSection.None, CellSectionEdge sectionEdge = CellSectionEdge.Middle,
        bool compact = false, float gapAbove = -1f, bool fitWrap = false)
    {
        Compact = compact;
        FitWrap = fitWrap;
        GapAbove = float.IsNaN(gapAbove) || float.IsInfinity(gapAbove) || gapAbove < 0f ? -1f : gapAbove;
        Section = section;
        SectionEdge = sectionEdge;
        QuietHover = quietHover;
        Separator = separator;
        JoinAbove = joinAbove;
        Indent = float.IsNaN(indent) || float.IsInfinity(indent) || indent <= 0f ? 0f : indent;
        Hover = hover;
        Skin = skin;
        Soft = soft;
        Band = band;
        Plain = plain;
        Key = key ?? "";
        Cells = cells ?? Array.Empty<CellSpec>();
        TwoLine = twoLine;
        ExplicitHeight = float.IsNaN(height) || float.IsInfinity(height) || height <= 0f ? 0f : height;
        RightInset = float.IsNaN(rightInset) || float.IsInfinity(rightInset) || rightInset <= 0f ? 0f : rightInset;
    }

    /// <summary>
    /// HEAD2: width at the right-hand end of the row that no cell may enter — the pane's scrollbar
    /// band (<see cref="HeadBlockLayout.RightColumnInset"/>). A row's cells are placed inside
    /// <c>rowWidth</c>, and a row is exactly as wide as the clipped viewport it sits in, so a cell
    /// packed flush against the row's right edge is drawn on the boundary of the clip: live at
    /// 3840×1080 the mod page's Uninstall button and its <c>Server</c> badge ended at the viewport
    /// edge and the player saw the button's right-hand side cut off under the scrollbar
    /// ("the uninstall button is too far to the right, it is getting cropped"). Zero for every row
    /// that wants the whole width, which is every row but a head block's.
    /// </summary>
    internal float RightInset { get; }

    /// <summary>
    /// INSTALLSIDES1: the row continues the row above it (the Install drawer under its listing). While
    /// selected, its fill and its accent rail reach up through the gap between the two rows, so the
    /// row, the drawer head and the drawer body read as one block with one rail down its left edge;
    /// the drawer's own content starts at <see cref="Indent"/>, where a thin seam line joins it.
    /// </summary>
    internal bool JoinAbove { get; }

    /// <summary>INSTALLSIDES1: where a joined row's seam starts, from the row's left edge (0: no seam).</summary>
    internal float Indent { get; }

    /// <summary>
    /// JOINLINKS1: the tinted section band the row belongs to (<see cref="CellSection.None"/> for none). The
    /// row's fill is the section's, a row that is not its section's first also fills the gap above it, and
    /// the section's rim runs down both sides (plus the top on <see cref="CellSectionEdge.First"/> and the
    /// bottom on <see cref="CellSectionEdge.Last"/>). Build rows of a section with <see cref="InSection"/>.
    /// </summary>
    internal CellSection Section { get; }

    /// <inheritdoc cref="Section"/>
    internal CellSectionEdge SectionEdge { get; }

    /// <summary>JOINLINKS1: the inner padding a section row's cells keep from its rim, each side.</summary>
    internal const float SectionPad = 12f;

    /// <summary>
    /// JOINLINKS1: this row inside a section band: the same cells behind a <see cref="SectionPad"/> spacer,
    /// the same right padding (<see cref="RightInset"/>), and every other facet unchanged.
    /// </summary>
    internal CellRowSpec InSection(CellSection section, CellSectionEdge edge)
    {
        var cells = new List<CellSpec>(Cells.Count + 1)
        {
            new CellSpec(CellKind.Spacer, "section-pad").With(width: CellWidth.Px(SectionPad)),
        };
        cells.AddRange(Cells);
        return new CellRowSpec(Key, cells, TwoLine, ExplicitHeight, Math.Max(RightInset, SectionPad), Band, Plain, Soft, Hover, Skin,
            JoinAbove, Indent, Separator, QuietHover, section, edge, Compact, GapAbove, FitWrap)
        {
            FitWrapFloor = FitWrapFloor,
            FitWrapCeiling = FitWrapCeiling,
        };
    }

    /// <summary>
    /// RADIOCLICK1: the panel reserves exactly <see cref="Height"/> for this row instead of raising it to the
    /// ordinary row floor (<see cref="PanelLayout.MinRowHeight"/>, 40 px). A radio list's option line and its
    /// explanation are compact; without it each took 40 px and the list read as loose.
    /// </summary>
    internal bool Compact { get; }

    /// <summary>
    /// RADIOCLICK2: the runtime sizes the row to its wrapping cell's measured text (TMP's preferred height at the
    /// cell's placed width, plus <see cref="FitWrapPad"/>) instead of <see cref="Height"/>, which is then only the
    /// game-free estimate. The estimate (<see cref="MenuPanelLayout.WrappedLines"/>) over-counts: live, a one-line
    /// radio explanation reserved two lines. Bounded by <see cref="FitWrapHeight"/>.
    /// </summary>
    internal bool FitWrap { get; }

    /// <summary>RADIOCLICK2: what a <see cref="FitWrap"/> row adds to its text's measured height.</summary>
    internal const float FitWrapPad = 2f;

    /// <summary>
    /// RADIOCLICK2: a <see cref="FitWrap"/> row's height for a measured text height: the text plus
    /// <see cref="FitWrapPad"/>, at least <paramref name="minText"/> (one line), at most twice the estimate. An unreadable
    /// measurement (zero, NaN) keeps the estimate.
    /// </summary>
    internal float FitWrapHeight(float measuredText, float minText)
    {
        if (!FitWrap || float.IsNaN(measuredText) || float.IsInfinity(measuredText) || measuredText <= 0.5f) return Height;
        float h = Math.Max(minText, measuredText) + FitWrapPad;
        float ceiling = FitWrapCeiling > 0f ? FitWrapCeiling : Height * 2f;
        return Math.Max(FitWrapFloor, Math.Min(h, ceiling));
    }

    /// <summary>
    /// FACTWRAP1: the most a <see cref="FitWrap"/> row is fitted to, in place of twice the estimate (0: twice the
    /// estimate). A dialog fact's value under-estimated as one line drew "every 3 days o…"; its row now grows with the
    /// measured text up to <see cref="DialogLayout.MaxFactLines"/> lines.
    /// </summary>
    internal float FitWrapCeiling { get; init; }

    /// <summary>
    /// TILES1: which edges of its tiles this row draws (<see cref="CellTiles"/>): the top rim on
    /// <see cref="CellSectionEdge.First"/>, the bottom on <see cref="CellSectionEdge.Last"/>; a row that is not a
    /// first also fills its gap above, so a tile's head and note rows read as one box. Only cells with a
    /// <see cref="CellSpec.Tile"/> are framed.
    /// </summary>
    internal CellSectionEdge TileEdge { get; init; }

    /// <summary>
    /// P7b: this row's tiles are not TILES1 frames but state-driven fills (<see cref="UiTileState"/>, set with
    /// <c>UiCellRowHandle.SetTileState</c>): hidden until asked, a list entry's hover or selection band confined to the
    /// tile's cells, or a thin rule down its center joined to the row above (unless <see cref="TileEdge"/> has
    /// <see cref="CellSectionEdge.First"/>). The master-detail widget lights its master half with it.
    /// </summary>
    internal bool TileStates { get; init; }

    /// <summary>
    /// PENDING3 (live 2026-10-06: with nothing pending, the Pending changes label vanished): the least height a
    /// <see cref="FitWrap"/> row is fitted to, so its other one-line cells (a row's name) still fit. A one-line wrapped
    /// value measured about 22 px; the 18 px name in that row was clipped by its ellipsis to nothing.
    /// </summary>
    internal float FitWrapFloor { get; init; }

    /// <summary>
    /// RADIOCLICK1: the space between the row above and this one, in place of <see cref="PanelLayout.RowGap"/>
    /// (-1: the ordinary gap). A radio option's explanation sits <see cref="UiRadioList.DescriptionGap"/> under its
    /// label; the next option starts <see cref="UiRadioList.OptionGap"/> below the one before.
    /// </summary>
    internal float GapAbove { get; }

    /// <summary>
    /// RADIOCLICK1: whether the runtime builds a row with its whole-row click target. A header, a band and a plain
    /// row get none (and no hover or press tint); <see cref="IUiCellRow.OnRowClick"/> adds one to such a row on
    /// demand (<see cref="IRuntimeCellRow.EnsureRowClick"/>), still without a tint.
    /// </summary>
    internal static bool RowClickAtBuild(CellRowSpec spec, bool header) => !header && !spec.Band && !spec.Plain;

    /// <summary>
    /// VANILLA1: the row draws a one-pixel rule along its top edge in
    /// <see cref="MenuPalette.RowSeparator"/> — the faint line the game's settings list shows between
    /// entries (the resting <c>ListButton_TopGradient</c>, measured as a steel line). False draws none.
    /// </summary>
    internal bool Separator { get; }

    /// <summary>
    /// VANILLA1 round 2: an unselected row lights under the pointer with
    /// <see cref="MenuPalette.RowHoverQuiet"/> (the game's entry hover hue at a low alpha) instead of
    /// the kit's or the soft row's hover, so every text tone on it stays readable. The selected
    /// colors are unchanged.
    /// </summary>
    internal bool QuietHover { get; }

    /// <summary>
    /// SETTINGS1 stage 2: the row is a solid band (the mod page's framework status bar): an opaque
    /// fill from the existing <see cref="MenuPalette.RowHover"/> and no row-wide click or hover
    /// tint. False for every other row.
    /// </summary>
    internal bool Band { get; }

    /// <summary>
    /// DIALOGS1: the row is not a list row — no row-wide click, no hover or press tint, and no fill
    /// (<see cref="MenuPalette.RowFill"/> is transparent). A dialog's title-free rows (its buttons, its
    /// text field, its option list) are built on these so the row itself never lights up under the
    /// pointer; only its cells take clicks. RADIOCLICK1: a plain row whose caller asks for a row click
    /// (<see cref="IUiCellRow.OnRowClick"/>, a radio list's line) gets a click target then, still without a tint.
    /// </summary>
    internal bool Plain { get; }

    /// <summary>
    /// SETTINGS1 stage 3 (the Mods list redesign): a list row whose hover is a subtle light tint and
    /// whose selected state is a soft accent-tinted fill beside the slim left accent bar, instead of
    /// the near-black hover and the solid selected fill every other cell row uses. The colors are
    /// derived from existing palette entries (<see cref="MenuPalette.Text"/>, <see cref="MenuPalette.Gold"/>)
    /// at a low alpha; see <see cref="ModsListLayout.HoverAlpha"/>.
    /// </summary>
    internal bool Soft { get; }

    /// <summary>
    /// KEYS3: the row reports the pointer entering and leaving it (<see cref="IUiCellRow.OnHover"/>).
    /// The runtime adds the kit's existing pointer trigger (the one buttons already carry) to the row
    /// object. False for every other row, which then has no trigger at all.
    /// </summary>
    internal bool Hover { get; }

    /// <summary>
    /// KEYS3: <see cref="CellRowSkin.GameEntry"/> draws the selected state as the game's settings-entry
    /// highlight band (borrowed, else the kit's selected fill without the gold stripe).
    /// </summary>
    internal CellRowSkin Skin { get; }

    /// <summary>
    /// KIT1: the row height the caller asked for, or 0 for the kind's own. A row's height never
    /// changes after creation (the panel measures it once through <c>MeasureRowHeight</c>), so a
    /// row with a <see cref="CellWrap.Wrap"/> cell has to be born tall enough for the wrapped text.
    /// </summary>
    internal float ExplicitHeight { get; }

    /// <summary>Unique within the panel: the row-click and readback address (LIST2 uses the listing's full name).</summary>
    internal string Key { get; }

    internal IReadOnlyList<CellSpec> Cells { get; }

    /// <summary>53 px row for name + one-liner; otherwise <see cref="UnityUiRuntimeMetrics.RowHeight"/>.</summary>
    internal bool TwoLine { get; }

    internal float Height => ExplicitHeight > 0f
        ? ExplicitHeight
        : TwoLine ? CellLayout.TwoLineRowHeight : UnityUiRuntimeMetrics.RowHeight;

    internal CellSpec? Find(string? key)
    {
        if (key == null) return null;
        for (int i = 0; i < Cells.Count; i++)
        {
            if (string.Equals(Cells[i].Key, key, StringComparison.Ordinal)) return Cells[i];
        }
        return null;
    }
}

/// <summary>One resolved cell: center x (row-relative) and width. <see cref="Visible"/> is false for hidden and dropped cells.</summary>
internal readonly struct CellPlacement
{
    internal CellPlacement(string key, int index, float x, float width, bool visible, bool dropped)
    {
        Key = key;
        Index = index;
        X = x;
        Width = width;
        Visible = visible;
        Dropped = dropped;
    }

    internal string Key { get; }
    internal int Index { get; }
    /// <summary>Center, offset from the row's center (positive = right).</summary>
    internal float X { get; }
    internal float Width { get; }
    internal bool Visible { get; }
    /// <summary>An optional cell the model dropped because the row was too narrow.</summary>
    internal bool Dropped { get; }
    internal float Left => X - Width * .5f;
    internal float Right => X + Width * .5f;

    public override string ToString() => Key + "@" + Left.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
        + ".." + Right.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + (Visible ? "" : Dropped ? " dropped" : " hidden");
}

/// <summary>
/// The column model behind <see cref="MenuPanelLayout.CellColumns"/>. Pure arithmetic, no
/// Unity: px / fraction / auto widths resolved left to right with <see cref="Gap"/> between,
/// right-aligned cells packed at the right edge, a spacer absorbing the remainder, minimum
/// widths enforced, and optional cells dropped from the right before any caption is squeezed
/// under its readable floor. UIHDR2's pinned header worked because its cells were positioned
/// from an explicit model like this one; the two inline-chip attempts (gen 40 / gen 42) that
/// composed existing controls did not.
/// </summary>
internal static class CellLayout
{
    /// <summary>Between cells; <see cref="SettingsAlign.InlineGap"/>.</summary>
    internal const float Gap = SettingsAlign.InlineGap;

    /// <summary>
    /// GRIDROW1: a blank lead cell's width that starts the next cell on the settings grid's name inset
    /// (<see cref="ModSettingsRowGrid.NameInset"/>), the cell gap after it included.
    /// </summary>
    internal static float GridNameLeadWidth => Math.Max(0f, ModSettingsRowGrid.NameInset - Gap);

    /// <summary>
    /// GRIDROW1: a <see cref="CellWidthMode.GridName"/> caption's width in a row <paramref name="rowWidth"/> wide: from the
    /// name inset to one cell gap before the grid's control column, so the cell after it starts on that column.
    /// </summary>
    internal static float GridNameWidth(float rowWidth) =>
        Math.Max(1f, ModSettingsRowGrid.For(rowWidth).ControlLeft - ModSettingsRowGrid.NameInset - Gap);

    /// <summary>Two-line row (36 px icon + name + one-liner) from the mock.</summary>
    internal const float TwoLineRowHeight = 53f;

    /// <summary>INSTALLSIDES2 item 4: where a fact line's second fact starts, in percent of the block's width.</summary>
    internal const float FactSecondColumnPercent = 50f;

    /// <summary>Listing icon square.</summary>
    internal const float ImageSize = 36f;

    internal const float ChevronWidth = 20f;
    internal const float ChevronFontSize = 14f;
    internal const float TextFontSize = 20f;
    internal const float TwoLineTitleFontSize = 19f;
    internal const float TwoLineNoteFontSize = 14f;

    /// <summary>C5: the loop-arrows glyph's drawn height (<see cref="CellSkin.LoopGlyph"/>), about the second line's cap height.</summary>
    internal const float LoopGlyphHeight = 11f;

    /// <summary>C5: its drawn width, at the glyph's own aspect (<see cref="LoopGlyph.TextureWidth"/> : <see cref="LoopGlyph.TextureHeight"/>).</summary>
    internal const float LoopGlyphWidth = LoopGlyphHeight * LoopGlyph.TextureWidth / LoopGlyph.TextureHeight;

    /// <summary>INSTALLSIDES1: the <see cref="CellKind.ScrollText"/> line sizes and spacing.</summary>
    internal const float ScrollBodyFontSize = 17f;

    /// <inheritdoc cref="ScrollBodyFontSize"/>
    internal const float ScrollHeadingFontSize = 20f;

    /// <inheritdoc cref="ScrollBodyFontSize"/>
    internal const float ScrollMutedFontSize = 15f;

    /// <summary>Vertical space between two lines of a scroll block.</summary>
    internal const float ScrollLineGap = 6f;

    /// <summary>Inset of a scroll block's text from its edges (the scroll bar sits in the right inset).</summary>
    internal const float ScrollPad = 10f;

    /// <summary>The scroll block's bar width (the game's thin bar).</summary>
    internal const float ScrollBarWidth = 6f;
    internal const float MutedFontSize = 16f;
    internal const float MonoFontSize = 16f;
    internal const float BadgeFontSize = 14f;

    /// <summary>Side padding inside a badge (both sides together) and inside a button caption.</summary>
    internal const float BadgePad = 16f;
    internal const float ButtonPad = MenuPanelLayout.HeaderButtonCaptionPad;

    /// <summary>Readable floor of a text cell; the model drops optional cells before going under it.</summary>
    internal const float MinTextWidth = 72f;
    internal const float MinButtonWidth = 96f;
    internal const float MinBadgeWidth = 48f;

    /// <summary>An input cell (LIST3): the settings-field face, its inner padding, and the narrowest field a placeholder stays readable in.</summary>
    internal const float InputFontSize = 18f;
    internal const float InputPad = 16f;
    internal const float MinInputWidth = 120f;

    /// <summary>SETTINGS1 stage 2: a <see cref="CellKind.Switch"/> cell's fixed width (track and knob).</summary>
    internal const float SwitchWidth = 44f;

    /// <summary>SETTINGS1 stage 2: a switch track's height inside its row.</summary>
    internal const float SwitchHeight = 22f;

    /// <summary>SETTINGS1 stage 3: the status diamond's cell width (the turned square plus air).</summary>
    internal const float DiamondWidth = 22f;

    /// <summary>SETTINGS1 stage 3: the side of the square that is turned 45 degrees (its diagonal is about 15 px).</summary>
    internal const float DiamondSide = 11f;

    /// <summary>SETTINGS1 stage 3: a hollow diamond's outline thickness.</summary>
    internal const float DiamondOutline = 2f;

    /// <summary>The mock's Install button column: `min-width:190px` at its 14 px face, 1.12× at ours.</summary>
    internal const float ButtonWidth = 200f;

    /// <summary>Mean glyph width of the monospace treatment as a fraction of the font size (TMP <c>mspace</c>).</summary>
    internal const float MonoGlyphEm = 0.62f;

    /// <summary>Header caption (uppercase, 12 px letter-spaced in the mock).</summary>
    internal const float HeaderFontSize = 13f;

    /// <summary>
    /// The sorted-column indicator appended to a header caption and to the toolbar's sort
    /// button. U+25BC (a WGL4 glyph), not U+25BE: the gen 8 capture drew the small triangle
    /// as TMP's missing-glyph box (<c>Downloads ☐</c>) and pushed <c>DOWNLOADS</c> into an
    /// ellipsis (LIST3 defects 4 and 5). One constant so both places change together.
    /// </summary>
    internal const string SortIndicator = " ▼";

    /// <summary>
    /// INSTALLCOLS1: the indicator of a column sorted ascending (A to Z, oldest first, fewest
    /// first). U+25B2, WGL4 like <see cref="SortIndicator"/>, which stays the descending mark.
    /// Same length as <see cref="SortIndicator"/>, so the runtime's tone split holds for both.
    /// </summary>
    internal const string SortIndicatorAscending = " ▲";

    /// <summary>
    /// Width a caption is expected to take at <paramref name="fontSize"/>: the same
    /// <see cref="MenuPanelLayout.AverageGlyphEm"/> estimate <see cref="MenuPanelLayout.ClipLabel"/>
    /// bounds captions with, a wider glyph for the monospace treatment.
    /// </summary>
    internal static float EstimateTextWidth(string? text, float fontSize, bool mono = false)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        int longest = 0;
        foreach (var line in text.Replace("\r", "").Split('\n'))
            longest = Math.Max(longest, line.Length);
        float em = mono ? MonoGlyphEm : MenuPanelLayout.AverageGlyphEm;
        return longest * fontSize * em;
    }

    /// <summary>Width an auto cell asks for: the caption plus the kind's padding.</summary>
    internal static float AutoWidth(CellSpec spec)
    {
        switch (spec.Kind)
        {
            case CellKind.Image: return ImageSize;
            case CellKind.Chevron: return ChevronWidth;
            case CellKind.Spacer: return 0f;
            case CellKind.Badge:
                return Math.Max(MinBadgeWidth, EstimateTextWidth(spec.Text, spec.FontSize) + BadgePad);
            case CellKind.Button:
                return Math.Max(ButtonWidth, EstimateTextWidth(spec.Text, spec.FontSize) + ButtonPad);
            case CellKind.Input:
                return Math.Max(MinInputWidth, EstimateTextWidth(spec.Placeholder, spec.FontSize) + InputPad);
            case CellKind.Switch:
                return SwitchWidth;
            case CellKind.Diamond:
                return DiamondWidth;
            case CellKind.Mono:
                return Math.Max(MinTextWidth, EstimateTextWidth(spec.Text, spec.FontSize, mono: true) + 4f);
            default:
                return Math.Max(MinTextWidth, EstimateTextWidth(spec.Text, spec.FontSize) + 4f);
        }
    }

    /// <summary>The floor a cell is never shrunk below.</summary>
    internal static float MinWidthOf(CellSpec spec)
    {
        if (spec.MinWidth > 0f) return spec.MinWidth;
        return spec.Kind switch
        {
            CellKind.Image => ImageSize,
            CellKind.Chevron => ChevronWidth,
            CellKind.Spacer => 0f,
            CellKind.Badge => MinBadgeWidth,
            CellKind.Button => MinButtonWidth,
            CellKind.Input => MinInputWidth,
            CellKind.Switch => SwitchWidth,
            CellKind.Diamond => DiamondWidth,
            _ => MinTextWidth,
        };
    }

    /// <summary>True when <paramref name="spec"/>'s caption fits <paramref name="width"/> without an ellipsis.</summary>
    internal static bool CaptionFits(CellSpec spec, float width)
    {
        float pad = spec.Kind switch
        {
            CellKind.Button => ButtonPad,
            CellKind.Badge => BadgePad,
            CellKind.Input => InputPad,
            _ => 4f,
        };
        string text = spec.Kind == CellKind.Input && string.IsNullOrEmpty(spec.Text) ? spec.Placeholder : spec.Text;
        return EstimateTextWidth(text, spec.FontSize, spec.Kind == CellKind.Mono) <= width - pad + .01f;
    }

    /// <summary>
    /// Resolve <paramref name="specs"/> for a row <paramref name="rowWidth"/> wide. One entry per
    /// spec, in order. Never throws; a degenerate request answers hidden cells.
    ///
    /// <para><paramref name="rightInset"/> (HEAD2, <see cref="CellRowSpec.RightInset"/>) is width at
    /// the right-hand end of the row that no cell enters: the cells are fitted into
    /// <c>rowWidth - rightInset</c> and the right-aligned group is packed from that edge rather than
    /// from the row's own. Fractions are still fractions of the whole row, so a caller's
    /// <see cref="CellWidth.Fraction"/> keeps meaning what it says.</para>
    /// </summary>
    internal static CellPlacement[] Resolve(
        float rowWidth, IReadOnlyList<CellSpec> specs, float rightInset = 0f)
    {
        if (specs == null || specs.Count == 0) return Array.Empty<CellPlacement>();
        rowWidth = MenuPanelLayout.Positive(rowWidth, UnityUiRuntimeMetrics.RowWidth);
        // A degenerate or absurd inset is no inset: the row still lays out, flush, rather than
        // collapsing every cell into a sliver.
        float inset = float.IsNaN(rightInset) || float.IsInfinity(rightInset) || rightInset <= 0f
            ? 0f
            : Math.Min(rightInset, rowWidth * .5f);
        float budget = Math.Max(1f, rowWidth - inset);
        int n = specs.Count;
        var width = new float[n];
        var live = new bool[n];
        var dropped = new bool[n];
        var flexible = new bool[n];
        // INSTALLSIDES1: overlay cells take no slot; they are placed over their target at the end.
        for (int i = 0; i < n; i++) live[i] = specs[i].Visible && specs[i].OverlayOf == null;

        // Drop optional cells from the right until the fixed asks fit.
        while (true)
        {
            float need = Measure(rowWidth, specs, live, width, flexible);
            if (need <= budget + .01f) break;
            int victim = NextDrop(specs, live);
            if (victim < 0) break;
            live[victim] = false;
            dropped[victim] = true;
        }

        // Still too wide: shrink flexible (fraction / auto text) cells towards their floors, evenly.
        float total = Measure(rowWidth, specs, live, width, flexible);
        if (total > budget + .01f)
        {
            float over = total - budget;
            while (over > .01f)
            {
                float slack = 0f;
                int count = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!live[i] || !flexible[i]) continue;
                    float room = width[i] - MinWidthOf(specs[i]);
                    if (room > .01f) { slack += room; count++; }
                }
                if (count == 0 || slack <= 0f) break;
                float take = Math.Min(over, slack);
                for (int i = 0; i < n; i++)
                {
                    if (!live[i] || !flexible[i]) continue;
                    float room = width[i] - MinWidthOf(specs[i]);
                    if (room <= .01f) continue;
                    width[i] -= take * (room / slack);
                }
                over -= take;
            }
        }

        // Remainder goes to the fill cell(s) (INSTALLCOLS1), else to the spacer(s); with neither it
        // stays as air between the left and right groups.
        float used = Measure(rowWidth, specs, live, width, flexible, keepWidths: true);
        float remainder = Math.Max(0f, budget - used);
        int fills = 0;
        for (int i = 0; i < n; i++)
            if (live[i] && IsFill(specs[i])) fills++;
        if (fills > 0)
        {
            float each = remainder / fills;
            for (int i = 0; i < n; i++)
                if (live[i] && IsFill(specs[i])) width[i] += each;
        }
        else
        {
            int spacers = 0;
            for (int i = 0; i < n; i++)
                if (live[i] && specs[i].Kind == CellKind.Spacer) spacers++;
            if (spacers > 0)
            {
                float each = remainder / spacers;
                for (int i = 0; i < n; i++)
                    if (live[i] && specs[i].Kind == CellKind.Spacer) width[i] = each;
            }
        }

        // Pack: left/centre cells from the left edge, right cells from the right edge, in order.
        var result = new CellPlacement[n];
        float left = -rowWidth * .5f;
        float cursor = left;
        bool first = true;
        for (int i = 0; i < n; i++)
        {
            if (!live[i] || specs[i].Align == CellAlign.Right) continue;
            if (!first) cursor += Gap;
            first = false;
            result[i] = new CellPlacement(specs[i].Key, i, cursor + width[i] * .5f, width[i], true, false);
            cursor += width[i];
        }
        float rightCursor = rowWidth * .5f - inset;
        bool firstRight = true;
        for (int i = n - 1; i >= 0; i--)
        {
            if (!live[i] || specs[i].Align != CellAlign.Right) continue;
            if (!firstRight) rightCursor -= Gap;
            firstRight = false;
            result[i] = new CellPlacement(specs[i].Key, i, rightCursor - width[i] * .5f, width[i], true, false);
            rightCursor -= width[i];
        }
        for (int i = 0; i < n; i++)
        {
            if (live[i]) continue;
            result[i] = new CellPlacement(specs[i].Key, i, 0f, 0f, false, dropped[i]);
        }

        // INSTALLSIDES1: an overlay sits at the right end of its target, never wider than it.
        for (int i = 0; i < n; i++)
        {
            var spec = specs[i];
            if (spec.OverlayOf == null || !spec.Visible) continue;
            int target = -1;
            for (int j = 0; j < n; j++)
            {
                if (j != i && specs[j].OverlayOf == null && string.Equals(specs[j].Key, spec.OverlayOf, StringComparison.Ordinal))
                {
                    target = j;
                    break;
                }
            }

            if (target < 0 || !result[target].Visible) continue;
            float ask = spec.Width.Mode == CellWidthMode.Px ? spec.Width.Value : AutoWidth(spec);
            float w = Math.Min(Math.Max(1f, ask), result[target].Width);
            if (spec.InlineAfter >= 0)
            {
                // C5: inline on the target's second line, after the text before it. The runtime measures that text with
                // the drawn font and moves the cell to the measured spot; this estimate is the same rule's.
                if (!InlinePlacement(specs[target], result[target], spec.InlineAfter, w, out float inlineLeft))
                {
                    result[i] = new CellPlacement(spec.Key, i, 0f, 0f, false, false);
                    continue;
                }

                result[i] = new CellPlacement(spec.Key, i, inlineLeft + w * .5f, w, true, false);
                continue;
            }

            // P7b: an overlay may sit at its target's left end, and keep an inset from that edge (inside the target).
            float edge = Math.Max(0f, Math.Min(float.IsNaN(spec.OverlayInset) ? 0f : spec.OverlayInset, result[target].Width - w));
            float center = spec.OverlayLeft ? result[target].Left + edge + w * .5f : result[target].Right - edge - w * .5f;
            result[i] = new CellPlacement(spec.Key, i, center, w, true, false);
        }

        return result;
    }

    /// <summary>
    /// C5: where an inline overlay <paramref name="width"/> wide sits on <paramref name="target"/>'s second line: the
    /// target's left, the caption's inset (<see cref="TextInset"/>) and the estimated width of the text before it. False
    /// when the note does not keep that text whole (<see cref="InlineNoteLine"/>).
    /// </summary>
    internal static bool InlinePlacement(CellSpec target, in CellPlacement placed, int after, float width, out float left)
    {
        left = 0f;
        if (target == null || target.Wrap != CellWrap.TwoLine) return false;
        TwoLineParts(target, out _, out string lead, out string note);
        if (note.Length == 0) return false;
        float noteWidth = TwoLineNoteWidth(target, placed.Width);
        string line = InlineNoteLine(lead, note, after, noteWidth, width, out int at);
        if (at < 0) return false;
        left = placed.Left + TextInset + EstimateTextWidth(line.Substring(0, at), TwoLineNoteFontSize);
        left = Math.Min(left, placed.Right - width);
        return true;
    }

    /// <summary>The runtime's caption inset inside a text cell (its TMP margin).</summary>
    internal const float TextInset = 2f;

    /// <summary>The width a two-line cell placed <paramref name="width"/> wide clips its second line to (the runtime's rule).</summary>
    internal static float TwoLineNoteWidth(CellSpec spec, float width) =>
        Math.Max(24f, width - 4f - Math.Max(0f, spec?.NoteReserve ?? 0f));

    /// <summary>
    /// A <see cref="CellWrap.TwoLine"/> cell's parts as the runtime draws them: the title (before the first newline), the
    /// toned lead (<see cref="CellSpec.NoteLead"/> and <c> · </c> when a note follows) and the muted note.
    /// </summary>
    internal static void TwoLineParts(CellSpec? spec, out string title, out string lead, out string note)
    {
        string text = spec?.Text ?? "";
        title = text;
        note = "";
        int nl = text.IndexOf('\n');
        if (nl >= 0)
        {
            title = text.Substring(0, nl);
            note = text.Substring(nl + 1).Replace('\n', ' ');
        }

        lead = spec?.NoteLead ?? "";
        if (lead.Length > 0 && note.Length > 0) lead += " · ";
    }

    /// <summary>
    /// C5: a two-line cell's second line (<paramref name="lead"/> + <paramref name="note"/>) clipped to
    /// <paramref name="noteWidth"/> with room <paramref name="reserve"/> wide kept free after the first
    /// <paramref name="after"/> characters of the note. <paramref name="at"/> is where the room goes in the answer (an index
    /// into it), or -1 when there is none: <paramref name="after"/> is outside the note, or the line clipped with the room
    /// taken out no longer keeps the text before it whole. Then the answer is the line clipped as if there were no room.
    /// The estimate is the one the runtime clips with (<see cref="MenuPanelLayout.ClipLabel"/> at
    /// <see cref="TwoLineNoteFontSize"/>), so the layout and the drawing agree.
    /// </summary>
    internal static string InlineNoteLine(string? lead, string? note, int after, float noteWidth, float reserve, out int at)
    {
        lead ??= "";
        note ??= "";
        at = -1;
        string full = lead + note;
        if (after > 0 && after <= note.Length && reserve > 0f)
        {
            string clipped = MenuPanelLayout.ClipLabel(full, noteWidth - reserve, TwoLineNoteFontSize);
            int split = lead.Length + after;
            if (clipped.Length >= split && string.CompareOrdinal(clipped, 0, full, 0, split) == 0)
            {
                at = split;
                return clipped;
            }
        }

        return MenuPanelLayout.ClipLabel(full, noteWidth, TwoLineNoteFontSize);
    }

    private static bool IsFill(CellSpec spec) =>
        spec.Kind != CellKind.Spacer && spec.Width.Mode == CellWidthMode.Fill;

    /// <summary>
    /// INSTALLCOLS1: the next optional cell to drop — the live one with the lowest positive
    /// <see cref="CellSpec.DropOrder"/> (rightmost on a tie), else the rightmost live optional cell
    /// with none. -1 when nothing is left to drop.
    /// </summary>
    private static int NextDrop(IReadOnlyList<CellSpec> specs, bool[] live)
    {
        int victim = -1;
        int best = int.MaxValue;
        for (int i = specs.Count - 1; i >= 0; i--)
        {
            if (!live[i] || !specs[i].Optional || specs[i].DropOrder <= 0) continue;
            if (specs[i].DropOrder < best) { best = specs[i].DropOrder; victim = i; }
        }
        if (victim >= 0) return victim;
        for (int i = specs.Count - 1; i >= 0; i--)
        {
            if (live[i] && specs[i].Optional) return i;
        }
        return -1;
    }

    /// <summary>
    /// Sum of the live cells' asked widths plus gaps. Fills <paramref name="width"/> with the
    /// ask (px, fraction of the row, or auto) unless <paramref name="keepWidths"/>, and marks
    /// which cells may shrink.
    /// </summary>
    private static float Measure(
        float rowWidth, IReadOnlyList<CellSpec> specs, bool[] live, float[] width, bool[] flexible, bool keepWidths = false)
    {
        float sum = 0f;
        int count = 0;
        for (int i = 0; i < specs.Count; i++)
        {
            if (!live[i]) continue;
            var spec = specs[i];
            if (!keepWidths)
            {
                float ask = spec.Width.Mode switch
                {
                    CellWidthMode.Px => spec.Width.Value,
                    CellWidthMode.Fraction => rowWidth * Clamp01(spec.Width.Value),
                    // INSTALLCOLS1: a fill cell asks only for its floor; the remainder is added last.
                    CellWidthMode.Fill => Math.Max(spec.Width.Value, 0f),
                    CellWidthMode.GridName => GridNameWidth(rowWidth),
                    _ => AutoWidth(spec),
                };
                if (spec.MaxWidth > 0f && (spec.Width.Mode == CellWidthMode.Fraction || spec.Width.Mode == CellWidthMode.Auto))
                    ask = Math.Min(ask, spec.MaxWidth);
                if (spec.Kind == CellKind.Spacer) ask = 0f;
                else ask = Math.Max(MinWidthOf(spec), MenuPanelLayout.Positive(ask, MinWidthOf(spec)));
                width[i] = ask;
                flexible[i] = spec.Kind != CellKind.Spacer
                    && spec.Kind != CellKind.Image
                    && spec.Kind != CellKind.Chevron
                    && spec.Width.Mode != CellWidthMode.Px
                    && spec.Width.Mode != CellWidthMode.Fill;
            }
            if (spec.Kind == CellKind.Spacer && !keepWidths) { count++; continue; }
            sum += width[i];
            count++;
        }
        return sum + Math.Max(0, count - 1) * Gap;
    }

    private static float Clamp01(float v) => float.IsNaN(v) ? 0f : v < 0f ? 0f : v > 1f ? 1f : v;

    /// <summary>Uppercase header caption with the sort indicator when this column is the sorted one.</summary>
    internal static string HeaderCaption(string? text, bool sorted) => HeaderCaption(text, sorted, ascending: false);

    /// <summary>INSTALLCOLS1: as above, with ▲ for a column sorted ascending and ▼ for descending.</summary>
    internal static string HeaderCaption(string? text, bool sorted, bool ascending)
        => HeaderCaption(text, sorted, ascending, smallCaps: false);

    /// <summary>
    /// VANILLA1: as above; a <paramref name="smallCaps"/> caption keeps its own case (the runtime draws
    /// it in small caps), so readback shows <c>Downloads ▼</c> rather than <c>DOWNLOADS ▼</c>.
    /// </summary>
    internal static string HeaderCaption(string? text, bool sorted, bool ascending, bool smallCaps)
    {
        string caption = smallCaps ? (text ?? "") : (text ?? "").ToUpperInvariant();
        return sorted ? caption + (ascending ? SortIndicatorAscending : SortIndicator) : caption;
    }

    /// <summary>
    /// Whether the two placements overlap horizontally (both visible and their spans intersect).
    /// Test helper shared with the runtime's own self-check.
    /// </summary>
    internal static bool Overlap(in CellPlacement a, in CellPlacement b) =>
        a.Visible && b.Visible && a.Left < b.Right - .01f && b.Left < a.Right - .01f;
}

/// <summary>
/// KIT1: the two-column head block of a detail page (approved mock
/// <c>2026-09-13-mods-page-mock/mods-ux-mock.html</c>, the <c>.head</c> / <c>.meta</c> grid). The
/// block is three <see cref="CellRowSpec"/> rows plus an optional fourth, because a cell row is the
/// one kit primitive that positions cells from an explicit column model — the same reason UIHDR2's
/// pinned header worked and the two inline-chip attempts did not. Left column, top to bottom: the
/// display-size title, the meta line (mono version, mono date, author, the Like button), the
/// wrapping one-liner. Right column, right-aligned against the same edges: the status word, the
/// action button, the side badges.
///
/// <para>Everything here is arithmetic, so the geometry has a game-free test at both fixtures
/// (3840×1080 → a 1664×916 frame → a 1288 px detail pane, and 1600×1024).</para>
/// </summary>
internal static class HeadBlockLayout
{
    // Row keys. The readback addresses a row by these, and `widget_click` a cell by
    // `<rowKey>/<cellKey>`, so the pane and the tests share the constants.
    internal const string RowTitle = "head-title";
    internal const string RowMeta = "head-meta";
    internal const string RowOneLiner = "head-oneliner";
    internal const string RowWarning = "head-warning";

    // Cell keys.
    internal const string CellTitle = "title";
    internal const string CellStatusLabel = "status-label";
    internal const string CellStatusWord = "status-word";
    internal const string CellVersion = "version";
    internal const string CellReleased = "released";
    internal const string CellAuthor = "author";
    internal const string CellLike = "like";
    internal const string CellAction = "action";
    internal const string CellOneLiner = "oneliner";
    internal const string CellBadge = "badge";
    internal const string CellWarningMarker = "warning-marker";
    internal const string CellWarning = "warning";
    internal const string CellGap = "gap";

    /// <summary>Display title: the mock's 34 px <c>.title</c>.</summary>
    internal const float TitleFontSize = 34f;

    /// <summary>The smallest floor the title is given (two readable text cells).</summary>
    internal const float TitleMinWidth = CellLayout.MinTextWidth * 2f;

    /// <summary>
    /// F11: the width the title never shrinks below on a row <paramref name="rowWidth"/> wide: its
    /// caption estimate, at least <see cref="TitleMinWidth"/>, at most 55 % of the content (the larger
    /// half <see cref="Columns"/> keeps for the left column). It fills any width beyond this.
    /// </summary>
    internal static float TitleFloor(string? title, float rowWidth)
    {
        float estimate = CellLayout.EstimateTextWidth(title, TitleFontSize) + 4f;
        float cap = Math.Max(TitleMinWidth, (ContentWidth(rowWidth) - CellLayout.Gap) * .55f);
        return Math.Min(cap, Math.Max(TitleMinWidth, estimate));
    }

    /// <summary>Meta line (<c>.metal</c> 15 px on the mock's 15 px body, 16 on our 22 px one).</summary>
    internal const float MetaFontSize = CellLayout.MutedFontSize;

    /// <summary>The one-liner: muted, a shade above the meta line so it reads as the description.</summary>
    internal const float OneLinerFontSize = 17f;

    internal const float WarningFontSize = 16f;

    /// <summary>Line pitch of the wrapping one-liner / warning line.</summary>
    internal const float LineHeight = 22f;

    /// <summary>Lines either wrapping line is allowed before TMP ellipsis takes over.</summary>
    internal const int MaxWrapLines = 4;

    /// <summary>The mock's <c>.right{min-width:250px}</c>: the right column never squeezes below this.</summary>
    internal const float RightColumnMinWidth = 250f;

    /// <summary>
    /// HEAD2: width at the right-hand end of every head row that no cell enters — the band the
    /// pane's vertical scrollbar lives in (<see cref="PanelLayout.ScrollbarWidth"/> plus
    /// <see cref="PanelLayout.ScrollbarInset"/>).
    ///
    /// <para><b>Why (user, 2026-09-13, live 3840×1080).</b> A pane's row is exactly as wide as its
    /// clipped viewport, so a cell packed flush against the row's right edge is drawn on the
    /// boundary of the clip with the scrollbar band immediately beyond it. Gen 1 put the whole right
    /// column there: <c>mods_ui rects</c> read <c>status-word</c> and <c>badge-0</c> ending at
    /// x 2732 on a 1484..2732 row, and on the CryptSnooze page the wider <b>Uninstall</b> button's
    /// right-hand side came out cropped and the <c>Server</c> badge read as half under the bar
    /// ("the uninstall button is too far to the right, it is getting cropped"). The fix is in the
    /// model rather than in the placement pass, so the edge has a geometry test at both
    /// fixtures.</para>
    /// </summary>
    internal const float RightColumnInset = PanelLayout.ScrollbarWidth + PanelLayout.ScrollbarInset;

    /// <summary>
    /// The width a head row's cells actually use: the row less <see cref="RightColumnInset"/>. Every
    /// column measurement here is taken from this, so the left column, the wrapped line counts and
    /// the right column all agree about where the content ends.
    /// </summary>
    internal static float ContentWidth(float rowWidth) =>
        Math.Max(1f, MenuPanelLayout.Positive(rowWidth, UnityUiRuntimeMetrics.RowWidth) - RightColumnInset);

    /// <summary>
    /// The right-hand edge the right column is packed against, as a row-relative x (the same space
    /// <see cref="CellPlacement.Right"/> is in): the row's right edge less the inset.
    /// </summary>
    internal static float ContentRight(float rowWidth) =>
        MenuPanelLayout.Positive(rowWidth, UnityUiRuntimeMetrics.RowWidth) * .5f - RightColumnInset;

    /// <summary>
    /// The inline toggle (the Like) is sized to its own caption, the way a side chip is: live gen 1
    /// drew `Like · 0` in a 170 px box because the cell asked for a cell row's 200 px action-button
    /// width and only the row's floor brought it down. <see cref="ToggleWidth"/> is the ask;
    /// <see cref="MinToggleWidth"/> is the floor that keeps an empty caption clickable.
    /// </summary>
    internal const float MinToggleWidth = 72f;

    /// <summary>
    /// SETTINGS1 stage 2: the width a small header button asks for (a custom page, Store page, Like on
    /// the mod page). The glyph estimate runs short of the game font — live 2026-09-29 <c>Example</c>
    /// drew as <c>Exam…</c> in a box sized by <see cref="ToggleWidth"/> — so the caption estimate is
    /// widened by <see cref="ButtonCaptionSlack"/> and padded, never below <see cref="MinToggleWidth"/>.
    /// </summary>
    internal static float ButtonWidth(string? caption) =>
        Math.Max(MinToggleWidth,
            CellLayout.EstimateTextWidth(caption, MetaFontSize) * ButtonCaptionSlack + CellLayout.ButtonPad + 12f);

    /// <summary>How much wider than the glyph estimate a header button's caption is allowed to be.</summary>
    internal const float ButtonCaptionSlack = 1.3f;

    /// <summary>Width the inline toggle asks for: its caption at <see cref="MetaFontSize"/> plus a button's padding.</summary>
    internal static float ToggleWidth(string? caption) =>
        Math.Max(MinToggleWidth, CellLayout.EstimateTextWidth(caption, MetaFontSize) + CellLayout.ButtonPad);

    /// <summary>
    /// The separator between meta cells (the mock's <c>2.0.4 · 2025-05-18 · by Corvane</c>). It
    /// leads the value it introduces and lives in that value's own text, <b>not</b> in a cell of its
    /// own: <see cref="CellLayout.Resolve"/> drops optional cells from the right, so a separator cell
    /// would outlive the value to its right for exactly one drop and leave a dangling dot on a narrow
    /// pane. Inside the value's text it cannot — the two are one cell. The runtime keeps it muted and
    /// outside a mono cell's <c>mspace</c> run (<c>UnityCellRow.WriteText</c>), because the value is
    /// tabular and the dot between values is not.
    /// </summary>
    internal const string MetaSeparator = "·";

    /// <summary>
    /// The text a meta cell carries: the value, or the separator and then the value for every cell
    /// after the first. This is also what the readback reports, so what a reader sees is what is drawn.
    /// </summary>
    internal static string MetaCellText(string? value, bool first) =>
        first || string.IsNullOrEmpty(value) ? value ?? "" : MetaSeparator + " " + value;

    /// <summary>True when <paramref name="text"/> leads with <see cref="MetaSeparator"/>; splits it off.</summary>
    internal static bool TrySplitMetaSeparator(string? text, out string value)
    {
        value = text ?? "";
        string lead = MetaSeparator + " ";
        if (!value.StartsWith(lead, StringComparison.Ordinal)) return false;
        value = value.Substring(lead.Length);
        return true;
    }

    /// <summary>The <c>(!)</c> cell in front of a warning line.</summary>
    internal const float MarkerWidth = 34f;

    /// <summary>Title row: tall enough for the 34 px display face plus its leading.</summary>
    internal static float TitleRowHeight =>
        Math.Max(UnityUiRuntimeMetrics.RowHeight, (float)Math.Ceiling(TitleFontSize * 1.3f));

    /// <summary>Meta row: an ordinary row, which is what the Like button and the action button want.</summary>
    internal static float MetaRowHeight => UnityUiRuntimeMetrics.RowHeight;

    /// <summary>
    /// Width the left column gets for its wrapping text on a row <paramref name="rowWidth"/> wide:
    /// the row's <see cref="ContentWidth"/> minus the right column and the gap between them. The
    /// right column is the widest of what its own contents ask for and
    /// <see cref="RightColumnMinWidth"/>, capped so the left column keeps the larger half — the same
    /// 45 % cap <see cref="MenuPanelLayout.HeaderColumns"/> puts on a pinned header's action column,
    /// so a narrow pane never clips the title. The two columns plus the gap plus
    /// <see cref="RightColumnInset"/> are the whole row, which is what keeps a fraction-width cell
    /// from being squeezed by the inset it already allowed for.
    /// </summary>
    internal static void Columns(float rowWidth, float rightAsk, out float leftWidth, out float rightWidth)
    {
        float content = ContentWidth(rowWidth);
        float gap = CellLayout.Gap;
        float cap = Math.Max(1f, (content - gap) * .45f);
        float want = Math.Max(RightColumnMinWidth, rightAsk <= 0f ? 0f : rightAsk);
        rightWidth = Math.Min(want, cap);
        leftWidth = Math.Max(1f, content - gap - rightWidth);
    }

    /// <summary>Lines the one-liner takes in the left column of a row <paramref name="rowWidth"/> wide.</summary>
    internal static int OneLinerLines(string? text, float rowWidth)
    {
        Columns(rowWidth, 0f, out float left, out _);
        return MenuPanelLayout.WrappedLines(text, left, OneLinerFontSize, MaxWrapLines);
    }

    /// <summary>Lines the warning line takes; its <c>(!)</c> cell and the gap come off the width first.</summary>
    internal static int WarningLines(string? text, float rowWidth) =>
        MenuPanelLayout.WrappedLines(text, WarningBodyWidth(rowWidth), WarningFontSize, MaxWrapLines);

    /// <summary>Width the warning sentence wraps in: the row's content less the marker and the gap.</summary>
    internal static float WarningBodyWidth(float rowWidth) =>
        Math.Max(1f, ContentWidth(rowWidth) - MarkerWidth - CellLayout.Gap);

    /// <summary>
    /// Height of a row whose tallest cell is <paramref name="lines"/> wrapped lines. Never shorter
    /// than one ordinary row, because the badges beside a one-line description still need their own
    /// height.
    /// </summary>
    internal static float WrappedRowHeight(int lines)
    {
        if (lines < 1) lines = 1;
        if (lines > MaxWrapLines) lines = MaxWrapLines;
        return Math.Max(UnityUiRuntimeMetrics.RowHeight, lines * LineHeight + PanelLayout.RowGap);
    }

    /// <summary>
    /// The whole block's height for this pane: what the panel reserves for it, so a test can say
    /// the head fits above the Settings section rather than only that each row is placed.
    /// </summary>
    internal static float BlockHeight(float rowWidth, string? oneLiner, string? warning)
    {
        float total = TitleRowHeight + PanelLayout.RowGap + MetaRowHeight + PanelLayout.RowGap;
        total += WrappedRowHeight(OneLinerLines(oneLiner, rowWidth));
        if (!string.IsNullOrEmpty(warning))
            total += PanelLayout.RowGap + WrappedRowHeight(WarningLines(warning, rowWidth));
        return total;
    }

    /// <summary>
    /// True when the right column of <paramref name="placements"/> is packed against the row's right
    /// edge: the last right-aligned cell ends on the edge and each earlier one ends one
    /// <see cref="CellLayout.Gap"/> left of its successor — the mock's "right column right-aligned
    /// under the status". <see cref="CellLayout.Resolve"/> packs right cells from the end of the spec
    /// list, so this walks them the same way; a cell the model dropped or hid is not checked.
    /// </summary>
    /// <summary>
    /// The cell rows one <see cref="HeadSpec"/> becomes, in draw order, for a pane whose row width
    /// is <paramref name="rowWidth"/>. Two to four rows: title, meta, the one-liner when there is
    /// one, the warning when there is one. Never throws; a null spec answers an empty list.
    ///
    /// <para>The right column is right-aligned cells on the first three rows, packed against the
    /// row's content edge by <see cref="CellLayout.Resolve"/> (the row less
    /// <see cref="RightColumnInset"/>, which is what keeps the action button clear of the pane's
    /// scrollbar and its own clip), so the status sits over the action button which sits over the
    /// badges without any of them being a row of its own. Every row carries the inset, so the
    /// wrapping one-liner and the warning sentence end on the same edge as the column beside
    /// them.</para>
    /// </summary>
    internal static List<CellRowSpec> Build(HeadSpec? spec, float rowWidth)
    {
        var rows = new List<CellRowSpec>(4);
        if (spec == null) return rows;
        rowWidth = MenuPanelLayout.Positive(rowWidth, UnityUiRuntimeMetrics.RowWidth);
        string key = string.IsNullOrEmpty(spec.Key) ? "head" : spec.Key;

        // ---- title | status ----------------------------------------------------------------
        // F11 (live 2026-10-07: "ReloadExample" drew as "ReloadExamp…" on a wide Mods page): the title
        // takes every pixel the status column leaves (a fill cell), not its auto width. The auto width
        // is only the AverageGlyphEm estimate of the caption, which runs short of the 34 px display
        // face, so a 13-character name was cut although the row had room to spare. Its floor is that
        // estimate (capped at the larger half of the row), so on a narrow pane the status cells
        // shrink first, as they did when the title was an auto cell.
        float titleFloor = TitleFloor(spec.Title, rowWidth);
        var title = new List<CellSpec>(4)
        {
            new CellSpec(CellKind.Text, CellTitle)
                .With(text: spec.Title, fontSize: TitleFontSize, width: CellWidth.Fill(titleFloor), minWidth: titleFloor),
            new CellSpec(CellKind.Spacer, CellGap),
        };
        if (spec.StatusLabel.Length > 0)
            title.Add(new CellSpec(CellKind.Muted, CellStatusLabel)
                .With(text: spec.StatusLabel, align: CellAlign.Right, fontSize: MetaFontSize));
        if (spec.StatusWord.Length > 0)
            title.Add(new CellSpec(CellKind.Text, CellStatusWord).With(
                text: spec.StatusWord, align: CellAlign.Right, fontSize: MetaFontSize,
                tone: LabelTones.ToCellTone(spec.StatusTone)));
        rows.Add(new CellRowSpec(
            key + "-title", title, height: TitleRowHeight, rightInset: RightColumnInset));

        // ---- meta (+ the Like toggle) | action -----------------------------------------------
        var meta = new List<CellSpec>(8);
        if (spec.MetaLast)
        {
            // SETTINGS1 stage 2: the mod page's button row — its custom pages first, then the
            // toggle, then the values (the release date), which are optional.
            for (int i = 0; i < spec.MetaButtons.Count; i++)
            {
                var button = spec.MetaButtons[i];
                if (button.Caption.Length == 0 || button.Key.Length == 0) continue;
                float w = ButtonWidth(button.Caption);
                meta.Add(new CellSpec(CellKind.Button, button.Key).With(
                    text: button.Caption, width: CellWidth.Px(w), minWidth: w, fontSize: MetaFontSize));
            }
        }

        int values = 0;
        var valueCells = new List<CellSpec>(4);
        for (int i = 0; i < spec.Meta.Count; i++)
        {
            var item = spec.Meta[i];
            if (item.Text.Length == 0) continue;
            var kind = item.Kind switch
            {
                HeadMetaKind.Mono => CellKind.Mono,
                HeadMetaKind.Muted => CellKind.Muted,
                _ => CellKind.Text,
            };
            valueCells.Add(new CellSpec(kind, item.Key).With(
                text: MetaCellText(item.Text, first: values == 0), fontSize: MetaFontSize,
                optional: item.Optional));
            values++;
        }
        if (!spec.MetaLast) meta.AddRange(valueCells);
        if (spec.ToggleCaption is { Length: > 0 } toggle)
        {
            float toggleWidth = spec.MetaLast ? ButtonWidth(toggle) : ToggleWidth(toggle);
            meta.Add(new CellSpec(CellKind.Button, CellLike).With(
                text: toggle, width: CellWidth.Px(toggleWidth), minWidth: toggleWidth,
                fontSize: MetaFontSize, tone: spec.ToggleOn ? CellTone.Gold : CellTone.Normal));
        }
        if (spec.MetaLast) meta.AddRange(valueCells);
        meta.Add(new CellSpec(CellKind.Spacer, CellGap));
        if (spec.ActionCaption is { Length: > 0 } action)
            meta.Add(new CellSpec(CellKind.Button, CellAction).With(
                text: action, align: CellAlign.Right, tone: LabelTones.ToCellTone(spec.ActionTone)));
        var metaRow = new CellRowSpec(
            key + "-meta", meta, height: MetaRowHeight, rightInset: RightColumnInset);
        if (!spec.MetaLast) rows.Add(metaRow);

        // ---- one-liner | badges ---------------------------------------------------------------
        float badgeAsk = 0f;
        var badges = new List<CellSpec>(spec.Badges.Count);
        for (int i = 0; i < spec.Badges.Count; i++)
        {
            var badge = spec.Badges[i];
            if (badge.Caption.Length == 0) continue;
            var cell = new CellSpec(CellKind.Badge, CellBadge + "-" + i.ToString(CultureInfo.InvariantCulture))
                .With(text: badge.Caption, tone: badge.Tone, align: CellAlign.Right, vAlign: CellVAlign.Top);
            // A fixed pill: the column model may squeeze flexible cells, and a squeezed badge
            // ellipsizes the one word that says which side the mod runs on.
            cell.Width = CellWidth.Px(CellLayout.AutoWidth(cell));
            badgeAsk += cell.Width.Value + (badges.Count > 0 ? CellLayout.Gap : 0f);
            badges.Add(cell);
        }

        bool hasOneLiner = spec.OneLiner.Length > 0;
        if (hasOneLiner || badges.Count > 0)
        {
            Columns(rowWidth, badgeAsk, out float leftWidth, out _);
            var third = new List<CellSpec>(badges.Count + 2);
            if (hasOneLiner)
                third.Add(new CellSpec(CellKind.Muted, CellOneLiner).With(
                    text: spec.OneLiner, width: CellWidth.Fraction(leftWidth / rowWidth),
                    wrap: CellWrap.Wrap, vAlign: CellVAlign.Top, fontSize: OneLinerFontSize,
                    minWidth: CellLayout.MinTextWidth));
            third.Add(new CellSpec(CellKind.Spacer, CellGap));
            third.AddRange(badges);
            rows.Add(new CellRowSpec(
                key + "-oneliner", third,
                height: WrappedRowHeight(hasOneLiner ? OneLinerLines(spec.OneLiner, rowWidth) : 1),
                rightInset: RightColumnInset));
        }

        if (spec.MetaLast) rows.Add(metaRow);

        // ---- warning -------------------------------------------------------------------------
        if (spec.Warning is { Length: > 0 } warning)
        {
            var line = new List<CellSpec>(2);
            if (spec.WarningMarker is { Length: > 0 } marker)
                // The floor is the marker's own width: a text cell's default floor is
                // CellLayout.MinTextWidth, which would give three characters 72 px and push the
                // sentence beside them out of the row.
                line.Add(new CellSpec(CellKind.Text, CellWarningMarker).With(
                    text: marker, width: CellWidth.Px(MarkerWidth), minWidth: MarkerWidth,
                    tone: CellTone.Danger, fontSize: WarningFontSize, vAlign: CellVAlign.Top));
            line.Add(new CellSpec(CellKind.Text, CellWarning).With(
                text: warning, width: CellWidth.Fraction(WarningBodyShare(rowWidth)),
                wrap: CellWrap.Wrap, vAlign: CellVAlign.Top, fontSize: WarningFontSize,
                tone: CellTone.Orange, minWidth: CellLayout.MinTextWidth));
            rows.Add(new CellRowSpec(
                key + "-warning", line, height: WrappedRowHeight(WarningLines(warning, rowWidth)),
                rightInset: RightColumnInset));
        }

        return rows;
    }

    /// <summary>
    /// The fraction of the row the warning body takes once its marker, the gap and the right inset
    /// are off. A fraction is of the whole row (<see cref="CellLayout.Resolve"/> multiplies it by
    /// the row width), so the inset has to be inside the numerator.
    /// </summary>
    internal static float WarningBodyShare(float rowWidth)
    {
        rowWidth = MenuPanelLayout.Positive(rowWidth, UnityUiRuntimeMetrics.RowWidth);
        return WarningBodyWidth(rowWidth) / rowWidth;
    }

    internal static bool RightColumnIsFlush(
        float rowWidth, CellRowSpec? row, CellPlacement[] placements, float tolerance = 0.5f)
    {
        if (row == null || placements == null) return false;
        var specs = row.Cells;
        rowWidth = MenuPanelLayout.Positive(rowWidth, UnityUiRuntimeMetrics.RowWidth);
        // The content edge, not the row's own: HEAD2 keeps the whole column one scrollbar band
        // clear of the clip. `row` is asked rather than the constant so a row built without the
        // inset is still checked against the edge it was actually laid out for.
        float right = rowWidth * .5f - (row.RightInset <= 0f ? 0f : row.RightInset);
        bool sawRight = false;
        int n = Math.Min(placements.Length, specs.Count);
        for (int i = n - 1; i >= 0; i--)
        {
            if (specs[i].Align != CellAlign.Right || !placements[i].Visible) continue;
            sawRight = true;
            if (Math.Abs(placements[i].Right - right) > tolerance) return false;
            right = placements[i].Left - CellLayout.Gap;
        }
        return sawRight;
    }
}

/// <summary>Which face one meta cell of a <see cref="HeadSpec"/> wears.</summary>
internal enum HeadMetaKind
{
    /// <summary>Tabular figures: a version, a release date.</summary>
    Mono = 0,
    /// <summary>Body text: <c>by &lt;author&gt;</c>.</summary>
    Text = 1,
    /// <summary>Dimmer body text.</summary>
    Muted = 2,
}

/// <summary>One cell of the head block's meta line.</summary>
internal readonly struct HeadMeta
{
    internal HeadMeta(string key, string text, HeadMetaKind kind = HeadMetaKind.Text, bool optional = false)
    {
        Key = key ?? "";
        Text = text ?? "";
        Kind = kind;
        Optional = optional;
    }

    internal string Key { get; }
    internal string Text { get; }
    internal HeadMetaKind Kind { get; }

    /// <summary>Dropped from the right when the row is too narrow (the mock's Released / Author).</summary>
    internal bool Optional { get; }

    internal static HeadMeta Mono(string key, string text, bool optional = false) =>
        new(key, text, HeadMetaKind.Mono, optional);
}

/// <summary>One side badge of the head block's right column.</summary>
internal readonly struct HeadBadge
{
    internal HeadBadge(string caption, CellTone tone)
    {
        Caption = caption ?? "";
        Tone = tone;
    }

    internal string Caption { get; }
    internal CellTone Tone { get; }
}

/// <summary>
/// KIT1: what a <c>panel.HeadBlock(spec)</c> draws. Every field is plain data decided by the caller,
/// so the block's geometry and readback have a game-free test
/// (<see cref="HeadBlockLayout.Build"/> turns this into the cell rows).
/// </summary>
internal sealed class HeadSpec
{
    /// <summary>Row-key prefix. The rows are <c>&lt;key&gt;-title</c>, <c>-meta</c>, <c>-oneliner</c>, <c>-warning</c>.</summary>
    internal string Key { get; set; } = "head";

    internal string Title { get; set; } = "";

    internal IReadOnlyList<HeadMeta> Meta { get; set; } = Array.Empty<HeadMeta>();

    /// <summary>The inline toggle-button cell of the meta line (the mod page's Like), or null.</summary>
    internal string? ToggleCaption { get; set; }

    /// <summary>The toggle is on: its cell wears <see cref="CellTone.Gold"/>.</summary>
    internal bool ToggleOn { get; set; }

    /// <summary>Wrapping description under the meta line. Empty draws no one-liner row.</summary>
    internal string OneLiner { get; set; } = "";

    /// <summary>The warning line's body <b>without</b> its marker, or null for no warning row.</summary>
    internal string? Warning { get; set; }

    /// <summary>The marker in front of the warning line, drawn in <see cref="MenuPalette.Red"/>.</summary>
    internal string WarningMarker { get; set; } = "(!)";

    /// <summary>The muted prefix of the status label (<c>Status: </c>).</summary>
    internal string StatusLabel { get; set; } = "";

    /// <summary>The colored status word.</summary>
    internal string StatusWord { get; set; } = "";

    internal LabelTone StatusTone { get; set; } = LabelTone.Normal;

    /// <summary>The action button under the status, or null for a read-only page.</summary>
    internal string? ActionCaption { get; set; }

    internal LabelTone ActionTone { get; set; } = LabelTone.Normal;

    internal IReadOnlyList<HeadBadge> Badges { get; set; } = Array.Empty<HeadBadge>();

    /// <summary>
    /// SETTINGS1 stage 2: small button cells at the start of the meta row, before the toggle — a
    /// mod page's custom pages. Each key is the cell key a click is bound to
    /// (<see cref="UiHeadBlock.OnMetaButton"/>).
    /// </summary>
    internal IReadOnlyList<HeadButton> MetaButtons { get; set; } = Array.Empty<HeadButton>();

    /// <summary>
    /// SETTINGS1 stage 2: draw the one-liner row before the meta row, and the meta row's buttons
    /// and toggle before its values — the mod page's default header (title, by-line, then one row
    /// of small buttons). False keeps the KIT1 order (title, meta, one-liner) every other head uses.
    /// </summary>
    internal bool MetaLast { get; set; }
}

/// <summary>SETTINGS1 stage 2: one small button of a head block's meta row.</summary>
internal readonly struct HeadButton
{
    internal HeadButton(string key, string caption)
    {
        Key = key ?? "";
        Caption = caption ?? "";
    }

    internal string Key { get; }

    internal string Caption { get; }
}

/// <summary>
/// Sidebar list scroll across <c>Show</c>, <c>RestoreScroll</c>, and the layout settle
/// pass that follows a row click. Game-free so the restore-then-settle ordering has a
/// test: Unity's settle re-layout zeros the offset after a correct restore, and a test
/// that only asserts at restore time stays green.
/// </summary>
internal static class SidebarScrollLayout
{
    /// <summary>
    /// Offset to write after this pass sizes the content. A pending preserve wins over
    /// both a show-reset and the current offset. Without a pending value a show writes
    /// the top (<c>0</c>); a later pass keeps <paramref name="currentY"/> so the wheel
    /// pump is not a second writer.
    /// </summary>
    internal static float OffsetToWrite(
        bool hasPending,
        float pendingNormalized,
        float pendingOffsetY,
        bool resetScroll,
        float currentY,
        float contentHeight,
        float viewportHeight)
    {
        if (hasPending)
        {
            return MenuPanelLayout.CanApplyNormalized(contentHeight, viewportHeight)
                ? MenuPanelLayout.ContentOffsetY(pendingNormalized, contentHeight, viewportHeight)
                : pendingOffsetY;
        }

        return resetScroll ? 0f : currentY;
    }

    /// <summary>
    /// Keep the armed preserve through provisional passes and through a re-layout that
    /// is not the settle re-measure, so a later settle pass can re-apply after Unity
    /// zeros the offset. Drop it on <see cref="RulerSettleState.Settled"/>, on give-up,
    /// and on a first show that already read a final canvas (no settle is coming).
    /// </summary>
    internal static bool KeepPending(RulerSettleState settle, bool firstShow)
    {
        if (settle == RulerSettleState.Armed) return true;
        if (settle == RulerSettleState.Settled || settle == RulerSettleState.GaveUp) return false;
        return !firstShow;
    }

    /// <summary>
    /// One layout pass: the offset to write and whether the pending preserve stays
    /// armed. <paramref name="currentY"/> is the offset before this pass resized the
    /// content — live, Unity often zeros it between restore and settle.
    /// </summary>
    internal static void Apply(
        bool hasPending,
        float pendingNormalized,
        float pendingOffsetY,
        bool resetScroll,
        bool firstShow,
        RulerSettleState settle,
        float currentY,
        float contentHeight,
        float viewportHeight,
        out float offsetY,
        out bool keepPending)
    {
        offsetY = OffsetToWrite(
            hasPending, pendingNormalized, pendingOffsetY, resetScroll, currentY,
            contentHeight, viewportHeight);
        keepPending = hasPending && KeepPending(settle, firstShow);
    }

    internal static string FormatPreserveLog(
        string pane, float capture, float restored, float contentHeight, float viewportHeight, string settle)
    {
        return "Mods workspace: scroll-preserve pane=" + pane
            + " capture=" + capture.ToString("0.###", CultureInfo.InvariantCulture)
            + " restore=" + restored.ToString("0.###", CultureInfo.InvariantCulture)
            + " content=" + ((int)contentHeight).ToString(CultureInfo.InvariantCulture)
            + " view=" + ((int)viewportHeight).ToString(CultureInfo.InvariantCulture)
            + " applied=offset"
            + " settle=" + settle;
    }
}

/// <summary>Where the next sidebar row is parented. Content rows scroll; chrome does not.</summary>
internal enum MenuRowPin
{
    Content = 0,
    Top = 1,
    Bottom = 2,
    /// <summary>
    /// Pinned listing header: a fixed band above the panel viewport (title,
    /// facts, action on the right). Not the workspace tab strip.
    /// </summary>
    Header = 3,
}

/// <summary>
/// Visual treatment of the next sidebar button. Catalog rows scroll; chrome is the
/// pinned Overview / Close pair. Selected is color only — no caption prefix.
/// </summary>
internal enum MenuRowTone
{
    Catalog = 0,
    Selected = 1,
    Chrome = 2,
    Search = 3,
}

/// <summary>
/// Sidebar row fills. Color is the selected marker. Chrome (Overview / Close) uses a
/// darker inset fill from the same panel palette, never the selected gold.
/// </summary>
internal static class MenuRowStyle
{
    internal const float CatalogFontSize = 22f;
    internal const float ChromeFontSize = 16f;
    internal const float ChromeInset = 16f;

    internal const float NormalR = 0.18f, NormalG = 0.18f, NormalB = 0.24f, NormalA = 0.95f;
    internal const float HoverR = 0.30f, HoverG = 0.30f, HoverB = 0.38f, HoverA = 1f;
    internal const float SelectedR = 0.42f, SelectedG = 0.34f, SelectedB = 0.16f, SelectedA = 0.95f;
    internal const float SelectedHoverR = 0.52f, SelectedHoverG = 0.42f, SelectedHoverB = 0.20f, SelectedHoverA = 1f;

    internal const float ChromeR = 0.12f, ChromeG = 0.14f, ChromeB = 0.18f, ChromeA = 0.95f;
    internal const float ChromeHoverR = 0.20f, ChromeHoverG = 0.24f, ChromeHoverB = 0.30f, ChromeHoverA = 1f;
    internal const float ChromeBandR = 0.08f, ChromeBandG = 0.09f, ChromeBandB = 0.12f, ChromeBandA = 0f;
    internal const float ChromeRuleR = 1f, ChromeRuleG = 1f, ChromeRuleB = 1f, ChromeRuleA = 0.22f;
    internal const float ChromeBorderR = 0.38f, ChromeBorderG = 0.46f, ChromeBorderB = 0.52f, ChromeBorderA = 0.85f;
    internal const float ChromeBorderHoverR = 0.58f, ChromeBorderHoverG = 0.68f, ChromeBorderHoverB = 0.74f, ChromeBorderHoverA = 1f;
    internal const float ChromeLabelR = 0.78f, ChromeLabelG = 0.86f, ChromeLabelB = 0.86f, ChromeLabelA = 1f;

    /// <summary>
    /// Sidebar search field. Cooler fill than catalog rows, not selected gold,
    /// not footer chrome. The empty field draws the word <see cref="SearchPlaceholder"/>
    /// on the left and, only when the live font has one (<see cref="PickSearchGlyph"/>), a
    /// magnifying-glass glyph on the right; both hide once the player types. Without a font
    /// glyph nothing is drawn there (the game has no search-icon sprite to borrow). There is
    /// no detached caption outside the box.
    /// </summary>
    internal const string SearchPlaceholder = "Search";
    internal const string SearchRowName = "search-row";
    internal const string SearchHintName = "search-hint";
    internal const string SearchHintWordName = "search-hint-word";
    internal const string SearchHintGlyphName = "search-hint-glyph";
    internal const float SearchGlyphWidth = 22f;
    /// <summary>U+2315 TELEPHONE RECORDER (⌕). BMP; looks like a magnifying glass.</summary>
    internal const string SearchGlyphPreferred = "\u2315";
    /// <summary>U+26B2 NEUTER (⚲). BMP fallback: circle with a stem.</summary>
    internal const string SearchGlyphAlt = "\u26B2";
    internal const float SearchR = 0.14f, SearchG = 0.20f, SearchB = 0.26f, SearchA = 0.98f;
    internal const float SearchBorderR = 0.50f, SearchBorderG = 0.62f, SearchBorderB = 0.72f, SearchBorderA = 0.95f;
    /// <summary>
    /// Hint word and glyph. <see cref="ChromeLabelR"/>: muted against typed white,
    /// readable on <see cref="SearchR"/> fill. The previous 0.55/0.60/0.66/0.90
    /// sat too close to the fill for the icon to separate.
    /// </summary>
    internal const float SearchPlaceholderR = ChromeLabelR;
    internal const float SearchPlaceholderG = ChromeLabelG;
    internal const float SearchPlaceholderB = ChromeLabelB;
    internal const float SearchPlaceholderA = ChromeLabelA;

    /// <summary>
    /// Floor for a hint Graphic's effective alpha. Below this the pixels do not
    /// separate from the fill even when <c>text</c> is assigned.
    /// </summary>
    internal const float SearchHintMinAlpha = 0.85f;

    /// <summary>
    /// Minimum Rec. 709 contrast ratio of the hint color against
    /// <see cref="SearchR"/> (no sRGB decode: the palette is authored as Unity colors).
    /// </summary>
    internal const float SearchHintMinContrastVsFill = 3f;

    /// <summary>Rec. 709 luminance of an authored Unity color.</summary>
    internal static float Luminance(float r, float g, float b) =>
        0.2126f * r + 0.7152f * g + 0.0722f * b;

    /// <summary>WCAG-style contrast ratio on authored Unity colors.</summary>
    internal static float ContrastRatio(float r1, float g1, float b1, float r2, float g2, float b2)
    {
        float a = Luminance(r1, g1, b1);
        float b = Luminance(r2, g2, b2);
        float hi = a > b ? a : b;
        float lo = a > b ? b : a;
        return (hi + 0.05f) / (lo + 0.05f);
    }

    /// <summary>
    /// Unity UI draws a GameObject's own Graphic before its children, then children
    /// in sibling order. <paramref name="fillSiblingIndex"/> of -1 means the fill
    /// Image lives on the parent (always behind every child). Otherwise the hint
    /// must have a strictly greater sibling index than the fill.
    /// </summary>
    internal static bool HintIsNotBehindFill(int hintSiblingIndex, int fillSiblingIndex) =>
        hintSiblingIndex >= 0 && (fillSiblingIndex < 0 || hintSiblingIndex > fillSiblingIndex);

    /// <summary>
    /// Drawability, not assignment. A TMP can hold <c>text = "Search"</c> with a
    /// zero mesh, zero alpha, or a collapsed rect — those are not visible.
    /// <paramref name="meshCount"/> is TMP <c>textInfo.characterCount</c>, or 1
    /// for a UI Image that already has a quad.
    /// </summary>
    internal static bool HintGraphicIsDrawable(
        bool assigned,
        bool active,
        bool graphicEnabled,
        float alpha,
        float rectW,
        float rectH,
        int hintSiblingIndex,
        int fillSiblingIndex,
        int meshCount)
    {
        return assigned
            && active
            && graphicEnabled
            && alpha >= SearchHintMinAlpha
            && rectW > 1f
            && rectH > 1f
            && HintIsNotBehindFill(hintSiblingIndex, fillSiblingIndex)
            && meshCount > 0;
    }

    /// <summary>
    /// First candidate the live font actually contains. An empty result means
    /// draw the icon from UI Images instead of a missing-glyph box.
    /// </summary>
    internal static string PickSearchGlyph(Func<string, bool>? fontHas)
    {
        if (fontHas == null) return "";
        if (fontHas(SearchGlyphPreferred)) return SearchGlyphPreferred;
        if (fontHas(SearchGlyphAlt)) return SearchGlyphAlt;
        return "";
    }

    /// <summary>Hint word and glyph are shown only while the field has no typed text.</summary>
    internal static bool SearchHintVisible(string? fieldText) => string.IsNullOrEmpty(fieldText);

    // ---- SEARCHCLEAR1: the clear button inside a search box ---------------------------------------

    /// <summary>
    /// SEARCHCLEAR1 (requested 2026-10-09): the clear button inside the Mods search and the Install
    /// search, at the right where the empty box draws its search mark. A child of the field named this;
    /// its presence also marks the field as a workspace search (Escape clears it, then leaves it).
    /// </summary>
    internal const string SearchClearName = "search-clear";

    /// <summary>The kit's clear glyph (U+00D7, in the game font; the Keybinds tab's Unbind mark).</summary>
    internal const string SearchClearGlyph = "×";

    /// <summary>Drawn when the live font lacks <see cref="SearchClearGlyph"/> (never a missing-glyph box).</summary>
    internal const string SearchClearFallbackGlyph = "x";

    /// <summary>The clear button's width: a square on the 28-unit field, the whole height its click target.</summary>
    internal const float SearchClearWidth = 28f;

    internal const float SearchClearFontSize = 20f;

    /// <summary>Hover brightens and a press darkens, by the kit's box-less button factors (the Quiet skin's).</summary>
    internal const float SearchClearHoverFactor = 1.3f;

    /// <inheritdoc cref="SearchClearHoverFactor"/>
    internal const float SearchClearPressedFactor = 0.7f;

    /// <summary>The clear button shows only while the box has text (the search mark shows while it is empty).</summary>
    internal static bool SearchClearVisible(string? fieldText) => !string.IsNullOrEmpty(fieldText);

    /// <summary>The kit's glyph when the font has it, else <see cref="SearchClearFallbackGlyph"/>.</summary>
    internal static string PickSearchClearGlyph(Func<string, bool>? fontHas) =>
        fontHas == null || fontHas(SearchClearGlyph) ? SearchClearGlyph : SearchClearFallbackGlyph;

    /// <summary>
    /// The glyph's color: the hint color at rest (the search mark's), brighter while the pointer is over
    /// it, darker while it is held down.
    /// </summary>
    internal static (float R, float G, float B, float A) SearchClearColor(bool hovered, bool pressed)
    {
        float f = pressed ? SearchClearPressedFactor : hovered ? SearchClearHoverFactor : 1f;
        static float C(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        return (C(SearchPlaceholderR * f), C(SearchPlaceholderG * f), C(SearchPlaceholderB * f), SearchPlaceholderA);
    }

    /// <summary>The typed text stops short of the clear button: the viewport's right inset with one attached.</summary>
    internal static float SearchTextRightInset(bool hasClear, float padX) => hasClear ? SearchClearWidth : padX;

    /// <summary>
    /// Sidebar catalog caption. Selected and unselected share the string so the row
    /// does not jump horizontally when the gold fill is applied.
    /// </summary>
    internal static string CatalogCaption(string? name, float rowWidth) =>
        MenuPanelLayout.ClipLabel(name, rowWidth);

    // ---- KIT1: the marker and the state dot of a sidebar row --------------------------------

    /// <summary>Child object names so the runtime can find the two parts again on a restyle.</summary>
    internal const string RowMarkerName = "row-marker";
    internal const string RowDotName = "row-dot";

    /// <summary>Width reserved for the marker in front of the caption (the mock's <c>(!)</c>).</summary>
    internal const float RowMarkerWidth = 22f;

    /// <summary>The state dot at the right edge: the mock's 9 px circle.</summary>
    internal const float RowDotSize = 9f;

    /// <summary>Caption size of the marker; a shade under the caption so the glyph does not shout.</summary>
    internal const float RowMarkerFontSize = 15f;

    /// <summary>Inset of either part from its edge of the row.</summary>
    internal const float RowMarkPad = 8f;

    /// <summary>
    /// Width left for a marked row's caption once the marker and the dot have taken their side of
    /// the row. A row with neither keeps the whole width, which is what an installed mod with no
    /// warning has always had.
    /// </summary>
    internal static float CaptionWidthFor(float rowWidth, bool marker, bool dot)
    {
        rowWidth = MenuPanelLayout.Positive(rowWidth, UnityUiRuntimeMetrics.RowWidth);
        float left = marker ? RowMarkPad + RowMarkerWidth : 0f;
        float right = dot ? RowMarkPad + RowDotSize + RowMarkPad : 0f;
        return Math.Max(1f, rowWidth - left - right);
    }

    /// <summary>
    /// The caption of a marked row: the mod's name and nothing else, clipped to the space the
    /// marker and the dot leave. The marker and the dot are drawn parts, so they are no longer
    /// characters in this string — a click, a search match and the readback all want the name.
    /// </summary>
    internal static string MarkedCaption(string? name, float rowWidth, bool marker, bool dot) =>
        MenuPanelLayout.ClipLabel(name, CaptionWidthFor(rowWidth, marker, dot));
}

/// <summary>How a workspace detail pane uses the frame (LIST3).</summary>
internal enum MenuPaneMode
{
    /// <summary>The sidebar / detail split (<see cref="MenuPanelLayout.Split"/>); the pane draws its title.</summary>
    Split = 0,
    /// <summary>The detail pane spans the whole frame width with no sidebar column and no pane title (the Install tab).</summary>
    FullWidth = 1,

    /// <summary>
    /// KEYS3: a list pane with a description column on its right (the Keybinds tab, like the game's
    /// Bindings Menu). No sidebar and no pane title; the pane takes the frame width left of the
    /// <see cref="Aside"/> column (<see cref="MenuPanelLayout.AsideSplit"/>).
    /// </summary>
    ListWithAside = 2,

    /// <summary>KEYS3: the description column at the right of a <see cref="ListWithAside"/> pane. No title band.</summary>
    Aside = 3,
}

/// <summary>Readback names of <see cref="MenuPaneMode"/> (<c>mods_ui</c> geometry <c>layout</c>).</summary>
internal static class MenuPaneModes
{
    internal const string SplitName = "split";
    internal const string FullWidthName = "full-width";
    internal const string ListWithAsideName = "list-with-aside";
    internal const string AsideName = "aside";

    internal static string Name(MenuPaneMode mode) => mode switch
    {
        MenuPaneMode.FullWidth => FullWidthName,
        MenuPaneMode.ListWithAside => ListWithAsideName,
        MenuPaneMode.Aside => AsideName,
        _ => SplitName,
    };

    /// <summary>Panes that draw no centered title band (the tab strip already names the tab).</summary>
    internal static bool Titleless(MenuPaneMode mode) =>
        mode is MenuPaneMode.FullWidth or MenuPaneMode.ListWithAside or MenuPaneMode.Aside;
}

internal readonly struct MenuPaneGeometry
{
    internal MenuPaneGeometry(float width, float height, float x, float y)
    { Width = width; Height = height; X = x; Y = y; }
    internal float Width { get; }
    internal float Height { get; }
    internal float X { get; }
    internal float Y { get; }
    internal float RowWidth => Math.Max(1f, Width - PanelLayout.PadSide * 2f);
}

/// <summary>
/// Canvas-space Mods workspace frame. Center-anchored like the two panes.
/// In game this is the union of <c>FullscreenMenu.ViewParent</c>
/// and native menu-root chrome whose rect sits in the footer band
/// under ViewParent (≤ 120 overlay units; see <see cref="NativeFrameExtent"/>).
/// The hosted page's <c>BackgroundImage</c> / <c>Background</c>
/// is stretch-fill of ViewParent and cannot size the frame.
/// </summary>
internal readonly struct ModsWorkspaceFrame
{
    internal const float MaxAspect = 2f;
    internal const float MatchTolerance = 8f;
    internal const string PathNative = "native-container";
    internal const string PathNativeCapped = "native-container-capped";
    /// <summary>
    /// HOSTS1: the title screen's (and the standalone window's) frame, sized as the in-game frame
    /// (<see cref="WorkspaceHostLayout.TitleFrame"/>). Its geometry is the in-game geometry: the strip
    /// consumes the frame top and lifts toward the bar exactly as on a <see cref="PathNative"/> frame.
    /// </summary>
    internal const string PathTitleHost = "title-host";

    /// <summary>
    /// HOSTS1 round 4: an in-game (top-tab) frame before the native read, sized as the canonical frame.
    /// In-game geometry, never presented (<see cref="MayPresent"/> needs a native path).
    /// </summary>
    internal const string PathNativePending = "native-pending";
    internal const string PathFallback = "fallback";
    internal const string LogPrefix = "Mods workspace: frame path=";
    internal const string ResourceName = "progenitor.ui.mods-background.png";
    // Live texture is 1672×941 (1.78:1). The tree sits hard right; a rune circle fills the left.
    internal const float ImageWidth = 1672f;
    internal const float ImageHeight = 941f;
    /// <summary>
    /// Workspace panes expose the shared artwork. Input controls and dialogs
    /// retain their own grounds; the single backdrop scrim supports body text.
    /// </summary>
    internal const float PanelFillAlpha = 0f;
    internal const float PaneFillR = 0.08f, PaneFillG = 0.08f, PaneFillB = 0.10f;
    internal const float ScrimR = 0.04f, ScrimG = 0.02f, ScrimB = 0.03f, ScrimA = 0.52f;
    /// <summary>Cover-crop horizontal align. 1 = keep the right (the tree).</summary>
    internal const float CoverAlignX = 1f;
    /// <summary>Cover-crop vertical align. 0.5 = keep the vertical center.</summary>
    internal const float CoverAlignY = 0.5f;
    /// <summary>Focal region (the tree's trunk and crown) as a fraction of the source PNG (left…right, bottom…top). The <c>Figure*</c> names predate the current art.</summary>
    internal const float FigureLeft = 0.48f;
    internal const float FigureRight = 0.90f;
    internal const float FigureBottom = 0.05f;
    internal const float FigureTop = 0.95f;
    internal const string RootName = "progenitor-ui-core-mods-workspace";
    internal const string HolderName = "frame";
    internal const string ImageName = "backdrop";
    internal const string ScrimName = "scrim";

    internal ModsWorkspaceFrame(float width, float height, float x, float y, string path, float tabLift = MenuPanelLayout.TabLift, float coverBelow = 0f)
    {
        Width = width;
        Height = height;
        X = x;
        Y = y;
        Path = string.IsNullOrEmpty(path) ? PathFallback : path;
        TabLift = tabLift;
        CoverBelow = coverBelow > 0f ? coverBelow : 0f;
    }

    /// <summary>
    /// HOSTS1 round 4/8: how far an in-game frame was moved to the canonical (vanilla) position
    /// (<see cref="WorkspaceHostLayout.PinCanonical"/>; since round 8 down, from the collapsed layout). Zero
    /// when the native frame was already there. Logged as <c>pinned=down56</c>.
    /// </summary>
    internal float CoverBelow { get; }

    internal float Width { get; }
    internal float TabLift { get; }
    internal float Height { get; }
    internal float X { get; }
    internal float Y { get; }
    internal string Path { get; }
    internal float Aspect => Height > 0.01f ? Width / Height : 0f;
    internal bool IsFallback => string.Equals(Path, PathFallback, StringComparison.Ordinal);

    internal static float RememberedRatio { get; private set; }

    /// <summary>HOSTS1: where the last title-host frame came from (<c>recorded</c>, <c>carried</c>, <c>model</c>); empty before one.</summary>
    internal static string LastTitleSource { get; private set; } = "";
    internal static ModsWorkspaceFrame Last { get; private set; }
    internal static string LastLog { get; private set; } = "";
    internal static NativeContentRect LastBackground { get; private set; }
    internal static string LastBackgroundWhy { get; private set; } = "";
    internal static NativeBackgroundOverhang LastOverhang { get; private set; }
    /// <summary>
    /// Frame index of the first presented workspace. 0 until
    /// <see cref="NoteShown"/>.
    /// </summary>
    internal static int FirstShowFrame { get; private set; }
    /// <summary>
    /// Layouts applied while the in-game workspace was still hidden waiting
    /// for a native frame. Zero when the first layout is the show layout.
    /// </summary>
    internal static int LayoutBeforeShow { get; private set; }
    internal static bool Shown { get; private set; }
    internal static string ShownPath { get; private set; } = "";
    internal const string ShownLogPrefix = "Ui: workspace shown ";
    /// <summary>
    /// Names of the native objects whose bottoms set the in-game frame
    /// (<c>ViewParent</c> plus qualifying footer chrome). Empty until a
    /// top-tab resolve.
    /// </summary>
    internal static string LastFrameExtentWhy { get; private set; } = "";

    internal const string NativeBackgroundName = "Background";
    internal const string NativeBackgroundImageName = "BackgroundImage";
    internal const string BackgroundWhyLive = "live";
    internal const string BackgroundWhyCached = "cached";
    internal const string BackgroundWhyMissing = "no-background";
    internal const string BackgroundWhyNoHosted = "no-hosted-view";
    internal const string BackgroundWhyNoImage = "no-background-image";

    internal static void ResetForTests()
    {
        RememberedRatio = 0f;
        LastTitleSource = "";
        WorkspaceHostLayout.ResetForTests();
        Last = default;
        LastLog = "";
        LastEscapeDismissedHud = false;
        LastBackground = default;
        LastBackgroundWhy = "";
        LastOverhang = default;
        LastFrameExtentWhy = "";
        ResetFirstShow();
    }

    internal static void ResetFirstShow()
    {
        FirstShowFrame = 0;
        LayoutBeforeShow = 0;
        Shown = false;
        ShownPath = "";
    }

    /// <summary>
    /// In-game top-tab must not draw against a fallback frame. Title
    /// (no FullscreenMenu) keeps the existing fallback.
    /// </summary>
    internal static bool RequiresNativeFrame(string? site) =>
        string.Equals(site, "top-tab", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Overlay mapping is trusted only when the chrome canvas has a real size.
    /// A 0×0 overlay InverseTransformPoint of ViewParent is the top-right flash.
    /// </summary>
    internal static bool OverlayIsReady(float width, float height) =>
        width > 1f && height > 1f;

    internal static bool IsNativePath(string? path) =>
        !string.IsNullOrEmpty(path)
        && path.StartsWith(PathNative, StringComparison.Ordinal);

    /// <summary>
    /// HOSTS1: a frame laid out with the in-game geometry (strip inside the frame top, lifted toward
    /// the bar): the native frame, and the title host that reproduces it.
    /// </summary>
    internal static bool UsesInGameGeometry(string? path) =>
        IsNativePath(path)
        || string.Equals(path, PathTitleHost, StringComparison.Ordinal)
        || string.Equals(path, PathNativePending, StringComparison.Ordinal);

    /// <summary>
    /// Whether panes and the backdrop may be drawn. In game a fallback path
    /// or a missed native read stays hidden.
    /// </summary>
    internal static bool MayPresent(string? site, bool nativeFound, string? framePath)
    {
        if (!RequiresNativeFrame(site)) return true;
        return nativeFound && IsNativePath(framePath);
    }

    /// <summary>
    /// Same gate as <see cref="MayPresent"/>: do not layout against fallback
    /// in game while hidden.
    /// </summary>
    internal static bool MayLayout(string? site, bool nativeFound, string? framePath) =>
        MayPresent(site, nativeFound, framePath);

    internal static void BeginFirstShow() => ResetFirstShow();

    internal static void NoteHiddenLayout()
    {
        if (!Shown) LayoutBeforeShow++;
    }

    internal static string FormatShownLog(int frame, string? path) =>
        ShownLogPrefix + "frame=" + frame
        + " framePath=" + (string.IsNullOrEmpty(path) ? PathFallback : path);

    internal static void NoteShown(int frame, string? path)
    {
        Shown = true;
        FirstShowFrame = frame;
        ShownPath = string.IsNullOrEmpty(path) ? PathFallback : path;
    }

    /// <summary>
    /// Metadata-backed names of the hosted-view painter: Spellbook <c>BackgroundImage</c>,
    /// Vampire Powers / Emotes <c>Background</c>.
    /// </summary>
    internal static bool IsNativeBackgroundName(string? name) =>
        string.Equals(name, NativeBackgroundName, StringComparison.Ordinal)
        || string.Equals(name, NativeBackgroundImageName, StringComparison.Ordinal);

    internal static bool LastEscapeDismissedHud { get; set; }

    /// <summary>The current floating-card layout. Used when ViewParent cannot be read.</summary>
    internal static ModsWorkspaceFrame Fallback(float canvasWidth, float canvasHeight)
    {
        float width = MenuPanelLayout.Positive(canvasWidth, 1920f);
        float height = MenuPanelLayout.Positive(canvasHeight, 1080f);
        float margin = Math.Min(32f, width * .04f);
        float workspace = Math.Min(1276f, width - margin * 2f);
        float top = Math.Min(MenuPanelLayout.TopInset, height * .25f);
        float bottom = Math.Min(MenuPanelLayout.BottomInset, height * .075f);
        float paneHeight = Math.Min(800f, height - top - bottom);
        float y = (bottom - top) * .5f;
        return new ModsWorkspaceFrame(workspace, paneHeight, 0f, y, PathFallback);
    }

    /// <summary>
    /// Size the workspace from a native content rect, a remembered native ratio
    /// (title screen), or the fallback cards. Never guesses a native size.
    /// </summary>
    internal static ModsWorkspaceFrame Resolve(
        float canvasWidth,
        float canvasHeight,
        string? site,
        in NativeContentRect native,
        float? remembered = null)
        => Resolve(canvasWidth, canvasHeight, site, native, NativeContentRect.Missing(BackgroundWhyMissing), remembered);

    /// <summary>
    /// In-game bar: the grown frame extent (ViewParent union footer chrome)
    /// when <paramref name="extent"/> is found, else ViewParent. Title /
    /// fallback frames ignore the overhang.
    /// </summary>
    internal static ModsWorkspaceFrame Resolve(
        float canvasWidth,
        float canvasHeight,
        string? site,
        in NativeContentRect native,
        in NativeContentRect extent,
        float? remembered = null)
    {
        string where = string.IsNullOrEmpty(site) ? "none" : site;
        bool topTab = string.Equals(where, "top-tab", StringComparison.OrdinalIgnoreCase);
        bool shortcut = string.Equals(where, "shortcut", StringComparison.OrdinalIgnoreCase);
        _ = remembered;
        NoteBackground(extent);
        if (extent.Found && !string.IsNullOrEmpty(extent.Why))
            LastFrameExtentWhy = extent.Why;

        if ((topTab || shortcut) && native.Found)
        {
            if (extent.Found)
                LastOverhang = NativeBackgroundOverhang.From(native, extent);
            var applied = ApplyNative(extent.Found ? extent : native);
            // Keep 32 design units below the native 48-unit outer bar. Grow upward
            // only into spare space; the existing footer/bottom edge stays fixed.
            applied = new ModsWorkspaceFrame(applied.Width, applied.Height, applied.X, applied.Y,
                applied.Path, WorkspaceHostLayout.TabLift(canvasHeight, applied.Y, applied.Height));
            // Round 4: the canonical position in game too (the leftover inner-tab row removed).
            applied = WorkspaceHostLayout.PinCanonical(canvasWidth, canvasHeight, applied, WorkspaceHostLayout.PinInGameFrame);
            Remember(native.Height > 0.01f ? native.Width / native.Height : applied.Aspect);
            Last = applied;
            return applied;
        }

        // In game, a top-tab workspace waits hidden for the native frame (MayPresent); the
        // floating card is only its placeholder geometry.
        if (topTab)
        {
            // Round 4 (user report 2026-09-30, "the window is small and then grows"): before the native
            // frame is read (the first frame of an open, reason=no-overlay), lay out with the canonical
            // in-game frame, not the 1276x800 floating card. The path is native-pending: in-game geometry
            // (strip inside the frame top), but not native, so the workspace stays hidden (MayPresent)
            // until the native read, and a pane laid out meanwhile already has the size it is shown at.
            var canonical = WorkspaceHostLayout.InGameFrameFor(canvasWidth, canvasHeight, out _);
            var pending = new ModsWorkspaceFrame(canonical.Width, canonical.Height, canonical.X, canonical.Y, PathNativePending,
                WorkspaceHostLayout.TabLift(MenuPanelLayout.Positive(canvasHeight, 1080f), canonical.Y, canonical.Height));
            Last = pending;
            return pending;
        }

        // HOSTS1: every Progenitor-owned window (the title, the shortcut at the title, the standalone
        // window in a world) is sized as the in-game frame, never as a floating card.
        var title = WorkspaceHostLayout.TitleFrame(canvasWidth, canvasHeight, out string source);
        LastTitleSource = source;
        Last = title;
        return title;
    }

    internal static string FormatLog(in ModsWorkspaceFrame frame, in NativeContentRect native, string? reason)
    {
        MenuPanelLayout.Split(frame.Width, out float sidebar, out float detail, out _);
        string line = LogPrefix + frame.Path
            + " applied=" + ((int)frame.Width) + "x" + ((int)frame.Height)
            + " at=(" + ((int)frame.X) + "," + ((int)frame.Y) + ")"
            + " split=" + ((int)sidebar) + "/" + ((int)detail)
            + " native=" + (native.Found
                ? ((int)native.Width) + "x" + ((int)native.Height)
                : "none");
        if (frame.CoverBelow > 0f) line += " pinned=down" + ((int)frame.CoverBelow);
        if (!string.IsNullOrEmpty(reason)) line += " reason=" + reason;
        return line;
    }

    internal static bool ShouldLog(string line) =>
        !string.Equals(line, LastLog, StringComparison.Ordinal);

    internal static void NoteLogged(string line) => LastLog = line ?? "";

    internal static bool MatchesNative(in ModsWorkspaceFrame frame, in NativeContentRect native, float tolerance = MatchTolerance)
    {
        if (!native.Found || string.Equals(frame.Path, PathNativeCapped, StringComparison.Ordinal))
            return false;
        return Math.Abs(frame.Width - native.Width) <= tolerance
            && Math.Abs(frame.Height - native.Height) <= tolerance
            && Math.Abs(frame.X - native.X) <= tolerance
            && Math.Abs(frame.Y - native.Y) <= tolerance;
    }

    internal static bool SameRatioAndSplit(in ModsWorkspaceFrame a, in ModsWorkspaceFrame b, float ratioTolerance = 0.02f)
    {
        if (a.Height < 1f || b.Height < 1f) return false;
        if (Math.Abs(a.Aspect - b.Aspect) > ratioTolerance) return false;
        MenuPanelLayout.Split(a.Width, out float aSide, out float aDetail, out _);
        MenuPanelLayout.Split(b.Width, out float bSide, out float bDetail, out _);
        float aShare = aSide / a.Width;
        float bShare = bSide / b.Width;
        return Math.Abs(aShare - bShare) < 0.01f
            && Math.Abs((aDetail / a.Width) - (bDetail / b.Width)) < 0.01f;
    }

    /// <summary>
    /// Cover-with-crop: scale the image so it fills the frame on both axes,
    /// then crop the overflow. Never stretches, never letterboxes. Size only;
    /// <see cref="CoverOffset"/> places the overflow.
    /// </summary>
    internal static void CoverCrop(
        float frameW, float frameH, float imageW, float imageH,
        out float drawnW, out float drawnH)
    {
        frameW = MenuPanelLayout.Positive(frameW, 1f);
        frameH = MenuPanelLayout.Positive(frameH, 1f);
        imageW = MenuPanelLayout.Positive(imageW, ImageWidth);
        imageH = MenuPanelLayout.Positive(imageH, ImageHeight);
        float scale = Math.Max(frameW / imageW, frameH / imageH);
        drawnW = imageW * scale;
        drawnH = imageH * scale;
    }

    /// <summary>
    /// Shift the cover-cropped image inside the frame. Align 0.5 is centered;
    /// 1 keeps the right (or top) edge, so side-cropping eats the left
    /// rune circle rather than the tree.
    /// </summary>
    internal static void CoverOffset(
        float frameW, float frameH, float drawnW, float drawnH,
        float alignX, float alignY,
        out float offsetX, out float offsetY)
    {
        frameW = MenuPanelLayout.Positive(frameW, 1f);
        frameH = MenuPanelLayout.Positive(frameH, 1f);
        drawnW = MenuPanelLayout.Positive(drawnW, frameW);
        drawnH = MenuPanelLayout.Positive(drawnH, frameH);
        if (alignX < 0f) alignX = 0f;
        else if (alignX > 1f) alignX = 1f;
        if (alignY < 0f) alignY = 0f;
        else if (alignY > 1f) alignY = 1f;
        // Positive align keeps that edge of the source: the image shifts the
        // other way so overflow is cropped from the opposite side.
        offsetX = (0.5f - alignX) * (drawnW - frameW);
        offsetY = (0.5f - alignY) * (drawnH - frameH);
    }

    /// <summary>
    /// Source-pixel window of the PNG that a cover-crop with the given align
    /// actually shows. Used to confirm the figure survives at both aspects.
    /// </summary>
    internal static void VisibleSourceRect(
        float frameW, float frameH, float imageW, float imageH,
        float alignX, float alignY,
        out float srcX, out float srcY, out float srcW, out float srcH)
    {
        CoverCrop(frameW, frameH, imageW, imageH, out float drawnW, out float drawnH);
        CoverOffset(frameW, frameH, drawnW, drawnH, alignX, alignY, out float ox, out float oy);
        imageW = MenuPanelLayout.Positive(imageW, ImageWidth);
        imageH = MenuPanelLayout.Positive(imageH, ImageHeight);
        float scaleX = drawnW / imageW;
        float scaleY = drawnH / imageH;
        float imageLeft = -drawnW * 0.5f + ox;
        float imageBottom = -drawnH * 0.5f + oy;
        float frameLeft = -frameW * 0.5f;
        float frameBottom = -frameH * 0.5f;
        srcX = (frameLeft - imageLeft) / scaleX;
        srcY = (frameBottom - imageBottom) / scaleY;
        srcW = frameW / scaleX;
        srcH = frameH / scaleY;
    }

    /// <summary>
    /// True when the focal region (the tree) lies fully inside the visible crop.
    /// </summary>
    internal static bool FigureSurvivesCrop(
        float srcX, float srcY, float srcW, float srcH,
        float imageW, float imageH)
    {
        imageW = MenuPanelLayout.Positive(imageW, ImageWidth);
        imageH = MenuPanelLayout.Positive(imageH, ImageHeight);
        float figL = FigureLeft * imageW;
        float figR = FigureRight * imageW;
        float figB = FigureBottom * imageH;
        float figT = FigureTop * imageH;
        const float tol = 1f;
        return srcX <= figL + tol
            && srcX + srcW >= figR - tol
            && srcY <= figB + tol
            && srcY + srcH >= figT - tol;
    }

    /// <summary>
    /// Intersection of the image layout rect and the holder/frame. This is the
    /// clipped, visible rect — not the pre-mask Image size.
    /// </summary>
    internal static void ClippedVisibleRect(
        float imageX, float imageY, float imageW, float imageH,
        float holderX, float holderY, float holderW, float holderH,
        out float visX, out float visY, out float visW, out float visH)
    {
        float left = Math.Max(imageX, holderX);
        float bottom = Math.Max(imageY, holderY);
        float right = Math.Min(imageX + imageW, holderX + holderW);
        float top = Math.Min(imageY + imageH, holderY + holderH);
        visX = left;
        visY = bottom;
        visW = Math.Max(0f, right - left);
        visH = Math.Max(0f, top - bottom);
    }

    /// <summary>
    /// Visible backdrop covers the whole workspace frame (both panes), not
    /// only the detail pane. The live 916-wide-in-a-1276-frame case fails.
    /// </summary>
    internal static bool VisibleRectCoversFrame(
        float visX, float visY, float visW, float visH,
        float frameX, float frameY, float frameW, float frameH,
        float tolerance = 2f)
    {
        if (visW + tolerance < frameW || visH + tolerance < frameH) return false;
        return visX <= frameX + tolerance
            && visY <= frameY + tolerance
            && visX + visW >= frameX + frameW - tolerance
            && visY + visH >= frameY + frameH - tolerance;
    }

    /// <summary>
    /// True when an owned graphic with alpha above zero extends outside the
    /// workspace frame. A full-screen tint, scrim or haze at the title site
    /// would crush the native smoke to the Options look. Alpha 0 blockers
    /// do not count — they take hits without covering pixels.
    /// </summary>
    internal static bool CoversOutsideFrame(
        float graphicX, float graphicY, float graphicW, float graphicH, float alpha,
        float frameX, float frameY, float frameW, float frameH,
        float epsilon = 1f)
    {
        if (alpha <= 0.001f || graphicW <= 0f || graphicH <= 0f) return false;
        return graphicX < frameX - epsilon
            || graphicY < frameY - epsilon
            || graphicX + graphicW > frameX + frameW + epsilon
            || graphicY + graphicH > frameY + frameH + epsilon;
    }

    /// <summary>
    /// Drawability of the workspace backdrop. Cross-canvas draw order is
    /// <c>Canvas.sortingOrder</c>, not sibling index inside one canvas: two
    /// root canvases at equal order have no defined winner, which is how the
    /// art painted over the panels while this guard stayed green.
    /// </summary>
    internal static bool BackdropIsDrawable(
        bool assigned,
        bool active,
        bool graphicEnabled,
        float alpha,
        float rectW,
        float rectH,
        int backdropSorting,
        int panelsSorting,
        int meshCount)
    {
        return assigned
            && active
            && graphicEnabled
            && alpha > 0.01f
            && rectW > 1f
            && rectH > 1f
            && meshCount > 0
            && ModsCanvasBand.LayerOrderIsResolved(
                ModsCanvasBand.Blocker, backdropSorting, panelsSorting, ModsCanvasBand.Overlay)
            && backdropSorting < panelsSorting;
    }

    /// <summary>Sidebar and detail must share one fill alpha.</summary>
    internal static bool PanesShareFillAlpha(float sidebarAlpha, float detailAlpha, float tolerance = 0.001f)
        => Math.Abs(sidebarAlpha - detailAlpha) <= tolerance
        && Math.Abs(sidebarAlpha - PanelFillAlpha) <= tolerance;

    /// <summary>
    /// Escape that closes the in-game tab-site workspace also dismisses
    /// FullscreenMenu, the way Escape on Spellbook / Social returns to the game.
    /// Main-menu and a focused search-clear do not.
    /// </summary>
    internal static bool ShouldDismissFullscreenMenuOnEscape(string? site, bool searchCleared) =>
        WorkspaceInputPolicy.DismissesGameMenuOnClose(WorkspaceHostLayout.HostOf(site), searchCleared);

    internal static string FallbackReason(in NativeContentRect native, string? site)
    {
        if (!string.Equals(site, "top-tab", StringComparison.OrdinalIgnoreCase) && !native.Found)
            return "title-host-" + (string.IsNullOrEmpty(LastTitleSource) ? WorkspaceHostLayout.SourceModel : LastTitleSource);
        if (!native.Found)
            return string.IsNullOrEmpty(native.Why) ? "no-container" : native.Why;
        return "";
    }

    private static ModsWorkspaceFrame ApplyNative(in NativeContentRect native)
    {
        float w = native.Width;
        float h = native.Height;
        float x = native.X;
        float y = native.Y;
        string path = PathNative;
        if (h > 0.01f && w / h > MaxAspect)
        {
            // Keep the native center so pillarbox is even on both sides.
            w = h * MaxAspect;
            path = PathNativeCapped;
        }

        return new ModsWorkspaceFrame(w, h, x, y, path);
    }

    private static void Remember(float aspect)
    {
        if (aspect > 0.01f && !float.IsNaN(aspect) && !float.IsInfinity(aspect))
            RememberedRatio = aspect;
    }

    private static void NoteBackground(in NativeContentRect background)
    {
        LastBackground = background;
        if (background.Found)
            LastBackgroundWhy = string.IsNullOrEmpty(background.Why) ? BackgroundWhyLive : background.Why;
        else
            LastBackgroundWhy = string.IsNullOrEmpty(background.Why) ? BackgroundWhyMissing : background.Why;
    }
}

/// <summary>
/// One coherent overlay band for Mods, lowest first: pointer blocker, workspace
/// backdrop art, panels, popups (dialogs, the join screens). Numbers are derived from
/// <see cref="UiOwnership.OverlaySorting"/> (the live-verified popup order)
/// rather than a free-standing constant that would drift. Native title canvases
/// that sit inside this band are why the title view is taken out of view instead
/// of painted over; modal canvases above OverlaySorting (e.g. 9000) are ignored
/// so this overlay never rises to outrank them.
/// </summary>
internal static class ModsCanvasBand
{
    internal const int BlockerSlot = 0;
    internal const int BackdropSlot = 1;
    internal const int PanelsSlot = 2;
    internal const int OverlaySlot = 3;
    internal const int SlotCount = 4;

    /// <summary>The overlay band (popups such as the join screens) — <see cref="UiOwnership.OverlaySorting"/>.</summary>
    internal static int Overlay => UiOwnership.OverlaySorting;

    internal static int Panels => Overlay - (OverlaySlot - PanelsSlot);
    internal static int Backdrop => Overlay - (OverlaySlot - BackdropSlot);
    internal static int Blocker => Overlay - (OverlaySlot - BlockerSlot);

    /// <summary>
    /// Strictly increasing draw order. Equal orders (the live 5000/5000 backdrop
    /// vs panels case) have no defined winner and must fail.
    /// </summary>
    internal static bool LayerOrderIsResolved(int blocker, int backdrop, int panels, int overlay)
        => blocker < backdrop && backdrop < panels && panels < overlay;

    /// <summary>
    /// Highest native (non-owned) canvas order strictly below the popups. Orders at
    /// or above them are the game's modals, not the title list.
    /// </summary>
    internal static int NativeMaxBelowOverlay(IReadOnlyList<int> nativeOrders)
    {
        int max = int.MinValue;
        if (nativeOrders == null) return 0;
        for (int i = 0; i < nativeOrders.Count; i++)
        {
            int o = nativeOrders[i];
            if (o >= Overlay) continue;
            if (o > max) max = o;
        }
        return max == int.MinValue ? 0 : max;
    }

    /// <summary>
    /// Whether this canvas counts as native for <see cref="NativeMaxBelowOverlay"/>.
    /// <c>overlay-N</c> under a <c>progenitor-ui-</c> root is ours (Panels 4999);
    /// a name-only prefix check is the live gen4 4999 leak.
    /// </summary>
    internal static bool CountTowardNativeMax(string? name, IReadOnlyList<string>? ancestors)
        => !UiOwnership.IsOwnedInHierarchy(name, ancestors);

    /// <summary>
    /// Native title sits in our band, so painting over it is undefined: hide the
    /// view instead. A native order of 4999 or 5000 on the current OverlaySorting
    /// is this case.
    /// </summary>
    internal static bool NativeOccupiesBand(int nativeOrder)
        => nativeOrder >= Blocker && nativeOrder <= Overlay;
}

/// <summary>
/// Native <c>FullscreenMenu.ViewParent</c> or hosted-view background rect in
/// overlay canvas units, center-anchored like <see cref="ModsWorkspaceFrame"/>.
/// </summary>
internal readonly struct NativeContentRect
{
    internal static NativeContentRect Missing(string why) =>
        new(false, 0f, 0f, 0f, 0f, why ?? "no-container");

    /// <summary>
    /// Overlay-local min/max of <c>ViewParent</c> after <c>TransformPoint</c> /
    /// <c>InverseTransformPoint</c>. Two corners, not <c>GetWorldCorners</c>: that
    /// out-array is an IL2CPP interop miss (zeros → <c>degenerate</c>) on this
    /// runtime. Same projection <c>NativeModsEntry.ReadScreenRect</c> uses.
    /// </summary>
    internal static NativeContentRect FromOverlayLocal(
        float minX, float minY, float maxX, float maxY, float minSize = 8f)
    {
        if (float.IsNaN(minX) || float.IsNaN(minY) || float.IsNaN(maxX) || float.IsNaN(maxY)
            || float.IsInfinity(minX) || float.IsInfinity(minY)
            || float.IsInfinity(maxX) || float.IsInfinity(maxY))
            return Missing("non-finite");
        float left = Math.Min(minX, maxX);
        float right = Math.Max(minX, maxX);
        float bottom = Math.Min(minY, maxY);
        float top = Math.Max(minY, maxY);
        float w = right - left;
        float h = top - bottom;
        if (w < minSize || h < minSize)
            return Missing("degenerate");
        return new NativeContentRect(true, w, h, (left + right) * 0.5f, (bottom + top) * 0.5f, "");
    }

    internal NativeContentRect(bool found, float width, float height, float x, float y, string why)
    {
        Found = found;
        Width = width;
        Height = height;
        X = x;
        Y = y;
        Why = why ?? "";
    }

    internal bool Found { get; }
    internal float Width { get; }
    internal float Height { get; }
    internal float X { get; }
    internal float Y { get; }
    internal string Why { get; }
    internal float Left => X - Width * 0.5f;
    internal float Right => X + Width * 0.5f;
    internal float Bottom => Y - Height * 0.5f;
    internal float Top => Y + Height * 0.5f;

    internal NativeContentRect WithWhy(string why) =>
        new(Found, Width, Height, X, Y, why ?? "");

    /// <summary>
    /// Client-area pixels (origin top-left) to overlay-local center-anchored
    /// units. Same conversion the live <c>rects</c> tests use at 3840×1080.
    /// </summary>
    internal static NativeContentRect FromClient(
        float clientX, float clientY, float clientW, float clientH,
        float screenW, float screenH)
    {
        float unityBottom = screenH - clientY - clientH;
        return new NativeContentRect(
            true, clientW, clientH,
            clientX + clientW * 0.5f - screenW * 0.5f,
            unityBottom + clientH * 0.5f - screenH * 0.5f,
            "");
    }

    /// <summary>Inverse of <see cref="FromClient"/>.</summary>
    internal void ToClient(
        float screenW, float screenH,
        out float clientX, out float clientY, out float clientW, out float clientH)
    {
        clientW = Width;
        clientH = Height;
        clientX = X - Width * 0.5f + screenW * 0.5f;
        float unityBottom = Y - Height * 0.5f + screenH * 0.5f;
        clientY = screenH - unityBottom - Height;
    }
}

/// <summary>
/// How far the grown frame extent extends past
/// <c>FullscreenMenu.ViewParent</c>. Cached from the once-per-open chrome
/// walk so a later frame (hosted instance gone) can still cover the footer.
/// </summary>
internal readonly struct NativeBackgroundOverhang
{
    internal NativeBackgroundOverhang(float left, float right, float bottom, float top, bool measured)
    {
        Left = left;
        Right = right;
        Bottom = bottom;
        Top = top;
        Measured = measured;
    }

    internal float Left { get; }
    internal float Right { get; }
    internal float Bottom { get; }
    internal float Top { get; }
    internal bool Measured { get; }

    internal static NativeBackgroundOverhang From(
        in NativeContentRect container, in NativeContentRect background)
    {
        if (!container.Found || !background.Found) return default;
        return new NativeBackgroundOverhang(
            Math.Max(0f, container.Left - background.Left),
            Math.Max(0f, background.Right - container.Right),
            Math.Max(0f, container.Bottom - background.Bottom),
            Math.Max(0f, background.Top - container.Top),
            measured: true);
    }

    internal NativeContentRect Apply(in NativeContentRect container)
    {
        if (!Measured || !container.Found) return container;
        return NativeContentRect.FromOverlayLocal(
            container.Left - Left,
            container.Bottom - Bottom,
            container.Right + Right,
            container.Top + Top);
    }
}

/// <summary>
/// One native <c>Graphic</c> under the FullscreenMenu root, used by the
/// game-free frame-extent union and the on-demand <c>mods_ui menu</c> probe.
/// </summary>
internal readonly struct NativeChromeGraphic
{
    internal NativeChromeGraphic(
        string path, string type, string zone, in NativeContentRect rect,
        bool enabled = true, bool activeInHierarchy = true,
        string? sprite = null, float r = 0f, float g = 0f, float b = 0f, float a = 1f,
        int sortingOrder = 0)
    {
        Path = path ?? "";
        Type = type ?? "";
        Zone = zone ?? NativeFrameExtent.ZoneElsewhere;
        Rect = rect;
        Enabled = enabled;
        ActiveInHierarchy = activeInHierarchy;
        Sprite = sprite ?? "";
        R = r;
        G = g;
        B = b;
        A = a;
        SortingOrder = sortingOrder;
    }

    internal string Path { get; }
    internal string Type { get; }
    internal string Zone { get; }
    internal NativeContentRect Rect { get; }
    internal bool Enabled { get; }
    internal bool ActiveInHierarchy { get; }
    internal string Sprite { get; }
    internal float R { get; }
    internal float G { get; }
    internal float B { get; }
    internal float A { get; }
    internal int SortingOrder { get; }
    internal string Name
    {
        get
        {
            string path = Path ?? "";
            int slash = path.LastIndexOf('/');
            return slash >= 0 && slash < path.Length - 1 ? path.Substring(slash + 1) : path;
        }
    }
}

/// <summary>
/// In-game frame = union of <c>FullscreenMenu.ViewParent</c>
/// and every native chrome graphic of the menu root whose rect overlaps
/// ViewParent horizontally, whose bottom is below ViewParent by at most
/// <see cref="FooterMaxBelow"/> overlay units (the footer strip), excluding
/// the outer-tab group above and anything wider than
/// <see cref="MaxWidthFactor"/> × ViewParent.
///
/// Expected objects: <c>EscapeMenuView.ConnectionInfoRect</c> /
/// <c>ConnectionInfoText*</c> / <c>ReportABugButton</c> / <c>FAQButton</c>
/// when System is the hosted page; unnamed
/// FullscreenMenu children (<c>FullscreenMenu</c> declares no footer field). HUD
/// <c>BottomBarParent</c> is the ability bar, typically wider than
/// 1.2× ViewParent, so the width cap excludes it.
/// </summary>
internal static class NativeFrameExtent
{
    internal const float FooterMaxBelow = 120f;
    internal const float MaxWidthFactor = 1.2f;
    internal const string ZoneViewParent = "viewparent";
    internal const string ZoneHosted = "hosted";
    internal const string ZoneOuterTab = "outer-tab";
    internal const string ZoneElsewhere = "elsewhere";
    internal const string ViewParentName = "ViewParent";
    internal const string WhyViewParent = "ViewParent";

    internal static bool QualifiesAsFooter(
        in NativeContentRect viewParent, in NativeContentRect graphic, string? zone)
    {
        if (!viewParent.Found || !graphic.Found) return false;
        if (string.Equals(zone, ZoneOuterTab, StringComparison.Ordinal)) return false;
        // A full-screen menu root can pass the width/bottom tests at 16:9.
        // Footer chrome must not reach above ViewParent into the navigation.
        if (graphic.Top > viewParent.Top + 0.5f) return false;
        if (graphic.Width > viewParent.Width * MaxWidthFactor + 0.5f) return false;
        if (graphic.Right <= viewParent.Left + 0.5f || graphic.Left >= viewParent.Right - 0.5f)
            return false;
        float below = viewParent.Bottom - graphic.Bottom;
        return below > 0.5f && below <= FooterMaxBelow;
    }

    /// <summary>
    /// Union ViewParent with every qualifying footer graphic. <paramref name="why"/>
    /// names the objects that set the bottom (<c>ViewParent</c> plus those that
    /// lowered it).
    /// </summary>
    internal static NativeContentRect Grow(
        in NativeContentRect viewParent,
        IReadOnlyList<NativeChromeGraphic>? graphics,
        out string why)
    {
        if (!viewParent.Found)
        {
            why = string.IsNullOrEmpty(viewParent.Why) ? "no-viewparent" : viewParent.Why;
            return viewParent;
        }

        float left = viewParent.Left;
        float right = viewParent.Right;
        float bottom = viewParent.Bottom;
        float top = viewParent.Top;
        var names = new List<string> { ViewParentName };
        if (graphics != null)
        {
            for (int i = 0; i < graphics.Count; i++)
            {
                var g = graphics[i];
                if (!QualifiesAsFooter(viewParent, g.Rect, g.Zone)) continue;
                if (g.Rect.Left < left) left = g.Rect.Left;
                if (g.Rect.Right > right) right = g.Rect.Right;
                if (g.Rect.Top > top) top = g.Rect.Top;
                if (g.Rect.Bottom < bottom - 0.5f)
                {
                    bottom = g.Rect.Bottom;
                    string name = string.IsNullOrEmpty(g.Name) ? "footer" : g.Name;
                    bool seen = false;
                    for (int n = 0; n < names.Count; n++)
                    {
                        if (string.Equals(names[n], name, StringComparison.Ordinal))
                        {
                            seen = true;
                            break;
                        }
                    }

                    if (!seen) names.Add(name);
                }
            }
        }

        why = string.Join("+", names);
        var grown = NativeContentRect.FromOverlayLocal(left, bottom, right, top);
        return grown.Found ? grown.WithWhy(why) : viewParent.WithWhy(why);
    }
}

/// <summary>
/// In-game tab-site outcomes. Native <c>SetState</c> destroys the previous
/// <c>FullscreenMenuView</c> instance and instantiates the next under
/// <c>ViewParent</c> (SetState.c); tab-to-tab is a replace, not a fade.
/// <c>ImageFadeSwap</c> is intra-view (spell-school art), not tab switching.
/// HUD open/close uses <c>HUDMenu.OpenMotions</c> on the menu host, which
/// already ran when FullscreenMenu came up.
/// </summary>
internal static class ModsTabSwitch
{
    internal readonly struct Outcome
    {
        internal bool ModsShown { get; init; }
        internal bool NativeViewCovered { get; init; }
        internal int VisibleScreenCount { get; init; }
        internal bool ModsTabSelected { get; init; }
        internal bool OpeningNativeTabStillHighlighted { get; init; }
    }

    /// <summary>
    /// Clicking Spellbook / Social / … while Mods is open: the owned observer
    /// <c>Close()</c>s Mods, native <c>OnLeftClicked</c> still runs, so one
    /// native screen is shown and that tab is highlighted.
    /// </summary>
    internal static Outcome AfterNativeTabActivated() => new()
    {
        ModsShown = false,
        NativeViewCovered = false,
        VisibleScreenCount = 1,
        ModsTabSelected = false,
        OpeningNativeTabStillHighlighted = false,
    };

    /// <summary>
    /// Opening Mods while a native tab is showing. Tab-to-tab is SetState
    /// replace (SetState.c:111–119 deactivates the previous
    /// <c>FullscreenMenuView</c> instance). Opening MODS must do the same:
    /// deactivate that instance, Refresh native outer tabs unselected, highlight
    /// only Mods. Covering the Spellbook page so two screens draw at once is
    /// the live gen4 defect.
    /// </summary>
    internal static Outcome AfterModsOpenedFromNativeTab() => new()
    {
        ModsShown = true,
        NativeViewCovered = false,
        VisibleScreenCount = 1,
        ModsTabSelected = true,
        OpeningNativeTabStillHighlighted = false,
    };

    /// <summary>Kept name for older tests; same as <see cref="AfterModsOpenedFromNativeTab"/>.</summary>
    internal static Outcome AfterModsOpenedOverNative() => AfterModsOpenedFromNativeTab();

    /// <summary>
    /// One Mods workspace, no native hosted page, only the Mods tab marked
    /// selected. Live gen4 (Spellbook still drawn, SPELLBOOK+MODS lit) fails.
    /// </summary>
    internal static bool IsSingleModsScreen(in Outcome o)
        => o.ModsShown
        && !o.NativeViewCovered
        && o.VisibleScreenCount == 1
        && o.ModsTabSelected
        && !o.OpeningNativeTabStillHighlighted;
}

/// <summary>
/// CSAMPLE1 (decided 2026-10-10, "left, like Back"): a plain button on a custom mod page is the game's compact
/// button at the row's left edge, sized to its caption, as the game's Back button; an inline block puts its buttons side
/// by side from the left. Game-free.
/// </summary>
internal static class PageButtonLayout
{
    /// <summary>The caption size: the settings grid's control font.</summary>
    internal const float FontSize = 16f;

    /// <summary>Caption padding, both sides together.</summary>
    internal const float CaptionPad = 48f;

    /// <summary>The narrowest page button (a one-word caption still reads as a button, as the game's Back does).</summary>
    internal const float MinWidth = 140f;

    /// <summary>Space between the buttons of one inline row.</summary>
    internal const float Gap = 8f;

    /// <summary>The height every grid control is drawn at.</summary>
    internal const float Height = ModSettingsGrid.ControlHeight;

    /// <summary>A button's width for <paramref name="caption"/>, never wider than <paramref name="maxWidth"/>.</summary>
    internal static float Width(string? caption, float maxWidth = float.PositiveInfinity)
    {
        float w = Math.Max(MinWidth, CellLayout.EstimateTextWidth(caption, FontSize) + CaptionPad);
        return maxWidth > 0f && w > maxWidth ? maxWidth : w;
    }

    /// <summary>
    /// The left edges of <paramref name="widths"/> laid side by side from 0 with <see cref="Gap"/>; a button that would
    /// cross <paramref name="rowWidth"/> keeps its place (the row clips it), so the order never changes.
    /// </summary>
    internal static float[] Lefts(IReadOnlyList<float> widths)
    {
        var lefts = new float[widths.Count];
        float x = 0f;
        for (int i = 0; i < widths.Count; i++)
        {
            lefts[i] = x;
            x += widths[i] + Gap;
        }

        return lefts;
    }
}
