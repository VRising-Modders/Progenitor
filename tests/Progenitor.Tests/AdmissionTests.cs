using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Progenitor.Install;
using Progenitor.Install.Admission;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job ADMIT1 fixtures: tiny assemblies compiled by Roslyn into byte arrays, once per test class.
/// Nothing here is ever loaded, reflected over or executed; the scanner reads bytes. The fake
/// framework assemblies carry only the names the scanner keys on (<c>BepInEx.BepInPlugin</c>,
/// <c>Progenitor.Plugins.PluginContext.UnsafeHarmony</c>, <c>HarmonyLib</c>,
/// <c>Il2CppInterop.Runtime.Injection.ClassInjector</c>, <c>ProjectM.Health</c>,
/// <c>Unity.Collections.NativeArray`1</c>, <c>Unity.Entities.EntityManager</c>).
/// </summary>
public sealed class AdmissionFixtures
{
    public const string SampleId = "example.sample";
    public const string SampleVersion = "1.0.0";

    private readonly List<MetadataReference> _runtime = new();
    private readonly Dictionary<string, byte[]> _framework = new(StringComparer.Ordinal);

    public string SentinelPath { get; }

    public byte[] Valid { get; }
    public byte[] ManagedGenericControl { get; }
    public byte[] UnsafeDirect { get; }
    public byte[] UnsafeViaHelperMain { get; }
    public byte[] UnsafeHelperDependency { get; }
    public byte[] CleanHelperDependency { get; }
    public byte[] ClassInjector { get; }
    public byte[] PInvoke { get; }
    public byte[] Calli { get; }
    public byte[] ReflectionEscape { get; }
    public byte[] MakeGenericTypeEscape { get; }
    public byte[] UnresolvedDependencyMain { get; }
    public byte[] UnresolvedBaseMain { get; }
    public byte[] ContosoHelpers { get; }
    public byte[] TwoEntryPoints { get; }
    public byte[] NoEntryPoint { get; }
    public byte[] Sentinel { get; }
    public byte[] InteropGenerics { get; }
    public byte[] ServerSide { get; }
    public byte[] HarmonyDirect { get; }

