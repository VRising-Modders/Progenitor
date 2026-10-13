using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.WorldRestore;

/// <summary>What the apply needs from its process (Bootstrap passes the real values; tests their own).</summary>
internal sealed class RestoreEnvironment
{
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    public Func<DateTime> LocalNow { get; set; } = () => DateTime.Now;

    /// <summary>The process's command line, split (<c>-saveName</c>, <c>-persistentDataPath</c> must agree with the request).</summary>
    public IReadOnlyList<string> Args { get; set; } = Array.Empty<string>();

    /// <summary>An environment variable (<c>VR_RESET_DAYS_INTERVAL</c>, <c>VR_DAY_OF_RESET</c>), or null.</summary>
    public Func<string, string?> GetEnv { get; set; } = _ => null;

    public string ProcessPath { get; set; } = "";
    public string ProgenitorVersion { get; set; } = "";

    /// <summary>The BepInEx folder: the request, the result and the journal live there; its parent is the server's root.</summary>
    public string BepInExRoot { get; set; } = "";

    /// <summary>One line per step (Bootstrap: the BepInEx log).</summary>
    public Action<string> Log { get; set; } = _ => { };

    /// <summary>
    /// WORLDRESTORE2 (H1/H2): the save folder could not be put back. Bootstrap stops the process here, before the game would
    /// create an empty world in its place. Tests record it.
    /// </summary>
    public Action<string> FailClosed { get; set; } = _ => { };

    /// <summary>The waits between rename attempts (an antivirus or indexer can hold a folder for a moment).</summary>
    public int[] RetryDelaysMs { get; set; } = { 100, 250, 500, 1000, 2000 };

    /// <summary>Free space kept on top of the two copies (spec §3.2 step 1: 10 % plus 256 MB).</summary>
    public long SpareBytes { get; set; } = 256L * 1024 * 1024;
}

/// <summary>
/// WORLDRESTORE1/2: the restore itself, applied by Bootstrap at a server start before the game picks its save (spec §3.2,
/// decided 2026-10-06, review 2026-10-07). Every step that changes the disk is recorded first in the journal
/// (<see cref="RestoreJournal"/>), so <see cref="WorldRestoreRecovery"/> can finish or undo it after a crash.
/// <list type="number">
/// <item><b>Validate</b>: the live-server fence (final paths), a dedicated server's save folder, a known source unchanged
/// since it was chosen, a host settings file under the server or the persistent-data root, room for two copies.</item>
/// <item><b>Back up</b> the whole current world (but <c>.TEMP\</c>) into <c>&lt;stamp&gt;-before-restore.partial</c>, every file
/// flushed, then <b>verify</b> it by SHA-256 (a newest save that does not decompress is a warning here: the live world may be
/// damaged and still be restored over), the sidecar, the folder flushes and the rename to its final name.</item>
/// <item><b>Stage</b> a copy of the source with the current per-save settings files; a Progenitor backup must match its
/// sidecar's SHA-256 list; strict verify, gzip included.</item>
/// <item>The <b>wipe decision</b> (journaled first), then the wipe date <b>recomputed</b> from the staged world and the next
/// start's settings: still due, or a picked date already past, refuses.</item>
/// <item><b>Swap</b> with two renames, each retried. When the second fails the first is undone; when that fails too the
/// process stops (fail closed) rather than let the game start an empty world.</item>
/// <item>After the swap: the replaced world stays in <c>.outgoing-&lt;id&gt;</c> until the restored world has saved once (it is
/// removed at the next start after that), or becomes a backup of its own when it differs from the verified one; then
/// <b>retention</b> (renamed to <c>.deleting-*</c> first).</item>
/// </list>
/// Any failure before the swap puts the settings file back and removes only the folders this run made.
/// </summary>
internal static class WorldRestoreApply
{
    internal sealed class Abort : Exception
    {
        internal Abort(string reason) : base(reason) { }
    }

    /// <summary>The save folder is gone and could not be put back: stop the process.</summary>
    internal sealed class Stranded : Exception
    {
        internal Stranded(string reason) : base(reason) { }
    }

