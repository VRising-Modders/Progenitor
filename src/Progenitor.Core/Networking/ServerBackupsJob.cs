using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Progenitor.WorldRestore;

namespace Progenitor.Networking;

/// <summary>
/// BACKUPNOW1, server: the one backup running while the game runs (<b>Back up now</b> and the "every N hours" schedule).
/// <list type="number">
/// <item><b>Save</b> (main thread): the folder's newest save is sampled, then the game's world save is triggered through the
/// same path as the graceful stop (<c>TriggerPersistenceSaveSystem.TriggerSave</c>, then <c>ModSave.SaveAll</c>, so the mod
/// data lands with the world).</item>
/// <item><b>Wait</b> (main thread, polled through the frame ticker, never blocking it: the game serializes the save on a later
/// frame) until the newest save file is newer than the sample, at most
/// <see cref="Persistence.WorldSaveTrigger.SaveConfirmTimeoutSeconds"/>; then a short settle.</item>
/// <item><b>Copy</b> off the main thread: <see cref="WorldBackupMaker.Make"/>, file IO only (no game or IL2CPP object, no
/// logger: its lines are queued and written by the main thread). An autosave that lands during the copy is detected and
/// the copy made again.</item>
/// <item><b>Finish</b> (main thread): the log lines, the status file, the audit line (who made it), the list's result.</item>
/// </list>
/// Game-free: the save, the scheduling and the thread are seams (live: <c>ServerBackupsGame</c>).
/// </summary>
internal static class ServerBackupsJob
{
    /// <summary>Trigger the world save: null when accepted, else why not (live: <c>WorldSaveTrigger.SaveCurrentWorld</c>).</summary>
    internal static Func<string?>? SaveWorld { get; set; }

    /// <summary>Sample the save folder before the trigger; the predicate says whether a newer save landed (null: not observable).</summary>
    internal static Func<Func<bool>>? SaveWatch { get; set; }

    /// <summary>Run an action on the main thread after a delay (live: <c>MainThread.PostDelayed</c>).</summary>
    internal static Action<double, Action>? Schedule { get; set; }

    /// <summary>Run the copy off the main thread (live: a background thread; tests: inline).</summary>
    internal static Action<Action>? RunOffThread { get; set; }

    internal static Func<DateTime> NowUtc { get; set; } = () => DateTime.UtcNow;

    internal static string ProgenitorVersion { get; set; } = "";

    /// <summary>Seconds between the save checks and the copy's completion checks.</summary>
    internal const double PollSeconds = 0.5;

    /// <summary>The settle after the save landed (the game may still close its file).</summary>
    internal const double SettleSeconds = 1.0;

    internal const int SaveTimeoutSeconds = Persistence.WorldSaveTrigger.SaveConfirmTimeoutSeconds;

    /// <summary>The backup in progress.</summary>
    internal sealed class Running
    {
        public string Kind { get; init; } = "";
        public string Trigger { get; init; } = "";
        public string By { get; init; } = "";
        public DateTime StartedUtc { get; init; }
        public BackupJob Job { get; init; } = new();
        public volatile bool Saving = true;

        /// <summary>Core is unloading: the copy stops between files (per run, so a later reset cannot clear it).</summary>
        public volatile bool CancelRequested;
        public volatile BackupOutcome? Done;
        internal readonly ConcurrentQueue<string> Lines = new();
        internal Action<BackupOutcome>? Finished;
        internal int Generation;
    }

    private static Running? _current;
    private static BackupOutcome? _last;
    private static string _lastBy = "";
    private static int _generation;

    internal static Running? Current => _current;

    /// <summary>The last finished backup of this Core generation (manual or interval), or null.</summary>
    internal static BackupOutcome? Last => _last;

    internal static string LastBy => _lastBy;

    internal static bool Busy => _current != null;

