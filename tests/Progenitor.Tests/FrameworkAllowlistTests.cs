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
/// FWALLOW1 (user, 2026-10-06, Mods → "Server-led sample": "I can't uninstall it, I think because
/// it is test and registered as required."): only Progenitor's real framework plugins — Core
/// (Bootstrap shares its GUID), the dev bridge and the preloader — are required. Every other id
/// under <c>progenitor</c> (samples, examples, apivalidation, test plugins) is an ordinary
/// mod: listed under Installed, with the switch, and with an Uninstall that deletes its DLL from
/// <c>BepInEx/progenitor-reload</c> even when no install ledger lists it.
///
/// <para>Game-free: the fake UI runtime, a temporary reload folder with placeholder DLL files whose
/// plugin GUIDs come from a seam, and a fake pause backend. Not live-verified.</para>
/// </summary>
[Collection("PluginLogger")]
public sealed class FrameworkAllowlistTests : IDisposable
{
    private const string Companion = "progenitor.samplecompanion";
    private const string ServerLed = "progenitor.sampleserverled";
    private const string Sample = "progenitor.sample";
    private const string ApiValidation = "progenitor.apivalidation";
    private const string TestPlugin = "progenitor.test.exercise";

    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly string _reload;
    private readonly object _owner = new();
    private readonly Dictionary<string, string[]> _guids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Backend _backend = new();

