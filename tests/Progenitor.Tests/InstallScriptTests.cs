using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace Progenitor.Tests;

/// <summary>
/// The player install and uninstall scripts served from GitHub Pages (docs/install.ps1, docs/uninstall.ps1). The text
/// checks hold everywhere; the runs need Windows PowerShell or PowerShell 7 and return early without one. Every run
/// targets a temporary fake game folder with -GamePath or a fake Steam folder with -SteamPath, and installs local
/// fixture zips (-OfflinePackage, -OfflineBepInEx), so nothing reaches the network or a real game install.
/// </summary>
public sealed class InstallScriptTests
{
    private static ITestOutputHelper? _log;

    public InstallScriptTests(ITestOutputHelper log) => _log = log;

    private const string PackageVersion = "2026.10.200";
    private const string NewerVersion = "2026.10.201";

    // ---- the text --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("install.ps1")]
    [InlineData("uninstall.ps1")]
    public void Scripts_use_only_the_project_site_github_releases_and_thunderstore(string name)
    {
        string text = Script(name);
        foreach (string forbidden in new[] { "api.github.com", "raw.githubusercontent", "githubusercontent.com/", "/repos/", "gist.github" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        var hosts = Regex.Matches(text, @"https?://([A-Za-z0-9.-]+)").Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();
        Assert.NotEmpty(hosts);
        foreach (string host in hosts)
            Assert.Contains(host, new[] { "vrising-modders.github.io", "github.com", "thunderstore.io" });
        foreach (Match m in Regex.Matches(text, @"https?://github\.com/[^\s'""]*"))
            Assert.StartsWith("https://github.com/VRising-Modders/Progenitor/releases/", m.Value, StringComparison.Ordinal);
        // No downloads over plain http.
        Assert.DoesNotMatch(new Regex(@"http://", RegexOptions.IgnoreCase), text);
    }

    [Theory]
    [InlineData("install.ps1")]
    [InlineData("uninstall.ps1")]
    public void Scripts_are_plain_ascii_without_a_bom_so_irm_and_iex_read_them_in_every_powershell(string name)
    {
        byte[] bytes = File.ReadAllBytes(ScriptPath(name));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, name + " starts with a BOM");
        int bad = Array.FindIndex(bytes, b => b > 0x7F);
        Assert.True(bad < 0, name + " has a non-ASCII byte at offset " + bad);
        string text = Encoding.ASCII.GetString(bytes);
        // PowerShell 7-only syntax would break Windows PowerShell 5.1, the default on Windows.
        Assert.DoesNotMatch(new Regex(@"\?\?|\?\.|\s&&\s|\s\|\|\s|-AsHashtable|\bclean\s*\{"), text);
    }

    /// <summary>Thunderstore shows no copy button: every installer command is a line of its own in a powershell block.</summary>
    [Theory]
    [InlineData("README.md")]
    [InlineData("docs/publishing.md")]
    [InlineData("templates/progenitor-mod/README.md")]
    public void Every_installer_command_in_the_markdown_docs_sits_alone_in_a_powershell_block(string file)
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepoRoot(), file));
        string? fence = null;
        int commands = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            var open = Regex.Match(t, @"^(`{3,})(\w*)$");
            if (open.Success)
            {
                if (fence == null) fence = open.Groups[2].Value;
                else if (open.Groups[2].Value.Length == 0) fence = null;
                else fence = open.Groups[2].Value; // a block inside a ````markdown example
                continue;
            }

            if (!t.Contains("irm https://", StringComparison.Ordinal)) continue;
            commands++;
            Assert.True(fence == "powershell", file + ":" + (i + 1) + " has an installer command outside a powershell block");
            Assert.Matches(@"^(irm |& \(\[scriptblock\]::Create\(\(irm )", t);
            Assert.Matches(@"^```", lines[i - 1].Trim());
            Assert.Equal("```", lines[i + 1].Trim());
        }

        Assert.True(commands >= 2, file + " shows no installer command");
    }

    [Fact]
    public void Both_scripts_carry_the_same_shared_region()
    {
        string a = SharedRegion(Script("install.ps1"));
        string b = SharedRegion(Script("uninstall.ps1"));
        Assert.Equal(a, b);
        Assert.Contains("function Resolve-PgTarget", a, StringComparison.Ordinal);
        Assert.Contains("function Get-PgRunning", a, StringComparison.Ordinal);
    }

    [Fact]
    public void The_uninstaller_names_the_same_progenitor_files_as_the_mods_menu_removal()
    {
        string un = Script("uninstall.ps1");
        string menu = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Progenitor.Core", "Install", "ClientUninstall.cs"));
        foreach (string name in new[] { "Progenitor.Dev.dll", "Progenitor.Sample.dll", "Progenitor.ReloadExample.dll", "Progenitor.Preloader.dll", "progenitor-files.zip" })
        {
            Assert.Contains(name, menu, StringComparison.Ordinal);
            Assert.Contains("'" + name + "'", un, StringComparison.Ordinal);
        }
        foreach (Match m in Regex.Matches(menu, "\"(progenitor-[a-z]+)\""))
            Assert.StartsWith("progenitor", m.Groups[1].Value, StringComparison.Ordinal); // every owned folder is matched by progenitor*
    }

    // ---- runs ------------------------------------------------------------------------------------------------

    [Fact]
    public void Scripts_parse_in_every_available_powershell()
    {
        foreach (string shell in Shells())
            foreach (string name in new[] { "install.ps1", "uninstall.ps1" })
            {
                var r = RunCommand(shell, "$e = $null; $t = $null; [System.Management.Automation.Language.Parser]::ParseFile('" + ScriptPath(name) + "', [ref]$t, [ref]$e) | Out-Null; 'errors=' + $e.Count; $e | ForEach-Object { $_.ToString() }");
                Assert.Contains("errors=0", r.Output, StringComparison.Ordinal);
            }
    }

    [Fact]
    public void Install_dry_run_install_update_and_rollback_in_a_fake_client_folder()
    {
        foreach (string shell in Shells())
        {
            using var fx = new Fixture();
            string game = fx.Game("VRising.exe");

            // Dry run: the plan, nothing written.
            var dry = Run(shell, "install.ps1", null, "-GamePath", game, "-WhatIf", "-OfflinePackage", fx.Package(PackageVersion), "-OfflineBepInEx", fx.BepInExPack());
            Assert.True(dry.Exit == 0, dry.Output);
            Assert.Contains("would install BepInExPack_V_Rising", dry.Output, StringComparison.Ordinal);
            Assert.Contains("would install " + PackageVersion, dry.Output, StringComparison.Ordinal);
            Assert.Contains("sha256 matches SHA256SUMS", dry.Output, StringComparison.Ordinal);
            Assert.Contains("Nothing was changed", dry.Output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(game, "BepInEx")), "a dry run wrote BepInEx");
            Assert.Single(Directory.GetFileSystemEntries(game));

            // Fresh install: BepInEx from the pack, Progenitor from the zip, the self-install record.
            var first = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", fx.Package(PackageVersion), "-OfflineBepInEx", fx.BepInExPack());
            Assert.True(first.Exit == 0, first.Output);
            Assert.Contains("Progenitor is installed.", first.Output, StringComparison.Ordinal);
            Assert.Contains("Next: start V Rising, then open the Mods menu", first.Output, StringComparison.Ordinal);
            Assert.Contains("installed BepInExPack_V_Rising", first.Output, StringComparison.Ordinal);
            foreach (string f in new[] { "winhttp.dll", "doorstop_config.ini", @"BepInEx\core\BepInEx.Core.dll", @"BepInEx\config\BepInEx.cfg", @"dotnet\coreclr.dll",
                         @"BepInEx\plugins\Progenitor.Bootstrap.dll", @"BepInEx\patchers\Progenitor.Preloader.dll", @"BepInEx\progenitor-core\Progenitor.dll",
                         @"BepInEx\progenitor-core\addresslib\client-1.json", @"BepInEx\progenitor-self-install.json" })
                Assert.True(File.Exists(Path.Combine(game, f)), f + " missing\n" + first.Output);
            Assert.False(File.Exists(Path.Combine(game, "manifest.json")), "the pack's listing files were extracted");
            Assert.False(File.Exists(Path.Combine(game, "icon.png")), "the pack's listing files were extracted");
            Assert.False(Directory.Exists(Path.Combine(game, "BepInExPack_V_Rising")));
            using (var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(game, "BepInEx", "progenitor-self-install.json"))))
            {
                var r = record.RootElement;
                Assert.Equal(1, r.GetProperty("schema").GetInt32());
                Assert.Equal("VRising_Modders-Progenitor", r.GetProperty("package").GetString());
                Assert.Equal(PackageVersion, r.GetProperty("version").GetString());
                Assert.Equal(Sha(Path.Combine(game, "BepInEx", "progenitor-core", "Progenitor.dll")), r.GetProperty("coreSha256").GetString());
                Assert.Equal(Sha(Path.Combine(game, "BepInEx", "plugins", "Progenitor.Bootstrap.dll")), r.GetProperty("bootstrapSha256").GetString());
            }

            // The player's settings, another mod, a staged Core and a table the new release no longer has.
            File.WriteAllText(Path.Combine(game, "BepInEx", "config", "BepInEx.cfg"), "player's own settings");
            File.WriteAllText(Path.Combine(game, "BepInEx", "plugins", "Other.dll"), "another mod");
            File.WriteAllText(Path.Combine(game, "BepInEx", "progenitor-core", "Progenitor.dll.next"), "staged");
            File.WriteAllText(Path.Combine(game, "BepInEx", "progenitor-core", "compat.json"), "runtime state");
            string oldBootstrap = Sha(Path.Combine(game, "BepInEx", "plugins", "Progenitor.Bootstrap.dll"));

            // Update: BepInEx kept, old files backed up, the stale table and the staged Core gone from the live folder.
            var update = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", fx.Package(NewerVersion, table: "client-2.json"));
            Assert.True(update.Exit == 0, update.Output);
            Assert.Contains("kept (already installed)", update.Output, StringComparison.Ordinal);
            Assert.Contains(NewerVersion + " (was " + PackageVersion + ")", update.Output, StringComparison.Ordinal);
            string backup = Assert.Single(Directory.GetDirectories(Path.Combine(game, "BepInEx", "progenitor-backups")));
            Assert.Contains(backup, update.Output, StringComparison.Ordinal);
            Assert.StartsWith("install-", Path.GetFileName(backup), StringComparison.Ordinal);
            Assert.Equal(oldBootstrap, Sha(Path.Combine(backup, "BepInEx", "plugins", "Progenitor.Bootstrap.dll")));
            Assert.True(File.Exists(Path.Combine(backup, "BepInEx", "progenitor-core", "addresslib", "client-1.json")));
            Assert.True(File.Exists(Path.Combine(backup, "BepInEx", "progenitor-core", "Progenitor.dll.next")));
            Assert.False(File.Exists(Path.Combine(game, "BepInEx", "progenitor-core", "addresslib", "client-1.json")));
            Assert.True(File.Exists(Path.Combine(game, "BepInEx", "progenitor-core", "addresslib", "client-2.json")));
            Assert.False(File.Exists(Path.Combine(game, "BepInEx", "progenitor-core", "Progenitor.dll.next")));
            Assert.Equal("runtime state", File.ReadAllText(Path.Combine(game, "BepInEx", "progenitor-core", "compat.json")));
            Assert.Equal("player's own settings", File.ReadAllText(Path.Combine(game, "BepInEx", "config", "BepInEx.cfg")));
            Assert.Equal("another mod", File.ReadAllText(Path.Combine(game, "BepInEx", "plugins", "Other.dll")));
            using (var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(game, "BepInEx", "progenitor-self-install.json"))))
                Assert.Equal(NewerVersion, record.RootElement.GetProperty("version").GetString());

            // A failure part way through the next update puts every file back.
            var before = Snapshot(game, "BepInEx\\progenitor-backups");
            var failed = Run(shell, "install.ps1", new() { ["PROGENITOR_INSTALL_FAIL_AFTER"] = "3" }, "-GamePath", game, "-OfflinePackage", fx.Package("2026.10.202", table: "client-3.json"));
            Assert.True(failed.Exit == 1, failed.Output);
            Assert.Contains("Install failed", failed.Output, StringComparison.Ordinal);
            Assert.Contains("Every change was undone", failed.Output, StringComparison.Ordinal);
            AssertSame(before, Snapshot(game, "BepInEx\\progenitor-backups"));
        }
    }

    [Fact]
    public void Install_checks_the_download_before_writing_anything()
    {
        string shell = Shells().FirstOrDefault()!;
        if (shell == null) return;
        using var fx = new Fixture();
        string game = fx.Game("VRising.exe");
        Directory.CreateDirectory(Path.Combine(game, "save-data"));
        File.WriteAllText(Path.Combine(game, "save-data", "world.save"), "a world");
        var before = Snapshot(game, null);

        // A zip whose SHA256SUMS row differs.
        string pkg = fx.Package(PackageVersion);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(pkg)!, "SHA256SUMS"), new string('0', 64) + "  " + Path.GetFileName(pkg) + "\n");
        var r = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", pkg, "-OfflineBepInEx", fx.BepInExPack());
        Assert.True(r.Exit == 1, r.Output);
        Assert.Contains("sha256 differs", r.Output, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", r.Output, StringComparison.Ordinal);
        AssertSame(before, Snapshot(game, null));

        // A release list that names another hash for the same version.
        string feed = Path.Combine(fx.Root, "releases.json");
        File.WriteAllText(feed, "{\"schema\":1,\"package\":\"VRising_Modders-Progenitor\",\"releases\":[{\"version\":\"" + PackageVersion
            + "\",\"url\":\"https://github.com/VRising-Modders/Progenitor/releases/download/v" + PackageVersion + "/Progenitor-" + PackageVersion + ".zip\",\"size\":1,\"sha256\":\"" + new string('a', 64) + "\"}]}");
        r = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", fx.Package(PackageVersion), "-OfflineBepInEx", fx.BepInExPack(), "-FeedUrl", feed);
        Assert.True(r.Exit == 1, r.Output);
        Assert.Contains("is not the file the release list names", r.Output, StringComparison.Ordinal);
        AssertSame(before, Snapshot(game, null));

        // The same list with the right hash installs.
        string good = fx.Package(PackageVersion);
        File.WriteAllText(feed, File.ReadAllText(feed).Replace(new string('a', 64), Sha(good)));
        r = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", good, "-OfflineBepInEx", fx.BepInExPack(), "-FeedUrl", feed);
        Assert.True(r.Exit == 0, r.Output);
        Assert.Contains("sha256 matches the release list", r.Output, StringComparison.Ordinal);

        // A zip that is not Progenitor's, and one with the development bridge, are refused before writing.
        var mid = Snapshot(game, null);
        r = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", fx.Package(NewerVersion, dev: true));
        Assert.True(r.Exit == 1, r.Output);
        Assert.Contains("development bridge", r.Output, StringComparison.Ordinal);
        r = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", fx.Package(NewerVersion, extra: "BepInEx/core/BepInEx.Core.dll"));
        Assert.True(r.Exit == 1, r.Output);
        Assert.Contains("outside Progenitor's folders", r.Output, StringComparison.Ordinal);
        AssertSame(mid, Snapshot(game, null));
        Assert.Equal("a world", File.ReadAllText(Path.Combine(game, "save-data", "world.save")));
    }

    [Fact]
    public void Install_versions_the_record_from_core_checks_core_against_the_list_keeps_three_backups_and_refuses_held_files()
    {
        string shell = Shells().FirstOrDefault()!;
        if (shell == null) return;
        using var fx = new Fixture();
        string game = fx.Game("VRising.exe");
        string bep = Path.Combine(game, "BepInEx");
        string record = Path.Combine(bep, "progenitor-self-install.json");

        // A BepInEx zip that is not the reviewed pack installs because it was named, and says so.
        var first = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", fx.Package(PackageVersion), "-OfflineBepInEx", fx.BepInExPack());
        Assert.True(first.Exit == 0, first.Output);
        Assert.Contains("not the reviewed pack 1.733.2", first.Output, StringComparison.Ordinal);

        // The record names the release that SHA256SUMS checked, even when Core's own (component) version is older,
        // as in a real release whose Core last changed one release earlier.
        var named = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", fx.Package("2026.10.300", coreVersion: "2026.10.299"));
        Assert.True(named.Exit == 0, named.Output);
        using (var doc = JsonDocument.Parse(File.ReadAllText(record)))
            Assert.Equal("2026.10.300", doc.RootElement.GetProperty("version").GetString());

        // Nothing checks the zip's name: the record takes Core's version instead.
        string unchecked_ = fx.Package("2026.10.303", coreVersion: "2026.10.298");
        File.Delete(Path.Combine(Path.GetDirectoryName(unchecked_)!, "SHA256SUMS"));
        var byCore = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", unchecked_);
        Assert.True(byCore.Exit == 0, byCore.Output);
        using (var doc = JsonDocument.Parse(File.ReadAllText(record)))
            Assert.Equal("2026.10.298", doc.RootElement.GetProperty("version").GetString());

        // Neither: no record.
        string bareZip = fx.Package("2026.10.301", coreVersion: null);
        File.Delete(Path.Combine(Path.GetDirectoryName(bareZip)!, "SHA256SUMS"));
        var bare = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", bareZip);
        Assert.True(bare.Exit == 0, bare.Output);
        Assert.Contains("no self-install record was written", bare.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(record));

        // A release list whose coreSha256 names another Core: refused after writing, and every change is undone.
        string pkg = fx.Package("2026.10.302");
        string feed = Path.Combine(fx.Root, "releases.json");
        File.WriteAllText(feed, "{\"schema\":1,\"releases\":[{\"version\":\"2026.10.302\",\"url\":\"https://github.com/VRising-Modders/Progenitor/releases/download/v2026.10.302/Progenitor-2026.10.302.zip\",\"size\":1,\"sha256\":\""
            + Sha(pkg) + "\",\"coreSha256\":\"" + new string('b', 64) + "\"}]}");
        var before = Snapshot(game, "BepInEx\\progenitor-backups");
        var wrongCore = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", pkg, "-FeedUrl", feed);
        Assert.True(wrongCore.Exit == 1, wrongCore.Output);
        Assert.Contains("not the one the release list names", wrongCore.Output, StringComparison.Ordinal);
        Assert.Contains("Every change was undone", wrongCore.Output, StringComparison.Ordinal);
        AssertSame(before, Snapshot(game, "BepInEx\\progenitor-backups"));

        // Only the last three install backups are kept.
        for (int i = 0; i < 3; i++)
        {
            var again = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", fx.Package("2026.10.31" + i));
            Assert.True(again.Exit == 0, again.Output);
        }
        Assert.Equal(3, Directory.GetDirectories(Path.Combine(bep, "progenitor-backups"), "install-*").Length);

        // A held Bootstrap means the game is running from here, whatever the process list says.
        using (File.Open(Path.Combine(bep, "plugins", "Progenitor.Bootstrap.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var held = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", fx.Package("2026.10.320"));
            Assert.True(held.Exit == 1, held.Output);
            Assert.Contains("is in use", held.Output, StringComparison.Ordinal);
            var heldUn = Run(shell, "uninstall.ps1", null, "-GamePath", game);
            Assert.True(heldUn.Exit == 1, heldUn.Output);
            Assert.Contains("is in use", heldUn.Output, StringComparison.Ordinal);
        }

        // A relative -GamePath is taken from PowerShell's current location.
        var relative = RunCommand(shell, "Set-Location -LiteralPath '" + fx.Root + "'\n& ([scriptblock]::Create([System.IO.File]::ReadAllText('"
            + ScriptPath("install.ps1") + "'))) -GamePath '.\\Game Folder' -WhatIf -OfflinePackage '" + fx.Package("2026.10.321") + "'");
        Assert.True(relative.Exit == 0, relative.Output);
        Assert.Contains("Target: " + game + " (VRising.exe)", relative.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_clears_a_waiting_removal_request_and_keeps_the_record_the_zip_carries()
    {
        string shell = Shells().FirstOrDefault()!;
        if (shell == null) return;
        using var fx = new Fixture();
        string game = fx.Game("VRising.exe");
        string bep = Path.Combine(game, "BepInEx");
        var first = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", fx.Package(PackageVersion), "-OfflineBepInEx", fx.BepInExPack());
        Assert.True(first.Exit == 0, first.Output);

        // "Remove Progenitor" was requested in the Mods menu: left in place, the next start would remove the new install.
        File.WriteAllText(Path.Combine(bep, "progenitor-uninstall.json"), "{\"schema\":1,\"files\":[\"plugins/Progenitor.Bootstrap.dll\"]}");
        File.WriteAllText(Path.Combine(bep, "progenitor-uninstall.done"), "done");
        var dry = Run(shell, "install.ps1", null, "-GamePath", game, "-WhatIf", "-OfflinePackage", fx.Package(NewerVersion));
        Assert.True(dry.Exit == 0, dry.Output);
        Assert.Contains("BepInEx/progenitor-uninstall.json", dry.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(bep, "progenitor-uninstall.json")));

        var r = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", fx.Package(NewerVersion, record: true));
        Assert.True(r.Exit == 0, r.Output);
        Assert.Contains("Cleared:     the Mods menu removal request (progenitor-uninstall.json)", r.Output, StringComparison.Ordinal);
        Assert.Contains("Cleared:     the Mods menu removal request (progenitor-uninstall.done)", r.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(bep, "progenitor-uninstall.json")));
        Assert.False(File.Exists(Path.Combine(bep, "progenitor-uninstall.done")));
        string backup = Assert.Single(Directory.GetDirectories(Path.Combine(bep, "progenitor-backups"), "install-*"));
        Assert.True(File.Exists(Path.Combine(backup, "BepInEx", "progenitor-uninstall.json")));
        Assert.True(File.Exists(Path.Combine(backup, "BepInEx", "progenitor-uninstall.done")));

        // The zip's own record is installed as it is.
        Assert.Equal(Fixture.ZipRecord(NewerVersion), File.ReadAllText(Path.Combine(bep, "progenitor-self-install.json")));
    }

    [Fact]
    public void Install_refuses_while_a_process_runs_from_the_target_folder()
    {
        string shell = Shells().FirstOrDefault()!;
        if (shell == null || !OperatingSystem.IsWindows()) return;
        using var fx = new Fixture();
        string game = fx.Game("VRising.exe");
        // A copy of cmd.exe named VRising.exe stands in for the game: the match is by full path.
        string exe = Path.Combine(game, "VRising.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe, overwrite: true);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("ping -n 60 127.0.0.1 >nul");
        using var game_ = Process.Start(psi)!;
        try
        {
            var before = Snapshot(game, null);
            var r = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", fx.Package(PackageVersion), "-OfflineBepInEx", fx.BepInExPack());
            Assert.True(r.Exit == 1, r.Output);
            Assert.Contains("is running from", r.Output, StringComparison.Ordinal);
            Assert.Contains("Quit the game", r.Output, StringComparison.Ordinal);
            AssertSame(before, Snapshot(game, null));
            var u = Run(shell, "uninstall.ps1", null, "-GamePath", game);
            Assert.True(u.Exit == 1, u.Output);
            Assert.Contains("is running from", u.Output, StringComparison.Ordinal);
        }
        finally
        {
            try { game_.Kill(entireProcessTree: true); game_.WaitForExit(10_000); } catch { /* already gone */ }
        }
    }

    [Fact]
    public void Install_finds_the_client_and_the_dedicated_server_through_steam_libraries()
    {
        string shell = Shells().FirstOrDefault()!;
        if (shell == null) return;
        using var fx = new Fixture();
        string steam = Path.Combine(fx.Root, "Steam");
        string lib = Path.Combine(fx.Root, "Library Two");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(lib, "steamapps", "common", "VRising"));
        Directory.CreateDirectory(Path.Combine(lib, "steamapps", "common", "VRisingDedicatedServer"));
        File.WriteAllText(Path.Combine(lib, "steamapps", "common", "VRising", "VRising.exe"), "");
        File.WriteAllText(Path.Combine(lib, "steamapps", "common", "VRisingDedicatedServer", "VRisingServer.exe"), "");
        File.WriteAllText(Path.Combine(lib, "steamapps", "appmanifest_1604030.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"1604030\"\n\t\"installdir\"\t\t\"VRising\"\n}\n");
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + steam.Replace("\\", "\\\\") + "\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + lib.Replace("\\", "\\\\") + "\"\n\t}\n}\n");

        var client = Run(shell, "install.ps1", null, "-SteamPath", steam, "-WhatIf", "-OfflinePackage", fx.Package(PackageVersion), "-OfflineBepInEx", fx.BepInExPack());
        Assert.True(client.Exit == 0, client.Output);
        Assert.Contains("Target: " + Path.Combine(lib, "steamapps", "common", "VRising") + " (VRising.exe)", client.Output, StringComparison.Ordinal);

        var server = Run(shell, "install.ps1", null, "-SteamPath", steam, "-Server", "-OfflinePackage", fx.Package(PackageVersion), "-OfflineBepInEx", fx.BepInExPack());
        Assert.True(server.Exit == 0, server.Output);
        Assert.Contains("(VRisingServer.exe)", server.Output, StringComparison.Ordinal);
        Assert.Contains("Next: start the server.", server.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(lib, "steamapps", "common", "VRisingDedicatedServer", "BepInEx", "progenitor-core", "Progenitor.dll")));

        string empty = Path.Combine(fx.Root, "Empty Steam");
        Directory.CreateDirectory(empty);
        var missing = Run(shell, "install.ps1", null, "-SteamPath", empty, "-WhatIf", "-OfflinePackage", fx.Package(PackageVersion));
        Assert.True(missing.Exit == 1, missing.Output);
        Assert.Contains("-GamePath", missing.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_reach_the_script_through_irm_iex_environment_variables_and_the_scriptblock_form()
    {
        foreach (string shell in Shells())
        {
            using var fx = new Fixture();
            string game = fx.Game("VRising.exe");
            string install = ScriptPath("install.ps1");
            string pkg = fx.Package(PackageVersion);
            string bep = fx.BepInExPack();

            // irm | iex: the downloaded text piped to iex, options from environment variables.
            var iex = RunCommand(shell,
                "$env:PROGENITOR_GAME_PATH = '" + game + "'; $env:PROGENITOR_WHATIF = '1'; $env:PROGENITOR_OFFLINE_PACKAGE = '" + pkg + "'; $env:PROGENITOR_OFFLINE_BEPINEX = '" + bep + "'\n"
                + "[System.IO.File]::ReadAllText('" + install + "') | iex\n'after-iex'");
            Assert.True(iex.Exit == 0, iex.Output);
            Assert.Contains("would install " + PackageVersion, iex.Output, StringComparison.Ordinal);
            Assert.Contains("after-iex", iex.Output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(game, "BepInEx")));

            // The real pipe: irm from a web server that labels the script as a download (application/octet-stream).
            using (var server = new ScriptServer(File.ReadAllBytes(install)))
            {
                var irm = RunCommand(shell,
                    "$env:PROGENITOR_GAME_PATH = '" + game + "'; $env:PROGENITOR_WHATIF = '1'; $env:PROGENITOR_OFFLINE_PACKAGE = '" + pkg + "'; $env:PROGENITOR_OFFLINE_BEPINEX = '" + bep + "'\n"
                    + "irm " + server.Url + " | iex\n'after-irm'");
                Assert.True(irm.Exit == 0, irm.Output);
                Assert.Contains("would install " + PackageVersion, irm.Output, StringComparison.Ordinal);
                Assert.Contains("after-irm", irm.Output, StringComparison.Ordinal);
            }

            // A failure under iex reports and leaves the window open.
            var bad = RunCommand(shell, "$env:PROGENITOR_GAME_PATH = '" + Path.Combine(fx.Root, "nowhere") + "'\n[System.IO.File]::ReadAllText('" + install + "') | iex\n'still-here ' + $LASTEXITCODE");
            Assert.Contains("There is no folder at", bad.Output, StringComparison.Ordinal);
            Assert.Contains("still-here 1", bad.Output, StringComparison.Ordinal);

            // The scriptblock form takes real parameters.
            var sb = RunCommand(shell,
                "& ([scriptblock]::Create([System.IO.File]::ReadAllText('" + install + "'))) -GamePath '" + game + "' -OfflinePackage '" + pkg + "' -OfflineBepInEx '" + bep + "'\n'after-scriptblock'");
            Assert.True(sb.Exit == 0, sb.Output);
            Assert.Contains("Progenitor is installed.", sb.Output, StringComparison.Ordinal);
            Assert.Contains("after-scriptblock", sb.Output, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(game, "BepInEx", "progenitor-core", "Progenitor.dll")));

            var unsb = RunCommand(shell,
                "& ([scriptblock]::Create([System.IO.File]::ReadAllText('" + ScriptPath("uninstall.ps1") + "'))) -GamePath '" + game + "' -WhatIf\n'after-uninstall'");
            Assert.True(unsb.Exit == 0, unsb.Output);
            Assert.Contains("Would move", unsb.Output, StringComparison.Ordinal);
            Assert.Contains("after-uninstall", unsb.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Uninstall_removes_progenitor_keeps_other_mods_bepinex_and_saves_and_rolls_back_on_failure()
    {
        foreach (string shell in Shells())
        {
            using var fx = new Fixture();
            string game = fx.Game("VRising.exe");
            var inst = Run(shell, "install.ps1", null, "-GamePath", game, "-OfflinePackage", fx.Package(PackageVersion), "-OfflineBepInEx", fx.BepInExPack());
            Assert.True(inst.Exit == 0, inst.Output);
            string bep = Path.Combine(game, "BepInEx");
            File.WriteAllText(Path.Combine(bep, "plugins", "Other.dll"), "another mod");
            Directory.CreateDirectory(Path.Combine(bep, "progenitor-reload"));
            File.WriteAllText(Path.Combine(bep, "progenitor-reload", "Installed.dll"), "a mod Progenitor installed");
            File.WriteAllText(Path.Combine(bep, "progenitor-reload", "HandCopied.dll"), "copied in by hand");
            File.WriteAllText(Path.Combine(bep, "progenitor-uninstall.json"), "{\"schema\":1,\"files\":[]}");
            File.WriteAllText(Path.Combine(bep, "plugins", "Ledgered.dll"), "ledgered mod");
            File.WriteAllText(Path.Combine(bep, "plugins", "Changed.dll"), "edited by hand");
            File.WriteAllText(Path.Combine(bep, "progenitor-install.json"), "{\"schema\":1,\"files\":["
                + "{\"path\":\"BepInEx/plugins/Ledgered.dll\",\"sha256\":\"" + ShaText("ledgered mod") + "\"},"
                + "{\"path\":\"BepInEx/plugins/Changed.dll\",\"sha256\":\"" + ShaText("what Progenitor wrote") + "\"},"
                // Rows inside folders that move as a whole: they must move with the folder, not nest inside its backup.
                + "{\"path\":\"BepInEx/progenitor-reload/Installed.dll\",\"sha256\":\"" + ShaText("a mod Progenitor installed") + "\"},"
                + "{\"path\":\"BepInEx/progenitor-core/Progenitor.dll\",\"sha256\":\"" + Sha(Path.Combine(bep, "progenitor-core", "Progenitor.dll")) + "\"},"
                + "{\"path\":\"../outside.txt\",\"sha256\":\"\"}]}");
            File.WriteAllText(Path.Combine(game, "..", "outside.txt"), "outside");
            File.WriteAllText(Path.Combine(bep, "config", "progenitor.cfg"), "[Mods]");
            File.WriteAllText(Path.Combine(bep, "config", "progenitor.ui.cfg"), "[Ui]");
            Directory.CreateDirectory(Path.Combine(bep, "progenitor-cache"));
            File.WriteAllText(Path.Combine(bep, "progenitor-cache", "index.json"), "{}");
            Directory.CreateDirectory(Path.Combine(bep, "plugins", "VRising_Modders-Progenitor"));
            File.WriteAllText(Path.Combine(bep, "plugins", "VRising_Modders-Progenitor", "Progenitor.Bootstrap.dll"), "mod-manager copy");
            Directory.CreateDirectory(Path.Combine(game, "save-data", "Saves"));
            File.WriteAllText(Path.Combine(game, "save-data", "Saves", "world.save"), "a world");

            // Dry run.
            var all = Snapshot(game, null);
            var dry = Run(shell, "uninstall.ps1", null, "-GamePath", game, "-WhatIf");
            Assert.True(dry.Exit == 0, dry.Output);
            Assert.Contains(@"BepInEx\progenitor-core", dry.Output, StringComparison.Ordinal);
            Assert.Contains(@"BepInEx\config\progenitor.cfg", dry.Output, StringComparison.Ordinal);
            AssertSame(all, Snapshot(game, null));

            // A failure part way puts everything back: early, and after the folders holding ledgered files have moved.
            foreach (string after in new[] { "4", "10" })
            {
                var failed = Run(shell, "uninstall.ps1", new() { ["PROGENITOR_INSTALL_FAIL_AFTER"] = after }, "-GamePath", game);
                Assert.True(failed.Exit == 1, failed.Output);
                Assert.Contains("Every change was undone", failed.Output, StringComparison.Ordinal);
                AssertSame(all, Snapshot(game, "BepInEx\\progenitor-backups"));
                Assert.Empty(Directory.Exists(Path.Combine(bep, "progenitor-backups")) ? Directory.GetFiles(Path.Combine(bep, "progenitor-backups"), "*", SearchOption.AllDirectories) : Array.Empty<string>());
            }

            // -KeepSettings.
            var un = Run(shell, "uninstall.ps1", null, "-GamePath", game, "-KeepSettings");
            Assert.True(un.Exit == 0, un.Output);
            Assert.Contains("Progenitor is removed.", un.Output, StringComparison.Ordinal);
            Assert.Contains("Progenitor " + PackageVersion, un.Output, StringComparison.Ordinal);
            foreach (string gone in new[] { @"plugins\Progenitor.Bootstrap.dll", @"patchers\Progenitor.Preloader.dll", "progenitor-core", "progenitor-reload",
                         "progenitor-cache", "progenitor-self-install.json", "progenitor-install.json", @"plugins\Ledgered.dll", @"plugins\VRising_Modders-Progenitor" })
                Assert.False(Path.Exists(Path.Combine(bep, gone)), gone + " is still there\n" + un.Output);
            foreach (string kept in new[] { @"plugins\Other.dll", @"plugins\Changed.dll", @"config\progenitor.cfg", @"config\progenitor.ui.cfg", @"config\BepInEx.cfg", @"core\BepInEx.Core.dll" })
                Assert.True(File.Exists(Path.Combine(bep, kept)), kept + " was removed\n" + un.Output);
            Assert.True(File.Exists(Path.Combine(game, "winhttp.dll")));
            Assert.Equal("a world", File.ReadAllText(Path.Combine(game, "save-data", "Saves", "world.save")));
            Assert.Equal("outside", File.ReadAllText(Path.Combine(game, "..", "outside.txt")));
            string backup = Assert.Single(Directory.GetDirectories(Path.Combine(bep, "progenitor-backups"), "uninstall-*"));
            Assert.True(File.Exists(Path.Combine(backup, "BepInEx", "progenitor-core", "Progenitor.dll")));
            Assert.True(File.Exists(Path.Combine(backup, "BepInEx", "progenitor-reload", "Installed.dll")));
            Assert.True(File.Exists(Path.Combine(backup, "BepInEx", "progenitor-reload", "HandCopied.dll")));
            Assert.False(Directory.Exists(Path.Combine(backup, "BepInEx", "progenitor-reload", "progenitor-reload")), "the reload folder nested inside its backup");
            Assert.False(Directory.Exists(Path.Combine(backup, "BepInEx", "progenitor-core", "progenitor-core")), "Core's folder nested inside its backup");
            Assert.True(File.Exists(Path.Combine(backup, "BepInEx", "progenitor-uninstall.json")));
            Assert.False(File.Exists(Path.Combine(bep, "progenitor-uninstall.json")));
            Assert.Contains("the Mods menu removal request", un.Output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(backup, "BepInEx", "progenitor-cache")), "the cache was kept in the backup");

            // Again: nothing of Progenitor left but its settings; without -KeepSettings they move to a backup too.
            var settings = Run(shell, "uninstall.ps1", null, "-GamePath", game);
            Assert.True(settings.Exit == 0, settings.Output);
            Assert.False(File.Exists(Path.Combine(bep, "config", "progenitor.cfg")));
            Assert.True(File.Exists(Path.Combine(bep, "config", "BepInEx.cfg")));
            var nothing = Run(shell, "uninstall.ps1", null, "-GamePath", game);
            Assert.True(nothing.Exit == 0, nothing.Output);
            Assert.Contains("Nothing to remove", nothing.Output, StringComparison.Ordinal);

            // -RemoveBepInEx: BepInEx and its loader files go; the player's plugins and settings stay in the backup.
            var loader = Run(shell, "uninstall.ps1", null, "-GamePath", game, "-RemoveBepInEx");
            Assert.True(loader.Exit == 0, loader.Output);
            Assert.Contains("Every other BepInEx mod in this folder stops loading", loader.Output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(bep));
            Assert.False(File.Exists(Path.Combine(game, "winhttp.dll")));
            Assert.False(File.Exists(Path.Combine(game, "doorstop_config.ini")));
            Assert.False(Directory.Exists(Path.Combine(game, "dotnet")));
            Assert.True(File.Exists(Path.Combine(game, "VRising.exe")));
            Assert.Equal("a world", File.ReadAllText(Path.Combine(game, "save-data", "Saves", "world.save")));
            string full = Assert.Single(Directory.GetDirectories(Path.Combine(game, "progenitor-backups"), "uninstall-*"));
            Assert.True(File.Exists(Path.Combine(full, "BepInEx", "plugins", "Other.dll")));
            Assert.True(File.Exists(Path.Combine(full, "BepInEx", "config", "BepInEx.cfg")));
            Assert.True(Directory.Exists(Path.Combine(full, "BepInEx", "progenitor-backups")), "earlier backups were not kept");
            Assert.False(Directory.Exists(Path.Combine(full, "BepInEx", "core")), "BepInEx's own files were kept in the backup");
            Assert.False(Directory.Exists(Path.Combine(full, "dotnet")));
        }
    }

    /// <summary>
    /// Opt-in (PROGENITOR_INSTALL_NETWORK_TEST=1): the real downloads, into a fake folder. Reads the latest GitHub release
    /// (or the release list once it is published) and the BepInEx pack from Thunderstore, so it is not part of CI.
    /// </summary>
    [Fact]
    public void Network_install_of_the_latest_release_into_a_fake_folder()
    {
        if (Environment.GetEnvironmentVariable("PROGENITOR_INSTALL_NETWORK_TEST") != "1") return;
        foreach (string shell in Shells())
        {
            using var fx = new Fixture();
            string game = fx.Game("VRising.exe");
            var dry = Run(shell, "install.ps1", null, "-GamePath", game, "-WhatIf");
            Assert.True(dry.Exit == 0, dry.Output);
            Assert.Contains("would install BepInExPack_V_Rising ", dry.Output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(game, "BepInEx")));

            var r = Run(shell, "install.ps1", null, "-GamePath", game);
            Assert.True(r.Exit == 0, r.Output);
            foreach (string f in new[] { "winhttp.dll", @"BepInEx\core\BepInEx.Core.dll", @"BepInEx\plugins\Progenitor.Bootstrap.dll", @"BepInEx\patchers\Progenitor.Preloader.dll",
                         @"BepInEx\progenitor-core\Progenitor.dll", @"BepInEx\progenitor-self-install.json" })
                Assert.True(File.Exists(Path.Combine(game, f)), f + " missing\n" + r.Output);
            Assert.False(File.Exists(Path.Combine(game, "manifest.json")));

            var again = Run(shell, "install.ps1", null, "-GamePath", game);
            Assert.True(again.Exit == 0, again.Output);
            Assert.Contains("kept (already installed)", again.Output, StringComparison.Ordinal);

            var un = Run(shell, "uninstall.ps1", null, "-GamePath", game, "-RemoveBepInEx");
            Assert.True(un.Exit == 0, un.Output);
            Assert.False(Directory.Exists(Path.Combine(game, "BepInEx")));
        }
    }

    // ---- fixtures and helpers --------------------------------------------------------------------------------

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "progenitor-installtest-" + Guid.NewGuid().ToString("N"));
        private int _n;

        public Fixture() => Directory.CreateDirectory(Root);

        public string Game(string exe)
        {
            string game = Path.Combine(Root, "Game Folder");
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, exe), "");
            return game;
        }

        /// <summary>A release zip with the manual-install layout, and a SHA256SUMS beside it.</summary>
        /// <param name="coreVersion">The version Core reports (default: <paramref name="version"/>); null for a Core with no version.</param>
        /// <summary>The self-install record a release zip carries (as tools/package.ps1 writes it), for a test package.</summary>
        public static string ZipRecord(string version) =>
            "{\"schema\":1,\"package\":\"VRising_Modders-Progenitor\",\"version\":\"" + version + "\",\"source\":\"package\"}";

        public string Package(string version, string table = "client-1.json", bool dev = false, string? extra = null, string? coreVersion = "", bool record = false)
        {
            string dir = Path.Combine(Root, "pkg" + (_n++));
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, "Progenitor-" + version + ".zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                Add(z, "BepInEx/plugins/Progenitor.Bootstrap.dll", "bootstrap " + version);
                Add(z, "BepInEx/patchers/Progenitor.Preloader.dll", "preloader " + version);
                if (coreVersion == null) Add(z, "BepInEx/progenitor-core/Progenitor.dll", "core " + version);
                else
                {
                    using var s = z.CreateEntry("BepInEx/progenitor-core/Progenitor.dll").Open();
                    s.Write(CoreDll(coreVersion.Length == 0 ? version : coreVersion));
                }
                Add(z, "BepInEx/progenitor-core/addresslib/" + table, "{}");
                Add(z, "BepInEx/progenitor-core/docs/guide/progenitor-guide.html", "<html></html>");
                if (record) Add(z, "BepInEx/progenitor-self-install.json", ZipRecord(version));
                if (dev) Add(z, "BepInEx/progenitor-reload/Progenitor.Dev.dll", "dev");
                if (extra != null) Add(z, extra, "extra");
            }

            File.WriteAllText(Path.Combine(dir, "SHA256SUMS"), "# test\n" + Sha(zip) + "  " + Path.GetFileName(zip) + "\n");
            return zip;
        }

        /// <summary>A zip shaped like BepInExPack_V_Rising from Thunderstore: listing files at the root, the pack in a folder.</summary>
        public string BepInExPack()
        {
            string zip = Path.Combine(Root, "BepInEx-BepInExPack_V_Rising-" + (_n++) + ".zip");
            using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
            Add(z, "manifest.json", "{}");
            Add(z, "icon.png", "png");
            Add(z, "README.md", "readme");
            Add(z, "BepInExPack_V_Rising/winhttp.dll", "shim");
            Add(z, "BepInExPack_V_Rising/doorstop_config.ini", "[General]");
            Add(z, "BepInExPack_V_Rising/.doorstop_version", "4");
            Add(z, "BepInExPack_V_Rising/dotnet/coreclr.dll", "clr");
            Add(z, "BepInExPack_V_Rising/BepInEx/core/BepInEx.Core.dll", "core");
            Add(z, "BepInExPack_V_Rising/BepInEx/config/BepInEx.cfg", "defaults");
            return zip;
        }

        private static readonly Dictionary<string, byte[]> CoreDlls = new();

        /// <summary>A small real DLL whose product version is "&lt;version&gt;+0123abc", as a release's Core reports it.</summary>
        public static byte[] CoreDll(string version)
        {
            lock (CoreDlls)
            {
                if (CoreDlls.TryGetValue(version, out var cached)) return cached;
                var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                    "[assembly: System.Reflection.AssemblyInformationalVersion(\"" + version + "+0123abc\")]\n"
                    + "[assembly: System.Reflection.AssemblyFileVersion(\"" + version + ".0\")]\npublic static class Core { }\n");
                var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Progenitor", new[] { tree },
                    new[] { Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                    new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
                using var ms = new MemoryStream();
                var result = compilation.Emit(ms, win32Resources: compilation.CreateDefaultWin32Resources(true, true, null, null));
                Assert.True(result.Success, string.Join("\n", result.Diagnostics));
                return CoreDlls[version] = ms.ToArray();
            }
        }

        private static void Add(ZipArchive z, string name, string text)
        {
            using var w = new StreamWriter(z.CreateEntry(name).Open());
            w.Write(text);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>Serves one script on loopback the way a static site may: as application/octet-stream, no charset.</summary>
    private sealed class ScriptServer : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        private readonly System.Threading.Tasks.Task _loop;

        public ScriptServer(byte[] body)
        {
            int port = FreePort();
            Url = "http://localhost:" + port + "/install.ps1";
            _listener.Prefixes.Add("http://localhost:" + port + "/");
            _listener.Start();
            _loop = System.Threading.Tasks.Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    System.Net.HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { return; }
                    ctx.Response.ContentType = "application/octet-stream";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
            });
        }

        public string Url { get; }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { /* stopping */ }
            try { _loop.Wait(5_000); } catch { /* stopped */ }
        }
    }

    private sealed record RunResult(int Exit, string Output);

    private static RunResult Run(string shell, string script, Dictionary<string, string>? env, params string[] args)
    {
        var psi = Start(shell, env);
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(ScriptPath(script));
        foreach (string a in args) psi.ArgumentList.Add(a);
        return Wait(shell, psi);
    }

    private static RunResult RunCommand(string shell, string command)
    {
        var psi = Start(shell, null);
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        return Wait(shell, psi);
    }

    private static ProcessStartInfo Start(string shell, Dictionary<string, string>? env)
    {
        var psi = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string key in new[] { "PROGENITOR_GAME_PATH", "PROGENITOR_SERVER", "PROGENITOR_WHATIF", "PROGENITOR_OFFLINE_PACKAGE", "PROGENITOR_OFFLINE_BEPINEX",
                     "PROGENITOR_STEAM_PATH", "PROGENITOR_FEED_URL", "PROGENITOR_KEEP_SETTINGS", "PROGENITOR_REMOVE_BEPINEX", "PROGENITOR_INSTALL_FAIL_AFTER" })
            psi.Environment.Remove(key);
        if (env != null)
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        return psi;
    }

    private static RunResult Wait(string shell, ProcessStartInfo psi)
    {
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(120_000), shell + " did not finish");
        var result = new RunResult(p.ExitCode, "[" + shell + "]\n" + output + err.Result);
        _log?.WriteLine(result.Output + "exit " + result.Exit + "\n");
        return result;
    }

    private static List<string>? _shells;

    /// <summary>Windows PowerShell 5.1 (the default on Windows) and PowerShell 7, whichever are installed.</summary>
    private static List<string> Shells()
    {
        if (_shells != null) return _shells;
        var found = new List<string>();
        foreach (string exe in new[] { "powershell", "pwsh" })
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("exit 0");
                using var p = Process.Start(psi);
                if (p == null) continue;
                p.WaitForExit(30_000);
                if (p.ExitCode == 0) found.Add(exe);
            }
            catch
            {
                // not installed
            }
        }

        return _shells = found;
    }

    private static Dictionary<string, string> Snapshot(string root, string? exclude)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, f);
            if (exclude != null && rel.StartsWith(exclude, StringComparison.OrdinalIgnoreCase)) continue;
            map[rel] = Sha(f);
        }

        foreach (string d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, d);
            if (exclude != null && rel.StartsWith(exclude, StringComparison.OrdinalIgnoreCase)) continue;
            map[rel + "\\"] = "dir";
        }

        return map;
    }

    private static void AssertSame(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        var added = after.Keys.Except(before.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = before.Keys.Except(after.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var changed = before.Keys.Where(k => after.TryGetValue(k, out var v) && v != before[k]).ToList();
        Assert.True(added.Count == 0 && removed.Count == 0 && changed.Count == 0,
            "added: " + string.Join(", ", added) + "; removed: " + string.Join(", ", removed) + "; changed: " + string.Join(", ", changed));
    }

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    private static string ShaText(string text) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(text))).ToLowerInvariant();

    private static string SharedRegion(string text)
    {
        int a = text.IndexOf("#region shared", StringComparison.Ordinal);
        int b = text.IndexOf("#endregion shared", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, "no shared region");
        return text.Substring(a, b - a);
    }

    private static string Script(string name) => File.ReadAllText(ScriptPath(name));

    private static string ScriptPath(string name) => Path.Combine(RepoRoot(), "docs", name);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Progenitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
