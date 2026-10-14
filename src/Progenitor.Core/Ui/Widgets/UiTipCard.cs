using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7b: the tip card and the tip list drawn with the kit. A card is an ordinary kit panel with the game's popup frame
/// (<see cref="UiLook.GameWindow"/>, no backdrop: not modal) holding a framework dialog's own rows
/// (<see cref="UiDialogView.Build"/> without the modal chrome): the centered title over the game's title rule, the
/// centered body, and the dialog's button row of the game's popup buttons in the dialog order, <b>Show all tips</b> on
/// the dismissal's side (left) and <b>Got it</b> as the main answer (right, the accent gold). It takes input only while
/// hovered (<see cref="UiCaptureMode.WhenHovered"/>), so it is never a controller screen (NAV1 navigates dialogs and
/// windows that capture while shown): a stray A press never answers it; B / Escape closes it as any shown panel, which
/// counts as Got it. After each show and once a frame while it is up, the
/// runtime's help half places it (<see cref="IUiHelpRuntime.PlaceTipCard"/>, <see cref="UiTipLayout"/>); without one
/// it keeps the framework's panel placement. Created under the plugin's own UI root and owner, so the plugin's
/// <c>Ui.RemoveAll</c> sweep reaches it too.
/// </summary>
internal sealed class UiTipCardPresenter : IUiTipPresenter
{
    public IUiTipCardView? Present(UiTipRequest tip, Action gotIt, Action showAll)
    {
        string title = string.IsNullOrWhiteSpace(tip.Title) ? UiTipCopy.ListTitle : tip.Title;
        var panel = Ui.Panel(title, tip.Owner, tip.PluginId);
        try
        {
            // The game's popup frame without the dialog's backdrop (not modal), and the dialog's own rows: the centered
            // title over the game's title rule, the centered body, and the popup-button row in the dialog order.
            panel.GameWindow();
            Ui.HidePaneTitle(panel);
            panel.CaptureMode = UiCaptureMode.WhenHovered;
            var dialog = UiDialogView.Build(panel, Content(title, tip.Body), chrome: false);
            var view = new UiTipCardView(panel)
            {
                Title = (dialog.Heading as UiHandle)?.Runtime,
                Buttons = (dialog.Buttons as UiCellRowHandle)?.NavWidget(null) as IRuntimeCellRow,
            };
            if (dialog.Buttons != null)
            {
                view.Keep(dialog.Buttons.OnClick(UiTipCopy.GotItKey, gotIt));
                view.Keep(dialog.Buttons.OnClick(UiTipCopy.ShowAllKey, showAll));
            }

            panel.Show();
            return view;
        }
        catch
        {
            try { panel.Dispose(); }
            catch { /* */ }
            throw;
        }
    }

    /// <summary>
    /// The card's rows as a framework dialog's (DIALOGS2): <b>Show all tips</b> in the dismissal's place on the left,
    /// <b>Got it</b> as the main answer on the right in the accent gold, both the game's popup buttons at one width.
    /// </summary>
    internal static DialogContent Content(string title, string body) => new()
    {
        Shape = UiDialogShape.Confirm,
        Title = title ?? "",
        Body = body ?? "",
        MainCaption = UiTipCopy.GotIt,
        MainTone = CellTone.Gold,
        MainKey = UiTipCopy.GotItKey,
        CancelCaption = UiTipCopy.ShowAll,
        CancelKey = UiTipCopy.ShowAllKey,
        RowKey = UiTipCopy.ButtonsRow,
        Footer = "",
    };

    public IUiTipListView? ShowList(object owner, string pluginId, IReadOnlyList<UiTipInfo> tips, Action showAgain)
    {
        var panel = Ui.Panel(UiTipCopy.ListTitle, owner, pluginId);
        try
        {
            panel.GameWindow();
            panel.CaptureMode = UiCaptureMode.WhenHovered;
            panel.ContentAlignment = "left";
            if (tips.Count == 0) panel.Note(UiTipCopy.ListEmpty);
            foreach (var tip in tips)
            {
                panel.Heading(tip.Title);
                if (!string.IsNullOrWhiteSpace(tip.Body)) panel.Note(tip.Body);
            }

            panel.BeginInline();
            var again = panel.Button(UiTipCopy.ShowAgain);
            var close = panel.Button(UiTipCopy.Close);
            panel.EndInline();
            var view = new UiTipListView(panel);
            view.Keep(again.OnClick(() =>
            {
                try { showAgain(); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: tip reset threw: " + e.Message); }
            }));
            view.Keep(close.OnClick(() =>
            {
                view.Dispose();
                UiTips.ListClosed(view);
            }));
            panel.Show();
            return view;
        }
        catch
        {
            try { panel.Dispose(); }
            catch { /* */ }
            throw;
        }
    }
}

/// <summary>
/// P7b: the controls of a panel a tip card must not cover, in screen pixels: every visible control that takes input
/// (buttons, toggles, fields, sliders, dropdowns, key fields), and of a cell row its button, field, switch and expander
/// cells, or the whole row when the row itself takes a click (a list row). Labels, headings, notes and read-outs are
/// not controls. Only what lies over the anchor's own rect counts.
/// </summary>
internal static class UiTipObstacles
{
    internal static IReadOnlyList<ScreenRect> Of(IUiPanel? anchor, ScreenRect anchorRect)
    {
        var list = new List<ScreenRect>();
        if (anchor is not UiPanelHandle host || host.IsDisposed || !anchorRect.IsKnown) return list;
        IUiRuntime runtime;
        try { runtime = Ui.CurrentRuntime(); }
        catch { return list; }
        foreach (var child in host.ChildrenSnapshot())
        {
            if (child == null || child.IsDisposed || !child.Visible) continue;
            switch (child)
            {
                case UiLabelHandle or UiToneLabelHandle or UiInfoHandle:
                    continue;
                case UiCellRowHandle row:
                    AddRow(list, row, anchorRect);
                    continue;
                default:
                    if (child.Runtime is not { Alive: true } widget) continue;
                    ScreenRect r;
                    try { r = runtime.WidgetRect(widget); }
                    catch { continue; }
                    Add(list, r, anchorRect);
                    continue;
            }
        }

        return list;
    }

