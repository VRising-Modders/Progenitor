using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Progenitor.WorldRestore;

/// <summary>
/// BACKUPNOW1: the backup settings of <c>[WorldRestore]</c> in the server's <c>progenitor.cfg</c>. Core binds them (Server rules
/// edits them); Bootstrap reads the file's text at a server start, before Core exists (<see cref="FromCfgText"/>).
/// </summary>
internal sealed class WorldBackupSettings
{
    internal const string Section = "WorldRestore";
    internal const string AtStartKey = "BackupAtStart";
    internal const string EveryHoursKey = "BackupEveryHours";
    internal const string KeepCountKey = "BackupKeepCount";
    internal const string KeepDaysKey = "BackupKeepDays";

    internal const int MaxEveryHours = 168;

    /// <summary>Back up the world at every server start (Bootstrap, before the game loads). Off by default.</summary>
    public bool AtStart { get; set; }

    /// <summary>Back up the world every this many hours while the server runs (0: off, the default; at most 168).</summary>
    public int EveryHours { get; set; }

    public int KeepCount { get; set; } = BackupPolicy.DefaultCount;
    public int KeepDays { get; set; } = BackupPolicy.DefaultDays;

    internal BackupPolicy Policy => new BackupPolicy(KeepCount, KeepDays).Clamped;

    internal static int ClampHours(int n) => n < 0 ? 0 : n > MaxEveryHours ? MaxEveryHours : n;

