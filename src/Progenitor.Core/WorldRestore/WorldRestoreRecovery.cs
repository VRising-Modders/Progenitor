using System;
using System.IO;

namespace Progenitor.WorldRestore;

/// <summary>What the start-time recovery pass did.</summary>
internal sealed class RecoveryOutcome
{
    /// <summary>The result of an interrupted restore it finished or undid, or null (nothing was interrupted).</summary>
    public RestoreResult? Result { get; set; }

    /// <summary>The save folder is missing and could not be put back: the process must stop before the game loads.</summary>
    public bool FailClosed { get; set; }

    public string Reason { get; set; } = "";

    /// <summary>A journal file is there but cannot be read: the debris guard decides whether the server may start.</summary>
    public bool UnreadableJournal { get; set; }

    /// <summary>The save folder the journal named (for the debris guard), or empty.</summary>
    public string JournalSaveFolder { get; set; } = "";
}

/// <summary>
/// WORLDRESTORE2 (H1, H3, M1, M6, L1): the pass that runs at every server start before any request is looked at. It reads
/// <c>BepInEx\progenitor-restore.journal.json</c>, the record of the restore in progress, and settles it:
/// <list type="bullet">
/// <item>Interrupted before the swap (<c>prepare</c>, <c>backed-up</c>, <c>staged</c>): the world was never touched. The
/// host settings file is put back from the journal and the copies are removed.</item>
/// <item>Interrupted during the swap (<c>swapping</c>): with the save folder there and no <c>.outgoing</c>, the first rename
/// never happened (undo as above); with both there, the second one did (finish). With the save folder missing, the
/// replaced world is renamed back from <c>.outgoing</c>; when that fails, the verified staged copy is rolled forward; when
/// neither is possible the process stops (fail closed), so the game never starts an empty world in its place.</item>
/// <item>Swapped (<c>swapped</c>): a restore whose result was never written is finished and reported as the success it
/// was (M6). Once the restored world has saved (Bootstrap's save hook marks the journal), the replaced world in
/// <c>.outgoing</c> is removed and the journal with it. A new request while it waits keeps the replaced world as a backup
/// of its own instead.</item>
/// </list>
/// File-only.
/// </summary>
internal static class WorldRestoreRecovery
{
    /// <param name="waitingRequestId">The id of the request file waiting now, or null when there is none.</param>
    internal static RecoveryOutcome Run(IWorldFs fs, RestoreEnvironment env, string? waitingRequestId)
    {
        var outcome = new RecoveryOutcome();
        RestoreJournal? j;
        try
        {
            j = WorldRestoreJournalIo.Read(fs, env.BepInExRoot, out bool unreadable);
            if (unreadable)
            {
                outcome.UnreadableJournal = true;
                WorldRestoreApply.Say(env, "the restore journal " + WorldRestoreJournalIo.PathOf(env.BepInExRoot) + " cannot be read");
                return outcome;
            }
        }
        catch (Exception e)
        {
            WorldRestoreApply.Say(env, "the restore journal could not be read: " + e.Message);
            return outcome;
        }

        if (j == null) return outcome;
        outcome.JournalSaveFolder = j.SaveFolder;
        bool newRequestWaiting = waitingRequestId != null && !string.Equals(waitingRequestId, j.RequestId, StringComparison.Ordinal);
        foreach (string p in new[] { j.SaveFolder, j.BackupsRoot, j.Backup, j.Incoming, j.Outgoing, j.Partial, j.SettingsPath })
        {
            if (!WorldRestorePaths.IsLiveServerPath(p)) continue;
            WorldRestoreApply.Say(env, "the journal names the live server path; it is left as it is: " + p);
            return outcome;
        }

        if (!WorldRestorePaths.TryLayout(j.SaveFolder, out var layout, out string why))
        {
            WorldRestoreApply.Say(env, "the journal's save folder is not a save folder (" + why + "); it is left as it is");
            return outcome;
        }

        var result = new RestoreResult
        {
            RequestId = j.RequestId,
            SourceName = j.SourceName,
            SaveFolder = j.SaveFolder,
            // The recovery's own time: a journal's start can be from an earlier process, or missing in a hand-written one.
            StartedUtc = env.UtcNow(),
            WipeApplied = j.WipeApplied,
        };
        result.Steps.Add(new RestoreStep { Name = "recovery", Ok = true, Detail = "journal phase " + j.Phase });

        switch (j.Phase)
        {
            case RestoreJournal.PhaseSwapped:
                return Swapped(fs, env, j, layout, result, newRequestWaiting, outcome);
            case RestoreJournal.PhaseSwapping:
            {
                bool save = fs.DirectoryExists(j.SaveFolder);
                bool gone = j.Outgoing.Length > 0 && fs.DirectoryExists(j.Outgoing);
                if (save && !gone) return Undo(fs, env, j, result, outcome, "interrupted before the first rename of the swap");
                if (save && gone)
                {
                    j.Phase = RestoreJournal.PhaseSwapped;
                    j.Incoming = "";
                    j.SwappedUtc = env.UtcNow();
                    WorldRestoreJournalIo.Write(fs, env.BepInExRoot, j);
                    return Swapped(fs, env, j, layout, result, newRequestWaiting, outcome);
                }

                if (gone)
                {
                    try
                    {
                        WorldRestoreApply.RetryMove(fs, j.Outgoing, j.SaveFolder, env);
                        WorldRestoreApply.Say(env, "recovery: the current world was renamed back from " + j.Outgoing);
                        return Undo(fs, env, j, result, outcome, "interrupted between the two renames of the swap; the current world was put back");
                    }
                    catch (Exception e) { WorldRestoreApply.Say(env, "recovery: " + j.Outgoing + " could not be renamed back: " + e.Message); }
                }

                if (j.IncomingVerified && j.Incoming.Length > 0 && fs.DirectoryExists(j.Incoming))
                {
                    try
                    {
                        WorldRestoreApply.RetryMove(fs, j.Incoming, j.SaveFolder, env);
                        WorldRestoreApply.Say(env, "recovery: the verified restored world was rolled forward from " + j.Incoming);
                        j.Phase = RestoreJournal.PhaseSwapped;
                        j.Incoming = "";
                        j.SwappedUtc = env.UtcNow();
                        WorldRestoreJournalIo.Write(fs, env.BepInExRoot, j);
                        result.Warnings.Add("The restore was interrupted during the swap and finished at the next start.");
                        return Swapped(fs, env, j, layout, result, newRequestWaiting, outcome);
                    }
                    catch (Exception e) { WorldRestoreApply.Say(env, "recovery: " + j.Incoming + " could not be rolled forward: " + e.Message); }
                }

                outcome.FailClosed = true;
                outcome.Reason = "the save folder " + j.SaveFolder + " is missing after an interrupted restore and could not be put back"
                    + (gone ? " from " + j.Outgoing : "") + "; the verified backup of the previous world is " + j.Backup
                    + ". Rename it (or " + j.Outgoing + ") to " + j.SaveFolder + " by hand, then start the server";
                result.Ok = false;
                result.Stranded = true;
                result.Reason = outcome.Reason;
                result.Summary = "STOPPED: " + outcome.Reason + ".";
                result.FinishedUtc = env.UtcNow();
                outcome.Result = result;
                return outcome;
            }

            default:
                return Undo(fs, env, j, result, outcome, "interrupted before the swap (" + j.Phase + ")");
        }
    }

