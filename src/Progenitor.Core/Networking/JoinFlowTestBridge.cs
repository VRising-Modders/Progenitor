using System;
using System.Collections.Generic;

namespace Progenitor.Networking;

/// <summary>
/// JOINFLOW1: the Core half of the dev bridge's <c>join_ui</c> op (client, main thread). Dev reaches this type
/// by reflection (Core grants Dev no <c>InternalsVisibleTo</c>); no plugin or network path reaches it. Every
/// press goes through the same handler the on-screen button calls (<see cref="JoinFlowPanel"/>), so a bridge
/// press is the button's own path, not a stand-in.
/// </summary>
internal static class JoinFlowTestBridge
{
    /// <summary>The screens as the session holds them, plus what is drawn.</summary>
    public static Dictionary<string, object?> Read()
    {
        var d = JoinFlowClient.Describe(DateTime.UtcNow, TimeZoneInfo.Local);
        d["ok"] = true;
        d["drawn"] = JoinFlowPanel.DescribeDrawn();
        d["playerZone"] = ResetCountdown.ZoneLabel(TimeZoneInfo.Local, DateTime.UtcNow);
        return d;
    }

    /// <summary>Press a screen button: continue, leave, back, close, install, dont-download, download.</summary>
    public static Dictionary<string, object?> Press(string button) => After(JoinFlowPanel.Press(button));

    /// <summary>A required row's action button, by mod id.</summary>
    public static Dictionary<string, object?> Row(string id) => After(JoinFlowPanel.RowAction(id));

    /// <summary>Toggle a recommended row's checkbox, by id (or package when it has no id).</summary>
    public static Dictionary<string, object?> Recommend(string key) => After(JoinFlowPanel.ToggleRecommended(key));

    /// <summary>Set the message page's "Don't show again until it changes" box.</summary>
    public static Dictionary<string, object?> DontShow(bool on) => After(JoinFlowPanel.SetDontShowAgain(on));

    /// <summary>Escape on the screens: Back where there is one, else Close.</summary>
    public static Dictionary<string, object?> Escape() => After(JoinFlowPanel.Escape());

    /// <summary>Open the screens now for this join (ignores "don't show again"; the in-world message as it would open).</summary>
    public static Dictionary<string, object?> Show() =>
        After(JoinFlowClient.ShowNow() ? "shown" : "no catalog or handshake facts this join");

    /// <summary>Progenitor's page, View mods: the joined server's mods page whatever its decision.</summary>
    public static Dictionary<string, object?> ShowMods() =>
        After(JoinFlowClient.ShowModsNow() ? "mods" : "no catalog or handshake facts this join");

    /// <summary>The admin's Preview.</summary>
    public static Dictionary<string, object?> Preview()
    {
        JoinFlowPanel.OpenPreview();
        return After("preview");
    }

    /// <summary>
    /// Forget what a server's screens showed (the message, "don't show again", the mods lists behind the Changed tags),
    /// so its next join is a first join. <paramref name="host"/> (an address such as <c>127.0.0.1:9976</c>): every
    /// remembered server at that address. Empty while joined: the joined server. Empty with no server joined (the title
    /// screen; 2026.10.128 this was a no-op and the next join still said the mod list changed): every remembered server.
    /// </summary>
    public static Dictionary<string, object?> ForgetSeen(string? host)
    {
        string path = JoinFlowClient.SeenPath;
        string h = (host ?? "").Trim();
        string key = JoinFlowClient.ServerKey;
        if (h.Length == 0 && key.Length > 0)
            return After(JoinSeenStore.Forget(path, key) ? "forgotten " + key : "nothing remembered for " + key);

        int removed = JoinSeenStore.ForgetHost(path, h);
        string what = h.Length > 0 ? "at " + h : "(no server joined: every server)";
        if (removed < 0) return After("the seen file could not be read or written; nothing forgotten " + what);
        return After(removed == 0
            ? "nothing remembered " + what
            : "forgotten " + removed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + (removed == 1 ? " server " : " servers ") + what);
    }

    /// <summary>
    /// Clear the stored Thunderstore confirmations (<c>thunderstoreAcks</c>) for operator <paramref name="fingerprint"/>
    /// (this join's when empty), so the next batch from this server asks again. The operator's pin stays. (The
    /// server-download <c>riskAck</c> it also cleared is gone with server distribution, 2026-10-05.)
    /// </summary>
    public static Dictionary<string, object?> ForgetTrust(string? fingerprint)
    {
        string fp = (fingerprint ?? "").Trim();
        if (fp.Length == 0) fp = (Read()["fingerprint"] as string) ?? "";
        bool ts = fp.Length > 0 && ModManifest.ForgetThunderstoreAcks(fp);
        return After(ts ? "Thunderstore confirmations cleared for " + fp : "no Thunderstore confirmation stored");
    }

    // ---- JOINFLOW2 ---------------------------------------------------------------------------

    /// <summary>"Install N mods" (every Thunderstore row and checked recommended row, behind one confirmation).</summary>
    public static Dictionary<string, object?> Install() => After(JoinFlowPanel.Press(JoinFlowLayout.ButtonInstall));

