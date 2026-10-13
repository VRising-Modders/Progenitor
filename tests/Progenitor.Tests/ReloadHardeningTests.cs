using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Boot = Progenitor.Bootstrap;

namespace Progenitor.Tests;

/// <summary>
/// Job FRLD/FRLD2: the reload-hardening half that is not a pure parse. R30's test-quality
/// note was that every test in this file used to read a <c>.cs</c> / <c>.ps1</c> / <c>.md</c>
/// off disk and assert on substrings — a shape that breaks on a renamed local and passes on
/// a reordered gate. These drive behavior instead:
///
/// <list type="bullet">
///   <item>the promote gate runs against a counting fake loader, so "the load happened
///     first" is a failing assertion rather than an index comparison in source text;</item>
///   <item>the tooling tests execute the production PowerShell functions on temp files;</item>
///   <item>the two remaining source reads are the two real source invariants — the mirrored
///     policy copies, and the store call sites that cannot be compiled here — and each says
///     "source pin" in its own assertion message.</item>
/// </list>
/// </summary>
public class ReloadHardeningTests
{
    /// <summary>A stand-in for the load-and-reflect step, counting how many times it ran.</summary>
    private sealed class CountingImageInspect
    {
        public int Loads;
        public byte[]? LastBytes;
        public string Error = "";

        public string Inspect(byte[] bytes)
        {
            Loads++;
            LastBytes = bytes;
            return Error;
        }
    }

    private static Func<string, string, Boot.AllowDecision> ListFrom(string? text)
    {
        var list = Boot.ReloadAllowPolicy.Parse(text);
        return (name, hash) => list.Decide(name, hash);
    }

    /// <summary>
    /// R30 finding 1. <c>SelfUpdate.PromoteCoreFile</c> is the fifth automatic code-loading
    /// path: it read <c>Progenitor.dll.next</c> and handed the bytes to
    /// <c>CoreLoader.TryValidateCoreImage</c>, which <c>LoadFromStream</c>s them and reads a
    /// static field off the image (running its static constructor) — before the allowlist was
    /// consulted, on first load and at the head of every <c>RequestReload</c>, whatever
    /// <c>Reload.WatchCore</c> says.
    ///
    /// The fix put the order inside <c>TryAcceptStagedCoreBytes</c>, so this test can assert
    /// it the only way that cannot be faked: the inspect step is a fake that counts, and an
    /// unlisted image must leave that count at zero. Move the gate below the load and the
    /// count becomes 1 and the test fails.
    /// </summary>
    [Fact]
    public void An_unlisted_staged_core_is_refused_before_anything_loads_or_reflects_on_it()
    {
        byte[] staged = Encoding.UTF8.GetBytes("staged Core image the operator never listed");
        string stagedHash = Boot.ReloadAllowPolicy.Sha256Hex(staged);
        string otherHash = Boot.ReloadAllowPolicy.Sha256Hex(new byte[] { 1, 2, 3 });

        // An active allowlist that carries a DIFFERENT build of the same file name.
        var listed = ListFrom(Boot.ReloadAllowPolicy.FormatSumsLine(otherHash, Boot.CoreLayout.FileName));
        var inspect = new CountingImageInspect();

        bool ok = Boot.CoreReloadPolicy.TryAcceptStagedCoreBytes(
            Boot.CoreLayout.FileName, staged, listed, inspect.Inspect, out var decision, out string error);

        Assert.False(ok);
        Assert.Equal(0, inspect.Loads); // the whole finding: nothing may load before the verdict
        Assert.Equal(Boot.AllowDecision.Unlisted, decision);
        Assert.Equal(
            Boot.ReloadAllowPolicy.FormatRefusedCore(
                Boot.CoreLayout.FileName, stagedHash, staged.LongLength, Boot.AllowDecision.Unlisted),
            error);

        // A malformed allowlist fails closed here too, exactly like the other four paths.
        var malformed = ListFrom("not a hash at all");
        var inspect2 = new CountingImageInspect();
        Assert.False(Boot.CoreReloadPolicy.TryAcceptStagedCoreBytes(
            Boot.CoreLayout.FileName, staged, malformed, inspect2.Inspect, out var bad, out _));
        Assert.Equal(0, inspect2.Loads);
        Assert.Equal(Boot.AllowDecision.Malformed, bad);
    }

    /// <summary>
    /// The gate is a gate, not a wall: a listed image and an absent allowlist both reach the
    /// load step exactly once, and the load sees the same array that was hashed (no re-read,
    /// so nothing can be swapped between the hash and the promote).
    /// </summary>
    [Fact]
    public void A_listed_staged_core_reaches_the_load_step_once_with_the_bytes_that_were_hashed()
    {
        byte[] staged = Encoding.UTF8.GetBytes("the Core generation the operator listed");
        string hash = Boot.ReloadAllowPolicy.Sha256Hex(staged);

        var listed = ListFrom(Boot.ReloadAllowPolicy.FormatSumsLine(hash, Boot.CoreLayout.FileName));
        var inspect = new CountingImageInspect();
        Assert.True(Boot.CoreReloadPolicy.TryAcceptStagedCoreBytes(
            Boot.CoreLayout.FileName, staged, listed, inspect.Inspect, out var decision, out string error));
        Assert.Equal(Boot.AllowDecision.Listed, decision);
        Assert.Equal("", error);
        Assert.Equal(1, inspect.Loads);
        Assert.Same(staged, inspect.LastBytes);

        // No allowlist file: unchanged behavior, the image is inspected as before.
        var none = ListFrom(null);
        var inspect2 = new CountingImageInspect();
        Assert.True(Boot.CoreReloadPolicy.TryAcceptStagedCoreBytes(
            Boot.CoreLayout.FileName, staged, none, inspect2.Inspect, out var noList, out _));
        Assert.Equal(Boot.AllowDecision.NoList, noList);
        Assert.Equal(1, inspect2.Loads);

        // A listed image that is not a usable Core still fails, with the inspect step's reason.
        var inspect3 = new CountingImageInspect { Error = "truncated PE (section table or CLI metadata extends past file length)" };
        Assert.False(Boot.CoreReloadPolicy.TryAcceptStagedCoreBytes(
            Boot.CoreLayout.FileName, staged, listed, inspect3.Inspect, out _, out string why));
        Assert.Equal(inspect3.Error, why);

        // Reload.WatchCore does not enter this decision: the promote path runs with the
        // watcher off, which is why gating it (above) and not the watcher is the fix.
        Assert.False(Boot.CoreWatchPolicy.DefaultWatchCore);
        Assert.True(Boot.CoreReloadPolicy.ShouldPromoteOnFirstLoad(watchEnabled: false));
        Assert.True(Boot.CoreReloadPolicy.ShouldPromoteOnFirstLoad(watchEnabled: true));
    }

