using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Install;
using Progenitor.WorldRestore;

namespace Progenitor.Networking;

/// <summary>
/// WORLDRESTORE1, server: the seams of the Backups list and the restore request (game-free; live values from
/// <c>ServerBackupsGame</c>). The request file is the only thing a restore writes while the game runs: Bootstrap applies
/// it at the next start (<see cref="WorldRestoreMarker"/>).
/// </summary>
internal static class ServerBackupsServer
{
    internal static IWorldFs Fs { get; set; } = PhysicalWorldFs.Instance;

    /// <summary>The save folder the running game uses (live: <c>ModSave.CurrentSaveDirectory</c>, else the host settings' folder).</summary>
    internal static Func<string?>? SaveFolderProbe { get; set; }

    internal static Func<string?>? BepInExRootProbe { get; set; }

    /// <summary>The <c>[WorldRestore] KeepBackups</c> value (live: the cfg entry).</summary>
    internal static Func<int>? KeepProbe { get; set; }

    /// <summary>Store a new keep count; null when stored, else why not.</summary>
    internal static Func<int, string?>? KeepWrite { get; set; }

    /// <summary>BACKUPNOW1: the backup settings of <c>[WorldRestore]</c> (live: the cfg entries).</summary>
    internal static Func<WorldBackupSettings>? BackupSettingsProbe { get; set; }

    /// <summary>BACKUPNOW1: store one backup setting (key, invariant value); null when stored, else why not.</summary>
    internal static Func<string, string, string?>? BackupSettingWrite { get; set; }

    internal static Func<int>? OnlineProbe { get; set; }
    internal static Func<string?>? ProcessPathProbe { get; set; }
    internal static Func<IReadOnlyList<string>>? ArgsProbe { get; set; }
    internal static Func<DateTime> NowLocal { get; set; } = () => DateTime.Now;
    internal static Func<DateTime> NowUtc { get; set; } = () => DateTime.UtcNow;

    /// <summary>"Restore and restart now": start the countdown that stops the server. Null when started, else why not.</summary>
    internal static Func<string, string?>? StartRestartCountdown { get; set; }

    /// <summary>Seconds left of the countdown, or -1.</summary>
    internal static Func<int>? CountdownProbe { get; set; }

    internal static Action? CancelCountdown { get; set; }

    internal static readonly Random Ids = new();

    /// <summary>
    /// BACKUPSDLG1: the pending restore request or its countdown changed (a request written, cancelled, a countdown that ended
    /// without a stop), whoever caused it: an admin's dialog, the dev bridge, another admin. Live: the Server rules channel tells
    /// the admins who read the Backups, so an open dialog asks again instead of showing the old request. Main thread.
    /// </summary>
    internal static event Action<BackupsPendingChange>? PendingChanged;

    /// <summary>Raise <see cref="PendingChanged"/> with the request and countdown as they are now.</summary>
    internal static void RaisePendingChanged(string why)
    {
        var handlers = PendingChanged;
        if (handlers == null) return;
        string id = "";
        try
        {
            string? root = Safe(BepInExRootProbe, null);
            if (!string.IsNullOrEmpty(root)) id = WorldRestoreMarker.ReadPending(Fs, root!, out _)?.RequestId ?? "";
        }
        catch { id = ""; }

        var change = new BackupsPendingChange(id, Safe(CountdownProbe, -1) >= 0, why ?? "");
        foreach (Action<BackupsPendingChange> handler in handlers.GetInvocationList())
        {
            try { handler(change); }
            catch (Exception e) { Plugin.Logger?.LogWarning("WorldRestore: a pending-change handler threw: " + e.Message); }
        }
    }

    /// <summary>BACKUPSDLG1: the running save's name (the world a restore replaces), or empty.</summary>
    internal static string SaveNameNow()
    {
        if (TryLayout(out var layout, out string folder, out _)) return layout.SaveName ?? "";
        return FolderName(folder);
    }

    private static string FolderName(string? folder)
    {
        try { return string.IsNullOrEmpty(folder) ? "" : System.IO.Path.GetFileName(folder!.TrimEnd('\\', '/')) ?? ""; }
        catch { return ""; }
    }

    /// <summary>"world world1" / "the world" (BACKUPSDLG1: the notes name the world they act on).</summary>
    internal static string WorldWords(string? saveName) => string.IsNullOrWhiteSpace(saveName) ? "the world" : "world " + saveName!.Trim();

    internal static int Keep()
    {
        try { return WorldBackupCatalog.ClampKeep(KeepProbe?.Invoke() ?? WorldBackupCatalog.DefaultKeep); }
        catch { return WorldBackupCatalog.DefaultKeep; }
    }