    /// <summary>
    /// Start a backup. Null when it started (the outcome comes later through <paramref name="finished"/>, main thread), else
    /// why not (nothing was started; nothing was saved unless the refusal says the save was refused).
    /// </summary>
    internal static string? Start(string kind, string trigger, string by, WorldRestorePaths.SaveLayout layout, string bepInExRoot, BackupPolicy policy,
        Action<BackupOutcome>? finished) => Start(kind, trigger, by, layout, bepInExRoot, policy, finished, null, "");

    /// <summary>
    /// AUTOUPDATE3: the same, for a pre-update backup: <paramref name="preUpdateKeep"/> is its own retention (counted only among
    /// pre-update backups) and <paramref name="updates"/> the label its sidecar keeps.
    /// </summary>
    internal static string? Start(string kind, string trigger, string by, WorldRestorePaths.SaveLayout layout, string bepInExRoot, BackupPolicy policy,
        Action<BackupOutcome>? finished, int? preUpdateKeep, string updates)
    {
        if (_current != null) return "a backup is already running (" + Phase(_current) + ")";
        var schedule = Schedule;
        var offThread = RunOffThread;
        var save = SaveWorld;
        if (schedule == null || offThread == null) return "this server cannot run a backup from here";
        if (save == null) return "this server cannot save its world from here (no world save path)";
        long estimate;
        long? free;
        try
        {
            estimate = WorldBackupMaker.Estimate(ServerBackupsServer.Fs, layout.SaveFolder);
            free = ServerBackupsServer.Fs.FreeBytes(layout.PersistRoot);
        }
        catch (Exception e) { return "the save folder could not be read (" + e.Message + ")"; }

        if (WorldBackupMaker.SpaceRefusal(estimate, free, new BackupJob().SpareBytes) is { } space) return space;

        Func<bool>? landed = null;
        try { landed = SaveWatch?.Invoke(); }
        catch { landed = null; }
        string? refusal;
        try { refusal = save(); }
        catch (Exception e) { refusal = e.Message; }
        if (!string.IsNullOrEmpty(refusal)) return "the world save was refused (" + refusal + ")";

        var run = new Running
        {
            Kind = kind,
            Trigger = trigger,
            By = by ?? "",
            StartedUtc = NowUtc(),
            Finished = finished,
            Generation = ++_generation,
            Job = new BackupJob
            {
                SaveFolder = layout.SaveFolder,
                Kind = kind,
                Trigger = trigger,
                StrictSave = true,
                Retention = policy,
                PreUpdateKeep = preUpdateKeep,
                Updates = updates ?? "",
            },
        };
        run.Job.Cancelled = () => run.CancelRequested;
        run.Job.Progress.Phase = BackupProgress.PhaseSaving;
        _current = run;
        Plugin.Logger?.LogInfo("WorldBackup: " + KindWord(kind) + " backup by " + (run.By.Length > 0 ? run.By : "?") + ": the world save was triggered; "
            + "the copy starts when the save file lands.");

        DateTime deadline = NowUtc().AddSeconds(SaveTimeoutSeconds);
        void WaitForSave()
        {
            if (run.Generation != _generation || _current != run) return;
            bool ok;
            try { ok = landed == null || landed(); }
            catch { ok = false; }
            if (ok)
            {
                schedule(SettleSeconds, () => Launch(run, bepInExRoot));
                return;
            }

            if (NowUtc() >= deadline)
            {
                Finish(run, Failed(run, "the world save did not land within " + SaveTimeoutSeconds + " seconds (no newer save file appeared)"));
                return;
            }

            schedule(PollSeconds, WaitForSave);
        }

        schedule(PollSeconds, WaitForSave);
        return null;
    }

