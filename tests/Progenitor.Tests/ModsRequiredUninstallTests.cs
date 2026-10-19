using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Bootstrap;
using Progenitor.Config;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.PluginActions;
using Progenitor.Plugins;
using Progenitor.Ui;
using BepInEx.Configuration;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Job MODS2, the two halves MODS1 did not have: the Install tab's loader row, and
/// <c>Uninstall Progenitor from this client</c> — the marker Core writes and the start-time removal
/// Bootstrap performs before Core loads. Game-free: a fake catalog, a temp BepInEx root, and a
/// fake for <c>MoveFileEx</c>.
///
/// <para>The required group itself (contents, order, read-only pages, provenance/group readback)
/// is tested in <c>ModsProvenanceTests</c>, which MODS2 updated rather than replaced.</para>
/// </summary>
public sealed class ModsRequiredUninstallTests : IDisposable
{
    private readonly string _bep;

    public ModsRequiredUninstallTests()
    {
        UiApi.ResetForTests();
        ConfigurationPane.ResetServerInstallStateForTests();
        _bep = Path.Combine(Path.GetTempPath(), "progenitor-mods2-" + Guid.NewGuid().ToString("N"), "BepInEx");
        Directory.CreateDirectory(_bep);
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        ConfigurationPane.ResetServerInstallStateForTests();
        ConfigHelper.UninstallAll();
        ClientUninstall.ClockUtc = null;
        UninstallAtStart.ScheduleDeleteOverride = null;
        UninstallAtStart.ClockUtc = null;
        try { Directory.Delete(Path.GetDirectoryName(_bep)!, recursive: true); }
        catch { /* temp */ }
    }

    // ---- The Install tab's loader row -------------------------------------------------------

    /// <summary>
    /// The BepInExPack listing is the plugin loader: its row reads <c>Installed · loader</c>, is
    /// disabled, carries the note that says Progenitor never writes <c>BepInEx/core</c>, reads back
    /// as <c>loader</c>, and is not installable — a click does nothing and it never counts towards
    /// the pending total. Detection is data-driven: the owner and name Thunderstore publishes.
    /// What this would catch: matching the loader on a display name, letting its row offer Install,
    /// counting it as pending, or dropping the <c>loader</c> readback state back to <c>none</c>.
    /// </summary>
    [Fact]
    public void The_loader_listing_is_installed_and_never_installable()
    {
        Assert.True(LoaderPackages.IsLoaderPackage("BepInEx", "BepInExPack_V_Rising", LoaderPackages.VRisingLoaderFullName));
        Assert.True(LoaderPackages.IsLoaderPackage(null, null, LoaderPackages.VRisingLoaderFullName));
        // Owner + name prefix, so a later pack is covered without a code change.
        Assert.True(LoaderPackages.IsLoaderPackage("bepinex", "BepInExPack_V_Rising_2", "BepInEx-BepInExPack_V_Rising_2"));
        // A mod that merely mentions BepInEx is not the loader.
        Assert.False(LoaderPackages.IsLoaderPackage("dyno", "ExampleCommandFramework", "dyno-ExampleCommandFramework"));
        Assert.False(LoaderPackages.IsLoaderPackage("someone", "BepInExPack", "someone-BepInExPack"));
        // The inspector's evidence for the same fact.
        Assert.True(LoaderPackages.HasCoreLayout(new[] { "core/BepInEx.Core.dll", "winhttp.dll" }));
        Assert.False(LoaderPackages.HasCoreLayout(new[] { "BepInExPack/plugins/Mod.dll" }));

        var catalog = new LoaderCatalog();
        var loader = Listing(LoaderPackages.VRisingLoaderFullName, "BepInExPack V Rising", owner: "BepInEx");
        var mod = Listing("dyno-ExampleCommandFramework", "Example Command Framework", owner: "dyno");
        catalog.Entries.Add(loader);
        catalog.Entries.Add(mod);
        var pane = new InstallModsPane(catalog);

        Assert.True(InstallModsPane.IsLoader(loader));
        Assert.False(InstallModsPane.IsLoader(mod));
        Assert.True(InstallModsPane.IsNotInstallable(loader));
        Assert.False(InstallModsPane.IsNotInstallable(mod));

        var action = pane.ActionFor(loader);
        Assert.Equal(InstallListingState.Loader, action.State);
        Assert.Equal("loader", InstallModsPane.StateName(action.State));
        Assert.Equal(InstallModsCopy.LoaderInstalled, action.Label);
        Assert.Equal("Installed \u00b7 loader", action.Label);
        Assert.False(action.Enabled);
        Assert.False(action.Restart);
        Assert.Equal(InstallModsCopy.LoaderNote, action.Reason);
        // MANAGED1: the loader keeps its own caption and note; it is never labeled managed elsewhere.
        Assert.Equal("", action.Managed);
        Assert.Equal("", InstallModsPane.RowManagedText(action));
        Assert.Equal(
            "The plugin loader is already installed; Progenitor never writes BepInEx/core.",
            InstallModsCopy.LoaderNote);
        // A tool-like row states no restart fact of its own.
        Assert.Equal("", InstallModsPane.HeadNoteText(action));

        var readback = pane.ListingToWidget(loader);
        Assert.Equal("loader", readback["state"]);
        Assert.Equal(InstallModsCopy.LoaderNote, readback["reason"]);

        // Not installable: the click is ignored and the pending total never counts it.
        Assert.False(pane.OnInstallClick(loader.Key));
        Assert.Equal(0, pane.PendingCount());
    }

