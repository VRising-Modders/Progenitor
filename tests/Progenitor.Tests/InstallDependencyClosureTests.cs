using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job DEPS1 fixtures: several distinct ordinary BepInEx plugin images, compiled once per class by
/// Roslyn into byte arrays. Distinct plugin GUIDs and DLL names on purpose — a dependency and the
/// package that needs it must land on two different pending files. Nothing is ever loaded,
/// reflected over or executed; identity is read from the bytes by <c>MetadataReader</c>.
/// </summary>
public sealed class DependencyPackageFixtures
{
    private readonly List<MetadataReference> _runtime = new();
    private readonly byte[] _bepInEx;

    public const string RootGuid = "modix.autolatchgates";
    public const string EcfGuid = "dyno.examplecommandframework";
    public const string BaseGuid = "dyno.vcfbase";

    /// <summary>The package the player clicked: no <c>[BepInProcess]</c>, so it loads anywhere.</summary>
    public byte[] AutoLatchGates { get; }

    /// <summary>Its Thunderstore dependency.</summary>
    public byte[] Ecf { get; }

    /// <summary>A dependency of the dependency, so the closure has two levels.</summary>
    public byte[] EcfBase { get; }

    /// <summary><c>[BepInProcess("VRisingServer.exe")]</c>: a dependency that only the server runs.</summary>
    public byte[] ServerOnlyVcf { get; }

    /// <summary>Two entry points: an archive layout the ordinary route refuses.</summary>
    public byte[] TwoEntryPoints { get; }

    public DependencyPackageFixtures()
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

        AutoLatchGates = Plugin("AutoLatchGates", RootGuid, "AutoLatchGates", "3.1.0", null);
        Ecf = Plugin("Ecf", EcfGuid, "ExampleCommandFramework", "0.11.0", null);
        EcfBase = Plugin("EcfBase", BaseGuid, "EcfBase", "1.0.0", null);
        ServerOnlyVcf = Plugin("Ecf", EcfGuid, "ExampleCommandFramework", "0.11.0", "VRisingServer.exe");
        TwoEntryPoints = Compile("Ecf", @"
using BepInEx;
namespace Ecf {
    [BepInPlugin(""dyno.one"", ""One"", ""1.0.0"")]
    public class One : BasePlugin { public override void Load() { } }
    [BepInPlugin(""dyno.two"", ""Two"", ""1.0.0"")]
    public class Two : BasePlugin { public override void Load() { } }
}", "BepInEx.Core");
    }

