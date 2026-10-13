using System;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// AUTOUPDATE3 (decision 7): the in-game notice an admin gets from the joined server about its mod updates
/// (<c>2 server updates available</c>, <c>ChatSkin 1.3.0 applied live</c>, a failed update rolled back, updates waiting for a
/// restart), drawn with the dialog kit like the server change notice (<see cref="JoinChangeNoticePanel"/>): <b>View</b> opens
/// the Server tab's Updates group, <b>Restart now</b> (while restart updates wait) asks the server to restart after warning its
/// players, <b>OK</b> (and Escape) closes it. Closes after <see cref="NoticeSeconds"/>, when the client leaves and on every
/// Core unload. The server sends it only to admins (<c>ServerAdminList.AuthorizeShared</c>) and decides every request again.
/// </summary>
internal static class UpdateNoticePanel
{
    private const string PluginId = "progenitor";
    internal const string Title = "Server updates";
    internal const string ViewCaption = "View";
    internal const string RestartCaption = "Restart now";
    internal const string CancelRestartCaption = "Cancel restart";
    internal const string CloseCaption = "OK";
    internal const string ButtonsRow = "update-notice-buttons";
    internal const string RestartKey = "restart";

    /// <summary>How long the notice stays up when nobody answers it.</summary>
    internal const double NoticeSeconds = 30;

    private static readonly object Gate = new();
    private static readonly object Owner = new();
    private static readonly OwnedPanel Owned = new();
    private static bool _subscribed;
    private static int _generation;
    private static IDisposable? _escape;

    /// <summary>The notice on show (the readback), or null.</summary>
    internal static (string Kind, string Text, bool OfferRestart)? Shown { get; private set; }

    internal static bool IsOpen => Owned.IsOpen;

    /// <summary>The answer to the last Restart now or Cancel restart pressed here (the readback).</summary>
    internal static string LastAnswer { get; private set; } = "";

    internal static void Install()
    {
        lock (Gate)
        {
            if (_subscribed) return;
            _subscribed = true;
        }

        JoinAdminClient.UpdateNoticeReceived += OnNotice;
        Handshake.ServerContextCleared += OnLeft;
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
            JoinAdminClient.UpdateNoticeReceived -= OnNotice;
            Handshake.ServerContextCleared -= OnLeft;
        }

        try { Dev.MainThread.DropOwnedBy(Owner, typeof(UpdateNoticePanel).Assembly); }
        catch { /* teardown */ }
        Close();
    }

    private static void OnLeft(string reason) => Close();

    private static void OnNotice(string kind, string text, bool offerRestart)
    {
        try { Open(kind, text, offerRestart); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: the server update notice threw: " + e.Message); }
    }

    /// <summary>Show a notice (replacing one on show). <paramref name="offerRestart"/> adds Restart now.</summary>
    internal static void Open(string kind, string text, bool offerRestart, bool offerCancel = false)
    {
        Close();
        Shown = (kind, text, offerRestart);
        var panel = Ui.Ui.Panel(Title, Owner, PluginId);
        var stale = Owned.Adopt(panel);
        if (stale != null && !ReferenceEquals(stale, panel))
        {
            try { stale.Dispose(); }
            catch { /* */ }
        }

        if (panel.Enabled)
        {
            try
            {
                var content = JoinNoticeDialog.Content(Title, text, null,
                    offerRestart ? "Restart now warns the players online first; you can cancel it during the warning." : null,
                    ViewCaption, CloseCaption, ButtonsRow);
                if (offerRestart) content.ExtraButtons.Add(new DialogButton(RestartKey, RestartCaption, DialogButtonRole.Option));
                else if (offerCancel) content.ExtraButtons.Add(new DialogButton(RestartKey, CancelRestartCaption, DialogButtonRole.Option));
                var view = JoinNoticeDialog.Build(panel, content);
                view.Buttons?.OnClick(UiDialogs.CellMain, View);
                view.Buttons?.OnClick(UiDialogs.CellCancel, Close);
                if (offerRestart) view.Buttons?.OnClick(RestartKey, () => Send(JoinAdminOps.UpdateRestart));
                else if (offerCancel) view.Buttons?.OnClick(RestartKey, () => Send(JoinAdminOps.UpdateCancelRestart));
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: the server update notice build threw: " + e.Message); }
        }

        Ui.Ui.Show(panel);
        try { _escape ??= Ui.Ui.PushEscape(OnEscape); }
        catch { /* OK and the timer still close it */ }
        int generation;
        lock (Gate) generation = ++_generation;
        try
        {
            Dev.MainThread.PostDelayed(NoticeSeconds, () =>
            {
                bool current;
                lock (Gate) current = generation == _generation;
                if (current) Close();
            }, Owner);
        }
        catch { /* OK and leaving still close it */ }
    }

    /// <summary>View: the Mods window on the Server tab's Updates group.</summary>
    internal static void View()
    {
        Close();
        try { ModsMenu.OpenServerUpdates(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: opening the Server tab's Updates threw: " + e.Message); }
    }

    /// <summary>Restart now / Cancel restart: one admin request; the answer replaces the notice.</summary>
    internal static string Send(string op)
    {
        Close();
        bool sent = JoinAdminClient.Request(new JoinAdminRequest { Op = op }, out string refusal, response =>
        {
            string answer = response == null ? "No answer from the server."
                : response.Ok ? (response.Note.Length > 0 ? response.Note : "Done.")
                : response.Error;
            LastAnswer = answer;
            bool counting = response?.Ok == true && op == JoinAdminOps.UpdateRestart && (response.State?.Updates?.Countdown ?? -1) >= 0;
            Open("answer", answer, offerRestart: false, offerCancel: counting);
        });
        if (!sent)
        {
            LastAnswer = refusal;
            Open("answer", refusal, offerRestart: false);
        }

        return sent ? "sent" : refusal;
    }

    private static bool OnEscape()
    {
        if (!Owned.IsOpen) return false;
        Close();
        return true;
    }

    internal static void Close()
    {
        lock (Gate) _generation++;
        Shown = null;
        _escape?.Dispose();
        _escape = null;
        var panel = Owned.Take();
        if (panel == null) return;
        try { panel.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: the server update notice dispose threw: " + e.Message); }
    }
}
