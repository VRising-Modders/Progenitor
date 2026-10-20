using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.SelfUpdate;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C4 (docs/mod-compatibility.md, C4 as built), game-free: under <b>Exact version for all</b> the server holds
/// players to its own Progenitor build (Core version and sha256) while that build is published on Thunderstore; the
/// catalog's Progenitor form carries the requirement; the join screens draw a Progenitor row; and the C3 wording fix (the
/// rows say <c>Needs exactly 1.2.0</c>, <c>Needs 1.2.x</c>, <c>Needs 1.2.0 or newer</c>).
/// </summary>
[Collection("ModManifest")]
public sealed class ProgenitorExactTests : IDisposable
{
    private const string Release = "2026.10.159";
    private static readonly string ServerSha = new('a', 64);
    private static readonly string OtherSha = new('b', 64);
    private const string Package = "Progenitor-Progenitor";
    private const string Trade = "example.trade";
    private static readonly DateTime Issued = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly ModManifestKey _key = ModManifestCrypto.Generate();

    public ProgenitorExactTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-c4-" + Guid.NewGuid().ToString("N"));
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
        ProgenitorExact.StatusSource = null;
        ProgenitorExactSource.Reset();
        ProgenitorExactSource.Mode = () => VersionMatching.Current().Mode;
        ProgenitorExactSource.Facts = () => SelfUpdates.Facts();
        ProgenitorExactSource.Releases = () => SelfUpdates.Releases;
        ProgenitorExactSource.Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        ProgenitorExactSource.Log = null;
        VersionMatching.SettingsSource = null;
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = null;
        Plugin.Logger = null;
        _key.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    // ---- the server's own build ------------------------------------------------------------------------------

    /// <summary>A server running the release <see cref="Release"/> that Progenitor installed (the self-install record names its Core).</summary>
    private static SelfFacts ServerFacts(string version = Release, bool record = true) => new()
    {
        CoreVersion = version,
        BootstrapVersion = version,
        CoreSha = ServerSha,
        BootstrapSha = new string('c', 64),
        Record = record ? new SelfInstallRecord { Package = Package, Version = Release, CoreSha = ServerSha, BootstrapSha = new string('c', 64) } : null,
    };

    private static List<SelfRelease> Listed(params string[] versions) =>
        versions.Select(v => new SelfRelease { Package = Package, Version = v, DownloadUrl = "https://thunderstore.io/x/" + v + "/" }).ToList();

    private static ProgenitorExactStatus Enforced() => ProgenitorExact.StatusOf(VersionMatchingMode.Exact, ServerFacts(), Listed(Release, "2026.10.160"));

    [Fact]
    public void Exact_holds_players_to_the_servers_published_release_and_the_other_modes_never_do()
    {
        var exact = Enforced();
        Assert.True(exact.Obtainable);
        Assert.True(exact.Enforced);
        Assert.Equal(Release, exact.Version);
        Assert.Equal(ServerSha, exact.Sha);
        Assert.True(exact.Requirement!.Valid);
        Assert.Equal("", exact.NotEnforcedLine);

        // Declared ranges and Same minor for all: Progenitor's protocols and capability bits decide, as before (dates have no
        // meaningful minor).
        foreach (var mode in new[] { VersionMatchingMode.Range, VersionMatchingMode.Minor })
        {
            var s = ProgenitorExact.StatusOf(mode, ServerFacts(), Listed(Release));
            Assert.True(s.Obtainable);
            Assert.False(s.Enforced);
            Assert.Null(s.Requirement);
            Assert.Null(ProgenitorExact.Check(s, sentTable: true, "2026.10.158", OtherSha));
            Assert.Equal("", s.NotEnforcedLine);
        }
    }

    [Fact]
    public void Exact_is_not_enforced_for_Progenitor_while_the_servers_build_cannot_be_fetched()
    {
        var dev = ProgenitorExact.StatusOf(VersionMatchingMode.Exact, ServerFacts("2026.10.159.3"), Listed(Release));
        Assert.False(dev.Enforced);
        Assert.Equal("Exact is not enforced for Progenitor: 2026.10.159.3 is a development build", dev.NotEnforcedLine);

        var hand = ProgenitorExact.StatusOf(VersionMatchingMode.Exact, ServerFacts(record: false), Listed(Release));
        Assert.False(hand.Enforced);
        Assert.Contains("has no install record from a published release", hand.Why);

        var gone = ProgenitorExact.StatusOf(VersionMatchingMode.Exact, ServerFacts(), Listed("2026.10.160"));
        Assert.Equal("Exact is not enforced for Progenitor: 2026.10.159 is not in Progenitor's release list", gone.NotEnforcedLine);

        var inactive = Listed(Release);
        inactive[0].Active = false;
        Assert.False(ProgenitorExact.StatusOf(VersionMatchingMode.Exact, ServerFacts(), inactive).Enforced);

        var unread = ProgenitorExact.StatusOf(VersionMatchingMode.Exact, ServerFacts(), new List<SelfRelease>());
        Assert.Contains("has not read Progenitor's release list yet", unread.Why);

        var unknownSha = ServerFacts();
        unknownSha.CoreSha = "";
        Assert.Equal("its Core image could not be read", ProgenitorExact.StatusOf(VersionMatchingMode.Exact, unknownSha, Listed(Release)).Why);

        // A record for another Core (the files changed since) does not name this build.
        var changed = ServerFacts();
        changed.Record = new SelfInstallRecord { Package = Package, Version = Release, CoreSha = OtherSha, BootstrapSha = new string('c', 64) };
        Assert.False(ProgenitorExact.StatusOf(VersionMatchingMode.Exact, changed, Listed(Release)).Enforced);

        // The update on trial (before its healthy mark writes the record) is the release that runs.
        var trial = ServerFacts(record: false);
        trial.State = new SelfState { Journal = new SelfJournal { State = SelfJournal.Trial, Version = Release, Package = Package, NewCore = ServerSha } };
        Assert.True(ProgenitorExact.StatusOf(VersionMatchingMode.Exact, trial, Listed(Release)).Enforced);
    }

