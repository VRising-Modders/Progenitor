using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Progenitor.Install;
using Progenitor.Install.Admission;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C3 fixtures: plugin images compiled by Roslyn with a stand-in <c>BepInEx.Core</c> and a stand-in
/// <c>Progenitor</c> assembly that carries <c>Progenitor.Plugins.NetworkCompatibilityAttribute</c> with the same shape as
/// the real one (byte enums, one- and two-argument constructors, a <c>Peers</c> property). Never loaded.
/// </summary>
public sealed class PackageCompatFixtures
{
    private readonly List<MetadataReference> _runtime = new();
    private readonly byte[] _bepInEx;
    private readonly byte[] _progenitor;

    public const string Guid = "team.trade";
    public const string Version = "1.4.0";

    public PackageCompatFixtures()
    {
        foreach (var path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
            if (path.Length > 0 && File.Exists(path) && !Path.GetFileName(path).StartsWith("Progenitor", StringComparison.OrdinalIgnoreCase))
                _runtime.Add(MetadataReference.CreateFromFile(path));

        _bepInEx = Compile("BepInEx.Core", @"
namespace BepInEx {
    public class BepInPlugin : System.Attribute { public BepInPlugin(string guid, string name, string version) { } }
    public abstract class BasePlugin { public abstract void Load(); }
}");
        _progenitor = Compile("Progenitor", @"
namespace Progenitor.Plugins {
    public enum NetworkCompatibilityLevel : byte { ServerMustHave = 0, NoNeedForSync = 1, ClientOnly = 2 }
    public enum NetworkVersionRule : byte { Exact = 0, Minor = 1, None = 2 }
    public sealed class NetworkCompatibilityAttribute : System.Attribute {
        public NetworkCompatibilityAttribute(NetworkCompatibilityLevel level) { }
        public NetworkCompatibilityAttribute(NetworkCompatibilityLevel level, NetworkVersionRule rule) { }
        public string Peers { get; set; }
    }
}");
    }

    /// <summary>A plugin image with <paramref name="compat"/> as its attribute line (empty: none).</summary>
    public byte[] Plugin(string compat) => Compile("Trade", @"
using BepInEx;
using Progenitor.Plugins;
namespace Trade {
    [BepInPlugin(""" + Guid + @""", ""Trade"", """ + Version + @""")]
    " + compat + @"
    public class Plugin : BasePlugin { public override void Load() { } }
}", withRefs: true);

    /// <summary>A plugin whose attribute line sits on a base class in the same image (generic or not).</summary>
    public byte[] DerivedPlugin(string compat, bool generic) => Compile("Trade", @"
using BepInEx;
using Progenitor.Plugins;
namespace Trade {
    " + compat + @"
    public abstract class TradeBase" + (generic ? "<T>" : "") + @" : BasePlugin { }
    public abstract class Middle : TradeBase" + (generic ? "<int>" : "") + @" { }
    [BepInPlugin(""" + Guid + @""", ""Trade"", """ + Version + @""")]
    public class Plugin : Middle { public override void Load() { } }
}", withRefs: true);

    private byte[] Compile(string name, string source, bool withRefs = false)
    {
        var references = new List<MetadataReference>(_runtime);
        if (withRefs)
        {
            references.Add(MetadataReference.CreateFromImage(_bepInEx));
            references.Add(MetadataReference.CreateFromImage(_progenitor));
        }

        var compilation = CSharpCompilation.Create(name,
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success, name + ": " + string.Join(Environment.NewLine, emit.Diagnostics));
        return ms.ToArray();
    }
}

/// <summary>
/// Mod compatibility C3: <c>compatibility.peers</c> in <c>progenitor.json</c> is parsed, an invalid range refuses the
/// package, and it must agree with the image's <c>[NetworkCompatibility]</c> <c>Peers</c> (as <c>side</c> agrees with
/// <c>[ProgenitorSide]</c>); the inspected range is kept in the verified-inspection cache. Game-free.
/// </summary>
public sealed class PackageCompatRangeTests : IClassFixture<PackageCompatFixtures>, IDisposable
{
    private readonly PackageCompatFixtures _f;
    private readonly string _dir;

    public PackageCompatRangeTests(PackageCompatFixtures f)
    {
        _f = f;
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-compat-pkg-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Sha(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private string Zip(byte[] image, string? peersJson)
    {
        var fields = new Dictionary<string, object?>
        {
            ["id"] = PackageCompatFixtures.Guid,
            ["version"] = PackageCompatFixtures.Version,
            ["side"] = "both",
            ["sha256"] = Sha(image),
        };
        if (peersJson != null) fields["compatibility"] = new Dictionary<string, object?> { ["peers"] = peersJson };
        string path = Path.Combine(_dir, System.Guid.NewGuid().ToString("N") + ".zip");
        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry("Trade.dll").Open()) s.Write(image, 0, image.Length);
            using (var w = new StreamWriter(zip.CreateEntry("progenitor.json").Open(), new UTF8Encoding(false)))
                w.Write(JsonSerializer.Serialize(fields));
        }

        return path;
    }

    [Fact]
    public void The_range_is_parsed_from_progenitor_json()
    {
        using var doc = JsonDocument.Parse("{\"id\":\"team.trade\",\"version\":\"1.4.0\",\"compatibility\":{\"peers\":\" >=1.2.0 <2.0.0 \"}}");
        Assert.True(PackageInspector.TryParseProgenitorJson(doc.RootElement, out var parsed, out var error), error);
        Assert.Equal(">=1.2.0 <2.0.0", parsed.Peers);
        Assert.Equal(">=1.2.0 <2.0.0", parsed.Manifest.Peers);

        using var none = JsonDocument.Parse("{\"id\":\"team.trade\",\"version\":\"1.4.0\"}");
        Assert.True(PackageInspector.TryParseProgenitorJson(none.RootElement, out var plain, out _));
        Assert.Equal("", plain.Peers);
        Assert.Null(plain.Manifest.Peers);
    }

    [Fact]
    public void An_invalid_range_refuses_the_package_with_a_readable_reason()
    {
        using var doc = JsonDocument.Parse("{\"id\":\"team.trade\",\"version\":\"1.4.0\",\"compatibility\":{\"peers\":\">=banana\"}}");
        Assert.False(PackageInspector.TryParseProgenitorJson(doc.RootElement, out _, out var error));
        Assert.StartsWith("progenitor.json compatibility.peers '>=banana' is not a valid compatibility range: ", error);

        var image = _f.Plugin("[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, Peers = \">=banana\")]");
        var inspection = PackageInspector.InspectFile(Zip(image, ">=banana"));
        Assert.False(inspection.Ok);
        Assert.StartsWith("progenitor.json compatibility.peers '>=banana'", inspection.Refusal);

        using var notString = JsonDocument.Parse("{\"id\":\"team.trade\",\"compatibility\":{\"peers\":5}}");
        Assert.False(PackageInspector.TryParseProgenitorJson(notString.RootElement, out _, out var e2));
        Assert.Contains("it is not a string", e2);
        using var notObject = JsonDocument.Parse("{\"id\":\"team.trade\",\"compatibility\":\"1.2.x\"}");
        Assert.False(PackageInspector.TryParseProgenitorJson(notObject.RootElement, out _, out var e3));
        Assert.StartsWith(InstallReasons.InspectContradictory, e3);
    }

    [Fact]
    public void A_range_that_agrees_with_the_image_is_accepted_and_kept()
    {
        var image = _f.Plugin("[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, Peers = \">= 1.2.0 <2.0.0\")]");
        var inspection = PackageInspector.InspectFile(Zip(image, ">=1.2.0 <2.0.0"));
        Assert.True(inspection.Ok, inspection.Refusal);
        Assert.Equal(CatalogProvenance.ManifestVerified, inspection.Provenance);
        Assert.Equal(">=1.2.0 <2.0.0", inspection.DeclaredPeers);
        Assert.Equal(">= 1.2.0 <2.0.0", inspection.EffectivePeers);   // the build's own text
    }

    [Fact]
    public void Neither_declaring_a_range_is_fine_and_the_effective_range_comes_from_the_image()
    {
        var undeclared = PackageInspector.InspectFile(Zip(_f.Plugin("[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave)]"), null));
        Assert.True(undeclared.Ok, undeclared.Refusal);
        Assert.Equal("", undeclared.DeclaredPeers);
        Assert.Equal("1.4.x", undeclared.EffectivePeers);

        var exact = PackageInspector.InspectFile(Zip(_f.Plugin(
            "[NetworkCompatibility(NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact)]"), null));
        Assert.True(exact.Ok, exact.Refusal);
        Assert.Equal("=1.4.0", exact.EffectivePeers);

        var any = PackageInspector.InspectFile(Zip(_f.Plugin(
            "[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.None)]"), null));
        Assert.Equal("*", any.EffectivePeers);

        var noAttribute = PackageInspector.InspectFile(Zip(_f.Plugin(""), null));
        Assert.True(noAttribute.Ok, noAttribute.Refusal);
        Assert.Equal("1.4.x", noAttribute.EffectivePeers);
    }

    [Fact]
    public void A_contradiction_refuses_in_both_directions_and_names_both_values()
    {
        var image = _f.Plugin("[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor, Peers = \"1.4.x\")]");
        var differ = PackageInspector.InspectFile(Zip(image, ">=1.2.0"));
        Assert.False(differ.Ok);
        Assert.Equal(InstallReasons.InspectContradictory + ": compatibility.peers is '>=1.2.0' but the plugin's [NetworkCompatibility] Peers is '1.4.x'",
            differ.Refusal);

        // In progenitor.json only.
        var jsonOnly = PackageInspector.InspectFile(Zip(_f.Plugin("[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave)]"), "1.4.x"));
        Assert.False(jsonOnly.Ok);
        Assert.EndsWith("compatibility.peers is '1.4.x' but the plugin's [NetworkCompatibility] Peers is not set", jsonOnly.Refusal);

        // In the image only.
        var imageOnly = PackageInspector.InspectFile(Zip(image, null));
        Assert.False(imageOnly.Ok);
        Assert.EndsWith("compatibility.peers is not set but the plugin's [NetworkCompatibility] Peers is '1.4.x'", imageOnly.Refusal);
        Assert.StartsWith(InstallReasons.InspectContradictory, imageOnly.Refusal);
    }

    [Fact]
    public void The_reader_returns_level_rule_and_peers_from_the_bytes()
    {
        var facts = Assert.Single(PluginCompatReader.Read(_f.Plugin(
            "[NetworkCompatibility(NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor, Peers = \"1.x\")]")));
        Assert.Equal(PackageCompatFixtures.Guid, facts.Guid);
        Assert.Equal(PackageCompatFixtures.Version, facts.Version);
        Assert.True(facts.Declared);
        Assert.Equal(NetworkCompatibilityLevel.NoNeedForSync, facts.Level);
        Assert.True(facts.RuleDeclared);
        Assert.Equal(NetworkVersionRule.Minor, facts.Rule);
        Assert.Equal("1.x", facts.DeclaredPeers);
        Assert.Equal("1.x", facts.EffectiveRange);
        Assert.Empty(PluginCompatReader.Read(new byte[16]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_reader_finds_an_attribute_inherited_from_a_base_class_in_the_image(bool generic)
    {
        // The attribute is Inherited and the runtime walks the base types, so the package must agree with it.
        var facts = Assert.Single(PluginCompatReader.Read(_f.DerivedPlugin(
            "[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, Peers = \"1.4.x\")]", generic)));
        Assert.True(facts.Declared);
        Assert.Equal("1.4.x", facts.DeclaredPeers);
        Assert.False(facts.RuleDeclared);

        var none = Assert.Single(PluginCompatReader.Read(_f.DerivedPlugin("", generic)));
        Assert.False(none.Declared);
        Assert.Equal("", none.DeclaredPeers);
    }

    [Fact]
    public void The_verified_cache_keeps_the_inspected_range()
    {
        var image = _f.Plugin("[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, Peers = \">=1.2.0 <2.0.0\")]");
        var inspection = PackageInspector.InspectFile(Zip(image, ">=1.2.0 <2.0.0"));
        Assert.True(inspection.Ok, inspection.Refusal);
        var entry = new CatalogEntry(
            new ManifestMod(PackageCompatFixtures.Guid, PackageCompatFixtures.Version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, false),
            CatalogProvenance.AdaptedListing, "thunderstore", "Thunderstore", null, owner: "Team", packageName: "Trade");
        var cache = new VerifiedManifestCache(Path.Combine(_dir, "BepInEx"));
        Assert.True(cache.Write(entry, inspection));

        var record = cache.Find("Team", "Trade", PackageCompatFixtures.Version)!;
        Assert.Equal(">=1.2.0 <2.0.0", record.Manifest.Peers);
        Assert.Equal(">=1.2.0 <2.0.0", record.EffectivePeers);
        Assert.Equal(">=1.2.0 <2.0.0", cache.EffectivePeersOf("Team-Trade", PackageCompatFixtures.Version));
        Assert.Null(cache.EffectivePeersOf("Team-Trade", "9.9.9"));
        Assert.Equal(">=1.2.0 <2.0.0", KeptCopyRanges.FromVerified(cache, "Team-Trade@" + PackageCompatFixtures.Version, "x"));
    }

    [Fact]
    public void The_package_script_reads_peers_from_the_image_bytes()
    {
        string? shell = PowerShell();
        if (shell == null) return;   // no PowerShell on this machine: the C# reader above covers the format
        string script = ReadRepoFile("tools", "make-dummy-package.ps1");
        string fn = ExtractFunction(script, "Get-DeclaredPeers");
        string dll = Path.Combine(_dir, "Trade.dll");
        File.WriteAllBytes(dll, _f.Plugin("[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, Peers = \">=1.2.0 <2.0.0\")]"));
        Assert.Equal("[>=1.2.0 <2.0.0]", Run(shell, fn + "\n'[' + (Get-DeclaredPeers $args[0]) + ']'\n", dll).Trim());
        File.WriteAllBytes(dll, _f.Plugin("[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave)]"));
        Assert.Equal("[]", Run(shell, fn + "\n'[' + (Get-DeclaredPeers $args[0]) + ']'\n", dll).Trim());
    }

    private static string ExtractFunction(string script, string name)
    {
        int start = script.IndexOf("function " + name, StringComparison.Ordinal);
        Assert.True(start >= 0, "tools/make-dummy-package.ps1 no longer defines " + name);
        int depth = 0;
        for (int i = script.IndexOf('{', start); i < script.Length; i++)
        {
            if (script[i] == '{') depth++;
            else if (script[i] == '}' && --depth == 0) return script.Substring(start, i - start + 1);
        }

        throw new InvalidOperationException("unbalanced braces in " + name);
    }

    private static string Run(string shell, string script, params string[] args)
    {
        string file = Path.Combine(Path.GetTempPath(), "progenitor-c3-" + System.Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, script, new UTF8Encoding(false));
        try
        {
            var psi = new ProcessStartInfo(shell) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }) psi.ArgumentList.Add(a);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            Assert.True(p.ExitCode == 0, shell + " exited " + p.ExitCode + ": " + stderr);
            return stdout;
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    private static string? PowerShell()
    {
        foreach (string exe in new[] { "powershell", "pwsh" })
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", "exit 0" }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p == null) continue;
                p.WaitForExit(60_000);
                if (p.ExitCode == 0) return exe;
            }
            catch { /* not installed under this name */ }
        }

        return null;
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Progenitor.sln")))
                return File.ReadAllText(Path.Combine(dir.FullName, Path.Combine(parts)));
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }
}
