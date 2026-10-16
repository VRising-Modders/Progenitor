using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Progenitor.Reload;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// A generic a plugin defines and instantiates with Core types is created in Core's load context,
/// which then keeps the plugin alive until that Core generation unloads (observed live 2026-10-16
/// with the Controls sample's <c>On&lt;T&gt;</c>: Core swaps were refused until a restart).
/// Game-free: the images are compiled with Roslyn, and the runtime half loads them into real
/// collectible contexts and reads the runtime's own cross-reference table.
/// </summary>
public sealed class CrossContextGenericsTests
{
    // ---- fixtures ------------------------------------------------------------------------------

    private const string CoreSource = @"
namespace Progenitor
{
    public interface IUiHandle { }
    public interface IUiButton : IUiHandle { }
    public sealed class Button : IUiButton { }
    public static class Ui
    {
        public static IUiButton MakeButton() => new Button();
        public static T Echo<T>(T value) => value;
    }
    public sealed class CoreBox<T> { public T Value; }
}";

    /// <summary>
    /// One method per case. <c>Run(name)</c> calls it, so the JIT meets only that case's
    /// instantiation. <c>Compiled</c> is compiled but never takes the branch that calls it.
    /// </summary>
    private const string PluginSource = @"
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Progenitor;
namespace FakePlugin
{
    public sealed class Mine { }
    public sealed class Box<T> { public T Value; }
    public sealed class Holder<T> { public static int Inner<U>() => 1; }
    public static class Demo
    {
        private static T On<T>(T handle) where T : IUiHandle => handle;
        private static IUiHandle OnPlain(IUiHandle handle) => handle;
        private static int Pair<A, B>(A a, B b) => 1;
        private static int One<A>(A a) => 1;
        // Not inlined into Run: inlining would compile its Pair<Mine, IUiButton> for every case.
        [MethodImpl(MethodImplOptions.NoInlining)] private static int Open<T>() => Pair<T, IUiButton>(default(T), null);

        public static int Run(string name)
        {
            switch (name)
            {
                case ""generic"": return Generic();
                case ""plain"": return Plain();
                case ""mixed-self-core"": return MixedSelfCore();
                case ""mixed-bcl-core"": return MixedBclCore();
                case ""bcl-only"": return BclOnly();
                case ""self-only"": return SelfOnly();
                case ""nested-bcl-core"": return NestedBclCore();
                case ""nested-bcl-self-core"": return NestedBclSelfCore();
                case ""nested-corebox-self"": return NestedCoreBoxSelf();
                case ""array-core"": return ArrayCore();
                case ""own-type-core"": return OwnTypeCore();
                case ""core-generic-self"": return CoreGenericSelf();
                case ""compiled-only"": return Compiled(false);
                case ""method-on-own-generic-type"": return MethodOnOwnGenericType();
                case ""open-with-core"": return Open<Mine>();
                default: return 0;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)] static int Generic() => On(Ui.MakeButton()) != null ? 1 : 0;
        [MethodImpl(MethodImplOptions.NoInlining)] static int Plain() => OnPlain(Ui.MakeButton()) != null ? 1 : 0;
        [MethodImpl(MethodImplOptions.NoInlining)] static int MixedSelfCore() => Pair(new Mine(), Ui.MakeButton());
        [MethodImpl(MethodImplOptions.NoInlining)] static int MixedBclCore() => Pair(""x"", Ui.MakeButton());
        [MethodImpl(MethodImplOptions.NoInlining)] static int BclOnly() => Pair(""x"", 1);
        [MethodImpl(MethodImplOptions.NoInlining)] static int SelfOnly() => One(new Mine());
        [MethodImpl(MethodImplOptions.NoInlining)] static int NestedBclCore() => One(new List<IUiButton>());
        [MethodImpl(MethodImplOptions.NoInlining)] static int NestedBclSelfCore() => One(new Dictionary<IUiButton, Mine>());
        [MethodImpl(MethodImplOptions.NoInlining)] static int NestedCoreBoxSelf() => One(new CoreBox<Mine>());
        [MethodImpl(MethodImplOptions.NoInlining)] static int ArrayCore() => One(new IUiButton[0]);
        [MethodImpl(MethodImplOptions.NoInlining)] static int OwnTypeCore() { var b = new Box<IUiButton>(); return b.Value == null ? 1 : 0; }
        [MethodImpl(MethodImplOptions.NoInlining)] static int CoreGenericSelf() => Ui.Echo(new Mine()) != null ? 1 : 0;
        [MethodImpl(MethodImplOptions.NoInlining)] static int Compiled(bool go) { if (go) return One(Ui.MakeButton()); return 0; }
        [MethodImpl(MethodImplOptions.NoInlining)] static int MethodOnOwnGenericType() => Holder<Mine>.Inner<IUiButton>();
    }
}";