    [Fact]
    public void The_server_compares_the_clients_core_sha_and_says_why_in_the_C3_style()
    {
        var s = Enforced();
        Assert.Null(ProgenitorExact.Check(s, sentTable: true, Release, ServerSha.ToUpperInvariant()));
        Assert.Equal("Progenitor: this server requires exactly Progenitor 2026.10.159 (you have 2026.10.158)",
            ProgenitorExact.Check(s, sentTable: true, "2026.10.158", OtherSha));
        Assert.Equal("Progenitor: this server requires exactly Progenitor 2026.10.159 (you have 2026.10.160)",
            ProgenitorExact.Check(s, sentTable: true, "2026.10.160", OtherSha));
        // The version alone is not enough: two builds of one release are two builds.
        Assert.Equal("Progenitor: this server requires exactly Progenitor 2026.10.159 (you have another build of 2026.10.159)",
            ProgenitorExact.Check(s, sentTable: true, Release, OtherSha));
        Assert.Equal("Progenitor: this server requires exactly Progenitor 2026.10.159 (you have 2026.10.159.4)",
            ProgenitorExact.Check(s, sentTable: true, "2026.10.159.4", OtherSha));
        // An older client (no feature table) cannot say which build it runs.
        Assert.Equal("Progenitor: this server requires exactly Progenitor 2026.10.159 (your Progenitor is too old to say which build it runs; update Progenitor to join)",
            ProgenitorExact.Check(s, sentTable: false, "", ""));
        // A client that sends a table but whose table missed the join hold is told to join again, not to update.
        Assert.Equal("Progenitor: this server requires exactly Progenitor 2026.10.159 (your Progenitor did not say which build it runs in time; join again)",
            ProgenitorExact.Check(s, sentTable: false, "", "", declaredTable: true));
        Assert.Equal("Progenitor: this server requires exactly Progenitor 2026.10.159 (you have 2026.10.159, but it did not say which build)",
            ProgenitorExact.Check(s, sentTable: true, Release, ""));
        // Not enforced: nobody is refused over Progenitor.
        Assert.Null(ProgenitorExact.Check(ProgenitorExactStatus.Off, sentTable: false, "", ""));
    }

    [Fact]
    public void A_join_waits_for_the_feature_table_of_a_C1_client_only_while_Exact_applies_to_Progenitor()
    {
        Assert.True(ProgenitorExact.HoldsForTable(Handshake.Capability.VersionRanges, progenitorExact: false));
        Assert.True(ProgenitorExact.HoldsForTable(Handshake.Capability.ModFeatures, progenitorExact: true));
        Assert.False(ProgenitorExact.HoldsForTable(Handshake.Capability.ModFeatures, progenitorExact: false));
        Assert.False(ProgenitorExact.HoldsForTable(Handshake.Capability.None, progenitorExact: true));
        Assert.Equal(67108864u, (uint)Handshake.Capability.ProgenitorExact);
        Assert.True((Handshake.SelfCapabilities & Handshake.Capability.ProgenitorExact) != 0);
    }

