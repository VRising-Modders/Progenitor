using System;
using System.IO;
using Progenitor.Bootstrap;
using Progenitor.Hosted;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of hosted Step C layout: the bundled child must get
/// <c>plugins/Progenitor.Bootstrap.dll</c> plus <c>progenitor-core/Progenitor.dll</c>, never a
/// leftover <c>plugins/Progenitor.dll</c>. Disk I/O and junctions stay in Staging's game-only
/// half; these tests compile that file under <c>PROGENITOR_TESTS</c>.
/// </summary>
public class HostedStagingTests
{
    private const string GameRoot = @"C:\FakeSteam\steamapps\common\VRising";
    private const string BundledServer = GameRoot + @"\VRising_Server";
    private const string BepInEx = GameRoot + @"\BepInEx";

    [Fact]
    public void Layout_strings_match_Bootstrap_CoreLayout()
    {
        Assert.Equal(CoreLayout.FolderName, Staging.Layout.CoreFolderName);
        Assert.Equal(CoreLayout.FileName, Staging.Layout.CoreDllName);
        Assert.Equal(CoreLayout.DllPath(BepInEx), Staging.Layout.CoreDllPath(BepInEx));
        Assert.Equal(
            Path.Combine(BepInEx, "plugins", "Progenitor.Bootstrap.dll"),
            Staging.Layout.BootstrapDllPath(BepInEx));
        Assert.Equal(
            Path.Combine(BepInEx, "plugins", "Progenitor.dll"),
            Staging.Layout.StalePluginsCoreDllPath(BepInEx));
    }

    [Fact]
    public void Core_and_Bootstrap_paths_reject_a_missing_root()
    {
        Assert.Throws<ArgumentException>(() => Staging.Layout.CoreDllPath(""));
        Assert.Throws<ArgumentException>(() => Staging.Layout.BootstrapDllPath(null!));
        Assert.Throws<ArgumentException>(() => Staging.Layout.StalePluginsCoreDllPath(""));
    }

    [Fact]
    public void AllSharedFolders_includes_progenitor_core_once()
    {
        var names = Staging.Layout.AllSharedFolders;
        Assert.Contains("core", names);
        Assert.Contains("plugins", names);
        Assert.Contains("progenitor-reload", names);
        Assert.Contains(Staging.Layout.CoreFolderName, names);
        int cores = 0;
        foreach (var n in names)
        {
            if (Staging.Layout.IsCoreFolderName(n)) cores++;
        }

        Assert.Equal(1, cores);
    }

    [Fact]
    public void Shared_mode_links_plugins_but_copies_progenitor_core()
    {
        var linked = Staging.Layout.FoldersToLink(HostedServerPluginsMode.Shared);
        Assert.Contains(Staging.Layout.PluginsFolderName, linked);
        Assert.DoesNotContain(Staging.Layout.CoreFolderName, linked);
        Assert.Contains("progenitor-reload", linked);
        Assert.False(Staging.Layout.SkipSharedFolder(HostedServerPluginsMode.Shared, "plugins"));
        Assert.True(Staging.Layout.SkipSharedFolder(HostedServerPluginsMode.Shared, "progenitor-core"));
        Assert.True(Staging.Layout.SkipSharedFolder(HostedServerPluginsMode.Shared, "Progenitor-Core"));
    }

    [Fact]
    public void ProgenitorOnly_skips_plugins_and_progenitor_core_in_the_link_loop()
    {
        var linked = Staging.Layout.FoldersToLink(HostedServerPluginsMode.ProgenitorOnly);
        Assert.DoesNotContain(Staging.Layout.PluginsFolderName, linked);
        Assert.DoesNotContain(Staging.Layout.CoreFolderName, linked);
        Assert.Contains("core", linked);
        Assert.Contains("progenitor-reload", linked);
        Assert.True(Staging.Layout.SkipSharedFolder(HostedServerPluginsMode.ProgenitorOnly, "plugins"));
        Assert.True(Staging.Layout.SkipSharedFolder(HostedServerPluginsMode.ProgenitorOnly, "Plugins"));
        Assert.True(Staging.Layout.SkipSharedFolder(HostedServerPluginsMode.ProgenitorOnly, "progenitor-core"));
        Assert.False(Staging.Layout.SkipSharedFolder(HostedServerPluginsMode.ProgenitorOnly, "patchers"));
    }

    [Theory]
    [InlineData("Progenitor.dll")]
    [InlineData("Progenitor.dll.next")]
    [InlineData("Progenitor.dll.prev")]
    [InlineData("progenitor.dll")]
    public void Leftover_Core_file_names_in_plugins_are_stale(string fileName)
    {
        Assert.True(Staging.Layout.IsStalePluginsCoreFile(fileName));
    }

