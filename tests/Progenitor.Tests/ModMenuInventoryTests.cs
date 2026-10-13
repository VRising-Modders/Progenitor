using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;

namespace Progenitor.Tests;

public sealed class ModMenuInventoryTests : IDisposable
{
    private readonly TestLogSource _log = new();

    public ModMenuInventoryTests()
    {
        Plugin.Logger = _log;
        Handshake.ResetTables();
        ModMenuInventory.ResetForTests();
        Handshake.UninstallClientServerContextWatch();
        WorldEpoch.ResetForTests();
    }

    public void Dispose()
    {
        Handshake.UninstallClientServerContextWatch();
        ModMenuInventory.ResetForTests();
        Handshake.ResetTables();
        WorldEpoch.ResetForTests();
        Plugin.Logger = null;
    }

    private static string ContextLine(string action, string reason, int epoch) =>
        ModMenuInventory.ContextLogPrefix + action + " reason=" + reason + " epoch=" + epoch;
    private static Handshake.ModRecord Declaration(string id, string version = "1.0",
        NetworkCompatibilityLevel level = NetworkCompatibilityLevel.ServerMustHave) =>
        Handshake.ModRecord.CreateDeclared(id, version, level, NetworkVersionRule.Exact);
    private static ManifestEnvelope Catalog(params ManifestMod[] mods) =>
        new(new ManifestBody(1, "Test server", "", new ManifestOperator("", "", ""), null, mods), "");
    private static ManifestMod Listed(string id, string version = "1.0",
        NetworkCompatibilityLevel level = NetworkCompatibilityLevel.ServerMustHave) =>
        new(id, version, level, NetworkVersionRule.Exact, false);
    private static ModMenuInventorySnapshot Build(ModMenuLocalObservation[]? local = null,
        Handshake.ModRecord[]? declared = null, Handshake.ModRecord[]? server = null,
        ManifestEnvelope? catalog = null) => ModMenuInventory.Build(local ?? Array.Empty<ModMenuLocalObservation>(),
            declared ?? Array.Empty<Handshake.ModRecord>(), server ?? Array.Empty<Handshake.ModRecord>(), catalog, "3.4.5");
    private static ModMenuInventoryRow Row(ModMenuInventorySnapshot snapshot, string id) =>
        Assert.Single(snapshot.Rows, r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Compatibility_declaration_does_not_become_installed_or_loaded_plugin()
    {
        var row = Row(Build(declared: new[] { Declaration("example.fake") }), "example.fake");
        Assert.Equal("Reported on this client", row.Status);
        Assert.Equal("Server requirement unknown", row.Requirement);
        Assert.Contains("Installation and loading have not been confirmed", row.Explanation);
    }

    [Fact]
    public void Catalog_only_mod_is_neither_local_loaded_nor_actual_server_requirement()
    {
        var row = Row(Build(catalog: Catalog(Listed("server.offer"))), "server.offer");
        Assert.Equal("Listed by server", row.Status);
        Assert.Equal("Server list", row.Location);
        Assert.Equal("Server requirement unknown", row.Requirement);
        Assert.Equal("Server 1.0", row.Version);
        Assert.Contains("Server activity and download availability are unconfirmed", row.Explanation);
    }

    [Theory]
    [InlineData(NetworkCompatibilityLevel.ServerMustHave, "Required by server")]
    [InlineData(NetworkCompatibilityLevel.NoNeedForSync, "Optional on this server")]
    [InlineData(NetworkCompatibilityLevel.ClientOnly, "Not required by server")]
    [InlineData((NetworkCompatibilityLevel)99, "Server requirement unknown")]
    public void Actual_server_handshake_controls_requirement_even_when_catalog_disagrees(
        NetworkCompatibilityLevel level, string expected)
    {
        var row = Row(Build(server: new[] { Declaration("example.mod", level: level) },
            catalog: Catalog(Listed("example.mod", level: NetworkCompatibilityLevel.ServerMustHave))), "example.mod");
        Assert.Equal(expected, row.Requirement);
        Assert.Equal("Listed by server", row.Status);
    }

    [Fact]
    public void Local_observation_beats_declaration_and_versions_from_both_sides_remain_visible()
    {
        var row = Row(Build(local: new[] { new ModMenuLocalObservation("example.mod", "Example", "1.2", true) },
            declared: new[] { Declaration("EXAMPLE.MOD", "invented") },
            server: new[] { Declaration("example.mod", "2.0") }, catalog: Catalog(Listed("example.mod", "old"))), "example.mod");
        Assert.Equal("Example", row.DisplayName);
        Assert.Equal("Loaded on this client", row.Status);
        Assert.Equal("Local 1.2; server 2.0", row.Version);
        Assert.Equal("Local + server list", row.Location);
    }

    [Fact]
    public void Reload_owned_assembly_is_narrower_evidence_than_a_plugin_instance()
    {
        var local = new[]
        {
            new ModMenuLocalObservation("example.mod", "Assembly name", "1.0", false),
            new ModMenuLocalObservation("example.other", "Instance name", "2.0", true),
        };
        var snapshot = Build(local: local);
        var row = Row(snapshot, "example.mod");
        Assert.Equal("Loaded on this client", row.Status);
        Assert.Contains("Activation status is unavailable", row.Explanation);
        Assert.Equal("Loaded on this client", Row(snapshot, "example.other").Status);
        Assert.Contains("A plugin instance is present", Row(snapshot, "example.other").Explanation);
    }

    [Fact]
    public void Unavailable_catalog_is_distinguished_from_received_empty_catalog()
    {
        Assert.Contains("unavailable", Build().ServerSummary);
        var received = Build(catalog: Catalog());
        Assert.Contains("received (0 entries)", received.ServerSummary);
        Assert.DoesNotContain("No server mods", received.ServerSummary);
    }

    [Fact]
    public void Overview_count_does_not_count_declarations_as_observed_mods()
    {
        var snapshot = Build(declared: new[] { Declaration("example.unverified") });
        Assert.StartsWith("1 mod found on this client.", snapshot.LocalSummary); // Core only.
        Assert.Contains("1 more reported; loading is unconfirmed", snapshot.LocalSummary);
    }

    [Fact]
    public void Current_Core_identity_wins_over_bootstrap_metadata_and_exact_scratch_only_is_hidden()
    {
        var snapshot = Build(local: new[]
        {
            new ModMenuLocalObservation(ModMenuInventory.CoreId, "Progenitor Bootstrap", "old", true),
            new ModMenuLocalObservation(ModMenuInventory.ScratchId.ToUpperInvariant(), "Scratch", "1", true),
            new ModMenuLocalObservation(ModMenuInventory.ScratchId + ".consumer", "Valid consumer", "1", true),
        }, declared: new[] { Declaration(Handshake.CapabilityGuid) });
        var core = Row(snapshot, ModMenuInventory.CoreId);
        Assert.Equal("Progenitor", core.DisplayName);
        Assert.Equal("3.4.5", core.Version);
        Assert.Equal("Running", core.Status);
        Assert.DoesNotContain(snapshot.Rows, r => string.Equals(r.Id, ModMenuInventory.ScratchId, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(snapshot.Rows, r => r.Id == Handshake.CapabilityGuid);
        Assert.NotNull(Row(snapshot, ModMenuInventory.ScratchId + ".consumer"));
    }

    [Fact]
    public void Partial_inventory_is_labelled_and_Core_survives_limit()
    {
        var local = new List<ModMenuLocalObservation>();
        for (int i = 0; i <= ModMenuInventory.MaxRows; i++)
            local.Add(new("mod." + i, "Example " + i, "1", true));
        var snapshot = ModMenuInventory.Build(local, Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>(), null, "3");
        Assert.Equal(ModMenuInventory.MaxRows, snapshot.Rows.Count);
        Assert.Contains("partial", snapshot.LocalSummary);
        Assert.NotNull(Row(snapshot, ModMenuInventory.CoreId));
    }

    [Fact]
    public void A_fresh_snapshot_does_not_remember_a_removed_local_instance()
    {
        Assert.Equal("Loaded on this client", Row(Build(local: new[] { new ModMenuLocalObservation("example.mod", "Example", "1", true) }), "example.mod").Status);
        var next = Build(server: new[] { Declaration("example.mod") });
        Assert.Equal("Listed by server", Row(next, "example.mod").Status);
    }

    [Fact]
    public void Disconnect_clears_server_inventory_even_when_handshake_and_catalog_are_still_held()
    {
        Handshake.RecordServerRequest(new[] { Declaration("example.mod") });
        var catalog = Catalog(Listed("example.mod"), Listed("server.offer"));
        Assert.True(ModMenuInventory.RestoreServerContext("handshake-ok", new[] { Declaration("example.mod") }, catalog));
        var live = ModMenuInventory.Snapshot();
        Assert.Contains("received (2 entries)", live.ServerSummary);
        Assert.Equal("Required by server", Row(live, "example.mod").Requirement);

        Assert.True(Handshake.InvalidateClientServerContext("disconnect", WorldEpoch.Current));
        Assert.False(ModMenuInventory.ServerContextLive);
        Assert.Contains("unavailable", ModMenuInventory.Snapshot().ServerSummary);
        Assert.Equal(1, _log.Infos.Count(line => line.StartsWith(ModMenuInventory.ContextLogPrefix + "cleared", StringComparison.Ordinal)));
        Assert.Contains(ContextLine("cleared", "disconnect", WorldEpoch.Current), _log.Infos);
    }

    [Fact]
    public void Epoch_change_clears_server_inventory_through_WorldLoaded()
    {
        Handshake.InstallClientServerContextWatch();
        Assert.True(ModMenuInventory.RestoreServerContext("handshake-ok", new[] { Declaration("example.mod") }, Catalog(Listed("example.mod"))));
        Assert.Contains("received", ModMenuInventory.Snapshot().ServerSummary);

        int epoch = WorldEpoch.Advance(ClientWorldEpoch.EndedSource);
        Assert.False(ModMenuInventory.ServerContextLive);
        Assert.Contains("unavailable", ModMenuInventory.Snapshot().ServerSummary);
        Assert.Contains(ContextLine("cleared", "disconnect", epoch), _log.Infos);
        Assert.Empty(Handshake.CopyServerAsked());
    }

    [Fact]
    public void Kick_like_end_without_WorldLoaded_clears_server_inventory()
    {
        Handshake.InstallClientServerContextWatch();
        Assert.True(ModMenuInventory.RestoreServerContext("handshake-ok", new[] { Declaration("example.mod") }, Catalog(Listed("example.mod"))));
        int worldEpoch = WorldEpoch.Current;

        Handshake.NotifyClientConnectionEnded();
        Assert.False(ModMenuInventory.ServerContextLive);
        Assert.Equal(worldEpoch, WorldEpoch.Current);
        Assert.Contains("unavailable", ModMenuInventory.Snapshot().ServerSummary);
        Assert.Contains(ContextLine("cleared", "disconnect", worldEpoch), _log.Infos);
        Assert.True(Handshake.ClientEpoch > Handshake.AcceptedEpoch);
    }

    [Fact]
    public void Core_only_refresh_of_a_surviving_world_does_not_clear()
    {
        Handshake.InstallClientServerContextWatch();
        Assert.True(ModMenuInventory.RestoreServerContext("handshake-ok", new[] { Declaration("example.mod") }, Catalog(Listed("example.mod"))));
        Assert.True(ModMenuInventory.ServerContextLive);
        int handshakeEpoch = Handshake.ClientEpoch;

        WorldEpoch.Advance(ClientWorldEpoch.CreatedSource);
        Assert.True(ModMenuInventory.ServerContextLive);
        Assert.Equal(handshakeEpoch, Handshake.ClientEpoch);
        Assert.Contains("received", ModMenuInventory.Snapshot().ServerSummary);
        Assert.DoesNotContain(_log.Infos, line => line.Contains("server context cleared", StringComparison.Ordinal));
    }

    [Fact]
    public void Stale_server_packet_older_than_the_clear_is_ignored()
    {
        Handshake.InstallClientServerContextWatch();
        Assert.True(ModMenuInventory.RestoreServerContext("handshake-ok", new[] { Declaration("example.mod") }, Catalog(Listed("example.mod"))));
        Handshake.NotifyClientConnectionEnded();
        Assert.False(ModMenuInventory.ServerContextLive);

        int old = Handshake.IncomingPacketEpoch;
        Assert.False(Handshake.AcceptsIncomingServerPacket(old));
        Assert.False(ModMenuInventory.RestoreServerContext("handshake-ok", new[] { Declaration("example.mod") }, Catalog(Listed("example.mod"))));
        Assert.False(ModMenuInventory.ServerContextLive);
        Assert.Contains(
            ModMenuInventory.StalePacketLogPrefix + old + " current=" + Handshake.ClientEpoch,
            _log.Infos);
    }

    [Fact]
    public void Normal_disconnect_then_reconnect_clears_then_restores()
    {
        Handshake.InstallClientServerContextWatch();
        Handshake.RecordServerRequest(new[] { Declaration("example.mod") });
        Handshake.NotifyHandshakeOk(3);
        Assert.True(ModMenuInventory.ServerContextLive);

        int epoch = WorldEpoch.Advance(ClientWorldEpoch.EndedSource);
        Assert.False(ModMenuInventory.ServerContextLive);
        Assert.Contains(ContextLine("cleared", "disconnect", epoch), _log.Infos);

        Handshake.RecordServerRequest(new[] { Declaration("example.mod") });
        Handshake.NotifyHandshakeOk(3);
        Assert.True(ModMenuInventory.ServerContextLive);
        Assert.Contains(ContextLine("restored", "handshake-ok", WorldEpoch.Current), _log.Infos);
    }

    [Fact]
    public void Handshake_epoch_change_clears_without_a_new_world()
    {
        Handshake.InstallClientServerContextWatch();
        Assert.True(ModMenuInventory.RestoreServerContext("handshake-ok", new[] { Declaration("example.mod") }, Catalog(Listed("example.mod"))));
        int worldEpoch = WorldEpoch.Current;

        Handshake.NotifyHandshakeEpochChanged();
        Assert.False(ModMenuInventory.ServerContextLive);
        Assert.Equal(worldEpoch, WorldEpoch.Current);
        Assert.Contains(ContextLine("cleared", "epoch", worldEpoch), _log.Infos);
    }

    [Fact]
    public void Successful_handshake_restores_server_inventory()
    {
        Assert.True(ModMenuInventory.RestoreServerContext("handshake-ok", new[] { Declaration("example.mod") }, Catalog(Listed("example.mod"))));
        Assert.True(Handshake.InvalidateClientServerContext("disconnect"));
        Assert.Contains("unavailable", ModMenuInventory.Snapshot().ServerSummary);

        Handshake.RecordServerRequest(new[] { Declaration("example.mod") });
        Handshake.NotifyHandshakeOk(3);
        Assert.True(ModMenuInventory.ServerContextLive);
        var snapshot = ModMenuInventory.Snapshot();
        Assert.Equal("Required by server", Row(snapshot, "example.mod").Requirement);
        Assert.Contains(ContextLine("restored", "handshake-ok", WorldEpoch.Current), _log.Infos);
    }

    [Fact]
    public void Server_list_status_is_unavailable_after_clear_and_available_after_restore()
    {
        Assert.Equal(ModMenuInventory.ServerListUnavailable, ModMenuInventory.DescribeServerList().State);
        Assert.Equal(0, ModMenuInventory.DescribeServerList().Epoch);

        var catalog = Catalog(Listed("example.mod"), Listed("server.offer"));
        Assert.True(ModMenuInventory.RestoreServerContext("handshake-ok", new[] { Declaration("example.mod") }, catalog, 12));
        var available = ModMenuInventory.DescribeServerList();
        Assert.Equal(ModMenuInventory.ServerListAvailable, available.State);
        Assert.Equal(2, available.Count);
        Assert.Equal(12, available.Epoch);

        Assert.True(Handshake.InvalidateClientServerContext("disconnect", 12));
        var cleared = ModMenuInventory.DescribeServerList();
        Assert.Equal(ModMenuInventory.ServerListUnavailable, cleared.State);
        Assert.Equal(0, cleared.Count);
        Assert.Equal(12, cleared.Epoch);

        Assert.True(ModMenuInventory.RestoreServerContext("handshake-ok", new[] { Declaration("example.mod") }, catalog, 13));
        var restored = ModMenuInventory.DescribeServerList();
        Assert.Equal(ModMenuInventory.ServerListAvailable, restored.State);
        Assert.Equal(2, restored.Count);
        Assert.Equal(13, restored.Epoch);
    }

    [Fact]
    public void A_second_clear_without_restore_does_not_log_again()
    {
        Assert.True(ModMenuInventory.RestoreServerContext("handshake-ok", new[] { Declaration("example.mod") }, Catalog(Listed("example.mod")), 2));
        Assert.False(ModMenuInventory.RestoreServerContext("handshake-ok", new[] { Declaration("example.mod") }, Catalog(Listed("example.mod")), 2));
        Assert.True(ModMenuInventory.ClearServerContext("disconnect", 2));
        Assert.False(ModMenuInventory.ClearServerContext("disconnect", 2));
        Assert.False(Handshake.InvalidateClientServerContext("disconnect", 2));
        Assert.Equal(1, _log.Infos.Count(line => line.Contains("server context restored", StringComparison.Ordinal)));
        Assert.Equal(1, _log.Infos.Count(line => line.Contains("server context cleared", StringComparison.Ordinal)));
    }
}