    internal static RestoreResult Run(IWorldFs fs, RestoreRequest request, RestoreEnvironment env)
    {
        fs = fs ?? throw new ArgumentNullException(nameof(fs));
        env ??= new RestoreEnvironment();
        var result = new RestoreResult
        {
            RequestId = request?.RequestId ?? "",
            SourceName = request?.SourceName ?? "",
            SaveFolder = request?.SaveFolder ?? "",
            WipeMode = request?.WipeMode ?? WipeModes.None,
            StartedUtc = env.UtcNow(),
        };

        RestoreJournal? journal = null;
        bool madePartial = false, madeBackup = false, madeIncoming = false, swapped = false;
        WorldRestorePaths.SaveLayout layout = default;
        List<WorldManifestEntry> current = new();

        void Log(string line) => Say(env, line);

        T Step<T>(string name, Func<T> body)
        {
            var clock = Stopwatch.StartNew();
            var step = new RestoreStep { Name = name };
            result.Steps.Add(step);
            try
            {
                T value = body();
                step.Ok = true;
                return value;
            }
            catch (Abort a)
            {
                step.Detail = a.Message;
                throw;
            }
            catch (Stranded s)
            {
                step.Detail = s.Message;
                throw;
            }
            catch (Exception e)
            {
                step.Detail = e.Message;
                throw new Abort(name + " failed: " + e.Message);
            }
            finally
            {
                step.Ms = clock.ElapsedMilliseconds;
                Log(name + (step.Ok ? " ok" : " FAILED: " + step.Detail) + (step.Ok && step.Detail.Length > 0 ? " (" + step.Detail + ")" : "")
                    + " in " + step.Ms + " ms");
            }
        }

        void Note(string detail)
        {
            if (result.Steps.Count > 0) result.Steps[result.Steps.Count - 1].Detail = detail;
        }

        try
        {
            if (request == null) throw new Abort("there is no request");
            Log("request " + request.RequestId + ": restore " + request.SourceName + " (" + request.SourcePath + ") over " + request.SaveFolder
                + ", wipe " + request.WipeMode);

            // 1. Validate (no file is changed).
            var source = Step("validate", () =>
            {
                string? why = Validate(fs, request, env, out layout, out var src);
                if (why != null) throw new Abort(why);
                Note(src.Count + " files in the source");
                return src;
            });

            // L1: copies a crashed run left behind (the recovery pass already handled any journal).
            CleanLeftovers(fs, layout.BackupsRoot, env);

            string backupsRoot = layout.BackupsRoot;
            string stamp = UniqueName(fs, backupsRoot, WorldRestorePaths.BackupFolderName(env.LocalNow(), WorldRestorePaths.KindBeforeRestore));
            string backup = Path.Combine(backupsRoot, stamp);
            string partial = backup + WorldRestorePaths.PartialSuffix;
            string incoming = Path.Combine(backupsRoot, UniqueName(fs, backupsRoot, WorldRestorePaths.IncomingName(request.RequestId)));
            string outgoing = Path.Combine(backupsRoot, WorldRestorePaths.OutgoingName(request.RequestId));
            if (fs.DirectoryExists(outgoing)) throw new Abort("a folder from an earlier restore is in the way: " + outgoing);
            journal = new RestoreJournal
            {
                RequestId = request.RequestId,
                Phase = RestoreJournal.PhasePrepare,
                SaveFolder = layout.SaveFolder,
                BackupsRoot = backupsRoot,
                Partial = partial,
                Backup = backup,
                Incoming = incoming,
                Outgoing = outgoing,
                SourceName = request.SourceName,
                BackupName = "Before restore of " + request.SourceName + ", " + WorldBackupCatalog.FormatTime(env.LocalNow()),
                KeepBackups = WorldBackupCatalog.ClampKeep(request.KeepBackups),
                ProtectSource = request.SourceKind == WorldBackupEntry.KindBackup ? request.SourcePath : "",
                StartedUtc = env.UtcNow(),
            };
            WorldRestoreJournalIo.Write(fs, env.BepInExRoot, journal);

            // 2. Back up the current world: the whole folder but .TEMP, write times kept, every file flushed.
            current = Step("backup", () =>
            {
                var files = WorldManifest.Build(fs, layout.SaveFolder, hash: true);
                if (WorldManifest.NewestSave(files) == null) throw new Abort("the current world has no save file to back up");
                fs.CreateDirectory(partial);
                madePartial = true;
                foreach (var f in files) fs.CopyFile(Path.Combine(layout.SaveFolder, f.Path), Path.Combine(partial, f.Path));
                Note(files.Count + " files, " + WorldManifest.TotalBytes(files) + " bytes");
                return files;
            });

            // 3. Verify it (SHA-256; the gzip check only warns here), then its sidecar, the flushes and its final name.
            Step("verify backup", () =>
            {
                string? bad = WorldManifest.Verify(fs, partial, current, null, strictSave: false, out string? warning);
                if (bad != null) throw new Abort("the backup of the current world did not check out: " + bad);
                if (warning != null)
                {
                    result.Warnings.Add("The current world's newest save does not read cleanly (" + warning + "); its backup is an exact copy.");
                    Note("warning: " + warning);
                }

                var sidecar = new BackupSidecar
                {
                    Kind = WorldRestorePaths.KindBeforeRestore,
                    SourceSaveName = layout.SaveName,
                    CreatedUtc = env.UtcNow(),
                    PersistenceVersion = layout.VersionFolder,
                    ProgenitorVersion = env.ProgenitorVersion ?? "",
                    StartDate = WorldBackupCatalog.ReadStartDate(fs, layout.SaveFolder)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                    RestoreOf = request.SourceName,
                    RequestId = request.RequestId,
                    ChainId = ChainFor(fs, layout, stamp),
                };
                sidecar.Files.AddRange(current);
                fs.WriteAllBytes(Path.Combine(partial, WorldRestorePaths.SidecarName), sidecar.ToJson());
                FlushTree(fs, partial, current);
                RetryMove(fs, partial, backup, env);
                madePartial = false;
                madeBackup = true;
                fs.FlushDirectory(backupsRoot);
                result.BackupPath = backup;
                result.BackupName = journal.BackupName;
                journal.Phase = RestoreJournal.PhaseBackedUp;
                WorldRestoreJournalIo.Write(fs, env.BepInExRoot, journal);
                return true;
            });

            // 4. Stage a copy of the source, with the current per-save settings files, and verify it strictly.
            Step("stage", () =>
            {
                var hashed = WorldManifest.Build(fs, request.SourcePath, hash: true, SkipForStaging);
                if (request.SourceKind == WorldBackupEntry.KindBackup)
                {
                    string? side = MatchesSidecar(fs, request.SourcePath, hashed);
                    if (side != null) throw new Abort("the backup " + request.SourceName + " does not match its own record: " + side);
                }

                fs.CreateDirectory(incoming);
                madeIncoming = true;
                var expected = new List<WorldManifestEntry>(hashed);
                foreach (var f in hashed) fs.CopyFile(Path.Combine(request.SourcePath, f.Path), Path.Combine(incoming, f.Path));
                foreach (var f in current)
                {
                    if (!WorldRestorePaths.IsPerSaveSettings(f.Path)) continue;
                    fs.CopyFile(Path.Combine(layout.SaveFolder, f.Path), Path.Combine(incoming, f.Path));
                    expected.Add(f);
                }

                string? bad = WorldManifest.Verify(fs, incoming, expected);
                if (bad != null) throw new Abort("the copy of " + request.SourceName + " did not check out: " + bad);
                FlushTree(fs, incoming, expected);
                journal.Phase = RestoreJournal.PhaseStaged;
                journal.IncomingVerified = true;
                WorldRestoreJournalIo.Write(fs, env.BepInExRoot, journal);
                Note(expected.Count + " files");
                return true;
            });

            // 5. The wipe decision (journaled before any write outside the staged copy), then the wipe date at this start.
            Step("wipe check", () =>
            {
                if (request.WipeMode != WipeModes.None)
                    result.WipeApplied = ApplyWipe(fs, request, env, layout, incoming, journal);
                journal.WipeApplied = result.WipeApplied;
                string? due = WipeDueNow(fs, request, env, layout, incoming);
                if (due != null) throw new Abort(due);
                Note(result.WipeApplied.Length > 0 ? result.WipeApplied : "no wipe at this start");
                return true;
            });

            // 6. The swap: two renames on one volume, each retried; the first undone when the second fails.
            Step("swap", () =>
            {
                journal.Phase = RestoreJournal.PhaseSwapping;
                WorldRestoreJournalIo.Write(fs, env.BepInExRoot, journal);
                RetryMove(fs, layout.SaveFolder, outgoing, env);
                try { RetryMove(fs, incoming, layout.SaveFolder, env); }
                catch (Exception e)
                {
                    try { RetryMove(fs, outgoing, layout.SaveFolder, env); }
                    catch (Exception back)
                    {
                        throw new Stranded("the restored world could not take the save folder's place (" + e.Message + ") and the current world "
                            + "could not be put back (" + back.Message + "): it is at " + outgoing + ", its verified backup at " + backup
                            + ". Rename " + outgoing + " to " + layout.SaveFolder + " before starting the server");
                    }

                    journal.Phase = RestoreJournal.PhaseStaged;
                    WorldRestoreJournalIo.Write(fs, env.BepInExRoot, journal);
                    throw new Abort("the restored world could not take the save folder's place (" + e.Message + "); the current world is back");
                }

                madeIncoming = false;
                swapped = true;
                return true;
            });

            // From here the restore stands: every problem is a warning, and the recovery pass finishes what is left.
            try
            {
                journal.Phase = RestoreJournal.PhaseSwapped;
                journal.Incoming = "";
                journal.SwappedUtc = env.UtcNow();
                WorldRestoreJournalIo.Write(fs, env.BepInExRoot, journal);
                fs.FlushDirectory(layout.VersionRoot);
                fs.FlushDirectory(backupsRoot);
            }
            catch (Exception e) { result.Warnings.Add("The journal could not record the swap (" + e.Message + "); the next start checks the folders."); }

            // 7-8. The replaced world and retention.
            try
            {
                FinishAfterSwap(fs, journal, layout, env, result);
                WorldRestoreJournalIo.Write(fs, env.BepInExRoot, journal);
            }
            catch (Exception e)
            {
                result.Ok = true;
                result.Summary = "Restored " + journal.SourceName + ". Backup of the previous world: " + journal.BackupName + ".";
                result.Warnings.Add("After the swap: " + e.Message);
            }
        }
        catch (Stranded s)
        {
            result.Ok = false;
            result.Stranded = true;
            result.Reason = s.Message;
            result.Summary = "STOPPED: " + s.Message + ".";
            Log("STRANDED: " + s.Message);
        }
        catch (Exception e)
        {
            string reason = e is Abort ? e.Message : "unexpected error: " + e.Message;
            result.Ok = false;
            result.Reason = reason;
            if (!swapped)
            {
                string? back = RollBackBeforeSwap(fs, journal, env, madePartial, madeIncoming, madeBackup);
                if (back != null)
                {
                    reason += "; " + back;
                    result.Reason = reason;
                }

                result.BackupPath = "";
                result.BackupName = "";
                result.WipeApplied = "";
                result.Summary = "Not restored: " + reason + "." + (back == null ? " Nothing changed." : "");
            }
            else result.Summary = "Not restored: " + reason + ".";

            Log("REFUSED: " + reason);
        }

        result.FinishedUtc = env.UtcNow();
        Log(result.Summary);
        return result;
    }

