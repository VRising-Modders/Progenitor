using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Progenitor.Install;
using Progenitor.Support;
using Xunit;

namespace Progenitor.Tests;

/// <summary>SUPPORT1: mods.md in the log zip: every installed mod with its author, links and help page. Game-free.</summary>
public sealed class SupportModListTests : IDisposable
{
    private readonly string _base;
    private readonly string _game;
    private readonly string _root;

    public SupportModListTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "progenitor-modlist-tests-" + Guid.NewGuid().ToString("N"));
        _game = Path.Combine(_base, "game");
        _root = Path.Combine(_game, "BepInEx");
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private static string Put(string full, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private string Dll(string relative) => Put(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)), "dll");

    [Fact]
    public void A_thunderstore_package_folder_gives_the_author_store_page_website_and_help_page()
    {
        string dll = Dll("plugins/SomeTeam-Minimap/Minimap.dll");
        Put(Path.Combine(_root, "plugins", "SomeTeam-Minimap", "manifest.json"),
            "{\"name\":\"Minimap\",\"version_number\":\"1.2.0\",\"website_url\":\"https://github.com/SomeTeam/Minimap\",\"description\":\"x\"}");
        Put(Path.Combine(_root, "plugins", "SomeTeam-Minimap", "help", "index.html"), "<html></html>");
        var mod = new SupportMod { Id = "someteam.minimap", Name = "Minimap", Version = "1.2.0", Location = dll, OnProgenitor = true };

        SupportModList.Resolve(new[] { mod }, _root);

        Assert.Equal("SomeTeam", mod.Author);
        Assert.Equal("SomeTeam-Minimap", mod.Package);
        Assert.Equal("https://thunderstore.io/c/v-rising/p/SomeTeam/Minimap/", mod.StoreUrl);
        Assert.Equal("https://github.com/SomeTeam/Minimap", mod.WebsiteUrl);
        Assert.StartsWith("help/index.html (in the mod's folder", mod.Help);
        Assert.Equal("BepInEx/plugins", mod.Folder);
    }

    [Fact]
    public void The_install_ledger_names_the_package_of_a_mod_progenitor_installed()
    {
        string dll = Dll("progenitor-reload/Arena.dll");
        new ServerInstallLedger("1", "", "", DateTime.UtcNow)
            .With(new ServerInstallLedgerEntry("BepInEx/progenitor-reload/Arena.dll", "", 3, ServerInstallLedger.CategoryMod,
                "package:Fighters-Arena@2.0.1", DateTime.UtcNow, pluginId: "fighters.arena"))
            .Write(_game);
        var mod = new SupportMod { Id = "fighters.arena", Name = "Arena", Version = "2.0.1", Location = dll, OnProgenitor = true };

        SupportModList.Resolve(new[] { mod }, _root);

        Assert.Equal("Fighters", mod.Author);
        Assert.Equal("https://thunderstore.io/c/v-rising/p/Fighters/Arena/", mod.StoreUrl);
        Assert.Equal("", mod.WebsiteUrl);
        Assert.Equal("BepInEx/progenitor-reload", mod.Folder);
    }

    [Fact]
    public void A_loose_dll_has_no_links_and_progenitor_has_its_own()
    {
        var loose = new SupportMod { Id = "loose.mod", Name = "Loose", Version = "0.1.0", Location = Dll("plugins/Loose.dll") };
        var progenitor = new SupportMod { Id = "progenitor", Name = "Progenitor Bootstrap", Version = "2026.10.5" };

        SupportModList.Resolve(new[] { loose, progenitor }, _root);

        Assert.Equal("", loose.Author);
        Assert.Equal("", loose.StoreUrl);
        Assert.Equal("", loose.Help);
        Assert.Equal(SupportModList.ProgenitorRepository, progenitor.WebsiteUrl);
        Assert.Equal(SupportModList.ProgenitorStore, progenitor.StoreUrl);
        Assert.True(progenitor.OnProgenitor);
    }

    [Theory]
    [InlineData("https://github.com/a/b", "https://github.com/a/b")]
    [InlineData("https://example.com/mod?token=abc#x", "https://example.com/mod")]
    [InlineData("https://user:pass@example.com/mod", "")]
    [InlineData("file:///C:/mods", "")]
    [InlineData("javascript:alert(1)", "")]
    [InlineData("https://github.com/YourTeam/ProgenitorMod", "")]
    [InlineData("not a url", "")]
    [InlineData("", "")]
    public void Only_plain_web_links_are_kept(string input, string expected) => Assert.Equal(expected, SupportModList.SafeUrl(input));

    [Theory]
    [InlineData("Owner-Name", true)]
    [InlineData("Owner_1-Name_2", true)]
    [InlineData("Owner-Name-Extra", false)]
    [InlineData("-Name", false)]
    [InlineData("Owner/x-Name", false)]
    public void Package_names_split_on_their_one_dash(string package, bool ok) =>
        Assert.Equal(ok, SupportModList.SplitPackage(package, out _, out _));

    [Fact]
    public void The_list_puts_progenitor_first_and_the_server_only_mods_last()
    {
        var mods = new[]
        {
            new SupportMod { Id = "b.mod", Name = "Beta", Version = "1.0.0", OnProgenitor = false, AlsoOnServer = true },
            new SupportMod { Id = "server.only", Version = "3.0.0", ServerOnly = true },
            new SupportMod { Id = "a.mod", Name = "Alpha", Version = "", OnProgenitor = true, Author = "Team", StoreUrl = "https://thunderstore.io/c/v-rising/p/Team/Alpha/" },
            new SupportMod { Id = "progenitor", Name = "Progenitor", Version = "2026.10.5" },
        };

        string text = SupportModList.Format("client", mods);

        Assert.StartsWith("# Installed mods (client)", text);
        Assert.Contains(SupportModList.ProgenitorLlms, text);
        int p = text.IndexOf("## Progenitor", StringComparison.Ordinal);
        int a = text.IndexOf("## Alpha", StringComparison.Ordinal);
        int b = text.IndexOf("## Beta", StringComparison.Ordinal);
        int s = text.IndexOf("## On the joined server only", StringComparison.Ordinal);
        Assert.True(p >= 0 && p < a && a < b && b < s, text);
        Assert.Contains("- Built on Progenitor: it is Progenitor", text);
        Assert.Contains("- Version: unknown", text);
        Assert.Contains("- Author: Team", text);
        Assert.Contains("- Thunderstore: https://thunderstore.io/c/v-rising/p/Team/Alpha/", text);
        Assert.Contains("- Built on Progenitor: no", text);
        Assert.Contains("- On the joined server too: yes", text);
        Assert.Contains("- server.only 3.0.0", text);
        Assert.DoesNotContain("## server.only", text);
    }

    [Fact]
    public void The_zip_holds_mods_md_redacted_without_file_paths()
    {
        string dll = Dll("plugins/SomeTeam-Minimap/Minimap.dll");
        Put(Path.Combine(_root, "plugins", "SomeTeam-Minimap", "manifest.json"),
            "{\"name\":\"Minimap\",\"version_number\":\"1.2.0\",\"website_url\":\"https://example.com/minimap?key=s3cret\"}");
        var request = new SupportBundleRequest
        {
            Side = "client",
            BepInExRoot = _root,
            NowLocal = new DateTime(2026, 10, 17, 9, 0, 0),
            UserProfile = SupportRedactorTests.Profile,
            UserName = "alice",
            MachineName = "DESKTOP-ABC123",
        };
        request.Mods.Add(new SupportMod { Id = "someteam.minimap", Name = "Minimap", Version = "1.2.0", Location = dll, OnProgenitor = true });
        request.Mods.Add(new SupportMod { Id = "loose.mod", Name = "Loose", Version = "0.1.0", Location = SupportRedactorTests.Profile + @"\Mods\Loose.dll" });

        var result = SupportBundle.Build(request);
        Assert.True(result.Ok, result.Error);
        Assert.Contains(SupportModList.EntryName, result.Entries);

        string text;
        using (var zip = ZipFile.OpenRead(result.Path))
        using (var reader = new StreamReader(zip.GetEntry(SupportModList.EntryName)!.Open(), Encoding.UTF8))
            text = reader.ReadToEnd();

        Assert.Contains("## Minimap", text);
        Assert.Contains("- Author: SomeTeam", text);
        Assert.Contains("- Built on Progenitor: yes", text);
        Assert.Contains("- Website or repository: https://example.com/minimap", text);
        Assert.DoesNotContain("s3cret", text);
        Assert.DoesNotContain("Minimap.dll", text);
        Assert.DoesNotContain("Loose.dll", text);
        Assert.DoesNotContain("alice", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_base, text, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void A_player_who_shares_a_mods_author_or_name_is_still_replaced_in_the_logs()
    {
        string dll = Dll("plugins/SomeTeam-Minimap/Minimap.dll");
        Put(Path.Combine(_root, "plugins", "SomeTeam-Minimap", "manifest.json"), "{\"name\":\"Minimap\",\"version_number\":\"1.2.0\"}");
        Put(Path.Combine(_root, "LogOutput.log"), "[Info] SomeTeam joined; Minimap loaded; Arena loaded\n");
        var request = new SupportBundleRequest
        {
            Side = "client",
            BepInExRoot = _root,
            NowLocal = new DateTime(2026, 10, 17, 9, 5, 0),
            UserProfile = SupportRedactorTests.Profile,
            UserName = "alice",
            MachineName = "DESKTOP-ABC123",
        };
        request.KnownNames.Add("SomeTeam");
        request.KnownNames.Add("Arena");
        request.Mods.Add(new SupportMod { Id = "someteam.minimap", Name = "Minimap", Version = "1.2.0", Location = dll });
        request.Mods.Add(new SupportMod { Id = "fighters.arena", Name = "Arena", Version = "2.0.0" });

        var result = SupportBundle.Build(request);
        Assert.True(result.Ok, result.Error);
        string log;
        using (var zip = ZipFile.OpenRead(result.Path))
        using (var reader = new StreamReader(zip.GetEntry("bepinex/LogOutput.log")!.Open(), Encoding.UTF8))
            log = reader.ReadToEnd();

        Assert.DoesNotContain("SomeTeam", log);
        Assert.DoesNotContain("Arena", log);
        // A mod name no player has is kept, so its log lines still name it.
        Assert.Contains("Minimap loaded", log);
    }

    [Fact]
    public void Mod_words_skip_authors_server_only_ids_and_player_names()
    {
        var mods = new[]
        {
            new SupportMod { Id = "someteam.minimap", Name = "Minimap", Author = "SomeTeam", Package = "SomeTeam-Minimap" },
            new SupportMod { Id = "server.only", ServerOnly = true },
        };
        var words = SupportModList.Words(mods, new[] { "someteam" });
        Assert.Equal(new[] { "Minimap" }, words);
    }

    [Fact]
    public void Line_breaks_and_control_characters_in_mod_facts_stay_on_one_line()
    {
        var mods = new[] { new SupportMod { Id = "x.mod", Name = "Evil\n## Fake heading\r\u0007", Version = "1.0\n- Author: someone" } };
        string text = SupportModList.Format("client", mods).Replace("\r\n", "\n");
        Assert.Contains("## Evil ## Fake heading", text);
        Assert.Contains("- Version: 1.0 - Author: someone", text);
        Assert.False(text.Contains((char)7), "a control character was kept");
        Assert.False(text.Contains("\n## Fake", StringComparison.Ordinal));
        Assert.Equal(200, SupportModList.Clean(new string('a', 500)).Length);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"name\":\"Minimap\"}")]
    public void A_malformed_manifest_gives_no_package(string manifest)
    {
        string dll = Dll("plugins/SomeTeam-Minimap/Minimap.dll");
        Put(Path.Combine(_root, "plugins", "SomeTeam-Minimap", "manifest.json"), manifest);
        var mod = new SupportMod { Id = "someteam.minimap", Name = "Minimap", Location = dll };

        SupportModList.Resolve(new[] { mod }, _root);

        Assert.Equal("", mod.Author);
        Assert.Equal("", mod.StoreUrl);
        Assert.Equal("", mod.WebsiteUrl);
        Assert.Equal("BepInEx/plugins", mod.Folder);
    }

    [Fact]
    public void An_oversized_manifest_is_not_read()
    {
        string dll = Dll("plugins/SomeTeam-Minimap/Minimap.dll");
        string pad = new string(' ', 300 * 1024);
        Put(Path.Combine(_root, "plugins", "SomeTeam-Minimap", "manifest.json"),
            "{\"name\":\"Minimap\",\"version_number\":\"1.2.0\",\"website_url\":\"https://example.com/m\"" + pad + "}");
        var mod = new SupportMod { Id = "someteam.minimap", Name = "Minimap", Location = dll };

        SupportModList.Resolve(new[] { mod }, _root);

        Assert.Equal("", mod.Author);
        Assert.Equal("", mod.WebsiteUrl);
    }
}
