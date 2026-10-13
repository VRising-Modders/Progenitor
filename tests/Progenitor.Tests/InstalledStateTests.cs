using System;
using System.IO;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

public sealed class InstalledStateTests : IDisposable
{
    private readonly string _root;

    public InstalledStateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "progenitor-installed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // INSTALLUX2 item 1: the confirmation is a modal dialog on its own panel now. Ui.ResetForTests
        // only FORGETS the open one, so without this its rows outlive the test and the next test's
        // click on prompt:<key> resolves to the dead one.
        Progenitor.Ui.UiDialog.Current?.Close();
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Each_state_comes_from_synthetic_folders_and_inventory()
    {
        byte[] matching = { 9, 8, 7, 6 };
        string sha = InstalledState.HashBytes(matching);
        var entry = Entry("example.mod", "1.2.0", sha);

        Assert.Equal("not-installed", InstalledState.Detect(entry, new InstalledStateQuery()).StateName);

        string pendingDir = Path.Combine(_root, "BepInEx", InstalledState.PendingFolder, "index");
        string reloadDir = Path.Combine(_root, "BepInEx", InstalledState.ReloadFolder);
        Directory.CreateDirectory(pendingDir);
        Directory.CreateDirectory(reloadDir);
        File.WriteAllBytes(Path.Combine(pendingDir, "example.mod.dll"), matching);
        var scanned = InstalledState.Scan(Path.Combine(_root, "BepInEx"), null);
        var pending = InstalledState.Detect(entry, scanned);
        Assert.Equal(InstalledKind.Pending, pending.State);
        Assert.Equal(InstalledState.ByProgenitor, pending.By);
        Assert.Equal(InstalledState.PendingReason, pending.Reason);
        Assert.Single(pending.Files);

        File.WriteAllBytes(Path.Combine(reloadDir, "example.mod.dll"), matching);
        var loaded = InstalledState.Detect(entry, InstalledState.Scan(Path.Combine(_root, "BepInEx"), null, loadedGeneration: "33"));
        Assert.Equal(InstalledKind.Loaded, loaded.State);
        Assert.Equal(InstalledState.LoadedReason("33"), loaded.Reason);
        Assert.Equal(2, loaded.Files.Count);

        File.Delete(Path.Combine(reloadDir, "example.mod.dll"));
        File.WriteAllBytes(Path.Combine(pendingDir, "example.mod.dll"), new byte[] { 1 });
        var update = InstalledState.Detect(entry, InstalledState.Scan(Path.Combine(_root, "BepInEx"), null));
        Assert.Equal(InstalledKind.UpdateAvailable, update.State);

        var elsewhere = InstalledState.Detect(
            entry,
            new InstalledStateQuery(
                inventory: new[] { new InstalledInventoryRow("example.mod", "1.0.0", "Local") }));
        Assert.Equal(InstalledKind.ManagedElsewhere, elsewhere.State);
        Assert.Equal(InstalledState.ByOther, elsewhere.By);
        Assert.Contains("update available: 1.0.0 → 1.2.0", elsewhere.Reason, StringComparison.Ordinal);
        Assert.Equal("1.0.0", elsewhere.LoadedVersion);
        // Revert: matching a plugins/ file, or skipping inventory for managed-elsewhere, fails this.
    }

    [Fact]
    public void Remove_deletes_only_progenitor_files_and_refuses_plugins()
    {
        string pendingDir = Path.Combine(_root, "BepInEx", InstalledState.PendingFolder, "index");
        string reloadDir = Path.Combine(_root, "BepInEx", InstalledState.ReloadFolder);
        string plugins = Path.Combine(_root, "BepInEx", InstalledState.PluginsFolder);
        Directory.CreateDirectory(pendingDir);
        Directory.CreateDirectory(reloadDir);
        Directory.CreateDirectory(plugins);
        string pending = Path.Combine(pendingDir, "example.mod.dll");
        string reload = Path.Combine(reloadDir, "example.mod.dll");
        string other = Path.Combine(plugins, "foreign.dll");
        File.WriteAllBytes(pending, new byte[] { 1 });
        File.WriteAllBytes(reload, new byte[] { 2 });
        File.WriteAllBytes(other, new byte[] { 3 });

        Assert.True(InstalledState.IsProgenitorOwnedPath(pending));
        Assert.True(InstalledState.IsProgenitorOwnedPath(reload));
        Assert.False(InstalledState.IsProgenitorOwnedPath(other));
        Assert.True(InstalledState.Remove(new[] { pending, reload }, out _));
        Assert.False(File.Exists(pending));
        Assert.False(File.Exists(reload));
        Assert.True(File.Exists(other));
        Assert.False(InstalledState.Remove(new[] { other }, out var error));
        Assert.Contains("Progenitor-written", error, StringComparison.Ordinal);
        Assert.True(File.Exists(other));
        // Revert: deleting BepInEx/plugins files, or leaving pending/reload behind, fails this.
    }

    [Fact]
    public void Version_compare_and_ledger_absence_are_tolerated()
    {
        Assert.True(InstalledState.IsNewer("0.11.0", "0.10.2"));
        Assert.False(InstalledState.IsNewer("0.9.0", "0.11.0"));
        Assert.Equal(0, InstalledState.CompareVersions("1.0.0", "1.0.0"));
        var empty = InstalledState.ParseLedger(null);
        Assert.Empty(empty);
        var rows = InstalledState.ParseLedger(
            "# sha256\tfileName\tid\tversion\n"
            + "abcd\texample.mod.dll\texample.mod\t1.0.0\thost\n");
        Assert.Equal("example.mod", rows[0].Id);
        Assert.Equal("abcd", rows[0].Sha256);
        var entry = Entry("example.mod", "1.0.0", "abcd");
        var fromLedger = InstalledState.Detect(
            entry,
            new InstalledStateQuery(ledger: rows));
        Assert.Equal(InstalledKind.Pending, fromLedger.State);
        // Revert: throwing on a missing ledger, or treating 0.11 as older than 0.10, fails this.
    }

    private static CatalogEntry Entry(string id, string version, string sha) =>
        new(
            new ManifestMod(id, version, NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, true, "n", id, sha, 1),
            CatalogProvenance.ManifestVerified,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2025-03-15T12:00:00Z",
            displayName: id,
            side: PackageSide.Client);
}