    internal static void Say(RestoreEnvironment env, string line)
    {
        try { env.Log("WorldRestore: " + line); }
        catch { /* the log never decides the restore */ }
    }

    /// <summary>
    /// Before the swap: put the host settings file back (bytes and write time), remove the folders this run made, and drop
    /// the journal. Null when all of it worked, else what did not.
    /// </summary>
    internal static string? RollBackBeforeSwap(IWorldFs fs, RestoreJournal? journal, RestoreEnvironment env, bool partial, bool incoming, bool backup)
    {
        if (journal == null) return null;
        string? problem = null;
        if (journal.SettingsWritten && journal.SettingsPath.Length > 0)
        {
            try
            {
                if (journal.SettingsExisted)
                {
                    WriteAtomic(fs, journal.SettingsPath, Convert.FromBase64String(journal.SettingsBefore));
                    if (journal.SettingsMtime != default) fs.SetLastWriteUtc(journal.SettingsPath, journal.SettingsMtime);
                }
                else fs.DeleteFile(journal.SettingsPath);
                journal.SettingsWritten = false;
            }
            catch (Exception s) { problem = "the host settings file could not be put back (" + s.Message + "): check " + journal.SettingsPath; }
        }

        foreach (var (made, dir) in new[] { (incoming, journal.Incoming), (partial, journal.Partial), (backup, journal.Backup) })
        {
            if (!made || string.IsNullOrEmpty(dir)) continue;
            try { if (fs.DirectoryExists(dir)) fs.DeleteDirectory(dir); }
            catch (Exception d) { Say(env, "a partial copy stays at " + dir + " (" + d.Message + ")"); }
        }

        if (problem == null)
        {
            try { WorldRestoreJournalIo.Delete(fs, env.BepInExRoot); }
            catch (Exception j) { Say(env, "the journal could not be removed: " + j.Message); }
        }
        else
        {
            try { WorldRestoreJournalIo.Write(fs, env.BepInExRoot, journal); } catch { /* the problem is reported */ }
        }

        return problem;
    }

