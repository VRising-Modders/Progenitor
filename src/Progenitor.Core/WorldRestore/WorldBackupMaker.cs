using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Progenitor.WorldRestore;

/// <summary>Where a backup is in its work (read from another thread: plain fields, written by the copying thread only).</summary>
internal sealed class BackupProgress
{
    internal const string PhaseSaving = "saving";
    internal const string PhaseCopying = "copying";
    internal const string PhaseVerifying = "verifying";
    internal const string PhaseRetention = "retention";

    public volatile string Phase = PhaseCopying;
    public volatile int Attempt = 1;
    public volatile int FilesDone;
    public volatile int FilesTotal;
    private long _bytesDone;
    private long _bytesTotal;

    public long BytesDone
    {
        get => System.Threading.Interlocked.Read(ref _bytesDone);
        set => System.Threading.Interlocked.Exchange(ref _bytesDone, value);
    }

    public long BytesTotal
    {
        get => System.Threading.Interlocked.Read(ref _bytesTotal);
        set => System.Threading.Interlocked.Exchange(ref _bytesTotal, value);
    }
}

/// <summary>One manual or scheduled backup to make.</summary>
internal sealed class BackupJob
{
    /// <summary>The live save folder (<c>&lt;persist&gt;\Saves\v&lt;N&gt;\&lt;save&gt;</c>).</summary>
    public string SaveFolder { get; set; } = "";

    /// <summary><see cref="WorldRestorePaths.KindManual"/>, <see cref="WorldRestorePaths.KindScheduled"/> or (AUTOUPDATE3) <see cref="WorldRestorePaths.KindPreUpdate"/>.</summary>
    public string Kind { get; set; } = WorldRestorePaths.KindManual;

    /// <summary><see cref="WorldRestorePaths.TriggerManual"/>, <see cref="WorldRestorePaths.TriggerStart"/> or <see cref="WorldRestorePaths.TriggerInterval"/>.</summary>
    public string Trigger { get; set; } = WorldRestorePaths.TriggerManual;

    /// <summary>
    /// The newest save must decompress (gzip CRC and length). True while the game runs: a save caught half-written is
    /// retried. False at a server start (the game is not writing): a damaged newest save is a warning and the backup is the
    /// exact copy, so the damaged world is still kept.
    /// </summary>
    public bool StrictSave { get; set; } = true;

    /// <summary>Copies tried before giving up when the save folder keeps changing (an autosave landing mid-copy).</summary>
    public int Attempts { get; set; } = 3;

    /// <summary>The wait before another copy (the game finishes the save that changed the folder).</summary>
    public int RetryWaitMs { get; set; } = 3000;

    /// <summary>Free space kept on top of the estimate plus 10 % (the restore's margin).</summary>
    public long SpareBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>The retention to run after a successful backup, or null.</summary>
    public BackupPolicy? Retention { get; set; }

    /// <summary>
    /// AUTOUPDATE3: a pre-update backup's own retention, the newest this many pre-update backups (counted only among them),
    /// run instead of <see cref="Retention"/>. Null runs no retention for it.
    /// </summary>
    public int? PreUpdateKeep { get; set; }

    /// <summary>AUTOUPDATE3: a pre-update backup's updates, written to its sidecar (the list's label).</summary>
    public string Updates { get; set; } = "";

    /// <summary>Paths retention never deletes (the source of a pending restore request).</summary>
    public List<string> Protect { get; } = new();

    /// <summary>Stop between files (Core unloading). The partial copy is removed.</summary>
    public Func<bool> Cancelled { get; set; } = () => false;

    public BackupProgress Progress { get; } = new();
}

/// <summary>What a manual or scheduled backup did.</summary>
internal sealed class BackupOutcome
{
    public bool Ok { get; set; }

    /// <summary>One sentence: "Made Manual backup from … (82.4 MB, 41 files)." or "Not backed up: …".</summary>
    public string Summary { get; set; } = "";

