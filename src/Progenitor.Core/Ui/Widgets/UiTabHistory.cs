using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// HUB1: a tab row's browser-style history (<see cref="UiTabsOptions.History"/>): every tab the player selects is pushed;
/// back and forward walk it. Selecting a tab after going back drops the forward part, as a browser does. Game-free.
/// </summary>
internal sealed class UiTabHistory
{
    /// <summary>The most entries kept; the oldest go first.</summary>
    internal const int MaxEntries = 64;

    private readonly List<int> _tabs = new();

    internal UiTabHistory(int first) => _tabs.Add(first);

    /// <summary>The entry the row is on now.</summary>
    internal int Index { get; private set; }

    /// <summary>The entries, oldest first (readback and tests).</summary>
    internal IReadOnlyList<int> Entries => _tabs;

    /// <summary>The tab of the entry the row is on now.</summary>
    internal int Current => _tabs[Index];

    internal bool CanGoBack => Index > 0;

    internal bool CanGoForward => Index < _tabs.Count - 1;

    /// <summary>
    /// The player selected <paramref name="tab"/>: drop the forward part and push it (selecting the tab the row is on
    /// pushes nothing).
    /// </summary>
    internal void Push(int tab)
    {
        if (tab == Current) return;
        if (Index < _tabs.Count - 1) _tabs.RemoveRange(Index + 1, _tabs.Count - Index - 1);
        _tabs.Add(tab);
        if (_tabs.Count > MaxEntries) _tabs.RemoveAt(0);
        Index = _tabs.Count - 1;
    }

    /// <summary>The tab one entry back (the row then shows it), or -1 at the start.</summary>
    internal int Back()
    {
        if (!CanGoBack) return -1;
        Index--;
        return _tabs[Index];
    }

    /// <summary>The tab one entry forward, or -1 at the end.</summary>
    internal int Forward()
    {
        if (!CanGoForward) return -1;
        Index++;
        return _tabs[Index];
    }

    /// <summary>Start again at <paramref name="tab"/> (the window opened anew).</summary>
    internal void Reset(int tab)
    {
        _tabs.Clear();
        _tabs.Add(tab);
        Index = 0;
    }
}

/// <summary>
/// HUB1: which tab row the back and forward keys act on. The keys are the Mods workspace's Navigate back and forward
/// (the mouse's back and forward buttons, Menu scope: only while the Mods menu is open) and the tab-history keys
/// Navigate back and forward (keys) (Alt+Left, Alt+Right, Window scope: in play too). The window drawn on top under the
/// pointer that has a tab history wins; else, only while the pointer is over no panel at all (not the Mods workspace,
/// not another window), the window drawn on top that holds the input while shown; else nothing, and the press goes on
/// to the Mods workspace's history. A target at the end of its history in that direction does not take the press
/// either. Game-free: <see cref="Pick"/> is the rule; <see cref="Target"/> applies it to the live rows once a frame.
/// </summary>
internal static class UiTabHistoryRoute
{
    /// <summary>One candidate row: its panel's draw order, its shown order (the tie-break), and the pointer and input facts.</summary>
    internal readonly struct Candidate
    {
        internal Candidate(int index, int drawOrder, long shownSequence, bool underPointer, bool holdsInput)
        {
            Index = index;
            DrawOrder = drawOrder;
            ShownSequence = shownSequence;
            UnderPointer = underPointer;
            HoldsInput = holdsInput;
        }

        internal int Index { get; }

        /// <summary>The panel's canvas sort order (what draws on top: higher).</summary>
        internal int DrawOrder { get; }

        internal long ShownSequence { get; }

        internal bool UnderPointer { get; }

        internal bool HoldsInput { get; }

        internal bool Above(in Candidate other) =>
            DrawOrder != other.DrawOrder ? DrawOrder > other.DrawOrder : ShownSequence > other.ShownSequence;
    }

    /// <summary>
    /// The candidate that takes the press: the topmost under the pointer; else, when <paramref name="pointerOverPanel"/> is
    /// false (the pointer is over no panel), the topmost holding the input; -1 for none.
    /// </summary>
    internal static int Pick(IReadOnlyList<Candidate> candidates, bool pointerOverPanel)
    {
        int pick = -1;
        for (int i = 0; i < candidates.Count; i++)
            if (candidates[i].UnderPointer && (pick < 0 || candidates[i].Above(candidates[pick]))) pick = i;
        if (pick >= 0) return candidates[pick].Index;
        if (pointerOverPanel) return -1;
        for (int i = 0; i < candidates.Count; i++)
            if (candidates[i].HoldsInput && (pick < 0 || candidates[i].Above(candidates[pick]))) pick = i;
        return pick >= 0 ? candidates[pick].Index : -1;
    }

    private static readonly List<WeakReference<UiTabsHandle>> Rows = new();

    internal static void Register(UiTabsHandle tabs)
    {
        lock (Rows)
        {
            Rows.RemoveAll(w => !w.TryGetTarget(out var t) || t.IsDisposed);
            Rows.Add(new WeakReference<UiTabsHandle>(tabs));
        }
    }

