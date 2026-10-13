using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using BepInEx.Configuration;
using Progenitor;
using Progenitor.Config;
using Progenitor.Dev;
using Progenitor.Hosted;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Shared-file bind gate and UninstallAll: Core must not leave a Core-typed
/// <c>ConfigEntry&lt;T&gt;</c> or a SettingChanged handler on BepInEx's process-lifetime
/// ConfigFile (evidence 65). Serialized with HarmonySlotHandoff tests because Bind honors
/// the process-wide SkipCoreModules set.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class ConfigHelperTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigFile _shared;
    private readonly ConfigFile _plugin;

    public ConfigHelperTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _shared = new ConfigFile(Path.Combine(_dir, ConfigHelper.SharedFileName), saveOnInit: true);
        _plugin = new ConfigFile(Path.Combine(_dir, "example.mod.cfg"), saveOnInit: true);
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
    }

    public void Dispose()
    {
        ConfigHelper.UninstallAll();
        ConfigSync.ResetForTests();
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        // Production debounce timing, even if a retry fact threw before its own restore.
        ConfigHelper.SetDebounceForTests(1.0);
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    public enum FakeCoreEnum
    {
        Alpha = 0,
        Beta = 1,
    }

    [Fact]
    public void Shared_file_is_identified_by_progenitor_cfg_name()
    {
        Assert.True(ConfigHelper.IsSharedConfigFile(_shared));
        Assert.False(ConfigHelper.IsSharedConfigFile(_plugin));
        Assert.False(ConfigHelper.IsSharedConfigFile(null));
    }

    [Fact]
    public void Bind_refuses_a_Core_enum_on_the_shared_file_the_old_path()
    {
        var hosted = Assert.Throws<ArgumentException>(() =>
            ConfigHelper.Bind(
                _shared,
                "Hosted",
                "ServerPluginsMode",
                HostedServerPluginsMode.Shared,
                "old path: ConfigEntry<HostedServerPluginsMode> on the shared file"));
        Assert.Contains("HostedServerPluginsMode", hosted.Message, StringComparison.Ordinal);
        Assert.Contains("collectible", hosted.Message, StringComparison.OrdinalIgnoreCase);

        var fake = Assert.Throws<ArgumentException>(() =>
            ConfigHelper.Bind(_shared, "Test", "Fake", FakeCoreEnum.Alpha, "old path"));
        Assert.Contains("FakeCoreEnum", fake.Message, StringComparison.Ordinal);
        Assert.Empty(ReadEntries(_shared));
    }

    [Fact]
    public void Bind_allows_BCL_primitives_and_string_on_the_shared_file()
    {
        var enabled = ConfigHelper.Bind(_shared, "Hosted", "Enabled", true, "on/off");
        var name = ConfigHelper.Bind(_shared, "Hosted", "Name", "Shared", "enum-as-string");
        var day = ConfigHelper.Bind(_shared, "Hosted", "Day", DayOfWeek.Monday, "BCL enum");
        Assert.True(enabled.Value);
        Assert.Equal("Shared", name.Value);
        Assert.Equal(DayOfWeek.Monday, day.Value);
    }

    [Fact]
    public void BindEnum_keeps_the_enum_name_so_existing_cfg_values_load()
    {
        var entry = ConfigHelper.BindEnum(
            _shared,
            "Hosted",
            "ServerPluginsMode",
            HostedServerPluginsMode.Shared,
            "string-bound Core enum");
        Assert.Equal("Shared", entry.Value);
        Assert.Equal(HostedServerPluginsMode.Shared, ConfigHelper.ReadEnum(entry, HostedServerPluginsMode.ProgenitorOnly));

        entry.Value = "ProgenitorOnly";
        Assert.Equal(HostedServerPluginsMode.ProgenitorOnly, ConfigHelper.ReadEnum(entry, HostedServerPluginsMode.Shared));
    }

    [Fact]
    public void ReadEnum_logs_and_uses_the_default_on_a_bad_value()
    {
        var entry = ConfigHelper.Bind(_shared, "Hosted", "ServerPluginsMode", "not-a-mode", "bad");
        var parsed = ConfigHelper.ReadEnum(entry, HostedServerPluginsMode.Shared);
        Assert.Equal(HostedServerPluginsMode.Shared, parsed);
    }

    [Fact]
    public void Bind_throws_when_Config_is_in_SkipCoreModules()
    {
        HarmonySlotHandoff.SetSkipModules("Config", refusedLiveWorld: false);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigHelper.Bind(_shared, "Hosted", "Enabled", true, "skip"));
        Assert.Contains("Config", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SkipCoreModules", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallAll_drops_shared_entries_and_SettingChanged_handlers_and_keeps_the_value_as_an_orphan()
    {
        var enabled = ConfigHelper.Bind(_shared, "Hosted", "Enabled", true, "on/off");
        var mode = ConfigHelper.BindEnum(
            _shared,
            "Hosted",
            "ServerPluginsMode",
            HostedServerPluginsMode.Shared,
            "string-bound");
        enabled.Value = false;
        mode.Value = "ProgenitorOnly";

        EventHandler<SettingChangedEventArgs> handler = (_, _) => { };
        ConfigHelper.AddSettingChanged(_shared, handler);

        Assert.NotEmpty(ReadEntries(_shared));
        Assert.NotNull(ReadSettingChanged(_shared));

        ConfigHelper.UninstallAll();
        ConfigHelper.UninstallAll();

        Assert.Empty(ReadEntries(_shared));
        Assert.DoesNotContain(ReadEntries(_shared).Values, e => e.SettingType.Assembly == typeof(ConfigHelper).Assembly);
        Assert.Null(ReadSettingChanged(_shared));

        var orphans = ReadOrphans(_shared);
        Assert.Equal("False", orphans[new ConfigDefinition("Hosted", "Enabled")]);
        Assert.Equal("ProgenitorOnly", orphans[new ConfigDefinition("Hosted", "ServerPluginsMode")]);

        var rebound = ConfigHelper.BindEnum(
            _shared,
            "Hosted",
            "ServerPluginsMode",
            HostedServerPluginsMode.Shared,
            "next generation");
        Assert.Equal("ProgenitorOnly", rebound.Value);
        Assert.Equal(HostedServerPluginsMode.ProgenitorOnly, ConfigHelper.ReadEnum(rebound, HostedServerPluginsMode.Shared));
    }

    [Fact]
    public void Bind_evicts_a_leftover_Core_typed_entry_from_the_old_path_and_rebinds_as_string()
    {
        _shared.Bind("Hosted", "ServerPluginsMode", HostedServerPluginsMode.ProgenitorOnly, "old path leftover");
        Assert.Equal(typeof(HostedServerPluginsMode), _shared[new ConfigDefinition("Hosted", "ServerPluginsMode")].SettingType);

        var rebound = ConfigHelper.BindEnum(
            _shared,
            "Hosted",
            "ServerPluginsMode",
            HostedServerPluginsMode.Shared,
            "upgrade to string");
        Assert.Equal(typeof(string), rebound.SettingType);
        Assert.Equal("ProgenitorOnly", rebound.Value);
        Assert.DoesNotContain(ReadEntries(_shared).Values, e => e.SettingType == typeof(HostedServerPluginsMode));
    }

    [Fact]
    public void UninstallAll_does_not_remove_entries_from_a_plugin_file()
    {
        var entry = ConfigHelper.Bind(_plugin, "General", "Enabled", true, "plugin GUID.cfg");
        ConfigHelper.UninstallAll();
        Assert.True(_plugin.ContainsKey(new ConfigDefinition("General", "Enabled")));
        Assert.True(entry.Value);
    }

    /// <summary>
    /// FCFG2. The mirror of the test above, which uninstalls twice and binds once: bind the
    /// same definition twice in one generation (ModManifest.Install runs from
    /// Handshake.Install and from the first-frame kick, and binds the transfer limits both
    /// times) and uninstall once. Before the fix, TrackedBinds held two records; the second
    /// eviction found the entry already removed, computed serialized == "" and overwrote the
    /// good orphan, so the next generation read the default.
    /// </summary>
    [Fact]
    public void An_int_bound_twice_keeps_its_value_across_one_UninstallAll()
    {
        var first = ConfigHelper.Bind(_shared, "Networking", "ModTransferMaxBytes", 33554432, "cap");
        var second = ConfigHelper.Bind(_shared, "Networking", "ModTransferMaxBytes", 33554432, "cap");
        Assert.Same(first, second);
        first.Value = 1048576;

        ConfigHelper.UninstallAll();

        var definition = new ConfigDefinition("Networking", "ModTransferMaxBytes");
        Assert.Equal("1048576", ReadOrphans(_shared)[definition]);

        var rebound = ConfigHelper.Bind(_shared, "Networking", "ModTransferMaxBytes", 33554432, "next generation");
        Assert.Equal(1048576, rebound.Value);
    }

    /// <summary>
    /// FCFG2, the bool half: <c>Networking.DenyTimeModDescriptor</c> is an operator privacy
    /// choice, and a blank orphan reverts it to the permissive default.
    /// </summary>
    [Fact]
    public void A_bool_bound_twice_keeps_false_across_one_UninstallAll()
    {
        var entry = ConfigHelper.Bind(_shared, "Networking", "DenyTimeModDescriptor", true, "privacy");
        ConfigHelper.Bind(_shared, "Networking", "DenyTimeModDescriptor", true, "privacy");
        entry.Value = false;

        ConfigHelper.UninstallAll();

        var definition = new ConfigDefinition("Networking", "DenyTimeModDescriptor");
        Assert.Equal("False", ReadOrphans(_shared)[definition]);

        var rebound = ConfigHelper.Bind(_shared, "Networking", "DenyTimeModDescriptor", true, "next generation");
        Assert.False(rebound.Value);
    }

    /// <summary>
    /// FCFG2, the on-disk half. Live BepInEx Saves inside Bind (SaveOnConfigSet), so an empty
    /// orphan reaches the cfg file, not just memory.
    /// </summary>
    [Fact]
    public void A_Save_after_UninstallAll_does_not_write_an_empty_value()
    {
        var entry = ConfigHelper.Bind(_shared, "Networking", "ModTransferMaxBytes", 33554432, "cap");
        ConfigHelper.Bind(_shared, "Networking", "ModTransferMaxBytes", 33554432, "cap");
        entry.Value = 1048576;

        ConfigHelper.UninstallAll();
        _shared.Save();

        var text = _shared.LastSavedText ?? "";
        Assert.Contains("ModTransferMaxBytes = 1048576", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ModTransferMaxBytes = \n", text + "\n", StringComparison.Ordinal);
        Assert.Contains("ModTransferMaxBytes = 1048576", File.ReadAllText(_shared.ConfigFilePath), StringComparison.Ordinal);
    }

    /// <summary>
    /// FCFG2, the whole generation sequence the live server runs: two binds, a Core swap, and
    /// the next generation's binds. The operator's five values must survive it.
    /// </summary>
    [Fact]
    public void Two_generations_of_double_binds_keep_the_operator_values()
    {
        var max = ConfigHelper.Bind(_shared, "Networking", "ModTransferMaxBytes", 33554432, "cap");
        ConfigHelper.Bind(_shared, "Networking", "ModTransferMaxBytes", 33554432, "cap");
        var deny = ConfigHelper.Bind(_shared, "Networking", "DenyTimeModDescriptor", true, "privacy");
        ConfigHelper.Bind(_shared, "Networking", "DenyTimeModDescriptor", true, "privacy");
        max.Value = 1048576;
        deny.Value = false;

        for (int generation = 0; generation < 3; generation++)
        {
            ConfigHelper.UninstallAll();
            max = ConfigHelper.Bind(_shared, "Networking", "ModTransferMaxBytes", 33554432, "cap");
            ConfigHelper.Bind(_shared, "Networking", "ModTransferMaxBytes", 33554432, "cap");
            deny = ConfigHelper.Bind(_shared, "Networking", "DenyTimeModDescriptor", true, "privacy");
            ConfigHelper.Bind(_shared, "Networking", "DenyTimeModDescriptor", true, "privacy");
            Assert.Equal(1048576, max.Value);
            Assert.False(deny.Value);
        }
    }

    /// <summary>
    /// FCFG2. A key bound twice is one tracked bind, so <c>DescribePins</c>' sharedBinds count
    /// stops over-counting and the eviction loop runs once per definition.
    /// </summary>
    [Fact]
    public void A_key_bound_twice_is_one_tracked_bind()
    {
        ConfigHelper.Bind(_shared, "Networking", "ModTransferMaxBytes", 33554432, "cap");
        ConfigHelper.Bind(_shared, "Networking", "ModTransferMaxBytes", 33554432, "cap");
        ConfigHelper.Bind(_shared, "Networking", "ModTransferBytesPerSecond", 1024, "rate");

        var binds = (System.Collections.ICollection?)typeof(ConfigHelper)
            .GetField("TrackedBinds", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null);
        Assert.Equal(2, binds!.Count);
    }

    /// <summary>
    /// FCFG4 (R50 #3), correcting FCFG3. <c>TryParkValueForSave</c> is <c>EvictToOrphan</c>'s twin
    /// and the one whose whole job is preserving the operator's local value across an explicit
    /// Save. It must refuse a value that was never read — parking it drops the live row and writes
    /// the key blank — but "never read" is a signal the caller passes, not the shape of the string.
    /// FCFG3 inferred it from <c>serialized == ""</c>; this asserts the two halves separately, so
    /// the empty-and-read case (the next test) can be parked.
    /// </summary>
    [Fact]
    public void Parking_a_value_that_was_never_read_keeps_the_live_row_and_the_key_on_disk()
    {
        var entry = _plugin.Bind("Networking", "ModTransferMaxBytes", 1048576, "operator value");
        var definition = new ConfigDefinition("Networking", "ModTransferMaxBytes");

        Assert.False(ConfigHelper.TryParkValueForSave(_plugin, definition, "", localValueWasRead: false));

        Assert.True(_plugin.ContainsKey(definition));
        Assert.Empty(ReadOrphans(_plugin));
        _plugin.Save();
        Assert.Contains("ModTransferMaxBytes = 1048576", _plugin.LastSavedText ?? "", StringComparison.Ordinal);
        Assert.Equal(1048576, entry.Value);
    }

    /// <summary>
    /// FCFG4 (R50 #3). An empty local value that <em>was</em> read is the operator's value and is
    /// parked like any other. <c>ReadToml</c> is total and <c>GetSerializedValue()</c> answers
    /// <c>""</c> as its normal, successful result for a cleared <c>string</c> entry, so under
    /// FCFG3's string-shaped guard this park was refused and the live row — holding the server's
    /// value — survived to be written by the next Save.
    /// </summary>
    [Fact]
    public void Parking_an_empty_value_that_was_read_parks_it_rather_than_keeping_the_live_row()
    {
        var entry = _plugin.Bind("Server", "Motd", "", "the operator cleared this");
        entry.Value = "server text"; // what the lock applied
        var definition = new ConfigDefinition("Server", "Motd");

        Assert.True(ConfigHelper.TryParkValueForSave(_plugin, definition, "", localValueWasRead: true));

        Assert.False(_plugin.ContainsKey(definition));
        Assert.Equal("", ReadOrphans(_plugin)[definition]);
    }

    /// <summary>
    /// FCFG4 (R50 #3), the whole regression end to end and on disk: an operator clears a synced
    /// <c>string</c>, the server pushes its own text, and an explicit <c>Save</c> during the
    /// session must write the operator's empty value back — not the server's. The file is read
    /// off disk rather than from <c>LastSavedText</c> because the <c>.cfg</c> is what survives the
    /// session.
    /// </summary>
    [Fact]
    public void An_operator_who_clears_a_synced_string_keeps_the_blank_on_disk_across_a_locked_Save()
    {
        var entry = _plugin.Bind("Server", "Motd", "", "cleared by the operator");
        var definition = new ConfigDefinition("Server", "Motd");
        string local = entry.GetSerializedValue(); // "" — the successful read of a cleared string
        Assert.Equal("", local);

        entry.Value = "Welcome to the server"; // the server value ApplyOne locked in
        Assert.True(ConfigHelper.TryParkValueForSave(_plugin, definition, local, localValueWasRead: true));

        _plugin.Save();

        string onDisk = File.ReadAllText(_plugin.ConfigFilePath);
        Assert.Contains("Motd = \n", onDisk + "\n", StringComparison.Ordinal);
        Assert.DoesNotContain("Welcome to the server", onDisk, StringComparison.Ordinal);
    }

    /// <summary>
    /// FCFG3. The park path still works for a value that was read: the row goes, the orphan
    /// carries the local Toml, and Save writes it instead of the live (server) value.
    /// </summary>
    [Fact]
    public void Parking_a_value_that_was_read_writes_the_local_and_not_the_locked_value()
    {
        var entry = _plugin.Bind("Networking", "ModTransferMaxBytes", 1048576, "local");
        entry.Value = 33554432; // the server value the lock applied
        var definition = new ConfigDefinition("Networking", "ModTransferMaxBytes");

        Assert.True(ConfigHelper.TryParkValueForSave(_plugin, definition, "1048576", localValueWasRead: true));

        Assert.False(_plugin.ContainsKey(definition));
        Assert.Equal("1048576", ReadOrphans(_plugin)[definition]);
        _plugin.Save();
        Assert.Contains("ModTransferMaxBytes = 1048576", _plugin.LastSavedText ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// FCFG3. The rule lives in <c>TryAddOrphan</c> so both callers get it: an empty string
    /// never overwrites a parked value. The next generation's Bind cannot parse "" and falls
    /// back to its default, which SaveOnConfigSet then writes over the operator's value.
    /// </summary>
    [Fact]
    public void An_empty_string_never_overwrites_a_parked_value()
    {
        var definition = new ConfigDefinition("Networking", "ModTransferMaxBytes");
        Assert.True(ConfigHelper.TryAddOrphan(_plugin, definition, "1048576"));
        Assert.True(ConfigHelper.TryAddOrphan(_plugin, definition, ""));
        Assert.Equal("1048576", ReadOrphans(_plugin)[definition]);

        // A definition with nothing parked yet is still allowed to park an empty string,
        // and a real value still replaces a parked one.
        var other = new ConfigDefinition("Networking", "Note");
        Assert.True(ConfigHelper.TryAddOrphan(_plugin, other, ""));
        Assert.Equal("", ReadOrphans(_plugin)[other]);
        Assert.True(ConfigHelper.TryAddOrphan(_plugin, definition, "512"));
        Assert.Equal("512", ReadOrphans(_plugin)[definition]);
    }

    /// <summary>
    /// FCFG3. <c>EvictToOrphan</c>'s guard is on the read, not on the entry: a live entry
    /// whose <c>GetSerializedValue()</c> and <c>BoxedValue</c> both throw leaves
    /// <c>serialized == ""</c> with nothing behind it, which is the same empty write by
    /// another route.
    /// </summary>
    [Fact]
    public void An_entry_that_cannot_be_serialized_does_not_blank_its_parked_value()
    {
        var definition = new ConfigDefinition("Networking", "ModTransferMaxBytes");
        ConfigHelper.TryAddOrphan(_plugin, definition, "1048576");

        var live = LiveEntries(_plugin);
        live[definition] = new ThrowingEntry(_plugin, definition);

        typeof(ConfigHelper)
            .GetMethod("EvictToOrphan", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { _plugin, definition, false });

        Assert.False(_plugin.ContainsKey(definition));
        Assert.Equal("1048576", ReadOrphans(_plugin)[definition]);
    }

    /// <summary>
    /// FCFG4 (R50 #7). <c>TryAddOrphan</c>'s contract says a caller about to drop the live row must
    /// not drop it when the park failed; FCFG3 wrote that rule down and then had
    /// <c>EvictToOrphan</c> remove the row first and discard the bool. With no reachable
    /// <c>OrphanedEntries</c> the value would then be in neither table and the next Save would
    /// write the freshly bound default — FCFG2's silent revert by another route. The row now stays.
    /// </summary>
    [Fact]
    public void An_unreachable_orphan_table_keeps_the_live_row_rather_than_dropping_it()
    {
        var file = new NoOrphanTableConfigFile(Path.Combine(_dir, "no-orphan-table.cfg"));
        file.Bind("Networking", "ModTransferMaxBytes", 1048576, "operator value");
        var definition = new ConfigDefinition("Networking", "ModTransferMaxBytes");
        Assert.Null(ConfigHelper.GetOrphanedEntries(file));

        typeof(ConfigHelper)
            .GetMethod("EvictToOrphan", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { file, definition, false });

        Assert.True(file.ContainsKey(definition));
        file.Save();
        Assert.Contains(
            "ModTransferMaxBytes = 1048576",
            File.ReadAllText(file.ConfigFilePath),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// FCFG5 (R52 #6), correcting FCFG4. The "keep the live row when the park failed" degradation
    /// is right for <c>UninstallAll</c> and wrong for the enum-as-string upgrade, whose whole
    /// purpose is that the <c>Bind&lt;T&gt;</c> on the next line does not meet a row of another
    /// <c>SettingType</c>. BepInEx returns the <em>existing</em> entry for a definition it already
    /// has, so a surviving row made <c>ConfigHelper.Bind&lt;string&gt;</c> throw
    /// <c>InvalidCastException</c> — <c>EvictMismatchedEntry</c>'s own try/catch wraps the
    /// eviction, which now returns normally. The reflection test above cannot see this, because it
    /// never goes through <c>Bind</c>.
    /// </summary>
    [Fact]
    public void An_unreachable_orphan_table_still_lets_a_mismatched_row_be_replaced_through_Bind()
    {
        // Its own directory: the name has to be the shared one, and _shared already holds that
        // path in _dir.
        string dir = Path.Combine(_dir, "no-orphan-shared");
        Directory.CreateDirectory(dir);
        var file = new NoOrphanTableSharedConfigFile(Path.Combine(dir, ConfigHelper.SharedFileName));
        file.Bind("Networking", "DenyTimeModDescriptor", FakeCoreEnum.Beta, "the old typed row");
        Assert.Null(ConfigHelper.GetOrphanedEntries(file));

        var entry = ConfigHelper.Bind(file, "Networking", "DenyTimeModDescriptor", "Alpha", "as a string");

        Assert.Equal(typeof(string), entry.SettingType);
        Assert.Equal(typeof(string), file[new ConfigDefinition("Networking", "DenyTimeModDescriptor")].SettingType);
    }

    /// <summary>
    /// A shared-file <c>ConfigFile</c> whose orphan table cannot be reached — same shape as
    /// <see cref="NoOrphanTableConfigFile"/>, named so <c>IsSharedConfigFile</c> answers true and
    /// <c>Bind</c> takes the eviction path.
    /// </summary>
    private sealed class NoOrphanTableSharedConfigFile : ConfigFile
    {
        public NoOrphanTableSharedConfigFile(string path)
            : base(path, saveOnInit: true)
        {
        }
    }

    /// <summary>
    /// FCFG5 (R52 #4), correcting FCFG4. <c>ConfigSync.RestoreSlot</c> is the park's inverse and
    /// had the ordering FCFG4 had just fixed in <c>EvictToOrphan</c>: it unparked first — putting
    /// the live row, still holding the <em>server's</em> value, back into <c>Entries</c> and
    /// deleting the orphan holding the local — then discarded the write-back's own result. A
    /// write-back that did nothing therefore left the operator's value in neither table, and
    /// <c>RestoreLocal</c>'s Save wrote the server's value into their <c>.cfg</c>.
    ///
    /// <para>Driven through <c>ConfigSync</c> on the shim rather than by reflection, because the
    /// bug is in the order of two calls and only the whole disconnect path shows it. The write
    /// fails once, and the assertion is that nothing was lost and nothing was saved; then it
    /// succeeds, and the same parked value lands in the entry.</para>
    /// </summary>
    [Fact]
    public void A_restore_whose_write_back_fails_keeps_the_park_and_the_next_attempt_succeeds()
    {
        ConfigSync.ResetForTests();
        var definition = new ConfigDefinition("Server", "Motd");
        var entry = new FlakyStringEntry(_plugin, definition, "operator text");
        LiveEntries(_plugin)[definition] = entry;

        using var registration = ConfigSync.Register("example.mod", entry);
        var snapshot = new[] { new ConfigSyncRecord("example.mod", "Server", "Motd", "server text") };
        Assert.Equal(1, ConfigSync.ApplyRecords(snapshot, snapshot: true));
        Assert.Equal("server text", entry.BoxedValue);
        Assert.False(_plugin.ContainsKey(definition));
        Assert.Equal("operator text", ReadOrphans(_plugin)[definition]);

        // The disconnect the operator's value has to survive.
        entry.FailWrites = true;
        int savesBeforeTheFailedRestore = _plugin.SaveCount;
        Assert.Equal(0, ConfigSync.RestoreLocal());

        Assert.Equal("operator text", ReadOrphans(_plugin)[definition]);
        Assert.False(_plugin.ContainsKey(definition));
        Assert.Equal("server text", entry.BoxedValue);
        Assert.Equal(savesBeforeTheFailedRestore, _plugin.SaveCount);
        // FCFG6 (R53 #4): one failure is still inside the retry bound, so the hold stands.
        Assert.False(_plugin.SaveOnConfigSet);

        // Still locked, so the next restore retries rather than walking past a lost value.
        entry.FailWrites = false;
        Assert.Equal(1, ConfigSync.RestoreLocal());

        Assert.Equal("operator text", entry.BoxedValue);
        Assert.True(_plugin.ContainsKey(definition));
        Assert.Empty(ReadOrphans(_plugin));
        // …and the restore releases it, which is the half a give-up has to reproduce.
        Assert.True(_plugin.SaveOnConfigSet);
    }

    /// <summary>
    /// FCFG6 (R53 #4), correcting FCFG5, and FCFG7 (R54 #2), correcting the unit FCFG6 counted in.
    /// The retry FCFG5 introduced had no end, and the lock it keeps holds
    /// <c>SaveOnConfigSet = false</c> for the whole <c>ConfigFile</c> — the hold is per file
    /// because that is the only granularity BepInEx has. FCFG6 gave it an end but spent the budget
    /// one attempt per <c>RestoreLocal</c> call, and that method is only reached when the client
    /// builds a new world or Core uninstalls: on a normal quit, where BepInEx never calls
    /// <c>Plugin.Unload</c>, the freeze outlived the session anyway.
    ///
    /// <para>So the budget is seconds now, and the pump is the module's own posted timer. This
    /// test drives exactly one <c>RestoreLocal</c> — one leave-to-menu — and then does nothing but
    /// run the posts the module scheduled for itself, advancing a fake clock by each post's own
    /// delay. The give-up must arrive inside the budget: an error line naming the key, the parked
    /// value and the reason; the parked local still on disk as an orphan; the lock and the file's
    /// hold released.</para>
    /// </summary>
    [Fact]
    public void A_restore_that_never_succeeds_retries_on_its_own_timer_and_gives_up_inside_the_budget()
    {
        ConfigSync.ResetForTests();
        var definition = new ConfigDefinition("Server", "Motd");
        var entry = new FlakyStringEntry(_plugin, definition, "operator text");
        LiveEntries(_plugin)[definition] = entry;

        double now = 100.0;
        var posted = new List<(double Delay, Action Run)>();
        ConfigSync.TestClock = () => now;
        ConfigSync.TestPostDelayed = (seconds, action, _) => posted.Add((seconds, action));

        using var registration = ConfigSync.Register("example.mod", entry);
        var snapshot = new[] { new ConfigSyncRecord("example.mod", "Server", "Motd", "server text") };
        Assert.Equal(1, ConfigSync.ApplyRecords(snapshot, snapshot: true));
        Assert.False(_plugin.SaveOnConfigSet);

        // One world-ready. Everything after this is the module pumping itself.
        entry.FailWrites = true;
        double firstFailure = now;
        Assert.Equal(0, ConfigSync.RestoreLocal());
        Assert.False(_plugin.SaveOnConfigSet);
        var first = Assert.Single(posted);
        Assert.Equal(ConfigSync.RestoreRetryIntervalSeconds, first.Delay);
        Assert.Empty(
            ConfigSync.TestLogs.FindAll(l => l.Contains("giving up on restoring", StringComparison.Ordinal)));

        int attempts = 1;
        while (posted.Count > 0)
        {
            var next = posted[0];
            posted.RemoveAt(0);
            // One post outstanding at a time, so this list never queues two timers for one key.
            Assert.Empty(posted);
            now += next.Delay;
            next.Run();
            attempts++;
            Assert.True(attempts <= ConfigSync.MaxRestoreAttempts, "the posted retry never ended");
        }

        // Seconds, not leave-to-menus: the whole thing is over within the advertised budget.
        Assert.True(
            now - firstFailure <= ConfigSync.RestoreRetryBudgetSeconds,
            "the retry ran for " + (now - firstFailure).ToString("0.##") + " s");

        // Loud: the key, the parked value and the reason are all in the line.
        string line = Assert.Single(
            ConfigSync.TestLogs.FindAll(l => l.Contains("giving up on restoring", StringComparison.Ordinal)));
        Assert.Contains("example.mod/Server/Motd", line, StringComparison.Ordinal);
        Assert.Contains("operator text", line, StringComparison.Ordinal);
        Assert.Contains("this entry cannot be written now", line, StringComparison.Ordinal);

        // Nothing is lost on disk, and the rest of the file autosaves again.
        Assert.Equal("operator text", ReadOrphans(_plugin)[definition]);
        Assert.True(_plugin.SaveOnConfigSet);

        // Unlocked: a later world-ready has nothing to do rather than counting another failure.
        Assert.Equal(0, ConfigSync.RestoreLocal());
        Assert.Empty(posted);
        Assert.Single(
            ConfigSync.TestLogs.FindAll(l => l.Contains("giving up on restoring", StringComparison.Ordinal)));
    }

    /// <summary>
    /// FCFG7 (R54 #2), the other end of the same budget. A Core <c>Uninstall</c> is the one caller
    /// that has no "next attempt": the generation is going away and the posted retry goes with it.
    /// So it drops the outstanding posts and runs the give-up at once for everything still locked,
    /// which is what actually releases the file's hold. The post it dropped must then be inert —
    /// the retry record is gone, so running it is a no-op rather than a second give-up against a
    /// cleared registry.
    /// </summary>
    [Fact]
    public void Uninstall_with_a_pending_retry_gives_up_at_once_and_the_dropped_post_is_inert()
    {
        ConfigSync.ResetForTests();
        var definition = new ConfigDefinition("Server", "Motd");
        var entry = new FlakyStringEntry(_plugin, definition, "operator text");
        LiveEntries(_plugin)[definition] = entry;

        double now = 100.0;
        var posted = new List<Action>();
        ConfigSync.TestClock = () => now;
        ConfigSync.TestPostDelayed = (_, action, _) => posted.Add(action);

        using (var registration = ConfigSync.Register("example.mod", entry))
        {
            var snapshot = new[] { new ConfigSyncRecord("example.mod", "Server", "Motd", "server text") };
            Assert.Equal(1, ConfigSync.ApplyRecords(snapshot, snapshot: true));

            entry.FailWrites = true;
            Assert.Equal(0, ConfigSync.RestoreLocal());
            Assert.Single(posted);
            Assert.False(_plugin.SaveOnConfigSet);

            ConfigSync.Uninstall();
        }

        // The give-up line is not asserted here: Uninstall ends by clearing TestLogs. What matters
        // is that the hold went back with seconds still on the clock.
        Assert.True(_plugin.SaveOnConfigSet);
        Assert.Equal("operator text", ReadOrphans(_plugin)[definition]);

        // The post Uninstall dropped: harmless, and it does not put the hold back on.
        posted[0]();
        Assert.True(_plugin.SaveOnConfigSet);
        Assert.Empty(
            ConfigSync.TestLogs.FindAll(l => l.Contains("giving up on restoring", StringComparison.Ordinal)));
    }

    /// <summary>
    /// FCFG7 (R54 #8). <c>GiveUpRestore</c> passed the literal <c>true</c> for
    /// <c>localValueWasRead</c>, and that flag is what lets <c>ConfigHelper.TryParkValueForSave</c>
    /// write an empty string over a good orphan (<c>emptyIsAValue</c>). It has a branch where no
    /// local Toml was ever read — only a boxed capture — and on that branch there is no operator
    /// value to park at all. The lock-time park already refuses such a slot (the live row stays in
    /// the file, no orphan is written); the give-up must refuse it for the same reason and say so.
    ///
    /// <para>The slot is built the only way the production code can produce it: an entry whose
    /// reads throw for long enough that <c>ReadToml</c> itself throws — every one of its three
    /// fallbacks included — and then start working, so the boxed capture on the next line
    /// succeeds.</para>
    /// </summary>
    [Fact]
    public void A_give_up_on_a_slot_whose_local_was_never_read_writes_nothing_and_says_so()
    {
        ConfigSync.ResetForTests();
        var definition = new ConfigDefinition("Server", "Motd");
        var entry = new UnreadableThenReadableEntry(_plugin, definition, "operator text");
        LiveEntries(_plugin)[definition] = entry;

        double now = 100.0;
        ConfigSync.TestClock = () => now;
        ConfigSync.TestPostDelayed = (_, _, _) => { };

        using var registration = ConfigSync.Register("example.mod", entry);
        // Armed after Register, which reads the entry itself to size the record. Three throws:
        // GetSerializedValue, ConfigSyncToml.Format, and the ToString fallback - so ReadToml
        // itself throws and the capture on the next line, read four, succeeds.
        entry.ThrowingReads = 3;
        var snapshot = new[] { new ConfigSyncRecord("example.mod", "Server", "Motd", "server text") };
        Assert.Equal(1, ConfigSync.ApplyRecords(snapshot, snapshot: true));
        Assert.Equal(0, entry.ThrowingReads);

        // The local was never read, so the lock-time park refused too: no orphan, live row intact.
        Assert.False(ReadOrphans(_plugin).ContainsKey(definition));

        entry.FailWrites = true;
        Assert.Equal(0, ConfigSync.RestoreLocal());
        now += ConfigSync.RestoreRetryBudgetSeconds;
        Assert.Equal(0, ConfigSync.RestoreLocal());

        string line = Assert.Single(
            ConfigSync.TestLogs.FindAll(l => l.Contains("giving up on restoring", StringComparison.Ordinal)));
        Assert.Contains("never read", line, StringComparison.Ordinal);

        // Nothing was written: no orphan row, and no blank one either.
        Assert.False(ReadOrphans(_plugin).ContainsKey(definition));
        Assert.Empty(
            ConfigSync.TestLogs.FindAll(l => l.Contains("could not be written as an orphan", StringComparison.Ordinal)));
        Assert.True(_plugin.SaveOnConfigSet);
    }

    /// <summary>
    /// FCFG6 (R53 #4), part (b). <c>SaveHolds</c> is a Core static and dies with the collectible
    /// generation; the <c>ConfigFile</c> it describes is BepInEx's and does not. <c>Uninstall</c>
    /// used to run one last restore and then clear the record thirteen lines later, so a final
    /// restore that failed left the file with <c>SaveOnConfigSet = false</c> and no surviving
    /// record of what it had been — permanent for the process, and the next generation's
    /// <c>HoldSaveOff</c> would read that <c>false</c> as the value to go back to.
    /// </summary>
    [Fact]
    public void Uninstall_with_a_standing_hold_does_not_leave_SaveOnConfigSet_off()
    {
        ConfigSync.ResetForTests();
        var definition = new ConfigDefinition("Server", "Motd");
        var entry = new FlakyStringEntry(_plugin, definition, "operator text");
        LiveEntries(_plugin)[definition] = entry;

        using (var registration = ConfigSync.Register("example.mod", entry))
        {
            var snapshot = new[] { new ConfigSyncRecord("example.mod", "Server", "Motd", "server text") };
            Assert.Equal(1, ConfigSync.ApplyRecords(snapshot, snapshot: true));
            Assert.False(_plugin.SaveOnConfigSet);

            // The first failure, on the one restore that has no "next time".
            entry.FailWrites = true;
            ConfigSync.Uninstall();
        }

        // The give-up line itself is not asserted here: Uninstall ends by clearing TestLogs. The
        // test above covers the line; this one covers the state BepInEx is left in.
        Assert.True(_plugin.SaveOnConfigSet);
        Assert.Equal("operator text", ReadOrphans(_plugin)[definition]);
    }

    /// <summary>
    /// An entry whose writes can be made to fail on demand. Its reads always work, so
    /// <c>ApplyOne</c> captures both a <c>LocalToml</c> and a <c>LocalBoxed</c> and the restore
    /// takes the boxed path — the one that used to set <c>ok = true</c> on the line after the
    /// write regardless of what the write did.
    /// </summary>
    private sealed class FlakyStringEntry : ConfigEntryBase
    {
        private string _value;

        public FlakyStringEntry(ConfigFile file, ConfigDefinition definition, string value)
            : base(file, definition, typeof(string))
        {
            _value = value;
        }

        public bool FailWrites;

        public override object? BoxedValue
        {
            get => _value;
            set
            {
                if (FailWrites) throw new InvalidOperationException("test: this entry cannot be written now");
                _value = (string)value!;
            }
        }
    }

    /// <summary>
    /// FCFG7 (R54 #8). An entry whose first <paramref name="throwingReads"/> reads throw and whose
    /// reads then work. <c>ConfigSync.ReadToml</c> tries three reads before giving up, so three
    /// throws make <c>ReadToml</c> itself throw and the slot records
    /// <c>LocalTomlRead = false</c>; the boxed capture on the very next line is read number four
    /// and succeeds, which is the only way a slot reaches the give-up with a boxed value and no
    /// Toml. Writes still fail on demand, as <see cref="FlakyStringEntry"/>'s do.
    /// </summary>
    private sealed class UnreadableThenReadableEntry : ConfigEntryBase
    {
        private string _value;

        public UnreadableThenReadableEntry(ConfigFile file, ConfigDefinition definition, string value)
            : base(file, definition, typeof(string))
        {
            _value = value;
        }

        public bool FailWrites;

        /// <summary>Reads still to throw. <c>Register</c> reads too, so a test arms this after it.</summary>
        public int ThrowingReads;

        public override object? BoxedValue
        {
            get
            {
                if (ThrowingReads > 0)
                {
                    ThrowingReads--;
                    throw new InvalidOperationException("test: this entry cannot be read yet");
                }
                return _value;
            }
            set
            {
                if (FailWrites) throw new InvalidOperationException("test: this entry cannot be written now");
                _value = (string)value!;
            }
        }
    }

    /// <summary>
    /// A <c>ConfigFile</c> whose orphan table cannot be reached: <c>OrphanedEntries</c> is private
    /// on the base, and reflection over the derived type does not see a base private member, which
    /// is the shape <c>ConfigHelper.GetOrphanedEntries</c> already warns about on a BepInEx it does
    /// not recognize.
    /// </summary>
    private sealed class NoOrphanTableConfigFile : ConfigFile
    {
        public NoOrphanTableConfigFile(string path)
            : base(path, saveOnInit: true)
        {
        }
    }

    private sealed class ThrowingEntry : ConfigEntryBase
    {
        public ThrowingEntry(ConfigFile file, ConfigDefinition definition)
            : base(file, definition, typeof(int))
        {
        }

        public override object? BoxedValue
        {
            get => throw new InvalidOperationException("test: this entry cannot be serialized");
            set => throw new InvalidOperationException("test: this entry cannot be written");
        }
    }

    private static IDictionary LiveEntries(ConfigFile file)
    {
        var prop = typeof(ConfigFile).GetProperty(
            "Entries",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ConfigFile.Entries not found.");
        return (IDictionary)prop.GetValue(file)!;
    }

    private static Dictionary<ConfigDefinition, ConfigEntryBase> ReadEntries(ConfigFile file)
    {
        var prop = typeof(ConfigFile).GetProperty(
            "Entries",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ConfigFile.Entries not found.");
        var dict = (IDictionary)prop.GetValue(file)!;
        var copy = new Dictionary<ConfigDefinition, ConfigEntryBase>();
        foreach (DictionaryEntry item in dict)
            copy.Add((ConfigDefinition)item.Key, (ConfigEntryBase)item.Value!);
        return copy;
    }

    private static Dictionary<ConfigDefinition, string> ReadOrphans(ConfigFile file)
    {
        var prop = typeof(ConfigFile).GetProperty(
            "OrphanedEntries",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ConfigFile.OrphanedEntries not found.");
        var dict = (IDictionary)prop.GetValue(file)!;
        var copy = new Dictionary<ConfigDefinition, string>();
        foreach (DictionaryEntry item in dict)
            copy.Add((ConfigDefinition)item.Key, (string)item.Value!);
        return copy;
    }

    // ---- FCFG8: the watch must not reload the write-back its own reload caused -------------
    //
    // Root cause, read out of BepInEx.Core.dll rather than guessed: ConfigFile.Reload() hands
    // every line to ConfigEntryBase.SetSerializedValue, which sets ConfigEntry<T>.BoxedValue ->
    // Value; that setter raises ConfigFile.OnSettingChanged, which calls Save() while
    // SaveOnConfigSet is true. The file therefore changes on disk during the reload, the
    // FileSystemWatcher fires again, and one hand edit produced two "reloaded" lines and two
    // Reloaded callbacks one debounce apart (evidence 207). ConfigFileShims.Reload models that
    // whole chain, so these tests fail without the hash filter.
    //
    // R69 #3 removed the write-back from the ordinary path: the watch now holds SaveOnConfigSet
    // off across the reload loop, so a reload reads and never writes (see
    // A_reload_leaves_the_operators_bytes_on_disk below). These tests therefore set
    // SaveOnConfigSetStuck, the instance whose flag the hold could not turn off — which is the
    // only case that can still produce a write-back, and the case the filter is kept for.

    [Fact]
    public void The_watch_ignores_the_write_back_its_own_reload_caused()
    {
        _plugin.SaveOnConfigSetStuck = true;
        var entry = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        int callbacks = 0;
        ConfigHelper.Reloaded += _ => callbacks++;
        var watch = new ConfigHelper.TestWatch(_plugin);

        // The hand edit. Deliberately not in the shape Save writes it back in — an editor
        // rarely reproduces BepInEx's spacing — so the write-back is a visible change on disk.
        File.WriteAllText(_plugin.ConfigFilePath, "[Watch]  Verbosity=Loud");
        watch.FireDebounced();

        Assert.Equal("Loud", entry.Value);
        Assert.Equal(1, _plugin.ReloadCount);
        Assert.Equal(1, callbacks);
        // The reload really did write the file back; otherwise this test proves nothing.
        Assert.NotEqual("[Watch]  Verbosity=Loud", File.ReadAllText(_plugin.ConfigFilePath));
        Assert.NotNull(watch.SelfWriteHash);

        // The watcher event caused by that write-back. Before FCFG8 this reloaded and fired a
        // second callback with every value unchanged.
        watch.FireDebounced();
        watch.FireDebounced();

        Assert.Equal(1, _plugin.ReloadCount);
        Assert.Equal(1, callbacks);
    }

    [Fact]
    public void A_genuine_edit_after_the_write_back_still_reloads()
    {
        _plugin.SaveOnConfigSetStuck = true;
        var entry = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        int callbacks = 0;
        ConfigHelper.Reloaded += _ => callbacks++;
        var watch = new ConfigHelper.TestWatch(_plugin);

        File.WriteAllText(_plugin.ConfigFilePath, "[Watch]  Verbosity=Loud");
        watch.FireDebounced();
        watch.FireDebounced();
        Assert.Equal(1, callbacks);

        File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");
        watch.FireDebounced();

        Assert.Equal("Quiet", entry.Value);
        Assert.Equal(2, _plugin.ReloadCount);
        Assert.Equal(2, callbacks);
    }

    [Fact]
    public void An_edit_that_lands_while_the_Reloaded_callbacks_run_is_not_taken_for_our_own_write()
    {
        _plugin.SaveOnConfigSetStuck = true;
        var entry = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        int callbacks = 0;
        bool raced = false;
        ConfigHelper.Reloaded += _ =>
        {
            callbacks++;
            if (raced) return;
            raced = true;
            // An operator saves the file while a subscriber is still working. Recording the
            // write-back hash after the callbacks would swallow this edit for good.
            File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");
        };
        var watch = new ConfigHelper.TestWatch(_plugin);

        File.WriteAllText(_plugin.ConfigFilePath, "[Watch]  Verbosity=Loud");
        watch.FireDebounced();
        Assert.Equal("Loud", entry.Value);

        watch.FireDebounced();

        Assert.Equal("Quiet", entry.Value);
        Assert.Equal(2, callbacks);
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_never_taken_for_our_own_write()
    {
        _plugin.SaveOnConfigSetStuck = true;
        var entry = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        ConfigHelper.Reloaded += _ => { };
        var watch = new ConfigHelper.TestWatch(_plugin);

        File.WriteAllText(_plugin.ConfigFilePath, "[Watch]  Verbosity=Loud");
        watch.FireDebounced();
        Assert.Equal("Loud", entry.Value);

        // Fail open: an unreadable file hashes to null, which never equals the recorded write-back.
        File.Delete(_plugin.ConfigFilePath);
        watch.FireDebounced();

        Assert.Equal(2, _plugin.ReloadCount);
    }

    // ---- FCFG9 (R64 #1): the filter is armed only by a real write and does not outlive it -----
    //
    // The hash is the bytes of *our own write-back*. Two things follow, and neither held before:
    // a reload that wrote nothing has no write-back to ignore (ConfigSync holds SaveOnConfigSet
    // false for a whole ConfigFile while a synced slot on it is locked, and that is Core's own
    // doing), and a match arriving later than that write-back's notification possibly could is
    // somebody saving those bytes on purpose.

    [Fact]
    public void A_reload_that_wrote_nothing_arms_no_self_write_filter()
    {
        // The shape ConfigSync puts a client's .cfg in for the duration of a synced-config hold.
        _plugin.SaveOnConfigSet = false;
        var entry = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        int callbacks = 0;
        ConfigHelper.Reloaded += _ => callbacks++;
        var watch = new ConfigHelper.TestWatch(_plugin);

        const string edited = "[Watch]  Verbosity=Loud";
        File.WriteAllText(_plugin.ConfigFilePath, edited);
        watch.FireDebounced();

        Assert.Equal("Loud", entry.Value);
        Assert.Equal(1, _plugin.ReloadCount);
        Assert.Equal(1, callbacks);
        // No write-back happened: the bytes on disk are still the operator's own.
        Assert.Equal(edited, File.ReadAllText(_plugin.ConfigFilePath));
        // ... so there is nothing to ignore, and nothing is armed.
        Assert.Null(watch.SelfWriteHash);

        // The value moves in memory while the file does not — a ConfigSync apply, or a mod
        // assigning Value. The operator saves the same bytes again to force it back.
        entry.Value = "Quiet";
        File.WriteAllText(_plugin.ConfigFilePath, edited);
        watch.FireDebounced();

        Assert.Equal(2, _plugin.ReloadCount);
        Assert.Equal(2, callbacks);
        Assert.Equal("Loud", entry.Value);
    }

    [Fact]
    public void Identical_bytes_saved_after_the_write_back_was_swallowed_still_reload()
    {
        _plugin.SaveOnConfigSetStuck = true;
        var entry = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        int callbacks = 0;
        ConfigHelper.Reloaded += _ => callbacks++;
        var watch = new ConfigHelper.TestWatch(_plugin);

        File.WriteAllText(_plugin.ConfigFilePath, "[Watch]  Verbosity=Loud");
        watch.FireDebounced();
        var writtenBack = File.ReadAllText(_plugin.ConfigFilePath);
        Assert.NotNull(watch.SelfWriteHash);

        // The notification that write-back raised, swallowed as it should be.
        watch.FireDebounced();
        Assert.Equal(1, _plugin.ReloadCount);
        Assert.Equal(1, callbacks);

        // Now the value moves away from the file (here by a plain assignment, which under the
        // default SaveOnConfigSet also rewrites the file) and the operator puts the earlier
        // content back — the "edit it back to force it" move R64 #1 describes. Seconds have
        // passed, which is all the expiry needs.
        entry.Value = "Quiet";
        watch.ExpireSelfWriteWindow();
        File.WriteAllText(_plugin.ConfigFilePath, writtenBack);
        watch.FireDebounced();

        Assert.Equal(2, _plugin.ReloadCount);
        Assert.Equal(2, callbacks);
        Assert.Equal("Loud", entry.Value);
    }

    [Fact]
    public void An_expired_write_back_hash_is_dropped_rather_than_matched_again()
    {
        _plugin.SaveOnConfigSetStuck = true;
        var entry = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        ConfigHelper.Reloaded += _ => { };
        var watch = new ConfigHelper.TestWatch(_plugin);

        File.WriteAllText(_plugin.ConfigFilePath, "[Watch]  Verbosity=Loud");
        watch.FireDebounced();
        var writtenBack = File.ReadAllText(_plugin.ConfigFilePath);
        Assert.Equal("Loud", entry.Value);

        // An expired match reloads, and that reload writes nothing (every value is already the
        // one on disk), so the filter is left disarmed rather than re-armed on the same bytes.
        watch.ExpireSelfWriteWindow();
        watch.FireDebounced();

        Assert.Equal(2, _plugin.ReloadCount);
        Assert.Equal(writtenBack, File.ReadAllText(_plugin.ConfigFilePath));
        Assert.Null(watch.SelfWriteHash);
    }

    // ---- FCFG10 (R65 #1, #6): arming means *our* write-back, and the window is the debounce's ---
    //
    // "The bytes changed across Reload()" is not the same claim as "the reload wrote". Reload()
    // runs every SettingChanged subscriber synchronously, so the span it covers includes other
    // people's writes; on a file ConfigSync is holding at SaveOnConfigSet = false it includes
    // nothing else, because our side cannot write there at all. The flag is read before Reload()
    // and required for the arm.

    [Fact]
    public void An_operator_save_that_lands_during_a_save_off_reload_is_not_armed_as_ours()
    {
        // The shape ConfigSync puts a client's .cfg in while a synced slot on it is locked.
        _plugin.SaveOnConfigSet = false;
        var entry = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        int callbacks = 0;
        ConfigHelper.Reloaded += _ => callbacks++;

        // An operator's save landing *inside* Reload(), modeled the way it reaches us: a
        // subscriber the reload runs while it is walking the file. Reload has already read the
        // file by then, so these bytes are not the ones this reload applies — they are the ones
        // the next notification is for.
        const string operatorSave = "[Watch] Verbosity = Quiet";
        bool wroteOnce = false;
        EventHandler<SettingChangedEventArgs> duringReload = (_, _) =>
        {
            if (wroteOnce) return;
            wroteOnce = true;
            File.WriteAllText(_plugin.ConfigFilePath, operatorSave);
        };
        _plugin.SettingChanged += duringReload;
        var watch = new ConfigHelper.TestWatch(_plugin);

        File.WriteAllText(_plugin.ConfigFilePath, "[Watch]  Verbosity=Loud");
        watch.FireDebounced();

        Assert.True(wroteOnce);
        Assert.Equal("Loud", entry.Value);
        Assert.Equal(1, _plugin.ReloadCount);
        Assert.Equal(1, callbacks);
        Assert.Equal(operatorSave, File.ReadAllText(_plugin.ConfigFilePath));
        // The bytes moved across Reload(), but not because we wrote them: this file cannot
        // write back at all. Arming here is what swallowed the save.
        Assert.Null(watch.SelfWriteHash);

        // The notification for that save.
        watch.FireDebounced();

        Assert.Equal("Quiet", entry.Value);
        Assert.Equal(2, _plugin.ReloadCount);
        Assert.Equal(2, callbacks);
    }

    [Fact]
    public void The_self_write_window_leaves_slack_for_the_restartable_debounce()
    {
        // Schedule() restarts the debounce on every notification, so the arm written in one
        // debounced reload cannot be read before arm + one interval. A window of two intervals
        // therefore had one of slack, and any notification inside that second pushed the read
        // past it — a spurious reload plus a log line about an editor that does not exist.
        Assert.Equal(1.0, ConfigHelper.DebounceSeconds);
        Assert.True(
            ConfigHelper.SelfWriteWindowSeconds >= 3.0 * ConfigHelper.DebounceSeconds,
            "the self-write window must outlast the debounce interval it is measured across, "
            + "plus slack for restarts: window=" + ConfigHelper.SelfWriteWindowSeconds
            + ", debounce=" + ConfigHelper.DebounceSeconds);
    }

    [Fact]
    public void The_expiry_line_does_not_claim_somebody_saved_the_file_deliberately()
    {
        var log = new TestLogSource();
        var previous = Plugin.Logger;
        Plugin.Logger = log;
        try
        {
            _plugin.SaveOnConfigSetStuck = true;
            var entry = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
            ConfigHelper.Reloaded += _ => { };
            var watch = new ConfigHelper.TestWatch(_plugin);

            File.WriteAllText(_plugin.ConfigFilePath, "[Watch]  Verbosity=Loud");
            watch.FireDebounced();
            Assert.Equal("Loud", entry.Value);
            Assert.NotNull(watch.SelfWriteHash);

            watch.ExpireSelfWriteWindow();
            watch.FireDebounced();
            Assert.Equal(2, _plugin.ReloadCount);

            string? expiry = null;
            foreach (var line in log.Infos)
            {
                if (!line.Contains("write-back window has passed", StringComparison.Ordinal)) continue;
                Assert.Null(expiry);
                expiry = line;
            }

            Assert.NotNull(expiry);
            Assert.Contains("reloading it", expiry!, StringComparison.Ordinal);
            // The one case the window's slack makes reachable is our own echo delayed by our own
            // restartable debounce, where nobody saved anything.
            Assert.DoesNotContain("was saved", expiry!, StringComparison.Ordinal);
        }
        finally
        {
            Plugin.Logger = previous;
        }
    }

    // ---- FCFG11: the watch reloads the LIVE ConfigFile instances, not the first one seen -------
    //
    // BepInEx builds a new ConfigFile for the same GUID.cfg in every BasePlugin constructor
    // (BasePlugin.Config = new ConfigFile(Paths.ConfigPath/GUID.cfg, ...), read out of
    // BepInEx.Unity.IL2CPP.dll), so a plugin hot reload inside one Core generation replaces the
    // object behind a path. Evidence 222: after the Sample was reloaded without a Core swap, every
    // watch reload printed the OLD instance's values as unchanged while the disk held the
    // operator's edit, and the Sample's own ctx.Config.File.Reload() read the file correctly.
    //
    // Two plugin instances are modeled as two ConfigFile objects over one path, in the order a
    // reload creates them: the first binds, the first is unloaded, the second binds.

    private ConfigFile SecondInstanceOf(ConfigFile file) =>
        new ConfigFile(file.ConfigFilePath, saveOnInit: false);

    [Fact]
    public void The_watch_reloads_the_second_plugin_instance_and_not_the_unloaded_one()
    {
        var first = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        var watch = new ConfigHelper.TestWatch(_plugin);

        // The plugin is hot-reloaded: the old instance goes, a new ConfigFile for the same path
        // arrives and binds through ConfigHelper exactly as the old one did.
        var reloadedPlugin = SecondInstanceOf(_plugin);
        ConfigHelper.DropWatchedFile(_plugin);
        var second = ConfigHelper.Bind(reloadedPlugin, "Watch", "Verbosity", "Normal", "");

        var seen = new List<ConfigFile>();
        ConfigHelper.Reloaded += f => seen.Add(f);

        File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");
        watch.FireDebounced();

        // The live instance is the one that learns the operator's edit.
        Assert.Equal("Quiet", second.Value);
        Assert.Equal(1, reloadedPlugin.ReloadCount);
        Assert.Same(reloadedPlugin, Assert.Single(seen));

        // The unloaded instance is never touched again — before FCFG11 it was the only one that
        // was, which is what evidence 222 recorded.
        Assert.Equal("Normal", first.Value);
        Assert.Equal(0, _plugin.ReloadCount);
    }

    [Fact]
    public void An_instance_nobody_dropped_is_reloaded_alongside_the_new_one()
    {
        // The same reload with no teardown call at all: Core must not need the plugin's help to
        // get the operator's edit into the live instance, because a plugin that crashed on unload
        // gives none.
        //
        // FCFG12 (R68 #8): the old instance is not evicted for being older. Nothing available here
        // can tell "the previous plugin instance, not yet collected" from "a second live owner of
        // one file", which the public Bind/Watch overloads permit — so both are reloaded. That is
        // the answer that is right for both: a reload makes an instance agree with the disk, so the
        // stale write-back the old instance was carrying is disarmed rather than merely ignored.
        var first = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        var watch = new ConfigHelper.TestWatch(_plugin);

        var reloadedPlugin = SecondInstanceOf(_plugin);
        var second = ConfigHelper.Bind(reloadedPlugin, "Watch", "Verbosity", "Normal", "");

        Assert.Equal(2, watch.LiveFiles.Count);
        Assert.Same(_plugin, watch.LiveFiles[0]);
        Assert.Same(reloadedPlugin, watch.LiveFiles[1]);

        var seen = new List<ConfigFile>();
        ConfigHelper.Reloaded += f => seen.Add(f);

        File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");
        watch.FireDebounced();

        Assert.Equal("Quiet", second.Value);
        Assert.Equal(1, reloadedPlugin.ReloadCount);

        // The instance nobody dropped is reloaded too.
        Assert.Equal("Quiet", first.Value);
        Assert.Equal(1, _plugin.ReloadCount);

        // R69 #4: Reloaded is raised once per reload, not once per instance, and the instance it
        // hands over is the newest. Two instances of one path are indistinguishable to a
        // subscriber — same ConfigFilePath, nothing else on the object identifies them — so a
        // second callback was a second copy of one fact and made the Sample log two "reload #N"
        // lines for one hand edit.
        Assert.Same(reloadedPlugin, Assert.Single(seen));
    }

    /// <summary>
    /// FCFG12 (R68 #8): two live owners of one path — which <see cref="ConfigHelper.Bind{T}"/> and
    /// <see cref="ConfigHelper.Watch"/> both permit, since they take any <c>ConfigFile</c>. Under
    /// FCFG11's supersede rule the first owner was evicted from the watch permanently while staying
    /// live with <c>SaveOnConfigSet = true</c>, so its next <c>Save</c> wrote its stale values back
    /// over the operator's edit: FCFG2's revert by a third route.
    /// </summary>
    [Fact]
    public void Two_live_owners_of_one_path_both_learn_the_edit_so_neither_save_reverts_it()
    {
        var suitePath = Path.Combine(_dir, "suite.shared.cfg");
        var ownerA = new ConfigFile(suitePath, saveOnInit: true);
        var ownerB = new ConfigFile(suitePath, saveOnInit: false);

        var a = ConfigHelper.Bind(ownerA, "Watch", "Verbosity", "Normal", "");
        var b = ConfigHelper.Bind(ownerB, "Watch", "Verbosity", "Normal", "");
        var watch = new ConfigHelper.TestWatch(ownerA);

        Assert.Equal(2, watch.LiveFiles.Count);

        File.WriteAllText(suitePath, "[Watch] Verbosity = Quiet");
        watch.FireDebounced();

        Assert.Equal("Quiet", a.Value);
        Assert.Equal("Quiet", b.Value);

        // The owner the old rule evicted is the one whose Save used to land the revert.
        ownerA.Save();
        Assert.Contains("Quiet", File.ReadAllText(suitePath), StringComparison.Ordinal);
        Assert.DoesNotContain("Normal", File.ReadAllText(suitePath), StringComparison.Ordinal);
    }

    /// <summary>
    /// The line a path gets when it gains a second instance. It names both objects because the two
    /// situations that produce it (an undropped hot reload, two live owners) need different answers
    /// and only the plugin author can tell them apart — and it is printed once per instance, not
    /// once per <c>Bind</c>.
    /// </summary>
    [Fact]
    public void A_path_that_gains_a_second_instance_says_so_once_and_names_both()
    {
        var log = new TestLogSource();
        var previous = Plugin.Logger;
        Plugin.Logger = log;
        try
        {
            ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
            ConfigHelper.Bind(_plugin, "Watch", "Enabled", true, "");
            Assert.DoesNotContain(log.Infos, l => l.Contains("registered ConfigFile instances", StringComparison.Ordinal));

            var reloadedPlugin = SecondInstanceOf(_plugin);
            ConfigHelper.Bind(reloadedPlugin, "Watch", "Verbosity", "Normal", "");
            ConfigHelper.Bind(reloadedPlugin, "Watch", "Enabled", true, "");
            ConfigHelper.Watch(reloadedPlugin);

            var lines = new List<string>();
            foreach (var line in log.Infos)
            {
                if (line.Contains("registered ConfigFile instances", StringComparison.Ordinal)) lines.Add(line);
            }

            var only = Assert.Single(lines);
            Assert.Contains(_plugin.ConfigFilePath + " now has 2 registered", only, StringComparison.Ordinal);
            Assert.Contains(
                "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_plugin).ToString(),
                only,
                StringComparison.Ordinal);
            Assert.Contains(
                "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(reloadedPlugin).ToString() + " (1 key(s)) (new)",
                only,
                StringComparison.Ordinal);
            Assert.Contains("DropWatchedFile", only, StringComparison.Ordinal);
        }
        finally
        {
            Plugin.Logger = previous;
        }
    }

    /// <summary>
    /// The multi-instance reload path is not a set of dead branches (R68 #8 counted three): with two
    /// instances the log line names the count, and a throwing instance does not stop the others from
    /// being reloaded.
    /// </summary>
    [Fact]
    public void A_throwing_instance_does_not_leave_the_others_holding_the_pre_edit_values()
    {
        var log = new TestLogSource();
        var previous = Plugin.Logger;
        Plugin.Logger = log;
        try
        {
            var first = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
            var watch = new ConfigHelper.TestWatch(_plugin);

            var reloadedPlugin = SecondInstanceOf(_plugin);
            var second = ConfigHelper.Bind(reloadedPlugin, "Watch", "Verbosity", "Normal", "");

            // The older instance is the one that throws, so a fail-fast loop would never reach the
            // live one.
            _plugin.ReloadThrows = true;

            File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");
            watch.FireDebounced();

            Assert.Equal("Normal", first.Value);
            Assert.Equal("Quiet", second.Value);
            Assert.Contains(
                log.Errors,
                l => l.Contains("registered ConfigFile instance(s)", StringComparison.Ordinal));
            Assert.Contains(
                log.Infos,
                l => l.Contains("reloaded " + _plugin.ConfigFilePath + " into 1 of 2 registered ConfigFile instances", StringComparison.Ordinal));
        }
        finally
        {
            _plugin.ReloadThrows = false;
            Plugin.Logger = previous;
        }
    }

    /// <summary>
    /// Mirror of the fact above: the <b>newest</b> registered instance throws and an older one
    /// loads. Reloaded is handed the newest instance that loaded, never the one that failed.
    /// </summary>
    [Fact]
    public void A_throwing_newest_instance_is_never_the_Reloaded_argument()
    {
        var log = new TestLogSource();
        var previous = Plugin.Logger;
        Plugin.Logger = log;
        var newest = SecondInstanceOf(_plugin);
        try
        {
            var first = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
            var watch = new ConfigHelper.TestWatch(_plugin);
            var second = ConfigHelper.Bind(newest, "Watch", "Verbosity", "Normal", "");
            Assert.Same(newest, watch.LiveFiles[watch.LiveFiles.Count - 1]);
            newest.ReloadThrows = true;

            var seen = new List<ConfigFile>();
            ConfigHelper.Reloaded += f => seen.Add(f);

            File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");
            watch.FireDebounced();

            Assert.Equal("Quiet", first.Value);
            Assert.Equal("Normal", second.Value);
            Assert.Single(seen);
            Assert.Same(_plugin, seen[0]);
            Assert.Contains(
                log.Infos,
                l => l.Contains("into 1 of 2 registered ConfigFile instances", StringComparison.Ordinal));
        }
        finally
        {
            newest.ReloadThrows = false;
            Plugin.Logger = previous;
        }
    }

    /// <summary>
    /// When every registered instance's Reload throws, the path does not agree with the disk:
    /// no Reloaded, no success line, an error that says so, and exactly one retry posted.
    /// </summary>
    [Fact]
    public void When_every_Reload_throws_Reloaded_is_not_raised_and_one_retry_is_posted()
    {
        MainThread.ResetForTests();
        ConfigHelper.SetDebounceForTests(0.05);
        var log = new TestLogSource();
        var previous = Plugin.Logger;
        Plugin.Logger = log;
        try
        {
            var entry = _plugin.Bind("Watch", "Verbosity", "Normal", "");
            var watch = new ConfigHelper.TestWatch(_plugin);
            int callbacks = 0;
            ConfigHelper.Reloaded += _ => callbacks++;

            _plugin.ReloadThrows = true;
            File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");
            watch.FireDebounced();

            Assert.Equal("Normal", entry.Value);
            Assert.Equal(0, callbacks);
            Assert.Contains(
                log.Errors,
                e => e.Contains("could be reloaded", StringComparison.Ordinal)
                    && e.Contains("Reloaded was not raised", StringComparison.Ordinal));
            Assert.DoesNotContain(
                log.Infos,
                i => i.Contains("ConfigHelper: reloaded " + _plugin.ConfigFilePath, StringComparison.Ordinal));
            Assert.Null(watch.SelfWriteHash);
            MainThread.LeftoverCounts(out _, out int delayed);
            Assert.Equal(1, delayed);
        }
        finally
        {
            _plugin.ReloadThrows = false;
            Plugin.Logger = previous;
            ConfigHelper.SetDebounceForTests(1.0);
            MainThread.ResetForTests();
        }
    }

    /// <summary>
    /// The retry recovers a transient failure (an editor's exclusive save), and is capped: a
    /// persistent failure gets one retry, which fails and posts nothing further.
    /// </summary>
    [Fact]
    public void An_all_threw_reload_retries_once_through_the_debounce_then_stops()
    {
        MainThread.ResetForTests();
        ConfigHelper.SetDebounceForTests(0.05);
        try
        {
            var entry = _plugin.Bind("Watch", "Verbosity", "Normal", "");
            var watch = new ConfigHelper.TestWatch(_plugin);
            int callbacks = 0;
            ConfigHelper.Reloaded += _ => callbacks++;

            // Transient: the throw clears before the retry comes due.
            _plugin.ReloadThrows = true;
            File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");
            watch.FireDebounced();
            Assert.Equal(0, callbacks);
            _plugin.ReloadThrows = false;
            Thread.Sleep(TimeSpan.FromSeconds(ConfigHelper.DebounceSeconds + 0.05));
            MainThread.Drain();
            Assert.Equal(1, callbacks);
            Assert.Equal("Quiet", entry.Value);

            // Persistent: the success above refilled the budget, so one retry, then nothing.
            callbacks = 0;
            int reloads = _plugin.ReloadCount;
            _plugin.ReloadThrows = true;
            File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Loud");
            watch.FireDebounced();
            MainThread.LeftoverCounts(out _, out int afterFirst);
            Assert.Equal(1, afterFirst);
            Thread.Sleep(TimeSpan.FromSeconds(ConfigHelper.DebounceSeconds + 0.05));
            MainThread.Drain();
            MainThread.LeftoverCounts(out _, out int afterRetry);
            Assert.Equal(0, afterRetry);
            Assert.Equal(0, callbacks);
            Assert.Equal(reloads, _plugin.ReloadCount);
            Assert.Equal("Quiet", entry.Value);
        }
        finally
        {
            _plugin.ReloadThrows = false;
            ConfigHelper.SetDebounceForTests(1.0);
            MainThread.ResetForTests();
        }
    }

    /// <summary>
    /// The retry budget is per external notification: a new save after a capped failure gets its
    /// own single retry, but the retry's own re-schedule never refills it.
    /// </summary>
    [Fact]
    public void Each_external_notification_gets_one_retry_and_no_more()
    {
        MainThread.ResetForTests();
        ConfigHelper.SetDebounceForTests(0.05);
        try
        {
            _plugin.Bind("Watch", "Verbosity", "Normal", "");
            var watch = new ConfigHelper.TestWatch(_plugin);
            _plugin.ReloadThrows = true;
            File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");

            for (int episode = 0; episode < 2; episode++)
            {
                watch.NotifyChanged();
                Thread.Sleep(TimeSpan.FromSeconds(ConfigHelper.DebounceSeconds + 0.05));
                MainThread.Drain(); // the debounced reload: all threw, one retry posted
                MainThread.LeftoverCounts(out _, out int retry);
                Assert.Equal(1, retry);

                Thread.Sleep(TimeSpan.FromSeconds(ConfigHelper.DebounceSeconds + 0.05));
                MainThread.Drain(); // the retry: all threw, capped
                MainThread.LeftoverCounts(out _, out int capped);
                Assert.Equal(0, capped);
            }
            Assert.Equal(0, _plugin.ReloadCount);
        }
        finally
        {
            _plugin.ReloadThrows = false;
            ConfigHelper.SetDebounceForTests(1.0);
            MainThread.ResetForTests();
        }
    }

    /// <summary>
    /// Collection is the second way out of the registry, and the one an unloaded plugin that called
    /// nothing takes: once no owner holds the object, the watch stops reloading it without anyone
    /// asking.
    /// </summary>
    [Fact]
    public void A_collected_instance_leaves_the_registry_without_a_drop()
    {
        var watch = MakeWatchWithACollectableInstance(out var live);

        // Through a helper, and never into a local of this frame: LiveFiles hands back a list of
        // strong references, and a Debug build keeps a local (or its temporary) rooted to the end
        // of the method, which would root the very instance this test needs collected.
        Assert.Equal(2, CountLive(watch));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.Same(live, Assert.Single(watch.LiveFiles));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CountLive(ConfigHelper.TestWatch watch) => watch.LiveFiles.Count;

    /// <summary>
    /// The collectable instance is created (and dropped) inside a method of its own so no local of
    /// the test frame keeps it alive; the JIT is free to keep a local rooted for the whole frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ConfigHelper.TestWatch MakeWatchWithACollectableInstance(out ConfigFile live)
    {
        var doomed = SecondInstanceOf(_plugin);
        ConfigHelper.Bind(doomed, "Watch", "Verbosity", "Normal", "");
        live = SecondInstanceOf(_plugin);
        ConfigHelper.Bind(live, "Watch", "Verbosity", "Normal", "");
        return new ConfigHelper.TestWatch(live);
    }

    [Fact]
    public void One_instance_binding_many_keys_is_registered_once()
    {
        ConfigHelper.Bind(_plugin, "Watch", "Enabled", true, "");
        ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        ConfigHelper.Watch(_plugin);
        var watch = new ConfigHelper.TestWatch(_plugin);

        Assert.Same(_plugin, Assert.Single(watch.LiveFiles));

        File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");
        watch.FireDebounced();

        // One reload, not one per Bind: a second registration of the same object would reload it
        // again and raise Reloaded again for a file that had already been read.
        Assert.Equal(1, _plugin.ReloadCount);
    }

    [Fact]
    public void An_instance_dropped_on_unload_is_never_reloaded_again()
    {
        var log = new TestLogSource();
        var previous = Plugin.Logger;
        Plugin.Logger = log;
        try
        {
            var entry = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
            var watch = new ConfigHelper.TestWatch(_plugin);
            int callbacks = 0;
            ConfigHelper.Reloaded += _ => callbacks++;

            Assert.True(ConfigHelper.DropWatchedFile(_plugin));
            Assert.False(ConfigHelper.DropWatchedFile(_plugin));
            Assert.Empty(watch.LiveFiles);

            // The window between the unload and the next instance's first Bind. Loading the edit
            // into the dead instance here is what makes BepInEx write its stale values back.
            File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");
            watch.FireDebounced();
            watch.FireDebounced();

            Assert.Equal(0, _plugin.ReloadCount);
            Assert.Equal("Normal", entry.Value);
            Assert.Equal(0, callbacks);
            Assert.Equal("[Watch] Verbosity = Quiet", File.ReadAllText(_plugin.ConfigFilePath));

            int said = 0;
            foreach (var line in log.Infos)
            {
                if (line.Contains("no live ConfigFile is registered", StringComparison.Ordinal)) said++;
            }
            // One line per empty spell, not one per notification.
            Assert.Equal(1, said);
        }
        finally
        {
            Plugin.Logger = previous;
        }
    }

    [Fact]
    public void The_live_instance_does_not_write_the_unloaded_instance_values_back_over_the_edit()
    {
        // FCFG2's silent revert by the FCFG11 route. The old instance holds Loud; the operator
        // edits the file to Quiet; the watch fires. Whichever instance the watch loaded, the next
        // Save on the LIVE object is what lands on disk — so if the live one never saw the edit,
        // the operator's value is gone as soon as anything saves.
        var first = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        first.Value = "Loud";
        var watch = new ConfigHelper.TestWatch(_plugin);

        var reloadedPlugin = SecondInstanceOf(_plugin);
        ConfigHelper.DropWatchedFile(_plugin);
        var second = ConfigHelper.Bind(reloadedPlugin, "Watch", "Verbosity", "Loud", "");

        File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");
        watch.FireDebounced();

        reloadedPlugin.Save();
        Assert.Contains("Quiet", File.ReadAllText(_plugin.ConfigFilePath), StringComparison.Ordinal);
        Assert.DoesNotContain("Loud", File.ReadAllText(_plugin.ConfigFilePath), StringComparison.Ordinal);
        Assert.Equal("Quiet", second.Value);
    }

    // ---- FCFG13: Core drops the plugin's ConfigFile when its context is torn down --------------
    //
    // FCFG12 left DropWatchedFile entirely to the plugin author, so an instance was dropped
    // promptly only where somebody had remembered to write the line. PluginContext.Attach now
    // tracks the handle below, and PluginReloader.SweepRegistrations repeats the drop for a plugin
    // whose UndoAll never ran. PluginContext and PluginReloader are not compiled into this
    // game-free suite (they bind BepInEx.Unity.IL2CPP and Harmony), so the call sites are pinned in
    // PluginContextContractTests; what is exercised here is the handle itself.

    [Fact]
    public void A_context_teardown_drops_its_file_without_the_plugin_calling_anything()
    {
        var entry = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        var watch = new ConfigHelper.TestWatch(_plugin);
        var teardown = ConfigHelper.DropWatchedFileOnUnload(_plugin);

        Assert.Same(_plugin, Assert.Single(watch.LiveFiles));

        // The whole unload: nothing in the plugin calls DropWatchedFile.
        teardown.Dispose();

        Assert.Empty(watch.LiveFiles);

        File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");
        watch.FireDebounced();

        // Not reloaded, so the unloaded instance cannot write its pre-edit values back and its
        // SettingChanged handlers do not run.
        Assert.Equal(0, _plugin.ReloadCount);
        Assert.Equal("Normal", entry.Value);
        Assert.Equal("[Watch] Verbosity = Quiet", File.ReadAllText(_plugin.ConfigFilePath));
    }

    [Fact]
    public void The_teardown_drop_and_a_plugins_own_call_are_the_same_drop_in_either_order()
    {
        // The Sample's SampleConfig.Detach still calls DropWatchedFile, and Detach runs from
        // OnUnload — ahead of UndoAll — so in the live ordering the plugin's call is the first drop
        // and Core's is the no-op second. The reverse order costs nothing either.
        ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        var watch = new ConfigHelper.TestWatch(_plugin);
        var teardown = ConfigHelper.DropWatchedFileOnUnload(_plugin);

        Assert.True(ConfigHelper.DropWatchedFile(_plugin));
        teardown.Dispose();
        teardown.Dispose();
        Assert.Empty(watch.LiveFiles);

        var second = SecondInstanceOf(_plugin);
        ConfigHelper.Bind(second, "Watch", "Verbosity", "Normal", "");
        var secondTeardown = ConfigHelper.DropWatchedFileOnUnload(second);

        secondTeardown.Dispose();
        Assert.False(ConfigHelper.DropWatchedFile(second));
        Assert.Empty(watch.LiveFiles);
    }

    [Fact]
    public void The_teardown_drop_takes_only_the_instance_it_was_made_for()
    {
        // Two live owners of one path (FCFG12's second case). One context going away must not
        // deregister the other's ConfigFile, or the survivor stops learning the operator's edits.
        var mine = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        var otherFile = SecondInstanceOf(_plugin);
        var theirs = ConfigHelper.Bind(otherFile, "Watch", "Verbosity", "Normal", "");
        var watch = new ConfigHelper.TestWatch(_plugin);

        ConfigHelper.DropWatchedFileOnUnload(_plugin).Dispose();

        Assert.Same(otherFile, Assert.Single(watch.LiveFiles));

        File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");
        watch.FireDebounced();

        Assert.Equal("Quiet", theirs.Value);
        Assert.Equal("Normal", mine.Value);
    }

    [Fact]
    public void A_teardown_drop_for_a_file_that_was_never_watched_is_harmless()
    {
        // A plugin whose OnLoad threw before it bound anything: the context is torn down all the
        // same, and the handle must not throw into somebody's unload.
        var never = SecondInstanceOf(_plugin);
        var teardown = ConfigHelper.DropWatchedFileOnUnload(never);
        teardown.Dispose();

        Assert.Null(Record.Exception(() => ConfigHelper.DropWatchedFileOnUnload(null).Dispose()));
    }

    // ---- R69 #3: a reload reads the file and does not write it --------------------------------
    //
    // ConfigFile.Reload assigns only the keys the file still names; ConfigFile.Save writes every
    // entry. FCFG12's loop reloads the registered instances oldest first, and the oldest is
    // normally the dead plugin generation's — so its write-back landed before the live instance
    // read, and a key the operator deleted came back at the dead generation's value. The watch now
    // holds SaveOnConfigSet off across the whole loop.

    /// <summary>
    /// The scenario R69 #3 is about, in the shape an operator produces it: change one key and
    /// <b>delete</b> another to get its default back. Without the hold, instance A (the dead
    /// generation, registered first) reloads first, its assignment of the changed key saves the
    /// whole file, the deleted key is rewritten at A's stale value, and the live instance then
    /// loads that value instead of the operator's file.
    /// </summary>
    [Fact]
    public void A_key_the_operator_deleted_is_not_re_injected_by_the_oldest_instance()
    {
        var deadVerbosity = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        ConfigHelper.Bind(_plugin, "Watch", "Enabled", true, "");

        var livePlugin = SecondInstanceOf(_plugin);
        var liveVerbosity = ConfigHelper.Bind(livePlugin, "Watch", "Verbosity", "Normal", "");
        var liveEnabled = ConfigHelper.Bind(livePlugin, "Watch", "Enabled", true, "");
        var watch = new ConfigHelper.TestWatch(_plugin);

        Assert.Same(_plugin, watch.LiveFiles[0]);
        Assert.Same(livePlugin, watch.LiveFiles[1]);

        // The dead generation drifted away from the live one before it was unloaded.
        deadVerbosity.Value = "Loud";

        // The hand edit: Enabled flipped, and the Verbosity line deleted.
        const string edited = "[Watch] Enabled = False";
        File.WriteAllText(_plugin.ConfigFilePath, edited);
        watch.FireDebounced();

        Assert.False(liveEnabled.Value);
        Assert.Equal("Normal", liveVerbosity.Value);
        Assert.Equal(edited, File.ReadAllText(_plugin.ConfigFilePath));
        Assert.DoesNotContain("Verbosity", File.ReadAllText(_plugin.ConfigFilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_reload_leaves_the_operators_bytes_on_disk()
    {
        var entry = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        var watch = new ConfigHelper.TestWatch(_plugin);

        // Deliberately not in the shape Save writes it back in, so a write-back would show.
        const string edited = "[Watch]  Verbosity=Loud";
        File.WriteAllText(_plugin.ConfigFilePath, edited);
        int savesBefore = _plugin.SaveCount;
        watch.FireDebounced();

        Assert.Equal("Loud", entry.Value);
        Assert.Equal(edited, File.ReadAllText(_plugin.ConfigFilePath));
        Assert.Equal(savesBefore, _plugin.SaveCount);
        // Nothing was written, so there is no write-back to filter.
        Assert.Null(watch.SelfWriteHash);

        // The flag is put back, so an ordinary Value write saves exactly as it did before.
        Assert.True(_plugin.SaveOnConfigSet);
        entry.Value = "Quiet";
        Assert.Equal(savesBefore + 1, _plugin.SaveCount);
    }

    [Fact]
    public void A_file_ConfigSync_is_holding_save_off_is_still_save_off_after_the_reload()
    {
        // The hold restores what it found, not "true": ConfigSync keeps SaveOnConfigSet false for
        // a whole ConfigFile while a synced slot on it is locked, and a reload must not release it.
        _plugin.SaveOnConfigSet = false;
        var entry = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        var watch = new ConfigHelper.TestWatch(_plugin);

        File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");
        watch.FireDebounced();

        Assert.Equal("Quiet", entry.Value);
        Assert.False(_plugin.SaveOnConfigSet);
    }

    [Fact]
    public void A_subscriber_that_turns_saving_back_on_inside_the_reload_keeps_it_on()
    {
        // The restore only puts back what it turned off, and only while it is still off. The file
        // starts save-off (ConfigSync holding a lock on it) and the lock is released inside the
        // reload, so what we found and what the file's owner now wants are opposite: putting back
        // what we found would write over their decision, and this is the one shape that tells the
        // guarded restore from an unconditional one.
        _plugin.SaveOnConfigSet = false;
        ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
        var watch = new ConfigHelper.TestWatch(_plugin);
        _plugin.SettingChanged += (_, _) => _plugin.SaveOnConfigSet = true;

        File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");
        watch.FireDebounced();

        Assert.True(_plugin.SaveOnConfigSet);
    }

    [Fact]
    public void An_instance_whose_hold_does_not_take_is_named_and_left_to_the_self_write_filter()
    {
        var log = new TestLogSource();
        var previous = Plugin.Logger;
        Plugin.Logger = log;
        try
        {
            // The defensive case: the flag was written and did not take. Core reads it back rather
            // than assuming, says so, and falls back to the FCFG8 hash filter for that instance.
            _plugin.SaveOnConfigSetStuck = true;
            var entry = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
            var watch = new ConfigHelper.TestWatch(_plugin);

            File.WriteAllText(_plugin.ConfigFilePath, "[Watch]  Verbosity=Loud");
            watch.FireDebounced();

            Assert.Equal("Loud", entry.Value);
            Assert.NotEqual("[Watch]  Verbosity=Loud", File.ReadAllText(_plugin.ConfigFilePath));
            Assert.NotNull(watch.SelfWriteHash);
            Assert.Contains(
                log.Warnings,
                l => l.Contains("could not hold SaveOnConfigSet off", StringComparison.Ordinal));
        }
        finally
        {
            Plugin.Logger = previous;
        }
    }

    [Fact]
    public void A_hold_that_throws_does_not_stop_the_reload()
    {
        var log = new TestLogSource();
        var previous = Plugin.Logger;
        Plugin.Logger = log;
        try
        {
            var entry = ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
            var watch = new ConfigHelper.TestWatch(_plugin);
            _plugin.SaveOnConfigSetThrows = true;

            File.WriteAllText(_plugin.ConfigFilePath, "[Watch]  Verbosity=Loud");
            watch.FireDebounced();

            Assert.Equal("Loud", entry.Value);
            Assert.Equal(1, _plugin.ReloadCount);
            Assert.Contains(
                log.Warnings,
                l => l.Contains("holding SaveOnConfigSet off", StringComparison.Ordinal)
                    && l.Contains("threw", StringComparison.Ordinal));
        }
        finally
        {
            _plugin.SaveOnConfigSetThrows = false;
            Plugin.Logger = previous;
        }
    }

    // ---- R69 #4: Reloaded is one fact per reload, not one per instance -------------------------

    /// <summary>
    /// The Sample's handler, wired for real: it reads <c>ConfigFilePath</c> off the
    /// <see cref="ConfigFile"/> it is handed and passes it to
    /// <c>SampleConfig.Observe</c>, which is what decides whether a <c>reload #N</c> line is
    /// written. Two registered instances of one path carry the same path, so per-instance dispatch
    /// counted one hand edit twice — the second line reporting every key unchanged, because the
    /// first had already moved the handler's before-values.
    /// </summary>
    [Fact]
    public void One_hand_edit_is_one_Sample_reload_however_many_instances_the_path_has()
    {
        Progenitor.Sample.SampleConfig.Reset();
        try
        {
            ConfigHelper.Bind(_plugin, "Watch", "Verbosity", "Normal", "");
            var livePlugin = SecondInstanceOf(_plugin);
            ConfigHelper.Bind(livePlugin, "Watch", "Verbosity", "Normal", "");
            var watch = new ConfigHelper.TestWatch(_plugin);
            Assert.Equal(2, watch.LiveFiles.Count);

            string ourPath = livePlugin.ConfigFilePath;
            ConfigHelper.Reloaded += f => Progenitor.Sample.SampleConfig.Observe(f.ConfigFilePath, ourPath);

            File.WriteAllText(_plugin.ConfigFilePath, "[Watch] Verbosity = Quiet");
            watch.FireDebounced();

            Assert.Equal(1, Progenitor.Sample.SampleConfig.OurReloads);
            Assert.Equal(0, Progenitor.Sample.SampleConfig.OtherReloads);
        }
        finally
        {
            Progenitor.Sample.SampleConfig.Reset();
        }
    }

    private static Delegate? ReadSettingChanged(ConfigFile file)
    {
        var field = typeof(ConfigFile).GetField(
            "SettingChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(file) as Delegate;
    }
}
