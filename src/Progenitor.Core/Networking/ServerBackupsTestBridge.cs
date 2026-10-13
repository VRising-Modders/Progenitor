using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.WorldRestore;

namespace Progenitor.Networking;

/// <summary>
/// WORLDRESTORE1: the Core half of the dev bridge's <c>world_backups</c> op (Dev reaches it by reflection). Server: list the
/// restore points (with their paths), plan or write a request (<c>dryRun</c> by default), read the pending request and the
/// last result, cancel. Client: the same through the page's own request path (the admin path the server audits), open the
/// Backups list, and set the wipe-date dialog's day. The dialogs themselves are driven with <c>mods_ui dialog_choose</c>,
/// <c>dialog_type</c> and <c>widget_click</c>.
/// </summary>
internal static class ServerBackupsTestBridge
{
    // ---- server -----------------------------------------------------------------------------

    private static void RequireServer()
    {
#if !PROGENITOR_TESTS
        if (!Plugin.IsServerProcess) throw new InvalidOperationException("this world_backups action runs on the server side");
#endif
    }

    private static ServerRulesFacts Facts()
    {
        var facts = ServerRulesServer.FactsProvider?.Invoke() ?? new ServerRulesFacts();
        facts.LockedBy ??= ServerSettingsLock.LockedBy;
        return facts;
    }

    public static Dictionary<string, object?> ServerList()
    {
        RequireServer();
        var state = ServerBackupsServer.BuildState(Facts(), out var all);
        var d = Describe(state);
        var paths = new List<object?>();
        foreach (var e in all) paths.Add(new Dictionary<string, object?> { ["id"] = e.Id, ["path"] = e.Path, ["files"] = e.Files });
        d["paths"] = paths;
        ServerBackupsServer.TryLayout(out var layout, out string folder, out string refusal);
        d["saveFolder"] = folder;
        d["backupsRoot"] = string.IsNullOrEmpty(layout.SaveFolder) ? "" : layout.BackupsRoot;
        d["layoutRefusal"] = refusal;
        d["side"] = "server";
        return d;
    }

    public static Dictionary<string, object?> ServerRequest(string id, string wipe, string date, bool restartNow, bool dryRun)
    {
        RequireServer();
        var request = new ServerRulesRequest
        {
            Op = ServerRulesRequest.OpRestore, Key = id ?? "", Wipe = wipe ?? "", Value = date ?? "", RestartNow = restartNow, DryRun = dryRun,
        };
        var outcome = ServerBackupsServer.Restore(request, Facts(), "dev bridge", out var written);
        if (written != null) Plugin.Logger?.LogInfo("WorldRestore: request " + written.RequestId + " written by the dev bridge.");
        return new Dictionary<string, object?>
        {
            ["ok"] = outcome.Ok, ["error"] = outcome.Error, ["note"] = outcome.Note, ["dryRun"] = dryRun,
            ["written"] = written == null ? null : RequestDict(written),
            ["side"] = "server",
        };
    }

    public static Dictionary<string, object?> ServerPending()
    {
        RequireServer();
        string root = ServerBackupsServer.BepInExRootProbe?.Invoke() ?? "";
        var pending = root.Length == 0 ? null : WorldRestoreMarker.ReadPending(ServerBackupsServer.Fs, root, out string error);
        return new Dictionary<string, object?>
        {
            ["ok"] = true, ["side"] = "server", ["path"] = root.Length == 0 ? "" : WorldRestoreMarker.RequestPath(root),
            ["pending"] = pending == null ? null : RequestDict(pending),
            ["countdown"] = ServerBackupsServer.CountdownProbe?.Invoke() ?? -1,
        };
    }