    private static void Launch(Running run, string bepInExRoot)
    {
        if (run.Generation != _generation || _current != run) return;
        run.Saving = false;
        run.Job.Progress.Phase = BackupProgress.PhaseCopying;
        var env = new RestoreEnvironment
        {
            BepInExRoot = bepInExRoot,
            ProgenitorVersion = ProgenitorVersion,
            Log = line => run.Lines.Enqueue(line),
            UtcNow = NowUtc,
            LocalNow = () => NowUtc().ToLocalTime(),
        };
        try
        {
            RunOffThread!(() =>
            {
                BackupOutcome outcome;
                try { outcome = WorldBackupMaker.Make(ServerBackupsServer.Fs, run.Job, env); }
                catch (Exception e) { outcome = Failed(run, "unexpected error: " + e.Message); }
                try { WorldBackupStatus.Record(ServerBackupsServer.Fs, bepInExRoot, outcome, run.By); }
                catch { /* the log has it */ }
                run.Done = outcome;
            });
        }
        catch (Exception e)
        {
            Finish(run, Failed(run, "the copy could not start (" + e.Message + ")"));
            return;
        }

        void WaitForCopy()
        {
            if (run.Generation != _generation || _current != run) return;
            Drain(run);
            if (run.Done is { } done)
            {
                Finish(run, done);
                return;
            }

            Schedule?.Invoke(PollSeconds, WaitForCopy);
        }

        // Inline in the tests: already done.
        WaitForCopy();
    }

    private static BackupOutcome Failed(Running run, string reason) => new()
    {
        Ok = false,
        Kind = run.Kind,
        Trigger = run.Trigger,
        Reason = reason,
        Summary = "Not backed up: " + reason + ".",
        StartedUtc = run.StartedUtc,
        FinishedUtc = NowUtc(),
    };

    private static void Finish(Running run, BackupOutcome outcome)
    {
        if (_current != run) return;
        Drain(run);
        _current = null;
        _last = outcome;
        _lastBy = run.By;
        if (run.Saving)
        {
            // Failed before the copy started: the status file has not heard of it.
            try { WorldBackupStatus.Record(ServerBackupsServer.Fs, ServerBackupsServer.BepInExRootProbe?.Invoke() ?? "", outcome, run.By); }
            catch { /* the log has it */ }
        }

        string line = "WorldBackup: " + KindWord(run.Kind) + " backup by " + (run.By.Length > 0 ? run.By : "?") + ": " + outcome.Summary;
        if (outcome.Ok) Plugin.Logger?.LogInfo(line);
        else Plugin.Logger?.LogWarning(line);
        foreach (string w in outcome.Warnings) Plugin.Logger?.LogWarning("WorldBackup: " + w);
        try { run.Finished?.Invoke(outcome); }
        catch (Exception e) { Plugin.Logger?.LogWarning("WorldBackup: the finish callback threw: " + e.Message); }
    }

    private static void Drain(Running run)
    {
        while (run.Lines.TryDequeue(out string? l)) Plugin.Logger?.LogInfo(l);
    }

    internal static string KindWord(string kind) =>
        kind == WorldRestorePaths.KindScheduled ? "scheduled" : kind == WorldRestorePaths.KindPreUpdate ? "pre-update" : "manual";

    /// <summary>"saving the world" / "copying 12 of 40 files (35 %)" / "checking the copy" / "applying retention".</summary>
    internal static string Phase(Running run)
    {
        var p = run.Job.Progress;
        switch (p.Phase)
        {
            case BackupProgress.PhaseSaving:
                return "saving the world";
            case BackupProgress.PhaseCopying:
            {
                long total = p.BytesTotal;
                int pct = total > 0 ? (int)Math.Min(100, p.BytesDone * 100 / total) : 0;
                return "copying " + p.FilesDone + " of " + p.FilesTotal + " files (" + pct + " %)" + (p.Attempt > 1 ? ", copy " + p.Attempt : "");
            }
            case BackupProgress.PhaseVerifying:
                return "checking the copy";
            default:
                return "applying retention";
        }
    }

    /// <summary>Core unloading: stop between files (the partial copy is removed) and forget the job.</summary>
    internal static void Cancel()
    {
        if (_current is { } running) running.CancelRequested = true;
        _generation++;
        _current = null;
    }

