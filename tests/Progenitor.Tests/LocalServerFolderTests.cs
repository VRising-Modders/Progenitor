using System;
using System.IO;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Same-machine dedicated-server folder validator. Fixtures live under %TEMP%. Tests must
/// not touch the production server. Each comment names the guard whose removal fails the test.
/// </summary>
public sealed class LocalServerFolderTests : IDisposable
{
    private readonly string _root;

    public LocalServerFolderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "progenitor-tabs2-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        LocalServerFolder.DosDeviceLookup = null;
        LocalServerFolder.ShareLookup = null;
        LocalServerFolder.ReparseLookup = null;
        LocalServerFolder.WriteAccessOverride = null;
        try { Directory.Delete(_root, recursive: true); }
        catch { /* temp */ }
    }

    [Fact]
    public void Valid_layout_stores_friendly_name_and_identity()
    {
        string path = MakeServer("valid");
        var folder = new LocalServerFolder();
        var result = folder.Apply(path);
        Assert.True(result.Ok, result.Refusal);
        Assert.True(result.Available);
        Assert.True(folder.HasValidatedServer);
        Assert.Equal("valid", result.FriendlyName);
        Assert.False(string.IsNullOrEmpty(result.Identity));
        Assert.True(LocalServerFolder.IsUnderOrEqual(result.CanonicalPath!, result.CanonicalPath!));
        Assert.True(folder.CanWrite(out var refusal), refusal);
        // Revert: skipping the exe/BepInEx/write checks still constructs Ok if those asserts are dropped.
        Assert.True(File.Exists(Path.Combine(result.CanonicalPath!, LocalServerFolder.ServerExecutableName)));
        Assert.True(Directory.Exists(Path.Combine(result.CanonicalPath!, LocalServerFolder.BepInExFolderName)));
    }

    [Fact]
    public void NormalizeTyped_strips_invisible_marks_quotes_whitespace_and_expands_variables()
    {
        string server = MakeServer("norm");
        Assert.Equal(server, LocalServerFolder.NormalizeTyped("\u200B \"" + server + "\" \uFEFF\u200C\u2060"));
        Assert.Equal("", LocalServerFolder.NormalizeTyped("\u200B \t\uFEFF"));
        Assert.Equal("", LocalServerFolder.NormalizeTyped(null));
        Environment.SetEnvironmentVariable("PROGENITOR_CONF1_TEST", _root);
        try
        {
            Assert.Equal(server, LocalServerFolder.NormalizeTyped("%PROGENITOR_CONF1_TEST%\\norm"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROGENITOR_CONF1_TEST", null);
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home + "\\x", LocalServerFolder.NormalizeTyped("~\\x"));

        // The validator and Apply go through the same normalization: a marker-wrapped real folder is valid.
        var folder = new LocalServerFolder();
        var result = folder.Apply("\u200B" + server + "\u200B");
        Assert.True(result.Ok, result.Refusal);
        Assert.True(folder.HasValidatedServer);
        Assert.False(new LocalServerFolder().Apply("\u200B \u200B").Ok);
        // Revert: validating the literal text (U+200B kept) refuses the folder as not an existing directory.
    }

    [Fact]
    public void Missing_exe_is_refused()
    {
        string path = Path.Combine(_root, "no-exe");
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, LocalServerFolder.BepInExFolderName));
        var result = LocalServerFolder.Validate(path);
        Assert.False(result.Ok);
        Assert.Equal(LocalServerFolder.RefusalMissingExe, result.Refusal);
        // Revert: treating a BepInEx folder without VRisingServer.exe as valid fails this.
    }

    [Fact]
    public void Client_root_is_refused()
    {
        string path = Path.Combine(_root, "client");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, LocalServerFolder.ClientExecutableName), "client");
        Directory.CreateDirectory(Path.Combine(path, LocalServerFolder.BepInExFolderName));
        var result = LocalServerFolder.Validate(path);
        Assert.False(result.Ok);
        Assert.Equal(LocalServerFolder.RefusalClientRoot, result.Refusal);
        // Revert: ignoring VRising.exe (client root) lets this folder pass.
    }

    [Fact]
    public void Escaping_link_is_refused()
    {
        string server = MakeServer("link-root");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        string bep = Path.Combine(server, LocalServerFolder.BepInExFolderName);
        Directory.Delete(bep);
        bool linked = TrySymbolicLink(bep, outside);
        if (!linked)
        {
            LocalServerFolder.ReparseLookup = path =>
                path.EndsWith(LocalServerFolder.BepInExFolderName, StringComparison.OrdinalIgnoreCase)
                    ? outside
                    : null;
            Directory.CreateDirectory(bep);
        }

        try
        {
            var result = LocalServerFolder.Validate(server);
            Assert.False(result.Ok);
            Assert.Equal(LocalServerFolder.RefusalEscapingLink, result.Refusal);
        }
        finally
        {
            LocalServerFolder.ReparseLookup = null;
        }
        // Revert: skipping the BepInEx-under-root check after following the link accepts this folder.
    }

    [Fact]
    public void Unwritable_folder_is_refused()
    {
        string path = MakeServer("ro");
        LocalServerFolder.WriteAccessOverride = _ => false;
        try
        {
            var result = LocalServerFolder.Validate(path);
            Assert.False(result.Ok);
            Assert.Equal(LocalServerFolder.RefusalUnwritable, result.Refusal);
        }
        finally
        {
            LocalServerFolder.WriteAccessOverride = null;
        }
        // Revert: dropping the write-access probe (or ignoring WriteAccessOverride) makes Ok true.
    }

    [Fact]
    public void Later_invalid_folder_is_unavailable_and_blocks_writes()
    {
        string path = MakeServer("later");
        var folder = new LocalServerFolder();
        Assert.True(folder.Apply(path).Ok);
        File.Delete(Path.Combine(path, LocalServerFolder.ServerExecutableName));
        var later = folder.Revalidate();
        Assert.False(later.Ok);
        Assert.False(later.Available);
        Assert.True(folder.IsUnavailable);
        Assert.False(folder.HasValidatedServer);
        Assert.False(folder.CanWrite(out var refusal));
        Assert.False(string.IsNullOrEmpty(refusal));
        Assert.False(folder.TryContainWrite(Path.Combine(path, "BepInEx", "x.dll"), out _));
        var choices = Progenitor.Ui.InstallModsPane.TargetChoices(folder);
        Assert.Equal(3, choices.Count);
        Assert.False(choices[1].Available);
        Assert.False(choices[2].Available);
        Assert.True(Progenitor.Ui.InstallModsPane.TargetOperationBlocked(InstallTarget.LocalServer, folder));
        // Revert: Revalidate keeping HasValidatedServer, or hiding the targets, fails this.
    }

    [Fact]
    public void Device_paths_and_empty_are_refused_and_the_live_server_is_refused_by_the_shared_fence()
    {
        Assert.Equal(LocalServerFolder.RefusalEmpty, LocalServerFolder.Validate("").Refusal);
        Assert.Equal(LocalServerFolder.RefusalUnrecognizedForm, LocalServerFolder.Validate(@"\\?\Volume{abcd}\VRising").Refusal);
        Assert.Equal(LocalServerFolder.RefusalUnrecognizedForm, LocalServerFolder.Validate(@"\\.\PhysicalDrive0").Refusal);
        Assert.Equal(LocalServerFolder.UnrecognisedSentinel, LocalServerFolder.Canonicalize(@"\\?\GLOBALROOT\Device\HarddiskVolume3\VRising"));

        // Review H1 (2026-10-08): the live dedicated server is refused through the product's shared fence (world saves,
        // staging, the hosted launcher and world restores already use it); the folder file names no machine path itself.
        Assert.Equal(LocalServerFolder.RefusalLiveServer, LocalServerFolder.Validate(Progenitor.WorldRestore.WorldRestorePaths.ForbiddenLiveServerRoot).Refusal);
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "LocalServerFolder.cs");
        Assert.DoesNotContain(@"C:\VRisingServer", src, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WorldRestore.WorldRestorePaths.IsLiveServerPath(path)", src, StringComparison.Ordinal);
        // Revert: dropping the fence lets the picker set the live server as the folder Progenitor stops and starts.
    }

    [Fact]
    public void Write_containment_refuses_a_path_outside_the_registered_root()
    {
        string path = MakeServer("contain");
        var folder = new LocalServerFolder();
        Assert.True(folder.Apply(path).Ok);
        Assert.True(folder.TryContainWrite(Path.Combine(path, "BepInEx", "pending.dll"), out _));
        Assert.False(folder.TryContainWrite(Path.Combine(_root, "outside", "escape.dll"), out var refusal));
        Assert.Equal(LocalServerFolder.RefusalWriteEscapes, refusal);
        // Revert: skipping IsUnderOrEqual on the destination accepts the outside write.
    }

    [Fact]
    public void Apply_empty_clears_and_a_refused_apply_keeps_the_previous_valid_folder()
    {
        string path = MakeServer("keep");
        var folder = new LocalServerFolder();
        Assert.True(folder.Apply(path).Ok);
        var refused = folder.Apply(Path.Combine(_root, "nope"));
        Assert.False(refused.Ok);
        Assert.True(folder.HasValidatedServer);
        Assert.True(folder.Apply("").Refusal == LocalServerFolder.RefusalEmpty || !folder.HasValidatedServer);
        Assert.False(folder.HasValidatedServer);
        // The last Apply("") is Clear. The refused Apply must not have cleared it first —
        // HasValidatedServer was true immediately after the nope apply (asserted above).
    }

    private string MakeServer(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, LocalServerFolder.ServerExecutableName), "exe");
        Directory.CreateDirectory(Path.Combine(path, LocalServerFolder.BepInExFolderName));
        return path;
    }

    private static bool TrySymbolicLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }
}
