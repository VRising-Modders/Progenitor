using System;
using System.Collections.Generic;

namespace Progenitor.Networking;

/// <summary>JOINNOTICE: the small in-game notice for a server change mid-session, as the readback and the panel see it.</summary>
internal sealed class JoinChangeNoticeState
{
    /// <summary><c>mods</c>, <c>message</c> or <c>both</c>.</summary>
    public string Kind { get; init; } = "";

    public string Text { get; init; } = "";

    /// <summary>The button that opens the page: <c>View mods</c>, <c>View message</c>, <c>View</c>.</summary>
    public string ViewCaption { get; init; } = "";

    /// <summary>The change it is about (the mods hash and the message hash): never shown twice for one change.</summary>
    public string Hash { get; init; } = "";
}

/// <summary>JOINNOTICE: the notice's words and kind, game-free. US English.</summary>
internal static class JoinChangeNoticePolicy
{
    public const string Title = "Server update";
    public const string ModsText = "This server's mods changed.";
    public const string MessageText = "The server message changed.";
    public const string BothText = "This server's mods and message changed.";
    public const string ViewMods = "View mods";
    public const string ViewMessage = "View message";
    public const string View = "View";
    public const string CloseButton = ServerModPauseText.CloseButton;

    /// <summary>How long the notice stays up if the player does not close it (the mod pause notice's 20 s).</summary>
    public const double NoticeSeconds = ServerModPauseText.NoticeSeconds;

    /// <summary>A player's own Players joining edit answered within this many seconds: the change is theirs, no notice.</summary>
    public const double OwnEditSeconds = 10;

    /// <summary>The notice for what changed, or null when nothing did.</summary>
    public static JoinChangeNoticeState? For(bool modsChanged, bool messageChanged, string hash)
    {
        if (!modsChanged && !messageChanged) return null;
        return new JoinChangeNoticeState
        {
            Kind = modsChanged && messageChanged ? "both" : modsChanged ? "mods" : "message",
            Text = modsChanged && messageChanged ? BothText : modsChanged ? ModsText : MessageText,
            ViewCaption = modsChanged && messageChanged ? View : modsChanged ? ViewMods : ViewMessage,
            Hash = hash ?? "",
        };
    }
}

/// <summary>
/// JOINNOTICE: when a re-signed catalog arrives mid-session (an admin's change resent to every joined client) and the
/// server's lists or message differ from what this session last saw, a small non-blocking notice says so and opens the
/// page with its New / Changed tags. Never during a join, a refusal, an install window, a batch or a rejoin (an open
/// page refreshes in place instead), once per change, and not for the admin whose own edit it was.
/// </summary>
internal static partial class JoinFlowClient
{
    // What this session last saw of the server's lists and message (the join's decision, then each change it showed or
    // refreshed in place). Empty until the join's first catalog decided.
    private static string _seenModsHash = "";
    private static string _seenMessageHash = "";
    private static bool _haveSeenHashes;
    private static string _lastChangeNoticeHash = "";
    private static JoinChangeNoticeState? _changeNotice;
    private static double _localAdminEditAt = double.NegativeInfinity;

    /// <summary>Raised (outside the lock) when the notice should show.</summary>
    internal static event Action<JoinChangeNoticeState>? ChangeNoticeRequested;

    /// <summary>Raised when the notice should close (pressed, a page opened, the client left).</summary>
    internal static event Action? ChangeNoticeDismissed;

    /// <summary>The notice on show, or null.</summary>
    internal static JoinChangeNoticeState? ChangeNotice
    {
        get { lock (Gate) return _changeNotice; }
    }

    /// <summary>This player's Players joining edit was accepted now (its catalog resend is their own change).</summary>
    internal static void NoteLocalAdminEdit()
    {
        lock (Gate) _localAdminEditAt = Clock();
    }

    private static string MessageHashOf(JoinSection? section)
    {
        string m = (section?.Message ?? "").Trim();
        return m.Length == 0 ? "" : JoinShowPolicy.MessageHash(section!.Message);
    }

    // Called with Gate held when this join's catalog first decided: the baseline.
    private static void NoteSeenLocked(JoinPageModel model)
    {
        _seenModsHash = JoinShowPolicy.ModsHash(JoinShowPolicy.ModsSnapshot(model));
        _seenMessageHash = MessageHashOf(_section);
        _haveSeenHashes = true;
    }