    /// <summary>The live tab rows (only those with a history, by default). Allocates: tests and the readback.</summary>
    internal static List<UiTabsHandle> Live(bool historyOnly = true)
    {
        var list = new List<UiTabsHandle>();
        lock (Rows)
        {
            Rows.RemoveAll(w => !w.TryGetTarget(out var t) || t.IsDisposed);
            foreach (var w in Rows)
                if (w.TryGetTarget(out var t) && (!historyOnly || t.HistoryOn)) list.Add(t);
        }

        return list;
    }

    /// <summary>HUB1: the first visible tab row on <paramref name="panel"/> (a window's controller triggers step it), or null.</summary>
    internal static UiTabsHandle? RowOn(UiPanelHandle panel)
    {
        lock (Rows)
        {
            foreach (var w in Rows)
                if (w.TryGetTarget(out var row) && !row.IsDisposed && ReferenceEquals(row.HostPanel, panel) && row.Visible) return row;
        }

        return null;
    }

    /// <summary>Whether the pointer is over a panel; tests replace it. Production asks the runtime.</summary>
    internal static Func<UiPanelHandle, bool> UnderPointer
    {
        get => _underPointer;
        set
        {
            _underPointer = value ?? DefaultUnderPointer;
            ResetCache();
        }
    }

    private static Func<UiPanelHandle, bool> _underPointer = DefaultUnderPointer;

    /// <summary>A panel's draw order (its canvas sort order, cached by the runtime); tests replace it.</summary>
    internal static Func<UiPanelHandle, int> DrawOrder { get; set; } = DefaultDrawOrder;

    /// <summary>Tests: forget the rows and put the production probes back.</summary>
    internal static void ResetForTests()
    {
        lock (Rows) Rows.Clear();
        _underPointer = DefaultUnderPointer;
        DrawOrder = DefaultDrawOrder;
        ResetCache();
    }

    private static bool DefaultUnderPointer(UiPanelHandle panel)
    {
        try { return panel.RuntimePanel != null && Ui.CurrentRuntime().PanelUnderPointer(panel.RuntimePanel); }
        catch { return false; }
    }

    private static int DefaultDrawOrder(UiPanelHandle panel)
    {
        try { return panel.RuntimePanel != null ? Ui.CurrentRuntime().PanelDrawOrder(panel.RuntimePanel) : 0; }
        catch { return 0; }
    }

    // ---- once a frame, without allocating ----------------------------------------------------------------------------

    private static int _cachedFrame = int.MinValue;
    private static UiTabsHandle? _cached;
    private static readonly List<UiTabsHandle> RowScratch = new();
    private static readonly List<Candidate> CandidateScratch = new();
    private static readonly List<UiPanelHandle> PanelScratch = new();

    /// <summary>Drop the frame's answer (a window shown, hidden or moved since it was taken; tests).</summary>
    internal static void ResetCache()
    {
        _cachedFrame = int.MinValue;
        _cached = null;
    }

    /// <summary>The row a press acts on this frame, or null (the workspace then takes it). Taken once a frame (<see cref="Keybinds.PumpFrame"/>).</summary>
    internal static UiTabsHandle? Target()
    {
        int frame = Keybinds.PumpFrame;
        if (frame == _cachedFrame && (_cached == null || !_cached.IsDisposed)) return _cached;
        _cached = Compute();
        _cachedFrame = frame;
        return _cached;
    }

    private static UiTabsHandle? Compute()
    {
        RowScratch.Clear();
        lock (Rows)
        {
            foreach (var w in Rows)
                if (w.TryGetTarget(out var t) && t.HistoryOn && t.Visible && t.HostPanel.State == UiHandleState.Shown) RowScratch.Add(t);
        }

        if (RowScratch.Count == 0) return null;
        CandidateScratch.Clear();
        for (int i = 0; i < RowScratch.Count; i++)
        {
            if (RowScratch[i].HostPanel is not UiPanelHandle panel || panel.IsDisposed) continue;
            // A workspace page's own tabs are the workspace's: its history covers them.
            if (ModsMenu.IsWorkspacePanel(panel)) continue;
            bool holds = panel.ShownCaptureMode == UiCaptureMode.WhileShown;
            bool under = panel.ShownCaptureMode != UiCaptureMode.Never && UnderPointer(panel);
            if (!holds && !under) continue;
            CandidateScratch.Add(new Candidate(i, DrawOrder(panel), panel.ShownSequence, under, holds));
        }

        if (CandidateScratch.Count == 0) return null;
        int pick = Pick(CandidateScratch, PointerOverAnyPanel());
        var target = pick >= 0 ? RowScratch[pick] : null;
        RowScratch.Clear();
        return target;
    }

    /// <summary>Whether the pointer is over any shown panel (the Mods workspace's panes, a window, a dialog).</summary>
    private static bool PointerOverAnyPanel()
    {
        Ui.ShownPanelsInto(PanelScratch);
        bool over = false;
        foreach (var panel in PanelScratch)
        {
            if (!UnderPointer(panel)) continue;
            over = true;
            break;
        }

        PanelScratch.Clear();
        return over;
    }

    /// <summary>
    /// A back (<paramref name="back"/>) or forward press: true when a window's tab row took it and moved. A target at the
    /// end of its history in that direction does not take it, so the press goes on to the Mods workspace.
    /// </summary>
    internal static bool TryNavigate(bool back)
    {
        UiTabsHandle? target;
        try { target = Target(); }
        catch { return false; }
        if (target == null) return false;
        return back ? target.HistoryBack() : target.HistoryForward();
    }
}