    internal static WorldBackupSettings BackupSettings()
    {
        try { return BackupSettingsProbe?.Invoke() ?? new WorldBackupSettings(); }
        catch { return new WorldBackupSettings(); }
    }

    /// <summary>The lock list's entry that locks <paramref name="settingId"/>, or null.</summary>
    internal static string? LockedBy(string settingId) => Safe(() => ServerSettingsLock.LockedBy(settingId), null);

    private static T Safe<T>(Func<T>? f, T fallback)
    {
        if (f == null) return fallback;
        try { return f(); }
        catch { return fallback; }
    }

    /// <summary>What the restore needs from the Server rules facts.</summary>
    internal readonly record struct WipeFacts(int Interval, string Day, bool Writable, string Reason, string HostSettingsPath, List<string> Layers);

    internal static WipeFacts WipeFactsOf(ServerRulesFacts facts)
    {
        var state = ServerRulesStateBuilder.Build(facts);
        var row = state.Row(ServerRules.ResetDaysInterval);
        int interval = ServerRulesStateBuilder.Int(row?.Next) ?? 0;
        string day = state.Row(ServerRules.DayOfReset)?.Next ?? "Any";
        string path = facts.Host?.WriteTarget?.Path ?? "";
        string reason = "";
        if (row == null) reason = "the server's wipe settings are not known.";
        else if (row.LockedBy.Length > 0) reason = ServerSettingsLock.NoteFor(row.LockedBy);
        else if (row.Override.Length > 0) reason = "the start command sets it (" + row.Override + "), so a settings file cannot change it.";
        else if (!row.Editable) reason = row.Reason.Length > 0 ? row.Reason : "it cannot be changed here.";
        else if (path.Length == 0) reason = ServerRulesStateBuilder.NoFileReason;
        else if (!UnderServerOrPersist(path, facts.Host?.PersistentDataPath))
            reason = "its settings file (" + path + ") is not under this server's folder or its persistent data.";
        var layers = new List<string>();
        if (facts.Host != null)
            foreach (var layer in facts.Host.Layers)
                if (!string.IsNullOrEmpty(layer.Path)) layers.Add(layer.Path);
        return new WipeFacts(interval, day.Length > 0 ? day : "Any", reason.Length == 0, reason, path, layers);
    }

