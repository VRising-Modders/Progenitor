using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// INSTALLSIMPLE1: the Install tab's Simple / Advanced view — the remembered view, which listings
/// Simple shows, the combined status / button / targets of one Simple row, the Show chips' counts,
/// the click keys and the readback. Game-free: the pane against a fake catalog and the recording UI
/// runtime; the combine rule directly against <see cref="SideCells.Decide"/> decisions.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class InstallSimpleViewTests : IDisposable
{
    private readonly CellRowTests.FakeCellRuntime _runtime = new();
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-simple-" + Guid.NewGuid().ToString("N"));

    public InstallSimpleViewTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        UiDialog.ForgetForTests();
        UiLayoutBatch.ResetForTests();
        CatalogUpdateTags.ResetProvidersForTests();
        CatalogUpdateTags.ConfiguredTagProvider = () => null;
        CatalogUpdateTags.BuildProvider = () => InstallSearchFilterTests.KnownClientBuild;
        InstallModsPane.ProcessFilterProbe = null;
        InstallModsPane.PlainOfferProbe = null;
        InstallModsPane.RememberedPick = null;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var p in _panels) p.Dispose();
        UiDialog.ForgetForTests();
        InstallModsPane.RememberedPick = null;
        UiLayoutBatch.ResetForTests();
        CatalogUpdateTags.ResetProvidersForTests();
        UiApi.ResetForTests();
        try { Directory.Delete(_root, true); } catch { }
    }

    private IUiPanel NewPanel()
    {
        var panel = UiApi.Panel("Install mods", _owner, "example.simple");
        _panels.Add(panel);
        return panel;
    }

    /// <summary>The remembered view, as <c>[Mods] InstallView</c> would hold it.</summary>
    private sealed class Store
    {
        public string? Value;
        public int Writes;
    }

    private sealed class Rig
    {
        private readonly InstallSimpleViewTests _owner;
        private IUiPanel? _panel;

        public Rig(InstallSimpleViewTests owner, InstallSidesPaneTests.FakeCatalog catalog, Store? store, Action<InstallModsPane>? setup = null)
        {
            _owner = owner;
            Catalog = catalog;
            Pane = new InstallModsPane(catalog, new NeverClock(), null);
            Pane.SetDetailRebuild(Rebuild);
            Pane.DialogPanelFactory = _ => owner.NewPanel();
            if (store != null)
            {
                Pane.ViewProbe = () => store.Value;
                Pane.ViewSink = v => { store.Value = v; store.Writes++; };
            }
            setup?.Invoke(Pane);
            Rebuild();
        }

        public InstallSidesPaneTests.FakeCatalog Catalog { get; }
        public InstallModsPane Pane { get; }

        public void Rebuild()
        {
            _panel?.Dispose();
            _panel = _owner.NewPanel();
            Pane.Build(_panel, true, false, null, () => { }, () => { }, DateTime.UtcNow);
        }

        public bool Click(string label) => UiCellRowHandle.TryInvoke(label, out _);

        public CellSpec Cell(string key, string cell) =>
            Pane.Rows.Single(r => r.Row.Key == key).Row.Cells.Single(c => c.Key == cell);

        public string[] Shown() => Pane.Rows.Where(r => r.Row.Visible).Select(r => r.Entry.Key).OrderBy(k => k).ToArray();
    }

    private static CatalogEntry Listing(
        string key, string[]? categories = null, string? description = null, string[]? dependencies = null, long? size = null,
        long downloads = 10, string released = "2026-09-11T00:00:00Z") =>
        new(
            new ManifestMod("", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, "o", key),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-12T00:00:00Z",
            displayName: key.Substring(2),
            description: description ?? "one-liner of " + key,
            foreignDependencies: dependencies,
            listingFileSize: size,
            owner: "o",
            packageName: key.Substring(2),
            categories: categories ?? new[] { "Client", "Oakveil Update" },
            releasedUtc: released,
            downloads: downloads);

    private static readonly string[] ClientTags = { "Client", "Oakveil Update" };
    private static readonly string[] ServerTags = { "Server", "Oakveil Update" };
    private static readonly string[] BothTags = { "Client", "Server", "Oakveil Update" };

    private static ServerPickerInputs Inputs(bool connected) => new()
    {
        Connected = connected,
        ConnectedServer = ServerIdentity.Connected("Forms Test", "127.0.0.1", 9976),
    };

    /// <summary>Connected to a server that authorized this player (the remote route is open).</summary>
    private static void AsAdmin(InstallModsPane pane, Func<CatalogEntry, (bool, string?)>? serverHas = null)
    {
        pane.ServerPickerProbe = () => Inputs(connected: true);
        pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.None);
        pane.ServerHandshakeProbe = serverHas ?? (_ => (true, null));
    }

    /// <summary>Connected to a server that does not count this player as an admin.</summary>
    private static void AsPlayer(InstallModsPane pane, Func<CatalogEntry, (bool, string?)>? serverHas = null)
    {
        pane.ServerPickerProbe = () => Inputs(connected: true);
        pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NotAdmin);
        pane.AdminOfferProbe = () => AdminOffer.AdminOnly;
        pane.ServerHandshakeProbe = serverHas ?? (_ => (true, null));
    }

    private static void NotConnected(InstallModsPane pane) => pane.ServerPickerProbe = () => Inputs(connected: false);

    private InstalledStateQuery ClientInstalls(params (string Key, string Plugin, string Version)[] mods)
    {
        var files = new List<InstalledFile>();
        var ledger = new List<InstalledLedgerRow>();
        var inventory = new List<InstalledInventoryRow>();
        foreach (var (key, plugin, version) in mods)
        {
            string reload = Path.Combine(_root, "client", "BepInEx", "progenitor-reload", plugin + ".dll");
            files.Add(new InstalledFile(reload, InstalledState.ReloadKind, "sha-" + key, InstalledState.ClientRoot, 10));
            ledger.Add(new InstalledLedgerRow(key, "sha-" + key, version, plugin + ".dll", source: "package:" + key + "@" + version,
                category: ServerInstallLedger.CategoryMod, root: InstalledState.ClientRoot, filePath: reload));
            inventory.Add(new InstalledInventoryRow(plugin, version, "Local"));
        }
        return new InstalledStateQuery(files, inventory, ledger);
    }

    // ---- The view and its memory ------------------------------------------------------------------

    [Fact]
    public void Simple_is_the_default_and_anything_but_Advanced_reads_Simple()
    {
        Assert.Equal(InstallView.Simple, InstallViewSetting.Default);
        Assert.Equal(InstallView.Simple, InstallViewSetting.Parse(null));
        Assert.Equal(InstallView.Simple, InstallViewSetting.Parse(""));
        Assert.Equal(InstallView.Simple, InstallViewSetting.Parse("Simple"));
        Assert.Equal(InstallView.Simple, InstallViewSetting.Parse("expert"));
        Assert.Equal(InstallView.Advanced, InstallViewSetting.Parse(" advanced "));
        Assert.Equal("Advanced", InstallViewSetting.ValueOf(InstallView.Advanced));
        Assert.Equal("Simple", InstallViewSetting.ValueOf(InstallView.Simple));
        // The cfg binding's default is Simple, under [Mods].
        string config = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ConfigurationPane.cs");
        Assert.Contains("InstallViewSetting.ConfigKey,", config, StringComparison.Ordinal);
        Assert.Contains("InstallViewSetting.SimpleName,", config, StringComparison.Ordinal);
        // The game wires the pane to it.
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        Assert.Contains("_installPane.ViewProbe = ", menu, StringComparison.Ordinal);
        Assert.Contains("_installPane.ViewSink = ", menu, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unwired_pane_is_the_Advanced_screen_with_no_switch()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.Add(Listing("o-a"));
        var rig = new Rig(this, catalog, store: null);
        Assert.Equal(InstallView.Advanced, rig.Pane.View);
        Assert.False(rig.Pane.ViewSwitchShown);
        Assert.DoesNotContain(rig.Pane.ToolbarRow!.Cells, c => c.Key == InstallModsPane.CellViewSimple);
        Assert.Contains(rig.Pane.Rows[0].Row.Cells, c => c.Key == InstallModsPane.CellClientA);
        Assert.False(rig.Pane.SetView(InstallView.Simple));
    }

    [Fact]
    public void The_switch_picks_the_view_and_the_choice_is_remembered()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.Add(Listing("o-a"));
        var store = new Store();
        var rig = new Rig(this, catalog, store, NotConnected);
        var pane = rig.Pane;

        // Nothing stored yet: Simple, the switch at the left of the search row, Simple on the gold face.
        Assert.Equal(InstallView.Simple, pane.View);
        var toolbar = pane.ToolbarRow!.Cells;
        Assert.Equal(new[] { InstallModsPane.CellViewSimple, InstallModsPane.CellViewAdvanced, InstallModsPane.CellSearch },
            toolbar.Take(3).Select(c => c.Key).ToArray());
        Assert.Equal(CellTone.ChipOn, toolbar[0].Tone);
        Assert.Equal(CellTone.ChipOff, toolbar[1].Tone);
        Assert.Equal(InstallSimpleCopy.ViewSimple, toolbar[0].Text);
        Assert.Equal(InstallSimpleCopy.ViewAdvanced, toolbar[1].Text);
        // Simple: no server picker, no side columns, one filter row.
        Assert.DoesNotContain(toolbar, c => c.Key == InstallModsPane.CellServerPick);
        Assert.DoesNotContain(pane.Rows[0].Row.Cells, c => c.Key == InstallModsPane.CellClient);
        Assert.Equal(new[] { InstallModsPane.FiltersKey }, pane.FilterRows.Select(r => r.Key).ToArray());

        // Advanced through the bridge's click key: written once, today's screen back.
        Assert.True(rig.Click(InstallModsPane.ToolbarKey + "/" + InstallModsPane.CellViewAdvanced));
        Assert.Equal("Advanced", store.Value);
        Assert.Equal(InstallView.Advanced, pane.View);
        Assert.Contains(pane.ToolbarRow!.Cells, c => c.Key == InstallModsPane.CellServerPick);
        Assert.Contains(pane.Rows[0].Row.Cells, c => c.Key == InstallModsPane.CellClientA);
        Assert.Equal(new[] { InstallModsPane.FiltersKey, InstallModsPane.FiltersOnlyKey }, pane.FilterRows.Select(r => r.Key).ToArray());
        Assert.Equal(CellTone.ChipOn, pane.ToolbarRow!.Cells.Single(c => c.Key == InstallModsPane.CellViewAdvanced).Tone);

        // Remembered: a new pane (a new session, a new Core) over the same setting opens in Advanced.
        var next = new Rig(this, new InstallSidesPaneTests.FakeCatalog(), store, NotConnected);
        Assert.Equal(InstallView.Advanced, next.Pane.View);

        // And back to Simple.
        Assert.True(rig.Click(InstallModsPane.ToolbarKey + "/" + InstallModsPane.CellViewSimple));
        Assert.Equal("Simple", store.Value);
        Assert.Equal(InstallView.Simple, pane.View);

        // The readback names the view.
        Assert.True(pane.TryDescribeSidebarRow(pane.ToolbarRow!, out var extra));
        var view = (Dictionary<string, object?>)extra!["view"]!;
        Assert.Equal("simple", view["mode"]);
        Assert.Equal("Simple", view["stored"]);
        Assert.Equal(true, view["switchShown"]);
    }

    // ---- Which listings Simple shows ----------------------------------------------------------------

    [Fact]
    public void Simple_hides_tools_non_mods_and_libraries_and_Advanced_lists_them()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.AddRange(new[]
        {
            Listing("o-mod"),
            Listing("o-tool", new[] { "Tools", "Client", "Oakveil Update" }),
            Listing("o-huge", size: 400L * 1024 * 1024),
            Listing("o-lib", description: "Plugin framework and utilities for mods."),
            Listing("o-user", dependencies: new[] { "o-lib-1.0.0", "o-camera-1.0.0" }),
            // Depended on, but a mod by its own words: listed.
            Listing("o-camera", description: "Makes the camera more like an MMO camera."),
            // Thunderstore's own Libraries tag.
            Listing("o-tagged", new[] { "Libraries", "Client", "Oakveil Update" }),
            // A framework nothing depends on is a mod too.
            Listing("o-lonely", description: "A tiny framework of my own."),
        });
        var rig = new Rig(this, catalog, new Store(), NotConnected);
        var pane = rig.Pane;
        Assert.Equal(new[] { "o-camera", "o-lonely", "o-mod", "o-user" }, rig.Shown());
        string Hidden(string key) => pane.SimpleRowFor(catalog.Entries.Single(e => e.Key == key)).HiddenReason;
        Assert.Equal(InstallSimpleRules.HiddenTool, Hidden("o-tool"));
        Assert.Equal(InstallSimpleRules.HiddenTool, Hidden("o-huge"));
        Assert.Equal(InstallSimpleRules.HiddenLibrary, Hidden("o-lib"));
        Assert.Equal(InstallSimpleRules.HiddenLibrary, Hidden("o-tagged"));

        // Advanced lists every one of them, exactly as before.
        pane.SetView(InstallView.Advanced);
        Assert.Equal(catalog.Entries.Select(e => e.Key).OrderBy(k => k).ToArray(), rig.Shown());
    }

    [Fact]
    public void The_library_rule_reads_dependencies_and_the_one_liner()
    {
        Assert.Equal("dyno-ExampleCommandFramework", InstallLibraries.PackageKeyOf("dyno-ExampleCommandFramework-0.10.4"));
        Assert.Equal("", InstallLibraries.PackageKeyOf("nodash"));
        Assert.True(InstallLibraries.DescribesLibrary("Command framework for developers"));
        Assert.True(InstallLibraries.DescribesLibrary("V Rising API Modding Platform"));
        Assert.True(InstallLibraries.DescribesLibrary("Example.Core a help library for mod development."));
        Assert.False(InstallLibraries.DescribesLibrary("Leveling, expertise, legacies, professions"));
        Assert.False(InstallLibraries.DescribesLibrary("Rapid fire"));     // "api" inside a word is not the word
        Assert.False(InstallLibraries.DescribesLibrary(null));
        var lib = Listing("o-lib", description: "Plugin framework.");
        var user = Listing("o-user", dependencies: new[] { "o-lib-1.0.0", "o-user-1.0.0" });
        var dependedOn = InstallLibraries.DependedOn(new[] { lib, user });
        Assert.Contains("o-lib", dependedOn);
        Assert.DoesNotContain("o-user", dependedOn);      // a self-reference never counts
        Assert.True(InstallLibraries.IsLibrary(lib, dependedOn));
        Assert.False(InstallLibraries.IsLibrary(lib, new HashSet<string>()));
    }

    /// <summary>
    /// Settings organization stage 8 (decision 7): Simple's search row ends in <b>Server settings »</b> while Simple acts on the
    /// connected server and this player is an admin of it; the click opens Progenitor's page at Server settings.
    /// </summary>
    [Fact]
    public void Server_settings_link_is_on_Simple_for_an_admin_acting_on_the_server()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.Add(Listing("o-c", ClientTags));
        int opened = 0;
        bool adminThere = true;
        void Wire(InstallModsPane p)
        {
            p.ServerSettingsAdminProbe = () => adminThere;
            p.OpenServerSettings = () => opened++;
        }

        static string[] Keys(Rig r) => r.Pane.IsSimple ? r.Pane.SimpleServerRow!.Cells.Select(c => c.Key).ToArray() : r.Pane.ToolbarRow!.Cells.Select(c => c.Key).ToArray();

        var admin = new Rig(this, catalog, new Store(), p => { AsAdmin(p); Wire(p); });
        Assert.Contains(InstallModsPane.CellServerSettings, Keys(admin));
        var cell = admin.Pane.SimpleServerRow!.Cells.Single(c => c.Key == InstallModsPane.CellServerSettings);
        Assert.Equal(InstallSimpleCopy.ServerSettingsLink, cell.Text);
        var link = admin.Pane.ServerSettingsLinkToWidget();
        Assert.Equal(true, link["shown"]);
        // Three homes: the link opens the Server tab.
        Assert.Equal(SettingsLinks.TargetServerTab, link["target"]);
        Assert.Equal(SettingsLinks.LandingTop, link["landing"]);
        Assert.True(admin.Click("simple-server/" + InstallModsPane.CellServerSettings));
        Assert.Equal(1, opened);

        // Advanced has its own server picker and no link.
        admin.Pane.SetView(InstallView.Advanced);
        admin.Rebuild();
        Assert.DoesNotContain(InstallModsPane.CellServerSettings, Keys(admin));

        // Not an admin of the joined server, or a player: the link still shows (the Server tab is read-only for them).
        // Not connected: no link.
        adminThere = false;
        var notAdmin = new Rig(this, catalog, new Store(), p => { AsAdmin(p); Wire(p); });
        Assert.Contains(InstallModsPane.CellServerSettings, Keys(notAdmin));
        Assert.True(notAdmin.Pane.PressServerSettings());
        adminThere = true;
        Assert.Contains(InstallModsPane.CellServerSettings, Keys(new Rig(this, catalog, new Store(), p => { AsPlayer(p); Wire(p); })));
        Assert.DoesNotContain(InstallModsPane.CellServerSettings, Keys(new Rig(this, catalog, new Store(), p => { NotConnected(p); Wire(p); })));
        Assert.Equal(2, opened);

        // Three homes: the live wiring opens the Server tab, for an admin of the joined server.
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        Assert.Contains("_installPane.ServerSettingsAdminProbe = ServerTabAdmin;", menu, StringComparison.Ordinal);
        Assert.Contains("_installPane.OpenServerSettings = OpenServerTabFromLink;", menu, StringComparison.Ordinal);
    }

    [Fact]
    public void A_server_only_mod_is_listed_only_for_the_connected_servers_admin()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.AddRange(new[] { Listing("o-c", ClientTags), Listing("o-s", ServerTags), Listing("o-b", BothTags) });

        var offline = new Rig(this, catalog, new Store(), NotConnected);
        Assert.Equal(new[] { "o-b", "o-c" }, offline.Shown());
        Assert.False(offline.Pane.SimpleActsOnServer);
        Assert.Equal(InstallSimpleRules.HiddenServerOnly, offline.Pane.SimpleRowFor(catalog.Entries[1]).HiddenReason);

        var player = new Rig(this, catalog, new Store(), p => AsPlayer(p));
        Assert.Equal(new[] { "o-b", "o-c" }, player.Shown());
        Assert.False(player.Pane.SimpleActsOnServer);

        var admin = new Rig(this, catalog, new Store(), p => AsAdmin(p));
        Assert.Equal(new[] { "o-b", "o-c", "o-s" }, admin.Shown());
        Assert.True(admin.Pane.SimpleActsOnServer);
        var row = admin.Pane.SimpleRowFor(catalog.Entries[1]);
        Assert.Equal(SideButton.Install, row.Button);
        Assert.Equal(InstallSimpleTargets.Server, row.Targets);
    }

    /// <summary>
    /// SIMPLESCOPE1 (project rule 2026-10-07): Simple says what it acts on and why; for a player who is not the joined server's
    /// admin a both-sides mod acts on this PC's half with a muted note that the admins manage the server half, and every
    /// confirmation that leaves a side alone names it under <c>Not changed</c>. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void Simple_states_its_scope_and_every_box_names_the_side_it_leaves_alone()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.AddRange(new[] { Listing("o-c", ClientTags), Listing("o-s", ServerTags), Listing("o-b", BothTags) });
        var both = catalog.Entries[2];

        var offline = new Rig(this, catalog, new Store(), NotConnected);
        Assert.Equal(("client-only", "not connected to a server"), offline.Pane.SimpleScope);
        Assert.Equal("", offline.Pane.SimpleServerHalfNote(both));

        var player = new Rig(this, catalog, new Store(), p => AsPlayer(p));
        Assert.Equal(("client-only", "not an admin on the connected server"), player.Pane.SimpleScope);
        var view = player.Pane.ViewToWidget();
        Assert.Equal("client-only", view["simpleScope"]);
        Assert.Equal("not an admin on the connected server", view["simpleScopeReason"]);
        string note = player.Pane.SimpleServerHalfNote(both);
        Assert.Contains("its admins manage it", note, StringComparison.Ordinal);
        Assert.Contains(player.Pane.DrawerLines(both, player.Pane.ActionFor(both), out _), l => l.Text == note);
        Assert.Equal("", player.Pane.SimpleServerHalfNote(catalog.Entries[0]));
        Assert.True(player.Click("o-b/" + InstallModsPane.CellInstall));
        Assert.Equal(InstallPromptKind.Install, player.Pane.Prompt);
        var kept = Assert.Single(player.Pane.PromptFacts(both, InstallPromptKind.Install), f => f.Label == InstallSimpleCopy.FactNotChanged);
        Assert.EndsWith(": its admins manage the server half", kept.Value, StringComparison.Ordinal);
        player.Pane.PromptNo();

        var admin = new Rig(this, catalog, new Store(), p => AsAdmin(p, _ => (false, null)));
        Assert.Equal(("all", "an admin on the connected server"), admin.Pane.SimpleScope);
        Assert.Equal("", admin.Pane.SimpleServerHalfNote(both));
        // A server-only mod for the admin: the box names this client as not changed.
        Assert.True(admin.Click("o-s/" + InstallModsPane.CellInstall));
        Assert.Equal(InstallPromptKind.Install, admin.Pane.Prompt);
        Assert.DoesNotContain(admin.Pane.PromptFacts(catalog.Entries[1], InstallPromptKind.Install), f => f.Label == InstallSimpleCopy.FactNotChanged);
        admin.Pane.PromptNo();

        // Advanced: the Server cell's Install of a both-sides mod leaves this client alone, and says so.
        Assert.True(admin.Pane.SetView(InstallView.Advanced));
        Assert.True(admin.Pane.OnSideClick(both.Key, server: true, SideButton.Install));
        Assert.Equal(InstallPromptKind.Install, admin.Pane.Prompt);
        // It also offers "install on both", so it names this client by that answer instead of "Not changed".
        var facts = admin.Pane.PromptFacts(both, InstallPromptKind.Install);
        Assert.DoesNotContain(facts, f => f.Label == InstallSimpleCopy.FactNotChanged);
        Assert.StartsWith("Only with ", Assert.Single(facts, f => f.Label == InstallModsCopy.FactThisClient).Value, StringComparison.Ordinal);
        admin.Pane.PromptNo();
    }

    /// <summary>
    /// F14 (live 2026-10-07): the <c>Not changed</c> line of a this-PC-only install of a both-sides mod reads whether the
    /// connected server has a copy. An admin is told its copy stays only when it has one; a server without a copy reads
    /// <c>(it doesn't have this mod)</c>; an unknown half says nothing changes there. A player who is not its admin keeps
    /// the admins-manage wording. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void A_this_PC_only_box_says_the_server_has_no_copy_instead_of_its_copy_stays()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.Add(Listing("o-b", BothTags));
        var both = catalog.Entries[0];

        string NotChanged(Action<InstallModsPane> setup)
        {
            var rig = new Rig(this, catalog, new Store(), setup);
            Assert.True(rig.Pane.SetView(InstallView.Advanced));
            Assert.True(rig.Pane.OnSideClick(both.Key, server: false, SideButton.Install));
            Assert.Equal(InstallPromptKind.Install, rig.Pane.Prompt);
            var facts = rig.Pane.PromptFacts(both, InstallPromptKind.Install);
            string value = Assert.Single(facts, f => f.Label == InstallSimpleCopy.FactNotChanged).Value;
            rig.Pane.PromptNo();
            return value;
        }

        // The handshake says the server has no copy (known, no version).
        string none = NotChanged(p => AsAdmin(p, _ => (true, null)));
        Assert.EndsWith(" (it doesn't have this mod)", none, StringComparison.Ordinal);
        Assert.DoesNotContain("its copy stays", none, StringComparison.Ordinal);

        // The server has its copy: it stays.
        Assert.EndsWith(": its copy stays as it is", NotChanged(p => AsAdmin(p, _ => (true, "1.0.0"))), StringComparison.Ordinal);

        // Nothing has said: no claim either way.
        Assert.EndsWith(": nothing changes there", NotChanged(p => AsAdmin(p, _ => (false, null))), StringComparison.Ordinal);

        // Not its admin: the admins manage the server half, whether or not it has a copy.
        Assert.EndsWith(": its admins manage the server half", NotChanged(p => AsPlayer(p, _ => (true, null))), StringComparison.Ordinal);
        Assert.EndsWith(": its admins manage the server half", NotChanged(p => AsPlayer(p, _ => (true, "1.0.0"))), StringComparison.Ordinal);
    }

    /// <summary>
    /// F7 (live 2026-10-07): a mod the Mods list's switch holds off (a hot suspend) is not a pending install: the restart
    /// banner does not count it. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void A_switched_off_mod_is_not_counted_as_waiting_for_a_restart()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        var entry = new CatalogEntry(
            new ManifestMod("o.c", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, "o", "o-c"),
            CatalogProvenance.AdaptedListing, InstallReasons.SourceThunderstore, InstallReasons.SourceLabelThunderstore, "2026-09-12T00:00:00Z",
            displayName: "c", owner: "o", packageName: "c", categories: ClientTags, releasedUtc: "2026-09-11T00:00:00Z");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog, new Store(), NotConnected);
        string pending = Path.Combine(_root, "client", "BepInEx", "progenitor-pending", "index", "o.c.dll");
        rig.Pane.SetInstalledQuery(new InstalledStateQuery(
            new[] { new InstalledFile(pending, InstalledState.PendingKind, "sha-c", InstalledState.ClientRoot, 10) },
            Array.Empty<InstalledInventoryRow>(),
            new[] { new InstalledLedgerRow("o-c", "sha-c", "1.0.0", "o.c.dll", source: "package:o-c.0.0",
                category: ServerInstallLedger.CategoryPending, root: InstalledState.ClientRoot, filePath: pending) }));
        bool off = false;
        rig.Pane.SwitchedOffProbe = id => off && id == "o.c";
        Assert.Single(rig.Pane.ClientPendingEntries());
        off = true;
        Assert.Empty(rig.Pane.ClientPendingEntries());

        // F2: every Mods-page action and the switch read fresh state afterwards.
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        Assert.Contains("RefreshAfterPageAction();", menu, StringComparison.Ordinal);
        Assert.Contains("if (CurrentSidebarModel(applySearch: false).Find(pluginId) == null)", menu, StringComparison.Ordinal);
        Assert.Contains("RefreshAfterPageAction();", SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.ModList.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Simple_lists_the_current_game_update_but_keeps_an_installed_older_mod()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.AddRange(new[]
        {
            Listing("o-new"),
            Listing("o-old", new[] { "Client", "Gloomrot Update" }),
            Listing("o-kept", new[] { "Client", "Gloomrot Update" }),
        });
        var rig = new Rig(this, catalog, new Store(), NotConnected);
        rig.Pane.SetInstalledQuery(ClientInstalls(("o-kept", "o.kept", "1.0.0")));
        rig.Rebuild();
        Assert.Equal(new[] { "o-kept", "o-new" }, rig.Shown());
        Assert.Equal(InstallSimpleRules.HiddenOtherUpdate, rig.Pane.SimpleRowFor(catalog.Entries[1]).HiddenReason);
    }

    // ---- The combined row: status, button, targets ---------------------------------------------

    private static readonly SideHalf Absent = new() { State = HalfState.Absent };

    private static SideHalf Has(string version) => new() { State = HalfState.Installed, Version = version, CanUninstall = true };

    private static InstallSimpleRow Row(
        PackageSide side, SideHalf client, SideHalf server, bool admin, bool connected = true, bool needsBoth = false, string latest = "1.0.0")
    {
        var route = admin ? new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.None)
            : connected ? new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NotAdmin)
            : new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NoServer);
        var sides = SideCells.Decide(new SideCellInputs
        {
            Side = side,
            NeedsBoth = needsBoth,
            Latest = latest,
            Client = client,
            Server = connected ? server : SideHalf.None,
            Target = connected ? ServerTargetKind.Server : ServerTargetKind.None,
            Route = route,
            Admin = !admin && connected ? AdminOffer.AdminOnly : AdminOffer.None,
        });
        return InstallSimpleRules.Decide(sides, side, actsOnServer: admin);
    }

    private static void Expect(InstallSimpleRow row, string kind, string words, SideButton button, InstallSimpleTargets targets)
    {
        Assert.Equal(kind, row.Kind);
        Assert.Equal(words, row.Words);
        Assert.Equal(button, row.Button);
        Assert.Equal(targets, row.Targets);
    }

    [Fact]
    public void Not_connected_acts_on_this_PC_only()
    {
        Expect(Row(PackageSide.Client, Absent, Absent, admin: false, connected: false),
            InstallSimpleRules.KindNotInstalled, "Not installed", SideButton.Install, InstallSimpleTargets.Client);
        var installed = Row(PackageSide.Client, Has("1.0.0"), Absent, admin: false, connected: false);
        Expect(installed, InstallSimpleRules.KindInstalled, "Installed", SideButton.Uninstall, InstallSimpleTargets.Client);
        Assert.Equal("✓ Installed", installed.StatusText);
        Assert.Equal(SideCellTone.Ok, installed.Tone);
        var update = Row(PackageSide.Client, Has("0.9.0"), Absent, admin: false, connected: false);
        Expect(update, InstallSimpleRules.KindUpdate, "Update available", SideButton.Update, InstallSimpleTargets.Client);
        Assert.Equal("↑ Update available", update.StatusText);
        Assert.Equal(SideCellTone.Warn, update.Tone);
        // A both-sided mod: this PC only.
        Expect(Row(PackageSide.Both, Absent, Absent, admin: false, connected: false),
            InstallSimpleRules.KindNotInstalled, "Not installed", SideButton.Install, InstallSimpleTargets.Client);
        // A server-only mod: not listed.
        Assert.Equal(InstallSimpleRules.HiddenServerOnly, Row(PackageSide.Server, Absent, Absent, admin: false, connected: false).HiddenReason);
    }

    [Fact]
    public void Connected_without_admin_acts_on_this_PC_only()
    {
        // The server has the both-sided mod and this PC does not: Simple reads this PC alone.
        var row = Row(PackageSide.Both, Absent, Has("1.0.0"), admin: false, needsBoth: true);
        Expect(row, InstallSimpleRules.KindNotInstalled, "Not installed", SideButton.Install, InstallSimpleTargets.Client);
        Assert.Equal(InstallSimpleTargets.Client, row.Acted);
        Expect(Row(PackageSide.Both, Has("1.0.0"), Absent, admin: false),
            InstallSimpleRules.KindInstalled, "Installed", SideButton.Uninstall, InstallSimpleTargets.Client);
        Assert.Equal(InstallSimpleRules.HiddenServerOnly, Row(PackageSide.Server, Absent, Absent, admin: false).HiddenReason);
    }

    [Fact]
    public void The_admin_of_the_connected_server_acts_on_both_with_one_button()
    {
        // Both-sided, nowhere: Install on both.
        var none = Row(PackageSide.Both, Absent, Absent, admin: true);
        Expect(none, InstallSimpleRules.KindNotInstalled, "Not installed", SideButton.Install, InstallSimpleTargets.Both);
        Assert.Equal(InstallSimpleTargets.Both, none.Acted);
        // Everywhere: Uninstall both.
        Expect(Row(PackageSide.Both, Has("1.0.0"), Has("1.0.0"), admin: true),
            InstallSimpleRules.KindInstalled, "Installed", SideButton.Uninstall, InstallSimpleTargets.Both);
        // One side missing: say which, and Install installs that side.
        var serverMissing = Row(PackageSide.Both, Has("1.0.0"), Absent, admin: true, needsBoth: true);
        Expect(serverMissing, InstallSimpleRules.KindMissingServer, "Missing on server", SideButton.Install, InstallSimpleTargets.Server);
        Assert.Equal("!", serverMissing.Glyph);
        Assert.Equal(SideCellTone.Warn, serverMissing.Tone);
        Expect(Row(PackageSide.Both, Absent, Has("1.0.0"), admin: true, needsBoth: true),
            InstallSimpleRules.KindMissingClient, "Missing on this PC", SideButton.Install, InstallSimpleTargets.Client);
        // Updates: every side that is behind.
        Expect(Row(PackageSide.Both, Has("0.9.0"), Has("1.0.0"), admin: true),
            InstallSimpleRules.KindUpdate, "Update available", SideButton.Update, InstallSimpleTargets.Client);
        Expect(Row(PackageSide.Both, Has("0.9.0"), Has("0.9.0"), admin: true),
            InstallSimpleRules.KindUpdate, "Update available", SideButton.Update, InstallSimpleTargets.Both);
        // Server-only: the server.
        Expect(Row(PackageSide.Server, Absent, Absent, admin: true),
            InstallSimpleRules.KindNotInstalled, "Not installed", SideButton.Install, InstallSimpleTargets.Server);
        Expect(Row(PackageSide.Server, Absent, Has("1.0.0"), admin: true),
            InstallSimpleRules.KindInstalled, "Installed", SideButton.Uninstall, InstallSimpleTargets.Server);
        // Client-only: this PC (the server cell is not used).
        Expect(Row(PackageSide.Client, Absent, Absent, admin: true),
            InstallSimpleRules.KindNotInstalled, "Not installed", SideButton.Install, InstallSimpleTargets.Client);
    }

    [Fact]
    public void Pending_and_running_states_keep_their_buttons()
    {
        var pending = new SideHalf { State = HalfState.Pending, Version = "1.0.0", CanCancelInstall = true };
        var leaving = new SideHalf { State = HalfState.RemovalPending, Version = "1.0.0", CanCancelUninstall = true };
        var busy = new SideHalf { Busy = "Downloading…", BusyCancelable = true };
        Expect(Row(PackageSide.Client, pending, Absent, admin: false, connected: false),
            InstallSimpleRules.KindPending, "Loads at next start", SideButton.CancelInstall, InstallSimpleTargets.Client);
        Expect(Row(PackageSide.Client, leaving, Absent, admin: false, connected: false),
            InstallSimpleRules.KindRemovalPending, "Removed at next start", SideButton.CancelUninstall, InstallSimpleTargets.Client);
        // Admin: a pending server copy with this PC's copy installed: the pending one leads.
        Expect(Row(PackageSide.Both, Has("1.0.0"), pending, admin: true),
            InstallSimpleRules.KindPending, "Loads at next start", SideButton.CancelInstall, InstallSimpleTargets.Server);
        Expect(Row(PackageSide.Both, pending, pending, admin: true),
            InstallSimpleRules.KindPending, "Loads at next start", SideButton.CancelInstall, InstallSimpleTargets.Both);
        var running = Row(PackageSide.Both, Absent, busy, admin: true);
        Expect(running, InstallSimpleRules.KindBusy, "Downloading…", SideButton.CancelInstall, InstallSimpleTargets.Server);
        Assert.Equal(SideCellTone.Pending, running.Tone);
        // A non-admin never reads the server's pending copy.
        Expect(Row(PackageSide.Both, Has("1.0.0"), pending, admin: false),
            InstallSimpleRules.KindInstalled, "Installed", SideButton.Uninstall, InstallSimpleTargets.Client);
    }

    [Fact]
    public void Simple_and_Advanced_read_the_same_side_decision()
    {
        // The combined row is a pure function of the cells the Advanced row draws, so the two views agree.
        string pane = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.Simple.cs");
        Assert.Contains("InstallSimpleRules.Decide(SideCellsCached(entry), SideOf(entry), SimpleActsOnServer)", pane, StringComparison.Ordinal);
        // An admin's acting is the existing route decision (remote route or the local-folder route), never a new check.
        Assert.Contains("route.Kind is ServerRouteKind.Local or ServerRouteKind.Remote", pane, StringComparison.Ordinal);
    }

    // ---- The pane: chips, counts, clicks, readback ---------------------------------------------------

    [Fact]
    public void Chips_count_all_installed_and_updates_with_the_search()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.AddRange(new[] { Listing("o-a"), Listing("o-b"), Listing("o-c"), Listing("o-tool", new[] { "Tools", "Oakveil Update" }) });
        var rig = new Rig(this, catalog, new Store(), NotConnected);
        var pane = rig.Pane;
        pane.SetInstalledQuery(ClientInstalls(("o-a", "o.a", "1.0.0"), ("o-b", "o.b", "0.9.0")));
        rig.Rebuild();

        var counts = pane.SimpleCounts();
        Assert.Equal(3, counts[InstallSimpleShow.All]);
        Assert.Equal(2, counts[InstallSimpleShow.Installed]);
        Assert.Equal(1, counts[InstallSimpleShow.Updates]);
        var cells = pane.FilterRows[0].Cells;
        Assert.Equal(new[] { InstallModsPane.CellShowCaption, InstallModsPane.CellShowAll, InstallModsPane.CellShowInstalled, InstallModsPane.CellShowUpdate },
            cells.Select(c => c.Key).ToArray());
        Assert.Equal("All 3", cells[1].Text);
        Assert.Equal("Installed 2", cells[2].Text);
        Assert.Equal("Updates 1", cells[3].Text);
        Assert.Equal(CellTone.ChipOn, cells[1].Tone);
        // Updates keeps the amber attention rim while it has matches.
        Assert.Equal(CellTone.ChipAttention, cells[3].Tone);
        Assert.Equal(CellTone.ChipOff, InstallModsPane.SimpleChipTone(false, InstallSimpleShow.Updates, 0));
        Assert.Equal(CellTone.ChipOff, InstallModsPane.SimpleChipTone(false, InstallSimpleShow.Installed, 4));

        // The chips filter (click keys filters/show-*), the search narrows the counts.
        Assert.True(rig.Click(InstallModsPane.FiltersKey + "/" + InstallModsPane.CellShowUpdate));
        Assert.Equal(new[] { "o-b" }, rig.Shown());
        Assert.True(rig.Click(InstallModsPane.FiltersKey + "/" + InstallModsPane.CellShowInstalled));
        Assert.Equal(new[] { "o-a", "o-b" }, rig.Shown());
        Assert.False(pane.AtDefaults);
        pane.Search = "o-c";
        counts = pane.SimpleCounts();
        Assert.Equal(1, counts[InstallSimpleShow.All]);
        Assert.Equal(0, counts[InstallSimpleShow.Installed]);
        pane.Search = "";
        Assert.True(rig.Click(InstallModsPane.FiltersKey + "/" + InstallModsPane.CellShowAll));
        Assert.True(pane.AtDefaults);
        // Advanced's own Show filter is untouched by Simple's chips.
        Assert.Equal(InstallShowFilter.All, pane.Filters.Show);

        // Readback.
        Assert.True(pane.TryDescribeSidebarRow(pane.FilterRows[0], out var extra));
        var filters = (Dictionary<string, object?>)extra!["filters"]!;
        Assert.Equal("simple", filters["view"]);
        Assert.Equal("all", filters["show"]);
        var c = (Dictionary<string, object?>)filters["counts"]!;
        Assert.Equal(3, c["all"]);
        Assert.Equal(2, c["installed"]);
        Assert.Equal(1, c["update"]);
    }

    /// <summary>
    /// REQRULE6 (user report 2026-10-08): Simple view, a both-sides mod this game has and the connected server lacks
    /// ("! Missing on server"), Install. The box said Not changed / This client: its copy stays as it is, and the user could
    /// not tell the Yes installs only the server's copy. The title names the server, the first fact says the server only and
    /// that this game already has it, the server row says it is remote and shows no folder, and no Not changed line repeats
    /// it. Every fact fits the box (at most two lines). Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void A_server_only_install_says_so_in_its_title_and_first_fact()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.Add(Listing("o-b", BothTags));
        var both = catalog.Entries[0];
        var rig = new Rig(this, catalog, new Store(), p =>
        {
            AsAdmin(p, _ => (true, null));
            p.ServerIdentityProbe = remote => remote ? ServerIdentity.Connected("Forms Test", "127.0.0.1", 9976) : ServerIdentity.Local("", "");
        });
        rig.Pane.SetInstalledQuery(ClientInstalls(("o-b", "o.b", "1.0.0")));
        rig.Rebuild();
        Assert.True(rig.Click("o-b/" + InstallModsPane.CellInstall));
        Assert.Equal(InstallPromptKind.Install, rig.Pane.Prompt);

        Assert.Equal("Install b 1.0.0 on Forms Test?", rig.Pane.PromptHeadingText(both, InstallPromptKind.Install));
        var facts = rig.Pane.PromptFacts(both, InstallPromptKind.Install);
        Assert.Equal(InstallModsCopy.FactMod, facts[0].Label);
        Assert.Equal((InstallModsCopy.FactInstallsOn, InstallModsCopy.ServerOnlyClientHasIt), (facts[1].Label, facts[1].Value));
        Assert.Equal((InstallModsCopy.FactServer, "Forms Test (remote)"), (facts[2].Label, facts[2].Value));
        Assert.Contains(facts, f => f.Label == InstallModsCopy.FactAddress);
        Assert.DoesNotContain(facts, f => f.Label == InstallModsCopy.FactFolder);
        Assert.DoesNotContain(facts, f => f.Label == InstallSimpleCopy.FactNotChanged);
        float label = DialogFacts.LabelWidth(facts);
        Assert.All(facts, f => Assert.InRange(DialogFacts.ValueLines(f.Value, DialogFacts.ValueWidth(label)), 1, 2));
        rig.Pane.PromptNo();
    }

    [Fact]
    public void The_target_words_name_this_game_the_server_or_both()
    {
        Assert.Equal("this game", InstallModsCopy.TargetWords(true, false, "Forms Test"));
        Assert.Equal("Forms Test", InstallModsCopy.TargetWords(false, true, "Forms Test"));
        Assert.Equal("this game and Forms Test", InstallModsCopy.TargetWords(true, true, "Forms Test"));
        Assert.Equal("Install HotDemo 0.1.0 on this game and Forms Test?", InstallModsCopy.PromptInstallHeadingOn("HotDemo", "0.1.0", "this game and Forms Test"));
        Assert.Equal("Update HotDemo to 0.2.0 on Forms Test?", InstallModsCopy.PromptUpdateHeadingOn("HotDemo", "0.2.0", "Forms Test"));
        Assert.Equal("Uninstall HotDemo from this game?", InstallModsCopy.PromptUninstallHeadingFrom("HotDemo", "this game"));
        Assert.Equal("Cancel installing HotDemo on Forms Test?", InstallModsCopy.PromptCancelInstallHeadingOn("HotDemo", "Forms Test"));
        Assert.Equal("Forms Test (on this PC)", InstallModsCopy.ServerFactValue(ServerIdentity.Local("Forms Test", "C:/srv")));
        Assert.Equal("Forms Test (remote)", InstallModsCopy.ServerFactValue(ServerIdentity.Connected("Forms Test", "127.0.0.1", 9976)));
        // The Mods page's uninstall heading follows its plan's sides.
        Assert.Equal("Uninstall HotDemo 0.1.0 from this game and Forms Test?", ModPageCopy.UninstallHeadingFrom("HotDemo", "0.1.0", "this game and Forms Test"));
    }

    [Fact]
    public void A_row_has_icon_mod_released_downloads_version_status_and_one_button()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.AddRange(new[]
        {
            Listing("o-a", downloads: 5000, released: "2026-09-10T00:00:00Z"),
            Listing("o-b", downloads: 20, released: "2026-09-12T00:00:00Z"),
        });
        var rig = new Rig(this, catalog, new Store(), NotConnected);
        rig.Pane.SetInstalledQuery(ClientInstalls(("o-b", "o.b", "1.0.0")));
        rig.Pane.ColumnRowWidthProbe = () => InstallColumns.LiveRowWidth;
        rig.Rebuild();
        var keys = rig.Pane.Rows[0].Row.Cells.Select(c => c.Key).ToArray();
        Assert.Equal(new[] { InstallModsPane.CellExpand, InstallModsPane.CellIcon, InstallModsPane.CellName, InstallModsPane.CellShare, InstallModsPane.CellReleased,
            InstallModsPane.CellDownloads, InstallModsPane.CellVersion, InstallModsPane.CellInstallStatus, InstallModsPane.CellInstall }, keys);
        Assert.Equal(InstallModsPane.SimpleKeys, keys);
        // Released and Downloads are Advanced's cells and formats.
        var a = catalog.Entries[0];
        Assert.Equal(rig.Pane.ReleasedShortText(a), rig.Cell("o-a", InstallModsPane.CellReleased).Text);
        Assert.Equal("5,000", rig.Cell("o-a", InstallModsPane.CellDownloads).Text);
        Assert.Equal(InstallColumns.DownloadsWidth, rig.Cell("o-a", InstallModsPane.CellDownloads).MinWidth, 3);
        Assert.Equal(InstallColumns.DateWidth, rig.Cell("o-a", InstallModsPane.CellReleased).MinWidth, 3);

        // Laid out left to right: Mod, Released, Downloads, Version, Status, the button at the far right,
        // the status words immediately left of the button.
        var cells = rig.Pane.Rows[0].Row.Cells;
        var placed = InstallColumns.Resolve(InstallColumns.LiveRowWidth, cells);
        float X(string key) => placed.Single(p => p.Key == key).X;
        Assert.True(X(InstallModsPane.CellName) < X(InstallModsPane.CellReleased));
        Assert.True(X(InstallModsPane.CellReleased) < X(InstallModsPane.CellDownloads));
        Assert.True(X(InstallModsPane.CellDownloads) < X(InstallModsPane.CellVersion));
        Assert.True(X(InstallModsPane.CellVersion) < X(InstallModsPane.CellInstallStatus));
        Assert.True(X(InstallModsPane.CellInstallStatus) < X(InstallModsPane.CellInstall));
        var status = placed.Single(p => p.Key == InstallModsPane.CellInstallStatus);
        var button = placed.Single(p => p.Key == InstallModsPane.CellInstall);
        Assert.Equal(CellLayout.Gap, button.Left - (status.X + status.Width / 2f), 2);
        // The Share overlay (chat stage 3, mod links) is hidden until the pointer is on the row.
        Assert.All(placed.Where(p => p.Key != InstallModsPane.CellShare), p => Assert.True(p.Visible, p.Key + " dropped"));
        Assert.False(placed.Single(p => p.Key == InstallModsPane.CellShare).Visible);
        // On a narrow pane the optional columns drop in Advanced's order, except the active sort column
        // (Released, the default) which never drops; Status and the button never do.
        var narrow = InstallColumns.Resolve(760f, cells);
        Assert.False(narrow.Single(p => p.Key == InstallModsPane.CellDownloads).Visible);
        Assert.True(narrow.Single(p => p.Key == InstallModsPane.CellReleased).Visible);
        Assert.True(narrow.Single(p => p.Key == InstallModsPane.CellInstallStatus).Visible);
        Assert.True(narrow.Single(p => p.Key == InstallModsPane.CellInstall).Visible);

        Assert.Equal("a\none-liner of o-a · o", rig.Cell("o-a", InstallModsPane.CellName).Text);
        Assert.Equal("1.0.0", rig.Cell("o-a", InstallModsPane.CellVersion).Text);
        Assert.Equal("Not installed", rig.Cell("o-a", InstallModsPane.CellInstallStatus).Text);
        Assert.Equal(CellTone.Muted, rig.Cell("o-a", InstallModsPane.CellInstallStatus).Tone);
        Assert.Equal("Install", rig.Cell("o-a", InstallModsPane.CellInstall).Text);
        Assert.Equal(CellKind.Button, rig.Cell("o-a", InstallModsPane.CellInstall).Kind);
        Assert.Equal("Installed", rig.Cell("o-b", InstallModsPane.CellInstallStatus).Text);
        Assert.Equal("✓", rig.Cell("o-b", InstallModsPane.CellInstallStatus).Glyph);
        Assert.Equal(CellTone.OkBright, rig.Cell("o-b", InstallModsPane.CellInstallStatus).Tone);
        Assert.Equal("Uninstall", rig.Cell("o-b", InstallModsPane.CellInstall).Text);

        // Header: Mod, Released, Downloads, Version, Status, with Advanced's captions and sorts.
        var header = rig.Pane.HeaderRow!.Cells;
        Assert.Equal(new[] { "", "", "Mod", "Released", "Downloads", "Version", "Status" }, header.Select(c => c.Text).ToArray());
        Assert.Equal(new[] { null, null, InstallModsPane.SortKeyName, InstallModsPane.SortKeyReleased, InstallModsPane.SortKeyDownloads, null, null },
            header.Select(c => c.SortKey).ToArray());
        // Default: newest released first.
        Assert.Equal("released", rig.Pane.SimpleSortName);
        Assert.Equal("desc", rig.Pane.SimpleSortDirection);
        Assert.Equal(new[] { "o-b", "o-a" }, rig.Pane.VisibleEntries().Select(e => e.Key).ToArray());
        // Downloads: most first (header/downloads), a second click reverses.
        Assert.True(rig.Click(InstallModsPane.HeaderKey + "/" + InstallModsPane.CellDownloads));
        Assert.Equal("downloads", rig.Pane.SimpleSortName);
        Assert.Equal("desc", rig.Pane.SimpleSortDirection);
        Assert.Equal(new[] { "o-a", "o-b" }, rig.Pane.VisibleEntries().Select(e => e.Key).ToArray());
        rig.Pane.SimpleHeaderSort(InstallModsPane.SortKeyDownloads);
        Assert.Equal("asc", rig.Pane.SimpleSortDirection);
        // Columns Simple does not show do not sort it.
        rig.Pane.SimpleHeaderSort(InstallModsPane.SortKeyAuthor);
        Assert.Equal("downloads", rig.Pane.SimpleSortName);
        // Mod: A to Z, then Z to A.
        rig.Pane.SimpleHeaderSort(InstallModsPane.SortKeyName);
        Assert.Equal("name", rig.Pane.SimpleSortName);
        Assert.Equal("asc", rig.Pane.SimpleSortDirection);
        Assert.Equal(new[] { "o-a", "o-b" }, rig.Pane.VisibleEntries().Select(e => e.Key).ToArray());
        rig.Pane.SimpleHeaderSort(InstallModsPane.SortKeyName);
        Assert.Equal("desc", rig.Pane.SimpleSortDirection);
        Assert.Equal(new[] { "o-b", "o-a" }, rig.Pane.VisibleEntries().Select(e => e.Key).ToArray());
        // Advanced's sort is its own.
        Assert.Equal(InstallSort.Released, rig.Pane.Sort);

        // The header and a row span the same width: Status sits over the words and the button.
        float statusHeader = header.Single(c => c.Key == InstallModsPane.CellInstallStatus).MinWidth;
        Assert.Equal(InstallModsPane.SimpleStatusWidth + CellLayout.Gap + InstallModsPane.SimpleButtonWidth, statusHeader, 3);
    }

    [Fact]
    public void The_button_runs_the_side_flows_and_the_readback_names_its_targets()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.AddRange(new[] { Listing("o-c", ClientTags), Listing("o-b", BothTags) });
        var rig = new Rig(this, catalog, new Store(), p => AsAdmin(p));
        var pane = rig.Pane;

        // Readback of a both-sides row.
        Assert.True(pane.TryDescribeSidebarRow(pane.Rows.Single(r => r.Entry.Key == "o-b").Row, out var extra));
        var simple = (Dictionary<string, object?>)((Dictionary<string, object?>)extra!["listing"]!)["simple"]!;
        Assert.Equal("not-installed", simple["kind"]);
        Assert.Equal("Install", simple["button"]);
        Assert.Equal(new List<string> { "client", "server" }, simple["targets"]);
        Assert.Equal(true, simple["listed"]);

        // One click, one confirmation that installs on both (no second "on both" yes), naming the server.
        Assert.True(rig.Click("o-b/" + InstallModsPane.CellInstall));
        Assert.Equal(InstallPromptKind.Install, pane.Prompt);
        Assert.True(pane.PromptIsSimpleBoth);
        var b = catalog.Entries.Single(e => e.Key == "o-b");
        Assert.False(pane.BothOfferFor(b, InstallPromptKind.Install).Offered);
        var (server, both) = pane.PromptServerFor(b, InstallPromptKind.Install);
        Assert.NotNull(server);
        Assert.True(both);
        pane.PromptNo();
        Assert.False(pane.PromptIsSimpleBoth);

        // A client-only mod: this PC alone.
        Assert.True(rig.Click("o-c/" + InstallModsPane.CellInstall));
        Assert.Equal(InstallPromptKind.Install, pane.Prompt);
        Assert.False(pane.PromptIsSimpleBoth);
        Assert.Null(pane.PromptServerFor(catalog.Entries.Single(e => e.Key == "o-c"), InstallPromptKind.Install).Server);
        pane.PromptNo();
    }

    [Fact]
    public void Uninstall_takes_only_the_sides_that_can_be_uninstalled()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.Add(Listing("o-b", BothTags));
        var rig = new Rig(this, catalog, new Store(), p => AsAdmin(p, _ => (true, "1.0.0")));
        var pane = rig.Pane;
        pane.SetInstalledQuery(ClientInstalls(("o-b", "o.b", "1.0.0")));
        rig.Rebuild();
        var row = pane.SimpleRowFor(catalog.Entries[0]);
        // The handshake view of the server copy carries no uninstall of its own: only this PC's copy can go.
        Assert.Equal(InstallSimpleRules.KindInstalled, row.Kind);
        Assert.Equal(SideButton.Uninstall, row.Button);
        Assert.Equal(InstallSimpleTargets.Client, row.Targets);
        Assert.Equal(InstallSimpleTargets.Both, row.Acted);
        Assert.True(rig.Click("o-b/" + InstallModsPane.CellInstall));
        Assert.Equal(InstallPromptKind.Uninstall, pane.Prompt);
        Assert.Equal("client", pane.PromptSideName);
        pane.PromptNo();
    }

    [Fact]
    public void A_both_sides_uninstall_on_the_local_route_opens_one_whole_row_confirmation()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.Add(Listing("o-b", BothTags));
        var rig = new Rig(this, catalog, new Store(), p =>
        {
            // Connected to the configured folder's server on this PC: the local-folder route.
            p.ServerPickerProbe = () => Inputs(connected: true);
            p.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None);
        });
        var pane = rig.Pane;
        pane.SetInstalledQuery(BothInstalls("o-b", "o.b", "1.0.0"));
        rig.Rebuild();
        Assert.True(pane.SimpleActsOnServer);
        var entry = catalog.Entries[0];
        var sides = pane.SideCellsFor(entry);
        Assert.True(sides.Client.Has(SideButton.Uninstall), sides.Client.Kind + " " + sides.Client.Text);
        Assert.True(sides.Server.Has(SideButton.Uninstall), sides.Server.Kind + " " + sides.Server.Text);
        var row = pane.SimpleRowFor(entry);
        Assert.Equal(InstallSimpleTargets.Both, row.Targets);
        Assert.Equal(SideButton.Uninstall, row.Button);
        Assert.True(rig.Click("o-b/" + InstallModsPane.CellInstall));
        Assert.Equal(InstallPromptKind.Uninstall, pane.Prompt);
        Assert.Equal("whole", pane.PromptSideName);
        var (server, both) = pane.PromptServerFor(entry, InstallPromptKind.Uninstall);
        Assert.NotNull(server);
        Assert.True(both);
        pane.PromptNo();
    }

    private InstalledStateQuery BothInstalls(string key, string plugin, string version)
    {
        var files = new List<InstalledFile>();
        var ledger = new List<InstalledLedgerRow>();
        foreach (var (root, folder) in new[] { (InstalledState.ClientRoot, "client"), (InstalledState.ServerRoot, "server") })
        {
            string reload = Path.Combine(_root, folder, "BepInEx", "progenitor-reload", plugin + ".dll");
            files.Add(new InstalledFile(reload, InstalledState.ReloadKind, "sha-" + key, root, 10));
            ledger.Add(new InstalledLedgerRow(key, "sha-" + key, version, plugin + ".dll", source: "package:" + key + "@" + version,
                category: ServerInstallLedger.CategoryMod, root: root, filePath: reload));
        }
        return new InstalledStateQuery(files, new List<InstalledInventoryRow> { new(plugin, version, "Local") }, ledger);
    }

    [Fact]
    public void The_header_readback_names_the_simple_columns()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.Add(Listing("o-a"));
        var rig = new Rig(this, catalog, new Store(), NotConnected);
        rig.Pane.ColumnRowWidthProbe = () => InstallColumns.LiveRowWidth;
        Assert.True(rig.Pane.TryDescribeSidebarRow(rig.Pane.HeaderRow!, out var extra));
        var columns = (Dictionary<string, object?>)extra!["columns"]!;
        Assert.Equal("simple", columns["view"]);
        Assert.Equal(new[] { "mod", "released", "downloads", "version", "status" }, (string[])columns["set"]!);
        Assert.Empty((List<string>)columns["dropped"]!);
        var widths = (Dictionary<string, object?>)columns["widths"]!;
        // Nothing drops at the 1236 px live row and the Mod column keeps the rest.
        Assert.True((double)widths[InstallModsPane.CellName]! > 300d, widths[InstallModsPane.CellName]!.ToString());
        Assert.True((double)widths[InstallModsPane.CellReleased]! > 0d);
        Assert.True((double)widths[InstallModsPane.CellDownloads]! > 0d);
        // The toolbar with the switch fits the live row in both views (Advanced also carries the
        // server picker), with the search field at its full width.
        rig.Pane.ServerPickerProbe = () => Inputs(connected: true);
        Assert.True(InstallModsPane.NaturalWidth(rig.Pane.ToolbarCells()) <= InstallColumns.LiveRowWidth);
        rig.Pane.SetView(InstallView.Advanced);
        var advanced = rig.Pane.ToolbarCells();
        Assert.Contains(advanced, c => c.Key == InstallModsPane.CellServerPick);
        Assert.True(InstallModsPane.NaturalWidth(advanced) <= InstallColumns.LiveRowWidth, InstallModsPane.NaturalWidth(advanced).ToString());
        Assert.Equal(InstallModsPane.SearchCellWidth, advanced.Single(c => c.Key == InstallModsPane.CellSearch).Width.Value, 3);
    }

    [Fact]
    public void Players_joining_is_a_server_settings_row_not_an_install_toolbar_button()
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.Add(Listing("o-a"));
        var rig = new Rig(this, catalog, new Store(), p => AsAdmin(p));
        string[] Keys() => rig.Pane.ToolbarRow!.Cells.Select(c => c.Key).ToArray();
        Assert.DoesNotContain("joinAdmin", Keys());
        rig.Pane.SetView(InstallView.Advanced);
        Assert.DoesNotContain("joinAdmin", Keys());
        string pane = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        Assert.DoesNotContain("JoinAdminButtonProbe", pane, StringComparison.Ordinal);
        // The entry point: "Players joining this server" / Open on Progenitor's page, for an admin of a
        // joined server that has the panel (the rule the toolbar button used), opening the popup unchanged.
        Assert.Equal("Players joining this server", JoinServerSettings.PlayersJoiningLabel);
        Assert.Equal("Open", JoinServerSettings.PlayersJoiningButton);
        string page = SourcePin.MethodBody(SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "JoinServerSettings.cs"),
            "private static void BuildPage(");
        // Settings organization stage 5: under Server settings, Mods players need.
        Assert.Contains("PlayersJoiningRow = p => AddPlayersJoiningRow(p, OpenPlayersJoining),", page, StringComparison.Ordinal);
        string settings = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "JoinServerSettings.cs");
        // The row's rule is the toolbar's, over the client's own inputs.
        string shown = SourcePin.MethodBody(settings, "internal static bool PlayersJoiningShown(");
        Assert.Contains("bool shown = JoinAdminOffer.Offered(inputs.Supports, inputs.HelloAdmin, inputs.Status);", shown, StringComparison.Ordinal);
        Assert.Contains("PlayersJoiningInputsProbe = () => (JoinAdminClient.ServerSupports, JoinAdminClient.HelloAdmin, RemoteInstallClient.Shared.Status);",
            settings, StringComparison.Ordinal);
        // JoinAdminClient.IsAdmin is the toolbar button's former rule (c4bbd91c: JoinAdminButtonProbe = () =>
        // JoinAdminClient.IsAdmin), over the same inputs: the hello's support, the remote-install status's
        // authorization when the server sent one, else the hello's admin flag.
        string client = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "JoinAdminClient.cs");
        Assert.Contains("internal static bool IsAdmin => JoinAdminOffer.Offered(_supports, _helloAdmin, RemoteInstallClient.Shared.Status);",
            client, StringComparison.Ordinal);
        RemoteInstallStatus Status(bool authorized) =>
            new(0, true, authorized, true, RemoteReachability.Unknown, "", false, null);
        Assert.False(JoinAdminOffer.Offered(supports: false, helloAdmin: true, status: Status(true)));
        Assert.True(JoinAdminOffer.Offered(true, helloAdmin: true, status: null));
        Assert.False(JoinAdminOffer.Offered(true, helloAdmin: false, status: null));
        Assert.True(JoinAdminOffer.Offered(true, helloAdmin: false, status: Status(true)));
        Assert.False(JoinAdminOffer.Offered(true, helloAdmin: true, status: Status(false)));
        Assert.Contains("JoinAdminPanel.Open()", SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "JoinServerSettings.cs"), StringComparison.Ordinal);
    }

    private sealed class NeverClock : ICatalogClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
        public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
