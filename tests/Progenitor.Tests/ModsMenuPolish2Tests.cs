using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Job POLISH2: the four Mods/Install polish items from the 2026-09-13 live check —
/// <list type="number">
/// <item>the Install tab's default sort is <b>Recently released</b> (user: "change the default sort
/// from Downloads to recent");</item>
/// <item>the Mods sidebar reads a promoted local-server copy as <c>installed</c>, not
/// <c>pendload</c>;</item>
/// <item>an uninstall prompt names the restart it actually owes (covered where POLISH1's prompt
/// tests live, <c>InstallTabPolishTests</c>);</item>
/// <item>a server-list claim for a plugin the install ledger already names is folded into the ledger
/// row instead of drawing the same plugin twice.</item>
/// </list>
///
/// <para>Game-free: the fake UI runtime, a synthetic install ledger and <c>BuildSidebar</c> called
/// directly. Nothing here touches the game, the network or a real BepInEx root.</para>
/// </summary>
[Collection("PluginLogger")]
public sealed class ModsMenuPolish2Tests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _game;

    public ModsMenuPolish2Tests()
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
        ModsMenu.UninstallSeam = null;
        _game = Path.Combine(Path.GetTempPath(), "progenitor-polish2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_game, "BepInEx"));
    }

    public void Dispose()
    {
        UiLayoutBatch.ResetForTests();
        ModsMenu.Close();
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.InstallLinkProbe = null;
        ModsMenu.InstalledRemover = null;
        ModsMenu.UninstallSeam = null;
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

    // ---- Item 1: the default sort ----------------------------------------------------------

    /// <summary>
    /// The default is Recently released everywhere it is decided: the filter state a fresh pane
    /// carries, the cycle the toolbar selector walks, and the fallback an unparsable readback key
    /// gets. <c>Clear filters</c> still keeps whatever sort the player chose — the sort is not a
    /// filter.
    /// What this would catch: leaving the field's initializer on Downloads, a cycle that does not
    /// start at the default (so one click from a fresh tab would not reach Downloads and three clicks
    /// would not come home), or a parse fallback that disagrees with the initializer.
    /// </summary>
    [Fact]
    public void Recently_released_is_the_default_the_cycle_start_and_the_parse_fallback()
    {
        Assert.Equal(InstallSort.Released, new InstallSearchFilters().Sort);

        // INSTALLCOLS1: the header is the one sort control; the default sorts newest first (▼).
        Assert.False(new InstallSearchFilters().SortReversed);
        Assert.Equal("desc", InstallColumns.DirectionName(InstallSort.Released, reversed: false));

        Assert.False(InstallModsPane.TryParseSortKey("no-such-key", out var fallback));
        Assert.Equal(InstallSort.Released, fallback);
        Assert.True(InstallModsPane.TryParseSortKey(InstallModsPane.SortKeyDownloads, out var downloads));
        Assert.Equal(InstallSort.Downloads, downloads);
        Assert.Equal(InstallModsPane.SortKeyReleased, InstallModsPane.SortKeyOf(new InstallSearchFilters().Sort));

        // Clear filters is unchanged: it resets the chips and keeps the sort.
        var filters = new InstallSearchFilters { Sort = InstallSort.Name, CurrentUpdateOnly = false };
        filters.Reset();
        Assert.Equal(InstallSort.Name, filters.Sort);
        Assert.True(filters.CurrentUpdateOnly);
        // Revert: a Downloads initializer, a cycle starting elsewhere, or a Downloads parse fallback
        // fails one of these.
    }

    // ---- Item 2: the sidebar's state -------------------------------------------------------

    /// <summary>
    /// The sidebar's state comes from the install-ledger rows themselves, in
    /// <see cref="InstalledState"/>'s own order. The live defect (client gen 2, 2026-09-13): ECF and
    /// AutoLatchGates were installed to the local server, it restarted and promoted both, the Install
    /// tab read them <c>installed</c> with <c>halves.server: loaded</c> — and the sidebar still said
    /// <c>pendload</c> for both, because it only counted the rows.
    /// What this would catch: reading the state as "ledgered and not loaded here" (the live defect),
    /// calling a copy still in <c>progenitor-pending/</c> installed, taking a <c>pending</c>-category
    /// server row as promoted, or letting the uninstall marker lose to either.
    /// </summary>
    [Fact]
    public void A_promoted_server_copy_is_installed_in_the_sidebar_state_rule()
    {
        var promotedServer = new[] { Ledgered(ServerInstallLedger.CategoryMod, InstalledState.ServerRoot, InstalledState.ReloadFolder) };
        Assert.Equal(
            ModPageState.Installed,
            ModPageStates.DecideFromLedger(uninstallPending: false, promotedServer, loadedHere: false));

        // Not promoted yet: the copy is in the local server's own pending folder.
        var pendingServer = new[] { Ledgered(ServerInstallLedger.CategoryPending, InstalledState.ServerRoot, InstalledState.PendingFolder) };
        Assert.Equal(
            ModPageState.PendingLoad,
            ModPageStates.DecideFromLedger(uninstallPending: false, pendingServer, loadedHere: false));

        // A reload copy on the server root that no `mod` row names is not evidence of a promote.
        var unreconciledServer = new[] { Ledgered(ServerInstallLedger.CategoryPending, InstalledState.ServerRoot, InstalledState.ReloadFolder) };
        Assert.Equal(
            ModPageState.PendingLoad,
            ModPageStates.DecideFromLedger(uninstallPending: false, unreconciledServer, loadedHere: false));

        // This root's own promoted copy that this process has not loaded stays pending, exactly as
        // InstalledState.DetectFromLedger reports it.
        var promotedHere = new[] { Ledgered(ServerInstallLedger.CategoryMod, InstalledState.ClientRoot, InstalledState.ReloadFolder) };
        Assert.Equal(
            ModPageState.PendingLoad,
            ModPageStates.DecideFromLedger(uninstallPending: false, promotedHere, loadedHere: false));
        Assert.Equal(
            ModPageState.Installed,
            ModPageStates.DecideFromLedger(uninstallPending: false, promotedHere, loadedHere: true));

        // The marker still wins over every one of them, and an unledgered loaded plugin is installed.
        Assert.Equal(
            ModPageState.PendingUninstall,
            ModPageStates.DecideFromLedger(uninstallPending: true, promotedServer, loadedHere: false));
        Assert.Equal(
            ModPageState.Installed,
            ModPageStates.DecideFromLedger(uninstallPending: false, Array.Empty<InstalledLedgerRow>(), loadedHere: false));

        // A row the old text ledger wrote has no path to judge by and keeps the previous answer.
        var pathless = new[] { new InstalledLedgerRow("dyno-ExampleCommandFramework", "sha", "0.10.4", "ExampleCommandFramework.dll", "package:dyno-ExampleCommandFramework@0.10.4", ServerInstallLedger.CategoryMod, InstalledState.ServerRoot, "") };
        Assert.Equal(
            ModPageState.PendingLoad,
            ModPageStates.DecideFromLedger(uninstallPending: false, pathless, loadedHere: false));
        // Revert: dropping the folder/root/category triple from IsPromotedServerLedgerRow makes the
        // second or third case read installed; restoring the row-count rule brings the defect back.
    }

    /// <summary>
    /// The same two installs through the readback the live check used: <c>entries[].state</c> for both
    /// rows, and the page's <c>Status:</c> word, which is decided from the same rows.
    /// What this would catch: fixing the marker's state without the page's word (or the other way
    /// round), so the sidebar and the page disagree about one install.
    /// </summary>
    [Fact]
    public void The_sidebar_readback_reads_both_promoted_server_installs_installed()
    {
        ModsMenu.LedgerForTests = new[]
        {
            Ledgered(ServerInstallLedger.CategoryMod, InstalledState.ServerRoot, InstalledState.ReloadFolder,
                package: "dyno-ExampleCommandFramework", file: "ExampleCommandFramework.dll", version: "0.10.4"),
            Ledgered(ServerInstallLedger.CategoryMod, InstalledState.ServerRoot, InstalledState.ReloadFolder,
                package: "Zenlab-AutoLatchGates", file: "AutoLatchGates.dll", version: "1.0.1"),
        };
        ModsMenu.Open();

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace()));
        Assert.Equal("installed", Entry(doc.RootElement, "ExampleCommandFramework").GetProperty("state").GetString());
        Assert.Equal("installed", Entry(doc.RootElement, "AutoLatchGates").GetProperty("state").GetString());
        Assert.Equal("progenitor", Entry(doc.RootElement, "AutoLatchGates").GetProperty("provenance").GetString());
        // Revert: SidebarStateOf counting rows instead of reading them reads `pendload` here.
    }

    // ---- Item 4: one plugin, one row -------------------------------------------------------

    /// <summary>
    /// The live sidebar drew ExampleCommandFramework twice: a Progenitor row from the local-server
    /// install ledger (identified by the file it wrote, <c>ExampleCommandFramework.dll</c>) and a
    /// second row <c>gg.dyno.ExampleCommandFramework</c> under <c>On this server (1)</c> from the
    /// joined server's own claim. The claim is folded into the ledger row — the row that can be
    /// uninstalled — which gains the claim's requirement text, and the server group counts only the
    /// claims no install of ours explains.
    /// What this would catch: the duplicate row itself, a fold that drops the ledger row instead of
    /// the claim (losing Remove), a fold that leaves the server count at 1, or a match loose enough to
    /// swallow an unrelated claim.
    /// </summary>
    /// <summary>
    /// F3 (live 2026-10-07): not connected, an install only the local server folder's ledger names is that server's: it is
    /// listed in the server group (captioned for the local server), not among this client's installed mods.
    /// </summary>
    [Fact]
    public void A_local_server_only_install_is_listed_in_the_server_group_when_no_server_claims_it()
    {
        var ledger = new[]
        {
            Ledgered(ServerInstallLedger.CategoryMod, InstalledState.ServerRoot, InstalledState.ReloadFolder,
                package: "dyno-ExampleCommandFramework", file: "ExampleCommandFramework.dll", version: "0.10.4"),
        };

        var model = ModMenuInventory.BuildSidebar(new List<KeyValuePair<string, string>>(), Array.Empty<ModMenuInventoryRow>(), ledger);
        var row = Assert.IsType<ModsSidebarRow>(model.Find("ExampleCommandFramework"));
        Assert.True(row.LocalServerOnly);
        Assert.Equal(ModMenuInventory.GroupServer, row.Group);
        Assert.True(row.Removable);
        Assert.DoesNotContain(model.VisibleModRows, r => r.Id == row.Id);
        Assert.Contains(model.ServerRows, r => r.Id == row.Id);
        Assert.Equal(1, model.Server);
        Assert.Equal(0, model.Shown);
    }

    [Fact]
    public void A_server_claim_for_a_ledgered_plugin_folds_into_the_ledger_row()
    {
        var entries = new List<KeyValuePair<string, string>>
        {
            new("gg.dyno.ExampleCommandFramework", "gg.dyno.ExampleCommandFramework"),
            new("other.server.only", "SomethingElse"),
        };
        var inventory = new[]
        {
            Claim("gg.dyno.ExampleCommandFramework", "gg.dyno.ExampleCommandFramework", "Required on this server"),
            Claim("other.server.only", "SomethingElse", "Optional on this server"),
        };
        var ledger = new[]
        {
            Ledgered(ServerInstallLedger.CategoryMod, InstalledState.ServerRoot, InstalledState.ReloadFolder,
                package: "dyno-ExampleCommandFramework", file: "ExampleCommandFramework.dll", version: "0.10.4"),
        };

        var model = ModMenuInventory.BuildSidebar(entries, inventory, ledger);

        // One row for the plugin, in the player's mods, and it is the ledger row.
        Assert.Single(model.Rows.Where(r => r.Id.IndexOf("ExampleCommandFramework", StringComparison.OrdinalIgnoreCase) >= 0));
        var folded = Assert.IsType<ModsSidebarRow>(model.Find("ExampleCommandFramework"));
        Assert.Equal(ModProvenance.Progenitor, folded.Provenance);
        Assert.Equal(ModMenuInventory.GroupMods, folded.Group);
        Assert.Equal("0.10.4", folded.Version);
        // The claim's requirement text came with it.
        Assert.Equal("Required on this server", folded.Inventory?.Requirement);
        Assert.Null(model.Find("gg.dyno.ExampleCommandFramework"));

        // Only the unmatched claim is left in the server group, and only it is counted.
        Assert.Equal(1, model.Server);
        Assert.Equal(
            new[] { "other.server.only" },
            model.Rows.Where(r => r.Provenance == ModProvenance.Server).Select(r => r.Id).ToArray());

        // The match rule: the ledger's own comparison first, then the claim's last dotted segment
        // against the file the ledger wrote or the package's name half. A bare id and a short segment
        // are never matched this way.
        Assert.True(ModMenuInventory.ClaimNamesLedgeredPlugin(ledger, "gg.dyno.ExampleCommandFramework"));
        Assert.True(ModMenuInventory.ClaimNamesLedgeredPlugin(ledger, "anyone.else.ExampleCommandFramework"));
        Assert.False(ModMenuInventory.ClaimNamesLedgeredPlugin(ledger, "gg.dyno.SomethingElse"));
        // A bare id equal to the file the ledger wrote is the ledger's own rule, not the new one.
        Assert.True(ModMenuInventory.ClaimNamesLedgeredPlugin(ledger, "ExampleCommandFramework"));
        Assert.False(ModMenuInventory.ClaimNamesLedgeredPlugin(ledger, "SomethingElse"));
        Assert.False(ModMenuInventory.ClaimNamesLedgeredPlugin(ledger, "a.b"));
        Assert.False(ModMenuInventory.ClaimNamesLedgeredPlugin(Array.Empty<InstalledLedgerRow>(), "gg.dyno.ExampleCommandFramework"));
        // Revert: dropping the fold gives two rows and `On this server (2)`; matching on the whole
        // GUID only puts the duplicate back; matching any shared segment folds SomethingElse in.
    }

    /// <summary>
    /// A claim nothing installed is still its own row, and a claim already carried by a loaded,
    /// ledgered plugin row is untouched: the fold only ever removes a server row a ledger-only row
    /// explains.
    /// What this would catch: a fold that eats every server claim, or one that runs when there is no
    /// ledger-only row to fold into.
    /// </summary>
    [Fact]
    public void A_claim_no_install_explains_keeps_its_own_row()
    {
        var entries = new List<KeyValuePair<string, string>> { new("gg.dyno.ExampleCommandFramework", "ECF") };
        var inventory = new[] { Claim("gg.dyno.ExampleCommandFramework", "ECF", "Required on this server") };

        var model = ModMenuInventory.BuildSidebar(entries, inventory, Array.Empty<InstalledLedgerRow>());
        var row = Assert.IsType<ModsSidebarRow>(model.Find("gg.dyno.ExampleCommandFramework"));
        Assert.Equal(ModProvenance.Server, row.Provenance);
        Assert.Equal(1, model.Server);
        Assert.Equal(0, model.Shown);
    }

    // ---- fixtures --------------------------------------------------------------------------

    /// <summary>One ledger row, with the folder its file sits in — which is what the state rule reads.</summary>
    private InstalledLedgerRow Ledgered(
        string category,
        string root,
        string folder,
        string package = "ExampleTeam-PhialStore",
        string file = "PhialStore.dll",
        string version = "1.0.0")
    {
        string path = Path.Combine(_game, root, "BepInEx", folder, file);
        return new InstalledLedgerRow(
            package, "sha", version, file, "package:" + package + "@" + version, category, root, path);
    }

    private static ModMenuInventoryRow Claim(string id, string name, string requirement) => new()
    {
        Id = id,
        DisplayName = name,
        Location = "Server list",
        Version = "",
        Status = "Listed by server",
        Requirement = requirement,
    };

    private static JsonElement Entry(JsonElement workspace, string id)
    {
        foreach (var entry in workspace.GetProperty("entries").EnumerateArray())
        {
            if (string.Equals(entry.GetProperty("id").GetString(), id, StringComparison.OrdinalIgnoreCase))
                return entry;
        }

        throw new Xunit.Sdk.XunitException("no sidebar entry for " + id);
    }
    /// <summary>
    /// F3 note (live 2026-10-08): the list counts each row by the group it is listed in, so local-server-only rows build the
    /// "On your local server" header (the readback listed them in that group, but no header or rows were drawn).
    /// Source-checked; not live-verified.
    /// </summary>
    [Fact]
    public void The_server_group_is_counted_by_the_group_its_rows_are_listed_in()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        Assert.Contains("if (row.ListProvenance == ModProvenance.Server) { serverCount++; continue; }", src, StringComparison.Ordinal);
        Assert.DoesNotContain("if (row.Provenance == ModProvenance.Server) { serverCount++; continue; }", src, StringComparison.Ordinal);
    }
}
