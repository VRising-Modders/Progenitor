using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Progenitor.Ui;

/// <summary>
/// NAV1: the production <see cref="INavHost"/>. The screen a controller acts on is the topmost that takes input:
/// an open dialog, else the newest shown window that captures input while shown (<see cref="UiCaptureMode.WhileShown"/>),
/// else the Mods workspace. A window that captures only under the pointer is never navigated: a controller there
/// is playing the game.
/// </summary>
internal sealed class UiNavHost : INavHost
{
    internal static readonly UiNavHost Instance = new();

    public NavSurface? Active()
    {
        if (UiDialog.Current is { IsOpen: true } dialog && dialog.Panel is UiPanelHandle dialogPanel && !dialogPanel.IsDisposed)
            return DialogSurface(dialog, dialogPanel);

        UiPanelHandle? top = null;
        foreach (var panel in Ui.ShownPanelsSnapshot())
        {
            if (panel.ShownCaptureMode != UiCaptureMode.WhileShown || ModsMenu.IsWorkspacePanel(panel)) continue;
            if (top == null || panel.ShownSequence > top.ShownSequence) top = panel;
        }

        if (top != null)
            return new NavSurface("panel:" + RuntimeHelpers.GetHashCode(top), new IReadOnlyList<UiHandle>[] { top.ChildrenSnapshot() });

        return ModsMenu.NavSurface();
    }

    private static NavSurface DialogSurface(UiDialog dialog, UiPanelHandle panel)
    {
        var surface = new NavSurface("dialog:" + RuntimeHelpers.GetHashCode(dialog), new IReadOnlyList<UiHandle>[] { panel.ChildrenSnapshot() });
        var view = dialog.View;
        var content = dialog.Content;
        // A long Choose: its first option. Else the dismissal (see StartKey).
        if (view != null && view.OptionRows.Count > 0 && view.OptionRows[0] is UiCellRowHandle first)
        {
            surface.Start = new NavTarget(first);
            return surface;
        }

        if (view?.Buttons is UiCellRowHandle buttons)
        {
            var keys = buttons.NavCellKeys();
            string want = StartKey(content);
            if (keys.Contains(want)) surface.Start = new NavTarget(buttons, want);
            else if (keys.Count > 0) surface.Start = new NavTarget(buttons, keys[0]);
        }

        return surface;
    }

    /// <summary>
    /// The cell a dialog's focus starts on: the dismissal (No, Cancel, Close) whenever there is one, else the main answer.
    /// A controller confirms with one press right, then A. Live 2026-10-10: a focus that started on the main answer
    /// uninstalled a mod with one A, and that question was marked neither by the danger flag nor by the danger tone
    /// (its Yes is orange), so no "is this destructive" test is trusted here.
    /// </summary>
    internal static string StartKey(DialogContent content) =>
        content.CancelCaption.Length > 0 ? content.CancelKey : content.MainKey;

    public ScreenRect Rect(NavTarget target)
    {
        var handle = target.Handle;
        if (handle == null || handle.IsDisposed) return ScreenRect.Unknown;
        if (handle is UiCellRowHandle row) return row.NavRect(target.Part);
        if (handle.Runtime is not { Alive: true } widget) return ScreenRect.Unknown;
        try { return Ui.CurrentRuntime().WidgetRect(widget); }
        catch { return ScreenRect.Unknown; }
    }

    public ScreenRect DrawnRect(NavTarget target)
    {
        var handle = target.Handle;
        if (handle == null || handle.IsDisposed) return ScreenRect.Unknown;
        if (handle is UiCellRowHandle row) return row.NavRect(target.Part);
        if (handle.Runtime is not { Alive: true } widget) return ScreenRect.Unknown;
        try { return Ui.CurrentRuntime().WidgetDrawnRect(widget); }
        catch { return ScreenRect.Unknown; }
    }

    public void Mark(NavTarget target, bool on)
    {
        var handle = target.Handle;
        if (handle == null || handle.IsDisposed) return;
        IRuntimeWidget? widget = handle is UiCellRowHandle row ? row.NavWidget(target.Part) : handle.Runtime;
        if (widget is not { Alive: true }) return;
        int option = handle is UiOptionsHandle { Multi: true } && int.TryParse(target.Part, out int i) ? i : -1;
        Ui.CurrentRuntime().SetNavFocus(widget, option, on);
    }

    public void Reveal(NavTarget target)
    {
        var handle = target.Handle;
        if (handle == null || handle.IsDisposed) return;
        // WINDOWS1: a control in a window's info column is revealed by scrolling that column.
        IRuntimeWidget? widget = handle is UiCellRowHandle cells ? cells.NavWidget(target.Part) : handle.Runtime;
        try { if (widget is { Alive: true } && Ui.CurrentRuntime().RevealInColumn(widget)) return; }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: nav column reveal threw: " + e.Message); }
        // The panel that holds it scrolls it into view (the section jumps' reveal).
        foreach (var panel in Ui.ShownPanelsSnapshot())
        {
            if (Array.IndexOf(panel.ChildrenSnapshot(), handle) < 0) continue;
            UiReveal.TryReveal(panel, handle, handle, out _, out _, out _);
            return;
        }
    }

    public bool FocusText(UiHandle handle)
    {
        if (handle?.Runtime is not { Alive: true } widget) return false;
        return Ui.CurrentRuntime().FocusTextIn(widget);
    }

    public void Click(UiHandle handle)
    {
        if (handle == null || handle.IsDisposed || handle.WidgetId == 0 || !handle.Enabled) return;
        UiSlotTable.BootstrapDispatch(handle.WidgetId, (byte)UiSlotKind.Click, 0, false, null);
    }

    public void Escape() => Ui.PressEscapeForNav();

    public void Scroll(NavTarget target, float amount)
    {
        var handle = target.Handle;
        if (handle == null || handle.IsDisposed) return;
        IRuntimeWidget? widget = handle is UiCellRowHandle row ? row.NavWidget(null) : handle.Runtime;
        if (widget is { Alive: true }) Ui.CurrentRuntime().ScrollNear(widget, amount);
    }
}
