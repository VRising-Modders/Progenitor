using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Job MODS1, updated by MODS2: the Mods tab is the player's list of installed mods. Provenance
/// comes from the same evidence the Install tab reads — this framework's own id list, then each
/// root's <c>BepInEx/progenitor-install.json</c> ledger (INSTORD2), then a server-only claim — and
/// it decides what the row may do: a ledgered install offers Remove, another tool's file is
/// read-only, and the loader plus this framework's own plugins are read-only rows in the collapsed
/// <c>Required (N)</c> group at the BOTTOM of the sidebar (MODS2 retired MODS1's developer toggle).
/// Game-free: a fake inventory, a synthetic ledger root and the fake UI runtime.
/// </summary>
[Collection("PluginLogger")]
public sealed class ModsProvenanceTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _game;

    public ModsProvenanceTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
        Handshake.ResetTables();
        ModMenuInventory.ResetForTests();
        WorldEpoch.ResetForTests();
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.InstallLinkProbe = null;
        ModsMenu.InstalledRemover = null;
        _game = Path.Combine(Path.GetTempPath(), "progenitor-mods1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_game, "BepInEx"));
    }

    public void Dispose()
    {
        UiLayoutBatch.ResetForTests();
        ModsMenu.Close();
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.InstallLinkProbe = null;
        ModsMenu.InstalledRemover = null;
        ModsMenu.ResetTabBindingsForTests();
        ModsWorkspaceFrame.ResetForTests();
        ModsMenu.UninstallLifecycleWatch();
        Handshake.UninstallClientServerContextWatch();
        ModMenuInventory.ResetForTests();
        Handshake.ResetTables();
        WorldEpoch.ResetForTests();
        Plugin.Logger = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_game, true); } catch { }
    }

    private string Bep => Path.Combine(_game, "BepInEx");

    private static KeyValuePair<string, string> Entry(string id, string name) => new(id, name);

    private static ModMenuInventoryRow Inventory(string id, string name, string location, string version = "1.0") =>
        new() { Id = id, DisplayName = name, Location = location, Version = version, Status = "Loaded on this client" };

    private static InstalledLedgerRow Ledgered(
        string id,
        string fileName,
        string version = "1.0.0",
        string category = "mod",
        string? filePath = null) =>
        new(id, "sha", version, fileName, "package:" + id + "@" + version, category, InstalledState.ClientRoot, filePath ?? "");

    private static ModsSidebarRow Row(ModsSidebarModel model, string id) =>
        Assert.IsType<ModsSidebarRow>(model.Find(id));

    /// <summary>
    /// The order of the three rules. A framework id is a developer mod even when something else
    /// also names it; a ledgered id is Progenitor's; a loaded file nobody ledgered is another
    /// tool's; a row only the server reported is server-only.
    /// What this would catch: reading provenance from the display name, from the plugins folder
    /// path, or from the session inspection sha instead of the install ledger.
    /// </summary>
    [Fact]
    public void Provenance_is_the_framework_list_then_the_install_ledger_then_the_server_list()
    {
        var entries = new List<KeyValuePair<string, string>>
        {
            Entry(ModMenuInventory.CoreId, "Progenitor"),
            Entry("progenitor.dev", "Progenitor Dev Bridge"),
            Entry("progenitor.sample.alpha", "Progenitor Sample Alpha"),
            Entry("example.phialstore", "PhialStore"),
            Entry("other.tool.mod", "GroundPickupGlow"),
            Entry("server.only.mod", "ServerOnly"),
        };
        var inventory = new[]
        {
            Inventory("example.phialstore", "PhialStore", "Local", "1.0.0"),
            Inventory("other.tool.mod", "GroundPickupGlow", "Local + server list", "2.1"),
            Inventory("server.only.mod", "ServerOnly", "Server list", ""),
        };
        var ledger = new[] { Ledgered("example.phialstore", "example.phialstore.dll") };

        var model = ModMenuInventory.BuildSidebar(entries, inventory, ledger);

        // MODS2: the framework's own ids read `required`, not `framework`. FWALLOW1: a sample is
        // not one of them — with no ledger row and no reload-folder file it is an ordinary row.
        Assert.Equal("required", Row(model, ModMenuInventory.CoreId).ProvenanceName);
        Assert.Equal("required", Row(model, "progenitor.dev").ProvenanceName);
        Assert.Equal("other", Row(model, "progenitor.sample.alpha").ProvenanceName);
        Assert.Equal("progenitor", Row(model, "example.phialstore").ProvenanceName);
        Assert.Equal("other", Row(model, "other.tool.mod").ProvenanceName);
        Assert.Equal("server", Row(model, "server.only.mod").ProvenanceName);

        // The read-only sentence belongs to another tool's file only.
        Assert.Equal(ModMenuInventory.OtherToolNote, Row(model, "other.tool.mod").PageNote);
        Assert.Null(Row(model, "example.phialstore").PageNote);
        // MODS2: a required row is read-only and says so; the loader says it is the loader.
        Assert.Equal(ModMenuInventory.RequiredNote, Row(model, ModMenuInventory.CoreId).PageNote);
        Assert.Equal(ModMenuInventory.LoaderNote, Row(model, ModMenuInventory.LoaderId).PageNote);

        // Muted second line: version, then the provenance word.
        Assert.Equal("1.0.0 · Progenitor", Row(model, "example.phialstore").Subtitle);
        Assert.Equal("2.1 · another tool", Row(model, "other.tool.mod").Subtitle);
        Assert.Equal(ModMenuInventory.VersionUnknown + " · on this server", Row(model, "server.only.mod").Subtitle);
        // No inventory row for Core in this fixture, so its version is unknown — and it still
        // reads `required`, which is the half that decides what the page may do.
        Assert.Equal(ModMenuInventory.VersionUnknown + " · required", Row(model, ModMenuInventory.CoreId).Subtitle);
    }

    /// <summary>
    /// A ledger row's package id is the listing key for an ordinary package and the plugin GUID for
    /// a Progenitor mod, so the plugin's own file name also has to name it.
    /// What this would catch: matching the ledger only on its package id, which reads an installed
    /// ordinary package as another tool's file.
    /// </summary>
    [Fact]
    public void A_ledger_row_named_by_its_package_still_matches_the_plugin_file_it_wrote()
    {
        var entries = new List<KeyValuePair<string, string>> { Entry("example.phialstore", "PhialStore") };
        var ledger = new[] { Ledgered("ExampleTeam-PhialStore", "example.phialstore.dll", "1.2.0") };

        var model = ModMenuInventory.BuildSidebar(entries, Array.Empty<ModMenuInventoryRow>(), ledger);
        Assert.Equal("progenitor", Row(model, "example.phialstore").ProvenanceName);
        Assert.Equal("1.2.0", ModMenuInventory.LedgerRowsForPlugin(ledger, "example.phialstore")[0].Version);
        // A row with no source is not an install record.
        Assert.Empty(ModMenuInventory.LedgerRowsForPlugin(
            new[] { new InstalledLedgerRow("example.phialstore", "sha", "1", "example.phialstore.dll") },
            "example.phialstore"));
    }

    /// <summary>
    /// MODS2: the default list and the two groups below it. The required group holds the loader
    /// first, then Core, then the rest of this framework's plugins, and it is the LAST group —
    /// after the server group (decided 2026-09-13). FWALLOW1: a sample in the reload folder is
    /// one of the player's mods, not a required row.
    /// What this would catch: listing required rows in the default list, counting them in the
    /// overview, putting the required group above the server group or above the mods, losing the
    /// loader row, or leaving server-only rows in the main list.
    /// </summary>
    [Fact]
    public void Required_and_server_rows_are_their_own_groups_below_the_player_mods()
    {
        var entries = new List<KeyValuePair<string, string>>
        {
            Entry(ModMenuInventory.CoreId, "Progenitor"),
            Entry("progenitor.sample", "Progenitor Sample"),
            Entry("blood.pool.hover", "BasinHoverTips"),
            Entry("clan.treasury.hud", "ClanResourceHUD"),
            Entry("server.only.mod", "ServerOnly"),
        };
        var inventory = new[]
        {
            Inventory("blood.pool.hover", "BasinHoverTips", "Local"),
            Inventory("clan.treasury.hud", "ClanResourceHUD", "Local"),
            Inventory("server.only.mod", "ServerOnly", "Server list", ""),
        };

        var reload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["progenitor.sample"] = @"C:\Game\BepInEx\progenitor-reload\Progenitor.Sample.dll",
        };
        var model = ModMenuInventory.BuildSidebar(
            entries, inventory, Array.Empty<InstalledLedgerRow>(), loaderVersion: "6.0.0", reloadFiles: reload);
        Assert.Equal(3, model.Shown);
        // The loader and Core: two required rows. The sample is not one (FWALLOW1).
        Assert.Equal(2, model.Required);
        Assert.Equal(1, model.Server);
        Assert.Equal(new[] { "progenitor.sample", "blood.pool.hover", "clan.treasury.hud" }, Ids(model.VisibleModRows));
        Assert.Equal(new[] { "server.only.mod" }, Ids(model.ServerRows));
        Assert.Equal(
            new[] { ModMenuInventory.LoaderId, ModMenuInventory.CoreId },
            Ids(model.RequiredRows));
        Assert.Equal("reload", Row(model, "progenitor.sample").ProvenanceName);
        Assert.Equal(ModMenuInventory.GroupMods, Row(model, "progenitor.sample").Group);
        Assert.Equal(ModMenuInventory.GroupServer, Row(model, "server.only.mod").Group);
        Assert.Equal(ModMenuInventory.GroupMods, Row(model, "blood.pool.hover").Group);
        Assert.Equal(ModMenuInventory.GroupRequired, Row(model, ModMenuInventory.CoreId).Group);
        Assert.Equal(ModMenuInventory.GroupRequired, Row(model, ModMenuInventory.LoaderId).Group);
        // The required rows are read-only: no ledger row, so nothing offers Remove.
        Assert.Empty(Row(model, ModMenuInventory.CoreId).Ledger);
        Assert.True(Row(model, ModMenuInventory.LoaderId).IsLoader);
        Assert.False(Row(model, ModMenuInventory.CoreId).IsLoader);
        Assert.Equal("6.0.0 · required", Row(model, ModMenuInventory.LoaderId).Subtitle);
        // The overview counts the player's mods only, and never mentions a developer row again.
        Assert.Equal("3 mods found on this client.", model.LocalSummary);
        Assert.DoesNotContain("developer", model.LocalSummary, StringComparison.Ordinal);

        // Sidebar order: the player's mods, then the server group, then the required group.
        Assert.Equal(
            new[]
            {
                "progenitor.sample", "blood.pool.hover", "clan.treasury.hud", "server.only.mod",
                ModMenuInventory.LoaderId, ModMenuInventory.CoreId,
            },
            Ids(model.Rows));
    }

    /// <summary>
    /// MODSPOLISH1 (live, 2026-09-13): the install ledger outranks the framework-id prefix. A mod
    /// the player installed from the Install tab that publishes its GUID under this framework's
    /// namespace — <c>progenitor.hotdemo</c>, ledgered <c>mod</c> on the client root — is
    /// one of the player's mods with Remove, not a <c>Required</c> row; the framework's own
    /// unledgered images keep the required group, and a ledgered mod nothing has loaded yet is
    /// still listed rather than swallowed by the prefix.
    /// What this would catch: restoring the prefix-first order (HotDemo goes back into
    /// <c>Required (8)</c> with no Remove and no ledger rows, so its page offers no action and it
    /// is counted in <c>status.mods.required</c> instead of <c>shown</c>), or letting any ledger
    /// row at all — a <c>framework</c> row for Core itself — take a framework image out of the
    /// required group.
    /// </summary>
    [Fact]
    public void A_ledgered_mod_under_the_framework_namespace_is_the_players_mod_not_a_required_row()
    {
        var entries = new List<KeyValuePair<string, string>>
        {
            Entry(ModMenuInventory.CoreId, "Progenitor"),
            Entry("progenitor.dev", "Progenitor Dev Bridge"),
            Entry("progenitor.sample.alpha", "Progenitor Sample Alpha"),
            Entry("progenitor.hotdemo", "HotDemo"),
            Entry("other.tool.mod", "GroundPickupGlow"),
        };
        var inventory = new[]
        {
            Inventory("progenitor.hotdemo", "HotDemo", "Local", "1.0.0"),
            Inventory("other.tool.mod", "GroundPickupGlow", "Local", "2.1"),
        };
        var ledger = new[]
        {
            // The live ledger row: installed from the Install tab, category `mod`, client root.
            Ledgered("progenitor.hotdemo", "progenitor.hotdemo.dll"),
            // Core's own image is ledgered too, as `framework` — that must not move it.
            Ledgered(ModMenuInventory.CoreId, "Progenitor.Core.dll", category: ServerInstallLedger.CategoryFramework),
        };

        var model = ModMenuInventory.BuildSidebar(entries, inventory, ledger, loaderVersion: "6.0.0");

        Assert.Equal("progenitor", Row(model, "progenitor.hotdemo").ProvenanceName);
        Assert.Equal(ModMenuInventory.GroupMods, Row(model, "progenitor.hotdemo").Group);
        // Provenance decides what the page may do: a Progenitor row carries its ledger rows (so
        // Remove has files to delete) and wears no read-only sentence.
        Assert.NotEmpty(Row(model, "progenitor.hotdemo").Ledger);
        Assert.Null(Row(model, "progenitor.hotdemo").PageNote);
        // The framework's own images are untouched, Core's `framework` ledger row included.
        Assert.Equal("required", Row(model, ModMenuInventory.CoreId).ProvenanceName);
        Assert.Empty(Row(model, ModMenuInventory.CoreId).Ledger);
        Assert.Equal("required", Row(model, "progenitor.dev").ProvenanceName);
        // FWALLOW1: the namespace alone makes nothing required; an unledgered sample is an ordinary row.
        Assert.Equal("other", Row(model, "progenitor.sample.alpha").ProvenanceName);
        // Counts follow: HotDemo is one of the three shown mods, not one of the three required rows.
        Assert.Equal(3, model.Shown);
        Assert.Equal(3, model.Required);
        Assert.Equal(
            new[] { "progenitor.sample.alpha", "progenitor.hotdemo", "other.tool.mod" },
            Ids(model.VisibleModRows));

        // The same rule in LedgerOnlyRows: a ledgered HotDemo nothing has loaded is still listed.
        var unloaded = ModMenuInventory.BuildSidebar(
            new List<KeyValuePair<string, string>> { Entry(ModMenuInventory.CoreId, "Progenitor") },
            Array.Empty<ModMenuInventoryRow>(),
            new[] { Ledgered("progenitor.hotdemo", "progenitor.hotdemo.dll") });
        Assert.Equal("progenitor", Row(unloaded, "progenitor.hotdemo").ProvenanceName);
        Assert.Equal(1, unloaded.Shown);

        // The predicate itself: `mod` and its `pending` download are the player's; `framework` is not.
        Assert.True(ModMenuInventory.IsLedgeredMod(new[] { Ledgered("x", "x.dll") }));
        Assert.True(ModMenuInventory.IsLedgeredMod(
            new[] { Ledgered("x", "x.dll", category: ServerInstallLedger.CategoryPending) }));
        Assert.False(ModMenuInventory.IsLedgeredMod(
            new[] { Ledgered("x", "x.dll", category: ServerInstallLedger.CategoryFramework) }));
        Assert.False(ModMenuInventory.IsLedgeredMod(Array.Empty<InstalledLedgerRow>()));
    }

    /// <summary>
    /// Search decides only whether a row is listed; it never changes provenance. The counts and the
    /// overview sentence follow the search together (live residual 1).
    /// </summary>
    [Fact]
    public void Search_filters_whatever_is_shown_and_the_counts_follow_it_together()
    {
        var entries = new List<KeyValuePair<string, string>>
        {
            Entry(ModMenuInventory.CoreId, "Progenitor"),
            Entry("blood.pool.hover", "BasinHoverTips"),
            Entry("clan.treasury.hud", "ClanResourceHUD"),
        };
        var inventory = new[]
        {
            Inventory("blood.pool.hover", "BasinHoverTips", "Local"),
            Inventory("clan.treasury.hud", "ClanResourceHUD", "Local"),
        };

        var model = ModMenuInventory.BuildSidebar(
            entries, inventory, Array.Empty<InstalledLedgerRow>(), null,
            (id, name) => ModsMenu.MatchesSearch(id, name, "blood"));
        Assert.Equal(1, model.Shown);
        Assert.Equal(new[] { "blood.pool.hover" }, Ids(model.VisibleModRows));
        Assert.Equal("1 mod found on this client.", model.LocalSummary);
        // A search narrows the required group too: neither the loader nor Core matches "blood".
        Assert.Equal(0, model.Required);
        Assert.Empty(Ids(model.RequiredRows));
        AssertSummaryMatchesCounts(model);
    }

    /// <summary>
    /// Live residual 1 (client gen 4): the overview and <c>status.mods</c> disagreed on a count.
    /// The counts are one set — the rows that pass the search — so the sentence and the readback
    /// cannot disagree, with or without a search. MODS2: the sentence counts the player's mods and
    /// the required rows have their own count, so a framework id is never in the sentence at all.
    /// What this would catch: counting the summary over every row and the status over the searched
    /// rows again, or letting a required id back into the overview sentence.
    /// </summary>
    [Fact]
    public void The_overview_sentence_and_the_status_counts_are_one_set()
    {
        var entries = new List<KeyValuePair<string, string>>
        {
            Entry(ModMenuInventory.CoreId, "Progenitor"),
            Entry("progenitor.dev", "Progenitor Dev Bridge"),
            Entry("progenitor.sample", "Progenitor Sample"),
            Entry("progenitor.sample.consentdemo", "Sample Consent"),
            Entry("progenitor.sample.protocol", "Sample Protocol"),
            Entry("progenitor.sample.ui", "Sample UI"),
            Entry("blood.pool.hover", "BasinHoverTips"),
            Entry("clan.treasury.hud", "ClanResourceHUD"),
        };
        var inventory = new[]
        {
            Inventory("blood.pool.hover", "BasinHoverTips", "Local"),
            Inventory("clan.treasury.hud", "ClanResourceHUD", "Local"),
        };

        // Every framework id counts, Core included, and the loader makes three required rows
        // (loader, Core, dev bridge). FWALLOW1: the four samples are the player's mods.
        var model = ModMenuInventory.BuildSidebar(entries, inventory, Array.Empty<InstalledLedgerRow>());
        Assert.Equal(3, model.Required);
        Assert.Equal(6, model.Shown);
        Assert.Equal("6 mods found on this client.", model.LocalSummary);
        AssertSummaryMatchesCounts(model);

        // A search narrows every group together.
        var searched = ModMenuInventory.BuildSidebar(
            entries, inventory, Array.Empty<InstalledLedgerRow>(), null,
            (id, name) => ModsMenu.MatchesSearch(id, name, "sample"));
        Assert.Equal(4, searched.Shown);
        Assert.Equal(0, searched.Required);
        AssertSummaryMatchesCounts(searched);
    }

    /// <summary>The sentence is built from the one number the readback reports as <c>shown</c>.</summary>
    private static void AssertSummaryMatchesCounts(ModsSidebarModel model)
    {
        Assert.Equal(
            model.Shown + (model.Shown == 1 ? " mod found on this client." : " mods found on this client."),
            model.LocalSummary);
        Assert.DoesNotContain("developer", model.LocalSummary, StringComparison.Ordinal);
    }

    /// <summary>
    /// MODS2 replaces MODS1's residuals 2 and 3 (a toggle whose caption and rebuild had to be
    /// pinned): there is no checkbox in the sidebar at all, and the overview sentence counts the
    /// player's mods only. Residual 4 stays pinned — the overview's true action sentence.
    /// What this would catch: the developer checkbox coming back, the required rows being counted
    /// in the overview sentence, or the stale "not available in this menu yet" line returning.
    /// </summary>
    [Fact]
    public void The_sidebar_has_no_developer_checkbox_and_the_overview_counts_the_player_mods()
    {
        using var blood = ModsMenu.Register(new object(), "blood.pool.hover", "BasinHoverTips");
        ModsMenu.Open();

        string overview = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        // Residual 4: no stale capability claim.
        ContainsText(overview, ModsLanding.Footer);
        Assert.DoesNotContain("not available in this menu yet", overview, StringComparison.Ordinal);
        Assert.Contains("1 mod found on this client", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("developer", overview, StringComparison.OrdinalIgnoreCase);

        // No toggle of any kind in the sidebar: the source has no Toggle row and the runtime has
        // no toggle widget to fire.
        string sidebar = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("list"));
        Assert.DoesNotContain("\"kind\":\"toggle\"", sidebar, StringComparison.Ordinal);
        Assert.DoesNotContain("Developer mods", sidebar, StringComparison.Ordinal);
        Assert.False(_runtime.FireToggle("Developer mods", true));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace()));
        var mods = doc.RootElement.GetProperty("mods");
        Assert.Equal(1, mods.GetProperty("shown").GetInt32());
        // MODS2 status shape: {shown, required, server}. The toggle's two fields are gone.
        Assert.False(mods.TryGetProperty("hiddenDeveloper", out _));
        Assert.False(mods.TryGetProperty("showDeveloper", out _));
        Assert.True(mods.GetProperty("required").GetInt32() >= 1);
    }

    private static string[] Ids(IEnumerable<ModsSidebarRow> rows)
    {
        var list = new List<string>();
        foreach (var row in rows) list.Add(row.Id);
        return list.ToArray();
    }

    /// <summary>
    /// The live sidebar and <c>mods_ui</c> readback. What this would catch: entries without
    /// provenance or group, a required row drawn in the default list, a missing
    /// <c>On this server (N)</c> or <c>Required (N)</c> group, the required group drawn above the
    /// server group, or a group state that does not survive a tab switch.
    /// </summary>
    [Fact]
    public void Sidebar_readback_carries_provenance_and_the_group_states_persist_in_the_tab_memory()
    {
        using var blood = ModsMenu.Register(new object(), "blood.pool.hover", "BasinHoverTips");
        var catalog = new ManifestEnvelope(
            new ManifestBody(1, "Test", "", new ManifestOperator("", "", ""), null,
                new[] { new ManifestMod("server.only.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, false) }),
            "");
        Assert.True(ModMenuInventory.RestoreServerContext("handshake-ok", Array.Empty<Handshake.ModRecord>(), catalog, 7));
        ModsMenu.Open();

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace()));
        var root = doc.RootElement;
        var entries = root.GetProperty("entries");
        Assert.Equal("other", Find(entries, "blood.pool.hover").GetProperty("provenance").GetString());
        Assert.Equal("mods", Find(entries, "blood.pool.hover").GetProperty("group").GetString());
        Assert.Equal("required", Find(entries, ModMenuInventory.CoreId).GetProperty("provenance").GetString());
        Assert.Equal("required", Find(entries, ModMenuInventory.CoreId).GetProperty("group").GetString());
        Assert.Equal("required", Find(entries, ModMenuInventory.LoaderId).GetProperty("provenance").GetString());
        Assert.Equal("server", Find(entries, "server.only.mod").GetProperty("provenance").GetString());
        Assert.Equal("server", Find(entries, "server.only.mod").GetProperty("group").GetString());
        var mods = root.GetProperty("mods");
        Assert.Equal(1, mods.GetProperty("shown").GetInt32());
        Assert.Equal(1, mods.GetProperty("server").GetInt32());
        // The loader and Core: two required rows for this fixture.
        Assert.Equal(2, mods.GetProperty("required").GetInt32());

        // Neither group's rows are drawn while both are collapsed, and the required caption is the
        // LAST row of the two: mods, On this server, Required.
        string sidebar = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("list"));
        Assert.Contains("BasinHoverTips", sidebar, StringComparison.Ordinal);
        Assert.Contains(ModsMenu.ServerGroupCaption(1), sidebar, StringComparison.Ordinal);
        Assert.Contains(ModsMenu.RequiredGroupCaption(2), sidebar, StringComparison.Ordinal);
        Assert.True(
            sidebar.IndexOf(ModsMenu.ServerGroupCaption(1), StringComparison.Ordinal)
            < sidebar.IndexOf(ModsMenu.RequiredGroupCaption(2), StringComparison.Ordinal),
            "the Required group must be drawn after the On this server group");
        Assert.True(
            sidebar.IndexOf("BasinHoverTips", StringComparison.Ordinal)
            < sidebar.IndexOf(ModsMenu.RequiredGroupCaption(2), StringComparison.Ordinal),
            "the Required group must be drawn after the player's mods");
        // The inventory has no local observation for a catalog-only entry, so its row is its id.
        Assert.DoesNotContain("server.only.mod", sidebar, StringComparison.Ordinal);
        Assert.DoesNotContain(ModMenuInventory.LoaderName + "\"", sidebar, StringComparison.Ordinal);

        // Expanding each group lists its rows; neither joins the default list's count.
        // MODLIST1: the group headings are cell rows now (`On this server 1 ►`); a click by their key
        // toggles them, and the MODS2 caption survives in the readback as `captionLegacy`.
        ModsMenu.ClickVisibleWidget("list", ModsListLayout.GroupKey(ModsListGroup.Server), null, null);
        Assert.Contains("server.only.mod", JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("list")), StringComparison.Ordinal);
        ModsMenu.ClickVisibleWidget("list", ModsListLayout.GroupKey(ModsListGroup.Required), null, null);
        string expanded = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("list"));
        Assert.Contains(ModMenuInventory.LoaderName, expanded, StringComparison.Ordinal);
        Assert.Contains("Progenitor", expanded, StringComparison.Ordinal);
        using (var after = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace())))
        {
            Assert.Equal(1, after.RootElement.GetProperty("mods").GetProperty("shown").GetInt32());
            Assert.Equal(2, after.RootElement.GetProperty("mods").GetProperty("required").GetInt32());
        }

        Assert.True(ModsMenu.TabStateForTests!.Mods.ServerGroupExpanded);
        Assert.True(ModsMenu.TabStateForTests!.Mods.RequiredGroupExpanded);

        // A required row's page is read-only: the note, and no Remove.
        ModsMenu.OpenMod(ModMenuInventory.CoreId);
        string corePage = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        ContainsText(corePage, ModMenuInventory.RequiredNote);
        Assert.DoesNotContain("\"label\":\"" + InstallModsCopy.Remove, corePage, StringComparison.Ordinal);
        // Progenitor's own About is the curated player text from Core.
        ContainsText(corePage, ProgenitorAbout.Markdown);

        // The loader's page is the one sentence, and nothing else.
        ModsMenu.OpenMod(ModMenuInventory.LoaderId);
        string loaderPage = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        ContainsText(loaderPage, ModMenuInventory.LoaderNote);
        Assert.DoesNotContain("All settings and keybinds", loaderPage, StringComparison.Ordinal);
        Assert.DoesNotContain("\"label\":\"" + InstallModsCopy.Remove, loaderPage, StringComparison.Ordinal);
    }

    private static JsonElement Find(JsonElement entries, string id)
    {
        foreach (var entry in entries.EnumerateArray())
            if (string.Equals(entry.GetProperty("id").GetString(), id, StringComparison.OrdinalIgnoreCase))
                return entry;
        throw new Xunit.Sdk.XunitException("no sidebar entry for " + id);
    }

    /// <summary>
    /// INSTORD2 fixtures: Remove on the Mods page deletes exactly the ledgered files of that plugin
    /// and drops their rows. What removal breaks: the mod stays loaded in this process until it
    /// restarts, so the page says so rather than claiming it is gone; anything the ledger does not
    /// name — a file another tool wrote, a config file, saved data — is not touched, so a mod
    /// removed and installed again comes back with its settings.
    /// </summary>
    [Fact]
    public void Remove_on_the_Mods_page_deletes_the_ledgered_files_and_keeps_everything_else()
    {
        byte[] bytes = { 3, 3, 3 };
        string sha = InstalledState.HashBytes(bytes);
        string reload = Write(Path.Combine(Bep, InstalledState.ReloadFolder, "example.phialstore.dll"), bytes);
        string foreign = Write(Path.Combine(Bep, "plugins", "GroundPickupGlow.dll"), new byte[] { 9 });
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            new ServerInstallLedgerEntry(
                "BepInEx/progenitor-reload/example.phialstore.dll", sha, 3,
                ServerInstallLedger.CategoryMod, "package:example.phialstore@1.0.0", DateTime.UtcNow),
        }).Write(_game);
        ModsMenu.LedgerForTests = InstalledState.Scan(Bep, null).Ledger;
        Assert.NotEmpty(ModsMenu.LedgerForTests);
        // MODSPAGE1: the page's status is what this process actually has, so the fixture says the
        // plugin is present on this client — without that it is a promoted copy nothing loaded yet,
        // which is the pending state this test is not about.
        Handshake.Provide("example.phialstore", "1.0.0");

        using var potion = ModsMenu.Register(new object(), "example.phialstore", "PhialStore");
        using var other = ModsMenu.Register(new object(), "other.tool.mod", "GroundPickupGlow");
        ModsMenu.Open();
        ModsMenu.OpenMod("example.phialstore");

        string page = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        // MODSPOLISH1: the sidebar subtitle row (`1.0.0 · Progenitor`) is no longer drawn on the
        // page — the head's meta line already carries the version and Uninstall already says this
        // menu owns the file — and the provenance is one muted block. A Progenitor-installed row
        // has no read-only sentence, so its block is the server requirement alone.
        // Live (HotDemo, 2026-10-03): no server word, no requirement sentence.
        DoesNotContainText(page, ModMenuInventory.RequirementUnknown);
        DoesNotContainText(page, "· Progenitor");
        // MODSPAGE1: the action is Uninstall and it asks a y/n before it removes anything.
        ContainsText(page, ModPageCopy.Uninstall);
        DoesNotContainText(page, ModMenuInventory.OtherToolNote);

        ModsMenu.ClickVisibleWidget("content", ModPageCopy.Uninstall, null, null);
        Assert.True(File.Exists(reload), "the prompt must not remove anything on its own");
        ModsMenu.ClickVisibleWidget("content", ModPageCopy.UninstallYes, null, null);

        Assert.False(File.Exists(reload));
        Assert.True(File.Exists(foreign));
        Assert.Equal(InstallModsCopy.RemovedNeedsRestart, ModsMenu.ModsActionNote);
        var after = ServerInstallLedger.TryRead(_game);
        Assert.NotNull(after);
        Assert.Empty(after!.Files);

        // Another tool's row is read-only: the note, and no Remove. Live F24 (2026-10-07): with no loaded
        // file to identify (no chainloader here) the note says why, not the generic other-tool line.
        ModsMenu.OpenMod("other.tool.mod");
        string readOnly = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        ContainsText(readOnly, ModSwitch.ManagedElsewhereReason(Progenitor.PluginActions.PluginFileActions.WhyNoLocation));
        DoesNotContainText(readOnly, ModMenuInventory.OtherToolNote);
        Assert.DoesNotContain("\"kind\":\"button\",\"label\":\"" + InstallModsCopy.Remove, readOnly, StringComparison.Ordinal);
        // The Remove sentence stayed on the page that produced it.
        DoesNotContainText(readOnly, InstallModsCopy.RemovedNeedsRestart);
    }

    /// <summary>
    /// The Update available line and the jump to the Install tab — the reverse of
    /// <c>OpenInstalledFromInstall</c>. What this would catch: offering an update for a version that
    /// is not newer, or a jump that leaves the Install tab without the listing selected.
    /// </summary>
    [Fact]
    public void An_update_the_install_index_knows_jumps_to_the_install_row()
    {
        ModsMenu.LedgerForTests = new[]
        {
            Ledgered("example.phialstore", "example.phialstore.dll", "1.0.0", ServerInstallLedger.CategoryPending),
        };
        using var potion = ModsMenu.Register(new object(), "example.phialstore", "PhialStore");
        ModsMenu.InstallLinkProbe = id =>
            string.Equals(id, "example.phialstore", StringComparison.OrdinalIgnoreCase)
                ? new ModsMenu.ModsInstallLink("ExampleTeam-PhialStore", "1.4.0")
                : null;

        ModsMenu.Open();
        ModsMenu.OpenMod("example.phialstore");
        string page = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        // MODPAGE2: the offer is the Details grid's Update row.
        ContainsText(page, ModPagePane.FactKeyPrefix + "update");
        ContainsText(page, "1.0.0 → 1.4.0");
        // MODSPAGE1: a pending install has not loaded yet, so its action cancels the install rather
        // than uninstalling a loaded mod.
        ContainsText(page, ModPageCopy.CancelInstall);

        Assert.True(UiCellRowHandle.TryInvoke(ModPagePane.FactKeyPrefix + "update/" + ModsMenu.CellOpenInInstall, out _));
        Assert.Equal(ModsTab.InstallMods, ModsMenu.SelectedTabForTests);
        Assert.Equal(
            ModsWorkspaceIdentity.Catalog("ExampleTeam-PhialStore"),
            ModsMenu.TabStateForTests!.InstallMods.Selection);

        // Not newer: no offer.
        ModsMenu.InstallLinkProbe = _ => new ModsMenu.ModsInstallLink("ExampleTeam-PhialStore", "1.0.0");
        ModsMenu.SelectTab("mods");
        ModsMenu.OpenMod("example.phialstore");
        Assert.DoesNotContain(
            ModPagePane.FactKeyPrefix + "update",
            JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")),
            StringComparison.Ordinal);
    }

    /// <summary>Widget readback is JSON, which escapes the muted line's middle dot and arrow.</summary>
    private static void ContainsText(string json, string text) =>
        Assert.Contains(JsonSerializer.Serialize(text).Trim('"'), json, StringComparison.Ordinal);

    private static void DoesNotContainText(string json, string text) =>
        Assert.DoesNotContain(JsonSerializer.Serialize(text).Trim('"'), json, StringComparison.Ordinal);

    private static string Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