    /// <summary>The world is the one it was: put the settings file back, remove the copies, and report a refusal.</summary>
    private static RecoveryOutcome Undo(IWorldFs fs, RestoreEnvironment env, RestoreJournal j, RestoreResult result, RecoveryOutcome outcome, string what)
    {
        string? problem = WorldRestoreApply.RollBackBeforeSwap(fs, j, env, partial: true, incoming: true, backup: true);
        result.Ok = false;
        result.Reason = "the restore was " + what + (problem != null ? "; " + problem : "");
        result.Summary = "Not restored: " + result.Reason + "." + (problem == null ? " Nothing changed." : "");
        result.FinishedUtc = env.UtcNow();
        WorldRestoreApply.Say(env, "recovery: " + result.Summary);
        outcome.Result = result;
        return outcome;
    }

    private static RecoveryOutcome Swapped(IWorldFs fs, RestoreEnvironment env, RestoreJournal j, WorldRestorePaths.SaveLayout layout,
        RestoreResult result, bool newRequestWaiting, RecoveryOutcome outcome)
    {
        if (!j.ResultWritten)
        {
            // M6: the swap happened; the result file never said so. Finish and report the true outcome.
            WorldRestoreApply.FinishAfterSwap(fs, j, layout, env, result);
            result.Warnings.Add("The restore was interrupted after the swap and finished at the next start.");
            result.FinishedUtc = env.UtcNow();
            WorldRestoreJournalIo.Write(fs, env.BepInExRoot, j);
            WorldRestoreApply.Say(env, "recovery: " + result.Summary);
            outcome.Result = result;
        }

        if (j.Outgoing.Length == 0 || !fs.DirectoryExists(j.Outgoing))
        {
            if (j.ResultWritten || outcome.Result == null) Close(fs, env);
            return outcome;
        }

        if (j.Confirmed || SavedSince(fs, j))
        {
            // H3: the restored world saved after its load; the replaced world (a copy of the verified backup) can go.
            try
            {
                string doomed = Path.Combine(layout.BackupsRoot, WorldRestorePaths.DeletingPrefix + Path.GetFileName(j.Outgoing));
                WorldRestoreApply.RetryMove(fs, j.Outgoing, doomed, env);
                fs.DeleteDirectory(doomed);
                WorldRestoreApply.Say(env, "the restored world has saved since the restore; the replaced copy " + j.Outgoing + " is removed");
                Close(fs, env);
            }
            catch (Exception e) { WorldRestoreApply.Say(env, "the replaced copy " + j.Outgoing + " stays (" + e.Message + ")"); }
            return outcome;
        }

        if (newRequestWaiting)
        {
            // A new restore before the restored world ever saved: keep the replaced world as a backup of its own.
            try
            {
                string keep = WorldRestoreApply.KeepAsBackup(fs, j.Outgoing, layout, j.RequestId, j.SourceName, env,
                    "Replaced by a restore whose world never saved before the next restore.");
                WorldRestoreApply.Say(env, "the replaced world of request " + j.RequestId + " is kept as the backup " + keep);
                Close(fs, env);
            }
            catch (Exception e) { WorldRestoreApply.Say(env, "the replaced world stays at " + j.Outgoing + " (" + e.Message + ")"); }
            return outcome;
        }

        WorldRestoreApply.Say(env, "the restored world has not saved yet; the replaced world stays at " + j.Outgoing + " until it does");
        return outcome;
    }

