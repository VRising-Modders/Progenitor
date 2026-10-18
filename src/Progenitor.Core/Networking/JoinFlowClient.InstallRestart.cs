using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Progenitor.Networking;

/// <summary>
/// INSTALLRESTART1 (docs/mods-menu.md, "Restart for pending installs"): the Mods window's pending-restart bar. One press
/// asks once, writes what the restart waits on (and, in a world, where to return: the JOINFLOW2 rejoin record, a server or
/// a hosted save; never a password), leaves the world the game's own way, starts JOINFLOW2's relauncher and quits. The
/// next start promotes the pending files as always, then rejoins the server or resumes the private game through the title
/// screen's own Continue, and one line says what happened: <c>Installed: ChatSkin. Rejoined Night Garden.</c>
/// </summary>
internal static partial class JoinFlowClient
{
    /// <summary>The client changes waiting for a restart (live: the Install tab's pending rows, else the client ledger).</summary>
    internal static Func<IReadOnlyList<PendingClientChange>>? PendingRestartProbe { get; set; }

    /// <summary>The hosted save's name as the title screen's Continue shows it (live: the hosted server's name). Null: the join's server name.</summary>
    internal static Func<string>? HostedSaveProbe { get; set; }

    /// <summary>Whether this start runs a recorded change's plugin (by id, else by name; live: the Mods inventory). Null: by id only.</summary>
    internal static Func<PendingClientChange, bool>? RunsNowProbe { get; set; }

    /// <summary>Builds the confirmation's panel (live: <c>Ui.Panel</c>). Null: no dialog (the bridge answers it).</summary>
    internal static Func<string, Progenitor.Ui.IUiPanel>? RestartDialogFactory { get; set; }

    /// <summary>Raised when the bar's facts may have changed (a quit started, a pickup): the Mods window redraws it.</summary>
    internal static event Action? RestartChanged;

    /// <summary><c>BepInEx/config/progenitor-install-restart.json</c>.</summary>
    internal static string InstallRestartPath => Path.Combine(ModManifest.ConfigDirectory, InstallRestartRecord.FileName);

    private sealed class PendingQuit
    {
        public double Since { get; init; }
        public RelaunchPlan? Plan { get; init; }
        public bool Manual { get; init; }
    }

    /// <summary>How long Restart waits for the game's Leave before it quits anyway.</summary>
    internal const double RestartLeaveTimeoutSeconds = 30;

    private static PendingQuit? _quit;
    private static JoinResume? _resume;
    private static string? _afterRestartLine;
    private static string _lastAfterRestart = "";
    private static string _lastInstallPickup = "";
    private static Dictionary<string, object?>? _lastInstallRestart;
    private static Progenitor.Ui.UiDialog? _restartConfirm;
    private static ClientRestartOffer? _confirmOffer;
    private static RelaunchPlan? _planCache;
    private static double _planAt = -100;

    private static void ResetInstallRestart()
    {
        try { _restartConfirm?.Close(); }
        catch { /* teardown */ }
        _restartConfirm = null;
        _confirmOffer = null;
        _quit = null;
        _resume = null;
        _afterRestartLine = null;
        _lastAfterRestart = "";
        _lastInstallPickup = "";
        _lastInstallRestart = null;
        _planCache = null;
        _planAt = -100;
        PendingRestartProbe = null;
        HostedSaveProbe = null;
        RunsNowProbe = null;
        JoinConnectingProbe = null;
        RestartDialogFactory = null;
        RestartChanged = null;
    }

