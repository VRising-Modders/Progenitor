using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Progenitor.WorldRestore;

/// <summary>
/// BACKUPNOW1: the retention of manual and scheduled backups: keep the newest <see cref="KeepCount"/> of each kind and/or keep
/// those younger than <see cref="KeepDays"/> days. 0 turns a rule off; both 0 keeps every one.
/// </summary>
internal readonly record struct BackupPolicy(int KeepCount, int KeepDays)
{
    internal const int DefaultCount = 10;
    internal const int DefaultDays = 0;
    internal const int MaxCount = 100;
    internal const int MaxDays = 3650;

    internal static int ClampCount(int n) => n < 0 ? DefaultCount : n > MaxCount ? MaxCount : n;

    internal static int ClampDays(int n) => n < 0 ? DefaultDays : n > MaxDays ? MaxDays : n;

    internal BackupPolicy Clamped => new(ClampCount(KeepCount), ClampDays(KeepDays));

    internal bool KeepsAll => KeepCount <= 0 && KeepDays <= 0;

    /// <summary>
    /// <c>the newest 10 of each kind, none older than 14 days</c> / <c>the newest 10 of each kind</c> / <c>those younger than
    /// 14 days</c> / <c>every one</c>.
    /// </summary>
    internal string Text()
    {
        var p = Clamped;
        string count = p.KeepCount > 0 ? "the newest " + p.KeepCount.ToString(CultureInfo.InvariantCulture) + " of each kind" : "";
        string days = p.KeepDays > 0 ? (p.KeepDays == 1 ? "1 day" : p.KeepDays.ToString(CultureInfo.InvariantCulture) + " days") : "";
        if (count.Length > 0 && days.Length > 0) return count + ", none older than " + days;
        if (count.Length > 0) return count;
        if (days.Length > 0) return "those younger than " + days;
        return "every one";
    }
}

/// <summary>
/// BACKUPNOW1: which manual and scheduled backups retention deletes, and the deletion itself.
/// <list type="bullet">
/// <item>Each kind (manual, scheduled) is counted on its own, so an hourly schedule never pushes out an admin's manual backup.
/// One policy (<see cref="BackupPolicy"/>) serves both: one rule to learn, and per-kind counting keeps them apart.</item>
/// <item>A backup goes when it is past the newest <c>KeepCount</c> of its kind <b>or</b> older than <c>KeepDays</c> days: when
/// both are set, the stricter rule wins (a backup must pass both to stay).</item>
/// <item>Never deleted: the newest backup of each kind (the last good copy, whatever its age), a protected path (the source
/// of a pending restore request), every restore-chain backup (<c>before-restore</c>, a replaced world: the keep count's, see
/// <see cref="WorldBackupCatalog.PruneList"/>), wipe archives and the game's <c>.BACKUP</c> copies.</item>
/// </list>
/// A deleted backup is renamed to <c>.deleting-&lt;name&gt;</c> first (never listed half-deleted). Retention runs after each
/// manual or scheduled backup. File-only.
/// </summary>
internal static class WorldBackupRetention
{
    /// <summary>What <paramref name="policy"/> deletes from <paramref name="entries"/> at <paramref name="nowUtc"/>, oldest last.</summary>
    internal static List<WorldBackupEntry> PruneList(IReadOnlyList<WorldBackupEntry> entries, BackupPolicy policy, DateTime nowUtc, params string?[] protect)
    {
        var result = new List<WorldBackupEntry>();
        var p = policy.Clamped;
        if (p.KeepsAll) return result;
        foreach (string kind in new[] { WorldRestorePaths.KindManual, WorldRestorePaths.KindScheduled })
        {
            var mine = new List<WorldBackupEntry>();
            foreach (var e in entries)
                if (e.Kind == WorldBackupEntry.KindBackup && string.Equals(e.BackupKind, kind, StringComparison.Ordinal)) mine.Add(e);
            mine.Sort((a, b) => b.CreatedLocal.CompareTo(a.CreatedLocal));
            for (int i = 1; i < mine.Count; i++)
            {
                var e = mine[i];
                bool overCount = p.KeepCount > 0 && i >= p.KeepCount;
                bool tooOld = p.KeepDays > 0 && AgeDays(e, nowUtc) > p.KeepDays;
                if (!overCount && !tooOld) continue;
                bool kept = false;
                foreach (string? path in protect ?? Array.Empty<string?>())
                    if (WorldRestorePaths.SamePath(e.Path, path)) kept = true;
                if (!kept) result.Add(e);
            }
        }

        return result;
    }