    public static Dictionary<string, object?> ServerResult()
    {
        RequireServer();
        string root = ServerBackupsServer.BepInExRootProbe?.Invoke() ?? "";
        var r = root.Length == 0 ? null : WorldRestoreMarker.ReadResult(ServerBackupsServer.Fs, root);
        object? result = null;
        if (r != null)
        {
            var steps = new List<object?>();
            foreach (var s in r.Steps) steps.Add(new Dictionary<string, object?> { ["name"] = s.Name, ["ok"] = s.Ok, ["detail"] = s.Detail, ["ms"] = s.Ms });
            result = new Dictionary<string, object?>
            {
                ["requestId"] = r.RequestId, ["ok"] = r.Ok, ["summary"] = r.Summary, ["reason"] = r.Reason, ["sourceName"] = r.SourceName,
                ["saveFolder"] = r.SaveFolder, ["backupPath"] = r.BackupPath, ["backupName"] = r.BackupName, ["wipeMode"] = r.WipeMode,
                ["wipeApplied"] = r.WipeApplied, ["warnings"] = new List<string>(r.Warnings), ["pruned"] = new List<string>(r.Pruned),
                ["startedUtc"] = WorldJson.Utc(r.StartedUtc), ["finishedUtc"] = WorldJson.Utc(r.FinishedUtc), ["steps"] = steps,
            };
        }

        return new Dictionary<string, object?>
        {
            ["ok"] = true, ["side"] = "server", ["path"] = root.Length == 0 ? "" : WorldRestoreMarker.ResultPath(root), ["result"] = result,
        };
    }

    public static Dictionary<string, object?> ServerCancel()
    {
        RequireServer();
        var outcome = ServerBackupsServer.Cancel();
        return new Dictionary<string, object?> { ["ok"] = outcome.Ok, ["error"] = outcome.Error, ["note"] = outcome.Note, ["side"] = "server" };
    }

