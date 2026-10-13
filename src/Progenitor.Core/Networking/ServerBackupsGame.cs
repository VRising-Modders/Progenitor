#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.WorldRestore;

namespace Progenitor.Networking;

/// <summary>
/// WORLDRESTORE1, server: the live values behind <see cref="ServerBackupsServer"/>: the running save folder, the BepInEx
/// folder, <c>[WorldRestore] KeepBackups</c>, the players online, and "Restore and restart now": a countdown broadcast at 60,
/// 30 and 10 seconds, then the graceful stop (<see cref="Progenitor.Install.ServerStopCommand"/>: save, wait for the file, quit).
/// Progenitor never starts the server again; the host's own restart (a service, a panel, a script) must.
/// </summary>
internal static class ServerBackupsGame
{
    internal const int CountdownSeconds = 60;
    private static readonly int[] Announce = { 60, 30, 10 };

    private static ConfigEntry<int>? _keep;
    private static ConfigEntry<bool>? _atStart;
    private static ConfigEntry<int>? _everyHours;
    private static ConfigEntry<int>? _keepCount;
    private static ConfigEntry<int>? _keepDays;

    /// <summary>BACKUPNOW1: seconds between the "every N hours" schedule's checks.</summary>
    internal const double ScheduleCheckSeconds = 60;
    private static int _generation;
    private static string _countdownId = "";
    private static DateTime _stopAtUtc;

