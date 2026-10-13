using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Progenitor.Bootstrap;
using Progenitor.Install;
using Progenitor.Install.Admission;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FINJ1: reload-folder plugins that register their own IL2CPP types are process-lifetime.
/// Observed live 2026-10-06: a Core swap cascaded a reload of ChatSkin, its second
/// <c>RegisterTypeInIl2Cpp</c> threw "already injected" and chat stopped opening until a restart.
/// Game-free: images are compiled with Roslyn or planted with <see cref="MetadataBuilder"/>.
/// </summary>
public class Il2CppInjectionPolicyTests
{
    // ---- the scan ----------------------------------------------------------------------------

    private const string InteropStub = @"
namespace Il2CppInterop.Runtime.Injection
{
    public static class ClassInjector
    {
        public static void RegisterTypeInIl2Cpp<T>() where T : class { }
        public static void RegisterTypeInIl2Cpp(System.Type t) { }
        public static bool IsTypeRegisteredInIl2Cpp<T>() where T : class => false;
    }
}";

    private const string CoreStub = "namespace Progenitor { public static class Api { public static int Value => 1; } }";

    private static readonly List<MetadataReference> Runtime = LoadRuntime();

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

    private static readonly byte[] Interop = Compile("Il2CppInterop.Runtime", InteropStub);
    private static readonly byte[] Core = Compile("Progenitor", CoreStub);