    // ---- AUTOUPDATE3: pre-update backups -----------------------------------------------------------------

    /// <summary>The default and the range of <c>[AutoUpdate] PreUpdateBackupKeep</c>.</summary>
    internal const int PreUpdateDefaultKeep = 2;
    internal const int PreUpdateMinKeep = 1;
    internal const int PreUpdateMaxKeep = 20;

    internal static int ClampPreUpdateKeep(int n) => n < PreUpdateMinKeep ? PreUpdateMinKeep : n > PreUpdateMaxKeep ? PreUpdateMaxKeep : n;

    /// <summary>
    /// AUTOUPDATE3: which pre-update backups their retention deletes: past the newest <paramref name="keep"/> (at least 1, so
    /// the newest always stays) of the pre-update kind, counted only among pre-update backups. No other kind is ever on the
    /// list: a manual, scheduled or restore-chain backup and a wipe archive are never deleted by it, and never count.
    /// </summary>
    internal static List<WorldBackupEntry> PreUpdatePruneList(IReadOnlyList<WorldBackupEntry> entries, int keep, params string?[] protect)
    {
        keep = ClampPreUpdateKeep(keep);
        var mine = new List<WorldBackupEntry>();
        foreach (var e in entries ?? Array.Empty<WorldBackupEntry>())
            if (e != null && e.IsPreUpdate) mine.Add(e);
        mine.Sort((a, b) => b.CreatedLocal.CompareTo(a.CreatedLocal));
        var result = new List<WorldBackupEntry>();
        for (int i = keep; i < mine.Count; i++)
        {
            bool kept = false;
            foreach (string? path in protect ?? Array.Empty<string?>())
                if (WorldRestorePaths.SamePath(mine[i].Path, path)) kept = true;
            if (!kept) result.Add(mine[i]);
        }

        return result;
    }

    /// <summary>
    /// AUTOUPDATE3: the newest backup that holds the current world (a manual, scheduled or pre-update backup), or null. Wipe
    /// archives and the restore chain's copies hold another world (the one before a wipe or a restore), so they never stand
    /// in for a pre-update backup.
    /// </summary>
    internal static WorldBackupEntry? NewestOfCurrentWorld(IReadOnlyList<WorldBackupEntry> entries)
    {
        WorldBackupEntry? best = null;
        foreach (var e in entries ?? Array.Empty<WorldBackupEntry>())
        {
            if (e == null || !e.IsMadeBackup || e.CreatedLocal == DateTime.MinValue) continue;
            if (best == null || e.CreatedLocal > best.CreatedLocal) best = e;
        }

        return best;
    }

    /// <summary>
    /// AUTOUPDATE3: the backup that stands in for a pre-update backup now (one newer than <paramref name="skipHours"/>), or null
    /// when one must be made. 0 hours never skips.
    /// </summary>
    internal static WorldBackupEntry? PreUpdateStandIn(IReadOnlyList<WorldBackupEntry> entries, int skipHours, DateTime nowUtc)
    {
        if (skipHours <= 0) return null;
        var newest = NewestOfCurrentWorld(entries);
        if (newest == null) return null;
        double ageHours = AgeDays(newest, nowUtc) * 24.0;
        return ageHours >= 0 && ageHours < skipHours ? newest : null;
    }

