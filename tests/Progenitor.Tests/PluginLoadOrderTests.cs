using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Progenitor.Bootstrap;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FORD1: dependency-aware load order for the reload folder. Live 2026-10-06 on the test server:
/// AutoLatchGates.dll (<c>[BepInDependency("gg.dyno.ExampleCommandFramework")]</c>, references the
/// ExampleCommandFramework assembly) loaded before ExampleCommandFramework.dll in file-name order and failed
/// with FileNotFoundException, and its context could not have resolved the sibling assembly anyway.
/// Images are compiled with Roslyn against a stub BepInEx, so the attributes are read the way BepInEx emits them.
/// </summary>
public class PluginLoadOrderTests
{
    private const string BepInExStub = @"
namespace BepInEx
{
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public class BepInPlugin : System.Attribute { public BepInPlugin(string guid, string name, string version) { } }
    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
    public class BepInDependency : System.Attribute
    {
        public enum DependencyFlags { HardDependency = 1, SoftDependency = 2 }
        public BepInDependency(string guid) { }
        public BepInDependency(string guid, DependencyFlags flags) { }
        public BepInDependency(string guid, string minimumVersion) { }
    }
}";

    private static readonly List<MetadataReference> Runtime = LoadRuntime();
    private static readonly byte[] BepIn = Compile("BepInEx.Core", BepInExStub);

