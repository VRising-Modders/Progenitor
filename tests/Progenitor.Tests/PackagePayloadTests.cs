using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Progenitor.PluginActions;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The preloader's unpack of the Thunderstore package's <c>progenitor-files.zip</c> (<c>PackagePayload.cs</c>, compiled
/// here as-is). A mod manager places that archive in <c>BepInEx/patchers/&lt;package&gt;/</c>; the preloader turns it back
/// into <c>BepInEx/progenitor-core</c> before Bootstrap looks for Core. Game-free: a temp BepInEx root.
/// </summary>
public sealed class PackagePayloadTests : IDisposable
{
    private readonly string _bep;
    private readonly List<string> _info = new();
    private readonly List<string> _warn = new();

    public PackagePayloadTests()
    {
        _bep = Path.Combine(Path.GetTempPath(), "progenitor-payload-" + Guid.NewGuid().ToString("N"), "BepInEx");
        Directory.CreateDirectory(_bep);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_bep)!, recursive: true); }
        catch { /* temp */ }
    }

    private string CoreDll => Path.Combine(_bep, "progenitor-core", "Progenitor.dll");

    /// <summary>
    /// A first start unpacks every entry into its BepInEx folder and writes the stamp; the next start with the same
    /// archive writes nothing, so a Core the player updated in game (or a hot swap's copy) stays. What this would catch:
    /// an unpack at every start that undoes an in-game update, or Core landing anywhere but progenitor-core.
    /// </summary>
    [Fact]
    public void The_archive_is_unpacked_once_and_an_unchanged_archive_writes_nothing()
    {
        string archive = PlaceArchive("patchers/VRising_Modders-Progenitor", Payload("core-1"));

        Assert.Equal(PackagePayloadOutcome.Applied, Apply(archive));
        Assert.Equal("core-1", File.ReadAllText(CoreDll));
        Assert.Equal("table", File.ReadAllText(Path.Combine(_bep, "progenitor-core", "addresslib", "client-1.json")));
        Assert.Equal("guide", File.ReadAllText(Path.Combine(_bep, "progenitor-core", "docs", "guide", "progenitor-guide.html")));
        Assert.Equal(PackagePayload.Sha256Hex(File.ReadAllBytes(archive)),
            File.ReadAllText(Path.Combine(_bep, "progenitor-core", PackagePayload.StampFileName)).Trim());
        Assert.Contains(_info, l => l.Contains("unpacked 3 files", StringComparison.Ordinal));
        // Nothing among the plugins, nothing left half-written.
        Assert.False(Directory.Exists(Path.Combine(_bep, "plugins")));
        Assert.Empty(Directory.GetFiles(_bep, "*.progenitor-tmp", SearchOption.AllDirectories));

        File.WriteAllText(CoreDll, "core-updated-in-game");
        Assert.Equal(PackagePayloadOutcome.Current, Apply(archive));
        Assert.Equal("core-updated-in-game", File.ReadAllText(CoreDll));
    }

    /// <summary>
    /// A mod-manager update (a different archive) replaces the files it names, drops a Core staged for the next start
    /// (which would otherwise undo the update) and leaves the files Core wrote there itself. A missing Core is put back
    /// even when the stamp matches.
    /// </summary>
    [Fact]
    public void A_new_archive_replaces_its_files_and_keeps_the_rest()
    {
        string archive = PlaceArchive("patchers/VRising_Modders-Progenitor", Payload("core-1"));
        Assert.Equal(PackagePayloadOutcome.Applied, Apply(archive));
        string fingerprint = Write("progenitor-core/addresslib/build-client-1.json", "fingerprint");
        string staged = Write("progenitor-core/Progenitor.dll.next", "older-staged-core");

        File.WriteAllBytes(archive, Payload("core-2"));
        Assert.Equal(PackagePayloadOutcome.Applied, Apply(archive));
        Assert.Equal("core-2", File.ReadAllText(CoreDll));
        Assert.Equal("fingerprint", File.ReadAllText(fingerprint));
        Assert.False(File.Exists(staged));

        File.Delete(CoreDll);
        Assert.Equal(PackagePayloadOutcome.Applied, Apply(archive));
        Assert.Equal("core-2", File.ReadAllText(CoreDll));
    }

    /// <summary>
    /// While another game process of this install runs it has Core loaded and may be watching the folder, so a changed
    /// archive waits; with no Core at all there is nothing to disturb and it is unpacked. A waiting Remove Progenitor
    /// request is never undone by putting Core back.
    /// </summary>
    [Fact]
    public void Another_process_defers_and_an_uninstall_request_skips()
    {
        string archive = PlaceArchive("patchers/VRising_Modders-Progenitor", Payload("core-1"));
        Assert.Equal(PackagePayloadOutcome.Applied, PackagePayload.Apply(_bep, archive, _info.Add, _warn.Add, () => true));

        File.WriteAllBytes(archive, Payload("core-2"));
        Assert.Equal(PackagePayloadOutcome.Deferred, PackagePayload.Apply(_bep, archive, _info.Add, _warn.Add, () => true));
        Assert.Equal("core-1", File.ReadAllText(CoreDll));

        Directory.Delete(Path.Combine(_bep, "progenitor-core"), recursive: true);
        Write(PackagePayload.UninstallMarker, "{}");
        Assert.Equal(PackagePayloadOutcome.Skipped, Apply(archive));
        Assert.False(File.Exists(CoreDll));

        Assert.Equal(PackagePayloadOutcome.NoPayload, Apply(null));
    }

    /// <summary>
    /// One entry the archive may not write refuses the whole archive: nothing is written, the reason is logged, and the
    /// game starts (Bootstrap then logs Core missing). What this would catch: a tampered or mistaken package writing into
    /// plugins, BepInEx/core, outside BepInEx, or over the stamp.
    /// </summary>
    [Theory]
    [InlineData("plugins/Progenitor.dll")]
    [InlineData("core/BepInEx.Core.dll")]
    [InlineData("progenitor-core/../plugins/x.dll")]
    [InlineData("../VRising.exe")]
    [InlineData("/progenitor-core/x.dll")]
    [InlineData("C:/progenitor-core/x.dll")]
    [InlineData("progenitor-core/" + PackagePayload.StampFileName)]
    [InlineData("Progenitor.dll")]
    [InlineData("progenitor-core/Progenitor.dll.")]
    [InlineData("progenitor-core/" + PackagePayload.StampFileName + ".")]
    public void An_entry_outside_the_Progenitor_folders_refuses_the_archive(string bad)
    {
        string archive = PlaceArchive("patchers", Payload("core-1", extra: bad));

        Assert.Equal(PackagePayloadOutcome.Refused, Apply(archive));
        Assert.False(Directory.Exists(Path.Combine(_bep, "progenitor-core")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_bep)!, "VRising.exe")));
        Assert.Single(_warn);
        Assert.Contains("was not unpacked", _warn[0], StringComparison.Ordinal);
    }

    [Fact]
    public void An_archive_without_Core_is_refused()
    {
        string archive = PlaceArchive("patchers", Zip(("progenitor-core/addresslib/client-1.json", "table")));
        Assert.Equal(PackagePayloadOutcome.Refused, Apply(archive));
        Assert.Contains("no progenitor-core/Progenitor.dll", _warn[0], StringComparison.Ordinal);
        Assert.Equal(PackagePayloadOutcome.Failed, Apply(Write("patchers/" + PackagePayload.FileName, "not a zip")));
    }

    /// <summary>
    /// The archive beside the patcher wins; otherwise one copy in patchers/ or a direct subfolder (the mod manager's
    /// package folder). Two copies in different folders are ambiguous and neither is applied.
    /// </summary>
    [Fact]
    public void The_archive_is_found_beside_the_patcher_or_in_one_package_folder()
    {
        Assert.Null(PackagePayload.Find(_bep, null));
        string managed = PlaceArchive("patchers/VRising_Modders-Progenitor", Payload("a"));
        Assert.Equal(managed, PackagePayload.Find(_bep, null));
        Assert.Equal(managed, PackagePayload.Find(_bep, Path.Combine(_bep, "patchers", "Elsewhere")));

        string second = PlaceArchive("patchers/Progenitor", Payload("b"));
        Assert.Null(PackagePayload.Find(_bep, null, _warn.Add));
        Assert.Contains("found 2 copies", _warn[0], StringComparison.Ordinal);
        Assert.Equal(second, PackagePayload.Find(_bep, Path.GetDirectoryName(second)));
    }

    /// <summary>
    /// A hosted child's <c>patchers</c> is a junction to the client's: the archive found through it belongs to the client,
    /// and the child's own staged Core (here newer, with a staged <c>.next</c>) is left exactly as staging made it.
    /// </summary>
    [Fact]
    public void An_archive_reached_through_a_link_is_left_alone()
    {
        string client = Path.Combine(Path.GetDirectoryName(_bep)!, "client", "BepInEx");
        string clientArchive = Path.Combine(client, "patchers", "VRising_Modders-Progenitor", PackagePayload.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(clientArchive)!);
        File.WriteAllBytes(clientArchive, Payload("core-1"));
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            "/c mklink /J \"" + Path.Combine(_bep, "patchers") + "\" \"" + Path.Combine(client, "patchers") + "\"")
            { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
        Write("progenitor-core/Progenitor.dll", "core-2-staged");
        string next = Write("progenitor-core/Progenitor.dll.next", "core-3-next");

        string? found = PackagePayload.Find(_bep, null);
        Assert.NotNull(found);
        Assert.Equal(PackagePayloadOutcome.Skipped, Apply(found));
        Assert.Equal("core-2-staged", File.ReadAllText(CoreDll));
        Assert.True(File.Exists(next));
        Directory.Delete(Path.Combine(_bep, "patchers"));
    }

    /// <summary>The preloader runs the unpack before the plugin actions and before Bootstrap can look for Core.</summary>
    [Fact]
    public void The_patcher_unpacks_before_anything_else_reads_BepInEx()
    {
        string init = SourcePin.MethodBody(SourcePin.ReadFile("src", "Progenitor.Preloader", "Patcher.cs"), "public override void Initialize()");
        SourcePin.AssertUncommented(init, "PackagePayload.Apply(");
        Assert.True(init.IndexOf("PackagePayload.Apply(", StringComparison.Ordinal)
                    < init.IndexOf("PluginFileActions.RunAll(", StringComparison.Ordinal));
    }

    // ---- fixtures --------------------------------------------------------------------------

    private PackagePayloadOutcome Apply(string? archive) =>
        PackagePayload.Apply(_bep, archive, _info.Add, _warn.Add, () => false);

    private static byte[] Payload(string core, string? extra = null)
    {
        var entries = new List<(string, string)>
        {
            ("progenitor-core/Progenitor.dll", core),
            ("progenitor-core/addresslib/client-1.json", "table"),
            ("progenitor-core/docs/guide/progenitor-guide.html", "guide"),
        };
        if (extra != null) entries.Add((extra, "x"));
        return Zip(entries.ToArray());
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                var bytes = Encoding.UTF8.GetBytes(text);
                s.Write(bytes, 0, bytes.Length);
            }

        return ms.ToArray();
    }

    private string PlaceArchive(string folder, byte[] bytes)
    {
        string path = Path.Combine(_bep, folder.Replace('/', Path.DirectorySeparatorChar), PackagePayload.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_bep, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
}
