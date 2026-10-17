using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FDEP, the deploy audit of 2026-09-06 (<c>docs/dev-loop.md</c>).
///
/// <c>tools/deploy.ps1</c> and <c>tools/package.ps1</c> are the only two things in this repo that
/// write outside it, so what they may and may not carry is pinned here rather than left to review.
/// Three kinds of test:
///
///   1. <b>Executed</b> — <c>deploy.ps1 -WhatIf</c> is run against a throwaway BepInEx tree and the
///      tree is then asserted to be byte-for-byte untouched, and <c>package.ps1</c>'s own
///      <c>Assert-NoDeveloperArtefacts</c> is lifted out and fed a seeded tree — and the shared
///      fence in <c>tools/common/Fence.ps1</c> is run against every spelling of the live server
///      root that Win32 accepts.
///   2. <b>Source pins</b> on the two scripts, so a later edit that widens a copy or drops a fence
///      fails here instead of on a player's machine.
///   3. <b>Doc pins</b> that the audit table still names every artifact it is supposed to rule on.
///
/// No executed test bare-<c>return</c>s green any more (R40's test note). A missing PowerShell is
/// a failure; a missing build output or address table is a case with its OWN documented behavior,
/// which the test then asserts. And the <c>C:\VRisingServer</c> fence is EXECUTED against the
/// spellings that used to defeat it rather than grepped for (R40 finding 4).
/// </summary>
public sealed class DeployPackageTests
{
    // ---- 1. executed: deploy.ps1 -WhatIf -------------------------------------------------