    internal static void Reset()
    {
        Cancel();
        _last = null;
        _lastBy = "";
        SaveWorld = null;
        SaveWatch = null;
        Schedule = null;
        RunOffThread = null;
        NowUtc = () => DateTime.UtcNow;
        ProgenitorVersion = "";
        ResetSchedule();
    }

    // ---- the "every N hours" schedule -----------------------------------------------------------------

    private static DateTime _coreStartUtc;
    private static DateTime? _lastScheduledUtc;
    private static bool _lastScheduledKnown;
    private static DateTime? _lastIntervalFailureUtc;

    internal static void ResetSchedule()
    {
        _coreStartUtc = default;
        _lastScheduledUtc = null;
        _lastScheduledKnown = false;
        _lastIntervalFailureUtc = null;
    }

    /// <summary>When the next interval backup is due (UTC), or null when the schedule is off.</summary>
    internal static DateTime? NextDueUtc(WorldBackupSettings settings, IReadOnlyList<WorldBackupEntry>? entries = null)
    {
        if (_coreStartUtc == default) _coreStartUtc = NowUtc();
        if (entries != null)
        {
            _lastScheduledUtc = WorldBackupSchedule.LastScheduledUtc(entries);
            _lastScheduledKnown = true;
        }

        return WorldBackupSchedule.NextDueUtc(settings.EveryHours, _lastScheduledUtc, _coreStartUtc, _lastIntervalFailureUtc);
    }

    /// <summary>
    /// The schedule's check (once a minute, main thread): start a scheduled backup when one is due. Returns what it did (for
    /// the log and the tests): null when nothing was due.
    /// </summary>
    internal static string? Tick(WorldBackupSettings settings)
    {
        if (_coreStartUtc == default) _coreStartUtc = NowUtc();
        if (settings.EveryHours <= 0 || Busy) return null;
        if (!ServerBackupsServer.TryLayout(out var layout, out _, out _)) return null;
        if (!_lastScheduledKnown)
        {
            try { _lastScheduledUtc = WorldBackupSchedule.LastScheduledUtc(WorldBackupCatalog.List(ServerBackupsServer.Fs, layout)); }
            catch { _lastScheduledUtc = null; }
            _lastScheduledKnown = true;
            try
            {
                string root0 = ServerBackupsServer.BepInExRootProbe?.Invoke() ?? "";
                if (WorldBackupStatus.Read(ServerBackupsServer.Fs, root0).TryGetValue(WorldRestorePaths.TriggerInterval, out var st) && !st.Ok
                    && st.FinishedUtc != default)
                    _lastIntervalFailureUtc = st.FinishedUtc;
            }
            catch { /* no status */ }
        }

        var now = NowUtc();
        if (!WorldBackupSchedule.IsDue(settings.EveryHours, _lastScheduledUtc, _coreStartUtc, _lastIntervalFailureUtc, now)) return null;
        string root = ServerBackupsServer.BepInExRootProbe?.Invoke() ?? "";
        string? why = Start(WorldRestorePaths.KindScheduled, WorldRestorePaths.TriggerInterval, "schedule", layout, root, settings.Policy, outcome =>
        {
            if (outcome.Ok)
            {
                _lastScheduledUtc = outcome.FinishedUtc;
                _lastIntervalFailureUtc = null;
            }
            else _lastIntervalFailureUtc = outcome.FinishedUtc == default ? NowUtc() : outcome.FinishedUtc;
        });
        if (why == null) return "started";
        _lastIntervalFailureUtc = now;
        var failed = new BackupOutcome
        {
            Ok = false, Kind = WorldRestorePaths.KindScheduled, Trigger = WorldRestorePaths.TriggerInterval, Reason = why,
            Summary = "Not backed up: " + why + ".", StartedUtc = now, FinishedUtc = now,
        };
        WorldBackupStatus.Record(ServerBackupsServer.Fs, root, failed, "schedule");
        Plugin.Logger?.LogWarning("WorldBackup: the scheduled backup did not start: " + why);
        return "refused: " + why;
    }
}