    internal static void Install()
    {
        ServerBackupsServer.SaveFolderProbe = SaveFolder;
        ServerBackupsServer.BepInExRootProbe = () =>
        {
            try { return BepInEx.Paths.BepInExRootPath; }
            catch { return null; }
        };
        ServerBackupsServer.KeepProbe = () => _keep?.Value ?? WorldBackupCatalog.DefaultKeep;
        ServerBackupsServer.KeepWrite = n =>
        {
            if (_keep == null) return "the [WorldRestore] KeepBackups entry is not bound yet.";
            _keep.Value = n;
            return null;
        };
        ServerBackupsServer.OnlineProbe = () => Progenitor.Players.PlayerDirectory.Online().Count;
        ServerBackupsServer.ProcessPathProbe = () =>
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName; }
            catch { return null; }
        };
        ServerBackupsServer.ArgsProbe = () =>
        {
            try { return Environment.GetCommandLineArgs(); }
            catch { return Array.Empty<string>(); }
        };
        ServerBackupsServer.StartRestartCountdown = Start;
        ServerBackupsServer.CountdownProbe = Remaining;
        ServerBackupsServer.CancelCountdown = () => Stop("cancelled");
        SharedConfig.BindWhenAvailable("[WorldRestore] KeepBackups", BindFrom);

        // BACKUPNOW1: Back up now and the schedule.
        ServerBackupsServer.BackupSettingsProbe = () => new WorldBackupSettings
        {
            AtStart = _atStart?.Value ?? false,
            EveryHours = WorldBackupSettings.ClampHours(_everyHours?.Value ?? 0),
            KeepCount = BackupPolicy.ClampCount(_keepCount?.Value ?? BackupPolicy.DefaultCount),
            KeepDays = BackupPolicy.ClampDays(_keepDays?.Value ?? BackupPolicy.DefaultDays),
        };
        ServerBackupsServer.BackupSettingWrite = WriteSetting;
        ServerBackupsJob.SaveWorld = () => Progenitor.Persistence.WorldSaveTrigger.SaveCurrentWorld();
        ServerBackupsJob.SaveWatch = () =>
        {
            // Sampled before the trigger: "newer than it was" only means something if "was" predates the save.
            string? folder = Progenitor.Persistence.WorldSaveTrigger.SaveFolder();
            DateTime before = Progenitor.Persistence.WorldSaveTrigger.NewestSaveWriteUtc(folder);
            return () => Progenitor.Persistence.WorldSaveTrigger.SaveLanded(folder, before);
        };
        ServerBackupsJob.Schedule = (seconds, action) => Progenitor.Dev.MainThread.PostDelayed(seconds, action);
        ServerBackupsJob.RunOffThread = work =>
        {
            // File IO only: no game or IL2CPP object and no logger on this thread (its lines are queued for the main thread).
            var thread = new System.Threading.Thread(() => work()) { IsBackground = true, Name = "progenitor-world-backup" };
            thread.Start();
        };
        ServerBackupsJob.ProgenitorVersion = MyPluginInfoVersion();
        int gen = _generation;
        void Check()
        {
            if (gen != _generation) return;
            try
            {
                string? did = ServerBackupsJob.Tick(ServerBackupsServer.BackupSettings());
                if (did != null) Plugin.Logger?.LogInfo("WorldBackup: schedule: " + did);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("WorldBackup: the schedule check threw: " + e.Message); }
            Progenitor.Dev.MainThread.PostDelayed(ScheduleCheckSeconds, Check);
        }

        Progenitor.Dev.MainThread.PostDelayed(ScheduleCheckSeconds, Check);
    }

    private static string MyPluginInfoVersion()
    {
        try { return typeof(ServerBackupsGame).Assembly.GetName().Version?.ToString() ?? ""; }
        catch { return ""; }
    }

    private static string? WriteSetting(string key, string value)
    {
        switch (key)
        {
            case WorldBackupSettings.AtStartKey:
                if (_atStart == null) return "the [WorldRestore] BackupAtStart entry is not bound yet.";
                _atStart.Value = value == "true";
                return null;
            case WorldBackupSettings.EveryHoursKey:
                if (_everyHours == null) return "the [WorldRestore] BackupEveryHours entry is not bound yet.";
                _everyHours.Value = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                return null;
            case WorldBackupSettings.KeepCountKey:
                if (_keepCount == null) return "the [WorldRestore] BackupKeepCount entry is not bound yet.";
                _keepCount.Value = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                return null;
            case WorldBackupSettings.KeepDaysKey:
                if (_keepDays == null) return "the [WorldRestore] BackupKeepDays entry is not bound yet.";
                _keepDays.Value = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                return null;
            default:
                return "unknown backup setting " + key + ".";
        }
    }

    internal static void Uninstall()
    {
        _generation++;
        _countdownId = "";
        _keep = null;
        _atStart = null;
        _everyHours = null;
        _keepCount = null;
        _keepDays = null;
        // A backup copying now stops between files and removes its partial copy.
        ServerBackupsJob.Reset();
    }

    private static void BindFrom(ConfigFile file)
    {
        try
        {
            _keep = ConfigHelper.Bind(file, "WorldRestore", "KeepBackups", WorldBackupCatalog.DefaultKeep,
                "Server. How many Progenitor world backups (the verified copy every world restore makes first) are kept: after a "
                + "restore, older ones past this count are deleted. 0 keeps every one; at most 100. The game's own wipe archives "
                + "(<save>_<ticks>) and .BACKUP copies are never deleted. Admins can change it on the Server tab (Server rules > "
                + "Keep Progenitor backups).");
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("WorldRestore: binding [WorldRestore] KeepBackups threw: " + e.Message); }

        try
        {
            _atStart = ConfigHelper.Bind(file, WorldBackupSettings.Section, WorldBackupSettings.AtStartKey, false,
                "Server. Back up the whole world folder (saves, StartDate.json, the progenitor folder's mod data) at every server start, "
                + "before the game loads (Bootstrap, read from this file at the start). With a world restore waiting, the restore's own "
                + "backup is that copy. A failed backup is logged and never stops the server. Server rules > Back up at every start.");
            _everyHours = ConfigHelper.Bind(file, WorldBackupSettings.Section, WorldBackupSettings.EveryHoursKey, 0,
                "Server. Back up the world every this many hours while the server runs (the world is saved first, then copied and "
                + "checked off the main thread). 0 (the default) is off; at most " + WorldBackupSettings.MaxEveryHours + ". Server rules > Back up every.");
            _keepCount = ConfigHelper.Bind(file, WorldBackupSettings.Section, WorldBackupSettings.KeepCountKey, BackupPolicy.DefaultCount,
                "Server. Retention of manual and scheduled backups: keep the newest this many of each kind (0: no count limit; at most "
                + BackupPolicy.MaxCount + "). With BackupKeepDays also set, a backup must pass both. The newest backup of each kind, the "
                + "source of a waiting restore and the backups made before restores are never deleted by it.");
            _keepDays = ConfigHelper.Bind(file, WorldBackupSettings.Section, WorldBackupSettings.KeepDaysKey, BackupPolicy.DefaultDays,
                "Server. Retention of manual and scheduled backups: delete those older than this many days (0, the default: no age "
                + "limit; at most " + BackupPolicy.MaxDays + "). Applied after each backup.");
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("WorldBackup: binding the [WorldRestore] backup settings threw: " + e.Message); }
    }

    private static string? SaveFolder()
    {
        try
        {
            string? dir = Progenitor.Persistence.ModSave.CurrentSaveDirectory;
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
        catch { /* the host settings' folder below */ }

        try { return ServerRulesGame.ResolveHost(out _).SaveFolder; }
        catch { return null; }
    }

    private static int Remaining()
    {
        if (_countdownId.Length == 0) return -1;
        int left = (int)Math.Ceiling((_stopAtUtc - DateTime.UtcNow).TotalSeconds);
        return left < 0 ? 0 : left;
    }

    private static string? Start(string requestId)
    {
        if (Progenitor.Install.ServerStopCommand.SaveWorld == null) return "this server cannot save and stop itself (no world save path)";
        if (_countdownId.Length > 0) return "a countdown is already running";
        int gen = ++_generation;
        _countdownId = requestId;
        _stopAtUtc = DateTime.UtcNow.AddSeconds(CountdownSeconds);
        Plugin.Logger?.LogInfo("WorldRestore: restart now for request " + requestId + ": the server stops in " + CountdownSeconds + " s.");
        foreach (int at in Announce)
        {
            int say = at;
            Progenitor.Dev.MainThread.PostDelayed(CountdownSeconds - say, () =>
            {
                if (gen != _generation) return;
                Broadcast("The server restarts in " + say + " seconds to restore an earlier world.");
            });
        }

        Progenitor.Dev.MainThread.PostDelayed(CountdownSeconds, () =>
        {
            if (gen != _generation) return;
            string id = _countdownId;
            _countdownId = "";
            string? root = null;
            try { root = BepInEx.Paths.BepInExRootPath; } catch { /* checked below */ }
            var pending = root == null ? null : WorldRestoreMarker.ReadPending(PhysicalWorldFs.Instance, root, out _);
            if (pending == null || !string.Equals(pending.RequestId, id, StringComparison.Ordinal))
            {
                Plugin.Logger?.LogInfo("WorldRestore: the restore request changed during the countdown; the server keeps running.");
                Broadcast("The restart is cancelled.");
                // BACKUPSDLG1: an open Backups dialog stops showing a countdown.
                ServerBackupsServer.RaisePendingChanged("countdown ended");
                return;
            }

            Broadcast("The server is stopping now to restore an earlier world.");
            Progenitor.Install.ServerStopCommand.Execute(true, reply => Plugin.Logger?.LogInfo("WorldRestore: stop: " + reply));
        });
        return null;
    }

    private static void Stop(string why)
    {
        if (_countdownId.Length == 0) return;
        _generation++;
        _countdownId = "";
        Plugin.Logger?.LogInfo("WorldRestore: the restart countdown is " + why + ".");
        Broadcast("The restart is cancelled.");
    }

    private static void Broadcast(string text)
    {
        try { Progenitor.Chat.Chat.Broadcast(text); }
        catch (Exception e) { Plugin.Logger?.LogWarning("WorldRestore: broadcast threw: " + e.Message); }
    }
}
#endif