    [Fact]
    public void A_plugin_that_calls_RegisterTypeInIl2Cpp_like_ChatSkin_is_detected()
    {
        // The ChatSkin shape: a canvas setup that injects its own driver type, no Progenitor reference.
        var image = Compile("ChatSkin", @"
namespace ChatSkin.Behaviors { public class UpdateDriver { } }
namespace ChatSkin
{
    public static class Canvas
    {
        public static void Setup()
        {
            if (!Il2CppInterop.Runtime.Injection.ClassInjector.IsTypeRegisteredInIl2Cpp<Behaviors.UpdateDriver>())
                Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<Behaviors.UpdateDriver>();
        }
    }
}", Interop);
        var scan = Il2CppInjectionPolicy.Scan(image);
        Assert.Equal("", scan.Error);
        Assert.True(scan.Injects);
        Assert.Equal(new[] { "Il2CppInterop.Runtime.Injection.ClassInjector::RegisterTypeInIl2Cpp" }, scan.Sites);
        Assert.False(scan.CoreBound);
        Assert.Equal("ChatSkin", scan.AssemblyName);
        // Revert: matching only the non-generic overload misses the generic call, which goes through a MethodSpec.
    }

    [Fact]
    public void A_plugin_that_references_Progenitor_is_core_bound()
    {
        var image = Compile("Bound", @"
public class Driver { }
public static class Setup
{
    public static int Go(System.Type t)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp(t);
        return Progenitor.Api.Value;
    }
}", Interop, Core);
        var scan = Il2CppInjectionPolicy.Scan(image);
        Assert.True(scan.Injects);
        Assert.True(scan.CoreBound);
    }

    [Fact]
    public void A_plugin_that_only_asks_whether_a_type_is_registered_is_not_marked()
    {
        var image = Compile("Asker", @"
public class Driver { }
public static class Setup
{
    public static bool Go() => Il2CppInterop.Runtime.Injection.ClassInjector.IsTypeRegisteredInIl2Cpp<Driver>();
}", Interop, Core);
        var scan = Il2CppInjectionPolicy.Scan(image);
        Assert.False(scan.Injects);
    }

    [Theory]
    [InlineData("Il2CppInterop.Runtime.Injection", "ClassInjector", "RegisterTypeInIl2CppWithInterfaces", true)]
    [InlineData("BepInEx.Unity.IL2CPP", "BasePlugin", "AddComponent", true)]
    [InlineData("BepInEx.Unity.IL2CPP", "IL2CPPChainloader", "AddUnityComponent", true)]
    [InlineData("Il2CppInterop.Runtime.Injection", "ClassInjector", "IsTypeRegisteredInIl2Cpp", false)]
    [InlineData("Some.Other", "ClassInjector", "RegisterTypeInIl2Cpp", false)]
    [InlineData("UnityEngine", "GameObject", "AddComponent", false)]
    public void Helper_mediated_injection_sites_are_detected_and_lookalikes_are_not(string ns, string type, string member, bool injects)
    {
        var scan = Il2CppInjectionPolicy.Scan(Planted((ns, type, member)));
        Assert.Equal(injects, scan.Injects);
        if (injects) Assert.Equal(ns + "." + type + "::" + member, Assert.Single(scan.Sites));
    }

    [Fact]
    public void An_unreadable_image_reports_an_error_and_no_sites()
    {
        var scan = Il2CppInjectionPolicy.Scan(new byte[] { 1, 2, 3, 4 });
        Assert.False(scan.Injects);
        Assert.NotEqual("", scan.Error);
        Assert.Same(Il2CppInjectionScan.None, Il2CppInjectionPolicy.Scan(null));
    }

    // ---- runtime detection ---------------------------------------------------------------------

    [Fact]
    public void Runtime_match_claims_only_names_the_plugins_own_assembly_defines()
    {
        // A sample in the Progenitor namespace is still a plugin: no name prefix is exempt.
        var mine = new HashSet<string> { "ChatSkin.Behaviors.UpdateDriver", "Progenitor.Sample.Driver" };
        var hits = Il2CppInjectionPolicy.MatchInjected(
            new[] { "Progenitor.Ticks.FrameTicker", "Other.Mod.Driver", "ChatSkin.Behaviors.UpdateDriver", "Progenitor.Sample.Driver", null, "" },
            mine.Contains);
        Assert.Equal(new[] { "ChatSkin.Behaviors.UpdateDriver", "Progenitor.Sample.Driver" }, hits);
        Assert.Empty(Il2CppInjectionPolicy.MatchInjected(new[] { "X" }, _ => throw new InvalidOperationException()));
        Assert.Empty(Il2CppInjectionPolicy.MatchInjected(null, mine.Contains));
    }

    // ---- decisions -----------------------------------------------------------------------------

    [Theory]
    [InlineData((int)LifetimeEvent.FileChanged, false, false, (int)LifetimeAction.Proceed)]
    [InlineData((int)LifetimeEvent.CoreSwap, false, true, (int)LifetimeAction.Proceed)]
    [InlineData((int)LifetimeEvent.FileChanged, true, false, (int)LifetimeAction.KeepRunning)]
    [InlineData((int)LifetimeEvent.FileChanged, true, true, (int)LifetimeAction.KeepRunning)]
    [InlineData((int)LifetimeEvent.FileRemoved, true, false, (int)LifetimeAction.KeepRunning)]
    [InlineData((int)LifetimeEvent.CoreSwap, true, false, (int)LifetimeAction.KeepRunning)]
    [InlineData((int)LifetimeEvent.CoreSwap, true, true, (int)LifetimeAction.Refuse)]
    [InlineData((int)LifetimeEvent.Suspend, true, false, (int)LifetimeAction.Refuse)]
    [InlineData((int)LifetimeEvent.Shutdown, true, true, (int)LifetimeAction.Proceed)]
    public void A_process_lifetime_plugin_is_never_unloaded_in_place(int ev, bool lifetime, bool coreBound, int expected) =>
        Assert.Equal((LifetimeAction)expected, Il2CppInjectionPolicy.Decide((LifetimeEvent)ev, lifetime, coreBound));

    [Fact]
    public void Pending_follows_the_file_on_disk()
    {
        var loaded = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(Il2CppInjectionPolicy.PendingNone, Il2CppInjectionPolicy.PendingFor(true, loaded, loaded));
        Assert.Equal(Il2CppInjectionPolicy.PendingChanged, Il2CppInjectionPolicy.PendingFor(true, loaded, loaded.AddSeconds(5)));
        Assert.Equal(Il2CppInjectionPolicy.PendingRemoved, Il2CppInjectionPolicy.PendingFor(false, loaded, loaded));
    }

    [Fact]
    public void The_core_swap_refusal_parks_the_watcher_like_a_generation_that_did_not_collect()
    {
        string text = Il2CppInjectionPolicy.RefuseCoreSwap(new[] { "Bound.dll" });
        Assert.StartsWith("Core reload refused:", text);
        Assert.Contains(Il2CppInjectionPolicy.RefusalMarker, text);
        Assert.Contains("Bound.dll", text);
        Assert.True(CoreWatchPolicy.ParkOnRefusal(text));
        // Revert: wording it as a "plugin ALC" refusal makes the watcher retry it forever.
        Assert.False(CoreWatchPolicy.HoldOnRefusal(text));
    }

    [Fact]
    public void The_orchestrator_refuses_before_freezing_when_a_core_bound_lifetime_plugin_is_loaded()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 2, CurrentAlcName = "progenitor-core:2" };
        ops.LifetimeBound.Add("Bound.dll");
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.True(result.Refused);
        Assert.True(result.OldGenerationRunning);
        Assert.Contains(Il2CppInjectionPolicy.RefusalMarker, result.Summary);
        Assert.Empty(ops.Steps); // nothing frozen, no plugin unloaded

        var free = new FakeReloadOps { CurrentGeneration = 2, CurrentAlcName = "progenitor-core:2" };
        Assert.False(CoreReloadOrchestrator.Run(free, "bridge").Refused);
    }

