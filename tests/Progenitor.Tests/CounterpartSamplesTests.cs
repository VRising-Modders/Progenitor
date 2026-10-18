using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.SampleClientLed;
using Progenitor.SampleServerLed;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// COUNTERPART-SAMPLES: the two counterpart samples (<c>samples/Progenitor.SampleServerLed</c>, a
/// mostly-server mod with an optional client part, and <c>samples/Progenitor.SampleClientLed</c>, a
/// mostly-client mod with an optional server part) and the one API they needed,
/// <c>ctx.Handshake.ServerPart</c> (<see cref="CounterpartPolicy.ServerPartFrom"/>).
///
/// <para>What is checked, game-free: each sample's shipped declaration turned into handshake rows and
/// run through the real join evaluation, pause planner and status-bar rules for every cell of its
/// join matrix (the README tables); the settings pages' scopes and grayed rows with and without the
/// server part; the server-led server honoring each player's preferences; and that no matrix cell
/// refuses or pauses anyone. The Plugin.cs halves are game-bound and are pinned by source.</para>
/// </summary>
[Collection("PluginLogger")]
public sealed class CounterpartSamplesTests : IDisposable
{
    private const string Version = "2026.9.70";
    private const string OtherMod = "test.counterpart.other";

    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly object _owner = new();
    private readonly List<IDisposable> _syncs = new();
    private JsonDocument? _rows;

    public CounterpartSamplesTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
        Handshake.ResetTables();
        ModMenuInventory.ResetForTests();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        SettingsCatalog.UseSyncedProbe(_ => false);
        ModSettingsPages.ResetForTests();
        ConfigSync.ResetForTests();
        ServerSettings.Uninstall();
        UiInfoHandle.Scheduler = null;
        UiSliderEntryHandle.SettleScheduler = null;
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ServerModPausePolicy.LoweredProbe = null;
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-counterparts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _rows?.Dispose();
        foreach (var sync in _syncs) { try { sync.Dispose(); } catch { } }
        ServerModPausePolicy.LoweredProbe = null;
        UiLayoutBatch.ResetForTests();
        ModsMenu.Close();
        ModSettingsPages.ResetForTests();
        UiInfoHandle.Scheduler = null;
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.ResetTabBindingsForTests();
        ModsWorkspaceFrame.ResetForTests();
        ModsMenu.UninstallLifecycleWatch();
        Handshake.UninstallClientServerContextWatch();
        ModMenuInventory.ResetForTests();
        Handshake.ResetTables();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        ServerSettings.Uninstall();
        ConfigSync.ResetForTests();
        Plugin.Logger = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---- A. the API: ctx.Handshake.ServerPart -------------------------------------------------------

