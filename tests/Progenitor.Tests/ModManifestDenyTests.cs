using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FMAN17: the deny-time descriptor and the disconnect-screen report.
///
/// <para>Two behavioral facts carry the whole design and are asserted here rather than argued in
/// a comment: a peer the handshake refused <b>still cannot obtain a package byte</b>
/// (<c>HandleRequest</c> refuses, because nothing on the deny path touches
/// <c>HandshakeOkUsers</c>), and the descriptor <b>names only the rows that client actually failed
/// on</b> rather than the server's inventory. Everything else here is copy and source pins.</para>
///
/// <para>Same collection as the other <c>ModManifest</c> suites: these mutate the module's
/// process-wide state. The handshake tables are supplied through
/// <c>ModManifestDeny.TestServerAsked</c> / <c>TestProvided</c> rather than mutated, because those
/// belong to the <c>Handshake</c> collection.</para>
/// </summary>
[Collection("ModManifest")]
public sealed class ModManifestDenyTests : IDisposable
{
    private const string Id = "progenitor.sample";
    private const string Other = "other.mod";
    private const string Third = "third.mod";

    private readonly string _dir;
    private readonly string _dll;

    public ModManifestDenyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-modmanifest-deny-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = true;
        ModManifest.TestConnectHost = "127.0.0.1:9976";
        _dll = Path.Combine(_dir, "sample.dll");
        File.WriteAllBytes(_dll, Encoding.UTF8.GetBytes("sample-package-bytes"));
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
        catch { /* best-effort temp cleanup */ }
    }

    // ------------------------------------------------------------ the two behavioral facts

    /// <summary>
    /// The server sends a denied peer a signed descriptor and <b>nothing else</b>: the user index never enters
    /// <c>HandshakeOkUsers</c>, and since 2026-10-05 a server sends no mod file to anyone. A <c>PrmR</c> request that
    /// still arrives (an older client) is dropped with one log line.
    /// </summary>
    [Fact]
    public void A_denied_peer_gets_the_descriptor_and_nothing_else()
    {
        ServeCatalog();
        ModManifest.OnHandshakeDenied(7, Failures(Id));

        Assert.Equal(new[] { 7 }, ModManifest.TestDenyUsers);
        Assert.NotNull(ModManifest.TestDenyEnvelope);

        ModManifest.DropRetiredPacketForTests(ModManifest.RequestEventId, 40);
        Assert.Contains(ModManifest.TestRetiredLog, l => l.Contains("PrmR", StringComparison.Ordinal));

        // And the deny path did not quietly become the ok path: no handshake-ok hook ran.
        Assert.Empty(ModManifest.TestOkHookUsers);
        Assert.Equal(-1, ModManifest.TestLastHandshakeOkUser);
    }

    /// <summary>
    /// The descriptor is the failing rows, not the inventory. Three mods are in the catalog; the
    /// client failed one; the envelope on the wire carries that one, still verifiably signed by the
    /// same operator key, with the same <c>issued</c> stamp so it can never trip the client's
    /// replay gate against a later full catalog.
    /// </summary>
    [Fact]
    public void The_descriptor_names_only_the_rows_the_client_failed()
    {
        ServeThreeModCatalog();
        var full = ModManifest.LastEnvelope!;
        Assert.Equal(3, full.Body.Mods.Count);

        ModManifest.OnHandshakeDenied(4, Failures(Other));

        var sent = ModManifest.TestDenyEnvelope!;
        Assert.Equal(new[] { Other }, Ids(sent));
        Assert.Equal(full.Body.Operator.Fp, sent.Body.Operator.Fp);
        Assert.Equal(full.Body.Operator.Spki, sent.Body.Operator.Spki);
        Assert.Equal(full.Body.Issued, sent.Body.Issued);
        Assert.Equal(full.Body.ServerName, sent.Body.ServerName);
        Assert.True(ModManifestCrypto.Verify(
            sent.Body.Operator.Spki,
            ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(sent.Body)),
            sent.Signature));
    }

    /// <summary>
    /// The narrowing itself, where a test can reach it (R45 finding 13's rule). Two ids fail: one
    /// because this server requires it, one because the <em>client</em> declared a
    /// <c>ServerMustHave</c> row this server does not have. Only the first can ever be described by
    /// this server's catalog, and only the first survives.
    /// </summary>
    [Fact]
    public void Only_the_rows_this_servers_own_check_rejected_reach_the_descriptor()
    {
        var server = new[]
        {
            Declared(Id, "0.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        var client = new[]
        {
            Declared(Id, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
            Declared("only.mine", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };

        Handshake.Compare.Evaluate(server, client, out var failedIds);
        Assert.Equal(2, failedIds.Count);
        Assert.Equal(new[] { Id }, Handshake.Compare.DenyDescriptorIds(failedIds, server, client));
        Assert.Empty(Handshake.Compare.DenyDescriptorIds(null, server, client));
    }

    // ------------------------------------------------------------ the switch

    [Fact]
    public void The_operator_switch_is_on_by_default_and_off_sends_nothing()
    {
        Assert.True(ModManifestDenyPolicy.DefaultEnabled);
        Assert.Equal("Networking", ModManifestDenyPolicy.ConfigSection);
        Assert.Equal("DenyTimeModDescriptor", ModManifestDenyPolicy.ConfigKey);

        ServeThreeModCatalog();
        ModManifest.TestDenyDescriptorEnabled = false;
        ModManifest.OnHandshakeDenied(3, Failures(Other));

        Assert.Empty(ModManifest.TestDenyUsers);
        Assert.Null(ModManifest.TestDenyEnvelope);
        Assert.Contains(Log().Infos, line => line == ModDenyLog.Disabled());

        // Once per Core generation, not once per refused join.
        ModManifest.OnHandshakeDenied(4, Failures(Other));
        Assert.Single(Log().Infos, line => line == ModDenyLog.Disabled());
    }

    [Fact]
    public void A_failing_id_the_catalogue_has_no_row_for_sends_nothing()
    {
        ServeThreeModCatalog();
        ModManifest.OnHandshakeDenied(9, Failures("not.in.the.catalogue"));

        Assert.Empty(ModManifest.TestDenyUsers);
        Assert.Null(ModManifest.TestDenyEnvelope);
        Assert.Contains(Log().Infos, line => line == ModDenyLog.NothingToSend(9));
    }

    [Fact]
    public void Narrow_refuses_every_empty_shape_rather_than_signing_the_whole_inventory()
    {
        ServeThreeModCatalog();
        var full = ModManifest.LastEnvelope!;
        using var key = ModManifestCrypto.Generate();

        Assert.Null(ModManifestDenyPolicy.Narrow(null, new[] { Other }, key));
        Assert.Null(ModManifestDenyPolicy.Narrow(full, null, key));
        Assert.Null(ModManifestDenyPolicy.Narrow(full, Array.Empty<string>(), key));
        Assert.Null(ModManifestDenyPolicy.Narrow(full, new[] { "" }, key));
        Assert.Null(ModManifestDenyPolicy.Narrow(full, new[] { Other }, null));
    }

    // ------------------------------------------------------------ the client report

    /// <summary>
    /// The half FMAN16 established is already on the client: with no wire at all, the rows the
    /// server asked about plus this client's own answer produce the diagnosis, and the consequence
    /// quoted per row is <c>Compare.Check</c>'s own sentence — the one the disconnect box shows.
    /// </summary>
    [Fact]
    public void The_report_builds_from_local_state_with_no_catalogue_at_all()
    {
        var rows = ModManifestDeny.BuildLocalRows(
            new[]
            {
                Declared(Id, "0.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
                Declared(Other, "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
            },
            new[] { Declared(Id, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) });

        Assert.Equal(2, rows.Count);
        var mismatch = Find(rows, Id);
        Assert.Equal("0.2.0", mismatch.RequiredVersion);
        Assert.Equal("0.1.0", mismatch.Have);
        Assert.False(mismatch.Missing);
        Assert.Equal(ModDenyOrigin.ServerRequires, mismatch.Origin);
        Assert.Equal(
            Handshake.Compare.EnforcedKickReason(
                Id,
                new[] { Declared(Id, "0.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) },
                new[] { Declared(Id, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) }),
            mismatch.EnforcedKickReason);

        var missing = Find(rows, Other);
        Assert.True(missing.Missing);
        Assert.False(missing.HasSource);
        Assert.Equal("  the server named no source for this mod.", ModDenyText.WhereLine(missing));
    }

    /// <summary>
    /// A row that fails because <em>this client</em> requires something the server does not run is
    /// labeled as such and never told to go and download it: the server cannot supply what it does
    /// not have, and saying otherwise would be a claim the code has not established.
    /// </summary>
    [Fact]
    public void A_requirement_that_is_only_on_this_machine_is_labelled_as_this_machines()
    {
        var rows = ModManifestDeny.BuildLocalRows(
            Array.Empty<Handshake.ModRecord>(),
            new[] { Declared("mine.mod", "3.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) });

        var row = Assert.Single(rows);
        Assert.Equal(ModDenyOrigin.YouRequire, row.Origin);
        Assert.Contains("this server does not run this mod", ModDenyText.WhereLine(row), StringComparison.Ordinal);
        Assert.Contains("you require 3.0", ModDenyText.RowLine(row), StringComparison.Ordinal);
    }

    /// <summary>
    /// The missing-link defect, closed. The row that needs a link is the missing one, and after the signed
    /// descriptor lands it has one. The rows themselves still come from local state: a catalog
    /// row for an id this client did not fail is not a refusal reason and never becomes one.
    /// </summary>
    [Fact]
    public void The_descriptor_gives_the_missing_row_the_link_local_state_could_not()
    {
        ServeThreeModCatalog();
        ModManifest.OnHandshakeDenied(5, Failures(Other));
        var narrowed = ModManifest.TestDenyEnvelope!;

        ModManifest.TestIsServer = false;
        ModManifestDeny.TestServerAsked = new[]
        {
            Declared(Other, "2.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        ModManifestDeny.TestProvided = Array.Empty<Handshake.ModRecord>();

        ModManifestDeny.OnJoinDenied("Mod mismatch: other.mod: expected 2.0.0, got missing", "127.0.0.1:9976");
        var before = ModManifestDeny.Current!;
        Assert.False(before.DescriptorReceived);
        Assert.Equal(0, before.SourcedRows);

        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(narrowed)));

        var after = ModManifestDeny.Current!;
        Assert.True(after.DescriptorReceived);
        var row = Assert.Single(after.Rows);
        Assert.Equal(Other, row.Id);
        Assert.True(row.HasSource);
        Assert.Equal("  get it from Thunderstore: ExampleTeam-Other", ModDenyText.WhereLine(row));
        Assert.Contains("sha256 ", ModDenyText.RowLine(row), StringComparison.Ordinal);

        // The catalog may not add rows: it carried one, and the report still has exactly the one
        // the client failed on.
        Assert.Single(after.Rows);
    }

    /// <summary>
    /// A refused join routes the catalog to the deny report.
    /// </summary>
    [Fact]
    public void A_refused_join_routes_the_catalogue_to_the_report()
    {
        ServeThreeModCatalog();
        var envelope = ModManifest.LastEnvelope!;
        ModManifest.TestIsServer = false;
        ModManifestDeny.TestServerAsked = new[]
        {
            Declared(Other, "2.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        ModManifestDeny.TestProvided = Array.Empty<Handshake.ModRecord>();

        ModManifestDeny.OnJoinDenied("Mod mismatch", "127.0.0.1:9976");
        Assert.True(ModManifestDeny.JoinWasDenied);
        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(envelope)));

        Assert.NotNull(ModManifestDeny.Current);

        // A new join attempt drops the refusal, so the next server's catalog prompts normally.
        ModManifestDeny.OnJoinAttempt();
        Assert.False(ModManifestDeny.JoinWasDenied);
        Assert.Null(ModManifestDeny.Current);
    }

    [Fact]
    public void The_panel_is_bounded_per_join_however_many_catalogues_arrive()
    {
        ModManifest.TestIsServer = false;
        ModManifestDeny.TestServerAsked = Array.Empty<Handshake.ModRecord>();
        ModManifestDeny.TestProvided = Array.Empty<Handshake.ModRecord>();

        for (int i = 0; i < ModManifestDenyPolicy.MaxPanelsPerJoin + 3; i++)
            ModManifestDeny.OnJoinDenied("Mod mismatch", "127.0.0.1:9976");

        Assert.Equal(ModManifestDenyPolicy.MaxPanelsPerJoin, ModManifestDeny.TestPanelsThisJoin);
        Assert.Contains(
            Log().Infos,
            line => line == ModDenyLog.PanelCapReached(ModManifestDenyPolicy.MaxPanelsPerJoin));
    }

    // ------------------------------------------------------------ copy

    /// <summary>
    /// The panel offers no way to accept, so it must say so. A player who reads "download for
    /// review" on a consent panel and then sees this screen has to be told that this one is
    /// different and why nothing here will fetch the file.
    /// </summary>
    [Fact]
    public void The_copy_states_that_nothing_was_downloaded_and_that_a_restart_is_needed()
    {
        var report = new ModDenyReport(
            "Mod mismatch: other.mod: expected 2.0.0, got missing",
            "127.0.0.1:9976",
            "Night Garden",
            "Ada",
            "a1b2c3d4e5f67890",
            descriptorReceived: true,
            new[]
            {
                new ModDenyRow(
                    Other,
                    "2.0.0",
                    have: null,
                    ModDenyOrigin.ServerRequires,
                    "other.mod: expected 2.0.0, got missing",
                    package: "ExampleTeam-Other",
                    url: null,
                    sha256: new string('a', 64),
                    size: 1234567),
            });

        string text = ModDenyText.Compose(report);
        Assert.Contains("Nothing was downloaded and nothing was installed.", text, StringComparison.Ordinal);
        Assert.Contains("the next time you start the game", text, StringComparison.Ordinal);
        Assert.Contains("other.mod: expected 2.0.0, got missing", text, StringComparison.Ordinal);
        Assert.Contains("get it from Thunderstore: ExampleTeam-Other", text, StringComparison.Ordinal);
        Assert.Contains("Ada", text, StringComparison.Ordinal);
        Assert.Contains("'Night Garden' refused this connection: 1 mod does not match.", text, StringComparison.Ordinal);
        Assert.Equal(1, report.SourcedRows);
    }

    [Fact]
    public void With_no_catalogue_the_copy_says_the_server_sent_none_rather_than_implying_a_link()
    {
        var report = new ModDenyReport(
            "Mod mismatch",
            "127.0.0.1:9976",
            "",
            "",
            "",
            descriptorReceived: false,
            new[] { new ModDenyRow(Other, "2.0.0", null, ModDenyOrigin.ServerRequires, null) });

        string text = ModDenyText.Compose(report);
        Assert.Contains("sent no mod catalog with the refusal", text, StringComparison.Ordinal);
        Assert.Contains("the server named no source for this mod.", text, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:9976 refused this connection", text, StringComparison.Ordinal);
        Assert.Equal(0, report.SourcedRows);
    }

    // ------------------------------------------------------------ source pins

    /// <summary>
    /// <c>Handshake.OnResponse</c>'s failure branch calls the deny hook unconditionally, with the
    /// <em>enforced</em> failures — not <c>failedIds</c>, which also carries rows this server does
    /// not have and therefore cannot describe. Pinned exactly, because a call made conditional or
    /// commented out is precisely how this feature would silently stop existing.
    /// </summary>
    [Fact]
    public void The_deny_hook_is_called_unconditionally_from_the_handshake_failure_branch()
    {
        // Compat C3: the judgment moved into JudgeAnswer (it may run after the feature table hold).
        string body = SourcePin.MethodBody(HandshakeSource(), "private static unsafe void OnResponse(")
            + SourcePin.MethodBody(HandshakeSource(), "private static void JudgeAnswer(");
        // Mod compatibility C4: the ids are the enforced failures, plus the Progenitor row when the server refused the
        // client's Progenitor build (so the descriptor keeps the signed requirement).
        SourcePin.AssertStatementExactly(body, "var denyIds = Compare.DenyDescriptorIds(failedIds, server, answered);");
        SourcePin.AssertStatementExactly(body, "if (progenitorRefused) denyIds.Add(ProgenitorExact.RowId);");
        SourcePin.AssertStatementExactly(
            body,
            "ModManifest.OnHandshakeDenied(user.Index, denyIds, (clientCaps & Capability.JoinFlow) != 0, judgedProgenitor);");
    }

    /// <summary>
    /// The deny path grants a catalog and nothing else. <c>HandshakeOkUsers</c> is the only gate
    /// <c>HandleRequest</c> consults before it will move a byte, so the hook's body must not name
    /// it at all — an <c>Add</c> there would turn option (c) into option (a) with one line.
    /// </summary>
    [Fact]
    public void The_deny_hook_never_names_the_transfer_authorisation_set()
    {
        string body = SourcePin.MethodBody(
            ModManifestSource(),
            "internal static void OnHandshakeDenied(");
        Assert.DoesNotContain("HandshakeOkUsers", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SentUsers", body, StringComparison.Ordinal);
    }

    /// <summary>The client marks the refusal, and drops it again at the next join attempt.</summary>
    [Fact]
    public void The_client_records_the_refusal_on_the_result_and_drops_it_on_the_next_request()
    {
        string source = HandshakeSource();
        string result = SourcePin.MethodBody(source, "private static unsafe void OnResult(");
        SourcePin.AssertStatementExactly(
            result,
            "ModManifestDeny.OnJoinDenied(r.Text, ModManifest.CurrentConnectHost());");
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(source, "private static unsafe void OnRequest("),
            "ModManifestDeny.OnJoinAttempt();");
        // An accepted or deferred result must dismiss the previous join's deny panel, not
        // leave it centered over the live world. Both branches, because pinning one line
        // once would pass with the other removed.
        int ended = 0;
        foreach (string line in result.Split('\n'))
        {
            string code = line;
            int cut = code.IndexOf("//", StringComparison.Ordinal);
            if (cut >= 0) code = code.Substring(0, cut);
            if (code.Trim() == "ModManifestDeny.OnJoinEnded();") ended++;
        }
        Assert.Equal(2, ended);
    }

    /// <summary>
    /// Main-menu return is a named Handshake seam, not the Error-state connection-ended
    /// signal — that screen is where the deny panel is meant to sit.
    /// </summary>
    [Fact]
    public void Returning_to_the_menu_dismisses_the_deny_report_through_the_named_seam()
    {
        string refresh = File.ReadAllText(Path.Combine(
            SourcePin.RepoRoot(), "src", "Progenitor.Core", "Networking", "HandshakeRefresh.cs"));
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(refresh, "internal static void NotifyClientReturnedToMenu()"),
            "try { ModManifestDeny.OnJoinEnded(); }");
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(refresh, "private static void OnClientWorldLoaded(WorldLoaded ev)"),
            "NotifyClientReturnedToMenu();");
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(refresh, "private static void OnClientConnectionWatch()"),
            "NotifyClientReturnedToMenu();");
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(
                File.ReadAllText(Path.Combine(
                    SourcePin.RepoRoot(), "src", "Progenitor.Core", "Networking", "ModManifestDeny.cs")),
                "internal static void OnJoinEnded()"),
            "ModManifestDenyPanel.Close();");
    }

    /// <summary>
    /// The routing itself: a verified catalog reaches the deny report on a refused join. The consent prompt that
    /// took the other branch went with server-to-client mod distribution (2026-10-05).
    /// </summary>
    [Fact]
    public void The_catalogue_routing_branches_on_the_refusal()
    {
        string body = SourcePin.MethodBody(ModManifestSource(), "internal static void AcceptEnvelope(");
        SourcePin.AssertStatementExactly(body, "if (ModManifestDeny.JoinWasDenied)");
        SourcePin.AssertStatementExactly(body, "ModManifestDeny.OnCatalogAccepted(accepted);");
        Assert.DoesNotContain("ModManifestConsent", body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ helpers

    private static string HandshakeSource() =>
        File.ReadAllText(Path.Combine(
            SourcePin.RepoRoot(), "src", "Progenitor.Core", "Networking", "Handshake.cs"));

    private static string ModManifestSource() =>
        File.ReadAllText(Path.Combine(
            SourcePin.RepoRoot(), "src", "Progenitor.Core", "Networking", "ModManifest.cs"));

    private static Handshake.ModRecord Declared(
        string id, string version, NetworkCompatibilityLevel level, NetworkVersionRule rule) =>
        Handshake.ModRecord.CreateDeclared(id, version, level, rule);

    private static List<string> Failures(params string[] ids) => new(ids);

    private static string[] Ids(ManifestEnvelope envelope)
    {
        var ids = new string[envelope.Body.Mods.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = envelope.Body.Mods[i].Id;
        return ids;
    }

    private static ModDenyRow Find(IReadOnlyList<ModDenyRow> rows, string id)
    {
        foreach (var row in rows)
        {
            if (string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase)) return row;
        }
        throw new InvalidOperationException("no row " + id);
    }

    private void ServeCatalog()
    {
        ModManifest.TestLoadedMods = new[]
        {
            new LoadedModInfo(Id, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
        };
        ModManifest.TestIsServer = true;
        _ = ModManifest.Describe();
    }

    /// <summary>Three loaded mods and an overlay that gives the middle one a Thunderstore package.</summary>
    private void ServeThreeModCatalog()
    {
        File.WriteAllText(
            Path.Combine(_dir, "progenitor-manifest.json"),
            "{\"serverName\":\"Night Garden\",\"operatorName\":\"Ada\",\"mods\":[{\"id\":\"" + Other
            + "\",\"namespace\":\"ExampleTeam\",\"package\":\"ExampleTeam-Other\",\"sha256\":\""
            + new string('b', 64) + "\",\"size\":2048}]}");
        ModManifest.TestLoadedMods = new[]
        {
            new LoadedModInfo(Id, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
            new LoadedModInfo(Other, "2.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
            new LoadedModInfo(Third, "3.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, true),
        };
        ModManifest.TestIsServer = true;
        _ = ModManifest.Describe();
    }

    private static TestLogSource Log() => (TestLogSource)Plugin.Logger!;
}