    private byte[] Plugin(string assembly, string guid, string name, string version, string? process)
    {
        string attributes = "[BepInPlugin(\"" + guid + "\", \"" + name + "\", \"" + version + "\")]"
            + (process == null ? "" : "\n    [BepInProcess(\"" + process + "\")]");
        return Compile(assembly, @"
using BepInEx;
namespace " + assembly + @" {
    " + attributes + @"
    public class Plugin : BasePlugin { public override void Load() { } }
}", "BepInEx.Core");
    }

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
/// Job DEPS1: installing a package together with the packages it needs. The live case is the user
/// clicking Install on <c>Modix-AutoLatchGates 3.1.0</c>, whose Thunderstore dependency
/// <c>dyno-ExampleCommandFramework</c> is not installed: INSTORD1 refused with
/// <c>requires dyno-ExampleCommandFramework — install it first</c>, and the decision was to install
/// the dependency alongside it.
///
/// <para>Covered here: the closure's order and exclusions, its version rule, a dependency that is
/// already satisfied, a cycle and an unknown package, one refused member refusing the whole set,
/// the execute order and what a partial failure reports, the half a server-only dependency lands
/// on, Remove's dependent awareness and the confirmation text. Game-free: zips are built in the
/// test, the transport is a fake, no process is started and nothing is ever loaded.</para>
/// </summary>
public sealed class InstallDependencyClosureTests : IClassFixture<DependencyPackageFixtures>, IDisposable
{
    private const string RootKey = "Modix-AutoLatchGates";
    private const string EcfKey = "dyno-ExampleCommandFramework";
    private const string BaseKey = "dyno-EcfBase";

    private readonly DependencyPackageFixtures _f;
    private readonly string _dir;
    private readonly string _clientRoot;
    private readonly string _serverRoot;

    public InstallDependencyClosureTests(DependencyPackageFixtures f)
    {
        _f = f;
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-deps1-" + Guid.NewGuid().ToString("N"));
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

    // ---- the closure ------------------------------------------------------------------------

    [Fact]
    public void The_closure_is_ordered_dependencies_first_and_never_names_the_loader_pack()
    {
        var index = Index(
            Listing("Modix", "AutoLatchGates", "3.1.0", "BepInEx-BepInExPack_V_Rising-1.733.0", "dyno-ExampleCommandFramework-0.10.3"),
            Listing("dyno", "ExampleCommandFramework", "0.11.0", "BepInEx-BepInExPack_V_Rising-1.733.0", "dyno-EcfBase-1.0.0"),
            Listing("dyno", "EcfBase", "1.0.0"));

        var closure = PackageDependencyClosure.Resolve(index[RootKey], Lookup(index), null);
        Assert.True(closure.Ok, closure.Refusal);
        Assert.Equal(new[] { BaseKey, EcfKey }, Packages(closure));
        Assert.Equal(2, closure.MissingCount);
        foreach (var member in closure.Members)
        {
            Assert.Equal(PackageDependencyClosure.WhyMissing, member.Why);
            Assert.Equal(PackageDependencyClosure.BySatisfiedClosure, member.SatisfiedBy);
        }

        // Revert: walking the loader pack, or adding a member before the package it depends on,
        // fails this — the loader is what is already running and Progenitor never writes BepInEx/core.
    }

    [Fact]
    public void The_version_installed_is_the_listed_one_or_the_newest_and_an_older_index_refuses()
    {
        var newer = Index(
            Listing("Modix", "AutoLatchGates", "3.1.0", "dyno-ExampleCommandFramework-0.10.3"),
            Listing("dyno", "ExampleCommandFramework", "0.11.0"));
        var picked = Assert.Single(PackageDependencyClosure.Resolve(newer[RootKey], Lookup(newer), null).Members);
        Assert.Equal("0.11.0", picked.Version);
        Assert.Equal("0.10.3", picked.RequestedVersion);
        Assert.Equal(PackageDependencyClosure.RuleNewest, picked.VersionRule);

        var exact = Index(
            Listing("Modix", "AutoLatchGates", "3.1.0", "dyno-ExampleCommandFramework-0.11.0"),
            Listing("dyno", "ExampleCommandFramework", "0.11.0"));
        Assert.Equal(
            PackageDependencyClosure.RuleListed,
            Assert.Single(PackageDependencyClosure.Resolve(exact[RootKey], Lookup(exact), null).Members).VersionRule);

        var older = Index(
            Listing("Modix", "AutoLatchGates", "3.1.0", "dyno-ExampleCommandFramework-1.2.0"),
            Listing("dyno", "ExampleCommandFramework", "0.11.0"));
        var refused = PackageDependencyClosure.Resolve(older[RootKey], Lookup(older), null);
        Assert.False(refused.Ok);
        Assert.Equal(InstallReasons.ClosureOlderThanListed(EcfKey, "0.11.0", "1.2.0"), refused.Refusal);
        Assert.Empty(refused.Members);
        // Revert: silently installing an older version than the listing asked for fails this.
    }

    [Fact]
    public void A_dependency_already_on_the_target_is_present_and_is_not_walked_or_planned_again()
    {
        var index = Index(
            Listing("Modix", "AutoLatchGates", "3.1.0", "dyno-ExampleCommandFramework-0.10.3"),
            Listing("dyno", "ExampleCommandFramework", "0.11.0", "dyno-EcfBase-1.0.0"),
            Listing("dyno", "EcfBase", "1.0.0"));

        var ledgered = new OrdinaryDependencyView(new[] { OrdinaryDependencies.LedgerSource(EcfKey, "0.11.0") });
        var byLedger = PackageDependencyClosure.Resolve(index[RootKey], Lookup(index), ledgered);
        var one = Assert.Single(byLedger.Members);
        Assert.Equal(EcfKey, one.Package);
        Assert.Equal(PackageDependencyClosure.WhyPresent, one.Why);
        Assert.Equal(PackageDependencyClosure.BySatisfiedInstalled, one.SatisfiedBy);
        Assert.Equal(0, byLedger.MissingCount);
        // EcfBase is only reachable *through* ECF, and a satisfied dependency is not walked.
        Assert.DoesNotContain(BaseKey, Packages(byLedger));

        // The same fact through a loaded plugin, which counts only through a cached inspection of
        // that package — never by reading the package name as a GUID.
        var cache = new OrdinaryIdentityCache(_clientRoot);
        cache.Write(new OrdinaryIdentityRecord(
            "dyno", "ExampleCommandFramework", "0.11.0", DependencyPackageFixtures.EcfGuid, "0.11.0", "sha"));
        var loaded = new OrdinaryDependencyView(null, new[] { DependencyPackageFixtures.EcfGuid }, cache);
        var byLoaded = Assert.Single(PackageDependencyClosure.Resolve(index[RootKey], Lookup(index), loaded).Members);
        Assert.Equal(PackageDependencyClosure.BySatisfiedLoaded, byLoaded.SatisfiedBy);

        var byName = new OrdinaryDependencyView(null, new[] { EcfKey }, cache);
        var unsatisfied = PackageDependencyClosure.Resolve(index[RootKey], Lookup(index), byName);
        // Not satisfied, so it is walked after all — and its own dependency joins the closure, which
        // is the same fact the satisfied case above proves by *not* containing EcfBase.
        Assert.Equal(new[] { BaseKey, EcfKey }, Packages(unsatisfied));
        Assert.Equal(2, unsatisfied.MissingCount);
        // Revert: re-planning a satisfied dependency, walking through it, or reading a package name
        // as a plugin GUID fails this.
    }

    [Fact]
    public void An_unknown_package_a_cycle_and_a_malformed_dependency_string_all_refuse_by_name()
    {
        var unknown = Index(Listing("Modix", "AutoLatchGates", "3.1.0", "dyno-ExampleCommandFramework-0.10.3"));
        var missing = PackageDependencyClosure.Resolve(unknown[RootKey], Lookup(unknown), null);
        Assert.False(missing.Ok);
        Assert.Equal(InstallReasons.ClosureUnknownPackage(EcfKey), missing.Refusal);

        var cyclic = Index(
            Listing("Modix", "AutoLatchGates", "3.1.0", "dyno-ExampleCommandFramework-0.11.0"),
            Listing("dyno", "ExampleCommandFramework", "0.11.0", "dyno-EcfBase-1.0.0"),
            Listing("dyno", "EcfBase", "1.0.0", "dyno-ExampleCommandFramework-0.11.0"));
        var cycle = PackageDependencyClosure.Resolve(cyclic[RootKey], Lookup(cyclic), null);
        Assert.False(cycle.Ok);
        Assert.Equal(
            InstallReasons.ClosureCycle(RootKey + " → " + EcfKey + " → " + BaseKey + " → " + EcfKey),
            cycle.Refusal);
        Assert.Empty(cycle.Members);

        // A dependency that comes back to the root is a cycle too.
        var toRoot = Index(
            Listing("Modix", "AutoLatchGates", "3.1.0", "dyno-ExampleCommandFramework-0.11.0"),
            Listing("dyno", "ExampleCommandFramework", "0.11.0", "Modix-AutoLatchGates-3.1.0"));
        Assert.False(PackageDependencyClosure.Resolve(toRoot[RootKey], Lookup(toRoot), null).Ok);

        var malformed = Index(Listing("Modix", "AutoLatchGates", "3.1.0", "ExampleCommandFramework"));
        var bad = PackageDependencyClosure.Resolve(malformed[RootKey], Lookup(malformed), null);
        Assert.False(bad.Ok);
        Assert.Equal(InstallReasons.ClosureMalformedDependency("ExampleCommandFramework"), bad.Refusal);
        // Revert: dropping the on-stack check recurses until the stack overflows; guessing a package
        // the index does not carry installs something nobody named.
    }

    [Fact]
    public void A_closure_member_satisfies_the_planner_where_a_bare_dependency_still_refuses()
    {
        var inspection = Inspect(("AutoLatchGates.dll", _f.AutoLatchGates));
        var entry = Listing("Modix", "AutoLatchGates", "3.1.0", "dyno-ExampleCommandFramework-0.10.3");

        var bare = InstallPlanner.Plan(entry, inspection, InstallTarget.Client, null, false, null, null);
        Assert.Equal(InstallDecision.UnknownBlocked, bare.Decision);
        Assert.Equal(InstallReasons.OrdinaryRequires(EcfKey), bare.Reason);

        var withClosure = new OrdinaryDependencyView(null, null, null, new[] { EcfKey });
        var planned = InstallPlanner.Plan(entry, inspection, InstallTarget.Client, null, false, null, withClosure);
        Assert.Equal(InstallDecision.RestartClientRequired, planned.Decision);
        Assert.Equal(OrdinaryDependencies.BySatisfiedClosure, planned.OrdinaryDependencies[0].SatisfiedBy);
        Assert.True(planned.OrdinaryDependencies[0].Satisfied);
        // Revert: letting the closure satisfy a dependency it does not name, or ignoring the closure
        // in the planner, flips one of these two plans.
    }

    // ---- plan with dependencies ---------------------------------------------------------------

    [Fact]
    public async Task PlanWithDependencies_plans_the_dependency_first_and_the_root_stops_refusing()
    {
        var catalog = Catalog(
            (RootKey, "3.1.0", Zip(("AutoLatchGates.dll", _f.AutoLatchGates)), new[] { "BepInEx-BepInExPack_V_Rising-1.733.0", "dyno-ExampleCommandFramework-0.10.3" }),
            (EcfKey, "0.11.0", Zip(("ECF.dll", _f.Ecf)), Array.Empty<string>()));

        Assert.True((await catalog.DownloadAndInspectAsync(RootKey, CancellationToken.None)).Ok);
        Assert.Equal(InstallDecision.UnknownBlocked, catalog.Plan(RootKey, InstallTarget.Client).Plan!.Decision);

        var set = await catalog.PlanWithDependenciesAsync(RootKey, InstallTarget.Client, CancellationToken.None);
        Assert.True(set.Ok, set.Error);
        Assert.Equal(1, set.ClosureCount);
        Assert.Equal(new[] { EcfKey, RootKey }, MemberPackages(set));
        Assert.Equal(InstallDecision.RestartClientRequired, set.Root!.Decision);
        Assert.Equal(InstallTarget.Client, set.Members[0].Target);

        var row = Assert.Single(set.DependencyRows());
        Assert.Equal(EcfKey, row["package"]);
        Assert.Equal("0.11.0", row["version"]);
        Assert.Equal(PackageDependencyClosure.BySatisfiedClosure, row["satisfiedBy"]);
        Assert.Equal("RestartClientRequired", row["decision"]);
        Assert.Equal("client", row["target"]);
        Assert.Equal(PackageDependencyClosure.RuleNewest, row["versionRule"]);
        Assert.NotNull(row["reason"]);
        // Revert: planning the root without its closure returns the INSTORD1 refusal, which the
        // first assert here pins as the pre-DEPS1 behavior.
    }

    /// <summary>
    /// INSTALL2 defect B (live, deploy #69). The same closure, driven through the Install tab's own
    /// click path — the row's button, the inspection, then the set plan — because that is where it
    /// broke: the pane asked for the plain plan first, the root refused on the very dependency the set
    /// would have installed (<c>requires dyno-ExampleCommandFramework — install it first</c>), and
    /// AutoLatchGates read <c>blocked</c> / <b>Cannot install</b> with <c>closureCount 0</c> and no
    /// prompt at all.
    ///
    /// <para>What this would catch: asking <c>PlanSelected</c> before <c>PlanWithDependenciesAsync</c>
    /// again, a prompt whose body does not offer to install the missing dependency, or a blocked row
    /// with an empty reason.</para>
    /// </summary>
    [Fact]
    public async Task The_pane_s_click_plans_the_closure_so_a_missing_dependency_is_offered_in_the_prompt()
    {
        var catalog = Catalog(
            (RootKey, "3.1.0", Zip(("AutoLatchGates.dll", _f.AutoLatchGates)), new[] { "BepInEx-BepInExPack_V_Rising-1.733.0", "dyno-ExampleCommandFramework-0.10.3" }),
            (EcfKey, "0.11.0", Zip(("ECF.dll", _f.Ecf)), Array.Empty<string>()));
        var pane = new Progenitor.Ui.InstallModsPane(catalog);

        Assert.True(pane.OnInstallClick(RootKey));
        await pane.ActiveDownload!;
        Assert.NotNull(pane.ActiveSetPlan);
        await pane.ActiveSetPlan!;

        Assert.True(catalog.TryGetEntry(RootKey, out var root) && root != null);
        Assert.Equal(Progenitor.Ui.InstallPromptKind.Install, pane.Prompt);
        Assert.Equal(RootKey, pane.PromptKey);
        Assert.Equal(Progenitor.Ui.InstallListingState.None, pane.ActionFor(root!).State);
        Assert.Equal(1, pane.PlanSet!.ClosureCount);

        string body = pane.PromptBodyText(root!, Progenitor.Ui.InstallPromptKind.Install);
        Assert.Equal("Install AutoLatchGates 3.1.0 on this game?", pane.PromptHeadingText(root!, Progenitor.Ui.InstallPromptKind.Install));
        Assert.Contains("Needs ExampleCommandFramework 0.11.0, which will be installed too.", body);
        Assert.Contains("Loads at the next restart of V Rising.", body);

        var action = pane.ActionToWidget(root!);
        var prompt = (Dictionary<string, object?>)action["prompt"]!;
        Assert.Equal("install", prompt["kind"]);
        Assert.Equal(body, prompt["body"]);
        var plan = (Dictionary<string, object?>)action["plan"]!;
        Assert.Equal(1, plan["closureCount"]);
        var rows = (List<Dictionary<string, object?>>)plan["dependencies"]!;
        Assert.Equal(EcfKey, Assert.Single(rows)["package"]);
        Assert.Equal(PackageDependencyClosure.BySatisfiedClosure, rows[0]["satisfiedBy"]);

        // Yes writes the dependency first and then the root, and both rows go pending.
        Assert.True(pane.PromptYes());
        var written = pane.LastExecuteSet!;
        Assert.True(written.Ok, written.Error);
        Assert.Equal(new[] { EcfKey, RootKey }, written.Members.Select(m => m.Package).ToArray());
        Assert.Equal(Progenitor.Ui.InstallListingState.Pending, pane.ActionFor(root!).State);
        Assert.True(catalog.TryGetEntry(EcfKey, out var ecf) && ecf != null);
        Assert.Equal(Progenitor.Ui.InstallListingState.Pending, pane.ActionFor(ecf!).State);
        var result = (Dictionary<string, object?>)pane.ActionToWidget(root!)["result"]!;
        Assert.Equal(2, ((List<Dictionary<string, object?>>)result["members"]!).Count);
    }

    /// <summary>
    /// INSTALL2 defect B, the other half: a set that genuinely cannot be installed leaves the row
    /// <c>blocked</c> <em>with the refusal on it</em> — the live row read <c>reason ''</c>, so nothing
    /// on screen said why.
    /// </summary>
    [Fact]
    public async Task A_refused_set_leaves_the_row_blocked_with_the_refusal_as_its_reason()
    {
        var catalog = Catalog(
            (RootKey, "3.1.0", Zip(("AutoLatchGates.dll", _f.AutoLatchGates)), new[] { "dyno-ExampleCommandFramework-0.10.3" }),
            (EcfKey, "0.11.0", Zip(("ECF.dll", _f.TwoEntryPoints)), Array.Empty<string>()));
        var pane = new Progenitor.Ui.InstallModsPane(catalog);

        Assert.True(pane.OnInstallClick(RootKey));
        await pane.ActiveDownload!;
        await pane.ActiveSetPlan!;

        Assert.True(catalog.TryGetEntry(RootKey, out var root) && root != null);
        // INSTALLUX1 item 1: the box opened on the click, so the refusal replaces its body and
        // leaves one way out rather than closing a box the player is looking at.
        Assert.Equal(Progenitor.Ui.InstallPromptKind.Install, pane.Prompt);
        Assert.Equal(
            InstallReasons.ClosureMemberRefused(EcfKey, InstallReasons.OrdinaryBepInExBlocked),
            pane.PromptRefusal);
        var action = pane.ActionFor(root!);
        Assert.Equal(Progenitor.Ui.InstallListingState.Blocked, action.State);
        Assert.Equal(Progenitor.Ui.InstallModsCopy.CannotInstall, action.Label);
        Assert.NotEqual("", action.Reason);
        Assert.Equal(InstallReasons.ClosureMemberRefused(EcfKey, InstallReasons.OrdinaryBepInExBlocked), pane.ActionError);
        // The row's own readback carries it too, which is what read '' live.
        Assert.Equal("blocked", pane.ListingToWidget(root!)["state"]);
        Assert.NotEqual("", (string)pane.ListingToWidget(root!)["reason"]!);
        Assert.False(Directory.Exists(Path.Combine(_clientRoot, ModManifestPending.PendingFolderName)));
    }

    [Fact]
    public async Task A_satisfied_dependency_is_reported_without_being_downloaded_again()
    {
        var catalog = Catalog(
            (RootKey, "3.1.0", Zip(("AutoLatchGates.dll", _f.AutoLatchGates)), new[] { "dyno-ExampleCommandFramework-0.10.3" }),
            (EcfKey, "0.11.0", Zip(("ECF.dll", _f.Ecf)), Array.Empty<string>()));
        Assert.True((await catalog.DownloadAndInspectAsync(RootKey, CancellationToken.None)).Ok);

        // ECF already ledgered on the client root: the set has no member to write but the root plans.
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            new ServerInstallLedgerEntry(
                "BepInEx/progenitor-reload/ECF.dll", "aa", 1, ServerInstallLedger.CategoryMod,
                OrdinaryDependencies.LedgerSource(EcfKey, "0.11.0"), DateTime.UtcNow),
        }).Write(Path.GetDirectoryName(_clientRoot)!);

        int downloadsBefore = _downloads;
        var set = await catalog.PlanWithDependenciesAsync(RootKey, InstallTarget.Client, CancellationToken.None);
        Assert.True(set.Ok, set.Error);
        Assert.Equal(0, set.ClosureCount);
        Assert.Equal(new[] { RootKey }, MemberPackages(set));
        Assert.Equal(downloadsBefore, _downloads);

        var row = Assert.Single(set.DependencyRows());
        Assert.Equal(PackageDependencyClosure.BySatisfiedInstalled, row["satisfiedBy"]);
        Assert.Null(row["decision"]);
        Assert.Equal(InstallReasons.ClosureAlreadySatisfied, row["reason"]);
        // Revert: downloading a dependency that is already ledgered fails the download count.
    }

    [Fact]
    public async Task One_refused_member_refuses_the_whole_set_and_writes_nothing()
    {
        var catalog = Catalog(
            (RootKey, "3.1.0", Zip(("AutoLatchGates.dll", _f.AutoLatchGates)), new[] { "dyno-ExampleCommandFramework-0.10.3" }),
            (EcfKey, "0.11.0", Zip(("ECF.dll", _f.TwoEntryPoints)), Array.Empty<string>()));
        Assert.True((await catalog.DownloadAndInspectAsync(RootKey, CancellationToken.None)).Ok);

        var set = await catalog.PlanWithDependenciesAsync(RootKey, InstallTarget.Client, CancellationToken.None);
        Assert.False(set.Ok);
        Assert.StartsWith("Cannot install — " + EcfKey + " refused: ", set.Error);
        // The member's own planner reason, verbatim: a package whose archive layout the ordinary
        // route cannot place keeps its existing INSTORD1 refusal inside the set's sentence.
        Assert.Equal(InstallReasons.ClosureMemberRefused(EcfKey, InstallReasons.OrdinaryBepInExBlocked), set.Error);
        Assert.Equal(InstallReasons.OrdinaryMultipleEntryPoints, Inspect(("ECF.dll", _f.TwoEntryPoints)).OrdinaryLayout!.Refusal);
        Assert.Null(set.Root);
        Assert.Empty(set.Members);

        // The refusal left no closure behind, so a plain plan of the root refuses exactly as before.
        Assert.Empty(catalog.RememberedClosure(RootKey));
        Assert.Equal(InstallReasons.OrdinaryRequires(EcfKey), catalog.Plan(RootKey, InstallTarget.Client).Plan!.Reason);
        Assert.False(Directory.Exists(Path.Combine(_clientRoot, ModManifestPending.PendingFolderName)));

        // Executing a refused set is refused too.
        Assert.False(catalog.ExecuteWithDependencies(set).Ok);
        // Revert: planning the members that did pass, or keeping the closure after a refusal, would
        // let the root install without the package it needs.
    }

    // ---- execute ------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteWithDependencies_writes_the_dependency_before_the_root_and_ledgers_both()
    {
        var catalog = Catalog(
            (RootKey, "3.1.0", Zip(("AutoLatchGates.dll", _f.AutoLatchGates)), new[] { "dyno-ExampleCommandFramework-0.10.3" }),
            (EcfKey, "0.11.0", Zip(("ECF.dll", _f.Ecf)), Array.Empty<string>()));
        Assert.True((await catalog.DownloadAndInspectAsync(RootKey, CancellationToken.None)).Ok);

        var set = await catalog.PlanWithDependenciesAsync(RootKey, InstallTarget.Client, CancellationToken.None);
        Assert.True(set.Ok, set.Error);
        var result = catalog.ExecuteWithDependencies(set);
        Assert.True(result.Ok, result.Error);
        Assert.Null(result.FailedPackage);

        Assert.Equal(new[] { EcfKey, RootKey }, WrittenPackages(result));
        string folder = Path.Combine(_clientRoot, ModManifestPending.PendingFolderName, "index");
        Assert.Equal(Path.GetFullPath(Path.Combine(folder, "ECF.dll")), Path.GetFullPath(result.Members[0].Path!));
        Assert.Equal(Path.GetFullPath(Path.Combine(folder, "AutoLatchGates.dll")), Path.GetFullPath(result.Members[1].Path!));
        Assert.True(File.Exists(Path.Combine(folder, "ECF.dll")));
        Assert.True(File.Exists(Path.Combine(folder, "AutoLatchGates.dll")));
        Assert.False(result.Members[0].HotLoaded);
        Assert.True(result.Members[1].IsRoot);

        var sources = ServerInstall.LedgerSources(_clientRoot);
        Assert.Contains(OrdinaryDependencies.LedgerSource(EcfKey, "0.11.0"), sources);
        Assert.Contains(OrdinaryDependencies.LedgerSource(RootKey, "3.1.0"), sources);

        // The readback rows carry what the pane shows for each member.
        var rows = result.MemberRows();
        Assert.Equal(2, rows.Count);
        Assert.Equal(EcfKey, rows[0]["package"]);
        Assert.Equal(false, rows[0]["hotLoaded"]);
        Assert.Null(rows[0]["serverPendingPath"]);

        // With both on the target, the root now plans on its own: the ledger satisfies the dependency.
        Assert.Empty(catalog.RememberedClosure(RootKey));
        Assert.Equal(InstallDecision.RestartClientRequired, catalog.Plan(RootKey, InstallTarget.Client).Plan!.Decision);
        // Revert: writing the root first, or dropping either ledger row, fails this.
    }

    [Fact]
    public async Task A_member_that_fails_stops_the_set_and_the_members_already_written_stay_installed()
    {
        var catalog = Catalog(
            (RootKey, "3.1.0", Zip(("AutoLatchGates.dll", _f.AutoLatchGates)), new[] { "dyno-ExampleCommandFramework-0.10.3" }),
            (EcfKey, "0.11.0", Zip(("ECF.dll", _f.Ecf)), Array.Empty<string>()));
        Assert.True((await catalog.DownloadAndInspectAsync(RootKey, CancellationToken.None)).Ok);
        var set = await catalog.PlanWithDependenciesAsync(RootKey, InstallTarget.Client, CancellationToken.None);
        Assert.True(set.Ok, set.Error);

        // The root's downloaded bytes go away after the plan was approved: its own write refuses.
        // (Nothing is in the pending folder yet, so this only reaches the downloaded copy.)
        int deleted = 0;
        foreach (string file in Directory.GetFiles(_clientRoot, "*AutoLatchGates*", SearchOption.AllDirectories))
        {
            File.Delete(file);
            deleted++;
        }

        Assert.True(deleted > 0, "the downloaded root package was not found, so this fact would pass for the wrong reason");

        var result = catalog.ExecuteWithDependencies(set);
        Assert.False(result.Ok);
        Assert.Equal(RootKey, result.FailedPackage);
        Assert.Equal(InstallReasons.ClosureMemberFailed(RootKey, InstallReasons.ChangedArtifact), result.Error);

        // The dependency is a real install: its file and its ledger row stay.
        var written = Assert.Single(result.Members);
        Assert.Equal(EcfKey, written.Package);
        string folder = Path.Combine(_clientRoot, ModManifestPending.PendingFolderName, "index");
        Assert.True(File.Exists(Path.Combine(folder, "ECF.dll")));
        Assert.False(File.Exists(Path.Combine(folder, "AutoLatchGates.dll")));
        Assert.Contains(OrdinaryDependencies.LedgerSource(EcfKey, "0.11.0"), ServerInstall.LedgerSources(_clientRoot));
        // Revert: rolling the dependency back would delete a package that is installed and ledgered;
        // continuing past the failure would leave the root installed without its own bytes verified.
    }

    [Fact]
    public async Task A_dependency_the_chosen_target_never_runs_is_installed_on_the_half_that_does()
    {
        var catalog = Catalog(
            true,
            (RootKey, "3.1.0", Zip(("AutoLatchGates.dll", _f.AutoLatchGates)), new[] { "dyno-ExampleCommandFramework-0.10.3" }),
            (EcfKey, "0.11.0", Zip(("ECF.dll", _f.ServerOnlyVcf)), Array.Empty<string>()));
        Assert.True((await catalog.DownloadAndInspectAsync(RootKey, CancellationToken.None)).Ok);

        var set = await catalog.PlanWithDependenciesAsync(RootKey, InstallTarget.Client, CancellationToken.None);
        Assert.True(set.Ok, set.Error);
        Assert.Equal(InstallTarget.LocalServer, set.Members[0].Target);
        Assert.Equal(InstallTarget.Client, set.Root!.Target);
        Assert.Equal(
            InstallReasons.ClosureMemberHalf("server", "local server"),
            set.Members[0].TargetReason);
        var row = Assert.Single(set.DependencyRows());
        Assert.Equal("localServer", row["target"]);
        Assert.Contains(InstallReasons.ClosureMemberHalf("server", "local server"), (string)row["reason"]!);

        var result = catalog.ExecuteWithDependencies(set);
        Assert.True(result.Ok, result.Error);
        Assert.True(File.Exists(Path.Combine(_serverRoot, ModManifestPending.PendingFolderName, "index", "ECF.dll")));
        Assert.False(File.Exists(Path.Combine(_clientRoot, ModManifestPending.PendingFolderName, "index", "ECF.dll")));
        Assert.True(File.Exists(Path.Combine(_clientRoot, ModManifestPending.PendingFolderName, "index", "AutoLatchGates.dll")));
        // Revert: planning the dependency for the root's own target refuses it with the INSTORD1
        // process mismatch, so the whole set would be refused instead of split across the halves.
    }

    // ---- remove -------------------------------------------------------------------------------

    [Fact]
    public async Task Remove_refuses_while_another_install_depends_on_the_package_and_force_removes_anyway()
    {
        var catalog = Catalog(
            (RootKey, "3.1.0", Zip(("AutoLatchGates.dll", _f.AutoLatchGates)), new[] { "dyno-ExampleCommandFramework-0.10.3" }),
            (EcfKey, "0.11.0", Zip(("ECF.dll", _f.Ecf)), Array.Empty<string>()));
        Assert.True((await catalog.DownloadAndInspectAsync(RootKey, CancellationToken.None)).Ok);
        var set = await catalog.PlanWithDependenciesAsync(RootKey, InstallTarget.Client, CancellationToken.None);
        Assert.True(catalog.ExecuteWithDependencies(set).Ok);
        string ecfFile = Path.Combine(_clientRoot, ModManifestPending.PendingFolderName, "index", "ECF.dll");

        Assert.Equal(new[] { RootKey }, catalog.DependentsOf(EcfKey));
        var refused = catalog.RemovePackage(EcfKey);
        Assert.False(refused.Ok);
        Assert.Equal(new[] { RootKey }, refused.Dependents);
        Assert.Equal(InstallReasons.RemoveHasDependents(EcfKey, new[] { RootKey }), refused.Message);
        Assert.Empty(refused.Removed);
        Assert.True(File.Exists(ecfFile));

        // The package that has the dependency is removable at any time; it breaks nothing.
        Assert.Empty(catalog.DependentsOf(RootKey));
        var root = catalog.RemovePackage(RootKey);
        Assert.True(root.Ok, root.Message);
        Assert.Empty(root.Dependents);

        // With its dependent gone the dependency removes without force; force removes it either way.
        Assert.Empty(catalog.DependentsOf(EcfKey));
        Assert.True(catalog.RemovePackage(EcfKey).Ok);
        Assert.False(File.Exists(ecfFile));
        // Revert: reading dependents from the package name instead of the ledger plus the index, or
        // deleting the files on the refused call, fails this.
    }

    [Fact]
    public async Task A_forced_removal_still_names_what_it_broke()
    {
        var catalog = Catalog(
            (RootKey, "3.1.0", Zip(("AutoLatchGates.dll", _f.AutoLatchGates)), new[] { "dyno-ExampleCommandFramework-0.10.3" }),
            (EcfKey, "0.11.0", Zip(("ECF.dll", _f.Ecf)), Array.Empty<string>()));
        Assert.True((await catalog.DownloadAndInspectAsync(RootKey, CancellationToken.None)).Ok);
        Assert.True(catalog.ExecuteWithDependencies(
            await catalog.PlanWithDependenciesAsync(RootKey, InstallTarget.Client, CancellationToken.None)).Ok);

        var forced = catalog.RemovePackage(EcfKey, force: true);
        Assert.True(forced.Ok, forced.Message);
        Assert.Equal(new[] { RootKey }, forced.Dependents);
        Assert.NotEmpty(forced.Removed);
        Assert.False(File.Exists(Path.Combine(_clientRoot, ModManifestPending.PendingFolderName, "index", "ECF.dll")));
        // The dependent is still installed, and now missing what it needs — which is why the
        // dependents list rides along on the forced result too.
        Assert.True(File.Exists(Path.Combine(_clientRoot, ModManifestPending.PendingFolderName, "index", "AutoLatchGates.dll")));
        // Revert: dropping the dependents from a forced result leaves the pane unable to say what broke.
    }

    // ---- the confirmation text ----------------------------------------------------------------

    [Fact]
    public async Task The_confirmation_body_names_the_side_the_author_the_version_and_each_dependency()
    {
        var catalog = Catalog(
            (RootKey, "3.1.0", Zip(("AutoLatchGates.dll", _f.AutoLatchGates)), new[] { "BepInEx-BepInExPack_V_Rising-1.733.0", "dyno-ExampleCommandFramework-0.10.3" }),
            (EcfKey, "0.11.0", Zip(("ECF.dll", _f.Ecf)), Array.Empty<string>()));
        Assert.True((await catalog.DownloadAndInspectAsync(RootKey, CancellationToken.None)).Ok);
        var set = await catalog.PlanWithDependenciesAsync(RootKey, InstallTarget.Client, CancellationToken.None);
        Assert.True(set.Ok, set.Error);

        Assert.Equal(
            "Client mod by Modix, version 3.1.0. Loads at the next restart of V Rising. "
                + "Needs ExampleCommandFramework 0.11.0, which will be installed too.",
            InstallSummary.Describe(set));

        // One sentence per dependency, and the loader pack is never one of them.
        Assert.Equal(1, Count(InstallSummary.Describe(set), "Needs "));
        // Revert: naming the owner in the dependency sentence, or the loader pack as a dependency,
        // changes this string.
    }

    [Fact]
    public void The_confirmation_body_says_which_is_installed_for_a_dependency_already_on_the_target()
    {
        var inspection = Inspect(("AutoLatchGates.dll", _f.AutoLatchGates));
        var entry = Listing("Modix", "AutoLatchGates", "3.1.0", "dyno-ExampleCommandFramework-0.10.3");
        var view = new OrdinaryDependencyView(new[] { OrdinaryDependencies.LedgerSource(EcfKey, "0.11.0") });
        var plan = InstallPlanner.Plan(entry, inspection, InstallTarget.Client, null, false, null, view);

        Assert.Equal(
            "Client mod by Modix, version 3.1.0. Loads at the next restart of V Rising. "
                + "Needs ExampleCommandFramework, which is installed.",
            InstallSummary.Describe(plan));

        var server = InstallPlanner.Plan(entry, inspection, InstallTarget.LocalServer, null, true, null, view);
        Assert.StartsWith("Server mod by Modix, version 3.1.0. Loads at the next restart of the local server.", InstallSummary.Describe(server));
        Assert.Equal("Needs ExampleCommandFramework 0.11.0, which will be installed too.", InstallSummary.Needs(EcfKey, "0.11.0", true));
        Assert.Equal("Loads in seconds.", InstallSummary.LoadsInSeconds);
        // Revert: spelling a satisfied dependency as one that will be installed misleads the y/n.
    }

    // ---- helpers ------------------------------------------------------------------------------

    private int _downloads;

    private static int Count(string text, string needle)
    {
        int n = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string[] Packages(DependencyClosureResult closure)
    {
        var list = new List<string>();
        for (int i = 0; i < closure.Members.Count; i++) list.Add(closure.Members[i].Package);
        return list.ToArray();
    }

    private static string[] MemberPackages(InstallPlanSet set)
    {
        var list = new List<string>();
        for (int i = 0; i < set.Members.Count; i++) list.Add(set.Members[i].Package);
        return list.ToArray();
    }

    private static string[] WrittenPackages(ExecuteSetResult result)
    {
        var list = new List<string>();
        for (int i = 0; i < result.Members.Count; i++) list.Add(result.Members[i].Package);
        return list.ToArray();
    }

    private static Func<string, CatalogEntry?> Lookup(Dictionary<string, CatalogEntry> index) =>
        package => index.TryGetValue(package, out var entry) ? entry : null;

    private static Dictionary<string, CatalogEntry> Index(params CatalogEntry[] entries)
    {
        var map = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < entries.Length; i++) map[entries[i].Key] = entries[i];
        return map;
    }

    private static CatalogEntry Listing(string owner, string name, string version, params string[] dependencies) =>
        new(
            new ManifestMod(
                "", version, NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact, false,
                owner, owner + "-" + name, null, 1, null),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "stamp",
            displayName: name,
            author: owner,
            owner: owner,
            packageName: name,
            foreignDependencies: dependencies);

    /// <summary>
    /// A real <see cref="InstallCatalogService"/> over a fake transport: the index is built from the
    /// packages named here and each download serves that package's zip. No network.
    /// </summary>
    private InstallCatalogService Catalog(
        params (string Key, string Version, string ZipPath, IReadOnlyList<string> Dependencies)[] packages) =>
        Catalog(false, packages);

    private InstallCatalogService Catalog(
        bool localServer,
        params (string Key, string Version, string ZipPath, IReadOnlyList<string> Dependencies)[] packages)
    {
        var bodies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var json = new StringBuilder("[");
        for (int i = 0; i < packages.Length; i++)
        {
            var package = packages[i];
            int dash = package.Key.IndexOf('-');
            string owner = package.Key.Substring(0, dash);
            string name = package.Key.Substring(dash + 1);
            string url = "https://thunderstore.io/package/download/" + owner + "/" + name + "/" + package.Version + "/";
            bodies[url] = File.ReadAllBytes(package.ZipPath);
            if (i > 0) json.Append(',');
            json.Append("{\"name\":\"").Append(name)
                .Append("\",\"full_name\":\"").Append(package.Key)
                .Append("\",\"owner\":\"").Append(owner)
                .Append("\",\"package_url\":\"https://thunderstore.io/c/v-rising/p/").Append(owner).Append('/').Append(name)
                .Append("/\",\"is_deprecated\":false,\"versions\":[{\"name\":\"").Append(name)
                .Append("\",\"full_name\":\"").Append(package.Key).Append('-').Append(package.Version)
                .Append("\",\"description\":\"d\",\"version_number\":\"").Append(package.Version)
                .Append("\",\"dependencies\":[");
            for (int d = 0; d < package.Dependencies.Count; d++)
            {
                if (d > 0) json.Append(',');
                json.Append('"').Append(package.Dependencies[d]).Append('"');
            }

            json.Append("],\"download_url\":\"").Append(url)
                .Append("\",\"file_size\":10,\"is_active\":true}]}");
        }

        json.Append(']');

        var transport = new ZipTransport(bodies, () => _downloads++);
        var catalog = new InstallCatalogService(
            _clientRoot,
            transport,
            new CatalogFetchOptions { Timeout = TimeSpan.FromSeconds(5) },
            new ImmediateClock(),
            localServerConfigured: localServer,
            serverBepInExRoot: localServer ? _serverRoot : null);
        Assert.True(catalog.LoadIndexFromJson(json.ToString(), "stamp").Ok);
        return catalog;
    }

    private PackageInspection Inspect(params (string Name, byte[] Body)[] entries) =>
        PackageInspector.InspectFile(Zip(entries));

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

    private sealed class ZipTransport : ICatalogTransport
    {
        private readonly Dictionary<string, byte[]> _bodies;
        private readonly Action _counted;

        public ZipTransport(Dictionary<string, byte[]> bodies, Action counted)
        {
            _bodies = bodies;
            _counted = counted;
        }

        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken)
        {
            string url = request.Uri.ToString();
            if (_bodies.TryGetValue(url, out var body))
            {
                _counted();
                return Task.FromResult(new CatalogTransportResponse(200, new MemoryStream(body)));
            }

            return Task.FromResult(new CatalogTransportResponse(404, new MemoryStream(Array.Empty<byte>())));
        }
    }

    private sealed class ImmediateClock : ICatalogClock
    {
        public DateTime UtcNow => new(2026, 9, 13, 9, 0, 0, DateTimeKind.Utc);

        public Task Delay(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
