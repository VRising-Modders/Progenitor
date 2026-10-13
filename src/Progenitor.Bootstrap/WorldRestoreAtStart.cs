using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Progenitor.WorldRestore;

namespace Progenitor.Bootstrap;

/// <summary>
/// WORLDRESTORE1: Bootstrap's half of the world restore. <see cref="Run"/> is called from <c>Plugin.Load</c> in a server
/// process, before any patch and before Core loads, which is before <c>GameBootstrap.Start</c> resolves the save to load
/// (research restore-world-backup-spec-2026-10-06 §2 and §3.1). It applies <c>BepInEx\progenitor-restore.json</c> when one is
/// there (<see cref="WorldRestoreMarker.ApplyAtStart(IWorldFs, string, RestoreEnvironment)"/>: validate, back up, verify, stage,
/// verify, swap, the wipe choice, retention, the result file), then the scheduled start backup when
/// <c>[WorldRestore] BackupAtStart</c> is on (BACKUPNOW1, <see cref="WorldBackupAtStart"/>), and logs the phase 0 timing lines: the time of <c>Plugin.Load</c> and the newest save file
/// it sees, then (from the <c>BeginCreateServerWorld</c> postfix) the folder and file the game chose, with how much later.
/// Bootstrap code: a change here needs a restart of each server. The file work is the shared file-only library in
/// <c>src/Progenitor.Core/WorldRestore</c> (Bootstrap and Core cannot reference each other).
/// </summary>
internal static class WorldRestoreAtStart
{
    private static DateTime _loadUtc;
    private static string _newestAtLoad = "";
    private static string _folderAtLoad = "";
    private static bool _beginCreateLogged;
    private static string _root = "";
    private static bool _saveNoted;
    private static Action<string>? _log;

    /// <summary>The exit code of a fail-closed stop (the save folder is missing and could not be put back).</summary>
    internal const int FailClosedExitCode = 75;

    internal static void Run(string bepInExRoot, Action<string> log)
    {
        _loadUtc = DateTime.UtcNow;
        _root = bepInExRoot ?? "";
        _log = log;
        string[] args;
        try { args = Environment.GetCommandLineArgs(); }
        catch { args = Array.Empty<string>(); }
        log("WorldRestore phase0: Plugin.Load at " + Stamp(_loadUtc) + " (" + SinceStart(_loadUtc) + " after the process started).");

        try
        {
            var env = new RestoreEnvironment
            {
                Args = args,
                ProcessPath = SafeProcessPath(),
                ProgenitorVersion = MyPluginInfo.PLUGIN_VERSION,
                BepInExRoot = bepInExRoot ?? "",
                GetEnv = name =>
                {
                    try { return Environment.GetEnvironmentVariable(name); }
                    catch { return null; }
                },
                Log = log,
                FailClosed = reason => StopProcess(reason, log),
            };
            var result = WorldRestoreMarker.ApplyAtStart(PhysicalWorldFs.Instance, bepInExRoot, env, out bool halted);
            if (result != null)
                log("WorldRestore: " + (result.Ok ? "applied" : "refused") + " request " + result.RequestId + result.Elapsed() + ": " + result.Summary);

            // BACKUPNOW1: the scheduled start backup ([WorldRestore] BackupAtStart), after the restore and before the game
            // loads. A failure is logged and never stops the server.
            try
            {
                var settings = WorldBackupSettings.FromCfg(PhysicalWorldFs.Instance, bepInExRoot ?? "");
                WorldBackupAtStart.Run(PhysicalWorldFs.Instance, bepInExRoot ?? "", env, settings, result, halted);
            }
            catch (Exception e) { log("WorldBackup: the start backup threw (the server starts anyway): " + e.Message); }
        }
        catch (Exception e)
        {
            log("WorldRestore: the start-time restore threw (nothing was swapped unless the log says so): " + e);
        }

        try
        {
            _folderAtLoad = WorldRestorePaths.SaveFolderFromArgs(PhysicalWorldFs.Instance, args) ?? "";
            bool exists = _folderAtLoad.Length > 0 && Directory.Exists(_folderAtLoad);
            var newest = exists
                ? WorldManifest.NewestSave(WorldManifest.Build(PhysicalWorldFs.Instance, _folderAtLoad, hash: false))
                : null;
            _newestAtLoad = newest == null ? "" : Path.GetFileName(newest.Path);
            log("WorldRestore phase0: at " + Stamp(DateTime.UtcNow) + " the save folder from the command line is "
                + (_folderAtLoad.Length == 0 ? "unknown (no -persistentDataPath and -saveName)" : _folderAtLoad + (exists ? "" : " (MISSING)"))
                + "; its newest save file is " + (newest == null ? "none" : _newestAtLoad + " (written " + Stamp(newest.LastWriteUtc) + ")")
                + ". The game picks its save later, in GameBootstrap.Start.");
        }
        catch (Exception e) { log("WorldRestore phase0: the save folder could not be read: " + e.Message); }
    }

