using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using Progenitor.Install;
using Progenitor.PublishCheck;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// tools/publishcheck (docs/publishing.md). The two fixtures were generated with
/// <c>tools/make-dummy-package.ps1 -Sample HotDemo -Configuration Release -Publishable</c> and
/// <c>... -PublishFlaws -Package HotDemoFlawed</c>; the DLL scan is covered with planted images.
/// </summary>
public class PublishCheckTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string Good => Fixture("ExampleTeam-HotDemo-0.1.0.zip");
    private static string Flawed => Fixture("ExampleTeam-HotDemoFlawed-0.1.0.zip");

    private static CheckStatus StatusOf(PublishReport report, string id)
    {
        var r = report.Find(id);
        Assert.True(r != null, "no result for " + id + "; got " + string.Join(", ", report.Results.Select(x => x.Id)));
        return r!.Status;
    }

    [Fact]
    public void The_publishable_fixture_passes()
    {
        var report = PublishChecker.CheckPath(Good);
        Assert.False(report.Failed, string.Join("\n", report.Results.Where(r => r.Status == CheckStatus.Fail)));
        foreach (var id in new[]
        {
            "manifest.bom", "manifest.fields", "manifest.name", "manifest.version", "manifest.website", "manifest.description",
            "manifest.side", "deps.bepinex", "deps.progenitor", "icon", "readme.requirement", "pj.present", "pj.fields", "pj.id",
            "pj.version", "pj.side", "pj.sha256", "pj.peers", "pj.hotloadable", "dll.layout", "dll.bepindependency", "dll.keys",
            "dll.config", "files.dev",
        })
            Assert.Equal(CheckStatus.Pass, StatusOf(report, id));
    }

    [Fact]
    public void The_flawed_fixture_fails_every_planted_fault_and_nothing_else()
    {
        var report = PublishChecker.CheckPath(Flawed);
        var failed = report.Results.Where(r => r.Status == CheckStatus.Fail).Select(r => r.Id).OrderBy(x => x).ToArray();
        Assert.Equal(new[]
        {
            "deps.bepinex", "deps.progenitor", "files.dev", "icon", "manifest.bom", "manifest.side", "pj.sha256", "pj.side", "readme.requirement",
        }, failed);
    }

    [Fact]
    public void Progenitors_own_inspection_agrees_with_the_checker()
    {
        var good = PackageInspector.InspectFile(Good);
        Assert.True(good.Ok, good.Refusal);
        Assert.Equal(CatalogProvenance.ManifestVerified, good.Provenance);
        Assert.Equal(PackageSide.Both, good.Side);
        Assert.True(good.HotLoadableDeclared);
        Assert.False(good.UnsupportedLayout);

        var flawed = PackageInspector.InspectFile(Flawed);
        Assert.False(flawed.Ok);
    }

    [Fact]
    public void Generated_progenitor_json_matches_the_fixture_and_parses_as_Progenitor_reads_it()
    {
        var files = PublishChecker.ReadZip(Good);
        var dll = files["Progenitor.HotDemo.dll"];
        string? text = ProgenitorJsonWriter.Build(dll, hotLoadable: true, pluginId: null, out var error);
        Assert.True(text != null, error);

        using var generated = JsonDocument.Parse(text!);
        using var shipped = JsonDocument.Parse(files["progenitor.json"]);
        foreach (var key in new[] { "id", "version", "side", "sha256" })
            Assert.Equal(shipped.RootElement.GetProperty(key).GetString(), generated.RootElement.GetProperty(key).GetString());
        Assert.True(generated.RootElement.GetProperty("hotLoadable").GetBoolean());

        Assert.True(PackageInspector.TryParseProgenitorJson(generated.RootElement, out var parsed, out var parseError), parseError);
        Assert.Equal(PackageSide.Both, parsed.Side);
        Assert.Equal(PublishChecker.Sha256(dll), parsed.Sha256);

        string path = Path.Combine(Path.GetTempPath(), "publishcheck-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ProgenitorJsonWriter.Write(path, text!);
            Assert.False(PublishChecker.HasBom(File.ReadAllBytes(path)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Generator_refuses_a_non_assembly_and_hot_loading_an_unsafe_plugin()
    {
        Assert.Null(ProgenitorJsonWriter.Build(new byte[200], true, null, out var error));
        Assert.Contains("not a managed assembly", error, StringComparison.Ordinal);

        // A planted image without [BepInPlugin] has nothing to describe.
        Assert.Null(ProgenitorJsonWriter.Build(Image(("Progenitor.Plugins", "PluginContext", "get_UnsafeHarmony")), true, null, out error));
        Assert.Contains("[BepInPlugin]", error, StringComparison.Ordinal);
    }

    // ---- single faults on the good package --------------------------------------------------------

    private static PublishReport Mutated(Action<Dictionary<string, byte[]>> change)
    {
        var files = PublishChecker.ReadZip(Good);
        change(files);
        var report = new PublishReport { Package = "mutated" };
        PublishChecker.Check(files, report);
        return report;
    }

    private static void EditManifest(Dictionary<string, byte[]> files, string key, object value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(files["manifest.json"])!.AsObject();
        node[key] = System.Text.Json.Nodes.JsonValue.Create(value);
        files["manifest.json"] = Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    [Fact]
    public void A_long_description_fails()
    {
        var report = Mutated(f => EditManifest(f, "description", "Client and server · Requires Progenitor. " + new string('x', 250)));
        Assert.Equal(CheckStatus.Fail, StatusOf(report, "manifest.description"));
    }

    [Theory]
    [InlineData("Client-only · Requires Progenitor. A pitch.", CheckStatus.Fail)] // the mod is both-sided
    [InlineData("Client and server: Requires Progenitor. A pitch.", CheckStatus.Pass)]
    [InlineData("Requires Progenitor. A pitch.", CheckStatus.Fail)]
    public void The_description_starts_with_the_mods_side(string description, CheckStatus expected)
    {
        var report = Mutated(f => EditManifest(f, "description", description));
        Assert.Equal(expected, StatusOf(report, "manifest.side"));
    }

    [Theory]
    [InlineData("Hot-Demo", CheckStatus.Fail)]
    [InlineData("hot_demo", CheckStatus.Warn)]
    [InlineData("HotDemo", CheckStatus.Pass)]
    public void The_name_is_PascalCase(string name, CheckStatus expected) =>
        Assert.Equal(expected, StatusOf(Mutated(f => EditManifest(f, "name", name)), "manifest.name"));

    [Fact]
    public void A_two_part_version_fails() =>
        Assert.Equal(CheckStatus.Fail, StatusOf(Mutated(f => EditManifest(f, "version_number", "0.1")), "manifest.version"));

    [Fact]
    public void Endorsement_wording_warns() =>
        Assert.Equal(CheckStatus.Warn, StatusOf(Mutated(f => EditManifest(f, "description",
            "Client and server · Requires Progenitor. The official HotDemo.")), "manifest.wording"));

    [Fact]
    public void A_DLL_under_BepInEx_plugins_fails_the_layout()
    {
        static void Move(Dictionary<string, byte[]> f)
        {
            f["BepInEx/plugins/ExampleTeam-HotDemo/Progenitor.HotDemo.dll"] = f["Progenitor.HotDemo.dll"];
            f.Remove("Progenitor.HotDemo.dll");
        }

        Assert.Equal(CheckStatus.Fail, StatusOf(Mutated(Move), "dll.layout"));
        // Progenitor's own inspection marks the same layout unsupported; the planner then refuses a verified package.
        Assert.True(InspectMutated(Good, Move).UnsupportedLayout);
    }

    private static PackageInspection InspectMutated(string zip, Action<Dictionary<string, byte[]>> change)
    {
        var files = PublishChecker.ReadZip(zip);
        change(files);
        string path = Path.Combine(Path.GetTempPath(), "publishcheck-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (var kv in files)
                    using (var s = z.CreateEntry(kv.Key).Open()) s.Write(kv.Value, 0, kv.Value.Length);
            return PackageInspector.InspectFile(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Bundling_Progenitor_itself_fails() =>
        Assert.Equal(CheckStatus.Fail, StatusOf(Mutated(f => f["Progenitor.Bootstrap.dll"] = new byte[10]), "files.framework"));

    [Fact]
    public void A_missing_progenitor_json_fails() =>
        Assert.Equal(CheckStatus.Fail, StatusOf(Mutated(f => f.Remove("progenitor.json")), "pj.present"));

    [Fact]
    public void A_peer_range_the_image_does_not_declare_fails()
    {
        var report = Mutated(f =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(f["progenitor.json"])!.AsObject();
            node["compatibility"] = new System.Text.Json.Nodes.JsonObject { ["peers"] = ">=0.1.0 <1.0.0" };
            f["progenitor.json"] = Encoding.UTF8.GetBytes(node.ToJsonString());
        });
        Assert.Equal(CheckStatus.Fail, StatusOf(report, "pj.peers"));
    }

    /// <summary>Progenitor is not a package on Thunderstore or the Progenitor Index, so a dependency on it can never resolve.</summary>
    [Theory]
    [InlineData("VRising_Modders-Progenitor-2026.10.1")]
    [InlineData("SomeoneElse-Progenitor-2026.10.1")]
    public void A_dependency_on_a_Progenitor_package_fails(string dependency) =>
        Assert.Equal(CheckStatus.Fail, StatusOf(Mutated(f => EditManifest(f, "dependencies",
            new[] { "BepInEx-BepInExPack_V_Rising-1.733.2", dependency })), "deps.progenitor"));

    [Fact]
    public void A_bundled_library_that_reads_keys_and_binds_config_directly_warns()
    {
        var report = Mutated(f => f["Helper.dll"] = Image(
            ("UnityEngine.InputSystem", "Keyboard", "get_current"),
            ("BepInEx.Configuration", "ConfigFile", "Bind"),
            ("BepInEx.Configuration", "ConfigFile", ".ctor")));
        Assert.False(report.Failed, string.Join("\n", report.Results.Where(r => r.Status == CheckStatus.Fail)));
        Assert.Equal(CheckStatus.Pass, StatusOf(report, "dll.keys"));
        Assert.Equal(CheckStatus.Warn, StatusOf(report, "dll.keys-libraries"));
        Assert.Equal(CheckStatus.Warn, StatusOf(report, "dll.config"));
        Assert.Equal(CheckStatus.Warn, StatusOf(report, "dll.configfile"));
    }

    [Fact]
    public void Key_reads_in_the_plugin_DLL_itself_fail()
    {
        byte[] input = Compile("UnityEngine.InputSystem", "namespace UnityEngine.InputSystem { public class Keyboard { public static Keyboard current => null!; } }");
        byte[] dll = Compile("KeyMod", PluginSource("example.keymod", "Keyboard.current?.ToString();", usings: "using UnityEngine.InputSystem;"), input);
        var report = new PublishReport();
        PublishChecker.Check(PlainPackage(dll, "KeyMod.dll", "> **Works better with Progenitor.**"), report);
        Assert.Equal(CheckStatus.Fail, StatusOf(report, "dll.keys"));
    }

    // ---- review fixes (PR 79) -------------------------------------------------------------------

    /// <summary>A plain BepInEx plugin compiled with its own copy of the two attributes (names are all the scan reads).</summary>
    private static string PluginSource(string guid, string body = "", string dependency = "", string usings = "") => usings + @"
namespace BepInEx {
    public enum DependencyFlags { HardDependency = 1, SoftDependency = 2 }
    public class BepInPlugin : System.Attribute { public BepInPlugin(string guid, string name, string version) { } }
    public class BepInDependency : System.Attribute {
        public BepInDependency(string guid) { }
        public BepInDependency(string guid, DependencyFlags flags) { }
    }
}
namespace Example {
    [BepInEx.BepInPlugin(""" + guid + @""", ""Example"", ""1.0.0"")]
    " + dependency + @"
    public class Plugin { public void Load() { " + body + @" } }
}";

    private static byte[] Compile(string name, string source, params byte[][] references)
    {
        var refs = new List<Microsoft.CodeAnalysis.MetadataReference>();
        foreach (var path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
            if (path.Length > 0 && File.Exists(path)) refs.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));
        foreach (var r in references) refs.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromImage(r));
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(name,
            new[] { Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source) }, refs,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return ms.ToArray();
    }

    /// <summary>A package around a plain plugin: no progenitor.json, no Progenitor dependency.</summary>
    private static Dictionary<string, byte[]> PlainPackage(byte[] dll, string dllName, string readmeBlock)
    {
        var good = PublishChecker.ReadZip(Good);
        return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes(
                "{\"name\":\"Example\",\"version_number\":\"1.0.0\",\"website_url\":\"https://example.com/x\","
                + "\"description\":\"Client and server · Shows more when Progenitor is installed.\","
                + "\"dependencies\":[\"BepInEx-BepInExPack_V_Rising-1.733.2\"]}"),
            ["README.md"] = Encoding.UTF8.GetBytes("# Example\n\n" + readmeBlock + "\n"),
            ["CHANGELOG.md"] = good["CHANGELOG.md"],
            ["LICENSE"] = good["LICENSE"],
            ["icon.png"] = good["icon.png"],
            [dllName] = dll,
        };
    }

    private const string SoftDependency = "[BepInEx.BepInDependency(\"progenitor\", BepInEx.DependencyFlags.SoftDependency)]";

    [Fact]
    public void A_soft_dependency_on_Progenitor_passes_without_a_manifest_dependency_or_progenitor_json()
    {
        byte[] dll = Compile("Soft", PluginSource("example.soft", dependency: SoftDependency));
        var report = new PublishReport();
        PublishChecker.Check(PlainPackage(dll, "Soft.dll", "> **Works better with Progenitor.** With Progenitor it also gets in-game settings."), report);
        Assert.False(report.Failed, string.Join("\n", report.Results.Where(r => r.Status == CheckStatus.Fail)));
        Assert.Equal(CheckStatus.Pass, StatusOf(report, "readme.requirement"));
        Assert.Equal(CheckStatus.Pass, StatusOf(report, "pj.present"));
        Assert.Null(report.Find("deps.progenitor"));
    }

    [Fact]
    public void A_soft_dependency_without_the_works_better_block_fails()
    {
        byte[] dll = Compile("Soft", PluginSource("example.soft", dependency: SoftDependency));
        var report = new PublishReport();
        PublishChecker.Check(PlainPackage(dll, "Soft.dll", "Nothing about the framework."), report);
        Assert.Equal(CheckStatus.Fail, StatusOf(report, "readme.requirement"));
    }

    [Fact]
    public void A_hard_dependency_needs_progenitor_json_but_no_Progenitor_package_in_the_manifest()
    {
        byte[] dll = Compile("Hard", PluginSource("example.hard", dependency: "[BepInEx.BepInDependency(\"progenitor\")]"));
        var report = new PublishReport();
        PublishChecker.Check(PlainPackage(dll, "Hard.dll", "> **Requires Progenitor.**"), report);
        Assert.Equal(CheckStatus.Pass, StatusOf(report, "deps.progenitor"));
        Assert.Equal(CheckStatus.Fail, StatusOf(report, "pj.present"));
    }

    [Fact]
    public void The_writer_refuses_a_GUID_Progenitor_cannot_install()
    {
        byte[] dll = Compile("BadId", PluginSource("bad id/with slash"));
        Assert.Null(ProgenitorJsonWriter.Build(dll, false, null, out var error));
        Assert.Contains("cannot be installed", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_id_Progenitor_cannot_install_fails_the_check()
    {
        byte[] dll = Compile("BadId", PluginSource("bad id/with slash"));
        var files = PlainPackage(dll, "BadId.dll", "> **Requires Progenitor.**");
        files["progenitor.json"] = Encoding.UTF8.GetBytes("{\"id\":\"bad id/with slash\",\"version\":\"1.0.0\",\"side\":\"both\",\"hotLoadable\":false,\"sha256\":\""
            + PublishChecker.Sha256(dll) + "\"}");
        var report = new PublishReport();
        PublishChecker.Check(files, report);
        Assert.Equal(CheckStatus.Fail, StatusOf(report, "pj.id"));
        Assert.Contains("cannot be installed", report.Find("pj.id")!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("\"1.x\"")]
    [InlineData("{\"peers\":3}")]
    [InlineData("{\"peers\":[\"1.x\"]}")]
    public void Compatibility_and_peers_of_the_wrong_JSON_type_fail(string compatibility)
    {
        var report = Mutated(f =>
        {
            string text = Encoding.UTF8.GetString(f["progenitor.json"]).TrimEnd().TrimEnd('}') + ",\"compatibility\":" + compatibility + "}";
            f["progenitor.json"] = Encoding.UTF8.GetBytes(text);
        });
        var peers = report.Results.Where(r => r.Id == "pj.peers").ToList();
        Assert.Single(peers);
        Assert.Equal(CheckStatus.Fail, peers[0].Status);
        Assert.False(InspectMutated(Good, f =>
        {
            string text = Encoding.UTF8.GetString(f["progenitor.json"]).TrimEnd().TrimEnd('}') + ",\"compatibility\":" + compatibility + "}";
            f["progenitor.json"] = Encoding.UTF8.GetBytes(text);
        }).Ok);
    }

    [Fact]
    public void The_main_DLL_is_the_one_progenitor_json_names_whatever_the_zip_order()
    {
        var good = PublishChecker.ReadZip(Good);
        byte[] other = Compile("Aaa", PluginSource("example.first"));
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { ["Aaa.dll"] = other };
        foreach (var kv in good) files[kv.Key] = kv.Value;

        var report = new PublishReport();
        PublishChecker.Check(files, report);
        Assert.Equal(CheckStatus.Warn, StatusOf(report, "dll.plugin"));
        Assert.Contains("Progenitor.HotDemo.dll", report.Find("dll.plugin")!.Message, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Pass, StatusOf(report, "pj.id"));
        Assert.Equal(CheckStatus.Pass, StatusOf(report, "pj.sha256"));
        Assert.Equal(CheckStatus.Pass, StatusOf(report, "pj.side"));
    }

    [Fact]
    public void A_sha256_of_another_DLL_in_the_package_is_accepted_as_Progenitor_accepts_it()
    {
        byte[] helper = Image(("System", "Console", "WriteLine"));
        var report = Mutated(f =>
        {
            f["Helper.dll"] = helper;
            var node = System.Text.Json.Nodes.JsonNode.Parse(f["progenitor.json"])!.AsObject();
            node["sha256"] = PublishChecker.Sha256(helper);
            f["progenitor.json"] = Encoding.UTF8.GetBytes(node.ToJsonString());
        });
        Assert.Equal(CheckStatus.Warn, StatusOf(report, "pj.sha256"));
    }

    // ---- the metadata scan ------------------------------------------------------------------------

    [Theory]
    [InlineData("UnityEngine", "Input", "GetKeyDown")]
    [InlineData("UnityEngine.InputSystem", "Keyboard", "get_current")]
    [InlineData("UnityEngine.InputSystem", "Mouse", "get_current")]
    [InlineData("UnityEngine.InputSystem", "Gamepad", "get_current")]
    public void Direct_key_reads_are_found(string ns, string type, string member) =>
        Assert.Equal(new[] { ns + "." + type + "::" + member }, ImageFacts.Read(Image((ns, type, member))).DirectKeyReads);

    [Fact]
    public void Progenitor_settings_and_hot_reload_references_are_read()
    {
        var bindOnly = ImageFacts.Read(Image(("Progenitor.Plugins", "PluginContext", "Bind")));
        Assert.True(bindOnly.UsesProgenitorBind);
        Assert.False(bindOnly.RegistersSettingsSource);
        Assert.False(bindOnly.BindsConfigFileDirectly);

        var page = ImageFacts.Read(Image(("Progenitor.Plugins", "PluginConfig", "Bind"), ("Progenitor.Plugins", "PluginContext", "get_Settings")));
        Assert.True(page.UsesProgenitorBind);
        Assert.True(page.RegistersSettingsSource);

        Assert.True(ImageFacts.Read(Image(("Progenitor.Plugins", "PluginContext", "get_UnsafeHarmony"))).UsesUnsafeHarmony);
        Assert.True(ImageFacts.Read(Image(("Il2CppInterop.Runtime.Injection", "ClassInjector", "RegisterTypeInIl2Cpp"))).InjectsIl2CppTypes);
        Assert.Empty(ImageFacts.Read(Image(("Progenitor.Ui", "PluginKeybinds", "Register"))).DirectKeyReads);
    }

    [Fact]
    public void The_fixture_plugin_is_read_like_the_runtime_reads_it()
    {
        var facts = ImageFacts.Read(PublishChecker.ReadZip(Good)["Progenitor.HotDemo.dll"]);
        Assert.True(facts.IsManagedAssembly);
        var entry = Assert.Single(facts.Plugins);
        Assert.Equal("progenitor.hotdemo", entry.Guid);
        Assert.Equal("both", entry.SideText);
        Assert.NotNull(facts.ProgenitorReference);
        Assert.Contains(entry.Dependencies, d => d.Guid == "progenitor" && !d.Soft);
    }

    [Fact]
    public void A_PNG_size_is_read_from_IHDR()
    {
        var png = PublishChecker.ReadZip(Good)["icon.png"];
        Assert.True(PublishChecker.TryPngSize(png, out int w, out int h));
        Assert.Equal((256, 256), (w, h));
        Assert.False(PublishChecker.TryPngSize(Encoding.ASCII.GetBytes("GIF89a not a png at all......."), out _, out _));
    }

    /// <summary>A minimal library image whose MemberRef table holds exactly <paramref name="refs"/>.</summary>
    private static byte[] Image(params (string Ns, string Type, string Member)[] refs)
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

/// <summary>The hotLoadable verdict is the Install tab's own (HotLoadTrust.CleanProgenitorImage), on the admission fixtures.</summary>
public class PublishCheckHotLoadTests : IClassFixture<AdmissionFixtures>
{
    private readonly AdmissionFixtures _f;

    public PublishCheckHotLoadTests(AdmissionFixtures f) => _f = f;

    [Fact]
    public void MakeGenericType_is_refused_for_hotLoadable_by_the_writer_and_the_Install_tab()
    {
        Assert.False(HotLoadTrust.CleanProgenitorImage(_f.MakeGenericTypeEscape, out _));
        Assert.Null(ProgenitorJsonWriter.Build(_f.MakeGenericTypeEscape, hotLoadable: true, null, out var error));
        Assert.Contains("reflection", error, StringComparison.Ordinal);
        Assert.Contains("-HotLoadable", error, StringComparison.Ordinal);
        Assert.NotNull(ProgenitorJsonWriter.Build(_f.MakeGenericTypeEscape, hotLoadable: false, null, out _));
    }

    [Fact]
    public void The_writer_agrees_with_the_Install_tab_on_every_admission_fixture()
    {
        var images = new Dictionary<string, byte[]>
        {
            ["Valid"] = _f.Valid, ["UnsafeDirect"] = _f.UnsafeDirect, ["ClassInjector"] = _f.ClassInjector, ["PInvoke"] = _f.PInvoke,
            ["Calli"] = _f.Calli, ["ReflectionEscape"] = _f.ReflectionEscape, ["MakeGenericType"] = _f.MakeGenericTypeEscape,
            ["UnresolvedDependency"] = _f.UnresolvedDependencyMain, ["HarmonyDirect"] = _f.HarmonyDirect, ["PlainBasePlugin"] = _f.PlainBasePlugin,
            ["ServerSide"] = _f.ServerSide,
        };
        foreach (var (name, image) in images)
        {
            bool clean = HotLoadTrust.CleanProgenitorImage(image, out _);
            bool written = ProgenitorJsonWriter.Build(image, hotLoadable: true, null, out var error) != null;
            Assert.True(clean == written, name + ": Install tab clean=" + clean + ", writer error=" + error);
        }
        Assert.True(HotLoadTrust.CleanProgenitorImage(_f.Valid, out _));
    }

    [Fact]
    public void The_check_fails_hotLoadable_on_an_image_the_Install_tab_would_not_load_hot()
    {
        var files = PublishChecker.ReadZip(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ExampleTeam-HotDemo-0.1.0.zip"));
        files.Remove("Progenitor.HotDemo.dll");
        files["Sample.dll"] = _f.MakeGenericTypeEscape;
        var node = System.Text.Json.Nodes.JsonNode.Parse(files["progenitor.json"])!.AsObject();
        node["id"] = AdmissionFixtures.SampleId;
        node["version"] = "0.1.0";
        node["side"] = "client";
        node["sha256"] = PublishChecker.Sha256(_f.MakeGenericTypeEscape);
        files["progenitor.json"] = Encoding.UTF8.GetBytes(node.ToJsonString());
        var report = new PublishReport();
        PublishChecker.Check(files, report);
        var hot = report.Find("pj.hotloadable");
        Assert.NotNull(hot);
        Assert.Equal(CheckStatus.Fail, hot!.Status);
        Assert.Contains("reflection", hot.Message, StringComparison.Ordinal);
    }
}
