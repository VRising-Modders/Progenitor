using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Ui;
using Progenitor.WorldRestore;

namespace Progenitor.Networking;

/// <summary>
/// WORLDRESTORE1, client: the Backups rows of Server rules (after the next-wipe row, admin only) and the restore dialogs:
/// <list type="number">
/// <item>the list (the disk use, the retention rule; two-line rows of kind and date over world age and size, Progenitor
/// backups first, then wipe archives, each newest first);</item>
/// <item>one restore point's facts with <b>Restore this world</b>;</item>
/// <item>when its wipe date has passed under the next start's settings: restart the wipe clock from today, turn wipes off,
/// or pick a new wipe date (the date picker inside the dialog);</item>
/// <item>the typed confirmation (<c>RESTORE</c>): the backup step, the chosen state, what happens and the next wipe date;
/// <b>Restore at next restart</b> or <b>Restore and restart now</b> (the server stops after a countdown; the host must
/// start it again).</item>
/// </list>
/// The requests travel on the Server rules channel (<see cref="ServerRulesRows.Requester"/>); the server decides each one
/// again. Game-free: the dialog factory is a seam.
/// <para>BACKUPSDLG1: every dialog here belongs to the server session it was opened in. Leaving that server (<see cref="Leave"/>)
/// closes them and drops the answers still on their way; the notices and confirmations name the server and the world; and
/// when the pending request changes elsewhere the server says so (<see cref="OnServerChanged"/>), the open view asks again
/// and shows the new state (<see cref="PlanRefresh"/>).</para>
/// </summary>
internal static class ServerBackupsRows
{
    internal const string BackupsLabel = "Backups";
    internal const string ShowLabel = "Restore an earlier world";
    internal const string ShowCaption = "Show";
    internal const string PendingLabel = "Restore pending";
    internal const string CancelLabel = "Cancel the restore";
    internal const string CancelCaption = "Cancel restore";
    internal const string ResultLabel = "Last restore";
    internal const string KeepLabel = "Keep Progenitor backups";

    // BACKUPNOW1
    internal const string BackupNowCaption = "Back up now";
    internal const string BackupNowLabel = "Back up the world now";
    internal const string LastBackupLabel = "Last backup";
    internal const string AtStartLabel = "Back up at every server start";
    internal const string EveryHoursLabel = "Back up every";
    internal const string KeepCountLabel = "Keep backups per kind";
    internal const string KeepDaysLabel = "Delete backups older than";
    internal const string DialogTag = "world-restore";

    internal const string RestoreNextCaption = "Restore at next restart";
    internal const string RestoreNowCaption = "Restore and restart now";
    internal const string RestoreNowKey = "restore-now";

    internal const double AskEverySeconds = 5;
    internal const double RefreshEverySeconds = 30;

    private static ServerBackupsState? _state;
    private static double _lastAsk = double.NegativeInfinity;

    /// <summary>BACKUPSDLG1: the server session the dialogs and answers belong to (advanced on every leave).</summary>
    private static int _session;

    /// <summary>BACKUPSDLG1: the pending request as the open Backups dialog was drawn with (<see cref="ServerBackups.PendingSignature(ServerBackupsState?)"/>).</summary>
    private static string _openSignature = "";

    /// <summary>BACKUPSDLG1: the readback's <c>lastPush</c>: the last pending-change push and what the open view did.</summary>
    private static string _lastPush = "";

    /// <summary>The key of a notice about the pending request (a restore or cancel answer): it goes stale when the request changes.</summary>
    internal const string NoticePendingKey = "pending";

    /// <summary>
    /// BACKUPSDLG1: the joined server's name as the Server tab shows it (live and default: the Server rules state's running
    /// <c>Name</c>, else the next start's). Tests set their own.
    /// </summary>
    internal static Func<string?>? ServerNameProbe { get; set; }

    /// <summary>The server session the dialogs belong to (advanced by <see cref="Leave"/>).</summary>
    internal static int Session => _session;

    /// <summary>The restore points the server last answered with (admins only), or null.</summary>
    internal static ServerBackupsState? State => _state;

    /// <summary>The last answer's note or refusal (the readback's <c>last</c>).</summary>
    internal static string LastAnswer { get; private set; } = "";

    /// <summary>Where the dialogs get their panel (live: the Mods window's dialog factory).</summary>
    internal static Func<string, IUiPanel>? DialogFactory { get; set; }

    /// <summary>The server's time zone (the wipe date is 00:00 there; live: <c>JoinServerSettings.ServerZone</c>).</summary>
    internal static Func<TimeZoneInfo> ServerZone { get; set; } = () => TimeZoneInfo.Local;

    internal static Func<double> Clock { get; set; } = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>The rows' show-if: the joined server has the channel and this player is an admin there.</summary>
    internal static bool Shown()
    {
        if (!ServerRulesRows.Shown() || !ServerRulesRows.CanEdit()) return false;
        // BACKUPNOW1: a running backup's progress is asked for more often (only while the page shows the rows).
        AskIfDue(_state == null || _state.JobRunning ? AskEverySeconds : RefreshEverySeconds);
        return true;
    }

    /// <summary>
    /// BACKUPROWS1 (live 2026-10-19: after Back up now the Last backup row stayed on "saving the world…" for 40 s, and the
    /// Backups row on "No restore points yet"): the show-if above runs only when the page is re-evaluated (its build, or an
    /// answer of the channel), so a running backup's progress was asked for once and never again. The rows' own lines,
    /// repainted every second while the page shows them, ask while a backup runs (at most every <see cref="AskEverySeconds"/>);
    /// the answer repaints them and re-evaluates the page. Returns whether a request went out.
    /// </summary>
    internal static bool KeepCurrent() => _state is { JobRunning: true } && AskIfDue(AskEverySeconds);

    /// <summary>The Backups row's line (<see cref="ServerBackups.Summary"/>), asking for news while a backup runs.</summary>
    internal static string SummaryLine()
    {
        KeepCurrent();
        return ServerBackups.Summary(_state);
    }

    /// <summary>The Last backup row's line (<see cref="ServerBackups.BackupLine"/>), asking for news while a backup runs.</summary>
    internal static string LastBackupLine()
    {
        KeepCurrent();
        return ServerBackups.BackupLine(_state);
    }

