using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Settings scopes, step 4 (docs/settings-scopes.md, decisions 2026-10-03 and the accepted mockup):
/// the mod page's Settings as one list with <b>Your settings</b> and <b>Server settings</b>
/// (<c>Applies to everyone on …</c>), <c>Admin only · value</c> for players who are not admins, rows
/// grayed with the framework's reason, hide-when-alone, empty sections omitted and Reset all covering
/// Your settings only. Each page state is driven through <see cref="ModsMenu.ScopeContextForTests"/>.
/// Game-free: the fake UI runtime and the ConfigFile shim; nothing here is live.
/// </summary>
[Collection("PluginLogger")]
public sealed class ModSettingsScopesPageTests : IDisposable
{
    private const string Id = "example.scopes.page";
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly object _owner = new();
    private JsonDocument? _rows;
    private SettingsScopeContext _context = new();
    private readonly List<IDisposable> _handles = new();

    public ModSettingsScopesPageTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
        Handshake.ResetTables();
        ModMenuInventory.ResetForTests();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        SettingsCatalog.UseSyncedProbe(e => ConfigSync.IsRegistered(Id, e));
        ModSettingsPages.ResetForTests();
        ConfigSync.ResetForTests();
        ServerSettings.Uninstall();
        ServerSettings.Install();
        UiInfoHandle.Scheduler = null;
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.ScopeContextForTests = _ => _context;
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-scopes-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _rows?.Dispose();
        foreach (var h in _handles) h.Dispose();
        ModsMenu.ScopeContextForTests = null;
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

    // ---- fixture: the Sample's shape — personal rows, an opted-in server-wide limit, a button -----

    private ConfigEntry<bool> _hints = null!;
    private ConfigEntry<int> _limit = null!;