    /// <summary>
    /// After the swap (also from the recovery pass): the replaced world becomes a backup of its own when it is not the
    /// verified backup's copy (else it waits in <c>.outgoing</c> for the restored world's first save), then retention.
    /// Idempotent. Sets <paramref name="result"/> to success.
    /// </summary>
    internal static void FinishAfterSwap(IWorldFs fs, RestoreJournal journal, WorldRestorePaths.SaveLayout layout, RestoreEnvironment env, RestoreResult result)
    {
        result.BackupPath = journal.Backup;
        result.BackupName = journal.BackupName;
        if (result.WipeApplied.Length == 0) result.WipeApplied = journal.WipeApplied;
        string outgoing = journal.Outgoing;
        try
        {
            if (outgoing.Length > 0 && fs.DirectoryExists(outgoing))
            {
                var gone = WorldManifest.Build(fs, outgoing, hash: true, WorldBackupCatalog.SkipSidecar);
                var side = BackupSidecar.Parse(SafeRead(fs, Path.Combine(journal.Backup, WorldRestorePaths.SidecarName)));
                string? diff = side == null ? "its backup's record is missing" : WorldManifest.Compare(gone, side.Files);
                if (diff == null) Say(env, "the replaced world equals its verified backup; it stays at " + outgoing + " until the restored world saves");
                else
                {
                    string keep = KeepAsBackup(fs, outgoing, layout, journal.RequestId, journal.SourceName, env,
                        "The world changed between its backup and the swap (" + diff + "); this is the world as it was replaced.");
                    journal.Outgoing = "";
                    result.Warnings.Add("The world changed after its backup (" + diff + "); it is kept as a second backup, " + keep + ".");
                }
            }
        }
        catch (Exception e)
        {
            result.Warnings.Add("The replaced world's folder stays at " + outgoing + " (" + e.Message + ").");
        }

        Prune(fs, layout, journal.KeepBackups, env, result, journal.Backup, journal.ProtectSource);
        result.Ok = true;
        result.Summary = "Restored " + journal.SourceName + ". Backup of the previous world: " + journal.BackupName + "."
            + (result.WipeApplied.Length > 0 ? " " + result.WipeApplied : "");
    }

    /// <summary>Turn a replaced world into a backup folder with a sidecar (never deleted by the restore). Returns its name.</summary>
    internal static string KeepAsBackup(IWorldFs fs, string folder, WorldRestorePaths.SaveLayout layout, string requestId, string restoredName,
        RestoreEnvironment env, string note)
    {
        var files = WorldManifest.Build(fs, folder, hash: true, WorldBackupCatalog.SkipSidecar);
        string name = UniqueName(fs, layout.BackupsRoot, WorldRestorePaths.BackupFolderName(env.LocalNow(), WorldRestorePaths.KindReplaced));
        var side = new BackupSidecar
        {
            Kind = WorldRestorePaths.KindBeforeRestore,
            SourceSaveName = layout.SaveName,
            CreatedUtc = env.UtcNow(),
            PersistenceVersion = layout.VersionFolder,
            ProgenitorVersion = env.ProgenitorVersion ?? "",
            StartDate = WorldBackupCatalog.ReadStartDate(fs, folder)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            RestoreOf = restoredName,
            RequestId = requestId,
            ChainId = ChainFor(fs, layout, name),
            Note = note,
        };
        side.Files.AddRange(files);
        fs.WriteAllBytes(Path.Combine(folder, WorldRestorePaths.SidecarName), side.ToJson());
        RetryMove(fs, folder, Path.Combine(layout.BackupsRoot, name), env);
        return name;
    }

