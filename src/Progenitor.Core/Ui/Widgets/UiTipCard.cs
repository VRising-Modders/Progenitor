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
            var view = new UiTipCardView(panel);
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

    public IDisposable? ShowList(object owner, string pluginId, IReadOnlyList<UiTipInfo> tips, Action showAgain)
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
            var leases = new List<IDisposable>
            {
                again.OnClick(() =>
                {
                    try { showAgain(); }
                    catch (Exception e) { Plugin.Logger?.LogWarning("Ui: tip reset threw: " + e.Message); }
                }),
                close.OnClick(() => panel.Dispose()),
            };
            panel.Show();
            return new ActionDisposable(() =>
            {
                foreach (var l in leases)
                {
                    try { l.Dispose(); }
                    catch { /* */ }
                }

                try { panel.Dispose(); }
                catch { /* */ }
            });
        }
        catch
        {
            try { panel.Dispose(); }
            catch { /* */ }
            throw;
        }
    }
}

/// <summary>A shown tip card's panel.</summary>
internal sealed class UiTipCardView : IUiTipCardView
{
    private readonly IUiPanel _panel;
    private readonly List<IDisposable> _leases = new();
    private bool _closed;

    internal UiTipCardView(IUiPanel panel) => _panel = panel;

    internal void Keep(IDisposable lease)
    {
        if (lease != null) _leases.Add(lease);
    }

    public IUiPanel? Panel => _panel;

    public bool Shown => !_closed && !_panel.IsDisposed && _panel.State == UiHandleState.Shown;

    public bool HiddenByOther => !_closed && !_panel.IsDisposed && _panel.State != UiHandleState.Shown;

    public void Place(ScreenRect anchor)
    {
        if (!Shown || _panel is not UiHandle { Runtime: IRuntimePanel runtime }) return;
        UiHelpRuntime.Current?.PlaceTipCard(runtime, anchor);
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