    public string Reason { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public string FolderName { get; set; } = "";

    /// <summary>How the backup reads in the list (<c>Manual backup from Wed, Oct 7, 2026 2:15 PM</c>).</summary>
    public string Name { get; set; } = "";

    public long Bytes { get; set; }
    public int Files { get; set; }
    public int Attempts { get; set; }

    /// <summary>The estimate the space check used (bytes of the save folder).</summary>
    public long Estimate { get; set; }

    public List<string> Warnings { get; } = new();
    public List<string> Pruned { get; } = new();
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public List<string> Log { get; } = new();
}

/// <summary>
/// BACKUPNOW1: a manual or scheduled backup of the whole world folder (saves, <c>StartDate.json</c>, the <c>progenitor\</c>
/// ModSave stores, every other file; only <c>.TEMP\</c> is left out), consistent while the game may be saving:
/// <list type="number">
/// <item>The free-space check: the folder's size plus 10 % plus <see cref="BackupJob.SpareBytes"/>.</item>
/// <item>The folder's shape (path, length, write time of every file) before the copy.</item>
/// <item>Each file is hashed (SHA-256, the source as read), then copied (flushed, write time kept).</item>
/// <item>The shape again after the copy: any difference (an autosave landed, an old autosave was deleted, a ModSave store was
/// written) and the copy is thrown away and made again, after a wait, up to <see cref="BackupJob.Attempts"/> times.</item>
/// <item>The copy is verified against the hashes taken from the source (a file that changed between its hash and its copy
/// differs here and is retried the same way) and the newest save must decompress.</item>
/// <item>The sidecar (<c>progenitor-backup.json</c>, with every file's SHA-256), the flushes and the rename from
/// <c>*.partial</c> to the final name; then retention (<see cref="WorldBackupRetention"/>).</item>
/// </list>
/// So a backup never mixes two saves without it being detected: the shape and every file's bytes are the same before, during
/// and after the copy. File-only (it runs off the main thread in Core and before the game in Bootstrap).
/// </summary>
internal static class WorldBackupMaker
{
    /// <summary>The bytes a backup of <paramref name="saveFolder"/> copies (everything but <c>.TEMP\</c>).</summary>
    internal static long Estimate(IWorldFs fs, string saveFolder) => WorldManifest.TotalBytes(WorldManifest.Build(fs, saveFolder, hash: false));

    /// <summary>The space a backup needs: the estimate plus 10 % plus <paramref name="spare"/>.</summary>
    internal static long Need(long estimate, long spare) => estimate + estimate / 10 + Math.Max(0, spare);

    /// <summary>Null when <paramref name="free"/> (unknown: null) covers <see cref="Need"/>, else the refusal.</summary>
    internal static string? SpaceRefusal(long estimate, long? free, long spare)
    {
        long need = Need(estimate, spare);
        if (free is { } f && f < need)
            return "not enough free disk space for a backup: " + WorldBackupCatalog.FormatBytes(need) + " needed (the world's "
                + WorldBackupCatalog.FormatBytes(estimate) + " plus 10 % and " + WorldBackupCatalog.FormatBytes(Math.Max(0, spare)) + "), "
                + WorldBackupCatalog.FormatBytes(f) + " free";
        return null;
    }

    private sealed class Changed : Exception
    {
        internal Changed(string why) : base(why) { }
    }

    private sealed class Refused : Exception
    {
        internal Refused(string why) : base(why) { }
    }

