using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.WorldState;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of admin-synced config (roadmap M2.5): Toml round trip, join lock,
/// disconnect restore, server push, ignore-unknown, non-BCL refuse.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class ConfigSyncTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigFile _file;

    public ConfigSyncTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-configsync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = new ConfigFile(Path.Combine(_dir, "example.mod.cfg"), saveOnInit: true);
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        ConfigSync.ResetForTests();
        Handshake.ResetTables();
        Handshake.UninstallClientServerContextWatch();
        WorldEpoch.ResetForTests();
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

    public enum NotBclEnum
    {
        Alpha = 0,
        Beta = 1,
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Toml_round_trips_bool(bool value)
    {
        var text = ConfigSyncToml.Format(value, typeof(bool));
        Assert.True(ConfigSyncToml.TryParse(text, typeof(bool), out var parsed));
        Assert.Equal(value, parsed);
        Assert.Equal(value ? "True" : "False", text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(-7)]
    public void Toml_round_trips_int(int value)
    {
        var text = ConfigSyncToml.Format(value, typeof(int));
        Assert.True(ConfigSyncToml.TryParse(text, typeof(int), out var parsed));
        Assert.Equal(value, parsed);
    }

    [Fact]
    public void Toml_round_trips_float()
    {
        const float value = 1.25f;
        var text = ConfigSyncToml.Format(value, typeof(float));
        Assert.True(ConfigSyncToml.TryParse(text, typeof(float), out var parsed));
        Assert.Equal(value, (float)parsed!);
        Assert.True(ConfigSyncToml.TryParse("-0.5", typeof(float), out var neg));
        Assert.Equal(-0.5f, (float)neg!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("a path with spaces")]
    public void Toml_round_trips_string(string value)
    {
        var text = ConfigSyncToml.Format(value, typeof(string));
        Assert.True(ConfigSyncToml.TryParse(text, typeof(string), out var parsed));
        Assert.Equal(value, parsed);
    }

    [Fact]
    public void Toml_round_trips_bcl_enum()
    {
        var text = ConfigSyncToml.Format(DayOfWeek.Wednesday, typeof(DayOfWeek));
        Assert.Equal("Wednesday", text);
        Assert.True(ConfigSyncToml.TryParse("wednesday", typeof(DayOfWeek), out var parsed));
        Assert.Equal(DayOfWeek.Wednesday, parsed);
        Assert.False(ConfigSyncToml.TryParse("not-a-day", typeof(DayOfWeek), out _));
    }

    [Fact]
    public void Wire_round_trips_a_snapshot_packet()
    {
        var records = new[]
        {
            new ConfigSyncRecord("example.mod", "General", "Enabled", "True", ConfigSync.TypeToken(typeof(bool))),
            new ConfigSyncRecord("example.mod", "Combat", "Damage", "1.5", ConfigSync.TypeToken(typeof(float))),
        };
        var packets = ConfigSyncWire.Pack(records, snapshot: true);
        Assert.Single(packets);
        Assert.True(ConfigSyncWire.TryRead(packets[0], out var snapshot, out var more, out var read, out var error));
        Assert.Equal("", error ?? "");
        Assert.True(snapshot);
        Assert.False(more);
        Assert.Equal(2, read.Count);
        Assert.Equal("example.mod", read[0].PluginId);
        Assert.Equal("General", read[0].Section);
        Assert.Equal("Enabled", read[0].Key);
        Assert.Equal("True", read[0].Toml);
        Assert.Equal("1.5", read[1].Toml);
        Assert.Equal(ConfigSync.TypeToken(typeof(bool)), read[0].TypeName);
        Assert.Equal(ConfigSync.TypeToken(typeof(float)), read[1].TypeName);
    }

    [Fact]
    public void Bind_refuses_a_non_BCL_type()
    {
        var typeEx = Assert.Throws<ArgumentException>(() =>
            ConfigSync.Bind(_file, "example.mod", "General", "Mode", NotBclEnum.Alpha, "plugin enum"));
        Assert.Contains("NotBclEnum", typeEx.Message, StringComparison.Ordinal);
        Assert.Contains("BCL", typeEx.Message, StringComparison.Ordinal);

        var classEx = Assert.Throws<ArgumentException>(() =>
            ConfigSync.ThrowIfNotSyncable(typeof(ConfigSyncTests)));
        Assert.Contains("ConfigSyncTests", classEx.Message, StringComparison.Ordinal);
        Assert.Empty(ConfigSync.SnapshotFor(Array.Empty<Handshake.ModRecord>()));
    }

    [Fact]
    public void Bind_allows_BCL_primitives_string_and_BCL_enum()
    {
        ConfigSync.Bind(_file, "example.mod", "General", "Enabled", true, "on");
        ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        ConfigSync.Bind(_file, "example.mod", "General", "Scale", 1.5f, "f");
        ConfigSync.Bind(_file, "example.mod", "General", "Title", "hi", "s");
        ConfigSync.Bind(_file, "example.mod", "General", "Day", DayOfWeek.Monday, "e");
        Assert.Equal(5, ConfigSync.RegisteredCount);
    }

    [Fact]
    public void Join_applies_server_values_locks_and_disconnect_restores_local()
    {
        var enabled = ConfigSync.Bind(_file, "example.mod", "General", "Enabled", true, "on");
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        enabled.Value = true;
        count.Value = 3;

        var snapshot = new[]
        {
            new ConfigSyncRecord("example.mod", "General", "Enabled", "False"),
            new ConfigSyncRecord("example.mod", "General", "Count", "9"),
        };
        Assert.Equal(2, ConfigSync.ApplyRecords(snapshot, snapshot: true));
        Assert.False(enabled.Value);
        Assert.Equal(9, count.Value);
        Assert.True(ConfigSync.IsLocked(enabled));
        Assert.True(ConfigSync.IsLocked("example.mod", "General", "Count"));
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains("applied 2 synced setting(s); locked until disconnect", StringComparison.Ordinal));

        Assert.True(ConfigSync.IsSaveHeldOff(_file));
        Assert.False(_file.SaveOnConfigSet);
        int savesWhileLocked = _file.SaveCount;
        count.Value = 99;
        Assert.Equal(9, count.Value);
        Assert.Equal(savesWhileLocked, _file.SaveCount);
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains("refused write to example.mod/General/Count while locked to the server value", StringComparison.Ordinal));

        int savesBeforeRestore = _file.SaveCount;
        Assert.Equal(2, ConfigSync.RestoreLocal());
        Assert.True(enabled.Value);
        Assert.Equal(3, count.Value);
        Assert.False(ConfigSync.IsLocked(enabled));
        Assert.False(ConfigSync.IsSaveHeldOff(_file));
        Assert.True(_file.SaveOnConfigSet);
        Assert.True(_file.SaveCount > savesBeforeRestore);
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains("restored 2 local setting(s) after disconnect", StringComparison.Ordinal));

        count.Value = 99;
        Assert.Equal(99, count.Value);
    }

    [Fact]
    public void Core_only_refresh_Created_does_not_unlock_synced_settings()
    {
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        count.Value = 3;
        Assert.Equal(1, ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") }, snapshot: true));
        Assert.True(ConfigSync.IsLocked(count));

        Handshake.InstallClientServerContextWatch();
        try
        {
            WorldEpoch.Advance(ClientWorldEpoch.CreatedSource);
            Assert.True(ConfigSync.IsLocked(count));
            Assert.Equal(9, count.Value);
        }
        finally
        {
            Handshake.UninstallClientServerContextWatch();
            WorldEpoch.ResetForTests();
        }
    }

    [Fact]
    public void Stale_packet_after_clear_does_not_reapply_or_restore()
    {
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        count.Value = 3;
        Handshake.InstallClientServerContextWatch();
        try
        {
            Assert.Equal(1, ConfigSync.ApplyRecords(
                new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") }, snapshot: true));
            Assert.True(ConfigSync.IsLocked(count));

            Handshake.NotifyClientConnectionEnded();
            Assert.False(ConfigSync.IsLocked(count));
            Assert.Equal(3, count.Value);

            Assert.False(ConfigSync.ApplyIncomingServerPacket(
                Handshake.IncomingPacketEpoch,
                new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") }));
            Assert.False(ConfigSync.IsLocked(count));
            Assert.Equal(3, count.Value);
        }
        finally
        {
            Handshake.UninstallClientServerContextWatch();
            WorldEpoch.ResetForTests();
        }
    }

    [Fact]
    public void World_epoch_change_unlocks_synced_settings_and_does_not_restore_twice()
    {
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        count.Value = 3;
        Assert.Equal(1, ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") }, snapshot: true));
        Assert.True(ConfigSync.IsLocked(count));
        Assert.Equal(9, count.Value);

        Handshake.InstallClientServerContextWatch();
        try
        {
            int epoch = WorldEpoch.Advance(ClientWorldEpoch.EndedSource);
            Assert.False(ConfigSync.IsLocked(count));
            Assert.Equal(3, count.Value);
            Assert.Contains(
                ConfigSync.TestLogs,
                line => line.Contains("restored 1 local setting(s) after disconnect", StringComparison.Ordinal));

            int logs = ConfigSync.TestLogs.Count;
            Assert.False(Handshake.InvalidateClientServerContext("disconnect", epoch));
            Assert.Equal(logs, ConfigSync.TestLogs.Count);
            Assert.Equal(0, ConfigSync.RestoreLocal());
        }
        finally
        {
            Handshake.UninstallClientServerContextWatch();
            WorldEpoch.ResetForTests();
        }
    }

    [Fact]
    public void SnapshotFor_only_includes_plugins_the_client_has()
    {
        ConfigSync.Bind(_file, "example.mod", "General", "Enabled", true, "on", pluginVersion: "1.0.0");
        var other = new ConfigFile(Path.Combine(_dir, "other.mod.cfg"), saveOnInit: true);
        ConfigSync.Bind(other, "other.mod", "General", "Enabled", false, "on", pluginVersion: "1.0.0");

        var none = ConfigSync.SnapshotFor(Array.Empty<Handshake.ModRecord>());
        Assert.Empty(none);

        var exampleRec = Handshake.ModRecord.Undeclared("example.mod", "1.0.0");
        var otherRec = Handshake.ModRecord.Undeclared("other.mod", "1.0.0");
        var justExample = ConfigSync.SnapshotFor(new[] { exampleRec }, new[] { exampleRec });
        Assert.Single(justExample);
        Assert.Equal("example.mod", justExample[0].PluginId);
        Assert.Equal(ConfigSync.TypeToken(typeof(bool)), justExample[0].TypeName);

        var both = ConfigSync.SnapshotFor(new[] { exampleRec, otherRec }, new[] { exampleRec, otherRec });
        Assert.Equal(2, both.Count);
    }

    [Fact]
    public void Server_change_is_pushed()
    {
        ConfigSync.TestIsServer = true;
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        ConfigSync.TestPushes.Clear();
        count.Value = 12;
        Assert.Single(ConfigSync.TestPushes);
        Assert.Equal("example.mod", ConfigSync.TestPushes[0].PluginId);
        Assert.Equal("General", ConfigSync.TestPushes[0].Section);
        Assert.Equal("Count", ConfigSync.TestPushes[0].Key);
        Assert.Equal("12", ConfigSync.TestPushes[0].Toml);
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains("pushed example.mod/General/Count", StringComparison.Ordinal));
    }

    [Fact]
    public void Push_while_locked_updates_the_server_value_and_keeps_the_cached_local()
    {
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") },
            snapshot: true);
        Assert.Equal(9, count.Value);

        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "15") },
            snapshot: false);
        Assert.Equal(15, count.Value);
        Assert.True(ConfigSync.IsLocked(count));

        ConfigSync.RestoreLocal();
        Assert.Equal(3, count.Value);
    }

    [Fact]
    public void Unknown_plugin_or_entry_is_ignored_with_one_log_line()
    {
        ConfigSync.Bind(_file, "example.mod", "General", "Enabled", true, "on");
        int applied = ConfigSync.ApplyRecords(
            new[]
            {
                new ConfigSyncRecord("missing.mod", "General", "Enabled", "False"),
                new ConfigSyncRecord("example.mod", "General", "NoSuchKey", "1"),
            },
            snapshot: true);
        Assert.Equal(0, applied);
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains("ignored missing.mod/General/Enabled: no such plugin or entry", StringComparison.Ordinal));
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains("ignored example.mod/General/NoSuchKey: no such plugin or entry", StringComparison.Ordinal));

        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("missing.mod", "General", "Enabled", "False") },
            snapshot: true);
        int ignored = 0;
        foreach (var line in ConfigSync.TestLogs)
        {
            if (line.Contains("ignored missing.mod/General/Enabled", StringComparison.Ordinal))
                ignored++;
        }
        Assert.Equal(1, ignored);
    }

    [Fact]
    public void Register_throws_when_Config_is_in_SkipCoreModules()
    {
        HarmonySlotHandoff.SetSkipModules("Config", refusedLiveWorld: false);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigSync.Bind(_file, "example.mod", "General", "Enabled", true, "skip"));
        Assert.Contains("Config", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SkipCoreModules", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Locked_write_is_reverted_and_does_not_flush_server_values_to_disk()
    {
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        var local = _file.Bind("General", "LocalOnly", 1);
        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") },
            snapshot: true);
        Assert.True(ConfigSync.IsSaveHeldOff(_file));
        Assert.False(_file.SaveOnConfigSet);

        int saves = _file.SaveCount;
        count.Value = 99;
        local.Value = 7;
        Assert.Equal(9, count.Value);
        Assert.Equal(7, local.Value);
        Assert.Equal(saves, _file.SaveCount);
        Assert.False(_file.SaveOnConfigSet);

        ConfigSync.RestoreLocal();
        Assert.Equal(3, count.Value);
        Assert.True(_file.SaveOnConfigSet);
        Assert.Contains("Count = 3", _file.LastSavedText ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("Count = 9", _file.LastSavedText ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_apply_does_not_force_SaveOnConfigSet_true_while_locked()
    {
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") },
            snapshot: true);
        Assert.False(_file.SaveOnConfigSet);
        count.Value = 1;
        Assert.Equal(9, count.Value);
        Assert.False(_file.SaveOnConfigSet);
        Assert.True(ConfigSync.IsSaveHeldOff(_file));
    }

    [Fact]
    public void SnapshotFor_answered_skips_a_version_mismatch_unless_Minor()
    {
        ConfigSync.Bind(_file, "example.mod", "General", "Enabled", true, "on", pluginVersion: "1.2.0");
        var serverExact = new[]
        {
            Handshake.ModRecord.CreateDeclared(
                "example.mod", "1.2.0",
                NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        var mismatch = ConfigSync.SnapshotFor(
            new[]
            {
                Handshake.ModRecord.CreateDeclared(
                    "example.mod", "1.3.0",
                    NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
            },
            serverExact);
        Assert.Empty(mismatch);
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains(
                "ignored example.mod synced settings: client version '1.3.0' does not match server '1.2.0' (Exact)",
                StringComparison.Ordinal));

        var serverMinor = new[]
        {
            Handshake.ModRecord.CreateDeclared(
                "example.mod", "1.2.0",
                NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor),
        };
        var minorOk = ConfigSync.SnapshotFor(
            new[]
            {
                Handshake.ModRecord.CreateDeclared(
                    "example.mod", "1.2.9",
                    NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor),
            },
            serverMinor);
        Assert.Single(minorOk);

        var minorBad = ConfigSync.SnapshotFor(
            new[]
            {
                Handshake.ModRecord.CreateDeclared(
                    "example.mod", "1.3.0",
                    NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor),
            },
            serverMinor);
        Assert.Empty(minorBad);

        var serverNone = new[]
        {
            Handshake.ModRecord.CreateDeclared(
                "example.mod", "1.2.0",
                NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None),
        };
        var noneSnap = ConfigSync.SnapshotFor(
            new[]
            {
                Handshake.ModRecord.CreateDeclared(
                    "example.mod", "1.2.9",
                    NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None),
            },
            serverNone);
        Assert.Single(noneSnap);
        Assert.Equal("example.mod", noneSnap[0].PluginId);
        Assert.DoesNotContain(
            ConfigSync.TestLogs,
            line => line.Contains("snapshot skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void SnapshotFor_answered_includes_matching_version()
    {
        ConfigSync.Bind(_file, "example.mod", "General", "Enabled", true, "on", pluginVersion: "1.0.0");
        var snap = ConfigSync.SnapshotFor(
            new[] { Handshake.ModRecord.Undeclared("example.mod", "1.0.0") },
            new[] { Handshake.ModRecord.Undeclared("example.mod", "1.0.0") });
        Assert.Single(snap);
        Assert.Equal("example.mod", snap[0].PluginId);
    }

    [Fact]
    public void Pack_logs_and_fails_closed_for_an_oversized_record()
    {
        var big = new string('x', ConfigSyncWire.MaxFieldBytes);
        var huge = new ConfigSyncRecord(big, big, big, big);
        var ok = new ConfigSyncRecord("example.mod", "General", "Enabled", "True");
        var packets = ConfigSync.PackAndLog(new[] { huge, ok }, snapshot: true);
        Assert.Single(packets);
        Assert.True(ConfigSyncWire.TryRead(packets[0], out _, out _, out var read, out _));
        Assert.Single(read);
        Assert.Equal("Enabled", read[0].Key);
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains("ConfigSync: skipped ", StringComparison.Ordinal)
                && line.Contains("record is ", StringComparison.Ordinal));
    }

    [Fact]
    public void Register_refuses_a_record_that_cannot_fit_a_packet()
    {
        var big = new string('x', ConfigSyncWire.MaxFieldBytes);
        var file = new ConfigFile(Path.Combine(_dir, "huge.mod.cfg"), saveOnInit: true);
        var ex = Assert.Throws<ArgumentException>(() =>
            ConfigSync.Bind(file, big, big, big, big, "too big"));
        Assert.Contains("cannot fit in a packet", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, ConfigSync.RegisteredCount);
    }

    [Fact]
    public void Later_push_uses_the_handshake_rearm_user_set()
    {
        Assert.True(ConfigSync.PushIncludesDisabledUsers);
        Assert.True(ConfigSync.ShouldPushToUser(isConnected: true, handshakeOk: true));
        Assert.False(ConfigSync.ShouldPushToUser(isConnected: true, handshakeOk: false));
        Assert.False(ConfigSync.ShouldPushToUser(isConnected: false, handshakeOk: true));
    }

    [Fact]
    public void Register_dispose_unregisters_so_tracked_bind_does_not_leak()
    {
        var entry = _file.Bind("General", "Tracked", 1);
        var handle = ConfigSync.Register("example.mod", "1.0.0", entry);
        Assert.Equal(1, ConfigSync.RegisteredCount);
        handle.Dispose();
        Assert.Equal(0, ConfigSync.RegisteredCount);
    }

    [Fact]
    public void PluginConfig_synced_bind_throws_untracked()
    {
        var ex = Assert.Throws<InvalidOperationException>(ConfigSync.ThrowIfUntrackedSyncedBind);
        Assert.Equal(ConfigSync.UntrackedSyncedBindMessage, ex.Message);
        Assert.Contains("ctx.Bind", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_project_does_not_reference_live_BepInEx_ConfigFile()
    {
        // CI ForbidGameReferences: this shim's Save is Entries then OrphanedEntries
        // (same order as live BepInEx). Parking the local as an orphan is what makes
        // LastSavedText hold the pre-join value; a failed park must fail the Save tests.
        Assert.Null(Type.GetType("BepInEx.Configuration.ConfigFile, BepInEx.Core"));
    }

    /// <summary>
    /// Threat model surface 3, "verified-not-assumed": <c>docs/config-sync.md</c> claimed the
    /// server's values never reach the player's <c>GUID.cfg</c> and marked the check manual and
    /// not yet done. This drives a synced entry through the real client apply path and asserts
    /// against the bytes on disk, not against <c>LastSavedText</c>: after the join the overlay
    /// holds the server value, a set of the locked key and of an unrelated non-synced key does
    /// not write, an explicit <c>Save()</c> writes the pre-join local, and after disconnect the
    /// file holds the restored local. A hostile server therefore cannot poison the file.
    /// </summary>
    [Fact]
    public void A_synced_value_never_reaches_the_cfg_on_disk()
    {
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        var localOnly = _file.Bind("General", "LocalOnly", 1);
        _file.Save();
        var beforeJoin = File.ReadAllText(_file.ConfigFilePath);
        Assert.Contains("Count = 3", beforeJoin, StringComparison.Ordinal);

        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") },
            snapshot: true);

        // The overlay is what the server sent; the file has not been touched.
        Assert.Equal(9, count.Value);
        Assert.True(ConfigSync.IsLocked(count));
        Assert.Equal(beforeJoin, File.ReadAllText(_file.ConfigFilePath));

        // Writes while locked: the synced key is reverted, the non-synced key takes but must not
        // auto-save (SaveOnConfigSet is held off while any synced slot on this file is locked).
        count.Value = 99;
        localOnly.Value = 7;
        Assert.Equal(9, count.Value);
        Assert.Equal(7, localOnly.Value);
        Assert.Equal(beforeJoin, File.ReadAllText(_file.ConfigFilePath));

        // The dangerous case: something calls Save() explicitly during the session.
        _file.Save();
        var duringJoin = File.ReadAllText(_file.ConfigFilePath);
        Assert.Contains("Count = 3", duringJoin, StringComparison.Ordinal);
        Assert.DoesNotContain("Count = 9", duringJoin, StringComparison.Ordinal);
        Assert.DoesNotContain("Count = 99", duringJoin, StringComparison.Ordinal);
        Assert.Contains("LocalOnly = 7", duringJoin, StringComparison.Ordinal);
        Assert.Equal(9, count.Value);

        // Disconnect: locals restored, saved, and the file still never held a server value.
        Assert.Equal(1, ConfigSync.RestoreLocal());
        Assert.Equal(3, count.Value);
        var afterDisconnect = File.ReadAllText(_file.ConfigFilePath);
        Assert.Contains("Count = 3", afterDisconnect, StringComparison.Ordinal);
        Assert.DoesNotContain("Count = 9", afterDisconnect, StringComparison.Ordinal);
        Assert.True(_file.SaveOnConfigSet);
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains(
                "refused write to example.mod/General/Count while locked to the server value",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Unregister_while_locked_keeps_the_server_value_and_rebind_reattaches()
    {
        var count = _file.Bind("General", "Count", 3);
        var handle = ConfigSync.Register("example.mod", "1.0.0", count);
        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") },
            snapshot: true);
        Assert.Equal(9, count.Value);
        Assert.True(ConfigSync.IsLocked(count));

        handle.Dispose();
        Assert.Equal(9, count.Value);
        Assert.True(ConfigSync.IsLocked("example.mod", "General", "Count"));
        Assert.Equal(0, ConfigSync.RegisteredCount);

        var rebound = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n", pluginVersion: "1.0.0");
        Assert.Equal(9, rebound.Value);
        Assert.True(ConfigSync.IsLocked(rebound));
        Assert.Equal(1, ConfigSync.RegisteredCount);
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains(
                "re-attached example.mod/General/Count to the locked server value after plugin reload",
                StringComparison.Ordinal));

        Assert.Equal(1, ConfigSync.RestoreLocal());
        Assert.Equal(3, rebound.Value);
        Assert.False(ConfigSync.IsLocked(rebound));
    }

    [Fact]
    public void Core_collection_restore_releases_unregistered_session_and_file_save_hold()
    {
        var count = _file.Bind("General", "Count", 3);
        _file.SaveOnConfigSet = true;
        var registration = ConfigSync.Register("example.mod", "1.0.0", count);
        ConfigSync.ApplyRecords(new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") }, snapshot: true);
        registration.Dispose();
        Assert.True(ConfigSync.IsLocked("example.mod", "General", "Count"));
        Assert.False(_file.SaveOnConfigSet);
        ConfigSync.RestoreLocal(finalAttempt: true);
        Assert.False(ConfigSync.IsLocked("example.mod", "General", "Count"));
        Assert.True(_file.SaveOnConfigSet);
        Assert.Equal(3, count.Value);
        Assert.Equal(0, ConfigSync.RegisteredCount);
    }

    [Fact]
    public void Explicit_Save_while_locked_writes_the_local_not_the_server_value()
    {
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        var local = _file.Bind("General", "LocalOnly", 1);
        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") },
            snapshot: true);
        Assert.Equal(9, count.Value);

        int saves = _file.SaveCount;
        _file.Save();
        Assert.True(_file.SaveCount > saves);
        Assert.Contains("Count = 3", _file.LastSavedText ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("Count = 9", _file.LastSavedText ?? "", StringComparison.Ordinal);
        Assert.Contains("LocalOnly = 1", _file.LastSavedText ?? "", StringComparison.Ordinal);
        Assert.Equal(9, count.Value);
        Assert.True(ConfigSync.IsLocked(count));

        ConfigSync.RestoreLocal();
        Assert.Equal(3, count.Value);
        Assert.Equal(1, local.Value);
    }

    [Fact]
    public void Minor_apply_skips_a_type_mismatch_with_a_schema_drift_line()
    {
        ConfigSync.TestApplyRule = NetworkVersionRule.Minor;
        var title = ConfigSync.Bind(_file, "example.mod", "General", "Count", "3", "s", pluginVersion: "1.2.9");
        int applied = ConfigSync.ApplyRecords(
            new[]
            {
                new ConfigSyncRecord(
                    "example.mod", "General", "Count", "9",
                    ConfigSync.TypeToken(typeof(int))),
            },
            snapshot: true);
        Assert.Equal(NetworkVersionRule.Minor, ConfigSync.TestApplyRule);
        Assert.Equal(NetworkVersionRule.Minor, ConfigSync.RuleFor("example.mod"));
        Assert.Equal(0, applied);
        Assert.Equal("3", title.Value);
        // C3: the server's value is skipped and the entry is locked to its declared default (here also "3") for the session.
        Assert.True(ConfigSync.IsLocked(title));
        Assert.Contains(ConfigSync.TestLogs, line => line.EndsWith("; using the default 3", StringComparison.Ordinal));
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains(
                "schema-drift example.mod/General/Count: client type '"
                + ConfigSync.TypeToken(typeof(string))
                + "' does not match server '"
                + ConfigSync.TypeToken(typeof(int))
                + "'",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Exact_apply_skips_a_type_mismatch_with_a_schema_drift_line()
    {
        ConfigSync.TestApplyRule = NetworkVersionRule.Exact;
        var title = ConfigSync.Bind(_file, "example.mod", "General", "Count", "3", "s", pluginVersion: "1.2.9");
        int applied = ConfigSync.ApplyRecords(
            new[]
            {
                new ConfigSyncRecord(
                    "example.mod", "General", "Count", "9",
                    ConfigSync.TypeToken(typeof(int))),
            },
            snapshot: true);
        Assert.Equal(NetworkVersionRule.Exact, ConfigSync.RuleFor("example.mod"));
        Assert.Equal(0, applied);
        Assert.Equal("3", title.Value);
        // C3: the server's value is skipped and the entry is locked to its declared default (here also "3") for the session.
        Assert.True(ConfigSync.IsLocked(title));
        Assert.Contains(ConfigSync.TestLogs, line => line.EndsWith("; using the default 3", StringComparison.Ordinal));
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains(
                "schema-drift example.mod/General/Count: client type '"
                + ConfigSync.TypeToken(typeof(string))
                + "' does not match server '"
                + ConfigSync.TypeToken(typeof(int))
                + "'",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            ConfigSync.TestLogs,
            line => line.Contains("type gate refused under Minor", StringComparison.Ordinal));
    }

    [Fact]
    public void Minor_apply_accepts_the_same_type()
    {
        ConfigSync.TestApplyRule = NetworkVersionRule.Minor;
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n", pluginVersion: "1.2.9");
        int applied = ConfigSync.ApplyRecords(
            new[]
            {
                new ConfigSyncRecord(
                    "example.mod", "General", "Count", "9",
                    ConfigSync.TypeToken(typeof(int))),
            },
            snapshot: true);
        Assert.Equal(1, applied);
        Assert.Equal(9, count.Value);
        Assert.True(ConfigSync.IsLocked(count));
    }

    [Fact]
    public void ShouldPushOnRegister_requires_server_and_a_handshake_ok_user()
    {
        Assert.False(ConfigSync.ShouldPushOnRegister(isServer: false, handshakeOkUserCount: 1));
        Assert.False(ConfigSync.ShouldPushOnRegister(isServer: true, handshakeOkUserCount: 0));
        Assert.True(ConfigSync.ShouldPushOnRegister(isServer: true, handshakeOkUserCount: 1));
        Assert.True(ConfigSync.ShouldPushOnRegister(isServer: true, handshakeOkUserCount: 3));
    }

    [Fact]
    public void Server_reregister_does_not_push_before_anyone_has_matched()
    {
        ConfigSync.TestIsServer = true;
        ConfigSync.TestHandshakeOkUserCount = 0;
        Assert.False(ConfigSync.ShouldPushOnRegister(ConfigSync.TestIsServer, ConfigSync.TestHandshakeOkUserCount));
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        count.Value = 12;
        ConfigSync.TestPushes.Clear();
        ConfigSync.Register("example.mod", "1.0.0", count);
        Assert.Empty(ConfigSync.TestPushes);
    }

    [Fact]
    public void Server_reregister_pushes_the_live_value()
    {
        ConfigSync.TestIsServer = true;
        ConfigSync.TestHandshakeOkUserCount = 1;
        Assert.True(ConfigSync.ShouldPushOnRegister(ConfigSync.TestIsServer, ConfigSync.TestHandshakeOkUserCount));
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        count.Value = 12;
        ConfigSync.TestPushes.Clear();
        var handle = ConfigSync.Register("example.mod", "1.0.0", count);
        handle.Dispose();
        ConfigSync.TestPushes.Clear();
        ConfigSync.Register("example.mod", "1.0.0", count);
        Assert.Single(ConfigSync.TestPushes);
        Assert.Equal("12", ConfigSync.TestPushes[0].Toml);
    }

    [Fact]
    public void Server_push_while_unbound_is_applied_on_reattach()
    {
        var count = _file.Bind("General", "Count", 3);
        var handle = ConfigSync.Register("example.mod", "1.0.0", count);
        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") },
            snapshot: true);
        Assert.Equal(9, count.Value);
        int saves = _file.SaveCount;

        handle.Dispose();
        Assert.Equal(saves, _file.SaveCount);
        Assert.True(ConfigSync.IsLocked("example.mod", "General", "Count"));

        ConfigSync.TestLogs.Clear();
        int applied = ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "15") },
            snapshot: false);
        Assert.Equal(1, applied);
        Assert.Equal(15, count.Value);
        Assert.DoesNotContain(
            ConfigSync.TestLogs,
            line => line.Contains("ignored example.mod/General/Count", StringComparison.Ordinal));
        Assert.Equal(saves, _file.SaveCount);

        var rebound = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n", pluginVersion: "1.0.0");
        Assert.Equal(15, rebound.Value);
        Assert.True(ConfigSync.IsLocked(rebound));
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains(
                "re-attached example.mod/General/Count to the locked server value after plugin reload",
                StringComparison.Ordinal));

        Assert.Equal(1, ConfigSync.RestoreLocal());
        Assert.Equal(3, rebound.Value);
    }

    [Fact]
    public void Type_gate_v1_packet_under_Minor_refuses_and_logs()
    {
        Assert.False(ConfigSync.TypeGateAllows(NetworkVersionRule.Minor, "", typeof(int)));
        Assert.True(ConfigSync.TypeGateAllows(NetworkVersionRule.Exact, "", typeof(int)));
        Assert.True(ConfigSync.TypeGateAllows(NetworkVersionRule.None, "", typeof(int)));
        Assert.True(ConfigSync.TypeGateAllows(
            NetworkVersionRule.Minor, ConfigSync.TypeToken(typeof(int)), typeof(int)));
        Assert.False(ConfigSync.TypeGateAllows(
            NetworkVersionRule.Minor, ConfigSync.TypeToken(typeof(int)), typeof(string)));

        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        ConfigSync.TestApplyRule = NetworkVersionRule.Minor;
        int applied = ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") },
            snapshot: true);
        Assert.Equal(0, applied);
        Assert.Equal(3, count.Value);
        Assert.False(ConfigSync.IsLocked(count));
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains(
                "schema-drift example.mod/General/Count: v1 packet has no type name; type gate refused under Minor (client type '"
                + ConfigSync.TypeToken(typeof(int)) + "')",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Type_gate_v1_packet_under_Exact_still_applies()
    {
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        ConfigSync.TestApplyRule = NetworkVersionRule.Exact;
        int applied = ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") },
            snapshot: true);
        Assert.Equal(1, applied);
        Assert.Equal(9, count.Value);
        Assert.True(ConfigSync.IsLocked(count));
        Assert.DoesNotContain(
            ConfigSync.TestLogs,
            line => line.Contains("type gate refused under Minor", StringComparison.Ordinal));
    }

    [Fact]
    public void Explicit_Save_after_rebind_writes_the_local_not_the_server_value()
    {
        var count = _file.Bind("General", "Count", 3);
        var handle = ConfigSync.Register("example.mod", "1.0.0", count);
        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") },
            snapshot: true);
        handle.Dispose();
        var rebound = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n", pluginVersion: "1.0.0");
        Assert.Equal(9, rebound.Value);

        _file.Save();
        Assert.Contains("Count = 3", _file.LastSavedText ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("Count = 9", _file.LastSavedText ?? "", StringComparison.Ordinal);
        Assert.Equal(9, rebound.Value);
    }

    [Fact]
    public void Explicit_Save_writes_local_for_a_key_bound_after_the_lock()
    {
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") },
            snapshot: true);
        Assert.Equal(9, count.Value);

        var title = ConfigSync.Bind(_file, "example.mod", "General", "Title", "hello", "s");
        _file.Save();
        Assert.Contains("Count = 3", _file.LastSavedText ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("Count = 9", _file.LastSavedText ?? "", StringComparison.Ordinal);
        Assert.Contains("Title = hello", _file.LastSavedText ?? "", StringComparison.Ordinal);

        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Title", "server") },
            snapshot: false);
        Assert.Equal("server", title.Value);
        _file.Save();
        Assert.Contains("Title = hello", _file.LastSavedText ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("Title = server", _file.LastSavedText ?? "", StringComparison.Ordinal);
        Assert.Contains("Count = 3", _file.LastSavedText ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_Save_writes_local_even_when_ConfigFile_Remove_throws()
    {
        var count = ConfigSync.Bind(_file, "example.mod", "General", "Count", 3, "n");
        _file.RemoveThrows = true;
        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "General", "Count", "9") },
            snapshot: true);
        Assert.Equal(9, count.Value);
        Assert.False(_file.ContainsKey(count.Definition));

        _file.Save();
        Assert.Contains("Count = 3", _file.LastSavedText ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("Count = 9", _file.LastSavedText ?? "", StringComparison.Ordinal);
        Assert.Equal(9, count.Value);
    }
}