    [Fact]
    public void A_pause_that_reaches_an_injecting_plugin_is_refused_whole()
    {
        var nodes = new[]
        {
            new SuspendUnitNode("a.dll", new[] { "a" }, null, false),
            new SuspendUnitNode("chat.dll", new[] { "chat" }, new[] { "a" }, false, injectsIl2CppTypes: true),
        };
        Assert.False(PluginSuspendPolicy.TryClosure(nodes, new[] { "a" }, out var selected, out var refusal));
        Assert.Empty(selected);
        Assert.Equal(Il2CppInjectionPolicy.FormatSuspendRefused("chat"), refusal);
        Assert.True(PluginSuspendPolicy.TryClosure(nodes.Take(1).ToArray(), new[] { "a" }, out _, out _));
    }

    // ---- what Core and the bridge read ---------------------------------------------------------

    private static string SampleRow(string pending) => Il2CppInjectionPolicy.FormatRow(new Il2CppInjectionPolicy.Row(
        "ChatSkin.dll", new[] { "chatskin" }, new[] { "ChatSkin" }, false, pending,
        new[] { "Il2CppInterop.Runtime.Injection.ClassInjector::RegisterTypeInIl2Cpp", "Chat|Skin\tDriver" }));

    [Fact]
    public void The_row_round_trips_and_Cores_mirror_reads_the_same_fields()
    {
        string text = SampleRow(Il2CppInjectionPolicy.PendingChanged);
        Assert.True(Il2CppInjectionPolicy.TryParseRow(text, out var row));
        Assert.True(ProcessLifetimeRow.TryParse(text, out var mirror));
        Assert.Equal("ChatSkin.dll", row.File);
        Assert.Equal(row.File, mirror!.File);
        Assert.Equal(row.Guids, mirror.Guids);
        Assert.Equal(row.Names, mirror.Names);
        Assert.Equal(row.CoreBound, mirror.CoreBound);
        Assert.Equal(row.Pending, mirror.Pending);
        Assert.Equal(row.Sites, mirror.Sites);
        Assert.Equal(2, row.Sites.Length); // separators inside a value are cleaned, never split it
        Assert.Equal(Il2CppInjectionPolicy.PendingChanged, ProcessLifetimeRow.PendingChanged);
        Assert.Equal(Il2CppInjectionPolicy.PendingRemoved, ProcessLifetimeRow.PendingRemoved);
        Assert.False(ProcessLifetimeRow.TryParse("garbage", out _));
    }