    /// <summary>
    /// The whole point of <c>-WhatIf</c>: it lists what would be copied, with a real sha256 prefix
    /// per file, and writes nothing at all. "Nothing at all" is asserted against a snapshot of the
    /// throwaway tree taken before the run — not just against the plugin folders, because deploy
    /// also seeds <c>progenitor.cfg</c> and creates <c>progenitor-core</c> /
    /// <c>progenitor-reload</c> on a normal run.
    /// </summary>
    [Fact]
    public void DeployWhatIfListsHashedFilesAndWritesNothing()
    {
        string shell = RequirePowerShell();

        string root = FindRepoRoot();
        string bootstrap = Path.Combine(root, "src", "Progenitor.Bootstrap", "bin", "Debug", "net6.0", "Progenitor.Bootstrap.dll");
        bool haveBuild = File.Exists(bootstrap);

        string tmp = Path.Combine(Path.GetTempPath(), "progenitor-fdep-" + Guid.NewGuid().ToString("N"));
        string gameDir = Path.Combine(tmp, "game");
        string testRoot = Path.Combine(tmp, "testroot");
        Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "plugins"));
        Directory.CreateDirectory(Path.Combine(testRoot, "server", "BepInEx", "plugins"));
        try
        {
            var before = SnapshotTree(tmp);

            var (exit, stdout, stderr) = Invoke(
                shell,
                Path.Combine(root, "tools"),
                Path.Combine(root, "tools", "deploy.ps1"),
                new[] { "-WhatIf", "-GameDir", gameDir, "-TestRoot", testRoot });

            // "Writes nothing" is asserted either way — that is the -WhatIf contract, and it holds
            // on a CI with no game-referencing build output just as it does on a dev machine.
            var after = SnapshotTree(tmp);
            Assert.Equal(
                string.Join("\n", before.Select(kv => kv.Key + " " + kv.Value)),
                string.Join("\n", after.Select(kv => kv.Key + " " + kv.Value)));

            if (!haveBuild)
            {
                // No bin/Debug/net6.0 (Core and Bootstrap reference game assemblies; CI has no game
                // install). deploy.ps1's documented behavior is to refuse, loudly, rather than to
                // plan an empty deploy. Asserting THAT keeps this test non-vacuous on a clean CI,
                // which xunit 2.9 gives no way to state as a skip.
                Assert.True(exit != 0, "deploy.ps1 planned a deploy with no build output:\n" + stdout);
                Assert.Contains("no built Progenitor*.dll", stderr + stdout);
                return;
            }

            Assert.True(exit == 0, "deploy.ps1 -WhatIf exited " + exit + ": " + stderr + "\n" + stdout);
            Assert.Contains("deploy plan (-WhatIf): nothing was written", stdout);
            Assert.Contains("file(s) would be copied", stdout);

            // Every planned row leads with 16 hex characters, and none of them is the dashes the
            // formatter falls back to when hashing failed (Get-FileHash is itself WhatIf-gated,
            // which is exactly the bug the direct SHA256 call in Get-Sha256Prefix avoids).
            var rows = stdout.Split('\n')
                .Select(l => l.Trim())
                .Where(l => Regex.IsMatch(l, "^[0-9a-f]{16}  \\["))
                .ToList();
            Assert.True(rows.Count >= 4, "expected planned rows with sha256 prefixes, got:\n" + stdout);
            Assert.DoesNotContain("----------------  [", stdout);

            // Bootstrap and Core are the two the restart window turns on, and protocol 3 means they
            // ship together: both must be in a default plan, on both sides.
            foreach (string needle in new[]
            {
                "[client] Progenitor.Bootstrap", "[server] Progenitor.Bootstrap",
                "[client] Progenitor.Core", "[server] Progenitor.Core",
            })
            {
                Assert.Contains(needle, stdout);
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// The address tables are copied per target, not inside the Core branch, so a Bootstrap-only
    /// deploy still refreshes them — Bootstrap reads that folder before Core is loaded, and the
    /// client's <c>RotateCameraBlocker$$GetConsumedInputs</c> and the server's kill sites have no
    /// prefix fallback, so a stale table is a silent no-patch.
    /// </summary>
    [Fact]
    public void DeployWhatIfShipsAddressTablesWithoutCore()
    {
        string shell = RequirePowerShell();

        string root = FindRepoRoot();
        bool haveBuild = File.Exists(Path.Combine(root, "src", "Progenitor.Bootstrap", "bin", "Debug", "net6.0", "Progenitor.Bootstrap.dll"));
        bool haveTables = Directory.Exists(Path.Combine(root, "tools", "addresslib"))
            && Directory.EnumerateFiles(Path.Combine(root, "tools", "addresslib"), "client-*.json").Any();

        string tmp = Path.Combine(Path.GetTempPath(), "progenitor-fdep-" + Guid.NewGuid().ToString("N"));
        string gameDir = Path.Combine(tmp, "game");
        string testRoot = Path.Combine(tmp, "testroot");
        Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "plugins"));
        Directory.CreateDirectory(Path.Combine(testRoot, "server", "BepInEx", "plugins"));
        try
        {
            var (exit, stdout, stderr) = Invoke(
                shell,
                Path.Combine(root, "tools"),
                Path.Combine(root, "tools", "deploy.ps1"),
                new[] { "-WhatIf", "-Projects", "Progenitor.Bootstrap", "-GameDir", gameDir, "-TestRoot", testRoot });

            if (!haveTables)
            {
                // The tables are gitignored build output. Their ABSENCE has a documented behavior
                // too — a warning naming the rebuild command, not a silent prefix-scan deploy — and
                // asserting it keeps this test from passing green on a clean CI while asserting
                // nothing (R40's test note).
                Assert.Contains("no address tables under tools/addresslib", stderr + stdout);
                return;
            }
            if (!haveBuild)
            {
                // Tables but no build output: deploy refuses before it can plan (same documented
                // refusal DeployWhatIfListsHashedFilesAndWritesNothing pins). Still an assertion.
                Assert.True(exit != 0, "deploy.ps1 planned a deploy with no build output:\n" + stdout);
                Assert.Contains("no built Progenitor*.dll", stderr + stdout);
                return;
            }
            Assert.True(exit == 0, "deploy.ps1 -WhatIf exited " + exit + ": " + stderr + "\n" + stdout);

            Assert.Contains("addresslib -> ", stdout);
            foreach (string side in new[] { "[client] addresslib", "[server] addresslib" })
                Assert.Contains(side, stdout);
            // ...and the plan still carries no Core row, which is what makes this a Bootstrap-only run.
            Assert.DoesNotContain("Progenitor.Core ->", stdout);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* temp */ }
        }
    }

    // ---- 2. executed: package.ps1's own exclusion assertion ------------------------------

    public static IEnumerable<object[]> DeveloperArtefacts() => new[]
    {
        new object[] { "BepInEx/progenitor-reload/Progenitor.Sample.3.delta" },
        new object[] { "BepInEx/progenitor-reload/Progenitor.Sample.delta-state" },
        new object[] { "BepInEx/config/progenitor-allow.txt" },
        new object[] { "BepInEx/plugins/Progenitor/progenitor-core/addresslib/build-client-247473480-6a147771-ec12531-66fe4b61f7a0d186.json" },
        new object[] { "BepInEx/plugins/Progenitor/progenitor-core/driftmap-248100992-6a1b2c33-ff01234-aabbccdd11223344.json" },
        new object[] { "BepInEx/plugins/Progenitor/progenitor-core/access.txt" },
        new object[] { "BepInEx/plugins/Progenitor/Progenitor.Bootstrap.pdb" },
        new object[] { "BepInEx/plugins/Progenitor/Progenitor.Bootstrap.deps.json" },
        new object[] { "BepInEx/progenitor-pending/example.host/example.mod.dll" },
    };

    /// <summary>
    /// Each of these is created by something other than the build — tools/enc, the reloader, Core at
    /// runtime, or the operator — and none of them may be sealed into a release zip. Run through
    /// <c>package.ps1</c>'s own function, so the test cannot drift from the script.
    /// </summary>
    [Theory]
    [MemberData(nameof(DeveloperArtefacts))]
    public void PackageRefusesDeveloperArtefacts(string relativePath)
    {
        string shell = RequirePowerShell();

        string stage = Path.Combine(Path.GetTempPath(), "progenitor-fdep-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A legitimate public layout, plus the one artifact under test.
            Seed(stage, "BepInEx/plugins/Progenitor/Progenitor.Bootstrap.dll");
            Seed(stage, "BepInEx/plugins/Progenitor/progenitor-core/Progenitor.core");
            Seed(stage, "BepInEx/plugins/Progenitor/progenitor-core/addresslib/client-247473480-6a147771-ec12531-66fe4b61f7a0d186.json");
            Seed(stage, "manifest.json");
            Seed(stage, relativePath);

            var (exit, _, stderr) = RunAssertNoDeveloperArtefacts(shell, stage);
            Assert.True(exit != 0, "package.ps1 accepted a staged " + relativePath);
            Assert.Contains("developer-only artifacts", stderr);
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { /* temp */ }
        }
    }

    /// <summary>The public layout itself passes — the assertion must not be a blanket refusal.</summary>
    [Fact]
    public void PackageAcceptsThePublicLayout()
    {
        string shell = RequirePowerShell();

        string stage = Path.Combine(Path.GetTempPath(), "progenitor-fdep-" + Guid.NewGuid().ToString("N"));
        try
        {
            Seed(stage, "manifest.json");
            Seed(stage, "README.md");
            Seed(stage, "CHANGELOG.md");
            Seed(stage, "icon.png");
            Seed(stage, "BepInEx/plugins/Progenitor/Progenitor.Bootstrap.dll");
            Seed(stage, "BepInEx/plugins/Progenitor/Progenitor.Preloader.patcher");
            Seed(stage, "BepInEx/plugins/Progenitor/progenitor-core/Progenitor.core");
            Seed(stage, "BepInEx/plugins/Progenitor/progenitor-core/addresslib/client-247473480-6a147771-ec12531-66fe4b61f7a0d186.json");
            Seed(stage, "BepInEx/plugins/Progenitor/progenitor-core/addresslib/server-248598344-6a148fd0-ed22e34-3dcd792996235366.json");

            var (exit, _, stderr) = RunAssertNoDeveloperArtefacts(shell, stage);
            Assert.True(exit == 0, "package.ps1 refused the public layout: " + stderr);
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { /* temp */ }
        }
    }

    // ---- 2b. executed: the manual-install zip (one copy into the game folder) -------------

    private static readonly string[] ManualRequired =
    {
        "BepInEx/plugins/Progenitor/Progenitor.Bootstrap.dll", "BepInEx/plugins/Progenitor/Progenitor.Preloader.patcher", "BepInEx/plugins/Progenitor/progenitor-core/Progenitor.core",
    };

    /// <summary>
    /// The manual zip's only root entry is <c>BepInEx/</c> and every file is inside <c>BepInEx/plugins/Progenitor/</c>:
    /// extracted into the folder that holds VRising.exe it lands one folder, the same one a player can copy into
    /// <c>BepInEx/plugins</c> by hand, and the Thunderstore root files (manifest, README, icon) stay out of the game
    /// folder. Entry names use <c>/</c>. Run through <c>package.ps1</c>'s own function.
    /// </summary>
    [Fact]
    public void ManualZipHoldsOnlyTheBepInExTree()
    {
        string shell = RequirePowerShell();
        string stage = Path.Combine(Path.GetTempPath(), "progenitor-fdep-" + Guid.NewGuid().ToString("N"));
        string zip = Path.Combine(Path.GetTempPath(), "progenitor-fdep-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var bepInEx = new[]
            {
                "BepInEx/plugins/Progenitor/Progenitor.Bootstrap.dll",
                "BepInEx/plugins/Progenitor/Progenitor.Preloader.patcher",
                "BepInEx/plugins/Progenitor/progenitor-core/Progenitor.core",
                "BepInEx/plugins/Progenitor/progenitor-core/addresslib/client-247473480-6a147771-ec12531-66fe4b61f7a0d186.json",
                "BepInEx/plugins/Progenitor/progenitor-core/docs/guide/progenitor-guide.html",
            };
            foreach (string f in bepInEx) Seed(stage, f);
            foreach (string f in new[] { "manifest.json", "README.md", "CHANGELOG.md", "icon.png", "PROGENITOR_GUIDE.md" }) Seed(stage, f);

            var (exit, _, stderr) = RunNewGameFolderZip(shell, stage, zip);
            Assert.True(exit == 0, "New-GameFolderZip failed: " + stderr);

            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            var names = archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(bepInEx.OrderBy(n => n, StringComparer.Ordinal).ToArray(), names);
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { /* temp */ }
            try { File.Delete(zip); } catch { /* temp */ }
        }
    }

    /// <summary>A staged tree that lacks one of the three images is refused, so a manual zip can never ship half an install.</summary>
    [Theory]
    [InlineData("BepInEx/plugins/Progenitor/Progenitor.Bootstrap.dll")]
    [InlineData("BepInEx/plugins/Progenitor/Progenitor.Preloader.patcher")]
    [InlineData("BepInEx/plugins/Progenitor/progenitor-core/Progenitor.core")]
    public void ManualZipRefusesAMissingPiece(string missing)
    {
        string shell = RequirePowerShell();
        string stage = Path.Combine(Path.GetTempPath(), "progenitor-fdep-" + Guid.NewGuid().ToString("N"));
        string zip = Path.Combine(Path.GetTempPath(), "progenitor-fdep-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            foreach (string f in ManualRequired.Where(f => f != missing)) Seed(stage, f);
            var (exit, _, stderr) = RunNewGameFolderZip(shell, stage, zip);
            Assert.True(exit != 0, "New-GameFolderZip accepted a tree without " + missing);
            Assert.Contains("has no " + missing, stderr);
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { /* temp */ }
            try { File.Delete(zip); } catch { /* temp */ }
        }
    }

    /// <summary>The script builds the manual zip from the asserted stage with the same three required entries, and names the two assets apart.</summary>
    [Fact]
    public void PackageWritesTheManualZipFromTheAssertedStage()
    {
        string s = ReadRepoFile("tools", "package.ps1");
        Assert.Contains("New-GameFolderZip -Stage $stage -ZipPath $manualZipPath -Required $requiredEntries", s);
        foreach (string entry in ManualRequired) Assert.Contains("\"" + entry + "\"", s);
        Assert.Contains("(\"Progenitor-\" + $version + \".zip\")", s);
        Assert.Contains("(\"Progenitor-\" + $version + \"-thunderstore.zip\")", s);
        // Both zips are attested in SHA256SUMS.
        Assert.Contains("Get-Item -LiteralPath $zipPath, $manualZipPath", s);
        // Built after both stage assertions ran.
        Assert.True(s.IndexOf("Assert-NoDeveloperArtefacts -Root $stage", StringComparison.Ordinal)
                    < s.IndexOf("New-GameFolderZip -Stage $stage", StringComparison.Ordinal));
    }

    private static string ScriptGuardFunctions()
    {
        string src = ReadRepoFile("tools", "package.ps1");
        return ExtractPowerShellFunction(src, "Get-InstallScriptNames") + "\n" + ExtractPowerShellFunction(src, "Assert-NoInstallScripts")
            + "\n" + ExtractPowerShellFunction(src, "Get-OutsideHomeEntries");
    }

    /// <summary>
    /// Release packages are a plain extract: a script, batch file or executable anywhere in a zip fails packaging
    /// (Nexus refuses .bat/.ps1 in a package). Run through <c>package.ps1</c>'s own functions.
    /// </summary>
    [Theory]
    [InlineData("install.bat")]
    [InlineData("BepInEx/plugins/setup.cmd")]
    [InlineData("BepInEx/progenitor-core/install.ps1")]
    [InlineData("BepInEx/patchers/run.exe")]
    [InlineData("tools/install.sh")]
    [InlineData("BepInEx/x.vbs")]
    public void PackageRefusesInstallScripts(string name)
    {
        string shell = RequirePowerShell();
        string script = "$ErrorActionPreference = 'Stop'\n" + ScriptGuardFunctions()
                        + "\nAssert-NoInstallScripts -Names @('BepInEx/plugins/Progenitor.Bootstrap.dll', $args[0]) -Where 'test'\n";
        var (exit, _, stderr) = RunPowerShellScript(shell, script, name);
        Assert.True(exit != 0, "package.ps1 accepted " + name);
        Assert.Contains("script or executable", stderr);
    }

    /// <summary>The real package contents pass the script guard: DLLs, JSON tables, the HTML guide and the Thunderstore files.</summary>
    [Fact]
    public void PackageAcceptsItsOwnFileTypes()
    {
        string shell = RequirePowerShell();
        string script = "$ErrorActionPreference = 'Stop'\n" + ScriptGuardFunctions()
                        + "\nAssert-NoInstallScripts -Names @('manifest.json', 'README.md', 'icon.png', 'BepInEx/plugins/Progenitor.Bootstrap.dll', "
                        + "'BepInEx/progenitor-core/addresslib/client-1.json', 'BepInEx/progenitor-core/docs/guide/progenitor-guide.html', 'BepInEx/progenitor-core/') -Where 'test'\n";
        var (exit, _, stderr) = RunPowerShellScript(shell, script);
        Assert.True(exit == 0, "the script guard refused Progenitor's own files: " + stderr);
    }

    /// <summary>
    /// Single-folder install: nothing in either zip's BepInEx tree is outside <c>BepInEx/plugins/Progenitor/</c> (Bootstrap
    /// places the preloader in <c>patchers</c> by itself). The three-location paths are refused; the Dev bridge of an
    /// <c>-IncludeDev</c> package and the Thunderstore root files are not BepInEx entries of the folder and pass.
    /// </summary>
    [Theory]
    [InlineData("BepInEx/patchers/Progenitor.Preloader.dll")]
    [InlineData("BepInEx/progenitor-core/Progenitor.dll")]
    [InlineData("BepInEx/plugins/Progenitor.Bootstrap.dll")]
    [InlineData("BepInEx/plugins/Other/Other.dll")]
    public void ManualZipRefusesAnEntryOutsideTheFolder(string stray)
    {
        string shell = RequirePowerShell();
        string stage = Path.Combine(Path.GetTempPath(), "progenitor-fdep-" + Guid.NewGuid().ToString("N"));
        string zip = Path.Combine(Path.GetTempPath(), "progenitor-fdep-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            foreach (string f in ManualRequired) Seed(stage, f);
            Seed(stage, stray);
            var (exit, _, stderr) = RunNewGameFolderZip(shell, stage, zip);
            Assert.True(exit != 0, "New-GameFolderZip accepted " + stray);
            Assert.Contains("outside BepInEx/plugins/Progenitor/", stderr);
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { /* temp */ }
            try { File.Delete(zip); } catch { /* temp */ }
        }
    }

    /// <summary>The outside-the-folder rule on its own: what passes and what is reported.</summary>
    [Fact]
    public void OutsideHomeEntriesNamesOnlyStrayBepInExFiles()
    {
        string shell = RequirePowerShell();
        string script = "$ErrorActionPreference = 'Stop'\n" + ScriptGuardFunctions()
                        + "\n(Get-OutsideHomeEntries -Names @('manifest.json', 'README.md', 'BepInEx/', 'BepInEx/plugins/Progenitor/Progenitor.Bootstrap.dll', "
                        + "'BepInEx\\plugins\\Progenitor\\progenitor-core\\Progenitor.core', 'BepInEx/progenitor-reload/Progenitor.Dev.dll', "
                        + "'BepInEx/patchers/Progenitor.Preloader.dll')) -join '|'\n";
        var (exit, stdout, stderr) = RunPowerShellScript(shell, script);
        Assert.True(exit == 0, stderr);
        Assert.Equal("BepInEx/patchers/Progenitor.Preloader.dll", stdout.Trim());
    }

    /// <summary>A staged install script never reaches the manual zip.</summary>
    [Fact]
    public void ManualZipRefusesAStagedScript()
    {
        string shell = RequirePowerShell();
        string stage = Path.Combine(Path.GetTempPath(), "progenitor-fdep-" + Guid.NewGuid().ToString("N"));
        string zip = Path.Combine(Path.GetTempPath(), "progenitor-fdep-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            foreach (string f in ManualRequired) Seed(stage, f);
            Seed(stage, "BepInEx/plugins/Progenitor/install.ps1");
            var (exit, _, stderr) = RunNewGameFolderZip(shell, stage, zip);
            Assert.True(exit != 0, "New-GameFolderZip accepted a staged install.ps1");
            Assert.Contains("script or executable", stderr);
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { /* temp */ }
            try { File.Delete(zip); } catch { /* temp */ }
        }
    }

    /// <summary>The script guard runs on the staged tree and on both finished zips.</summary>
    [Fact]
    public void PackageChecksScriptsOnStageAndBothZips()
    {
        string s = ReadRepoFile("tools", "package.ps1");
        Assert.Contains("Assert-NoInstallScripts -Names @(Get-ChildItem -LiteralPath $stage", s);
        Assert.Contains("Assert-NoInstallScripts -Names @($zip.Entries", s);
        Assert.Contains("Assert-NoInstallScripts -Names $names -Where $ZipPath", s);
    }

    private static (int Exit, string Out, string Err) RunNewGameFolderZip(string shell, string stage, string zip)
    {
        string fn = ScriptGuardFunctions() + "\n" + ExtractPowerShellFunction(ReadRepoFile("tools", "package.ps1"), "New-GameFolderZip");
        string script = "$ErrorActionPreference = 'Stop'\n" + fn + "\nNew-GameFolderZip -Stage $args[0] -ZipPath $args[1] -Required @("
                        + PsList(ManualRequired) + ")\n";
        return RunPowerShellScript(shell, script, stage, zip);
    }

    // ---- 3. executed: the shared fence (tools/common/Fence.ps1) ---------------------------
    //
    // R40 finding 4. The fence used to be `$root -like "C:\VRisingServer*"` on the RAW parameter
    // string in deploy.ps1 and stage-test-server.ps1, so `-GameDir C:/VRisingServer` walked
    // straight through it into `robocopy /MIR`. These tests EXECUTE the fence rather than
    // grepping for it, and each says which mutation it catches.

    /// <summary>
    /// Every spelling of <c>C:\VRisingServer</c> Win32 accepts is refused. This is the finding's
    /// own list.
    ///
    /// Mutation check (measured): replacing <c>Test-PathUnderRoot</c>'s body with
    /// <c>return $Path -like ($Root + '*')</c> — the reverted fence — fails this test, on the
    /// forward-slash, <c>\\?\</c>, admin-share, dot-segment, lowercase and 8.3 rows.
    ///
    /// The 8.3 row is the one row whose verdict is reached differently on different machines, and
    /// it is why this test could not run on CI. Where <c>C:\VRisingServer</c> EXISTS the alias is
    /// expanded by <c>GetFullPath</c> before the fence compares anything (measured on both hosts:
    /// pwsh 7.6 and Windows PowerShell 5.1 each return the long name for <c>C:\VRISIN~1</c> and for
    /// <c>C:\PROGRA~1\dotnet\x</c>). Where it does NOT exist — a clean CI runner — nothing can
    /// expand it, and until the fence refused unresolvable aliases outright the row came back
    /// <c>under=False, no-throw</c> (measured on the CI baseline, run 34794896168). Both machines
    /// now refuse it; which rule carries the row is the only difference, and
    /// <see cref="TheFenceRefusesAnUnresolvableShortNameAlias"/> pins the second one on every
    /// machine.
    /// </summary>
    [Fact]
    public void TheFenceRefusesEverySpellingOfTheLiveServerRoot()
    {
        string shell = RequirePowerShell();

        // Every one of these reaches C:\VRisingServer on Windows. Read-only: the fence resolves
        // and compares, it never creates anything, and none of these is handed to a script that
        // writes — the live server is not touched by this test.
        string[] spellings =
        {
            @"C:\VRisingServer",
            @"C:/VRisingServer",
            @"C:/VRisingServer/BepInEx/config",
            @"C:\VRisingServer\",
            @"c:\vrisingserver\BepInEx",
            @"C:\VRisingServer\BepInEx\plugins",
            @"\\?\C:\VRisingServer\BepInEx",
            @"//?/C:/VRisingServer",
            @"\\.\C:\VRisingServer",
            @"\\localhost\C$\VRisingServer",
            @"C:\GameModding\..\VRisingServer\x",
            @"C:\VRISIN~1",
        };

        var rows = FenceRows(RunFence(shell, FenceProbe(spellings, "'C:\\VRisingServer'")));
        Assert.Equal(spellings.Length, rows.Count);
        foreach (var row in rows)
        {
            Assert.True(row.Under == "True", "the fence did not see " + row.Path + " as the live server root");
            Assert.True(row.Threw == "threw", "Assert-NotLiveServerPath accepted " + row.Path);
        }
    }

    /// <summary>
    /// ...and the boundary holds in the other direction: a sibling whose name merely starts with
    /// the root's name is not under it, and neither is the same leaf on another drive. Without
    /// this, a "fix" for the finding could be a bare <c>StartsWith</c>, which would refuse the
    /// test tree the dev loop actually deploys into if it were ever named next to the live one.
    ///
    /// Mutation check (measured): dropping the <c>+ '\'</c> from <c>Test-PathUnderRoot</c>'s
    /// boundary comparison fails this test on the two <c>VRisingServerBackup</c> rows, and so does
    /// the raw-string <c>-like</c> revert.
    /// </summary>
    [Fact]
    public void TheFenceAllowsSiblingsOfTheLiveServerRoot()
    {
        string shell = RequirePowerShell();

        string[] allowed =
        {
            @"C:\VRisingServerBackup",
            @"C:\VRisingServerBackup\BepInEx",
            @"C:\GameModding\_testserver",
            @"C:\GameModding\_testserver\server\BepInEx\plugins",
            @"D:\VRisingServer",
        };

        var rows = FenceRows(RunFence(shell, FenceProbe(allowed, "'C:\\VRisingServer'")));
        Assert.Equal(allowed.Length, rows.Count);
        foreach (var row in rows)
        {
            Assert.True(row.Under == "False", "the fence refused " + row.Path + ", which is not the live server");
            Assert.True(row.Threw == "no-throw", "Assert-NotLiveServerPath threw on " + row.Path);
        }
    }

    /// <summary>
    /// An 8.3 alias that resolves to nothing is refused, not compared as a literal name. This is
    /// the <c>C:\VRISIN~1</c> row of <see cref="TheFenceRefusesEverySpellingOfTheLiveServerRoot"/>
    /// on a machine where the forbidden root does not exist — the CI case — and it is written
    /// against a throwaway root under <c>%TEMP%</c> that is never created, so it asserts the same
    /// property on every machine and goes nowhere near <c>C:\VRisingServer</c>.
    ///
    /// <para>An unresolvable alias is not "somewhere else": it is a name whose meaning arrives
    /// later, the moment the directory it abbreviates is created. A fence that reads it as a
    /// literal answers <c>under=False</c> today and <c>under=True</c> tomorrow for the same
    /// string.</para>
    ///
    /// Mutation check (measured): deleting the <c>Test-FenceShortNameForm</c> branch from
    /// <c>ConvertTo-FenceCanonicalPath</c>'s segment loop — the best-effort pass as it was — fails
    /// this test on both rows with <c>under=False, no-throw</c>, under pwsh 7 and Windows
    /// PowerShell 5.1 alike.
    /// </summary>
    [Fact]
    public void TheFenceRefusesAnUnresolvableShortNameAlias()
    {
        string shell = RequirePowerShell();

        string tmp = Path.Combine(Path.GetTempPath(), "progenitor-fshort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // The root is NOT created: this is the shape of a CI runner that has no live server.
            // ABSENT~1 is the alias Windows would hand out for `absent-root` in this directory.
            string root = Path.Combine(tmp, "absent-root");
            Assert.False(Directory.Exists(root));

            string body =
                "$root = $args[0]\n" +
                "foreach ($p in @($args[1], (Join-Path $args[1] 'BepInEx\\plugins'))) {\n" +
                "  $under = Test-PathUnderRoot -Path $p -Root $root\n" +
                "  $threw = 'no-throw'\n" +
                "  try { Assert-NotForbiddenPath -Path $p -ForbiddenRoots @($root) } catch { $threw = 'threw' }\n" +
                "  Write-Output (\"ROW|$p|$under|$threw\")\n" +
                "}\n";

            var rows = FenceRows(RunFence(shell, body, root, Path.Combine(tmp, "ABSENT~1")));
            Assert.Equal(2, rows.Count);
            foreach (var row in rows)
            {
                Assert.True(row.Under == "True",
                    "the fence read the unresolvable alias " + row.Path + " as a name of its own");
                Assert.True(row.Threw == "threw", "Assert-NotForbiddenPath accepted " + row.Path);
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// ...and the refusal is about what the alias RESOLVES to, not about the tilde. The directory
    /// named <c>ABSENT~1</c> here is really named that, which is the case this arm settles: a
    /// genuine alias never reaches it, because <c>GetFullPath</c> has already expanded the ones
    /// that resolve (and on .NET, unlike .NET Framework, the enumeration would not match a short
    /// name anyway). Three rows,
    /// each guarding a different way the rule above could be written too widely: a directory that
    /// is really named <c>ABSENT~1</c> resolves to exactly one entry and is compared on its merits;
    /// so is a long name that contains a tilde; and so is a long tilde name that does not exist
    /// yet, which no filesystem lookup can settle either. Refusing every segment with a <c>~</c>
    /// in it would refuse real directories — <c>C:\Users\RUNNE~1\AppData\Local\Temp</c> is where
    /// this suite's own staging tests run on a Windows runner.
    ///
    /// Mutation check (measured): dropping the <c>$hits.Count -eq 1</c> arm (refusing on shape
    /// alone) fails this test on the <c>ABSENT~1</c> row; widening
    /// <c>Test-FenceShortNameForm</c> to any segment containing a tilde fails it on the
    /// <c>archive~2026</c> row — and not on <c>my~notes</c>, which exists and so is resolved
    /// before the shape is ever consulted.
    /// </summary>
    [Fact]
    public void TheFenceStillComparesShortNameFormDirectoriesThatExist()
    {
        string shell = RequirePowerShell();

        string tmp = Path.Combine(Path.GetTempPath(), "progenitor-fshort-" + Guid.NewGuid().ToString("N"));
        string alias = Path.Combine(tmp, "ABSENT~1");
        string tilde = Path.Combine(tmp, "my~notes");
        // Never created: a long tilde name the fence cannot resolve is still not an 8.3 alias.
        string absentTilde = Path.Combine(tmp, "archive~2026");
        Directory.CreateDirectory(alias);
        Directory.CreateDirectory(tilde);
        try
        {
            string root = Path.Combine(tmp, "absent-root");
            string body =
                "$root = $args[0]\n" +
                "foreach ($p in @($args[1], (Join-Path $args[2] 'about-to-be-created'), (Join-Path $args[3] 'about-to-be-created'))) {\n" +
                "  $under = Test-PathUnderRoot -Path $p -Root $root\n" +
                "  $threw = 'no-throw'\n" +
                "  try { Assert-NotForbiddenPath -Path $p -ForbiddenRoots @($root) } catch { $threw = 'threw' }\n" +
                "  Write-Output (\"ROW|$p|$under|$threw\")\n" +
                "}\n";

            var rows = FenceRows(RunFence(shell, body, root, alias, tilde, absentTilde));
            Assert.Equal(3, rows.Count);
            foreach (var row in rows)
            {
                Assert.True(row.Under == "False", "the fence refused " + row.Path + ", which is not an 8.3 alias of the root");
                Assert.True(row.Threw == "no-throw", "Assert-NotForbiddenPath threw on " + row.Path);
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// A junction whose TARGET the fence cannot place makes the whole path unplaceable, including
    /// the paths below it — which are the ones the dev-loop scripts actually hand to a write.
    /// <c>ConvertTo-FenceCanonicalPath</c> recurses into a reparse point's target, and a verdict
    /// coming back out of that recursion has to propagate: combining the refusal sentinel with the
    /// remaining segments builds a path-shaped string that is under no root at all, so the deeper
    /// the caller's path, the safer it looked.
    ///
    /// <para>Found in review of the 8.3 rule above, which is what makes this reachable for an
    /// ordinary alias; it was already reachable through a target the fence refuses for its form.
    /// The link here points at an alias that stops resolving — the directory it named is deleted
    /// after the junction is made — which is the case the alias rule exists for, arrived at
    /// through a link.</para>
    ///
    /// Mutation check (measured): assigning the recursion's result without the sentinel guard —
    /// the branch as it was — fails this test on the <c>BepInEx\plugins</c> row with
    /// <c>under=False, no-throw</c>, while the link's own row still passes.
    /// </summary>
    [Fact]
    public void TheFenceRefusesAPathUnderAJunctionWhoseTargetItCannotPlace()
    {
        string shell = RequirePowerShell();

        string tmp = Path.Combine(Path.GetTempPath(), "progenitor-flink-" + Guid.NewGuid().ToString("N"));
        string gone = Path.Combine(tmp, "GONE~1");
        string link = Path.Combine(tmp, "link");
        Directory.CreateDirectory(gone);
        try
        {
            var (mkExit, _, _) = RunPowerShellScript(
                shell,
                "cmd /c mklink /J \"$($args[1])\" \"$($args[0])\" | Out-Null\nif (Test-Path -LiteralPath $args[1]) { exit 0 } else { exit 1 }\n",
                gone, link);
            Assert.True(mkExit == 0, "could not create a junction under %TEMP%");
            Directory.Delete(gone);
            Assert.False(Directory.Exists(gone));

            string body =
                "$root = $args[0]\n" +
                "foreach ($p in @($args[1], (Join-Path $args[1] 'BepInEx\\plugins'))) {\n" +
                "  $under = Test-PathUnderRoot -Path $p -Root $root\n" +
                "  $threw = 'no-throw'\n" +
                "  try { Assert-NotForbiddenPath -Path $p -ForbiddenRoots @($root) } catch { $threw = 'threw' }\n" +
                "  Write-Output (\"ROW|$p|$under|$threw\")\n" +
                "}\n";

            var rows = FenceRows(RunFence(shell, body, Path.Combine(tmp, "absent-root"), link));
            Assert.Equal(2, rows.Count);
            foreach (var row in rows)
            {
                Assert.True(row.Under == "True",
                    "the fence placed " + row.Path + ", whose junction target it cannot resolve");
                Assert.True(row.Threw == "threw", "Assert-NotForbiddenPath accepted " + row.Path);
            }
        }
        finally
        {
            try { Directory.Delete(link); } catch { /* junction */ }
            try { Directory.Delete(tmp, true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// R41 finding 6. <c>\\?\</c> and <c>\\.\</c> do not only prefix drive paths: over the Win32
    /// device namespace (<c>\\?\Volume{…}\</c>, <c>\\?\GLOBALROOT\Device\…</c>,
    /// <c>\\.\PhysicalDrive0</c>, <c>\\?\pipe\…</c>) the remainder after the four-character strip
    /// is NOT rooted, so <c>GetFullPath</c> anchored it to the working directory and both
    /// spellings walked through the fence with a canonical form that pointed under the repo — the
    /// diagnostic pointed at the wrong place too. The fence now recognizes exactly two forms,
    /// <c>&lt;drive&gt;:\…</c> and <c>\\server\share\…</c>, and refuses everything else outright,
    /// before any root is compared. The empty string is refused on the same rule.
    ///
    /// Mutation check (measured): reverting the prefix rule to the unconditional
    /// <c>$p = $p.Substring(4)</c> fails this test on the <c>Volume{…}</c>, <c>GLOBALROOT</c>,
    /// <c>PhysicalDrive0</c> and <c>pipe</c> rows — each canonicalizes to a path under the current
    /// directory and the fence answers <c>under=False, no-throw</c>, which is the finding exactly.
    /// Dropping only the post-<c>GetFullPath</c> shape check leaves the four device rows refused
    /// but lets the empty-string row through.
    /// </summary>
    [Fact]
    public void TheFenceRefusesUnrecognisedPathForms()
    {
        string shell = RequirePowerShell();

        // Read-only, as everywhere in this region: the fence resolves and compares, and none of
        // these strings is handed to a script that writes.
        string[] unrecognised =
        {
            @"\\?\Volume{00000000-0000-0000-0000-000000000000}\VRisingServer",
            @"\\?\GLOBALROOT\Device\HarddiskVolume3\VRisingServer",
            @"\\.\Volume{00000000-0000-0000-0000-000000000000}\VRisingServer\BepInEx\plugins",
            @"\\.\PhysicalDrive0",
            @"\\?\pipe\progenitor",
            @"\\server-with-no-share",
            "",
        };

        var rows = FenceRows(RunFence(shell, FenceProbe(unrecognised, "'C:\\VRisingServer'")));
        Assert.Equal(unrecognised.Length, rows.Count);
        foreach (var row in rows)
        {
            Assert.True(row.Under == "True",
                "the fence treated the unrecognised form '" + row.Path + "' as somewhere else rather than as refused");
            Assert.True(row.Threw == "threw",
                "Assert-NotLiveServerPath accepted the unrecognised form '" + row.Path + "'");
        }

        // ...while the two recognized forms still decide on their merits, in both directions: the
        // UNC spelling of the live server's own share is refused, and a plain drive-letter sibling
        // is not. A "refuse everything exotic" mutation that also refused the sibling fails here.
        var uncRow = Assert.Single(FenceRows(RunFence(shell,
            FenceProbe(new[] { @"\\localhost\C$\VRisingServer" }, "'C:\\VRisingServer'"))));
        Assert.Equal("True", uncRow.Under);
        Assert.Equal("threw", uncRow.Threw);

        var siblingRow = Assert.Single(FenceRows(RunFence(shell,
            FenceProbe(new[] { @"C:\VRisingServerBackup\BepInEx" }, "'C:\\VRisingServer'"))));
        Assert.Equal("False", siblingRow.Under);
        Assert.Equal("no-throw", siblingRow.Threw);

        // The refusal names the FORM, so the diagnostic can never print a canonical path that
        // quietly became relative to the working directory.
        string reason = RunFence(shell,
            "try { Assert-NotLiveServerPath '\\\\?\\GLOBALROOT\\Device\\HarddiskVolume3\\x' }\n" +
            "catch { Write-Output (\"MSG|\" + $_.Exception.Message) }\n");
        Assert.Contains("unrecognised path form", reason);
        Assert.DoesNotContain(FindRepoRoot(), reason);
    }

    /// <summary>
    /// A junction is checked at its TARGET, so a test root symlinked at a forbidden tree is
    /// refused. Built against a throwaway root under %TEMP% rather than the real live server —
    /// the fence takes its roots as a parameter, so the property is identical and nothing here
    /// goes near <c>C:\VRisingServer</c>.
    ///
    /// Mutation check (measured): deleting the reparse-point branch from
    /// <c>ConvertTo-FenceCanonicalPath</c> fails this test — the link path stops resolving into
    /// the forbidden root — and so does dropping the whole filesystem pass
    /// (<c>ConvertTo-FenceCanonicalPath</c> -&gt; <c>ConvertTo-FenceNormalizedPath</c>), and so
    /// does the raw-string <c>-like</c> revert.
    /// </summary>
    [Fact]
    public void TheFenceFollowsAJunctionToItsTarget()
    {
        string shell = RequirePowerShell();

        string tmp = Path.Combine(Path.GetTempPath(), "progenitor-fdep-" + Guid.NewGuid().ToString("N"));
        string real = Path.Combine(tmp, "real");
        string link = Path.Combine(tmp, "link");
        Directory.CreateDirectory(Path.Combine(real, "inner"));
        try
        {
            var (mkExit, _, _) = RunPowerShellScript(
                shell,
                "cmd /c mklink /J \"$($args[1])\" \"$($args[0])\" | Out-Null\nif (Test-Path -LiteralPath $args[1]) { exit 0 } else { exit 1 }\n",
                real, link);
            // mklink /J needs no elevation, and this suite is Windows-only by construction, so a
            // failure here is a real failure rather than an absent precondition.
            Assert.True(mkExit == 0, "could not create a junction under %TEMP%; the fence's reparse-point resolution is untested without one");

            string body =
                "$root = $args[0]\n" +
                "$p = Join-Path $args[1] 'inner\\about-to-be-created'\n" +
                "$under = Test-PathUnderRoot -Path $p -Root $root\n" +
                "$threw = 'no-throw'\n" +
                "try { Assert-NotForbiddenPath -Path $p -ForbiddenRoots @($root) } catch { $threw = 'threw' }\n" +
                "Write-Output (\"ROW|$p|$under|$threw\")\n";

            var rows = FenceRows(RunFence(shell, body, real, link));
            Assert.Single(rows);
            Assert.Equal("True", rows[0].Under);
            Assert.Equal("threw", rows[0].Threw);
        }
        finally
        {
            try { Directory.Delete(link); } catch { /* junction */ }
            try { Directory.Delete(tmp, true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// R42 finding 8. A <c>subst</c> drive is neither a spelling nor a reparse point: nothing in
    /// the normalizer and nothing <c>GetFullPath</c> does sees through it, so <c>S:\</c> for
    /// <c>subst S: &lt;root&gt;</c> used to canonicalize to <c>S:\</c> and compare as somewhere
    /// else. The fence now resolves the letter through its DOS-device definition first.
    ///
    /// The subst is made against a throwaway folder under <c>%TEMP%</c> and removed in
    /// <c>finally</c> — never against <c>C:\VRisingServer</c>, whose refusal is asserted through
    /// the injected definition in
    /// <c>TheFenceResolvesDriveAndShareDefinitionsThroughTheirDefinition</c> instead. The fence
    /// takes its roots as a parameter, so the property proved here is the same one.
    ///
    /// The letter is shared with every process of the logon session, including this suite run
    /// concurrently from another worktree, so it is claimed and proven ours
    /// (<see cref="ClaimSubstDrive"/>) and proven still ours after the fence has answered. Picking
    /// a "free" letter and then subst-ing it failed about one run in three with three concurrent
    /// runs (measured 2026-09-28): a subst refused by a letter taken in between, or a fence
    /// answering for a letter whose definition another run had changed.
    ///
    /// Mutation check (measured): deleting the <c>Resolve-FenceMappedPath</c> call from
    /// <c>ConvertTo-FenceCanonicalPath</c> fails this test — the path stays <c>Q:\…</c> and the
    /// fence answers <c>under=False, no-throw</c>, which is the finding exactly.
    /// </summary>
    [Fact]
    public void TheFenceResolvesASubstDriveToItsTarget()
    {
        string shell = RequirePowerShell();

        string tmp = Path.Combine(Path.GetTempPath(), "progenitor-fdep4-" + Guid.NewGuid().ToString("N"));
        string real = Path.Combine(tmp, "real");
        Directory.CreateDirectory(Path.Combine(real, "inner"));
        string? letter = null;
        try
        {
            letter = ClaimSubstDrive(shell, real);

            string body =
                "$root = $args[0]\n" +
                "$p = $args[1] + ':\\inner\\about-to-be-created'\n" +
                "$under = Test-PathUnderRoot -Path $p -Root $root\n" +
                "$threw = 'no-throw'\n" +
                "try { Assert-NotForbiddenPath -Path $p -ForbiddenRoots @($root) } catch { $threw = 'threw' }\n" +
                "Write-Output (\"ROW|$p|$under|$threw\")\n";

            var rows = FenceRows(RunFence(shell, body, real, letter));
            // The fence's answer is about OUR mapping only if the letter still carries just that.
            Assert.True(OwnsSubstDrive(letter, real),
                letter + ": no longer carries only this test's subst after the fence ran: " + DescribeDosDevice(letter));
            Assert.Single(rows);
            Assert.Equal("True", rows[0].Under);
            Assert.Equal("threw", rows[0].Threw);
        }
        finally
        {
            if (letter != null) ReleaseSubstDrive(letter, real);
            try { Directory.Delete(tmp, true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// R43. When the QueryDosDevice lookup is UNAVAILABLE — the <c>Add-Type</c> that builds the
    /// P/Invoke fails, say — the fence used to fall back to <c>DriveInfo.DriveType</c>, and a
    /// <c>subst</c> drive reports <c>Fixed</c>: a subst of the live server passed. The lookup now
    /// answers a distinct "unavailable" value, and in that state every existing letter except the
    /// system drive is refused.
    ///
    /// The subst is real (a throwaway temp folder, claimed with <see cref="ClaimSubstDrive"/> and
    /// released in <c>finally</c>) so the letter genuinely exists and genuinely reports
    /// <c>Fixed</c>; only the lookup is injected as unavailable, both by returning the sentinel and
    /// by throwing. The rows that must stay allowed: a letter with no definition (it names
    /// nothing), and the system drive, which is what makes "refuse everything" livable.
    ///
    /// Mutation check (measured): making the injected lookup answer <c>$null</c> for the sentinel
    /// and for a throw (the old fail-open behavior) fails this test on the subst row: the drive
    /// reports <c>Fixed</c> and passes.
    /// </summary>
    [Fact]
    public void TheFenceRefusesASubstDriveWhenTheDosDeviceLookupIsUnavailable()
    {
        string shell = RequirePowerShell();

        string tmp = Path.Combine(Path.GetTempPath(), "progenitor-fdep5-" + Guid.NewGuid().ToString("N"));
        string real = Path.Combine(tmp, "real");
        Directory.CreateDirectory(Path.Combine(real, "inner"));
        string? letter = null;
        try
        {
            letter = ClaimSubstDrive(shell, real);
            // A letter with no definition, picked from the far end so it does not race the Q..Y claims.
            string? absent = "ZYXWVUTSRQPONMLKJIHGFED".Select(c => c.ToString())
                .FirstOrDefault(l => l != letter && DosDeviceDefinitions(l).Count == 0);
            Assert.True(absent != null, "no undefined drive letter to probe");

            string probe =
                "$root = $args[0]\n" +
                "foreach ($p in @(($args[1] + ':\\inner\\about-to-be-created'), ($args[2] + ':\\x'), ($env:SystemDrive + '\\progenitor-elsewhere'))) {\n" +
                "  $under = Test-PathUnderRoot -Path $p -Root $root\n" +
                "  $threw = 'no-throw'\n" +
                "  try { Assert-NotForbiddenPath -Path $p -ForbiddenRoots @($root) } catch { $threw = 'threw' }\n" +
                "  Write-Output (\"ROW|$p|$under|$threw\")\n" +
                "}\n";

            foreach (string lookup in new[]
            {
                "$ProgenitorFenceDosDeviceLookup = { param($l) $ProgenitorFenceDosDeviceUnavailable }\n",
                "$ProgenitorFenceDosDeviceLookup = { param($l) throw 'Add-Type: csc could not compile' }\n",
            })
            {
                var rows = FenceRows(RunFence(shell, lookup + probe, real, letter, absent!));
                Assert.True(OwnsSubstDrive(letter, real),
                    letter + ": no longer carries only this test's subst after the fence ran: " + DescribeDosDevice(letter));
                Assert.Equal(3, rows.Count);

                // The subst letter exists and reports Fixed; with the lookup unavailable it is refused.
                Assert.True(rows[0].Under == "True", "unavailable lookup let the subst " + rows[0].Path + " through (" + lookup.Trim() + ")");
                Assert.Equal("threw", rows[0].Threw);

                // A letter with no definition is still left alone.
                Assert.Equal("False", rows[1].Under);
                Assert.Equal("no-throw", rows[1].Threw);

                // The system drive is not refused.
                Assert.Equal("False", rows[2].Under);
                Assert.Equal("no-throw", rows[2].Threw);
            }
        }
        finally
        {
            if (letter != null) ReleaseSubstDrive(letter, real);
            try { Directory.Delete(tmp, true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// R42 finding 8, the three definitions that cannot be created on the machine the suite runs
    /// on: a <c>subst</c> of the LIVE SERVER (which must never exist, so it is injected rather
    /// than made), a <c>net use</c> mapping (which needs a real share and stays for the session),
    /// and a share of the live tree. <c>Fence.ps1</c> takes its three system lookups —
    /// <c>QueryDosDevice</c>, <c>Get-PSDrive</c>'s <c>DisplayRoot</c> and the local share table —
    /// as overridable scriptblocks precisely so each answer is drivable without the mapping.
    ///
    /// The rows, in order: a subst onto the live root; a mapped letter whose UNC only
    /// <c>DisplayRoot</c> states (the device string carries no share, so a fence that skipped
    /// <c>DisplayRoot</c> would have to refuse); a mapped letter resolved from the
    /// <c>LanmanRedirector</c> device string alone; a plain non-admin share of the live tree; a
    /// local share the share table cannot name (refused — the fence's failure mode); a drive whose
    /// device family it has never seen (refused for the same reason); and a share on ANOTHER host,
    /// which is left alone, because refusing every UNC would refuse a legitimate root.
    ///
    /// Mutation check (measured): deleting the <c>Resolve-FenceMappedPath</c> call from
    /// <c>ConvertTo-FenceCanonicalPath</c> fails this test (<c>Failed: 2, Passed: 0</c> with the
    /// subst test, on the first row, <c>S:\BepInEx\plugins</c>). Returning <c>$Path</c> instead of
    /// the sentinel when a definition cannot be read leaves the subst test green and fails this
    /// one on <c>\\localhost\no-such-share\x</c> — the first of the two rows that rule owns, the
    /// other being <c>V:\VRisingServer</c>.
    /// </summary>
    [Fact]
    public void TheFenceResolvesDriveAndShareDefinitionsThroughTheirDefinition()
    {
        string shell = RequirePowerShell();

        // Injected definitions. Nothing here is created: these are answers, not mappings, and the
        // live server is named only as the thing the fence must refuse.
        string fakes =
            "$ProgenitorFenceDosDeviceLookup = {\n" +
            "  param($l)\n" +
            "  switch ($l) {\n" +
            "    'S' { '\\??\\C:\\VRisingServer' }\n" +                                    // subst
            "    'Z' { '\\Device\\LanmanRedirector' }\n" +                                 // mapped, UNC from DisplayRoot
            "    'W' { '\\Device\\LanmanRedirector\\;W:0000000000012345\\localhost\\vrising' }\n" +
            "    'V' { '\\Device\\SomeFilterDriverVolume' }\n" +                            // unknown family
            "    default { $null }\n" +
            "  }\n" +
            "}\n" +
            "$ProgenitorFenceDisplayRootLookup = { param($l) if ($l -eq 'Z') { '\\\\localhost\\C$' } else { $null } }\n" +
            "$ProgenitorFenceShareLookup = { param($n) if ($n -eq 'vrising') { 'C:\\VRisingServer' } else { $null } }\n";

        string[] refused =
        {
            @"S:\BepInEx\plugins",
            @"Z:\VRisingServer\BepInEx",
            @"W:\Plugins",
            @"\\localhost\vrising\Plugins",
            @"\\localhost\no-such-share\x",
            @"V:\VRisingServer",
        };

        var rows = FenceRows(RunFence(shell, fakes + FenceProbe(refused, "'C:\\VRisingServer'")));
        Assert.Equal(refused.Length, rows.Count);
        foreach (var row in rows)
        {
            Assert.True(row.Under == "True", "the fence did not resolve " + row.Path + " to the live server");
            Assert.True(row.Threw == "threw", "Assert-NotLiveServerPath accepted " + row.Path);
        }

        // ...and the direction a blunt "refuse every mapping" fix would have broken.
        var remote = Assert.Single(FenceRows(RunFence(shell,
            fakes + FenceProbe(new[] { @"\\fileserver\share\x" }, "'C:\\VRisingServer'"))));
        Assert.Equal("False", remote.Under);
        Assert.Equal("no-throw", remote.Threw);
    }

    /// <summary>
    /// <c>deploy.ps1</c> itself refuses <c>-GameDir C:/VRisingServer</c>: the finding's failure
    /// scenario, run against the real script. Under <c>-WhatIf</c>, which writes nothing even if
    /// the fence were absent, so this test can never be the thing that does the damage.
    ///
    /// Mutation check (measured): reverting deploy.ps1's fence to
    /// <c>if ($root -like "C:\VRisingServer*")</c> and dropping the two parameter-level calls
    /// fails the two admin-share rows — the run gets past the fence, and under the old code that
    /// is a live tree it goes on to enumerate.
    ///
    /// The forward-slash rows do NOT fail under that mutation, and the honest reason is worth
    /// writing down: <c>Join-Path "C:/VRisingServer" "BepInEx\plugins"</c> normalizes to a
    /// backslash path, so the derived <c>$t.Dir</c> happened to trip the old <c>-like</c> one
    /// root later than it should have. They are kept because the fix makes the refusal happen on
    /// the PARAMETER, before <c>$targets</c> exists — and because R40's stage-test-server
    /// scenario, where the same spelling reaches <c>robocopy /MIR</c>, had no such accident.
    /// </summary>
    [Theory]
    [InlineData("-GameDir", "C:/VRisingServer")]
    [InlineData("-TestRoot", "C:/VRisingServer")]
    [InlineData("-GameDir", @"\\localhost\C$\VRisingServer")]
    [InlineData("-TestRoot", @"\\localhost\C$\VRisingServer")]
    public void DeployRefusesAnAlternateSpellingOfTheLiveServerRoot(string parameterName, string spelling)
    {
        string shell = RequirePowerShell();

        string tmp = Path.Combine(Path.GetTempPath(), "progenitor-fdep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmp, "game", "BepInEx", "plugins"));
        Directory.CreateDirectory(Path.Combine(tmp, "testroot", "server", "BepInEx", "plugins"));
        try
        {
            string[] args = parameterName == "-GameDir"
                ? new[] { "-WhatIf", "-GameDir", spelling, "-TestRoot", Path.Combine(tmp, "testroot") }
                : new[] { "-WhatIf", "-GameDir", Path.Combine(tmp, "game"), "-TestRoot", spelling };

            var (exit, stdout, stderr) = Invoke(
                shell,
                Path.Combine(FindRepoRoot(), "tools"),
                Path.Combine(FindRepoRoot(), "tools", "deploy.ps1"),
                args);

            Assert.True(exit != 0, "deploy.ps1 accepted " + parameterName + " " + spelling + ":\n" + stdout);
            Assert.Contains("refusing to touch the live server", stderr + stdout);
            Assert.DoesNotContain("deploy plan (-WhatIf)", stdout);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* temp */ }
        }
    }

    // ---- 4. source pins ------------------------------------------------------------------

    /// <summary>
    /// All four scripts that take a root parameter share one fence, and none still carries the
    /// raw-string <c>-like</c> the finding was about. The behavior is tested above; this pin is
    /// about a fifth script — or a re-edit — reintroducing a private copy.
    /// </summary>
    [Fact]
    public void EveryToolsScriptWithARootParameterDotSourcesTheSharedFence()
    {
        foreach (string script in new[] { "deploy.ps1", "stage-test-server.ps1", "package.ps1", "test-server.ps1" })
        {
            string s = ReadRepoFile("tools", script);
            Assert.Contains("common\\Fence.ps1", s);
            // On CODE lines only: the reverted form is quoted in the comments that explain why it
            // was wrong, and those must stay readable.
            foreach (string line in s.Replace("\r\n", "\n").Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("#", StringComparison.Ordinal) || t.StartsWith("<#", StringComparison.Ordinal)) continue;
                Assert.DoesNotContain("-like \"C:\\VRisingServer", t);
                Assert.DoesNotContain("-like 'C:\\VRisingServer", t);
            }
        }

        string fence = ReadRepoFile("tools", "common", "Fence.ps1");
        // Job FUI27 converted the suite's source pins to SourcePin, which reads the code part of a
        // C# line. The pins in this file read PowerShell, where the comment marker is # and those
        // helpers would be a category error, so they stay Assert.Contains and stay vacuous against
        // a #-commented line. 29 pins here are in that state; closing them
        // needs a marker-aware helper and was out of that job's stated .cs scope.
        // Normalization, boundary and resolution: the three properties the finding asked for.
        Assert.Contains("[System.IO.Path]::GetFullPath", fence);
        Assert.Contains("StringComparison]::OrdinalIgnoreCase", fence);
        Assert.Contains("$r + '\\'", fence);            // directory-boundary compare
        Assert.Contains("ReparsePoint", fence);         // junctions and symlinks
        Assert.Contains("GetFileSystemEntries", fence); // 8.3 short names
        Assert.Contains("Replace('/', '\\')", fence);   // forward slashes

        // R41 finding 6: the recognized-form rule, and the refusal that precedes every comparison.
        Assert.Contains("$ProgenitorFenceUnrecognisedPath", fence);
        Assert.Contains("unrecognised path form", fence);
        // The \\?\ strip is conditional on a drive letter, not unconditional.
        Assert.DoesNotContain("$p = $p.Substring(4)", fence);
    }

    /// <summary>
    /// The fence covers every root each run touches, and is asserted before anything is written
    /// or planned. Order is the property that matters, so it is asserted as order: in all three
    /// dev-loop scripts the first fence call precedes the first line that could write.
    /// </summary>
    [Fact]
    public void DeployFencesEveryLiveServerRoot()
    {
        string s = ReadRepoFile("tools", "deploy.ps1");
        foreach (string root in new[] { "$t.Under", "$t.Dir", "$t.Reload", "$t.Core", "$t.Config" })
            Assert.Contains(root, s);
        Assert.Contains("Assert-NotLiveServerPath $GameDir", s);
        Assert.Contains("Assert-NotLiveServerPath $TestRoot", s);
        AssertFencedBeforeFirstWrite(s, "deploy.ps1");

        // stage-test-server.ps1 fences its own roots too, GameDir included — asserted on the fence
        // CALL, not on the parameter declaration. The previous pin's Assert.Contains("$GameDir",
        // stage) was satisfied by the param block at :27 and therefore could not fail.
        string stage = ReadRepoFile("tools", "stage-test-server.ps1");
        Assert.Contains("foreach ($root in @($TestRoot, $GameDir,", stage);
        Assert.Contains("Assert-NotLiveServerPath $root", stage);
        AssertFencedBeforeFirstWrite(stage, "stage-test-server.ps1");

        // test-server.ps1 promotes, deletes and launches out of -TestRoot; it is fenced too.
        string testServer = ReadRepoFile("tools", "test-server.ps1");
        Assert.Contains("Assert-NotLiveServerPath $TestRoot", testServer);
        AssertFencedBeforeFirstWrite(testServer, "test-server.ps1");
    }

    /// <summary>
    /// The first fence call in a script precedes every line that could create, copy, delete,
    /// mirror or launch. Moving the fence loop to the end of the script — which a grep-only pin
    /// would still pass — fails here.
    /// </summary>
    private static void AssertFencedBeforeFirstWrite(string script, string name)
    {
        string[] lines = script.Replace("\r\n", "\n").Split('\n');
        int fenceAt = -1;
        int writeAt = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("#", StringComparison.Ordinal) || t.StartsWith("<#", StringComparison.Ordinal)) continue;
            if (fenceAt < 0 && (t.Contains("Assert-NotLiveServerPath") || t.Contains("Assert-NotForbiddenPath")))
                fenceAt = i;
            if (writeAt < 0 && (t.Contains("New-Item") || t.Contains("Copy-Item") || t.Contains("Remove-Item") ||
                                t.Contains("Move-Item") || t.Contains("Set-Content") || t.Contains("WriteAll") ||
                                t.Contains("robocopy") || t.Contains("Win32_Process")))
                writeAt = i;
        }
        Assert.True(fenceAt >= 0, name + " never calls the fence");
        Assert.True(writeAt < 0 || fenceAt < writeAt,
            name + ": the fence is at line " + (fenceAt + 1) + " but the first write is at line " + (writeAt + 1));
    }

    /// <summary>-WhatIf exists, comes from SupportsShouldProcess, and does not build.</summary>
    [Fact]
    public void DeploySupportsWhatIfAndDoesNotBuildUnderIt()
    {
        string s = ReadRepoFile("tools", "deploy.ps1");
        Assert.Contains("SupportsShouldProcess = $true", s);
        Assert.Contains("$plan = [bool]$WhatIfPreference", s);
        // The build is behind the flag: a plan run must not write into bin/.
        Assert.Contains("skipping dotnet build", s);
        // Hashing is done directly, not through the WhatIf-gated cmdlet: under -WhatIf,
        // Get-FileHash's own pipeline is short-circuited and returns nothing. It may still be
        // NAMED (the comment that explains this), but never CALLED.
        Assert.Contains("System.Security.Cryptography.SHA256", s);
        foreach (string line in s.Replace("\r\n", "\n").Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("#", StringComparison.Ordinal)) continue;
            Assert.DoesNotContain("Get-FileHash", t);
        }
    }

    /// <summary>
    /// Deploy creates none of the runtime-owned artifacts. This is a negative pin: the script must
    /// not gain a line that writes an allowlist, a pending folder, a delta, a fingerprint or the
    /// Dev opt-in marker into a game tree.
    /// </summary>
    [Fact]
    public void DeployCreatesNoRuntimeOwnedArtefact()
    {
        string s = ReadRepoFile("tools", "deploy.ps1");
        foreach (string forbidden in new[]
        {
            "progenitor-pending", "progenitor-dev.enable", ".delta-state",
        })
        {
            // Named only in the comment block that says deploy must not create it, never in a path
            // it writes. A Join-Path / Copy-Item mentioning one is the failure this pin catches.
            foreach (string line in s.Replace("\r\n", "\n").Split('\n'))
            {
                if (!line.Contains(forbidden)) continue;
                string t = line.Trim();
                Assert.True(
                    t.StartsWith("#", StringComparison.Ordinal) || t.StartsWith("<#", StringComparison.Ordinal) ||
                    !(t.Contains("Copy-Item") || t.Contains("New-Item") || t.Contains("WriteAll")),
                    "deploy.ps1 writes " + forbidden + ": " + t);
            }
        }
        // The allowlist gets its own line, because deploy DOES write progenitor.cfg in the
        // same folder and the two must not be confused.
        Assert.DoesNotContain("progenitor-allow.txt\"", s);
    }

    /// <summary>Both scripts select address tables by side prefix, never <c>*.json</c>.</summary>
    [Fact]
    public void AddressTableGlobIsSidePrefixed()
    {
        foreach (string script in new[] { "deploy.ps1", "package.ps1" })
        {
            string s = ReadRepoFile("tools", script);
            Assert.Contains("client-*.json", s);
            Assert.Contains("server-*.json", s);
            Assert.Contains("build-", s); // the fingerprint is named in the comment that explains the filter
        }
    }

    /// <summary>package.ps1 asserts the exclusions on the staged tree AND on the sealed zip.</summary>
    [Fact]
    public void PackageAssertsExclusionsTwice()
    {
        string s = ReadRepoFile("tools", "package.ps1");
        Assert.Contains("function Assert-NoDeveloperArtefacts", s);
        Assert.Contains("Assert-NoDeveloperArtefacts -Root $stage", s);
        // ...and on the zip entries.
        Assert.Contains("developer-only artifacts: ", s);
        foreach (string pattern in new[] { "*.delta", "*.delta-state", "progenitor-allow.txt", "build-*.json", "driftmap-*.json", "progenitor-pending" })
            Assert.Contains(pattern, s);
        // The Dev/token/marker assertions that were already there stay.
        Assert.Contains("Assert-NoDevPayload -Root $stage", s);
    }

    /// <summary>The dev-loop artifacts cannot be committed by accident.</summary>
    [Fact]
    public void GitIgnoreCoversDevLoopArtefacts()
    {
        string s = ReadRepoFile(".gitignore").Replace("\r\n", "\n");
        foreach (string line in new[] { "*.delta", "*.delta-state", "driftmap-*.json", "build-*.json", "progenitor-allow.txt", "*.token", "progenitor-dev.enable" })
            Assert.Contains("\n" + line + "\n", "\n" + s + "\n");
    }

    // ---- 4. doc pin ----------------------------------------------------------------------

    /// <summary>
    /// The audit table names every artifact it rules on. A new runtime artifact that never gets a
    /// row is the failure mode this catches — the table is the checklist a restart window is read
    /// against.
    /// </summary>
    [Fact]
    public void DevLoopCarriesTheDeployAudit()
    {
        string doc = ReadRepoFile("docs", "dev-loop.md");
        Assert.Contains("## Deploy audit 2026-09-06", doc);
        Assert.Contains("pwsh tools/deploy.ps1 -WhatIf", doc);
        foreach (string artefact in new[]
        {
            "Progenitor.Bootstrap.dll", "Progenitor.dll", "Progenitor.Dev.dll", "Progenitor.Sample.dll",
            "localization", "assets", "addresslib", "build-<side>-<build>.json", "driftmap-<build>.json",
            "progenitor-pending", "delta-state", "progenitor-allow.txt", "progenitor-dev.enable",
            "SHA256SUMS", "C:\\VRisingServer",
        })
        {
            Assert.Contains(artefact, doc);
        }
    }

    // ---- helpers -------------------------------------------------------------------------

    private readonly record struct FenceRow(string Path, string Under, string Threw);

    /// <summary>Rows the fence snippets print: <c>ROW|&lt;path&gt;|&lt;under&gt;|&lt;threw&gt;</c>.</summary>
    private static List<FenceRow> FenceRows(string stdout)
    {
        var rows = new List<FenceRow>();
        foreach (string line in stdout.Replace("\r\n", "\n").Split('\n'))
        {
            string t = line.Trim();
            if (!t.StartsWith("ROW|", StringComparison.Ordinal)) continue;
            string[] parts = t.Split('|');
            Assert.True(parts.Length == 4, "malformed fence row: " + t);
            rows.Add(new FenceRow(parts[1], parts[2], parts[3]));
        }
        Assert.True(rows.Count > 0, "the fence script printed no rows:\n" + stdout);
        return rows;
    }

    /// <summary>
    /// A snippet that asks both fence entry points about each path: the predicate
    /// <c>Test-PathUnderRoot</c> and the throwing <c>Assert-NotLiveServerPath</c>, so a fence that
    /// answers correctly but forgets to throw is caught too.
    /// </summary>
    private static string FenceProbe(string[] paths, string psRootLiteral)
    {
        return "foreach ($p in @(" + PsList(paths) + ")) {\n" +
               "  $under = Test-PathUnderRoot -Path $p -Root " + psRootLiteral + "\n" +
               "  $threw = 'no-throw'\n" +
               "  try { Assert-NotLiveServerPath $p } catch { $threw = 'threw' }\n" +
               "  Write-Output (\"ROW|$p|$under|$threw\")\n" +
               "}\n";
    }

    /// <summary>
    /// Claims a drive letter from the end of the alphabet (so the pick cannot collide with a fixed
    /// disk) by <c>subst</c>-ing it onto <paramref name="real"/>, and returns it only once the
    /// letter's definition list is proven to be exactly that one mapping. Drive letters live in the
    /// logon session's DOS-device namespace, shared by every process of the user — this suite
    /// running in another worktree above all — so looking for a free letter and then using it is a
    /// race. The subst itself is the claim: a letter that refuses it, or that turns out to carry
    /// another definition as well (<c>DefineDosDevice</c> stacks), has exactly our definition
    /// removed and the next letter is tried. subst needs no elevation and this suite is
    /// Windows-only by construction, so running out of letters is a real failure.
    /// </summary>
    private static string ClaimSubstDrive(string shell, string real)
    {
        var tried = new List<string>();
        foreach (char c in "QRTUVWXY")
        {
            string letter = c.ToString();
            if (DosDeviceDefinitions(letter).Count != 0) { tried.Add(letter + ": in use"); continue; }

            var (exit, stdout, stderr) = RunPowerShellScript(shell, "& subst.exe $args[0] $args[1]\nexit $LASTEXITCODE\n",
                letter + ":", real);
            if (exit != 0) { tried.Add(letter + ": subst exited " + exit + " (" + (stdout + stderr).Trim() + ")"); continue; }
            if (OwnsSubstDrive(letter, real)) return letter;

            tried.Add(letter + ": shared after subst (" + DescribeDosDevice(letter) + ")");
            ReleaseSubstDrive(letter, real);
        }
        Assert.Fail("could not claim a drive letter in Q..Y for a subst onto a temp folder; the fence's " +
                    "drive-definition resolution is untested without one. " + string.Join("; ", tried));
        return "";
    }

    /// <summary>True when <paramref name="letter"/> carries exactly one definition: the subst onto <paramref name="real"/>.</summary>
    private static bool OwnsSubstDrive(string letter, string real)
    {
        var defs = DosDeviceDefinitions(letter);
        return defs.Count == 1 && string.Equals(defs[0], SubstTarget(real), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes this test's subst of <paramref name="letter"/> and nothing else: an exact-match
    /// removal of our own target, so a definition another process has made keeps its mapping.
    /// </summary>
    private static void ReleaseSubstDrive(string letter, string real)
    {
        DefineDosDevice(DDD_RAW_TARGET_PATH | DDD_REMOVE_DEFINITION | DDD_EXACT_MATCH_ON_REMOVE,
            letter + ":", SubstTarget(real));
    }

    /// <summary>The object-manager target <c>subst</c> records for <paramref name="real"/>.</summary>
    private static string SubstTarget(string real) =>
        @"\??\" + Path.GetFullPath(real).TrimEnd(Path.DirectorySeparatorChar);

    private static string DescribeDosDevice(string letter)
    {
        var defs = DosDeviceDefinitions(letter);
        return defs.Count == 0 ? "no definition" : string.Join(" | ", defs);
    }

    /// <summary>The letter's DOS-device definitions, current one first; empty when it has none.</summary>
    private static List<string> DosDeviceDefinitions(string letter)
    {
        var buffer = new char[32 * 1024];
        uint written = QueryDosDevice(letter + ":", buffer, (uint)buffer.Length);
        if (written == 0) return new List<string>();
        return new string(buffer, 0, (int)written).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private const uint DDD_RAW_TARGET_PATH = 0x1;
    private const uint DDD_REMOVE_DEFINITION = 0x2;
    private const uint DDD_EXACT_MATCH_ON_REMOVE = 0x4;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, char[] targetPath, uint max);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "DefineDosDeviceW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DefineDosDevice(uint flags, string deviceName, string targetPath);

    /// <summary>Runs a snippet with tools/common/Fence.ps1 dot-sourced — the real file, not a copy.</summary>
    private static string RunFence(string shell, string body, params string[] args)
    {
        string fence = System.IO.Path.Combine(FindRepoRoot(), "tools", "common", "Fence.ps1");
        Assert.True(File.Exists(fence), "the shared fence is missing: " + fence);
        string script = "$ErrorActionPreference = 'Stop'\n. '" + fence + "'\n" + body;
        var (exit, stdout, stderr) = RunPowerShellScript(shell, script, args);
        Assert.True(exit == 0, "fence snippet exited " + exit + ": " + stderr + "\n" + stdout);
        return stdout;
    }

    /// <summary>A PowerShell array literal of single-quoted paths (no path here contains a quote).</summary>
    private static string PsList(string[] values)
    {
        foreach (string v in values) Assert.DoesNotContain("'", v);
        return string.Join(", ", values.Select(v => "'" + v + "'"));
    }

    private static void Seed(string root, string relativePath)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "seed");
    }

    private static SortedDictionary<string, long> SnapshotTree(string root)
    {
        var map = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (string d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            map[d.Substring(root.Length) + Path.DirectorySeparatorChar] = -1;
        foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            map[f.Substring(root.Length)] = new FileInfo(f).Length;
        return map;
    }

    private static (int Exit, string Out, string Err) RunAssertNoDeveloperArtefacts(string shell, string stage)
    {
        string fn = ExtractPowerShellFunction(ReadRepoFile("tools", "package.ps1"), "Assert-NoDeveloperArtefacts");
        string script = "$ErrorActionPreference = 'Stop'\n" + fn + "\nAssert-NoDeveloperArtefacts -Root $args[0]\n";
        return RunPowerShellScript(shell, script, stage);
    }

    /// <summary>
    /// Lifts one <c>function Name { … }</c> out of a script by brace matching, so a test can
    /// execute it without running the script's body (deploy.ps1 deploys; package.ps1 packages).
    /// Same seam ReloadHardeningTests uses for <c>Format-SumsRow</c>.
    /// </summary>
    private static string ExtractPowerShellFunction(string script, string name)
    {
        int start = script.IndexOf("function " + name, StringComparison.Ordinal);
        Assert.True(start >= 0, "source pin: tools script no longer defines " + name);
        int open = script.IndexOf('{', start);
        Assert.True(open > start, name + " has no body");
        int depth = 0;
        for (int i = open; i < script.Length; i++)
        {
            if (script[i] == '{') depth++;
            else if (script[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return script.Substring(start, i - start + 1);
            }
        }
        throw new InvalidOperationException("unbalanced braces in " + name);
    }

    private static string RunPowerShellFile(string shell, string scriptPath, params string[] args)
    {
        var (exit, stdout, stderr) = Invoke(shell, Path.GetDirectoryName(scriptPath)!, scriptPath, args);
        Assert.True(exit == 0, shell + " exited " + exit + ": " + stderr + "\n" + stdout);
        return stdout;
    }

    private static (int, string, string) RunPowerShellScript(string shell, string script, params string[] args)
    {
        string file = Path.Combine(Path.GetTempPath(), "progenitor-fdep-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, script, new UTF8Encoding(false));
        try { return Invoke(shell, Path.GetTempPath(), file, args); }
        finally { try { File.Delete(file); } catch { /* temp */ } }
    }

    private static (int, string, string) Invoke(string shell, string workingDir, string file, string[] args)
    {
        var psi = new ProcessStartInfo(shell)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(file);
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        Assert.NotNull(p);
        string stdout = p!.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        return (p.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// The PowerShell these tests need. A missing one is a FAILURE, not a green pass.
    ///
    /// R40's test note: every executed test in this file used to bare-<c>return</c> when its
    /// precondition was absent, so on a clean CI the whole behavioral half reported green while
    /// asserting nothing. xunit 2.9 has no dynamic skip (<c>Assert.Skip</c> is v3, and the
    /// SkippableFact package is not a dependency this repo takes), so the fix is to remove the
    /// optionality instead: everything under test here is a Windows PowerShell script operating on
    /// <c>C:\</c> paths, so a run without PowerShell is not a run this suite can be satisfied by.
    /// The other preconditions (a game-referencing build output, address tables) are genuinely
    /// absent on CI, so those tests assert the documented behavior of the ABSENT case instead of
    /// returning — see <c>DeployWhatIfListsHashedFilesAndWritesNothing</c>.
    /// </summary>
    private static string RequirePowerShell()
    {
        string? shell = PowerShellExe();
        Assert.True(
            shell != null,
            "no pwsh/powershell on PATH. These tests execute tools/*.ps1, which is what they are for; " +
            "a run without PowerShell cannot assert anything about them and must not report green.");
        return shell!;
    }

    private static string? _shell;
    private static bool _shellChecked;

    /// <summary>First working PowerShell on PATH, or null. Probed once per test run.</summary>
    private static string? PowerShellExe()
    {
        if (_shellChecked) return _shell;
        _shellChecked = true;
        foreach (string exe in new[] { "pwsh", "powershell" })
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("exit 0");
                using var p = Process.Start(psi);
                if (p == null) continue;
                p.WaitForExit(60_000);
                if (p.ExitCode == 0) { _shell = exe; return _shell; }
            }
            catch
            {
                // not installed under this name
            }
        }
        return null;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Progenitor.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        string path = Path.Combine(FindRepoRoot(), Path.Combine(parts));
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        return File.ReadAllText(path);
    }
}