    [Theory]
    [InlineData("Progenitor.Bootstrap.dll")]
    [InlineData("Progenitor.Bootstrap.dll.next")]
    [InlineData("SomeOther.dll")]
    [InlineData("")]
    [InlineData(null)]
    public void Bootstrap_and_other_plugin_files_are_not_stale_Core(string? fileName)
    {
        Assert.False(Staging.Layout.IsStalePluginsCoreFile(fileName));
    }

    [Fact]
    public void Missing_stale_dll_is_none()
    {
        Assert.Equal(
            Staging.Layout.StaleCoreDllDisposition.None,
            Staging.Layout.DecideStalePluginsCoreDll(staleDllExists: false, pluginsFolderIsReparsePoint: true));
        Assert.Equal(
            Staging.Layout.StaleCoreDllDisposition.None,
            Staging.Layout.DecideStalePluginsCoreDll(staleDllExists: false, pluginsFolderIsReparsePoint: false));
    }

    [Fact]
    public void Stale_dll_through_a_junction_is_refused()
    {
        // Shared mode junctions plugins at the client. Deleting would unlink the client's file.
        Assert.Equal(
            Staging.Layout.StaleCoreDllDisposition.Refuse,
            Staging.Layout.DecideStalePluginsCoreDll(staleDllExists: true, pluginsFolderIsReparsePoint: true));
    }

    [Fact]
    public void Stale_dll_in_a_real_child_folder_is_deleted()
    {
        // ProgenitorOnly writes a real plugins folder under serverDir.
        Assert.Equal(
            Staging.Layout.StaleCoreDllDisposition.Delete,
            Staging.Layout.DecideStalePluginsCoreDll(staleDllExists: true, pluginsFolderIsReparsePoint: false));
    }

    [Fact]
    public void ProgenitorOnly_plugins_looks_staged_with_Bootstrap_and_no_stale_Core()
    {
        Assert.True(Staging.Layout.ProgenitorOnlyFolderLooksStaged(
            Staging.Layout.PluginsFolderName,
            destExists: true,
            destIsReparsePoint: false,
            markerPresent: true,
            markerContents: "stamp-v3",
            expectedStamp: "stamp-v3",
            expectedDllPresent: true,
            stalePluginsCoreDllPresent: false));
    }

    [Fact]
    public void ProgenitorOnly_plugins_with_leftover_Core_dll_is_not_staged()
    {
        Assert.False(Staging.Layout.ProgenitorOnlyFolderLooksStaged(
            Staging.Layout.PluginsFolderName,
            destExists: true,
            destIsReparsePoint: false,
            markerPresent: true,
            markerContents: "stamp-v3",
            expectedStamp: "stamp-v3",
            expectedDllPresent: true,
            stalePluginsCoreDllPresent: true));
    }

    [Fact]
    public void ProgenitorOnly_plugins_junction_is_not_staged()
    {
        Assert.False(Staging.Layout.ProgenitorOnlyFolderLooksStaged(
            Staging.Layout.PluginsFolderName,
            destExists: true,
            destIsReparsePoint: true,
            markerPresent: true,
            markerContents: "stamp-v3",
            expectedStamp: "stamp-v3",
            expectedDllPresent: true,
            stalePluginsCoreDllPresent: false));
    }

    [Fact]
    public void ProgenitorOnly_core_folder_looks_staged_with_the_Core_dll()
    {
        Assert.True(Staging.Layout.ProgenitorOnlyFolderLooksStaged(
            Staging.Layout.CoreFolderName,
            destExists: true,
            destIsReparsePoint: false,
            markerPresent: true,
            markerContents: "stamp-v3",
            expectedStamp: "stamp-v3",
            expectedDllPresent: true,
            stalePluginsCoreDllPresent: true));
    }

    [Fact]
    public void ProgenitorOnly_core_folder_without_the_dll_is_not_staged()
    {
        Assert.False(Staging.Layout.ProgenitorOnlyFolderLooksStaged(
            Staging.Layout.CoreFolderName,
            destExists: true,
            destIsReparsePoint: false,
            markerPresent: true,
            markerContents: "stamp-v3",
            expectedStamp: "stamp-v3",
            expectedDllPresent: false,
            stalePluginsCoreDllPresent: false));
    }

    [Fact]
    public void ProgenitorOnly_stale_stamp_is_not_staged()
    {
        Assert.False(Staging.Layout.ProgenitorOnlyFolderLooksStaged(
            Staging.Layout.PluginsFolderName,
            destExists: true,
            destIsReparsePoint: false,
            markerPresent: true,
            markerContents: "stamp-old",
            expectedStamp: "stamp-v3",
            expectedDllPresent: true,
            stalePluginsCoreDllPresent: false));
    }

