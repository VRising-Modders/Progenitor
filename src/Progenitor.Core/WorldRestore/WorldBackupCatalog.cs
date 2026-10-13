using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Progenitor.WorldRestore;

/// <summary>One restore point of the current save.</summary>
internal sealed class WorldBackupEntry
{
    internal const string KindArchive = "archive";
    internal const string KindBackup = "backup";

    /// <summary><c>archive:&lt;folder&gt;</c> or <c>backup:&lt;folder&gt;</c>: what a request names (never a path from a client).</summary>
    public string Id { get; set; } = "";

    public string Kind { get; set; } = "";
    public string FolderName { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>When it was made: the wipe (archive, from its ticks) or the backup (its sidecar), local time.</summary>
    public DateTime CreatedLocal { get; set; }

    /// <summary>
    /// When it was made, UTC: a backup's sidecar time, or an archive's wipe time read in the catalog's zone. Default (unset)
    /// for an entry built by hand; <see cref="CreatedUtcOr"/> then converts <see cref="CreatedLocal"/>.
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary><see cref="CreatedUtc"/>, else <see cref="CreatedLocal"/> read in <paramref name="zone"/> (null: the machine's zone).</summary>
    internal DateTime CreatedUtcOr(TimeZoneInfo? zone) =>
        CreatedUtc != default ? DateTime.SpecifyKind(CreatedUtc, DateTimeKind.Utc) : WorldTime.ToUtc(CreatedLocal, zone);

    /// <summary>The newest save file's write time (UTC), or null when the folder has none.</summary>
    public DateTime? LastPlayedUtc { get; set; }

    public DateTime? StartDate { get; set; }

    /// <summary>The world's age at its newest save: days from <see cref="StartDate"/>, or null.</summary>
    public int? AgeDays { get; set; }

    public long SizeBytes { get; set; }
    public int Files { get; set; }

    /// <summary><see cref="WorldManifest.ShapeHash"/> of the restorable files.</summary>
    public string Shape { get; set; } = "";

    public bool Restorable { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>A Progenitor backup's sidecar kind (<c>before-restore</c>), or empty.</summary>
    public string BackupKind { get; set; } = "";

    /// <summary>A Progenitor backup: what was restored over the world it holds.</summary>
    public string RestoreOf { get; set; } = "";

    /// <summary>A Progenitor backup's restore chain (<see cref="BackupSidecar.ChainId"/>; empty for a backup older than chains).</summary>
    public string ChainId { get; set; } = "";

    /// <summary>BACKUPNOW1: a manual or scheduled backup's trigger (<c>manual</c>, <c>start</c>, <c>interval</c>), or empty.</summary>
    public string Trigger { get; set; } = "";

    /// <summary>AUTOUPDATE3: a pre-update backup's updates (<c>ChatSkin 1.3.0, BroaderMap 2.0.2</c>), or empty.</summary>
    public string Updates { get; set; } = "";

    /// <summary>AUTOUPDATE3: a backup the server made before applying mod updates (its own keep count).</summary>
    public bool IsPreUpdate => Kind == KindBackup && string.Equals(BackupKind, WorldRestorePaths.KindPreUpdate, StringComparison.Ordinal);

    /// <summary>BACKUPNOW1: a manual or scheduled backup (its own retention), not one of the restore chain.</summary>
    public bool IsMadeBackup => Kind == KindBackup && WorldRestorePaths.IsMadeBackupKind(BackupKind);

    /// <summary>A Progenitor backup of the restore chain (<c>before-restore</c>, a replaced world): the keep count's.</summary>
    public bool IsRestoreChain => Kind == KindBackup && !WorldRestorePaths.IsMadeBackupKind(BackupKind);

    /// <summary>"Wipe archive", "Manual backup", "Scheduled backup", "Before update" or "Before restore".</summary>
    public string KindLabel => KindLabelOf(Kind, BackupKind);

    /// <summary>
    /// AUTOUPDATE3: the kind as the Backups list's row reads it: a pre-update backup names the updates it came before
    /// (<c>Before update · ChatSkin 1.3.0, BroaderMap 2.0.2</c>); every other kind is <see cref="KindLabel"/>.
    /// </summary>
    public string RowLabel => IsPreUpdate && Updates.Length > 0 ? KindLabel + " · " + Updates : KindLabel;

    internal static string KindLabelOf(string kind, string? backupKind) =>
        kind == KindArchive ? "Wipe archive"
        : backupKind == WorldRestorePaths.KindManual ? "Manual backup"
        : backupKind == WorldRestorePaths.KindScheduled ? "Scheduled backup"
        : backupKind == WorldRestorePaths.KindPreUpdate ? "Before update"
        : "Before restore";

    /// <summary>
    /// WORLDRESTORE2 (L7): the short label a sentence and the next backup's sidecar use, never nested:
    /// <c>the wipe archive from Wed, Sep 30, 2026 4:00 AM</c>, <c>the manual backup from …</c>, <c>the scheduled backup from
    /// …</c> or <c>the backup from Wed, Oct 7, 2026 9:30 AM</c>.
    /// </summary>
    public string BaseLabel =>
        (Kind == KindArchive ? "the wipe archive from "
            : BackupKind == WorldRestorePaths.KindManual ? "the manual backup from "
            : BackupKind == WorldRestorePaths.KindScheduled ? "the scheduled backup from "
            : BackupKind == WorldRestorePaths.KindPreUpdate ? "the backup made before an update from "
            : "the backup from ") + WorldBackupCatalog.FormatTime(CreatedLocal);

    /// <summary>
    /// How the entry reads in the list and as a dialog title: <c>Wipe archive from Wed, Sep 30, 2026 4:00 AM</c>,
    /// <c>Manual backup from …</c>, <c>Scheduled backup from …</c>, or <c>Before restore of the wipe archive from …, Wed, Oct 7,
    /// 2026 9:30 AM</c>.
    /// </summary>
    public string DisplayName =>
        Kind == KindArchive
            ? "Wipe archive from " + WorldBackupCatalog.FormatTime(CreatedLocal)
            : IsMadeBackup
                ? RowLabel + " from " + WorldBackupCatalog.FormatTime(CreatedLocal)
                : "Before restore of " + (RestoreOf.Length > 0 ? RestoreOf : "an earlier world") + ", " + WorldBackupCatalog.FormatTime(CreatedLocal);
}

/// <summary>
/// WORLDRESTORE1: the restore points of a save (spec §1.2, §1.6, §4.1): the game's wipe archives beside it and Progenitor's
/// backups under <c>progenitor-backups</c>, newest first, with their sizes and world ages; and the retention rule (user
/// decision 2026-10-06): keep the newest N Progenitor backups, never delete a wipe archive or a game <c>.BACKUP</c> copy.
/// File-only.
/// </summary>
internal static class WorldBackupCatalog
{
    internal const int DefaultKeep = 5;
    internal const int MaxKeep = 100;

    internal static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    internal static string FormatDay(DateTime local) => local.ToString("ddd, MMM d, yyyy", English);

    internal static string FormatTime(DateTime local) => local.ToString("ddd, MMM d, yyyy h:mm tt", English);

    /// <summary>
    /// The restore points of the save at <paramref name="layout"/>, newest first, their times read in <paramref name="zone"/>
    /// (null: the machine's zone; the apply and backup paths pass <see cref="RestoreEnvironment.Zone"/>).
    /// </summary>
    internal static List<WorldBackupEntry> List(IWorldFs fs, WorldRestorePaths.SaveLayout layout, TimeZoneInfo? zone = null)
    {
        var list = new List<WorldBackupEntry>();
        foreach (string dir in SafeDirs(fs, layout.VersionRoot))
        {
            string name = System.IO.Path.GetFileName(dir);
            if (!WorldRestorePaths.IsArchiveName(layout.SaveName, name, out long ticks)) continue;
            if (WorldRestorePaths.ArchiveTime(ticks) is not { } when) continue;
            var e = Describe(fs, dir, WorldBackupEntry.KindArchive, null, zone);
            e.CreatedLocal = when;
            e.CreatedUtc = WorldTime.ToUtc(when, zone);
            list.Add(e);
        }

        foreach (string dir in SafeDirs(fs, layout.BackupsRoot))
        {
            string name = System.IO.Path.GetFileName(dir);
            if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith(WorldRestorePaths.PartialSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            // .deleting-* starts with a dot too: retention's half-deleted folders are never listed.
            BackupSidecar? sidecar = null;
            string? sidecarPath = SidecarPath(fs, dir);
            try { if (sidecarPath != null) sidecar = BackupSidecar.Parse(fs.ReadAllBytes(sidecarPath)); }
            catch { sidecar = null; }
            if (sidecar == null) continue;
            var e = Describe(fs, dir, WorldBackupEntry.KindBackup, sidecar, zone);
            e.CreatedLocal = sidecar.CreatedUtc == default ? DateTime.MinValue : WorldTime.ToLocal(sidecar.CreatedUtc, zone);
            e.CreatedUtc = sidecar.CreatedUtc == default ? default : DateTime.SpecifyKind(sidecar.CreatedUtc, DateTimeKind.Utc);
            e.BackupKind = sidecar.Kind;
            e.RestoreOf = sidecar.RestoreOf;
            e.ChainId = sidecar.ChainId;
            e.Trigger = sidecar.Trigger;
            e.Updates = sidecar.Updates;
            list.Add(e);
        }

        list.Sort((a, b) => b.CreatedLocal.CompareTo(a.CreatedLocal));
        return list;
    }

    private static IReadOnlyList<string> SafeDirs(IWorldFs fs, string path)
    {
        try { return fs.ListDirectories(path); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// A folder as a restore point: its files (minus <c>.TEMP</c> and the sidecar), start date, age (the day of its newest
    /// save in <paramref name="zone"/>; null: the machine's zone) and size.
    /// </summary>
    internal static WorldBackupEntry Describe(IWorldFs fs, string dir, string kind, BackupSidecar? sidecar, TimeZoneInfo? zone = null)
    {
        string name = System.IO.Path.GetFileName(dir);
        var e = new WorldBackupEntry { Id = kind + ":" + name, Kind = kind, FolderName = name, Path = dir };
        List<WorldManifestEntry> files;
        try { files = WorldManifest.Build(fs, dir, hash: false, SkipSidecar); }
        catch (Exception ex)
        {
            e.Reason = "The folder could not be read (" + ex.Message + ").";
            return e;
        }

        e.Files = files.Count;
        e.SizeBytes = WorldManifest.TotalBytes(files);
        e.Shape = WorldManifest.ShapeHash(files);
        var newest = WorldManifest.NewestSave(files);
        e.LastPlayedUtc = newest?.LastWriteUtc;
        e.StartDate = ReadStartDate(fs, dir) ?? WorldRestoreWipe.ParseStartDate(sidecar?.StartDate);
        if (e.StartDate is { } start && e.LastPlayedUtc is { } played)
            e.AgeDays = Math.Max(0, (int)(WorldTime.ToLocal(played, zone).Date - start).TotalDays);
        if (newest == null) e.Reason = "It holds no save file.";
        else e.Restorable = true;
        return e;
    }

    internal static bool SkipSidecar(string relative) => WorldRestorePaths.IsSidecarName(relative);

    /// <summary>The backup folder's sidecar file (the usual name, else a pre-update backup's), or null when it has none.</summary>
    internal static string? SidecarPath(IWorldFs fs, string dir)
    {
        foreach (string name in new[] { WorldRestorePaths.SidecarName, WorldRestorePaths.PreUpdateSidecarName })
        {
            string path = System.IO.Path.Combine(dir, name);
            try { if (fs.FileExists(path)) return path; }
            catch { /* next */ }
        }

        return null;
    }

    internal static DateTime? ReadStartDate(IWorldFs fs, string dir)
    {
        string path = System.IO.Path.Combine(dir, WorldRestorePaths.StartDateName);
        try { return fs.FileExists(path) ? WorldRestoreWipe.ParseStartDate(WorldJson.Text(fs.ReadAllBytes(path))) : null; }
        catch { return null; }
    }

    /// <summary>The total size of the entries of one kind (null: all).</summary>
    internal static long Bytes(IEnumerable<WorldBackupEntry> entries, string? kind = null)
    {
        long n = 0;
        foreach (var e in entries)
            if (kind == null || e.Kind == kind) n += e.SizeBytes;
        return n;
    }

    /// <summary>BACKUPNOW1: the total size of the Progenitor backups of one sidecar kind (manual, scheduled), or of the restore chain (null).</summary>
    internal static long BackupBytes(IEnumerable<WorldBackupEntry> entries, string? backupKind)
    {
        long n = 0;
        foreach (var e in entries)
        {
            if (e.Kind != WorldBackupEntry.KindBackup) continue;
            if (backupKind == null ? e.IsRestoreChain : string.Equals(e.BackupKind, backupKind, StringComparison.Ordinal)) n += e.SizeBytes;
        }

        return n;
    }

    /// <summary>The smallest keep count other than 0 (keep all): one backup could be the world a restore just replaced.</summary>
    internal const int MinKeep = 2;

    /// <summary>A keep count: 0 (keep all) or <see cref="MinKeep"/> to <see cref="MaxKeep"/>.</summary>
    internal static int ClampKeep(int keep) => keep < 0 ? DefaultKeep : keep == 0 ? 0 : keep < MinKeep ? MinKeep : keep > MaxKeep ? MaxKeep : keep;

    /// <summary>
    /// WORLDRESTORE2 (M1): what retention deletes. The newest <paramref name="keep"/> Progenitor backups stay (0 keeps all), and
    /// so does every protected one: the paths in <paramref name="protect"/> (the backup a restore just made, the source of a
    /// pending request) and the oldest backup of each restore chain (the world before the first restore of that chain). Wipe
    /// archives are never on the list.
    /// </summary>
    internal static List<WorldBackupEntry> PruneList(IReadOnlyList<WorldBackupEntry> entries, int keep, params string?[] protect)
    {
        var result = new List<WorldBackupEntry>();
        keep = ClampKeep(keep);
        if (keep == 0) return result;
        var backups = new List<WorldBackupEntry>();
        // BACKUPNOW1: manual and scheduled backups have their own retention (WorldBackupRetention); the keep count is the
        // restore chain's.
        foreach (var e in entries)
            if (e.IsRestoreChain) backups.Add(e);
        backups.Sort((a, b) => b.CreatedLocal.CompareTo(a.CreatedLocal));
        var oldestOfChain = new Dictionary<string, WorldBackupEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in backups)
        {
            oldestOfChain[e.ChainId ?? ""] = e;
        }

        for (int i = keep; i < backups.Count; i++)
        {
            var e = backups[i];
            bool kept = oldestOfChain.ContainsValue(e);
            foreach (string? p in protect ?? Array.Empty<string?>())
                if (WorldRestorePaths.SamePath(e.Path, p)) kept = true;
            if (!kept) result.Add(e);
        }

        return result;
    }

    /// <summary><c>82.4 MB</c>.</summary>
    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return kb.ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return mb.ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        return (mb / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
    }
}

/// <summary>
/// The world backup and restore path's conversion between UTC and local time. Callers pass the zone their environment
/// names (<see cref="RestoreEnvironment.Zone"/>, <c>ServerBackupsServer.Zone</c>). Null or the machine's own zone (what
/// production passes) converts exactly as <see cref="DateTime.ToLocalTime"/> and <see cref="DateTime.ToUniversalTime"/>
/// do; tests pass a fixed zone so the suite reads the same in every machine zone.
/// </summary>
internal static class WorldTime
{
    /// <summary><paramref name="utc"/> (Unspecified counts as UTC, as <see cref="DateTime.ToLocalTime"/> reads it) in <paramref name="zone"/>.</summary>
    internal static DateTime ToLocal(DateTime utc, TimeZoneInfo? zone)
    {
        if (zone == null || ReferenceEquals(zone, TimeZoneInfo.Local)) return utc.ToLocalTime();
        if (utc.Kind == DateTimeKind.Local) utc = utc.ToUniversalTime();
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
    }

    /// <summary>
    /// A wall-clock time of <paramref name="zone"/> as UTC (a UTC value is returned as is). A time the zone skips (its clock
    /// moved forward) reads with the zone's standard offset rather than throw.
    /// </summary>
    internal static DateTime ToUtc(DateTime local, TimeZoneInfo? zone)
    {
        if (local.Kind == DateTimeKind.Utc) return local;
        if (zone == null || ReferenceEquals(zone, TimeZoneInfo.Local)) return DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall)) return DateTime.SpecifyKind(wall - zone.BaseUtcOffset, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeToUtc(wall, zone);
    }
}

/// <summary>BACKUPNOW1: the game's own autosaves in a save folder (read-only, for the Backups list's comparison).</summary>
internal sealed class AutosaveSummary
{
    /// <summary>The save folder is missing or could not be read.</summary>
    public bool Missing { get; set; }

    public int Count { get; set; }
    public long Bytes { get; set; }
    public DateTime? NewestUtc { get; set; }
    public DateTime? OldestUtc { get; set; }
}

/// <summary>
/// BACKUPNOW1: the game's autosaves: top-level <c>AutoSave_&lt;N&gt;.save.gz</c> (or <c>.save</c>) files of the save folder. The
/// game rotates them (it keeps the newest <c>AutoSaveCount</c> by write time and deletes the rest, after its
/// <c>AutoSaveSmartKeep</c> buckets); named saves are not autosaves. File-only.
/// </summary>
internal static class WorldAutosaves
{
    internal const string Prefix = "AutoSave_";

    internal static bool IsAutosave(string relative) =>
        WorldRestorePaths.CountsAsSaveFile(relative) && relative.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    internal static AutosaveSummary Summarize(IWorldFs fs, string? saveFolder)
    {
        var s = new AutosaveSummary();
        try
        {
            if (string.IsNullOrEmpty(saveFolder) || !fs.DirectoryExists(saveFolder!))
            {
                s.Missing = true;
                return s;
            }

            foreach (var f in fs.ListFiles(saveFolder!))
            {
                if (!IsAutosave(f.Relative)) continue;
                s.Count++;
                s.Bytes += f.Length;
                if (s.NewestUtc == null || f.LastWriteUtc > s.NewestUtc) s.NewestUtc = f.LastWriteUtc;
                if (s.OldestUtc == null || f.LastWriteUtc < s.OldestUtc) s.OldestUtc = f.LastWriteUtc;
            }
        }
        catch
        {
            s = new AutosaveSummary { Missing = true };
        }

        return s;
    }
}
