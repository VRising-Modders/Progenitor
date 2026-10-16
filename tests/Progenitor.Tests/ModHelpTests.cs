using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The mod page's Help button: where a mod's HTML help comes from (its own folder, an embedded resource extracted to a
/// version-scoped cache, or a declared path), that nothing outside those folders is ever opened, and the <c>file://</c>
/// URI the browser gets. Game-free: temporary folders only.
/// </summary>
public sealed class ModHelpTests : IDisposable
{
    private readonly string _root;

    public ModHelpTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "progenitor-help-" + Guid.NewGuid().ToString("N"), "My Game", "BepInEx");
        Directory.CreateDirectory(_root);
        PackageManifestBeside.RevalidateMs = 0;
        PackageManifestBeside.ForgetCache();
        ModHelpStore.ResetForTests();
    }

    public void Dispose()
    {
        ModHelpStore.ResetForTests();
        PackageManifestBeside.ForgetCache();
        try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_root)!)!, recursive: true); }
        catch { /* temp */ }
    }

    private string Cache => Path.Combine(_root, "cache");

    private string Dll(params string[] parts)
    {
        string path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A });
        return path;
    }

    private static string Write(string path, string text = "<html></html>")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static ModHelpResource Res(string path, string text) => new(path, Encoding.UTF8.GetBytes(text));

    /// <summary>What this would catch: a mod's <c>help/index.html</c> in its own folder not being found.</summary>
    [Fact]
    public void Help_in_the_mods_own_folder_is_found()
    {
        string dll = Dll("plugins", "Corvane-Minimap", "Minimap.dll");
        Assert.False(ModHelp.Resolve("example.minimap", dll, null, null, Cache).Found);
        string index = Write(Path.Combine(_root, "plugins", "Corvane-Minimap", "help", "index.html"));
        var target = ModHelp.Resolve("example.minimap", dll, null, null, Cache);
        Assert.Equal(ModHelpSource.Folder, target.Source);
        Assert.Equal("folder", target.SourceName);
        Assert.Equal(ModHelp.IndexPath, target.Relative);
        Assert.Equal(Path.GetFullPath(index), Path.GetFullPath(target.File));
        Assert.Null(target.Extract);
    }

    /// <summary>
    /// A mod-manager package with its DLL one folder down finds the help at the package root.
    /// What this would catch: only looking beside the DLL in the <c>Owner-Name/plugins/X.dll</c> layout.
    /// </summary>
    [Fact]
    public void Help_at_the_package_folder_is_found_for_a_dll_one_folder_down()
    {
        string pkg = Path.Combine(_root, "plugins", "Corvane-Minimap");
        string dll = Dll("plugins", "Corvane-Minimap", "plugins", "Minimap.dll");
        File.WriteAllText(Path.Combine(pkg, "manifest.json"), JsonSerializer.Serialize(new { name = "Minimap", version_number = "1.0.0", description = "" }));
        Write(Path.Combine(pkg, "help", "index.html"));
        var target = ModHelp.Resolve("example.minimap", dll, null, null, Cache);
        Assert.Equal(ModHelpSource.Folder, target.Source);
        Assert.Equal(Path.GetFullPath(pkg), Path.GetFullPath(target.Root));
    }

    /// <summary>
    /// A shared folder's <c>help/index.html</c> belongs to no one mod; the DLL's own name does.
    /// What this would catch: every plugin in <c>BepInEx/plugins</c> or <c>progenitor-reload</c> offering one stray help page.
    /// </summary>
    [Theory]
    [InlineData("plugins")]
    [InlineData("progenitor-reload")]
    public void A_shared_folders_help_is_not_taken_but_the_dlls_own_name_and_folder_are(string shared)
    {
        string dll = Dll(shared, "Minimap.dll");
        Write(Path.Combine(_root, shared, "help", "index.html"));
        Assert.False(ModHelp.Resolve("example.minimap", dll, null, null, Cache).Found);

        // <shared>/<AssemblyName>/help/index.html, the plugin-local folder convention.
        string own = Write(Path.Combine(_root, shared, "Minimap", "help", "index.html"));
        var target = ModHelp.Resolve("example.minimap", dll, null, null, Cache);
        Assert.Equal(ModHelpSource.Folder, target.Source);
        Assert.Equal(Path.GetFullPath(own), Path.GetFullPath(target.File));
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, shared, "Minimap")), Path.GetFullPath(target.Root));

        // <dll name>.help.html beside the DLL wins: it names this mod even in a shared folder.
        string beside = Write(Path.Combine(_root, shared, "Minimap.help.html"));
        target = ModHelp.Resolve("example.minimap", dll, null, null, Cache);
        Assert.Equal(Path.GetFullPath(beside), Path.GetFullPath(target.File));
        Assert.Equal("Minimap.help.html", target.Relative);
    }

    /// <summary>What this would catch: drawing a Help button that opens nothing.</summary>
    [Fact]
    public void No_help_anywhere_is_no_target()
    {
        string dll = Dll("plugins", "Corvane-Minimap", "Minimap.dll");
        var target = ModHelp.Resolve("example.minimap", dll, ModHelpFacts.Empty, null, Cache);
        Assert.False(target.Found);
        Assert.Equal("none", target.SourceName);
        Assert.False(ModHelp.Resolve("example.minimap", null, null, null, Cache).Found);
        Assert.False(ModHelp.Resolve("example.minimap", "", null, "help.html", null).Found);
        Assert.Null(ModHelp.Materialize(ModHelpTarget.None, out string error));
        Assert.NotEmpty(error);
    }

    /// <summary>
    /// Embedded help is extracted, with its sibling files, into <c>cache/progenitor-help/&lt;guid&gt;/&lt;version&gt;/</c>, so a
    /// new version never opens the old one's page.
    /// What this would catch: extracting into a shared or unversioned folder, or leaving the stylesheet behind.
    /// </summary>
    [Fact]
    public void Embedded_help_is_extracted_to_a_version_scoped_cache_with_its_assets()
    {
        string dll = Dll("progenitor-reload", "Minimap.dll");
        var facts = new ModHelpFacts
        {
            Version = "1.2.0",
            Embedded = new[] { Res("help/index.html", "<link href=\"style.css\">v1.2"), Res("help/style.css", "body{}"), Res("help/img/logo.png", "png") },
        };
        var target = ModHelp.Resolve("example.minimap", dll, facts, null, Cache);
        Assert.Equal(ModHelpSource.Embedded, target.Source);
        string folder = Path.Combine(Cache, ModHelp.CacheFolderName, "example.minimap", "1.2.0");
        Assert.Equal(Path.GetFullPath(folder), Path.GetFullPath(target.Root));
        Assert.False(File.Exists(target.File)); // nothing is written until the button is pressed

        string? file = ModHelp.Materialize(target, out string error);
        Assert.Equal("", error);
        Assert.Equal(Path.GetFullPath(Path.Combine(folder, "help", "index.html")), file);
        Assert.Equal("<link href=\"style.css\">v1.2", File.ReadAllText(file!));
        Assert.Equal("body{}", File.ReadAllText(Path.Combine(folder, "help", "style.css")));
        Assert.True(File.Exists(Path.Combine(folder, "help", "img", "logo.png")));

        var next = ModHelp.Resolve("example.minimap", dll, new ModHelpFacts { Version = "1.3.0", Embedded = new[] { Res("help/index.html", "v1.3") } }, null, Cache);
        string? nextFile = ModHelp.Materialize(next, out _);
        Assert.Contains(Path.DirectorySeparatorChar + "1.3.0" + Path.DirectorySeparatorChar, nextFile, StringComparison.Ordinal);
        Assert.Equal("v1.3", File.ReadAllText(nextFile!));
        Assert.Equal("<link href=\"style.css\">v1.2", File.ReadAllText(file!));

        // A single-file Help.html is the fallback name.
        var single = ModHelp.Resolve("example.single", dll, new ModHelpFacts { Version = "1", Embedded = new[] { Res("Help.html", "x") } }, null, Cache);
        Assert.Equal(ModHelpSource.Embedded, single.Source);
        Assert.Equal(ModHelp.SingleName, single.Relative);
    }

    /// <summary>
    /// The resource names a project produces: <c>LogicalName="help/..."</c> keeps its folders, the SDK default
    /// <c>&lt;Assembly&gt;.help.&lt;rest&gt;</c> lands under <c>help/</c>, and anything else (a README, another assembly's
    /// help, a name that climbs out) is not taken.
    /// What this would catch: missing the SDK's default name, or extracting a resource called <c>help/../../x</c>.
    /// </summary>
    [Fact]
    public void Embedded_resource_names_map_to_help_paths_and_unsafe_ones_are_skipped()
    {
        var names = new[]
        {
            "Minimap.help.index.html", "Minimap.help.style.css", "help\\img\\logo.png", "Minimap.README.md",
            "Other.help.index.html", "help/../../evil.html", "Minimap.Help.html", "docs/manual.html", "docs/manual.css", "docs2/x.css",
        };
        var opened = new List<string>();
        var found = ModHelp.EmbeddedHelp(names, n => { opened.Add(n); return new MemoryStream(Encoding.UTF8.GetBytes(n)); }, "Minimap", "docs/manual.html");
        var paths = found.Select(r => r.Path).ToArray();
        Assert.Equal(new[] { "help/index.html", "help/style.css", "help/img/logo.png", "Help.html", "docs/manual.html", "docs/manual.css" }, paths);
        Assert.DoesNotContain("help/../../evil.html", opened);
        Assert.Equal("Minimap.help.index.html", Encoding.UTF8.GetString(found[0].Bytes));
        Assert.Null(ModHelp.HelpPathOfResource("help/../x.html", "Minimap"));
    }

    /// <summary>
    /// What this would catch: a declared path, an embedded name or a crafted target that leads outside the mod's folder
    /// (or to a URL or another drive) being opened.
    /// </summary>
    [Theory]
    [InlineData("../outside.html")]
    [InlineData("help/../../outside.html")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\help.html")]
    [InlineData("https://example.com/help.html")]
    [InlineData("file:///C:/help.html")]
    [InlineData("")]
    [InlineData("help/./index.html")]
    public void Paths_that_leave_the_mods_folder_are_refused(string declared)
    {
        Assert.Null(ModHelp.SafeRelative(declared));
        string dll = Dll("plugins", "Corvane-Minimap", "Minimap.dll");
        Write(Path.Combine(_root, "plugins", "outside.html"));
        Write(Path.Combine(_root, "outside.html"));
        Assert.False(ModHelp.Resolve("example.minimap", dll, new ModHelpFacts { Declared = declared }, declared, Cache).Found);
    }

    [Fact]
    public void A_crafted_target_outside_its_root_is_not_opened_or_written()
    {
        string root = Path.Combine(_root, "plugins", "Corvane-Minimap");
        Directory.CreateDirectory(root);
        string outside = Write(Path.Combine(_root, "plugins", "outside.html"));
        var escape = new ModHelpTarget(ModHelpSource.Folder, root, "../outside.html", outside, null);
        Assert.Null(ModHelp.Materialize(escape, out string error));
        Assert.NotEmpty(error);

        var cache = Path.Combine(Cache, ModHelp.CacheFolderName, "x", "1");
        var write = new ModHelpTarget(ModHelpSource.Embedded, cache, "help/index.html", Path.Combine(cache, "help", "index.html"),
            new[] { Res("help/index.html", "ok"), Res("../../escaped.html", "no") });
        Assert.NotNull(ModHelp.Materialize(write, out _));
        Assert.False(File.Exists(Path.Combine(Cache, ModHelp.CacheFolderName, "escaped.html")));
        Assert.False(ModHelp.IsUnder(root, root));
        Assert.True(ModHelp.IsUnder(root, Path.Combine(root, "a", "b.html")));
        Assert.False(ModHelp.IsUnder(root, root + "-evil" + Path.DirectorySeparatorChar + "b.html"));
    }

    [ModHelp("docs/manual.html")]
    private sealed class DeclaredHelpPlugin
    {
    }

    private sealed class DerivedHelpPlugin : DeclaredHelpPluginBase
    {
    }

    [ModHelp("guide.html")]
    private class DeclaredHelpPluginBase
    {
    }

    /// <summary>
    /// <c>[ModHelp]</c> and <c>ctx.Menu.Help</c> (its store) name a file in the mod's folder or an embedded one; the API's
    /// path wins over the attribute's, the conventional files win over both, and disposing the API's lease clears it.
    /// What this would catch: the attribute not being read (or read only on the exact type), or a stale declared path.
    /// </summary>
    [Fact]
    public void The_attribute_and_the_api_name_the_help_file()
    {
        Assert.Equal("docs/manual.html", ModHelp.DeclaredHelp(typeof(DeclaredHelpPlugin)));
        Assert.Equal("guide.html", ModHelp.DeclaredHelp(typeof(DerivedHelpPlugin)));
        Assert.Equal("", ModHelp.DeclaredHelp(typeof(ModHelpTests)));
        var facts = ModHelp.FromType(typeof(DeclaredHelpPlugin), "2.0.0");
        Assert.Equal("docs/manual.html", facts.Declared);
        Assert.Equal("2.0.0", facts.Version);

        string dll = Dll("plugins", "Corvane-Minimap", "Minimap.dll");
        string dir = Path.GetDirectoryName(dll)!;
        Assert.False(ModHelp.Resolve("example.minimap", dll, facts, null, Cache).Found);
        string manual = Write(Path.Combine(dir, "docs", "manual.html"));
        var target = ModHelp.Resolve("example.minimap", dll, facts, null, Cache);
        Assert.Equal(ModHelpSource.Declared, target.Source);
        Assert.Equal("declared", target.SourceName);
        Assert.Equal(Path.GetFullPath(manual), Path.GetFullPath(target.File));

        string api = Write(Path.Combine(dir, "api.html"));
        using (var lease = ModHelpStore.Declare("example.minimap", "api.html"))
        {
            Assert.Equal("api.html", ModHelpStore.DeclaredPath("example.minimap"));
            target = ModHelp.Resolve("example.minimap", dll, facts, ModHelpStore.DeclaredPath("example.minimap"), Cache);
            Assert.Equal(Path.GetFullPath(api), Path.GetFullPath(target.File));
        }

        Assert.Equal("", ModHelpStore.DeclaredPath("example.minimap"));

        // A declared embedded file is extracted like the conventional one.
        var embedded = new ModHelpFacts { Version = "2.0.0", Declared = "docs/embedded.html", Embedded = new[] { Res("docs/embedded.html", "e") } };
        target = ModHelp.Resolve("example.minimap", dll, embedded, null, Cache);
        Assert.Equal(ModHelpSource.Declared, target.Source);
        Assert.NotNull(target.Extract);

        // The conventional file wins over a declared one.
        Write(Path.Combine(dir, "help", "index.html"));
        Assert.Equal(ModHelpSource.Folder, ModHelp.Resolve("example.minimap", dll, facts, "api.html", Cache).Source);
    }

    /// <summary>What this would catch: an embedded page shadowing the file the mod ships in its folder, or the reverse order for the declared path.</summary>
    [Fact]
    public void Folder_then_embedded_then_declared()
    {
        string dll = Dll("plugins", "Corvane-Minimap", "Minimap.dll");
        string dir = Path.GetDirectoryName(dll)!;
        Write(Path.Combine(dir, "docs", "manual.html"));
        var facts = new ModHelpFacts { Version = "1", Declared = "docs/manual.html", Embedded = new[] { Res("help/index.html", "e") } };
        Assert.Equal(ModHelpSource.Embedded, ModHelp.Resolve("example.minimap", dll, facts, null, Cache).Source);
        Write(Path.Combine(dir, "help", "index.html"));
        Assert.Equal(ModHelpSource.Folder, ModHelp.Resolve("example.minimap", dll, facts, null, Cache).Source);
    }

    /// <summary>
    /// The Progenitor entry's guide is found by path when the package ships it, and otherwise the entry has no button.
    /// What this would catch: a Help button on Progenitor's page with no guide installed.
    /// </summary>
    [Fact]
    public void The_progenitor_guide_is_resolved_by_path_when_present()
    {
        string core = Path.Combine(_root, "progenitor-core");
        var guides = new List<(string, string)> { (core, ModHelp.ProgenitorGuidePath), (core, ModHelp.IndexPath) };
        Assert.False(ModHelp.Resolve("progenitor", null, null, null, Cache, guides).Found);
        string guide = Write(Path.Combine(core, "docs", "guide", "progenitor-guide.html"));
        var target = ModHelp.Resolve("progenitor", null, null, null, Cache, guides);
        Assert.Equal(ModHelpSource.Guide, target.Source);
        Assert.Equal(ModHelp.ProgenitorGuidePath, target.Relative);
        Assert.Equal(Path.GetFullPath(guide), Path.GetFullPath(target.File));
        Assert.Equal(Path.GetFullPath(guide), ModHelp.Materialize(target, out _));
    }

    /// <summary>
    /// What this would catch: a space, <c>#</c>, <c>%</c> or a non-ASCII folder name reaching the browser unescaped (a
    /// broken link, or a fragment that cuts the path), or a relative path being turned into a URI at all.
    /// </summary>
    [Fact]
    public void File_uris_escape_spaces_unicode_and_reserved_characters_and_round_trip()
    {
        string file = Write(Path.Combine(_root, "plugins", "Mod #1 100%", "Ünïcødé 模组", "help", "index.html"));
        string? uri = ModHelp.FileUri(file);
        Assert.NotNull(uri);
        Assert.StartsWith("file:///", uri, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("#", uri, StringComparison.Ordinal);
        Assert.Contains("My%20Game", uri, StringComparison.Ordinal);
        Assert.Contains("Mod%20%231%20100%25", uri, StringComparison.Ordinal);
        Assert.Contains("%C3%9Cn%C3%AFc%C3%B8d%C3%A9%20%E6%A8%A1%E7%BB%84", uri, StringComparison.Ordinal);
        Assert.True(uri!.All(c => c < 128));
        var parsed = new Uri(uri);
        Assert.True(parsed.IsFile);
        Assert.Equal(Path.GetFullPath(file), Path.GetFullPath(parsed.LocalPath));

        Assert.Null(ModHelp.FileUri("help/index.html"));
        Assert.Null(ModHelp.FileUri(""));
        Assert.Null(ModHelp.FileUri(null));
        if (Path.DirectorySeparatorChar == '\\')
        {
            Assert.Equal("file:///C:/Games/My%20Mods/help/index.html", ModHelp.FileUri("C:\\Games\\My Mods\\help\\index.html"));
            Assert.Equal("file://server/share/a%20b.html", ModHelp.FileUri("\\\\server\\share\\a b.html"));
        }
    }

    [Fact]
    public void Cache_folder_names_are_safe()
    {
        Assert.Equal("example.minimap", ModHelp.FolderName("example.minimap", "x"));
        Assert.Equal("a_b_c", ModHelp.FolderName("a/b\\c", "x"));
        Assert.Equal("x", ModHelp.FolderName("..", "x"));
        Assert.Equal("0", ModHelp.FolderName("", "0"));
        string folder = ModHelp.CacheFolder(Cache, "../evil", "1.0.0/../..");
        Assert.True(ModHelp.IsUnder(Path.Combine(Cache, ModHelp.CacheFolderName), folder));
    }
}