    private void Register(bool hideLimitWhenAlone = false, bool withServer = true, bool withPersonal = true)
    {
        var file = new ConfigFile(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".cfg"), saveOnInit: true);
        _hints = file.Bind("Menu", "ShowHints", true, "Show the hints.");
        _limit = file.Bind("Menu", "ExampleLimit", 10, "Demonstration limit.");
        if (withServer) _handles.Add(ConfigSync.Register(Id, _limit));
        _handles.Add(ModSettingsPages.Register(_owner, Id, page =>
        {
            if (withPersonal)
                page.Section("Personal").Toggle(_hints)
                    .Section("Demo").Button("Log a line", () => { });
            if (withServer)
            {
                page.Section("Server").Slider(_limit, 1, 20, scope: SettingScope.Server);
                if (hideLimitWhenAlone) page.HideWhenAlone();
            }
        }));
    }

    /// <summary>The server sent its value: ConfigSync locked the limit (a joined, compatible Progenitor server).</summary>
    private static void ServerSent(int value) =>
        ConfigSync.ApplyRecords(new[] { new ConfigSyncRecord(Id, "Menu", "ExampleLimit", value.ToString()) }, snapshot: true);

    private static SettingsScopeContext Joined(bool admin, ServerPartState part = ServerPartState.Running) => new()
    {
        Connection = SettingConnection.Progenitor,
        ServerPart = part,
        CanEditServerWide = admin,
        ServerName = "Progenitor Forms Test",
        ServerAddress = "127.0.0.1:9976",
    };

    private void Open()
    {
        if (!ModsMenu.IsOpen) ModsMenu.Open();
        ModsMenu.OpenMod(Id);
    }

    private JsonElement PageRows()
    {
        _rows?.Dispose();
        _rows = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
        return _rows.RootElement;
    }

    private static List<JsonElement> All(JsonElement rows, string key)
    {
        var list = new List<JsonElement>();
        foreach (var row in rows.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value)) list.Add(value);
        return list;
    }

    private static JsonElement Setting(JsonElement rows, string id) =>
        All(rows, ModSettingsView.RowSetting).Single(s => s.GetProperty("id").GetString() == id);

    private static bool HasSetting(JsonElement rows, string id) =>
        All(rows, ModSettingsView.RowSetting).Any(s => s.GetProperty("id").GetString() == id);

    private static string[] ScopeHeadings(JsonElement rows) =>
        All(rows, ModSettingsScopes.RowScopeSection).Select(s => s.GetProperty("heading").GetString() ?? "").ToArray();

    private static JsonElement Scope(JsonElement rows, string heading) =>
        All(rows, ModSettingsScopes.RowScopeSection).Single(s => s.GetProperty("heading").GetString() == heading);

    private const string LimitId = Id + "/Menu/ExampleLimit";
    private const string HintsId = Id + "/Menu/ShowHints";

    // ---- grouping -------------------------------------------------------------------------------

    [Fact]
    public void Your_settings_then_server_settings_with_author_subsections()
    {
        Register();
        _context = Joined(admin: true);
        ServerSent(12);
        Open();
        var rows = PageRows();

        Assert.Equal(new[] { "Your preferences", "Server settings" }, ScopeHeadings(rows));
        // "Personal" and "Server" restate the scope headings and draw no subheading; "Demo" does.
        var subheadings = All(rows, ModSettingsView.RowSection).Select(s => s.GetProperty("title").GetString()).ToArray();
        Assert.Equal(new[] { "Demo" }, subheadings);
        Assert.Equal("personal", Setting(rows, HintsId).GetProperty("scopeSection").GetString());
        Assert.Equal("client", Setting(rows, HintsId).GetProperty("owner").GetString());
        Assert.Equal("Personal", Setting(rows, HintsId).GetProperty("section").GetString());
        Assert.Equal("server-wide", Setting(rows, LimitId).GetProperty("scopeSection").GetString());
        Assert.Equal("server", Setting(rows, LimitId).GetProperty("owner").GetString());
        Assert.Equal(SettingsScopeContext.DevicePersonalLine, Scope(rows, "Your preferences").GetProperty("line").GetString());
        Assert.Equal("Applies to everyone on Progenitor Forms Test", Scope(rows, "Server settings").GetProperty("line").GetString());
        // The line is drawn as its own muted row under the heading, and the scope heading carries the red rule.
        Assert.Equal("Applies to everyone on Progenitor Forms Test",
            _runtime.CellRow(ModSettingsScopes.ScopeLineKeyPrefix + "1")!.Cells().Single(c => c.Key == ModSettingsScopes.CellScopeLine).Text);
        Assert.Equal(2, _runtime.SectionRules);
    }

    [Fact]
    public void An_empty_scope_section_is_omitted()
    {
        Register(withServer: false);
        Open();
        Assert.Equal(new[] { "Your preferences" }, ScopeHeadings(PageRows()));

        ModsMenu.Close();
        ModSettingsPages.ResetForTests();
        Register(withPersonal: false);
        _context = Joined(admin: true);
        ServerSent(10);
        Open();
        Assert.Equal(new[] { "Server settings" }, ScopeHeadings(PageRows()));
    }

    // ---- page states ----------------------------------------------------------------------------

    [Fact]
    public void As_admin_the_server_setting_is_edited_through_the_server()
    {
        Register();
        _context = Joined(admin: true);
        ServerSent(12);
        Open();
        var rows = PageRows();
        var limit = Setting(rows, LimitId);
        Assert.Equal("server-request", limit.GetProperty("control").GetString());
        Assert.False(limit.GetProperty("adminOnly").GetBoolean());
        Assert.Equal("editable", limit.GetProperty("availability").GetProperty("state").GetString());
        Assert.True(Scope(rows, "Server settings").GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public void An_admins_server_wide_switch_is_drawn_as_a_switch_not_a_text_field()
    {
        // Review fix (live: "Progenitor signature" drew as a text field reading "true"): a server-owned row's Kind is
        // ReadOnly whatever its type, so the entry's bool type decides the control.
        var file = new ConfigFile(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".cfg"), saveOnInit: true);
        var signature = file.Bind("List", "Signature", true, "Signature.");
        _handles.Add(ConfigSync.Register(Id, signature));
        _handles.Add(ServerSettings.Register(Id, signature));
        _handles.Add(ModSettingsPages.Register(_owner, Id, page => page.Section("Server").Toggle(signature, label: "Signature", scope: SettingScope.Server)));
        ConfigSync.ApplyRecords(new[] { new ConfigSyncRecord(Id, "List", "Signature", "true") }, snapshot: true);
        _context = Joined(admin: true);
        Open();
        Assert.True(_runtime.HasWidget("toggle", "Signature"));
        Assert.False(_runtime.HasWidget("input", "Signature"));
    }

    [Fact]
    public void A_player_who_is_not_an_admin_reads_admin_only_and_the_value()
    {
        Register();
        _context = Joined(admin: false);
        ServerSent(12);
        Open();
        var rows = PageRows();
        var limit = Setting(rows, LimitId);
        Assert.Equal("readonly", limit.GetProperty("control").GetString());
        Assert.True(limit.GetProperty("adminOnly").GetBoolean());
        Assert.Equal("admin-only", limit.GetProperty("availability").GetProperty("state").GetString());
        Assert.Equal("Admin only · 12", _runtime.ValueOf("info", "Example limit"));
        string help = limit.GetProperty("helpLine").GetString()!;
        Assert.Contains("Only server admins can change this.", help, StringComparison.Ordinal);
        Assert.DoesNotContain(ModSettingsLayout.ReadOnlyLockedReason, help, StringComparison.Ordinal);
        Assert.False(Scope(rows, "Server settings").GetProperty("canEdit").GetBoolean());
        // Your settings stay the player's own.
        Assert.Equal("toggle", Setting(rows, HintsId).GetProperty("control").GetString());
        Assert.True(Setting(rows, HintsId).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void Not_connected_grays_server_settings_and_says_the_server_you_join()
    {
        Register();
        _context = new SettingsScopeContext();
        Open();
        var rows = PageRows();
        var limit = Setting(rows, LimitId);
        Assert.False(limit.GetProperty("enabled").GetBoolean());
        Assert.True(limit.GetProperty("greyed").GetBoolean());
        Assert.Equal("Not connected to a server.", limit.GetProperty("reason").GetString());
        Assert.Equal("not-connected", limit.GetProperty("availability").GetProperty("code").GetString());
        Assert.DoesNotContain(ModSettingsLayout.ReadOnlyAwaitingReason, limit.GetProperty("helpLine").GetString(), StringComparison.Ordinal);
        Assert.Equal("Applies to everyone on the server you join", Scope(rows, "Server settings").GetProperty("line").GetString());
        Assert.True(Setting(rows, HintsId).GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData(1, "Waiting for the server…")]
    [InlineData(2, "Server doesn't run Progenitor.")]
    public void Joining_and_a_vanilla_server_have_their_own_reasons(int connection, string reason)
    {
        Register();
        _context = new SettingsScopeContext { Connection = (SettingConnection)connection, ServerAddress = "1.2.3.4:9876" };
        Open();
        var rows = PageRows();
        Assert.Equal(reason, Setting(rows, LimitId).GetProperty("reason").GetString());
        Assert.Equal("Applies to everyone on 1.2.3.4:9876", Scope(rows, "Server settings").GetProperty("line").GetString());
    }

    [Fact]
    public void A_server_without_the_mod_grays_it_and_hide_when_alone_drops_it_and_its_section()
    {
        Register();
        _context = Joined(admin: true, part: ServerPartState.NotOnServer);
        Open();
        var limit = Setting(PageRows(), LimitId);
        Assert.Equal("Needs the server part of this mod.", limit.GetProperty("reason").GetString());
        Assert.False(limit.GetProperty("hideWhenAlone").GetBoolean());

        ModsMenu.Close();
        ModSettingsPages.ResetForTests();
        Register(hideLimitWhenAlone: true);
        Open();
        var rows = PageRows();
        // The row is hidden, not grayed; with nothing left the Server settings section is omitted
        // (its heading hidden, or not read back at all as a hidden widget).
        Assert.False(HasSetting(rows, LimitId) && Setting(rows, LimitId).GetProperty("visible").GetBoolean());
        var view = ModsMenu.ModPage!.SettingsView!;
        Assert.Contains(LimitId, view.HiddenByShowIf);
        var server = All(rows, ModSettingsScopes.RowScopeSection).Where(s => s.GetProperty("heading").GetString() == "Server settings").ToList();
        Assert.True(server.Count == 0 || server[0].GetProperty("shown").GetInt32() == 0);
        Assert.Contains("Your preferences", ScopeHeadings(rows));
    }

    /// <summary>
    /// JOINADMIN1's Players joining row has this shape: a Server-wide button with a show-if. It follows the
    /// show-if on build and live (the hello / remote-install status re-evaluates an open page through
    /// <see cref="ModsMenu.NoteSettingsScopeChanged"/>), and settings search finds a button by its caption
    /// and its help, not by its row label.
    /// </summary>
    [Fact]
    public void A_server_wide_button_follows_its_show_if_live()
    {
        bool admin = false;
        const string open = Id + "/button/Open";
        _handles.Add(ModSettingsPages.Register(_owner, Id, page =>
        {
            page.Section("Server message");
            page.Button("Show", () => { }, label: "Server message", enableIf: () => true).InScope(SettingScope.ServerWide);
            page.Button("Open", () => { }, label: "Players joining this server", help: "Mods shared with refused players.", showIf: () => admin)
                .InScope(SettingScope.ServerWide);
        }));
        _context = Joined(admin: true);
        Open();
        var view = ModsMenu.ModPage!.SettingsView!;
        Assert.Contains(open, view.RowIds);
        Assert.Contains(open, view.HiddenByShowIf);

        admin = true;
        Assert.True(ModsMenu.NoteSettingsScopeChanged("test"));
        view = ModsMenu.ModPage!.SettingsView!;
        Assert.DoesNotContain(open, view.HiddenByShowIf);
        Assert.Equal(2, view.ShownRows);

        // Search: a button matches by its caption and its help, never by its row label.
        string layout = SourcePin.MethodBody(SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModSettingsLayout.cs"),
            "internal static bool Matches(ModSettingsItem item, string? query)");
        Assert.Contains("return Contains(item.Label, q) || Contains(item.Options.Help, q);", layout, StringComparison.Ordinal);
    }

    /// <summary>
    /// JOINADMIN1: the Players joining row as the runtime builds it — registered once on Progenitor's own page
    /// (<see cref="ConfigSync.FrameworkSettingsId"/>) at Core start, before any hello, with the server message
    /// section's Show button before it, and opened as Progenitor's mod page. The row is evaluated on every
    /// build, follows the hello / status live, asks for a fresh status while hidden, and logs its decision.
    /// </summary>
    [Fact]
    public void The_players_joining_row_on_Progenitors_page_follows_the_hello_and_the_status()
    {
        const string pageId = ConfigSync.FrameworkSettingsId;
        const string rowId = pageId + "/button/" + JoinServerSettings.PlayersJoiningButton;
        int asks = 0;
        int opens = 0;
        (bool, bool, RemoteInstallStatus?) inputs = (false, false, null);
        JoinServerSettings.ResetPlayersJoiningForTests();
        try
        {
            // Registered at Core start, before the join: nothing known yet.
            _handles.Add(ModSettingsPages.Register(_owner, pageId, page =>
            {
                page.Section(JoinServerSettings.PageSection);
                page.Button("Show", () => { }, label: "Server message").InScope(SettingScope.ServerWide);
                JoinServerSettings.AddPlayersJoiningRow(page, () => opens++);
            }));
            JoinServerSettings.PlayersJoiningInputsProbe = () => inputs;
            JoinServerSettings.PlayersJoiningStatusAsk = () => { asks++; return true; };

            // The hello arrives (admin), no remote-install status yet; then the page opens.
            inputs = (true, true, null);
            _context = Joined(admin: true);
            // Three homes: the row is on the Server tab (When players join / Mods players need), not on the mod page.
            if (!ModsMenu.IsOpen) ModsMenu.Open();
            ModsMenu.SelectTab("server");
            Assert.NotNull(ModsMenu.ServerPaneForTests?.SettingsView);
            var view = ModsMenu.ServerPaneForTests!.SettingsView!;
            Assert.Contains(rowId, view.RowIds);
            Assert.DoesNotContain(rowId, view.HiddenByShowIf);
            Assert.Contains(_log.Infos, l => l.Contains("JoinAdmin: Players joining row considered: shown True (serverSupports True, helloAdmin True, remote-install status none)"));

            // A status that says not authorized hides it (the toolbar's rule) and asks for a fresh one.
            inputs = (true, true, new RemoteInstallStatus(0, true, false, true, RemoteReachability.Unknown, "", false, null));
            Assert.True(ModsMenu.NoteSettingsScopeChanged("test"));
            view = ModsMenu.ServerPaneForTests!.SettingsView!;
            Assert.Contains(rowId, view.HiddenByShowIf);
            Assert.True(asks > 0);
            Assert.Contains(_log.Infos, l => l.Contains("shown False (serverSupports True, helloAdmin True, remote-install status not authorized)"));

            // The fresh status authorizes: shown again, in place.
            inputs = (true, true, new RemoteInstallStatus(0, true, true, true, RemoteReachability.Unknown, "", false, null));
            Assert.True(ModsMenu.NoteSettingsScopeChanged("test"));
            view = ModsMenu.ServerPaneForTests!.SettingsView!;
            Assert.DoesNotContain(rowId, view.HiddenByShowIf);
            Assert.Equal(2, view.ShownRows);
        }
        finally
        {
            JoinServerSettings.ResetPlayersJoiningForTests();
        }
    }

    [Fact]
    public void A_named_server_part_without_its_value_reads_as_a_version_mismatch()
    {
        Register();
        _context = Joined(admin: true, part: ServerPartState.Running);
        Open();
        Assert.Equal("Server runs a different version of this mod.", Setting(PageRows(), LimitId).GetProperty("reason").GetString());
    }

    [Fact]
    public void Reset_all_covers_your_settings_only()
    {
        // Entries that carry their default (the shim's ConfigEntry does not), both changed from it.
        var file = new ConfigFile(Path.Combine(_dir, "reset.cfg"), saveOnInit: true);
        var hints = new DefaultedEntry<bool>(file, "Menu", "ShowHints", true) { Stored = false };
        var limit = new DefaultedEntry<int>(file, "Menu", "ExampleLimit", 10);
        _handles.Add(ConfigSync.Register(Id, limit));
        _handles.Add(ModSettingsPages.Register(_owner, Id, page => page.Setting(hints).Setting(limit, scope: SettingScope.Server)));
        _context = Joined(admin: true);
        ServerSent(12);
        Open();
        var view = ModsMenu.ModPage!.SettingsView!;
        Assert.Equal(12, limit.Stored);
        Assert.Equal(new[] { HintsId }, view.ResettableRows().Select(r => r.RowId).ToArray());
    }

    [Fact]
    public void A_change_of_admin_status_rebuilds_the_page_with_the_other_control()
    {
        Register();
        _context = Joined(admin: true);
        ServerSent(12);
        Open();
        Assert.Equal("server-request", Setting(PageRows(), LimitId).GetProperty("control").GetString());

        _context = Joined(admin: false);
        Assert.True(ModsMenu.ModPage!.SettingsView!.NeedsRebuildForScope());
        Assert.True(ModsMenu.NoteSettingsScopeChanged("test"));
        Assert.Equal("readonly", Setting(PageRows(), LimitId).GetProperty("control").GetString());
        Assert.False(ModsMenu.ModPage!.SettingsView!.NeedsRebuildForScope());
    }

    [Fact]
    public void A_gray_alone_changes_in_place()
    {
        Register();
        _context = new SettingsScopeContext { Connection = SettingConnection.Waiting };
        Open();
        var page = ModsMenu.ModPage;
        Assert.Equal("waiting", Setting(PageRows(), LimitId).GetProperty("availability").GetProperty("code").GetString());

        _context = new SettingsScopeContext { Connection = SettingConnection.NotProgenitor };
        Assert.True(ModsMenu.NoteSettingsScopeChanged("test"));
        Assert.Same(page, ModsMenu.ModPage);
        Assert.Equal("not-progenitor", Setting(PageRows(), LimitId).GetProperty("availability").GetProperty("code").GetString());
    }

    [Fact]
    public void Hide_when_alone_needs_a_row_before_it()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ModSettingsPages.Register(_owner, "example.scopes.bad", page => page.HideWhenAlone()));
        Assert.Throws<InvalidOperationException>(() =>
            ModSettingsPages.Register(_owner, "example.scopes.bad2", page => page.Button("B", () => { }).Divider().HideWhenAlone()));
    }

    // ---- regrouping (pure) -------------------------------------------------------------------------

    [Fact]
    public void A_section_with_both_scopes_is_split_and_dividers_follow_the_row_before()
    {
        var file = new ConfigFile(Path.Combine(_dir, "split.cfg"), saveOnInit: true);
        var local = SettingsRow.ForEntry(Id, file.Bind("S", "Local", 1, ""), false);
        var synced = SettingsRow.ForEntry(Id, file.Bind("S", "Synced", 1, ""), true);
        var section = new ModSettingsSection("Mixed", ModSettingsSectionKind.Page, new List<ModSettingsItem>
        {
            ModSettingsItem.ForRow(local),
            ModSettingsItem.ForDivider("d1"),
            ModSettingsItem.ForRow(synced),
            ModSettingsItem.ForDivider("d2"),
        });
        var groups = ModSettingsScopes.Regroup(new[] { section });
        Assert.Equal(new[] { SettingScope.Personal, SettingScope.ServerWide }, groups.Select(g => g.Scope).ToArray());
        Assert.Equal(new[] { "Mixed" }, groups[0].Sections.Select(s => s.Title).ToArray());
        Assert.Equal(new[] { local.RowId, "d1" }, groups[0].Sections[0].Items.Select(i => i.Id).ToArray());
        Assert.Equal(new[] { synced.RowId, "d2" }, groups[1].Sections[0].Items.Select(i => i.Id).ToArray());
        Assert.All(groups[1].Sections, s => Assert.Equal(SettingScope.ServerWide, s.Scope));
    }

    private sealed class DefaultedEntry<T> : ConfigEntryBase
    {
        public DefaultedEntry(ConfigFile file, string section, string key, T value)
            : base(file, new ConfigDefinition(section, key), typeof(T))
        {
            Stored = value;
            DefaultValue = value;
            Description = new ModSettingsPageTests.FakeDescription(null);
        }

        public object? Stored { get; set; }

        public object? DefaultValue { get; }

        public ModSettingsPageTests.FakeDescription Description { get; }

        public override object? BoxedValue
        {
            get => Stored;
            set => Stored = value;
        }
    }

    [Theory]
    [InlineData(false, false, false, 0)]
    [InlineData(true, false, false, 2)]
    [InlineData(true, false, true, 1)]
    [InlineData(true, true, false, 3)]
    public void The_connection_comes_from_today_s_facts(bool connected, bool handshakeOk, bool requestSeen, int expected) =>
        Assert.Equal((SettingConnection)expected, SettingsScopeContext.ConnectionOf(connected, handshakeOk, requestSeen));
}
