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
/// HUB1: which tab row the back and forward keys act on (the mouse's back and forward buttons, Alt+Left and Alt+Right,
/// the same Core keybinds as the Mods workspace's history). The topmost window under the pointer that has a tab history
/// wins; else the newest shown window that holds the input while shown; else nothing, and the press goes on to the
/// Mods workspace. Game-free: <see cref="Pick"/> is the rule; <see cref="TryNavigate"/> applies it to the live rows.
/// </summary>
internal static class UiTabHistoryRoute
{
    /// <summary>One candidate row: its panel's shown order, whether the pointer is over that panel, whether it holds the input.</summary>
    internal readonly struct Candidate
    {
        internal Candidate(int index, long shownSequence, bool underPointer, bool holdsInput)
        {
            Index = index;
            ShownSequence = shownSequence;
            UnderPointer = underPointer;
            HoldsInput = holdsInput;
        }

        internal int Index { get; }

        internal long ShownSequence { get; }

        internal bool UnderPointer { get; }

        internal bool HoldsInput { get; }
    }

    /// <summary>The candidate that takes the press: under the pointer first (the newest), else holding the input (the newest); -1 for none.</summary>
    internal static int Pick(IReadOnlyList<Candidate> candidates)
    {
        int pick = -1;
        long best = long.MinValue;
        foreach (var c in candidates)
            if (c.UnderPointer && c.ShownSequence > best) { pick = c.Index; best = c.ShownSequence; }
        if (pick >= 0) return pick;
        foreach (var c in candidates)
            if (c.HoldsInput && c.ShownSequence > best) { pick = c.Index; best = c.ShownSequence; }
        return pick;
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

    /// <summary>The live tab rows (only those with a history, by default).</summary>
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
        foreach (var row in Live(historyOnly: false))
            if (ReferenceEquals(row.HostPanel, panel) && row.Visible) return row;
        return null;
    }

    /// <summary>Whether the pointer is over a panel; tests replace it. Production asks the runtime.</summary>
    internal static Func<UiPanelHandle, bool> UnderPointer { get; set; } = DefaultUnderPointer;

    /// <summary>Tests: forget the rows and put the production pointer probe back.</summary>
    internal static void ResetForTests()
    {
        lock (Rows) Rows.Clear();
        UnderPointer = DefaultUnderPointer;
    }

    private static bool DefaultUnderPointer(UiPanelHandle panel)
    {
        try { return panel.RuntimePanel != null && Ui.CurrentRuntime().PanelUnderPointer(panel.RuntimePanel); }
        catch { return false; }
    }

    /// <summary>Whether a live tab row with a history sits on a shown panel (no allocation: the keybind pump asks every frame).</summary>
    internal static bool AnyShown()
    {
        lock (Rows)
        {
            foreach (var w in Rows)
                if (w.TryGetTarget(out var t) && t.HistoryOn && t.HostPanel.State == UiHandleState.Shown) return true;
        }

        return false;
    }

    /// <summary>The row the press would act on now, or null (the workspace then takes it).</summary>
    internal static UiTabsHandle? Target()
    {
        if (!AnyShown()) return null;
        var rows = Live();
        if (rows.Count == 0) return null;
        var candidates = new List<Candidate>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].HostPanel is not UiPanelHandle panel || panel.IsDisposed || panel.State != UiHandleState.Shown) continue;
            if (!rows[i].Visible) continue;
            // A workspace page's own tabs are the workspace's: its history covers them.
            if (ModsMenu.IsWorkspacePanel(panel)) continue;
            bool holds = panel.ShownCaptureMode == UiCaptureMode.WhileShown;
            bool under = panel.ShownCaptureMode != UiCaptureMode.Never && UnderPointer(panel);
            if (!holds && !under) continue;
            candidates.Add(new Candidate(i, panel.ShownSequence, under, holds));
        }

        int pick = Pick(candidates);
        return pick >= 0 ? rows[pick] : null;
    }

    /// <summary>
    /// A back (<paramref name="back"/>) or forward press: true when a window's tab row took it (it moved, or it is the
    /// target and is at that end of its history, so the press is not passed to the workspace behind it).
    /// </summary>
    internal static bool TryNavigate(bool back)
    {
        UiTabsHandle? target;
        try { target = Target(); }
        catch { return false; }
        if (target == null) return false;
        if (back) target.HistoryBack();
        else target.HistoryForward();
        return true;
    }
}
