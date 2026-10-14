using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Reload;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// 2026-10-05, security: mods come only from Thunderstore and a server never sends mod files. What is pinned here: the
/// retired transfer packets are dropped with one log line, the obsolete offer API is a no-op that logs once, the install
/// window opens only when every failing row is fixable on the client, and the one-time cleanup removes exactly what an
/// older Core installed from a server.
/// </summary>
[Collection("ModManifest")]
public sealed class NoServerDistributionTests : IDisposable
{
    private const string Sample = "progenitor.sample";
    private const string Package = "ExampleTeam-Progenitor_Sample";

    private readonly string _dir;

    public NoServerDistributionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-nodist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = "127.0.0.1:9976";
        Plugin.Logger = new TestLogSource();
    }

    public void Dispose()
    {
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = null;
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private static TestLogSource Log() => (TestLogSource)Plugin.Logger!;

    // ---- outcome 1: the retired packets ----------------------------------------------------------

    [Fact]
    public void A_retired_request_or_chunk_packet_is_dropped_with_one_log_line_per_kind()
    {
        ModManifest.DropRetiredPacketForTests(ModManifest.RequestEventId, 40);
        ModManifest.DropRetiredPacketForTests(ModManifest.RequestEventId, 40);
        ModManifest.DropRetiredPacketForTests(ModManifest.ChunkEventId, 1200);
        ModManifest.DropRetiredPacketForTests(ModManifest.ChunkEventId, 1200);

        Assert.Equal(2, ModManifest.TestRetiredLog.Count);
        Assert.Single(ModManifest.TestRetiredLog, l => l.Contains("PrmR", StringComparison.Ordinal));
        Assert.Single(ModManifest.TestRetiredLog, l => l.Contains("PrmC", StringComparison.Ordinal));
        // Nothing was staged or installed: there is no staging folder and no pending folder.
        Assert.False(Directory.Exists(Path.Combine(_dir, ModFolders.StagingFolderName)));
        Assert.False(Directory.Exists(Path.Combine(_dir, ModFolders.PendingFolderName)));
    }

    [Fact]
    public void The_client_never_registers_a_request_sender_and_the_server_never_a_chunk_sender()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "ModManifest.cs");
        Assert.DoesNotContain("SendToServer(RequestEventId", src, StringComparison.Ordinal);
        Assert.DoesNotContain("SendToUser(world, userEntity, ChunkEventId", src, StringComparison.Ordinal);
        SourcePin.AssertUncommented(src, "CustomEvents.OnReceive(ChunkEventId, OnRetiredChunkPacket)");
        SourcePin.AssertUncommented(src, "CustomEvents.OnReceiveFromClient(RequestEventId, OnRetiredRequestPacket)");
        Assert.False(File.Exists(Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core", "Networking", "ModManifestTransfer.cs")));
        Assert.False(File.Exists(Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core", "Networking", "ModManifestPromote.cs")));
    }

    // ---- outcome 3: the obsolete offer API ----------------------------------------------------------

    [Fact]
    public void The_obsolete_offer_is_a_no_op_that_logs_once_per_mod()
    {
        string dll = Path.Combine(_dir, "sample.dll");
        File.WriteAllBytes(dll, Encoding.UTF8.GetBytes("sample-package-bytes"));
#pragma warning disable CS0618 // the obsolete API is what this test pins
        using (var first = ModManifest.Offer(Sample, dll))
        using (var second = ModManifest.Offer(Sample, dll))
        {
            Assert.NotNull(first);
            Assert.False(ModManifest.TryGetOfferTransferState(Sample, out string sha, out long size, out string reason));
            Assert.Equal("", sha);
            Assert.Equal(0, size);
            Assert.Equal("removed", reason);
        }

        ModManifest.Offer("other.mod", dll).Dispose();
#pragma warning restore CS0618

        Assert.Equal(2, ModManifest.TestRetiredLog.Count);
        Assert.Contains(ModManifest.TestRetiredLog, l => l.Contains(Sample, StringComparison.Ordinal) && l.Contains("Thunderstore", StringComparison.Ordinal));
        Assert.Contains(ModManifest.TestRetiredLog, l => l.Contains("other.mod", StringComparison.Ordinal));

        // The catalog carries no hash or size for the mod: nothing is offered.
        ModManifest.TestIsServer = true;
        ModManifest.TestLoadedMods = new[]
        {
            new LoadedModInfo(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
        };
        _ = ModManifest.Describe();
        var row = Assert.Single(ModManifest.LastEnvelope!.Body.Mods);
        Assert.True(string.IsNullOrEmpty(row.Sha256));
        Assert.Null(row.Size);
    }

    [Fact]
    public void The_plugin_context_offer_methods_are_obsolete_no_ops()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Plugins", "PluginContext.cs");
        string offer = SourcePin.MethodBody(src, "public IDisposable OfferPackage(string path)");
        SourcePin.AssertUncommented(offer, "return ModManifest.Offer(_ctx.Guid, path);");
        int at = src.IndexOf("public IDisposable OfferPackage(string path)", StringComparison.Ordinal);
        Assert.Contains("[Obsolete(", src.Substring(Math.Max(0, at - 200), 200), StringComparison.Ordinal);
        string state = SourcePin.MethodBody(src, "public bool TryGetOfferedPackageState(");
        SourcePin.AssertUncommented(state, "reason = \"removed\";");
        SourcePin.AssertUncommented(state, "return false;");
    }

    // ---- outcome 4: the install window's trigger ------------------------------------------------------

    private static Handshake.ModRecord Must(string id, string version) =>
        Handshake.ModRecord.CreateDeclared(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);

    [Theory]
    [InlineData(1)] // MissFix.Thunderstore
    [InlineData(2)] // MissFix.Local
    public void A_miss_the_client_can_fix_defers_for_a_capable_client(int fixValue)
    {
        var fix = (MissFix)fixValue;
        var server = new[] { Must(Sample, "0.1.0") };
        var client = Array.Empty<Handshake.ModRecord>();
        var problems = Handshake.Compare.Evaluate(server, client, out var failedIds);

        var verdict = Handshake.Compare.DecideMiss(problems, failedIds, server, client, _ => fix, graceAvailable: true, clientCanInstall: true);

        Assert.True(verdict.Defer, verdict.Why);
        Assert.Equal(new[] { Sample }, verdict.Ids);
        Assert.Equal(new[] { fix }, verdict.Fixes);
    }

    /// <summary>
    /// A Core from before 2026-10-05 declares InstallWindow but never re-answers after a client-side fix: it would wait
    /// out the window and be kicked. Only a client that also declares ClientFix gets a window; the bit rides the same
    /// capability word, which older servers read and ignore.
    /// </summary>
    [Fact]
    public void Only_a_client_that_declares_client_fix_can_spend_an_install_window()
    {
        var old = Handshake.Capability.InstallWindow | Handshake.Capability.RemoteInstall | Handshake.Capability.JoinFlow
            | Handshake.Capability.JoinAdmin | Handshake.Capability.ClientPolicy;
        Assert.False(Handshake.Compare.CanSpendInstallWindow(old));
        Assert.False(Handshake.Compare.CanSpendInstallWindow(Handshake.Capability.ClientFix));
        Assert.False(Handshake.Compare.CanSpendInstallWindow(Handshake.Capability.None));
        Assert.True(Handshake.Compare.CanSpendInstallWindow(Handshake.SelfCapabilities));
        Assert.Equal(256u, (uint)Handshake.Capability.ClientFix);

        // Wire: this build's word carries the bit and reads back whole; the old word is unchanged.
        var row = Handshake.Compare.CreateCapabilityRecord(Handshake.SelfCapabilities);
        Assert.Equal(Handshake.SelfCapabilities, Handshake.Compare.ReadCapabilityBlob(row.Version));
        Assert.Equal("1c7", Handshake.Compare.CreateCapabilityRecord(old).Version);
        // CLIENTPOLICY3 added PolicyRecheck (512), chat command hints CommandHints (1024) and REQRULE2 PolicyRuleUpdates (8192);
        // mod compatibility C1/C2 added ModFeatures (32768) and ModMessages (65536); items added ItemStatSync (131072),
        // ItemLedgerChunks (262144) and ItemDefinitionPriority (2097152); C3 added VersionRanges (8388608); C4 added
        // ProgenitorExact (67108864); requests and subscriptions added ModExchange (134217728).
        Assert.Equal("1ca7a7c7", row.Version);

        // The handshake and the client policy both ask the same question.
        string hs = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "Handshake.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(hs, "private static bool TryDeferKick("),
            "clientCanInstall: Compare.CanSpendInstallWindow(clientCaps)");
        string policy = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "Handshake.ClientPolicy.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(policy, "private static bool TryDeferPolicy("),
            "if (!Compare.CanSpendInstallWindow(clientCaps))");
    }

    [Fact]
    public void A_miss_nothing_on_the_client_fixes_kicks_and_says_why()
    {
        var server = new[] { Must(Sample, "0.1.0"), Must("other.mod", "1.0") };
        var client = Array.Empty<Handshake.ModRecord>();
        var problems = Handshake.Compare.Evaluate(server, client, out var failedIds);

        var verdict = Handshake.Compare.DecideMiss(problems, failedIds, server, client,
            f => f.Id == Sample ? MissFix.Thunderstore : MissFix.None, graceAvailable: true, clientCanInstall: true);

        Assert.False(verdict.Defer);
        Assert.Contains("'other.mod' cannot be fixed during the join", verdict.Why, StringComparison.Ordinal);
        Assert.False(Handshake.Compare.DecideMiss(problems, failedIds, server, client, _ => MissFix.Thunderstore,
            graceAvailable: true, clientCanInstall: false).Defer);
    }

    [Fact]
    public void The_fix_row_round_trips_and_marks_only_asked_rows()
    {
        var answer = new List<Handshake.ModRecord>
        {
            Must("kept.mod", "1.0"),
            Must("missing.mod", ""),
            Must("off.mod", "2.0"),
        };
        var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kept.mod", "off.mod" };
        uint mask = InstallWindowFixes.LocalFixMask(answer, asked, g => g != "missing.mod");
        Assert.Equal(0b101u, mask);

        Assert.True(InstallWindowFixes.AppendFixRow(answer, mask));
        var row = answer[^1];
        Assert.Equal(InstallWindowFixes.FixGuid, row.Guid);
        Assert.True(Handshake.Compare.IsReservedGuid(row.Guid));
        Assert.Equal(mask, InstallWindowFixes.ReadFixBlob(row.Version));
        Assert.True(InstallWindowFixes.DeclaredLocal(answer, mask, "kept.mod"));
        Assert.False(InstallWindowFixes.DeclaredLocal(answer, mask, "missing.mod"));
        Assert.True(InstallWindowFixes.DeclaredLocal(answer, mask, "OFF.MOD"));

        // The bits are positions in the answer as sent (reserved rows included); a bit on a reserved row is no fix.
        var ids = InstallWindowFixes.DeclaredIds(answer, mask | (1u << (answer.Count - 1)));
        Assert.Equal(new[] { "kept.mod", "off.mod" }, ids.OrderBy(x => x, StringComparer.Ordinal));

        // Nothing to declare writes no row; a malformed blob reads as nothing declared.
        Assert.False(InstallWindowFixes.AppendFixRow(new List<Handshake.ModRecord>(), 0));
        Assert.Equal(0u, InstallWindowFixes.ReadFixBlob("2ff"));
        Assert.Equal(0u, InstallWindowFixes.ReadFixBlob("1zz"));
        Assert.Equal(0u, InstallWindowFixes.ReadFixBlob(""));
        // A throwing probe is "no fix".
        Assert.Equal(0u, InstallWindowFixes.LocalFixMask(answer, null, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void A_handshake_row_is_fixed_locally_first_else_only_by_a_package_serving_the_needed_version()
    {
        PackageVersionAvailability Avail(string package, string version, NetworkVersionRule rule) =>
            package == Package && version == "0.1.0" ? PackageVersionAvailability.Available : PackageVersionAvailability.VersionGone;

        Assert.Equal(MissFix.Local, InstallWindowFixes.ForHandshakeRow(Sample, true, null, "0.1.0", NetworkVersionRule.Exact, null));
        Assert.Equal(MissFix.Thunderstore, InstallWindowFixes.ForHandshakeRow(Sample, false, Package, "0.1.0", NetworkVersionRule.Exact, Avail));
        Assert.Equal(MissFix.None, InstallWindowFixes.ForHandshakeRow(Sample, false, Package, "0.2.0", NetworkVersionRule.Exact, Avail));
        Assert.Equal(MissFix.None, InstallWindowFixes.ForHandshakeRow(Sample, false, null, "0.1.0", NetworkVersionRule.Exact, Avail));
        // The index not read (no availability, or Unknown) is not a fix: the server kicks as before.
        Assert.Equal(MissFix.None, InstallWindowFixes.ForHandshakeRow(Sample, false, Package, "0.1.0", NetworkVersionRule.Exact, null));
        Assert.Equal(MissFix.None, InstallWindowFixes.ForHandshakeRow(Sample, false, Package, "0.1.0", NetworkVersionRule.Exact,
            (_, _, _) => PackageVersionAvailability.Unknown));
        Assert.Equal(MissFix.None, InstallWindowFixes.ForHandshakeRow(Sample, false, Package, "0.1.0", NetworkVersionRule.Exact,
            (_, _, _) => throw new InvalidOperationException()));
    }

    [Fact]
    public void The_server_reads_the_index_in_the_background_and_answers_unknown_until_it_has()
    {
        var gate = new TaskCompletionSource<JoinIndexLoad>();
        int loads = 0;
        var now = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        var availability = new ServerPackageAvailability((wanted, _) =>
        {
            Interlocked.Increment(ref loads);
            return gate.Task;
        }, () => now);

        Assert.Equal(PackageVersionAvailability.Unknown, availability.Availability(Package, "0.1.0", NetworkVersionRule.Exact));
        var running = availability.Want(new[] { Package });
        Assert.NotNull(running);
        Assert.Null(availability.Want(new[] { Package }));           // one read at a time
        Assert.Equal(PackageVersionAvailability.Unknown, availability.Availability(Package, "0.1.0", NetworkVersionRule.Exact));

        var versions = new Dictionary<string, PackageVersions>(StringComparer.OrdinalIgnoreCase)
        {
            [Package] = new PackageVersions(Package, "", new[] { "0.2.0", "0.1.0" }, null, null, local: true),
        };
        gate.SetResult(new JoinIndexLoad("[]", "", versions, ""));
        Assert.True(running!.Wait(5000));
        Assert.True(SpinWait.SpinUntil(() => availability.Running == null, 5000));

        Assert.Equal(PackageVersionAvailability.Available, availability.Availability(Package, "0.1.0", NetworkVersionRule.Exact));
        Assert.Equal(PackageVersionAvailability.VersionGone, availability.Availability(Package, "0.3.0", NetworkVersionRule.Exact));
        Assert.Equal(PackageVersionAvailability.Unknown, availability.Availability("Owner-Other", "1.0", NetworkVersionRule.Exact));
        Assert.Null(availability.Want(new[] { Package }));           // fresh, nothing new
        Assert.Equal(1, loads);
        // TSTRAFFIC1: the server reuses a read for 30 minutes (was 10); past that it reads again (a conditional check).
        now = now.AddMinutes(11);
        Assert.Null(availability.Want(new[] { Package }));           // still fresh at 11 minutes
        now = now.AddMinutes(20);
        Assert.Equal(TimeSpan.FromMinutes(30), ServerPackageAvailability.MaxAge);
        Assert.NotNull(availability.Want(new[] { Package }));        // stale: read again
    }

    /// <summary>A read that throws is not retried by every handshake: the next read waits <see cref="ServerPackageAvailability.RetryAfterFailure"/>.</summary>
    [Fact]
    public void A_failed_index_read_backs_off_and_disposing_releases_what_the_reader_owns()
    {
        int loads = 0;
        var now = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        var owned = new Disposable();
        var availability = new ServerPackageAvailability((_, _) =>
        {
            Interlocked.Increment(ref loads);
            throw new InvalidOperationException("no network");
        }, () => now, owned);

        var first = availability.Want(new[] { Package });
        Assert.NotNull(first);
        Assert.True(SpinWait.SpinUntil(() => availability.Running == null, 5000));
        Assert.Contains("no network", availability.LastError, StringComparison.Ordinal);

        Assert.Null(availability.Want(new[] { Package }));
        now = now.AddSeconds(30);
        Assert.Null(availability.Want(new[] { Package }));
        Assert.Equal(1, loads);
        now = now.Add(ServerPackageAvailability.RetryAfterFailure);
        Assert.NotNull(availability.Want(new[] { Package }));
        Assert.True(SpinWait.SpinUntil(() => availability.Running == null, 5000));
        Assert.Equal(2, loads);

        availability.Dispose();
        availability.Dispose();
        Assert.Equal(1, owned.Count);
        now = now.AddHours(1);
        Assert.Null(availability.Want(new[] { Package }));
        Assert.Equal(PackageVersionAvailability.Unknown, availability.Availability(Package, "0.1.0", NetworkVersionRule.Exact));
    }

    private sealed class Disposable : IDisposable
    {
        public int Count;

        public void Dispose() => Count++;
    }

    [Fact]
    public void A_client_policy_refusal_defers_only_when_every_problem_is_fixable_on_the_client()
    {
        PackageVersionAvailability Avail(string package, string version, NetworkVersionRule rule) =>
            package == "Owner-Robes" ? PackageVersionAvailability.Available : PackageVersionAvailability.NotListed;

        var policy = new ClientPolicyConfig { Mode = ClientPolicyMode.Approved, Unverified = UnverifiedPolicy.Allow };
        policy.Required.Add(new ClientModRow("robes.pack", "Owner-Robes", "1.2.0", ClientModRule.Exact, "Robes.", "Robe Pack"));
        var report = new ClientModReport();
        report.Entries.Add(new ClientModEntry("hot.mod", "Hot", "1.0", ClientModSources.Progenitor));
        var verdict = ClientModJudge.Judge(policy, report, new ClientModServerFacts());
        Assert.Equal(ClientJudgmentOutcome.Refuse, verdict.Outcome);

        Assert.True(InstallWindowFixes.PolicyFixable(verdict, policy, report, Avail, out var keys, out string why), why);
        Assert.Contains("robes.pack", keys);
        Assert.Contains("hot.mod", keys);

        // A not-allowed mod that loaded at start cannot be paused: no window.
        var startup = new ClientModReport();
        startup.Entries.Add(new ClientModEntry("cheaty.mod", "Cheaty", "1.0", ClientModSources.Chainloader));
        var refused = ClientModJudge.Judge(policy, startup, new ClientModServerFacts());
        Assert.False(InstallWindowFixes.PolicyFixable(refused, policy, startup, Avail, out _, out why));
        Assert.Contains("cheaty.mod", why, StringComparison.Ordinal);

        // A required mod Thunderstore does not serve: no window.
        var noTs = new ClientPolicyConfig();
        noTs.Required.Add(new ClientModRow("ui.mod", "Owner-Ui", "1.0", ClientModRule.Exact));
        var missing = ClientModJudge.Judge(noTs, new ClientModReport(), new ClientModServerFacts());
        Assert.False(InstallWindowFixes.PolicyFixable(missing, noTs, new ClientModReport(), Avail, out _, out why));
        Assert.Contains("ui.mod", why, StringComparison.Ordinal);
    }

    // ---- outcome 7: the one-time cleanup ----------------------------------------------------------

    private static string Sha(byte[] bytes) => ReloadAllowPolicy.Sha256Hex(bytes);

    [Fact]
    public void The_cleanup_deletes_only_the_bytes_a_server_sent_and_drops_the_record()
    {
        string reload = Path.Combine(_dir, "progenitor-reload");
        string pending = Path.Combine(_dir, "progenitor-pending");
        Directory.CreateDirectory(reload);
        byte[] sent = Encoding.UTF8.GetBytes("server-sent-bytes");
        byte[] other = Encoding.UTF8.GetBytes("the players own build");
        File.WriteAllBytes(Path.Combine(reload, "Sent.dll"), sent);
        File.WriteAllText(Path.Combine(reload, "Sent.dll" + LegacyServerMods.SessionMarkerSuffix), "");
        File.WriteAllBytes(Path.Combine(reload, "Mine.dll"), other);
        string hostDir = Path.Combine(pending, ModManifestIds.NormalizeHostKey("fp@host"));
        Directory.CreateDirectory(hostDir);
        File.WriteAllBytes(Path.Combine(hostDir, "Sent.dll"), sent);

        string record = Path.Combine(_dir, LegacyServerMods.RecordFileName);
        LegacyServerMods.WriteRecordAt(record, new[]
        {
            new InstalledServerMod("sent.mod", "1.0", "Sent.dll", Sha(sent), "fp@host", "1.2.3.4:9876", false, "2026-10-04T00:00:00Z"),
            // Same file name the player now uses for other bytes: never deleted.
            new InstalledServerMod("mine.mod", "1.0", "Mine.dll", Sha(sent), "fp@host", "1.2.3.4:9876", false, "2026-10-04T00:00:00Z"),
            // A path in the file name is never followed; the row's empty last columns still read.
            new InstalledServerMod("evil.mod", "1.0", "..\\Mine.dll", Sha(other), "fp@host", "", false, ""),
        });

        var info = new List<string>();
        var result = LegacyServerMods.CleanUp(record, pending, reload, Path.Combine(_dir, ReloadAllowPolicy.FileName), info.Add, info.Add);

        Assert.False(File.Exists(Path.Combine(reload, "Sent.dll")));
        Assert.False(File.Exists(Path.Combine(reload, "Sent.dll" + LegacyServerMods.SessionMarkerSuffix)));
        Assert.False(File.Exists(Path.Combine(hostDir, "Sent.dll")));
        Assert.Equal(other, File.ReadAllBytes(Path.Combine(reload, "Mine.dll")));
        Assert.Equal(3, result.RowsDropped);
        Assert.Equal(0, result.RowsKept);
        Assert.False(File.Exists(record));
        Assert.Contains(info, l => l.StartsWith(LegacyServerMods.LogPrefix, StringComparison.Ordinal) && l.Contains("sent by 1.2.3.4:9876", StringComparison.Ordinal));
        Assert.Contains(info, l => l.Contains("not the server's copy", StringComparison.Ordinal));

        // Run again (the next start): nothing to do, and silent.
        info.Clear();
        Assert.True(LegacyServerMods.CleanUp(record, pending, reload, "", info.Add, info.Add).Nothing);
        Assert.Empty(info);
    }

    [Fact]
    public void The_cleanup_removes_an_always_rows_allowlist_line_and_keeps_the_others()
    {
        string reload = Path.Combine(_dir, "progenitor-reload");
        Directory.CreateDirectory(reload);
        byte[] sent = Encoding.UTF8.GetBytes("server-sent-always");
        File.WriteAllBytes(Path.Combine(reload, "Sent.dll"), sent);
        string keep = new string('b', 64);
        string allow = Path.Combine(_dir, ReloadAllowPolicy.FileName);
        File.WriteAllText(allow, ReloadAllowPolicy.FormatSumsLine(Sha(sent), "Sent.dll") + "\n"
            + ReloadAllowPolicy.FormatSumsLine(keep, "Progenitor.dll") + "\n");
        string record = Path.Combine(_dir, LegacyServerMods.RecordFileName);
        LegacyServerMods.WriteRecordAt(record, new[]
        {
            new InstalledServerMod("sent.mod", "1.0", "Sent.dll", Sha(sent), "fp@host", "1.2.3.4:9876", true, ""),
        });

        LegacyServerMods.CleanUp(record, Path.Combine(_dir, "progenitor-pending"), reload, allow, null, null);

        string after = File.ReadAllText(allow);
        Assert.DoesNotContain(Sha(sent), after, StringComparison.Ordinal);
        Assert.Contains(keep, after, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(reload, "Sent.dll")));
    }

    /// <summary>
    /// A Thunderstore install the install ledger records with the same bytes under the same name is the player's install
    /// now: it and its allowlist row stay; only the record row goes.
    /// </summary>
    [Fact]
    public void The_cleanup_keeps_a_ledger_tracked_install_with_identical_bytes()
    {
        string game = Path.Combine(_dir, "game");
        string reload = Path.Combine(game, "BepInEx", "progenitor-reload");
        Directory.CreateDirectory(reload);
        byte[] bytes = Encoding.UTF8.GetBytes("same bytes from thunderstore");
        string file = Path.Combine(reload, "Same.dll");
        File.WriteAllBytes(file, bytes);
        new ServerInstallLedger("", "", "", DateTime.UtcNow, new[]
        {
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/Same.dll", Sha(bytes), bytes.Length,
                ServerInstallLedger.CategoryMod, "package:Owner-Same@1.0.0", DateTime.UtcNow),
        }).Write(game);
        string allow = Path.Combine(_dir, ReloadAllowPolicy.FileName);
        File.WriteAllText(allow, ReloadAllowPolicy.FormatSumsLine(Sha(bytes), "Same.dll") + "\n");
        string record = Path.Combine(_dir, LegacyServerMods.RecordFileName);
        LegacyServerMods.WriteRecordAt(record, new[]
        {
            new InstalledServerMod("same.mod", "1.0", "Same.dll", Sha(bytes), "fp@host", "1.2.3.4:9876", true, "2026-10-04T00:00:00Z"),
        });

        var lines = new List<string>();
        var result = LegacyServerMods.CleanUp(record, Path.Combine(game, "BepInEx", "progenitor-pending"), reload, allow,
            lines.Add, lines.Add, LegacyServerMods.LedgerShaLookup(game));

        Assert.True(File.Exists(file));
        Assert.Contains(Sha(bytes), File.ReadAllText(allow), StringComparison.Ordinal);
        Assert.Equal(1, result.RowsDropped);
        Assert.Empty(result.Deleted);
        Assert.False(File.Exists(record));
        Assert.Contains(lines, l => l.Contains("the install ledger records it", StringComparison.Ordinal));

        // Without the ledger the same bytes are the server's copy and go.
        LegacyServerMods.WriteRecordAt(record, new[]
        {
            new InstalledServerMod("same.mod", "1.0", "Same.dll", Sha(bytes), "fp@host", "1.2.3.4:9876", false, ""),
        });
        LegacyServerMods.CleanUp(record, Path.Combine(game, "BepInEx", "progenitor-pending"), reload, allow, null, null, null);
        Assert.False(File.Exists(file));
    }

    /// <summary>A record row without a sha256 cannot tell the server's bytes from anything else: nothing is deleted and the row stays.</summary>
    [Fact]
    public void A_record_row_without_a_sha256_deletes_nothing_and_is_kept()
    {
        string reload = Path.Combine(_dir, "progenitor-reload");
        Directory.CreateDirectory(reload);
        File.WriteAllBytes(Path.Combine(reload, "NoSha.dll"), Encoding.UTF8.GetBytes("unknown bytes"));
        string record = Path.Combine(_dir, LegacyServerMods.RecordFileName);
        LegacyServerMods.WriteRecordAt(record, new[]
        {
            new InstalledServerMod("nosha.mod", "1.0", "NoSha.dll", "", "fp@host", "1.2.3.4:9876", false, "2026-10-04T00:00:00Z"),
        });

        var warnings = new List<string>();
        var result = LegacyServerMods.CleanUp(record, Path.Combine(_dir, "progenitor-pending"), reload, "", null, warnings.Add);

        Assert.True(File.Exists(Path.Combine(reload, "NoSha.dll")));
        Assert.Equal(1, result.RowsKept);
        Assert.Equal(0, result.RowsDropped);
        Assert.Single(LegacyServerMods.ReadRecord(record));
        Assert.Contains(warnings, w => w.Contains("no sha256", StringComparison.Ordinal));
    }

    [Fact]
    public void The_cleanup_runs_once_per_core_generation_from_the_live_start()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "ModManifest.cs");
        SourcePin.AssertUncommented(src, "CleanUpLegacyServerModsOnce();");
        string live = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "ModVersionLive.cs");
        int forget = live.IndexOf("ForgetServerCopies()", StringComparison.Ordinal);
        int repair = live.IndexOf("RepairMissingActive()", StringComparison.Ordinal);
        Assert.True(forget >= 0 && repair >= 0 && forget < repair, "the server's kept copies are forgotten before the repair runs");
    }
}

