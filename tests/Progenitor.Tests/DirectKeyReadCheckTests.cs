using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of <see cref="DirectKeyReadCheck"/>, the load-time metadata check that
/// flags a plugin referencing a direct key or device read. Images are built with
/// <see cref="MetadataBuilder"/>, so every case names exactly the references it plants.
/// </summary>
public class DirectKeyReadCheckTests
{
    [Theory]
    [InlineData("UnityEngine", "Input", "GetKeyDown")]
    [InlineData("UnityEngine", "Input", "GetKey")]
    [InlineData("UnityEngine", "Input", "GetButtonUp")]
    [InlineData("UnityEngine", "Input", "GetMouseButtonDown")]
    [InlineData("UnityEngine", "Input", "get_anyKeyDown")]
    [InlineData("UnityEngine", "Input", "get_inputString")]
    [InlineData("UnityEngine", "Event", "get_current")]
    [InlineData("UnityEngine.InputSystem", "Keyboard", "get_current")]
    [InlineData("UnityEngine.InputSystem", "Mouse", "get_current")]
    [InlineData("UnityEngine.InputSystem", "InputSystem", "get_devices")]
    [InlineData("UnityEngine.InputSystem", "InputSystem", "GetDevice")]
    [InlineData("UnityEngine.InputSystem", "InputSystem", "add_onEvent")]
    [InlineData("UnityEngine.InputSystem", "InputSystem", "get_onAnyButtonPress")]
    [InlineData("ProjectM", "InputState", "IsInputPressed")]
    [InlineData("ProjectM.Shared", "Anything", "IsInputDown")]
    public void A_planted_direct_read_is_found(string ns, string type, string member)
    {
        var hits = DirectKeyReadCheck.Find(Image((ns, type, member), ("System", "Console", "WriteLine")));
        Assert.Equal(new[] { ns + "." + type + "::" + member }, hits);
    }

    [Theory]
    [InlineData("UnityEngine", "Input", "get_mousePosition")]
    [InlineData("UnityEngine", "Input", "GetAxis")]
    [InlineData("UnityEngine.InputSystem", "InputSystem", "get_settings")]
    [InlineData("UnityEngine.InputSystem", "Keyboard", "get_f5Key")]
    [InlineData("UnityEngine.InputSystem", "Gamepad", "get_current")]
    [InlineData("Progenitor.Ui", "PluginKeybinds", "Register")]
    [InlineData("Progenitor.Ui", "Keybinds", "Register")]
    [InlineData("Progenitor.Plugins", "PluginContext", "get_Input")]
    [InlineData("Some.Mod", "Input", "GetKeyDown")]
    [InlineData("ProjectM", "InputState", "IsInputCapReached")]
    [InlineData("Other", "Thing", "IsInputPressed")]
    public void Keybind_api_and_unrelated_references_are_not_flagged(string ns, string type, string member)
    {
        Assert.Empty(DirectKeyReadCheck.Find(Image((ns, type, member))));
    }

    [Fact]
    public void Repeated_references_are_reported_once_and_sorted()
    {
        var hits = DirectKeyReadCheck.Find(Image(
            ("UnityEngine.InputSystem", "Mouse", "get_current"),
            ("UnityEngine", "Input", "GetKeyDown"),
            ("UnityEngine.InputSystem", "Mouse", "get_current")));
        Assert.Equal(new[] { "UnityEngine.Input::GetKeyDown", "UnityEngine.InputSystem.Mouse::get_current" }, hits);
    }

    [Fact]
    public void The_test_assembly_itself_is_clean()
    {
        // It names every pattern as string data and references the keybind types; neither is a read.
        string path = typeof(DirectKeyReadCheckTests).Assembly.Location;
        Assert.Empty(DirectKeyReadCheck.Find(File.ReadAllBytes(path)));
    }

