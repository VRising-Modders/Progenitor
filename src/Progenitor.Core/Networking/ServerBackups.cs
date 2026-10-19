using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.WorldRestore;

namespace Progenitor.Networking;

/// <summary>One restore point as the server shares it with an admin (never a path).</summary>
internal sealed class ServerBackupView
{
    public string Id { get; set; } = "";

    /// <summary><c>archive</c> or <c>backup</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>"Wipe archive" or "Before restore".</summary>
    public string KindLabel { get; set; } = "";

    /// <summary>"Wipe archive from Wed, Oct 1, 2026" / "Before restore of …, …".</summary>
    public string Name { get; set; } = "";

    /// <summary>The short label for a sentence: "the wipe archive from …" / "the backup from …".</summary>
    public string Label { get; set; } = "";

    /// <summary>When it was made, server local time (<c>yyyy-MM-ddTHH:mm:ss</c>).</summary>
    public string Created { get; set; } = "";

    /// <summary>Its <c>StartDate.json</c> (<c>yyyy-MM-dd</c>), or empty.</summary>
    public string StartDate { get; set; } = "";

    public int? AgeDays { get; set; }
    public long Size { get; set; }
    public bool Restorable { get; set; }
    public string Reason { get; set; } = "";
    public string Shape { get; set; } = "";

    /// <summary>BACKUPNOW1: a Progenitor backup's sidecar kind (<c>manual</c>, <c>scheduled</c>, <c>before-restore</c>), or empty.</summary>
    public string BackupKind { get; set; } = "";

    /// <summary>BACKUPNOW1: what made a manual or scheduled backup (<c>manual</c>, <c>start</c>, <c>interval</c>), or empty.</summary>
    public string Trigger { get; set; } = "";

