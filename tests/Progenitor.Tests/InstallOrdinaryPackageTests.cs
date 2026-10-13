using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Progenitor.Bootstrap;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job INSTORD1 fixtures: ordinary BepInEx plugin images compiled by Roslyn into byte arrays once
/// per class. The fake <c>BepInEx.Core</c> carries only the attribute names the scanner keys on
/// (<c>BepInPlugin</c>, <c>BepInProcess</c>, <c>BepInDependency</c>). Nothing is ever loaded,
/// reflected over or executed: the identity comes from <c>MetadataReader</c> over the bytes.
/// </summary>
public sealed class OrdinaryPackageFixtures
{
    private readonly List<MetadataReference> _runtime = new();
    private byte[] _bepInEx = Array.Empty<byte>();

    /// <summary>One ordinary plugin, no <c>[BepInProcess]</c>: loads anywhere.</summary>
    public byte[] Plugin { get; }

    /// <summary>A second plugin GUID in the same image.</summary>
    public byte[] TwoEntryPoints { get; }

    /// <summary>A companion library: no <c>[BepInPlugin]</c> anywhere.</summary>
    public byte[] Companion { get; }

    /// <summary><c>[BepInProcess("VRisingServer.exe")]</c> only.</summary>
    public byte[] ServerOnly { get; }

    /// <summary><c>[BepInProcess("VRising.exe")]</c> only.</summary>
    public byte[] ClientOnly { get; }

    /// <summary>A plugin declaring a <c>[BepInDependency]</c> GUID.</summary>
    public byte[] WithDependency { get; }

    public const string PluginGuid = "team.phialstore";
    public const string SecondGuid = "team.second";
    public const string DependencyGuid = "other.pool";