/// <summary>
/// 2026-10-05: inside the install window a Thunderstore batch that loaded everything answers the handshake again once
/// the installed handshake mods declare themselves, so the server's window closes on its own.
/// </summary>
[Collection("ModManifest")]
public sealed class WindowThunderstoreReAnswerTests : IDisposable
{
    private const string Sample = "progenitor.sample";
    private const string Package = "ExampleTeam-Progenitor_Sample";
    private readonly string _dir;
    private readonly UiTests.FakeUiRuntime _runtime = new() { IsClient = true };
    private readonly Installer _installer = new();
    private readonly Func<bool> _defaultReAnswer = JoinFlowClient.ReAnswerHandshake;
    private readonly Func<IReadOnlyList<Handshake.ModRecord>> _defaultProvided = ModVersionJoin.Provided;
    private readonly Func<bool> _defaultAccepted = JoinFlowClient.JoinAccepted;
    private double _now = 100;
    private int _reAnswers;
    private bool _declared;

    public WindowThunderstoreReAnswerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-window-ts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Plugin.Logger = new TestLogSource();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = "127.0.0.1:9976";
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        JoinFlowPanel.Install();
        JoinFlowClient.Clock = () => _now;
        JoinFlowClient.InstallerFactory = _ => _installer;
        JoinFlowClient.TargetProbe = () => new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "Lidgren" };
        JoinFlowClient.ReAnswerHandshake = () =>
        {
            Interlocked.Increment(ref _reAnswers);
            return true;
        };
        JoinFlowClient.JoinAccepted = () => false;
        ModVersionJoin.Provided = () => _declared
            ? new[] { Handshake.ModRecord.CreateDeclared(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) }
            : Array.Empty<Handshake.ModRecord>();
        ClientInstallWindow.ResetForTests();
        ClientInstallWindow.TestHost = new WindowHost();
    }

    public void Dispose()
    {
        JoinFlowPanel.Uninstall();
        JoinFlowClient.Uninstall();
        JoinFlowClient.ReAnswerHandshake = _defaultReAnswer;
        JoinFlowClient.JoinAccepted = _defaultAccepted;
        ModVersionJoin.Provided = _defaultProvided;
        JoinFlowClient.Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        ClientInstallWindow.ResetForTests();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestConnectHost = null;
        ModManifestDeny.TestServerAsked = null;
        ModManifestDeny.TestProvided = null;
        UiApi.ResetForTests();
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private sealed class WindowHost : IModInstallWindowHost
    {
        public void Info(string line) { }

        public void Warn(string line) { }
    }

    private sealed class Installer : IJoinPackageInstaller
    {
        public Task<JoinInstallOutcome> InstallAsync(JoinInstallRequest request, Action<JoinInstallPhase, int> progress, CancellationToken ct)
        {
            progress(JoinInstallPhase.Downloading, 40);
            return Task.FromResult(new JoinInstallOutcome(JoinInstallPhase.Loaded, "", Sample, "x"));
        }
    }

    private static byte[] Descriptor()
    {
        using var key = ModManifestCrypto.Generate();
        var overrides = new ManifestOverrides
        {
            ServerName = "Night Garden",
            Mods = new[] { new ManifestModOverride { Id = Sample, Version = "0.1.0", Package = Package } },
        };
        var loaded = new[] { new LoadedModInfo(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true) };
        var envelope = ModManifest.Build(loaded, overrides, key, DateTime.UtcNow, null);
        return ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(envelope));
    }

    [Fact]
    public void A_window_batch_that_loaded_everything_re_answers_once_the_mod_is_declared()
    {
        ModManifestDeny.TestServerAsked = new[] { Handshake.ModRecord.CreateDeclared(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) };
        ModManifestDeny.TestProvided = Array.Empty<Handshake.ModRecord>();
        Assert.Equal(DeferredRoute.Opened, ClientInstallWindow.OnResult(Handshake.ResultDeferred,
            Handshake.Compare.DeferredResultMessage(new[] { Sample }, 60)));
        Assert.True(JoinFlowClient.InstallWindowOpen);

        ModManifest.AcceptEnvelope(Descriptor());
        var session = JoinFlowClient.Session!;
        Assert.Equal(JoinMode.InstallWindow, session.Mode);
        if (session.Page == JoinPage.Message) session.Continue();

        Assert.Equal("ts-trust", JoinFlowTestBridge.Install()["result"]);
        Assert.Equal("install", JoinFlowTestBridge.TsInstall()["result"]);
        Assert.True(SpinWait.SpinUntil(() => JoinFlowClient.InstallStateOf(Sample)?.Terminal == true, 5000));
        Thread.Sleep(20);
        _now += 1;
        JoinFlowClient.Tick();

        // Loaded, but not declared yet: the client waits rather than answering with the old rows.
        Assert.Equal(new[] { Sample }, JoinFlowClient.WindowReAnswerPending);
        Assert.Equal(0, _reAnswers);

        _declared = true;
        _now += 0.1;
        JoinFlowClient.Tick();
        Assert.Equal(1, _reAnswers);
        Assert.Empty(JoinFlowClient.WindowReAnswerPending);
        Assert.Contains(((TestLogSource)Plugin.Logger!).Infos, l => l.Contains("re-answered the handshake after the Thunderstore install", StringComparison.Ordinal));

        // Once: later ticks do not answer again.
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Equal(1, _reAnswers);
    }

    // A6: open the window, install, and tick until the batch is done (not yet declared).
    private JoinFlowSession InstallInWindow()
    {
        ModManifestDeny.TestServerAsked = new[] { Handshake.ModRecord.CreateDeclared(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) };
        ModManifestDeny.TestProvided = Array.Empty<Handshake.ModRecord>();
        ClientInstallWindow.OnResult(Handshake.ResultDeferred, Handshake.Compare.DeferredResultMessage(new[] { Sample }, 60));
        ModManifest.AcceptEnvelope(Descriptor());
        var session = JoinFlowClient.Session!;
        if (session.Page == JoinPage.Message) session.Continue();
        JoinFlowTestBridge.Install();
        JoinFlowTestBridge.TsInstall();
        Assert.True(SpinWait.SpinUntil(() => JoinFlowClient.InstallStateOf(Sample)?.Terminal == true, 5000));
        Thread.Sleep(20);
        _now += 1;
        JoinFlowClient.Tick();
        return JoinFlowClient.Session!;
    }

    /// <summary>
    /// A6 (live 2026-10-07): the page said "the join continues by itself" while nothing had reached the server. It says
    /// so only once the answer went out, in the success tone; before that it says the server is being told.
    /// </summary>
    [Fact]
    public void A6_the_window_page_says_the_join_continues_only_once_the_answer_went_out()
    {
        var session = InstallInWindow();
        Assert.Contains(JoinText.WindowTellingNote, session.Notice, StringComparison.Ordinal);
        Assert.True(session.NoticeGood);
        _declared = true;
        _now += 0.1;
        JoinFlowClient.Tick();
        Assert.Equal(1, _reAnswers);
        Assert.Equal(JoinText.WindowLoadedNote, JoinFlowClient.Session!.Notice);
        Assert.True(JoinFlowClient.Session!.NoticeGood);
        Assert.Equal(true, JoinFlowTestBridge.Read()["noticeGood"]);
    }

    /// <summary>A6: an answer that did not go out: the page says the server was not told, and Join again.</summary>
    [Fact]
    public void A6_an_answer_that_did_not_go_out_offers_join_again()
    {
        JoinFlowClient.ReAnswerHandshake = () => false;
        InstallInWindow();
        _declared = true;
        _now += 0.1;
        JoinFlowClient.Tick();
        var read = JoinFlowTestBridge.Read();
        Assert.Equal("result", read["page"]);
        var result = (Dictionary<string, object?>)read["result"]!;
        Assert.Equal(true, result["joinAgain"]);
        Assert.Equal("join-now", result["kind"]);
        Assert.DoesNotContain("continues by itself", (string)read["notice"]!, StringComparison.Ordinal);
        Assert.Contains("could not be told on this connection", (string)read["notice"]!, StringComparison.Ordinal);
        Assert.Equal(JoinText.JoinAgainButton, JoinFlowLayout.ResultButtons(JoinFlowClient.Session!.Result!).Last().Caption);
    }

    /// <summary>
    /// A6 (live 2026-10-07): the server ended the join the window held: the page shows its reason and, with the fix in
    /// place, Join again; never "the join continues".
    /// </summary>
    [Fact]
    public void A6_a_refusal_ends_the_window_page_with_the_reason_and_join_again()
    {
        InstallInWindow();
        ModManifestDeny.TestProvided = new[] { Handshake.ModRecord.CreateDeclared(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) };
        string reason = Handshake.Compare.NotInstalledReason(new[] { Sample }, 60);
        Assert.True(JoinFlowClient.EndWindow(reason));
        var session = JoinFlowClient.Session!;
        Assert.Equal(JoinMode.Refused, session.Mode);
        Assert.Equal(JoinPage.Result, session.Page);
        Assert.True(session.Result!.JoinAgain);
        Assert.Contains("ended this join: Mod mismatch: " + Sample + " was not confirmed within 60 s", session.Notice, StringComparison.Ordinal);
        Assert.Contains("joining again finishes it", session.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain("continues by itself", session.Notice, StringComparison.Ordinal);
        // Not a window any more: a second end does nothing.
        Assert.False(JoinFlowClient.EndWindow(reason));
    }
}