    /// <summary>Retention (M1, M2): the newest N backups and every protected one stay; the rest are renamed to <c>.deleting-*</c>, then deleted.</summary>
    internal static void Prune(IWorldFs fs, WorldRestorePaths.SaveLayout layout, int keep, RestoreEnvironment env, RestoreResult result, params string?[] protect)
    {
        try
        {
            var entries = WorldBackupCatalog.List(fs, layout);
            foreach (var old in WorldBackupCatalog.PruneList(entries, keep, protect))
            {
                try
                {
                    string doomed = Path.Combine(layout.BackupsRoot, WorldRestorePaths.DeletingPrefix + old.FolderName);
                    RetryMove(fs, old.Path, doomed, env);
                    fs.DeleteDirectory(doomed);
                    result.Pruned.Add(old.FolderName);
                }
                catch (Exception e) { result.Warnings.Add("The old backup " + old.FolderName + " could not be removed (" + e.Message + ")."); }
            }

            if (result.Pruned.Count > 0) Say(env, "retention: removed " + string.Join(", ", result.Pruned) + " (keep " + keep + ")");
        }
        catch (Exception e) { result.Warnings.Add("Retention did not run (" + e.Message + ")."); }
    }

    /// <summary>L1: remove <c>*.partial</c>, <c>.incoming-*</c> and <c>.deleting-*</c> folders no journal refers to.</summary>
    internal static void CleanLeftovers(IWorldFs fs, string backupsRoot, RestoreEnvironment env)
    {
        IReadOnlyList<string> dirs;
        try { dirs = fs.ListDirectories(backupsRoot); }
        catch { return; }
        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            bool leftover = name.EndsWith(WorldRestorePaths.PartialSuffix, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(".incoming-", StringComparison.Ordinal) || name.StartsWith(WorldRestorePaths.DeletingPrefix, StringComparison.Ordinal);
            if (!leftover) continue;
            try
            {
                fs.DeleteDirectory(dir);
                Say(env, "removed a leftover copy " + name);
            }
            catch (Exception e) { Say(env, "a leftover copy stays at " + dir + " (" + e.Message + ")"); }
        }
    }