    // ---- The uninstall marker Core writes ---------------------------------------------------

    /// <summary>
    /// Confirm writes <c>BepInEx/progenitor-uninstall.json</c> naming the ledgered mods, Core,
    /// Bootstrap, the dev bridge, the samples, the caches, the ledger and the marker itself —
    /// nothing else, and nothing at all under BepInEx's own <c>core/</c>. Nothing is deleted while
    /// the game runs. Cancel deletes the request.
    /// What this would catch: deleting files from the running client, listing <c>core/</c> or a
    /// plugin another tool wrote, or a Cancel that leaves the marker on disk.
    /// </summary>
    [Fact]
    public void Confirm_writes_the_marker_and_Cancel_deletes_it_without_touching_a_file()
    {
        string ledgered = Write(Path.Combine(_bep, InstalledState.ReloadFolder, "example.phialstore.dll"), new byte[] { 1 });
        string core = Write(Path.Combine(_bep, "progenitor-core", "Progenitor.dll"), new byte[] { 2 });
        string bootstrap = Write(Path.Combine(_bep, "plugins", "Progenitor.Bootstrap.dll"), new byte[] { 3 });
        string dev = Write(Path.Combine(_bep, "plugins", "Progenitor.Dev.dll"), new byte[] { 4 });
        string cache = Write(Path.Combine(_bep, ListingDiskCache.CacheFolder, "readme.txt"), new byte[] { 5 });
        string foreign = Write(Path.Combine(_bep, "plugins", "GroundPickupGlow.dll"), new byte[] { 9 });
        string loaderCore = Write(Path.Combine(_bep, "core", "BepInEx.Core.dll"), new byte[] { 7 });
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            new ServerInstallLedgerEntry(
                "BepInEx/progenitor-reload/example.phialstore.dll", "sha", 1,
                ServerInstallLedger.CategoryMod, "package:example.phialstore@1.0.0", DateTime.UtcNow),
        }).Write(GameRoot);

        var pane = NewPane();
        ConfigurationPane.ClientRootProbe = () => _bep;

        var files = ConfigurationPane.ClientUninstallFiles();
        Assert.Contains("progenitor-reload/example.phialstore.dll", files);
        Assert.Contains("progenitor-core/Progenitor.dll", files);
        Assert.Contains("plugins/Progenitor.Bootstrap.dll", files);
        Assert.Contains("plugins/Progenitor.Dev.dll", files);
        Assert.Contains(ListingDiskCache.CacheFolder + "/readme.txt", files);
        Assert.Contains(ServerInstallLedger.FileName, files);
        Assert.Contains(ClientUninstall.FileName, files);
        // Another tool's plugin and BepInEx's own core folder are never listed.
        Assert.DoesNotContain("plugins/GroundPickupGlow.dll", files);
        foreach (string listed in files)
            Assert.False(
                listed.StartsWith("core/", StringComparison.OrdinalIgnoreCase),
                "the plan must never name BepInEx/core: " + listed);

        // The prompt names the count, the files and what stays.
        string prompt = pane.ClientUninstallPromptText;
        Assert.Contains(files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " files: ", prompt, StringComparison.Ordinal);
        Assert.Contains("At the next start of V Rising", prompt, StringComparison.Ordinal);
        Assert.Contains("Mods installed by other tools and BepInEx stay.", prompt, StringComparison.Ordinal);
        Assert.Equal("Remove Progenitor from this client?", ConfigurationPane.ClientRemoveHeading);
        Assert.Equal("Remove at next start", ConfigurationPane.ClientRemoveButton);
        Assert.Equal("Uninstall Progenitor from this client", ConfigurationPane.UninstallClientButton);

        // The prompt is a state of its own; opening it writes nothing.
        Assert.False(pane.ClientUninstallPromptOpen);
        pane.RequestClientUninstall();
        Assert.True(pane.ClientUninstallPromptOpen);
        Assert.False(File.Exists(ClientUninstall.PathFor(_bep)));

        ClientUninstall.ClockUtc = () => new DateTime(2026, 9, 13, 10, 11, 12, DateTimeKind.Utc);
        Assert.True(pane.ConfirmClientUninstall().Completed);
        Assert.False(pane.ClientUninstallPromptOpen);
        string marker = ClientUninstall.PathFor(_bep);
        Assert.True(File.Exists(marker));
        // Not one file was removed: this process is running from them.
        foreach (string path in new[] { ledgered, core, bootstrap, dev, cache, foreign, loaderCore })
            Assert.True(File.Exists(path), path);

        using (var doc = JsonDocument.Parse(File.ReadAllBytes(marker)))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("schema").GetInt32());
            Assert.Equal("2026-09-13T10:11:12Z", doc.RootElement.GetProperty("requestedUtc").GetString());
            Assert.Equal(files.Count, doc.RootElement.GetProperty("files").GetArrayLength());
        }

        var readback = pane.ClientUninstallReadback();
        Assert.Equal(true, readback["requested"]);
        Assert.Equal("2026-09-13T10:11:12Z", readback["requestedUtc"]);
        Assert.Equal(files.Count, Assert.IsAssignableFrom<IReadOnlyList<string>>(readback["files"]).Count);

        // Cancel is the delete of the request, and nothing else.
        Assert.True(pane.CancelClientUninstall().Completed);
        Assert.False(File.Exists(marker));
        Assert.Equal(false, pane.ClientUninstallReadback()["requested"]);
        Assert.True(File.Exists(bootstrap));

        // Settings organization stage 4: Progenitor's page draws the action (Your settings · Private games), through the
        // same request.
        string page = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ProgenitorPage.cs");
        SourcePin.AssertUncommented(page, "ConfigurationPane.WriteClientUninstall()");
        SourcePin.AssertUncommented(page, "ConfigurationPane.CancelClientUninstallRequest()");
    }

    // ---- The removal Bootstrap performs at the next start ----------------------------------

    /// <summary>
    /// Bootstrap honors the marker before Core loads: it deletes the listed files that exist,
    /// empties the Progenitor-owned folders, schedules its own DLL for the next reboot, writes the
    /// <c>.done</c> note, removes the request and reports that the caller must stop.
    /// What this would catch: loading Core anyway, deleting a file the marker does not name,
    /// deleting Bootstrap's live DLL instead of scheduling it, or leaving the request on disk so
    /// the removal runs again at every start.
    /// </summary>
    [Fact]
    public void Bootstrap_honours_the_marker_at_start_and_does_not_load_Core()
    {
        string mod = Write(Path.Combine(_bep, InstalledState.ReloadFolder, "example.phialstore.dll"), new byte[] { 1 });
        string core = Write(Path.Combine(_bep, "progenitor-core", "Progenitor.dll"), new byte[] { 2 });
        string bootstrap = Write(Path.Combine(_bep, "plugins", "Progenitor.Bootstrap.dll"), new byte[] { 3 });
        string foreign = Write(Path.Combine(_bep, "plugins", "GroundPickupGlow.dll"), new byte[] { 9 });
        string loaderCore = Write(Path.Combine(_bep, "core", "BepInEx.Core.dll"), new byte[] { 7 });
        WriteMarker(
            "progenitor-reload/example.phialstore.dll",
            "progenitor-core/Progenitor.dll",
            "plugins/Progenitor.Bootstrap.dll",
            ClientUninstall.FileName);

        var scheduled = new List<string>();
        UninstallAtStart.ScheduleDeleteOverride = path => { scheduled.Add(path); return true; };
        var logged = new List<string>();

        var outcome = UninstallAtStart.Honour(_bep, logged.Add);

        Assert.True(outcome.Requested);
        Assert.True(outcome.Honoured);
        Assert.False(File.Exists(mod));
        Assert.False(File.Exists(core));
        // Live DLL: scheduled for the next reboot, still on disk, counted as left.
        Assert.True(File.Exists(bootstrap));
        Assert.Equal(new[] { bootstrap }, scheduled);
        Assert.Equal(1, outcome.Left);
        Assert.Equal(2, outcome.Deleted);
        // Nothing the marker did not name.
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(loaderCore));
        // The emptied Progenitor folders are gone; plugins/ and core/ stay.
        Assert.False(Directory.Exists(Path.Combine(_bep, "progenitor-core")));
        Assert.False(Directory.Exists(Path.Combine(_bep, InstalledState.ReloadFolder)));
        Assert.True(Directory.Exists(Path.Combine(_bep, "plugins")));
        Assert.True(Directory.Exists(Path.Combine(_bep, "core")));
        // The request is gone and the done note is the evidence it ran.
        Assert.False(File.Exists(ClientUninstall.PathFor(_bep)));
        string done = File.ReadAllText(Path.Combine(_bep, UninstallAtStart.DoneFileName));
        Assert.Contains("deleted=2", done, StringComparison.Ordinal);
        Assert.Contains("left=1", done, StringComparison.Ordinal);
        Assert.Equal(ClientUninstall.DoneFileName, UninstallAtStart.DoneFileName);
        Assert.Equal(ClientUninstall.FileName, UninstallAtStart.FileName);
        // One line, and it says Core was not loaded.
        Assert.Single(logged);
        Assert.Contains("Core was not loaded", logged[0], StringComparison.Ordinal);

        // A second start has nothing to do, and the caller loads normally.
        Assert.False(UninstallAtStart.HonourIfRequested(_bep, logged.Add));

        // Load honors the request before BindDiagnostics, PendingInstalls and CoreLoader.
        string load = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Bootstrap", "Plugin.cs"), "public override void Load()");
        SourcePin.AssertUncommented(load, "UninstallAtStart.HonourIfRequested(Paths.BepInExRootPath, Log.LogInfo)");
        Assert.True(
            load.IndexOf("UninstallAtStart.HonourIfRequested", StringComparison.Ordinal)
            < load.IndexOf("PendingInstalls.PromoteAtStart", StringComparison.Ordinal),
            "the uninstall must be honored before pending installs are promoted");
        Assert.True(
            load.IndexOf("UninstallAtStart.HonourIfRequested", StringComparison.Ordinal)
            < load.IndexOf("ProcessHost.Install", StringComparison.Ordinal),
            "the uninstall must be honored before ProcessHost.Install, which loads Core");
    }

    /// <summary>
    /// A mod manager installs the package into folders of its own: Bootstrap in <c>plugins/&lt;package&gt;/</c>, the
    /// preloader and <c>progenitor-files.zip</c> in <c>patchers/&lt;package&gt;/</c>. The plan names them (and the manual
    /// install's top-level preloader); Bootstrap schedules both loaded DLLs for the next reboot and deletes the archive,
    /// so the preloader cannot put Core back at the next start. What this would catch: a removal that leaves the archive
    /// (Progenitor comes back), tries to delete a loaded DLL, or lists another mod's files in the package folders.
    /// </summary>
    [Fact]
    public void A_mod_manager_install_is_removed_from_its_package_folders()
    {
        Write(Path.Combine(_bep, "progenitor-core", "Progenitor.dll"), new byte[] { 2 });
        string boot = Write(Path.Combine(_bep, "plugins", "VRising_Modders-Progenitor", "Progenitor.Bootstrap.dll"), new byte[] { 3 });
        string pre = Write(Path.Combine(_bep, "patchers", "VRising_Modders-Progenitor", "Progenitor.Preloader.dll"), new byte[] { 4 });
        string archive = Write(Path.Combine(_bep, "patchers", "VRising_Modders-Progenitor", "progenitor-files.zip"), new byte[] { 5 });
        string topPre = Write(Path.Combine(_bep, "patchers", "Progenitor.Preloader.dll"), new byte[] { 6 });
        string otherMod = Write(Path.Combine(_bep, "plugins", "Someone-Mod", "Mod.dll"), new byte[] { 7 });
        string flattenedCore = Write(Path.Combine(_bep, "plugins", "VRising_Modders-Progenitor", "Progenitor.dll"), new byte[] { 9 });
        string otherCore = Write(Path.Combine(_bep, "plugins", "Someone-Mod", "Progenitor.dll"), new byte[] { 10 });
        string otherPatcher = Write(Path.Combine(_bep, "patchers", "Someone-Patcher", "Patcher.dll"), new byte[] { 8 });

        var files = ClientUninstall.Plan(_bep);
        Assert.Contains("plugins/VRising_Modders-Progenitor/Progenitor.Bootstrap.dll", files);
        Assert.Contains("patchers/VRising_Modders-Progenitor/Progenitor.Preloader.dll", files);
        Assert.Contains("patchers/VRising_Modders-Progenitor/progenitor-files.zip", files);
        Assert.Contains("patchers/Progenitor.Preloader.dll", files);
        Assert.DoesNotContain("plugins/Someone-Mod/Mod.dll", files);
        // An older layout's Core beside Bootstrap goes too; a Progenitor.dll in another mod's folder is not ours to remove.
        Assert.Contains("plugins/VRising_Modders-Progenitor/Progenitor.dll", files);
        Assert.DoesNotContain("plugins/Someone-Mod/Progenitor.dll", files);
        Assert.DoesNotContain("patchers/Someone-Patcher/Patcher.dll", files);
        Assert.Equal(PackagePayload.FileName, ClientUninstall.OwnedPatcherFileNames[1]);

        Assert.True(ClientUninstall.TryWrite(_bep, files));
        var scheduled = new List<string>();
        UninstallAtStart.ScheduleDeleteOverride = path => { scheduled.Add(path); return true; };
        var outcome = UninstallAtStart.Honour(_bep);

        Assert.True(outcome.Honoured);
        Assert.False(File.Exists(archive));
        Assert.False(File.Exists(flattenedCore));
        Assert.True(File.Exists(otherCore));
        Assert.Equal(new[] { boot, pre, topPre }.OrderBy(p => p), scheduled.OrderBy(p => p));
        Assert.True(File.Exists(otherMod));
        Assert.True(File.Exists(otherPatcher));
        Assert.False(UninstallAtStart.IsRunningImage("progenitor-core/Progenitor.Bootstrap.dll"));
        Assert.False(UninstallAtStart.IsRunningImage("plugins/Progenitor.Preloader.dll"));
    }

    /// <summary>
    /// A marker that names anything outside the BepInEx root — absolute, a walk-up, or BepInEx's
    /// own <c>core/</c> — is refused whole: nothing is deleted, the request is left on disk, and
    /// the caller loads normally rather than running without a framework.
    /// What this would catch: applying the safe half of a tampered request, or resolving a
    /// <c>..</c> path and deleting a game file.
    /// </summary>
    [Theory]
    [InlineData("../../VRising.exe")]
    [InlineData("core/BepInEx.Core.dll")]
    [InlineData("C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("/etc/hosts")]
    public void A_marker_that_leaves_BepInEx_is_refused_whole(string bad)
    {
        string core = Write(Path.Combine(_bep, "progenitor-core", "Progenitor.dll"), new byte[] { 2 });
        string loaderCore = Write(Path.Combine(_bep, "core", "BepInEx.Core.dll"), new byte[] { 7 });
        string outside = Write(Path.Combine(GameRoot, "VRising.exe"), new byte[] { 8 });
        WriteMarker("progenitor-core/Progenitor.dll", bad);

        var logged = new List<string>();
        var outcome = UninstallAtStart.Honour(_bep, logged.Add);

        Assert.True(outcome.Requested);
        Assert.False(outcome.Honoured);
        Assert.Equal(UninstallAtStart.RefusalOutsideRoot, outcome.Reason);
        Assert.Equal(0, outcome.Deleted);
        // Not one file went, the safe one included, and the request is still there to inspect.
        Assert.True(File.Exists(core));
        Assert.True(File.Exists(loaderCore));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(ClientUninstall.PathFor(_bep)));
        Assert.False(File.Exists(Path.Combine(_bep, UninstallAtStart.DoneFileName)));
        Assert.Single(logged);
        Assert.Contains("refused", logged[0], StringComparison.Ordinal);

        // Core refuses to write such a path in the first place, and refuses to read one back.
        Assert.False(ClientUninstall.IsAllowedRelativePath(bad));
        Assert.False(UninstallAtStart.IsAllowedRelativePath(bad));
        Assert.False(ClientUninstall.TryWrite(_bep, new[] { "progenitor-core/Progenitor.dll", bad }));
        Assert.Null(ClientUninstall.TryRead(_bep));
        Assert.True(ClientUninstall.IsAllowedRelativePath("plugins/Progenitor.Dev.dll"));
    }

    // ---- fixtures --------------------------------------------------------------------------

    private string GameRoot => Path.GetDirectoryName(_bep)!;

    private ConfigurationPane NewPane()
    {
        var file = new ConfigFile(Path.Combine(GameRoot, ConfigHelper.SharedFileName), saveOnInit: true);
        return new ConfigurationPane(ConfigurationPane.Bind(file), new LocalServerFolder(), new object());
    }

    private void WriteMarker(params string[] files)
    {
        var sb = new StringBuilder();
        sb.Append("{\"schema\":1,\"requestedUtc\":\"2026-09-13T10:11:12Z\",\"files\":[");
        for (int i = 0; i < files.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(JsonSerializer.Serialize(files[i]));
        }

        sb.Append("]}");
        File.WriteAllText(ClientUninstall.PathFor(_bep), sb.ToString());
    }

    private static string Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static CatalogEntry Listing(string key, string display, string? owner = null)
    {
        int dash = key.IndexOf('-');
        string ns = dash > 0 ? key.Substring(0, dash) : (owner ?? "n");
        string name = dash > 0 ? key.Substring(dash + 1) : key;
        return new CatalogEntry(
            new ManifestMod("", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, ns, key),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-13T00:00:00Z",
            displayName: display,
            author: owner,
            owner: owner,
            packageName: name);
    }

    /// <summary>The smallest catalog the pane needs: entries, lookup and search.</summary>
    private sealed class LoaderCatalog : IInstallCatalog
    {
        public List<CatalogEntry> Entries { get; } = new();

        public InstallProgress Status => InstallProgress.Idle;

        public Task<CatalogLoadResult> LoadIndexAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogLoadResult(true, null, Entries.Count, "stamp"));

        public IReadOnlyList<CatalogEntry> Search(string? query) => Entries;

        public bool TryGetEntry(string key, out CatalogEntry? entry)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (!string.Equals(Entries[i].Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                entry = Entries[i];
                return true;
            }

            entry = null;
            return false;
        }

        public Task<InspectCallResult> DownloadAndInspectAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new InspectCallResult(false, InstallReasons.FetchNoUrl, null, null));

        public PlanCallResult Plan(string key, InstallTarget target) =>
            new(false, InstallReasons.NotInspected, null);

        public ApproveCallResult Approve(string planHash) => new(true, null, planHash, false);

        public ExecuteCallResult ExecuteToPending(string planHash) =>
            new(true, null, "pending/" + planHash, false);

        public Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(ListingDocumentSet.Unavailable(key ?? "", InstallReasons.UnknownEntry));
    }
}