    /// <summary>
    /// The client's view of its server part follows the rule the mod page uses: named with a version is
    /// Running; not named in a request of 1–15 rows is Absent; no request, an empty one or a full one
    /// proves nothing. What this would catch: a plugin told "absent" on a server whose 16-row request
    /// may have dropped it, or "running" for an absence sentinel with no version.
    /// </summary>
    [Fact]
    public void ServerPart_reads_the_joined_servers_request_like_the_mod_page()
    {
        const string guid = "test.cp.mine";
        var mine = Handshake.ModRecord.CreateDeclared(guid, "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor);
        var other = Handshake.ModRecord.CreateDeclared(OtherMod, "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor);

        Assert.Equal(CounterpartPresence.Unknown, CounterpartPolicy.ServerPartFrom(null, guid, truncated: false));
        Assert.Equal(CounterpartPresence.Unknown, CounterpartPolicy.ServerPartFrom(Array.Empty<Handshake.ModRecord>(), guid, truncated: false));
        Assert.Equal(CounterpartPresence.Running, CounterpartPolicy.ServerPartFrom(new[] { other, mine }, guid, truncated: false));
        Assert.Equal(CounterpartPresence.Running, CounterpartPolicy.ServerPartFrom(new[] { mine }, guid.ToUpperInvariant(), truncated: false));
        Assert.Equal(CounterpartPresence.Absent, CounterpartPolicy.ServerPartFrom(new[] { other }, guid, truncated: false));
        // A row with no version is an absence sentinel, not a server part.
        Assert.Equal(CounterpartPresence.Absent,
            CounterpartPolicy.ServerPartFrom(new[] { other, new Handshake.ModRecord(guid, "") }, guid, truncated: false));
        // A full request may have left the mod out: unknown.
        var full = Enumerable.Range(0, Handshake.MaxMods)
            .Select(i => Handshake.ModRecord.CreateDeclared("test.cp.fill" + i, "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None))
            .ToArray();
        Assert.Equal(CounterpartPresence.Unknown, CounterpartPolicy.ServerPartFrom(full, guid, truncated: false));
        Assert.Equal(CounterpartPresence.Unknown, CounterpartPolicy.ServerPartFrom(new[] { other }, "  ", truncated: false));

        // The facade reads Core's recorded request through this rule, and is Running on the server.
        string context = SourcePin.ReadFile("src", "Progenitor.Core", "Plugins", "PluginContext.cs");
        SourcePin.AssertUncommented(context, "public CounterpartPresence ServerPart");
        SourcePin.AssertUncommented(context, "if (_ctx.IsServer) return CounterpartPresence.Running;");
        SourcePin.AssertUncommented(context, "CounterpartPolicy.ServerPartFrom(Handshake.CopyServerAsked(), _ctx.Guid, Handshake.ServerRequestTruncated)");

        // Review: a request that filled all 16 wire slots keeps 15 rows once its metadata row is
        // stripped; it may have dropped the mod, so it proves nothing. The flag, not the count, says so.
        var fifteen = full.Take(Handshake.MaxMods - 1).ToArray();
        Assert.Equal(CounterpartPresence.Absent, CounterpartPolicy.ServerPartFrom(fifteen, guid, truncated: false));
        Assert.Equal(CounterpartPresence.Unknown, CounterpartPolicy.ServerPartFrom(fifteen, guid, truncated: true));
        Assert.False(CounterpartPolicy.RequestProvesAbsence(15, truncated: true));
        Assert.True(CounterpartPolicy.RequestProvesAbsence(15, truncated: false));
        Handshake.RecordServerRequest(fifteen, truncated: true);
        Assert.True(Handshake.ServerRequestTruncated);
        Handshake.ForgetServerRequest();
        Assert.False(Handshake.ServerRequestTruncated);
        string handshake = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "Handshake.cs");
        SourcePin.AssertUncommented(handshake, "RecordServerRequest(askedRows, truncated: count >= MaxMods);");
        string list = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.ModList.cs");
        SourcePin.AssertUncommented(list, "CounterpartPolicy.RequestProvesAbsence(asked.Length, Handshake.ServerRequestTruncated)");
    }

    // ---- B. declarations and the join matrices ------------------------------------------------------

    /// <summary>The shipped attributes use the declaration constants the matrices below are computed from.</summary>
    [Fact]
    public void Both_plugins_declare_exactly_the_tested_constants()
    {
        string server = SourcePin.ReadFile("samples", "Progenitor.SampleServerLed", "Plugin.cs");
        SourcePin.AssertUncommented(server, "[BepInPlugin(ServerLedDeclaration.Guid, ServerLedDeclaration.Name, ServerLedDeclaration.Version)]");
        SourcePin.AssertUncommented(server, "[NetworkCompatibility(ServerLedDeclaration.Level, ServerLedDeclaration.Rule, Peers = ServerLedDeclaration.Peers)]");
        SourcePin.AssertUncommented(server, "[Counterpart(ServerLedDeclaration.Need, ClientExtras = ServerLedDeclaration.ClientExtras,");
        SourcePin.AssertUncommented(server, "[ProgenitorSide(Side.Both)]");
        Assert.Equal(NetworkCompatibilityLevel.ServerMustHave, ServerLedDeclaration.Level);
        Assert.Equal(CounterpartNeed.Optional, ServerLedDeclaration.Need);
        Assert.False(string.IsNullOrWhiteSpace(ServerLedDeclaration.ClientExtras));

        string client = SourcePin.ReadFile("samples", "Progenitor.SampleClientLed", "Plugin.cs");
        SourcePin.AssertUncommented(client, "[BepInPlugin(ClientLedDeclaration.Guid, ClientLedDeclaration.Name, ClientLedDeclaration.Version)]");
        SourcePin.AssertUncommented(client, "[NetworkCompatibility(ClientLedDeclaration.Level, ClientLedDeclaration.Rule, Peers = ClientLedDeclaration.Peers)]");
        SourcePin.AssertUncommented(client, "[Counterpart(ClientLedDeclaration.Need,");
        SourcePin.AssertUncommented(client, "[ProgenitorSide(Side.Both)]");
        Assert.Equal(NetworkCompatibilityLevel.NoNeedForSync, ClientLedDeclaration.Level);
        Assert.Equal(CounterpartNeed.Limited, ClientLedDeclaration.Need);

        // Both lower (server-led) or keep (client-led) the row to NoNeedForSync on both sides.
        foreach (bool isServer in new[] { false, true })
        {
            Assert.Equal(NetworkCompatibilityLevel.NoNeedForSync, CounterpartPolicy.EffectiveLevel(
                ServerLedDeclaration.Level, ServerLedDeclaration.Need, isServer, ServerLedDeclaration.ClientExtras));
            Assert.Equal(NetworkCompatibilityLevel.NoNeedForSync, CounterpartPolicy.EffectiveLevel(
                ClientLedDeclaration.Level, ClientLedDeclaration.Need, isServer, null));
        }

        // Why the client-led sample is not ServerMustHave + Limited: the server would keep ServerMustHave.
        Assert.Equal(NetworkCompatibilityLevel.ServerMustHave, CounterpartPolicy.EffectiveLevel(
            NetworkCompatibilityLevel.ServerMustHave, CounterpartNeed.Limited, isServer: true, null));
    }

    /// <summary>
    /// Mod compatibility C5: both samples carry a literal semantic version and declare every 1.x as a
    /// peer, so a 1.1 that adds a field or a feature joins a 1.0, a 2.0 does not, and neither does a
    /// build from before the move to versioned messages (date versions such as 2026.10.5, whose raw
    /// packets the 1.x parts no longer read). What this would catch: a range that admits the old wire
    /// format, or a version the contract cannot speak about.
    /// </summary>
    [Fact]
    public void Both_samples_declare_a_semantic_version_and_every_1x_as_a_peer()
    {
        foreach (var (version, peers) in new[]
                 {
                     (ServerLedDeclaration.Version, ServerLedDeclaration.Peers),
                     (ClientLedDeclaration.Version, ClientLedDeclaration.Peers),
                 })
        {
            Assert.Equal("1.0.0", version);
            Assert.True(CompatRange.TryParse(peers, out var range, out string? error), error);
            Assert.True(range!.Includes(version));
            Assert.True(range.Includes("1.1.0"));
            Assert.True(range.Includes("1.9.3"));
            Assert.False(range.Includes("2.0.0"));
            Assert.False(range.Includes("0.9.0"));
            Assert.False(range.Includes("2026.10.5"));
        }
    }

    /// <summary>
    /// The server-led matrix, cell by cell: the server's join decision for a Progenitor client and a
    /// vanilla client, the client's pause decision, its view of the server part and its status-bar
    /// words. What this would catch: a declaration change that makes the server refuse clients without
    /// the client part, or pauses the client part on a server without the server part.
    /// </summary>
    [Fact]
    public void Server_led_matrix_allows_every_cell_and_pauses_nothing()
    {
        var d = ServerLed();

        var noneNone = Cell(d, clientHalf: false, serverHalf: false);
        Assert.True(noneNone.Allowed && noneNone.VanillaAllowed && !noneNone.Paused);

        var noneServer = Cell(d, clientHalf: false, serverHalf: true);
        Assert.True(noneServer.Allowed, string.Join("; ", noneServer.Problems));
        Assert.True(noneServer.VanillaAllowed);        // NoNeedForSync: a client that never answers is not kicked

        var clientNone = Cell(d, clientHalf: true, serverHalf: false);
        Assert.True(clientNone.Allowed && clientNone.VanillaAllowed);
        Assert.False(clientNone.Paused);
        Assert.Equal(CounterpartPresence.Absent, clientNone.Presence);
        Assert.Equal("Client and server", clientNone.SidesWord);
        Assert.Equal("Server part not on this server · limited", clientNone.ServerPartText);

        var clientServer = Cell(d, clientHalf: true, serverHalf: true);
        Assert.True(clientServer.Allowed && !clientServer.Paused);
        Assert.Equal(CounterpartPresence.Running, clientServer.Presence);
        Assert.Equal("Server part running", clientServer.ServerPartText);

        // On a server without Progenitor there is no request at all: nothing is known, nothing is said.
        var vanillaServer = Cell(d, clientHalf: true, serverHalf: false, progenitorServer: false);
        Assert.False(vanillaServer.Paused);
        Assert.Equal(CounterpartPresence.Unknown, vanillaServer.Presence);
        Assert.Equal("", vanillaServer.ServerPartText);
    }

    /// <summary>The client-led matrix, the same four cells (and a vanilla server).</summary>
    [Fact]
    public void Client_led_matrix_allows_every_cell_and_pauses_nothing()
    {
        var d = ClientLed();

        var noneServer = Cell(d, clientHalf: false, serverHalf: true);
        Assert.True(noneServer.Allowed && noneServer.VanillaAllowed, string.Join("; ", noneServer.Problems));

        var clientNone = Cell(d, clientHalf: true, serverHalf: false);
        Assert.True(clientNone.Allowed && !clientNone.Paused);
        Assert.Equal(CounterpartPresence.Absent, clientNone.Presence);
        Assert.Equal("Client and server", clientNone.SidesWord);
        Assert.Equal("Server part not on this server · limited", clientNone.ServerPartText);

        var clientServer = Cell(d, clientHalf: true, serverHalf: true);
        Assert.True(clientServer.Allowed && !clientServer.Paused);
        Assert.Equal(CounterpartPresence.Running, clientServer.Presence);
        Assert.Equal("Server part running", clientServer.ServerPartText);

        var noneNone = Cell(d, clientHalf: false, serverHalf: false);
        Assert.True(noneNone.Allowed && noneNone.VanillaAllowed);

        var vanillaServer = Cell(d, clientHalf: true, serverHalf: false, progenitorServer: false);
        Assert.False(vanillaServer.Paused);
        Assert.True(vanillaServer.ClientAllowed);
        Assert.Equal("", vanillaServer.ServerPartText);
    }

    /// <summary>
    /// No refusal path in either matrix at matching versions, and the one refusal the declarations keep
    /// on purpose: two sides that both run a sample at different major.minor versions. What this would
    /// catch: a row that kicks for a missing counterpart, or a version rule silently dropped.
    /// </summary>
    [Fact]
    public void No_cell_refuses_and_only_a_version_mismatch_between_two_halves_does()
    {
        foreach (var d in new[] { ServerLed(), ClientLed() })
        {
            foreach (bool client in new[] { false, true })
                foreach (bool server in new[] { false, true })
                {
                    var cell = Cell(d, client, server);
                    Assert.True(cell.Allowed, d.Guid + " client=" + client + " server=" + server + ": " + string.Join("; ", cell.Problems));
                    Assert.True(cell.ClientAllowed, d.Guid + " client=" + client + " server=" + server + ": the client refuses");
                    Assert.True(cell.VanillaAllowed);
                    Assert.False(cell.Paused);
                }

            var mismatch = Cell(d, clientHalf: true, serverHalf: true, clientVersion: "2026.10.1");
            Assert.False(mismatch.Allowed);
            Assert.False(mismatch.ClientAllowed);
            Assert.Contains(mismatch.Problems, p => p.Contains(d.Guid, StringComparison.OrdinalIgnoreCase));
            // Same major.minor, another patch: allowed.
            Assert.True(Cell(d, clientHalf: true, serverHalf: true, clientVersion: "2026.9.99").Allowed);
        }
    }

    /// <summary>
    /// The reverse check is live: the same client half declared <c>Required</c> (a ServerMustHave
    /// client row) is refused by the client's own check on a server without the server half, and is
    /// accepted with it. What this would catch: a Cell helper that only ever asks the server.
    /// </summary>
    [Fact]
    public void The_client_side_check_refuses_a_required_counterpart()
    {
        var required = ServerLed() with { Need = CounterpartNeed.Required, ClientExtras = null };
        var without = Cell(required, clientHalf: true, serverHalf: false);
        Assert.False(without.ClientAllowed);
        Assert.True(Cell(required, clientHalf: true, serverHalf: true).ClientAllowed);
    }

    // ---- C. settings: scopes and grayed rows -------------------------------------------------------

    /// <summary>
    /// The cfg page contains server options and local popup options, but no duplicate personal
    /// server controls. Those come from Core's manifest. Local server-dependent rows gray offline.
    /// </summary>
    [Fact]
    public void Server_led_cfg_page_has_no_duplicate_personal_server_rows()
    {
        ServerSettings.Install();
        var file = File("serverled");
        var entries = ServerLedEntries.Bind(new ServerLedBinder(file, ServerLedDeclaration.Guid, _syncs), isClient: true);
        var registration = ModSettingsPages.Register(_owner, ServerLedDeclaration.Guid,
            page => ServerLedSettings.Build(page, entries, () => ServerLedPolicy.PresenceText(CounterpartPresence.Unknown)));
        string id = ServerLedDeclaration.Guid;

        var sections = registration.Sections();
        Assert.Equal(new[] { ServerLedSettings.SectionServer, ServerLedSettings.SectionForClient },
            sections.Select(s => s.Title).ToArray());

        Assert.True(ServerSettings.IsEditable(id + "/Server/AnnounceMinutes"));
        Assert.True(ServerSettings.IsEditable(id + "/Server/Greeting"));
        Assert.False(ServerSettings.IsEditable(id + "/ForServer/ReceiveAnnouncements"));
        Assert.Equal(2, registration.ServerEditCount);

        Open(id);
        var rows = PageRows();
        void Expect(string key, string scope, bool needsServerPart, string fallback)
        {
            var row = SettingRow(rows, id + "/" + key);
            Assert.Equal(scope, row.GetProperty("scopeDeclared").GetString() ?? "");
            Assert.Equal(needsServerPart, row.GetProperty("needsServerPart").GetBoolean());
            Assert.Equal(needsServerPart, row.GetProperty("greyed").GetBoolean());
            if (needsServerPart)
            {
                Assert.Equal(SettingAvailability.NotConnectedReason, row.GetProperty("reason").GetString());
                Assert.Equal(fallback, row.GetProperty("fallback").GetString());
            }
        }

        Expect("Server/AnnounceMinutes", "server", true, ServerLedSettings.FallbackServer);
        Expect("Server/Greeting", "server", true, ServerLedSettings.FallbackServer);
        Assert.DoesNotContain("ForServer/ReceiveAnnouncements", rows.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("ForServer/Delivery", rows.GetRawText(), StringComparison.Ordinal);
        Expect("Client/PopupSeconds", "local", true, ServerLedSettings.FallbackPopup);
        Expect("Client/LogAnnouncements", "local", true, ServerLedSettings.FallbackPopup);
        Expect("Client/JoinCheck", "local", false, "");

        // The server builds only the server options: its page exists to opt them into admin edits.
        ModsMenu.Close();
        registration.Dispose();
        var serverFile = File("serverled-server");
        var serverEntries = ServerLedEntries.Bind(new ServerLedBinder(serverFile, "test.serverled.server", _syncs), isClient: false);
        Assert.Null(serverEntries.PopupSeconds);
        var serverPage = ModSettingsPages.Register(_owner, "test.serverled.server", page => ServerLedSettings.Build(page, serverEntries, null));
        Assert.Equal(new[] { ServerLedSettings.SectionServer }, serverPage.Sections().Select(s => s.Title).ToArray());
        Assert.Equal(2, serverPage.ServerEditCount);
    }

    /// <summary>
    /// Grayed with the server part and without it, through the view that draws the page: running
    /// enables every row; known absent grays the needs-server-part rows with "not on this server" and
    /// the fallback, and leaves the local-only row working.
    /// </summary>
    [Fact]
    public void Server_led_rows_follow_the_server_part()
    {
        var file = File("serverled-view");
        var entries = ServerLedEntries.Bind(new ServerLedBinder(file, ServerLedDeclaration.Guid, _syncs), isClient: true);
        var registration = ModSettingsPages.Register(_owner, ServerLedDeclaration.Guid, page => ServerLedSettings.Build(page, entries, null));

        var state = ServerPartState.NotOnServer;
        var view = new ModSettingsView(ServerLedDeclaration.Guid, ModSettingsLayout.Build(registration.Sections(), null, null), "", () => state);
        var panel = UiApi.Panel("ServerLed", _owner, "example.ui.test");
        view.Build(panel, (_, _, _) => { });

        Assert.Contains(ModSettingsLayout.ServerPartMissingReason, _runtime.HelpOf("toggle", "Log announcements"), StringComparison.Ordinal);
        Assert.Contains(ServerLedSettings.FallbackPopup, _runtime.HelpOf("toggle", "Log announcements"), StringComparison.Ordinal);
        Assert.False(_runtime.InteractableOf("toggle", "Log announcements"));
        Assert.True(_runtime.InteractableOf("toggle", "Join check"));

        state = ServerPartState.Running;
        view.Reevaluate();
        Assert.True(_runtime.InteractableOf("toggle", "Log announcements"));
        Assert.DoesNotContain(ServerLedSettings.FallbackPopup, _runtime.HelpOf("toggle", "Log announcements"), StringComparison.Ordinal);
        Assert.True(_runtime.InteractableOf("toggle", "Join check"));

        view.Release();
        panel.Dispose();
    }

    /// <summary>
    /// The client-led page: the clock section is local and never grayed; the server section's rows
    /// need the server part (the server default is server-scoped and admin-editable); the server builds
    /// only the server default.
    /// </summary>
    [Fact]
    public void Client_led_page_grays_only_the_server_section()
    {
        ServerSettings.Install();
        var file = File("clientled");
        string id = ClientLedDeclaration.Guid;
        var entries = ClientLedEntries.Bind(new ClientLedBinder(file, id, _syncs), isClient: true);
        var registration = ModSettingsPages.Register(_owner, id,
            page => ClientLedSettings.Build(page, entries, () => "12:00", () => ClientLedPolicy.LocalEstimateLine));

        Assert.Equal(new[] { ClientLedSettings.SectionClock, ClientLedSettings.SectionServer },
            registration.Sections().Select(s => s.Title).ToArray());
        Assert.True(ServerSettings.IsEditable(id + "/Server/DefaultFormat"));
        Assert.False(ServerSettings.IsEditable(id + "/Clock/Format"));

        Open(id);
        var rows = PageRows();
        var format = SettingRow(rows, id + "/Clock/Format");
        Assert.Equal("local", format.GetProperty("scopeDeclared").GetString());
        Assert.False(format.GetProperty("greyed").GetBoolean());
        Assert.False(SettingRow(rows, id + "/Clock/ShowSeconds").GetProperty("greyed").GetBoolean());
        var serverTime = SettingRow(rows, id + "/Clock/ShowServerTime");
        Assert.True(serverTime.GetProperty("greyed").GetBoolean());
        Assert.Equal(ClientLedSettings.FallbackServerTime, serverTime.GetProperty("fallback").GetString());
        var serverDefault = SettingRow(rows, id + "/Server/DefaultFormat");
        Assert.Equal("server", serverDefault.GetProperty("scopeDeclared").GetString());
        Assert.True(serverDefault.GetProperty("greyed").GetBoolean());
        Assert.Equal(ClientLedSettings.FallbackServerDefault, serverDefault.GetProperty("fallback").GetString());
        ModsMenu.Close();

        // Through the view: known absent grays the server toggle; running enables it; the clock never grays.
        var state = ServerPartState.NotOnServer;
        var view = new ModSettingsView(id, ModSettingsLayout.Build(registration.Sections(), null, null), "", () => state);
        var panel = UiApi.Panel("ClientLed", _owner, "example.ui.test");
        view.Build(panel, (_, _, _) => { });
        Assert.False(_runtime.InteractableOf("toggle", "Show server time"));
        Assert.Contains(ClientLedSettings.FallbackServerTime, _runtime.HelpOf("toggle", "Show server time"), StringComparison.Ordinal);
        Assert.True(_runtime.InteractableOf("toggle", "Show seconds"));
        state = ServerPartState.Running;
        view.Reevaluate();
        Assert.True(_runtime.InteractableOf("toggle", "Show server time"));
        view.Release();
        panel.Dispose();
        registration.Dispose();

        var serverEntries = ClientLedEntries.Bind(new ClientLedBinder(File("clientled-server"), "test.clientled.server", _syncs), isClient: false);
        Assert.Null(serverEntries.Format);
        var serverPage = ModSettingsPages.Register(_owner, "test.clientled.server", page => ClientLedSettings.Build(page, serverEntries, null, null));
        Assert.Single(serverPage.Sections());
        Assert.Equal(1, serverPage.ServerEditCount);
    }

    // ---- D. the server-led server honors each player's preferences ----------------------------------

    /// <summary>The sample reads real persisted values using full platform IDs. Reopening the
    /// store models a reload; removing the popup client changes routing, not saved preferences.</summary>
    [Fact]
    public void Server_led_server_applies_persisted_preferences_per_platform_account()
    {
        var store = new PlayerSettingsStore(_dir, ServerLedDeclaration.Guid);
        const ulong popup = ulong.MaxValue, off = ulong.MaxValue - 1, fresh = 7;
        store.Set(popup, "ForServer/Delivery", "Popup", "Chat");
        store.Set(off, "ForServer/ReceiveAnnouncements", "false", "true");
        ServerLedPolicy.PlayerPrefs Read(ulong id) => ServerLedPolicy.PreferencesFor(id,
            account => bool.Parse(store.Get(account, "ForServer/ReceiveAnnouncements", "true")),
            account => store.Get(account, "ForServer/Delivery", "Chat"));

        Assert.Equal(ServerLedPolicy.Route.Chat, ServerLedPolicy.RouteFor(Read(fresh), false));
        Assert.Equal(ServerLedPolicy.Route.Popup, ServerLedPolicy.RouteFor(Read(popup), true));
        Assert.Equal(ServerLedPolicy.Route.Skip, ServerLedPolicy.RouteFor(Read(off), true));
        Assert.Equal(ServerLedPolicy.Route.Skip, ServerLedPolicy.RouteFor(Read(off), false));
        Assert.Equal(ServerLedPolicy.Route.Chat, ServerLedPolicy.RouteFor(Read(popup), false));

        store = new PlayerSettingsStore(_dir, ServerLedDeclaration.Guid);
        Assert.Equal(ServerLedPolicy.Route.Popup, ServerLedPolicy.RouteFor(Read(popup), true));
        Assert.Equal(ServerLedPolicy.Route.Skip, ServerLedPolicy.RouteFor(Read(off), false));
        Assert.Equal(ServerLedPolicy.Route.Chat, ServerLedPolicy.RouteFor(Read(fresh), true));
        store.Set(off, "ForServer/ReceiveAnnouncements", "true", "true");
        Assert.Equal(ServerLedPolicy.Route.Chat, ServerLedPolicy.RouteFor(Read(off), false));
        Assert.Contains("server-owned per-account", ServerLedPolicy.StatusLines(10, "Hello", Read(popup))[1], StringComparison.Ordinal);

        string plugin = SourcePin.ReadFile("samples", "Progenitor.SampleServerLed", "Plugin.cs");
        SourcePin.AssertUncommented(plugin, "RouteFor(Preferences(user.PlatformId), showsPopups)");
        SourcePin.AssertUncommented(plugin, "id => _receive.For(new PlayerAccountId(id))");
        SourcePin.AssertUncommented(plugin, "id => _delivery.For(new PlayerAccountId(id))");
        SourcePin.AssertUncommented(plugin, "half.Status(ctx.User.PlatformId)");
        SourcePin.AssertUncommented(plugin, "bool showsPopups = _ctx.Peers.Of(entity).Has(ServerLedMessages.FeaturePopups);");
        SourcePin.AssertUncommented(plugin, "if (_ctx.Messages.SendTo(entity, message) == SendResult.Sent) popup++;");
        SourcePin.AssertUncommented(plugin, "else if (_ctx.Chat.SendTo(entity, text)) chat++;");
    }

    [Fact]
    public void Server_led_uses_Core_personal_settings_without_cfg_or_custom_push()
    {
        string plugin = SourcePin.ReadFile("samples", "Progenitor.SampleServerLed", "Plugin.cs");
        SourcePin.AssertUncommented(plugin, "PlayerSetting<bool> receive = ctx.Settings.PerPlayer(\"ForServer\", \"ReceiveAnnouncements\", true,");
        SourcePin.AssertUncommented(plugin, "PlayerSetting<string> delivery = ctx.Settings.PerPlayer(\"ForServer\", \"Delivery\", ServerLedPolicy.DeliveryChat,");
        SourcePin.AssertUncommented(plugin, "acceptableValues: new AcceptableValueList<string>(ServerLedPolicy.DeliveryChoices)");
        SourcePin.AssertUncommented(plugin, "scope: SettingScope.ServerWide, description: description");
        SourcePin.AssertUncommented(plugin, "_ctx.Features.Offer(ServerLedMessages.FeaturePopups);");
        Assert.DoesNotContain("SendToServer", plugin, StringComparison.Ordinal);
        Assert.DoesNotContain("PrefsMessage", plugin, StringComparison.Ordinal);
        Assert.DoesNotContain("FeaturePrefs", plugin, StringComparison.Ordinal);
        Assert.DoesNotContain("SayGoodbye", plugin, StringComparison.Ordinal);
        string settings = SourcePin.ReadFile("samples", "Progenitor.SampleServerLed", "ServerLedSettings.cs");
        Assert.DoesNotContain("ReceiveAnnouncements", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("entries.Delivery", settings, StringComparison.Ordinal);
    }

    /// <summary>The announcement schedule, its text, and the popup showing exactly that text.</summary>
    [Fact]
    public void Server_led_announcements_are_scheduled_and_worded()
    {
        Assert.False(ServerLedPolicy.IsDue(599, 0, 10));
        Assert.True(ServerLedPolicy.IsDue(600, 0, 10));
        Assert.Equal(60, ServerLedPolicy.IntervalSeconds(0));          // clamped to one minute
        Assert.Equal(7200, ServerLedPolicy.IntervalSeconds(100000));   // and to two hours
        Assert.Equal("Hi · 1 player online · server up 5 min.", ServerLedPolicy.AnnouncementText(" Hi ", 1, 5));
        Assert.Equal("Server announcement. · 3 players online · server up 2 h 5 min.", ServerLedPolicy.AnnouncementText("", 3, 125));
        Assert.Equal(ServerLedPolicy.MaxGreetingChars, ServerLedPolicy.CleanGreeting(new string('x', 500)).Length);
        Assert.DoesNotContain("\n", ServerLedPolicy.CleanGreeting("a\nb"), StringComparison.Ordinal);
        // Review: the popup shows exactly the chat line's text, carried in the message.
        string text = ServerLedPolicy.AnnouncementText("Grüße", 2, 61);
        Assert.Equal(text, ServerLedPolicy.PopupText(new AnnounceMessage { Sequence = 1, Text = text }));
        string cut = ServerLedPolicy.PopupText(new AnnounceMessage { Text = new string('·', 600) })!;
        Assert.Equal(ServerLedPolicy.MaxPopupChars, cut.Length);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
        Assert.Null(ServerLedPolicy.PopupText(new AnnounceMessage { Text = "  " }));
        Assert.Null(ServerLedPolicy.PopupText(null));
        string plugin = SourcePin.ReadFile("samples", "Progenitor.SampleServerLed", "Plugin.cs");
        SourcePin.AssertUncommented(plugin, "var message = new AnnounceMessage { Sequence = sequence, Text = text };");
        SourcePin.AssertUncommented(plugin, "ServerLedPolicy.PopupText(message) is not { } text");
    }

    // ---- E. the client-led clock --------------------------------------------------------------------

    /// <summary>
    /// The client's own format wins; Server default follows the server part while it runs and is
    /// 24-hour otherwise (the fallback the page states).
    /// </summary>
    [Fact]
    public void Client_led_format_takes_the_server_default_only_from_a_running_server_part()
    {
        Assert.Equal("12-hour", ClientLedPolicy.EffectiveFormat("12-hour", CounterpartPresence.Running, "24-hour"));
        Assert.Equal("24-hour", ClientLedPolicy.EffectiveFormat("24-hour", CounterpartPresence.Absent, "12-hour"));
        Assert.Equal("12-hour", ClientLedPolicy.EffectiveFormat("Server default", CounterpartPresence.Running, "12-hour"));
        Assert.Equal("24-hour", ClientLedPolicy.EffectiveFormat("Server default", CounterpartPresence.Absent, "12-hour"));
        Assert.Equal("24-hour", ClientLedPolicy.EffectiveFormat("Server default", CounterpartPresence.Unknown, "12-hour"));
        Assert.Equal("24-hour", ClientLedPolicy.EffectiveFormat("Server default", CounterpartPresence.Running, "Server default"));
        Assert.Equal("24-hour", ClientLedPolicy.EffectiveFormat(null, CounterpartPresence.Running, null));

        Assert.Equal("21:04:05", ClientLedPolicy.FormatLocal(new DateTime(2026, 9, 29, 21, 4, 5), "24-hour", true));
        Assert.Equal("9:04 PM", ClientLedPolicy.FormatLocal(new DateTime(2026, 9, 29, 21, 4, 5), "12-hour", false));
        Assert.Equal("12:00 AM", ClientLedPolicy.FormatGameHour(0, "12-hour"));
        Assert.Equal("23:30", ClientLedPolicy.FormatGameHour(23.5, "24-hour"));
    }

    /// <summary>The estimate between answers and the next sunrise or sunset.</summary>
    [Fact]
    public void Client_led_advances_the_server_clock_and_finds_the_next_event()
    {
        var answer = new TimeAnswer
        {
            Sequence = 1, Day = 4, Hour = 23, DayLengthSeconds = 1440, SunriseHour = 8, SunsetHour = 20, UptimeMinutes = 3,
        };
        // 1440 real seconds per game day: one real minute is one game hour.
        var (day, hour) = ClientLedPolicy.Advance(answer, 120);
        Assert.Equal(5, day);
        Assert.Equal(1, hour, 6);
        var (evt, minutes) = ClientLedPolicy.NextEvent(hour, 8, 20, 1440);
        Assert.Equal("Sunrise", evt);
        Assert.Equal(7, minutes, 6);
        Assert.Equal("Sunset", ClientLedPolicy.NextEvent(12, 8, 20, 1440).Event);
        Assert.Equal("Day 5, 01:00 · sunrise in 7 min", ClientLedPolicy.ServerTimeLine(answer, 120, "24-hour"));

        Assert.Equal("Local time 10:00.", ClientLedPolicy.PopupBody("10:00", null, showServerTime: false));
        Assert.Contains(ClientLedPolicy.LocalEstimateLine, ClientLedPolicy.PopupBody("10:00", null, showServerTime: true), StringComparison.Ordinal);
        Assert.Contains("In-game: Day 5", ClientLedPolicy.PopupBody("10:00", "Day 5, 01:00", showServerTime: true), StringComparison.Ordinal);
    }

    /// <summary>
    /// The client asks only a server part that offers <c>time</c> on an accepted join; the server part
    /// answers only a handshaken player, at most once a second, and forgets a slot on leave.
    /// </summary>
    [Fact]
    public void Client_led_requests_go_only_to_a_server_part_that_answers_and_are_validated()
    {
        Assert.False(ClientLedPolicy.ShouldRequest(serverAnswers: false, true, 999, null, pressWaiting: true));
        Assert.False(ClientLedPolicy.ShouldRequest(serverAnswers: true, false, 999, null, pressWaiting: true));
        Assert.True(ClientLedPolicy.ShouldRequest(serverAnswers: true, true, 999, null, pressWaiting: false));
        Assert.False(ClientLedPolicy.ShouldRequest(serverAnswers: true, true, 1, null, pressWaiting: true));
        Assert.False(ClientLedPolicy.ShouldRequest(serverAnswers: true, true, 5, 10, pressWaiting: false));
        Assert.True(ClientLedPolicy.ShouldRequest(serverAnswers: true, true, 5, 10, pressWaiting: true));
        Assert.True(ClientLedPolicy.ShouldRequest(serverAnswers: true, true, 31, 30, pressWaiting: false));

        Assert.Equal(ClientLedPolicy.Verdict.Ok, ClientLedPolicy.Admit("ok", null));
        Assert.Equal(ClientLedPolicy.Verdict.NotHandshaken, ClientLedPolicy.Admit(null, null));
        Assert.Equal(ClientLedPolicy.Verdict.TooSoon, ClientLedPolicy.Admit("ok", 0.5));
        Assert.Equal(ClientLedPolicy.Verdict.Ok, ClientLedPolicy.Admit("ok", 1.0));

        string plugin = SourcePin.ReadFile("samples", "Progenitor.SampleClientLed", "Plugin.cs");
        SourcePin.AssertUncommented(plugin, "_ctx.Features.Offer(ClientLedMessages.FeatureTime);");
        SourcePin.AssertUncommented(plugin, "private bool ServerAnswers => _ctx.Peer.Has(ClientLedMessages.FeatureTime);");
        SourcePin.AssertUncommented(plugin, "_ctx.Messages.SendToServer(new TimeRequest { Sequence = ++_sequence }) == SendResult.Sent");

        var table = new ClientLedPolicy.RequestTable();
        table.Note(4, 10);
        Assert.Equal(2, table.SecondsSince(4, 12));
        Assert.True(table.Forget(4));
        Assert.Null(table.SecondsSince(4, 12));
    }

    [Fact]
    public void Server_led_unidentified_players_do_not_read_a_persisted_account()
    {
        var prefs = ServerLedPolicy.PreferencesFor(0,
            _ => throw new InvalidOperationException("Must not read account zero"),
            _ => throw new InvalidOperationException("Must not read account zero"));
        Assert.Equal(ServerLedPolicy.Route.Chat, ServerLedPolicy.RouteFor(prefs, true));
        Assert.Equal(ServerLedPolicy.Route.Chat,
            ServerLedPolicy.RouteFor(new ServerLedPolicy.PlayerPrefs(true, "unknown-choice"), true));
    }

    /// <summary>
    /// Review: the join check is written once per join. A Core swap or plugin reload blips the state to
    /// Unknown and back within the same join (same client world); the key of the join already logged is
    /// kept process-wide, so it is not written again. A new join (another world) writes it again.
    /// </summary>
    [Fact]
    public void Server_led_join_check_is_logged_once_per_join()
    {
        string? logged = null;
        int lines = 0;
        void Observe(ulong world, CounterpartPresence presence)
        {
            if (!ServerLedPolicy.ShouldLogJoinCheck(world, presence, logged, out string key)) return;
            logged = key;
            lines++;
        }

        Observe(0, CounterpartPresence.Unknown);          // at the menu
        Observe(41, CounterpartPresence.Unknown);         // joined, request not in yet
        Observe(41, CounterpartPresence.Running);         // logged
        Observe(41, CounterpartPresence.Unknown);         // Core swap blip
        Observe(41, CounterpartPresence.Running);         // same join: not again
        Assert.Equal(1, lines);
        Observe(0, CounterpartPresence.Unknown);          // left
        Observe(57, CounterpartPresence.Absent);          // another join
        Assert.Equal(2, lines);

        string plugin = SourcePin.ReadFile("samples", "Progenitor.SampleServerLed", "Plugin.cs");
        SourcePin.AssertUncommented(plugin, "ServerLedPolicy.ShouldLogJoinCheck(ClientWorldEpoch.ObservedSequence, presence, last, out string key)");
        SourcePin.AssertUncommented(plugin, "AppDomain.CurrentDomain.SetData(JoinCheckSlot, key);");
    }

    /// <summary>
    /// Live 2026-09-29: a mod page open while the join happened kept "not confirmed running" and no
    /// server-part text until the mod was selected again. The handshake evidence (the request, the
    /// accepted result, the end of the join) now rewrites the open page's status bar and re-grays its
    /// rows in place: the same page and row objects, new text, new grayed state.
    /// </summary>
    [Fact]
    public void An_open_mod_page_follows_the_server_part_in_place()
    {
        const string id = "test.counterpart.live";
        ModRegistry.ResetForTests();
        ModRegistry.NoteLoading(id, "Live", "1.0.0", null, new CounterpartDeclaration(CounterpartNeed.Limited));
        ModRegistry.NoteLoaded(id);
        Handshake.Provide(id, "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor);
        var file = File("live");
        var extra = file.Bind("S", "Extra", false, "");
        ModSettingsPages.Register(_owner, id, page => page.Toggle(extra, needsServerPart: true, fallback: "Works alone."));
        try
        {
            Open(id);
            var page = ModsMenu.ModPage!;
            var frameworkRow = page.FrameworkRow;
            Assert.Equal("", Framework(PageRows()).GetProperty("serverPartText").GetString());
            Assert.Equal(SettingAvailability.NotConnectedReason, SettingRow(PageRows(), id + "/S/Extra").GetProperty("reason").GetString());

            // Joined a server that runs another mod but not this one; the join is accepted.
            var other = Handshake.ModRecord.CreateDeclared(OtherMod, "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor);
            Handshake.RecordServerRequest(new[] { other });
            ModMenuInventory.RestoreServerContext("test-handshake-ok");
            Handshake.NotifyServerPartEvidence("handshake-ok");
            Assert.Same(page, ModsMenu.ModPage);
            Assert.Same(frameworkRow, page.FrameworkRow);
            Assert.Equal("Server part not on this server · limited", Framework(PageRows()).GetProperty("serverPartText").GetString());
            var row = SettingRow(PageRows(), id + "/S/Extra");
            Assert.True(row.GetProperty("greyed").GetBoolean());
            Assert.Equal(ModSettingsLayout.ServerPartMissingReason, row.GetProperty("reason").GetString());

            // A server that runs it: the request itself refreshes the page.
            Handshake.RecordServerRequest(new[] { other, Handshake.ModRecord.CreateDeclared(id, "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor) });
            ModMenuInventory.RestoreServerContext("test-handshake-ok");
            Handshake.NotifyServerPartEvidence("handshake-ok");
            Assert.Same(page, ModsMenu.ModPage);
            Assert.Equal("Server part running", Framework(PageRows()).GetProperty("serverPartText").GetString());
            Assert.False(SettingRow(PageRows(), id + "/S/Extra").GetProperty("greyed").GetBoolean());

            // The join ends: back to unknown, in place.
            ModMenuInventory.ClearServerContext("test-disconnect");
            Handshake.ForgetServerRequest();
            Assert.Same(page, ModsMenu.ModPage);
            Assert.Equal("", Framework(PageRows()).GetProperty("serverPartText").GetString());
            Assert.Equal(SettingAvailability.NotConnectedReason, SettingRow(PageRows(), id + "/S/Extra").GetProperty("reason").GetString());
        }
        finally
        {
            Handshake.Unprovide(id);
            ModRegistry.ResetForTests();
        }
    }

    /// <summary>
    /// Live: the readback listed button and info rows as page-setting rows with key "undefined". They
    /// now carry <c>kind</c> (setting / button / info) and a key (the id's tail).
    /// </summary>
    [Fact]
    public void Button_and_info_rows_read_back_with_a_kind_and_a_key()
    {
        const string id = "test.counterpart.readback";
        var file = File("readback");
        var flag = file.Bind("S", "Flag", true, "");
        ModSettingsPages.Register(_owner, id, page => page
            .Section("One").Toggle(flag).Info("Local time", () => "12:00").Button("Run", () => { })
            .Section("Two").Button("Run", () => { }));
        Open(id);
        var rows = PageRows();
        var seen = new List<(string Kind, string Key)>();
        foreach (var candidate in rows.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object || !candidate.TryGetProperty(ModSettingsView.RowSetting, out var setting)) continue;
            Assert.True(setting.TryGetProperty("key", out var key), "a page-setting row without a key");
            Assert.True(setting.TryGetProperty("kind", out var kind), "a page-setting row without a kind");
            seen.Add((kind.GetString()!, key.GetString()!));
        }

        Assert.Contains(("setting", "Flag"), seen);
        Assert.Contains(("info", "info/Local time"), seen);
        Assert.Contains(("button", "button/Run"), seen);
        Assert.Contains(("button", "button/Run#2"), seen);
    }

    private static JsonElement Framework(JsonElement rows)
    {
        foreach (var row in rows.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(ModPagePane.RowFramework, out var framework))
                return framework;
        throw new Xunit.Sdk.XunitException("no page-framework row");
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private sealed record Declaration(string Guid, NetworkCompatibilityLevel Level, NetworkVersionRule Rule, CounterpartNeed Need, string? ClientExtras);

    private static Declaration ServerLed() => new(ServerLedDeclaration.Guid, ServerLedDeclaration.Level, ServerLedDeclaration.Rule,
        ServerLedDeclaration.Need, ServerLedDeclaration.ClientExtras);

    private static Declaration ClientLed() => new(ClientLedDeclaration.Guid, ClientLedDeclaration.Level, ClientLedDeclaration.Rule,
        ClientLedDeclaration.Need, null);

    private sealed record Outcome(
        bool Allowed, bool ClientAllowed, bool VanillaAllowed, bool Paused, IReadOnlyList<string> Problems,
        CounterpartPresence Presence, string SidesWord, string ServerPartText);

    /// <summary>
    /// One matrix cell. The server always runs one other Progenitor mod (so its request is not empty,
    /// and a missing half is known absent) unless <paramref name="progenitorServer"/> is false, which is
    /// a server without Progenitor: no request, nothing to refuse with.
    /// </summary>
    private static Outcome Cell(Declaration d, bool clientHalf, bool serverHalf, bool progenitorServer = true,
        string clientVersion = Version, string serverVersion = Version)
    {
        var serverRows = new List<Handshake.ModRecord>();
        if (progenitorServer)
        {
            serverRows.Add(Handshake.ModRecord.CreateDeclared(OtherMod, "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor));
            if (serverHalf)
                serverRows.Add(Handshake.ModRecord.CreateDeclared(d.Guid, serverVersion,
                    CounterpartPolicy.EffectiveLevel(d.Level, d.Need, isServer: true, d.ClientExtras), d.Rule));
        }

        var provided = new List<Handshake.ModRecord>();
        if (clientHalf)
            provided.Add(Handshake.ModRecord.CreateDeclared(d.Guid, clientVersion,
                CounterpartPolicy.EffectiveLevel(d.Level, d.Need, isServer: false, d.ClientExtras), d.Rule));

        IReadOnlyList<Handshake.ModRecord> asked = progenitorServer
            ? Handshake.Compare.PackRequest(serverRows).Rows
            : Array.Empty<Handshake.ModRecord>();
        var reply = Handshake.Compare.PackReply(asked, provided).Rows;
        var problems = progenitorServer ? Handshake.Compare.Evaluate(serverRows, reply) : new List<string>();
        bool vanilla = !Handshake.Compare.ServerRequiresAnswer(serverRows);

        // Review: the client's own side. It predicts the server's verdict from the request
        // (PredictAccepted, what per-server pause runs), and its declared rows are checked the other
        // way round: a client row that demanded the server part would refuse here.
        bool clientAllowed = Handshake.Compare.Evaluate(provided, asked).Count == 0
            && (!progenitorServer || ServerModPausePolicy.PredictAccepted(asked, provided, out _));

        ServerModPausePolicy.LoweredProbe = id => string.Equals(id, d.Guid, StringComparison.OrdinalIgnoreCase)
            && CounterpartPolicy.RunsWithout(d.Need);
        bool paused;
        try
        {
            (IReadOnlyList<string>, string) Closure(IReadOnlyList<string> g) => (g, "");
            var plan = progenitorServer
                ? ServerModPausePolicy.PlanPause(asked, false, provided, null, new[] { d.Guid }, Closure, autoPause: true)
                : ServerModPausePlan.Nothing;
            paused = plan.Pause.Count > 0;
        }
        finally { ServerModPausePolicy.LoweredProbe = null; }

        var presence = CounterpartPolicy.ServerPartFrom(asked, d.Guid, truncated: false);
        string sidesWord = "", partText = "";
        if (clientHalf)
        {
            // The status bar's rules (ModsMenu.FrameworkFactsFor): a declared counterpart is two-sided;
            // "limited" when it runs without the server part and the part is known absent.
            var sides = ModFrameworkLine.DecideSides(PackageSide.Unknown, false, provided[0].Level, hasCounterpart: true);
            var part = ModFrameworkLine.DecideServerPart(sides, paused: false,
                serverAskedAboutIt: presence == CounterpartPresence.Running, knownAbsent: presence == CounterpartPresence.Absent);
            var line = ModFrameworkLine.Compose(new ModFrameworkFacts
            {
                Sides = sides, ServerPart = part,
                Limited = CounterpartPolicy.RunsWithout(d.Need) && part == ServerPartState.NotOnServer,
            });
            sidesWord = ModFrameworkLine.SidesWordOf(sides);
            partText = line.ServerPartText;
        }

        return new Outcome(problems.Count == 0, clientAllowed, vanilla, paused, problems, presence, sidesWord, partText);
    }

    private ConfigFile File(string name) => new(Path.Combine(_dir, name + ".cfg"), saveOnInit: true);

    private static void Open(string id)
    {
        if (!ModsMenu.IsOpen) ModsMenu.Open();
        ModsMenu.OpenMod(id);
    }

    private JsonElement PageRows()
    {
        _rows?.Dispose();
        _rows = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
        return _rows.RootElement;
    }

    private static JsonElement SettingRow(JsonElement rows, string id)
    {
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty(ModSettingsView.RowSetting, out var setting)) continue;
            if (string.Equals(setting.GetProperty("id").GetString(), id, StringComparison.Ordinal)) return setting;
        }

        throw new Xunit.Sdk.XunitException("no setting row " + id);
    }

    /// <summary>The config-file shim behind the server-led binder; synced entries are registered with ConfigSync.</summary>
    private sealed class ServerLedBinder : Progenitor.SampleServerLed.IEntryBinder
    {
        private readonly ConfigFile _file;
        private readonly string _guid;
        private readonly List<IDisposable> _syncs;

        internal ServerLedBinder(ConfigFile file, string guid, List<IDisposable> syncs) { _file = file; _guid = guid; _syncs = syncs; }

        public ConfigEntry<int> Int(string section, string key, int value, string description, bool synced, int min, int max) =>
            Sync(_file.Bind(section, key, value, description), synced);

        public ConfigEntry<string> Text(string section, string key, string value, string description, bool synced, string[]? choices = null) =>
            Sync(_file.Bind(section, key, value, description), synced);

        public ConfigEntry<bool> Flag(string section, string key, bool value, string description) =>
            _file.Bind(section, key, value, description);

        private ConfigEntry<T> Sync<T>(ConfigEntry<T> entry, bool synced)
        {
            if (synced) _syncs.Add(ConfigSync.Register(_guid, entry));
            return entry;
        }
    }

    /// <summary>The config-file shim behind the client-led binder.</summary>
    private sealed class ClientLedBinder : Progenitor.SampleClientLed.IEntryBinder
    {
        private readonly ConfigFile _file;
        private readonly string _guid;
        private readonly List<IDisposable> _syncs;

        internal ClientLedBinder(ConfigFile file, string guid, List<IDisposable> syncs) { _file = file; _guid = guid; _syncs = syncs; }

        public ConfigEntry<string> Text(string section, string key, string value, string description, bool synced, string[]? choices = null)
        {
            var entry = _file.Bind(section, key, value, description);
            if (synced) _syncs.Add(ConfigSync.Register(_guid, entry));
            return entry;
        }

        public ConfigEntry<bool> Flag(string section, string key, bool value, string description) =>
            _file.Bind(section, key, value, description);
    }
}
