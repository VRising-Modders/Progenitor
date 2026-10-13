using System;
using System.IO;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// FCFG: <c>ctx.Bind</c> can carry an <c>AcceptableValue*</c> on a Synced entry. The bounds stay
/// local metadata — nothing about them is on the wire — so the receiving side has to enforce them
/// itself. These cover the round trip (an in-range server value applies verbatim and is not
/// mentioned), the range clamp (an out-of-range one is stored clamped, with a line naming both the
/// server value and what was actually stored), the list refusal (R40 #8 — element 0 is not a
/// nearest, so the local value stays unless the entry's own default is in the list), and the two
/// other paths that write a server value: the session lock and the re-attach (R40 #7).
///
/// <para>
/// The fake entry here deliberately does <b>not</b> clamp on set, unlike the one in
/// <c>UiSettingsTests</c>: if it did, a passing assert could not tell "ConfigSync clamped" from
/// "BepInEx clamped silently, which is the bug".
/// </para>
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class ConfigSyncBoundsTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigFile _file;

    public ConfigSyncBoundsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-configbounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = new ConfigFile(Path.Combine(_dir, "example.mod.cfg"), saveOnInit: true);
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        ConfigSync.ResetForTests();
        SyncedBounds.ResetCache();
    }

    public void Dispose()
    {
        ConfigHelper.UninstallAll();
        ConfigSync.ResetForTests();
        SyncedBounds.ResetCache();
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void A_bounded_synced_entry_round_trips_an_in_range_server_value()
    {
        var entry = new BoundedEntry(_file, new ConfigDefinition("Sample", "SpawnMultiplier"), typeof(int), 3,
            new FakeRange(1, 10));
        using var reg = ConfigSync.Register("example.mod", entry);

        int applied = ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "Sample", "SpawnMultiplier", "7") },
            snapshot: true);

        Assert.Equal(1, applied);
        Assert.Equal(7, entry.BoxedValue);
        Assert.True(ConfigSync.IsLocked(entry));
        Assert.DoesNotContain(
            ConfigSync.TestLogs,
            line => line.Contains("clamped", StringComparison.Ordinal));

        // Disconnect gives the player's own value back, so the bound value really was a lock and
        // not a write into the local entry.
        ConfigSync.RestoreLocal();
        Assert.Equal(3, entry.BoxedValue);
    }

    [Fact]
    public void A_synced_value_outside_the_local_range_is_clamped_with_a_log_line()
    {
        // The server binds 1..50; this client's own bind says 1..10. Nothing on the wire says so.
        var entry = new BoundedEntry(_file, new ConfigDefinition("Sample", "SpawnMultiplier"), typeof(int), 3,
            new FakeRange(1, 10));
        using var reg = ConfigSync.Register("example.mod", entry);

        int applied = ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "Sample", "SpawnMultiplier", "50") },
            snapshot: true);

        Assert.Equal(1, applied);
        Assert.Equal(10, entry.BoxedValue);
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains("server value '50'", StringComparison.Ordinal)
                    && line.Contains("example.mod/Sample/SpawnMultiplier", StringComparison.Ordinal)
                    && line.Contains("[1 to 10]", StringComparison.Ordinal)
                    && line.Contains("clamped to '10'", StringComparison.Ordinal));
    }

    /// <summary>
    /// R40 #8. A list is an unordered set, so <c>AcceptableValueList.Clamp</c>'s element 0 is not
    /// a "nearest" — for `{Low, Med, High}` and a pushed `Extreme` it answers the *quietest*
    /// setting. With no declared default to fall back on the record is refused outright.
    /// </summary>
    [Fact]
    public void A_synced_value_outside_the_local_list_is_refused_with_the_allowed_set_named()
    {
        var entry = new BoundedEntry(_file, new ConfigDefinition("Sample", "Difficulty"), typeof(string), "High",
            new FakeList("Low", "Med", "High"));
        using var reg = ConfigSync.Register("example.mod", entry);

        int applied = ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "Sample", "Difficulty", "Extreme") },
            snapshot: true);

        Assert.Equal(0, applied);
        Assert.Equal("High", entry.BoxedValue);
        Assert.False(ConfigSync.IsLocked(entry));
        Assert.DoesNotContain(
            ConfigSync.TestLogs,
            line => line.Contains("clamped", StringComparison.Ordinal));
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains("server value 'Extreme'", StringComparison.Ordinal)
                    && line.Contains("one of: Low, Med, High", StringComparison.Ordinal)
                    && line.Contains("keeping the local value", StringComparison.Ordinal));
    }

    /// <summary>
    /// The one list case with a truthful answer: this client's own bind declared a default that is
    /// in the list, so that is what the server value becomes.
    /// </summary>
    [Fact]
    public void A_list_value_falls_back_to_the_entrys_own_default_when_that_default_is_in_the_list()
    {
        var entry = new BoundedEntry(_file, new ConfigDefinition("Sample", "Difficulty"), typeof(string), "High",
            new FakeList("Low", "Med", "High"), defaultValue: "Med");
        using var reg = ConfigSync.Register("example.mod", entry);

        int applied = ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "Sample", "Difficulty", "Extreme") },
            snapshot: true);

        Assert.Equal(1, applied);
        Assert.Equal("Med", entry.BoxedValue);
        Assert.True(ConfigSync.IsLocked(entry));
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains("server value 'Extreme'", StringComparison.Ordinal)
                    && line.Contains("one of: Low, Med, High", StringComparison.Ordinal)
                    && line.Contains("default 'Med'", StringComparison.Ordinal));
    }

    /// <summary>
    /// R40 #7. A push that lands while the plugin is unbound goes through the same gate, so the
    /// session lock never records a value the entry does not hold.
    /// </summary>
    [Fact]
    public void A_push_onto_the_session_lock_is_clamped_and_the_reattach_records_the_clamped_value()
    {
        var entry = new BoundedEntry(_file, new ConfigDefinition("Sample", "SpawnMultiplier"), typeof(int), 3,
            new FakeRange(1, 10));
        var reg = ConfigSync.Register("example.mod", entry);

        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "Sample", "SpawnMultiplier", "7") },
            snapshot: true);
        reg.Dispose();

        // Unbound: the push lands on the session lock.
        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "Sample", "SpawnMultiplier", "50") },
            snapshot: false);
        Assert.Equal(10, entry.BoxedValue);

        // The reload re-binds a fresh entry; the re-attach writes the clamped value and the lock
        // now compares against a number the entry really holds.
        var rebound = new BoundedEntry(_file, new ConfigDefinition("Sample", "SpawnMultiplier"), typeof(int), 3,
            new FakeRange(1, 10));
        using var reg2 = ConfigSync.Register("example.mod", rebound);

        Assert.Equal(10, rebound.BoxedValue);
        Assert.True(ConfigSync.IsLocked(rebound));
        Assert.False(ConfigSync.TryRefuseWrite(rebound, 10));
        Assert.True(ConfigSync.TryRefuseWrite(rebound, 4));
    }

    /// <summary>
    /// R40 #7. The reload may narrow the bounds, and the re-attach writes into that new entry — so
    /// it gates against the new entry, not against the one the session lock captured.
    /// </summary>
    [Fact]
    public void A_reattach_refuses_a_locked_value_the_new_binds_list_does_not_contain()
    {
        var entry = new BoundedEntry(_file, new ConfigDefinition("Sample", "Difficulty"), typeof(string), "High",
            new FakeList("Low", "Med", "High"));
        var reg = ConfigSync.Register("example.mod", entry);

        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "Sample", "Difficulty", "High") },
            snapshot: true);
        Assert.True(ConfigSync.IsLocked(entry));
        reg.Dispose();

        // The reload binds a narrower list that no longer contains the locked value.
        var rebound = new BoundedEntry(_file, new ConfigDefinition("Sample", "Difficulty"), typeof(string), "Low",
            new FakeList("Low", "Med"));
        using var reg2 = ConfigSync.Register("example.mod", rebound);

        Assert.Equal("Low", rebound.BoxedValue);
        Assert.False(ConfigSync.IsLocked(rebound));
        Assert.Contains(
            ConfigSync.TestLogs,
            line => line.Contains("one of: Low, Med", StringComparison.Ordinal)
                    && line.Contains("keeping the local value", StringComparison.Ordinal));
    }

    [Fact]
    public void An_entry_with_no_bounds_is_applied_untouched()
    {
        var plain = ConfigSync.Bind(_file, "example.mod", "Sample", "EmberStack", 20, "stack");

        ConfigSync.ApplyRecords(
            new[] { new ConfigSyncRecord("example.mod", "Sample", "EmberStack", "9999") },
            snapshot: true);

        Assert.Equal(9999, plain.Value);
        Assert.DoesNotContain(
            ConfigSync.TestLogs,
            line => line.Contains("clamped", StringComparison.Ordinal));
    }

    /// <summary>Duck-typed stand-in for <c>AcceptableValueRange&lt;int&gt;</c>.</summary>
    private sealed class FakeRange
    {
        public FakeRange(int min, int max)
        {
            MinValue = min;
            MaxValue = max;
        }

        public object MinValue { get; }

        public object MaxValue { get; }

        public bool IsValid(object value) => (int)value >= (int)MinValue && (int)value <= (int)MaxValue;

        public object Clamp(object value)
        {
            int v = (int)value;
            if (v < (int)MinValue) return MinValue;
            if (v > (int)MaxValue) return MaxValue;
            return value;
        }

        public string ToDescriptionString() => "# Acceptable value range: From " + MinValue + " to " + MaxValue;
    }

    /// <summary>Duck-typed stand-in for <c>AcceptableValueList&lt;string&gt;</c>, clamp included.</summary>
    private sealed class FakeList
    {
        private readonly string[] _values;

        public FakeList(params string[] values) => _values = values;

        public object AcceptableValues => _values;

        public bool IsValid(object value) => Array.IndexOf(_values, value as string) >= 0;

        // Exactly BepInEx: anything invalid becomes element 0, not the nearest.
        public object Clamp(object value) => IsValid(value) ? value : _values[0];

        public string ToDescriptionString() => "# Acceptable values: " + string.Join(", ", _values);
    }

    /// <summary>
    /// A config entry carrying <c>Description.AcceptableValues</c> whose setter stores whatever it
    /// is given. See the class remark: a clamping setter would hide the behavior under test.
    /// </summary>
    private sealed class BoundedEntry : ConfigEntryBase
    {
        private object _value;

        public BoundedEntry(
            ConfigFile file,
            ConfigDefinition definition,
            Type type,
            object value,
            object acceptable,
            object? defaultValue = null)
            : base(file, definition, type)
        {
            _value = value;
            Description = new FakeDescription(acceptable);
            DefaultValue = defaultValue;
        }

        public object Description { get; }

        /// <summary>
        /// Real <c>ConfigEntryBase</c> has this; the unit-test shim does not, which is why
        /// <c>SyncedBounds</c> reads it reflectively. Null here means "no declared default".
        /// </summary>
        public object? DefaultValue { get; }

        public override object? BoxedValue
        {
            get => _value;
            set => _value = value!;
        }
    }

    private sealed class FakeDescription
    {
        public FakeDescription(object acceptableValues) => AcceptableValues = acceptableValues;

        public object AcceptableValues { get; }
    }
}