    /// <summary>M7: a host settings file the restore may write lies under the server's folder or the persistent-data root.</summary>
    internal static bool UnderServerOrPersist(string path, string? persist)
    {
        string? root = Safe(BepInExRootProbe, null);
        string server = "";
        try { server = string.IsNullOrEmpty(root) ? "" : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(root!).TrimEnd('\\', '/')) ?? ""; }
        catch { server = ""; }
        return (!string.IsNullOrEmpty(persist) && WorldRestorePaths.IsUnder(path, persist)) || (server.Length > 0 && WorldRestorePaths.IsUnder(path, server));
    }

    /// <summary>The save folder as a layout, or why it cannot be restored here (the live-server fence first).</summary>
    internal static bool TryLayout(out WorldRestorePaths.SaveLayout layout, out string folder, out string refusal)
    {
        layout = default;
        refusal = "";
        folder = Safe(SaveFolderProbe, null) ?? "";
        string process = Safe(ProcessPathProbe, null) ?? "";
        var args = Safe(ArgsProbe, (IReadOnlyList<string>)Array.Empty<string>());
        if (WorldRestorePaths.IsLiveServerPath(folder) || WorldRestorePaths.IsLiveServerPath(process) || WorldRestorePaths.IsLiveServerCommandLine(args)
            || WorldRestorePaths.IsLiveServerPath(Safe(BepInExRootProbe, null)))
        {
            refusal = "World restores never run on the live server path (" + WorldRestorePaths.ForbiddenLiveServerRoot + ").";
            return false;
        }

        if (folder.Length == 0)
        {
            refusal = "The save folder of this server is not known yet.";
            return false;
        }

        if (!WorldRestorePaths.TryLayout(folder, out layout, out string why))
        {
            refusal = why;
            return false;
        }

        // L4: a hosted private game is listed, never restored (the hosted flow is phase 3).
        if (WorldRestorePaths.IsHostedGame(args, folder))
        {
            refusal = WorldRestorePaths.HostedRefusal;
            return false;
        }

        return true;
    }

    /// <summary>The Backups state now: the restore points, sizes, the pending request, the last result.</summary>
    internal static ServerBackupsState BuildState(ServerRulesFacts? facts, out List<WorldBackupEntry> all)
    {
        all = new List<WorldBackupEntry>();
        var now = NowLocal();
        var s = new ServerBackupsState
        {
            Keep = Keep(),
            ServerNow = now.ToString(ServerBackups.LocalFormat, CultureInfo.InvariantCulture),
            Online = Safe(OnlineProbe, 0),
            Countdown = Safe(CountdownProbe, -1),
        };
        s.KeepLockedBy = Safe(() => ServerSettingsLock.LockedBy(ServerBackups.KeepSettingId), null) ?? "";
        if (facts != null)
        {
            var w = WipeFactsOf(facts);
            s.NextInterval = w.Interval;
            s.NextDay = w.Day;
            s.IntervalWritable = w.Writable;
            s.IntervalReason = w.Reason;
        }

        // BACKUPNOW1: the game's autosave rotation as the server's host settings set it (an argument or variable first).
        if (facts?.Host != null)
        {
            try
            {
                s.AutosaveKeep = facts.Host.EffectiveInt("AutoSaveCount") ?? -1;
                s.AutosaveEvery = facts.Host.EffectiveInt("AutoSaveInterval") ?? -1;
            }
            catch { /* unknown */ }
        }

        bool ok = TryLayout(out var layout, out string saveFolder, out string refusal);
        // BACKUPSDLG1: the dialogs name the world they act on.
        s.SaveName = ok ? layout.SaveName ?? "" : FolderName(saveFolder);
        string? root = Safe(BepInExRootProbe, null);
        if (ok && string.IsNullOrEmpty(root))
        {
            ok = false;
            refusal = "The BepInEx folder of this server is not known.";
        }

        string? locked = Safe(() => ServerSettingsLock.LockedBy(ServerBackups.RestoreSettingId), null);
        if (ok && locked != null)
        {
            ok = false;
            refusal = ServerSettingsLock.NoteFor(locked);
        }

        s.RestoreAllowed = ok;
        s.RestoreRefusal = refusal;
        if (layout.SaveFolder != null && layout.SaveFolder.Length > 0)
        {
            try { all = WorldBackupCatalog.List(Fs, layout); }
            catch (Exception e) { Plugin.Logger?.LogWarning("WorldRestore: listing the restore points threw: " + e.Message); }
            try { s.CurrentBytes = WorldManifest.TotalBytes(WorldManifest.Build(Fs, layout.SaveFolder, hash: false)); }
            catch { s.CurrentBytes = 0; }
            s.FreeBytes = Fs.FreeBytes(layout.PersistRoot) ?? -1;
            var autosaves = WorldAutosaves.Summarize(Fs, layout.SaveFolder);
            s.AutosaveKnown = !autosaves.Missing;
            s.AutosaveCount = autosaves.Count;
            s.AutosaveBytes = autosaves.Bytes;
            s.AutosaveNewest = autosaves.NewestUtc?.ToLocalTime().ToString(ServerBackups.LocalFormat, CultureInfo.InvariantCulture) ?? "";
            s.AutosaveOldest = autosaves.OldestUtc?.ToLocalTime().ToString(ServerBackups.LocalFormat, CultureInfo.InvariantCulture) ?? "";
        }

        // M1: what the next restore's retention would delete (its new backup counted, the pending request's source kept).
        if (all.Count > 0 && layout.SaveFolder != null && layout.SaveFolder.Length > 0)
        {
            string chain = "(next)";
            foreach (var e in all)
                if (e.IsRestoreChain) { chain = e.ChainId; break; }
            var imagined = new List<WorldBackupEntry>(all)
            {
                new() { Kind = WorldBackupEntry.KindBackup, FolderName = "(next)", Path = System.IO.Path.Combine(layout.BackupsRoot, "(next)"), CreatedLocal = now.AddYears(1), ChainId = chain },
            };
            string pendingSource = "";
            if (!string.IsNullOrEmpty(root) && WorldRestoreMarker.ReadPending(Fs, root!, out _) is { } p) pendingSource = p.SourcePath;
            foreach (var e in WorldBackupCatalog.PruneList(imagined, s.Keep, imagined[imagined.Count - 1].Path, pendingSource))
                s.PruneNext.Add(new ServerBackupView { Id = e.Id, Name = e.DisplayName });
        }

        s.Count = all.Count;
        s.ArchiveBytes = WorldBackupCatalog.Bytes(all, WorldBackupEntry.KindArchive);
        s.BackupBytes = WorldBackupCatalog.Bytes(all, WorldBackupEntry.KindBackup);
        // BACKUPNOW1: the newest entries, but every kind keeps its newest (an hourly schedule never hides a manual backup).
        foreach (var e in ServerBackups.PickNewest(all, ServerBackups.GroupOf, ServerBackups.MaxEntries)) s.Entries.Add(View(e));
        s.More = Math.Max(0, all.Count - s.Entries.Count);
        FillBackups(s, all, root);

        if (!string.IsNullOrEmpty(root))
        {
            var pending = WorldRestoreMarker.ReadPending(Fs, root!, out _);
            if (pending != null)
            {
                s.PendingId = pending.RequestId;
                s.PendingName = pending.SourceName;
                s.PendingRequestedUtc = WorldJson.Utc(pending.RequestedUtc);
                s.PendingWipe = pending.WipeMode;
                s.PendingWipeDate = pending.WipeDate;
                s.PendingRestartNow = pending.RestartNow;
            }

            var result = WorldRestoreMarker.ReadResult(Fs, root!);
            if (result != null)
            {
                s.ResultId = result.RequestId.Length > 0 ? result.RequestId : "unreadable";
                s.ResultOk = result.Ok;
                s.ResultSummary = result.Summary;
                s.ResultFinishedUtc = WorldJson.Utc(result.FinishedUtc);
            }
        }

        return s;
    }

    /// <summary>BACKUPNOW1: the manual and scheduled backup part of the state.</summary>
    private static void FillBackups(ServerBackupsState s, List<WorldBackupEntry> all, string? root)
    {
        s.ManualBytes = WorldBackupCatalog.BackupBytes(all, WorldRestorePaths.KindManual);
        s.ScheduledBytes = WorldBackupCatalog.BackupBytes(all, WorldRestorePaths.KindScheduled);
        s.PreUpdateBytes = WorldBackupCatalog.BackupBytes(all, WorldRestorePaths.KindPreUpdate);
        var settings = BackupSettings();
        s.AtStart = settings.AtStart;
        s.EveryHours = WorldBackupSettings.ClampHours(settings.EveryHours);
        s.KeepCount = BackupPolicy.ClampCount(settings.KeepCount);
        s.KeepDays = BackupPolicy.ClampDays(settings.KeepDays);
        s.BackupNowLockedBy = LockedBy(ServerBackups.BackupNowSettingId) ?? "";
        s.AtStartLockedBy = LockedBy(ServerBackups.AtStartSettingId) ?? "";
        s.EveryHoursLockedBy = LockedBy(ServerBackups.EveryHoursSettingId) ?? "";
        s.KeepCountLockedBy = LockedBy(ServerBackups.KeepCountSettingId) ?? "";
        s.KeepDaysLockedBy = LockedBy(ServerBackups.KeepDaysSettingId) ?? "";

        bool ok = TryLayout(out var live, out _, out string refusal);
        if (ok && string.IsNullOrEmpty(root))
        {
            ok = false;
            refusal = "The BepInEx folder of this server is not known.";
        }

        if (ok && s.BackupNowLockedBy.Length > 0)
        {
            ok = false;
            refusal = ServerSettingsLock.NoteFor(s.BackupNowLockedBy);
        }

        s.BackupAllowed = ok;
        s.BackupRefusal = refusal;
        if (!string.IsNullOrEmpty(live.SaveFolder))
            s.BackupsWhere = System.IO.Path.Combine(WorldRestorePaths.BackupsFolderName, live.VersionFolder, live.SaveName);

        try
        {
            if (ServerBackupsJob.NextDueUtc(settings, all) is { } due)
                s.NextScheduled = due.ToLocalTime().ToString(ServerBackups.LocalFormat, CultureInfo.InvariantCulture);
        }
        catch { /* no schedule line */ }

        if (ServerBackupsJob.Current is { } run)
        {
            s.JobRunning = true;
            s.JobKind = run.Kind;
            s.JobBy = run.By;
            s.JobPhase = ServerBackupsJob.Phase(run);
        }

        if (ServerBackupsJob.Last is { } last)
        {
            s.LastBackupSummary = last.Summary;
            s.LastBackupOk = last.Ok;
            s.LastBackupFinished = last.FinishedUtc == default ? "" : last.FinishedUtc.ToLocalTime().ToString(ServerBackups.LocalFormat, CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrEmpty(root))
        {
            var failure = WorldBackupStatus.ScheduledFailure(WorldBackupStatus.Read(Fs, root!));
            if (failure != null)
            {
                s.ScheduleFailure = failure.Summary;
                s.ScheduleFailureAt = failure.FinishedUtc == default ? "" : failure.FinishedUtc.ToLocalTime().ToString(ServerBackups.LocalFormat, CultureInfo.InvariantCulture);
            }
        }

        // What retention deletes after the next manual and the next scheduled backup (the pending restore's source kept).
        if (all.Count > 0)
        {
            string pendingSource = "";
            if (!string.IsNullOrEmpty(root) && WorldRestoreMarker.ReadPending(Fs, root!, out _) is { } p) pendingSource = p.SourcePath;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string kind in new[] { WorldRestorePaths.KindManual, WorldRestorePaths.KindScheduled })
                foreach (var e in WorldBackupRetention.NextDeletes(all, settings.Policy, NowUtc(), kind, pendingSource))
                    if (seen.Add(e.Id) && s.PruneNextBackup.Count < ServerBackups.MaxEntries)
                        s.PruneNextBackup.Add(new ServerBackupView { Id = e.Id, Name = e.DisplayName });
        }
    }

    internal static ServerBackupView View(WorldBackupEntry e) => new()
    {
        Id = e.Id,
        Kind = e.Kind,
        // AUTOUPDATE3: a pre-update backup's kind names the updates it came before (an older client shows it as is).
        KindLabel = e.RowLabel,
        Name = e.DisplayName,
        Label = e.BaseLabel,
        Created = e.CreatedLocal == DateTime.MinValue ? "" : e.CreatedLocal.ToString(ServerBackups.LocalFormat, CultureInfo.InvariantCulture),
        StartDate = e.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
        AgeDays = e.AgeDays,
        Size = e.SizeBytes,
        Restorable = e.Restorable,
        Reason = e.Reason,
        Shape = e.Shape,
        BackupKind = e.Kind == WorldBackupEntry.KindBackup ? e.BackupKind : "",
        Trigger = e.Trigger,
    };

    /// <summary>
    /// Decide a restore request: the checks, the wipe plan and, unless <see cref="ServerRulesRequest.DryRun"/>, the request
    /// file. Null <paramref name="written"/> on a dry run or a refusal.
    /// </summary>
    internal static (bool Ok, string Error, string Note) Restore(ServerRulesRequest request, ServerRulesFacts facts, string requestedBy,
        out RestoreRequest? written)
    {
        written = null;
        BuildState(facts, out var all);
        if (!TryLayout(out var layout, out string folder, out string refusal)) return (false, refusal, "");
        string? root = Safe(BepInExRootProbe, null);
        if (string.IsNullOrEmpty(root)) return (false, "The BepInEx folder of this server is not known.", "");
        if (Safe(() => ServerSettingsLock.LockedBy(ServerBackups.RestoreSettingId), null) is { } locked)
            return (false, ServerSettingsLock.NoteFor(locked), "");
        WorldBackupEntry? entry = null;
        foreach (var e in all)
            if (string.Equals(e.Id, request.Key, StringComparison.Ordinal)) entry = e;
        if (entry == null) return (false, "That restore point is not there any more. Review the list.", "");
        if (!entry.Restorable) return (false, "That restore point cannot be restored: " + entry.Reason, "");
        if ((request.Shape ?? "").Length > 0 && !string.Equals(request.Shape, entry.Shape, StringComparison.OrdinalIgnoreCase))
            return (false, "That restore point changed since the list was read. Review it again.", "");

        var wipe = WipeFactsOf(facts);
        var now = NowLocal();
        DateTime? picked = null;
        string mode = string.IsNullOrEmpty(request.Wipe) ? WipeModes.None : request.Wipe.Trim().ToLowerInvariant();
        if (mode == WipeModes.Date)
        {
            if (!DateTime.TryParseExact((request.Value ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return (false, "Give the new wipe date as yyyy-MM-dd.", "");
            picked = d;
        }

        var plan = ServerBackups.Plan(entry.StartDate, mode, picked, wipe.Interval, wipe.Day, wipe.Writable, wipe.Reason, now);
        if (plan.Error.Length > 0) return (false, plan.Error, "");

        var current = WorldManifest.Build(Fs, layout.SaveFolder, hash: false);
        if (WorldManifest.NewestSave(current) == null) return (false, "The current world has no save file to back up yet.", "");
        long need = WorldManifest.TotalBytes(current) + entry.SizeBytes;
        need += need / 10 + 256L * 1024 * 1024;
        if (Fs.FreeBytes(layout.PersistRoot) is { } free && free < need)
            return (false, "Not enough free disk space: " + WorldBackupCatalog.FormatBytes(need) + " needed, " + WorldBackupCatalog.FormatBytes(free) + " free.", "");

        string next = ServerBackups.NextWipeText(plan);
        string world = WorldWords(layout.SaveName);
        if (request.DryRun)
            return (true, "", "Dry run: " + world + " would go back to " + entry.BaseLabel + " at the next restart, after a verified backup of "
                + "the current world. Next wipe after the restore: " + next + ". Nothing was written.");

        var r = new RestoreRequest
        {
            RequestId = NowUtc().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Ids.Next(0x1000, 0xFFFF).ToString("x4", CultureInfo.InvariantCulture),
            RequestedUtc = NowUtc(),
            RequestedBy = requestedBy ?? "",
            SaveFolder = layout.SaveFolder,
            SaveName = layout.SaveName,
            SourceKind = entry.Kind,
            SourcePath = entry.Path,
            SourceName = entry.BaseLabel,
            ExpectShape = entry.Shape,
            ExpectStartDate = entry.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            WipeMode = plan.Mode,
            WipeIntervalDays = plan.IntervalDays,
            WipeDayOfReset = plan.DayOfReset,
            HostSettingsPath = plan.Mode is WipeModes.WipesOff or WipeModes.Date ? wipe.HostSettingsPath : "",
            WipeLayers = wipe.Layers,
            WipeDate = picked?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            KeepBackups = Keep(),
            RestartNow = request.RestartNow,
        };
        bool replaced = WorldRestoreMarker.ReadPending(Fs, root!, out _) != null;
        try { WorldRestoreMarker.Write(Fs, root!, r); }
        catch (Exception e) { return (false, "The restore request could not be written (" + e.Message + ").", ""); }
        written = r;

        // BACKUPSDLG1: the note names the world (the client's notice adds the server: "On <server>: world <save> goes back …").
        string note = ServerBackups.Capitalize(world) + " goes back to " + entry.BaseLabel + " at the next restart, after a verified backup of the "
            + "current world. Next wipe after the restore: " + next + "." + (replaced ? " It replaces the earlier request." : "");
        if (request.RestartNow)
        {
            string? why;
            var start = StartRestartCountdown;
            try { why = start == null ? "this server cannot stop itself from here." : start(r.RequestId); }
            catch (Exception e) { why = e.Message; }
            note += why == null
                ? " The server stops after a 60-second countdown; the host's own restart must bring it back."
                : " It was not stopped now (" + why + "); the restore waits for the next restart.";
        }

        RaisePendingChanged(request.RestartNow ? "written, restart now" : "written");
        return (true, "", note);
    }

    /// <summary>Delete the pending request (and stop a countdown). The note says what happened.</summary>
    internal static (bool Ok, string Error, string Note) Cancel()
    {
        string? root = Safe(BepInExRootProbe, null);
        if (string.IsNullOrEmpty(root)) return (false, "The BepInEx folder of this server is not known.", "");
        bool countdown = Safe(CountdownProbe, -1) >= 0;
        if (countdown)
        {
            try { CancelCountdown?.Invoke(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("WorldRestore: cancelling the countdown threw: " + e.Message); }
        }

        bool had;
        try { had = WorldRestoreMarker.Cancel(Fs, root!); }
        catch (Exception e)
        {
            if (countdown) RaisePendingChanged("countdown cancelled");
            return (false, "The restore request could not be deleted (" + e.Message + ").", "");
        }

        if (had || countdown) RaisePendingChanged("cancelled");
        string world = WorldWords(SaveNameNow());
        return (true, "", had ? "The restore of " + world + " is cancelled: the next restart loads the current world." + (countdown ? " The server keeps running." : "")
            : "No restore was pending for " + world + ".");
    }

    /// <summary>Store the keep count (0 to 100; 0 keeps every Progenitor backup).</summary>
    internal static (bool Ok, string Error, string Note) SetKeep(string? value)
    {
        if (Safe(() => ServerSettingsLock.LockedBy(ServerBackups.KeepSettingId), null) is { } locked) return (false, ServerSettingsLock.NoteFor(locked), "");
        if (!int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0 || n > WorldBackupCatalog.MaxKeep
            || n > 0 && n < WorldBackupCatalog.MinKeep)
            return (false, "Give 0 (keep every backup) or a whole number from " + WorldBackupCatalog.MinKeep + " to " + WorldBackupCatalog.MaxKeep + ".", "");
        string? why;
        var write = KeepWrite;
        try { why = write == null ? "the setting is not bound on this server." : write(n); }
        catch (Exception e) { why = e.Message; }
        if (why != null) return (false, "Not saved: " + why, "");
        return (true, "", n == 0 ? "Saved: every Progenitor backup is kept." : "Saved: the newest " + n + " Progenitor backups are kept (applied after the next restore).");
    }

    /// <summary>
    /// BACKUPNOW1: <b>Back up now</b>. The checks (the fence, the save folder, the lock, a backup already running, the free space),
    /// then, unless <see cref="ServerRulesRequest.DryRun"/>, the world save and the copy (<see cref="ServerBackupsJob"/>). The
    /// note says what will happen; <paramref name="finished"/> gets the outcome on the main thread.
    /// </summary>
    internal static (bool Ok, string Error, string Note) BackupNow(ServerRulesRequest request, string requestedBy, Action<BackupOutcome>? finished)
    {
        if (!TryLayout(out var layout, out _, out string refusal)) return (false, refusal, "");
        string? root = Safe(BepInExRootProbe, null);
        if (string.IsNullOrEmpty(root)) return (false, "The BepInEx folder of this server is not known.", "");
        if (LockedBy(ServerBackups.BackupNowSettingId) is { } locked) return (false, ServerSettingsLock.NoteFor(locked), "");
        if (ServerBackupsJob.Current is { } run) return (false, "A backup is already running (" + ServerBackupsJob.Phase(run) + ").", "");
        if (!Fs.DirectoryExists(layout.SaveFolder)) return (false, "The save folder " + layout.SaveName + " does not exist yet.", "");
        List<WorldManifestEntry> shape;
        try { shape = WorldManifest.Build(Fs, layout.SaveFolder, hash: false); }
        catch (Exception e) { return (false, "The save folder could not be read (" + e.Message + ").", ""); }
        if (WorldManifest.NewestSave(shape) == null) return (false, "The world has no save file yet.", "");
        long estimate = WorldManifest.TotalBytes(shape);
        long? free = Fs.FreeBytes(layout.PersistRoot);
        if (WorldBackupMaker.SpaceRefusal(estimate, free, new BackupJob().SpareBytes) is { } space) return (false, Sentence(space), "");
        string where = System.IO.Path.Combine(WorldRestorePaths.BackupsFolderName, layout.VersionFolder, layout.SaveName);
        var settings = BackupSettings();
        if (request.DryRun)
            return (true, "", "Dry run: a manual backup of " + layout.SaveName + " (about " + WorldBackupCatalog.FormatBytes(estimate) + ", " + shape.Count
                + " files) would be made in " + where + " under the server's persistent data"
                + (free is { } f ? " (" + WorldBackupCatalog.FormatBytes(f) + " free)" : "") + ", after a world save. Retention keeps "
                + settings.Policy.Text() + ". Nothing was written.");

        string? why = ServerBackupsJob.Start(WorldRestorePaths.KindManual, WorldRestorePaths.TriggerManual, requestedBy, layout, root!, settings.Policy, finished);
        if (why != null) return (false, "Not backed up: " + why + ".", "");
        return (true, "", ServerBackups.Capitalize(WorldWords(layout.SaveName)) + " is saved now, then copied and checked file by file into " + where + " (about "
            + WorldBackupCatalog.FormatBytes(estimate) + "). Play goes on; the list shows the progress and the result.");
    }

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1) + ".";

    /// <summary>BACKUPNOW1: store one backup setting (<c>BackupAtStart</c> true/false, <c>BackupEveryHours</c>, <c>BackupKeepCount</c>, <c>BackupKeepDays</c>).</summary>
    internal static (bool Ok, string Error, string Note) SetBackupSetting(string? key, string? value)
    {
        string k = (key ?? "").Trim();
        string? id = ServerBackups.SettingIdOf(k);
        if (id == null) return (false, "Unknown backup setting '" + k + "'.", "");
        if (LockedBy(id) is { } locked) return (false, ServerSettingsLock.NoteFor(locked), "");
        string v = (value ?? "").Trim();
        string stored, note;
        if (k == WorldBackupSettings.AtStartKey)
        {
            if (!bool.TryParse(v, out bool b)) return (false, "Give true or false.", "");
            stored = b ? "true" : "false";
            note = b ? "Saved: the world is backed up at every server start, before the game loads (from the next start)." : "Saved: no backup at server starts.";
        }
        else
        {
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return (false, "Give a whole number.", "");
            switch (k)
            {
                case WorldBackupSettings.EveryHoursKey:
                    if (n < 0 || n > WorldBackupSettings.MaxEveryHours) return (false, "Give 0 (off) or a whole number of hours from 1 to " + WorldBackupSettings.MaxEveryHours + ".", "");
                    note = n == 0 ? "Saved: no backups while the server runs." : "Saved: the world is backed up every " + (n == 1 ? "hour" : n + " hours") + " while the server runs.";
                    break;
                case WorldBackupSettings.KeepCountKey:
                    if (n < 0 || n > BackupPolicy.MaxCount) return (false, "Give 0 (no count limit) or a whole number from 1 to " + BackupPolicy.MaxCount + ".", "");
                    note = n == 0 ? "Saved: no count limit for manual and scheduled backups." : "Saved: the newest " + n + " manual and the newest " + n + " scheduled backups are kept.";
                    break;
                default:
                    if (n < 0 || n > BackupPolicy.MaxDays) return (false, "Give 0 (no age limit) or a whole number of days from 1 to " + BackupPolicy.MaxDays + ".", "");
                    note = n == 0 ? "Saved: no age limit for manual and scheduled backups." : "Saved: manual and scheduled backups older than " + (n == 1 ? "1 day" : n + " days") + " are deleted (the newest of each kind stays).";
                    break;
            }

            stored = n.ToString(CultureInfo.InvariantCulture);
        }

        string? why;
        var write = BackupSettingWrite;
        try { why = write == null ? "the setting is not bound on this server." : write(k, stored); }
        catch (Exception e) { why = e.Message; }
        if (why != null) return (false, "Not saved: " + why, "");
        return (true, "", note + (k == WorldBackupSettings.KeepCountKey || k == WorldBackupSettings.KeepDaysKey ? " Applied after the next backup." : ""));
    }

    internal static void Reset()
    {
        Fs = PhysicalWorldFs.Instance;
        BackupSettingsProbe = null;
        BackupSettingWrite = null;
        SaveFolderProbe = null;
        BepInExRootProbe = null;
        KeepProbe = null;
        KeepWrite = null;
        OnlineProbe = null;
        ProcessPathProbe = null;
        ArgsProbe = null;
        NowLocal = () => DateTime.Now;
        NowUtc = () => DateTime.UtcNow;
        StartRestartCountdown = null;
        CountdownProbe = null;
        CancelCountdown = null;
        PendingChanged = null;
    }
}

/// <summary>BACKUPSDLG1: the pending request's id now (empty: none), whether a restart countdown runs, and why it changed.</summary>
internal readonly record struct BackupsPendingChange(string PendingId, bool Countdown, string Why);

/// <summary>WORLDRESTORE1: the Server rules channel's restore operations.</summary>
internal static partial class ServerRulesServer
{
    internal const string BackupsAdminError = "Only server administrators can see and restore backups.";

    private static ServerRulesResponse Backups(ServerRulesRequest request, string op, in RemoteInstallActor actor, ServerRulesResponse response, string row)
    {
        if (!actor.Authorized)
        {
            response.Error = BackupsAdminError;
            WriteAudit(false, actor, row, "", op, response.Error);
            return response;
        }

        ServerRulesFacts facts;
        try { facts = FactsProvider?.Invoke() ?? new ServerRulesFacts(); }
        catch (Exception e)
        {
            response.Error = "The server could not read its settings (" + e.Message + ").";
            return response;
        }

        facts.LockedBy ??= ServerSettingsLock.LockedBy;
        (bool Ok, string Error, string Note) outcome = (true, "", "");
        string audit = "";
        switch (op)
        {
            case ServerRulesRequest.OpRestore:
                outcome = ServerBackupsServer.Restore(request, facts, actor.CharacterName, out var written);
                audit = request.Key + (request.Wipe.Length > 0 ? " wipe=" + request.Wipe : "") + (request.Value.Length > 0 ? " " + request.Value : "")
                    + (request.RestartNow ? " restartNow" : "") + (request.DryRun ? " dryRun" : "");
                if (written != null) Plugin.Logger?.LogInfo("WorldRestore: request " + written.RequestId + " written: " + written.SourceName + " over "
                    + written.SaveFolder + ", wipe " + written.WipeMode + ", keep " + written.KeepBackups + (written.RestartNow ? ", restart now" : ""));
                break;
            case ServerRulesRequest.OpRestoreCancel:
                outcome = ServerBackupsServer.Cancel();
                audit = "cancel";
                break;
            case ServerRulesRequest.OpBackupsKeep:
                outcome = ServerBackupsServer.SetKeep(request.Value);
                audit = "keep=" + request.Value;
                break;
            case ServerRulesRequest.OpBackupNow:
            {
                var who = actor;
                string doneRow = row;
                outcome = ServerBackupsServer.BackupNow(request, actor.CharacterName, done =>
                    WriteAudit(done.Ok, who, doneRow, "", "backup_now done " + (done.Ok ? done.FolderName : ""), done.Ok ? "" : done.Reason));
                audit = "backup_now" + (request.DryRun ? " dryRun" : "");
                break;
            }
            case ServerRulesRequest.OpBackupsSet:
                outcome = ServerBackupsServer.SetBackupSetting(request.Key, request.Value);
                audit = request.Key + "=" + request.Value;
                break;
        }

        response.Ok = outcome.Ok;
        response.Error = outcome.Error;
        response.Note = outcome.Note;
        response.Backups = ServerBackupsServer.BuildState(facts, out _);
        if (op != ServerRulesRequest.OpBackups && !(op is ServerRulesRequest.OpRestore or ServerRulesRequest.OpBackupNow && request.DryRun && outcome.Ok))
            WriteAudit(outcome.Ok, actor, row, "", audit, outcome.Error);
        return response;
    }
}