    /// <summary>Make the backup. Never throws; the outcome says what happened.</summary>
    internal static BackupOutcome Make(IWorldFs fs, BackupJob job, RestoreEnvironment env)
    {
        env ??= new RestoreEnvironment();
        var outcome = new BackupOutcome { Kind = job.Kind, Trigger = job.Trigger, StartedUtc = env.UtcNow() };
        string partial = "";
        bool madePartial = false;
        void Say(string line)
        {
            outcome.Log.Add(line);
            try { env.Log("WorldBackup: " + line); }
            catch { /* the log never decides */ }
        }

        try
        {
            if (!WorldRestorePaths.IsMadeBackupKind(job.Kind)) throw new Refused("unknown backup kind '" + job.Kind + "'");
            if (WorldRestorePaths.IsLiveServerPath(job.SaveFolder) || WorldRestorePaths.IsLiveServerPath(env.BepInExRoot))
                throw new Refused("refusing the live dedicated server path (" + WorldRestorePaths.ForbiddenLiveServerRoot + "); backups never run there");
            if (!WorldRestorePaths.TryLayout(job.SaveFolder, out var layout, out string why)) throw new Refused(why.TrimEnd('.'));
            if (!fs.DirectoryExists(layout.SaveFolder)) throw new Refused("the save folder " + layout.SaveFolder + " does not exist");

            string backupsRoot = layout.BackupsRoot;
            CleanOwnLeftovers(fs, backupsRoot, Say);
            var shape = WorldManifest.Build(fs, layout.SaveFolder, hash: false);
            if (WorldManifest.NewestSave(shape) == null) throw new Refused("the world has no save file yet");
            outcome.Estimate = WorldManifest.TotalBytes(shape);
            if (SpaceRefusal(outcome.Estimate, fs.FreeBytes(layout.PersistRoot), job.SpareBytes) is { } space) throw new Refused(space);

            string name = WorldRestoreApply.UniqueName(fs, backupsRoot, WorldRestorePaths.BackupFolderName(env.LocalNow(), job.Kind));
            string backup = Path.Combine(backupsRoot, name);
            partial = backup + WorldRestorePaths.PartialSuffix;
            List<WorldManifestEntry>? copied = null;
            string? saveWarning = null;
            string lastChange = "";
            int attempts = Math.Max(1, job.Attempts);
            for (int attempt = 1; attempt <= attempts && copied == null; attempt++)
            {
                job.Progress.Attempt = attempt;
                outcome.Attempts = attempt;
                if (attempt > 1)
                {
                    Say("the save folder changed during the copy (" + lastChange + "); copying again (attempt " + attempt + " of " + attempts + ")");
                    fs.Sleep(job.RetryWaitMs);
                }

                try
                {
                    copied = CopyOnce(fs, job, layout.SaveFolder, partial, ref madePartial, out saveWarning);
                }
                catch (Changed c)
                {
                    lastChange = c.Message;
                    RemovePartial(fs, partial, ref madePartial, Say);
                }
            }

            if (copied == null)
                throw new Refused("the save folder kept changing during the copy (" + attempts + " tries; last: " + lastChange + "); try again in a minute");

            // The sidecar, the flushes and the final name.
            job.Progress.Phase = BackupProgress.PhaseVerifying;
            var sidecar = new BackupSidecar
            {
                Kind = job.Kind,
                Trigger = job.Trigger,
                Attempts = outcome.Attempts,
                Updates = job.Updates ?? "",
                SourceSaveName = layout.SaveName,
                CreatedUtc = env.UtcNow(),
                PersistenceVersion = layout.VersionFolder,
                ProgenitorVersion = env.ProgenitorVersion ?? "",
                StartDate = WorldBackupCatalog.ReadStartDate(fs, partial)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                Note = saveWarning == null ? "" : "The newest save did not read cleanly when this backup was made (" + saveWarning + "); it is an exact copy.",
            };
            sidecar.Files.AddRange(copied);
            fs.WriteAllBytes(Path.Combine(partial, WorldRestorePaths.SidecarNameFor(job.Kind)), sidecar.ToJson());
            FlushTree(fs, partial, copied);
            WorldRestoreApply.RetryMove(fs, partial, backup, env);
            madePartial = false;
            fs.FlushDirectory(backupsRoot);
            if (saveWarning != null) outcome.Warnings.Add("The newest save does not read cleanly (" + saveWarning + "); the backup is an exact copy.");

            outcome.Ok = true;
            outcome.BackupPath = backup;
            outcome.FolderName = name;
            outcome.Files = copied.Count;
            outcome.Bytes = WorldManifest.TotalBytes(copied);
            var entry = new WorldBackupEntry
            {
                Kind = WorldBackupEntry.KindBackup, BackupKind = job.Kind, CreatedLocal = WorldTime.ToLocal(sidecar.CreatedUtc, env.Zone),
                CreatedUtc = sidecar.CreatedUtc, Updates = sidecar.Updates,
            };
            outcome.Name = entry.DisplayName;
            Say("made " + name + ": " + copied.Count + " files, " + WorldBackupCatalog.FormatBytes(outcome.Bytes)
                + (outcome.Attempts > 1 ? ", after " + outcome.Attempts + " copies" : ""));

            if (string.Equals(job.Kind, WorldRestorePaths.KindPreUpdate, StringComparison.Ordinal))
            {
                // AUTOUPDATE3: counted only among pre-update backups; manual and scheduled backups are never touched.
                if (job.PreUpdateKeep is int keep)
                {
                    job.Progress.Phase = BackupProgress.PhaseRetention;
                    WorldBackupRetention.PrunePreUpdate(fs, layout, keep, env, outcome.Pruned, outcome.Warnings, job.Protect.ToArray());
                }
            }
            else if (job.Retention is { } policy)
            {
                job.Progress.Phase = BackupProgress.PhaseRetention;
                WorldBackupRetention.Prune(fs, layout, policy, env, outcome.Pruned, outcome.Warnings, job.Protect.ToArray());
            }

            outcome.Summary = "Made " + outcome.Name + " (" + WorldBackupCatalog.FormatBytes(outcome.Bytes) + ", " + outcome.Files + " files"
                + (outcome.Pruned.Count > 0 ? "; retention removed " + outcome.Pruned.Count : "") + ").";
        }
        catch (Exception e)
        {
            string reason = e is Refused || e is Changed ? e.Message : "unexpected error: " + e.Message;
            if (job.Cancelled()) reason = "stopped (Progenitor is unloading)";
            RemovePartial(fs, partial, ref madePartial, Say);
            outcome.Ok = false;
            outcome.Reason = reason;
            outcome.Summary = "Not backed up: " + reason + ".";
            Say("FAILED: " + reason);
        }

        outcome.FinishedUtc = env.UtcNow();
        return outcome;
    }

