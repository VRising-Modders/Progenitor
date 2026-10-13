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
/// Mod compatibility C3, section 4 (docs/config-sync.md): the join snapshot follows the join's version judgment
/// (ranges, Version matching, per-mod rules); a client that declared VersionRanges gets a "sent" marker per plugin the
/// snapshot covered, and locks that plugin's synced keys the server did not send to their declared defaults; a type
/// mismatch and an unknown enum name lock to the default too. Game-free, on the ConfigFile shim.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class ConfigSyncRangeTests : IDisposable
{
    private const string Trade = "example.trade";
    private readonly string _dir;
    private readonly ConfigFile _file;

    public ConfigSyncRangeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-configsync-range-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = new ConfigFile(Path.Combine(_dir, "example.trade.cfg"), saveOnInit: true);
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

    private static Handshake.ModRecord Row(string guid, string version) =>
        Handshake.ModRecord.CreateDeclared(guid, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);

    /// <summary>A judgment like the server's for a client that sent its table: this server's Trade declares <c>&gt;=1.2.0 &lt;2.0.0</c>.</summary>
    private static VersionJudgeContext Ranges() => new()
    {
        ClientRanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        ServerRange = g => string.Equals(g, Trade, StringComparison.OrdinalIgnoreCase) ? ">=1.2.0 <2.0.0" : null,
    };

    private static ConfigSyncRecord Value(string key, string toml, Type type) =>
        new(Trade, "Trade", key, toml, ConfigSync.TypeToken(type));

    // ---------------------------------------------------------------- server: which plugins are sent

    [Fact]
    public void The_snapshot_follows_the_range_judgment_when_versions_differ()
    {
        ConfigSync.Bind(_file, Trade, "Trade", "Fee", 5, "fee", pluginVersion: "1.4.0");
        var server = new[] { Row(Trade, "1.4.0") };

        // Server 1.4.0's range includes the 1.2.0 client: sent (the Exact attribute rule alone would have refused it).
        var ctx = Ranges();
        var passed = ConfigSync.SnapshotFor(new[] { Row(Trade, "1.2.0") }, server, ctx, markSent: false);
        Assert.Single(passed);
        Assert.Equal("Fee", passed[0].Key);
        Assert.Empty(ctx.Accepted);

        // Today's rule with no judgment: Exact refuses the same pair.
        Assert.Empty(ConfigSync.SnapshotFor(new[] { Row(Trade, "1.2.0") }, server, versions: null, markSent: false));
        Assert.Empty(ConfigSync.SnapshotFor(new[] { Row(Trade, "1.2.0") }, server));

        // Outside the range: not sent, one line naming the reason.
        ConfigSync.TestLogs.Clear();
        ConfigSync.ResetForTests();
        ConfigSync.Bind(_file, Trade, "Trade", "Fee", 5, "fee", pluginVersion: "1.4.0");
        var failed = ConfigSync.SnapshotFor(new[] { Row(Trade, "1.1.0") }, server, Ranges(), markSent: true);
        Assert.Empty(failed);
        Assert.Contains(ConfigSync.TestLogs, l => l.StartsWith(
            "ConfigSync: ignored example.trade synced settings: client version '1.1.0' does not match server '1.4.0' (this server's 1.4.0 works with",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Version_matching_decides_over_the_range()
    {
        ConfigSync.Bind(_file, Trade, "Trade", "Fee", 5, "fee", pluginVersion: "1.4.0");
        var ctx = Ranges();
        ctx.Settings = new VersionMatchSettings(VersionMatchingMode.Exact);
        Assert.Empty(ConfigSync.SnapshotFor(new[] { Row(Trade, "1.2.0") }, new[] { Row(Trade, "1.4.0") }, ctx, markSent: false));

        var rule = Ranges();
        rule.Settings = new VersionMatchSettings(VersionMatchingMode.Exact, new Dictionary<string, ClientModRule> { [Trade] = ClientModRule.Any });
        Assert.Single(ConfigSync.SnapshotFor(new[] { Row(Trade, "1.2.0") }, new[] { Row(Trade, "1.4.0") }, rule, markSent: false));
    }

    [Fact]
    public void Markers_follow_each_covered_plugin_only_for_a_client_that_reads_them()
    {
        ConfigSync.Bind(_file, Trade, "Trade", "Fee", 5, "fee", pluginVersion: "1.4.0");
        ConfigSync.Bind(_file, Trade, "Trade", "Tax", 2, "tax", pluginVersion: "1.4.0");
        var framework = new ConfigFile(Path.Combine(_dir, "progenitor.cfg"), saveOnInit: true);
        ConfigSync.Bind(framework, ConfigSync.FrameworkSettingsId, "Join", "Message", "hi", "m");
        var server = new[] { Row(Trade, "1.4.0"), Row("example.quiet", "3.0.0") };
        var client = new[] { Row(Trade, "1.4.0"), Row("example.quiet", "3.0.0"), Row("example.clientonly", "1.0.0") };

        var marked = ConfigSync.SnapshotFor(client, server, Ranges(), markSent: true);
        // Trade's two entries, then its marker; Quiet has no synced key here and is still marked; the client-only mod
        // (not on this server) and Progenitor's own settings are never marked.
        var tradeMarker = marked.FindIndex(r => ConfigSyncWire.IsSentMarker(r) && r.PluginId == Trade);
        Assert.True(tradeMarker >= 0);
        Assert.Equal(2, marked.Take(tradeMarker).Count(r => r.PluginId == Trade && !ConfigSyncWire.IsSentMarker(r)));
        Assert.Contains(marked, r => ConfigSyncWire.IsSentMarker(r) && r.PluginId == "example.quiet");
        Assert.DoesNotContain(marked, r => ConfigSyncWire.IsSentMarker(r) && r.PluginId == "example.clientonly");
        Assert.DoesNotContain(marked, r => ConfigSyncWire.IsSentMarker(r) && ConfigSync.IsFrameworkSettings(r.PluginId));
        Assert.Contains(marked, r => r.PluginId == ConfigSync.FrameworkSettingsId && r.Key == "Message");

        var unmarked = ConfigSync.SnapshotFor(client, server, Ranges(), markSent: false);
        Assert.DoesNotContain(unmarked, r => ConfigSyncWire.IsSentMarker(r));
        Assert.Equal(3, unmarked.Count);

        // The marker survives the wire and is recognized; it is never a real entry (empty section and key).
        var packets = ConfigSyncWire.Pack(marked, snapshot: true);
        Assert.True(ConfigSyncWire.TryRead(packets[0], out _, out _, out var read, out _));
        Assert.Equal(marked.Count(r => ConfigSyncWire.IsSentMarker(r)), read.Count(r => ConfigSyncWire.IsSentMarker(r)));
    }

    // ---------------------------------------------------------------- client: the default lock

    [Fact]
    public void A_marked_plugin_key_the_snapshot_lacks_is_locked_to_its_default_and_restored_on_disconnect()
    {
        var fee = ConfigSync.Bind(_file, Trade, "Trade", "Fee", 5, "fee", pluginVersion: "1.4.0");
        var tax = ConfigSync.Bind(_file, Trade, "Trade", "Tax", 2, "tax", pluginVersion: "1.4.0");
        fee.Value = 8;
        tax.Value = 3;

        int applied = ConfigSync.ApplyRecords(new[] { Value("Tax", "4", typeof(int)), ConfigSyncWire.SentMarker(Trade) }, snapshot: true);
        Assert.Equal(1, applied);
        Assert.False(ConfigSync.IsLocked(fee));
        Assert.Equal(1, ConfigSync.CompleteSnapshot());

        Assert.Equal(5, fee.Value);
        Assert.True(ConfigSync.IsLocked(fee));
        Assert.Equal(4, tax.Value);
        Assert.Contains(ConfigSync.TestLogs, l => l ==
            "ConfigSync: example.trade/Trade/Fee not sent by the server (its version predates it); using the default 5");

        // A write is refused while locked, exactly as for a server value.
        fee.Value = 9;
        Assert.Equal(5, fee.Value);

        ConfigSync.RestoreLocal();
        Assert.False(ConfigSync.IsLocked(fee));
        Assert.Equal(8, fee.Value);
        Assert.Equal(3, tax.Value);
    }

    [Fact]
    public void The_lock_waits_for_the_last_packet_of_the_snapshot()
    {
        var fee = ConfigSync.Bind(_file, Trade, "Trade", "Fee", 5, "fee", pluginVersion: "1.4.0");
        var tax = ConfigSync.Bind(_file, Trade, "Trade", "Tax", 2, "tax", pluginVersion: "1.4.0");
        fee.Value = 8;
        int epoch = Handshake.IncomingPacketEpoch;

        // The marker arrives in the first packet; Fee arrives in the second (the last): nothing is default-locked.
        Assert.True(ConfigSync.ApplyIncomingServerPacket(epoch, new[] { ConfigSyncWire.SentMarker(Trade) }, snapshot: true, more: true));
        Assert.False(ConfigSync.IsLocked(fee));
        Assert.True(ConfigSync.ApplyIncomingServerPacket(epoch, new[] { Value("Fee", "7", typeof(int)) }, snapshot: true, more: false));
        Assert.Equal(7, fee.Value);
        Assert.Equal(2, tax.Value);
        Assert.True(ConfigSync.IsLocked(tax));
        Assert.DoesNotContain(ConfigSync.TestLogs, l => l.Contains("Trade/Fee not sent", StringComparison.Ordinal));

        // A later push (not a snapshot) never completes anything.
        ConfigSync.RestoreLocal();
        Assert.True(ConfigSync.ApplyIncomingServerPacket(epoch, new[] { ConfigSyncWire.SentMarker(Trade) }, snapshot: false, more: false));
        Assert.False(ConfigSync.IsLocked(fee));
        Assert.False(ConfigSync.IsLocked(tax));
    }

    [Fact]
    public void An_unmarked_plugin_keeps_its_local_value_as_before()
    {
        var fee = ConfigSync.Bind(_file, Trade, "Trade", "Fee", 5, "fee", pluginVersion: "1.4.0");
        var tax = ConfigSync.Bind(_file, Trade, "Trade", "Tax", 2, "tax", pluginVersion: "1.4.0");
        fee.Value = 8;

        // An older server (or an older client's snapshot): no markers.
        ConfigSync.ApplyRecords(new[] { Value("Tax", "4", typeof(int)) }, snapshot: true);
        Assert.Equal(0, ConfigSync.CompleteSnapshot());
        Assert.Equal(8, fee.Value);
        Assert.False(ConfigSync.IsLocked(fee));

        // A marker for another plugin does not touch this one.
        ConfigSync.ApplyRecords(new[] { ConfigSyncWire.SentMarker("example.other") }, snapshot: true);
        Assert.Equal(0, ConfigSync.CompleteSnapshot());
        Assert.False(ConfigSync.IsLocked(fee));
    }

    [Fact]
    public void A_type_mismatch_locks_to_the_default_and_restores_on_disconnect()
    {
        var fee = ConfigSync.Bind(_file, Trade, "Trade", "Fee", 5, "fee", pluginVersion: "1.4.0");
        fee.Value = 8;
        ConfigSync.TestApplyRule = NetworkVersionRule.Exact;

        Assert.Equal(0, ConfigSync.ApplyRecords(new[] { Value("Fee", "cheap", typeof(string)) }, snapshot: true));
        Assert.Equal(5, fee.Value);
        Assert.True(ConfigSync.IsLocked(fee));
        Assert.Contains(ConfigSync.TestLogs, l => l ==
            "ConfigSync: schema-drift example.trade/Trade/Fee: client type 'System.Int32' does not match server 'System.String'; using the default 5");

        ConfigSync.RestoreLocal();
        Assert.Equal(8, fee.Value);
        Assert.False(ConfigSync.IsLocked(fee));
    }

    [Fact]
    public void An_enum_name_the_client_does_not_know_is_the_default_with_a_schema_drift_line()
    {
        var day = ConfigSync.Bind(_file, Trade, "Trade", "Market", DayOfWeek.Monday, "day", pluginVersion: "1.4.0");
        day.Value = DayOfWeek.Friday;
        ConfigSync.TestApplyRule = NetworkVersionRule.Exact;

        Assert.Equal(0, ConfigSync.ApplyRecords(new[] { Value("Market", "Funday", typeof(DayOfWeek)) }, snapshot: true));
        Assert.Equal(DayOfWeek.Monday, day.Value);
        Assert.True(ConfigSync.IsLocked(day));
        Assert.Contains(ConfigSync.TestLogs, l => l ==
            "ConfigSync: schema-drift example.trade/Trade/Market: 'Funday' is not a value this client's DayOfWeek knows; using the default Monday");
        Assert.DoesNotContain(ConfigSync.TestLogs, l => l.Contains("could not apply", StringComparison.Ordinal));

        // A name it knows applies as before (case-insensitive, as BepInEx reads it).
        Assert.Equal(1, ConfigSync.ApplyRecords(new[] { Value("Market", "wednesday", typeof(DayOfWeek)) }, snapshot: false));
        Assert.Equal(DayOfWeek.Wednesday, day.Value);

        ConfigSync.RestoreLocal();
        Assert.Equal(DayOfWeek.Friday, day.Value);
    }
    [Fact]
    public void A_row_the_server_only_requires_and_does_not_run_is_never_marked()
    {
        // A server plugin can Require a companion client mod it does not load: being on the request is not running it.
        var server = new[] { Row(Trade, "1.4.0"), Row("example.companion", "1.0.0") };
        var client = new[] { Row(Trade, "1.4.0"), Row("example.companion", "1.0.0") };
        ConfigSync.ServerRunsPlugin = id => string.Equals(id, Trade, StringComparison.OrdinalIgnoreCase);
        try
        {
            var marked = ConfigSync.SnapshotFor(client, server, Ranges(), markSent: true);
            Assert.Contains(marked, r => ConfigSyncWire.IsSentMarker(r) && r.PluginId == Trade);
            Assert.DoesNotContain(marked, r => ConfigSyncWire.IsSentMarker(r) && r.PluginId == "example.companion");
        }
        finally { ConfigSync.ServerRunsPlugin = null; }
    }

    [Fact]
    public void A_plugin_with_a_record_too_large_to_send_loses_its_marker_so_nothing_is_locked_to_a_default()
    {
        var huge = new ConfigSyncRecord(Trade, "Trade", "Motd", new string('x', 20000), ConfigSync.TypeToken(typeof(string)));
        var records = new List<ConfigSyncRecord>
        {
            Value("Fee", "5", typeof(int)), huge, ConfigSyncWire.SentMarker(Trade),
            new("example.quiet", "Q", "K", "1", ConfigSync.TypeToken(typeof(int))), ConfigSyncWire.SentMarker("example.quiet"),
        };

        var kept = ConfigSync.WithoutMarkersOfUnsendable(records);
        Assert.DoesNotContain(kept, r => ConfigSyncWire.IsSentMarker(r) && r.PluginId == Trade);
        Assert.Contains(kept, r => ConfigSyncWire.IsSentMarker(r) && r.PluginId == "example.quiet");

        // Everything fits: the list is passed through unchanged.
        var fits = records.Where(r => r.Key != "Motd").ToList();
        Assert.Same(fits, ConfigSync.WithoutMarkersOfUnsendable(fits));
    }
}