    /// <summary>
    /// BACKUPNOW1 server <c>backup_now</c>: the server's own checks; <paramref name="dryRun"/> (the default) writes nothing, else the
    /// world save and the copy start (as an admin's Back up now, by "dev bridge"; read the progress with <c>list</c> or
    /// <c>schedule</c>).
    /// </summary>
    public static Dictionary<string, object?> ServerBackupNow(bool dryRun)
    {
        RequireServer();
        var outcome = ServerBackupsServer.BackupNow(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow, DryRun = dryRun }, "dev bridge", null);
        return new Dictionary<string, object?>
        {
            ["ok"] = outcome.Ok, ["error"] = outcome.Error, ["note"] = outcome.Note, ["dryRun"] = dryRun, ["side"] = "server",
            ["job"] = JobDict(),
        };
    }

    /// <summary>BACKUPNOW1 server <c>schedule</c>: the settings, the next due time, the running job, the last results of each trigger.</summary>
    public static Dictionary<string, object?> ServerSchedule()
    {
        RequireServer();
        var settings = ServerBackupsServer.BackupSettings();
        ServerBackupsServer.TryLayout(out var layout, out _, out _);
        List<WorldBackupEntry>? all = null;
        if (!string.IsNullOrEmpty(layout.SaveFolder))
        {
            try { all = WorldBackupCatalog.List(ServerBackupsServer.Fs, layout, ServerBackupsServer.ZoneNow()); }
            catch { all = null; }
        }

        var due = ServerBackupsJob.NextDueUtc(settings, all);
        string root = ServerBackupsServer.BepInExRootProbe?.Invoke() ?? "";
        var status = new Dictionary<string, object?>();
        foreach (var (k, v) in WorldBackupStatus.Read(ServerBackupsServer.Fs, root))
            status[k] = new Dictionary<string, object?> { ["ok"] = v.Ok, ["summary"] = v.Summary, ["name"] = v.Name, ["by"] = v.By, ["finishedUtc"] = WorldJson.Utc(v.FinishedUtc) };
        return new Dictionary<string, object?>
        {
            ["ok"] = true, ["side"] = "server",
            ["atStart"] = settings.AtStart, ["everyHours"] = settings.EveryHours, ["keepCount"] = settings.KeepCount, ["keepDays"] = settings.KeepDays,
            ["schedule"] = settings.ScheduleText(),
            ["nextDueUtc"] = due is { } d ? WorldJson.Utc(d) : null,
            ["lastScheduledUtc"] = all == null ? null : WorldBackupSchedule.LastScheduledUtc(all) is { } l ? WorldJson.Utc(l) : null,
            ["job"] = JobDict(),
            ["last"] = ServerBackupsJob.Last is { } last ? OutcomeDict(last) : null,
            ["status"] = status,
            ["statusPath"] = root.Length == 0 ? "" : WorldBackupStatus.PathOf(root),
        };
    }

    /// <summary>
    /// BACKUPNOW1 server <c>retention</c>: what the manual/scheduled policy deletes now, after the next manual and the next
    /// scheduled backup, and what the next restore's keep count deletes (nothing is deleted here).
    /// </summary>
    public static Dictionary<string, object?> ServerRetention()
    {
        RequireServer();
        var settings = ServerBackupsServer.BackupSettings();
        var state = ServerBackupsServer.BuildState(Facts(), out var all);
        string root = ServerBackupsServer.BepInExRootProbe?.Invoke() ?? "";
        string pending = root.Length > 0 && WorldRestoreMarker.ReadPending(ServerBackupsServer.Fs, root, out _) is { } p ? p.SourcePath : "";
        var now = ServerBackupsServer.NowUtc();
        List<object?> Names(IEnumerable<WorldBackupEntry> list)
        {
            var l = new List<object?>();
            foreach (var e in list) l.Add(new Dictionary<string, object?> { ["id"] = e.Id, ["name"] = e.DisplayName, ["folder"] = e.FolderName });
            return l;
        }

        var restore = new List<object?>();
        foreach (var e in state.PruneNext) restore.Add(new Dictionary<string, object?> { ["id"] = e.Id, ["name"] = e.Name });
        return new Dictionary<string, object?>
        {
            ["ok"] = true, ["side"] = "server",
            ["policy"] = settings.Policy.Text(), ["keepCount"] = settings.Policy.KeepCount, ["keepDays"] = settings.Policy.KeepDays,
            ["now"] = Names(WorldBackupRetention.PruneList(all, settings.Policy, now, pending)),
            ["afterManual"] = Names(WorldBackupRetention.NextDeletes(all, settings.Policy, now, WorldRestorePaths.KindManual, ServerBackupsServer.ZoneNow(), pending)),
            ["afterScheduled"] = Names(WorldBackupRetention.NextDeletes(all, settings.Policy, now, WorldRestorePaths.KindScheduled, ServerBackupsServer.ZoneNow(), pending)),
            ["restoreKeep"] = state.Keep,
            ["afterRestore"] = restore,
            ["protectedPendingSource"] = pending,
        };
    }

    private static Dictionary<string, object?>? JobDict()
    {
        if (ServerBackupsJob.Current is not { } run) return null;
        var p = run.Job.Progress;
        return new Dictionary<string, object?>
        {
            ["kind"] = run.Kind, ["trigger"] = run.Trigger, ["by"] = run.By, ["phase"] = ServerBackupsJob.Phase(run), ["step"] = p.Phase,
            ["attempt"] = p.Attempt, ["filesDone"] = p.FilesDone, ["filesTotal"] = p.FilesTotal, ["bytesDone"] = p.BytesDone, ["bytesTotal"] = p.BytesTotal,
            ["startedUtc"] = WorldJson.Utc(run.StartedUtc),
        };
    }

    internal static Dictionary<string, object?> OutcomeDict(BackupOutcome o) => new()
    {
        ["ok"] = o.Ok, ["summary"] = o.Summary, ["reason"] = o.Reason, ["kind"] = o.Kind, ["trigger"] = o.Trigger, ["path"] = o.BackupPath,
        ["folder"] = o.FolderName, ["name"] = o.Name, ["bytes"] = o.Bytes, ["files"] = o.Files, ["attempts"] = o.Attempts,
        ["warnings"] = new List<string>(o.Warnings), ["pruned"] = new List<string>(o.Pruned),
        ["startedUtc"] = WorldJson.Utc(o.StartedUtc), ["finishedUtc"] = WorldJson.Utc(o.FinishedUtc),
    };

    internal static Dictionary<string, object?> RequestDict(RestoreRequest r) => new()
    {
        ["requestId"] = r.RequestId, ["requestedUtc"] = WorldJson.Utc(r.RequestedUtc), ["saveFolder"] = r.SaveFolder, ["saveName"] = r.SaveName,
        ["sourceKind"] = r.SourceKind, ["sourcePath"] = r.SourcePath, ["sourceName"] = r.SourceName, ["expectShape"] = r.ExpectShape,
        ["expectStartDate"] = r.ExpectStartDate, ["wipeMode"] = r.WipeMode, ["wipeIntervalDays"] = r.WipeIntervalDays,
        ["wipeDayOfReset"] = r.WipeDayOfReset, ["hostSettingsPath"] = r.HostSettingsPath, ["wipeDate"] = r.WipeDate,
        ["keepBackups"] = r.KeepBackups, ["restartNow"] = r.RestartNow,
    };

    internal static Dictionary<string, object?> Describe(ServerBackupsState s)
    {
        var d = ServerBackupsRows.ReadbackOf(s);
        d["ok"] = true;
        return d;
    }

    // ---- client -----------------------------------------------------------------------------

    public static Dictionary<string, object?> Read()
    {
        var d = ServerBackupsRows.Readback();
        d["ok"] = true;
        d["side"] = "client";
        d["shown"] = ServerRulesRows.Supported() && ServerRulesRows.CanEdit();
#if !PROGENITOR_TESTS
        d["answers"] = ServerRulesClient.Answers;
        d["pending_requests"] = ServerRulesClient.PendingCount;
#endif
        return d;
    }

    public static Dictionary<string, object?> List()
    {
        string? refusal = ServerBackupsRows.Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }, null);
        var d = Read();
        d["result"] = refusal == null ? "sent backups" : "not sent: " + refusal;
        return d;
    }

    public static Dictionary<string, object?> Request(string id, string wipe, string date, bool restartNow, bool dryRun)
    {
        string result = "sent";
        var request = new ServerRulesRequest
        {
            Op = ServerRulesRequest.OpRestore, Key = id ?? "", Wipe = wipe ?? "", Value = date ?? "", RestartNow = restartNow, DryRun = dryRun,
            Shape = ServerBackupsRows.State?.Find(id)?.Shape ?? "",
        };
        string? refusal = ServerBackupsRows.Ask(request, r => result = r == null ? "no answer" : (r.Ok ? "ok: " + r.Note : "refused: " + r.Error));
        var d = Read();
        d["result"] = refusal == null ? result : "not sent: " + refusal;
        return d;
    }

    public static Dictionary<string, object?> Cancel()
    {
        string result = "sent";
        string? refusal = ServerBackupsRows.Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestoreCancel },
            r => result = r == null ? "no answer" : (r.Ok ? "ok: " + r.Note : "refused: " + r.Error));
        var d = Read();
        d["result"] = refusal == null ? result : "not sent: " + refusal;
        return d;
    }

    /// <summary>BACKUPNOW1 client <c>backup_now</c>: through the page's own request (the admin path the server audits).</summary>
    public static Dictionary<string, object?> BackupNow(bool dryRun)
    {
        string result = "sent";
        string? refusal = ServerBackupsRows.Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow, DryRun = dryRun },
            r => result = r == null ? "no answer" : (r.Ok ? "ok: " + r.Note : "refused: " + r.Error));
        var d = Read();
        d["result"] = refusal == null ? result : "not sent: " + refusal;
        return d;
    }

    /// <summary>BACKUPNOW1 client <c>set</c>: one backup setting (<c>key</c> BackupAtStart / BackupEveryHours / BackupKeepCount / BackupKeepDays).</summary>
    public static Dictionary<string, object?> Set(string key, string value)
    {
        string result = "sent";
        string? refusal = ServerBackupsRows.Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsSet, Key = key ?? "", Value = value ?? "" },
            r => result = r == null ? "no answer" : (r.Ok ? "ok: " + r.Note : "refused: " + r.Error));
        var d = Read();
        d["result"] = refusal == null ? result : "not sent: " + refusal;
        return d;
    }

    /// <summary>BACKUPNOW1 client <c>open_backup_now</c>: the Back up now confirmation as the button opens it.</summary>
    public static Dictionary<string, object?> OpenBackupNow()
    {
        var dialog = ServerBackupsRows.ShowBackupNow();
        var d = Read();
        d["result"] = dialog != null ? "opened" : "not opened: " + (ServerBackups.BackupNowRefusal(ServerBackupsRows.State) ?? "the dialogs are not available");
        return d;
    }

    /// <summary>Open the Backups list as the Show button does (it asks the server first).</summary>
    public static Dictionary<string, object?> Open()
    {
        string? refusal = ServerBackupsRows.OpenList();
        var d = Read();
        d["result"] = refusal == null ? "asked; the list opens on the answer" : "not opened: " + refusal;
        return d;
    }

    public static Dictionary<string, object?> DialogDate(string date)
    {
        string iso = ServerBackupsRows.SetDialogDate(date);
        var d = Read();
        d["result"] = "date set: " + iso;
        return d;
    }
}