    /// <summary>One copy of the folder into <paramref name="partial"/>; throws <see cref="Changed"/> when the folder moved under it.</summary>
    private static List<WorldManifestEntry> CopyOnce(IWorldFs fs, BackupJob job, string save, string partial, ref bool madePartial, out string? saveWarning)
    {
        saveWarning = null;
        job.Progress.Phase = BackupProgress.PhaseCopying;
        var before = WorldManifest.Build(fs, save, hash: false);
        if (WorldManifest.NewestSave(before) == null) throw new Changed("the save file is gone");
        job.Progress.FilesTotal = before.Count;
        job.Progress.FilesDone = 0;
        job.Progress.BytesTotal = WorldManifest.TotalBytes(before);
        job.Progress.BytesDone = 0;
        if (fs.DirectoryExists(partial)) fs.DeleteDirectory(partial);
        fs.CreateDirectory(partial);
        madePartial = true;
        var copied = new List<WorldManifestEntry>(before.Count);
        long done = 0;
        foreach (var f in before)
        {
            if (job.Cancelled()) throw new Refused("stopped (Progenitor is unloading)");
            string from = Path.Combine(save, f.Path);
            string sha;
            try
            {
                sha = WorldManifest.HashFile(fs, from);
                fs.CopyFile(from, Path.Combine(partial, f.Path));
            }
            catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException || e is IOException || e is UnauthorizedAccessException)
            {
                // A file deleted, replaced or held by the game's save while it was read: the folder changed.
                throw new Changed(f.Path + ": " + e.Message);
            }

            copied.Add(new WorldManifestEntry { Path = f.Path, Length = f.Length, LastWriteUtc = f.LastWriteUtc, Sha256 = sha });
            done += f.Length;
            job.Progress.FilesDone = copied.Count;
            job.Progress.BytesDone = done;
        }

        job.Progress.Phase = BackupProgress.PhaseVerifying;
        var after = WorldManifest.Build(fs, save, hash: false);
        string? moved = WorldManifest.Compare(after, before) ?? ShapeDiff(after, before);
        if (moved != null) throw new Changed(moved);
        string? bad = WorldManifest.Verify(fs, partial, copied, null, job.StrictSave, out saveWarning);
        // A copy that differs from the hash of its source as read: the file changed between the two reads.
        if (bad != null) throw new Changed(bad);
        return copied;
    }

    /// <summary>The first file whose write time moved (the shape check <see cref="WorldManifest.Compare"/> leaves out), or null.</summary>
    private static string? ShapeDiff(IReadOnlyList<WorldManifestEntry> after, IReadOnlyList<WorldManifestEntry> before)
    {
        var map = new Dictionary<string, WorldManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in before) map[e.Path] = e;
        foreach (var e in after)
            if (map.TryGetValue(e.Path, out var b) && b.LastWriteUtc != e.LastWriteUtc) return e.Path + " was written again";
        return null;
    }

    private static void RemovePartial(IWorldFs fs, string partial, ref bool madePartial, Action<string> say)
    {
        if (!madePartial || partial.Length == 0) return;
        try
        {
            if (fs.DirectoryExists(partial)) fs.DeleteDirectory(partial);
            madePartial = false;
        }
        catch (Exception e) { say("a partial copy stays at " + partial + " (" + e.Message + "); the next backup removes it"); }
    }

    /// <summary>Remove <c>*-manual.partial</c> and <c>*-scheduled.partial</c> folders an interrupted backup left (never a restore's).</summary>
    internal static void CleanOwnLeftovers(IWorldFs fs, string backupsRoot, Action<string> say)
    {
        IReadOnlyList<string> dirs;
        try { dirs = fs.ListDirectories(backupsRoot); }
        catch { return; }
        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            if (!IsOwnPartial(name)) continue;
            try
            {
                fs.DeleteDirectory(dir);
                say("removed an interrupted backup's copy " + name);
            }
            catch (Exception e) { say("an interrupted backup's copy stays at " + dir + " (" + e.Message + ")"); }
        }
    }

    internal static bool IsOwnPartial(string name)
    {
        if (!name.EndsWith(WorldRestorePaths.PartialSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        string bare = name.Substring(0, name.Length - WorldRestorePaths.PartialSuffix.Length);
        // yyyyMMdd-HHmmss-<kind>[-n]
        foreach (string kind in new[] { WorldRestorePaths.KindManual, WorldRestorePaths.KindScheduled, WorldRestorePaths.KindPreUpdate })
        {
            int i = bare.IndexOf("-" + kind, StringComparison.Ordinal);
            if (i == 15) return true;
        }

        return false;
    }

    private static void FlushTree(IWorldFs fs, string root, IReadOnlyList<WorldManifestEntry> files)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        foreach (var f in files)
        {
            string? d = Path.GetDirectoryName(Path.Combine(root, f.Path));
            if (!string.IsNullOrEmpty(d)) dirs.Add(d!);
        }

        foreach (string d in dirs) fs.FlushDirectory(d);
    }
}