    private static List<MetadataReference> LoadRuntime()
    {
        var list = new List<MetadataReference>();
        foreach (var path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
            if (path.Length > 0 && File.Exists(path)) list.Add(MetadataReference.CreateFromFile(path));
        return list;
    }

    private static byte[] Compile(string name, string source, params byte[][] refs)
    {
        var references = new List<MetadataReference>(Runtime);
        foreach (var r in refs) references.Add(MetadataReference.CreateFromImage(r));
        var compilation = CSharpCompilation.Create(
            name, new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        return ms.ToArray();
    }

    private static readonly byte[] Ecf = Compile("ExampleCommandFramework", @"
namespace ExampleCommandFramework
{
    [BepInEx.BepInPlugin(""gg.dyno.ExampleCommandFramework"", ""ExampleCommandFramework"", ""0.10.3"")]
    public class Plugin { }
    public static class CommandRegistry { public static int Register(string name) => name.Length; }
}", BepIn);

    private static readonly byte[] Doors = Compile("AutoLatchGates", @"
namespace AutoLatchGates
{
    [BepInEx.BepInPlugin(""io.modix.AutoLatchGates"", ""AutoLatchGates"", ""1.0.0"")]
    [BepInEx.BepInDependency(""gg.dyno.ExampleCommandFramework"")]
    public class Plugin { public static int Load() => ExampleCommandFramework.CommandRegistry.Register(""doors""); }
}", BepIn, Ecf);

    private static PluginImageInfo Info(string file, string asm, string[]? guids = null, PluginDependencyRef[]? deps = null, string[]? refs = null) =>
        new(file, asm, guids ?? Array.Empty<string>(), deps ?? Array.Empty<PluginDependencyRef>(), refs ?? Array.Empty<string>());

    private static PluginDependencyRef Hard(string g) => new(g, false);
    private static PluginDependencyRef Soft(string g) => new(g, true);

    private static string[] Order(IReadOnlyList<PluginImageInfo> pending, List<PluginLoadStep> plan) =>
        plan.Where(s => !s.Waiting).Select(s => pending[s.Index].File).ToArray();

    // ---- reading -------------------------------------------------------------------------------

    [Fact]
    public void The_metadata_read_finds_guids_dependencies_and_assembly_references()
    {
        var doors = PluginLoadOrder.Read("AutoLatchGates.dll", Doors);
        Assert.Equal("", doors.Error);
        Assert.Equal("AutoLatchGates", doors.AssemblyName);
        Assert.Equal(new[] { "io.modix.AutoLatchGates" }, doors.Guids);
        var dep = Assert.Single(doors.Dependencies);
        Assert.Equal("gg.dyno.ExampleCommandFramework", dep.Guid);
        Assert.False(dep.Soft);
        Assert.Contains("ExampleCommandFramework", doors.AssemblyRefs);
    }

    [Fact]
    public void Soft_flags_and_version_overloads_are_decoded()
    {
        var image = Compile("Flags", @"
[BepInEx.BepInPlugin(""flags"", ""Flags"", ""1.0.0"")]
[BepInEx.BepInDependency(""soft.one"", BepInEx.BepInDependency.DependencyFlags.SoftDependency)]
[BepInEx.BepInDependency(""hard.flag"", BepInEx.BepInDependency.DependencyFlags.HardDependency)]
[BepInEx.BepInDependency(""versioned"", ""1.2.0"")]
public class P { }", BepIn);
        var info = PluginLoadOrder.Read("Flags.dll", image);
        Assert.True(info.Dependencies.Single(d => d.Guid == "soft.one").Soft);
        Assert.False(info.Dependencies.Single(d => d.Guid == "hard.flag").Soft);
        Assert.False(info.Dependencies.Single(d => d.Guid == "versioned").Soft);
        Assert.NotEqual("", PluginLoadOrder.Read("x.dll", new byte[] { 1, 2, 3 }).Error);
    }

    // ---- planning ------------------------------------------------------------------------------

    [Fact]
    public void The_live_case_loads_ExampleCommandFramework_before_AutoLatchGates()
    {
        var pending = new[]
        {
            PluginLoadOrder.Read("AutoLatchGates.dll", Doors),
            PluginLoadOrder.Read("ExampleCommandFramework.dll", Ecf),
        };
        var plan = PluginLoadOrder.Plan(pending, new[] { "progenitor" }, null, externalKnown: true);
        Assert.Equal(new[] { "ExampleCommandFramework.dll", "AutoLatchGates.dll" }, Order(pending, plan));
        Assert.All(plan, s => Assert.False(s.Waiting));
        // The provider is named, so a provider that fails turns the dependent into a wait.
        Assert.Equal(new[] { 1 }, plan.Single(s => s.Index == 0).HardProviders);
        // Revert: file-name order puts AutoLatchGates first, the live failure.
    }

    [Fact]
    public void A_folder_without_dependencies_keeps_file_name_order()
    {
        var pending = new[] { Info("c.dll", "C"), Info("A.dll", "A"), Info("b.dll", "B") };
        Assert.Equal(new[] { "A.dll", "b.dll", "c.dll" }, Order(pending, PluginLoadOrder.Plan(pending, null, null, true)));
    }

    [Fact]
    public void A_missing_hard_dependency_waits_and_so_does_whatever_needs_the_waiter()
    {
        var pending = new[]
        {
            Info("Doors.dll", "Doors", new[] { "doors" }, new[] { Hard("gg.dyno.ExampleCommandFramework") }),
            Info("Extra.dll", "Extra", new[] { "extra" }, new[] { Hard("doors") }),
            Info("Free.dll", "Free", new[] { "free" }),
        };
        var plan = PluginLoadOrder.Plan(pending, new[] { "progenitor" }, null, externalKnown: true);
        Assert.Equal(new[] { "Free.dll" }, Order(pending, plan));
        var doors = plan.Single(s => s.Index == 0);
        Assert.Equal("gg.dyno.ExampleCommandFramework", doors.WaitingFor);
        Assert.Contains("is waiting for gg.dyno.ExampleCommandFramework", doors.Reason);
        var extra = plan.Single(s => s.Index == 1);
        Assert.Equal("Doors.dll", extra.WaitingFor);
        Assert.Contains("itself waiting for gg.dyno.ExampleCommandFramework", extra.Reason);
    }

    [Fact]
    public void Satisfied_soft_and_unknown_dependencies_never_wait()
    {
        var pending = new[]
        {
            Info("A.dll", "A", new[] { "a" }, new[] { Hard("progenitor"), Hard("chain.plugin"), Soft("absent.soft") }),
        };
        // progenitor and a chainloader plugin are loaded; a soft dependency that is absent is no wait.
        Assert.False(PluginLoadOrder.Plan(pending, new[] { "progenitor", "chain.plugin" }, null, true).Single().Waiting);
        // The chainloader list could not be read: fail open, as before this change.
        var unknown = new[] { Info("B.dll", "B", new[] { "b" }, new[] { Hard("not.listed") }) };
        Assert.False(PluginLoadOrder.Plan(unknown, null, null, externalKnown: false).Single().Waiting);
        Assert.True(PluginLoadOrder.Plan(unknown, null, null, externalKnown: true).Single().Waiting);
    }

    [Fact]
    public void A_soft_dependency_orders_without_making_anything_wait()
    {
        var pending = new[]
        {
            Info("A.dll", "A", new[] { "a" }, new[] { Soft("z") }),
            Info("Z.dll", "Z", new[] { "z" }),
        };
        Assert.Equal(new[] { "Z.dll", "A.dll" }, Order(pending, PluginLoadOrder.Plan(pending, null, null, true)));
        Assert.Empty(PluginLoadOrder.Plan(pending, null, null, true).Single(s => s.Index == 0).HardProviders);
    }

    [Fact]
    public void An_assembly_reference_alone_orders_a_library_dll_first()
    {
        var pending = new[]
        {
            Info("App.dll", "App", new[] { "app" }, refs: new[] { "SharedLib", "System.Runtime" }),
            Info("SharedLib.dll", "SharedLib"), // no [BepInPlugin]: a helper library dropped in the folder
        };
        Assert.Equal(new[] { "SharedLib.dll", "App.dll" }, Order(pending, PluginLoadOrder.Plan(pending, null, null, true)));
        // Already loaded by an earlier scan: nothing to order against, nothing to wait for.
        var later = new[] { Info("App.dll", "App", new[] { "app" }, refs: new[] { "SharedLib" }) };
        Assert.False(PluginLoadOrder.Plan(later, null, new[] { "SharedLib" }, true).Single().Waiting);
    }

    [Fact]
    public void A_cycle_loads_in_file_name_order_and_its_dependents_still_come_after_it()
    {
        var pending = new[]
        {
            Info("A.dll", "A", new[] { "a" }, new[] { Hard("c") }),       // A depends on the cycle
            Info("B.dll", "B", new[] { "b" }, new[] { Hard("c") }),
            Info("C.dll", "C", new[] { "c" }, new[] { Hard("b") }),
        };
        var plan = PluginLoadOrder.Plan(pending, null, null, true);
        var order = Order(pending, plan);
        Assert.Equal(3, order.Length);
        Assert.True(Array.IndexOf(order, "A.dll") > Array.IndexOf(order, "C.dll"), string.Join(",", order));
        Assert.True(Array.IndexOf(order, "A.dll") > Array.IndexOf(order, "B.dll"), string.Join(",", order));
        Assert.Contains(plan, s => s.InCycle);
        Assert.DoesNotContain(plan, s => s.InCycle && pending[s.Index].File == "A.dll");
    }

    // ---- what unloads with what ---------------------------------------------------------------

    [Fact]
    public void Dependents_follow_hard_and_assembly_edges_transitively_but_not_soft_ones()
    {
        var all = new[]
        {
            Info("Ecf.dll", "ExampleCommandFramework", new[] { "ecf" }),
            Info("Doors.dll", "Doors", new[] { "doors" }, refs: new[] { "ExampleCommandFramework" }),
            Info("Extra.dll", "Extra", new[] { "extra" }, new[] { Hard("doors") }),
            Info("Optional.dll", "Optional", new[] { "optional" }, new[] { Soft("ecf") }),
        };
        var closure = PluginLoadOrder.DependentClosure(all, new[] { 0 });
        Assert.Equal(new[] { 0, 1, 2 }, closure);
        Assert.True(PluginLoadOrder.NeedsAtRuntime(all[1], all[0]));
        Assert.False(PluginLoadOrder.NeedsAtRuntime(all[3], all[0]));
        Assert.False(PluginLoadOrder.NeedsAtRuntime(all[0], all[0]));
    }

    [Fact]
    public void Unload_order_puts_a_library_dependent_before_its_provider_through_the_assembly_key()
    {
        // DescribeNode keys a GUID-less library as "asm:<name>"; the dependent lists that key.
        var nodes = new[]
        {
            new PluginUnloadOrder.Node("asm:SharedLib", null, 0),
            new PluginUnloadOrder.Node("app", new[] { "asm:SharedLib" }, 1),
        };
        Assert.Equal(new[] { 1, 0 }, PluginUnloadOrder.ReverseTopological(nodes));
    }

    // ---- the runtime mechanism, in plain .NET ---------------------------------------------------

    [Fact]
    public void A_collectible_context_can_bind_a_sibling_contexts_assembly_and_both_collect_dependent_first()
    {
        var (doorsWeak, ecfWeak, result) = LoadPairAndUnload();
        Assert.Equal("doors".Length, result);
        // A provider's LoaderAllocator is released a few collections after its dependent's, not in the same one.
        for (int i = 0; i < 50 && (doorsWeak.IsAlive || ecfWeak.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            System.Threading.Thread.Sleep(5);
        }
        Assert.False(doorsWeak.IsAlive, "the dependent context did not collect");
        Assert.False(ecfWeak.IsAlive, "the provider context did not collect after its dependent");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference, WeakReference, int) LoadPairAndUnload()
    {
        // As in ReloaderHost: each context's Resolving looks the sibling up instead of capturing it, and
        // every handler is detached before Unload. A handler that holds its own context's assembly pins
        // that context for good (the reason ReloaderHost.Unload detaches it first).
        var loaded = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var bepCtx = new AssemblyLoadContext("bep", isCollectible: true);
        loaded["BepInEx.Core"] = bepCtx.LoadFromStream(new MemoryStream(BepIn));
        Func<AssemblyLoadContext, AssemblyName, Assembly?> resolve = (_, n) => n.Name != null && loaded.TryGetValue(n.Name, out var a) ? a : null;
        var ecfCtx = new AssemblyLoadContext("progenitor-reload:ExampleCommandFramework:1", isCollectible: true);
        ecfCtx.Resolving += resolve;
        loaded["ExampleCommandFramework"] = ecfCtx.LoadFromStream(new MemoryStream(Ecf));
        var doorsCtx = new AssemblyLoadContext("progenitor-reload:AutoLatchGates:1", isCollectible: true);
        doorsCtx.Resolving += resolve;
        var doors = doorsCtx.LoadFromStream(new MemoryStream(Doors));
        int result = (int)doors.GetType("AutoLatchGates.Plugin")!.GetMethod("Load")!.Invoke(null, null)!;
        var weakDoors = new WeakReference(doorsCtx);
        var weakVcf = new WeakReference(ecfCtx);
        // Dependent first, then its provider, as the scan's unload order does.
        doorsCtx.Resolving -= resolve;
        doorsCtx.Unload();
        ecfCtx.Resolving -= resolve;
        loaded.Clear();
        ecfCtx.Unload();
        bepCtx.Unload();
        return (weakDoors, weakVcf, result);
    }

    // ---- what Core and the bridge read ---------------------------------------------------------

    private static string WaitingRow() => PluginLoadOrder.FormatWaitingRow(
        "AutoLatchGates.dll", new[] { "io.modix.AutoLatchGates" }, "gg.dyno.ExampleCommandFramework",
        PluginLoadOrder.FormatWaitingMissing("AutoLatchGates.dll", "gg.dyno.ExampleCommandFramework"));

    [Fact]
    public void Waiting_plugins_reach_reload_status_and_the_Mods_tab()
    {
        Assert.True(WaitingPluginRow.TryParse(WaitingRow(), out var row));
        Assert.Equal("AutoLatchGates.dll", row!.File);
        Assert.Equal(new[] { "io.modix.AutoLatchGates" }, row.Guids);
        Assert.Equal("gg.dyno.ExampleCommandFramework", row.WaitingFor);
        Assert.False(WaitingPluginRow.TryParse("garbage", out _));

        var problem = Assert.Single(new WaitingPluginSource(() => new[] { WaitingRow(), "bad" }).Current());
        Assert.Equal(ModProblemSeverity.Warning, problem.Severity);
        Assert.Equal(ModProblemKinds.DependencyMissing, problem.Kind);
        Assert.Equal("io.modix.AutoLatchGates", problem.ModId);
        Assert.Equal(WaitingPluginSource.ListLine, problem.ShortLine);
        Assert.Contains("gg.dyno.ExampleCommandFramework", problem.Sentence);

        try
        {
            CoreReloadStatus.SetWaitingPlugins(new[] { WaitingRow() });
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(CoreReloadStatus.ToDump(1, null, 5)));
            var entry = Assert.Single(doc.RootElement.GetProperty("waitingPlugins").EnumerateArray());
            Assert.Equal("gg.dyno.ExampleCommandFramework", entry.GetProperty("waitingFor").GetString());
            Assert.Equal("io.modix.AutoLatchGates", entry.GetProperty("guids")[0].GetString());
        }
        finally
        {
            CoreReloadStatus.SetWaitingPlugins(null);
        }
    }
}