    /// <summary>H2: a rename retried with backoff (antivirus, indexer and backup tools hold folders for a moment).</summary>
    internal static void RetryMove(IWorldFs fs, string from, string to, RestoreEnvironment env)
    {
        int[] delays = env.RetryDelaysMs ?? Array.Empty<int>();
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                fs.MoveDirectory(from, to);
                if (attempt > 0) Say(env, "renamed " + from + " after " + (attempt + 1) + " attempts");
                return;
            }
            catch (Exception e) when (attempt < delays.Length && (e is IOException || e is UnauthorizedAccessException))
            {
                if (!fs.DirectoryExists(from) && fs.DirectoryExists(to)) return;
                fs.Sleep(delays[attempt]);
            }
        }
    }

    /// <summary>H3: flush every folder of a copied tree (each file was flushed as it was written).</summary>
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

    private static byte[]? SafeRead(IWorldFs fs, string path)
    {
        try { return fs.FileExists(path) ? fs.ReadAllBytes(path) : null; }
        catch { return null; }
    }

    /// <summary>The new backup's chain: the newest existing restore-chain backup's (a backup from before chains is in the empty one), else a new one.</summary>
    internal static string ChainFor(IWorldFs fs, WorldRestorePaths.SaveLayout layout, string stamp)
    {
        // BACKUPNOW1: manual and scheduled backups are not in a restore chain.
        foreach (var e in WorldBackupCatalog.List(fs, layout))
            if (e.IsRestoreChain) return e.ChainId;
        return stamp;
    }

    /// <summary>M2: a Progenitor backup's files equal its sidecar's SHA-256 list (the per-save settings aside). Null when they do.</summary>
    internal static string? MatchesSidecar(IWorldFs fs, string folder, IReadOnlyList<WorldManifestEntry> hashed)
    {
        var side = BackupSidecar.Parse(SafeRead(fs, WorldBackupCatalog.SidecarPath(fs, folder) ?? Path.Combine(folder, WorldRestorePaths.SidecarName)));
        if (side == null) return "it has no readable progenitor-backup.json";
        var listed = new List<WorldManifestEntry>();
        foreach (var f in side.Files)
            if (!WorldRestorePaths.IsPerSaveSettings(f.Path) && !WorldRestorePaths.IsTransient(f.Path)) listed.Add(f);
        foreach (var f in listed)
            if (f.Sha256.Length == 0) return f.Path + " has no SHA-256 in the record";
        return WorldManifest.Compare(hashed, listed) is { } diff ? diff : WorldManifest.Compare(listed, hashed);
    }

    /// <summary>The source's files a restore copies: everything but <c>.TEMP\</c>, the sidecar and the per-save settings.</summary>
    internal static bool SkipForStaging(string relative) =>
        WorldBackupCatalog.SkipSidecar(relative) || WorldRestorePaths.IsPerSaveSettings(relative);

    /// <summary>Step 1 (spec §3.2): null when the request may run, else why not. <paramref name="sourceFiles"/>: the source's shape.</summary>
    internal static string? Validate(IWorldFs fs, RestoreRequest r, RestoreEnvironment env, out WorldRestorePaths.SaveLayout layout,
        out List<WorldManifestEntry> sourceFiles)
    {
        sourceFiles = new List<WorldManifestEntry>();
        layout = default;
        if (FenceRefusal(r, env) is { } fence) return fence;
        if (WorldRestorePaths.IsHostedGame(env.Args, r.SaveFolder)) return WorldRestorePaths.HostedRefusal.TrimEnd('.');
        if (!WorldRestorePaths.TryLayout(r.SaveFolder, out layout, out string why)) return why;
        if (!string.Equals(layout.SaveName, r.SaveName, StringComparison.OrdinalIgnoreCase))
            return "the request's save name (" + r.SaveName + ") is not its folder's (" + layout.SaveName + ")";
        string? argSave = WorldRestorePaths.Arg(env.Args, "saveName") ?? WorldRestorePaths.Arg(env.Args, "serverSaveName");
        if (argSave != null && !string.Equals(argSave.Trim(), layout.SaveName, StringComparison.OrdinalIgnoreCase))
            return "this server starts with -saveName " + argSave + ", the request is for " + layout.SaveName;
        string? argPersist = WorldRestorePaths.Arg(env.Args, "persistentDataPath");
        if (argPersist != null && !WorldRestorePaths.SamePath(argPersist.Trim().Trim('"'), layout.PersistRoot))
            return "this server starts with -persistentDataPath " + argPersist + ", the request is for " + layout.PersistRoot;
        if (!fs.DirectoryExists(layout.SaveFolder)) return "the save folder " + layout.SaveFolder + " does not exist";

        string src = (r.SourcePath ?? "").Trim();
        if (src.Length == 0 || !fs.DirectoryExists(src)) return "the restore point " + r.SourceName + " is gone (" + src + ")";
        if (WorldRestorePaths.SamePath(src, layout.SaveFolder)) return "the source is the live save folder";
        string parent = Path.GetDirectoryName(Path.GetFullPath(src).TrimEnd('\\', '/')) ?? "";
        string name = Path.GetFileName(Path.GetFullPath(src).TrimEnd('\\', '/'));
        if (r.SourceKind == WorldBackupEntry.KindArchive)
        {
            if (!WorldRestorePaths.SamePath(parent, layout.VersionRoot) || !WorldRestorePaths.IsArchiveName(layout.SaveName, name, out _))
                return "the source " + src + " is not a wipe archive of " + layout.SaveName;
        }
        else if (r.SourceKind == WorldBackupEntry.KindBackup)
        {
            if (!WorldRestorePaths.SamePath(parent, layout.BackupsRoot) || name.StartsWith(".", StringComparison.Ordinal)
                || WorldBackupCatalog.SidecarPath(fs, src) == null)
                return "the source " + src + " is not a Progenitor backup of " + layout.SaveName;
        }
        else return "unknown source kind '" + r.SourceKind + "'";

        sourceFiles = WorldManifest.Build(fs, src, hash: false, WorldBackupCatalog.SkipSidecar);
        if (WorldManifest.NewestSave(sourceFiles) == null) return "the restore point holds no save file";
        if (r.ExpectShape.Length == 0) return "the request does not say which state of the restore point it expects";
        if (!string.Equals(WorldManifest.ShapeHash(sourceFiles), r.ExpectShape, StringComparison.OrdinalIgnoreCase))
            return "the restore point changed after it was chosen; choose it again";

        if (!WipeModes.IsKnown(r.WipeMode)) return "unknown wipe choice '" + r.WipeMode + "'";
        if (r.WipeMode is WipeModes.WipesOff or WipeModes.Date)
        {
            if (string.IsNullOrWhiteSpace(r.HostSettingsPath)) return "the request names no host settings file for the wipe choice";
            if (!string.Equals(Path.GetFileName(r.HostSettingsPath), WorldRestorePaths.HostSettingsName, StringComparison.OrdinalIgnoreCase))
                return "the wipe choice's settings file is not a " + WorldRestorePaths.HostSettingsName;
            if (r.WipeMode == WipeModes.Date && r.WipeIntervalDays < 1) return "the new wipe date gives no interval";
        }

        // M7: the settings file lies under the server's root or the persistent-data root.
        if (!string.IsNullOrWhiteSpace(r.HostSettingsPath))
        {
            string serverRoot = ServerRoot(env);
            if (!WorldRestorePaths.IsUnder(r.HostSettingsPath, layout.PersistRoot) && !(serverRoot.Length > 0 && WorldRestorePaths.IsUnder(r.HostSettingsPath, serverRoot)))
                return "the host settings file " + r.HostSettingsPath + " is not under this server's folder or its persistent data";
        }

        var currentShape = WorldManifest.Build(fs, layout.SaveFolder, hash: false);
        long need = WorldManifest.TotalBytes(currentShape) + WorldManifest.TotalBytes(sourceFiles);
        need += need / 10 + Math.Max(0, env.SpareBytes);
        long? free = fs.FreeBytes(layout.PersistRoot);
        if (free is { } f && f < need)
            return "not enough free disk space: " + WorldBackupCatalog.FormatBytes(need) + " needed, " + WorldBackupCatalog.FormatBytes(f) + " free";
        return null;
    }

    /// <summary>The server's root: the folder that holds BepInEx.</summary>
    internal static string ServerRoot(RestoreEnvironment env)
    {
        try { return string.IsNullOrEmpty(env.BepInExRoot) ? "" : Path.GetDirectoryName(Path.GetFullPath(env.BepInExRoot).TrimEnd('\\', '/')) ?? ""; }
        catch { return ""; }
    }

    /// <summary>M3: the live-server fence over every path the request and the process name (final paths). Null when clear.</summary>
    internal static string? FenceRefusal(RestoreRequest r, RestoreEnvironment env)
    {
        var paths = new List<string?> { r.SaveFolder, r.SourcePath, r.HostSettingsPath, env.ProcessPath, env.BepInExRoot };
        paths.AddRange(r.WipeLayers);
        foreach (string? p in paths)
            if (WorldRestorePaths.IsLiveServerPath(p)) return LiveRefusal;
        return WorldRestorePaths.IsLiveServerCommandLine(env.Args) ? LiveRefusal : null;
    }

    internal static readonly string LiveRefusal =
        "refusing the live dedicated server path (" + WorldRestorePaths.ForbiddenLiveServerRoot + "); a world restore never runs there";

    /// <summary>
    /// Step 5: the admin's wipe choice. <see cref="WipeModes.ClockToday"/> rewrites the staged <c>StartDate.json</c>; the
    /// interval choices write the next-start host settings (the staged copy when the file is the per-save one; otherwise the
    /// original bytes and write time are journaled first, M7). Returns the sentence the result reports.
    /// </summary>
    private static string ApplyWipe(IWorldFs fs, RestoreRequest r, RestoreEnvironment env, WorldRestorePaths.SaveLayout layout, string incoming,
        RestoreJournal journal)
    {
        if (r.WipeMode == WipeModes.ClockToday)
        {
            string path = Path.Combine(incoming, WorldRestorePaths.StartDateName);
            if (fs.FileExists(path)) fs.CopyFile(path, Path.Combine(incoming, WorldRestorePaths.StartDateBeforeRestoreName));
            var day = env.LocalNow().Date;
            string today = WorldRestoreWipe.FormatStartDate(day);
            WriteAtomic(fs, path, Encoding.UTF8.GetBytes(today));
            if (WorldRestoreWipe.ParseStartDate(WorldJson.Text(fs.ReadAllBytes(path))) != day)
                throw new Abort("the restored StartDate.json did not read back as today");
            return "The wipe clock restarts today: StartDate.json is " + today + " (the original is kept as " + WorldRestorePaths.StartDateBeforeRestoreName + ").";
        }

        int interval = r.WipeMode == WipeModes.WipesOff ? 0 : r.WipeIntervalDays;
        string write = StagedPath(r.HostSettingsPath, layout, incoming);
        bool perSave = !string.Equals(write, r.HostSettingsPath, StringComparison.Ordinal);
        byte[]? before = fs.FileExists(write) ? fs.ReadAllBytes(write) : null;
        byte[]? after = WorldRestoreWipe.SetTopLevel(before, "ResetDaysInterval", interval.ToString(CultureInfo.InvariantCulture));
        if (after != null && r.WipeMode == WipeModes.Date && r.WipeDayOfReset.Length > 0)
            after = WorldRestoreWipe.SetTopLevel(after, "DayOfReset", JsonSerializer.Serialize(r.WipeDayOfReset));
        if (after == null) throw new Abort(write + " is not a JSON object; the wipe choice was not written");
        if (!perSave)
        {
            journal.SettingsPath = write;
            journal.SettingsExisted = before != null;
            journal.SettingsBefore = before == null ? "" : Convert.ToBase64String(before);
            journal.SettingsMtime = before != null ? fs.GetLastWriteUtc(write) : default;
            journal.SettingsWritten = true;
            WorldRestoreJournalIo.Write(fs, env.BepInExRoot, journal);
        }

        WriteAtomic(fs, write, after);
        if (ReadInt(fs.ReadAllBytes(write), "ResetDaysInterval") != interval)
            throw new Abort(write + " did not read back ResetDaysInterval " + interval);
        return interval == 0
            ? "Scheduled wipes are off (ResetDaysInterval 0 in " + r.HostSettingsPath + ")."
            : "The next wipe is on " + r.WipeDate + " (ResetDaysInterval " + interval
              + (r.WipeDayOfReset.Length > 0 ? ", DayOfReset " + r.WipeDayOfReset : "") + " in " + r.HostSettingsPath + ").";
    }

    /// <summary>A path inside the live save folder, moved into the staged copy; any other path as it is.</summary>
    private static string StagedPath(string path, WorldRestorePaths.SaveLayout layout, string incoming)
    {
        if (string.IsNullOrEmpty(path) || !WorldRestorePaths.IsUnder(path, layout.SaveFolder)) return path;
        string rel = Path.GetFullPath(path).Substring(Path.GetFullPath(layout.SaveFolder).TrimEnd('\\', '/').Length).TrimStart('\\', '/');
        return Path.Combine(incoming, rel);
    }

    /// <summary>
    /// M4: the wipe check the game makes at this start, recomputed from the staged world's <c>StartDate.json</c> and the
    /// next start's settings (the command line and environment first, else the highest settings layer that sets the key).
    /// Null when no wipe is due, else why the restore stops.
    /// </summary>
    internal static string? WipeDueNow(IWorldFs fs, RestoreRequest r, RestoreEnvironment env, WorldRestorePaths.SaveLayout layout, string incoming)
    {
        int interval = 0;
        string day = "Any";
        var layers = new List<string>(r.WipeLayers);
        if (layers.Count == 0 && r.HostSettingsPath.Length > 0) layers.Add(r.HostSettingsPath);
        foreach (string layer in layers)
        {
            string path = StagedPath(layer, layout, incoming);
            byte[]? bytes = SafeRead(fs, path);
            if (bytes == null) continue;
            if (ReadIntOrText(bytes, "ResetDaysInterval") is { } n) interval = n;
            if (ReadDay(bytes) is { } d) day = d;
        }

        string? argInterval = WorldRestorePaths.Arg(env.Args, "resetDaysInterval") ?? SafeEnv(env, "VR_RESET_DAYS_INTERVAL");
        if (argInterval != null && int.TryParse(argInterval.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ai)) interval = ai;
        string? argDay = WorldRestorePaths.Arg(env.Args, "dayOfReset") ?? SafeEnv(env, "VR_DAY_OF_RESET");
        if (argDay != null && DayWord(argDay.Trim()) is { } ad) day = ad;

        var now = env.LocalNow();
        if (r.WipeMode == WipeModes.Date && (!DateTime.TryParseExact(r.WipeDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var picked)
            || picked.Date <= now.Date))
            return "the picked wipe date (" + r.WipeDate + ") is not after today any more; choose the restore again";
        var start = WorldRestoreWipe.ParseStartDate(WorldJson.Text(SafeRead(fs, Path.Combine(incoming, WorldRestorePaths.StartDateName)) ?? Array.Empty<byte>()));
        if (!WorldRestoreWipe.IsDue(start, interval, day, now, out var reset)) return null;
        return "the restored world would be wiped at this start (" + (reset is { } d2 ? "its wipe date " + d2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "its start date is unknown") + ", ResetDaysInterval " + interval + "); choose a wipe answer and request it again";
    }

    private static string? SafeEnv(RestoreEnvironment env, string name)
    {
        try { return env.GetEnv(name); }
        catch { return null; }
    }

    private static int? ReadIntOrText(byte[] json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(WorldJson.StripBom(json), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            int? last = null;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int n)) last = n;
                else if (p.Value.ValueKind == JsonValueKind.String && int.TryParse(p.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int t)) last = t;
            }

            return last;
        }
        catch (Exception) { return null; }
    }

    /// <summary><c>DayOfReset</c> as the game reads it: -1 or a word (Any), 0 to 6 (Monday to Sunday).</summary>
    private static string? ReadDay(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(WorldJson.StripBom(json), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            string? last = null;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(p.Name, "DayOfReset", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int n)) last = n < 0 ? "Any" : n <= 6 ? WorldRestoreWipe.Days[n + 1] : null;
                else if (p.Value.ValueKind == JsonValueKind.String) last = DayWord(p.Value.GetString() ?? "");
            }

            return last;
        }
        catch (Exception) { return null; }
    }

    private static string? DayWord(string text)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return n < 0 ? "Any" : n <= 6 ? WorldRestoreWipe.Days[n + 1] : null;
        foreach (string d in WorldRestoreWipe.Days)
            if (string.Equals(d, text, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    /// <summary>Temp file, then replace; both flushed.</summary>
    internal static void WriteAtomic(IWorldFs fs, string path, byte[] bytes)
    {
        string temp = path + ".progenitor-restore-tmp";
        try
        {
            fs.WriteAllBytes(temp, bytes);
            fs.MoveFile(temp, path, overwrite: true);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) fs.FlushDirectory(dir!);
        }
        catch
        {
            try { fs.DeleteFile(temp); } catch { /* the original reason stands */ }
            throw;
        }
    }

    internal static int? ReadInt(byte[] json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(WorldJson.StripBom(json),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            int? last = null;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int n))
                    last = n;
            return last;
        }
        catch (Exception) { return null; }
    }

    internal static string UniqueName(IWorldFs fs, string root, string name)
    {
        string candidate = name;
        for (int i = 2; i < 100; i++)
        {
            string path = Path.Combine(root, candidate);
            if (!fs.DirectoryExists(path) && !fs.DirectoryExists(path + WorldRestorePaths.PartialSuffix)) return candidate;
            candidate = name + "-" + i.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }
}

/// <summary>WORLDRESTORE2: reading and writing the journal (atomic, flushed).</summary>
internal static class WorldRestoreJournalIo
{
    internal static string PathOf(string bepInExRoot) => Path.Combine(bepInExRoot ?? "", WorldRestorePaths.JournalFileName);

    internal static RestoreJournal? Read(IWorldFs fs, string bepInExRoot, out bool unreadable)
    {
        unreadable = false;
        string path = PathOf(bepInExRoot);
        if (!fs.FileExists(path)) return null;
        var j = RestoreJournal.Parse(fs.ReadAllBytes(path));
        unreadable = j == null;
        return j;
    }

    internal static void Write(IWorldFs fs, string bepInExRoot, RestoreJournal journal)
    {
        if (string.IsNullOrEmpty(bepInExRoot)) throw new InvalidOperationException("the BepInEx folder is not known; the restore cannot keep its journal");
        WorldRestoreApply.WriteAtomic(fs, PathOf(bepInExRoot), journal.ToJson());
    }

    internal static void Delete(IWorldFs fs, string bepInExRoot)
    {
        if (string.IsNullOrEmpty(bepInExRoot)) return;
        fs.DeleteFile(PathOf(bepInExRoot));
    }
}