    /// <summary>
    /// WORLDRESTORE2 (H1/H2): the save folder is missing and could not be put back. The process stops here, before
    /// <c>GameBootstrap.Start</c> would create an empty world in its place; the result file and the log say what to rename.
    /// A host that restarts the server on exit gets the same stop until an operator fixes the folder.
    /// </summary>
    private static void StopProcess(string reason, Action<string> log)
    {
        log("WorldRestore: FAIL CLOSED: " + reason + ". The server process exits now (code " + FailClosedExitCode + ").");
        try { Console.Error.WriteLine("Progenitor WorldRestore: FAIL CLOSED: " + reason); } catch { /* no console */ }
        // Give BepInEx's disk log a moment to flush the line.
        try { System.Threading.Thread.Sleep(2500); } catch { /* exiting anyway */ }
        Environment.Exit(FailClosedExitCode);
    }

    /// <summary>
    /// WORLDRESTORE2 (H3): the save hook. The first accepted save after a restore marks its journal, so the next start
    /// removes the replaced world it kept until then. Once per process.
    /// </summary>
    internal static void NoteSave(bool accepted)
    {
        if (!accepted || _saveNoted || _root.Length == 0) return;
        _saveNoted = true;
        try
        {
            var env = new RestoreEnvironment { BepInExRoot = _root, Log = _log ?? (_ => { }) };
            WorldRestoreRecovery.NoteSaved(PhysicalWorldFs.Instance, env);
        }
        catch (Exception e) { _log?.Invoke("WorldRestore: the save could not be noted in the restore journal: " + e.Message); }
    }

    /// <summary>
    /// The <c>BeginCreateServerWorld</c> postfix (after <c>GameBootstrap.Start</c> resolved the save and
    /// <c>CheckIfResetServer</c> ran): one line with the time, the folder and the file the game chose.
    /// </summary>
    internal static void NoteBeginCreate(string? dir, string? saveToLoad, Action<string>? log)
    {
        if (_beginCreateLogged || log == null) return;
        _beginCreateLogged = true;
        var now = DateTime.UtcNow;
        string name = saveToLoad ?? "";
        string match = _newestAtLoad.Length == 0 || name.Length == 0
            ? "no comparison"
            : string.Equals(Path.GetFileName(name), _newestAtLoad, StringComparison.OrdinalIgnoreCase)
                ? "the same file Plugin.Load saw as newest"
                : "NOT the file Plugin.Load saw as newest (" + _newestAtLoad + ")";
        log("WorldRestore phase0: BeginCreateServerWorld at " + Stamp(now) + ", "
            + (_loadUtc == default ? "?" : ((long)(now - _loadUtc).TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms")
            + " after Plugin.Load: SaveDirectory " + (dir ?? "?") + ", SaveToLoad " + (name.Length > 0 ? name : "<none>") + " (" + match + ").");
    }

    private static string Stamp(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string SinceStart(DateTime utc)
    {
        try
        {
            var start = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            return ((long)(utc - start).TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms";
        }
        catch { return "? ms"; }
    }

    private static string SafeProcessPath()
    {
        try { return Process.GetCurrentProcess().MainModule?.FileName ?? ""; }
        catch { return ""; }
    }
}