    public FrameworkAllowlistTests()
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
        ModsMenu.SetInstallCatalogForTests(null);
        ModRegistry.ResetForTests();
        ModSwitch.ResetForTests();
        ModEnableState.ResetForTests();
        ModsMenu.ResetStage3ForTests();
        ReloadFolderPlugins.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-fwallow-" + Guid.NewGuid().ToString("N"));
        _reload = Path.Combine(_dir, "BepInEx", "progenitor-reload");
        Directory.CreateDirectory(_reload);
        ModEnableState.TestDirectory = _dir;
        ModSwitch.BackendOverride = () => _backend;
        ModsMenu.ReloadFolderProbe = () => _reload;
        ModsMenu.ReloadGuidsProbe = GuidsOf;
    }

    public void Dispose()
    {
        ModsMenu.Close();
        ModsMenu.ResetStage3ForTests();
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.ResetTabBindingsForTests();
        ModsWorkspaceFrame.ResetForTests();
        ModsMenu.UninstallLifecycleWatch();
        Handshake.UninstallClientServerContextWatch();
        ModMenuInventory.ResetForTests();
        Handshake.ResetTables();
        WorldEpoch.ResetForTests();
        ModRegistry.ResetForTests();
        ModSwitch.ResetForTests();
        ModEnableState.TestDirectory = null;
        ModEnableState.ResetForTests();
        ReloadFolderPlugins.ResetForTests();
        ModsMenu.ReloadFolderProbe = null;
        ModsMenu.ReloadGuidsProbe = null;
        Plugin.Logger = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private IReadOnlyList<string> GuidsOf(string path) =>
        _guids.TryGetValue(Path.GetFileName(path), out var g) ? g : Array.Empty<string>();

    /// <summary>A placeholder DLL in the reload folder that "declares" <paramref name="guids"/>.</summary>
    private string Dll(string fileName, params string[] guids)
    {
        string path = Path.Combine(_reload, fileName);
        File.WriteAllText(path, "placeholder " + fileName);
        _guids[fileName] = guids;
        return path;
    }

    private static KeyValuePair<string, string> Entry(string id, string name) => new(id, name);

    // ---- the rule ---------------------------------------------------------------------------------

    /// <summary>
    /// The framework test is an exact allowlist. What this would catch: the namespace prefix coming
    /// back (every sample, example, apivalidation and test plugin is required again, with no switch
    /// and no Uninstall), or the allowlist losing the dev bridge or the preloader.
    /// </summary>
    [Fact]
    public void Only_Core_the_dev_bridge_and_the_preloader_are_framework_ids()
    {
        Assert.True(ModMenuInventory.IsFrameworkId("progenitor"));
        Assert.True(ModMenuInventory.IsFrameworkId("PROGENITOR"));
        Assert.True(ModMenuInventory.IsFrameworkId(" progenitor.dev "));
        Assert.True(ModMenuInventory.IsFrameworkId("progenitor.preloader"));

        foreach (var id in new[]
                 {
                     Sample, "progenitor.sample.alpha", ServerLed, Companion, ApiValidation, TestPlugin,
                     "progenitor.hotdemo", "progenitor.reloadexample", "progenitor.devtools",
                     "progenitor.bootstrap", "progenitorx", "", null,
                 })
            Assert.False(ModMenuInventory.IsFrameworkId(id), id ?? "(null)");

        // Shipped examples: `.sample*` and anything containing `example`; never a framework id.
        Assert.True(ModMenuInventory.IsShippedExampleId(Companion));
        Assert.True(ModMenuInventory.IsShippedExampleId(ServerLed));
        Assert.True(ModMenuInventory.IsShippedExampleId("progenitor.sample.alpha"));
        Assert.True(ModMenuInventory.IsShippedExampleId("progenitor.reloadexample"));
        Assert.False(ModMenuInventory.IsShippedExampleId(ApiValidation));
        Assert.False(ModMenuInventory.IsShippedExampleId("progenitor"));
    }

    /// <summary>
    /// The lists that decide "framework" elsewhere name the same plugins. What this would catch: the
    /// Mods list calling a plugin required that the switch would let you turn off (or the reverse),
    /// or the handshake allowing a different set than the menu protects. The handshake list is not
    /// read by the menu, and the menu's is not read by the handshake.
    /// </summary>
    [Fact]
    public void The_framework_allowlist_agrees_with_the_switch_and_handshake_lists()
    {
        var menu = ModMenuInventory.FrameworkIds.Select(i => i.ToLowerInvariant()).OrderBy(i => i).ToArray();
        var switchList = ModEnableState.RequiredIds.Where(i => i != "bepinex").Select(i => i.ToLowerInvariant()).OrderBy(i => i).ToArray();
        var handshake = ClientModJudge.ProgenitorIds.Select(i => i.ToLowerInvariant()).OrderBy(i => i).ToArray();
        Assert.Equal(switchList, menu);
        Assert.Equal(handshake, menu);
        Assert.Equal(ModMenuInventory.CoreId, ModMenuInventory.FrameworkIds[0]);
    }

    /// <summary>
    /// SRVUNINST1: the Install tab asks the same question as the Mods page. A reload-folder copy no
    /// ledger lists is offered as a reload removal there too; the framework's own DLL never is.
    /// What this would catch: the Install tab's "Managed elsewhere" for a mod the Mods page can remove.
    /// </summary>
    [Fact]
    public void The_install_tab_offers_the_same_reload_folder_removal()
    {
        Dll("Progenitor.HotDemo.dll", "progenitor.hotdemo");
        Dll("Progenitor.Dev.dll", ModMenuInventory.DevId);
        var offer = ModsMenu.PlainOfferOf("progenitor.hotdemo");
        Assert.NotNull(offer);
        Assert.True(offer!.Reload);
        Assert.True(offer.CanUninstall);
        Assert.False(ModsMenu.PlainOfferOf(ModMenuInventory.DevId)?.Reload ?? false);
    }

    /// <summary>
    /// Review (SRVUNINST1 B): the notice's advice checks for a copy before the switch's entry, which an uninstall
    /// does not always clear. Turned off, then uninstalled, reads "not installed", never "turn it back on".
    /// </summary>
    [Fact]
    public void A_turned_off_then_uninstalled_mod_reads_not_installed()
    {
        Assert.True(ModEnableState.Set("progenitor.hotdemo", "HotDemo", enabled: false, heldWith: null, DateTime.UtcNow, out _));
        Assert.Equal(RecheckLocalState.NotInstalled, ModsMenu.RecheckLocalStateOf("progenitor.hotdemo"));
        Dll("Progenitor.HotDemo.dll", "progenitor.hotdemo");
        Assert.NotEqual(RecheckLocalState.NotInstalled, ModsMenu.RecheckLocalStateOf("progenitor.hotdemo"));
        // A package id is not judged.
        Assert.Equal(RecheckLocalState.Unknown, ModsMenu.RecheckLocalStateOf("Example-HotDemo"));
    }

    // ---- classification in the Mods list ----------------------------------------------------------

    /// <summary>
    /// Samples, apivalidation and test plugins whose DLL sits in the reload folder are Installed rows
    /// with Uninstall and an accurate provenance line; Core and the dev bridge stay required, even if a
    /// file in the reload folder names them. What this would catch: a sample in <c>Required by
    /// Progenitor</c>, the "Part of Progenitor" note on a sample, or Core becoming removable.
    /// </summary>
    [Fact]
    public void Reload_folder_samples_are_installed_removable_rows_and_the_framework_stays_required()
    {
        var entries = new List<KeyValuePair<string, string>>
        {
            Entry(ModMenuInventory.CoreId, "Progenitor"),
            Entry(ModMenuInventory.DevId, "Progenitor Dev Bridge"),
            Entry(Companion, "Companion sample"),
            Entry(ServerLed, "Server-led sample"),
            Entry(ApiValidation, "API validation"),
            Entry(TestPlugin, "Exercise"),
            Entry("progenitor.hotdemo", "HotDemo"),
        };
        var reload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Companion] = Path.Combine(_reload, "Progenitor.SampleCompanion.dll"),
            [ServerLed] = Path.Combine(_reload, "Progenitor.SampleServerLed.dll"),
            [ApiValidation] = Path.Combine(_reload, "Progenitor.ApiValidation.dll"),
            [TestPlugin] = Path.Combine(_reload, "Progenitor.Exercise.dll"),
            // A dev bridge copied into the reload folder is still the dev bridge.
            [ModMenuInventory.DevId] = Path.Combine(_reload, "Progenitor.Dev.dll"),
        };
        var ledger = new[]
        {
            new InstalledLedgerRow("progenitor.hotdemo", "sha", "1.0.0", "progenitor.hotdemo.dll",
                "package:Progenitor-HotDemo@1.0.0", "mod", InstalledState.ClientRoot, ""),
        };

        var model = ModMenuInventory.BuildSidebar(entries, Array.Empty<ModMenuInventoryRow>(), ledger, "6.0.0", null, reload);

        foreach (var id in new[] { Companion, ServerLed, ApiValidation, TestPlugin })
        {
            var row = model.Find(id)!;
            Assert.Equal(ModProvenance.Reload, row.Provenance);
            Assert.Equal("reload", row.ProvenanceName);
            Assert.Equal(ModMenuInventory.GroupMods, row.Group);
            Assert.True(row.Removable, id);
            Assert.False(row.IsFramework, id);
            Assert.Equal(reload[id], row.ReloadFile);
            Assert.NotEqual(ModMenuInventory.RequiredNote, row.PageNote);
            Assert.DoesNotContain("Part of Progenitor", row.PageNote ?? "", StringComparison.Ordinal);
        }

        Assert.Equal(ModMenuInventory.ExampleNote, model.Find(Companion)!.PageNote);
        Assert.Equal(ModMenuInventory.ExampleNote, model.Find(ServerLed)!.PageNote);
        Assert.Equal(ModMenuInventory.ReloadFolderNote, model.Find(ApiValidation)!.PageNote);
        Assert.Equal(ModMenuInventory.ReloadFolderNote, model.Find(TestPlugin)!.PageNote);

        // A ledgered sample-namespace mod keeps its ledger provenance (MODSPOLISH1) and is removable.
        Assert.Equal(ModProvenance.Progenitor, model.Find("progenitor.hotdemo")!.Provenance);
        Assert.True(model.Find("progenitor.hotdemo")!.Removable);

        // The framework: required, read-only, the reload copy of the dev bridge included.
        foreach (var id in new[] { ModMenuInventory.CoreId, ModMenuInventory.DevId, ModMenuInventory.LoaderId })
        {
            var row = model.Find(id)!;
            Assert.Equal(ModProvenance.Required, row.Provenance);
            Assert.True(row.IsFramework, id);
            Assert.False(row.Removable, id);
            Assert.Equal("", row.ReloadFile);
        }

        Assert.Equal(new[] { ModMenuInventory.LoaderId, ModMenuInventory.CoreId, ModMenuInventory.DevId },
            model.RequiredRows.Select(r => r.Id).ToArray());
        Assert.Equal(5, model.Shown);
    }

    // ---- the reload folder ------------------------------------------------------------------------

    /// <summary>
    /// The map is the reloader's own view: top-level DLLs only, and a GUID two files declare is left
    /// out. Removal deletes exactly the one file and refuses a framework id, a changed file and an
    /// ambiguous one. What this would catch: deleting a file in a subfolder or outside the reload
    /// folder, deleting both copies of a duplicated plugin, or deleting the dev bridge.
    /// </summary>
    [Fact]
    public void Removal_deletes_only_the_one_reload_file_that_declares_the_plugin()
    {
        string companion = Dll("Progenitor.SampleCompanion.dll", Companion);
        string other = Dll("Progenitor.ApiValidation.dll", ApiValidation);
        string dev = Dll("Progenitor.Dev.dll", ModMenuInventory.DevId);
        Dll("DupA.dll", "dup.plugin");
        Dll("DupB.dll", "dup.plugin");
        Directory.CreateDirectory(Path.Combine(_reload, "nested"));
        string nested = Path.Combine(_reload, "nested", "Nested.dll");
        File.WriteAllText(nested, "nested");
        _guids["Nested.dll"] = new[] { "nested.plugin" };

        var map = ReloadFolderPlugins.Map(_reload, GuidsOf);
        Assert.Equal(Path.GetFullPath(companion), map[Companion]);
        Assert.False(map.ContainsKey("dup.plugin"), "an ambiguous GUID is not offered");
        Assert.False(map.ContainsKey("nested.plugin"), "the reloader reads top-level DLLs only");

        // The framework is never removed, whatever file holds it.
        var refusedDev = ReloadFolderPlugins.Remove(_reload, ModMenuInventory.DevId, null, GuidsOf);
        Assert.False(refusedDev.Ok);
        Assert.Equal(ReloadFolderPlugins.WhyFramework, refusedDev.Message);
        Assert.True(File.Exists(dev));

        Assert.Equal(ReloadFolderPlugins.WhyAmbiguous, ReloadFolderPlugins.Remove(_reload, "dup.plugin", null, GuidsOf).Message);
        Assert.Equal(ReloadFolderPlugins.WhyNoFile, ReloadFolderPlugins.Remove(_reload, "nested.plugin", null, GuidsOf).Message);
        Assert.True(File.Exists(nested));

        // The page's file must still be the file the folder holds.
        var changed = ReloadFolderPlugins.Remove(_reload, Companion, Path.Combine(_dir, "elsewhere", "Progenitor.SampleCompanion.dll"), GuidsOf);
        Assert.False(changed.Ok);
        Assert.Equal(ReloadFolderPlugins.WhyChanged, changed.Message);
        Assert.True(File.Exists(companion));

        var removed = ReloadFolderPlugins.Remove(_reload, Companion, companion, GuidsOf);
        Assert.True(removed.Ok, removed.Message);
        Assert.True(removed.HotUnloaded);
        Assert.Equal(new[] { Path.GetFullPath(companion) }, removed.Removed);
        Assert.False(File.Exists(companion));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(dev));

        // Confinement on its own: inside the folder, a DLL, not nested.
        Assert.True(ReloadFolderPlugins.IsConfined(_reload, other));
        Assert.False(ReloadFolderPlugins.IsConfined(_reload, nested));
        Assert.False(ReloadFolderPlugins.IsConfined(_reload, Path.Combine(_reload, "..", "x.dll")));
        Assert.False(ReloadFolderPlugins.IsConfined(_reload, Path.Combine(_reload, "notes.txt")));
    }

    // ---- the page ---------------------------------------------------------------------------------

    /// <summary>
    /// The user's case, end to end in the menu: a sample nothing ledgered shows its provenance line,
    /// a live switch and Uninstall; the prompt says it was installed outside the Install tab and names
    /// the file; Yes deletes that file only. Core's page offers no action and refuses the switch.
    /// What this would catch: a sample page still saying "Part of Progenitor", no Uninstall for a
    /// non-ledgered file, the ledger remover being asked instead of the reload folder, or a deleted
    /// neighbor.
    /// </summary>
    [Fact]
    public void A_sample_page_offers_the_switch_and_Uninstall_and_removal_deletes_its_file()
    {
        string companion = Dll("Progenitor.SampleCompanion.dll", Companion);
        string serverLed = Dll("Progenitor.SampleServerLed.dll", ServerLed);
        _backend.Hot.Add(Companion);
        ModRegistry.NoteLoading(Companion, "Companion sample", "1.0.0", Array.Empty<string>(), null);
        ModRegistry.NoteLoaded(Companion);
        int ledgerRemovals = 0;
        ModsMenu.InstalledRemover = _ => { ledgerRemovals++; return ServerInstallResult.Refuse("ledger"); };
        using var reg = ModsMenu.Register(_owner, Companion, "Companion sample");
        using var regCore = ModsMenu.Register(_owner, ModMenuInventory.DevId, "Progenitor Dev Bridge");
        ModsMenu.Open();

        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace())))
        {
            var sample = EntryOf(doc.RootElement, Companion);
            Assert.Equal("reload", sample.GetProperty("provenance").GetString());
            Assert.Equal("mods", sample.GetProperty("group").GetString());
            Assert.False(sample.GetProperty("framework").GetBoolean());
            Assert.True(sample.GetProperty("removable").GetBoolean());
            Assert.Equal(ModMenuInventory.ExampleNote, sample.GetProperty("provenanceLine").GetString());
            Assert.Equal(Path.GetFullPath(companion), sample.GetProperty("reloadFile").GetString());

            var core = EntryOf(doc.RootElement, ModMenuInventory.CoreId);
            Assert.True(core.GetProperty("framework").GetBoolean());
            Assert.False(core.GetProperty("removable").GetBoolean());
            Assert.Equal("required", core.GetProperty("group").GetString());
            var dev = EntryOf(doc.RootElement, ModMenuInventory.DevId);
            Assert.True(dev.GetProperty("framework").GetBoolean());
            Assert.False(dev.GetProperty("removable").GetBoolean());
        }

        ModsMenu.OpenMod(Companion);
        Assert.Equal(ModOwner.Progenitor, ModsMenu.OwnerOf(Companion, null));
        using (var page = Widgets())
        {
            string text = page.RootElement.GetRawText();
            Assert.Contains(Escaped(ModMenuInventory.ExampleNote), text, StringComparison.Ordinal);
            Assert.DoesNotContain(Escaped(ModMenuInventory.RequiredNote), text, StringComparison.Ordinal);
            var framework = Extra(page, ModPagePane.RowFramework)!.Value;
            Assert.True(framework.GetProperty("switch").GetProperty("visible").GetBoolean());
            Assert.True(framework.GetProperty("switch").GetProperty("interactive").GetBoolean());
            Assert.Equal("live", framework.GetProperty("switch").GetProperty("mode").GetString());
            Assert.Equal(ModPageCopy.Uninstall,
                Extra(page, ModPagePane.RowAction)!.Value.GetProperty("label").GetString());
        }

        ModsMenu.ClickVisibleWidget("content", ModPageCopy.Uninstall, null, null);
        using (var page = Widgets())
        {
            string text = page.RootElement.GetRawText();
            Assert.Contains("Installed outside Progenitor", text, StringComparison.Ordinal);
            Assert.Contains("Progenitor.SampleCompanion.dll", text, StringComparison.Ordinal);
        }
        Assert.True(File.Exists(companion), "nothing is deleted before Yes");

        ModsMenu.ClickVisibleWidget("content", ModPageCopy.UninstallYes, null, null);
        Assert.False(File.Exists(companion));
        Assert.True(File.Exists(serverLed), "only the sample's own file goes");
        Assert.Equal(0, ledgerRemovals);
        Assert.True(ModsMenu.LastReloadRemoval!.Ok);
        Assert.Equal(InstallModsCopy.RemovedPending, ModsMenu.ModsActionNote);

        // Core: no action, and its switch is refused.
        ModsMenu.OpenMod(ModMenuInventory.CoreId);
        using (var page = Widgets())
        {
            Assert.Null(Extra(page, ModPagePane.RowAction));
            var framework = Extra(page, ModPagePane.RowFramework)!.Value;
            Assert.False(framework.GetProperty("switch").GetProperty("interactive").GetBoolean());
            Assert.Equal("refused", framework.GetProperty("switch").GetProperty("mode").GetString());
        }
    }

    private static JsonDocument Widgets() =>
        JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));

    private static JsonElement? Extra(JsonDocument doc, string key)
    {
        foreach (var row in doc.RootElement.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value)) return value;
        return null;
    }

    private static JsonElement EntryOf(JsonElement workspace, string id)
    {
        foreach (var entry in workspace.GetProperty("entries").EnumerateArray())
            if (string.Equals(entry.GetProperty("id").GetString(), id, StringComparison.OrdinalIgnoreCase)) return entry;
        throw new Xunit.Sdk.XunitException("no sidebar entry for " + id);
    }

    private static string Escaped(string text) => JsonSerializer.Serialize(text).Trim('"');

    /// <summary>A stand-in for Bootstrap's pause entry points: the named plugins are hot-loaded.</summary>
    private sealed class Backend : IServerModPauseBackend
    {
        internal readonly List<string> Hot = new();
        public bool Supported => true;
        public string UnsupportedReason => "";
        public string PendingRequest { get; set; } = "";
        public string ResumeReports { get; set; } = "";
        public IReadOnlyList<string> Suspendable() => Hot.ToList();
        public IReadOnlyList<PausedPlugin> Suspended() => Array.Empty<PausedPlugin>();
        public IReadOnlyList<string> Closure(IReadOnlyList<string> guids, out string refusal) { refusal = ""; return guids.ToList(); }
        public IReadOnlyList<string> Suspend(IReadOnlyList<string> guids, IReadOnlyList<string> rowOwners, out string report) { report = ""; return guids.ToList(); }
        public IReadOnlyList<string> Resume(IReadOnlyList<string>? guids, out string report) { report = ""; return guids?.ToList() ?? new List<string>(); }
        public bool? HostsPrivateGame() => false;
        public IReadOnlyList<string> KeepPaused(IReadOnlyList<string> guids) => Array.Empty<string>();
        public string ConnectionIdentity() => "";
    }
}
