using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// REMOTETEST1 (requested 2026-10-03): <c>[Install] TreatLocalServerAsRemote</c> sends a connected
/// server through the remote-install route even when it is the configured local folder's server, so
/// the remote route can be tested against the isolated test server on this PC. Plus: every removal,
/// mark and remote step leaves an <c>Install:</c> log line (live: a confirmed "Cancel installing
/// QStacks?" logged nothing).
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class TreatLocalServerAsRemoteTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "progenitor-remotetest-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Line, bool Warn)> _log = new();

    public TreatLocalServerAsRemoteTests()
    {
        Directory.CreateDirectory(ClientBep);
        Directory.CreateDirectory(ServerBep);
        InstallLog.TestSink = (line, warn) => { lock (_log) _log.Add((line, warn)); };
    }

    public void Dispose()
    {
        InstallLog.TestSink = null;
        TreatLocalServerAsRemote.TestValue = null;
        try { Directory.Delete(_base, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string ClientGame => Path.Combine(_base, "VRising");
    private string ClientBep => Path.Combine(ClientGame, "BepInEx");
    private string ServerGame => Path.Combine(_base, "server");
    private string ServerBep => Path.Combine(ServerGame, "BepInEx");

    private static RemoteInstallStatus Status(bool admin = true) =>
        new(0, true, admin, admin, RemoteReachability.Reachable, "", admin, Array.Empty<RemoteServerRow>());

    /// <summary>The live join: 127.0.0.1:9976, the configured folder's server, handshake ok.</summary>
    private static ServerRouteFacts LiveJoin(bool treatAsRemote, bool admin = true, bool connected = true, int port = 9976) => new()
    {
        ConfiguredPath = @"C:\GameModding\_testserver\server",
        PathValid = true,
        CanonicalPath = @"C:\GameModding\_testserver\server",
        FolderPort = 9976,
        Connected = connected,
        ConnectedAddress = connected ? "127.0.0.1" : "",
        ConnectedPort = connected ? port : 0,
        PortRecorded = true,
        IsLocalMachine = connected,
        HandshakeOk = connected,
        RemoteStatus = connected ? Status(admin) : null,
        TreatLocalAsRemote = treatAsRemote,
    };

    // ---- route selection ---------------------------------------------------------------------

    [Fact]
    public void Off_the_connected_local_folder_is_the_local_route()
    {
        var decision = ServerRouteSelector.Decide(LiveJoin(treatAsRemote: false));
        Assert.Equal(ServerRouteKind.Local, decision.Route.Kind);
        Assert.False(decision.Route.ForcedRemote);
        Assert.Equal("local", decision.Route.EffectiveName);
    }

    [Fact]
    public void On_the_connected_local_folder_is_the_remote_route_and_says_why()
    {
        var decision = ServerRouteSelector.Decide(LiveJoin(treatAsRemote: true));
        Assert.Equal(ServerRouteKind.Remote, decision.Route.Kind);
        Assert.Equal(ServerRouteBlock.None, decision.Route.Block);
        Assert.True(decision.Route.ForcedRemote);
        Assert.Equal("remote (forced by TreatLocalServerAsRemote)", decision.Route.EffectiveName);
        Assert.Contains("remote (forced by TreatLocalServerAsRemote)", decision.Why);
        // The physical fact the picker merges on is unchanged: one "Connected: <name>" entry.
        Assert.True(decision.ConnectedIsLocal);
        // Revert: deciding the local route before the flag puts this back on folder writes.
    }

    [Fact]
    public void On_admin_rules_apply_to_the_forced_route()
    {
        var decision = ServerRouteSelector.Decide(LiveJoin(treatAsRemote: true, admin: false));
        Assert.Equal(ServerRouteBlock.NotAdmin, decision.Route.Block);
        Assert.True(decision.Route.ForcedRemote);
        Assert.False(decision.Route.CanInstall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disconnected_local_management_requires_remote_testing_to_be_off(bool forced)
    {
        var decision = ServerRouteSelector.Decide(LiveJoin(treatAsRemote: forced, connected: false));
        Assert.Equal(forced ? ServerRouteKind.None : ServerRouteKind.Local, decision.Route.Kind);
        Assert.Equal(forced, decision.Route.ForcedRemote);
        Assert.Equal(!forced, decision.Route.CanManage);
        Assert.Equal(!forced, decision.Route.CanInstall);
        var entry = new ServerPickEntry(ServerPicker.LocalPrefix + @"C:\GameModding\_testserver\server",
            ServerPickKind.LocalFolder, "Test", "Test", @"C:\GameModding\_testserver\server", "");
        var picked = ServerPicker.RouteFor(entry, decision.Route, @"C:\GameModding\_testserver\server");
        Assert.Equal(decision.Route.Kind, picked.Kind);
        if (forced)
        {
            Assert.Equal(ServerRouteBlock.Disconnected, picked.Block);
            Assert.Equal("Connect to manage", picked.Reason);
        }
    }

    [Fact]
    public void On_a_connected_server_that_is_not_the_folder_is_remote_but_not_forced()
    {
        var decision = ServerRouteSelector.Decide(LiveJoin(treatAsRemote: true, port: 9977));
        Assert.Equal(ServerRouteKind.Remote, decision.Route.Kind);
        Assert.False(decision.Route.ForcedRemote);
        Assert.Equal("remote", decision.Route.EffectiveName);
    }

    // ---- picker --------------------------------------------------------------------------------

    [Fact]
    public void The_picker_still_merges_the_folder_into_the_connected_entry_and_lists_other_folders()
    {
        var decision = ServerRouteSelector.Decide(LiveJoin(treatAsRemote: true));
        var inputs = new ServerPickerInputs
        {
            Connected = true,
            ConfiguredPath = @"C:\GameModding\_testserver\server",
            ConfiguredName = "Progenitor Forms Test",
            ConfiguredPort = 9976,
            ConnectedIsConfigured = decision.ConnectedIsLocal,
            ConnectedPort = 9976,
            ConnectedIsThisMachine = true,
            Discovered = new[] { new LocalServerCandidate(@"D:\OtherServer", "Other", ServerPicker.SourceConfigured, port: 9999) },
        };
        var folder = ServerPicker.ConnectedFolder(inputs);
        Assert.NotNull(folder);
        Assert.Equal(@"C:\GameModding\_testserver\server", folder!.Value.Path);

        // Another folder on this PC is still a local-folder entry with the local route.
        var other = new ServerPickEntry(ServerPicker.LocalPrefix + @"D:\OtherServer", ServerPickKind.LocalFolder, "Other (this PC)", "Other", @"D:\OtherServer", "");
        Assert.Equal(ServerRouteKind.Local, ServerPicker.RouteFor(other, decision.Route, @"D:\OtherServer").Kind);
        var connected = new ServerPickEntry(ServerPicker.IdConnected, ServerPickKind.Connected, "Connected: Progenitor Forms Test", "Progenitor Forms Test");
        Assert.True(ServerPicker.RouteFor(connected, decision.Route, @"C:\GameModding\_testserver\server").ForcedRemote);
    }

    // ---- no local writes -----------------------------------------------------------------------

    private sealed class NoTransport : ICatalogTransport
    {
        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network in this test");
    }

    private const string Key = "Progenitor-HotDemo";
    private const string File1 = "progenitor.hotdemo.dll";

    private static string IndexJson() =>
        "[{\"name\":\"HotDemo\",\"full_name\":\"Progenitor-HotDemo\",\"owner\":\"Progenitor\",\"package_url\":\"\",\"is_deprecated\":false,"
        + "\"versions\":[{\"name\":\"HotDemo\",\"full_name\":\"Progenitor-HotDemo-0.1.0\",\"version_number\":\"0.1.0\",\"description\":\"d\","
        + "\"dependencies\":[],\"download_url\":\"https://thunderstore.io/package/download/Progenitor/HotDemo/0.1.0/\",\"file_size\":10,\"is_active\":true}]}]";

    private string SeedServerCopy()
    {
        string file = Path.Combine(ServerBep, ModFolders.ReloadFolderName, File1);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        new ServerInstallLedger("", "", "", DateTime.UtcNow, new[]
        {
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/" + File1, InstalledState.HashBytes(new byte[] { 1, 2, 3 }), 3,
                ServerInstallLedger.CategoryMod, "package:" + Key + "@0.1.0", DateTime.UtcNow, pluginId: "progenitor.hotdemo"),
        }).Write(ServerGame);
        return file;
    }

    [Fact]
    public void While_forced_the_catalog_neither_reads_nor_writes_the_local_server_folder()
    {
        string serverCopy = SeedServerCopy();
        string ledgerBefore = File.ReadAllText(ServerInstallLedger.PathFor(ServerGame));
        bool forced = true;
        var catalog = new InstallCatalogService(ClientBep, new NoTransport(), null, null, null, true, null, ServerBep, Path.Combine(_base, "nolocal"), false)
        {
            ServerRootSuppressed = () => forced,
        };
        Assert.True(catalog.LoadIndexFromJson(IndexJson(), "stamp").Ok);

        Assert.Null(catalog.ServerBepInExRoot);
        Assert.Equal(new[] { Path.GetFullPath(ClientBep) }, catalog.RootsFor(InstallTarget.Both));
        Assert.Empty(catalog.RootsFor(InstallTarget.LocalServer));

        var removed = catalog.RemovePackage(Key, true, InstallTarget.Both);
        Assert.True(removed.NoOp);
        Assert.True(File.Exists(serverCopy), "a forced-remote removal deleted the local server's file");
        var marked = catalog.MarkUninstall(Key, true, InstallTarget.LocalServer);
        Assert.True(marked.NoOp);
        Assert.False(File.Exists(UninstallMarkers.MarkerFor(serverCopy)));
        Assert.Equal(ledgerBefore, File.ReadAllText(ServerInstallLedger.PathFor(ServerGame)));

        // Off again: the same catalog reaches the folder.
        forced = false;
        Assert.Equal(Path.GetFullPath(ServerBep), catalog.ServerBepInExRoot);
        Assert.Equal(2, catalog.RootsFor(InstallTarget.Both).Count);
    }

    // ---- live bug 1: no rebuild loop -------------------------------------------------------------

    /// <summary>
    /// Live (gen 17): the Mods menu rebuilt its catalog whenever <c>ServerBepInExRoot</c> differed from
    /// the configured root, and while forced that property is null, so every Install-tab build threw
    /// the catalog away and the tab looped "Loading the package index" / "not loaded" ~10x/s. The
    /// rebuild test now asks <see cref="InstallCatalogService.BuiltFor"/>, which is the configured root
    /// in every route state, and the loaded index survives the switch flipping either way.
    /// </summary>
    [Fact]
    public void A_catalog_is_still_the_one_built_for_the_folder_in_every_route_state()
    {
        bool forced = false;
        var catalog = new InstallCatalogService(ClientBep, new NoTransport(), null, null, null, true, null, ServerBep, Path.Combine(_base, "nolocal"), false)
        {
            ServerRootSuppressed = () => forced,
        };
        Assert.True(catalog.LoadIndexFromJson(IndexJson(), "stamp").Ok);
        int count = catalog.Search(null).Count;

        foreach (bool state in new[] { false, true, false, true })
        {
            forced = state;
            Assert.True(catalog.BuiltFor(ServerBep), "state forced=" + state + " would rebuild the catalog");
            Assert.False(catalog.BuiltFor(null));
            Assert.False(catalog.BuiltFor(Path.Combine(_base, "other", "BepInEx")));
            Assert.Equal(count, catalog.Search(null).Count);
            Assert.True(catalog.TryGetEntry(Key, out _));
        }
    }

    // ---- live bug 2: remote packets do not rebuild the list ----------------------------------------

    private static CatalogEntry Listing(string key) =>
        new(
            new ManifestMod("", "1.0.0", Progenitor.Plugins.NetworkCompatibilityLevel.NoNeedForSync, Progenitor.Plugins.NetworkVersionRule.None, false, "o", key),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-12T00:00:00Z",
            displayName: key.Substring(2),
            description: "one-liner of " + key,
            owner: "o",
            packageName: key.Substring(2),
            categories: new[] { "Server", "Oakveil Update" },
            releasedUtc: "2026-09-11T00:00:00Z",
            downloads: 10);

    /// <summary>
    /// Live (gen 17): every remote status/progress packet asked for a full rebuild, which re-filtered
    /// ~290 rows every 1-2 s. A packet that changes nothing structural (same route, same pending
    /// counts, the buttons the rows were built with) now updates the cells in place; a route change
    /// still rebuilds once — in every route state, forced not-admin included.
    /// </summary>
    [Theory]
    [InlineData((int)ServerRouteKind.Remote, (int)ServerRouteBlock.None, true)]
    [InlineData((int)ServerRouteKind.None, (int)ServerRouteBlock.NotAdmin, true)]
    [InlineData((int)ServerRouteKind.Local, (int)ServerRouteBlock.None, false)]
    [InlineData((int)ServerRouteKind.Remote, (int)ServerRouteBlock.None, false)]
    public void A_remote_packet_refreshes_cells_in_place_and_only_a_route_change_rebuilds(
        int kindValue, int blockValue, bool forced)
    {
        Progenitor.Ui.Ui.ResetForTests();
        Progenitor.Ui.Ui.SetRuntimeForTests(new CellRowTests.FakeCellRuntime());
        UiDialog.ForgetForTests();
        UiLayoutBatch.ResetForTests();
        CatalogUpdateTags.ResetProvidersForTests();
        CatalogUpdateTags.ConfiguredTagProvider = () => null;
        CatalogUpdateTags.BuildProvider = () => InstallSearchFilterTests.KnownClientBuild;
        var owner = new object();
        try
        {
            var catalog = new InstallSidesPaneTests.FakeCatalog();
            for (int i = 0; i < 12; i++) catalog.Entries.Add(Listing("o-mod" + i));
            var route = new ServerRoute((ServerRouteKind)kindValue, (ServerRouteBlock)blockValue, "", forced);
            var pane = new InstallModsPane(catalog, null, null) { ServerRouteProbe = () => route };
            int rebuilds = 0;
            IUiPanel? panel = null;
            void Build()
            {
                panel?.Dispose();
                panel = Progenitor.Ui.Ui.Panel("Install mods", owner, "example.remotetest");
                pane.Build(panel, true, false, null, () => { }, () => { }, DateTime.UtcNow);
            }

            pane.SetDetailRebuild(() => rebuilds++);
            Build();
            Assert.NotEmpty(pane.Rows);

            for (int i = 0; i < 5; i++) pane.NoteRemoteChanged();
            Assert.Equal(0, rebuilds);
            Assert.Equal((0, 5), pane.RemoteRefreshCounts);

            // The route moving (an admin authenticated, a status arriving) is structural: one rebuild.
            route = new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.Unreachable, "dns", forced);
            pane.NoteRemoteChanged();
            Assert.Equal(1, rebuilds);
            panel?.Dispose();
        }
        finally
        {
            CatalogUpdateTags.ResetProvidersForTests();
            UiDialog.ForgetForTests();
            UiLayoutBatch.ResetForTests();
            Progenitor.Ui.Ui.ResetForTests();
        }
    }

    // ---- readback ------------------------------------------------------------------------------

    [Fact]
    public void The_readback_names_the_forced_route()
    {
        var route = ServerRouteSelector.Decide(LiveJoin(treatAsRemote: true)).Route;
        var pane = new InstallModsPane(new InstallSidesPaneTests.FakeCatalog()) { ServerRouteProbe = () => route };
        Assert.Equal("remote (forced by TreatLocalServerAsRemote)", pane.PickerToWidget()["route"]);
        Assert.True(pane.CurrentRoute.ForcedRemote);
    }

    // ---- logging -------------------------------------------------------------------------------

    [Fact]
    public void Removals_and_marks_are_logged_with_what_they_touched()
    {
        string serverCopy = SeedServerCopy();
        var catalog = new InstallCatalogService(ClientBep, new NoTransport(), null, null, null, true, null, ServerBep, Path.Combine(_base, "nolocal"), false);
        Assert.True(catalog.LoadIndexFromJson(IndexJson(), "stamp").Ok);

        Assert.True(catalog.MarkUninstall(Key, true, InstallTarget.LocalServer).Ok);
        Assert.Contains(_log, l => l.Line.StartsWith("Install: marked for removal at the next start " + Key));
        Assert.True(catalog.CancelUninstall(Key, InstallTarget.LocalServer).Ok);
        Assert.Contains(_log, l => l.Line.StartsWith("Install: unmarked (kept) " + Key));

        var removed = catalog.RemovePackage(Key, true, InstallTarget.LocalServer);
        Assert.True(removed.Ok && !removed.NoOp);
        Assert.False(File.Exists(serverCopy));
        Assert.Contains(_log, l => l.Line.StartsWith("Install: removed " + Key + " (local server)") && l.Line.Contains(File1));

        var nothing = catalog.RemovePackage(Key, true, InstallTarget.Client);
        Assert.True(nothing.NoOp);
        Assert.Contains(_log, l => l.Line.StartsWith("Install: nothing removed for " + Key + " (client)"));
    }

    [Fact]
    public void Remote_steps_are_logged_when_sent_and_when_they_finish()
    {
        var client = RemoteInstallClient.Shared;
        client.ResetForTests();
        try
        {
            var sent = new List<RemoteInstallRequest>();
            client.Send = bytes => { Assert.True(RemoteInstallWire.TryDecodeRequest(bytes, out var r)); sent.Add(r); return true; };
            client.Accept(Status());
            int nonce = client.Begin(RemoteInstallOp.Uninstall, Key, "", false, out string? refusal);
            Assert.True(nonce > 0, refusal);
            Assert.Contains(_log, l => l.Line.StartsWith("Install: remote Uninstall " + Key) && l.Line.Contains("sent"));

            client.Accept(new RemoteInstallProgress(nonce, RemoteInstallOp.Uninstall, RemoteInstallStage.PendingRestart, RemoteInstallReason.None, Key, "", "removed at the next server restart"));
            Assert.Contains(_log, l => l.Line.StartsWith("Install: remote Uninstall " + Key + " finished: PendingRestart"));
        }
        finally
        {
            client.ResetForTests();
        }
    }
}
