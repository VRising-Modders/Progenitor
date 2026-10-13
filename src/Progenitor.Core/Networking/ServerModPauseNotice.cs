using System;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// The notice per-server mod pause shows: <c>Mods paused for this server</c>, one line per paused mod with its readable
/// name and why (<see cref="ServerModPauseText.Paused"/>). Drawn with the dialog kit (<see cref="JoinNoticeDialog"/>): the
/// game's popup frame, title, body and a vanilla <b>OK</b> button. It subscribes from <c>ModManifest.Install</c> on a
/// client, is disposed (never left hidden) when it is closed (OK, Escape), when the client leaves, after
/// <see cref="ServerModPauseText.NoticeSeconds"/>, and on every Core unload.
/// </summary>
internal static class ServerModPauseNotice
{
    private const string PluginId = "progenitor";

    /// <summary>The button row's key (its OK is the <c>main</c> cell).</summary>
    internal const string ButtonsRow = "pause-notice-buttons";

    private static readonly object Gate = new();
    private static readonly object Owner = new();
    private static readonly OwnedPanel Owned = new();
    private static bool _subscribed;
    private static int _generation;
    private static IDisposable? _escape;
    private static PauseNoticeContent? _shown;

    internal static bool IsOpen => Owned.IsOpen;

    /// <summary>The notice on show (its parts), or null; the readback and the tests read it.</summary>
    internal static PauseNoticeContent? Shown => Owned.IsOpen ? _shown : null;

    internal static void Install()
    {
        lock (Gate)
        {
            if (_subscribed) return;
            _subscribed = true;
        }
        ServerModPause.PauseNoticeRequested += OnRequested;
        ServerModPause.NoticeDismissed += Close;
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
            ServerModPause.PauseNoticeRequested -= OnRequested;
            ServerModPause.NoticeDismissed -= Close;
        }
        try { Plugin.Frame -= DismissHidden; }
        catch { /* teardown */ }
        try { Dev.MainThread.DropOwnedBy(Owner, typeof(ServerModPauseNotice).Assembly); }
        catch { /* teardown */ }
        Close();
    }

    private static void OnRequested(PauseNoticeContent content)
    {
        try { Open(content); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: mod pause notice open threw: " + e.Message); }
    }

    /// <summary>A plain notice (title and one paragraph).</summary>
    internal static void Open(string title, string text) => Open(ServerModPauseText.Plain(title, text));

    internal static void Open(PauseNoticeContent content) => Open(content, ServerModPauseText.NoticeSeconds);

    /// <summary>
    /// Open <paramref name="content"/>; it closes by itself after <paramref name="closeAfterSeconds"/> (0 or less: only
    /// OK, Escape or the next leave close it; CLIENTPOLICY3's disconnect notice on the main menu).
    /// </summary>
    internal static void Open(PauseNoticeContent content, double closeAfterSeconds)
    {
        if (content == null) return;
        DropHeld(logClosed: false);
        var panel = Ui.Ui.Panel(content.Title, Owner, PluginId);
        var stale = Owned.Adopt(panel);
        if (stale != null && !ReferenceEquals(stale, panel))
        {
            try { stale.Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: mod pause notice stale dispose threw: " + e.Message); }
        }
        if (panel.Enabled)
        {
            try
            {
                // NOTICEOPEN1: a fix the player can make here gets its button first (Open Install Mods, Open Mods); OK stays.
                bool open = content.OpenCaption.Length > 0 && OpenRequested != null;
                var view = JoinNoticeDialog.Build(panel, JoinNoticeDialog.Content(content.Title, content.Body, content.Lines,
                    content.Note, open ? content.OpenCaption : ServerModPauseText.CloseButton, open ? ServerModPauseText.CloseButton : null, ButtonsRow));
                if (open)
                {
                    view.Buttons?.OnClick(UiDialogs.CellMain, () => PressOpen());
                    view.Buttons?.OnClick(UiDialogs.CellCancel, Close);
                }
                else view.Buttons?.OnClick(UiDialogs.CellMain, Close);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: mod pause notice build threw: " + e.Message); }
        }
        _shown = content;
        Ui.Ui.Show(panel);
        try { _escape ??= Ui.Ui.PushEscape(OnEscape); }
        catch { /* OK and the timer still close it */ }
        Plugin.Logger?.LogInfo("Ui: mod pause notice opened: " + content.Text);

        int generation;
        lock (Gate) generation = ++_generation;
        if (closeAfterSeconds <= 0) return;
        try
        {
            Dev.MainThread.PostDelayed(closeAfterSeconds, () =>
            {
                bool current;
                lock (Gate) current = generation == _generation;
                if (current) Close();
            }, Owner);
        }
        catch { /* the close button and the leave still close it */ }
    }

    /// <summary>
    /// NOTICEOPEN1: open what the notice's button names (live: <c>ModsMenu.OpenForPolicyNotice</c>, the Mods workspace on
    /// the Install Mods tab at the mod's row, or the Mods list at its page). Null: no button.
    /// </summary>
    internal static Action<string, string, string>? OpenRequested { get; set; }

    /// <summary>NOTICEOPEN1: the open button (and the bridge's press): close the notice, then open. Returns what happened.</summary>
    internal static string PressOpen()
    {
        var shown = Shown;
        var open = OpenRequested;
        // E1 (live 2026-10-07): the notice closes by itself after its seconds; its fix stays available (the bridge, and a
        // player who reads the readback) from the last notice of this connection.
        bool closedAlready = false;
        if (shown == null)
        {
            shown = ClientPolicyNoticeClient.LastActionable;
            closedAlready = shown != null;
        }

        if (shown == null) return "no notice";
        if (shown.OpenKind.Length == 0 || open == null) return "no open button";
        Close();
        try { open(shown.OpenKind, shown.OpenKey, shown.OpenName); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: mod pause notice open (" + shown.OpenKind + ") threw: " + e.Message);
            return "failed: " + e.Message;
        }

        Plugin.Logger?.LogInfo("Ui: mod pause notice: " + shown.OpenCaption + " for " + shown.OpenKey + (closedAlready ? " (the notice had closed)" : ""));
        return "opened " + shown.OpenKind + (closedAlready ? " (the notice had closed)" : "");
    }

    private static bool OnEscape()
    {
        if (!Owned.IsOpen) return false;
        Close();
        return true;
    }

    internal static void Close() => DropHeld(logClosed: true);

    private static void DismissHidden()
    {
        if (Owned.Held == null || Owned.IsOpen) return;
        DropHeld(logClosed: true);
    }

    private static void DropHeld(bool logClosed)
    {
        _escape?.Dispose();
        _escape = null;
        _shown = null;
        var panel = Owned.Take();
        if (panel == null) return;
        try { panel.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: mod pause notice dispose threw: " + e.Message); }
        if (logClosed) Plugin.Logger?.LogInfo("Ui: mod pause notice closed.");
    }
}