    private static void RaiseRestartChanged()
    {
        try { RestartChanged?.Invoke(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Install restart: a listener threw: " + e.Message); }
    }

    // ---- the bar ------------------------------------------------------------------------------

    /// <summary>The relaunch plan, probed at most every 2 s (registry reads and this process's start time).</summary>
    private static RelaunchPlan? PlanNow()
    {
        var relauncher = Relauncher;
        if (relauncher == null) return null;
        double now = Clock();
        if (_planCache != null && now - _planAt < 2 && now >= _planAt) return _planCache;
        try { _planCache = relauncher.Plan(); }
        catch (Exception e) { _planCache = new RelaunchPlan { Mode = RelaunchMode.Manual, Why = e.Message }; }
        _planAt = now;
        return _planCache;
    }

    /// <summary>What the pending-restart bar offers now (<see cref="ClientRestartOffer.None"/> when nothing waits).</summary>
    internal static ClientRestartOffer CurrentRestartOffer()
    {
        IReadOnlyList<PendingClientChange> pending;
        try { pending = PendingRestartProbe?.Invoke() ?? Array.Empty<PendingClientChange>(); }
        catch { pending = Array.Empty<PendingClientChange>(); }
        if (pending.Count == 0) return ClientRestartOffer.None;

        bool inWorld;
        try { inWorld = ConnectHost?.WorldExists == true; }
        catch { inWorld = false; }
        JoinTarget? target = null;
        string save = "";
        if (inWorld)
        {
            lock (Gate)
            {
                target = CurrentTargetLocked();
                // The hosted server's name is its save's HostSettings.Name, the Continue caption; a stock name still counts here.
                save = ServerNameLocked() ?? "";
                if (save.Length == 0) save = (_serverName ?? "").Trim();
            }

            try
            {
                var probe = HostedSaveProbe;
                if (probe != null) save = probe() ?? "";
            }
            catch { /* the join's name stands */ }
        }

        return ClientRestartOffer.Decide(new ClientRestartFacts
        {
            Pending = pending,
            InWorld = inWorld,
            Target = target,
            HostedSave = save,
            Plan = PlanNow(),
        });
    }

    /// <summary>
    /// The client is between a connect and the server's answer (live: a held connection whose handshake has not answered
    /// <c>ok</c>, on a server with Progenitor and outside an install window). Null: only this flow's own reconnect counts.
    /// </summary>
    internal static Func<bool>? JoinConnectingProbe { get; set; }

    /// <summary>What <see cref="PressRestartButton"/> and <see cref="AnswerRestart"/> answer while a join is in progress.</summary>
    internal const string RestartRefusedJoining = "a join is in progress";

    /// <summary>
    /// A join is in progress: this flow's reconnect (Join now, the after-restart rejoin) has not ended, or the client is
    /// connecting (<see cref="JoinConnectingProbe"/>). The restart is never started then: before the client world exists
    /// there is nothing to leave, and quitting mid-join would drop the connect halfway.
    /// </summary>
    internal static bool JoinBlocksRestart
    {
        get
        {
            JoinReconnect? r;
            lock (Gate) r = _reconnect;
            if (r != null && !r.Finished) return true;
            try { return JoinConnectingProbe?.Invoke() == true; }
            catch { return false; }
        }
    }

    /// <summary>The bar's button: open the confirmation (the dialog kit). Returns <c>confirm</c>, or why nothing opened.</summary>
    internal static string PressRestartButton()
    {
        if (Interlocked.CompareExchange(ref _relaunching, 0, 0) != 0) return "already restarting";
        if (JoinBlocksRestart)
        {
            Plugin.Logger?.LogInfo("Install restart: not asked; " + RestartRefusedJoining);
            return RestartRefusedJoining;
        }

        var offer = CurrentRestartOffer();
        if (!offer.Shown) return "nothing waits for a restart";
        _confirmOffer = offer;
        var factory = RestartDialogFactory;
        if (factory == null) return "confirm";
        try
        {
            try { _restartConfirm?.Close(); }
            catch { /* replaced */ }
            var dialog = Progenitor.Ui.UiDialog.Open(
                factory, "install-restart", "confirm", "restart",
                offer.ConfirmTitle, Progenitor.Ui.UiRichText.Literal(offer.ConfirmBody),
                offer.ConfirmYes, offer.Manual ? Progenitor.Ui.LabelTone.Orange : Progenitor.Ui.LabelTone.Normal, offer.ConfirmNo);
            _restartConfirm = dialog;
            dialog.OnYes(() => AnswerRestart(yes: true, dryRun: false)).OnNo(() => AnswerRestart(yes: false, dryRun: false));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Install restart: the confirmation could not open: " + e.Message);
            return "the confirmation could not open: " + e.Message;
        }

        Plugin.Logger?.LogInfo("Install restart: asked \"" + offer.ConfirmTitle + "\" (" + ClientRestartText.KindName(offer.Kind) + ")");
        return "confirm";
    }

    /// <summary>The confirmation's answer: <c>Not now</c> closes it; <c>Restart</c> / <c>Quit to desktop</c> runs the restart.</summary>
    internal static string AnswerRestart(bool yes, bool dryRun)
    {
        var offer = _confirmOffer;
        _confirmOffer = null;
        var dialog = _restartConfirm;
        _restartConfirm = null;
        if (dryRun || !yes)
        {
            try { dialog?.Close(); }
            catch { /* answered */ }
        }

        if (!yes)
        {
            Plugin.Logger?.LogInfo("Install restart: Not now");
            return "not now";
        }

        // A join that started while the confirmation was open: nothing restarts (the bar asks again afterwards).
        if (!dryRun && JoinBlocksRestart)
        {
            try { dialog?.Close(); }
            catch { /* answered */ }
            Plugin.Logger?.LogInfo("Install restart: not started; " + RestartRefusedJoining);
            return RestartRefusedJoining;
        }

        return RunRestart(offer ?? CurrentRestartOffer(), dryRun);
    }

    /// <summary>
    /// The restart itself: the records, then (unless a dry run) the game's Leave when in a world and, once out, the
    /// relauncher and the quit. Returns <c>dry-run</c>, <c>leaving</c>, <c>quitting</c>, <c>quitting-manual</c>, or why not.
    /// </summary>
    internal static string RunRestart(ClientRestartOffer offer, bool dryRun)
    {
        if (offer == null || !offer.Shown) return "nothing waits for a restart";
        if (!dryRun && Interlocked.CompareExchange(ref _relaunching, 1, 0) != 0) return "already restarting";
        var now = DateTime.UtcNow;
        string rejoinPath = "";
        try
        {
            InstallRestartRecord.Write(InstallRestartPath, InstallRestartRecord.From(offer, now, dryRun));
            if (offer.ReturnTo != ClientRestartOffer.ReturnNone)
            {
                var record = offer.ReturnTo == ClientRestartOffer.ReturnHosted
                    ? new RejoinRecord
                    {
                        Kind = RejoinRecord.KindHosted,
                        Save = offer.ReturnName,
                        ServerName = offer.ReturnName,
                        Created = ModManifestJson.FormatIssued(now),
                    }
                    : RejoinRecord.From(offer.Target!, Array.Empty<RejoinRow>(), now);
                record.DryRun = dryRun;
                RejoinStore.Write(RejoinPath, record);
                rejoinPath = RejoinPath;
            }
        }
        catch (Exception e)
        {
            if (!dryRun) Interlocked.Exchange(ref _relaunching, 0);
            Plugin.Logger?.LogWarning("Install restart: the restart records could not be written: " + e.Message);
            return "the restart records could not be written: " + e.Message;
        }

        var plan = offer.Manual ? null : PlanNow();
        bool inWorld;
        try { inWorld = ConnectHost?.WorldExists == true; }
        catch { inWorld = false; }
        _lastInstallRestart = new Dictionary<string, object?>
        {
            ["dryRun"] = dryRun,
            ["offer"] = offer.Describe(),
            ["mode"] = offer.Manual ? "manual" : "automatic",
            ["why"] = offer.ManualWhy,
            ["fileName"] = plan?.FileName ?? "",
            ["arguments"] = plan?.Arguments ?? "",
            ["leaveFirst"] = inWorld,
            ["recordPath"] = InstallRestartPath,
            ["rejoinRecordPath"] = rejoinPath,
            ["at"] = ModManifestJson.FormatIssued(now),
        };
        Plugin.Logger?.LogInfo("Install restart" + (dryRun ? " (dry run)" : "") + ": " + ClientRestartText.KindName(offer.Kind) + ", "
            + offer.Count + " change(s); return " + offer.ReturnTo + (offer.ReturnName.Length > 0 ? " " + offer.ReturnName : "")
            + "; relaunch " + (offer.Manual ? "manual (" + offer.ManualWhy + ")" : "automatic") + (inWorld ? "; leaving the world first" : ""));
        if (dryRun) return "dry-run";

        if (inWorld && ConnectHost != null)
        {
            // Leave cleanly: the game's own Leave (the server saves the character), then quit once the world is gone.
            try { ConnectHost.RequestLeave(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Install restart: Leave threw: " + e.Message); }
            _quit = new PendingQuit { Since = Clock(), Plan = plan, Manual = offer.Manual };
            RaiseRestartChanged();
            return "leaving";
        }

        return StartAndQuit(plan, offer.Manual);
    }

    private static string StartAndQuit(RelaunchPlan? plan, bool manual)
    {
        var relauncher = Relauncher;
        bool started = false;
        string error = "";
        if (!manual && plan != null && relauncher != null)
        {
            try { started = relauncher.Start(plan, out error); }
            catch (Exception e) { error = e.Message; }
            if (!started) Plugin.Logger?.LogWarning("Install restart: the relauncher did not start (" + error + "); quitting for a manual start");
        }

        if (relauncher == null)
        {
            Plugin.Logger?.LogWarning("Install restart: no relauncher here; nothing quits");
            Interlocked.Exchange(ref _relaunching, 0);
            return "restarting is not available here";
        }

        relauncher.Quit(0);
        return started ? "quitting" : "quitting-manual";
    }

    private static void TickInstallRestart(double now)
    {
        var q = _quit;
        if (q == null) return;
        bool world;
        try { world = ConnectHost?.WorldExists == true; }
        catch { world = false; }
        if (world && now - q.Since < RestartLeaveTimeoutSeconds) return;
        _quit = null;
        if (world) Plugin.Logger?.LogWarning("Install restart: the world was still up after " + RestartLeaveTimeoutSeconds + " s; quitting anyway");
        StartAndQuit(q.Plan, q.Manual);
    }

    // ---- after the restart ----------------------------------------------------------------------

    /// <summary>
    /// The install-restart record at this start (<paramref name="source"/> <c>start</c> or <c>bridge</c>): the after-restart
    /// line, shown at once when nothing is rejoined, else when the rejoin or resume ends. A dry run's record waits for the bridge.
    /// </summary>
    internal static string PickUpInstallRestart(string source)
    {
        var record = InstallRestartRecord.Read(InstallRestartPath);
        if (record == null)
        {
            _lastInstallPickup = "none";
            return _lastInstallPickup;
        }

        if (record.DryRun && source == "start")
        {
            _lastInstallPickup = "dry-run-waiting";
            return _lastInstallPickup;
        }

        RejoinStore.Delete(InstallRestartPath);
        if (!record.Fresh(DateTime.UtcNow))
        {
            _lastInstallPickup = "expired";
            return _lastInstallPickup;
        }

        var (installed, removed, notApplied) = record.Outcome(RunsNow);
        string line = ClientRestartText.AfterLine(installed, removed, notApplied, "");
        _lastAfterRestart = line;
        Plugin.Logger?.LogInfo("Install restart: after the restart at " + source + ": " + line);
        if (record.ReturnTo == ClientRestartOffer.ReturnNone)
        {
            ShowAfterRestartNotice(line);
            _lastInstallPickup = "shown";
        }
        else
        {
            _afterRestartLine = line;
            _lastInstallPickup = "with-" + record.ReturnTo;
        }

        RaiseRestartChanged();
        return _lastInstallPickup;
    }

    private static bool RunsNow(PendingClientChange change)
    {
        if (change.Id.Length > 0 && LoadedVersion(change.Id) != null) return true;
        var probe = RunsNowProbe;
        if (probe == null) return false;
        try { return probe(change); }
        catch { return false; }
    }

    /// <summary>The after-restart line with what the return did (<c>Rejoined …</c>), once; null when there is none.</summary>
    private static string? TakeAfterRestartLine(string returned)
    {
        var line = _afterRestartLine;
        if (line == null) return null;
        _afterRestartLine = null;
        string full = (line.Length > 0 ? line + " " : "") + returned;
        _lastAfterRestart = full;
        return full;
    }

    private static void ShowAfterRestartNotice(string line)
    {
        var factory = RestartDialogFactory;
        if (factory == null || line.Length == 0) return;
        try
        {
            var content = new Progenitor.Ui.DialogContent
            {
                Shape = Progenitor.Ui.UiDialogShape.Notice,
                Title = ClientRestartText.RestartFinishedTitle,
                Body = Progenitor.Ui.UiRichText.Literal(line),
                MainCaption = "OK",
                MainTone = Progenitor.Ui.CellTone.Gold,
            };
            Progenitor.Ui.UiDialog.OpenContent(factory, content, "install-restart", "after", "after");
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Install restart: the after-restart notice could not open: " + e.Message); }
    }

    // ---- Restart and resume ---------------------------------------------------------------------

    /// <summary>Open the Resuming page and press the title screen's Continue once it names the recorded save.</summary>
    private static void OpenResume(RejoinRecord record)
    {
        var host = ConnectHost;
        JoinFlowSession opened;
        lock (Gate)
        {
            var view = new JoinRejoinView
            {
                ServerName = record.Save,
                Endpoint = "",
                AfterRestart = true,
                Resume = true,
                Phase = "waiting",
                Status = ClientRestartText.ResumeStarting,
                Progress = 5,
            };
            var model = JoinPageModel.Build(new JoinFacts { ServerName = record.Save, Host = "" });
            opened = new JoinFlowSession(JoinMode.Rejoin, model, LiveEffects) { RejoinView = view };
            _session = opened;
            _rejoinRecord = record;
            _reconnect = null;
            _resume = null;
            if (host == null)
            {
                view.Phase = "failed";
                view.Status = "Resuming is not available here. Continue your private game from the title screen.";
            }
            else _resume = new JoinResume(record.Save, host, Clock());
        }

        if (host == null) RejoinStore.Delete(RejoinPath);
        Plugin.Logger?.LogInfo("JoinFlow: resuming the private game " + record.Save + " through the title screen's Continue");
        RaiseChanged();
        bool drawn;
        try { drawn = ScreensDrawn(); }
        catch { drawn = false; }
        if (drawn) opened.Opened();
    }

    private static void TickResume(double now)
    {
        JoinResume? r;
        JoinFlowSession? s;
        lock (Gate)
        {
            r = _resume;
            s = _session;
        }

        if (r == null) return;
        string before = r.Phase;
        r.Tick(now);
        var view = s != null && s.Mode == JoinMode.Rejoin && s.RejoinView?.Resume == true ? s.RejoinView : null;
        bool changed = false;
        if (view != null)
        {
            int progress = r.Progress(now);
            if (view.Phase != r.Phase || Math.Abs(view.Progress - progress) >= 10) changed = true;
            view.Phase = r.Phase;
            view.Progress = progress;
        }

        if (before == "waiting" && r.Phase == "starting")
            Plugin.Logger?.LogInfo("JoinFlow: resume: pressed the title screen's Continue (" + r.ContinueSeen + ")");
        if (!r.Finished)
        {
            if (changed) RaiseChanged();
            return;
        }

        JoinFlowSession? closeJoined = null;
        lock (Gate)
        {
            if (ReferenceEquals(_resume, r)) _resume = null;
            _rejoinRecord = null;
            if (view != null)
                view.Status = r.Phase == "joined"
                    ? TakeAfterRestartLine(ClientRestartText.Resumed(r.Save)) ?? ClientRestartText.Resumed(r.Save)
                    : r.Phase == "canceled" ? "Canceled." : "Could not resume: " + r.Error;
            // Decided 2026-10-06: a resumed private game has nothing to decide; the Resuming page closes by itself.
            if (r.Phase == "joined" && !JoinShowPolicy.JoinedPageNeeded(null) && _session is { Closed: false, Mode: JoinMode.Rejoin } open
                && ReferenceEquals(open.RejoinView, view))
                closeJoined = open;
        }

        RejoinStore.Delete(RejoinPath);
        Plugin.Logger?.LogInfo("JoinFlow: resume " + r.Phase + (r.Error.Length > 0 ? " (" + r.Error + ")" : "")
            + (closeJoined != null ? "; " + (view?.Status ?? "") + " Nothing to decide; the Resuming page closes" : ""));
        if (closeJoined != null)
        {
            try { closeJoined.Close(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: closing the Resuming page threw: " + e.Message); }
        }

        RaiseChanged();
    }

    // ---- readback --------------------------------------------------------------------------------

    /// <summary>The bridge's <c>pendingRestart</c> and the last press, the records and the resume.</summary>
    internal static Dictionary<string, object?> DescribeInstallRestart()
    {
        var offer = CurrentRestartOffer();
        var d = offer.Describe();
        var r = _resume;
        var installRecord = InstallRestartRecord.Read(InstallRestartPath);
        return new Dictionary<string, object?>
        {
            ["pendingRestart"] = d,
            ["confirmOpen"] = _confirmOffer != null,
            ["restarting"] = Interlocked.CompareExchange(ref _relaunching, 0, 0) != 0,
            ["leaving"] = _quit != null,
            ["lastRestart"] = _lastInstallRestart,
            ["installRecord"] = installRecord == null ? null : new Dictionary<string, object?>
            {
                ["created"] = installRecord.Created,
                ["dryRun"] = installRecord.DryRun,
                ["returnTo"] = installRecord.ReturnTo,
                ["returnName"] = installRecord.ReturnName,
                ["mods"] = installRecord.Mods.ConvertAll(m => (object?)new Dictionary<string, object?> { ["name"] = m.Name, ["id"] = m.Id, ["action"] = m.Action }),
            },
            ["rejoinRecord"] = DescribeRecord(RejoinStore.Peek(RejoinPath)),
            ["resume"] = r == null ? null : new Dictionary<string, object?>
            {
                ["save"] = r.Save, ["phase"] = r.Phase, ["error"] = r.Error, ["continueSeen"] = r.ContinueSeen, ["pressed"] = r.Pressed,
            },
            ["lastPickup"] = _lastInstallPickup,
            ["afterRestart"] = _lastAfterRestart,
        };
    }
}
