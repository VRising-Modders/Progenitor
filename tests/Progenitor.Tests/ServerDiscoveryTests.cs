using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Install;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>The fake discovery sources: no test reads the real registry or the machine's processes.</summary>
internal sealed class FakeDiscoverySources : IServerDiscoverySources
{
    public List<string> RegistryRoots { get; } = new();
    public List<string> Uninstall { get; } = new();
    public List<string> Running { get; } = new();
    public bool Throw { get; set; }

    public IReadOnlyList<string> RegistrySteamRoots() => Throw ? throw new UnauthorizedAccessException() : RegistryRoots;
    public IReadOnlyList<string> UninstallLocations() => Throw ? throw new UnauthorizedAccessException() : Uninstall;
    public IReadOnlyList<string> RunningServerFolders() => Throw ? throw new UnauthorizedAccessException() : Running;
}

/// <summary>Dedicated-server discovery from the registry, app manifests, the uninstall key and running servers. Suggest-only.</summary>
public sealed class ServerDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-discovery-" + Guid.NewGuid().ToString("N"));

    public ServerDiscoveryTests()
    {
        Directory.CreateDirectory(_root);
        LocalServerFolder.WriteAccessOverride = null;
        LocalServerFolder.ReparseLookup = null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* temp */ }
    }

    private string Server(string relative, bool bepInEx = false)
    {
        string dir = Path.Combine(_root, relative);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, LocalServerFolder.ServerExecutableName), "exe");
        Directory.CreateDirectory(Path.Combine(dir, LocalServerFolder.BepInExFolderName));
        if (bepInEx)
        {
            Directory.CreateDirectory(Path.Combine(dir, "BepInEx", "core"));
            File.WriteAllText(Path.Combine(dir, "BepInEx", "core", "BepInEx.Core.dll"), "dll");
        }

        return Path.GetFullPath(dir);
    }

    private static string Acf(string installdir) =>
        "\"AppState\"\n{\n\t\"appid\"\t\t\"1829350\"\n\t\"name\"\t\t\"V Rising Dedicated Server\"\n\t\"installdir\"\t\t\"" + installdir + "\"\n}\n";

    [Fact]
    public void The_app_manifest_installdir_is_read_and_unsafe_values_refused()
    {
        Assert.Equal("VRisingDedicatedServer", SteamLibraryScan.ParseInstallDir(Acf("VRisingDedicatedServer")));
        Assert.Equal("V Rising Server", SteamLibraryScan.ParseInstallDir(Acf("V Rising Server")));
        Assert.Null(SteamLibraryScan.ParseInstallDir(Acf("..\\escape")));
        Assert.Null(SteamLibraryScan.ParseInstallDir(Acf("C:\\elsewhere")));
        Assert.Null(SteamLibraryScan.ParseInstallDir(""));
        Assert.Null(SteamLibraryScan.ParseInstallDir("\"AppState\" { }"));
    }

    [Fact]
    public void Registry_roots_are_normalized()
    {
        Assert.Equal(@"c:\program files (x86)\steam", SteamLibraryScan.NormalizeRoot("c:/program files (x86)/steam"));
        Assert.Equal(@"D:\Steam", SteamLibraryScan.NormalizeRoot("\"D:\\Steam\\\""));
        Assert.Equal("", SteamLibraryScan.NormalizeRoot("  "));
    }

    [Fact]
    public void A_registry_steam_root_feeds_the_library_parse_and_the_app_manifest()
    {
        string steam = Path.Combine(_root, "Steam");
        string library = Path.Combine(_root, "LibraryD");
        string server = Server(Path.Combine("LibraryD", "steamapps", "common", "Custom Server Name"), bepInEx: true);
        var fs = new MapSteamLibraryFileSystem();
        fs.Files[Path.Combine(steam, SteamLibraryScan.LibrariesVdfRelative)] =
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + library.Replace("\\", "\\\\") + "\"\n\t}\n}\n";
        fs.Files[Path.Combine(library, "steamapps", SteamLibraryScan.AppManifestName)] = Acf("Custom Server Name");
        var sources = new FakeDiscoverySources();
        sources.RegistryRoots.Add(steam);

        var hits = new SteamLibraryScan(fs, sources).SuggestDetailed();
        var hit = Assert.Single(hits);
        Assert.Equal(server, hit.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(SteamLibraryScan.SourceSteamRegistry, hit.Source);
        Assert.True(hit.HasBepInEx);
        Assert.Contains("BepInEx installed", hit.Detail);
        Assert.False(File.Exists(Path.Combine(server, LocalServerFolder.WriteProbeName)));
    }

    [Fact]
    public void A_running_server_and_the_uninstall_key_are_sources_and_duplicates_merge()
    {
        string server = Server("Anywhere\\Server");
        string other = Server("Other\\Server");
        var sources = new FakeDiscoverySources();
        sources.Running.Add(server);
        sources.Uninstall.Add(server + "\\"); // the same folder, spelled with a trailing slash
        sources.Uninstall.Add(other);

        var hits = new SteamLibraryScan(new MapSteamLibraryFileSystem(), sources).SuggestDetailed();
        Assert.Equal(2, hits.Count);
        Assert.Equal(SteamLibraryScan.SourceRunning, hits[0].Source);
        Assert.True(hits[0].Running);
        Assert.False(hits[0].HasBepInEx);
        Assert.Contains("no BepInEx yet", hits[0].Detail);
        Assert.Equal(SteamLibraryScan.SourceUninstall, hits[1].Source);
        Assert.Equal(other, hits[1].Path, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Absent_or_failing_sources_suggest_nothing_and_never_throw()
    {
        var sources = new FakeDiscoverySources { Throw = true };
        Assert.Empty(new SteamLibraryScan(new MapSteamLibraryFileSystem(), sources).SuggestDetailed());
        sources.Throw = false;
        sources.Uninstall.Add(Path.Combine(_root, "missing"));
        Assert.Empty(new SteamLibraryScan(new MapSteamLibraryFileSystem(), sources).SuggestDetailed());
    }

    [Fact]
    public void A_test_file_system_never_reaches_the_live_registry()
    {
        // A scan over a test file system with no sources given uses none, so this suite never reads
        // the machine's registry or its running processes.
        var scan = new SteamLibraryScan(new MapSteamLibraryFileSystem());
        Assert.Empty(scan.DiscoverSourced());
    }
}