    /// <summary>A save file in the restored folder written after the swap: the restored world loaded and saved.</summary>
    private static bool SavedSince(IWorldFs fs, RestoreJournal j)
    {
        if (j.SwappedUtc == default) return false;
        try
        {
            var newest = WorldManifest.NewestSave(WorldManifest.Build(fs, j.SaveFolder, hash: false));
            return newest != null && newest.LastWriteUtc > j.SwappedUtc;
        }
        catch { return false; }
    }

    private static void Close(IWorldFs fs, RestoreEnvironment env)
    {
        try { WorldRestoreJournalIo.Delete(fs, env.BepInExRoot); }
        catch (Exception e) { WorldRestoreApply.Say(env, "the restore journal could not be removed: " + e.Message); }
    }

    /// <summary>
    /// WORLDRESTORE3: the last check before the game may load, independent of the journal. For every save folder this start
    /// knows (the command line, the journal, the request): when it is missing and restore debris lies beside its backups
    /// (<c>.outgoing-*</c>, <c>.incoming-*</c>, <c>*.partial</c>) or the journal could not be read, the world is put back from
    /// the one <c>.outgoing-*</c> folder that holds a world, or the start is refused (the caller stops the process). An
    /// unreadable journal next to a present save folder is set aside. Null when the game may start, else why not.
    /// </summary>
    internal static string? Guard(IWorldFs fs, RestoreEnvironment env, System.Collections.Generic.IEnumerable<string?> saveFolders, bool unreadableJournal)
    {
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool anyKnown = false;
        foreach (string? candidate in saveFolders)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !WorldRestorePaths.TryLayout(candidate, out var layout, out _)) continue;
            if (!seen.Add(layout.SaveFolder) || WorldRestorePaths.IsLiveServerPath(layout.SaveFolder)) continue;
            anyKnown = true;
            if (fs.DirectoryExists(layout.SaveFolder)) continue;
            var outgoing = new System.Collections.Generic.List<string>();
            bool debris = false;
            foreach (string dir in SafeDirs(fs, layout.BackupsRoot))
            {
                string name = Path.GetFileName(dir);
                bool isOut = name.StartsWith(".outgoing-", StringComparison.Ordinal);
                if (isOut || name.StartsWith(".incoming-", StringComparison.Ordinal) || name.EndsWith(WorldRestorePaths.PartialSuffix, StringComparison.OrdinalIgnoreCase))
                    debris = true;
                if (isOut && WorldManifest.NewestSave(WorldManifest.Build(fs, dir, hash: false)) != null) outgoing.Add(dir);
            }