    /// <summary>
    /// R30 finding 13. An allowlist whose <em>path</em> cannot be resolved used to come back
    /// Absent — "there is no allowlist" — the one way an unreadable allowlist could fail open.
    /// It is Malformed now, so every decision it makes refuses, and the store logs it.
    /// </summary>
    [Fact]
    public void An_allowlist_whose_path_cannot_be_resolved_fails_closed_with_a_log_line()
    {
        var list = Boot.ReloadAllowPolicy.UnresolvedPath("TypeNotInitializedException: Paths");
        Assert.Equal(Boot.AllowListStatus.Malformed, list.Status);

        string hash = Boot.ReloadAllowPolicy.Sha256Hex(new byte[] { 9 });
        foreach (string name in new[] { Boot.CoreLayout.FileName, "Progenitor.Sample.dll" })
        {
            var decision = list.Decide(name, hash);
            Assert.Equal(Boot.AllowDecision.Malformed, decision);
            Assert.False(Boot.ReloadAllowPolicy.MayLoad(decision));
        }

        string line = Boot.ReloadAllowPolicy.FormatMalformed(list.Error);
        Assert.Contains("cannot resolve " + Boot.ReloadAllowPolicy.DisplayPath, line, StringComparison.Ordinal);
        Assert.Contains("TypeNotInitializedException: Paths", line, StringComparison.Ordinal);

        // Source pin (deliberate): both stores bind BepInEx's Paths, so neither is compiled by
        // this project and no fake can reach the empty-path branch. This is the one assertion
        // in the file that reads text, and it exists because the call site cannot be executed.
        foreach (var parts in new[]
        {
            new[] { "src", "Progenitor.Bootstrap", "ReloadAllowStore.cs" },
            new[] { "src", "Progenitor.Core", "Reload", "ReloadAllowStore.cs" },
        })
        {
            string store = ReadRepoFile(parts);
            SourcePin.AssertUncommented(store, "ReloadAllowPolicy.UnresolvedPath(");
            Assert.DoesNotContain(
                "if (path.Length == 0)\n                return Publish(ReloadAllowPolicy.Absent());",
                store.Replace("\r\n", "\n"),
                StringComparison.Ordinal);
            // R31 finding 27: and the path itself is decided by the policy below, not by a bare
            // Path.Combine whose relative result the store then stats.
            SourcePin.AssertUncommented(store, "ReloadAllowPolicy.TryConfigPath(");
            Assert.DoesNotContain(
                "System.IO.Path.Combine(Paths.ConfigPath, ReloadAllowPolicy.FileName)",
                store,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// R31 finding 27: the last fail-open branch of path resolution. R30 finding 13 closed the
    /// throwing case (a null <c>Paths.ConfigPath</c>) and the stat failure, but an <em>empty</em>
    /// config folder combined into a working-directory-relative name: it did not exist, so the
    /// store published <see cref="Boot.AllowListStatus.Absent"/> and every load line read
    /// <c>allowlist=none</c> — the exact shape R30 finding 13 set out to eliminate, one branch
    /// over. The decision moved into the policy, so it can be driven here instead of pinned in
    /// source: anything that is not an absolute path is an unresolved path, and an unresolved
    /// path is Malformed, which refuses.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("config")]
    [InlineData("./config")]
    [InlineData("..\\config")]
    public void A_config_folder_that_is_not_absolute_fails_closed(string folder)
    {
        Assert.False(Boot.ReloadAllowPolicy.TryConfigPath(folder, out string path, out string why));
        Assert.Equal("", path);
        Assert.NotEqual("", why);

        // What the store does with that reason: Malformed, so nothing loads on any of the six
        // paths, and the line names the file the operator has to fix.
        var list = Boot.ReloadAllowPolicy.UnresolvedPath(why);
        Assert.Equal(Boot.AllowListStatus.Malformed, list.Status);
        Assert.Equal(
            Boot.AllowDecision.Malformed,
            list.Decide(Boot.CoreLayout.FileName, Boot.ReloadAllowPolicy.Sha256Hex(new byte[] { 7 })));
        Assert.Contains(why, Boot.ReloadAllowPolicy.FormatMalformed(list.Error), StringComparison.Ordinal);
    }

    [Fact]
    public void An_absolute_config_folder_still_resolves_the_allowlist_beside_it()
    {
        string folder = Path.Combine(Path.GetTempPath(), "progenitor-frld3-config");
        Assert.True(Boot.ReloadAllowPolicy.TryConfigPath(folder, out string path, out string why));
        Assert.Equal("", why);
        Assert.Equal(Path.Combine(folder, Boot.ReloadAllowPolicy.FileName), path);
    }

    // ---- R31 finding 1: the sixth staged-code path ---------------------------------------

    /// <summary>
    /// R31 finding 1, the critical one. <c>SelfUpdate.PromoteFile</c> sent Core to the gated
    /// path and let everything else through raw, so <c>Progenitor.Bootstrap.dll.next</c> was
    /// <c>File.Move</c>d over the live chainloader plugin on every process start and again at
    /// every quit — no hash, no allowlist row, no PE check — over the very assembly that
    /// enforces the allowlist for the other five paths.
    ///
    /// Same assertion shape as the Core gate above, and for the same reason: the PE/name check
    /// is a counting fake, and an unlisted image must leave that count at zero.
    /// </summary>
    [Fact]
    public void An_unlisted_staged_bootstrap_is_refused_before_it_is_inspected_or_promoted()
    {
        byte[] staged = Encoding.UTF8.GetBytes("a Bootstrap the operator never listed");
        string stagedHash = Boot.ReloadAllowPolicy.Sha256Hex(staged);
        string otherHash = Boot.ReloadAllowPolicy.Sha256Hex(new byte[] { 4, 5, 6 });

        var listed = ListFrom(
            Boot.ReloadAllowPolicy.FormatSumsLine(otherHash, Boot.CoreReloadPolicy.BootstrapFileName));
        var inspect = new CountingImageInspect();

        Assert.False(Boot.CoreReloadPolicy.TryAcceptStagedBootstrapBytes(
            Boot.CoreReloadPolicy.BootstrapFileName, staged, listed, inspect.Inspect,
            out var decision, out string error));
        Assert.Equal(0, inspect.Loads);
        Assert.Equal(Boot.AllowDecision.Unlisted, decision);
        Assert.Equal(
            Boot.ReloadAllowPolicy.FormatRefusedBootstrap(
                Boot.CoreReloadPolicy.BootstrapFileName, stagedHash, staged.LongLength, Boot.AllowDecision.Unlisted),
            error);

        // A malformed allowlist fails closed here too — including the unresolved-path shape
        // finding 27 closed, which is how an empty config folder reaches this path.
        var malformed = ListFrom("this is not a row");
        var inspect2 = new CountingImageInspect();
        Assert.False(Boot.CoreReloadPolicy.TryAcceptStagedBootstrapBytes(
            Boot.CoreReloadPolicy.BootstrapFileName, staged, malformed, inspect2.Inspect, out var bad, out _));
        Assert.Equal(0, inspect2.Loads);
        Assert.Equal(Boot.AllowDecision.Malformed, bad);

        // Listed: the check runs exactly once, on the array that was hashed.
        var allowed = ListFrom(
            Boot.ReloadAllowPolicy.FormatSumsLine(stagedHash, Boot.CoreReloadPolicy.BootstrapFileName));
        var inspect3 = new CountingImageInspect();
        Assert.True(Boot.CoreReloadPolicy.TryAcceptStagedBootstrapBytes(
            Boot.CoreReloadPolicy.BootstrapFileName, staged, allowed, inspect3.Inspect, out var ok, out _));
        Assert.Equal(Boot.AllowDecision.Listed, ok);
        Assert.Equal(1, inspect3.Loads);
        Assert.Same(staged, inspect3.LastBytes);
    }

    /// <summary>
    /// The PE half of the gate. Bootstrap is not collectible and this promote does not map it,
    /// so nothing is loaded: a complete managed PE whose metadata assembly name is
    /// <c>Progenitor.Bootstrap</c>, and nothing else, may be promoted.
    /// </summary>
    [Fact]
    public void Only_a_complete_managed_pe_named_Progenitor_Bootstrap_may_be_promoted()
    {
        Assert.Equal("empty", Boot.CoreReloadPolicy.CheckStagedBootstrapImage(Array.Empty<byte>(), "Progenitor.Bootstrap"));
        Assert.Equal(
            "not a PE image or truncated",
            Boot.CoreReloadPolicy.CheckStagedBootstrapImage(
                Encoding.UTF8.GetBytes("#!/bin/sh\nrm -rf /\n"), "Progenitor.Bootstrap"));

        // A real managed assembly: this test assembly. Complete PE, wrong identity.
        string self = typeof(ReloadHardeningTests).Assembly.Location;
        byte[] selfBytes = File.ReadAllBytes(self);
        string selfName = System.Reflection.AssemblyName.GetAssemblyName(self).Name ?? "";
        Assert.True(Boot.CoreImagePolicy.HasCompleteCliMetadata(selfBytes));
        Assert.Equal(
            "assembly name is " + selfName + ", expected " + Boot.CoreReloadPolicy.BootstrapAssemblySimpleName,
            Boot.CoreReloadPolicy.CheckStagedBootstrapImage(selfBytes, selfName));

        // Truncated: the header survives, the metadata does not.
        var truncated = new byte[selfBytes.Length / 3];
        Array.Copy(selfBytes, truncated, truncated.Length);
        Assert.Equal(
            "truncated PE (section table or CLI metadata extends past file length)",
            Boot.CoreReloadPolicy.CheckStagedBootstrapImage(truncated, "Progenitor.Bootstrap"));

        // A name that could not be read at all is a refusal, never a pass.
        Assert.StartsWith(
            "no managed assembly name",
            Boot.CoreReloadPolicy.CheckStagedBootstrapImage(selfBytes, null),
            StringComparison.Ordinal);

        // And the accepting case, on the same bytes with the right identity.
        Assert.Equal("", Boot.CoreReloadPolicy.CheckStagedBootstrapImage(
            selfBytes, Boot.CoreReloadPolicy.BootstrapAssemblySimpleName));
    }

    /// <summary>
    /// An in-memory <see cref="Boot.IPromoteFileSystem"/> that can do the one thing R32 finding 4
    /// is about and a real disk cannot be made to do reliably: change a file's contents
    /// <em>between</em> the moment the gate approved them and the moment the rotate moves them.
    /// Against <c>File</c> that is a race a test would have to win; here it is a delegate.
    ///
    /// <para><c>WriteNewExclusive</c> models <c>FileMode.CreateNew</c> — an existing path throws,
    /// so the production sequence cannot silently reuse a file somebody else created at a name it
    /// chose.</para>
    /// </summary>
    private sealed class FakePromoteDisk : Boot.IPromoteFileSystem
    {
        private readonly Dictionary<string, byte[]> _files =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Rewrites the contents about to be rotated in. Null means no attacker.</summary>
        public Func<string, byte[], byte[]>? TamperOnRotate;

        /// <summary>Returns an exception to throw from <c>ReadAllBytes</c> for a path, or null to read it.</summary>
        public Func<string, Exception?>? FailRead;

        /// <summary>Returns an exception to throw from <c>Move(source, destination)</c>, or null to move.</summary>
        public Func<string, string, Exception?>? FailMove;

        /// <summary>Returns an exception to throw from <c>Replace</c>, or null to replace.</summary>
        public Func<string, string, string, Exception?>? FailReplace;

        /// <summary>
        /// Returns an exception to throw from <c>Delete</c>, or null to delete. This is the hook
        /// R34 finding 11 needs: on Windows a <c>.prev</c> that still has a live image section
        /// refuses to be deleted, and that delete is the *first* statement of the rotate.
        /// </summary>
        public Func<string, Exception?>? FailDelete;

        /// <summary>Every path the disk has ever held a file at, in call order, for ordering assertions.</summary>
        public readonly List<string> Ops = new();

        public readonly List<string> Staged = new();

        public void Put(string path, byte[] bytes) => _files[path] = bytes;

        public byte[]? Get(string path) => _files.TryGetValue(path, out var b) ? b : null;

        public bool Exists(string path) => _files.ContainsKey(path);

        public void CreateDirectory(string path) { }

        public void Delete(string path)
        {
            Ops.Add("delete " + path);
            var fail = FailDelete?.Invoke(path);
            if (fail != null) throw fail;
            if (!_files.Remove(path)) throw new FileNotFoundException(path);
        }

        public void Move(string source, string destination)
        {
            Ops.Add("move " + source + " -> " + destination);
            var fail = FailMove?.Invoke(source, destination);
            if (fail != null) throw fail;
            if (!_files.TryGetValue(source, out var bytes)) throw new FileNotFoundException(source);
            if (_files.ContainsKey(destination)) throw new IOException("destination exists: " + destination);
            _files.Remove(source);
            _files[destination] = Tamper(source, bytes);
        }

        public void Replace(string source, string destination, string backup)
        {
            Ops.Add("replace " + source + " -> " + destination + " (backup " + backup + ")");
            var fail = FailReplace?.Invoke(source, destination, backup);
            if (fail != null) throw fail;
            if (!_files.TryGetValue(source, out var bytes)) throw new FileNotFoundException(source);
            if (_files.TryGetValue(destination, out var old)) _files[backup] = old;
            _files.Remove(source);
            _files[destination] = Tamper(source, bytes);
        }

        public byte[] ReadAllBytes(string path)
        {
            var fail = FailRead?.Invoke(path);
            if (fail != null) throw fail;
            return _files.TryGetValue(path, out var b) ? (byte[])b.Clone() : throw new FileNotFoundException(path);
        }

        public void WriteNewExclusive(string path, byte[] bytes)
        {
            if (_files.ContainsKey(path))
                throw new IOException("the file already exists: " + path);
            Staged.Add(path);
            _files[path] = (byte[])bytes.Clone();
        }

        /// <summary>Only the staged temp is swapped, and only on the way in — that is the finding.</summary>
        private byte[] Tamper(string source, byte[] bytes) =>
            TamperOnRotate != null && source.EndsWith(Boot.PromoteSequence.TempSuffix, StringComparison.Ordinal)
                ? TamperOnRotate(source, bytes)
                : bytes;
    }

    private static Boot.PromoteRequest BootstrapRequest(string live, string quarantine = "quarantined") =>
        new(live, live + ".next", "Bootstrap", Boot.PromoteKind.Bootstrap)
        {
            RowName = Boot.CoreReloadPolicy.BootstrapFileName,
            BackupPath = live + ".prev",
            DeleteBackupAfterPromote = true,
            QuarantinePath = quarantine,
        };

    /// <summary>Accepts any image. The identity check has its own test above; this one is about the rotate.</summary>
    private static string AcceptAnyImage(byte[] image, string tmp) => "";

    /// <summary>
    /// The exact shape Windows produces when something holds the file: an <see cref="IOException"/>
    /// whose HRESULT is <c>0x80070020</c> (ERROR_SHARING_VIOLATION). R34 finding 13 turned that
    /// HRESULT into the *only* thing that classifies a verify read failure as transient, so the
    /// tests have to produce it rather than any old <c>IOException</c>.
    /// </summary>
    private static IOException SharingViolation(string message = "the file is being used by another process") =>
        new(message) { HResult = unchecked((int)0x80070020) };

    /// <summary>No real backoff in tests; the retry count is what is being asserted, not the clock.</summary>
    private static readonly Action<int> NoPause = _ => { };

    /// <summary>
    /// R32 finding 4, the exploit as an assertion. The gate approves image A;
    /// the file the rotate is about to move is swapped to image B; the sequence must land A or
    /// nothing, never B.
    ///
    /// <para><b>Why this test fails if the fix is reverted.</b> The bare-move promote at the
    /// bottom of this test <em>is</em> the reverted code — gate the array, move the file, no
    /// re-read — run against the same disk and the same swap. It lands the attacker's bytes, and
    /// the assertions say so. Delete the re-read-and-compare from
    /// <c>PromoteSequence.Promote</c> and the first half of this test becomes the second half:
    /// <c>Status</c> is <c>Promoted</c> rather than <c>VerifyFailed</c>, and the live file holds
    /// <c>attacker</c>. There is no way to satisfy both halves without the verify.</para>
    /// </summary>
    [Fact]
    public void A_swap_between_the_gate_and_the_rotate_is_detected_and_rolled_back()
    {
        byte[] liveBytes = Encoding.UTF8.GetBytes("the Bootstrap the operator is running");
        byte[] gated = Encoding.UTF8.GetBytes("the Bootstrap the operator staged and listed");
        byte[] attacker = Encoding.UTF8.GetBytes("the Bootstrap nobody listed");
        const string live = @"C:\plugins\Progenitor.Bootstrap.dll";

        var disk = new FakePromoteDisk();
        disk.Put(live, liveBytes);
        disk.Put(live + ".next", gated);
        // The spin loop: whatever the promote staged, overwrite it the instant before the move.
        disk.TamperOnRotate = (_, _) => attacker;

        var listed = ListFrom(Boot.ReloadAllowPolicy.FormatSumsLine(
            Boot.ReloadAllowPolicy.Sha256Hex(gated), Boot.CoreReloadPolicy.BootstrapFileName));

        var result = Boot.PromoteSequence.Promote(
            disk, BootstrapRequest(live), listed, AcceptAnyImage);

        Assert.Equal(Boot.PromoteStatus.VerifyFailed, result.Status);
        Assert.True(result.Restored, "the previous image must be back at the live path");
        Assert.Equal(liveBytes, disk.Get(live));
        Assert.NotEqual(attacker, disk.Get(live));
        Assert.Equal(Boot.ReloadAllowPolicy.Sha256Hex(gated), result.GatedHash);
        Assert.Equal(Boot.ReloadAllowPolicy.Sha256Hex(attacker), result.PromotedHash);
        Assert.Contains("PROMOTE VERIFY FAILED", result.Error, StringComparison.Ordinal);

        // The same swap through the sequence the finding describes: gate the array, move the
        // file, ask no further questions. This is what the assertions above would look like
        // without the fix, and it is why they cannot pass by accident.
        var bare = new FakePromoteDisk();
        bare.Put(live, liveBytes);
        bare.Put(live + ".next", gated);
        bare.TamperOnRotate = (_, _) => attacker;
        BareMovePromote(bare, live, listed);
        Assert.Equal(attacker, bare.Get(live));
    }

    /// <summary>
    /// The pre-FRLD4 promote, kept only as the control in the test above: hash the array, ask the
    /// allowlist, write the array to a temp, move the temp over the live file. Every step is the
    /// production sequence's except the re-read.
    /// </summary>
    private static void BareMovePromote(
        Boot.IPromoteFileSystem fs, string live, Func<string, string, Boot.AllowDecision> decide)
    {
        byte[] bytes = fs.ReadAllBytes(live + ".next");
        if (!Boot.CoreReloadPolicy.TryAcceptStagedBootstrapBytes(
                Boot.CoreReloadPolicy.BootstrapFileName, bytes, decide, _ => "", out _, out _))
            return;
        string tmp = live + Boot.PromoteSequence.TempSuffix;
        fs.WriteNewExclusive(tmp, bytes);
        if (fs.Exists(live)) fs.Move(live, live + ".prev");
        fs.Move(tmp, live);
    }

    /// <summary>
    /// The Core half of finding 4. Same swap, the other gate and the <c>File.Replace</c> rotate:
    /// the backup <c>File.Replace</c> writes is what the rollback restores from.
    /// </summary>
    [Fact]
    public void The_core_promote_also_verifies_what_landed_and_restores_the_backup()
    {
        byte[] liveBytes = Encoding.UTF8.GetBytes("Core generation N");
        byte[] gated = Encoding.UTF8.GetBytes("Core generation N+1, listed");
        const string live = @"C:\core\Progenitor.dll";

        var disk = new FakePromoteDisk();
        disk.Put(live, liveBytes);
        disk.Put(live + ".next", gated);
        disk.TamperOnRotate = (_, _) => Encoding.UTF8.GetBytes("Core generation nobody listed");

        var listed = ListFrom(Boot.ReloadAllowPolicy.FormatSumsLine(
            Boot.ReloadAllowPolicy.Sha256Hex(gated), Boot.CoreLayout.FileName));

        var request = new Boot.PromoteRequest(live, live + ".next", "Core", Boot.PromoteKind.Core)
        {
            RowName = Boot.CoreLayout.FileName,
            BackupPath = live + ".prev",
            UseReplace = true,
        };

        var result = Boot.PromoteSequence.Promote(disk, request, listed, AcceptAnyImage);

        Assert.Equal(Boot.PromoteStatus.VerifyFailed, result.Status);
        Assert.True(result.Restored);
        Assert.Equal(liveBytes, disk.Get(live));
        Assert.Contains("PROMOTE VERIFY FAILED", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// R33 finding 9. The rollback used to <c>Delete(current)</c> and then <c>Move(backup,
    /// current)</c>, so a move that failed between the two — an anti-virus scan, an indexer, a
    /// second process mid-promote — left the folder with <em>no</em> live file, which is the one
    /// outcome the rollback exists to prevent. Here the move back into the live path is blocked
    /// and the restore must still succeed, because it goes through the atomic <c>Replace</c>:
    /// the live path is never empty for an instant, and the unverified image is parked at
    /// <c>.rejected.tmp</c> rather than deleted. Revert <c>RestoreFrom</c> to delete-then-move
    /// and this test fails on both the <c>Restored</c> flag and the missing live file.
    /// </summary>
    [Fact]
    public void The_restore_is_atomic_so_a_move_that_cannot_run_still_leaves_the_previous_image_live()
    {
        byte[] liveBytes = Encoding.UTF8.GetBytes("the Bootstrap the operator is running");
        byte[] gated = Encoding.UTF8.GetBytes("the Bootstrap the operator staged and listed");
        const string live = @"C:\plugins\Progenitor.Bootstrap.dll";

        var disk = new FakePromoteDisk();
        disk.Put(live, liveBytes);
        disk.Put(live + ".next", gated);
        disk.TamperOnRotate = (_, _) => Encoding.UTF8.GetBytes("the Bootstrap nobody listed");
        // Exactly the failure the finding describes: the backup will not move back.
        disk.FailMove = (source, destination) =>
            string.Equals(source, live + ".prev", StringComparison.OrdinalIgnoreCase)
            && string.Equals(destination, live, StringComparison.OrdinalIgnoreCase)
                ? new IOException("the file is in use by another process")
                : null;

        var listed = ListFrom(Boot.ReloadAllowPolicy.FormatSumsLine(
            Boot.ReloadAllowPolicy.Sha256Hex(gated), Boot.CoreReloadPolicy.BootstrapFileName));

        var result = Boot.PromoteSequence.Promote(
            disk, BootstrapRequest(live), listed, AcceptAnyImage);

        Assert.Equal(Boot.PromoteStatus.VerifyFailed, result.Status);
        Assert.True(result.Restored, "the atomic replace must not need the blocked move");
        Assert.Equal(liveBytes, disk.Get(live));
        Assert.Contains(Boot.PromoteSequence.ClassMismatch, result.Error, StringComparison.Ordinal);
        // The parked copy is cleaned up once the live file is confirmed.
        Assert.Null(disk.Get(live + Boot.PromoteSequence.RejectedSuffix));
    }

    /// <summary>
    /// The same finding, one rung further down: a file system that will not <c>Replace</c> at all
    /// <em>and</em> a backup that will not move — the crash-between-steps case. The sequence has
    /// to choose between an image nobody approved and no image at all, and it must choose the
    /// former: no live <c>Progenitor.Bootstrap.dll</c> means BepInEx chainloads nothing and
    /// Progenitor is gone until someone restores it by hand. It reports <c>Restored = false</c>,
    /// the backup is still on disk for a hand repair, and the operator gets the line that says
    /// the live file may be an image the allowlist never approved.
    /// </summary>
    [Fact]
    public void A_restore_that_cannot_run_at_all_still_leaves_an_image_at_the_live_path()
    {
        byte[] liveBytes = Encoding.UTF8.GetBytes("the Bootstrap the operator is running");
        byte[] gated = Encoding.UTF8.GetBytes("the Bootstrap the operator staged and listed");
        byte[] attacker = Encoding.UTF8.GetBytes("the Bootstrap nobody listed");
        const string live = @"C:\plugins\Progenitor.Bootstrap.dll";

        var disk = new FakePromoteDisk();
        disk.Put(live, liveBytes);
        disk.Put(live + ".next", gated);
        disk.TamperOnRotate = (_, _) => attacker;
        disk.FailReplace = (_, _, _) => new PlatformNotSupportedException("no atomic replace here");
        disk.FailMove = (source, destination) =>
            string.Equals(source, live + ".prev", StringComparison.OrdinalIgnoreCase)
            && string.Equals(destination, live, StringComparison.OrdinalIgnoreCase)
                ? new IOException("the file is in use by another process")
                : null;

        var listed = ListFrom(Boot.ReloadAllowPolicy.FormatSumsLine(
            Boot.ReloadAllowPolicy.Sha256Hex(gated), Boot.CoreReloadPolicy.BootstrapFileName));

        var result = Boot.PromoteSequence.Promote(
            disk, BootstrapRequest(live), listed, AcceptAnyImage);

        Assert.Equal(Boot.PromoteStatus.VerifyFailed, result.Status);
        Assert.False(result.Restored);
        Assert.NotNull(disk.Get(live));
        Assert.Equal(attacker, disk.Get(live));
        Assert.Contains("may be an image the allowlist never approved", result.Error, StringComparison.Ordinal);
        Assert.Contains(Boot.PromoteSequence.ClassMismatch, result.Error, StringComparison.Ordinal);
        // The backup was never consumed, so a hand repair still has the previous image.
        Assert.Equal(liveBytes, disk.Get(live + ".prev"));
    }

    /// <summary>
    /// R33 finding 10. A verify read that throws compared nothing, so it cannot be reported as
    /// the swap. It is its own status, carries <c>class=io-error</c> instead of
    /// <c>PROMOTE VERIFY FAILED</c>, keeps the image that was just rotated in, and leaves
    /// <c>.next</c> and the backup alone so the next start rotates the same bytes and verifies
    /// them again. Before this, the hash was <c>""</c>, the comparison failed, and a scanner
    /// holding the file for a moment rolled back a legitimate update and reported a swap attack
    /// on every start, forever — the second half of this test is that "next start", and it
    /// completes.
    /// </summary>
    [Fact]
    public void A_verify_read_that_throws_is_an_io_error_that_keeps_the_image_not_a_reported_swap()
    {
        byte[] liveBytes = Encoding.UTF8.GetBytes("the Bootstrap the operator is running");
        byte[] gated = Encoding.UTF8.GetBytes("the Bootstrap the operator staged and listed");
        const string live = @"C:\plugins\Progenitor.Bootstrap.dll";

        var disk = new FakePromoteDisk();
        disk.Put(live, liveBytes);
        disk.Put(live + ".next", gated);
        // Nobody tampers with anything; the live path simply cannot be opened for the verify.
        // R34 finding 13: it has to be a *sharing violation*, because that is now the only read
        // failure the sequence is willing to call transient.
        int reads = 0;
        disk.FailRead = path =>
        {
            if (!string.Equals(path, live, StringComparison.OrdinalIgnoreCase)) return null;
            reads++;
            return SharingViolation();
        };

        var listed = ListFrom(Boot.ReloadAllowPolicy.FormatSumsLine(
            Boot.ReloadAllowPolicy.Sha256Hex(gated), Boot.CoreReloadPolicy.BootstrapFileName));

        var result = Boot.PromoteSequence.Promote(
            disk, BootstrapRequest(live), listed, AcceptAnyImage, tempName: null, verifyPause: NoPause);

        Assert.Equal(Boot.PromoteStatus.VerifyUnreadable, result.Status);
        // The classification is only reached after a bounded in-process retry, so a holder that
        // lets go within the window costs nothing at all.
        Assert.Equal(Boot.PromoteSequence.VerifyReadAttempts, reads);
        Assert.False(result.Restored);
        Assert.Contains(Boot.PromoteSequence.ClassIoError, result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("PROMOTE VERIFY FAILED", result.Error, StringComparison.Ordinal);
        Assert.Equal("", result.PromotedHash);

        // The gated image stayed: a busy disk does not roll back a legitimate promote.
        Assert.Equal(gated, disk.Get(live));
        // And the retry is possible on the next start, because .next was not consumed.
        Assert.Equal(gated, disk.Get(live + ".next"));
        Assert.Equal(liveBytes, disk.Get(live + ".prev"));

        // Next start, same files, disk no longer busy: it verifies and completes.
        var again = new FakePromoteDisk();
        again.Put(live, disk.Get(live)!);
        again.Put(live + ".next", disk.Get(live + ".next")!);
        var second = Boot.PromoteSequence.Promote(
            again, BootstrapRequest(live), listed, AcceptAnyImage);
        Assert.Equal(Boot.PromoteStatus.Promoted, second.Status);
        Assert.Equal(gated, again.Get(live));
    }

    /// <summary>
    /// R34 finding 5, the exploit as an assertion. The restore used to park the rejected image at
    /// the compile-time constant <c>&lt;live&gt;.rejected.tmp</c> — a <c>public const</c> in the
    /// API surface, in the same attacker-writable folder the promote temp was randomized to get
    /// out of. A held file there made <c>Replace</c> throw, made the fallback's "do not stack"
    /// check bail, and returned <c>Restored = false</c> with the swapped image still live. One
    /// zero-byte file turned "swap detected and rolled back" into "swap detected and kept", for
    /// that restore and every later one.
    ///
    /// <para>So: a held file at <b>every fixed path the sequence names</b> — the rejected suffix
    /// and the promote temp suffix — that refuses to be deleted or moved onto, plus a file system
    /// with no <c>Replace</c> so the move-pair fallback is the code under test. The restore must
    /// still put the previous image back, and must not have touched either held file.</para>
    /// </summary>
    [Fact]
    public void A_held_file_at_every_fixed_path_cannot_stop_the_restore()
    {
        byte[] liveBytes = Encoding.UTF8.GetBytes("the Bootstrap the operator is running");
        byte[] gated = Encoding.UTF8.GetBytes("the Bootstrap the operator staged and listed");
        byte[] attacker = Encoding.UTF8.GetBytes("the Bootstrap nobody listed");
        byte[] squatter = Encoding.UTF8.GetBytes("");
        const string live = @"C:\plugins\Progenitor.Bootstrap.dll";

        string[] fixedPaths =
        {
            live + Boot.PromoteSequence.RejectedSuffix,
            live + Boot.PromoteSequence.TempSuffix,
        };

        var disk = new FakePromoteDisk();
        disk.Put(live, liveBytes);
        disk.Put(live + ".next", gated);
        foreach (string held in fixedPaths) disk.Put(held, squatter);

        disk.TamperOnRotate = (_, _) => attacker;
        // No atomic replace, so the fallback move pair is what runs — the branch the constant
        // park name disabled.
        disk.FailReplace = (_, _, _) => new PlatformNotSupportedException("no atomic replace here");
        // The squatted paths are held FileShare.None: unmovable and undeletable.
        disk.FailDelete = path =>
            fixedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
                ? SharingViolation("held by another process")
                : null;
        disk.FailMove = (source, destination) =>
            fixedPaths.Contains(destination, StringComparer.OrdinalIgnoreCase)
            || fixedPaths.Contains(source, StringComparer.OrdinalIgnoreCase)
                ? SharingViolation("held by another process")
                : null;

        var listed = ListFrom(Boot.ReloadAllowPolicy.FormatSumsLine(
            Boot.ReloadAllowPolicy.Sha256Hex(gated), Boot.CoreReloadPolicy.BootstrapFileName));

        var result = Boot.PromoteSequence.Promote(
            disk, BootstrapRequest(live), listed, AcceptAnyImage, tempName: null, verifyPause: NoPause);

        Assert.Equal(Boot.PromoteStatus.VerifyFailed, result.Status);
        Assert.True(result.Restored, "a squatted park path must not disable the rollback");
        Assert.Equal(liveBytes, disk.Get(live));
        Assert.Contains(Boot.PromoteSequence.ClassMismatch, result.Error, StringComparison.Ordinal);

        // The squatters were never named, so they were never touched.
        foreach (string held in fixedPaths) Assert.Equal(squatter, disk.Get(held));
    }

    /// <summary>
    /// R34 finding 5, the other half: parking is evidence, never a precondition. Here nothing can
    /// be moved to a <c>.rejected.tmp</c> name at all — random or not — and there is no
    /// <c>Replace</c>. The sequence must delete the rejected image and move the backup in anyway:
    /// a failure to save a souvenir of the swap can never be a reason to leave the swap live. The
    /// operator is told, in the error line, that the evidence copy was lost.
    /// </summary>
    [Fact]
    public void A_park_that_cannot_run_deletes_the_rejected_image_rather_than_keeping_it_live()
    {
        byte[] liveBytes = Encoding.UTF8.GetBytes("the Bootstrap the operator is running");
        byte[] gated = Encoding.UTF8.GetBytes("the Bootstrap the operator staged and listed");
        byte[] attacker = Encoding.UTF8.GetBytes("the Bootstrap nobody listed");
        const string live = @"C:\plugins\Progenitor.Bootstrap.dll";

        var disk = new FakePromoteDisk();
        disk.Put(live, liveBytes);
        disk.Put(live + ".next", gated);
        disk.TamperOnRotate = (_, _) => attacker;
        disk.FailReplace = (_, _, _) => new PlatformNotSupportedException("no atomic replace here");
        disk.FailMove = (_, destination) =>
            destination.EndsWith(Boot.PromoteSequence.RejectedSuffix, StringComparison.OrdinalIgnoreCase)
                ? new IOException("cannot create the aside copy")
                : null;

        var listed = ListFrom(Boot.ReloadAllowPolicy.FormatSumsLine(
            Boot.ReloadAllowPolicy.Sha256Hex(gated), Boot.CoreReloadPolicy.BootstrapFileName));

        var result = Boot.PromoteSequence.Promote(
            disk, BootstrapRequest(live), listed, AcceptAnyImage, tempName: null, verifyPause: NoPause);

        Assert.Equal(Boot.PromoteStatus.VerifyFailed, result.Status);
        Assert.True(result.Restored, "an unparkable rejected image is deleted, not kept live");
        Assert.Equal(liveBytes, disk.Get(live));
        Assert.NotEqual(attacker, disk.Get(live));
        Assert.Contains(
            Boot.PromoteSequence.FormatParkFailed(Path.GetFileName(live)),
            result.Error,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// R34 finding 11. <c>Rotate</c>'s first statement is a cleanup delete of the backup, and
    /// Windows refuses to delete a file that still has a live image section — which is precisely
    /// how a stale <c>.prev</c> comes to exist in the first place (the post-promote delete is
    /// best-effort for the same reason). That delete throwing used to reach the unconditional
    /// catch and call <c>RestoreFrom(".prev", live)</c>, putting the <em>old</em> image back over
    /// the freshly promoted good one and deleting the good one — at process exit, so the operator
    /// finds out one launch later, if ever.
    ///
    /// <para>Nothing moved, so nothing is owed: the live image must be exactly as it was and
    /// <c>.prev</c> must not have been read, moved or replaced. Revert the progress tracking and
    /// the live file becomes <c>stale</c> and this test fails on the first assertion.</para>
    /// </summary>
    [Fact]
    public void A_rotate_that_throws_before_it_moves_anything_leaves_the_live_image_and_prev_alone()
    {
        byte[] good = Encoding.UTF8.GetBytes("the Bootstrap that was promoted this session");
        byte[] stale = Encoding.UTF8.GetBytes("the Bootstrap from before the last promote");
        byte[] gated = Encoding.UTF8.GetBytes("the Bootstrap a deploy staged mid-session");
        const string live = @"C:\plugins\Progenitor.Bootstrap.dll";

        var disk = new FakePromoteDisk();
        disk.Put(live, good);
        disk.Put(live + ".prev", stale);   // survived the last promote: still mapped by this process
        disk.Put(live + ".next", gated);
        disk.FailDelete = path =>
            string.Equals(path, live + ".prev", StringComparison.OrdinalIgnoreCase)
                ? new IOException("the file is in use by another process")
                : null;

        var listed = ListFrom(Boot.ReloadAllowPolicy.FormatSumsLine(
            Boot.ReloadAllowPolicy.Sha256Hex(gated), Boot.CoreReloadPolicy.BootstrapFileName));

        Assert.Throws<IOException>(() => Boot.PromoteSequence.Promote(
            disk, BootstrapRequest(live), listed, AcceptAnyImage, tempName: null, verifyPause: NoPause));

        // The rotate never mutated the live path, so the restore must not have run.
        Assert.Equal(good, disk.Get(live));
        Assert.Equal(stale, disk.Get(live + ".prev"));
        Assert.DoesNotContain(
            disk.Ops,
            op => op.StartsWith("move " + live + ".prev -> ", StringComparison.OrdinalIgnoreCase)
                || op.StartsWith("replace " + live + ".prev -> ", StringComparison.OrdinalIgnoreCase));
        // And .next is still staged, so the next start retries it once the image section is gone.
        Assert.Equal(gated, disk.Get(live + ".next"));
    }

    /// <summary>
    /// R34 finding 13. "Did the read throw" was a signal the adversary owns: hold the live file
    /// and the detected swap degrades from restore-plus-error to keep-plus-warning, with the
    /// unapproved image live and one warning line in a log full of them. The io-error class is now
    /// narrow — a Win32 sharing or lock violation, after a bounded retry — and every other read
    /// failure is a verify that did not happen, which is not a verify that passed.
    ///
    /// <para>The companion test above is the transient half: same shape, sharing violation,
    /// <c>VerifyUnreadable</c> and the image kept. Only the HRESULT differs between them, which is
    /// the whole point of the finding.</para>
    /// </summary>
    [Fact]
    public void A_read_failure_that_is_not_a_sharing_violation_is_a_failed_verify_and_restores()
    {
        byte[] liveBytes = Encoding.UTF8.GetBytes("the Bootstrap the operator is running");
        byte[] gated = Encoding.UTF8.GetBytes("the Bootstrap the operator staged and listed");
        const string live = @"C:\plugins\Progenitor.Bootstrap.dll";

        var disk = new FakePromoteDisk();
        disk.Put(live, liveBytes);
        disk.Put(live + ".next", gated);

        int reads = 0;
        disk.FailRead = path =>
        {
            if (!string.Equals(path, live, StringComparison.OrdinalIgnoreCase)) return null;
            reads++;
            // A generic IOException — no ERROR_SHARING_VIOLATION HRESULT — is not something this
            // sequence can call transient.
            return new IOException("the read failed for some other reason");
        };

        var listed = ListFrom(Boot.ReloadAllowPolicy.FormatSumsLine(
            Boot.ReloadAllowPolicy.Sha256Hex(gated), Boot.CoreReloadPolicy.BootstrapFileName));

        var result = Boot.PromoteSequence.Promote(
            disk, BootstrapRequest(live), listed, AcceptAnyImage, tempName: null, verifyPause: NoPause);

        // Not retried: the retry exists for the transient class, not to slow every failure down.
        Assert.Equal(1, reads);
        Assert.Equal(Boot.PromoteStatus.VerifyFailed, result.Status);
        Assert.True(result.Restored);
        Assert.Equal(liveBytes, disk.Get(live));
        Assert.Contains(Boot.PromoteSequence.ClassMismatch, result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Boot.PromoteSequence.ClassIoError, result.Error, StringComparison.Ordinal);
        Assert.Equal("", result.PromotedHash);
    }

    /// <summary>
    /// The classifier itself, so the one HRESULT the whole io-error branch hangs on is pinned
    /// rather than left to a message string. R34 finding 13.
    /// </summary>
    [Fact]
    public void Only_a_sharing_or_lock_violation_counts_as_a_transient_read_failure()
    {
        Assert.True(Boot.PromoteSequence.IsTransientSharingFailure(
            new IOException("held") { HResult = unchecked((int)0x80070020) }));   // ERROR_SHARING_VIOLATION
        Assert.True(Boot.PromoteSequence.IsTransientSharingFailure(
            new IOException("locked") { HResult = unchecked((int)0x80070021) })); // ERROR_LOCK_VIOLATION

        Assert.False(Boot.PromoteSequence.IsTransientSharingFailure(null));
        Assert.False(Boot.PromoteSequence.IsTransientSharingFailure(new IOException("generic")));
        Assert.False(Boot.PromoteSequence.IsTransientSharingFailure(new UnauthorizedAccessException("denied")));
        // Derives from IOException, but a live file that has vanished is a missing image, not a
        // busy disk — and it is trivially attacker-producible.
        Assert.False(Boot.PromoteSequence.IsTransientSharingFailure(
            new FileNotFoundException("gone") { HResult = unchecked((int)0x80070020) }));
        // Right low word, wrong facility: not a Win32 HRESULT at all.
        Assert.False(Boot.PromoteSequence.IsTransientSharingFailure(
            new IOException("not win32") { HResult = unchecked((int)0x80130020) }));
    }

    /// <summary>
    /// The accepting path, with nothing tampering: the destination hashes to the gated bytes,
    /// <c>.next</c> is consumed, <c>.prev</c> is dropped (Bootstrap keeps no backup once the
    /// rotate verified) and no temp survives.
    /// </summary>
    [Fact]
    public void A_listed_image_promotes_and_the_destination_hash_is_the_gated_hash()
    {
        byte[] liveBytes = Encoding.UTF8.GetBytes("the Bootstrap the operator is running");
        byte[] gated = Encoding.UTF8.GetBytes("the Bootstrap the operator staged and listed");
        const string live = @"C:\plugins\Progenitor.Bootstrap.dll";

        var disk = new FakePromoteDisk();
        disk.Put(live, liveBytes);
        disk.Put(live + ".next", gated);

        var listed = ListFrom(Boot.ReloadAllowPolicy.FormatSumsLine(
            Boot.ReloadAllowPolicy.Sha256Hex(gated), Boot.CoreReloadPolicy.BootstrapFileName));

        var result = Boot.PromoteSequence.Promote(
            disk, BootstrapRequest(live), listed, AcceptAnyImage);

        Assert.Equal(Boot.PromoteStatus.Promoted, result.Status);
        Assert.Equal(gated, disk.Get(live));
        Assert.Equal(result.GatedHash, result.PromotedHash);
        Assert.False(disk.Exists(live + ".next"), "a promoted .next must be consumed");
        Assert.False(disk.Exists(live + ".prev"), "Bootstrap drops .prev once the rotate verified");
        foreach (string staged in disk.Staged)
            Assert.False(disk.Exists(staged), "no promote temp may survive the sequence");
    }

    /// <summary>
    /// R32 finding 4's other half: the temp path itself. It must not be the compile-time
    /// constant <c>CoreImagePolicy.PromoteTempPath</c> a process could sit on, and a file already
    /// sitting at the name the sequence picked is a refusal rather than something to reuse.
    /// </summary>
    [Fact]
    public void The_promote_temp_is_random_exclusive_and_never_reused()
    {
        const string live = @"C:\plugins\Progenitor.Bootstrap.dll";
        string first = Boot.PromoteSequence.NewTempName(Path.GetFileName(live));
        string second = Boot.PromoteSequence.NewTempName(Path.GetFileName(live));
        Assert.NotEqual(first, second);
        Assert.EndsWith(Boot.PromoteSequence.TempSuffix, first, StringComparison.Ordinal);
        Assert.NotEqual(Path.GetFileName(Boot.CoreImagePolicy.PromoteTempPath(live)), first);

        byte[] gated = Encoding.UTF8.GetBytes("listed bytes");
        var disk = new FakePromoteDisk();
        disk.Put(live, Encoding.UTF8.GetBytes("live"));
        disk.Put(live + ".next", gated);
        // The attacker got there first and created the file at the name we are about to use.
        const string chosen = "Progenitor.Bootstrap.dll.deadbeef" + ".promote.tmp";
        disk.Put(Path.Combine(@"C:\plugins", chosen), Encoding.UTF8.GetBytes("squatted"));

        var listed = ListFrom(Boot.ReloadAllowPolicy.FormatSumsLine(
            Boot.ReloadAllowPolicy.Sha256Hex(gated), Boot.CoreReloadPolicy.BootstrapFileName));

        var result = Boot.PromoteSequence.Promote(
            disk, BootstrapRequest(live), listed, AcceptAnyImage, () => chosen);

        Assert.Equal(Boot.PromoteStatus.Rejected, result.Status);
        Assert.Contains("could not stage", result.Error, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetBytes("live"), disk.Get(live));
    }

    /// <summary>
    /// The finding's original scenario, still an assertion and now against the production
    /// sequence rather than a copy of it: a local process drops a <c>.next</c> the operator's
    /// allowlist has never seen. The live Bootstrap must be byte-for-byte what it was, and the
    /// staged bytes quarantined rather than left to be retried on the next start.
    /// </summary>
    [Fact]
    public void The_promote_sequence_refuses_an_unlisted_next_and_leaves_the_live_bootstrap_untouched()
    {
        byte[] liveBytes = Encoding.UTF8.GetBytes("the Bootstrap the operator is running");
        byte[] staged = Encoding.UTF8.GetBytes("MZ but not really");
        const string live = @"C:\plugins\Progenitor.Bootstrap.dll";
        const string quarantine = @"C:\plugins\Progenitor.Bootstrap.dll.failed-20260904000000000";

        var disk = new FakePromoteDisk();
        disk.Put(live, liveBytes);
        disk.Put(live + ".next", staged);

        var listsOnlyTheLiveImage = ListFrom(Boot.ReloadAllowPolicy.FormatSumsLine(
            Boot.ReloadAllowPolicy.Sha256Hex(liveBytes), Boot.CoreReloadPolicy.BootstrapFileName));

        var refused = Boot.PromoteSequence.Promote(
            disk,
            BootstrapRequest(live, quarantine),
            listsOnlyTheLiveImage,
            // Never reached for a refused image; returning "" here would let a bad gate through.
            (_, _) => "");

        Assert.Equal(Boot.PromoteStatus.Refused, refused.Status);
        Assert.Equal(
            Boot.ReloadAllowPolicy.FormatRefusedBootstrap(
                Boot.CoreReloadPolicy.BootstrapFileName,
                Boot.ReloadAllowPolicy.Sha256Hex(staged),
                staged.LongLength,
                Boot.AllowDecision.Unlisted),
            refused.Error);
        Assert.Equal(liveBytes, disk.Get(live));
        Assert.False(disk.Exists(live + ".next"), "a refused .next must not be left for the next start to retry");
        Assert.Equal(quarantine, refused.QuarantinedAs);
        Assert.Equal(staged, disk.Get(quarantine));

        // Listed this time, but the image check refuses it: the live file is still untouched and
        // the reason travels back unchanged.
        disk.Put(live + ".next", staged);
        var listedAnyway = ListFrom(Boot.ReloadAllowPolicy.FormatSumsLine(
            Boot.ReloadAllowPolicy.Sha256Hex(staged), Boot.CoreReloadPolicy.BootstrapFileName));
        var rejected = Boot.PromoteSequence.Promote(
            disk,
            BootstrapRequest(live, quarantine + "-2"),
            listedAnyway,
            (image, _) => Boot.CoreReloadPolicy.CheckStagedBootstrapImage(image, "Progenitor.Bootstrap"));

        Assert.Equal(Boot.PromoteStatus.Rejected, rejected.Status);
        Assert.Equal("not a PE image or truncated", rejected.Error);
        Assert.Equal(liveBytes, disk.Get(live));
    }

    /// <summary>
    /// The same production sequence against the real file system, so the
    /// <see cref="Boot.IPromoteFileSystem"/> contract the fake above implements is pinned to
    /// what <c>File</c> actually does: exclusive create, move, re-read, and no temp left behind
    /// in the folder. This is the half a fake disk cannot vouch for.
    /// </summary>
    [Fact]
    public void The_real_file_system_promote_lands_the_gated_bytes_and_cleans_up()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-frld4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string live = Path.Combine(dir, Boot.CoreReloadPolicy.BootstrapFileName);
            byte[] liveBytes = Encoding.UTF8.GetBytes("live");
            byte[] gated = File.ReadAllBytes(typeof(ReloadHardeningTests).Assembly.Location);
            File.WriteAllBytes(live, liveBytes);
            File.WriteAllBytes(live + ".next", gated);

            var listed = ListFrom(Boot.ReloadAllowPolicy.FormatSumsLine(
                Boot.ReloadAllowPolicy.Sha256Hex(gated), Boot.CoreReloadPolicy.BootstrapFileName));

            var result = Boot.PromoteSequence.Promote(
                Boot.RealPromoteFileSystem.Instance,
                BootstrapRequest(live, Path.Combine(dir, "quarantined")),
                listed,
                // The production inspect's own move: read the assembly name off the temp the
                // sequence staged, without loading it. The identity rule itself is asserted by
                // Only_a_complete_managed_pe_named_Progenitor_Bootstrap_may_be_promoted; here the
                // point is that the temp is a real, complete, readable managed PE holding the
                // gated bytes. (The only assembly actually named Progenitor.Bootstrap is the
                // built one, which CI has no game install to produce, so this stands in.)
                (image, tmp) =>
                {
                    Assert.Equal(
                        System.Reflection.AssemblyName.GetAssemblyName(
                            typeof(ReloadHardeningTests).Assembly.Location).Name,
                        System.Reflection.AssemblyName.GetAssemblyName(tmp).Name);
                    Assert.True(Boot.CoreImagePolicy.HasCompleteCliMetadata(image));
                    return "";
                });

            Assert.Equal(Boot.PromoteStatus.Promoted, result.Status);
            Assert.Equal(gated, File.ReadAllBytes(live));
            Assert.Equal(result.GatedHash, result.PromotedHash);
            Assert.False(File.Exists(live + ".next"));
            Assert.Empty(Directory.GetFiles(dir, "*" + Boot.PromoteSequence.TempSuffix));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// R30 finding 11. Three places claimed a <c>SHA256SUMS</c> row could be pasted into the
    /// allowlist; the per-entry rows could not, and one pasted row made the whole file
    /// Malformed — which refuses Core too. There is one grammar now
    /// (<c>ReloadAllowPolicy.RowGrammar</c>), one writer (<c>package.ps1</c>'s
    /// <c>Format-SumsRow</c>, mirrored by <c>FormatSumsLine</c>) and one reader
    /// (<c>Parse</c>/<c>TryRowName</c>). This runs the real PowerShell writer and feeds its
    /// output to the real parser.
    /// </summary>
    [Fact]
    public void A_SHA256SUMS_row_pastes_into_the_allowlist_and_allows_that_exact_file()
    {
        byte[] image = Encoding.UTF8.GetBytes("a released Core generation");
        string hash = Boot.ReloadAllowPolicy.Sha256Hex(image);
        const string entryPath = "BepInEx/progenitor-core/Progenitor.dll";
        const string zipName = "Progenitor-0.4.0.zip";

        // The row as tools/package.ps1 writes it, produced by that script's own function when
        // PowerShell is available, and by the C# mirror of it otherwise.
        string row = FormatSumsRowWithPackageScript(hash, entryPath, zipName)
            ?? Boot.ReloadAllowPolicy.FormatSumsEntryLine(hash, zipName, entryPath);
        Assert.Equal(Boot.ReloadAllowPolicy.FormatSumsEntryLine(hash, zipName, entryPath), row);

        // Pasted in as-is, together with a plain zip row and a comment: still an Active list.
        string pasted = string.Join(
            "\n",
            "# pasted from packaging/out/SHA256SUMS",
            Boot.ReloadAllowPolicy.FormatSumsLine(hash, zipName),
            row);
        var list = Boot.ReloadAllowPolicy.Parse(pasted);
        Assert.Equal(Boot.AllowListStatus.Active, list.Status);
        Assert.Equal(2, list.Count);

        // ...and the qualified row is what allows the file it names, by its leaf.
        Assert.Equal(Boot.AllowDecision.Listed, list.Decide(Boot.CoreLayout.FileName, hash));
        Assert.Equal(Boot.AllowDecision.Listed, list.Decide(Boot.CoreLayout.FileName + ".next", hash));
        Assert.Equal(Boot.AllowDecision.Unlisted, list.Decide("Evil.dll", hash));

        // A bare path is still malformed: that is a typo, not a pasted release row.
        var typo = Boot.ReloadAllowPolicy.Parse(hash + "  BepInEx/plugins/Progenitor.Bootstrap.dll");
        Assert.Equal(Boot.AllowListStatus.Malformed, typo.Status);
        Assert.Contains("line 1", typo.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Our own trees keep hot swap because <c>deploy.ps1</c> writes <c>[Reload] WatchCore =
    /// true</c> into them; the shipped default is false. This runs the production
    /// <c>Set-BepInExCfgEntry</c> against a temp cfg rather than grepping for its name, so a
    /// merge that clobbers a neighboring entry, loses the section or is not idempotent fails
    /// here. Skipped when no PowerShell is on PATH (the CI image may have none).
    /// </summary>
    [Fact]
    public void Deploy_seeds_WatchCore_true_into_a_developer_tree_cfg_without_touching_other_entries()
    {
        Assert.False(Boot.CoreWatchPolicy.DefaultWatchCore);

        string? shell = PowerShellExe();
        if (shell == null) return;

        string dir = Path.Combine(Path.GetTempPath(), "progenitor-frld2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string cfg = Path.Combine(dir, "progenitor.cfg");
            File.WriteAllText(
                cfg,
                "[Dev]\nBridge = true\n\n[" + Boot.CoreWatchPolicy.ConfigSection + "]\n"
                + "SomethingElse = 3\n\n[Other]\nKeep = me\n");

            string fn = ExtractPowerShellFunction(ReadRepoFile("tools", "deploy.ps1"), "Set-BepInExCfgEntry");
            string script = fn + "\nSet-BepInExCfgEntry -Path $args[0] -Section $args[1] -Key $args[2] -Value 'true'\n";

            string first = RunPowerShell(shell, dir, script, cfg, Boot.CoreWatchPolicy.ConfigSection, Boot.CoreWatchPolicy.ConfigKey).Trim();
            string after = File.ReadAllText(cfg).Replace("\r\n", "\n");

            Assert.Equal("added", first);
            Assert.Contains("[" + Boot.CoreWatchPolicy.ConfigSection + "]\n" + Boot.CoreWatchPolicy.ConfigKey + " = true", after, StringComparison.Ordinal);
            Assert.Contains("SomethingElse = 3", after, StringComparison.Ordinal);
            Assert.Contains("Bridge = true", after, StringComparison.Ordinal);
            Assert.Contains("[Other]\nKeep = me", after, StringComparison.Ordinal);

            // Idempotent: deploy runs on every build and must not stack entries.
            string second = RunPowerShell(shell, dir, script, cfg, Boot.CoreWatchPolicy.ConfigSection, Boot.CoreWatchPolicy.ConfigKey).Trim();
            Assert.Equal("already set", second);
            Assert.Equal(after, File.ReadAllText(cfg).Replace("\r\n", "\n"));

            // A tree that had it off is flipped back on rather than duplicated.
            File.WriteAllText(cfg, "[" + Boot.CoreWatchPolicy.ConfigSection + "]\n" + Boot.CoreWatchPolicy.ConfigKey + " = false\n");
            Assert.Equal("updated", RunPowerShell(shell, dir, script, cfg, Boot.CoreWatchPolicy.ConfigSection, Boot.CoreWatchPolicy.ConfigKey).Trim());
            string flipped = File.ReadAllText(cfg).Replace("\r\n", "\n");
            Assert.Contains(Boot.CoreWatchPolicy.ConfigKey + " = true", flipped, StringComparison.Ordinal);
            Assert.DoesNotContain(Boot.CoreWatchPolicy.ConfigKey + " = false", flipped, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// Source pin, and the one that earns it: Bootstrap and Core cannot reference each other,
    /// so <c>ReloadAllowPolicy</c> exists twice and "the two copies are the same code" is a
    /// real invariant no behavioral test can express. Both copies are compiled by this
    /// project, so <see cref="ReloadAllowPolicyTests"/> covers what they *do*.
    /// </summary>
    [Fact]
    public void The_two_policy_copies_are_the_same_code()
    {
        string boot = StripLineComments(ReadRepoFile("src", "Progenitor.Bootstrap", "ReloadAllowPolicy.cs"))
            .Replace("namespace Progenitor.Bootstrap;", "namespace X;");
        string core = StripLineComments(ReadRepoFile("src", "Progenitor.Core", "Reload", "ReloadAllowPolicy.cs"))
            .Replace("namespace Progenitor.Reload;", "namespace X;");
        Assert.Equal(boot, core);
    }

    /// <summary>
    /// Job FREL2, the Bootstrap half of FREL1 (R61 finding 1). The plugin-identity resolution
    /// order exists twice for the same reason the policy and the promote do — Bootstrap may not
    /// reference Core — and this is the copy that runs in production, because Core's
    /// <c>PluginReloader</c> only runs when <c>BootstrapLink.OwnsLifetime</c> is false. Both
    /// copies are compiled by this project, so <c>PluginReloaderTests</c> covers what the order
    /// <em>does</em>; this pins that the two are one file with one line changed, so a fix landed
    /// on either reloader is landed on both.
    /// </summary>
    [Fact]
    public void The_two_plugin_identity_copies_are_the_same_code()
    {
        string boot = StripLineComments(ReadRepoFile("src", "Progenitor.Bootstrap", "PluginIdentity.cs"))
            .Replace("namespace Progenitor.Bootstrap;", "namespace X;");
        string core = StripLineComments(ReadRepoFile("src", "Progenitor.Core", "Reload", "PluginIdentity.cs"))
            .Replace("namespace Progenitor.Reload;", "namespace X;");
        Assert.Equal(boot, core);
    }

    /// <summary>
    /// Job FREL2: the production reloader reads <c>[BepInPlugin]</c> the way BepInEx does. The
    /// mirror test above pins the helper; this pins the two things in <c>ReloaderHost</c> itself
    /// that the helper cannot — that discovery passes an explicit <c>inherit</c> rather than
    /// taking the generic overload's <c>true</c> default (the trigger behind every null-metadata
    /// dereference in that file), and that the file has exactly one
    /// <c>MetadataHelper.GetMetadata</c> call left, inside <c>IdentityOf</c>. ReloaderHost binds
    /// BepInEx and cannot be compiled here, so this is a source pin, and it is the kind that
    /// earns it: a re-added bare read is precisely the drift it exists to catch.
    /// </summary>
    [Fact]
    public void The_production_reloader_reads_the_plugin_attribute_the_way_BepInEx_does()
    {
        string src = ReadRepoFile("src", "Progenitor.Bootstrap", "ReloaderHost.cs");
        string code = StripLineComments(src);

        Assert.Contains("private const bool DiscoveryInherit = false;", code, StringComparison.Ordinal);
        Assert.Contains("GetCustomAttribute<BepInPlugin>(DiscoveryInherit)", code, StringComparison.Ordinal);
        // The default-inherit form is the bug; no occurrence of it may come back.
        Assert.DoesNotContain("GetCustomAttribute<BepInPlugin>()", code, StringComparison.Ordinal);

        int reads = 0;
        for (int i = code.IndexOf("MetadataHelper.GetMetadata(", StringComparison.Ordinal); i >= 0;
             i = code.IndexOf("MetadataHelper.GetMetadata(", i + 1, StringComparison.Ordinal))
            reads++;
        Assert.Equal(1, reads);
        Assert.Contains("PluginIdentity.Resolve(", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The equivalence <c>ModManifestPhase3Tests.ReloadScan</c> rests on, asserted against the two
    /// reloaders rather than stated in its docstring (R36's test note). Neither reloader is
    /// compiled by this project — both bind the game — so this is a source pin, and it is the kind
    /// that earns it: the claim is about the enumeration those two files perform, and it fails the
    /// moment either one grows a second pattern, a second folder or a recursive search.
    /// </summary>
    [Fact]
    public void Both_reloaders_enumerate_exactly_one_flat_dll_pattern_on_the_reload_folder()
    {
        foreach (var parts in new[]
        {
            new[] { "src", "Progenitor.Core", "Reload", "PluginReloader.cs" },
            new[] { "src", "Progenitor.Bootstrap", "ReloaderHost.cs" },
        })
        {
            string src = StripLineComments(ReadRepoFile(parts));
            var enumerations = new List<string>();
            int at = 0;
            while (true)
            {
                int i = src.IndexOf("Directory.GetFiles(", at, StringComparison.Ordinal);
                if (i < 0) break;
                int end = src.IndexOf(')', i);
                enumerations.Add(src.Substring(i, end - i + 1));
                at = i + 1;
            }

            string where = string.Join(" | ", parts);
            // Job FENC2 added the delta channel, so there are now three enumerations and each one
            // is still flat, still on the reload folder, and still a named pattern: the plugin
            // DLLs, the deltas waiting to be applied, and the deltas being dropped for one plugin
            // whose baseline just went away. Anything else — a recursive search, a second folder,
            // a bare GetFiles(Folder) — is the drift this pin exists to catch.
            Assert.Equal(
                new[]
                {
                    "Directory.GetFiles(Folder, \"*.dll\")",
                    "Directory.GetFiles(Folder, \"*.delta\")",
                    // Job FENC7 (R42 finding 4): the sweep's key is the plugin DLL's FILE STEM,
                    // the one identity the delta pipeline uses; it was the assembly name, which
                    // named no file the watcher ever wrote wherever the two strings differ.
                    "Directory.GetFiles(Folder, stem + \".*.delta\")",
                },
                enumerations);
            Assert.DoesNotContain("SearchOption", src, StringComparison.Ordinal);
            foreach (string only in enumerations)
                Assert.False(only.Contains("AllDirectories", StringComparison.Ordinal), "source pin: " + where);
        }
    }

    /// <summary>
    /// R38 finding 6: the pin above covers the two files that <em>read</em> the reload folder, and
    /// the promote — which <em>deletes</em> from it — was the one recursive enumeration anywhere
    /// near it. A marker in a subfolder therefore deleted a file no reloader would ever have
    /// loaded, including FCOMPAT3's plugin-local dependency folders, which live under exactly this
    /// root. Same rule, extended to the third file: the reload root is enumerated flat. The
    /// pending root is not covered — nothing scans it, and its tree is the promote's own. (Core's mirror of the promote
    /// went with server-to-client mod distribution, 2026-10-05; Bootstrap's copy stays.)
    /// </summary>
    [Fact]
    public void The_promote_enumerates_the_reload_root_as_flat_as_the_reloaders()
    {
        foreach (var parts in new[]
        {
            new[] { "src", "Progenitor.Bootstrap", "PendingInstalls.cs" },
        })
        {
            string src = StripLineComments(ReadRepoFile(parts));
            string where = string.Join(" | ", parts);
            SourcePin.AssertUncommented(src, "Directory.GetFiles(reloadRoot, \"*\" + SessionMarkerSuffix, SearchOption.TopDirectoryOnly)");
            Assert.DoesNotContain(
                "Directory.GetFiles(reloadRoot, \"*\" + SessionMarkerSuffix, SearchOption.AllDirectories)",
                src,
                StringComparison.Ordinal);
            Assert.False(
                src.Contains("reloadRoot, \"*\" + SessionMarkerSuffix, SearchOption.AllDirectories", StringComparison.Ordinal),
                "source pin: " + where);
        }
    }

    /// <summary>
    /// Job FENC2. The <c>.delta</c> gate is the eighth automatic code-loading path and, like the
    /// allowlist policy it calls, it exists twice because Bootstrap and Core cannot reference each
    /// other. Same pin, same rule: edit Bootstrap's copy first, then re-mirror.
    /// <see cref="DeltaApplyTests.The_two_delta_gates_agree"/> covers what they do.
    /// </summary>
    [Fact]
    public void The_two_delta_gate_copies_are_the_same_code()
    {
        string boot = StripLineComments(ReadRepoFile("src", "Progenitor.Bootstrap", "DeltaApply.cs"))
            .Replace("namespace Progenitor.Bootstrap;", "namespace X;");
        string core = StripLineComments(ReadRepoFile("src", "Progenitor.Core", "Reload", "DeltaApply.cs"))
            .Replace("namespace Progenitor.Reload;", "namespace X;");
        Assert.Equal(boot, core);
    }

    /// <summary>
    /// Job FREL3 (R62 finding 3): "every unload step is guarded on its own" asserted over the
    /// whole sequence rather than one step of it.
    ///
    /// <para>FREL1/FREL2 made the claim and <c>PluginReloaderTests</c> pinned a single instance of
    /// it (<c>try { SweepRegistrations(...); }</c>), so nothing would have gone red if any of the
    /// other guards were dropped or an unguarded step added. Two bare calls were in fact living
    /// among guarded neighbors: <c>ReloaderHost</c>'s <c>WarnIfDetourCachePins</c>, a diagnostic
    /// immediately in front of <c>context.Unload()</c> — a throw past its inner scan guard escaped
    /// the whole method, so the collectible plugin ALC was never asked to unload and every later
    /// Core swap would refuse with <c>PluginAlcsAlive</c> — and Core's
    /// <c>PluginOwnedAssemblies.RemoveEventHandlers</c>, safe only because the callee guards
    /// internally.</para>
    ///
    /// <para>This enumerates every <c>;</c>-terminated statement between the two markers each
    /// bracketed method carries and fails on one that is not inside a <c>try</c>. The exemptions
    /// are listed here, each with its reason, so adding one is an edit a reviewer sees rather than
    /// a silence. A source pin because neither reloader can be compiled in this project — both bind
    /// BepInEx — and because the claim is about the shape of the source, which is exactly what a
    /// source pin can hold.</para>
    ///
    /// <para><b>Job FREL4 (R63 finding 3): three regions, named, not two.</b> FREL3's two marker
    /// pairs did not bracket the same sequence — Bootstrap's wrapped <c>ReloaderHost.Unload</c>
    /// while Core's wrapped <c>SweepRegistrations</c>, one step of the sequence — so Core's own
    /// <c>Unload</c>, the method holding <c>context?.Unload()</c>, was never read at all and its
    /// five bare statements sat outside the pin behind an empty exemption list that read as "Core
    /// needs none". Markers now carry the method name, every expected region is named here (a
    /// missing one fails rather than silently reducing coverage), and the guarded floor is the
    /// count measured in that method rather than one number shared by all of them — with 14
    /// guarded steps in <c>SweepRegistrations</c> a shared floor of 8 let six of them be relocated
    /// out of the region unnoticed.</para>
    /// </summary>
    [Fact]
    public void Every_unload_step_is_inside_a_try()
    {
        // A statement is exempt when it starts with one of these. A logger call is exempt
        // everywhere: it is the sink every catch in these sequences already writes to, so if it
        // throws the teardown cannot be reported at all and wrapping it would only move the
        // problem. Every other exemption is named per region below, with why it cannot throw.
        string[] loggers = { "Plugin.Logger?.Log", "ProcessHost.Log?.Log" };

        // Reasons for the per-region exemptions, all four of which are shared by the two Unload
        // methods (the sequences are mirrors of each other):
        //  * `var identity = IdentityOf(plugin);` — total by construction. IdentityOf is
        //    PluginIdentity.Resolve, whose loop catches every reader that throws and whose last
        //    line returns an identity built from the type name; it has no other exit.
        //    PluginReloaderTests.A_throwing_metadata_read_still_yields_an_identity drives that.
        //  * `loaded.Plugins.Clear();` — List<T>.Clear over a non-null field. It writes _size and
        //    _version and clears the array; it has no failure mode short of a corrupt heap.
        //  * `DropDeltasFor(loaded.Stem);` — its whole body is one try/catch in the same file.
        //  * `DropDeltaStateFor(loaded.Stem);` — DeltaApply.DeleteStateFile, whose whole body is
        //    `try { …; return true; } catch { return false; }`.
        // What is NOT exempt any more: PluginOwnedAssemblies.Of. It documents "never throws" and
        // its body is a try/catch, but that is callee courtesy rather than a guard, and it stands
        // in front of context.Unload(); FREL4 wrapped it in Core's Unload instead of listing it.
        // Job FREL5 (R64 finding 7): the rule now holds at BOTH call sites. The other one opened
        // SweepRegistrations one line above its own start marker, so the pin could not read it —
        // the marker moved up over it and the statement is wrapped, which is why that region's
        // floor is 15 rather than FREL4's 14 and why its exemption list is still empty.
        string[] unloadExemptions =
        {
            "var identity = IdentityOf(plugin);",
            "loaded.Plugins.Clear();",
            "DropDeltasFor(loaded.Stem);",
            "DropDeltaStateFor(loaded.Stem);",
        };

        // Guarded floors are the counts measured in these three methods at FREL4. A step moved out
        // of its region drops the count and fails here; a step added inside raises it and passes.
        var cases = new (string[] Parts, string Method, int GuardedFloor, string[] Exempt)[]
        {
            (new[] { "src", "Progenitor.Core", "Reload", "PluginReloader.cs" }, "Unload", 7, unloadExemptions),
            // Job FCFG13 raised this floor from 15 to 16: the ConfigHelper.DropWatchedFile backstop
            // for a plugin whose UndoAll never ran is a sixteenth guarded step. The JsonCacheSweep
            // step (a plugin's System.Text.Json cache entries, after its Unload) is the seventeenth.
            (new[] { "src", "Progenitor.Core", "Reload", "PluginReloader.cs" }, "SweepRegistrations", 17, Array.Empty<string>()),
            (new[] { "src", "Progenitor.Bootstrap", "ReloaderHost.cs" }, "Unload", 9, unloadExemptions),
        };

        foreach (var (parts, method, floor, exempt) in cases)
        {
            string where = string.Join("/", parts) + " " + method + "()";
            string src = ReadRepoFile(parts).Replace("\r\n", "\n");
            string startMarker = "// ---- unload steps: " + method + " (";
            string endMarker = "// ---- end unload steps: " + method + " ----";
            int start = src.IndexOf(startMarker, StringComparison.Ordinal);
            int end = src.IndexOf(endMarker, StringComparison.Ordinal);
            Assert.True(start >= 0, "no unload-steps start marker for " + where);
            Assert.True(end > start, "no unload-steps end marker after the start for " + where);
            // One region per method per file: a second pair of the same name would make "which
            // statements were read" ambiguous.
            Assert.Equal(-1, src.IndexOf(startMarker, start + 1, StringComparison.Ordinal));

            var unguarded = UnguardedStatements(src.Substring(start, end - start));
            // The sequence must actually have been read: a marker pair around nothing would pass
            // vacuously, which is the failure mode of every source pin.
            Assert.True(unguarded.Guarded >= floor,
                where + ": " + unguarded.Guarded + " guarded steps found, expected at least " + floor);

            foreach (string statement in unguarded.Bare)
            {
                bool allowed = IsLoggerOnly(statement, loggers);
                foreach (string prefix in exempt)
                    allowed |= statement.StartsWith(prefix, StringComparison.Ordinal);
                Assert.True(allowed, where + ": unload step is not inside a try: " + statement);
            }
        }
    }

    /// <summary>True when the only call in <paramref name="statement"/> is a logger call.</summary>
    private static bool IsLoggerOnly(string statement, string[] loggers)
    {
        foreach (string logger in loggers)
        {
            int at = statement.IndexOf(logger, StringComparison.Ordinal);
            if (at < 0) continue;
            // Nothing before the logger may itself be a call: `if (x)` is fine, `F(x); Log(...)`
            // is not — and the statement scanner never joins two statements anyway.
            string before = statement.Substring(0, at);
            if (before.Contains("(", StringComparison.Ordinal)
                && !before.TrimEnd().EndsWith(")", StringComparison.Ordinal))
                continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Every <c>;</c>-terminated statement in <paramref name="region"/> that contains a call and
    /// is <b>not</b> inside a <c>try</c>, plus a count of the guarded steps so a mis-placed marker
    /// cannot make the pin vacuous.
    ///
    /// <para>Line-oriented on purpose. A one-line <c>try { X; }</c> (the shape both sequences use)
    /// is guarded and consumes nothing else; a multi-line <c>try</c> opens a block that runs until
    /// a <c>}</c> at the same indentation whose next code line is not <c>catch</c> or
    /// <c>finally</c>, so its <c>catch</c> bodies count as guarded too. Continuation lines are
    /// joined, so a statement is judged by where it <em>starts</em>.</para>
    /// </summary>
    private static (List<string> Bare, int Guarded) UnguardedStatements(string region)
    {
        var bare = new List<string>();
        int guarded = 0;
        int tryIndent = -1;
        string pending = "";
        string[] lines = region.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i].TrimEnd();
            string text = raw.Trim();
            if (text.Length == 0 || text.StartsWith("//", StringComparison.Ordinal)
                || text.StartsWith("*", StringComparison.Ordinal))
                continue;
            int indent = raw.Length - raw.TrimStart().Length;

            if (tryIndent >= 0)
            {
                if (indent == tryIndent && text.StartsWith("}", StringComparison.Ordinal)
                    && !NextCode(lines, i + 1).StartsWith("catch", StringComparison.Ordinal)
                    && !NextCode(lines, i + 1).StartsWith("finally", StringComparison.Ordinal))
                    tryIndent = -1;
                continue;
            }

            if (text.StartsWith("try", StringComparison.Ordinal)
                || text.StartsWith("catch", StringComparison.Ordinal)
                || text.StartsWith("finally", StringComparison.Ordinal))
            {
                if (text.StartsWith("try", StringComparison.Ordinal)) guarded++;
                if (!text.Contains("}", StringComparison.Ordinal)) tryIndent = indent;
                pending = "";
                continue;
            }

            pending = pending.Length == 0 ? text : pending + " " + text;
            if (text.EndsWith(";", StringComparison.Ordinal))
            {
                if (pending.Contains("(", StringComparison.Ordinal)) bare.Add(pending);
                pending = "";
            }
            else if (text.EndsWith("{", StringComparison.Ordinal) || text.StartsWith("}", StringComparison.Ordinal))
            {
                pending = "";
            }
        }
        return (bare, guarded);
    }

    private static string NextCode(string[] lines, int from)
    {
        for (int i = from; i < lines.Length; i++)
        {
            string text = lines[i].Trim();
            if (text.Length == 0 || text.StartsWith("//", StringComparison.Ordinal)) continue;
            return text;
        }
        return "";
    }

    /// <summary>Drops <c>//</c> line comments (never <c>///</c> doc comments) so the mirror header does not count as drift.</summary>
    private static string StripLineComments(string text)
    {
        var kept = new List<string>();
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
        {
            string t = line.TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal) && !t.StartsWith("///", StringComparison.Ordinal))
                continue;
            string trimmed = line.TrimEnd();
            // A removed comment block must not leave a blank-line difference behind.
            if (trimmed.Length == 0 && kept.Count > 0 && kept[kept.Count - 1].Length == 0)
                continue;
            kept.Add(trimmed);
        }
        return string.Join("\n", kept).Trim();
    }

    // ---- tooling seam -------------------------------------------------------------------

    /// <summary>Runs <c>tools/package.ps1</c>'s own <c>Format-SumsRow</c>, or null when no PowerShell is available.</summary>
    private static string? FormatSumsRowWithPackageScript(string hash, string entryPath, string zipName)
    {
        string? shell = PowerShellExe();
        if (shell == null) return null;
        string fn = ExtractPowerShellFunction(ReadRepoFile("tools", "package.ps1"), "Format-SumsRow");
        string script = fn + "\nFormat-SumsRow -Hash $args[0] -Name $args[1] -Container $args[2]\n";
        return RunPowerShell(shell, Path.GetTempPath(), script, hash, entryPath, zipName).Trim();
    }

    /// <summary>
    /// Lifts one <c>function Name { … }</c> out of a script by brace matching, so a test can
    /// execute it without running the script's body (deploy.ps1 deploys; package.ps1 packages).
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

    private static string RunPowerShell(string shell, string workingDir, string script, params string[] args)
    {
        string file = Path.Combine(Path.GetTempPath(), "progenitor-frld2-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, script, new UTF8Encoding(false));
        try
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
            p.WaitForExit(60_000);
            Assert.True(p.ExitCode == 0, shell + " exited " + p.ExitCode + ": " + stderr);
            return stdout;
        }
        finally
        {
            try { File.Delete(file); } catch { /* temp */ }
        }
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
                if (p.ExitCode == 0)
                {
                    _shell = exe;
                    return _shell;
                }
            }
            catch
            {
                // not installed under this name
            }
        }
        return null;
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Progenitor.sln")))
            {
                string path = Path.Combine(dir.FullName, Path.Combine(parts));
                if (!File.Exists(path))
                    throw new FileNotFoundException(path);
                return File.ReadAllText(path);
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }
}
