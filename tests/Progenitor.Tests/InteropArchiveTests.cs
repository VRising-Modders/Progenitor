using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Progenitor.Resilience;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FUPD4. The archive that gives <c>tools/driftmap</c> an <c>--old</c> folder to diff
/// against, driven over a temporary folder tree that stands in for a V Rising install: a
/// <c>GameAssembly.dll</c> of a few bytes, an "interop" folder of fake assemblies and an archive
/// root. No game, no BepInEx, no real install — the same reason the rest of
/// <c>Resilience/</c> is compiled into this suite.
///
/// <para>What these tests are actually defending. BepInEx regenerates <c>BepInEx/interop</c>
/// <b>in place</b> on a game update, so the previous build's set exists only if it was copied
/// beforehand; there is no recovering it afterwards. That makes three of the properties below
/// load-bearing rather than tidy: an archive is written for a build the first time it is seen
/// (or that build is lost), the same build is never re-copied (or every start costs 200 MB of
/// disk I/O), and a copy that fails part-way never appears under the final name (or the next
/// drift map is silently computed from half an interop set, which is worse than having none).
/// </para>
/// </summary>
public class InteropArchiveTests : IDisposable
{
    private const string BuildA = "247473480-6a147771-ec12531-66fe4b61f7a0d186";
    private const string BuildB = "248100992-6a1b2c33-ff01234-aabbccdd11223344";
    private const string BuildC = "249000111-7c2d3e44-11223344-556677889900aabb";
    private const string BuildD = "250000222-8d3e4f55-22334455-66778899aabbccdd";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "progenitor-archive-" + Guid.NewGuid().ToString("N"));

    private string Interop => Path.Combine(_root, "interop");
    private string Archives => Path.Combine(_root, "addresslib");

    public InteropArchiveTests()
    {
        Directory.CreateDirectory(Interop);
        Directory.CreateDirectory(Archives);
        WriteInterop("ProjectM.dll", "one");
        WriteInterop("Unity.Entities.dll", "two");
        WriteInterop("Stunlock.Core.dll", "three");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void WriteInterop(string name, string body) =>
        File.WriteAllText(Path.Combine(Interop, name), body);

    private InteropArchiveRequest Request(string build = BuildA, string side = "client") => new()
    {
        Side = side,
        Build = build,
        InteropDirectory = Interop,
        ArchiveRoot = Archives,
        // The fake interop set is a few bytes, and the production 512 MiB headroom would refuse
        // it on any nearly-full CI disk. The refusal itself has its own test below.
        MinimumFreeBytes = 0,
    };

    private static void Age(string directory, DateTime utc) =>
        Directory.SetLastWriteTimeUtc(directory, utc);

    /// <summary>A fixed clock, so the temp-age guard is a decision and not a race with the suite.</summary>
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The path a pass with this pid and start time would write its copy to.</summary>
    private string Temp(string build, int pid, DateTime started) =>
        Path.Combine(Archives, InteropArchive.TemporaryFolderName("client", build, pid, started));

    /// <summary>
    /// A hand-made archive folder that carries a manifest, the way <c>Ensure</c> publishes one.
    /// Since FUPD6 an archive with no manifest is Incomplete and is never offered as
    /// <c>--old</c>, so a test that only wants a folder to exist still has to describe it.
    /// </summary>
    private static string PublishFolder(string root, string side, string build)
    {
        var path = Path.Combine(root, InteropArchive.FolderNameFor(side, build));
        Directory.CreateDirectory(path);
        File.WriteAllText(
            Path.Combine(path, InteropArchiveManifest.FileName),
            InteropArchiveManifest.Describe(path, side, build, 4242, Now).Format());
        return path;
    }

    private IEnumerable<string> ArchiveFolders() =>
        Directory.GetDirectories(Archives).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal)!;

    // ------------------------------------------------------------------ first run

    [Fact]
    public void TheFirstRunOnABuildArchivesTheWholeInteropSet()
    {
        var result = InteropArchive.Ensure(Request());

        Assert.Equal(InteropArchiveStatus.Created, result.Status);
        Assert.True(result.HasArchive);
        Assert.Equal("interop-client-" + BuildA, result.FolderName);
        Assert.Equal(Path.Combine(Archives, "interop-client-" + BuildA), result.Path);
        Assert.Equal(3, result.FileCount);
        Assert.Equal(1, result.Retained);
        Assert.Empty(result.Removed);
        Assert.Empty(result.Notes);

        // Byte-for-byte, because "an assembly changed" is what the fingerprint already says and
        // "what moved inside it" is the only thing this folder is for.
        foreach (var file in Directory.GetFiles(Interop))
        {
            var copied = Path.Combine(result.Path, Path.GetFileName(file));
            Assert.True(File.Exists(copied), copied + " was not archived");
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(copied));
        }
    }

    [Fact]
    public void TheArchiveIsNamedForTheSideAndBuildSoTwoSidesNeverCollide()
    {
        Assert.Equal("interop-client-" + BuildA, InteropArchive.FolderNameFor("client", BuildA));
        Assert.Equal("interop-server-" + BuildA, InteropArchive.FolderNameFor("server", BuildA));

        InteropArchive.Ensure(Request(BuildA, "client"));
        InteropArchive.Ensure(Request(BuildA, "server"));

        Assert.Equal(
            new[] { "interop-client-" + BuildA, "interop-server-" + BuildA },
            ArchiveFolders());
        Assert.Single(InteropArchive.List(Archives, "client"));
        Assert.Single(InteropArchive.List(Archives, "server"));
    }

    // ------------------------------------------------------------------ idempotence

    [Fact]
    public void TheSameBuildIsNotArchivedASecondTime()
    {
        var first = InteropArchive.Ensure(Request());
        Assert.Equal(InteropArchiveStatus.Created, first.Status);

        // A mark only the first copy could carry. If the second run re-copied, the folder would
        // have been rebuilt from the live interop set and this would be back to "one". The same
        // length on purpose: since FUPD6 the archive carries a manifest, and a length that
        // disagreed with it would make this an Incomplete archive rather than an untouched one.
        var witness = Path.Combine(first.Path, "ProjectM.dll");
        File.WriteAllText(witness, "ONE");

        int copies = 0;
        var request = Request();
        request.CopyFile = (from, to) => { copies++; File.Copy(from, to, true); };
        var second = InteropArchive.Ensure(request);

        Assert.Equal(InteropArchiveStatus.AlreadyPresent, second.Status);
        Assert.True(second.HasArchive);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(0, copies);
        Assert.Equal("ONE", File.ReadAllText(witness));
        Assert.Single(InteropArchive.List(Archives, "client"));
    }

    [Fact]
    public void ANewBuildIdIsWhatTriggersANewArchive()
    {
        InteropArchive.Ensure(Request(BuildA));
        WriteInterop("ProjectM.dll", "regenerated in place by BepInEx after the update");
        var second = InteropArchive.Ensure(Request(BuildB));

        Assert.Equal(InteropArchiveStatus.Created, second.Status);
        Assert.Equal(2, InteropArchive.List(Archives, "client").Count);

        // The old build's copy still holds the OLD bytes. That is the entire point: the live
        // folder has been overwritten and only the archive remembers what was there.
        Assert.Equal("one", File.ReadAllText(
            Path.Combine(Archives, "interop-client-" + BuildA, "ProjectM.dll")));
        Assert.Equal("regenerated in place by BepInEx after the update", File.ReadAllText(
            Path.Combine(Archives, "interop-client-" + BuildB, "ProjectM.dll")));
    }

    // ------------------------------------------------------------------ retention

    [Fact]
    public void RetentionKeepsTheNewestThreeArchivesPerSide()
    {
        var builds = new[] { BuildA, BuildB, BuildC };
        for (int i = 0; i < builds.Length; i++)
        {
            var made = InteropArchive.Ensure(Request(builds[i]));
            Assert.Equal(InteropArchiveStatus.Created, made.Status);
            Age(made.Path, new DateTime(2026, 9, 1 + i, 0, 0, 0, DateTimeKind.Utc));
        }
        // Another side's archive must survive a client retention pass untouched.
        var server = InteropArchive.Ensure(Request(BuildA, "server"));
        Age(server.Path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var fourth = InteropArchive.Ensure(Request(BuildD));

        Assert.Equal(InteropArchiveStatus.Created, fourth.Status);
        Assert.Equal(InteropArchive.DefaultRetain, fourth.Retained);
        Assert.Equal(new[] { "interop-client-" + BuildA }, fourth.Removed);
        Assert.False(Directory.Exists(Path.Combine(Archives, "interop-client-" + BuildA)));
        foreach (var kept in new[] { BuildB, BuildC, BuildD })
            Assert.True(Directory.Exists(Path.Combine(Archives, "interop-client-" + kept)));
        Assert.True(Directory.Exists(server.Path));
    }

    [Fact]
    public void TheArchiveForTheBuildRunningNowIsNeverTheOneRetentionDeletes()
    {
        // A build reinstalled after a detour keeps an old folder timestamp, so by time alone it
        // would be the deletion candidate — while being the only set that describes the running
        // game. Retention pins it.
        foreach (var build in new[] { BuildB, BuildC, BuildD })
            Age(InteropArchive.Ensure(Request(build)).Path, new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc));
        var oldest = InteropArchive.Ensure(Request(BuildA));
        Age(oldest.Path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var again = InteropArchive.Ensure(Request(BuildA));

        Assert.Equal(InteropArchiveStatus.AlreadyPresent, again.Status);
        Assert.True(Directory.Exists(oldest.Path));
        Assert.DoesNotContain("interop-client-" + BuildA, again.Removed);
    }

    [Fact]
    public void TheRetentionCountIsClampedSoATypoCannotMeanKeepNothing()
    {
        Assert.Equal(InteropArchive.MinRetain, InteropArchive.ClampRetain(0));
        Assert.Equal(InteropArchive.MinRetain, InteropArchive.ClampRetain(-4));
        Assert.Equal(InteropArchive.MaxRetain, InteropArchive.ClampRetain(9999));
        Assert.Equal(5, InteropArchive.ClampRetain(5));
        Assert.Equal(InteropArchive.DefaultRetain, InteropArchive.ClampRetain("not a number"));
        Assert.Equal(7, InteropArchive.ClampRetain("7"));

        var request = Request(BuildB);
        request.Retain = 0;
        InteropArchive.Ensure(Request(BuildA));
        var second = InteropArchive.Ensure(request);

        // Clamped to one: the build now running survives, the previous one does not.
        Assert.Equal(1, second.Retained);
        Assert.Equal(new[] { "interop-client-" + BuildA }, second.Removed);
    }

    [Fact]
    public void TheRetentionCountComesFromTheConfigFileAndFallsBackToTheDefault()
    {
        var bepInEx = Path.Combine(_root, "BepInEx");
        Directory.CreateDirectory(Path.Combine(bepInEx, "config"));
        var cfg = Path.Combine(bepInEx, "config", InteropArchive.ConfigFileName);

        Assert.Equal(InteropArchive.DefaultRetain, InteropArchive.ReadRetainFromConfig(bepInEx));

        File.WriteAllText(cfg, string.Join("\n", new[]
        {
            "[Compat]",
            "InteropArchives = 9",       // right key, wrong section: ignored
            "",
            "[" + InteropArchive.ConfigSection + "]",
            "# " + InteropArchive.ConfigKey + " = 8",
            InteropArchive.ConfigKey + " = 5",
        }));
        Assert.Equal(5, InteropArchive.ReadRetainFromConfig(bepInEx));

        File.WriteAllText(cfg, "[" + InteropArchive.ConfigSection + "]\n"
                               + InteropArchive.ConfigKey + " = banana\n");
        Assert.Equal(InteropArchive.DefaultRetain, InteropArchive.ReadRetainFromConfig(bepInEx));

        Assert.Equal(InteropArchive.DefaultRetain,
            InteropArchive.ReadRetainFromConfig(Path.Combine(_root, "no-such-bepinex")));
    }

    // ------------------------------------------------------------------ atomicity

    [Fact]
    public void APartialCopyIsNeverLeftUnderTheFinalName()
    {
        int copied = 0;
        var request = Request();
        request.CopyFile = (from, to) =>
        {
            if (++copied == 2) throw new IOException("the disk went away mid-copy");
            File.Copy(from, to, overwrite: true);
        };

        var result = InteropArchive.Ensure(request);

        Assert.Equal(InteropArchiveStatus.Failed, result.Status);
        Assert.False(result.HasArchive);
        Assert.Equal("", result.FolderName);
        Assert.Contains("the disk went away mid-copy", result.Reason);

        // Nothing under the final name, and no half-copy left behind under any name either.
        Assert.False(Directory.Exists(Path.Combine(Archives, "interop-client-" + BuildA)));
        Assert.Empty(Directory.GetDirectories(Archives));
        Assert.Empty(InteropArchive.List(Archives, "client"));

        // And the next pass, with a working disk, still archives the build: the failure did not
        // mark it as done.
        var retry = InteropArchive.Ensure(Request());
        Assert.Equal(InteropArchiveStatus.Created, retry.Status);
        Assert.Equal(3, retry.FileCount);
        // Three assemblies plus the FUPD6 manifest that says they are all there.
        Assert.Equal(4, Directory.GetFiles(retry.Path).Length);
    }

    [Fact]
    public void AnAbandonedHalfCopyIsSweptByTheNextPassAndNeverCountedAsAnArchive()
    {
        // Exactly what a killed process leaves: the temp folder, populated, never renamed, with
        // the owner stamp of a process that is no longer there (job FUPD6).
        var abandoned = Temp(BuildA, pid: 4242, started: Now);
        Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "ProjectM.dll"), "half");

        Assert.Empty(InteropArchive.List(Archives, "client"));

        var request = Request(BuildB);
        request.ProcessIsAlive = _ => false;
        var result = InteropArchive.Ensure(request);

        Assert.Equal(InteropArchiveStatus.Created, result.Status);
        Assert.False(Directory.Exists(abandoned));
        Assert.Equal(new[] { "interop-client-" + BuildB }, ArchiveFolders());
    }

    [Fact]
    public void ACancelledPassLeavesNoArchiveAtAll()
    {
        using var cancel = new CancellationTokenSource();
        var request = Request();
        request.Cancellation = cancel.Token;
        request.CopyFile = (from, to) => { cancel.Cancel(); File.Copy(from, to, true); };

        var result = InteropArchive.Ensure(request);

        Assert.Equal(InteropArchiveStatus.Failed, result.Status);
        Assert.Contains("canceled", result.Reason);
        Assert.Empty(Directory.GetDirectories(Archives));
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public void ACopyThatWouldNotFitIsRefusedWithTheNumbersRatherThanFillingTheDisk()
    {
        var request = Request();
        request.MinimumFreeBytes = 512L * 1024 * 1024;
        request.FreeSpaceProbe = _ => 1024;

        var result = InteropArchive.Ensure(request);

        Assert.Equal(InteropArchiveStatus.RefusedDiskSpace, result.Status);
        Assert.False(result.HasArchive);
        Assert.Empty(Directory.GetDirectories(Archives));
        Assert.Contains("512.0 MB headroom", result.Reason);
        Assert.Contains("1.0 KB free", result.Reason);
        Assert.Contains("NOT archived, not enough disk", InteropArchive.DescribeStatus(result));
        Assert.Contains("no --old input", InteropArchive.DescribeStatus(result));
    }

    [Fact]
    public void FreeSpaceThatCannotBeReadIsNotARefusal()
    {
        var request = Request();
        request.MinimumFreeBytes = long.MaxValue / 2;
        request.FreeSpaceProbe = _ => throw new IOException("network path, no idea");

        var result = InteropArchive.Ensure(request);

        Assert.Equal(InteropArchiveStatus.Created, result.Status);
        Assert.Contains(result.Notes, n => n.Contains("could not read free space"));
    }

    [Fact]
    public void NothingToArchiveIsAStatusAndNeverAnException()
    {
        var noBuild = Request();
        noBuild.Build = "";
        Assert.Equal(InteropArchiveStatus.NotPossible, InteropArchive.Ensure(noBuild).Status);

        var noInterop = Request();
        noInterop.InteropDirectory = Path.Combine(_root, "not-here");
        Assert.Equal(InteropArchiveStatus.NotPossible, InteropArchive.Ensure(noInterop).Status);

        var emptyInterop = Request();
        emptyInterop.InteropDirectory = Path.Combine(_root, "empty");
        Directory.CreateDirectory(emptyInterop.InteropDirectory);
        Assert.Equal(InteropArchiveStatus.NotPossible, InteropArchive.Ensure(emptyInterop).Status);

        var noRoot = Request();
        noRoot.ArchiveRoot = "";
        Assert.Equal(InteropArchiveStatus.NotPossible, InteropArchive.Ensure(noRoot).Status);

        Assert.Equal(InteropArchiveStatus.NotPossible, InteropArchive.Ensure(null!).Status);
        Assert.Empty(Directory.GetDirectories(Archives));
    }

    // ------------------------------------------------------------------ the operator's command

    [Fact]
    public void TheDriftMapCommandDiffsTheNewestEARLIERArchiveAgainstTheLiveInteropFolder()
    {
        Age(InteropArchive.Ensure(Request(BuildA)).Path, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        Age(InteropArchive.Ensure(Request(BuildB)).Path, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        Age(InteropArchive.Ensure(Request(BuildC)).Path, new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc));

        var command = InteropArchive.FormatDriftMapCommand(Archives, "client", BuildC, Interop);

        Assert.StartsWith("dotnet run --project tools/driftmap -- --old ", command);
        // The previous build, not the running one and not the oldest.
        Assert.Contains("--old " + Path.Combine(Archives, "interop-client-" + BuildB), command);
        Assert.DoesNotContain("interop-client-" + BuildC + " ", command);
        Assert.Contains("--new " + Interop, command);
        Assert.Contains("--side client --build " + BuildC, command);
        Assert.Contains("--out " + Path.Combine(Archives, "driftmap-" + BuildC + ".json"), command);
    }

    [Fact]
    public void WithOnlyTheRunningBuildArchivedTheCommandSaysSoInsteadOfInventingAFolder()
    {
        InteropArchive.Ensure(Request(BuildA));

        var command = InteropArchive.FormatDriftMapCommand(Archives, "client", BuildA, Interop);

        Assert.DoesNotContain("--old", command);
        Assert.Contains("no earlier interop archive", command);
        Assert.Contains("baseline", command);
    }

    [Fact]
    public void ThePathsInTheCommandAreQuotedWhenTheyContainSpaces()
    {
        var spaced = Path.Combine(_root, "Program Files", "addresslib");
        PublishFolder(spaced, "client", BuildA);

        var command = InteropArchive.FormatDriftMapCommand(
            spaced, "client", BuildB, @"C:\Program Files (x86)\Steam\VRising\BepInEx\interop");

        Assert.Contains("--old \"" + Path.Combine(spaced, "interop-client-" + BuildA) + "\"", command);
        Assert.Contains("--new \"C:\\Program Files (x86)\\Steam\\VRising\\BepInEx\\interop\"", command);
    }

    [Fact]
    public void ListIsNewestFirstAndReadsTheBuildIdBackOutOfTheFolderName()
    {
        Age(InteropArchive.Ensure(Request(BuildA)).Path, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        Age(InteropArchive.Ensure(Request(BuildC)).Path, new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc));
        Age(InteropArchive.Ensure(Request(BuildB)).Path, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        var list = InteropArchive.List(Archives, "client");

        Assert.Equal(new[] { BuildC, BuildB, BuildA }, list.Select(e => e.Build).ToArray());
        Assert.All(list, e => Assert.Equal("client", e.Side));
        Assert.Equal(Path.Combine(Archives, "interop-client-" + BuildC), list[0].Path);
        Assert.Empty(InteropArchive.List(Path.Combine(_root, "nowhere"), "client"));
    }

    // ------------------------------------------------------------------ the report row

    [Fact]
    public void TheReportSaysWhetherThisBuildIsStillDiffable()
    {
        var created = new InteropArchiveResult(
            InteropArchiveStatus.Created, "interop-client-" + BuildB,
            Path.Combine(Archives, "interop-client-" + BuildB), 214, 187_000_000,
            new[] { "interop-client-" + BuildA }, "", Array.Empty<string>(), 3);

        var text = InteropArchive.DescribeStatus(created);
        Assert.Contains("interop-client-" + BuildB + " created", text);
        Assert.Contains("214 files", text);
        Assert.Contains("178.3 MB", text);
        Assert.Contains("keeping 3", text);
        Assert.Contains("removed interop-client-" + BuildA, text);

        Assert.Contains("already archived", InteropArchive.DescribeStatus(new InteropArchiveResult(
            InteropArchiveStatus.AlreadyPresent, "interop-client-" + BuildB, "", 214, 0,
            Array.Empty<string>(), "", Array.Empty<string>(), 3)));
        Assert.Equal("off", InteropArchive.DescribeStatus(
            InteropArchiveResult.None(InteropArchiveStatus.Disabled)));
        Assert.Equal("not run", InteropArchive.DescribeStatus(null));
    }

    [Fact]
    public void TheArchiveRowIsInTheOneResilienceBlockAndNotASecondOne()
    {
        var fingerprint = new BuildFingerprint(
            "client", BuildA, "2026-09-05T00:00:00Z", "0.1.0.0",
            @"C:\VRising\GameAssembly.dll", 1, "sha", Interop,
            Array.Empty<AssemblyFingerprint>(), true,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<MemberBinding>());
        var diff = ResilienceDiff.Compare(null, fingerprint);
        var archive = new InteropArchiveResult(
            InteropArchiveStatus.Created, "interop-client-" + BuildA, "", 3, 12,
            Array.Empty<string>(), "", Array.Empty<string>(), 1);

        var block = ResilienceReport.Format(diff, fingerprint, archive);

        var rows = block.Split('\n').Where(l => l.TrimStart().StartsWith("archive")).ToArray();
        Assert.Single(rows);
        Assert.Contains("interop-client-" + BuildA + " created", rows[0]);
        // Placed between the interop line and the address-library line, so the block reads
        // "here is the interop set, here is the copy of it, here is the table".
        // Row labels, not free text: the temp folder these tests run in has "archive" in its
        // own path, and that path is printed on the interop row.
        var labels = block.Split('\n').Select(l => l.Trim().Split(' ')[0]).ToList();
        Assert.True(labels.IndexOf("interop") < labels.IndexOf("archive"));
        Assert.True(labels.IndexOf("archive") < labels.IndexOf("addresslib"));

        // And no row at all when the step is off: the block is exactly what it always was.
        Assert.DoesNotContain("archive",
            ResilienceReport.Format(diff, fingerprint).Split('\n').Select(l => l.Trim().Split(' ')[0]));
    }

    [Fact]
    public void SizesReadTheWayAnOperatorReadsADisk()
    {
        Assert.Equal("512 B", InteropArchive.Size(512));
        Assert.Equal("1.0 KB", InteropArchive.Size(1024));
        Assert.Equal("1.0 MB", InteropArchive.Size(1024 * 1024));
        Assert.Equal("1.50 GB", InteropArchive.Size(1536L * 1024 * 1024));
        Assert.Equal("unknown", InteropArchive.Size(-1));
    }

    // ------------------------------------------------------------------ the whole pass

    [Fact]
    public void ThePassArchivesBeforeItStoresTheFingerprintAndTheFingerprintNamesTheArchive()
    {
        var gameAssembly = Path.Combine(_root, "GameAssembly.dll");
        File.WriteAllBytes(gameAssembly, Encoding.ASCII.GetBytes("MZ not really a PE, but a stable build id"));
        var store = Path.Combine(_root, "store");

        var options = new ResilienceOptions
        {
            Side = "client",
            GameAssemblyPath = gameAssembly,
            InteropDirectory = Interop,
            AddressTableDirectory = store,
            StoreDirectory = store,
            ScanMembers = false,
            UseLengthAndTimeFastPath = false,
            WriteStore = true,
            ArchiveInterop = true,
            ArchiveDirectory = Archives,
            ArchiveMinimumFreeBytes = 0,
        };

        var result = ResilienceCheck.Run(options);

        Assert.NotNull(result.Archive);
        Assert.Equal(InteropArchiveStatus.Created, result.Archive!.Status);
        Assert.Equal("interop-client-" + result.Current.Build, result.Archive.FolderName);
        Assert.True(Directory.Exists(result.Archive.Path));

        // The stored fingerprint names its own archive, so an operator reading the JSON later
        // knows whether that build can still be diffed.
        Assert.Equal(result.Archive.FolderName, result.Current.InteropArchiveFolder);
        Assert.True(BuildFingerprint.TryLoadFile(result.StoredPath, out var reloaded, out _));
        Assert.Equal(result.Archive.FolderName, reloaded!.InteropArchiveFolder);
        Assert.Contains("archive", result.Report);

        // Second pass, same build: no re-copy, and the fingerprint still names the archive.
        var again = ResilienceCheck.Run(options);
        Assert.Equal(InteropArchiveStatus.AlreadyPresent, again.Archive!.Status);
        Assert.Equal(result.Archive.FolderName, again.Current.InteropArchiveFolder);
    }

    [Fact]
    public void ThePassDoesNotArchiveUnlessItWasAskedTo()
    {
        var gameAssembly = Path.Combine(_root, "GameAssembly.dll");
        File.WriteAllBytes(gameAssembly, new byte[] { 1, 2, 3, 4 });
        var options = new ResilienceOptions
        {
            Side = "client",
            GameAssemblyPath = gameAssembly,
            InteropDirectory = Interop,
            StoreDirectory = Path.Combine(_root, "store"),
            ScanMembers = false,
            WriteStore = false,
        };

        var result = ResilienceCheck.Run(options);

        Assert.Equal(InteropArchiveStatus.Disabled, result.Archive!.Status);
        Assert.Equal("", result.Current.InteropArchiveFolder);
        Assert.Empty(Directory.GetDirectories(Archives));
    }

    [Fact]
    public void AFingerprintRoundTripsItsArchiveName()
    {
        var fingerprint = new BuildFingerprint(
            "server", BuildB, "2026-09-05T00:00:00Z", "0.1.0.0",
            @"C:\VRising\GameAssembly.dll", 10, "sha", Interop,
            new[] { new AssemblyFingerprint("ProjectM.dll", "1.0", 1, "aa", "t") }, true,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<MemberBinding>(),
            "", "", "interop-server-" + BuildB);

        Assert.True(BuildFingerprint.TryParse(fingerprint.ToJson(), out var parsed, out _));
        Assert.Equal("interop-server-" + BuildB, parsed!.InteropArchiveFolder);

        // A fingerprint written before this field existed reads as "no archive", not as a crash.
        Assert.True(BuildFingerprint.TryParse("{\"side\":\"client\",\"build\":\"" + BuildA + "\"}",
            out var old, out _));
        Assert.Equal("", old!.InteropArchiveFolder);
        Assert.Equal("interop-client-" + BuildA, old.WithInteropArchive("interop-client-" + BuildA).InteropArchiveFolder);
    }

    // ------------------------------------------------------- FUPD6: who owns a temp copy

    [Fact]
    public void TheTempFolderNameCarriesTheOwningProcessAndItsStartTime()
    {
        // A build id is full of dashes, so the stamp has to be read from the last marker, not by
        // splitting the whole name. This is the property the sweep's ownership rule rests on.
        var name = InteropArchive.TemporaryFolderName("client", BuildA, 4242, Now);

        Assert.StartsWith(InteropArchive.TempPrefix + "client-" + BuildA, name);
        Assert.Contains(InteropArchive.TempStampMarker + "4242-20260905120000-", name);
        Assert.True(InteropArchive.TryParseTemporaryName(name, out int pid, out var started));
        Assert.Equal(4242, pid);
        Assert.Equal(Now, started);

        // A retention glob can never see it, which is why the dot prefix is there.
        Assert.DoesNotContain(name, ArchiveFolders());

        // And a name from before FUPD6, or from anything else, has no owner to ask.
        Assert.False(InteropArchive.TryParseTemporaryName(
            InteropArchive.TempPrefix + "client-" + BuildA + ".tmp-deadbeef", out _, out _));
        Assert.False(InteropArchive.TryParseTemporaryName(
            InteropArchive.TempPrefix + "client-" + BuildA + InteropArchive.TempStampMarker + "x-y-z",
            out _, out _));
        Assert.False(InteropArchive.TryParseTemporaryName(null, out _, out _));
    }

    [Fact]
    public void TheSweepLeavesAnotherLiveProcessInFlightCopyAlone()
    {
        // The whole of R53 finding 5: two V Rising processes out of the same folder share this
        // archive root, the same side and the same build id. Deleting the other one's copy while
        // it is being written is how a truncated set gets published under the final name.
        var inFlight = Temp(BuildA, pid: 4242, started: Now.AddMinutes(-1));
        Directory.CreateDirectory(inFlight);
        File.WriteAllText(Path.Combine(inFlight, "ProjectM.dll"), "half");

        var request = Request(BuildB);
        request.UtcNow = () => Now;
        request.ProcessIsAlive = pid => pid == 4242;

        var result = InteropArchive.Ensure(request);

        Assert.Equal(InteropArchiveStatus.Created, result.Status);
        Assert.True(Directory.Exists(inFlight));
        Assert.Equal("half", File.ReadAllText(Path.Combine(inFlight, "ProjectM.dll")));
        Assert.Contains(result.Notes, n => n.Contains("left") && n.Contains("4242"));
    }

    [Fact]
    public void ATempCopyOlderThanTheAgeGuardIsSweptEvenWhenItsProcessLooksAlive()
    {
        // A recycled pid, or a probe that cannot answer, must not pin a folder forever.
        var stale = Temp(BuildA, pid: 4242, started: Now.AddHours(-7));
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "ProjectM.dll"), "half");

        var request = Request(BuildB);
        request.UtcNow = () => Now;
        request.ProcessIsAlive = _ => true;

        var result = InteropArchive.Ensure(request);

        Assert.Equal(InteropArchiveStatus.Created, result.Status);
        Assert.False(Directory.Exists(stale));
        Assert.Contains(result.Notes, n => n.Contains("older than"));
    }

    [Fact]
    public void AProbeThatCannotAnswerReadsAsAliveSoNothingLiveIsDeleted()
    {
        var inFlight = Temp(BuildA, pid: 4242, started: Now);
        Directory.CreateDirectory(inFlight);

        var request = Request(BuildB);
        request.UtcNow = () => Now;
        request.ProcessIsAlive = _ => throw new InvalidOperationException("the OS would not say");

        Assert.Equal(InteropArchiveStatus.Created, InteropArchive.Ensure(request).Status);
        Assert.True(Directory.Exists(inFlight));
    }

    // -------------------------------------- FUPD7: a pid is not an owner (R54 finding 6)

    [Fact]
    public void APidThatWasRecycledOntoAnotherProcessIsNotTheOwner()
    {
        // The developer loop this repository runs: test-server.ps1 stop kills VRisingServer.exe
        // mid-archive, Windows hands 4242 to a dotnet.exe or a grok.exe job within minutes, and
        // the pid probe alone reads that as "the owner is still writing it". Three of those in an
        // afternoon left half a gigabyte behind, with a log line for each saying it was deliberate.
        var stranded = Temp(BuildA, pid: 4242, started: Now.AddMinutes(-10));
        Directory.CreateDirectory(stranded);
        File.WriteAllText(Path.Combine(stranded, "ProjectM.dll"), "half");

        var request = Request(BuildB);
        request.UtcNow = () => Now;
        request.ProcessIsAlive = _ => true;
        // Alive, same pid — and started after the copy was stamped, which the real owner cannot
        // have done: it had to exist before it could write its own folder name.
        request.ProcessStartUtc = _ => Now.AddMinutes(-2);

        var result = InteropArchive.Ensure(request);

        Assert.Equal(InteropArchiveStatus.Created, result.Status);
        Assert.False(Directory.Exists(stranded));
        Assert.Contains(result.Notes, n => n.Contains("recycled pid") && n.Contains("4242"));
        // Swept, so it is not disk anybody is still paying for.
        Assert.Equal(0, result.TemporaryFolders);
    }

    [Fact]
    public void AnOwnerThatStartedBeforeItsOwnStampKeepsItsCopy()
    {
        var inFlight = Temp(BuildA, pid: 4242, started: Now.AddMinutes(-1));
        Directory.CreateDirectory(inFlight);
        File.WriteAllText(Path.Combine(inFlight, "ProjectM.dll"), "half");

        var request = Request(BuildB);
        request.UtcNow = () => Now;
        request.ProcessIsAlive = _ => true;
        request.ProcessStartUtc = _ => Now.AddHours(-3);

        var result = InteropArchive.Ensure(request);

        Assert.Equal(InteropArchiveStatus.Created, result.Status);
        Assert.True(Directory.Exists(inFlight));
        Assert.Contains(result.Notes, n => n.Contains("left") && n.Contains("4242"));
    }

    [Fact]
    public void AStartTimeInsideTheGraceIsStillTheOwner()
    {
        // The stamp is yyyyMMddHHmmss, truncated, so a process that started inside the same second
        // reads as up to a second late. The grace absorbs that; nothing beyond it is absorbed.
        var inFlight = Temp(BuildA, pid: 4242, started: Now);
        Directory.CreateDirectory(inFlight);

        var request = Request(BuildB);
        request.UtcNow = () => Now;
        request.ProcessIsAlive = _ => true;
        request.ProcessStartUtc = _ => Now.Add(InteropArchive.ProcessStartGrace);

        Assert.Equal(InteropArchiveStatus.Created, InteropArchive.Ensure(request).Status);
        Assert.True(Directory.Exists(inFlight));
    }

    [Fact]
    public void AStartTimeTheOsWillNotGiveIsNotEvidenceOfAnything()
    {
        // Denied, or a process that exited between the two probes. The sweep fails closed: deleting
        // a live process's in-flight copy is the failure this whole rule exists to stop.
        var inFlight = Temp(BuildA, pid: 4242, started: Now.AddMinutes(-1));
        Directory.CreateDirectory(inFlight);

        var request = Request(BuildB);
        request.UtcNow = () => Now;
        request.ProcessIsAlive = _ => true;
        request.ProcessStartUtc = _ => null;

        Assert.Equal(InteropArchiveStatus.Created, InteropArchive.Ensure(request).Status);
        Assert.True(Directory.Exists(inFlight));
    }

    [Fact]
    public void TheAgeCapIsMatchedToACopyThatTakesSecondsNotToTheCalendar()
    {
        // Evidence 193: the real copy is 172 files, 179.8 MB, seconds. Six hours was a guard on the
        // calendar. The cap now only has to cover an unstamped folder and a start time the OS will
        // not give, because the recycled pid is caught above.
        Assert.Equal(TimeSpan.FromMinutes(30), InteropArchive.DefaultMaximumTemporaryAge);
        Assert.Equal(TimeSpan.FromSeconds(5), InteropArchive.ProcessStartGrace);

        var stale = Temp(BuildA, pid: 4242, started: Now.AddMinutes(-31));
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "ProjectM.dll"), "half");

        var request = Request(BuildB);
        request.UtcNow = () => Now;
        request.ProcessIsAlive = _ => true;
        // Even a start time that checks out does not save it past the cap.
        request.ProcessStartUtc = _ => Now.AddHours(-2);

        var result = InteropArchive.Ensure(request);

        Assert.Equal(InteropArchiveStatus.Created, result.Status);
        Assert.False(Directory.Exists(stale));
        // The number has to print in the units it is set in: "0h" would be a lie.
        Assert.Contains(result.Notes, n => n.Contains("older than 30 min"));

        // One minute under the cap and it is still somebody's copy.
        var young = Temp(BuildC, pid: 4243, started: Now.AddMinutes(-29));
        Directory.CreateDirectory(young);
        var second = Request(BuildD);
        second.UtcNow = () => Now;
        second.ProcessIsAlive = _ => true;
        second.ProcessStartUtc = _ => Now.AddHours(-2);
        Assert.Equal(InteropArchiveStatus.Created, InteropArchive.Ensure(second).Status);
        Assert.True(Directory.Exists(young));
    }

    [Fact]
    public void ATempTheSweepCouldNotReclaimIsCountedAndPrinted()
    {
        // Retention counts interop-* and can never match .interop-*, so before FUPD7 a surviving
        // temp was disk outside the "~540 MB per game root" ceiling the report advertises, with
        // nothing naming it. It is the operator's disk either way, so it is on the row.
        var inFlight = Temp(BuildA, pid: 4242, started: Now.AddMinutes(-1));
        Directory.CreateDirectory(inFlight);
        File.WriteAllText(Path.Combine(inFlight, "ProjectM.dll"), new string('x', 4096));

        var request = Request(BuildB);
        request.UtcNow = () => Now;
        request.ProcessIsAlive = _ => true;

        var result = InteropArchive.Ensure(request);

        Assert.Equal(InteropArchiveStatus.Created, result.Status);
        Assert.Equal(1, result.TemporaryFolders);
        Assert.Equal(4096, result.TemporaryBytes);

        var row = InteropArchive.DescribeStatus(result);
        Assert.Contains("plus 1 unfinished copy 4.0 KB not yet reclaimed", row);

        // A pass with nothing left over says nothing, so the row stays boring on every ordinary start.
        Assert.DoesNotContain("not yet reclaimed", InteropArchive.DescribeStatus(
            new InteropArchiveResult(InteropArchiveStatus.Created, "interop-client-" + BuildB, "",
                3, 9, Array.Empty<string>(), "", Array.Empty<string>(), 1)));
    }

    [Fact]
    public void ATempCopyWithNoOwnerStampIsJudgedByItsAgeAlone()
    {
        var fresh = Path.Combine(Archives, InteropArchive.TempPrefix + "client-" + BuildA + ".tmp-deadbeef");
        var ancient = Path.Combine(Archives, InteropArchive.TempPrefix + "client-" + BuildC + ".tmp-cafebabe");
        Directory.CreateDirectory(fresh);
        Directory.CreateDirectory(ancient);
        Age(fresh, Now.AddMinutes(-5));
        Age(ancient, Now.AddDays(-3));

        var request = Request(BuildB);
        request.UtcNow = () => Now;

        Assert.Equal(InteropArchiveStatus.Created, InteropArchive.Ensure(request).Status);
        Assert.True(Directory.Exists(fresh));
        Assert.False(Directory.Exists(ancient));
    }

    [Fact]
    public void ACopyWhoseTempFolderIsDeletedUnderneathItStopsInsteadOfRebuildingTheTree()
    {
        // FUPD4's loop called CreateDirectory(parent) per file, so a sweep that removed the tree
        // half way through was silently repaired file by file and the hole was published.
        var request = Request();
        int copied = 0;
        string? temp = null;
        request.CopyFile = (from, to) =>
        {
            temp ??= Path.GetDirectoryName(to);
            File.Copy(from, to, overwrite: true);
            if (++copied == 1) Directory.Delete(temp!, recursive: true);
        };

        var result = InteropArchive.Ensure(request);

        Assert.Equal(InteropArchiveStatus.Failed, result.Status);
        Assert.Contains("removed while it was being written", result.Reason);
        Assert.Empty(Directory.GetDirectories(Archives));
    }

    [Fact]
    public void ACopyThatDoesNotAddUpToTheSourceIsDiscardedRatherThanPublished()
    {
        // The publish-time count. A copy that "succeeded" but is short never gets a final name.
        var request = Request();
        request.CopyFile = (from, to) => File.WriteAllText(to, "short");

        var result = InteropArchive.Ensure(request);

        Assert.Equal(InteropArchiveStatus.Failed, result.Status);
        Assert.Contains("does not match the source", result.Reason);
        Assert.Contains("discarded rather than published", result.Reason);
        Assert.Empty(Directory.GetDirectories(Archives));
        Assert.Empty(InteropArchive.List(Archives, "client"));
    }

    // ------------------------------------------------------- FUPD6: the manifest

    [Fact]
    public void ThePublishedArchiveListsEveryFileWithItsLengthAndSha256()
    {
        var result = InteropArchive.Ensure(Request());

        Assert.Equal(InteropArchiveStatus.Created, result.Status);
        Assert.True(InteropArchiveManifest.TryLoad(result.Path, out var manifest, out var error), error);
        Assert.Equal(InteropArchiveManifest.CurrentVersion, manifest!.Version);
        Assert.Equal("client", manifest.Side);
        Assert.Equal(BuildA, manifest.Build);
        Assert.Equal(3, manifest.Files.Count);
        Assert.Equal(new[] { "ProjectM.dll", "Stunlock.Core.dll", "Unity.Entities.dll" },
            manifest.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        var one = manifest.Files.Single(f => f.Path == "ProjectM.dll");
        Assert.Equal(3, one.Length);
        Assert.Equal(64, one.Sha256.Length);
        Assert.True(manifest.Verify(result.Path, out var why), why);
        Assert.True(InteropArchive.VerifyArchive(result.Path, out why), why);

        // The manifest is not one of the archived files: the counts an operator reads stay the
        // counts of the interop set.
        Assert.Equal(3, result.FileCount);
        Assert.Equal(InteropArchiveStatus.AlreadyPresent, InteropArchive.Ensure(Request()).Status);
        Assert.Equal(3, InteropArchive.Ensure(Request()).FileCount);
    }

    [Fact]
    public void AnArchiveWhoseManifestDisagreesWithItsFilesIsIncompleteAndNeverOfferedAsOld()
    {
        var created = InteropArchive.Ensure(Request());
        Assert.Equal(InteropArchiveStatus.Created, created.Status);
        // Whatever ate it - a lost sweep race, a half-finished delete, a bad disk.
        File.Delete(Path.Combine(created.Path, "Unity.Entities.dll"));

        var again = InteropArchive.Ensure(Request());

        Assert.Equal(InteropArchiveStatus.Incomplete, again.Status);
        Assert.False(again.HasArchive);
        Assert.Contains("missing Unity.Entities.dll", again.Reason);
        Assert.Contains("INCOMPLETE", InteropArchive.DescribeStatus(again));

        var entry = InteropArchive.List(Archives, "client").Single();
        Assert.False(entry.Complete);
        Assert.Contains("missing Unity.Entities.dll", entry.IncompleteReason);

        // And driftmap is told there is nothing safe to diff against rather than handed it.
        var command = InteropArchive.FormatDriftMapCommand(Archives, "client", BuildB, Interop);
        Assert.Contains("INCOMPLETE", command);
        Assert.DoesNotContain("dotnet run", command);
    }

    [Fact]
    public void AnArchiveWithNoManifestThatStillMatchesTheLiveSetIsDescribedRatherThanCondemned()
    {
        // A FUPD4-era archive. It cannot be recreated - the interop set it copied is the one
        // still on disk only until the next game update - so a missing manifest that agrees with
        // the live folder is written, not thrown away.
        var created = InteropArchive.Ensure(Request());
        File.Delete(Path.Combine(created.Path, InteropArchiveManifest.FileName));
        Assert.False(InteropArchive.VerifyArchive(created.Path, out _));

        var again = InteropArchive.Ensure(Request());

        Assert.Equal(InteropArchiveStatus.AlreadyPresent, again.Status);
        Assert.True(again.HasArchive);
        Assert.Contains(again.Notes, n => n.Contains("predates manifests"));
        Assert.True(InteropArchive.VerifyArchive(created.Path, out var why), why);
    }

    [Fact]
    public void AnArchiveWithNoManifestAndTheWrongContentsStaysIncomplete()
    {
        var created = InteropArchive.Ensure(Request());
        File.Delete(Path.Combine(created.Path, InteropArchiveManifest.FileName));
        File.Delete(Path.Combine(created.Path, "Stunlock.Core.dll"));

        var again = InteropArchive.Ensure(Request());

        Assert.Equal(InteropArchiveStatus.Incomplete, again.Status);
        Assert.Contains("no " + InteropArchiveManifest.FileName, again.Reason);
        Assert.Contains("2 files", again.Reason);
        Assert.False(File.Exists(Path.Combine(created.Path, InteropArchiveManifest.FileName)));
    }

    [Fact]
    public void TheDriftMapCommandSkipsAnIncompleteArchiveAndTakesTheNextGoodOne()
    {
        var older = InteropArchive.Ensure(Request(BuildA));
        Age(older.Path, DateTime.UtcNow.AddDays(-2));
        var newer = InteropArchive.Ensure(Request(BuildB));
        Age(newer.Path, DateTime.UtcNow.AddDays(-1));
        File.Delete(Path.Combine(newer.Path, "ProjectM.dll"));

        var command = InteropArchive.FormatDriftMapCommand(Archives, "client", BuildC, Interop);

        Assert.Contains("--old", command);
        Assert.Contains("interop-client-" + BuildA, command);
        Assert.DoesNotContain("interop-client-" + BuildB, command);
    }

    [Fact]
    public void TheManifestRoundTripsAndRefusesJunkRatherThanGuessing()
    {
        var manifest = new InteropArchiveManifest(
            1, "server", BuildB, "2026-09-05T12:00:00Z", 4242,
            new[]
            {
                new InteropArchiveFile("ProjectM.dll", 3, new string('a', 64)),
                new InteropArchiveFile("sub/dir/Unity Entities.dll", 7, new string('b', 64)),
            });

        Assert.True(InteropArchiveManifest.TryParse(manifest.Format(), out var parsed, out var error), error);
        Assert.Equal("server", parsed!.Side);
        Assert.Equal(BuildB, parsed.Build);
        Assert.Equal(4242, parsed.Pid);
        Assert.Equal(10, parsed.Bytes);
        // A path with a space in it survives, because the length is the second token, not the last.
        Assert.Equal("sub/dir/Unity Entities.dll", parsed.Files[1].Path);
        Assert.Equal(7, parsed.Files[1].Length);

        Assert.False(InteropArchiveManifest.TryParse("", out _, out error));
        Assert.Contains("empty", error);
        Assert.False(InteropArchiveManifest.TryParse("something else 1\n--\n", out _, out error));
        Assert.Contains(InteropArchiveManifest.Magic, error);
        Assert.False(InteropArchiveManifest.TryParse(
            InteropArchiveManifest.Magic + " 99\n--\n", out _, out error));
        Assert.Contains("newer than this Core", error);
        Assert.False(InteropArchiveManifest.TryParse(
            InteropArchiveManifest.Magic + " 1\nside=client\n", out _, out error));
        Assert.Contains("no '--' line", error);
        Assert.False(InteropArchiveManifest.TryParse(
            InteropArchiveManifest.Magic + " 1\nfiles=2\n--\naa 3 ProjectM.dll\n", out _, out error));
        Assert.Contains("says 2 files", error);
        Assert.False(InteropArchiveManifest.TryParse(
            InteropArchiveManifest.Magic + " 1\nbytes=99\n--\naa 3 ProjectM.dll\n", out _, out error));
        Assert.Contains("says 99 bytes", error);
        Assert.False(InteropArchiveManifest.TryLoad(Archives, out _, out error));
        Assert.Contains("no " + InteropArchiveManifest.FileName, error);
    }

    [Fact]
    public void AVerifyFailureNamesTheFileAndTheNumbers()
    {
        var created = InteropArchive.Ensure(Request());
        Assert.True(InteropArchiveManifest.TryLoad(created.Path, out var manifest, out _));

        File.WriteAllText(Path.Combine(created.Path, "ProjectM.dll"), "much longer than three");
        Assert.False(manifest!.Verify(created.Path, out var why));
        Assert.Contains("ProjectM.dll is 22 bytes", why);

        File.WriteAllText(Path.Combine(created.Path, "Extra.dll"), "one");
        File.WriteAllText(Path.Combine(created.Path, "ProjectM.dll"), "one");
        Assert.False(manifest.Verify(created.Path, out why));
        Assert.Contains("the folder holds 4 files", why);

        Directory.Delete(created.Path, recursive: true);
        Assert.False(manifest.Verify(created.Path, out why));
        Assert.Contains("folder is gone", why);
    }

    [Fact]
    public void TheOffRowSaysWhyRatherThanJustOff()
    {
        Assert.Equal("off", InteropArchive.DescribeStatus(
            InteropArchiveResult.None(InteropArchiveStatus.Disabled)));
        Assert.Equal("off - " + InteropArchivePolicy.ReasonDefaultOff, InteropArchive.DescribeStatus(
            InteropArchiveResult.None(InteropArchiveStatus.Disabled, InteropArchivePolicy.ReasonDefaultOff)));
    }
}