    [Fact]
    public void The_handshake_refuses_with_the_progenitor_row_and_never_opens_an_install_window_for_it()
    {
        string hs = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "Handshake.cs");
        string body = SourcePin.MethodBody(hs, "private static void JudgeAnswer(");
        SourcePin.AssertStatementExactly(body, "string? coreRefusal = ProgenitorExact.Check(status, core.Table, core.Core, core.Sha, (clientCaps & Capability.ModFeatures) != 0);");
        SourcePin.AssertStatementExactly(body, "failedIds.Add(ProgenitorExact.RowId);");
        // The install window needs every failing id to be a row this server enforces; "progenitor" never is.
        var verdict = Handshake.Compare.DecideMiss(
            new[] { "Progenitor: this server requires exactly Progenitor 2026.10.159 (you have 2026.10.158)" },
            new[] { ProgenitorExact.RowId },
            Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>(),
            _ => MissFix.Thunderstore, graceAvailable: true, clientCanInstall: true);
        Assert.False(verdict.Defer);
    }

    [Fact]
    public void Right_after_a_core_load_an_unread_image_is_pending_and_a_join_waits_until_the_status_is_known()
    {
        // Live 2026-10-10: the first read after a server Core load said "its Core image could not be read" and a join in
        // that window was not held to the server's build.
        var logs = new List<string>();
        double now = 100;
        var facts = ServerFacts();
        facts.CoreSha = "";
        int reads = 0;
        ProgenitorExactSource.Mode = () => VersionMatchingMode.Exact;
        ProgenitorExactSource.Facts = () => { reads++; return facts; };
        ProgenitorExactSource.Releases = () => Listed(Release);
        ProgenitorExactSource.Clock = () => now;
        ProgenitorExactSource.Log = logs.Add;
        ProgenitorExactSource.Install();

        var pending = ProgenitorExact.ServerStatus();
        Assert.True(pending.Pending);
        Assert.False(pending.Enforced);
        Assert.True(pending.HoldsJoins);
        Assert.Equal(ProgenitorExact.PendingWhy, pending.Why);
        Assert.Equal("", pending.NotEnforcedLine);
        Assert.Empty(logs);
        // Not cached: the next read asks again.
        ProgenitorExact.ServerStatus();
        Assert.Equal(2, reads);

        // A join answered now waits; its judgment runs once the image is read, with the enforced status.
        var judged = new List<ProgenitorExactStatus>();
        var later = new Queue<Action>();
        ProgenitorExact.JudgeWhenKnown(pending, ProgenitorExact.ServerStatus, judged.Add, later.Enqueue);
        Assert.Empty(judged);
        now += ProgenitorExact.PendingRetrySeconds;
        later.Dequeue()();
        Assert.Empty(judged);
        facts.CoreSha = ServerSha;
        later.Dequeue()();
        Assert.Single(judged);
        Assert.True(judged[0].Enforced);
        Assert.Empty(later);
        Assert.NotNull(ProgenitorExact.Check(judged[0], sentTable: true, "2026.10.160", OtherSha));
        Assert.StartsWith("VersionMatching: Exact applies to Progenitor", logs.Single());

        // Past the startup window an unreadable image is final: not enforced, logged, and a join is judged at once.
        ProgenitorExactSource.Reset();
        ProgenitorExactSource.Log = logs.Add;
        ProgenitorExactSource.Install();
        facts.CoreSha = "";
        now += ProgenitorExactSource.PendingSeconds;
        var final = ProgenitorExact.ServerStatus();
        Assert.False(final.Pending);
        Assert.False(final.HoldsJoins);
        Assert.Equal("Exact is not enforced for Progenitor: its Core image could not be read", final.NotEnforcedLine);
        judged.Clear();
        ProgenitorExact.JudgeWhenKnown(final, ProgenitorExact.ServerStatus, judged.Add, later.Enqueue);
        Assert.Single(judged);
        Assert.Empty(later);

        // A join answered while pending is judged on the final status once the window ends (never waits forever).
        ProgenitorExactSource.Reset();
        ProgenitorExactSource.Install();
        var early = ProgenitorExact.ServerStatus();
        Assert.True(early.Pending);
        judged.Clear();
        ProgenitorExact.JudgeWhenKnown(early, ProgenitorExact.ServerStatus, judged.Add, later.Enqueue);
        now += ProgenitorExactSource.PendingSeconds;
        later.Dequeue()();
        Assert.Single(judged);
        Assert.False(judged[0].Pending);
        Assert.False(judged[0].Enforced);

        // Review fix: a wait for an answer that was superseded (another answer, a disconnect) ends without judging.
        ProgenitorExactSource.Reset();
        ProgenitorExactSource.Install();
        var gens = new AnswerGenerations();
        int g1 = gens.Next(7);
        judged.Clear();
        later.Clear();
        ProgenitorExact.JudgeWhenKnown(ProgenitorExact.ServerStatus(), ProgenitorExact.ServerStatus, judged.Add, later.Enqueue,
            () => gens.IsCurrent(7, g1));
        int g2 = gens.Next(7);
        Assert.False(gens.IsCurrent(7, g1));
        Assert.True(gens.IsCurrent(7, g2));
        facts.CoreSha = ServerSha;
        later.Dequeue()();
        Assert.Empty(judged);
        Assert.Empty(later);
        gens.Forget(7);
        Assert.False(gens.IsCurrent(7, g2));
        facts.CoreSha = "";

        // Under the other modes nothing waits.
        Assert.False(ProgenitorExact.StatusOf(VersionMatchingMode.Range, facts, Listed(Release), starting: true).HoldsJoins);
        ProgenitorExactSource.Uninstall();
    }

    [Fact]
    public void The_status_source_caches_reads_the_mode_on_every_call_and_logs_a_change_once()
    {
        var logs = new List<string>();
        double now = 100;
        var mode = VersionMatchingMode.Exact;
        int reads = 0;
        ProgenitorExactSource.Mode = () => mode;
        ProgenitorExactSource.Facts = () => { reads++; return ServerFacts(); };
        ProgenitorExactSource.Releases = () => Listed(Release);
        ProgenitorExactSource.Clock = () => now;
        ProgenitorExactSource.Log = logs.Add;
        ProgenitorExactSource.Install();

        Assert.True(ProgenitorExact.ServerStatus().Enforced);
        Assert.True(ProgenitorExact.ServerStatus().Enforced);
        Assert.Equal(1, reads);
        Assert.Single(logs);
        Assert.StartsWith("VersionMatching: Exact applies to Progenitor: players need exactly 2026.10.159", logs[0]);

        // An admin's change applies at once (the mode is not cached).
        mode = VersionMatchingMode.Range;
        Assert.False(ProgenitorExact.ServerStatus().Enforced);
        now += ProgenitorExactSource.CacheSeconds + 1;
        mode = VersionMatchingMode.Exact;
        ProgenitorExactSource.Releases = () => Listed("2026.10.160");
        Assert.False(ProgenitorExact.ServerStatus().Enforced);
        Assert.Equal("VersionMatching: Exact is not enforced for Progenitor: 2026.10.159 is not in Progenitor's release list", logs.Last());
        Assert.Equal("Exact is not enforced for Progenitor: 2026.10.159 is not in Progenitor's release list", ProgenitorExactSource.LineIfExact());

        ProgenitorExactSource.Uninstall();
        Assert.Null(ProgenitorExact.StatusSource);
        Assert.False(ProgenitorExact.ServerStatus().Enforced);
    }

    // ---- the catalog's Progenitor form ----------------------------------------------------------------------

    private static LoadedModInfo[] Loaded() => new[]
    {
        new LoadedModInfo(Trade, "1.4.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
    };

    private (ManifestEnvelope Current, ManifestEnvelope Extended, ManifestEnvelope Progenitor) Forms(ProgenitorRequirement? req) =>
        ModManifest.BuildForms(Loaded(), new ManifestOverrides { ServerName = "Night Garden" }, _key, Issued, null, _ => ">=1.2.0 <2.0.0",
            new VersionMatchSettings(VersionMatchingMode.Exact), req);

    private static bool Verifies(ManifestBody body, string signature) =>
        ModManifestCrypto.Verify(body.Operator.Spki, ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(body)), signature);

    [Fact]
    public void The_progenitor_form_signs_the_servers_build_and_the_older_forms_are_unchanged()
    {
        var (current, extended, progenitor) = Forms(new ProgenitorRequirement(Release, ServerSha));
        Assert.True(Verifies(progenitor.Body, progenitor.Signature));
        Assert.Equal(Release, progenitor.Body.Progenitor);
        Assert.Equal(ServerSha, progenitor.Body.ProgenitorSha);
        string canonical = ModManifestJson.CanonicalBody(progenitor.Body);
        Assert.Contains("\"profileCode\":null,\"progenitor\":\"2026.10.159\",\"progenitorSha\":\"" + ServerSha + "\",\"schema\":1", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("progenitor", ModManifestJson.CanonicalBody(extended.Body), StringComparison.Ordinal);
        Assert.DoesNotContain("progenitor", ModManifestJson.CanonicalBody(current.Body), StringComparison.Ordinal);
        Assert.Equal(extended.Body.Issued, progenitor.Body.Issued);

        // A C3 client re-canonicalizes without the keys it does not know: it would reject this form, so it never gets it.
        Assert.True(ModManifestJson.TryParseEnvelope(ModManifestJson.CanonicalEnvelope(progenitor), out var parsed, out var error), error);
        Assert.Equal(Release, parsed.Body.Progenitor);
        Assert.True(Verifies(parsed.Body, parsed.Signature));
        var c3View = new ManifestBody(parsed.Body.Schema, parsed.Body.ServerName, parsed.Body.Issued, parsed.Body.Operator, parsed.Body.ProfileCode, parsed.Body.Mods)
        {
            Matching = parsed.Body.Matching,
        };
        Assert.False(Verifies(c3View, parsed.Signature));
        // A tampered build fails the signature.
        Assert.True(ModManifestJson.TryParseEnvelope(ModManifestJson.CanonicalEnvelope(progenitor).Replace(ServerSha, OtherSha, StringComparison.Ordinal), out var tampered, out _));
        Assert.False(Verifies(tampered.Body, tampered.Signature));

        // No requirement: the Progenitor form is the extended envelope itself.
        var (_, ext2, prog2) = Forms(null);
        Assert.Same(ext2, prog2);
        Assert.Equal("", ModCatalogForms.ExtensionStamp(ext2.Body).Contains("progenitor=", StringComparison.Ordinal) ? "x" : "");
        Assert.Contains("progenitor=2026.10.159|" + ServerSha + ";", ModCatalogForms.ExtensionStamp(progenitor.Body), StringComparison.Ordinal);
    }

    [Fact]
    public void Each_user_gets_the_form_its_capabilities_read_and_a_C4_client_accepts_it()
    {
        ProgenitorExact.StatusSource = Enforced;
        VersionMatching.SettingsSource = () => new VersionMatchSettings(VersionMatchingMode.Exact);
        ModManifest.TestLoadedMods = Loaded();
        ModManifest.TestIsServer = true;
        ModManifest.TestRangeUsers.Add(5);
        ModManifest.TestRangeUsers.Add(6);
        ModManifest.TestProgenitorUsers.Add(5);

        ModManifest.OnHandshakeOk(5);
        ModManifest.OnHandshakeOk(6);
        ModManifest.OnHandshakeOk(7);
        var toC4 = ModManifest.TestSent.Single(s => s.User == 5).Envelope;
        var toC3 = ModManifest.TestSent.Single(s => s.User == 6).Envelope;
        var toOld = ModManifest.TestSent.Single(s => s.User == 7).Envelope;
        Assert.Same(ModManifest.ProgenitorEnvelope, toC4);
        Assert.Same(ModManifest.ExtendedEnvelope, toC3);
        Assert.Same(ModManifest.LastEnvelope, toOld);
        Assert.Equal(Release, toC4.Body.Progenitor);
        Assert.Null(toC3.Body.Progenitor);
        Assert.Null(toOld.Body.Progenitor);

        // The client's accept path verifies the Progenitor form and keeps the requirement.
        string json = ModManifestJson.CanonicalEnvelope(toC4);
        ModManifest.TestIsServer = false;
        ModManifest.ClearJoinState();
        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(json));
        Assert.True(ModManifest.Pending!.SignatureValid);
        var req = ProgenitorExact.RequirementOf(ModManifest.LastEnvelope!.Body);
        Assert.NotNull(req);
        Assert.Equal(Release, req!.Version);
        Assert.Equal(ServerSha, req.Sha);
    }

    [Fact]
    public void Turning_exact_on_or_the_build_becoming_unobtainable_re_signs_and_asks_for_a_resend()
    {
        ModManifest.TestLoadedMods = Loaded();
        ModManifest.TestIsServer = true;
        var status = ProgenitorExactStatus.Off;
        ProgenitorExact.StatusSource = () => status;
        _ = ModManifest.Describe();
        var before = ModManifest.ProgenitorEnvelope!;
        Assert.Null(before.Body.Progenitor);
        ModManifest.TakeCatalogResend(out _, out _);

        status = Enforced();
        ModManifest.RebuildCatalog("scan");
        Assert.NotSame(before, ModManifest.ProgenitorEnvelope);
        Assert.Equal(Release, ModManifest.ProgenitorEnvelope!.Body.Progenitor);
        Assert.True(ModManifest.TakeCatalogResend(out _, out _));

        // Unchanged: nothing to re-sign.
        var same = ModManifest.ProgenitorEnvelope;
        ModManifest.RebuildCatalog("scan");
        Assert.Same(same, ModManifest.ProgenitorEnvelope);
        Assert.False(ModManifest.TakeCatalogResend(out _, out _));

        status = ProgenitorExact.StatusOf(VersionMatchingMode.Exact, ServerFacts(), Listed("2026.10.160"));
        ModManifest.RebuildCatalog("scan");
        Assert.Null(ModManifest.ProgenitorEnvelope!.Body.Progenitor);
        Assert.True(ModManifest.TakeCatalogResend(out _, out _));
    }

    [Fact]
    public void A_refusal_carries_the_requirement_it_was_judged_with_even_before_the_catalog_is_re_signed()
    {
        // The catalog was signed while Exact did not apply to Progenitor; the join was judged a moment later with it.
        ProgenitorExact.StatusSource = () => ProgenitorExactStatus.Off;
        VersionMatching.SettingsSource = () => new VersionMatchSettings(VersionMatchingMode.Exact);
        ModManifest.TestLoadedMods = Loaded();
        ModManifest.TestIsServer = true;
        ModManifest.TestRangeUsers.Add(5);
        ModManifest.TestProgenitorUsers.Add(5);
        ModManifest.TestRangeUsers.Add(6);
        var judged = Enforced().Requirement!;

        ModManifest.OnHandshakeDenied(5, new List<string> { ProgenitorExact.RowId }, joinFlowClient: true, judged);
        var toC4 = ModManifest.TestDenyEnvelope!;
        Assert.Equal(Release, toC4.Body.Progenitor);
        Assert.Equal(ServerSha, toC4.Body.ProgenitorSha);
        Assert.True(Verifies(toC4.Body, toC4.Signature));

        // A C3 client never gets the keys (it could not verify them).
        ModManifest.TestDenyEnvelope = null;
        ModManifest.OnHandshakeDenied(6, new List<string> { ProgenitorExact.RowId }, joinFlowClient: true, judged);
        Assert.Null(ModManifest.TestDenyEnvelope?.Body.Progenitor);
    }

    [Fact]
    public void The_hold_and_the_judgment_read_exact_for_progenitor_once_and_a_mid_session_kick_says_the_same_sentence()
    {
        string hs = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "Handshake.cs");
        string body = SourcePin.MethodBody(hs, "private static unsafe void OnResponse(");
        SourcePin.AssertStatementExactly(body, "var progenitorStatus = ProgenitorExact.ServerStatus();");
        SourcePin.AssertStatementExactly(body, "if (HoldForFeatureTable(user.Index, name, clientCaps, progenitorStatus.HoldsJoins, Judge)) return;");
        // Live 2026-10-10: a status not known yet (right after a Core load) is waited for, not judged as not enforced.
        Assert.Contains("ProgenitorExact.JudgeWhenKnown(progenitorStatus, ProgenitorExact.ServerStatus,", body);
        Assert.Contains("() => PendingAnswers.IsCurrent(answeredIndex, answerGeneration));", body);
        SourcePin.AssertStatementExactly(SourcePin.MethodBody(hs, "private static void OnPlayerDisconnected("), "PendingAnswers.Forget(ev.User.Index);");
        string judge = SourcePin.MethodBody(hs, "private static void JudgeAnswer(");
        SourcePin.AssertStatementExactly(judge, "var status = progenitorStatus ?? ProgenitorExact.ServerStatus();");

        // A re-answer mid-session runs the same judgment, so the disconnect carries the same sentence, first.
        string problem = ProgenitorExact.Check(Enforced(), sentTable: true, "2026.10.158", OtherSha)!;
        Assert.Equal("Mod mismatch: Progenitor: this server requires exactly Progenitor 2026.10.159 (you have 2026.10.158); example.trade: x",
            Handshake.Compare.MismatchKickReason(new[] { problem, "example.trade: x" }));
    }

    [Fact]
    public void A_refusal_over_progenitor_keeps_the_signed_requirement_even_without_a_failing_mod_row()
    {
        var (_, _, progenitor) = Forms(new ProgenitorRequirement(Release, ServerSha));
        var onlyProgenitor = ModManifestDenyPolicy.Narrow(progenitor, new[] { ProgenitorExact.RowId }, _key);
        Assert.NotNull(onlyProgenitor);
        Assert.Empty(onlyProgenitor!.Body.Mods);
        Assert.Equal(Release, onlyProgenitor.Body.Progenitor);
        Assert.Equal("exact", onlyProgenitor.Body.Matching);
        Assert.True(Verifies(onlyProgenitor.Body, onlyProgenitor.Signature));

        var both = ModManifestDenyPolicy.Narrow(progenitor, new[] { Trade, ProgenitorExact.RowId }, _key);
        Assert.Single(both!.Body.Mods);
        Assert.Equal(ServerSha, both.Body.ProgenitorSha);

        // A mod refusal alone does not carry it, and the extended form (a C3 client) never does.
        Assert.Null(ModManifestDenyPolicy.Narrow(progenitor, new[] { Trade }, _key)!.Body.Progenitor);
        var (_, extended, _) = Forms(new ProgenitorRequirement(Release, ServerSha));
        Assert.Null(ModManifestDenyPolicy.Narrow(extended, new[] { ProgenitorExact.RowId }, _key));
    }

    // ---- the client's Progenitor row -------------------------------------------------------------------------

    private static ProgenitorExact.ClientFacts Own(string version, string sha, SelfInstallKind kind = SelfInstallKind.Managed,
        ProgenitorExact.FixSource source = ProgenitorExact.FixSource.Thunderstore) => new()
    {
        CoreVersion = version,
        CoreSha = sha,
        Kind = kind,
        SourceOf = _ => source,
    };

    [Fact]
    public void The_row_offers_the_servers_build_when_it_can_come_here_and_tells_the_admin_otherwise()
    {
        var req = new ProgenitorRequirement(Release, ServerSha);
        var ready = ProgenitorExact.Decide(req, Own(Release, ServerSha.ToUpperInvariant()));
        Assert.Equal(JoinRowStatus.Ready, ready.Status);
        Assert.Equal(JoinRowAction.Ready, ready.Action);

        var older = ProgenitorExact.Decide(req, Own("2026.10.158", OtherSha));
        Assert.Equal(JoinRowStatus.Older, older.Status);
        Assert.Equal(JoinRowAction.UseProgenitor, older.Action);
        Assert.Equal(ProgenitorExact.NoteThunderstore, older.Note);

        var kept = ProgenitorExact.Decide(req, Own("2026.10.160", OtherSha, source: ProgenitorExact.FixSource.Kept));
        Assert.Equal(JoinRowStatus.Newer, kept.Status);
        Assert.Equal(JoinRowAction.UseProgenitor, kept.Action);
        Assert.Equal(ProgenitorExact.NoteKept, kept.Note);

        var sameRelease = ProgenitorExact.Decide(req, Own("2026.10.159.2", OtherSha));
        Assert.True(sameRelease.SameVersion);

        var dev = ProgenitorExact.Decide(req, Own("2026.10.160.1", OtherSha, SelfInstallKind.Development));
        Assert.Equal(JoinRowAction.TellAdmin, dev.Action);
        Assert.Equal(ProgenitorExact.NoteDevelopment, dev.Note);

        var manager = ProgenitorExact.Decide(req, Own("2026.10.160", OtherSha, SelfInstallKind.ModManager));
        Assert.Equal(JoinRowAction.TellAdmin, manager.Action);
        Assert.Equal("a mod manager installed Progenitor here; install Progenitor 2026.10.159 there", manager.Note);

        var nowhere = ProgenitorExact.Decide(req, Own("2026.10.160", OtherSha, source: ProgenitorExact.FixSource.None));
        Assert.Equal(JoinRowAction.TellAdmin, nowhere.Action);
        Assert.Equal("Progenitor 2026.10.159 is not in the release list here", nowhere.Note);

        var placing = Own("2026.10.158", OtherSha);
        placing.Placing = Release;
        Assert.Equal(ProgenitorExact.NotePlacing, ProgenitorExact.Decide(req, placing).Note);
    }

    private ManifestBody CatalogBody(string matching, string? progenitor, params ManifestMod[] mods) =>
        new(1, "Night Garden", "2026-10-10T12:00:00Z", _key.ToOperator("operator"), null, mods)
        {
            Matching = matching,
            Progenitor = progenitor,
            ProgenitorSha = progenitor == null ? null : ServerSha,
        };

    private static Handshake.ModRecord Row(string id, string version) =>
        Handshake.ModRecord.CreateDeclared(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);

    private static ManifestMod Mod(string id, string version, string? match) =>
        new(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true) { Peers = ">=1.2.0 <2.0.0", Match = match };

    private JoinPageModel Model(ManifestBody? catalog, ProgenitorExact.ClientFacts? own, string have = "1.3.0", string server = "1.2.0") =>
        JoinPageModel.Build(new JoinFacts
        {
            ServerName = "Night Garden",
            Asked = new[] { Row(Trade, server) },
            Provided = new[] { Row(Trade, have) },
            Catalog = catalog,
            Refused = true,
            OwnRangeOf = (id, v) => ">=1.2.0 <2.0.0",
            Progenitor = own,
        });

    [Fact]
    public void The_join_screens_draw_the_progenitor_row_first_with_its_action()
    {
        var model = Model(CatalogBody("exact", Release, Mod(Trade, "1.2.0", "exact")), Own("2026.10.158", OtherSha));
        var row = model.Required[0];
        Assert.Equal(ProgenitorExact.RowId, row.Id);
        Assert.Equal(JoinRowOrigin.Progenitor, row.Origin);
        Assert.Equal("Needs exactly 2026.10.159 · You have 2026.10.158", JoinText.VersionLine(row));
        Assert.Equal("Use Progenitor 2026.10.159", JoinText.ActionCaption(row));
        Assert.Equal("use-progenitor", JoinText.ActionName(row.Action));
        Assert.Equal("progenitor", JoinText.OriginName(row.Origin));
        Assert.Equal(ProgenitorExact.NoteThunderstore, JoinText.SourceLine(row));
        Assert.Equal("Older than the server's", JoinFlowLayout.StatusWord(row));
        Assert.Equal("you have 2026.10.158, needs exactly 2026.10.159", JoinFlowLayout.CompactVersion(row));
        Assert.Equal(2, model.NeedAttention);

        var same = Model(CatalogBody("exact", Release), Own("2026.10.159", OtherSha)).Required[0];
        Assert.Equal("Needs exactly 2026.10.159 · You have another build of 2026.10.159", JoinText.VersionLine(same));
        Assert.Equal("Another build", JoinFlowLayout.StatusWord(same));

        var ready = Model(CatalogBody("exact", Release), Own(Release, ServerSha)).Required.Single(r => r.Id == ProgenitorExact.RowId);
        Assert.True(ready.Ready);
        Assert.Equal("Needs exactly 2026.10.159 · You have 2026.10.159", JoinText.VersionLine(ready));

        var dev = Model(CatalogBody("exact", Release), Own("2026.10.160.1", OtherSha, SelfInstallKind.Development));
        var devRow = dev.Required[0];
        Assert.Equal(JoinRowAction.TellAdmin, devRow.Action);
        Assert.StartsWith("Progenitor: this server requires exactly 2026.10.159, and " + ProgenitorExact.NoteDevelopment + ". Ask its admins.",
            JoinText.CannotFixFooter(dev));
        Assert.Contains("requires exactly Progenitor 2026.10.159, but I have 2026.10.160.1", JoinText.TellAdminMessage(devRow, "Night Garden"));

        // No requirement in the catalog (Range, Same minor, an older server, the C3 form), or no facts: no row.
        Assert.DoesNotContain(Model(CatalogBody("exact", null), Own("2026.10.158", OtherSha)).Required, r => r.Id == ProgenitorExact.RowId);
        Assert.DoesNotContain(Model(CatalogBody("exact", Release), null).Required, r => r.Id == ProgenitorExact.RowId);
    }

    [Fact]
    public void The_progenitor_row_fits_its_caption_and_says_which_build_the_server_needs()
    {
        // Live 2026-10-10: the 170 px action cell cut the caption to "Use Progenitor 2…" and the line to
        // "Newer than the server's · you have 2026.10.90…".
        float width = JoinFlowLayout.SectionRowWidth(DialogLayout.ContentRowWidth());
        var newer = Model(CatalogBody("exact", Release), Own("2026.10.160", OtherSha, source: ProgenitorExact.FixSource.Kept)).Required[0];
        Assert.Equal(JoinRowAction.UseProgenitor, newer.Action);
        var spec = JoinFlowLayout.Required(newer, "", width);
        var action = spec.Cells.Single(c => c.Key == JoinFlowLayout.CellAction);
        Assert.Equal("Use Progenitor 2026.10.159", action.Text);
        Assert.True(action.Width.Value > JoinFlowLayout.ActionWidth);
        Assert.True(CellLayout.EstimateTextWidth(action.Text, DialogLayout.ButtonFontSize) + 28f <= action.Width.Value + 0.5f);

        var name = spec.Cells.Single(c => c.Key == JoinFlowLayout.CellName);
        string line = name.Text.Split('\n')[1];
        Assert.StartsWith("needs exactly 2026.10.159", line, StringComparison.Ordinal);
        string drawn = (string.IsNullOrEmpty(name.NoteLead) ? "" : name.NoteLead + " · ") + line;
        float noteWidth = width - action.Width.Value - 24f;
        Assert.Equal(drawn, MenuPanelLayout.ClipLabel(drawn, noteWidth, CellLayout.TwoLineNoteFontSize));

        // Wide enough: the status lead and both versions; narrow: what the server needs is what stays.
        JoinFlowLayout.AttentionLine(newer, "", 1000f, out var lead, out var note, out _);
        Assert.Equal("Newer than the server's", lead);
        Assert.Equal("needs exactly 2026.10.159, you have 2026.10.160", note);
        JoinFlowLayout.AttentionLine(newer, "", 150f, out lead, out note, out _);
        Assert.Equal("", lead);
        Assert.Equal("needs exactly 2026.10.159", note);

        // A ready Progenitor row keeps the ordinary cell.
        var ready = Model(CatalogBody("exact", Release), Own(Release, ServerSha)).Required.Single(r => r.Id == ProgenitorExact.RowId);
        Assert.Equal(JoinFlowLayout.ActionWidth, JoinFlowLayout.RequiredActionWidth(ready));
    }

    private sealed class Effects : IJoinFlowEffects
    {
        public readonly List<string> Calls = new();
        public bool Close;

        public bool CopyToClipboard(string text) { Calls.Add("copy:" + text); return true; }
        public void Leave() => Calls.Add("leave");
        public void Closed(bool completed) => Calls.Add("closed:" + completed);
        public void MarkSeen() { }
        public void PageShown(JoinPage page) { }

        public string UseProgenitor(JoinRequiredRow row, JoinMode mode, out bool close)
        {
            Calls.Add("use-progenitor:" + row.RequiredVersion + ":" + mode);
            close = Close;
            return "queued";
        }
    }

    [Fact]
    public void Use_progenitor_asks_the_effects_and_a_refused_join_closes_the_screens_for_the_swap()
    {
        var model = Model(CatalogBody("exact", Release), Own("2026.10.158", OtherSha), have: "1.2.0");
        var fx = new Effects { Close = true };
        var s = new JoinFlowSession(JoinMode.Refused, model, fx);
        Assert.Equal(JoinPage.Mods, s.Page);
        Assert.Equal("use-progenitor (closed)", s.RowAction(ProgenitorExact.RowId));
        Assert.Equal(new[] { "use-progenitor:2026.10.159:Refused", "closed:False" }, fx.Calls);

        var inWorld = new Effects { Close = false };
        var w = new JoinFlowSession(JoinMode.InWorld, model, inWorld);
        w.Continue();
        Assert.Equal("use-progenitor", w.RowAction(ProgenitorExact.RowId));
        Assert.Equal("queued", w.Notice);
        Assert.False(w.Closed);

        // Tell the admin on the Progenitor row copies its own message.
        var devModel = Model(CatalogBody("exact", Release), Own("2026.10.160.1", OtherSha, SelfInstallKind.Development), have: "1.2.0");
        var tell = new Effects();
        new JoinFlowSession(JoinMode.Refused, devModel, tell).RowAction(ProgenitorExact.RowId);
        Assert.Contains(tell.Calls, c => c.StartsWith("copy:Hi! Night Garden requires exactly Progenitor 2026.10.159", StringComparison.Ordinal));
    }

    // ---- the wording fix (C3 leftover) -----------------------------------------------------------------------

    [Fact]
    public void Server_rows_say_how_the_server_matches_the_version()
    {
        string Line(string matching, string? match, string have = "1.3.0") =>
            JoinText.VersionLine(Model(CatalogBody(matching, null, Mod(Trade, "1.2.0", match)), null, have).Required.Single());

        Assert.Equal("Needs exactly 1.2.0 · You have 1.3.0 (newer)", Line("exact", "exact"));
        Assert.Equal("Needs 1.2.x · You have 1.3.0 (newer)", Line("minor", "minor"));
        Assert.Equal("Needs 1.2.0 or newer · You have 1.1.0", Line("range", "at-least", have: "1.1.0"));
        // A per-mod Exact rule under declared ranges.
        Assert.Equal("Needs exactly 1.2.0 · You have 1.3.0 (newer)", Line("range", "exact"));
        // Declared ranges without a rule, and an older server's catalog: the plain version.
        Assert.Equal("Needs 1.2.0 · You have 1.1.0", Line("range", null, have: "1.1.0"));

        var exactRow = Model(CatalogBody("exact", null, Mod(Trade, "1.2.0", "exact")), null).Required.Single();
        Assert.Equal(ClientModRule.Exact, exactRow.MatchRule);
        Assert.Equal("you have 1.3.0, needs exactly 1.2.0", JoinFlowLayout.CompactVersion(exactRow));
        var plain = Model(null, null).Required.Single();
        Assert.Null(plain.MatchRule);
        Assert.Equal("you have 1.3.0, server 1.2.0", JoinFlowLayout.CompactVersion(plain));

        Assert.Equal("exactly 1.2.0", JoinText.NeedsPhrase(ClientModRule.Exact, "1.2.0"));
        Assert.Equal("1.2.x", JoinText.NeedsPhrase(ClientModRule.Minor, "1.2.0"));
        Assert.Equal("1.2.0 or newer", JoinText.NeedsPhrase(ClientModRule.AtLeast, "1.2.0"));
        Assert.Equal("any version", JoinText.NeedsPhrase(ClientModRule.Any, "1.2.0"));
        Assert.Equal("any version", JoinText.NeedsPhrase(ClientModRule.Exact, ""));
    }

    [Fact]
    public void The_admins_see_what_exact_means_for_progenitor_while_exact_is_chosen()
    {
        var s = new JoinAdminState { MatchingSupported = true, VersionMatching = "exact", ProgenitorExactLine = ProgenitorExact.EnforcedLine(Release) };
        Assert.Equal("Players also need exactly Progenitor 2026.10.159, this server's build. " + ProgenitorExact.RecheckSentence, ProgenitorExact.AdminLine(s, "exact"));
        Assert.Equal("", ProgenitorExact.AdminLine(s, "range"));
        Assert.Equal("", ProgenitorExact.AdminLine(null, "exact"));
        Assert.Equal("Players must have exactly the versions this server runs, including Progenitor.", VersionMatchingText.HelpExact);

        // On the wire: the server's state carries the line inside versionMatching; an older server sends none.
        var packet = JoinAdminWire.EncodeResponse(new JoinAdminResponse { Nonce = 3, Op = "read", Ok = true, State = s });
        Assert.True(JoinAdminWire.TryDecodeResponse(packet, out var back));
        Assert.Equal(s.ProgenitorExactLine, back.State!.ProgenitorExactLine);
        var old = new JoinAdminState { MatchingSupported = true, VersionMatching = "exact" };
        Assert.True(JoinAdminWire.TryDecodeResponse(JoinAdminWire.EncodeResponse(new JoinAdminResponse { Nonce = 4, Op = "read", Ok = true, State = old }), out var oldBack));
        Assert.Equal("", oldBack.State!.ProgenitorExactLine);
    }
}
