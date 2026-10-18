using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.WorldState;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// LOCALSAVE1: while a synced key holds <c>SaveOnConfigSet</c> off on a cfg, a change to a non-synced
/// entry in that cfg is saved by ConfigSync after a debounce, with the locked keys still written as the
/// player's own pre-join values. Asserts on bytes read back from disk (the shim's <c>Save()</c> writes
/// a real file).
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class ConfigSyncLocalSaveTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigFile _file;
    private readonly List<(double Seconds, Action Run)> _posted = new();

    public ConfigSyncLocalSaveTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-configsync-localsave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = new ConfigFile(Path.Combine(_dir, "example.mod.cfg"), saveOnInit: true);
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        ConfigSync.ResetForTests();
        Handshake.ResetTables();
        Handshake.UninstallClientServerContextWatch();
        WorldEpoch.ResetForTests();
        ConfigSync.TestPostLocalSave = (seconds, run, _) => _posted.Add((seconds, run));
    }

    public void Dispose()
    {
        ConfigHelper.UninstallAll();
        ConfigSync.ResetForTests();
        Handshake.UninstallClientServerContextWatch();
        Handshake.ResetTables();
        WorldEpoch.ResetForTests();
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private string Disk() => File.ReadAllText(_file.ConfigFilePath);

    /// <summary>A synced Count (local 3, server 9) and a non-synced LocalOnly (1) in one cfg, joined.</summary>
    private (ConfigEntry<int> Count, ConfigEntry<int> Local) Join()
    {
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n", pluginVersion: "1.0.0");
        var local = _file.Bind("General", "LocalOnly", 1);
        _file.Save();
        Assert.Contains("Count = 3", Disk(), StringComparison.Ordinal);

        Assert.Equal(1, ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") },
            snapshot: true));
        Assert.Equal(9, count.Value);
        Assert.True(ConfigSync.IsSaveHeldOff(_file));
        Assert.False(_file.SaveOnConfigSet);
        return (count, local);
    }

    private static void AssertLocalOnDisk(string disk, int localOnly)
    {
        Assert.Contains("LocalOnly = " + localOnly, disk, StringComparison.Ordinal);
        Assert.Contains("Count = 3", disk, StringComparison.Ordinal);
        Assert.DoesNotContain("Count = 9", disk, StringComparison.Ordinal);
        Assert.DoesNotContain("Count = 11", disk, StringComparison.Ordinal);
        Assert.DoesNotContain("Count = 99", disk, StringComparison.Ordinal);
    }

    private static int SavedLines() =>
        ConfigSync.TestLogs.Count(l => l.StartsWith("ConfigSync: saved '", StringComparison.Ordinal));

    [Fact]
    public void A_local_change_while_joined_reaches_disk_after_the_debounce()
    {
        var (count, local) = Join();
        Assert.Empty(_posted);
        var beforeChange = Disk();

        local.Value = 7;
        Assert.Equal(beforeChange, Disk());
        Assert.Single(_posted);
        Assert.Equal(ConfigSync.LocalSaveDebounceSeconds, _posted[0].Seconds);
        Assert.Equal(1, ConfigSync.PendingLocalSaveCount);

        _posted[0].Run();
        AssertLocalOnDisk(Disk(), 7);
        Assert.Equal(0, ConfigSync.PendingLocalSaveCount);

        // The session is untouched: still locked to the server value, auto-save still held off.
        Assert.Equal(9, count.Value);
        Assert.True(ConfigSync.IsLocked(count));
        Assert.True(ConfigSync.IsSaveHeldOff(_file));
        Assert.False(_file.SaveOnConfigSet);

        // Logged once per file, not once per save.
        Assert.Equal(1, SavedLines());
        Assert.Contains(ConfigSync.TestLogs, l => l.Contains("(debounce)", StringComparison.Ordinal));
        local.Value = 8;
        _posted[^1].Run();
        AssertLocalOnDisk(Disk(), 8);
        Assert.Equal(1, SavedLines());
    }

    [Fact]
    public void Synced_values_pushed_by_the_server_never_reach_disk_on_this_path()
    {
        var (count, local) = Join();

        // Neither the join snapshot nor a later push schedules a save: sync's own writes are not local changes.
        Assert.Empty(_posted);
        Assert.Equal(1, ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "11") },
            snapshot: false));
        Assert.Equal(11, count.Value);
        Assert.Empty(_posted);

        // A refused write to the locked key is not a local change either.
        count.Value = 99;
        Assert.Equal(11, count.Value);
        Assert.Empty(_posted);

        local.Value = 7;
        Assert.Single(_posted);
        // A push landing between the change and its save does not leak into it.
        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") },
            snapshot: false);
        Assert.Single(_posted);

        _posted[0].Run();
        AssertLocalOnDisk(Disk(), 7);
        Assert.Equal(9, count.Value);
    }

    [Fact]
    public void Several_changes_inside_the_debounce_make_one_save()
    {
        var (_, local) = Join();
        local.Value = 4;
        local.Value = 5;
        local.Value = 6;
        Assert.Equal(3, _posted.Count);

        int saves = _file.SaveCount;
        _posted[0].Run();
        _posted[1].Run();
        Assert.Equal(saves, _file.SaveCount);

        _posted[2].Run();
        Assert.Equal(saves + 1, _file.SaveCount);
        AssertLocalOnDisk(Disk(), 6);

        // A stale or repeated post after the save writes nothing.
        _posted[2].Run();
        _posted[0].Run();
        Assert.Equal(saves + 1, _file.SaveCount);
    }

    [Fact]
    public void A_pending_save_is_flushed_on_disconnect()
    {
        var (count, local) = Join();
        local.Value = 7;
        Assert.Equal(1, ConfigSync.PendingLocalSaveCount);

        Assert.Equal(1, ConfigSync.RestoreLocal());
        Assert.Equal(0, ConfigSync.PendingLocalSaveCount);
        Assert.Equal(3, count.Value);
        AssertLocalOnDisk(Disk(), 7);
        Assert.Contains(ConfigSync.TestLogs, l => l.Contains("(disconnect)", StringComparison.Ordinal));

        int saves = _file.SaveCount;
        _posted[0].Run();
        Assert.Equal(saves, _file.SaveCount);
    }

    [Fact]
    public void A_pending_save_is_flushed_when_the_plugin_unloads_while_joined()
    {
        var countEntry = _file.Bind("General", "Count", 3);
        var handle = ConfigSync.Register("example.mod", "1.0.0", countEntry);
        var local = _file.Bind("General", "LocalOnly", 1);
        _file.Save();
        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") },
            snapshot: true);
        Assert.Equal(9, countEntry.Value);

        local.Value = 7;
        Assert.Equal(1, ConfigSync.PendingLocalSaveCount);

        // A hot reload unregisters the synced row; the session lock stays, and the local change is saved now.
        handle.Dispose();
        Assert.True(ConfigSync.IsLocked("example.mod", "General", "Count"));
        Assert.Equal(0, ConfigSync.PendingLocalSaveCount);
        AssertLocalOnDisk(Disk(), 7);
        Assert.Contains(ConfigSync.TestLogs, l => l.Contains("(plugin unload)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pending_save_is_flushed_on_Core_unload_and_on_process_exit()
    {
        var (count, local) = Join();
        local.Value = 7;
        Assert.Equal(1, ConfigSync.FlushLocalSaves("process exit"));
        AssertLocalOnDisk(Disk(), 7);
        Assert.Equal(9, count.Value);
        Assert.Contains(ConfigSync.TestLogs, l => l.Contains("(process exit)", StringComparison.Ordinal));
        Assert.Equal(0, ConfigSync.FlushLocalSaves("process exit"));

        local.Value = 8;
        Assert.Equal(1, ConfigSync.PendingLocalSaveCount);
        ConfigSync.Uninstall();
        Assert.Equal(0, ConfigSync.PendingLocalSaveCount);
        AssertLocalOnDisk(Disk(), 8);
    }

    [Fact]
    public void An_explicit_Save_during_the_hold_still_writes_the_parked_local()
    {
        var (count, local) = Join();
        local.Value = 7;
        _file.Save();
        AssertLocalOnDisk(Disk(), 7);
        Assert.Equal(9, count.Value);

        // The page path (TrySaveHeldFile) also carries the pending change, so the debounce has nothing left.
        local.Value = 8;
        Assert.True(ConfigSync.TrySaveHeldFile(_file, out _));
        Assert.Equal(0, ConfigSync.PendingLocalSaveCount);
        AssertLocalOnDisk(Disk(), 8);
    }

    [Fact]
    public void A_file_whose_mod_turned_auto_save_off_is_left_to_the_mod()
    {
        _file.SaveOnConfigSet = false;
        var (_, local) = Join();
        local.Value = 7;
        Assert.Empty(_posted);
        Assert.Equal(0, ConfigSync.PendingLocalSaveCount);
    }

    [Fact]
    public void Nothing_is_scheduled_on_a_file_no_lock_holds()
    {
        ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n", pluginVersion: "1.0.0");
        var local = _file.Bind("General", "LocalOnly", 1);
        Assert.True(_file.SaveOnConfigSet);
        local.Value = 7;
        Assert.Empty(_posted);
        Assert.Contains("LocalOnly = 7", Disk(), StringComparison.Ordinal);
    }
}