            if (!debris && !unreadableJournal) continue;
            if (outgoing.Count == 1)
            {
                try
                {
                    WorldRestoreApply.RetryMove(fs, outgoing[0], layout.SaveFolder, env);
                    WorldRestoreApply.Say(env, "the save folder " + layout.SaveFolder + " was missing after a restore; the world in " + outgoing[0]
                        + " was put back");
                    continue;
                }
                catch (Exception e)
                {
                    return "the save folder " + layout.SaveFolder + " is missing after a restore and the world in " + outgoing[0]
                        + " could not be renamed back (" + e.Message + "). Rename it to " + layout.SaveFolder + " by hand, then start the server";
                }
            }

            return "the save folder " + layout.SaveFolder + " is missing after a restore"
                + (unreadableJournal ? " and the restore journal cannot be read" : "")
                + (outgoing.Count == 0 ? "; no .outgoing folder holds a world" : "; " + outgoing.Count + " .outgoing folders hold a world (" + string.Join(", ", outgoing) + ")")
                + ". Put the right world back at " + layout.SaveFolder + " (a verified backup is under " + layout.BackupsRoot
                + "), remove the journal, then start the server";
        }

        if (!unreadableJournal) return null;
        string path = WorldRestoreJournalIo.PathOf(env.BepInExRoot);
        if (!anyKnown)
            return "the restore journal " + path + " cannot be read and this start does not name its save folder (-persistentDataPath and "
                + "-saveName), so it cannot be checked. Check the save folder by hand, then remove the journal";
        // Every known save folder is there: the world is safe; the journal is set aside for the operator.
        WorldRestoreApply.Say(env, "the unreadable restore journal is set aside as " + path + ".unreadable (the save folder is in place)");
        try { fs.MoveFile(path, path + ".unreadable", overwrite: true); } catch { /* reported above */ }
        return null;
    }

    private static System.Collections.Generic.IReadOnlyList<string> SafeDirs(IWorldFs fs, string path)
    {
        try { return fs.ListDirectories(path); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// H3: Bootstrap's save hook. The first save after a restore marks the journal confirmed, so the next start may remove
    /// the replaced world. True when it marked one.
    /// </summary>
    internal static bool NoteSaved(IWorldFs fs, RestoreEnvironment env)
    {
        var j = WorldRestoreJournalIo.Read(fs, env.BepInExRoot, out _);
        if (j == null || j.Phase != RestoreJournal.PhaseSwapped || j.Confirmed) return false;
        j.Confirmed = true;
        j.ConfirmedUtc = env.UtcNow();
        WorldRestoreJournalIo.Write(fs, env.BepInExRoot, j);
        WorldRestoreApply.Say(env, "the restored world saved; the replaced copy is removed at the next start");
        return true;
    }
}