    /// <summary>The settings in a <c>progenitor.cfg</c> text (BepInEx's <c>Key = value</c> lines); missing or bad values are the defaults.</summary>
    internal static WorldBackupSettings FromCfgText(string? text)
    {
        var s = new WorldBackupSettings();
        if (string.IsNullOrEmpty(text)) return s;
        bool inSection = false;
        foreach (string raw in text!.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            if (line[0] == '[')
            {
                inSection = string.Equals(line, "[" + Section + "]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim();
            string value = line.Substring(eq + 1).Trim();
            if (string.Equals(key, AtStartKey, StringComparison.OrdinalIgnoreCase))
            {
                if (bool.TryParse(value, out bool b)) s.AtStart = b;
            }
            else if (string.Equals(key, EveryHoursKey, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) s.EveryHours = ClampHours(n);
            }
            else if (string.Equals(key, KeepCountKey, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) s.KeepCount = BackupPolicy.ClampCount(n);
            }
            else if (string.Equals(key, KeepDaysKey, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) s.KeepDays = BackupPolicy.ClampDays(n);
            }
        }

        return s;
    }

    /// <summary>Read <c>BepInEx\config\progenitor.cfg</c>; the defaults (everything off) when it cannot be read.</summary>
    internal static WorldBackupSettings FromCfg(IWorldFs fs, string bepInExRoot)
    {
        try
        {
            string path = Path.Combine(bepInExRoot ?? "", "config", "progenitor.cfg");
            return fs.FileExists(path) ? FromCfgText(WorldJson.Text(fs.ReadAllBytes(path))) : new WorldBackupSettings();
        }
        catch { return new WorldBackupSettings(); }
    }

    /// <summary><c>at every server start and every 6 hours</c> / <c>off</c>.</summary>
    internal string ScheduleText()
    {
        var parts = new List<string>();
        if (AtStart) parts.Add("at every server start");
        if (EveryHours > 0) parts.Add("every " + (EveryHours == 1 ? "hour" : EveryHours.ToString(CultureInfo.InvariantCulture) + " hours"));
        return parts.Count == 0 ? "off" : string.Join(" and ", parts);
    }
}

/// <summary>
/// BACKUPNOW1: when the next "every N hours" backup is due (game-free; Core's ticker asks once a minute).
/// <list type="bullet">
/// <item>Off when the interval is 0.</item>
/// <item>Due an interval after the newest scheduled backup on disk (one made at a server start counts), else an interval after
/// Core started.</item>
/// <item>Never in the first <see cref="StartGraceMinutes"/> after Core started (the world is still loading; a start backup
/// covers that moment).</item>
/// <item>After a failure, again <see cref="RetryMinutes"/> later (or an interval, when that is shorter), not every minute.</item>
/// </list>
/// </summary>
internal static class WorldBackupSchedule
{
    internal const double StartGraceMinutes = 10;
    internal const double RetryMinutes = 30;

    internal static DateTime? NextDueUtc(int everyHours, DateTime? lastScheduledUtc, DateTime coreStartUtc, DateTime? lastFailureUtc)
    {
        if (everyHours <= 0) return null;
        var interval = TimeSpan.FromHours(Math.Min(everyHours, WorldBackupSettings.MaxEveryHours));
        DateTime due = (lastScheduledUtc ?? coreStartUtc) + interval;
        var grace = coreStartUtc.AddMinutes(StartGraceMinutes);
        if (due < grace) due = grace;
        if (lastFailureUtc is { } failed && (lastScheduledUtc == null || failed > lastScheduledUtc.Value))
        {
            var retry = failed + TimeSpan.FromMinutes(Math.Min(RetryMinutes, interval.TotalMinutes));
            if (due < retry) due = retry;
        }

        return due;
    }

    internal static bool IsDue(int everyHours, DateTime? lastScheduledUtc, DateTime coreStartUtc, DateTime? lastFailureUtc, DateTime nowUtc) =>
        NextDueUtc(everyHours, lastScheduledUtc, coreStartUtc, lastFailureUtc) is { } due && nowUtc >= due;

    /// <summary>The newest scheduled backup's creation time (UTC) among <paramref name="entries"/>, or null.</summary>
    internal static DateTime? LastScheduledUtc(IEnumerable<WorldBackupEntry> entries)
    {
        DateTime? best = null;
        foreach (var e in entries)
        {
            if (e.Kind != WorldBackupEntry.KindBackup || e.BackupKind != WorldRestorePaths.KindScheduled || e.CreatedLocal == DateTime.MinValue) continue;
            var utc = e.CreatedLocal.ToUniversalTime();
            if (best == null || utc > best.Value) best = utc;
        }

        return best;
    }
}

/// <summary>One trigger's last backup result in <c>BepInEx\progenitor-backups.status.json</c>.</summary>
internal sealed class BackupStatusEntry
{
    public bool Ok { get; set; }
    public string Summary { get; set; } = "";
    public string Name { get; set; } = "";
    public string By { get; set; } = "";
    public DateTime FinishedUtc { get; set; }
}

/// <summary>
/// BACKUPNOW1: <c>BepInEx\progenitor-backups.status.json</c>: the last result of each trigger (<c>start</c> from Bootstrap,
/// <c>interval</c> and <c>manual</c> from Core), so the Backups list can say that a scheduled backup failed even when it
/// failed before Core loaded. File-only.
/// </summary>
internal static class WorldBackupStatus
{
    internal static string PathOf(string bepInExRoot) => Path.Combine(bepInExRoot ?? "", WorldRestorePaths.BackupStatusFileName);

    internal static Dictionary<string, BackupStatusEntry> Read(IWorldFs fs, string bepInExRoot)
    {
        var map = new Dictionary<string, BackupStatusEntry>(StringComparer.Ordinal);
        try
        {
            string path = PathOf(bepInExRoot);
            if (string.IsNullOrEmpty(bepInExRoot) || !fs.FileExists(path)) return map;
            using var doc = JsonDocument.Parse(WorldJson.StripBom(fs.ReadAllBytes(path)), new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                map[p.Name] = new BackupStatusEntry
                {
                    Ok = WorldJson.Bool(p.Value, "ok"),
                    Summary = WorldJson.Str(p.Value, "summary"),
                    Name = WorldJson.Str(p.Value, "name"),
                    By = WorldJson.Str(p.Value, "by"),
                    FinishedUtc = WorldJson.Time(p.Value, "finishedUtc") ?? default,
                };
            }
        }
        catch { /* an unreadable status is no status */ }

        return map;
    }

    /// <summary>Record <paramref name="outcome"/> as the last result of its trigger. Never throws.</summary>
    internal static void Record(IWorldFs fs, string bepInExRoot, BackupOutcome outcome, string by = "")
    {
        if (string.IsNullOrEmpty(bepInExRoot)) return;
        try
        {
            var map = Read(fs, bepInExRoot);
            map[outcome.Trigger.Length > 0 ? outcome.Trigger : WorldRestorePaths.TriggerManual] = new BackupStatusEntry
            {
                Ok = outcome.Ok, Summary = outcome.Summary, Name = outcome.Name, By = by ?? "", FinishedUtc = outcome.FinishedUtc,
            };
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                foreach (var (k, v) in map)
                {
                    w.WriteStartObject(k);
                    w.WriteBoolean("ok", v.Ok);
                    w.WriteString("summary", v.Summary);
                    w.WriteString("name", v.Name);
                    if (v.By.Length > 0) w.WriteString("by", v.By);
                    w.WriteString("finishedUtc", WorldJson.Utc(v.FinishedUtc));
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            WorldRestoreApply.WriteAtomic(fs, PathOf(bepInExRoot), ms.ToArray());
        }
        catch { /* the log has the outcome */ }
    }

    /// <summary>
    /// The last scheduled backup (start or interval) when it failed and no scheduled backup succeeded after it; else null.
    /// </summary>
    internal static BackupStatusEntry? ScheduledFailure(IReadOnlyDictionary<string, BackupStatusEntry> map)
    {
        BackupStatusEntry? last = null;
        foreach (string k in new[] { WorldRestorePaths.TriggerStart, WorldRestorePaths.TriggerInterval })
            if (map.TryGetValue(k, out var e) && (last == null || e.FinishedUtc > last.FinishedUtc)) last = e;
        return last is { Ok: false } ? last : null;
    }
}

/// <summary>
/// BACKUPNOW1: the scheduled backup at a server start, run by Bootstrap after the start-time restore
/// (<see cref="WorldRestoreMarker.ApplyAtStart(IWorldFs, string, RestoreEnvironment, out bool)"/>) and before the game loads.
/// <para><b>With a restore request waiting</b> the world before the restore is copied once, before the swap: by the
/// restore's own before-restore backup (the restore always makes it, verified, in the restore chain). So when that restore
/// ran at this start and succeeded, no second copy is made here. When it was refused (the world is unchanged; a refused
/// restore removes the backup it made), this backup is made as usual, of that same unchanged world.</para>
/// A failed backup is logged and recorded in the status file; it never stops the server.
/// </summary>
internal static class WorldBackupAtStart
{
    /// <summary>What happened (a skip says why); null outcome when nothing was copied.</summary>
    internal sealed class Report
    {
        public bool Attempted { get; set; }
        public string Skipped { get; set; } = "";
        public BackupOutcome? Outcome { get; set; }
    }

    internal static Report Run(IWorldFs fs, string bepInExRoot, RestoreEnvironment env, WorldBackupSettings settings, RestoreResult? restore, bool halted)
    {
        var report = new Report();
        void Say(string line)
        {
            try { env.Log("WorldBackup: " + line); }
            catch { /* never decides */ }
        }

        if (settings == null || !settings.AtStart) return Skip(report, "", null);
        if (halted) return Skip(report, "the start backup is skipped: this start stops before the game loads (or belongs to the live server path)", Say);
        if (WorldRestorePaths.IsLiveServerPath(bepInExRoot) || WorldRestorePaths.IsLiveServerPath(env.ProcessPath) || WorldRestorePaths.IsLiveServerCommandLine(env.Args))
            return Skip(report, "the start backup never runs on the live server path", Say);
        if (restore != null && restore.RanThisStart && restore.Ok && restore.BackupPath.Length > 0)
            return Skip(report, "the start backup is the restore's own backup at this start (" + restore.BackupName + "): the world before the restore is copied once", Say);
        string? folder;
        try { folder = WorldRestorePaths.SaveFolderFromArgs(fs, env.Args); }
        catch { folder = null; }
        if (string.IsNullOrEmpty(folder)) return Skip(report, "the start backup is skipped: the command line names no -persistentDataPath and -saveName", Say);
        if (WorldRestorePaths.IsHostedGame(env.Args, folder)) return Skip(report, "the start backup is skipped: a hosted private game", Say);
        if (!fs.DirectoryExists(folder!)) return Skip(report, "the start backup is skipped: " + folder + " does not exist yet (a first start)", Say);

        var job = new BackupJob
        {
            SaveFolder = folder!,
            Kind = WorldRestorePaths.KindScheduled,
            Trigger = WorldRestorePaths.TriggerStart,
            StrictSave = false,
            Attempts = 2,
            RetryWaitMs = 500,
            Retention = settings.Policy,
        };
        report.Attempted = true;
        Say("start backup of " + folder + " (before the game loads)");
        var outcome = WorldBackupMaker.Make(fs, job, env);
        report.Outcome = outcome;
        WorldBackupStatus.Record(fs, bepInExRoot, outcome);
        Say((outcome.Ok ? "start backup done" : "START BACKUP FAILED (the server starts anyway)") + WorldRestoreResultElapsed(outcome) + ": " + outcome.Summary);
        return report;
    }

    private static Report Skip(Report r, string why, Action<string>? say)
    {
        r.Skipped = why;
        if (why.Length > 0) say?.Invoke(why);
        return r;
    }

    private static string WorldRestoreResultElapsed(BackupOutcome o)
    {
        if (o.StartedUtc == default || o.FinishedUtc < o.StartedUtc) return "";
        return " in " + ((long)(o.FinishedUtc - o.StartedUtc).TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms";
    }
}
