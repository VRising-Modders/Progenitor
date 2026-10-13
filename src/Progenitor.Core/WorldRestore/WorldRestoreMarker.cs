using System;
using System.Collections.Generic;
using System.IO;

namespace Progenitor.WorldRestore;

/// <summary>
/// WORLDRESTORE1: the request file's life (spec §3.2). Core writes <c>BepInEx\progenitor-restore.json</c> atomically (one
/// request at a time: a new one replaces the old; Cancel deletes it). At the next server start Bootstrap applies it
/// (<see cref="ApplyAtStart"/>), writes <c>progenitor-restore.done.json</c> and renames the request to
/// <c>progenitor-restore.applied-&lt;id&gt;.json</c> or <c>.refused-&lt;id&gt;.json</c>, so it can never run twice (a done file
/// with the same id refuses it too). File-only.
/// </summary>
internal static class WorldRestoreMarker
{
    internal static string RequestPath(string bepInExRoot) => Path.Combine(bepInExRoot, WorldRestorePaths.RequestFileName);

    internal static string ResultPath(string bepInExRoot) => Path.Combine(bepInExRoot, WorldRestorePaths.ResultFileName);

    /// <summary>Write (or replace) the pending request.</summary>
    internal static void Write(IWorldFs fs, string bepInExRoot, RestoreRequest request) =>
        WorldRestoreApply.WriteAtomic(fs, RequestPath(bepInExRoot), request.ToJson());