    [Fact]
    public void Reload_status_lists_process_lifetime_plugins()
    {
        try
        {
            CoreReloadStatus.SetProcessLifetimePlugins(new[] { SampleRow(Il2CppInjectionPolicy.PendingRemoved), "bad row" });
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(CoreReloadStatus.ToDump(3, "progenitor-core:3", 5)));
            var list = doc.RootElement.GetProperty("processLifetimePlugins");
            var entry = Assert.Single(list.EnumerateArray());
            Assert.Equal("ChatSkin.dll", entry.GetProperty("file").GetString());
            Assert.Equal("removed", entry.GetProperty("restartPending").GetString());
            Assert.False(entry.GetProperty("coreBound").GetBoolean());
            Assert.Equal("chatskin", entry.GetProperty("guids")[0].GetString());
        }
        finally
        {
            CoreReloadStatus.SetProcessLifetimePlugins(null);
        }
    }

    [Fact]
    public void The_Mods_tab_shows_restart_required_while_a_change_waits_and_a_note_otherwise()
    {
        var pending = new ProcessLifetimeSource(() => new[] { SampleRow(Il2CppInjectionPolicy.PendingChanged) }).Current().ToList();
        var warn = Assert.Single(pending);
        Assert.Equal(ModProblemSeverity.Warning, warn.Severity);
        Assert.Equal(ModProblemKinds.RestartRequired, warn.Kind);
        Assert.Equal("chatskin", warn.ModId);
        Assert.Equal(ProcessLifetimeSource.ListLineRestart, warn.ShortLine);
        Assert.Equal(ProcessLifetimeSource.RestartWhatToDo, warn.WhatToDo);

        var removed = Assert.Single(new ProcessLifetimeSource(() => new[] { SampleRow(Il2CppInjectionPolicy.PendingRemoved) }).Current());
        Assert.Equal(ProcessLifetimeSource.RemovedSentence, removed.Sentence);

        var quiet = Assert.Single(new ProcessLifetimeSource(() => new[] { SampleRow("") }).Current());
        Assert.Equal(ModProblemSeverity.Info, quiet.Severity);
        Assert.Equal(ProcessLifetimeSource.ListLineNote, quiet.ShortLine);

        Assert.Empty(new ProcessLifetimeSource(() => throw new InvalidOperationException("older Bootstrap")).Current());
    }

    [Fact]
    public void Admission_refuses_a_hot_replacement_of_a_process_lifetime_plugin_under_its_own_reason()
    {
        var values = new BootstrapSnapshotValues(false, 4, false, false, true, "ChatSkin", "1.0.0", "", 0,
            false, true, "Absent", @"C:\x\BepInEx\progenitor-reload", ProcessLifetimeMarked: true);
        var snap = BootstrapAdmissionSnapshotSource.Compose(values, "chatskin", null);
        Assert.NotNull(snap.CurrentPlugin);
        Assert.True(snap.CurrentPlugin!.ProcessLifetimeMarked);
        Assert.False(snap.CurrentPlugin.UnsafeHarmonyMarked);
        Assert.Contains("IL2CPP types", AdmissionReasonText.For(AdmissionReasonCode.ProcessLifetimeMarked));
        Assert.Contains("IL2CPP types", ModVersionRouteText.ProcessLifetime);
    }

    /// <summary>A minimal library image whose MemberRef table holds exactly <paramref name="refs"/>.</summary>
    private static byte[] Planted(params (string Ns, string Type, string Member)[] refs)
    {
        var md = new MetadataBuilder();
        md.AddModule(0, md.GetOrAddString("planted.dll"), md.GetOrAddGuid(Guid.NewGuid()), default, default);
        md.AddAssembly(md.GetOrAddString("planted"), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var scope = md.AddAssemblyReference(md.GetOrAddString("External"), new Version(1, 0, 0, 0), default, default, 0, default);
        var signature = new BlobBuilder();
        new BlobEncoder(signature).MethodSignature().Parameters(0, r => r.Void(), _ => { });
        var sig = md.GetOrAddBlob(signature);
        md.AddTypeDefinition(
            default, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        foreach (var (ns, type, member) in refs)
        {
            var typeRef = md.AddTypeReference(scope, md.GetOrAddString(ns), md.GetOrAddString(type));
            md.AddMemberReference(typeRef, md.GetOrAddString(member), sig);
        }
        var pe = new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(md), new BlobBuilder());
        var output = new BlobBuilder();
        pe.Serialize(output);
        return output.ToArray();
    }
}