    internal DateTime? CreatedTime =>
        DateTime.TryParseExact(Created, ServerBackups.LocalFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    internal DateTime? StartTime =>
        DateTime.TryParseExact(StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
}

/// <summary>WORLDRESTORE1: the Backups part of the Server rules state, for an admin (the <c>backups</c> answer).</summary>
internal sealed class ServerBackupsState
{
    public List<ServerBackupView> Entries { get; } = new();

    /// <summary>Restore points not on the wire (the newest <see cref="ServerBackups.MaxEntries"/> are).</summary>
    public int More { get; set; }

    public int Count { get; set; }
    public long ArchiveBytes { get; set; }
    public long BackupBytes { get; set; }
    public long CurrentBytes { get; set; }

    /// <summary>Free bytes on the save's volume, or -1.</summary>
    public long FreeBytes { get; set; } = -1;

    /// <summary>Keep the newest this many Progenitor backups (0: all).</summary>
    public int Keep { get; set; } = WorldBackupCatalog.DefaultKeep;

    public string KeepLockedBy { get; set; } = "";

    public bool RestoreAllowed { get; set; }

    /// <summary>Why a restore cannot be asked for here (the live-server fence, an unknown save folder, a lock), or empty.</summary>
    public string RestoreRefusal { get; set; } = "";

    /// <summary>The next start's <c>ResetDaysInterval</c> and <c>DayOfReset</c> (what a restored world's wipe date is computed with).</summary>
    public int NextInterval { get; set; }

    public string NextDay { get; set; } = "Any";

    /// <summary>The interval can be written for the next start (no command-line override, not locked, a file is known).</summary>
    public bool IntervalWritable { get; set; }

    public string IntervalReason { get; set; } = "";

    /// <summary>The server's local date and time now (<c>yyyy-MM-ddTHH:mm:ss</c>): the wipe check's clock.</summary>
    public string ServerNow { get; set; } = "";

    public int Online { get; set; }

    public string PendingId { get; set; } = "";
    public string PendingName { get; set; } = "";
    public string PendingRequestedUtc { get; set; } = "";
    public string PendingWipe { get; set; } = "";
    public string PendingWipeDate { get; set; } = "";
    public bool PendingRestartNow { get; set; }

    /// <summary>Seconds left of a "Restore and restart now" countdown, or -1.</summary>
    public int Countdown { get; set; } = -1;

    /// <summary>BACKUPSDLG1: the running save's name (the world a restore replaces), or empty (an older server).</summary>
    public string SaveName { get; set; } = "";

    /// <summary>M1: the Progenitor backups the next restore's retention would delete (id and name).</summary>
    public List<ServerBackupView> PruneNext { get; } = new();

    public string ResultId { get; set; } = "";
    public bool ResultOk { get; set; }
    public string ResultSummary { get; set; } = "";
    public string ResultFinishedUtc { get; set; } = "";

    // ---- BACKUPNOW1: manual and scheduled backups ------------------------------------------------------

    public long ManualBytes { get; set; }
    public long ScheduledBytes { get; set; }

    /// <summary>AUTOUPDATE3: the disk use of the backups made before an update.</summary>
    public long PreUpdateBytes { get; set; }

    /// <summary>Where backups go, relative to the server's persistent data (<c>progenitor-backups\v4\world1</c>), or empty.</summary>
    public string BackupsWhere { get; set; } = "";

    /// <summary>A backup can be made here now (the fence, a known save folder, the Back up now lock).</summary>
    public bool BackupAllowed { get; set; }

    public string BackupRefusal { get; set; } = "";
    public string BackupNowLockedBy { get; set; } = "";

    public bool AtStart { get; set; }
    public int EveryHours { get; set; }
    public int KeepCount { get; set; } = BackupPolicy.DefaultCount;
    public int KeepDays { get; set; } = BackupPolicy.DefaultDays;
    public string AtStartLockedBy { get; set; } = "";
    public string EveryHoursLockedBy { get; set; } = "";
    public string KeepCountLockedBy { get; set; } = "";
    public string KeepDaysLockedBy { get; set; } = "";

    /// <summary>When the next "every N hours" backup is due, server local time (<c>yyyy-MM-ddTHH:mm:ss</c>), or empty.</summary>
    public string NextScheduled { get; set; } = "";

    /// <summary>A backup is running: its kind, who asked (a name or <c>schedule</c>) and where it is.</summary>
    public bool JobRunning { get; set; }

    public string JobKind { get; set; } = "";
    public string JobBy { get; set; } = "";
    public string JobPhase { get; set; } = "";

    /// <summary>The last manual or interval backup of this server session: its sentence, success and finish time (local).</summary>
    public string LastBackupSummary { get; set; } = "";

    public bool LastBackupOk { get; set; }
    public string LastBackupFinished { get; set; } = "";

    /// <summary>The last scheduled backup (at a start or every N hours) failed and none succeeded since: its sentence and time.</summary>
    public string ScheduleFailure { get; set; } = "";

    public string ScheduleFailureAt { get; set; } = "";

    /// <summary>What retention deletes after the next manual and the next scheduled backup (id and name).</summary>
    public List<ServerBackupView> PruneNextBackup { get; } = new();

    /// <summary>The game's autosaves in the save folder now: count, total size, newest and oldest write time (local).</summary>
    public bool AutosaveKnown { get; set; }

    public int AutosaveCount { get; set; }
    public long AutosaveBytes { get; set; }
    public string AutosaveNewest { get; set; } = "";
    public string AutosaveOldest { get; set; } = "";

    /// <summary>The effective <c>AutoSaveCount</c> and <c>AutoSaveInterval</c> (seconds) the game runs with, or -1 when unknown.</summary>
    public int AutosaveKeep { get; set; } = -1;

    public int AutosaveEvery { get; set; } = -1;

    internal BackupPolicy Policy => new BackupPolicy(KeepCount, KeepDays).Clamped;

    internal WorldBackupSettings Settings => new() { AtStart = AtStart, EveryHours = EveryHours, KeepCount = KeepCount, KeepDays = KeepDays };

    internal DateTime ServerNowTime =>
        DateTime.TryParseExact(ServerNow, ServerBackups.LocalFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTime.Now;

    internal ServerBackupView? Find(string? id)
    {
        foreach (var e in Entries)
            if (string.Equals(e.Id, id, StringComparison.Ordinal)) return e;
        return null;
    }
}

/// <summary>What a restore of one entry does to its wipe clock (the dialog's lines; the server decides with the same code).</summary>
internal sealed class RestoreWipePlan
{
    /// <summary>The restored world's wipe date under the next start's settings has passed (or its start date is unknown).</summary>
    public bool Due { get; set; }

    /// <summary>The wipe date that has passed (when known).</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>The chosen mode, after the checks.</summary>
    public string Mode { get; set; } = WipeModes.None;

    public int IntervalDays { get; set; }

    /// <summary><c>Any</c> to write, or empty to keep <c>DayOfReset</c>.</summary>
    public string DayOfReset { get; set; } = "";

    /// <summary>The next wipe after the restore, or null when wipes are off.</summary>
    public DateTime? NextWipe { get; set; }

    /// <summary>The restored world's start date after the restore (today when the clock restarts).</summary>
    public DateTime? StartAfter { get; set; }

    public string Error { get; set; } = "";
}

/// <summary>
/// WORLDRESTORE1: shared rules of the Backups list and the restore dialog (game-free): the wipe plan for a chosen entry and
/// wipe choice, and the sentences both sides draw.
/// </summary>
internal static class ServerBackups
{
    internal const string LocalFormat = "yyyy-MM-ddTHH:mm:ss";

    /// <summary>
    /// BACKUPROWS1: the reason the server's Backups push gives when a manual or scheduled backup finished (the restore request
    /// did not change): the client asks once, so the Last backup and Backups rows show the result.
    /// </summary>
    internal const string BackupFinishedWhy = "backup finished";
    internal const int MaxEntries = 20;

    /// <summary>The setting ids of the lock list (<c>[ServerSettings] LockedInGame</c>): group <c>server-rules</c> locks both.</summary>
    internal static string RestoreSettingId => ServerRules.Id("WorldRestore");

    internal static string KeepSettingId => ServerRules.Id("KeepBackups");

    /// <summary>BACKUPNOW1: the lock ids of Back up now and the four backup settings (group <c>server-rules</c> locks them all).</summary>
    internal static string BackupNowSettingId => ServerRules.Id("BackupNow");

    internal static string AtStartSettingId => ServerRules.Id(WorldBackupSettings.AtStartKey);
    internal static string EveryHoursSettingId => ServerRules.Id(WorldBackupSettings.EveryHoursKey);
    internal static string KeepCountSettingId => ServerRules.Id(WorldBackupSettings.KeepCountKey);
    internal static string KeepDaysSettingId => ServerRules.Id(WorldBackupSettings.KeepDaysKey);

    /// <summary>The lock id of a backup setting's key, or null when the key is not one.</summary>
    internal static string? SettingIdOf(string? key) => key switch
    {
        WorldBackupSettings.AtStartKey => AtStartSettingId,
        WorldBackupSettings.EveryHoursKey => EveryHoursSettingId,
        WorldBackupSettings.KeepCountKey => KeepCountSettingId,
        WorldBackupSettings.KeepDaysKey => KeepDaysSettingId,
        _ => null,
    };

    internal const string TypedWord = "RESTORE";

    internal const string WipeClockLabel = "Restart the wipe clock from today";
    internal const string WipesOffLabel = "Turn wipes off";
    internal const string PickDateLabel = "Pick a new wipe date";

    /// <summary>
    /// The plan for restoring a world that started on <paramref name="start"/> with the wipe choice
    /// <paramref name="mode"/> (and <paramref name="picked"/> for a new date), under the next start's interval and day.
    /// <see cref="RestoreWipePlan.Error"/> says why the choice is refused (a due wipe needs one of the three; an interval
    /// choice needs a writable interval).
    /// </summary>
    internal static RestoreWipePlan Plan(DateTime? start, string? mode, DateTime? picked, int interval, string? day, bool intervalWritable,
        string intervalReason, DateTime nowLocal)
    {
        var plan = new RestoreWipePlan { Mode = string.IsNullOrEmpty(mode) ? WipeModes.None : mode!, StartAfter = start };
        plan.Due = WorldRestoreWipe.IsDue(start, interval, day, nowLocal, out var dueDate);
        plan.DueDate = dueDate;
        if (!WipeModes.IsKnown(plan.Mode))
        {
            plan.Error = "Unknown wipe choice.";
            return plan;
        }

        if (!plan.Due)
        {
            if (plan.Mode != WipeModes.None)
            {
                plan.Error = "No wipe choice is needed: the restored world's wipe date has not passed.";
                return plan;
            }

            plan.NextWipe = start is { } s ? WorldRestoreWipe.ResetDate(s, interval, day) : null;
            return plan;
        }

        switch (plan.Mode)
        {
            case WipeModes.None:
                plan.Error = "This world's wipe date has passed, so the next start would wipe it again. Choose: restart the wipe clock "
                    + "from today, turn wipes off, or pick a new wipe date.";
                return plan;
            case WipeModes.ClockToday:
                plan.StartAfter = nowLocal.Date;
                plan.NextWipe = WorldRestoreWipe.ResetDate(nowLocal.Date, interval, day);
                return plan;
            case WipeModes.WipesOff:
                if (!intervalWritable)
                {
                    plan.Error = "Wipes cannot be turned off here: " + intervalReason;
                    return plan;
                }

                plan.IntervalDays = 0;
                plan.NextWipe = null;
                return plan;
            default:
            {
                if (!intervalWritable)
                {
                    plan.Error = "The wipe date cannot be changed here: " + intervalReason;
                    return plan;
                }

                if (start is not { } s)
                {
                    plan.Error = "The world's start date is not known, so a wipe date cannot be set from it.";
                    return plan;
                }

                if (picked is not { } p)
                {
                    plan.Error = "Pick the new wipe date.";
                    return plan;
                }

                var fit = WorldRestoreWipe.ForDate(s, p, day, nowLocal, ServerRules.ResetDaysMax, out string error);
                if (fit is not { } f)
                {
                    plan.Error = error;
                    return plan;
                }

                plan.IntervalDays = f.Interval;
                plan.DayOfReset = f.Day;
                plan.NextWipe = WorldRestoreWipe.ResetDate(s, f.Interval, f.Day.Length > 0 ? f.Day : day);
                return plan;
            }
        }
    }

    /// <summary>"Wed, Oct 1, 2026" / "Off".</summary>
    internal static string NextWipeText(RestoreWipePlan plan) =>
        plan.NextWipe is { } d ? WorldBackupCatalog.FormatDay(d) + " (the first server start on or after it)" : "Off (no scheduled wipe)";

    /// <summary>"day 34" / "age unknown".</summary>
    internal static string AgeText(int? days) => days is int d ? "day " + d.ToString(CultureInfo.InvariantCulture) : "age unknown";

    /// <summary>LISTROWS1: an entry's first list line, the kind and when it was made: <c>Wipe archive · Wed, Oct 1, 2026 3:12 PM</c>.</summary>
    internal static string EntryTitle(ServerBackupView e) =>
        e.KindLabel + " · " + (e.CreatedTime is { } t ? WorldBackupCatalog.FormatTime(t) : "date unknown");

    /// <summary>LISTROWS1: an entry's muted second line, the world age and the size: <c>World age: day 34 · 82.4 MB</c>.</summary>
    internal static string EntryNote(ServerBackupView e) =>
        (e.AgeDays is int d ? "World age: day " + d.ToString(CultureInfo.InvariantCulture) : "World age unknown")
        + " · " + WorldBackupCatalog.FormatBytes(e.Size) + (e.Restorable ? "" : " · cannot be restored");

    /// <summary>"3 restore points, 246 MB" / "None yet".</summary>
    internal static string Summary(ServerBackupsState? s)
    {
        if (s == null) return "…";
        if (s.Count == 0) return "No restore points yet";
        return s.Count.ToString(CultureInfo.InvariantCulture) + (s.Count == 1 ? " restore point, " : " restore points, ")
            + WorldBackupCatalog.FormatBytes(s.ArchiveBytes + s.BackupBytes);
    }

    /// <summary>LISTROWS1: the list's Disk use value: <c>Restore points 186 MB · current world 22 MB · free 360 GB</c>.</summary>
    internal static string DiskLine(ServerBackupsState s) =>
        "Restore points " + WorldBackupCatalog.FormatBytes(s.ArchiveBytes + s.BackupBytes) + " · current world "
        + WorldBackupCatalog.FormatBytes(s.CurrentBytes) + (s.FreeBytes >= 0 ? " · free " + WorldBackupCatalog.FormatBytes(s.FreeBytes) : "");

    /// <summary>LISTROWS1: the list's Kept value: <c>newest 2 Progenitor backups; wipe archives are never deleted</c>.</summary>
    internal static string RetentionLine(ServerBackupsState s) =>
        (s.Keep == 0 ? "every backup made before a restore" : "newest " + s.Keep.ToString(CultureInfo.InvariantCulture) + " backups made before a restore")
        + "; wipe archives are never deleted";

    internal static string WipeModeText(string? mode, string? date) => mode switch
    {
        WipeModes.ClockToday => "the wipe clock restarts from that day",
        WipeModes.WipesOff => "scheduled wipes off",
        WipeModes.Date => "next wipe " + (DateTime.TryParseExact(date ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? WorldBackupCatalog.FormatDay(d) : date),
        _ => "",
    };

    // ---- BACKUPSDLG1: which server and world a dialog is about; a pending request that changed elsewhere ----------

    /// <summary>
    /// A notice's body named for its server: <c>On My server: world world1 goes back to …</c>. The server's own sentence
    /// keeps its words; only its first letter is lowered after the prefix (not an acronym). Unchanged when the name is unknown.
    /// </summary>
    internal static string OnServer(string? server, string? text)
    {
        string t = (text ?? "").Trim();
        string name = (server ?? "").Trim();
        if (name.Length == 0 || t.Length == 0) return t;
        return "On " + name + ": " + LowerFirst(t);
    }

    /// <summary>"My server, world world1" / "My server" / "world world1" / empty.</summary>
    internal static string WhereLine(string? server, string? world)
    {
        string name = (server ?? "").Trim();
        string save = (world ?? "").Trim();
        if (save.Length == 0) return name;
        return name.Length == 0 ? "world " + save : name + ", world " + save;
    }

    /// <summary>The first letter raised ("world world1" → "World world1").</summary>
    internal static string Capitalize(string text) =>
        string.IsNullOrEmpty(text) ? text ?? "" : char.ToUpperInvariant(text[0]) + text.Substring(1);

    /// <summary>The first letter lowered, unless the first word is an acronym (its second letter is upper case too).</summary>
    internal static string LowerFirst(string text)
    {
        if (string.IsNullOrEmpty(text) || !char.IsUpper(text[0])) return text ?? "";
        if (text.Length > 1 && char.IsUpper(text[1])) return text;
        return char.ToLowerInvariant(text[0]) + text.Substring(1);
    }

    /// <summary>The pending request as a dialog remembers it: its id and whether a restart countdown runs.</summary>
    internal static string PendingSignature(string? pendingId, bool countdown) => (pendingId ?? "") + (countdown ? "|countdown" : "");

    internal static string PendingSignature(ServerBackupsState? s) => s == null ? "" : PendingSignature(s.PendingId, s.Countdown >= 0);

    /// <summary>The note an open view shows after the pending request changed elsewhere.</summary>
    internal static string ChangedNote(ServerBackupsState s)
    {
        string line = PendingLine(s);
        return "Changed on the server: " + (line.Length > 0 ? LowerFirst(line) : "no restore is pending now.");
    }

    /// <summary>"Restore of Wipe archive from … at the next restart (scheduled wipes off)."</summary>
    internal static string PendingLine(ServerBackupsState? s)
    {
        if (s == null || s.PendingId.Length == 0) return "";
        string wipe = WipeModeText(s.PendingWipe, s.PendingWipeDate);
        string when = s.Countdown >= 0 ? "when the server stops in " + s.Countdown + " s" : "at the next restart";
        return "Restore of " + s.PendingName + " " + when + (wipe.Length > 0 ? " (" + wipe + ")" : "") + ".";
    }

    internal static string ResultLine(ServerBackupsState? s) => s == null || s.ResultId.Length == 0 ? "" : s.ResultSummary;

    // ---- BACKUPNOW1 -------------------------------------------------------------------------------------

    /// <summary>
    /// The list's group of an entry: 0 manual, 1 scheduled, 2 before a restore (the restore chain), 3 wipe archives,
    /// 4 (AUTOUPDATE3) before an update. <see cref="GroupOrder"/> is the order the list shows them in.
    /// </summary>
    internal static int GroupOf(ServerBackupView e) =>
        string.Equals(e.Kind, WorldBackupEntry.KindArchive, StringComparison.Ordinal) ? 3
        : e.BackupKind == WorldRestorePaths.KindManual ? 0
        : e.BackupKind == WorldRestorePaths.KindScheduled ? 1
        : e.BackupKind == WorldRestorePaths.KindPreUpdate ? GroupPreUpdate
        : 2;

    internal static int GroupOf(WorldBackupEntry e) =>
        e.Kind == WorldBackupEntry.KindArchive ? 3 : e.BackupKind == WorldRestorePaths.KindManual ? 0 : e.BackupKind == WorldRestorePaths.KindScheduled ? 1
        : e.BackupKind == WorldRestorePaths.KindPreUpdate ? GroupPreUpdate : 2;

    /// <summary>AUTOUPDATE3: the group of the backups made before an update.</summary>
    internal const int GroupPreUpdate = 4;

    /// <summary>AUTOUPDATE3: the list's group order: manual, scheduled, before an update, before a restore, wipe archives.</summary>
    internal static readonly int[] GroupOrder = { 0, 1, GroupPreUpdate, 2, 3 };

    /// <summary>
    /// The newest <paramref name="max"/> of <paramref name="newestFirst"/>, except that every group present keeps its newest
    /// entry (an hourly schedule never hides the one manual backup): a missing group's newest replaces the oldest pick of
    /// the largest group. Returned in <paramref name="newestFirst"/>'s order.
    /// </summary>
    internal static List<T> PickNewest<T>(IReadOnlyList<T> newestFirst, Func<T, int> group, int max)
    {
        var chosen = new HashSet<int>();
        for (int i = 0; i < newestFirst.Count && i < max; i++) chosen.Add(i);
        if (newestFirst.Count > max && max > 0)
        {
            var seen = new HashSet<int>();
            foreach (int i in chosen) seen.Add(group(newestFirst[i]));
            for (int i = 0; i < newestFirst.Count; i++)
            {
                int g = group(newestFirst[i]);
                if (!seen.Add(g)) continue;
                // The largest group gives up its oldest pick (a group of one never does).
                var counts = new Dictionary<int, int>();
                foreach (int c in chosen) counts[group(newestFirst[c])] = counts.TryGetValue(group(newestFirst[c]), out int n) ? n + 1 : 1;
                int largest = -1, most = 0;
                foreach (var (k, n) in counts)
                    if (n > most) { most = n; largest = k; }
                if (most <= 1) break;
                int drop = -1;
                foreach (int c in chosen)
                    if (group(newestFirst[c]) == largest && c > drop) drop = c;
                chosen.Remove(drop);
                chosen.Add(i);
            }
        }

        var list = new List<T>(chosen.Count);
        for (int i = 0; i < newestFirst.Count; i++)
            if (chosen.Contains(i)) list.Add(newestFirst[i]);
        return list;
    }

    /// <summary>
    /// The disk use of each kind that has any: <c>manual 40 MB · scheduled 120 MB · before restore 82 MB · wipe archives 100 MB</c>.
    /// </summary>
    internal static string KindsLine(ServerBackupsState s)
    {
        var parts = new List<string>();
        long chain = Math.Max(0, s.BackupBytes - s.ManualBytes - s.ScheduledBytes - s.PreUpdateBytes);
        if (s.ManualBytes > 0) parts.Add("manual " + WorldBackupCatalog.FormatBytes(s.ManualBytes));
        if (s.ScheduledBytes > 0) parts.Add("scheduled " + WorldBackupCatalog.FormatBytes(s.ScheduledBytes));
        if (s.PreUpdateBytes > 0) parts.Add("before update " + WorldBackupCatalog.FormatBytes(s.PreUpdateBytes));
        if (chain > 0) parts.Add("before restore " + WorldBackupCatalog.FormatBytes(chain));
        if (s.ArchiveBytes > 0) parts.Add("wipe archives " + WorldBackupCatalog.FormatBytes(s.ArchiveBytes));
        return string.Join(" · ", parts);
    }

    /// <summary><c>the newest 10 of each kind, none older than 14 days; the newest of each kind always stays</c>.</summary>
    internal static string BackupRetentionLine(ServerBackupsState s)
    {
        var p = s.Policy;
        return p.KeepsAll ? "every manual and scheduled backup" : p.Text() + "; the newest of each kind always stays";
    }

    /// <summary><c>at every server start and every 6 hours; next Wed, Oct 7, 2026 3:00 PM</c> / <c>off</c>.</summary>
    internal static string ScheduleLine(ServerBackupsState s)
    {
        string text = s.Settings.ScheduleText();
        if (s.EveryHours > 0 && DateTime.TryParseExact(s.NextScheduled, LocalFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var next))
            text += "; next " + WorldBackupCatalog.FormatTime(next);
        return text;
    }

    /// <summary>
    /// The Last backup line: <c>Manual backup by Ann: copying 12 of 40 files (35 %)…</c> while one runs, else the last result's
    /// sentence; empty when there is neither.
    /// </summary>
    internal static string BackupLine(ServerBackupsState? s)
    {
        if (s == null) return "";
        if (s.JobRunning)
            return (s.JobKind == WorldRestorePaths.KindScheduled ? "Scheduled backup"
                    : s.JobKind == WorldRestorePaths.KindPreUpdate ? "Backup before an update" : "Manual backup")
                + (s.JobBy.Length > 0 && s.JobKind == WorldRestorePaths.KindManual ? " by " + s.JobBy : "") + ": " + s.JobPhase + "…";
        return s.LastBackupSummary;
    }

    /// <summary>Why Back up now cannot run, or null (a backup running is a refusal too).</summary>
    internal static string? BackupNowRefusal(ServerBackupsState? s)
    {
        if (s == null) return "The server has not answered yet.";
        if (!s.BackupAllowed) return s.BackupRefusal.Length > 0 ? s.BackupRefusal : "Backups cannot be made here.";
        if (s.JobRunning) return "A backup is already running (" + s.JobPhase + ").";
        return null;
    }

    /// <summary>The game's autosaves: <c>4 files, 330 MB; the game keeps 10, one every 2 minutes</c>.</summary>
    internal static string AutosaveLine(ServerBackupsState s)
    {
        if (!s.AutosaveKnown) return "";
        string files = s.AutosaveCount == 0
            ? "none yet"
            : s.AutosaveCount.ToString(CultureInfo.InvariantCulture) + (s.AutosaveCount == 1 ? " file, " : " files, ") + WorldBackupCatalog.FormatBytes(s.AutosaveBytes);
        var rule = new List<string>();
        if (s.AutosaveKeep >= 0) rule.Add("the game keeps " + s.AutosaveKeep.ToString(CultureInfo.InvariantCulture));
        if (s.AutosaveEvery > 0) rule.Add("one every " + Seconds(s.AutosaveEvery));
        return files + (rule.Count > 0 ? "; " + string.Join(", ", rule) : "");
    }

    /// <summary>The autosaves' times: <c>Wed, Oct 7, 2026 1:00 PM to 1:08 PM</c> (oldest to newest), or empty.</summary>
    internal static string AutosaveSpan(ServerBackupsState s)
    {
        bool o = DateTime.TryParseExact(s.AutosaveOldest, LocalFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var a);
        bool n = DateTime.TryParseExact(s.AutosaveNewest, LocalFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var b);
        if (!s.AutosaveKnown || !n) return "";
        if (!o || a == b) return WorldBackupCatalog.FormatTime(b);
        return WorldBackupCatalog.FormatTime(a) + " to " + (a.Date == b.Date ? b.ToString("h:mm tt", WorldBackupCatalog.English) : WorldBackupCatalog.FormatTime(b));
    }

    /// <summary><c>2 minutes</c>, <c>90 seconds</c>, <c>1 hour</c>.</summary>
    internal static string Seconds(int s)
    {
        if (s % 3600 == 0) return s == 3600 ? "hour" : (s / 3600).ToString(CultureInfo.InvariantCulture) + " hours";
        if (s % 60 == 0) return s == 60 ? "minute" : (s / 60).ToString(CultureInfo.InvariantCulture) + " minutes";
        return s.ToString(CultureInfo.InvariantCulture) + " seconds";
    }

    /// <summary>
    /// BACKUPNOTE1: the comparison in the Backups list header, once, when the game's autosaves are known: two label/value lines
    /// (a centered note was cut to one line at the narrowest dialog). What the game's autosaves are:
    /// </summary>
    internal const string AutosavesAreText = "Save files only, rotated in the save folder; no mod data of that moment; not restorable here yet.";

    /// <summary>BACKUPNOTE1: what Progenitor backups add to them.</summary>
    internal const string BackupsAddText = "The whole world folder with mod data and start date, checked file by file, kept by your rule, restorable.";

    /// <summary>What a backup holds, for the confirmation and the docs.</summary>
    internal const string IncludesText =
        "The whole world folder: its saves, StartDate.json, and the mod data Progenitor keeps for it (the progenitor folder, ModSave). "
        + "Not included: other mods' files outside the world folder (their own files, BepInEx config).";
}