    internal static bool AskIfDue(double every = AskEverySeconds)
    {
        double now = Clock();
        if (now - _lastAsk < every && now >= _lastAsk) return false;
        _lastAsk = now;
        return Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }, null) == null;
    }

    /// <summary>Send one request; the answer's backups replace <see cref="State"/>. Null when sent, else why not.</summary>
    internal static string? Ask(ServerRulesRequest request, Action<ServerRulesResponse?>? done)
    {
        var requester = ServerRulesRows.Requester;
        if (requester == null) return "Not connected to a server with Progenitor.";
        string? refusal;
        // BACKUPSDLG1: an answer that arrives after the client left this server (or the "no answer" a leave gives every
        // waiting request) belongs to the old session: it changes nothing and opens nothing.
        int session = _session;
        string op = request.Op;
        try
        {
            refusal = requester(request, response =>
            {
                if (session != _session)
                {
                    Plugin.Logger?.LogInfo("WorldRestore: the " + op + " answer belongs to an earlier server session; dropped.");
                    return;
                }

                Accept(response);
                done?.Invoke(response);
            });
        }
        catch (Exception e) { refusal = e.Message; }

        return refusal;
    }

    internal static void Accept(ServerRulesResponse? response)
    {
        if (response == null)
        {
            LastAnswer = ServerRulesRows.NoAnswerError;
            return;
        }

        if (response.Backups != null) _state = response.Backups;
        if (response.Op != ServerRulesRequest.OpBackups || !response.Ok)
            LastAnswer = response.Ok ? response.Note : response.Error;
    }

    // ---- BACKUPSDLG1: the server session, its names, and changes made elsewhere ------------------------

    /// <summary>
    /// The client left the server (disconnect, kick, world ended, a new join's handshake; live: <c>Handshake.ServerContextCleared</c>):
    /// every Backups dialog closes without a callback, the state and the last answer are forgotten, and answers still on their
    /// way are dropped. Returns how many dialogs closed.
    /// </summary>
    internal static int Leave(string reason)
    {
        bool had = _state != null || LastAnswer.Length > 0;
        _session++;
        _state = null;
        LastAnswer = "";
        _lastAsk = double.NegativeInfinity;
        _dateDialog = null;
        _dateLatest = "";
        _openSignature = "";
        _lastPush = "";
        int closed = 0;
        try { closed = UiDialog.CloseTag(DialogTag); }
        catch (Exception e) { Plugin.Logger?.LogWarning("WorldRestore: closing the Backups dialogs threw: " + e.Message); }
        if (closed > 0 || had)
            Plugin.Logger?.LogInfo("WorldRestore: left the server (" + (reason ?? "") + "): closed " + closed + " Backups dialog(s); answers of that session are dropped.");
        return closed;
    }

    /// <summary>The joined server's name (the Server tab's), or empty.</summary>
    internal static string ServerName()
    {
        var probe = ServerNameProbe;
        if (probe != null)
        {
            try { return (probe() ?? "").Trim(); }
            catch { return ""; }
        }

        var row = ServerRulesRows.Row(ServerRules.Name);
        string loaded = (row?.Loaded ?? "").Trim();
        return loaded.Length > 0 ? loaded : (row?.Next ?? "").Trim();
    }

    /// <summary>The world a restore replaces: the running save's name, else the next start's <c>SaveName</c>, or empty.</summary>
    internal static string WorldName(ServerBackupsState? s)
    {
        if (s is { SaveName.Length: > 0 }) return s.SaveName;
        return (ServerRulesRows.State?.SaveName ?? "").Trim();
    }

    /// <summary>The Backups dialog on screen, or null.</summary>
    internal static UiDialog? OpenDialog() =>
        UiDialog.Current is { IsOpen: true } d && string.Equals(d.Tag, DialogTag, StringComparison.Ordinal) ? d : null;

    /// <summary>What an open view does after the pending request changed elsewhere.</summary>
    internal enum Refresh
    {
        /// <summary>Nothing it shows depends on the request (or the request is what it was drawn with).</summary>
        None,

        /// <summary>The list again, with a note saying what changed.</summary>
        List,

        /// <summary>The restore point's facts again, with the note (a confirmation falls back here: its typed word is dropped).</summary>
        Detail,
    }

    /// <summary>
    /// Game-free decision: the open view <paramref name="kind"/> (about <paramref name="key"/>), drawn when the pending request
    /// was <paramref name="openedSignature"/>, and the state the server answers now. A confirmation, or a notice about the
    /// request, never keeps showing a request that no longer matches.
    /// </summary>
    internal static (Refresh Action, string Note) PlanRefresh(string kind, string key, string openedSignature, ServerBackupsState now)
    {
        if (now == null || string.Equals(ServerBackups.PendingSignature(now), openedSignature ?? "", StringComparison.Ordinal)) return (Refresh.None, "");
        string note = ServerBackups.ChangedNote(now);
        bool listed = now.Find(key) != null;
        switch (kind)
        {
            case "list":
                return (Refresh.List, note);
            case "detail":
            case "confirm-typed":
                return listed ? (Refresh.Detail, note) : (Refresh.List, note);
            case "wipe":
            case "wipe-date":
                // These do not show the request; the confirmation after them is drawn from the new state.
                return listed ? (Refresh.None, "") : (Refresh.List, note);
            case "notice":
                return string.Equals(key, NoticePendingKey, StringComparison.Ordinal) ? (Refresh.List, note) : (Refresh.None, "");
            default:
                return (Refresh.None, "");
        }
    }

    /// <summary>
    /// The server's push (<c>ServerRulesWire.KindBackupsChanged</c>): the pending request or its countdown changed elsewhere.
    /// With a Backups dialog open whose request differs, ask once for the state and redraw the view (<see cref="ApplyRefresh"/>);
    /// otherwise only mark the rows' state due, so an open page asks on its next draw. No polling. Returns what it did.
    /// </summary>
    internal static string OnServerChanged(string pendingId, bool countdown, string why)
    {
        _lastAsk = double.NegativeInfinity;
        string head = "pending " + (string.IsNullOrEmpty(pendingId) ? "none" : pendingId) + (countdown ? ", countdown" : "") + " (" + (why ?? "") + ")";
        string result;
        var open = OpenDialog();
        // BACKUPROWS1: a backup finished (or one was running as this page last heard): ask once, so the Last backup and
        // Backups rows show the result without waiting for the page's next draw.
        bool backupNews = string.Equals(why, ServerBackups.BackupFinishedWhy, StringComparison.Ordinal) || _state is { JobRunning: true };
        bool viewCurrent = open != null && string.Equals(ServerBackups.PendingSignature(pendingId, countdown), _openSignature, StringComparison.Ordinal);
        if (backupNews && (open == null || viewCurrent))
        {
            string? refusal = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }, null);
            _lastAsk = Clock();
            result = (open == null ? "no Backups dialog open" : "the open view is current") + "; "
                + (refusal == null ? "asked for the backup's result" : "not asked: " + refusal);
        }
        else if (open == null) result = "no Backups dialog open";
        else if (viewCurrent) result = "the open view is current";
        else
        {
            string? refusal = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }, response =>
            {
                if (response?.Backups == null) return;
                ApplyRefresh();
            });
            _lastAsk = Clock();
            result = refusal == null ? "asked again for the " + open.Kind + " view" : "not asked: " + refusal;
        }

        _lastPush = head + ": " + result;
        Plugin.Logger?.LogInfo("WorldRestore: the server says the restore request changed: " + _lastPush + ".");
        return result;
    }

    /// <summary>Redraw the open Backups view from <see cref="State"/> when the request it shows changed (<see cref="PlanRefresh"/>).</summary>
    internal static Refresh ApplyRefresh()
    {
        var open = OpenDialog();
        var s = _state;
        if (open == null || s == null) return Refresh.None;
        var (action, note) = PlanRefresh(open.Kind, open.Key, _openSignature, s);
        string kind = open.Kind;
        switch (action)
        {
            case Refresh.List:
                ShowList(note);
                break;
            case Refresh.Detail:
                ShowDetail(open.Key, note);
                break;
            default:
                return Refresh.None;
        }

        string line = "the " + kind + " view shows the " + (action == Refresh.List ? "list" : "restore point") + " again";
        _lastPush = _lastPush.Length > 0 ? _lastPush + "; " + line : line;
        Plugin.Logger?.LogInfo("WorldRestore: the restore request changed on the server; " + line + ".");
        return action;
    }

    // ---- the rows ---------------------------------------------------------------------------------------

    /// <summary>The Backups rows, placed after the next-wipe row of Server rules.</summary>
    internal static void Build(SettingsPageBuilder page)
    {
        page.Info(BackupsLabel, SummaryLine, refreshSeconds: 1,
                help: "Restore points of this world: the game's wipe archives (made when a scheduled wipe ran) and the backups "
                    + "Progenitor makes before every restore. Only admins see them.", showIf: Shown)
            .InScope(SettingScope.ServerWide)
            .Readback("worldBackups", Readback);
        page.Button(ShowCaption, () => RunOrThrow(OpenList()), label: ShowLabel,
                help: "Lists the restore points: kind, date, world age and size. Picking one shows Restore this world. Every restore "
                    + "first makes a verified backup of the current world, and applies when the server restarts.", showIf: Shown)
            .InScope(SettingScope.ServerWide);
        page.Info(PendingLabel, () => ServerBackups.PendingLine(_state), refreshSeconds: 1,
                help: "A restore waits for the next server start; Cancel restore deletes the request.",
                showIf: () => Shown() && (_state?.PendingId.Length ?? 0) > 0)
            .InScope(SettingScope.ServerWide)
            .Status(() => true);
        page.Button(CancelCaption, () => RunOrThrow(CancelPending()), label: CancelLabel,
                help: "Deletes the restore request (and stops a restart countdown); the next start loads the current world.",
                showIf: () => Shown() && (_state?.PendingId.Length ?? 0) > 0)
            .InScope(SettingScope.ServerWide);
        page.Info(ResultLabel, () => ServerBackups.ResultLine(_state), refreshSeconds: 1,
                help: "What the last restore did, from the server's progenitor-restore.done.json.",
                showIf: () => Shown() && (_state?.ResultId.Length ?? 0) > 0)
            .InScope(SettingScope.ServerWide)
            .Status(() => _state is { ResultId.Length: > 0, ResultOk: false });
        page.AdminSetting(KeepSpec(), Shown);

        // BACKUPNOW1: Back up now, its progress and result, and the schedule and retention of manual and scheduled backups.
        page.Button(BackupNowCaption, () => RunOrThrow(ShowBackupNow() == null ? BackupNowRefusalOrNull() ?? "The dialogs are not available here." : null),
                label: BackupNowLabel,
                help: "Saves the world, then copies the whole world folder (saves, mod data, StartDate.json) into a Manual backup, checked "
                    + "file by file, while the server keeps running. It is listed under Restore an earlier world and can be restored.",
                showIf: Shown)
            .InScope(SettingScope.ServerWide);
        page.Info(LastBackupLabel, LastBackupLine, refreshSeconds: 1,
                help: "The backup running now (saving, copying, checking) or the last manual or scheduled backup's result.",
                showIf: () => Shown() && ServerBackups.BackupLine(_state).Length > 0)
            .InScope(SettingScope.ServerWide)
            .Status(() => _state is { JobRunning: false, LastBackupOk: false, LastBackupSummary.Length: > 0 });
        page.AdminSetting(AtStartSpec(), Shown);
        page.AdminSetting(EveryHoursSpec(), Shown);
        page.AdminSetting(KeepCountSpec(), Shown);
        page.AdminSetting(KeepDaysSpec(), Shown);
    }

    private static string? BackupNowRefusalOrNull() => ServerBackups.BackupNowRefusal(_state);

    private static void SendSetting(string key, string? value, Action<bool, string> done)
    {
        string? refusal = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsSet, Key = key, Value = value ?? "" },
            response => done(response?.Ok ?? false, response == null ? ServerRulesRows.NoAnswerError : response.Ok ? response.Note : response.Error));
        if (refusal != null) done(false, refusal);
    }

    internal static AdminSettingSpec AtStartSpec() => new()
    {
        SettingId = ServerBackups.AtStartSettingId,
        Key = WorldBackupSettings.AtStartKey,
        Label = AtStartLabel,
        Help = "A Scheduled backup of the whole world folder at every server start, before the game loads (the safest moment). With a "
            + "restore waiting, the restore's own backup is that copy. A failed backup is logged and shown in the Backups list; the server "
            + "starts anyway. Also [WorldRestore] BackupAtStart in the server's config.",
        Kind = AdminSettingKind.Toggle,
        Read = () => _state == null ? null : _state.AtStart ? "true" : "false",
        Send = (value, done) => SendSetting(WorldBackupSettings.AtStartKey, value, done),
        LockedBy = () => _state is { AtStartLockedBy.Length: > 0 } s ? s.AtStartLockedBy : null,
    };

    internal static AdminSettingSpec EveryHoursSpec() => new()
    {
        SettingId = ServerBackups.EveryHoursSettingId,
        Key = WorldBackupSettings.EveryHoursKey,
        Label = EveryHoursLabel,
        Help = "A Scheduled backup every this many hours while the server runs: the world is saved, then copied and checked without "
            + "stopping play. 0 is off. Also [WorldRestore] BackupEveryHours in the server's config.",
        Kind = AdminSettingKind.Number,
        Min = 0,
        Max = WorldBackupSettings.MaxEveryHours,
        Units = "hours",
        RangeChecked = true,
        Read = () => _state?.EveryHours.ToString(CultureInfo.InvariantCulture),
        Send = (value, done) => SendSetting(WorldBackupSettings.EveryHoursKey, value, done),
        LockedBy = () => _state is { EveryHoursLockedBy.Length: > 0 } s ? s.EveryHoursLockedBy : null,
        Display = v => v == "0" ? "Off" : v,
    };

    internal static AdminSettingSpec KeepCountSpec() => new()
    {
        SettingId = ServerBackups.KeepCountSettingId,
        Key = WorldBackupSettings.KeepCountKey,
        Label = KeepCountLabel,
        Help = "After each manual or scheduled backup, older ones past this many of each kind are deleted (0: no count limit). With an "
            + "age limit too, a backup must pass both. The newest of each kind, the source of a waiting restore and the backups made "
            + "before restores are never deleted by it. Also [WorldRestore] BackupKeepCount.",
        Kind = AdminSettingKind.Number,
        Min = 0,
        Max = BackupPolicy.MaxCount,
        Units = "of each kind",
        RangeChecked = true,
        Read = () => _state?.KeepCount.ToString(CultureInfo.InvariantCulture),
        Send = (value, done) => SendSetting(WorldBackupSettings.KeepCountKey, value, done),
        LockedBy = () => _state is { KeepCountLockedBy.Length: > 0 } s ? s.KeepCountLockedBy : null,
        Display = v => v == "0" ? "All" : v,
    };

    internal static AdminSettingSpec KeepDaysSpec() => new()
    {
        SettingId = ServerBackups.KeepDaysSettingId,
        Key = WorldBackupSettings.KeepDaysKey,
        Label = KeepDaysLabel,
        Help = "After each manual or scheduled backup, those older than this many days are deleted (0: no age limit). The newest of "
            + "each kind always stays. Also [WorldRestore] BackupKeepDays.",
        Kind = AdminSettingKind.Number,
        Min = 0,
        Max = BackupPolicy.MaxDays,
        Units = "days",
        RangeChecked = true,
        Read = () => _state?.KeepDays.ToString(CultureInfo.InvariantCulture),
        Send = (value, done) => SendSetting(WorldBackupSettings.KeepDaysKey, value, done),
        LockedBy = () => _state is { KeepDaysLockedBy.Length: > 0 } s ? s.KeepDaysLockedBy : null,
        Display = v => v == "0" ? "Never" : v,
    };

    // ---- Back up now ------------------------------------------------------------------------------------

    /// <summary>The Back up now confirmation (where, the estimated size, what is included). Null when it cannot open.</summary>
    internal static UiDialog? ShowBackupNow()
    {
        var s = _state;
        if (s == null || DialogFactory == null) return null;
        var content = BackupNowContent(s);
        var dialog = Open(content, "backup-now");
        if (dialog == null || content.Shape != UiDialogShape.Confirm) return dialog;
        dialog.OnYes(() =>
        {
            string? refusal = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow },
                response => Notice(response, "Backing up", "Not backed up"));
            if (refusal != null) Open(ServerNoticeContent("Not backed up", refusal), "notice");
        });
        return dialog;
    }

    internal static DialogContent BackupNowContent(ServerBackupsState s)
    {
        string? refusal = ServerBackups.BackupNowRefusal(s);
        if (refusal != null) return NoticeContent("Back up now", refusal);
        long need = WorldBackupMaker.Need(s.CurrentBytes, new BackupJob().SpareBytes);
        var facts = new List<DialogFact>();
        // BACKUPSDLG1: which server and world.
        string server = ServerBackups.WhereLine(ServerName(), WorldName(s));
        if (server.Length > 0) facts.Add(new DialogFact(ServerFactLabel, server));
        facts.AddRange(new List<DialogFact>
        {
            new("Where", "On the server, in " + (s.BackupsWhere.Length > 0 ? s.BackupsWhere : "progenitor-backups") + " beside its Saves folder"),
            new("Size", "about " + WorldBackupCatalog.FormatBytes(s.CurrentBytes) + (s.FreeBytes >= 0 ? " (" + WorldBackupCatalog.FormatBytes(s.FreeBytes) + " free)" : "")),
            new("Includes", ServerBackups.IncludesText),
            new("How", "The server saves the world, then copies and checks every file while play goes on. A save that lands during the "
                + "copy is caught and the copy made again."),
            new("Kept", ServerBackups.BackupRetentionLine(s)),
        });
        if (s.FreeBytes >= 0 && s.FreeBytes < need)
            facts.Add(new DialogFact("Disk", "Not enough free space: " + WorldBackupCatalog.FormatBytes(need) + " needed.", DialogFactTone.Danger));
        string deletes = PruneBackupNames(s, WorldRestorePaths.KindManual);
        if (deletes.Length > 0) facts.Add(new DialogFact("Retention", "After it, retention deletes: " + deletes + ".", DialogFactTone.Warn));
        return new DialogContent
        {
            Shape = UiDialogShape.Confirm,
            Title = "Back up the world now?",
            Facts = facts,
            MainCaption = BackupNowCaption,
            MainTone = CellTone.Gold,
            CancelCaption = "Cancel",
            FrameWidth = DialogLayout.WideFrameWidth,
        };
    }

    /// <summary>The names of the backups retention deletes after the next backup (from the server's preview), or empty.</summary>
    internal static string PruneBackupNames(ServerBackupsState s, string? kind)
    {
        var names = new List<string>();
        foreach (var p in s.PruneNextBackup)
            if (kind == null || p.Name.StartsWith(WorldBackupEntry.KindLabelOf(WorldBackupEntry.KindBackup, kind), StringComparison.Ordinal)) names.Add(p.Name);
        // At most two names (the header's line stays short); the rest are counted.
        if (names.Count > 2) return names[0] + "; " + names[1] + "; and " + (names.Count - 2) + " more";
        return string.Join("; ", names);
    }

    internal static AdminSettingSpec KeepSpec() => new()
    {
        SettingId = ServerBackups.KeepSettingId,
        Key = "KeepBackups",
        Label = KeepLabel,
        Help = "After a restore, Progenitor keeps this many of the backups it made before restores (the newest, at least 2) and "
            + "deletes older ones (manual and scheduled backups have their own rule below); 0 "
            + "keeps every one. The oldest backup of each restore chain and the source of a waiting restore are always kept, and "
            + "the game's wipe archives and .BACKUP copies are never deleted. Also [WorldRestore] KeepBackups in the server's config.",
        Kind = AdminSettingKind.Number,
        Min = 0,
        Max = WorldBackupCatalog.MaxKeep,
        Units = "backups",
        RangeChecked = true,
        Read = () => _state?.Keep.ToString(CultureInfo.InvariantCulture),
        Send = (value, done) =>
        {
            string? refusal = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsKeep, Value = value ?? "" },
                response => done(response?.Ok ?? false, response == null ? ServerRulesRows.NoAnswerError : response.Ok ? response.Note : response.Error));
            if (refusal != null) done(false, refusal);
        },
        LockedBy = () => _state is { KeepLockedBy.Length: > 0 } s ? s.KeepLockedBy : null,
        Display = v => v == "0" ? "All" : v,
    };

    private static void RunOrThrow(string? refusal)
    {
        if (refusal != null) throw new InvalidOperationException(refusal);
    }

    /// <summary>Cancel restore: the server deletes the request.</summary>
    internal static string? CancelPending() =>
        Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestoreCancel }, response => Notice(response, "Restore cancelled", "Not cancelled", NoticePendingKey));

    // ---- the dialogs ------------------------------------------------------------------------------------

    /// <summary>The Show button: ask for the list now, then open it. Null when asked, else why not.</summary>
    internal static string? OpenList()
    {
        if (DialogFactory == null) return "The dialogs are not available here.";
        _lastAsk = Clock();
        return Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }, response =>
        {
            if (response?.Backups == null)
            {
                Open(ServerNoticeContent("Backups", response == null ? ServerRulesRows.NoAnswerError : response.Error.Length > 0 ? response.Error : "The server sent no list."), "notice");
                return;
            }

            ShowList();
        });
    }

    /// <summary>The list dialog over <see cref="State"/> (BACKUPSDLG1: <paramref name="note"/> says what changed elsewhere).</summary>
    internal static UiDialog? ShowList(string note = "")
    {
        var s = _state;
        if (s == null) return null;
        var content = ListContent(s, note);
        var dialog = Open(content, "list");
        if (dialog == null || content.Shape != UiDialogShape.Choose) return dialog;
        var shown = ListedEntries(s);
        dialog.OnPick(i =>
        {
            if (i >= 0 && i < shown.Count) ShowDetail(shown[i].Id);
        });
        return dialog;
    }

    internal const string ManualHeading = "Manual backups";
    internal const string ScheduledHeading = "Scheduled backups";
    internal const string BackupsHeading = "Backups made before a restore";

    /// <summary>AUTOUPDATE3: the heading of the backups the server made before applying mod updates.</summary>
    internal const string PreUpdateHeading = "Backups made before an update";
    internal const string ArchivesHeading = "Wipe archives";
    internal const string DiskLabel = "Disk use";
    internal const string KeptLabel = "Kept";
    internal const string DeletesLabel = "Next restore deletes";
    internal const string RefusedLabel = "Restores";
    internal const string ListedLabel = "Listed";
    internal const string KindsLabel = "By kind";
    internal const string BackupKeptLabel = "Backups kept";
    internal const string ScheduleLabel = "Scheduled";
    internal const string ScheduleFailedLabel = "Schedule failed";
    internal const string AutosavesLabel = "Game autosaves";
    internal const string AutosaveTimesLabel = "Autosaved";
    internal const string AutosavesAreLabel = "Autosaves are";
    internal const string BackupsAddLabel = "Backups add";
    internal const string NextBackupDeletesLabel = "Next backup deletes";

    /// <summary>
    /// LISTROWS1: the entries the list shows, in its order: the newest <see cref="DialogLayout.MaxListOptions"/> restore
    /// points, then the Progenitor backups before the wipe archives, each group newest first. The picked index is an
    /// index into this list.
    /// </summary>
    internal static List<ServerBackupView> ListedEntries(ServerBackupsState s)
    {
        // BACKUPNOW1: the groups (manual, scheduled, AUTOUPDATE3 before an update, before a restore, wipe archives); every group
        // keeps its newest entry.
        var newest = ServerBackups.PickNewest(NewestFirst(s.Entries), ServerBackups.GroupOf, DialogLayout.MaxListOptions);
        var list = new List<ServerBackupView>(newest.Count);
        foreach (int g in ServerBackups.GroupOrder)
            foreach (var e in newest)
                if (ServerBackups.GroupOf(e) == g) list.Add(e);
        return list;
    }

    private static bool IsArchive(ServerBackupView e) => string.Equals(e.Kind, WorldBackupEntry.KindArchive, StringComparison.Ordinal);

    /// <summary>A stable newest-first copy: an unknown date sorts last, and the wire's order breaks ties.</summary>
    private static List<ServerBackupView> NewestFirst(IReadOnlyList<ServerBackupView> entries)
    {
        var order = new List<int>(entries.Count);
        for (int i = 0; i < entries.Count; i++) order.Add(i);
        order.Sort((x, y) =>
        {
            var tx = entries[x].CreatedTime;
            var ty = entries[y].CreatedTime;
            int c = tx.HasValue && ty.HasValue ? ty.Value.CompareTo(tx.Value) : tx.HasValue ? -1 : ty.HasValue ? 1 : 0;
            return c != 0 ? c : x.CompareTo(y);
        });
        var list = new List<ServerBackupView>(order.Count);
        foreach (int i in order) list.Add(entries[i]);
        return list;
    }

    /// <summary>LISTROWS1: the group headings over <paramref name="shown"/> (in <see cref="ListedEntries"/> order).</summary>
    internal static List<DialogOptionHeading> ListHeadings(IReadOnlyList<ServerBackupView> shown)
    {
        var list = new List<DialogOptionHeading>(4);
        for (int i = 0; i < shown.Count; i++)
        {
            int g = ServerBackups.GroupOf(shown[i]);
            if (i > 0 && g == ServerBackups.GroupOf(shown[i - 1])) continue;
            list.Add(new DialogOptionHeading(i, g switch
            {
                0 => ManualHeading, 1 => ScheduledHeading, 2 => BackupsHeading, ServerBackups.GroupPreUpdate => PreUpdateHeading, _ => ArchivesHeading,
            }));
        }

        return list;
    }

    /// <summary>
    /// LISTROWS1: the list's header as left-aligned label/value lines (the wipe dialog's facts): the disk use, what is
    /// kept, which backups the next restore deletes (amber, only when any), why restores cannot run here, and how many
    /// are listed when not all are.
    /// </summary>
    internal static List<DialogFact> HeaderFacts(ServerBackupsState s, int shown)
    {
        var facts = new List<DialogFact>
        {
            new(DiskLabel, ServerBackups.DiskLine(s)),
        };
        // BACKUPNOW1: the disk use of each kind, the schedule, a running or last backup, and the second retention rule.
        bool made = s.ManualBytes > 0 || s.ScheduledBytes > 0;
        if (made || s.PreUpdateBytes > 0) facts.Add(new DialogFact(KindsLabel, ServerBackups.KindsLine(s)));
        facts.Add(new DialogFact(KeptLabel, ServerBackups.RetentionLine(s)));
        if (made || s.AtStart || s.EveryHours > 0) facts.Add(new DialogFact(BackupKeptLabel, ServerBackups.BackupRetentionLine(s)));
        if (s.AtStart || s.EveryHours > 0) facts.Add(new DialogFact(ScheduleLabel, ServerBackups.ScheduleLine(s)));
        if (s.ScheduleFailure.Length > 0)
            facts.Add(new DialogFact(ScheduleFailedLabel, (DateTime.TryParseExact(s.ScheduleFailureAt, ServerBackups.LocalFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var at) ? WorldBackupCatalog.FormatTime(at) + ": " : "") + s.ScheduleFailure, DialogFactTone.Warn));
        string backupLine = ServerBackups.BackupLine(s);
        if (backupLine.Length > 0)
            facts.Add(new DialogFact(LastBackupLabel, backupLine, !s.JobRunning && !s.LastBackupOk ? DialogFactTone.Warn : DialogFactTone.Normal));
        // The game's own autosaves, and what Progenitor backups add to them (once).
        if (s.AutosaveKnown)
        {
            facts.Add(new DialogFact(AutosavesLabel, ServerBackups.AutosaveLine(s)));
            string span = ServerBackups.AutosaveSpan(s);
            if (span.Length > 0) facts.Add(new DialogFact(AutosaveTimesLabel, span));
            facts.Add(new DialogFact(AutosavesAreLabel, ServerBackups.AutosavesAreText));
            facts.Add(new DialogFact(BackupsAddLabel, ServerBackups.BackupsAddText));
        }

        string deletes = PruneNames(s, null);
        if (deletes.Length > 0) facts.Add(new DialogFact(DeletesLabel, deletes, DialogFactTone.Warn));
        string backupDeletes = PruneBackupNames(s, null);
        if (backupDeletes.Length > 0) facts.Add(new DialogFact(NextBackupDeletesLabel, backupDeletes, DialogFactTone.Warn));
        if (!s.RestoreAllowed && s.RestoreRefusal.Length > 0)
            facts.Add(new DialogFact(RefusedLabel, "not available here: " + s.RestoreRefusal, DialogFactTone.Warn));
        int total = Math.Max(s.Count, s.Entries.Count);
        if (shown > 0 && total > shown) facts.Add(new DialogFact(ListedLabel, "the newest " + shown + " of " + total + " restore points"));
        return facts;
    }

    internal static DialogContent ListContent(ServerBackupsState s, string note = "")
    {
        var shown = ListedEntries(s);
        var facts = HeaderFacts(s, shown.Count);
        // BACKUPSDLG1: which server and world the list is of.
        string server = ServerBackups.WhereLine(ServerName(), WorldName(s));
        if (server.Length > 0) facts.Insert(0, new DialogFact(ServerFactLabel, server));
        if (shown.Count == 0)
        {
            return new DialogContent
            {
                Shape = UiDialogShape.Notice,
                Title = "Backups",
                Body = "No restore points yet. The game makes a wipe archive when a scheduled wipe runs, and Progenitor backs up the "
                    + "current world before every restore, on Back up now, and on its schedule.",
                Facts = facts,
                Note = note ?? "",
                MainCaption = "Close",
                FrameWidth = DialogLayout.WideFrameWidth,
            };
        }

        var options = new List<string>(shown.Count);
        var notes = new List<string>(shown.Count);
        foreach (var e in shown)
        {
            options.Add(ServerBackups.EntryTitle(e));
            notes.Add(ServerBackups.EntryNote(e));
        }

        // LISTROWS1: full-width two-line rows (kind and date over a muted world age and size) under group headings.
        return new DialogContent
        {
            Shape = UiDialogShape.Choose,
            Title = "Backups",
            Facts = facts,
            Note = note ?? "",
            Options = options,
            OptionNotes = notes,
            OptionHeadings = ListHeadings(shown),
            CancelCaption = "Close",
            FrameWidth = DialogLayout.WideFrameWidth,
        };
    }

    internal static UiDialog? ShowDetail(string id, string note = "")
    {
        var s = _state;
        var e = s?.Find(id);
        if (s == null || e == null) return null;
        var content = DetailContent(s, e, note);
        var dialog = Open(content, "detail", id);
        if (dialog == null) return null;
        if (content.Shape == UiDialogShape.Confirm) dialog.OnYes(() => ShowWipeOrConfirm(id));
        dialog.OnNo(() => ShowList());
        return dialog;
    }

    internal static DialogContent DetailContent(ServerBackupsState s, ServerBackupView e, string note = "")
    {
        var facts = new List<DialogFact>
        {
            new("Kind", e.KindLabel),
            new("Made", e.CreatedTime is { } t ? WorldBackupCatalog.FormatTime(t) : "unknown"),
            new("World started", e.StartTime is { } st ? WorldBackupCatalog.FormatDay(st) : "unknown"),
            new("World age", ServerBackups.AgeText(e.AgeDays) + " at its last save"),
            new("Size", WorldBackupCatalog.FormatBytes(e.Size)),
        };
        bool can = e.Restorable && s.RestoreAllowed;
        string why = !e.Restorable ? e.Reason : !s.RestoreAllowed ? s.RestoreRefusal : "";
        if (why.Length > 0) facts.Add(new DialogFact("Cannot restore", why, DialogFactTone.Warn));
        // BACKUPSDLG1: a request already waiting is shown before another replaces it.
        string pending = ServerBackups.PendingLine(s);
        if (pending.Length > 0) facts.Add(new DialogFact(PendingLabel, pending, DialogFactTone.Warn));
        string stays = can ? "The archive or backup itself stays as it is: restoring copies it." : "";
        string changed = (note ?? "").Trim();
        return new DialogContent
        {
            Shape = can ? UiDialogShape.Confirm : UiDialogShape.Notice,
            Title = e.Name,
            Facts = facts,
            Note = changed.Length == 0 ? stays : stays.Length == 0 ? changed : changed + " " + stays,
            MainCaption = can ? "Restore this world" : "Back",
            MainTone = CellTone.Orange,
            CancelCaption = can ? "Back" : "",
            FrameWidth = DialogLayout.WideFrameWidth,
        };
    }

    /// <summary>The plan for <paramref name="e"/> with the next start's wipe settings and the server's clock.</summary>
    internal static RestoreWipePlan PlanFor(ServerBackupsState s, ServerBackupView e, string mode, DateTime? picked) =>
        ServerBackups.Plan(e.StartTime, mode, picked, s.NextInterval, s.NextDay, s.IntervalWritable, s.IntervalReason, s.ServerNowTime);

    internal static UiDialog? ShowWipeOrConfirm(string id)
    {
        var s = _state;
        var e = s?.Find(id);
        if (s == null || e == null) return null;
        var plan = PlanFor(s, e, WipeModes.None, null);
        if (!plan.Due) return ShowConfirm(id, WipeModes.None, null);
        var content = WipeContent(s, e, plan);
        var dialog = Open(content, "wipe", id);
        if (dialog == null) return null;
        var modes = WipeOptions(s);
        dialog.OnPick(i =>
        {
            if (i < 0 || i >= modes.Count) return;
            if (modes[i] == WipeModes.Date) ShowDate(id, "");
            else ShowConfirm(id, modes[i], null);
        });
        dialog.OnNo(() => ShowDetail(id));
        return dialog;
    }

    /// <summary>The three answers (the two interval answers only when the interval can be written for the next start).</summary>
    internal static List<string> WipeOptions(ServerBackupsState s)
    {
        var list = new List<string> { WipeModes.ClockToday };
        if (s.IntervalWritable)
        {
            list.Add(WipeModes.WipesOff);
            list.Add(WipeModes.Date);
        }

        return list;
    }

    internal static string WipeOptionLabel(string mode) => mode switch
    {
        WipeModes.ClockToday => ServerBackups.WipeClockLabel,
        WipeModes.WipesOff => ServerBackups.WipesOffLabel,
        _ => ServerBackups.PickDateLabel,
    };

    internal static DialogContent WipeContent(ServerBackupsState s, ServerBackupView e, RestoreWipePlan plan)
    {
        string started = e.StartTime is { } st ? "It started on " + WorldBackupCatalog.FormatDay(st) + ". " : "Its start date is not known. ";
        string due = plan.DueDate is { } d
            ? "With scheduled wipes every " + s.NextInterval + (s.NextInterval == 1 ? " day" : " days")
              + (s.NextDay.Length > 0 && s.NextDay != "Any" ? " (on " + s.NextDay + ")" : "") + ", its wipe date was " + WorldBackupCatalog.FormatDay(d)
              + ", which has passed."
            : "Scheduled wipes are on, so nothing rules a wipe out.";
        var options = new List<string>();
        foreach (string m in WipeOptions(s)) options.Add(WipeOptionLabel(m));
        var lines = new List<string>();
        if (!s.IntervalWritable && s.IntervalReason.Length > 0)
            lines.Add("Turning wipes off or picking a new date is not possible here: " + s.IntervalReason);
        return new DialogContent
        {
            Shape = UiDialogShape.Choose,
            Title = "The next start would wipe this world again",
            Body = started + due + " Restored as it is, the next start archives it again and starts a fresh world. Choose what its "
                + "wipe clock does:",
            Lines = lines,
            Options = options,
            CancelCaption = "Back",
            FrameWidth = DialogLayout.WideFrameWidth,
        };
    }

    internal static UiDialog? ShowDate(string id, string error)
    {
        var s = _state;
        var e = s?.Find(id);
        if (s == null || e == null) return null;
        var content = DateContent(s, e, error);
        var dialog = Open(content, "wipe-date", id);
        if (dialog == null) return null;
        // The picker's Done stores the value in the field; it is read here once the dialog has closed.
        _dateLatest = content.DateValue;
        try { dialog.View?.DateField?.OnChanged(v => _dateLatest = v ?? ""); }
        catch (Exception x) { Plugin.Logger?.LogWarning("WorldRestore: the date field: " + x.Message); }
        _dateDialog = dialog;
        dialog.OnYes(() =>
        {
            _dateDialog = null;
            var picked = DayOf(_dateLatest);
            var plan = PlanFor(s, e, WipeModes.Date, picked);
            if (plan.Error.Length > 0) ShowDate(id, plan.Error);
            else ShowConfirm(id, WipeModes.Date, picked);
        });
        dialog.OnNo(() => ShowWipeOrConfirm(id));
        return dialog;
    }

    private static UiDialog? _dateDialog;
    private static string _dateLatest = "";

    /// <summary>
    /// Bridge <c>world_backups dialog_date</c>: set the open wipe-date dialog's day (<c>yyyy-MM-dd</c>, 00:00 server time) as
    /// the picker's Done would. Returns the value set.
    /// </summary>
    internal static string SetDialogDate(string day)
    {
        var dialog = _dateDialog;
        if (dialog == null || !dialog.IsOpen) throw new InvalidOperationException("the wipe-date dialog is not open");
        if (!DateTime.TryParseExact((day ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            throw new ArgumentException("args.date must be yyyy-MM-dd");
        string iso = IsoOf(d);
        dialog.SetDateForBridge(iso);
        _dateLatest = iso;
        return iso;
    }

    internal static DialogContent DateContent(ServerBackupsState s, ServerBackupView e, string error)
    {
        var start = e.StartTime ?? s.ServerNowTime.Date;
        var first = WorldRestoreWipe.FirstFutureDate(start, s.ServerNowTime);
        return new DialogContent
        {
            Shape = UiDialogShape.Confirm,
            Title = ServerBackups.PickDateLabel,
            Body = "The restored world is wiped at the first server start on or after this day (00:00 server time). Progenitor sets "
                + "the scheduled-wipe interval to the days from its start date (" + WorldBackupCatalog.FormatDay(start) + ") to the day you "
                + "pick, and Wipe on to Any when the weekday would move it.",
            DateCaption = "Next wipe",
            DateValue = IsoOf(first),
            DateZone = Safe(ServerZone),
            Note = error,
            MainCaption = "Use this date",
            MainTone = CellTone.Gold,
            CancelCaption = "Back",
            FrameWidth = DialogLayout.WideFrameWidth,
        };
    }

    internal static UiDialog? ShowConfirm(string id, string mode, DateTime? picked)
    {
        var s = _state;
        var e = s?.Find(id);
        if (s == null || e == null) return null;
        var plan = PlanFor(s, e, mode, picked);
        var content = ConfirmContent(s, e, plan, picked);
        var dialog = Open(content, "confirm-typed", id);
        if (dialog == null) return null;
        void Send(bool now)
        {
            var request = new ServerRulesRequest
            {
                Op = ServerRulesRequest.OpRestore,
                Key = e.Id,
                Shape = e.Shape,
                Wipe = mode,
                Value = picked?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                RestartNow = now,
            };
            string? refusal = Ask(request, response => Notice(response, now ? "Restore requested; the server stops soon" : "Restore requested",
                "Not restored", NoticePendingKey));
            if (refusal != null) Open(ServerNoticeContent("Not restored", refusal), "notice", NoticePendingKey);
        }

        dialog.OnSubmit(_ => Send(false), null);
        dialog.OnExtra(RestoreNowKey, () => Send(true));
        dialog.OnNo(() => ShowDetail(id));
        return dialog;
    }

    internal static DialogContent ConfirmContent(ServerBackupsState s, ServerBackupView e, RestoreWipePlan plan, DateTime? picked)
    {
        string now = WorldBackupCatalog.FormatTime(s.ServerNowTime);
        string when = e.CreatedTime is { } t ? WorldBackupCatalog.FormatTime(t) : "an unknown date";
        string started = plan.Mode == WipeModes.ClockToday
            ? WorldBackupCatalog.FormatDay(s.ServerNowTime) + " (the wipe clock restarts; it was " + (e.StartTime is { } o ? WorldBackupCatalog.FormatDay(o) : "unknown") + ")"
            : e.StartTime is { } st ? WorldBackupCatalog.FormatDay(st) : "unknown";
        var facts = new List<DialogFact>();
        // BACKUPSDLG1: which server and which of its worlds goes back.
        string server = ServerBackups.WhereLine(ServerName(), WorldName(s));
        if (server.Length > 0) facts.Add(new DialogFact(ServerFactLabel, server));
        facts.AddRange(new List<DialogFact>
        {
            new("Backup first", "The whole current world (saves, mod data, StartDate.json), checked file by file and kept as \"Before "
                + "restore of " + (e.Label.Length > 0 ? e.Label : e.Name) + ", " + now + "\". If it cannot be made and checked, nothing changes."),
            new("Restores", e.KindLabel + " from " + when + ", " + ServerBackups.AgeText(e.AgeDays) + ", " + WorldBackupCatalog.FormatBytes(e.Size)),
            new("World started", started),
            new("What happens", "At the next restart the server loads that world. Everything after it is not in it: characters made "
                + "later, items, castles, clan changes. Mod data restores with it. " + s.Online + (s.Online == 1 ? " player is" : " players are")
                + " online now."),
            new("Next wipe after", ServerBackups.NextWipeText(plan), plan.Error.Length > 0 ? DialogFactTone.Danger : DialogFactTone.Normal),
            new("Restart now", "Stops the server after a 60-second countdown with chat warnings. Progenitor cannot start it again: the "
                + "host's own restart (a service, a panel or a script) must bring it back.", DialogFactTone.Warn),
        });
        string pending = ServerBackups.PendingLine(s);
        if (pending.Length > 0) facts.Add(new DialogFact("Replaces", pending, DialogFactTone.Warn));
        string prune = PruneWarning(s, e.Id);
        if (prune.Length > 0) facts.Add(new DialogFact("Retention", prune, DialogFactTone.Warn));
        if (plan.Error.Length > 0) facts.Add(new DialogFact("Refused", plan.Error, DialogFactTone.Danger));
        var content = new DialogContent
        {
            Shape = UiDialogShape.Prompt,
            Title = "Restore " + (e.Kind == WorldBackupEntry.KindArchive ? "the wipe archive" : "the backup") + " from " + when + "?",
            Facts = facts,
            Note = "Type " + ServerBackups.TypedWord + " to confirm.",
            InputInitial = "",
            InputPlaceholder = "Type " + ServerBackups.TypedWord,
            MaxLength = 16,
            ConfirmWord = ServerBackups.TypedWord,
            ExtrasNeedWord = true,
            MainCaption = RestoreNextCaption,
            MainTone = CellTone.Danger,
            Danger = true,
            CancelCaption = "Cancel",
            RowKey = DialogTag + "-buttons",
            FrameWidth = DialogLayout.WideFrameWidth,
        };
        content.ExtraButtons.Add(new DialogButton(RestoreNowKey, RestoreNowCaption, DialogButtonRole.Option, CellTone.Danger));
        return content;
    }

    /// <summary>
    /// M1: the backups the next restore's retention deletes (the chosen source never is), or empty:
    /// <c>After this restore, keeping the newest 5 deletes: Before restore of …</c>.
    /// </summary>
    internal static string PruneWarning(ServerBackupsState s, string? sourceId)
    {
        string names = PruneNames(s, sourceId);
        if (names.Length == 0) return "";
        return "After " + (sourceId == null ? "the next restore" : "this restore") + ", keeping the newest " + s.Keep + " deletes: "
            + names + ".";
    }

    /// <summary>The names of the backups the next restore's retention deletes (never <paramref name="sourceId"/>), or empty.</summary>
    internal static string PruneNames(ServerBackupsState s, string? sourceId)
    {
        var names = new List<string>();
        foreach (var p in s.PruneNext)
            if (!string.Equals(p.Id, sourceId, StringComparison.Ordinal)) names.Add(p.Name);
        return string.Join("; ", names);
    }

    internal static DialogContent NoticeContent(string title, string body) => new()
    {
        Shape = UiDialogShape.Notice,
        Title = title,
        Body = UiRichText.Literal(body ?? ""),
        MainCaption = "OK",
        FrameWidth = DialogLayout.WideFrameWidth,
    };

    /// <summary>The label of the server-and-world fact (BACKUPSDLG1).</summary>
    internal const string ServerFactLabel = "Server";

    /// <summary>BACKUPSDLG1: a notice from (or about) the joined server, named for it: <c>On My server: world world1 goes back …</c>.</summary>
    internal static DialogContent ServerNoticeContent(string title, string body) => NoticeContent(title, ServerBackups.OnServer(ServerName(), body));

    private static void Notice(ServerRulesResponse? response, string okTitle, string refusedTitle, string key = "")
    {
        if (DialogFactory == null) return;
        if (response == null) Open(ServerNoticeContent(refusedTitle, ServerRulesRows.NoAnswerError), "notice", key);
        else Open(ServerNoticeContent(response.Ok ? okTitle : refusedTitle, response.Ok ? response.Note : response.Error), "notice", key);
    }

    private static UiDialog? Open(DialogContent content, string kind, string key = "")
    {
        var factory = DialogFactory;
        if (factory == null) return null;
        // BACKUPSDLG1: the request this view is drawn with (a change elsewhere is compared with it).
        _openSignature = ServerBackups.PendingSignature(_state);
        try { return UiDialog.OpenContent(factory, content, DialogTag, kind, key); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("WorldRestore: the " + kind + " dialog could not open: " + e.Message);
            return null;
        }
    }

    private static TimeZoneInfo Safe(Func<TimeZoneInfo> zone)
    {
        try { return zone() ?? TimeZoneInfo.Local; }
        catch { return TimeZoneInfo.Local; }
    }

    /// <summary>00:00 of <paramref name="day"/> in the server's zone, as ISO UTC (the picker's value).</summary>
    internal static string IsoOf(DateTime day)
    {
        var zone = Safe(ServerZone);
        var local = DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified);
        DateTime utc;
        try { utc = TimeZoneInfo.ConvertTimeToUtc(local, zone); }
        catch (ArgumentException) { utc = TimeZoneInfo.ConvertTimeToUtc(local.AddHours(1), zone); }
        return utc.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>The server-zone day of a picker value (ISO UTC), or null.</summary>
    internal static DateTime? DayOf(string? isoUtc)
    {
        if (string.IsNullOrWhiteSpace(isoUtc)) return null;
        if (!DateTime.TryParse(isoUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
            return null;
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Safe(ServerZone)).Date;
    }

    /// <summary>Readback <c>worldBackups</c>: the summary, the pending request, the last result and the entries.</summary>
    internal static Dictionary<string, object?> Readback() => ReadbackOf(_state);

    internal static Dictionary<string, object?> ReadbackOf(ServerBackupsState? s)
    {
        var entries = new List<object?>();
        if (s != null)
            foreach (var e in s.Entries)
                entries.Add(new Dictionary<string, object?>
                {
                    ["id"] = e.Id, ["kind"] = e.Kind, ["name"] = e.Name, ["created"] = e.Created, ["startDate"] = e.StartDate,
                    ["ageDays"] = e.AgeDays, ["size"] = e.Size, ["restorable"] = e.Restorable, ["reason"] = e.Reason, ["shape"] = e.Shape,
                    ["backupKind"] = e.BackupKind, ["trigger"] = e.Trigger,
                });
        return new Dictionary<string, object?>
        {
            ["known"] = s != null,
            ["summary"] = ServerBackups.Summary(s),
            ["count"] = s?.Count ?? 0,
            ["more"] = s?.More ?? 0,
            ["disk"] = s == null ? "" : ServerBackups.DiskLine(s),
            ["retention"] = s == null ? "" : ServerBackups.RetentionLine(s),
            ["keep"] = s?.Keep,
            ["restoreAllowed"] = s?.RestoreAllowed ?? false,
            ["restoreRefusal"] = s?.RestoreRefusal ?? "",
            ["nextInterval"] = s?.NextInterval,
            ["nextDay"] = s?.NextDay,
            ["intervalWritable"] = s?.IntervalWritable ?? false,
            ["intervalReason"] = s?.IntervalReason ?? "",
            ["serverNow"] = s?.ServerNow ?? "",
            ["online"] = s?.Online ?? 0,
            ["pending"] = s == null || s.PendingId.Length == 0 ? null : new Dictionary<string, object?>
            {
                ["id"] = s.PendingId, ["name"] = s.PendingName, ["requestedUtc"] = s.PendingRequestedUtc, ["wipe"] = s.PendingWipe,
                ["wipeDate"] = s.PendingWipeDate, ["restartNow"] = s.PendingRestartNow, ["line"] = ServerBackups.PendingLine(s),
            },
            ["countdown"] = s?.Countdown ?? -1,
            ["result"] = s == null || s.ResultId.Length == 0 ? null : new Dictionary<string, object?>
            {
                ["id"] = s.ResultId, ["ok"] = s.ResultOk, ["summary"] = s.ResultSummary, ["finishedUtc"] = s.ResultFinishedUtc,
            },
            ["last"] = LastAnswer,
            // BACKUPSDLG1: the names the dialogs show, the server session and the last pending-change push.
            ["serverName"] = ServerName(),
            ["saveName"] = WorldName(s),
            ["session"] = _session,
            ["lastPush"] = _lastPush,
            ["entries"] = entries,
            // BACKUPNOW1
            ["backups"] = s == null ? null : new Dictionary<string, object?>
            {
                ["allowed"] = s.BackupAllowed, ["refusal"] = s.BackupRefusal, ["where"] = s.BackupsWhere, ["kinds"] = ServerBackups.KindsLine(s),
                ["manualBytes"] = s.ManualBytes, ["scheduledBytes"] = s.ScheduledBytes,
                ["atStart"] = s.AtStart, ["everyHours"] = s.EveryHours, ["keepCount"] = s.KeepCount, ["keepDays"] = s.KeepDays,
                ["schedule"] = ServerBackups.ScheduleLine(s), ["nextScheduled"] = s.NextScheduled, ["retention"] = ServerBackups.BackupRetentionLine(s),
                ["job"] = s.JobRunning ? new Dictionary<string, object?> { ["kind"] = s.JobKind, ["by"] = s.JobBy, ["phase"] = s.JobPhase } : null,
                ["lastBackup"] = s.LastBackupSummary.Length == 0 ? null : new Dictionary<string, object?>
                {
                    ["ok"] = s.LastBackupOk, ["summary"] = s.LastBackupSummary, ["finished"] = s.LastBackupFinished,
                },
                ["line"] = ServerBackups.BackupLine(s),
                ["scheduleFailure"] = s.ScheduleFailure.Length == 0 ? null : new Dictionary<string, object?> { ["summary"] = s.ScheduleFailure, ["at"] = s.ScheduleFailureAt },
                ["pruneNext"] = PruneList(s.PruneNextBackup),
                ["autosaves"] = !s.AutosaveKnown ? null : new Dictionary<string, object?>
                {
                    ["count"] = s.AutosaveCount, ["bytes"] = s.AutosaveBytes, ["newest"] = s.AutosaveNewest, ["oldest"] = s.AutosaveOldest,
                    ["keep"] = s.AutosaveKeep, ["everySeconds"] = s.AutosaveEvery, ["line"] = ServerBackups.AutosaveLine(s),
                    ["span"] = ServerBackups.AutosaveSpan(s),
                },
            },
        };
    }

    private static List<object?> PruneList(IEnumerable<ServerBackupView> list)
    {
        var l = new List<object?>();
        foreach (var e in list) l.Add(new Dictionary<string, object?> { ["id"] = e.Id, ["name"] = e.Name });
        return l;
    }

    /// <summary>Tests and teardown.</summary>
    internal static void Reset()
    {
        _state = null;
        _lastAsk = double.NegativeInfinity;
        LastAnswer = "";
        _openSignature = "";
        _lastPush = "";
        _dateDialog = null;
        _dateLatest = "";
        ServerNameProbe = null;
        DialogFactory = null;
        ServerZone = () => TimeZoneInfo.Local;
        Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>Tests: set the state as an answer would.</summary>
    internal static void SetStateForTests(ServerBackupsState? s) => _state = s;
}