    /// <summary>AUTOUPDATE3: delete what <see cref="PreUpdatePruneList"/> says, each renamed to <c>.deleting-*</c> first.</summary>
    internal static void PrunePreUpdate(IWorldFs fs, WorldRestorePaths.SaveLayout layout, int keep, RestoreEnvironment env, List<string> pruned,
        List<string> warnings, params string?[] protect)
    {
        try
        {
            var keepPaths = new List<string?>(protect ?? Array.Empty<string?>());
            if (!string.IsNullOrEmpty(env.BepInExRoot) && WorldRestoreMarker.ReadPending(fs, env.BepInExRoot, out _) is { } pending)
                keepPaths.Add(pending.SourcePath);
            foreach (var old in PreUpdatePruneList(WorldBackupCatalog.List(fs, layout), keep, keepPaths.ToArray()))
            {
                try
                {
                    string doomed = Path.Combine(layout.BackupsRoot, WorldRestorePaths.DeletingPrefix + old.FolderName);
                    WorldRestoreApply.RetryMove(fs, old.Path, doomed, env);
                    fs.DeleteDirectory(doomed);
                    pruned.Add(old.FolderName);
                }
                catch (Exception e) { warnings.Add("The old pre-update backup " + old.FolderName + " could not be removed (" + e.Message + ")."); }
            }

            if (pruned.Count > 0)
                WorldRestoreApply.Say(env, "pre-update backup retention (the newest " + ClampPreUpdateKeep(keep) + "): removed " + string.Join(", ", pruned));
        }
        catch (Exception e) { warnings.Add("Pre-update backup retention did not run (" + e.Message + ")."); }
    }

    /// <summary>Days since the backup was made (fractional), from its local creation time.</summary>
    internal static double AgeDays(WorldBackupEntry e, DateTime nowUtc)
    {
        if (e.CreatedLocal == DateTime.MinValue) return double.MaxValue;
        var made = e.CreatedLocal.Kind == DateTimeKind.Utc ? e.CreatedLocal : DateTime.SpecifyKind(e.CreatedLocal, DateTimeKind.Local).ToUniversalTime();
        return (nowUtc.ToUniversalTime() - made).TotalDays;
    }

    /// <summary>
    /// The preview the Backups list shows: what retention deletes after the next backup of <paramref name="kind"/> (one more
    /// of that kind, made now).
    /// </summary>
    internal static List<WorldBackupEntry> NextDeletes(IReadOnlyList<WorldBackupEntry> entries, BackupPolicy policy, DateTime nowUtc, string kind,
        params string?[] protect)
    {
        var imagined = new List<WorldBackupEntry>(entries)
        {
            new()
            {
                Kind = WorldBackupEntry.KindBackup, BackupKind = kind, FolderName = "(next)", Path = "(next " + kind + ")",
                CreatedLocal = nowUtc.ToLocalTime().AddSeconds(1),
            },
        };
        var list = PruneList(imagined, policy, nowUtc, protect);
        list.RemoveAll(e => e.FolderName == "(next)");
        return list;
    }

    /// <summary>
    /// Delete what <paramref name="policy"/> says, each renamed to <c>.deleting-*</c> first. The source of the pending restore
    /// request (<c>BepInEx\progenitor-restore.json</c>) is protected as well as <paramref name="protect"/>.
    /// </summary>
    internal static void Prune(IWorldFs fs, WorldRestorePaths.SaveLayout layout, BackupPolicy policy, RestoreEnvironment env, List<string> pruned,
        List<string> warnings, params string?[] protect)
    {
        try
        {
            var keep = new List<string?>(protect ?? Array.Empty<string?>());
            if (!string.IsNullOrEmpty(env.BepInExRoot) && WorldRestoreMarker.ReadPending(fs, env.BepInExRoot, out _) is { } pending)
                keep.Add(pending.SourcePath);
            var entries = WorldBackupCatalog.List(fs, layout);
            foreach (var old in PruneList(entries, policy, env.UtcNow(), keep.ToArray()))
            {
                try
                {
                    string doomed = Path.Combine(layout.BackupsRoot, WorldRestorePaths.DeletingPrefix + old.FolderName);
                    WorldRestoreApply.RetryMove(fs, old.Path, doomed, env);
                    fs.DeleteDirectory(doomed);
                    pruned.Add(old.FolderName);
                }
                catch (Exception e) { warnings.Add("The old backup " + old.FolderName + " could not be removed (" + e.Message + ")."); }
            }

            if (pruned.Count > 0)
                WorldRestoreApply.Say(env, "backup retention (" + policy.Text() + "): removed " + string.Join(", ", pruned));
        }
        catch (Exception e) { warnings.Add("Backup retention did not run (" + e.Message + ")."); }
    }
}
