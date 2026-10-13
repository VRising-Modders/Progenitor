using System;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// JOINNOTICE: the notice for a server change mid-session ("This server's mods changed." with <b>View mods</b> and
/// <b>OK</b>), drawn with the dialog kit (<see cref="JoinNoticeDialog"/>: the game's popup frame, title, body, vanilla
/// buttons; requested 2026-10-05). The per-server mod pause notice's lifecycle (<see cref="ServerModPauseNotice"/>): it
/// subscribes from <c>ModManifest.Install</c> on a client and closes after <see cref="JoinChangeNoticePolicy.NoticeSeconds"/>,
/// on OK (and Escape), on View, when the client leaves and on every Core unload. The decision and the words are
/// <see cref="JoinFlowClient"/>'s and <see cref="JoinChangeNoticePolicy"/>'s.
/// </summary>
internal static class JoinChangeNoticePanel
{
    private const string PluginId = "progenitor";

    /// <summary>The button row's key: <c>main</c> is View, <c>cancel</c> is OK.</summary>
    internal const string ButtonsRow = "change-notice-buttons";

    private static readonly object Gate = new();
    private static readonly object Owner = new();
    private static readonly OwnedPanel Owned = new();
    private static bool _subscribed;
    private static int _generation;
    private static IDisposable? _escape;

    internal static bool IsOpen => Owned.IsOpen;

    internal static void Install()
    {
        lock (Gate)
        {
            if (_subscribed) return;
            _subscribed = true;
        }

        JoinFlowClient.ChangeNoticeRequested += OnRequested;
        JoinFlowClient.ChangeNoticeDismissed += Close;
        try { Plugin.Frame += DismissHidden; }
        catch { /* Open/Close still work */ }
    }

    internal static void Uninstall()
    {
        bool subscribed;
        lock (Gate)
        {
            subscribed = _subscribed;
            _subscribed = false;
        }

        if (subscribed)
        {
            JoinFlowClient.ChangeNoticeRequested -= OnRequested;
            JoinFlowClient.ChangeNoticeDismissed -= Close;
        }

        try { Plugin.Frame -= DismissHidden; }
        catch { /* teardown */ }
        try { Dev.MainThread.DropOwnedBy(Owner, typeof(JoinChangeNoticePanel).Assembly); }
        catch { /* teardown */ }
        Close();
    }

    private static void OnRequested(JoinChangeNoticeState notice)
    {
        try { Open(notice); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: server change notice open threw: " + e.Message); }
    }

    internal static void Open(JoinChangeNoticeState notice)
    {
        if (notice == null) return;
        DropHeld();
        var panel = Ui.Ui.Panel(JoinChangeNoticePolicy.Title, Owner, PluginId);
        var stale = Owned.Adopt(panel);
        if (stale != null && !ReferenceEquals(stale, panel))
        {
            try { stale.Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: server change notice stale dispose threw: " + e.Message); }
        }

        if (panel.Enabled)
        {
            try
            {
                var view = JoinNoticeDialog.Build(panel, JoinNoticeDialog.Content(JoinChangeNoticePolicy.Title, notice.Text, null, null,
                    notice.ViewCaption, JoinChangeNoticePolicy.CloseButton, ButtonsRow));
                view.Buttons?.OnClick(UiDialogs.CellMain, () => JoinFlowClient.ViewChangeNotice());
                view.Buttons?.OnClick(UiDialogs.CellCancel, () => JoinFlowClient.CloseChangeNotice());
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: server change notice build threw: " + e.Message); }
        }

        Ui.Ui.Show(panel);
        try { _escape ??= Ui.Ui.PushEscape(OnEscape); }
        catch { /* OK and the timer still close it */ }
        int generation;
        lock (Gate) generation = ++_generation;
        try
        {
            Dev.MainThread.PostDelayed(JoinChangeNoticePolicy.NoticeSeconds, () =>
            {
                bool current;
                lock (Gate) current = generation == _generation;
                if (current) JoinFlowClient.CloseChangeNotice();
            }, Owner);
        }
        catch { /* OK and leaving still close it */ }
    }

    private static bool OnEscape()
    {
        if (!Owned.IsOpen) return false;
        JoinFlowClient.CloseChangeNotice();
        return true;
    }

    private static void Close() => DropHeld();

    private static void DismissHidden()
    {
        if (Owned.Held == null || Owned.IsOpen) return;
        JoinFlowClient.CloseChangeNotice();
    }

    private static void DropHeld()
    {
        _escape?.Dispose();
        _escape = null;
        var panel = Owned.Take();
        if (panel == null) return;
        try { panel.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: server change notice dispose threw: " + e.Message); }
    }
}