    public OrdinaryPackageFixtures()
    {
        foreach (var path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
        {
            if (path.Length > 0 && File.Exists(path))
                _runtime.Add(MetadataReference.CreateFromFile(path));
        }

        _bepInEx = Compile("BepInEx.Core", @"
namespace BepInEx {
    public class BepInPlugin : System.Attribute { public BepInPlugin(string guid, string name, string version) { } }
    public class BepInProcess : System.Attribute { public BepInProcess(string exe) { } }
    public class BepInDependency : System.Attribute { public BepInDependency(string guid) { } }
    public abstract class BasePlugin { public abstract void Load(); }
}");

        Plugin = PluginImage("[BepInPlugin(\"" + PluginGuid + "\", \"PhialStore\", \"1.2.3\")]");
        TwoEntryPoints = Compile("PhialStore", Head
            + "    [BepInPlugin(\"" + PluginGuid + "\", \"PhialStore\", \"1.2.3\")]\n"
            + "    public class One : BasePlugin { public override void Load() { } }\n"
            + "    [BepInPlugin(\"" + SecondGuid + "\", \"Second\", \"1.0.0\")]\n"
            + "    public class Two : BasePlugin { public override void Load() { } }\n}",
            "BepInEx.Core");
        Companion = Compile("PoolShared", @"
namespace PoolShared { public static class Helpers { public static int Answer() => 42; } }");
        ServerOnly = PluginImage("[BepInPlugin(\"" + PluginGuid + "\", \"PhialStore\", \"1.2.3\")]\n    [BepInProcess(\"VRisingServer.exe\")]");
        ClientOnly = PluginImage("[BepInPlugin(\"" + PluginGuid + "\", \"PhialStore\", \"1.2.3\")]\n    [BepInProcess(\"VRising.exe\")]");
        WithDependency = PluginImage("[BepInPlugin(\"" + PluginGuid + "\", \"PhialStore\", \"1.2.3\")]\n    [BepInDependency(\"" + DependencyGuid + "\")]");
    }

    private const string Head = @"
using BepInEx;
namespace PhialStore {
";

    private byte[] PluginImage(string attributes) =>
        Compile("PhialStore", Head + "    " + attributes + @"
    public class Plugin : BasePlugin { public override void Load() { } }
}", "BepInEx.Core");

    private byte[] Compile(string name, string source, params string[] refs)
    {
        var references = new List<MetadataReference>(_runtime);
        for (int i = 0; i < refs.Length; i++) references.Add(MetadataReference.CreateFromImage(_bepInEx));
        var compilation = CSharpCompilation.Create(
            name,
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success, name + ": " + string.Join(Environment.NewLine, emit.Diagnostics));
        return ms.ToArray();
    }
}

/// <summary>
/// Job INSTORD1: the ordinary-package restart route — approved archive layout, plugin identity read
/// from the bytes, dependency handling, target policy, pending placement, the owned-file ledger,
/// Remove, the promote gate and the readback. Game-free: zips are built in the test, no network, no
/// process is started and nothing is ever loaded.
/// </summary>
public sealed class InstallOrdinaryPackageTests : IClassFixture<OrdinaryPackageFixtures>, IDisposable
{
    private readonly OrdinaryPackageFixtures _f;
    private readonly string _dir;
    private readonly string _clientRoot;
    private readonly string _serverRoot;

    public InstallOrdinaryPackageTests(OrdinaryPackageFixtures f)
    {
        _f = f;
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-instord1-" + Guid.NewGuid().ToString("N"));
        _clientRoot = Path.Combine(_dir, "client", "BepInEx");
        _serverRoot = Path.Combine(_dir, "server", "BepInEx");
        Directory.CreateDirectory(_clientRoot);
        Directory.CreateDirectory(_serverRoot);
    }

    public void Dispose()
    {
        // INSTALLUX2 item 1: the confirmation is a modal dialog on its own panel now. Ui.ResetForTests
        // only FORGETS the open one, so without this its rows outlive the test and the next test's
        // click on prompt:<key> resolves to the dead one.
        Progenitor.Ui.UiDialog.Current?.Close();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch { /* temp */ }
    }

    // ---- layout and identity ---------------------------------------------------------------

    [Fact]
    public void A_root_dll_declaring_BepInPlugin_is_an_approved_ordinary_layout()
    {
        var inspection = Inspect(("PhialStore.dll", _f.Plugin), ("README.md", Text("hi")), ("manifest.json", Text("{}")));
        Assert.True(inspection.Ok, inspection.Refusal);
        Assert.True(inspection.OrdinaryApproved);
        Assert.Equal(OrdinaryPackageFixtures.PluginGuid, inspection.OrdinaryLayout!.Plugin!.PluginGuid);
        Assert.Equal("PhialStore.dll", inspection.OrdinaryLayout.Plugin.FileName);
        Assert.Empty(inspection.OrdinaryLayout.Companions);
        Assert.Equal("unknown", inspection.OrdinaryLayout.LoadsIn);
    }

    [Fact]
    public void A_plugins_subfolder_with_companions_is_approved_and_the_companions_are_named()
    {
        var inspection = Inspect(
            ("BepInEx/plugins/team-PhialStore/PhialStore.dll", _f.Plugin),
            ("BepInEx/plugins/team-PhialStore/PoolShared.dll", _f.Companion),
            ("icon.png", Text("png")));
        Assert.True(inspection.OrdinaryApproved);
        Assert.Equal("PhialStore.dll", inspection.OrdinaryLayout!.Plugin!.FileName);
        Assert.Equal(new[] { "PoolShared.dll" }, inspection.OrdinaryLayout.CompanionNames);
        // The plugins/ layout is still recorded as unsupported for the copy-into-plugins route;
        // this route places the DLL in Progenitor's own folder instead.
        Assert.True(inspection.UnsupportedLayout);
    }

    [Fact]
    public void Two_plugin_entry_points_are_refused_and_nothing_is_approved()
    {
        var inspection = Inspect(("PhialStore.dll", _f.TwoEntryPoints));
        Assert.False(inspection.OrdinaryApproved);
        Assert.Equal(InstallReasons.OrdinaryMultipleEntryPoints, inspection.OrdinaryLayout!.Refusal);
    }

    [Fact]
    public void A_second_dll_declaring_a_plugin_is_refused_as_more_than_one_entry_point()
    {
        var inspection = Inspect(("A.dll", _f.Plugin), ("B.dll", _f.ClientOnly));
        Assert.False(inspection.OrdinaryApproved);
        Assert.Equal(InstallReasons.OrdinaryMultipleEntryPoints, inspection.OrdinaryLayout!.Refusal);
    }

    [Fact]
    public void No_dll_declaring_a_plugin_is_refused_rather_than_named_from_the_package()
    {
        var inspection = Inspect(("PoolShared.dll", _f.Companion));
        Assert.False(inspection.OrdinaryApproved);
        Assert.Equal(InstallReasons.OrdinaryNoEntryPoint, inspection.OrdinaryLayout!.Refusal);
    }

    [Fact]
    public void A_patchers_entry_is_refused()
    {
        var inspection = Inspect(("PhialStore.dll", _f.Plugin), ("BepInEx/patchers/Pre.dll", _f.Companion));
        Assert.False(inspection.OrdinaryApproved);
        Assert.StartsWith("this package writes BepInEx/", inspection.OrdinaryLayout!.Refusal); // INSTPLUG1: the refusal names the entry
    }

    [Fact]
    public void A_config_entry_is_refused()
    {
        var inspection = Inspect(("PhialStore.dll", _f.Plugin), ("BepInEx/config/team.cfg", Text("x=1")));
        Assert.False(inspection.OrdinaryApproved);
        Assert.StartsWith("this package writes BepInEx/", inspection.OrdinaryLayout!.Refusal); // INSTPLUG1: the refusal names the entry
    }

    [Fact]
    public void A_file_this_route_would_have_to_place_is_refused_by_name()
    {
        var inspection = Inspect(("PhialStore.dll", _f.Plugin), ("data/table.csv", Text("a,b")));
        Assert.False(inspection.OrdinaryApproved);
        Assert.Equal(InstallReasons.OrdinaryExtraFile("data/table.csv"), inspection.OrdinaryLayout!.Refusal);
    }

    [Fact]
    public void A_dll_outside_the_plugins_folder_is_refused()
    {
        var inspection = Inspect(("PhialStore.dll", _f.Plugin), ("libs/PoolShared.dll", _f.Companion));
        Assert.False(inspection.OrdinaryApproved);
        Assert.Equal(InstallReasons.OrdinaryDllElsewhere, inspection.OrdinaryLayout!.Refusal);
    }

    [Fact]
    public void Bytes_that_are_not_a_readable_assembly_keep_the_existing_refusal()
    {
        var inspection = Inspect(("Mod.dll", Text("fake-dll:not-a-pe-image" + new string('X', 64))));
        Assert.False(inspection.OrdinaryApproved);
        Assert.Equal(InstallReasons.OrdinaryUnreadableImage("Mod.dll"), inspection.OrdinaryLayout!.Refusal);
        var plan = Plan(Entry(), inspection, InstallTarget.Client);
        Assert.Equal(InstallDecision.UnknownBlocked, plan.Decision);
    }

    [Fact]
    public void BepInProcess_gives_loadsIn_and_a_target_that_never_runs_it_is_refused()
    {
        var server = Inspect(("PhialStore.dll", _f.ServerOnly));
        Assert.Equal("server", server.OrdinaryLayout!.LoadsIn);

        var blocked = Plan(Entry(), server, InstallTarget.Client);
        Assert.Equal(InstallDecision.UnknownBlocked, blocked.Decision);
        Assert.Equal(InstallReasons.OrdinaryProcessMismatch("server", OrdinaryPackage.ClientProcess), blocked.Reason);

        var allowed = Plan(Entry(), server, InstallTarget.LocalServer, localServerConfigured: true);
        Assert.Equal(InstallDecision.RestartServerRequired, allowed.Decision);

        var both = Plan(Entry(), server, InstallTarget.Both, localServerConfigured: true);
        Assert.Equal(InstallDecision.UnknownBlocked, both.Decision);
        Assert.Equal(InstallReasons.OrdinaryProcessMismatch("server", OrdinaryPackage.ClientProcess), both.Reason);
    }

    [Fact]
    public void An_approved_ordinary_package_plans_a_restart_install_that_says_hot_swap_is_refused()
    {
        var inspection = Inspect(("PhialStore.dll", _f.Plugin));
        var plan = Plan(Entry(), inspection, InstallTarget.Client);
        Assert.Equal(InstallDecision.RestartClientRequired, plan.Decision);
        Assert.True(plan.CanExecuteToPending);
        Assert.Equal(OrdinaryPackageFixtures.PluginGuid, plan.PluginId);
        Assert.Equal("PhialStore.dll", plan.PluginFileName);
        Assert.Contains(InstallReasons.OrdinaryRestartRoute, plan.Reason);
        Assert.Contains(InstallReasons.OrdinaryHotSwapRefused, plan.Reason);
        Assert.Contains(InstallReasons.RestartClient, plan.Reason);
        Assert.True(plan.LedgerClient);
        Assert.False(plan.LedgerServer);
    }

    // ---- dependencies ----------------------------------------------------------------------

    [Fact]
    public void The_BepInEx_pack_dependency_is_satisfied_by_the_running_BepInEx()
    {
        var inspection = Inspect(("PhialStore.dll", _f.Plugin));
        var plan = Plan(Entry(foreign: new[] { "BepInEx-BepInExPack_V_Rising-1.733.0" }), inspection, InstallTarget.Client);
        Assert.Equal(InstallDecision.RestartClientRequired, plan.Decision);
        Assert.Single(plan.OrdinaryDependencies);
        Assert.Equal("BepInEx-BepInExPack_V_Rising", plan.OrdinaryDependencies[0].Package);
        Assert.Equal(OrdinaryDependencies.BySatisfiedBepInEx, plan.OrdinaryDependencies[0].SatisfiedBy);
    }

    [Fact]
    public void Another_thunderstore_dependency_that_is_not_installed_is_refused_with_the_exact_text()
    {
        var inspection = Inspect(("PhialStore.dll", _f.Plugin));
        var plan = Plan(Entry(foreign: new[] { "dyno-ExampleCommandFramework-0.10.4" }), inspection, InstallTarget.Client);
        Assert.Equal(InstallDecision.UnknownBlocked, plan.Decision);
        Assert.Equal("requires dyno-ExampleCommandFramework — install it first", plan.Reason);
        Assert.Equal(InstallReasons.OrdinaryRequires("dyno-ExampleCommandFramework"), plan.Reason);
        Assert.False(plan.OrdinaryDependencies[0].Satisfied);
    }

    [Fact]
    public void A_progenitor_ledgered_install_of_the_dependency_satisfies_it()
    {
        var inspection = Inspect(("PhialStore.dll", _f.Plugin));
        var view = new OrdinaryDependencyView(
            new[] { OrdinaryDependencies.LedgerSource("dyno-ExampleCommandFramework", "0.10.4") });
        var plan = InstallPlanner.Plan(
            Entry(foreign: new[] { "dyno-ExampleCommandFramework-0.10.4" }),
            inspection,
            InstallTarget.Client,
            null,
            false,
            null,
            view);
        Assert.Equal(InstallDecision.RestartClientRequired, plan.Decision);
        Assert.Equal(OrdinaryDependencies.BySatisfiedLedger, plan.OrdinaryDependencies[0].SatisfiedBy);
    }

    [Fact]
    public void A_loaded_plugin_satisfies_the_dependency_only_through_a_cached_inspection_of_that_package()
    {
        var inspection = Inspect(("PhialStore.dll", _f.Plugin));
        var cache = new OrdinaryIdentityCache(_clientRoot);
        var entry = Entry(foreign: new[] { "other-Pool-1.0.0" });

        // Loaded, but no inspection ever recorded that package's GUID: refused, not guessed.
        var uncached = new OrdinaryDependencyView(null, new[] { OrdinaryPackageFixtures.DependencyGuid }, cache);
        Assert.Equal(
            InstallDecision.UnknownBlocked,
            InstallPlanner.Plan(entry, inspection, InstallTarget.Client, null, false, null, uncached).Decision);

        // The package name is never read as a GUID either.
        var byName = new OrdinaryDependencyView(null, new[] { "other-Pool" }, cache);
        Assert.Equal(
            InstallDecision.UnknownBlocked,
            InstallPlanner.Plan(entry, inspection, InstallTarget.Client, null, false, null, byName).Decision);

        cache.Write(new OrdinaryIdentityRecord("other", "Pool", "1.0.0", OrdinaryPackageFixtures.DependencyGuid, "1.0.0", "sha"));
        var cached = new OrdinaryDependencyView(null, new[] { OrdinaryPackageFixtures.DependencyGuid }, cache);
        var plan = InstallPlanner.Plan(entry, inspection, InstallTarget.Client, null, false, null, cached);
        Assert.Equal(InstallDecision.RestartClientRequired, plan.Decision);
        Assert.Equal(OrdinaryDependencies.BySatisfiedLoaded, plan.OrdinaryDependencies[0].SatisfiedBy);
    }

    [Fact]
    public void The_identity_cache_writes_one_file_per_package_version()
    {
        var cache = new OrdinaryIdentityCache(_clientRoot);
        Assert.True(cache.Write(new OrdinaryIdentityRecord("team", "PhialStore", "1.2.3", OrdinaryPackageFixtures.PluginGuid, "1.2.3", "sha")));
        string expected = Path.Combine(_clientRoot, "progenitor-cache", "identity", "team", "PhialStore", "1.2.3.json");
        Assert.True(File.Exists(expected));
        Assert.Equal(OrdinaryPackageFixtures.PluginGuid, cache.TryRead("team", "PhialStore", "1.2.3")!.PluginGuid);
        Assert.Single(cache.TryReadAll("team", "PhialStore"));
        Assert.Null(cache.TryRead("team", "PhialStore", "9.9.9"));
    }

    // ---- placement and the ledger -----------------------------------------------------------

    [Fact]
    public void The_client_install_writes_pending_index_and_a_client_ledger_row()
    {
        string zip = Zip(("PhialStore.dll", _f.Plugin));
        var inspection = PackageInspector.InspectFile(zip);
        var plan = Plan(Entry(), inspection, InstallTarget.Client);
        var result = InstallStaging.ExecuteToPending(_clientRoot, plan, inspection, zip);
        Assert.True(result.Ok, result.Error);

        string expected = Path.Combine(_clientRoot, ModManifestPending.PendingFolderName, "index", "PhialStore.dll");
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(result.PendingPath!));
        Assert.True(File.Exists(expected));
        Assert.False(Directory.Exists(Path.Combine(_clientRoot, "plugins")));

        var ledger = ServerInstallLedger.TryRead(Path.GetDirectoryName(_clientRoot)!);
        Assert.NotNull(ledger);
        var rows = ledger!.OfCategory(ServerInstallLedger.CategoryPending);
        Assert.Single(rows);
        Assert.Equal("BepInEx/progenitor-pending/index/PhialStore.dll", rows[0].Path);
        Assert.Equal(OrdinaryDependencies.LedgerSource("team-PhialStore", "1.2.3"), rows[0].Source);
    }

    [Fact]
    public void Companions_land_flat_beside_the_plugin_and_are_ledgered_too()
    {
        string zip = Zip(
            ("BepInEx/plugins/team-PhialStore/PhialStore.dll", _f.Plugin),
            ("BepInEx/plugins/team-PhialStore/PoolShared.dll", _f.Companion));
        var inspection = PackageInspector.InspectFile(zip);
        var plan = Plan(Entry(), inspection, InstallTarget.Client);
        var result = InstallStaging.ExecuteToPending(_clientRoot, plan, inspection, zip);
        Assert.True(result.Ok, result.Error);

        string folder = Path.Combine(_clientRoot, ModManifestPending.PendingFolderName, "index");
        Assert.True(File.Exists(Path.Combine(folder, "PhialStore.dll")));
        Assert.True(File.Exists(Path.Combine(folder, "PoolShared.dll")));

        var ledger = ServerInstallLedger.TryRead(Path.GetDirectoryName(_clientRoot)!)!;
        Assert.Equal(2, ledger.OfCategory(ServerInstallLedger.CategoryPending).Count);
    }

    [Fact]
    public void The_server_install_writes_the_server_root_and_both_writes_both_roots()
    {
        string zip = Zip(("PhialStore.dll", _f.Plugin));
        var inspection = PackageInspector.InspectFile(zip);

        var serverPlan = Plan(Entry(), inspection, InstallTarget.LocalServer, localServerConfigured: true);
        Assert.True(serverPlan.LedgerServer);
        Assert.False(serverPlan.LedgerClient);
        Assert.True(InstallStaging.ExecuteToPending(_serverRoot, serverPlan, inspection, zip).Ok);
        Assert.True(File.Exists(Path.Combine(_serverRoot, ModManifestPending.PendingFolderName, "index", "PhialStore.dll")));
        Assert.Single(ServerInstallLedger.TryRead(Path.GetDirectoryName(_serverRoot)!)!.OfCategory(ServerInstallLedger.CategoryPending));
        Assert.Null(ServerInstallLedger.TryRead(Path.GetDirectoryName(_clientRoot)!));

        var bothPlan = Plan(Entry(), inspection, InstallTarget.Both, localServerConfigured: true);
        Assert.True(bothPlan.LedgerClient);
        Assert.True(bothPlan.LedgerServer);
        Assert.True(InstallStaging.ExecuteToPending(_clientRoot, bothPlan, inspection, zip).Ok);
        Assert.True(File.Exists(Path.Combine(_clientRoot, ModManifestPending.PendingFolderName, "index", "PhialStore.dll")));
        Assert.Single(ServerInstallLedger.TryRead(Path.GetDirectoryName(_clientRoot)!)!.OfCategory(ServerInstallLedger.CategoryPending));
    }

    // ---- Remove ----------------------------------------------------------------------------

    [Fact]
    public void Remove_deletes_exactly_the_ledgered_files_and_rows_of_that_package()
    {
        string zip = Zip(("PhialStore.dll", _f.Plugin), ("PoolShared.dll", _f.Companion));
        var inspection = PackageInspector.InspectFile(zip);
        var plan = Plan(Entry(), inspection, InstallTarget.Client);
        Assert.True(InstallStaging.ExecuteToPending(_clientRoot, plan, inspection, zip).Ok);

        string game = Path.GetDirectoryName(_clientRoot)!;
        string folder = Path.Combine(_clientRoot, ModManifestPending.PendingFolderName, "index");

        // A file no ledger row names, and another package's row, must both survive.
        string stranger = Path.Combine(folder, "SomeoneElse.dll");
        File.WriteAllBytes(stranger, Text("stranger"));
        var keep = ServerInstallLedger.TryRead(game)!.With(new ServerInstallLedgerEntry(
            "BepInEx/plugins/Progenitor.Bootstrap.dll", "aa", 1, ServerInstallLedger.CategoryFramework, "client-image:aa", DateTime.UtcNow));
        keep.Write(game);

        var result = ServerInstall.RemovePackage(_clientRoot, OrdinaryDependencies.LedgerSource("team-PhialStore", "1.2.3"));
        Assert.True(result.Ok, result.Message);
        Assert.False(result.NoOp);
        Assert.Empty(result.Skipped);
        Assert.Equal(2, result.Removed.Count);
        Assert.False(File.Exists(Path.Combine(folder, "PhialStore.dll")));
        Assert.False(File.Exists(Path.Combine(folder, "PoolShared.dll")));
        Assert.True(File.Exists(stranger));

        var after = ServerInstallLedger.TryRead(game)!;
        Assert.Empty(after.OfCategory(ServerInstallLedger.CategoryPending));
        Assert.Single(after.OfCategory(ServerInstallLedger.CategoryFramework));
    }

    [Fact]
    public void Remove_also_takes_the_promoted_reload_copy_and_keeps_a_changed_file()
    {
        string zip = Zip(("PhialStore.dll", _f.Plugin));
        var inspection = PackageInspector.InspectFile(zip);
        var plan = Plan(Entry(), inspection, InstallTarget.Client);
        Assert.True(InstallStaging.ExecuteToPending(_clientRoot, plan, inspection, zip).Ok);

        string reload = Path.Combine(_clientRoot, ModFolders.ReloadFolderName);
        Directory.CreateDirectory(reload);
        File.WriteAllBytes(Path.Combine(reload, "PhialStore.dll"), _f.Plugin);

        var removed = ServerInstall.RemovePackage(_clientRoot, OrdinaryDependencies.LedgerSource("team-PhialStore", "1.2.3"));
        Assert.True(removed.Ok);
        Assert.False(File.Exists(Path.Combine(reload, "PhialStore.dll")));

        // A second install whose pending bytes the player then edited is reported and kept.
        Assert.True(InstallStaging.ExecuteToPending(_clientRoot, plan, inspection, zip).Ok);
        string pending = Path.Combine(_clientRoot, ModManifestPending.PendingFolderName, "index", "PhialStore.dll");
        File.WriteAllBytes(pending, Text("the player's own build"));
        var second = ServerInstall.RemovePackage(_clientRoot, OrdinaryDependencies.LedgerSource("team-PhialStore", "1.2.3"));
        Assert.True(second.Ok);
        Assert.Single(second.Skipped);
        Assert.True(File.Exists(pending));
    }

    [Fact]
    public void Remove_of_a_package_no_ledger_names_is_a_no_op()
    {
        var result = ServerInstall.RemovePackage(_clientRoot, OrdinaryDependencies.LedgerSource("team-PhialStore", "1.2.3"));
        Assert.True(result.Ok);
        Assert.True(result.NoOp);
        Assert.Empty(result.Removed);
    }

    // ---- the promote gate -------------------------------------------------------------------

    [Fact]
    public void An_index_host_pending_file_promotes_into_the_reload_folder_without_a_session_marker()
    {
        string zip = Zip(("PhialStore.dll", _f.Plugin));
        var inspection = PackageInspector.InspectFile(zip);
        var plan = Plan(Entry(), inspection, InstallTarget.Client);
        Assert.True(InstallStaging.ExecuteToPending(_clientRoot, plan, inspection, zip).Ok);

        string pendingRoot = Path.Combine(_clientRoot, ModManifestPending.PendingFolderName);
        string reloadRoot = Path.Combine(_clientRoot, ModFolders.ReloadFolderName);
        var rows = PendingInstalls.PromoteAll(pendingRoot, reloadRoot, allowListPath: "", info: null, warn: null);

        var row = Assert.Single(Rows(rows, "PhialStore.dll"));
        Assert.Equal(PromoteOutcome.Promoted, row.Outcome);
        Assert.Equal(AllowDecision.NoList, row.Decision);
        // Flat in the reload root, exactly where both reloaders' single flat scan looks.
        Assert.True(File.Exists(Path.Combine(reloadRoot, "PhialStore.dll")));
        Assert.False(File.Exists(Path.Combine(pendingRoot, "index", "PhialStore.dll")));
    }

    [Fact]
    public void With_an_allowlist_the_index_host_file_needs_its_own_row()
    {
        string zip = Zip(("PhialStore.dll", _f.Plugin));
        var inspection = PackageInspector.InspectFile(zip);
        var plan = Plan(Entry(), inspection, InstallTarget.Client);
        Assert.True(InstallStaging.ExecuteToPending(_clientRoot, plan, inspection, zip).Ok);

        string pendingRoot = Path.Combine(_clientRoot, ModManifestPending.PendingFolderName);
        string reloadRoot = Path.Combine(_clientRoot, ModFolders.ReloadFolderName);
        string allow = Path.Combine(_dir, "progenitor-allow.txt");
        File.WriteAllText(allow, "0000000000000000000000000000000000000000000000000000000000000000  Other.dll\n");

        var refused = Assert.Single(Rows(
            PendingInstalls.PromoteAll(pendingRoot, reloadRoot, allow, null, null), "PhialStore.dll"));
        Assert.Equal(PromoteOutcome.Refused, refused.Outcome);
        Assert.Equal(AllowDecision.Unlisted, refused.Decision);
        Assert.False(File.Exists(Path.Combine(reloadRoot, "PhialStore.dll")));

        File.WriteAllText(allow, Sha(_f.Plugin) + "  PhialStore.dll\n");
        var listed = Assert.Single(Rows(
            PendingInstalls.PromoteAll(pendingRoot, reloadRoot, allow, null, null), "PhialStore.dll"));
        Assert.Equal(PromoteOutcome.Promoted, listed.Outcome);
        Assert.Equal(AllowDecision.Listed, listed.Decision);
        Assert.True(File.Exists(Path.Combine(reloadRoot, "PhialStore.dll")));
    }

    // ---- readback ---------------------------------------------------------------------------

    [Fact]
    public void The_listing_state_name_gains_removed()
    {
        Assert.Equal("removed", InstallModsPane.StateName(InstallListingState.Removed));
        Assert.Equal("pending", InstallModsPane.StateName(InstallListingState.Pending));
    }

    [Fact]
    public void The_remove_captions_say_what_a_loaded_removal_still_costs()
    {
        Assert.Equal("Remove", InstallModsCopy.Remove);
        Assert.Equal("Remove · restart to unload", InstallModsCopy.RemoveRestartToUnload);
        Assert.Contains("still loaded until this process restarts", InstallModsCopy.RemovedNeedsRestart);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static List<PromoteRow> Rows(IReadOnlyList<PromoteRow> rows, string name)
    {
        var list = new List<PromoteRow>();
        for (int i = 0; i < rows.Count; i++)
            if (string.Equals(rows[i].FileName, name, StringComparison.OrdinalIgnoreCase)) list.Add(rows[i]);
        return list;
    }

    private static InstallPlan Plan(
        CatalogEntry entry,
        PackageInspection inspection,
        InstallTarget target,
        bool localServerConfigured = false) =>
        InstallPlanner.Plan(entry, inspection, target, null, localServerConfigured, null, null);

    private static CatalogEntry Entry(string[]? foreign = null) =>
        new(
            new ManifestMod("", "1.2.3", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact, false, "team", "team-PhialStore", null, 1, null),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "stamp",
            displayName: "PhialStore",
            author: "team",
            owner: "team",
            packageName: "PhialStore",
            foreignDependencies: foreign);

    private PackageInspection Inspect(params (string Name, byte[] Body)[] entries)
    {
        var inspection = PackageInspector.InspectFile(Zip(entries));
        return inspection;
    }

    private string Zip(params (string Name, byte[] Body)[] entries)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "pkg-" + Guid.NewGuid().ToString("N") + ".zip");
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        for (int i = 0; i < entries.Length; i++)
        {
            var created = zip.CreateEntry(entries[i].Name, CompressionLevel.NoCompression);
            using var s = created.Open();
            s.Write(entries[i].Body, 0, entries[i].Body.Length);
        }

        return path;
    }

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    private static string Sha(byte[] bytes)
    {
        var sb = new StringBuilder(64);
        foreach (byte b in SHA256.HashData(bytes)) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