    [Fact]
    public void May_delete_a_marked_progenitor_core_folder_under_server_BepInEx()
    {
        var dest = Path.Combine(BundledServer, "BepInEx", Staging.Layout.CoreFolderName);
        Assert.True(Staging.Layout.MayRecursiveDeleteStagedFolder(
            dest, BundledServer, markerPresent: true, out var reason), reason);
        Assert.Equal("", reason);
    }

    [Fact]
    public void Must_not_delete_an_unmarked_progenitor_core_folder()
    {
        var dest = Path.Combine(BundledServer, "BepInEx", Staging.Layout.CoreFolderName);
        Assert.False(Staging.Layout.MayRecursiveDeleteStagedFolder(
            dest, BundledServer, markerPresent: false, out var reason));
        Assert.Contains(HostedPathRules.StagedCopyMarkerName, reason);
    }

    [Fact]
    public void Must_not_delete_progenitor_core_under_the_live_dedicated_server()
    {
        var dest = Path.Combine(HostedPathRules.ForbiddenLiveServerRoot, "BepInEx", Staging.Layout.CoreFolderName);
        Assert.False(Staging.Layout.MayRecursiveDeleteStagedFolder(
            dest, HostedPathRules.ForbiddenLiveServerRoot, markerPresent: true, out var reason));
        Assert.Contains("live dedicated server", reason);
    }

    [Fact]
    public void Must_not_delete_progenitor_core_outside_serverDir()
    {
        var dest = Path.Combine(GameRoot, "BepInEx", Staging.Layout.CoreFolderName);
        Assert.False(Staging.Layout.MayRecursiveDeleteStagedFolder(
            dest, BundledServer, markerPresent: true, out var reason));
        Assert.Contains("not inside serverDir", reason);
    }

    [Fact]
    public void Marked_plugins_still_deletes_through_the_shared_folder_rule()
    {
        var dest = Path.Combine(BundledServer, "BepInEx", "plugins");
        Assert.True(Staging.Layout.MayRecursiveDeleteStagedFolder(
            dest, BundledServer, markerPresent: true, out var reason), reason);
    }

    [Fact]
    public void Linkable_folder_names_include_progenitor_core_and_shared_loader_folders()
    {
        Assert.True(Staging.Layout.IsLinkableFolderName("plugins"));
        Assert.True(Staging.Layout.IsLinkableFolderName("progenitor-core"));
        Assert.True(Staging.Layout.IsLinkableFolderName("Progenitor-Core"));
        Assert.True(Staging.Layout.IsLinkableFolderName("core"));
        Assert.False(Staging.Layout.IsLinkableFolderName("config"));
        Assert.False(Staging.Layout.IsLinkableFolderName("cache"));
        Assert.False(Staging.Layout.IsLinkableFolderName("interop"));
    }

    [Fact]
    public void Copied_progenitor_core_is_not_a_shared_junction_check()
    {
        // A junction at the client's progenitor-core is never "already staged": the child's
        // .next promote would File.Move the running client's Core.
        Assert.False(Staging.Layout.ProgenitorOnlyFolderLooksStaged(
            Staging.Layout.CoreFolderName,
            destExists: true,
            destIsReparsePoint: true,
            markerPresent: false,
            markerContents: null,
            expectedStamp: "stamp-v4",
            expectedDllPresent: true,
            stalePluginsCoreDllPresent: false));
        Assert.True(Staging.Layout.ProgenitorOnlyFolderLooksStaged(
            Staging.Layout.CoreFolderName,
            destExists: true,
            destIsReparsePoint: false,
            markerPresent: true,
            markerContents: "stamp-v4",
            expectedStamp: "stamp-v4",
            expectedDllPresent: true,
            stalePluginsCoreDllPresent: false));
    }

    [Fact]
    public void Next_sidecar_must_match_the_client()
    {
        Assert.True(Staging.Layout.NextSidecarLooksStaged(
            clientHasNext: false, destHasNext: false, nextFilesMatch: false));
        Assert.False(Staging.Layout.NextSidecarLooksStaged(
            clientHasNext: false, destHasNext: true, nextFilesMatch: false));
        Assert.False(Staging.Layout.NextSidecarLooksStaged(
            clientHasNext: true, destHasNext: false, nextFilesMatch: false));
        Assert.False(Staging.Layout.NextSidecarLooksStaged(
            clientHasNext: true, destHasNext: true, nextFilesMatch: false));
        Assert.True(Staging.Layout.NextSidecarLooksStaged(
            clientHasNext: true, destHasNext: true, nextFilesMatch: true));
    }