    private static void AddRow(List<ScreenRect> list, UiCellRowHandle row, ScreenRect anchorRect)
    {
        bool rowClick = false;
        try { rowClick = row.NavWidget(null) is IRuntimeCellRow { ClickEvent: not null }; }
        catch { rowClick = false; }
        if (rowClick)
        {
            Add(list, row.NavRect(null), anchorRect);
            return;
        }

        foreach (var cell in row.Cells)
        {
            if (!cell.Visible || !IsControl(cell.Kind)) continue;
            Add(list, row.NavRect(cell.Key), anchorRect);
        }
    }

    internal static bool IsControl(CellKind kind) =>
        kind is CellKind.Button or CellKind.Input or CellKind.Switch or CellKind.Chevron;

    private static void Add(List<ScreenRect> list, ScreenRect r, ScreenRect anchorRect)
    {
        if (!r.IsKnown) return;
        if (r.MaxX <= anchorRect.MinX || r.MinX >= anchorRect.MaxX || r.MaxY <= anchorRect.MinY || r.MinY >= anchorRect.MaxY) return;
        list.Add(r);
    }
}

/// <summary>The open tip list's panel.</summary>
internal sealed class UiTipListView : IUiTipListView
{
    private readonly IUiPanel _panel;
    private readonly List<IDisposable> _leases = new();
    private bool _closed;

    internal UiTipListView(IUiPanel panel) => _panel = panel;

    internal void Keep(IDisposable lease)
    {
        if (lease != null) _leases.Add(lease);
    }

    /// <summary>On screen: not closed, not disposed, not hidden (the Escape sweep hides it).</summary>
    public bool Open => !_closed && !_panel.IsDisposed && _panel.State == UiHandleState.Shown;

    internal IUiPanel Panel => _panel;

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        foreach (var l in _leases)
        {
            try { l.Dispose(); }
            catch { /* */ }
        }

        _leases.Clear();
        try { _panel.Dispose(); }
        catch { /* */ }
    }
}

/// <summary>A shown tip card's panel.</summary>
internal sealed class UiTipCardView : IUiTipCardView
{
    private readonly IUiPanel _panel;
    private readonly List<IDisposable> _leases = new();
    private bool _closed;

    internal UiTipCardView(IUiPanel panel) => _panel = panel;

    /// <summary>The dialog title row's runtime widget (tinted gold), or null.</summary>
    internal IRuntimeWidget? Title { get; init; }

    /// <summary>The button row's runtime row (the main answer's caption is tinted gold), or null.</summary>
    internal IRuntimeCellRow? Buttons { get; init; }

    internal void Keep(IDisposable lease)
    {
        if (lease != null) _leases.Add(lease);
    }

    public IUiPanel? Panel => _panel;

    public bool Shown => !_closed && !_panel.IsDisposed && _panel.State == UiHandleState.Shown;

    public bool HiddenByOther => !_closed && !_panel.IsDisposed && _panel.State != UiHandleState.Shown;

    public void Place(ScreenRect anchor, IReadOnlyList<ScreenRect> controls)
    {
        if (!Shown || _panel is not UiHandle { Runtime: IRuntimePanel runtime }) return;
        var help = UiHelpRuntime.Current;
        if (help == null) return;
        help.PlaceTipCard(runtime, anchor, controls);
        help.TintTipCard(Title, Buttons, UiTipCopy.GotItKey);
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        foreach (var l in _leases)
        {
            try { l.Dispose(); }
            catch { /* */ }
        }

        _leases.Clear();
        try { _panel.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: tip card dispose threw: " + e.Message); }
    }
}