    /// <summary>The Thunderstore confirmation's Install.</summary>
    public static Dictionary<string, object?> TsInstall() => After(JoinFlowPanel.Press(JoinFlowLayout.ButtonTsInstall));

    /// <summary>Join now on the Result page.</summary>
    public static Dictionary<string, object?> JoinNow() => After(JoinFlowPanel.Press(JoinFlowLayout.ButtonJoinNow));

    /// <summary>Restart and rejoin; <paramref name="dryRun"/> writes the record and reports the relaunch command without quitting.</summary>
    public static Dictionary<string, object?> Restart(bool dryRun) => After(JoinFlowPanel.Restart(dryRun));

    /// <summary>Later on the Result page.</summary>
    public static Dictionary<string, object?> Later() => After(JoinFlowPanel.Press(JoinFlowLayout.ButtonLater));

    /// <summary>Cancel (or Close/Continue once it ended) on the Rejoin page.</summary>
    public static Dictionary<string, object?> Cancel() => After(JoinFlowPanel.Press(JoinFlowLayout.ButtonCancel));

    /// <summary>The rejoin record on disk (<c>progenitor-rejoin.json</c>), unchanged.</summary>
    public static Dictionary<string, object?> RejoinRead()
    {
        var record = RejoinStore.Peek(JoinFlowClient.RejoinPath);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["path"] = JoinFlowClient.RejoinPath,
            ["exists"] = System.IO.File.Exists(JoinFlowClient.RejoinPath),
            ["record"] = JoinFlowClient.DescribeRecord(record),
            ["raw"] = SafeRaw(JoinFlowClient.RejoinPath),
        };
    }

    /// <summary>Delete the rejoin record.</summary>
    public static Dictionary<string, object?> RejoinClear()
    {
        bool deleted = RejoinStore.Delete(JoinFlowClient.RejoinPath);
        var d = RejoinRead();
        d["result"] = deleted ? "deleted" : "no record";
        return d;
    }

    /// <summary>
    /// The after-relaunch pickup without a restart, as if the game had just started at the main menu: take the record
    /// (a dry run's too) and rejoin, or say why it was ignored (<c>expired</c>, <c>already-used</c>, <c>invalid</c>, <c>none</c>).
    /// </summary>
    public static Dictionary<string, object?> RejoinNow() => After("pickup: " + JoinFlowClient.PickUp("bridge"));

    // ---- JOINTURNON ----------------------------------------------------------------------------------

    /// <summary>
    /// Press the Mods list's switch for <paramref name="id"/> without the UI (the same path and the same enable state as
    /// the switch on the mod's page), then the readback. <c>result</c> is <c>mod-switch ok</c> or <c>mod-switch refused</c>;
    /// <c>switch</c> holds what the switch said and the off copy the screens now see.
    /// </summary>
    public static Dictionary<string, object?> ModSwitch(string id, bool on)
    {
        var sw = JoinFlowClient.BridgeSwitch(id, on);
        var d = After("mod-switch " + ((sw["ok"] as bool?) == true ? "ok" : "refused"));
        d["switch"] = sw;
        return d;
    }

    // ---- CLIENTPOLICY2 -------------------------------------------------------------------------------

    /// <summary>
    /// A "Not allowed on this server" row's action, by its id (the readback's <c>notAllowed[].id</c>): Pause for this
    /// server, or Turn off and restart (the on-screen button's handler). <c>result</c> is <c>pause</c>,
    /// <c>turn-off-restart</c>, <c>done</c> (nothing left to do) or why nothing happened.
    /// </summary>
    public static Dictionary<string, object?> NotAllowed(string id) => After(JoinFlowPanel.NotAllowedAction(id));

    /// <summary>
    /// The Turn off and restart confirmation's buttons (<paramref name="yes"/>: Turn off and restart; else Cancel), through
    /// the dialog's own click path. <c>result</c> is <c>confirmed</c>, <c>canceled</c> or <c>no turn-off confirmation is open</c>.
    /// </summary>
    public static Dictionary<string, object?> NotAllowedConfirm(bool yes)
    {
        var open = Progenitor.Ui.UiDialog.Current;
        if (open == null || !open.IsOpen || open.Kind != "turn-off-restart") return After("no turn-off confirmation is open");
        Progenitor.Ui.UiDialog.ChooseForBridge(yes ? "yes" : "no");
        return After(yes ? "confirmed" : "canceled");
    }

    /// <summary>
    /// The client mod policy as this client holds it for the joined (or last) server: the policy remembered in
    /// <c>progenitor-server-profiles.json</c>, what the automatic pause would take now, and the last automatic pause.
    /// </summary>
    public static Dictionary<string, object?> Policy()
    {
        var d = ServerModPause.DescribePolicyPause();
        d["ok"] = true;
        d["report"] = ClientModReportClient.Describe(withEntries: false);
        return d;
    }

    // ---- JOINNOTICE ----------------------------------------------------------------------------------

    /// <summary>The server-change notice's View (the mods page, or the message): its button's own handler.</summary>
    public static Dictionary<string, object?> ChangeNoticeView() => After(JoinFlowClient.ViewChangeNotice());

    /// <summary>The server-change notice's OK.</summary>
    public static Dictionary<string, object?> ChangeNoticeClose() => After(JoinFlowClient.CloseChangeNotice());

    // ---- JOINFLOW4: the version store -------------------------------------------------------------

    /// <summary>The version store: every mod's copies on disk, kept copies (hash-checked), the journal, the last join and restore.</summary>
    public static Dictionary<string, object?> Versions() => ModVersionJoin.BridgeDescribe();

    /// <summary>
    /// A version-store action: <c>park</c> (id; restart: keep it loaded and mark it), <c>swap</c> (id, version; restart;
    /// forServer: empty is the player's choice, <c>current</c> the joined server), <c>restore</c> (id[, version]),
    /// <c>restore_all</c>, <c>forget</c> (id, version; force for the player's own), <c>prefer</c> (id, version),
    /// <c>reconcile</c>, <c>evict</c>, <c>plan</c> (the swaps the last server request would need).
    /// </summary>
    public static Dictionary<string, object?> VersionsRun(string action, string id, string version, bool restart, bool force, string forServer) =>
        ModVersionJoin.BridgeRun(action ?? "", id ?? "", version ?? "", restart, force, forServer ?? "");

    // ---- JOINLINKS1: links in the server message ---------------------------------------------------

    /// <summary>Click the message's link <paramref name="index"/> (the readback's <c>drawn.links[].index</c>): the confirmation opens.</summary>
    public static Dictionary<string, object?> LinkClick(int index) => After(JoinFlowPanel.OpenLink(index));

    /// <summary>A link confirmation button: <c>copy</c>, <c>cancel</c> or <c>open</c> (the on-screen button's handler).</summary>
    public static Dictionary<string, object?> LinkButton(string button) => After(JoinFlowPanel.LinkButton(button));

    /// <summary>
    /// Open in browser's test switch: <paramref name="on"/> records the URL (<c>drawn.linkOpener.lastUrl</c>) instead of
    /// launching a browser. Off by default; it stays as set until this Core generation unloads.
    /// </summary>
    public static Dictionary<string, object?> LinkDryRun(bool on)
    {
        JoinLinkOpener.DryRun = on;
        return After(on ? "link dry run on" : "link dry run off");
    }

    private static string SafeRaw(string path)
    {
        try { return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Render a message with explicit reset settings at an explicit time, game-free, without changing the server's
    /// settings. <paramref name="nowUtc"/> empty is now. RESETWIPE1: the countdown's precision is automatic and the
    /// passed text is <c>now</c>; <paramref name="wipeEvery"/> and <paramref name="wipeDay"/> with
    /// <paramref name="startDate"/> (<c>yyyy-MM-dd</c>) stand for the game's scheduled wipes (<paramref name="wipeEvery"/>
    /// 0: off; -1: the joined server's schedule as this client knows it). <paramref name="repeat"/> and
    /// <paramref name="every"/> are the cfg-only fallback, used only while wipes are off.
    /// </summary>
    public static Dictionary<string, object?> Render(
        string message, string nextUtc, string repeat, int every, int wipeEvery, string wipeDay, string startDate, string nowUtc)
    {
        var serverZone = JoinFlowClient.ServerZone ?? TimeZoneInfo.Local;
        GameWipeSchedule? wipe = wipeEvery < 0
            ? JoinServerSettings.GameWipe()
            : GameWipeSchedule.From(ServerRules.ParseStartDate(startDate), wipeEvery, wipeDay);
        var section = JoinServerSettings.BuildSection(message, nextUtc, repeat, every, null, null, null,
            serverZone, DateTime.UtcNow, wipe: wipe);
        DateTime now = ResetCountdown.TryParseUtc(nowUtc, out var pinned) ? pinned : DateTime.UtcNow;
        string rendered = ResetCountdown.Render(section.Message, section.Reset, now, serverZone, TimeZoneInfo.Local, out bool live);
        bool set = ResetCountdown.TryNext(section.Reset, now, serverZone, out var next, out bool passed);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["now"] = ResetCountdown.FormatUtc(now),
            ["rendered"] = rendered,
            ["liveCountdown"] = live,
            ["resetSet"] = section.Reset is { IsSet: true },
            ["fromWipeSchedule"] = wipe is { On: true },
            ["wipe"] = wipe == null ? null : new Dictionary<string, object?>
            {
                ["every"] = wipe.IntervalDays, ["day"] = wipe.DayOfReset,
                ["nextDate"] = wipe.NextDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["row"] = wipe.On ? wipe.RowText() : null,
            },
            ["effectiveNextUtc"] = set ? ResetCountdown.FormatUtc(next) : "",
            ["passed"] = passed,
            ["precision"] = set && !passed ? ResetCountdown.AutoPrecision(next - now).ToString() : "",
        };
    }

    private static Dictionary<string, object?> After(string result)
    {
        var d = Read();
        d["result"] = result;
        return d;
    }
}