    [Fact]
    public void Warning_line_names_the_plugin_the_reads_and_the_fix()
    {
        string line = DirectKeyReadCheck.WarningLine("Some Mod", new[] { "UnityEngine.Input::GetKeyDown" });
        Assert.Contains("Some Mod", line, StringComparison.Ordinal);
        Assert.Contains("UnityEngine.Input::GetKeyDown", line, StringComparison.Ordinal);
        Assert.Contains("ctx.Keybinds.Register", line, StringComparison.Ordinal);
        Assert.Equal("direct-key-read", DirectKeyReadCheck.Kind);
    }

    /// <summary>
    /// Built plugin and framework images, when present on this machine (they need the game's
    /// interop to build, so a clean checkout has none and this checks nothing). Every sample must be
    /// clean, and the dev bridge may reference only <see cref="DevBridgeAllowed"/>; Core's own image is
    /// the framework input layer and must be found, which proves the matcher reads real IL2CPP
    /// interop references.
    /// </summary>
    [Fact]
    public void Built_samples_and_dev_bridge_are_clean_and_core_is_detected_when_built()
    {
        string repo = SourcePin.RepoRoot();
        var dirty = new List<string>();
        foreach (string dll in BuiltImages(repo, "samples"))
        {
            var hits = DirectKeyReadCheck.Find(File.ReadAllBytes(dll));
            if (hits.Count > 0) dirty.Add(Path.GetRelativePath(repo, dll) + ": " + string.Join(", ", hits));
        }
        foreach (string dll in BuiltImages(repo, Path.Combine("src", "Progenitor.Dev")))
        {
            var hits = DirectKeyReadCheck.Find(File.ReadAllBytes(dll)).Where(h => !DevBridgeAllowed.Contains(h)).ToList();
            if (hits.Count > 0) dirty.Add(Path.GetRelativePath(repo, dll) + ": " + string.Join(", ", hits));
        }

        Assert.True(dirty.Count == 0, "Built images reference direct key reads:\n  " + string.Join("\n  ", dirty));

        foreach (string core in BuiltImages(repo, Path.Combine("src", "Progenitor.Core")).Where(p => Path.GetFileName(p) == "Progenitor.dll"))
            Assert.Contains("UnityEngine.InputSystem.Keyboard::get_current", DirectKeyReadCheck.Find(File.ReadAllBytes(core)));
    }

    /// <summary>
    /// The references the dev bridge's image may carry: <c>Keyboard.current</c> and <c>Mouse.current</c>, the
    /// devices its <c>key</c> and <c>mouse</c> ops queue events for (<c>src/Progenitor.Dev/Ops/KeyInjectOps.cs</c>, allowlisted with
    /// its reason in <see cref="DirectKeyReadSourceTests.Allowlist"/>). The op writes input; it binds no
    /// action to a key, and the source scan still keeps every other Dev file clean.
    /// </summary>
    private static readonly HashSet<string> DevBridgeAllowed = new(StringComparer.Ordinal)
    {
        "UnityEngine.InputSystem.Keyboard::get_current",
        // The mouse op (src/Progenitor.Dev/Ops/MouseInjectOps.cs): the device its queued mouse events are for.
        "UnityEngine.InputSystem.Mouse::get_current",
    };

    /// <summary>Each project's own output DLL under <paramref name="root"/>/**/bin (named after its folder).</summary>
    private static IEnumerable<string> BuiltImages(string repo, string root)
    {
        string dir = Path.Combine(repo, root);
        if (!Directory.Exists(dir)) yield break;
        foreach (string project in Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories))
        {
            string bin = Path.Combine(Path.GetDirectoryName(project)!, "bin");
            if (!Directory.Exists(bin)) continue;
            string assemblyName = AssemblyNameOf(project);
            foreach (string dll in Directory.EnumerateFiles(bin, assemblyName + ".dll", SearchOption.AllDirectories))
                yield return dll;
        }
    }

    private static string AssemblyNameOf(string project)
    {
        string text = File.ReadAllText(project);
        const string open = "<AssemblyName>";
        int at = text.IndexOf(open, StringComparison.Ordinal);
        if (at >= 0)
        {
            int end = text.IndexOf("</AssemblyName>", at, StringComparison.Ordinal);
            if (end > at) return text.Substring(at + open.Length, end - at - open.Length).Trim();
        }

        return Path.GetFileNameWithoutExtension(project);
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
