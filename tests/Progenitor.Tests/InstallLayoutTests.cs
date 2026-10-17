using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Progenitor.Bootstrap;
using Progenitor.PluginActions;
using Progenitor.SelfUpdate;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Single-folder install (<c>BepInEx/plugins/Progenitor/</c>): where Core is resolved from (new, legacy, both, neither,
/// a mod-manager folder, a hosted child), what Bootstrap does with the preloader and with a legacy plugins-root
/// Bootstrap at start, and the preloader's own "Progenitor is gone" check. Game-free: string paths over a fake file
/// set, and temp folders for the file moves.
/// </summary>
public class InstallLayoutTests
{
    private const string Root = @"C:\Game\BepInEx";

    private static string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    private static readonly string HomeDir = P("plugins", "Progenitor");
    private static readonly string NewCore = P("plugins", "Progenitor", "progenitor-core", "Progenitor.core");
    private static readonly string LegacyCore = P("progenitor-core", "Progenitor.dll");
    private static readonly string LegacyCoreImage = P("progenitor-core", "Progenitor.core");

    private static Func<string, bool> Files(params string[] present)
    {
        var set = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    // ---- resolution ---------------------------------------------------------------------------------------------

    [Fact]
    public void New_layout_resolves_beside_Bootstrap()
    {
        var home = InstallLayout.Resolve(Root, HomeDir, false, Files(NewCore));
        Assert.True(home.Found);
        Assert.False(home.Legacy);
        Assert.Equal(NewCore, home.CoreFile);
        Assert.Equal(Path.GetDirectoryName(NewCore), home.Folder);
        Assert.Null(home.StaleLegacyFolder);
    }

    [Fact]
    public void Legacy_layout_still_resolves_under_its_old_file_name()
    {
        var home = InstallLayout.Resolve(Root, HomeDir, false, Files(LegacyCore));
        Assert.True(home.Found);
        Assert.True(home.Legacy);
        Assert.Equal(LegacyCore, home.CoreFile);
        Assert.Equal(P("progenitor-core"), home.Folder);
    }

    [Fact]
    public void Legacy_folder_prefers_the_new_file_name_over_the_old_one()
    {
        var home = InstallLayout.Resolve(Root, P("plugins"), false, Files(LegacyCore, LegacyCoreImage));
        Assert.Equal(LegacyCoreImage, home.CoreFile);
    }

    [Fact]
    public void Both_present_the_new_folder_wins_and_the_legacy_one_is_reported_stale()
    {
        var home = InstallLayout.Resolve(Root, HomeDir, false, Files(NewCore, LegacyCore));
        Assert.Equal(NewCore, home.CoreFile);
        Assert.False(home.Legacy);
        Assert.Equal(P("progenitor-core"), home.StaleLegacyFolder);
        Assert.Contains(P("progenitor-core"), InstallLayout.FormatStaleLegacy(home));
    }

    [Fact]
    public void Neither_present_lists_every_path_tried_and_points_at_the_new_folder()
    {
        var home = InstallLayout.Resolve(Root, HomeDir, false, Files());
        Assert.False(home.Found);
        Assert.False(home.Legacy);
        Assert.Equal(NewCore, home.CoreFile);
        // Bootstrap's folder is the canonical one, so it is tried once.
        Assert.Equal(new[] { NewCore, LegacyCoreImage, LegacyCore }, home.Tried);
        string line = InstallLayout.FormatMissing(home);
        Assert.StartsWith("Core missing:", line);
        foreach (var path in home.Tried) Assert.Contains(path, line);
    }

    [Fact]
    public void Neither_present_but_a_legacy_folder_exists_points_there()
    {
        var dirs = Files(P("progenitor-core"));
        var home = InstallLayout.Resolve(Root, HomeDir, false, Files(), dirs);
        Assert.False(home.Found);
        Assert.Equal(P("progenitor-core"), home.Folder);
        Assert.Equal(LegacyCoreImage, home.CoreFile);
    }

    [Fact]
    public void A_Progenitor_dll_under_plugins_is_never_taken_for_Core()
    {
        string pluginsDll = P("plugins", "Progenitor", "progenitor-core", "Progenitor.dll");
        var home = InstallLayout.Resolve(Root, HomeDir, false, Files(pluginsDll));
        Assert.False(home.Found);
        Assert.DoesNotContain(pluginsDll, home.Tried);
    }

    [Fact]
    public void A_renamed_package_folder_resolves_beside_its_own_Bootstrap_first()
    {
        string managed = P("plugins", "Some-Package", "Progenitor");
        string managedCore = Path.Combine(managed, "progenitor-core", "Progenitor.core");
        var home = InstallLayout.Resolve(Root, managed, false, Files(managedCore, NewCore));
        Assert.Equal(managedCore, home.CoreFile);
        Assert.Equal(managedCore, home.Tried[0]);
        Assert.Equal(NewCore, home.Tried[1]);
    }

    [Fact]
    public void A_hosted_child_reads_its_own_staged_copy_before_the_clients_folder()
    {
        // The child's plugins folder is a junction at the client's, so the client's Core is visible there too.
        var home = InstallLayout.Resolve(Root, HomeDir, true, Files(NewCore, LegacyCore));
        Assert.Equal(LegacyCore, home.CoreFile);
        Assert.True(home.Legacy);
        Assert.Null(home.StaleLegacyFolder);
        var none = InstallLayout.Resolve(Root, HomeDir, true, Files());
        Assert.Equal(LegacyCoreImage, none.CoreFile);
        Assert.True(none.Legacy);
    }

    [Fact]
    public void A_hosted_child_never_resolves_the_clients_files_through_the_junction()
    {
        // PR #48 review M1: only its own staged copy, even when that is missing and the client's Core is visible.
        string managedCore = P("plugins", "Some-Package", "Progenitor", "progenitor-core", "Progenitor.core");
        var home = InstallLayout.Resolve(Root, Path.GetDirectoryName(Path.GetDirectoryName(managedCore)), true,
            Files(NewCore, managedCore), Files(Path.GetDirectoryName(NewCore)!));
        Assert.False(home.Found);
        Assert.True(home.Legacy);
        Assert.Equal(P("progenitor-core"), home.Folder);
        Assert.Equal(new[] { LegacyCoreImage, LegacyCore }, home.Tried);
        Assert.DoesNotContain(home.Tried, t => t.Contains(Path.Combine("plugins", ""), StringComparison.OrdinalIgnoreCase));
    }

    // ---- Core's own resolution (PR #48 review M2) ---------------------------------------------------------------

    [Fact]
    public void Core_follows_the_resolution_its_Bootstrap_publishes()
    {
        var home = InstallLayout.ForCore(Root, true, Path.GetDirectoryName(NewCore), NewCore, Files(NewCore, LegacyCore));
        Assert.Equal(NewCore, home.CoreFile);
        Assert.True(home.Found);
        Assert.False(home.Legacy);
        var legacy = InstallLayout.ForCore(Root, true, P("progenitor-core"), LegacyCore, Files(LegacyCore));
        Assert.True(legacy.Legacy);
    }

    [Fact]
    public void Core_under_a_Bootstrap_that_publishes_nothing_uses_the_legacy_folder()
    {
        // An older Bootstrap loaded Core from BepInEx/progenitor-core, even with a single-folder Core on disk.
        var home = InstallLayout.ForCore(Root, true, null, null, Files(NewCore, LegacyCore));
        Assert.Equal(LegacyCore, home.CoreFile);
        Assert.True(home.Legacy);
        var none = InstallLayout.ForCore(Root, true, "", "", Files(NewCore));
        Assert.True(none.Legacy);
        Assert.False(none.Found);
    }

    [Fact]
    public void Core_without_any_Bootstrap_resolves_the_tree_normally()
    {
        var home = InstallLayout.ForCore(Root, false, null, null, Files(NewCore, LegacyCore));
        Assert.Equal(NewCore, home.CoreFile);
    }

    // ---- the running Bootstrap (PR #48 review L1) ---------------------------------------------------------------

    [Fact]
    public void The_running_Bootstraps_own_file_is_used_for_its_tree()
    {
        string root = TempRoot();
        try
        {
            string managed = Path.Combine(root, "plugins", "Some-Package", "Progenitor", InstallLayout.BootstrapFileName);
            string other = TempRoot();
            try
            {
                var home = new InstallLayout.CoreHome(Path.Combine(root, "x"), Path.Combine(root, "x", "Progenitor.core"), true, false, Array.Empty<string>(), null);
                InstallLayout.SetCurrent(root, home, managed);
                Assert.Equal(managed, InstallLayout.BootstrapFile(root));
                Assert.Equal(managed, SelfPaths.DefaultBootstrap(root));
                // Another tree: its single-folder Bootstrap, else its legacy one when only that exists.
                Assert.Equal(Path.Combine(InstallLayout.HomeFolder(other), InstallLayout.BootstrapFileName), InstallLayout.BootstrapFile(other));
                Directory.CreateDirectory(Path.Combine(other, "plugins"));
                File.WriteAllText(InstallLayout.LegacyBootstrapPath(other), "b");
                Assert.Equal(InstallLayout.LegacyBootstrapPath(other), InstallLayout.BootstrapFile(other));
            }
            finally
            {
                InstallLayout.SetCurrent(root, null);
                Cleanup(other);
            }
        }
        finally { Cleanup(root); }
    }

    // ---- releases from before the single-folder layout (PR #48 review H2) --------------------------------------

    [Fact]
    public void Only_an_image_that_carries_the_layout_type_counts_as_single_folder()
    {
        // This test assembly compiles InstallLayout in, exactly as Core and Bootstrap do; the BCL does not.
        Assert.True(InstallLayout.KnowsSingleFolder(File.ReadAllBytes(typeof(InstallLayout).Assembly.Location)));
        Assert.False(InstallLayout.KnowsSingleFolder(File.ReadAllBytes(typeof(Xunit.Assert).Assembly.Location)));
        Assert.False(InstallLayout.KnowsSingleFolder(new byte[] { 1, 2, 3 }));
        Assert.False(InstallLayout.KnowsSingleFolder(null));
    }

    [Fact]
    public void A_rollback_to_a_copy_from_before_the_layout_is_refused()
    {
        string root = TempRoot();
        try
        {
            string newCore = Path.Combine(root, "new.dll");
            string oldCore = Path.Combine(root, "old.dll");
            File.Copy(typeof(InstallLayout).Assembly.Location, newCore);
            File.Copy(typeof(Xunit.Assert).Assembly.Location, oldCore);
            Assert.True(BootHealth.PreviousFitsLayout(newCore, null));
            Assert.True(BootHealth.PreviousFitsLayout(newCore, newCore));
            Assert.False(BootHealth.PreviousFitsLayout(oldCore, null));
            Assert.False(BootHealth.PreviousFitsLayout(newCore, oldCore));
            // A missing kept Core is left to the restore's own error.
            Assert.True(BootHealth.PreviousFitsLayout(Path.Combine(root, "missing.dll"), null));
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void A_release_from_before_the_layout_is_refused_by_the_update_reader(bool coreNew, bool bootNew, bool ok)
    {
        var zip = SelfUpdateTests.Zip(
            SelfUpdateTests.CoreImage(5, "2026.10.20.0", "c", singleFolder: coreNew),
            SelfUpdateTests.BootImage(5, "2026.10.20.0", "b", singleFolder: bootNew), "2026.10.20");
        var image = Progenitor.Install.SelfImageReader.Read(zip);
        Assert.Equal(ok, image.Ok);
        if (!ok) Assert.Contains("install that release by hand", image.Error);
    }

    [Fact]
    public void A_rollback_to_a_synthetic_old_copy_is_refused_and_an_unreadable_one_is_left_to_the_restore()
    {
        string root = TempRoot();
        try
        {
            string oldCore = Path.Combine(root, "old.dll");
            string oldBoot = Path.Combine(root, "oldboot.dll");
            string newCore = Path.Combine(root, "new.dll");
            File.WriteAllBytes(oldCore, SelfUpdateTests.CoreImage(5, "2026.10.1.0", singleFolder: false));
            File.WriteAllBytes(oldBoot, SelfUpdateTests.BootImage(5, "2026.10.1.0", singleFolder: false));
            File.WriteAllBytes(newCore, SelfUpdateTests.CoreImage(5, "2026.10.2.0"));
            Assert.False(BootHealth.PreviousFitsLayout(oldCore, null));
            Assert.False(BootHealth.PreviousFitsLayout(newCore, oldBoot));
            Assert.True(BootHealth.PreviousFitsLayout(newCore, null));
            File.WriteAllText(Path.Combine(root, "text.dll"), "not an assembly");
            Assert.True(BootHealth.PreviousFitsLayout(Path.Combine(root, "text.dll"), null));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void A_legacy_Bootstrap_renamed_aside_by_deploy_is_deleted_at_the_next_start()
    {
        string root = TempRoot();
        try
        {
            string legacy = InstallLayout.LegacyBootstrapPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
            File.WriteAllText(legacy + LayoutAtStart.RemovedSuffix, "old");
            string running = Path.Combine(InstallLayout.HomeFolder(root), InstallLayout.BootstrapFileName);
            Assert.Null(LayoutAtStart.RemoveLegacyBootstrap(root, running, false, out _));
            Assert.False(File.Exists(legacy + LayoutAtStart.RemovedSuffix));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void The_process_resolution_answers_for_its_own_tree_only()
    {
        string root = TempRoot();
        try
        {
            string other = TempRoot();
            var mine = new InstallLayout.CoreHome(Path.Combine(root, "x"), Path.Combine(root, "x", "Progenitor.core"), true, false, Array.Empty<string>(), null);
            try
            {
                InstallLayout.SetCurrent(root, mine);
                Assert.Equal(Path.Combine(root, "x"), InstallLayout.CoreFolder(root));
                Assert.Equal(Path.Combine(other, "plugins", "Progenitor", "progenitor-core"), InstallLayout.CoreFolder(other));
            }
            finally
            {
                InstallLayout.SetCurrent(root, null);
                Cleanup(other);
            }
        }
        finally { Cleanup(root); }
    }

    // ---- preloader placement decisions ----------------------------------------------------------------------------

    [Theory]
    [InlineData(true, "2026.10.5.0", false, null, false, (int)InstallLayout.PreloaderAction.Place)]
    [InlineData(true, "2026.10.5.0", true, "2026.10.4.0", false, (int)InstallLayout.PreloaderAction.Replace)]
    [InlineData(true, "2026.10.5.0", true, "2026.10.5.0", true, (int)InstallLayout.PreloaderAction.Keep)]
    [InlineData(true, "2026.10.5.0", true, "2026.10.5.0", false, (int)InstallLayout.PreloaderAction.Replace)]
    [InlineData(true, "2026.10.5.0", true, "2026.10.6.0", false, (int)InstallLayout.PreloaderAction.Keep)]
    [InlineData(true, "2026.10.5.0", true, "", false, (int)InstallLayout.PreloaderAction.Replace)]
    [InlineData(false, null, true, "2026.10.4.0", false, (int)InstallLayout.PreloaderAction.NoSource)]
    [InlineData(true, "", false, null, false, (int)InstallLayout.PreloaderAction.NoSource)]
    public void Preloader_placement_decision(
        bool shippedExists, string? shipped, bool installedExists, string? installed, bool sameBytes,
        int expected)
    {
        static Version? V(string? s) => string.IsNullOrEmpty(s) ? null : Version.Parse(s);
        Assert.Equal((InstallLayout.PreloaderAction)expected, InstallLayout.DecidePreloader(shippedExists, V(shipped), installedExists, V(installed), sameBytes));
    }

    [Fact]
    public void Preloader_is_placed_updated_kept_and_never_downgraded_on_disk()
    {
        string root = TempRoot();
        var seam = LayoutAtStart.ReadVersion;
        try
        {
            // The test images carry their version as text; the seam reads it back.
            LayoutAtStart.ReadVersion = path => Version.TryParse(File.ReadAllText(path).Split('|')[0], out var v) ? v : null;
            string shipped = Path.Combine(root, "plugins", "Progenitor", InstallLayout.ShippedPreloaderFileName);
            string placed = InstallLayout.PatchersPreloaderPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(shipped)!);

            // Missing: placed.
            File.WriteAllText(shipped, "1.0.5.0|a");
            Assert.Contains("placed", LayoutAtStart.PlacePreloader(root, shipped, out bool ok1));
            Assert.True(ok1);
            Assert.Equal("1.0.5.0|a", File.ReadAllText(placed));

            // Same version and bytes: nothing.
            Assert.Null(LayoutAtStart.PlacePreloader(root, shipped, out _));

            // Newer shipped: replaced, and nothing is left beside it.
            File.WriteAllText(shipped, "1.0.6.0|b");
            Assert.Contains("updated", LayoutAtStart.PlacePreloader(root, shipped, out bool ok2));
            Assert.True(ok2);
            Assert.Equal("1.0.6.0|b", File.ReadAllText(placed));
            Assert.Equal(new[] { placed }, Directory.GetFiles(Path.GetDirectoryName(placed)!));

            // Older shipped (a downgraded folder): the newer placed copy stays.
            File.WriteAllText(shipped, "1.0.4.0|c");
            Assert.Null(LayoutAtStart.PlacePreloader(root, shipped, out _));
            Assert.Equal("1.0.6.0|b", File.ReadAllText(placed));

            // Nothing shipped: the installed copy is left alone.
            File.Delete(shipped);
            Assert.Null(LayoutAtStart.PlacePreloader(root, shipped, out _));
            Assert.True(File.Exists(placed));
        }
        finally
        {
            LayoutAtStart.ReadVersion = seam;
            Cleanup(root);
        }
    }

    [Fact]
    public void A_preloader_set_aside_at_an_earlier_start_is_cleaned_up()
    {
        string root = TempRoot();
        var seam = LayoutAtStart.ReadVersion;
        try
        {
            LayoutAtStart.ReadVersion = path => Version.Parse(File.ReadAllText(path));
            string shipped = Path.Combine(root, "plugins", "Progenitor", InstallLayout.ShippedPreloaderFileName);
            string placed = InstallLayout.PatchersPreloaderPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(shipped)!);
            Directory.CreateDirectory(Path.GetDirectoryName(placed)!);
            File.WriteAllText(shipped, "1.0.0.0");
            File.WriteAllText(placed, "1.0.0.0");
            File.WriteAllText(placed + LayoutAtStart.AsideSuffix, "0.9.0.0");
            Assert.Null(LayoutAtStart.PlacePreloader(root, shipped, out _));
            Assert.False(File.Exists(placed + LayoutAtStart.AsideSuffix));
        }
        finally
        {
            LayoutAtStart.ReadVersion = seam;
            Cleanup(root);
        }
    }

    // ---- legacy Bootstrap cleanup -----------------------------------------------------------------------------------

    [Fact]
    public void Legacy_Bootstrap_decisions()
    {
        string legacy = InstallLayout.LegacyBootstrapPath(Root);
        string running = Path.Combine(HomeDir, InstallLayout.BootstrapFileName);
        Assert.Equal(InstallLayout.LegacyBootstrapAction.None, InstallLayout.DecideLegacyBootstrap(Root, running, false, false));
        Assert.Equal(InstallLayout.LegacyBootstrapAction.Remove, InstallLayout.DecideLegacyBootstrap(Root, running, true, false));
        // The running image is never removed (a Bootstrap still deployed at the plugins root).
        Assert.Equal(InstallLayout.LegacyBootstrapAction.None, InstallLayout.DecideLegacyBootstrap(Root, legacy, true, false));
        Assert.Equal(InstallLayout.LegacyBootstrapAction.None, InstallLayout.DecideLegacyBootstrap(Root, "", true, false));
        // A hosted child sees the client's plugins through a junction.
        Assert.Equal(InstallLayout.LegacyBootstrapAction.LeaveHosted, InstallLayout.DecideLegacyBootstrap(Root, running, true, true));
    }

    [Fact]
    public void Legacy_Bootstrap_and_its_staged_copies_are_removed_from_disk()
    {
        string root = TempRoot();
        try
        {
            string legacy = InstallLayout.LegacyBootstrapPath(root);
            string running = Path.Combine(InstallLayout.HomeFolder(root), InstallLayout.BootstrapFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(running)!);
            File.WriteAllText(running, "new");
            File.WriteAllText(legacy, "old");
            File.WriteAllText(legacy + ".next", "old-next");
            string? line = LayoutAtStart.RemoveLegacyBootstrap(root, running, false, out bool ok);
            Assert.True(ok);
            Assert.Contains("removed the old", line);
            Assert.False(File.Exists(legacy));
            Assert.False(File.Exists(legacy + ".next"));
            Assert.True(File.Exists(running));
            Assert.Null(LayoutAtStart.RemoveLegacyBootstrap(root, running, false, out _));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void A_hosted_child_changes_nothing_outside_its_folder()
    {
        string root = TempRoot();
        try
        {
            string legacy = InstallLayout.LegacyBootstrapPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
            File.WriteAllText(legacy, "client's");
            string shipped = Path.Combine(InstallLayout.HomeFolder(root), InstallLayout.ShippedPreloaderFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(shipped)!);
            File.WriteAllText(shipped, "x");
            var lines = new List<string>();
            LayoutAtStart.Run(root, Path.Combine(InstallLayout.HomeFolder(root), InstallLayout.BootstrapFileName), true, lines.Add, lines.Add);
            Assert.Empty(lines);
            Assert.True(File.Exists(legacy));
            Assert.False(File.Exists(InstallLayout.PatchersPreloaderPath(root)));
        }
        finally { Cleanup(root); }
    }

    // ---- uninstall and the preloader's own check ----------------------------------------------------------------------

    [Theory]
    [InlineData("plugins/Progenitor.Bootstrap.dll", true)]
    [InlineData("plugins/Progenitor/Progenitor.Bootstrap.dll", true)]
    [InlineData("plugins\\Progenitor\\Progenitor.Bootstrap.dll", true)]
    [InlineData("plugins/Progenitor/progenitor-core/Progenitor.core", false)]
    [InlineData("patchers/Progenitor.Bootstrap.dll", false)]
    public void Uninstall_schedules_every_Bootstrap_under_plugins(string relative, bool expected)
    {
        Assert.Equal(expected, UninstallAtStart.IsBootstrapDll(relative));
    }

    [Fact]
    public void Uninstall_plan_names_the_home_folder_and_the_placed_preloader()
    {
        string root = TempRoot();
        try
        {
            string home = InstallLayout.HomeFolder(root);
            Directory.CreateDirectory(Path.Combine(home, "progenitor-core", "addresslib"));
            File.WriteAllText(Path.Combine(home, InstallLayout.BootstrapFileName), "b");
            File.WriteAllText(Path.Combine(home, InstallLayout.ShippedPreloaderFileName), "p");
            File.WriteAllText(Path.Combine(home, "progenitor-core", InstallLayout.CoreFileName), "c");
            File.WriteAllText(Path.Combine(home, "progenitor-core", "addresslib", "client-1.json"), "{}");
            Directory.CreateDirectory(Path.Combine(root, "patchers"));
            File.WriteAllText(InstallLayout.PatchersPreloaderPath(root), "p");
            var plan = Progenitor.Install.ClientUninstall.Plan(root);
            Assert.Contains("plugins/Progenitor/Progenitor.Bootstrap.dll", plan);
            Assert.Contains("plugins/Progenitor/Progenitor.Preloader.patcher", plan);
            Assert.Contains("plugins/Progenitor/progenitor-core/Progenitor.core", plan);
            Assert.Contains("plugins/Progenitor/progenitor-core/addresslib/client-1.json", plan);
            Assert.Contains("patchers/Progenitor.Preloader.dll", plan);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Uninstall_never_names_another_mods_file_in_a_shared_Bootstrap_folder()
    {
        // PR #48 review N1: Bootstrap placed in plugins/MyMods beside someone else's mod.
        string root = TempRoot();
        try
        {
            string shared = Path.Combine(root, "plugins", "MyMods");
            Directory.CreateDirectory(Path.Combine(shared, "progenitor-core", "addresslib"));
            Directory.CreateDirectory(Path.Combine(shared, "OtherMod"));
            File.WriteAllText(Path.Combine(shared, InstallLayout.BootstrapFileName), "b");
            File.WriteAllText(Path.Combine(shared, InstallLayout.BootstrapFileName + ".next"), "b2");
            File.WriteAllText(Path.Combine(shared, InstallLayout.ShippedPreloaderFileName), "p");
            File.WriteAllText(Path.Combine(shared, "progenitor-core", InstallLayout.CoreFileName), "c");
            File.WriteAllText(Path.Combine(shared, "progenitor-core", "addresslib", "client-1.json"), "{}");
            File.WriteAllText(Path.Combine(shared, "OtherMod.dll"), "x");
            File.WriteAllText(Path.Combine(shared, "OtherMod", "OtherMod.cfg"), "y");
            Assert.False(Progenitor.Install.ClientUninstall.LooksLikeProgenitorHome(root, "plugins/MyMods"));

            InstallLayout.SetCurrent(root,
                new InstallLayout.CoreHome(Path.Combine(shared, "progenitor-core"), Path.Combine(shared, "progenitor-core", "Progenitor.core"), true, false, Array.Empty<string>(), null),
                Path.Combine(shared, InstallLayout.BootstrapFileName));
            try
            {
                var plan = Progenitor.Install.ClientUninstall.Plan(root);
                Assert.Contains("plugins/MyMods/Progenitor.Bootstrap.dll", plan);
                Assert.Contains("plugins/MyMods/Progenitor.Bootstrap.dll.next", plan);
                Assert.Contains("plugins/MyMods/Progenitor.Preloader.patcher", plan);
                Assert.Contains("plugins/MyMods/progenitor-core/Progenitor.core", plan);
                Assert.Contains("plugins/MyMods/progenitor-core/addresslib/client-1.json", plan);
                Assert.DoesNotContain("plugins/MyMods/OtherMod.dll", plan);
                Assert.DoesNotContain("plugins/MyMods/OtherMod/OtherMod.cfg", plan);
            }
            finally { InstallLayout.SetCurrent(root, null); }

            // The same folder without the other mod is a home and goes whole.
            File.Delete(Path.Combine(shared, "OtherMod.dll"));
            Assert.True(Progenitor.Install.ClientUninstall.LooksLikeProgenitorHome(root, "plugins/MyMods"));
            // A folder with no Core is never a home.
            Assert.False(Progenitor.Install.ClientUninstall.LooksLikeProgenitorHome(root, "plugins/MyMods/OtherMod"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Preloader_retires_itself_only_when_no_Bootstrap_is_left()
    {
        string root = TempRoot();
        try
        {
            string placed = InstallLayout.PatchersPreloaderPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(placed)!);
            File.WriteAllText(placed, "p");
            Directory.CreateDirectory(Path.Combine(root, "plugins", "Some-Package", "Progenitor"));
            string boot = Path.Combine(root, "plugins", "Some-Package", "Progenitor", InstallLayout.BootstrapFileName);
            File.WriteAllText(boot, "b");
            Assert.True(ProgenitorPresence.BootstrapInstalled(root));

            File.Delete(boot);
            Assert.False(ProgenitorPresence.BootstrapInstalled(root));

            // A copy somewhere else (a package manager's own patchers folder) is never renamed.
            string elsewhere = Path.Combine(root, "patchers", "Some-Package", InstallLayout.PreloaderFileName);
            Assert.False(ProgenitorPresence.IsPlacedCopy(root, elsewhere));
            Assert.Contains("nothing was done", ProgenitorPresence.Retire(root, elsewhere));

            string line = ProgenitorPresence.Retire(root, placed);
            Assert.Contains("renamed", line);
            Assert.False(File.Exists(placed));
            Assert.True(File.Exists(placed + ProgenitorPresence.RetiredSuffix));
        }
        finally { Cleanup(root); }
    }

    // ---- allowlist rows for the renamed image -------------------------------------------------------------------------

    [Fact]
    public void An_allowlist_row_can_name_the_single_folder_image()
    {
        string root = TempRoot();
        try
        {
            string core = Path.Combine(root, InstallLayout.CoreFileName);
            File.WriteAllBytes(core, new byte[] { 1, 2, 3 });
            string hex = Progenitor.Bootstrap.ReloadAllowPolicy.Sha256Hex(new byte[] { 1, 2, 3 });
            Assert.True(Progenitor.Reload.ReloadAllowStore.TryCoreRowName(core, hex, out string name, out string error), error);
            Assert.Equal("Progenitor.core", name);

            // A staged copy lists under the live name only once the live file holds the same bytes.
            File.WriteAllBytes(core + ".next", new byte[] { 1, 2, 3 });
            Assert.True(Progenitor.Reload.ReloadAllowStore.TryCoreRowName(core + ".next", hex, out name, out error), error);
            Assert.Equal("Progenitor.core", name);
            Assert.False(Progenitor.Reload.ReloadAllowStore.TryCoreRowName(Path.Combine(root, "Other.core"), hex, out _, out _));
        }
        finally { Cleanup(root); }
    }

    // ---- update packages ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("bepinex/plugins/progenitor/progenitor.bootstrap.dll", true)]
    [InlineData("bepinex/plugins/progenitor.bootstrap.dll", true)]
    [InlineData("plugins/progenitor.bootstrap.dll", true)]
    [InlineData("bepinex/patchers/progenitor.bootstrap.dll", false)]
    public void Update_package_Bootstrap_entries(string entry, bool expected)
    {
        Assert.Equal(expected, Progenitor.Install.SelfImageReader.IsBootstrapEntry(entry));
    }

    [Theory]
    [InlineData("bepinex/plugins/progenitor/progenitor-core/progenitor.core", InstallLayout.CoreFileName, true)]
    [InlineData("bepinex/progenitor-core/progenitor.dll", InstallLayout.LegacyCoreFileName, true)]
    [InlineData("bepinex/progenitor-core/progenitor.dll", InstallLayout.CoreFileName, false)]
    [InlineData("bepinex/plugins/progenitor/progenitor-core/addresslib/progenitor.core", InstallLayout.CoreFileName, false)]
    public void Update_package_Core_entries(string entry, string name, bool expected)
    {
        Assert.Equal(expected, Progenitor.Install.SelfImageReader.IsCoreEntry(entry, name));
    }

    private static void Cleanup(string bepInExRoot)
    {
        try { Directory.Delete(Path.GetDirectoryName(bepInExRoot)!, true); }
        catch { /* a temp folder */ }
    }

    private static string TempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-layout-" + Guid.NewGuid().ToString("N"), "BepInEx");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