    /// <summary>Case, the member the scan must name (or null), the instantiation it must list.</summary>
    public static IEnumerable<object?[]> Cases() => new[]
    {
        new object?[] { "generic", "FakePlugin.Demo.On", "On<IUiButton>" },
        new object?[] { "plain", null, null },
        // The plugin's own type is skipped (it is the defining context), so Core still wins.
        new object?[] { "mixed-self-core", "FakePlugin.Demo.Pair", "Pair<Mine, IUiButton>" },
        // A runtime-library argument is never a candidate.
        new object?[] { "mixed-bcl-core", "FakePlugin.Demo.Pair", "Pair<string, IUiButton>" },
        new object?[] { "bcl-only", null, null },
        new object?[] { "self-only", null, null },
        // List<IUiButton> itself lives in Core, so One<List<IUiButton>> does too.
        new object?[] { "nested-bcl-core", "FakePlugin.Demo.One", "One<List<IUiButton>>" },
        // Dictionary<IUiButton, Mine> lives in the plugin (the newer context): no pin.
        new object?[] { "nested-bcl-self-core", null, null },
        new object?[] { "nested-corebox-self", null, null },
        new object?[] { "array-core", "FakePlugin.Demo.One", "One<IUiButton[]>" },
        new object?[] { "own-type-core", "FakePlugin.Box", "Box<IUiButton>" },
        // A generic Core defines over a plugin type lives in the plugin.
        new object?[] { "core-generic-self", null, null },
        new object?[] { "compiled-only", "FakePlugin.Demo.One", "One<IUiButton>" },
        new object?[] { "method-on-own-generic-type", "FakePlugin.Holder.Inner", "Inner<IUiButton>" },
    };

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
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        return ms.ToArray();
    }

    private static readonly byte[] CoreImage = Compile("Progenitor", CoreSource);
    private static readonly byte[] PluginImage = Compile("FakePlugin", PluginSource, CoreImage);

    private static readonly Lazy<IReadOnlyList<CrossContextGeneric>> PluginScan = new(() =>
    {
        var hits = CrossContextGenerics.Scan(PluginImage, null, out string error);
        Assert.Equal("", error);
        return hits;
    });

    // ---- the rule ------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_scan_names_exactly_the_instantiations_that_live_in_Core(string name, string? member, string? instantiation)
    {
        _ = name;
        if (member == null) return; // covered by the negative list below
        var hit = Assert.Single(PluginScan.Value, h => h.Member == member);
        Assert.Contains(instantiation!, hit.Instantiations);
        Assert.Equal(new[] { "Progenitor" }, hit.Holders);
    }

    [Fact]
    public void The_scan_names_nothing_else()
    {
        var expected = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var c in Cases())
            if (c[2] is string inst) expected.Add(c[1] + " " + inst);
        // Open<T>'s Pair<T, IUiButton> is decided by its Core argument whatever T turns out to be.
        expected.Add("FakePlugin.Demo.Pair Pair<?, IUiButton>");
        var actual = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var h in PluginScan.Value)
            foreach (var i in h.Instantiations) actual.Add(h.Member + " " + i);
        Assert.Equal(expected, actual);
    }

    // Homes as letters: X fixed (never unloads), S the plugin, F Core, U an open parameter.
    [Theory]
    [InlineData("S", "F", "F")]
    [InlineData("S", "SF", "F")]
    [InlineData("S", "XF", "F")]
    [InlineData("S", "UF", "F")]
    [InlineData("S", "XS", "S")]
    [InlineData("S", "U", "S")]
    [InlineData("X", "F", "F")]
    [InlineData("X", "FS", "S")]
    [InlineData("X", "FU", "U")]
    [InlineData("X", "X", "X")]
    [InlineData("F", "S", "S")]
    [InlineData("F", "FX", "F")]
    public void Homes_follow_the_runtime_choice(string definition, string args, string expected)
    {
        static ContextHome H(char c) => c switch
        {
            'S' => ContextHome.Self,
            'F' => ContextHome.Framework,
            'U' => ContextHome.Unknown,
            _ => ContextHome.Fixed,
        };
        Assert.Equal(H(expected[0]), CrossContextGenerics.Instantiate(H(definition[0]), args.Select(H).ToArray(), out _));
    }

    // ---- the runtime agrees ----------------------------------------------------------------------

    private static readonly Lazy<string> Folder = new(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-xctx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Progenitor.dll"), CoreImage);
        File.WriteAllBytes(Path.Combine(dir, "FakePlugin.dll"), PluginImage);
        return dir;
    });

    private sealed class Pair : IDisposable
    {
        internal readonly AssemblyLoadContext CoreContext = new("xctx-core", isCollectible: true);
        internal readonly AssemblyLoadContext PluginContext = new("xctx-plugin", isCollectible: true);
        internal readonly Assembly Core;
        internal readonly Assembly Plugin;

        internal Pair()
        {
            Core = CoreContext.LoadFromAssemblyPath(Path.Combine(Folder.Value, "Progenitor.dll"));
            var core = Core;
            PluginContext.Resolving += (_, n) => n.Name == "Progenitor" ? core : null;
            Plugin = PluginContext.LoadFromAssemblyPath(Path.Combine(Folder.Value, "FakePlugin.dll"));
        }

        internal Type CoreType => Core.GetType("Progenitor.Ui")!;
        internal Type PluginType => Plugin.GetType("FakePlugin.Demo")!;

        internal void Run(string name) => PluginType.GetMethod("Run")!.Invoke(null, new object[] { name });

        internal bool CoreHoldsPlugin() =>
            CrossContextGenerics.FindReference(
                CrossContextGenerics.LoaderAllocatorOf(CoreType),
                CrossContextGenerics.LoaderAllocatorOf(PluginType)) >= 0;

        public void Dispose()
        {
            PluginContext.Unload();
            CoreContext.Unload();
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_runtime_records_a_Core_to_plugin_reference_exactly_when_the_scan_says_so(string name, string? member, string? instantiation)
    {
        _ = instantiation;
        using var pair = new Pair();
        Assert.NotNull(CrossContextGenerics.LoaderAllocatorOf(pair.CoreType));
        Assert.NotNull(CrossContextGenerics.LoaderAllocatorOf(pair.PluginType));
        Assert.Null(CrossContextGenerics.LoaderAllocatorOf(typeof(string)));
        Assert.False(pair.CoreHoldsPlugin());
        pair.Run(name);
        Assert.Equal(member != null, pair.CoreHoldsPlugin());
    }

    [Fact]
    public void The_pin_diagnosis_names_the_generic_and_the_slot()
    {
        using var pair = new Pair();
        pair.Run("generic");
        var lines = new List<string>();
        CrossContextGenerics.ClearRemembered();
        try
        {
            int n = PluginPinHolders.DiagnoseRuntimeReferences(pair.PluginType, pair.CoreType, lines.Add);
            Assert.True(n >= 2, string.Join("\n", lines));
            Assert.Contains(CrossContextGenerics.DiagnosisLine("FakePlugin.Demo.On"), lines);
            Assert.Contains("pin-diag plugin-holder runtime-generic FakePlugin.Demo.On -> released when this Core generation unloads", lines);
            Assert.Contains(lines, l => Regex.IsMatch(l,
                @"^pin-diag plugin-holder <Core LoaderAllocator>\.m_slots\[\d+\] -> load context xctx-plugin \(runtime cross-reference"));
        }
        finally
        {
            PluginPinHolders.Clear();
        }
    }

    [Fact]
    public void Admission_warns_once_per_member_and_remembers_the_result()
    {
        using var pair = new Pair();
        CrossContextGenerics.ClearRemembered();
        var warnings = new List<string>();
        var hits = CrossContextGenerics.Admit(pair.Plugin, warnings.Add);
        Assert.Equal(hits.Count, warnings.Count);
        Assert.Contains(warnings, w => w.StartsWith(CrossContextGenerics.WarningLine("FakePlugin.Demo.On") + " Instantiations: On<IUiButton>", StringComparison.Ordinal));
        Assert.Same(hits, CrossContextGenerics.Recall("FakePlugin"));
        // An assembly that never unloads is not checked.
        Assert.Empty(CrossContextGenerics.Admit(typeof(string).Assembly, warnings.Add));
        CrossContextGenerics.ClearRemembered();
    }

    [Fact]
    public void The_messages_are_stable()
    {
        Assert.Equal(
            "X.On is a generic defined in this plugin and instantiated only with Progenitor types; the runtime will keep "
            + "this plugin loaded until Progenitor Core unloads, so hot reloads of it leak and block Core swaps. Make the "
            + "helper non-generic.",
            CrossContextGenerics.WarningLine("X.On"));
        Assert.Equal("pin-diag plugin-holder runtime-generic X.On -> released when this Core generation unloads",
            CrossContextGenerics.DiagnosisLine("X.On"));
    }

    [Fact]
    public void An_unreadable_image_has_no_findings()
    {
        Assert.Empty(CrossContextGenerics.Scan(new byte[] { 1, 2, 3 }, null, out string error));
        Assert.NotEqual("", error);
        Assert.Empty(CrossContextGenerics.Scan((byte[]?)null, null, out _));
    }

    // ---- the guard over every built plugin in this repository -------------------------------------

    private static string FindRepoRoot()
    {
        for (var dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "Progenitor.sln"))) return dir;
        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }

    /// <summary>The newest built image of every sample, template and the Dev plugin that exists on disk.</summary>
    private static List<string> BuiltPlugins(string root)
    {
        var projects = new List<string>();
        foreach (var top in new[] { "samples", "templates" })
        {
            string dir = Path.Combine(root, top);
            if (Directory.Exists(dir)) projects.AddRange(Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories));
        }
        projects.Add(Path.Combine(root, "src", "Progenitor.Dev", "Progenitor.Dev.csproj"));

        var images = new List<string>();
        foreach (var csproj in projects)
        {
            if (!File.Exists(csproj)) continue;
            string projectDir = Path.GetDirectoryName(csproj)!;
            string bin = Path.Combine(projectDir, "bin");
            if (!Directory.Exists(bin)) continue;
            var m = Regex.Match(File.ReadAllText(csproj), "<AssemblyName>([^<$]+)</AssemblyName>");
            string name = m.Success ? m.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(csproj);
            var newest = Directory.GetFiles(bin, name + ".dll", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "ref" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest != null) images.Add(newest);
        }
        return images;
    }

    [Fact]
    public void No_built_sample_template_or_Dev_plugin_defines_a_generic_that_lives_in_Core()
    {
        // The samples build only against an installed game; without one this has nothing to read and
        // the Roslyn cases above carry the rule.
        string root = FindRepoRoot();
        var report = new StringBuilder();
        foreach (string image in BuiltPlugins(root))
        {
            var hits = CrossContextGenerics.ScanFile(image, null, out string error);
            Assert.True(error.Length == 0, image + ": " + error);
            foreach (var hit in hits)
                report.Append(Path.GetRelativePath(root, image)).Append(": ").Append(hit.Member)
                    .Append(" <- ").Append(string.Join(", ", hit.Instantiations)).AppendLine();
        }
        Assert.True(report.Length == 0,
            "These generics are defined in a plugin and instantiated with Progenitor types, so the runtime creates "
            + "them in Core's load context and keeps the plugin alive until Core unloads (docs/hot-reload.md, "
            + "\"Generic helpers\"). Make each helper non-generic, then rebuild:" + Environment.NewLine + report);
    }
}