    // Called with Gate held from OnJoinAttempt and Uninstall.
    private static JoinChangeNoticeState? ForgetChangeNoticeLocked()
    {
        var had = _changeNotice;
        _changeNotice = null;
        _haveSeenHashes = false;
        _seenModsHash = _seenMessageHash = "";
        _lastChangeNoticeHash = "";
        _localAdminEditAt = double.NegativeInfinity;
        return had;
    }

    /// <summary>
    /// Called with Gate held for a catalog that arrived after this join decided: what changed against what this session
    /// saw, and the notice to show (or null). <paramref name="screensOpen"/>: the open page refreshes in place instead.
    /// </summary>
    private static JoinChangeNoticeState? MidSessionChangeLocked(bool screensOpen, out string why)
    {
        why = "";
        if (!_haveSeenHashes) return null;
        var model = JoinPageModel.Build(BuildFactsLocked());
        string mods = JoinShowPolicy.ModsHash(JoinShowPolicy.ModsSnapshot(model));
        string message = MessageHashOf(_section);
        bool modsChanged = !string.Equals(mods, _seenModsHash, StringComparison.Ordinal);
        // A removed message is nothing to view.
        bool messageChanged = message.Length > 0 && !string.Equals(message, _seenMessageHash, StringComparison.Ordinal);
        _seenModsHash = mods;
        _seenMessageHash = message;
        if (!modsChanged && !messageChanged) { why = "nothing this session saw changed"; return null; }
        if (screensOpen) { why = "the open screens refreshed in place"; return null; }
        if (FlowBusyLocked()) { why = "a join, install or rejoin is under way"; return null; }
        if (!messageChanged && Clock() - _localAdminEditAt <= JoinChangeNoticePolicy.OwnEditSeconds) { why = "your own Players joining edit"; return null; }
        var notice = JoinChangeNoticePolicy.For(modsChanged, messageChanged, mods + "|" + message);
        if (notice == null || string.Equals(notice.Hash, _lastChangeNoticeHash, StringComparison.Ordinal)) { why = "already shown"; return null; }
        _lastChangeNoticeHash = notice.Hash;
        _changeNotice = notice;
        return notice;
    }

    // Called with Gate held: something of a join flow runs, so no notice (its own page answers).
    private static bool FlowBusyLocked() =>
        _refused || _window || (_batch != null && !_batch.Done) || _reconnect != null || _inWorldDueAt >= 0
        || (_session != null && !_session.Closed);

    private static void RaiseChangeNotice(JoinChangeNoticeState notice)
    {
        Plugin.Logger?.LogInfo("JoinFlow: notice: " + notice.Text + " (" + notice.Kind + ")");
        try { ChangeNoticeRequested?.Invoke(notice); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: the change notice threw: " + e.Message); }
    }

    private static void DismissChangeNotice()
    {
        try { ChangeNoticeDismissed?.Invoke(); }
        catch { /* the panel closes itself after its time */ }
    }

    /// <summary>The notice closed (OK, its time ran out): nothing is opened.</summary>
    internal static string CloseChangeNotice()
    {
        JoinChangeNoticeState? had;
        lock (Gate)
        {
            had = _changeNotice;
            _changeNotice = null;
        }

        DismissChangeNotice();
        return had == null ? "no notice" : "closed";
    }

    /// <summary>The notice's View: the mods page (New / Changed tags), or the message (then the mods page when due).</summary>
    internal static string ViewChangeNotice()
    {
        JoinChangeNoticeState? had;
        lock (Gate)
        {
            had = _changeNotice;
            _changeNotice = null;
        }

        DismissChangeNotice();
        if (had == null) return "no notice";
        bool shown = had.Kind == "mods" ? ShowModsNow() : ShowNow();
        Plugin.Logger?.LogInfo("JoinFlow: notice: View (" + had.Kind + ") -> " + (shown ? "opened" : "nothing to show"));
        return shown ? (had.Kind == "mods" ? "mods" : "message") : "no catalog or handshake facts this join";
    }

    internal static Dictionary<string, object?> DescribeChangeNotice()
    {
        lock (Gate)
        {
            var n = _changeNotice;
            return new Dictionary<string, object?>
            {
                ["shown"] = n != null,
                ["kind"] = n?.Kind ?? "",
                ["text"] = n?.Text ?? "",
                ["view"] = n?.ViewCaption ?? "",
                ["hash"] = n?.Hash ?? _lastChangeNoticeHash,
            };
        }
    }
}