    [Fact]
    public void Copied_core_with_client_next_and_no_child_next_is_not_staged()
    {
        Assert.False(Staging.Layout.ProgenitorOnlyFolderLooksStaged(
            Staging.Layout.CoreFolderName,
            destExists: true,
            destIsReparsePoint: false,
            markerPresent: true,
            markerContents: "stamp-v4",
            expectedStamp: "stamp-v4",
            expectedDllPresent: true,
            stalePluginsCoreDllPresent: false,
            clientHasNext: true,
            destHasNext: false,
            nextFilesMatch: false));
    }

    [Fact]
    public void Stamp_token_includes_next_and_prev_ticks()
    {
        Assert.Equal("v4", Staging.Layout.StampVersion);
        Assert.Equal("layout=bootstrap+progenitor-core", Staging.Layout.StampLayoutLine);
        Assert.Equal("progenitorCoreUtcTicks=123", Staging.Layout.StampDllUtcLine("progenitorCore", 123));
        Assert.Equal("progenitorCoreNextUtcTicks=456", Staging.Layout.StampNextUtcLine("progenitorCore", exists: true, 456));
        Assert.Equal("progenitorCoreNextUtcTicks=0", Staging.Layout.StampNextUtcLine("progenitorCore", exists: false, 456));
        Assert.Equal("bootstrapPrevUtcTicks=0", Staging.Layout.StampPrevUtcLine("bootstrap", exists: false, 1));
        Assert.Equal("bootstrapPrevUtcTicks=9", Staging.Layout.StampPrevUtcLine("bootstrap", exists: true, 9));
    }

    [Fact]
    public void Leftover_Core_through_a_junction_must_refuse_spawn()
    {
        Assert.True(Staging.Layout.MustRefuseSpawn(Staging.Layout.StaleCoreDllDisposition.Refuse));
        Assert.False(Staging.Layout.MustRefuseSpawn(Staging.Layout.StaleCoreDllDisposition.None));
        Assert.False(Staging.Layout.MustRefuseSpawn(Staging.Layout.StaleCoreDllDisposition.Delete));
    }

    [Fact]
    public void Leftover_child_with_mapped_Core_refuses_the_copy()
    {
        Assert.True(Staging.Layout.MustRefuseCoreCopyWhileLeftoverChildAlive(
            leftoverBundledChildAlive: true, destNeedsWrite: true));
        Assert.False(Staging.Layout.MustRefuseCoreCopyWhileLeftoverChildAlive(
            leftoverBundledChildAlive: true, destNeedsWrite: false));
        Assert.False(Staging.Layout.MustRefuseCoreCopyWhileLeftoverChildAlive(
            leftoverBundledChildAlive: false, destNeedsWrite: true));
    }

    [Fact]
    public void Leftover_child_scan_fails_closed_on_throw_or_unidentified_processes()
    {
        Assert.True(Staging.Layout.LeftoverChildScanTreatAsAlive(
            scanThrew: true, processesSeen: 0, processesIdentified: 0, foundMatchingChild: false));
        Assert.True(Staging.Layout.LeftoverChildScanTreatAsAlive(
            scanThrew: false, processesSeen: 2, processesIdentified: 0, foundMatchingChild: false));
        Assert.True(Staging.Layout.LeftoverChildScanTreatAsAlive(
            scanThrew: false, processesSeen: 1, processesIdentified: 1, foundMatchingChild: true));
        Assert.False(Staging.Layout.LeftoverChildScanTreatAsAlive(
            scanThrew: false, processesSeen: 0, processesIdentified: 0, foundMatchingChild: false));
        Assert.False(Staging.Layout.LeftoverChildScanTreatAsAlive(
            scanThrew: false, processesSeen: 2, processesIdentified: 2, foundMatchingChild: false));
    }

    [Fact]
    public void Leftover_bundled_child_owns_dest_under_VRising_Server()
    {
        var dest = Path.Combine(BundledServer, "BepInEx", Staging.Layout.CoreFolderName, Staging.Layout.CoreDllName);
        var childExe = BundledServer + @"\VRisingServer.exe";
        Assert.True(Staging.Layout.LeftoverChildOwnsDest(childExe, BundledServer, dest));
        Assert.False(Staging.Layout.LeftoverChildOwnsDest(
            HostedPathRules.ForbiddenLiveServerRoot + @"\VRisingServer.exe",
            BundledServer,
            dest));
        Assert.False(Staging.Layout.LeftoverChildOwnsDest(
            childExe,
            HostedPathRules.ForbiddenLiveServerRoot,
            Path.Combine(HostedPathRules.ForbiddenLiveServerRoot, "BepInEx", Staging.Layout.CoreFolderName, Staging.Layout.CoreDllName)));
        Assert.False(Staging.Layout.LeftoverChildOwnsDest(childExe, BundledServer, GameRoot + @"\BepInEx\progenitor-core\Progenitor.dll"));
        Assert.False(Staging.Layout.LeftoverChildOwnsDest(null, BundledServer, dest));
    }
}
