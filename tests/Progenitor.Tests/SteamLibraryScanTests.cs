using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Install;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Suggest-only Steam library discovery. Guard comments name what a removal must break.
/// </summary>
public sealed class SteamLibraryScanTests : IDisposable
{
    private readonly string _root;

    public SteamLibraryScanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "progenitor-steam-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        LocalServerFolder.WriteAccessOverride = null;
        LocalServerFolder.ReparseLookup = null;
    }

    public void Dispose()
    {
        LocalServerFolder.WriteAccessOverride = null;
        LocalServerFolder.ReparseLookup = null;
        try { Directory.Delete(_root, recursive: true); }
        catch { /* temp */ }
    }

    [Fact]
    public void ParseLibraryFolders_reads_path_entries_from_a_vdf_fixture()
    {
        const string vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}\n";
        var paths = SteamLibraryScan.ParseLibraryFolders(vdf);
        Assert.Equal(2, paths.Count);
        Assert.Contains(paths, p => p.IndexOf("SteamLibrary", StringComparison.OrdinalIgnoreCase) >= 0);
        Assert.DoesNotContain(paths, p => p.IndexOf("VRisingServer", StringComparison.OrdinalIgnoreCase) >= 0);
        // Revert: ignoring the path key, or treating uuid-like keys as folders, fails this.
    }

    [Fact]
    public void Suggest_yields_validated_candidates_only_and_does_not_write()
    {
        string steam = Path.Combine(_root, "Steam");
        string library = Path.Combine(_root, "Lib");
        string valid = Path.Combine(library, "steamapps", "common", "VRisingDedicatedServer");
        Directory.CreateDirectory(valid);
        File.WriteAllText(Path.Combine(valid, LocalServerFolder.ServerExecutableName), "exe");
        Directory.CreateDirectory(Path.Combine(valid, LocalServerFolder.BepInExFolderName));
        string client = Path.Combine(_root, "ClientLib", "steamapps", "common", "VRisingDedicatedServer");
        Directory.CreateDirectory(client);
        File.WriteAllText(Path.Combine(client, LocalServerFolder.ClientExecutableName), "client");

        string vdf = "\"libraryfolders\"\n{\n\t\"0\" { \"path\" \"" + library.Replace("\\", "\\\\")
            + "\" }\n\t\"1\" { \"path\" \""
            + Path.Combine(_root, "ClientLib").Replace("\\", "\\\\")
            + "\" }\n}\n";
        var fs = new MapSteamLibraryFileSystem();
        fs.Roots.Add(steam);
        fs.Files[Path.Combine(steam, SteamLibraryScan.LibrariesVdfRelative)] = vdf;
        var scan = new SteamLibraryScan(fs);
        var hits = scan.Suggest();
        Assert.Single(hits);
        Assert.Equal(Path.GetFullPath(valid), hits[0], StringComparer.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(valid, LocalServerFolder.WriteProbeName)));
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "SteamLibraryScan.cs");
        Assert.DoesNotContain(@"C:\VRisingServer", src, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ValidateSuggestion", src, StringComparison.Ordinal);
        // Revert: offering a client root, writing a probe during scan, or hard-coding C:\VRisingServer fails this.
    }

    [Fact]
    public void Suggest_caps_at_three_and_never_registers_a_folder()
    {
        string steam = Path.Combine(_root, "Steam");
        var fs = new MapSteamLibraryFileSystem();
        fs.Roots.Add(steam);
        var libraries = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            string lib = Path.Combine(_root, "L" + i.ToString());
            string server = Path.Combine(lib, "steamapps", "common", "VRisingDedicatedServer");
            Directory.CreateDirectory(server);
            File.WriteAllText(Path.Combine(server, LocalServerFolder.ServerExecutableName), "exe");
            Directory.CreateDirectory(Path.Combine(server, LocalServerFolder.BepInExFolderName));
            libraries.Add(lib);
        }

        var vdf = "\"libraryfolders\"\n{\n";
        for (int i = 0; i < libraries.Count; i++)
            vdf += "\t\"" + i.ToString() + "\" { \"path\" \"" + libraries[i].Replace("\\", "\\\\") + "\" }\n";
        vdf += "}\n";
        fs.Files[Path.Combine(steam, SteamLibraryScan.LibrariesVdfRelative)] = vdf;
        var folder = new LocalServerFolder();
        var scan = new SteamLibraryScan(fs);
        var hits = scan.Suggest();
        Assert.Equal(SteamLibraryScan.MaxCandidates, hits.Count);
        Assert.False(folder.HasValidatedServer);
        // Revert: returning every library or calling Apply inside Suggest fails this.
    }
}

/// <summary>Injectable Steam roots and vdf bodies. Existence of server folders is the real temp disk.</summary>
internal sealed class MapSteamLibraryFileSystem : ISteamLibraryFileSystem
{
    public List<string> Roots { get; } = new();
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> SteamRoots() => Roots;

    public bool FileExists(string path) => Files.ContainsKey(path);

    public bool DirectoryExists(string path) => false;

    public string? ReadText(string path) => Files.TryGetValue(path, out var text) ? text : null;
}