    public AdmissionFixtures()
    {
        foreach (var path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
        {
            if (path.Length > 0 && File.Exists(path))
                _runtime.Add(MetadataReference.CreateFromFile(path));
        }

        Framework("BepInEx.Core", @"
namespace BepInEx {
    public class BepInPlugin : System.Attribute { public BepInPlugin(string guid, string name, string version) { } }
    public class BepInDependency : System.Attribute { public BepInDependency(string guid) { } }
}");
        Framework("BepInEx.Unity.IL2CPP", @"
namespace BepInEx.Unity.IL2CPP { public abstract class BasePlugin { public abstract void Load(); } }", "BepInEx.Core");
        Framework("Progenitor", @"
namespace Progenitor.Plugins {
    public enum Side { Server = 1, Client = 2, Both = 3 }
    public sealed class ProgenitorSideAttribute : System.Attribute { public ProgenitorSideAttribute(Side side) { Side = side; } public Side Side { get; } }
    public class PluginContext { public object UnsafeHarmony => new object(); public object Log => new object(); }
    public abstract class ProgenitorPlugin<TContext> : BepInEx.Unity.IL2CPP.BasePlugin where TContext : PluginContext, new()
    {
        public sealed override void Load() { }
        protected abstract void OnLoad(TContext ctx);
    }
}", "BepInEx.Core", "BepInEx.Unity.IL2CPP");
        Framework("0Harmony", @"namespace HarmonyLib { public class Harmony { public Harmony(string id) { } public void PatchAll() { } } }");
        Framework("Il2CppInterop.Runtime", @"
namespace Il2CppInterop.Runtime.Injection { public static class ClassInjector { public static void RegisterTypeInIl2Cpp<T>() { } } }");
        Framework("ProjectM", @"namespace ProjectM { public struct Health { public float Value; } public class Team { } }");
        Framework("Unity.Collections", @"
namespace Unity.Collections { public struct NativeArray<T> where T : struct { public int Length => 0; public T this[int i] => default; } }");
        Framework("Unity.Entities", @"
namespace Unity.Entities { public class EntityManager { public T GetComponentData<T>(int entity) where T : struct => default; public void SetComponentData<T>(int entity, T value) where T : struct { } } }",
            "Unity.Collections", "ProjectM");
        Framework("Contoso.Helpers", @"
namespace Contoso { public static class Helpers { public static int Answer() => 42; } public abstract class HelperPlugin : Progenitor.Plugins.ProgenitorPlugin<Progenitor.Plugins.PluginContext> { } }",
            "Progenitor", "BepInEx.Unity.IL2CPP", "BepInEx.Core");
        ContosoHelpers = _framework["Contoso.Helpers"];

        const string head = @"
using System;
using System.Collections.Generic;
using BepInEx;
using Progenitor.Plugins;
namespace Sample {
    public class Ctx : PluginContext { }
";
        const string attrs = "    [BepInPlugin(\"example.sample\", \"Sample\", \"1.0.0\")]\n    [ProgenitorSide(Side.Client)]\n";

        Valid = Plugin(head + attrs + @"
    public class Plugin : ProgenitorPlugin<Ctx> { protected override void OnLoad(Ctx ctx) { var l = ctx.Log; } }
}");
        ManagedGenericControl = Plugin(head + attrs + @"
    public class Plugin : ProgenitorPlugin<Ctx> {
        protected override void OnLoad(Ctx ctx) {
            var list = new List<ProjectM.Health>(); list.Add(default);
            var map = new Dictionary<int, ProjectM.Health>(); map[1] = default;
            var nested = new List<List<ProjectM.Health>>(); nested.Add(list);
            Func<ProjectM.Health, int> f = h => (int)h.Value; f(default);
        }
    }
}", "ProjectM");
        UnsafeDirect = Plugin(head + attrs + @"
    public class Plugin : ProgenitorPlugin<Ctx> { protected override void OnLoad(Ctx ctx) { var h = ctx.UnsafeHarmony; } }
}");
        UnsafeHelperDependency = Compile("Helper", @"
namespace Helper { public static class Grab { public static object Harmony(Progenitor.Plugins.PluginContext c) => c.UnsafeHarmony; } }", "Progenitor");
        CleanHelperDependency = Compile("Helper", @"
namespace Helper { public static class Grab { public static object Harmony(Progenitor.Plugins.PluginContext c) => c.Log; } }", "Progenitor");
        UnsafeViaHelperMain = Plugin(head + attrs + @"
    public class Plugin : ProgenitorPlugin<Ctx> { protected override void OnLoad(Ctx ctx) { var h = Helper.Grab.Harmony(ctx); } }
}", extraImages: new[] { ("Helper", UnsafeHelperDependency) });
        ClassInjector = Plugin(head + attrs + @"
    public class Plugin : ProgenitorPlugin<Ctx> { protected override void OnLoad(Ctx ctx) { Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<Ctx>(); } }
}", "Il2CppInterop.Runtime");
        PInvoke = Plugin(head + attrs + @"
    public class Plugin : ProgenitorPlugin<Ctx> {
        [System.Runtime.InteropServices.DllImport(""kernel32.dll"")] static extern uint GetTickCount();
        protected override void OnLoad(Ctx ctx) { GetTickCount(); }
    }
}");
        Calli = Plugin(head + attrs + @"
    public unsafe class Plugin : ProgenitorPlugin<Ctx> {
        static int Square(int x) => x * x;
        protected override void OnLoad(Ctx ctx) { delegate*<int, int> f = &Square; f(3); }
    }
}", allowUnsafe: true);
        ReflectionEscape = Plugin(head + attrs + @"
    public class Plugin : ProgenitorPlugin<Ctx> {
        public void M<T>() { }
        protected override void OnLoad(Ctx ctx) {
            var t = Type.GetType(""Sample.Ctx"");
            var o = Activator.CreateInstance(typeof(Ctx));
            typeof(Plugin).GetMethod(""M"")!.MakeGenericMethod(typeof(int));
        }
    }
}");
        // tools/publishcheck: Type.MakeGenericType alone, so the checker's hotLoadable verdict is tested on it.
        MakeGenericTypeEscape = Plugin(head + attrs + @"
    public class Plugin : ProgenitorPlugin<Ctx> {
        protected override void OnLoad(Ctx ctx) { typeof(List<>).MakeGenericType(typeof(int)); }
    }
}");
        UnresolvedDependencyMain = Plugin(head + attrs + @"
    public class Plugin : ProgenitorPlugin<Ctx> { protected override void OnLoad(Ctx ctx) { Contoso.Helpers.Answer(); } }
}", "Contoso.Helpers");
        UnresolvedBaseMain = Plugin(head + attrs + @"
    public class Plugin : Contoso.HelperPlugin { protected override void OnLoad(PluginContext ctx) { } }
}", "Contoso.Helpers");
        TwoEntryPoints = Plugin(head + attrs + @"
    public class Plugin : ProgenitorPlugin<Ctx> { protected override void OnLoad(Ctx ctx) { } }
    [BepInPlugin(""example.second"", ""Second"", ""1.0.0"")]
    public class Second : ProgenitorPlugin<Ctx> { protected override void OnLoad(Ctx ctx) { } }
}");
        NoEntryPoint = Plugin(head + @"
    public class Plugin : ProgenitorPlugin<Ctx> { protected override void OnLoad(Ctx ctx) { } }
}");
        SentinelPath = Path.Combine(Path.GetTempPath(), "progenitor-admit1-sentinel-" + Guid.NewGuid().ToString("N") + ".txt");
        string sentinelLiteral = SentinelPath.Replace("\\", "\\\\");
        Sentinel = Plugin(head + attrs + @"
    public class Plugin : ProgenitorPlugin<Ctx> {
        static Plugin() { System.IO.File.WriteAllText(""" + sentinelLiteral + @""", ""cctor""); }
        protected override void OnLoad(Ctx ctx) { System.IO.File.WriteAllText(""" + sentinelLiteral + @""", ""onload""); }
    }
    internal static class Init {
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Run() { System.IO.File.WriteAllText(""" + sentinelLiteral + @""", ""module-init""); }
    }
}");
        InteropGenerics = Plugin(head + attrs + @"
    public class Plugin : ProgenitorPlugin<Ctx> {
        protected override void OnLoad(Ctx ctx) {
            var em = new Unity.Entities.EntityManager();
            var h = em.GetComponentData<ProjectM.Health>(1);
            var arr = new Unity.Collections.NativeArray<ProjectM.Health>();
            var nested = new Unity.Collections.NativeArray<Unity.Collections.NativeArray<ProjectM.Health>>();
            var deep = em.GetComponentData<Unity.Collections.NativeArray<ProjectM.Health>>(2);
            var list = new List<ProjectM.Health>(); list.Add(h);
            var ints = new Unity.Collections.NativeArray<int>();
        }
    }
}", "ProjectM", "Unity.Collections", "Unity.Entities");
        ServerSide = Plugin(head + "    [BepInPlugin(\"example.sample\", \"Sample\", \"1.0.0\")]\n    [ProgenitorSide(Side.Server)]\n" + @"
    public class Plugin : ProgenitorPlugin<Ctx> { protected override void OnLoad(Ctx ctx) { } }
}");
        HarmonyDirect = Plugin(head + attrs + @"
    public class Plugin : ProgenitorPlugin<Ctx> { protected override void OnLoad(Ctx ctx) { new HarmonyLib.Harmony(""x"").PatchAll(); } }
}", "0Harmony");
        // HOTDEMO1: a clean plugin that is NOT a ProgenitorPlugin — the trusted flag never covers it.
        PlainBasePlugin = Plugin(head + attrs + @"
    public class Plugin : BepInEx.Unity.IL2CPP.BasePlugin { public override void Load() { } }
}");
    }

    public byte[] PlainBasePlugin { get; }

    private void Framework(string name, string source, params string[] refs) =>
        _framework[name] = Compile(name, source, refs);

    private byte[] Plugin(string source, params string[] refs) => Plugin(source, false, null, refs);

    private byte[] Plugin(string source, bool allowUnsafe = false, (string Name, byte[] Bytes)[]? extraImages = null, params string[] refs)
    {
        var all = new List<string> { "BepInEx.Core", "BepInEx.Unity.IL2CPP", "Progenitor" };
        all.AddRange(refs);
        return Compile("Sample", source, allowUnsafe, extraImages, all.ToArray());
    }

    private byte[] Compile(string name, string source, params string[] refs) => Compile(name, source, false, null, refs);

    private byte[] Compile(string name, string source, bool allowUnsafe, (string Name, byte[] Bytes)[]? extraImages, params string[] refs)
    {
        var references = new List<MetadataReference>(_runtime);
        foreach (var r in refs) references.Add(MetadataReference.CreateFromImage(_framework[r]));
        if (extraImages != null)
            foreach (var (_, bytes) in extraImages) references.Add(MetadataReference.CreateFromImage(bytes));
        var compilation = CSharpCompilation.Create(
            name,
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: allowUnsafe, nullableContextOptions: NullableContextOptions.Enable));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success, name + ": " + string.Join(Environment.NewLine, emit.Diagnostics));
        return ms.ToArray();
    }
}

public sealed class ArtifactPreflightTests : IClassFixture<AdmissionFixtures>
{
    private readonly AdmissionFixtures _f;

    public ArtifactPreflightTests(AdmissionFixtures f) => _f = f;

    private static PreflightOutcome Scan(byte[] image, params (string Name, byte[] Bytes)[] bundled)
    {
        var deps = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, b) in bundled) deps[n] = b;
        return ArtifactPreflight.Scan(image, deps);
    }

    [Fact]
    public void Valid_sample_shaped_plugin_scans_clean_with_identity_side_and_progenitor_base()
    {
        var scan = Scan(_f.Valid);
        Assert.True(scan.Valid, scan.RefusalDetail);
        Assert.Equal("Sample", scan.AssemblyName);
        var entry = Assert.Single(scan.EntryPoints);
        Assert.Equal(AdmissionFixtures.SampleId, entry.Guid);
        Assert.Equal(AdmissionFixtures.SampleVersion, entry.Version);
        Assert.Equal("Sample.Plugin", entry.TypeName);
        Assert.True(scan.SideDeclared);
        Assert.Equal(PackageSide.Client, scan.DeclaredSide);
        Assert.True(scan.BaseChainResolved);
        Assert.True(scan.BaseIsProgenitorPlugin);
        Assert.Contains("Progenitor.Plugins.ProgenitorPlugin`1", scan.BaseChain);
        Assert.Empty(scan.Findings);
        Assert.Empty(scan.UnresolvedReferences);
        Assert.Empty(scan.InteropInstantiations);
        Assert.Contains("Progenitor", scan.AssemblyReferences);
        // Revert: reading [BepInPlugin] by instantiating the attribute, or walking the base chain
        // with Type.BaseType, would need Assembly.Load — this test would then load the fixture.
    }

    [Fact]
    public void Direct_UnsafeHarmony_is_a_finding()
    {
        var scan = Scan(_f.UnsafeDirect);
        Assert.True(scan.Valid);
        var finding = Assert.Single(scan.Findings);
        Assert.Equal(AdmissionReasonCode.UnsafeHarmonyReference, finding.Code);
        Assert.Equal("main", finding.Image);
        Assert.Contains("get_UnsafeHarmony", finding.Site);
    }

    [Fact]
    public void Helper_mediated_UnsafeHarmony_in_a_bundled_dependency_is_found_only_when_the_dependency_is_scanned()
    {
        var withUnsafe = Scan(_f.UnsafeViaHelperMain, ("Helper", _f.UnsafeHelperDependency));
        Assert.True(withUnsafe.Valid);
        var finding = Assert.Single(withUnsafe.Findings, f => f.Code == AdmissionReasonCode.UnsafeHarmonyReference);
        Assert.Equal("Helper", finding.Image);
        Assert.Empty(withUnsafe.UnresolvedReferences);

        var withClean = Scan(_f.UnsafeViaHelperMain, ("Helper", _f.CleanHelperDependency));
        Assert.Empty(withClean.Findings);

        // Without the closure the helper is unresolved, not silently clean.
        var missing = Scan(_f.UnsafeViaHelperMain);
        Assert.Empty(missing.Findings);
        Assert.Contains(missing.UnresolvedReferences, r => r.EndsWith("-> Helper", StringComparison.Ordinal));
        // Revert: scanning the main image only would report the helper build as clean.
    }

    [Fact]
    public void ClassInjector_PInvoke_calli_and_Harmony_are_findings()
    {
        Assert.Contains(Scan(_f.ClassInjector).Findings, f => f.Code == AdmissionReasonCode.ClassInjectorReference);
        var pinvoke = Scan(_f.PInvoke);
        Assert.Contains(pinvoke.Findings, f => f.Code == AdmissionReasonCode.NativeImport && f.Site.StartsWith("DllImport", StringComparison.Ordinal));
        Assert.Contains(pinvoke.Findings, f => f.Code == AdmissionReasonCode.NativeImport && f.Site.StartsWith("ModuleRef kernel32", StringComparison.Ordinal));
        var calli = Scan(_f.Calli);
        Assert.Contains(calli.Findings, f => f.Code == AdmissionReasonCode.NativeImport && f.Site.StartsWith("calli ", StringComparison.Ordinal));
        Assert.DoesNotContain(calli.Findings, f => f.Site.StartsWith("DllImport", StringComparison.Ordinal));
        Assert.Contains(Scan(_f.HarmonyDirect).Findings, f => f.Code == AdmissionReasonCode.HarmonyReference);
        // Revert: dropping the IL walk loses the calli row; dropping the ModuleRef pass loses the import.
    }

    [Fact]
    public void Calli_walker_does_not_mistake_operand_bytes_for_an_opcode()
    {
        // ldc.i4 0x29 : the 0x29 is an operand, not calli.
        Assert.False(ArtifactPreflight.BodyHasCalli(new byte[] { 0x20, 0x29, 0x00, 0x00, 0x00, 0x2A }));
        Assert.True(ArtifactPreflight.BodyHasCalli(new byte[] { 0x29, 0x01, 0x00, 0x00, 0x11, 0x2A }));
        // A switch with two targets followed by ret; 0x29 inside the jump table is data.
        Assert.False(ArtifactPreflight.BodyHasCalli(new byte[] { 0x45, 0x02, 0, 0, 0, 0x29, 0, 0, 0, 0x29, 0, 0, 0, 0x2A }));
    }

    [Fact]
    public void Reflection_and_dynamic_load_references_are_escape_findings()
    {
        var scan = Scan(_f.ReflectionEscape);
        Assert.True(scan.Valid);
        var escapes = scan.Findings.Where(f => f.Code == AdmissionReasonCode.ReflectionEscape).ToList();
        Assert.Contains(escapes, f => f.Site.Contains("System.Type::GetType", StringComparison.Ordinal));
        Assert.Contains(escapes, f => f.Site == "System.Activator");
        Assert.Contains(escapes, f => f.Site.EndsWith("::MakeGenericMethod", StringComparison.Ordinal));
        Assert.DoesNotContain(scan.Findings, f => f.Code == AdmissionReasonCode.NativeImport);
    }

    [Fact]
    public void Unresolved_dependency_and_unresolved_base_chain_are_reported_not_assumed()
    {
        var dep = Scan(_f.UnresolvedDependencyMain);
        Assert.True(dep.Valid);
        Assert.Contains(dep.UnresolvedReferences, r => r == "main -> Contoso.Helpers");
        Assert.True(dep.BaseChainResolved);

        var baseChain = Scan(_f.UnresolvedBaseMain);
        Assert.False(baseChain.BaseChainResolved);
        Assert.Contains("Contoso.HelperPlugin", baseChain.BaseChain);
        Assert.False(baseChain.BaseIsProgenitorPlugin);

        // With Contoso in the closure the chain walks through its bytes to the Progenitor base.
        var resolved = Scan(_f.UnresolvedBaseMain, ("Contoso.Helpers", _f.ContosoHelpers));
        Assert.True(resolved.BaseChainResolved);
        Assert.True(resolved.BaseIsProgenitorPlugin);
        Assert.Empty(resolved.UnresolvedReferences);
        // Revert: treating any unknown base as "probably a plugin" would set BaseChainResolved true.
    }

    [Fact]
    public void Two_entry_points_and_no_entry_point_are_counted_exactly()
    {
        Assert.Equal(2, Scan(_f.TwoEntryPoints).EntryPoints.Count);
        Assert.Empty(Scan(_f.NoEntryPoint).EntryPoints);
    }

    [Fact]
    public void Malformed_truncated_and_non_managed_bytes_are_refused_with_a_reason()
    {
        var truncated = ArtifactPreflight.Scan(new ReadOnlyMemory<byte>(_f.Valid, 0, _f.Valid.Length / 2));
        Assert.False(truncated.Valid);
        Assert.True(truncated.Refusal == AdmissionReasonCode.MalformedImage || truncated.Refusal == AdmissionReasonCode.NotManagedImage, truncated.Refusal.ToString());

        var garbage = new byte[4096];
        for (int i = 0; i < garbage.Length; i++) garbage[i] = (byte)(i * 31 + 7);
        var junk = ArtifactPreflight.Scan(garbage);
        Assert.False(junk.Valid);
        Assert.Equal(AdmissionReasonCode.MalformedImage, junk.Refusal);

        var tiny = ArtifactPreflight.Scan(new byte[] { 0x4D, 0x5A });
        Assert.False(tiny.Valid);
        Assert.Equal(AdmissionReasonCode.MalformedImage, tiny.Refusal);

        // A bundled image that does not parse refuses the whole scan, naming the dependency.
        var bad = Scan(_f.Valid, ("Helper", garbage));
        Assert.False(bad.Valid);
        Assert.Contains("bundled Helper", bad.RefusalDetail);
    }

    [Fact]
    public void Interop_generic_instantiations_are_enumerated_and_managed_containers_are_not_flagged()
    {
        var scan = Scan(_f.InteropGenerics);
        Assert.True(scan.Valid);
        var texts = scan.InteropInstantiations.Select(i => i.Text).ToList();
        Assert.Contains(texts, t => t == "Unity.Entities.EntityManager::GetComponentData<ProjectM.Health>");
        Assert.Contains(texts, t => t == "Unity.Collections.NativeArray`1<ProjectM.Health>");
        Assert.Contains(texts, t => t == "Unity.Collections.NativeArray`1<Unity.Collections.NativeArray`1<ProjectM.Health>>");
        Assert.Contains(texts, t => t == "Unity.Entities.EntityManager::GetComponentData<Unity.Collections.NativeArray`1<ProjectM.Health>>");
        Assert.DoesNotContain(texts, t => t.StartsWith("System.Collections", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t == "Unity.Collections.NativeArray`1<System.Int32>");
        Assert.True(scan.ManagedGenericInstantiations > 0);

        var control = Scan(_f.ManagedGenericControl);
        Assert.True(control.Valid);
        Assert.Empty(control.InteropInstantiations);
        Assert.True(control.ManagedGenericInstantiations >= 3);
        // Revert: flagging any generic argument that is a game value type would list
        // List<ProjectM.Health> and fail the control.
    }

    [Fact]
    public void Scan_never_runs_the_module_initializer_or_static_constructor()
    {
        Assert.False(File.Exists(_f.SentinelPath));
        var scan = Scan(_f.Sentinel);
        Assert.True(scan.Valid, scan.RefusalDetail);
        Assert.Single(scan.EntryPoints);
        Assert.False(File.Exists(_f.SentinelPath), "the scan executed candidate code: the sentinel file exists");

        // Repeated scans, with the sentinel image as a bundled dependency too.
        Scan(_f.Valid, ("Sentinel", _f.Sentinel));
        Assert.False(File.Exists(_f.SentinelPath));
        // Revert: any Assembly.Load / Activator / attribute instantiation in the scanner runs the
        // module initializer and writes the sentinel — this assert is the executable proof.
    }

    [Fact]
    public void Server_side_attribute_is_read_as_a_side_declaration()
    {
        var scan = Scan(_f.ServerSide);
        Assert.True(scan.SideDeclared);
        Assert.Equal(PackageSide.Server, scan.DeclaredSide);
    }
}

public sealed class AdmissionEvaluatorTests : IClassFixture<AdmissionFixtures>
{
    private readonly AdmissionFixtures _f;

    public AdmissionEvaluatorTests(AdmissionFixtures f) => _f = f;

    private const string Build = "1.1.5.1234";
    private const string Bootstrap = "bootstrap/3";
    private const string Core = "core/7";

    private static string Sha(byte[] b) => CatalogFetcher.ToHex(SHA256.HashData(b));

    private static IReadOnlyList<CompatibilityIdentity> Identities() => new[]
    {
        new CompatibilityIdentity(CompatibilityKeys.GameBuild, Build),
        new CompatibilityIdentity(CompatibilityKeys.BootstrapContract, Bootstrap),
        new CompatibilityIdentity(CompatibilityKeys.CoreContract, Core),
    };

    private static RuntimeGenerationSnapshot Live(
        long generation = 4,
        bool busy = false,
        bool allow = true,
        bool server = false,
        LoadedPluginState? current = null,
        AdmissionSide side = AdmissionSide.Client) =>
        new(true, null, side, generation, busy, allow, server, current, Identities());

    private static QualificationRecord Record(byte[] image, DependencyClosure? closure = null, PackageSide side = PackageSide.Client,
        AdmissionOperation op = AdmissionOperation.InitialInstall, InstallTarget target = InstallTarget.Client,
        string build = Build, string bootstrap = Bootstrap, string core = Core, string pluginId = AdmissionFixtures.SampleId) =>
        new("sample-initial-client", pluginId, new ArtifactIdentity(Sha(image), image.Length), closure ?? DependencyClosure.Empty,
            build, bootstrap, core, side, op, target, "2026-09-12T00:00:00Z", "research/hot-load/sample-initial.md");

    private AdmissionRequest Request(
        byte[] image,
        RuntimeGenerationSnapshot? snapshot = null,
        QualificationRecord? record = null,
        AdmissionOperation op = AdmissionOperation.InitialInstall,
        InstallTarget target = InstallTarget.Client,
        PackageSide side = PackageSide.Client,
        int schema = PackageDeclaration.LegacySchemaVersion,
        bool declaresHot = true,
        string pluginId = AdmissionFixtures.SampleId,
        string version = AdmissionFixtures.SampleVersion,
        (string Name, byte[] Bytes)[]? bundled = null,
        bool embedded = false,
        string policy = "admission/1+test")
    {
        var deps = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        var closure = new List<KeyValuePair<string, string>>();
        if (bundled != null)
        {
            foreach (var (n, b) in bundled)
            {
                deps[n] = b;
                closure.Add(new KeyValuePair<string, string>(n, Sha(b)));
            }
        }
        var scan = ArtifactPreflight.Scan(image, deps);
        return new AdmissionRequest(
            op, target,
            new PackageDeclaration(schema, pluginId, version, side, declaresHot, false, false),
            new ArtifactIdentity(Sha(image), image.Length),
            new DependencyClosure(closure),
            Identities(),
            scan,
            policy,
            snapshot ?? RuntimeGenerationSnapshot.Unavailable(),
            record == null ? null : new[] { record },
            embedded);
    }

    [Fact]
    public void Qualified_image_with_live_snapshot_is_ApplyNowEligible_and_carries_facts()
    {
        var result = AdmissionEvaluator.Evaluate(Request(_f.Valid, Live(), Record(_f.Valid)));
        Assert.Equal(AdmissionDecision.ApplyNowEligible, result.Decision);
        Assert.Equal(AdmissionReasonCode.Qualified, result.Reasons[0].Code);
        Assert.Equal("sample-initial-client", result.Facts["qualificationRecord"]);
        Assert.Equal("4", result.Facts["generation"]);
        Assert.Equal("true", result.Facts["baseIsProgenitorPlugin"]);
        Assert.Equal("0", result.Facts["interopInstantiations"]);
    }

    [Fact]
    public void Production_default_snapshot_is_unavailable_so_even_a_qualified_image_is_Unknown()
    {
        var result = AdmissionEvaluator.Evaluate(Request(_f.Valid, RuntimeGenerationSnapshot.Unavailable(), Record(_f.Valid)));
        Assert.Equal(AdmissionDecision.Unknown, result.Decision);
        Assert.Equal(AdmissionReasonCode.SnapshotUnavailable, result.Reasons[0].Code);
        Assert.Contains(RuntimeGenerationSnapshot.NotImplementedReason, result.Reasons[0].Text);
        Assert.True(result.Has(AdmissionReasonCode.Qualified));
        Assert.Equal(RuntimeGenerationSnapshot.Unavailable().Available, UnavailableAdmissionSnapshotSource.Instance.Capture("x", InstallTarget.Client).Available);
        // Revert: skipping the snapshot check makes production ApplyNowEligible with no running-process facts.
    }

    [Fact]
    public void No_record_is_RestartInstall_and_an_embedded_record_is_ignored()
    {
        // HOTDEMO1 (user policy 2026-10-03): a clean hotLoadable ProgenitorPlugin needs no record any
        // more. The missing record stays in the reasons as a fact.
        var none = AdmissionEvaluator.Evaluate(Request(_f.Valid, Live()));
        Assert.Equal(AdmissionDecision.ApplyNowEligible, none.Decision);
        Assert.Equal(AdmissionReasonCode.TrustedHotLoadable, none.Reasons[0].Code);
        Assert.True(none.Has(AdmissionReasonCode.NoQualificationRecord));

        // A plugin that is not a ProgenitorPlugin still needs a record.
        var plain = AdmissionEvaluator.Evaluate(Request(_f.PlainBasePlugin, Live()));
        Assert.Equal(AdmissionDecision.RestartInstall, plain.Decision);
        Assert.Equal(AdmissionReasonCode.NotProgenitorPlugin, plain.Reasons[0].Code);
        Assert.True(plain.Has(AdmissionReasonCode.NoQualificationRecord));

        var embedded = AdmissionEvaluator.Evaluate(Request(_f.PlainBasePlugin, Live(), embedded: true));
        Assert.Equal(AdmissionDecision.RestartInstall, embedded.Decision);
        Assert.Equal(AdmissionReasonCode.NotProgenitorPlugin, embedded.Reasons[0].Code);
        Assert.True(embedded.Has(AdmissionReasonCode.EmbeddedQualificationIgnored));

        var embeddedButQualified = AdmissionEvaluator.Evaluate(Request(_f.Valid, Live(), Record(_f.Valid), embedded: true));
        Assert.Equal(AdmissionDecision.ApplyNowEligible, embeddedButQualified.Decision);
        Assert.True(embeddedButQualified.Has(AdmissionReasonCode.EmbeddedQualificationIgnored));
        // Revert: consulting the embedded record would let a package self-certify.
    }

    [Theory]
    [InlineData("artifactSha256")]
    [InlineData("dependencyClosure")]
    [InlineData("gameBuild")]
    [InlineData("bootstrapContract")]
    [InlineData("coreContract")]
    [InlineData("side")]
    [InlineData("operation")]
    [InlineData("target")]
    public void Record_mismatch_on_any_key_refuses_and_names_the_key(string key)
    {
        QualificationRecord record = key switch
        {
            "artifactSha256" => Record(_f.ManagedGenericControl),
            "dependencyClosure" => Record(_f.Valid, new DependencyClosure(new[] { new KeyValuePair<string, string>("Helper", Sha(_f.CleanHelperDependency)) })),
            "gameBuild" => Record(_f.Valid, build: "1.1.6.0"),
            "bootstrapContract" => Record(_f.Valid, bootstrap: "bootstrap/4"),
            "coreContract" => Record(_f.Valid, core: "core/8"),
            "side" => Record(_f.Valid, side: PackageSide.Server),
            "operation" => Record(_f.Valid, op: AdmissionOperation.Replace),
            _ => Record(_f.Valid, target: InstallTarget.LocalServer),
        };
        var result = AdmissionEvaluator.Evaluate(Request(_f.Valid, Live(), record));
        // HOTDEMO1: a mismatched record no longer decides (the trusted flag admits a clean
        // ProgenitorPlugin), but it is still named, key and all, and never reads as Qualified.
        Assert.Equal(AdmissionDecision.ApplyNowEligible, result.Decision);
        Assert.Equal(AdmissionReasonCode.TrustedHotLoadable, result.Reasons[0].Code);
        var mismatch = Assert.Single(result.Reasons, r => r.Code == AdmissionReasonCode.QualificationMismatch);
        Assert.Contains(key, mismatch.Text);
        Assert.False(result.Has(AdmissionReasonCode.Qualified));
        // Revert: matching on pluginId + version instead of the full key set makes every row Qualified.
    }

    [Fact]
    public void Record_for_a_different_plugin_is_a_pluginId_mismatch_not_a_match()
    {
        var result = AdmissionEvaluator.Evaluate(Request(_f.PlainBasePlugin, Live(), Record(_f.PlainBasePlugin, pluginId: "example.other")));
        Assert.Equal(AdmissionDecision.RestartInstall, result.Decision);
        Assert.Contains(result.Reasons, r => r.Code == AdmissionReasonCode.QualificationMismatch && r.Text.Contains("pluginId"));
        Assert.False(result.Has(AdmissionReasonCode.Qualified));
    }

    [Fact]
    public void Scanner_findings_refuse_hot_activation_and_unknowns_stay_unknown()
    {
        Assert.Equal(AdmissionReasonCode.UnsafeHarmonyReference, First(_f.UnsafeDirect, out var d1)); Assert.Equal(AdmissionDecision.RestartInstall, d1);
        Assert.Equal(AdmissionReasonCode.ClassInjectorReference, First(_f.ClassInjector, out var d2)); Assert.Equal(AdmissionDecision.RestartInstall, d2);
        Assert.Equal(AdmissionReasonCode.NativeImport, First(_f.PInvoke, out var d3)); Assert.Equal(AdmissionDecision.RestartInstall, d3);
        Assert.Equal(AdmissionReasonCode.NativeImport, First(_f.Calli, out var d4)); Assert.Equal(AdmissionDecision.RestartInstall, d4);
        Assert.Equal(AdmissionReasonCode.HarmonyReference, First(_f.HarmonyDirect, out var d5)); Assert.Equal(AdmissionDecision.RestartInstall, d5);
        Assert.Equal(AdmissionReasonCode.ReflectionEscape, First(_f.ReflectionEscape, out var d6)); Assert.Equal(AdmissionDecision.Unknown, d6);
        Assert.Equal(AdmissionReasonCode.UnresolvedDependency, First(_f.UnresolvedDependencyMain, out var d7)); Assert.Equal(AdmissionDecision.Unknown, d7);
        Assert.Equal(AdmissionReasonCode.UnresolvedBaseChain, First(_f.UnresolvedBaseMain, out var d8)); Assert.Equal(AdmissionDecision.Unknown, d8);
        Assert.Equal(AdmissionReasonCode.MultiplePluginEntryPoints, First(_f.TwoEntryPoints, out var d9)); Assert.Equal(AdmissionDecision.RestartInstall, d9);
        Assert.Equal(AdmissionReasonCode.NoPluginEntryPoint, First(_f.NoEntryPoint, out var d10)); Assert.Equal(AdmissionDecision.RestartInstall, d10);

        var helper = AdmissionEvaluator.Evaluate(Request(_f.UnsafeViaHelperMain, Live(), Record(_f.UnsafeViaHelperMain), bundled: new[] { ("Helper", _f.UnsafeHelperDependency) }));
        Assert.Equal(AdmissionReasonCode.UnsafeHarmonyReference, helper.Reasons[0].Code);
        Assert.Equal(AdmissionDecision.RestartInstall, helper.Decision);
        // Revert: honoring the record before the findings would admit the UnsafeHarmony fixture.
    }

    private AdmissionReasonCode First(byte[] image, out AdmissionDecision decision)
    {
        var result = AdmissionEvaluator.Evaluate(Request(image, Live(), Record(image)));
        decision = result.Decision;
        return result.Reasons[0].Code;
    }

    [Fact]
    public void Malformed_image_and_identity_mismatch_are_Blocked()
    {
        var truncated = new byte[_f.Valid.Length / 2];
        Array.Copy(_f.Valid, truncated, truncated.Length);
        var malformed = AdmissionEvaluator.Evaluate(Request(truncated, Live(), Record(truncated)));
        Assert.Equal(AdmissionDecision.Blocked, malformed.Decision);
        Assert.True(malformed.Reasons[0].Code is AdmissionReasonCode.MalformedImage or AdmissionReasonCode.NotManagedImage);

        var id = AdmissionEvaluator.Evaluate(Request(_f.Valid, Live(), Record(_f.Valid, pluginId: "example.other"), pluginId: "example.other"));
        Assert.Equal(AdmissionDecision.Blocked, id.Decision);
        Assert.Equal(AdmissionReasonCode.IdentityMismatch, id.Reasons[0].Code);
        Assert.Contains("example.other@1.0.0, image example.sample@1.0.0", id.Reasons[0].Text);

        var version = AdmissionEvaluator.Evaluate(Request(_f.Valid, Live(), Record(_f.Valid), version: "1.0.1"));
        Assert.Equal(AdmissionReasonCode.IdentityMismatch, version.Reasons[0].Code);
    }

    [Fact]
    public void Unknown_schema_undeclared_capability_and_unsupported_operation_refuse_before_qualification()
    {
        var schema = AdmissionEvaluator.Evaluate(Request(_f.Valid, Live(), Record(_f.Valid), schema: 2));
        Assert.Equal(AdmissionDecision.Unknown, schema.Decision);
        Assert.Equal(AdmissionReasonCode.UnknownDeclarationSchema, schema.Reasons[0].Code);

        var undeclared = AdmissionEvaluator.Evaluate(Request(_f.Valid, Live(), Record(_f.Valid), declaresHot: false));
        Assert.Equal(AdmissionDecision.RestartInstall, undeclared.Decision);
        Assert.Equal(AdmissionReasonCode.HotLoadNotDeclared, undeclared.Reasons[0].Code);

        foreach (var op in new[] { AdmissionOperation.Replace, AdmissionOperation.Remove })
        {
            var result = AdmissionEvaluator.Evaluate(Request(_f.Valid, Live(), Record(_f.Valid, op: op), op: op));
            Assert.Equal(AdmissionDecision.RestartInstall, result.Decision);
            Assert.Equal(AdmissionReasonCode.UnsupportedOperation, result.Reasons[0].Code);
        }
    }

    [Fact]
    public void Runtime_snapshot_refusals_each_name_their_state()
    {
        Assert.Equal(AdmissionReasonCode.LoaderBusy, Primary(Live(busy: true)));
        Assert.Equal(AdmissionReasonCode.AllowlistDenied, Primary(Live(allow: false)));
        Assert.Equal(AdmissionReasonCode.PriorGenerationPinned, Primary(Live(current: new LoadedPluginState(AdmissionFixtures.SampleId, null, false, true))));
        Assert.Equal(AdmissionReasonCode.UnsafeHarmonyMarked, Primary(Live(current: new LoadedPluginState(AdmissionFixtures.SampleId, null, true, false))));
        Assert.Equal(AdmissionReasonCode.AlreadyLoaded, Primary(Live(current: new LoadedPluginState(AdmissionFixtures.SampleId, new ArtifactIdentity(Sha(_f.Valid), _f.Valid.Length), false, false))));
        Assert.Equal(AdmissionReasonCode.SideMismatch, Primary(Live(side: AdmissionSide.Server)));

        var server = AdmissionEvaluator.Evaluate(Request(_f.Valid, Live(server: false), Record(_f.Valid, target: InstallTarget.LocalServer), target: InstallTarget.LocalServer));
        Assert.Equal(AdmissionDecision.RestartInstall, server.Decision);
        Assert.Equal(AdmissionReasonCode.LocalServerRuntimeUnavailable, server.Reasons[0].Code);
        Assert.Contains("install for next start", server.Reasons[0].Text);

        var serverConnected = AdmissionEvaluator.Evaluate(Request(_f.Valid, Live(server: true), Record(_f.Valid, target: InstallTarget.LocalServer), target: InstallTarget.LocalServer));
        Assert.Equal(AdmissionDecision.ApplyNowEligible, serverConnected.Decision);

        var replaceMarked = AdmissionEvaluator.Evaluate(Request(
            _f.Valid, Live(current: new LoadedPluginState(AdmissionFixtures.SampleId, null, true, false)),
            Record(_f.Valid, op: AdmissionOperation.Replace), op: AdmissionOperation.Replace));
        Assert.Equal(AdmissionDecision.RestartInstall, replaceMarked.Decision);
        Assert.Equal(AdmissionReasonCode.UnsupportedOperation, replaceMarked.Reasons[0].Code);
        // Revert: any of these refusals turning into ApplyNowEligible would let the planner offer
        // Apply now against a busy, denied, pinned, marked, wrong-side or disconnected runtime.
    }

    private AdmissionReasonCode Primary(RuntimeGenerationSnapshot snapshot)
    {
        var result = AdmissionEvaluator.Evaluate(Request(_f.Valid, snapshot, Record(_f.Valid)));
        Assert.Equal(AdmissionDecision.RestartInstall, result.Decision);
        Assert.True(result.Has(AdmissionReasonCode.Qualified));
        return result.Reasons[0].Code;
    }

    [Fact]
    public void Recheck_refuses_image_generation_side_closure_and_policy_drift()
    {
        var approved = Request(_f.Valid, Live(), Record(_f.Valid));
        Assert.Equal(AdmissionDecision.ApplyNowEligible, AdmissionEvaluator.Recheck(approved, approved).Decision);

        var image = AdmissionEvaluator.Recheck(approved, Request(_f.ManagedGenericControl, Live(), Record(_f.ManagedGenericControl)));
        Assert.Equal(AdmissionDecision.RestartInstall, image.Decision);
        Assert.Equal(AdmissionReasonCode.ImageChanged, image.Reasons[0].Code);

        var generation = AdmissionEvaluator.Recheck(approved, Request(_f.Valid, Live(generation: 5), Record(_f.Valid)));
        Assert.Equal(AdmissionReasonCode.GenerationChanged, generation.Reasons[0].Code);
        Assert.Contains("4 -> 5", generation.Reasons[0].Text);

        var side = AdmissionEvaluator.Recheck(approved, Request(_f.Valid, Live(side: AdmissionSide.Server), Record(_f.Valid)));
        Assert.Equal(AdmissionReasonCode.SideChanged, side.Reasons[0].Code);

        var closure = AdmissionEvaluator.Recheck(approved, Request(_f.Valid, Live(), Record(_f.Valid), bundled: new[] { ("Helper", _f.CleanHelperDependency) }));
        Assert.Equal(AdmissionReasonCode.DependencyRewritten, closure.Reasons[0].Code);

        var policy = AdmissionEvaluator.Recheck(approved, Request(_f.Valid, Live(), Record(_f.Valid), policy: "admission/2+test"));
        Assert.Equal(AdmissionReasonCode.PolicyChanged, policy.Reasons[0].Code);

        var gone = AdmissionEvaluator.Recheck(approved, Request(_f.Valid, RuntimeGenerationSnapshot.Unavailable(), Record(_f.Valid)));
        Assert.Equal(AdmissionReasonCode.SnapshotUnavailable, gone.Reasons[0].Code);
        // Revert: rechecking only the plan hash would miss the generation and policy rows.
    }

    [Fact]
    public void Every_reason_code_has_a_sentence()
    {
        foreach (AdmissionReasonCode code in Enum.GetValues(typeof(AdmissionReasonCode)))
        {
            string text = AdmissionReasonText.For(code);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(code.ToString(), text);
        }
    }

    [Fact]
    public void Result_holds_only_plain_data()
    {
        var result = AdmissionEvaluator.Evaluate(Request(_f.Valid, Live(), Record(_f.Valid)));
        foreach (var kv in result.Facts) Assert.IsType<string>(kv.Value);
        foreach (var p in typeof(AdmissionRequest).GetProperties())
            Assert.False(typeof(System.Reflection.Assembly).IsAssignableFrom(p.PropertyType) || typeof(Type).IsAssignableFrom(p.PropertyType) || typeof(Delegate).IsAssignableFrom(p.PropertyType), p.Name);
        foreach (var p in typeof(PreflightOutcome).GetProperties())
            Assert.False(typeof(System.Reflection.Assembly).IsAssignableFrom(p.PropertyType) || typeof(Type).IsAssignableFrom(p.PropertyType) || typeof(Delegate).IsAssignableFrom(p.PropertyType), p.Name);
    }
}

public sealed class QualificationPolicyTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Json(string sha = Sha, string size = "1024", string closure = "{\"Helper\": \"" + Sha + "\"}", string side = "client", string op = "InitialInstall")
        => "{\"schema\":1,\"records\":[{\"id\":\"r1\",\"pluginId\":\"example.sample\",\"artifactSha256\":\"" + sha + "\",\"artifactSize\":" + size
           + ",\"dependencyClosure\":" + closure + ",\"gameBuild\":\"1.1.5\",\"bootstrapContract\":\"b/3\",\"coreContract\":\"c/7\",\"side\":\"" + side
           + "\",\"operation\":\"" + op + "\",\"target\":\"Client\",\"qualifiedUtc\":\"2026-09-12T00:00:00Z\",\"evidence\":\"research/x.md\"}]}";

    [Fact]
    public void Well_formed_record_parses_with_its_full_key()
    {
        var policy = QualificationPolicy.Parse(Json());
        Assert.Empty(policy.Errors);
        var r = Assert.Single(policy.Records);
        Assert.Equal(Sha, r.Artifact.Sha256);
        Assert.Equal(1024, r.Artifact.Size);
        Assert.Equal("helper=" + Sha + ";", r.Closure.Canonical);
        Assert.Equal(PackageSide.Client, r.Side);
        Assert.Equal(AdmissionOperation.InitialInstall, r.Operation);
        Assert.Equal("v1/1/r1", policy.Version);
        Assert.Single(policy.For("EXAMPLE.SAMPLE"));
        Assert.Empty(policy.For("example.other"));
    }

    [Theory]
    [InlineData("sha", "abc")]
    [InlineData("size", "0")]
    [InlineData("closure", "[]")]
    [InlineData("side", "everywhere")]
    [InlineData("op", "HotSwap")]
    public void Partial_or_invalid_record_is_dropped_never_admitted(string field, string value)
    {
        string json = field switch
        {
            "sha" => Json(sha: value),
            "size" => Json(size: value),
            "closure" => Json(closure: value),
            "side" => Json(side: value),
            _ => Json(op: value),
        };
        var policy = QualificationPolicy.Parse(json);
        Assert.Empty(policy.Records);
        Assert.Single(policy.Errors);
        // Revert: defaulting a missing key would let a half-written record match.
    }

    [Fact]
    public void Wrong_schema_bad_json_and_missing_file_fail_closed()
    {
        Assert.Empty(QualificationPolicy.Parse("{\"schema\":2,\"records\":[]}").Records);
        Assert.NotEmpty(QualificationPolicy.Parse("{\"schema\":2,\"records\":[]}").Errors);
        Assert.NotEmpty(QualificationPolicy.Parse("{not json").Errors);
        Assert.NotEmpty(QualificationPolicy.Parse("[]").Errors);
        string root = Path.Combine(Path.GetTempPath(), "progenitor-qual-" + Guid.NewGuid().ToString("N"));
        Assert.Same(QualificationPolicy.Empty, QualificationPolicy.LoadLocal(root));
    }

    [Fact]
    public void Sample_placeholder_parses_to_zero_records_so_nothing_is_qualified()
    {
        var policy = QualificationPolicy.Parse(QualificationPolicy.SamplePlaceholderJson);
        Assert.Empty(policy.Records);
        var error = Assert.Single(policy.Errors);
        Assert.Contains("artifactSha256", error);
        Assert.Contains("\"pluginId\": \"example.sample\"", QualificationPolicy.SamplePlaceholderJson);
        Assert.Contains("\"operation\": \"InitialInstall\"", QualificationPolicy.SamplePlaceholderJson);
        // Revert: giving the placeholder a real-looking hash would qualify bytes nobody reviewed.
    }

    [Fact]
    public void Local_file_is_read_from_BepInEx_config()
    {
        string root = Path.Combine(Path.GetTempPath(), "progenitor-qual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", QualificationPolicy.FileName), Json());
        try
        {
            Assert.Single(QualificationPolicy.LoadLocal(root).Records);
        }
        finally { Directory.Delete(root, true); }
    }
}

public sealed class AdmissionPlannerTests : IClassFixture<AdmissionFixtures>, IDisposable
{
    private readonly AdmissionFixtures _f;
    private readonly string _root;

    public AdmissionPlannerTests(AdmissionFixtures f)
    {
        _f = f;
        _root = Path.Combine(Path.GetTempPath(), "progenitor-admit-" + Guid.NewGuid().ToString("N"), "BepInEx");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, true); } catch (IOException) { }
    }

    private sealed class FakeSnapshots : IAdmissionSnapshotSource
    {
        public RuntimeGenerationSnapshot Snapshot { get; set; } = RuntimeGenerationSnapshot.Unavailable();
        public int Captures { get; private set; }
        public RuntimeGenerationSnapshot Capture(string pluginId, InstallTarget target) { Captures++; return Snapshot; }
    }

    private static IReadOnlyList<CompatibilityIdentity> Identities() => new[]
    {
        new CompatibilityIdentity(CompatibilityKeys.GameBuild, "1.1.5"),
        new CompatibilityIdentity(CompatibilityKeys.BootstrapContract, "b/3"),
        new CompatibilityIdentity(CompatibilityKeys.CoreContract, "c/7"),
    };

    private static RuntimeGenerationSnapshot Live(long generation = 1, bool server = false) =>
        new(true, null, AdmissionSide.Client, generation, false, true, server, null, Identities());

    private static string RecordJson(byte[] image, string target = "Client") =>
        "{\"schema\":1,\"records\":[{\"id\":\"sample-initial\",\"pluginId\":\"example.sample\",\"artifactSha256\":\"" + CatalogFetcher.ToHex(SHA256.HashData(image))
        + "\",\"artifactSize\":" + image.Length + ",\"dependencyClosure\":{},\"gameBuild\":\"1.1.5\",\"bootstrapContract\":\"b/3\",\"coreContract\":\"c/7\",\"side\":\"client\",\"operation\":\"InitialInstall\",\"target\":\""
        + target + "\",\"qualifiedUtc\":\"2026-09-12T00:00:00Z\",\"evidence\":\"research/x.md\"}]}";

    private string Zip(byte[] dll, bool embedded = false)
    {
        string dllSha = CatalogFetcher.ToHex(SHA256.HashData(dll));
        string json = "{\"id\":\"example.sample\",\"version\":\"1.0.0\",\"sha256\":\"" + dllSha + "\",\"side\":\"client\",\"hotLoadable\":true}";
        string path = Path.Combine(Path.GetDirectoryName(_root)!, "pkg-" + Guid.NewGuid().ToString("N") + ".zip");
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Write(zip, "progenitor.json", Encoding.UTF8.GetBytes(json));
        Write(zip, "example.sample.dll", dll);
        if (embedded) Write(zip, AdmissionPipeline.EmbeddedRecordName, Encoding.UTF8.GetBytes(RecordJson(dll)));
        return path;
    }

    private static void Write(ZipArchive zip, string name, byte[] body)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var s = entry.Open();
        s.Write(body, 0, body.Length);
    }

    private static string Listing() =>
        "[{\"name\":\"Sample\",\"full_name\":\"team-Sample\",\"owner\":\"team\",\"package_url\":\"https://thunderstore.io/c/v-rising/p/team/Sample/\",\"is_deprecated\":false,"
        + "\"versions\":[{\"name\":\"Sample\",\"full_name\":\"team-Sample-1.0.0\",\"description\":\"d\",\"version_number\":\"1.0.0\",\"dependencies\":[],"
        + "\"download_url\":\"https://thunderstore.io/package/download/team/Sample/1.0.0/\",\"file_size\":10,\"is_active\":true}]}]";

    private sealed class NoTransport : ICatalogTransport
    {
        public System.Threading.Tasks.Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, System.Threading.CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network in this test");
    }

    private (InstallCatalogService Catalog, FakeSnapshots Snapshots) Service(byte[] dll, string? policyJson, bool embedded = false, bool localServer = false)
    {
        var snapshots = new FakeSnapshots();
        var pipeline = new AdmissionPipeline(snapshots, policyJson == null ? null : QualificationPolicy.Parse(policyJson), Identities());
        var catalog = new InstallCatalogService(_root, new NoTransport(), null, null, null, localServer, pipeline);
        catalog.IndexSource = IndexKit.Mirror(Listing()); // INDEX1: only an Index install hot loads
        Assert.True(catalog.LoadIndexFromJson(Listing(), "stamp").Ok);
        var inspect = catalog.InspectLocalZip("team-Sample", Zip(dll, embedded));
        Assert.True(inspect.Ok, inspect.Error);
        return (catalog, snapshots);
    }

    private bool ReloadWritten => Directory.Exists(Path.Combine(_root, "progenitor-reload"));
    private bool PendingWritten => Directory.Exists(Path.Combine(_root, "progenitor-pending"));

    [Fact]
    public void Qualified_image_with_live_snapshot_plans_ApplyNow_and_HOT1_Confirm_writes_the_reload_folder_not_pending()
    {
        var (catalog, snapshots) = Service(_f.Valid, RecordJson(_f.Valid));
        snapshots.Snapshot = Live();
        var plan = catalog.Plan("team-Sample", InstallTarget.Client);
        Assert.True(plan.Ok, plan.Error);
        Assert.Equal(InstallDecision.ApplyNow, plan.Plan!.Decision);
        // HOT1: the executor exists now, so an admitted plan carries it. The ADMIT1 gap sentence is
        // gone from this plan; what stays is that only ApplyNowEligible ever reaches the executor.
        Assert.True(plan.Plan.ExecutorAvailable);
        Assert.Equal(InstallReasons.ExecutorHotLoad, plan.Plan.ExecutorReason);
        Assert.StartsWith(InstallReasons.ExecutorHotLoad, plan.Plan.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(InstallReasons.ExecutorNotImplemented, plan.Plan.Reason);
        Assert.NotNull(plan.Plan.Admission);
        Assert.Equal(AdmissionDecision.ApplyNowEligible, plan.Plan.Admission!.Decision);

        var approved = catalog.Approve(plan.Plan.PlanHash);
        Assert.True(approved.Ok, approved.Error);
        var executed = catalog.ExecuteToPending(plan.Plan.PlanHash);
        Assert.True(executed.Ok, executed.Error);
        Assert.True(executed.HotLoaded);
        Assert.True(ReloadWritten);
        Assert.False(PendingWritten);
        Assert.False(File.Exists(_f.SentinelPath));
        // Revert: an admitted plan that still wrote pending only leaves the hot-load lane
        // unreachable; an unadmitted plan that wrote progenitor-reload/ would load unreviewed bytes.
    }

    [Fact]
    public void INDEX1_the_same_qualified_image_from_Thunderstore_installs_for_the_next_start_and_never_writes_the_reload_folder()
    {
        var (catalog, snapshots) = Service(_f.Valid, RecordJson(_f.Valid));
        snapshots.Snapshot = Live();
        Assert.Equal(InstallDecision.ApplyNow, catalog.Plan("team-Sample", InstallTarget.Client).Plan!.Decision);

        // The listing is Thunderstore's only now: same bytes, same record, same live snapshot, restart route.
        catalog.IndexSource = null;
        Assert.True(catalog.InspectLocalZip("team-Sample", Zip(_f.Valid, false)).Ok);
        Assert.False(catalog.TryGetEntry("team-Sample", out var entry) && entry!.FromIndex);
        var plan = catalog.Plan("team-Sample", InstallTarget.Client).Plan!;
        Assert.Equal(InstallDecision.RestartClientRequired, plan.Decision);
        Assert.Contains(InstallReasons.HotLoadIndexOnly, plan.Reason);
        Assert.False(plan.ExecutorAvailable);
        Assert.True(catalog.Approve(plan.PlanHash).Ok);
        Assert.True(catalog.ExecuteToPending(plan.PlanHash).Ok);
        Assert.True(PendingWritten);
        Assert.False(ReloadWritten);
    }

    [Fact]
    public void Default_pipeline_has_no_snapshot_so_a_qualified_image_is_still_a_restart_plan_with_the_snapshot_reason()
    {
        var (catalog, _) = Service(_f.Valid, RecordJson(_f.Valid));
        var plan = catalog.Plan("team-Sample", InstallTarget.Client).Plan!;
        Assert.Equal(InstallDecision.RestartClientRequired, plan.Decision);
        Assert.StartsWith(InstallReasons.AdmissionUnknown, plan.Reason, StringComparison.Ordinal);
        Assert.Contains(RuntimeGenerationSnapshot.NotImplementedReason, plan.Reason);
        Assert.Contains(InstallReasons.RestartClient, plan.Reason);
        Assert.False(plan.ExecutorAvailable);
        Assert.DoesNotContain(InstallReasons.EligibilityNotWired, plan.Reason);
        Assert.False(ReloadWritten);
    }

    [Fact]
    public void No_record_and_an_embedded_record_both_plan_restart_with_the_admission_reason()
    {
        // HOTDEMO1: the clean ProgenitorPlugin fixture is trusted on its flag now, so the restart
        // case is the plain BepInEx plugin (no record, not a ProgenitorPlugin).
        var (catalog, snapshots) = Service(_f.PlainBasePlugin, null, embedded: true);
        snapshots.Snapshot = Live();
        var plan = catalog.Plan("team-Sample", InstallTarget.Client).Plan!;
        Assert.Equal(InstallDecision.RestartClientRequired, plan.Decision);
        Assert.Contains(AdmissionReasonText.For(AdmissionReasonCode.NotProgenitorPlugin), plan.Reason);
        Assert.StartsWith(InstallReasons.AdmissionRefusedHotLoad, plan.Reason, StringComparison.Ordinal);
        Assert.Contains(AdmissionReasonText.For(AdmissionReasonCode.NoQualificationRecord), plan.Reason);
        Assert.Contains(AdmissionReasonText.For(AdmissionReasonCode.EmbeddedQualificationIgnored), plan.Reason);
        Assert.True(plan.Admission!.Has(AdmissionReasonCode.EmbeddedQualificationIgnored));
    }

    [Fact]
    public void UnsafeHarmony_fixture_is_refused_hot_activation_but_keeps_the_restart_install()
    {
        var (catalog, snapshots) = Service(_f.UnsafeDirect, RecordJson(_f.UnsafeDirect));
        snapshots.Snapshot = Live();
        var plan = catalog.Plan("team-Sample", InstallTarget.Client).Plan!;
        Assert.Equal(InstallDecision.RestartClientRequired, plan.Decision);
        Assert.Contains(AdmissionReasonText.For(AdmissionReasonCode.UnsafeHarmonyReference), plan.Reason);
        Assert.True(catalog.Approve(plan.PlanHash).Ok);
        Assert.True(catalog.ExecuteToPending(plan.PlanHash).Ok);
        Assert.True(PendingWritten);
        Assert.False(ReloadWritten);
    }

    [Fact]
    public void Generation_change_after_approval_invalidates_the_approval_and_writes_nothing()
    {
        var (catalog, snapshots) = Service(_f.Valid, RecordJson(_f.Valid));
        snapshots.Snapshot = Live(generation: 1);
        var plan = catalog.Plan("team-Sample", InstallTarget.Client).Plan!;
        Assert.Equal(InstallDecision.ApplyNow, plan.Decision);
        Assert.True(catalog.Approve(plan.PlanHash).Ok);

        // Core swapped, or the snapshot became unavailable: the recomputed plan is a restart plan
        // with a different hash, so the approved ApplyNow hash no longer executes.
        snapshots.Snapshot = RuntimeGenerationSnapshot.Unavailable("Core reloaded after approval");
        var executed = catalog.ExecuteToPending(plan.PlanHash);
        Assert.False(executed.Ok);
        Assert.Equal(InstallReasons.ApprovalInvalid, executed.Error);
        Assert.False(PendingWritten);
        Assert.False(ReloadWritten);

        var again = catalog.Plan("team-Sample", InstallTarget.Client).Plan!;
        Assert.Equal(InstallDecision.RestartClientRequired, again.Decision);
        Assert.Contains("Core reloaded after approval", again.Reason);
        // Revert: caching the admission result on the plan instead of re-evaluating at Execute
        // would run the approved ApplyNow hash after the generation changed.
    }

    [Fact]
    public void Local_server_target_without_a_runtime_connection_plans_restart_server_with_the_reason()
    {
        var (catalog, snapshots) = Service(_f.Valid, RecordJson(_f.Valid, "LocalServer"), localServer: true);
        snapshots.Snapshot = Live(server: false);
        var plan = catalog.Plan("team-Sample", InstallTarget.LocalServer).Plan!;
        Assert.Equal(InstallDecision.RestartServerRequired, plan.Decision);
        Assert.Contains(AdmissionReasonText.For(AdmissionReasonCode.LocalServerRuntimeUnavailable), plan.Reason);
        Assert.Contains(InstallReasons.RestartServer, plan.Reason);
        Assert.False(plan.ExecutorAvailable);

        snapshots.Snapshot = Live(server: true);
        Assert.Equal(InstallDecision.ApplyNow, catalog.Plan("team-Sample", InstallTarget.LocalServer).Plan!.Decision);
        // Revert: a configured server folder alone offering Apply now fails the first half.
    }

    [Fact]
    public void Legacy_planner_path_is_unchanged_when_no_admission_result_is_supplied()
    {
        var (catalog, _) = Service(_f.Valid, null);
        // The pipeline always supplies a result here; the legacy stub is only reached without one.
        var withoutAdmission = new InstallCatalogService(_root, new NoTransport());
        withoutAdmission.IndexSource = IndexKit.Mirror(Listing());
        Assert.True(withoutAdmission.LoadIndexFromJson(Listing(), "stamp").Ok);
        Assert.True(withoutAdmission.InspectLocalZip("team-Sample", Zip(_f.Valid)).Ok);
        var legacy = withoutAdmission.Plan("team-Sample", InstallTarget.Client).Plan!;
        Assert.Equal(InstallDecision.RestartClientRequired, legacy.Decision);
        Assert.Contains(InstallReasons.EligibilityNotWired, legacy.Reason);
        Assert.Null(legacy.Admission);
        Assert.NotNull(catalog.Plan("team-Sample", InstallTarget.Client).Plan!.Admission);
    }

    [Fact]
    public void Executor_gap_text_is_one_sentence_shared_by_plan_and_pane()
    {
        Assert.StartsWith(InstallModsCopy.ApplyNowUnavailable, InstallReasons.ExecutorNotImplemented, StringComparison.Ordinal);
        Assert.Equal("Apply now: not yet available (executor)", InstallModsCopy.ApplyNowUnavailable);
        // HOT2: the gap sentence still exists and is still shared, because a plan can still reach
        // ApplyNow with no executor for its root (a local server that is a different process). What
        // changed is that the pane's own lane is no longer closed: the executor, the Bootstrap
        // admission snapshot and the pre-write recheck all exist now.
        Assert.True(InstallModsPane.CanHotLoadFromThisPane);
        Assert.Contains(
            InstallModsCopy.ApplyNowUnavailable,
            InstallModsCopy.ReviewLine(InstallTarget.Client, InstallDecision.ApplyNow, "a.dll", executorAvailable: false));
    }
}