    /// <summary>The pending request, or null (none, or unreadable: <paramref name="error"/> says which).</summary>
    internal static RestoreRequest? ReadPending(IWorldFs fs, string bepInExRoot, out string error)
    {
        error = "";
        string path = RequestPath(bepInExRoot);
        if (!fs.FileExists(path)) return null;
        try { return RestoreRequest.Parse(fs.ReadAllBytes(path), out error); }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>Delete the pending request. True when one was there.</summary>
    internal static bool Cancel(IWorldFs fs, string bepInExRoot)
    {
        string path = RequestPath(bepInExRoot);
        if (!fs.FileExists(path)) return false;
        fs.DeleteFile(path);
        return true;
    }

    /// <summary>The last apply's result, or null.</summary>
    internal static RestoreResult? ReadResult(IWorldFs fs, string bepInExRoot)
    {
        string path = ResultPath(bepInExRoot);
        try { return fs.FileExists(path) ? RestoreResult.Parse(fs.ReadAllBytes(path)) : null; }
        catch { return null; }
    }

    /// <summary>
    /// Bootstrap, server process, <c>Plugin.Load</c>. In order: the live-server fence over the process (before any file
    /// operation), the recovery pass over the journal of an interrupted restore (<see cref="WorldRestoreRecovery"/>), then a
    /// pending request. Writes the result file, settles the journal and renames the request so it never runs twice. When the
    /// save folder cannot be put back it calls <see cref="RestoreEnvironment.FailClosed"/> (Bootstrap stops the process).
    /// Returns the result to report, or null. Never throws.
    /// </summary>
    internal static RestoreResult? ApplyAtStart(IWorldFs fs, string bepInExRoot, RestoreEnvironment env) => ApplyAtStart(fs, bepInExRoot, env, out _);

    /// <summary>
    /// <inheritdoc cref="ApplyAtStart(IWorldFs, string, RestoreEnvironment)"/> <paramref name="halted"/>: the live-server fence
    /// held, or the process was told to stop (fail closed); nothing else may run before the game (BACKUPNOW1: the start backup).
    /// </summary>
    internal static RestoreResult? ApplyAtStart(IWorldFs fs, string bepInExRoot, RestoreEnvironment env, out bool halted)
    {
        var known = new List<string?>();
        bool unreadable = false, fenced = false, stopped = false;
        RestoreResult? result = null;
        try { result = ApplyAtStartCore(fs, bepInExRoot, env, known, ref unreadable, ref fenced, ref stopped); }
        catch (Exception e) { Say(env, "the start-time restore threw: " + e.Message); }

        halted = fenced || stopped;
        if (halted) return result;
        // WORLDRESTORE3: whatever happened above, the game never starts with the save folder missing beside restore debris
        // or an unreadable journal.
        string? refusal;
        try
        {
            try { known.Insert(0, WorldRestorePaths.SaveFolderFromArgs(fs, env.Args)); } catch { /* the others */ }
            refusal = WorldRestoreRecovery.Guard(fs, env, known, unreadable);
        }
        catch (Exception e) { refusal = "the save folder could not be checked after a restore (" + e.Message + ")"; }

        if (refusal != null)
        {
            var stop = new RestoreResult
            {
                RequestId = result?.RequestId ?? "",
                Ok = false,
                Stranded = true,
                Reason = refusal,
                Summary = "STOPPED: " + refusal + ".",
                StartedUtc = env.UtcNow(),
                FinishedUtc = env.UtcNow(),
            };
            try { WorldRestoreApply.WriteAtomic(fs, ResultPath(bepInExRoot), stop.ToJson()); } catch { /* the log says it */ }
            Stop(env, refusal);
            halted = true;
            return stop;
        }

        return result;
    }

    private static RestoreResult? ApplyAtStartCore(IWorldFs fs, string bepInExRoot, RestoreEnvironment env, List<string?> known,
        ref bool unreadable, ref bool fenced, ref bool stopped)
    {
        if (string.IsNullOrEmpty(env.BepInExRoot)) env.BepInExRoot = bepInExRoot;
        // M3: nothing is read or written for a process of the live server.
        if (WorldRestorePaths.IsLiveServerPath(bepInExRoot) || WorldRestorePaths.IsLiveServerPath(env.ProcessPath)
            || WorldRestorePaths.IsLiveServerCommandLine(env.Args))
        {
            Say(env, "this process belongs to the live server path (" + WorldRestorePaths.ForbiddenLiveServerRoot + "); restores are off here");
            fenced = true;
            return null;
        }

        string path = RequestPath(bepInExRoot);
        bool waiting;
        try { waiting = fs.FileExists(path); }
        catch { waiting = false; }

        RestoreRequest? request = null;
        string error = "";
        if (waiting)
        {
            try { request = RestoreRequest.Parse(fs.ReadAllBytes(path), out error); }
            catch (Exception e) { error = e.Message; }
        }

        RecoveryOutcome recovery;
        try { recovery = WorldRestoreRecovery.Run(fs, env, waiting ? request?.RequestId ?? "(unreadable)" : null); }
        catch (Exception e)
        {
            Say(env, "the recovery pass threw: " + e.Message);
            recovery = new RecoveryOutcome();
        }

        RestoreResult? recovered = recovery.Result;
        unreadable = recovery.UnreadableJournal;
        known.Add(recovery.JournalSaveFolder);
        known.Add(request?.SaveFolder);
        if (recovered != null) Publish(fs, bepInExRoot, recovered, env);
        if (recovery.FailClosed)
        {
            Stop(env, recovery.Reason);
            stopped = true;
            return recovered;
        }

        // An unreadable journal: no request runs until the debris guard has seen the save folder.
        if (unreadable) return recovered;

        if (!waiting) return recovered;

        RestoreResult result;
        if (request != null && recovered != null && string.Equals(recovered.RequestId, request.RequestId, StringComparison.Ordinal))
            result = recovered;
        else
        {
            bool publish = true;
            if (request == null) result = Refused("", "the request file could not be read (" + error + ")", env);
            else
            {
                var last = ReadResult(fs, bepInExRoot);
                if (last != null && string.Equals(last.RequestId, request.RequestId, StringComparison.Ordinal))
                {
                    // M6: it ran already and only the rename of the request was lost: report what it did, never run it again.
                    Say(env, "request " + request.RequestId + " already ran (" + (last.Ok ? "restored" : "refused") + "); it is not run again");
                    result = last;
                    publish = false;
                }
                else
                {
                    result = WorldRestoreApply.Run(fs, request, env);
                    result.RanThisStart = true;
                }
            }

            if (publish) Publish(fs, bepInExRoot, result, env);
        }

        if (result.Stranded)
        {
            // The request stays: the next start runs the recovery pass again.
            Stop(env, result.Reason);
            stopped = true;
            return result;
        }

        string id = request?.RequestId ?? "unreadable-" + env.UtcNow().ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string done = Path.Combine(bepInExRoot, result.Ok ? WorldRestorePaths.AppliedName(id) : WorldRestorePaths.RefusedName(id));
        try { fs.MoveFile(path, done, overwrite: true); }
        catch (Exception e)
        {
            Say(env, "the request file could not be renamed (" + e.Message + "); deleting it so it cannot run again");
            try { fs.DeleteFile(path); }
            catch (Exception d) { Say(env, "the request file could not be deleted either (" + d.Message + "): remove " + path + " by hand"); }
        }

        return result;
    }

    /// <summary>Write the result file, then mark the journal of the same request: its result is out.</summary>
    private static void Publish(IWorldFs fs, string bepInExRoot, RestoreResult result, RestoreEnvironment env)
    {
        try { WorldRestoreApply.WriteAtomic(fs, ResultPath(bepInExRoot), result.ToJson()); }
        catch (Exception e)
        {
            Say(env, "the result file could not be written: " + e.Message);
            return;
        }

        try
        {
            var j = WorldRestoreJournalIo.Read(fs, bepInExRoot, out _);
            if (j == null || !string.Equals(j.RequestId, result.RequestId, StringComparison.Ordinal) || j.Phase != RestoreJournal.PhaseSwapped) return;
            j.ResultWritten = true;
            if (j.Outgoing.Length == 0 || !fs.DirectoryExists(j.Outgoing)) WorldRestoreJournalIo.Delete(fs, bepInExRoot);
            else WorldRestoreJournalIo.Write(fs, bepInExRoot, j);
        }
        catch (Exception e) { Say(env, "the restore journal could not be updated: " + e.Message); }
    }

    private static void Stop(RestoreEnvironment env, string reason)
    {
        Say(env, "STOPPING THE SERVER before the game loads: " + reason);
        try { env.FailClosed(reason); }
        catch (Exception e) { Say(env, "the stop threw: " + e.Message); }
    }

    private static RestoreResult Refused(string id, string reason, RestoreEnvironment env)
    {
        var now = env.UtcNow();
        var r = new RestoreResult
        {
            RequestId = id,
            Ok = false,
            Reason = reason,
            Summary = "Not restored: " + reason + ". Nothing changed.",
            StartedUtc = now,
            FinishedUtc = now,
        };
        Say(env, "REFUSED: " + reason);
        return r;
    }

    private static void Say(RestoreEnvironment env, string line)
    {
        try { env.Log("WorldRestore: " + line); }
        catch { /* never decides */ }
    }
}
