using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Progenitor;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// CLIENTPOLICY1 (docs/client-mod-policy.md, stage 1): the schema 2 store and its upgrade, the panel operations and
/// their authorization, the judgment matrix, the AtLeast rule, the report's chunking and caps, the signed section
/// and the mods-changed hash, the refused-join lines, and the panel model. Game-free.
/// </summary>
[Collection("ModManifest")]
public sealed class ClientModPolicyTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RemoteInstallActor Admin = new("Admin", 76561198000000001UL, isAdmin: true);
    private static readonly RemoteInstallActor Player = new("Player", 76561198000000002UL, isAdmin: false);

    private readonly string _dir;
    private readonly List<string> _audit = new();
    private double _now = 1000;

    public ClientModPolicyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-clientpolicy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = new[]
        {
            new LoadedModInfo("server.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
        };
        ModManifest.TestIsServer = true;
        Plugin.Logger = new TestLogSource();
        int window = 60;
        JoinAdminServer.WindowGet = () => window;
        JoinAdminServer.WindowSet = s => { window = s; return true; };
        JoinAdminServer.Audit = _audit.Add;
        JoinAdminServer.AuditClock = () => T0;
        JoinAdminServer.Now = () => _now;
        RefusedJoins.Clock = () => T0;
        ClientModReportClient.ResetForTests();
    }

    public void Dispose()
    {
        ModManifest.ResetForTests();
        JoinServerSettings.TestContent = null;
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestIsServer = false;
        ClientModPolicyRecords.Clear();
        ClientModReportClient.ResetForTests();
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private JoinAdminResponse Apply(JoinAdminRequest r, RemoteInstallActor? actor = null, bool handshakeOk = true)
    {
        _now += 7;
        return JoinAdminServer.Apply(r, handshakeOk, actor ?? Admin);
    }

    private static JoinAdminRequest Req(string op, string id = "", string package = "", string version = "", string rule = "", string reason = "", string value = "", string name = "") =>
        new() { Op = op, Id = id, Package = package, Version = version, Rule = rule, Reason = reason, Value = value, Name = name };

    // ---- the store: schema 2, the upgrade, the caps --------------------------------------------------------------

    [Fact]
    public void A_schema_1_file_loads_as_schema_2_with_the_defaults_and_saves_as_schema_2()
    {
        const string v1 = "{\"schema\":1,\"recommended\":[{\"id\":\"maps.mod\",\"package\":\"Owner-Maps\",\"version\":\"1.2.0\",\"reason\":\"Maps.\"}],\"shared\":[{\"id\":\"server.mod\"}]}";
        Assert.True(JoinAdminConfig.TryParse(v1, out var config, out var error), error);
        Assert.Equal("maps.mod", Assert.Single(config.Recommended).Id);
        Assert.Empty(config.Policy.Required);
        Assert.Equal(ClientPolicyMode.Any, config.Policy.Mode);
        Assert.Empty(config.Policy.Approved);
        Assert.Equal(UnverifiedPolicy.Allow, config.Policy.Unverified);
        Assert.False(config.Policy.Active);

        File.WriteAllText(JoinAdminServer.FilePath, v1);
        JoinAdminServer.Reset();
        var r = Apply(Req(JoinAdminOps.PolicyMode, value: "approved"));
        Assert.True(r.Ok, r.Error);
        string saved = File.ReadAllText(JoinAdminServer.FilePath);
        Assert.Contains("\"schema\": 2", saved);
        Assert.Contains("\"mode\": \"approved\"", saved);
        Assert.Contains("maps.mod", saved);
    }

    [Fact]
    public void Schema_2_round_trips_every_field()
    {
        var c = new JoinAdminConfig();
        c.Policy.Required.Add(new ClientModRow("robes.pack", "Owner-Robes", "1.2.0", ClientModRule.Exact, "Our robes.", "Robe Pack"));
        c.Policy.Required.Add(new ClientModRow("", "Owner-Ui", "", ClientModRule.Any, "The event UI."));
        c.Policy.Mode = ClientPolicyMode.Approved;
        c.Policy.Unverified = UnverifiedPolicy.Refuse;
        c.Policy.Approved.Add(new ClientModRow("cosmetic.mod", "", "", ClientModRule.Any, "", "Cosmetic"));
        c.Policy.Approved.Add(new ClientModRow("minimap.mod", "Owner-Map", "2.0", ClientModRule.AtLeast));
        Assert.True(JoinAdminConfig.TryParse(c.ToJson(), out var back, out var error), error);
        Assert.Equal(2, back.Policy.Required.Count);
        Assert.Equal(ClientModRule.Exact, back.Policy.Required[0].Rule);
        Assert.Equal("Our robes.", back.Policy.Required[0].Reason);
        Assert.Equal("Robe Pack", back.Policy.Required[0].Name);
        Assert.Equal("Owner-Ui", back.Policy.Required[1].Key);
        Assert.Equal(ClientPolicyMode.Approved, back.Policy.Mode);
        Assert.Equal(UnverifiedPolicy.Refuse, back.Policy.Unverified);
        Assert.Equal(new[] { "cosmetic.mod", "minimap.mod" }, back.Policy.Approved.Select(a => a.Key));
        Assert.Equal(ClientModRule.AtLeast, back.Policy.Approved[1].Rule);
        Assert.Equal(c.ToJson(), back.ToJson());
    }

    [Fact]
    public void Rows_over_a_cap_bad_or_repeated_are_skipped_with_a_note_and_an_unknown_schema_is_refused()
    {
        var sb = new StringBuilder("{\"schema\":2,\"recommended\":[{\"id\":\"both.mod\",\"reason\":\"x\"}],\"requiredClient\":[");
        for (int i = 0; i < 18; i++) sb.Append(i > 0 ? "," : "").Append("{\"id\":\"req").Append(i).Append(".mod\",\"version\":\"1.0\",\"rule\":\"at-least\"}");
        sb.Append(",{\"id\":\"req0.mod\"},{\"id\":\"bad mod\"},{\"id\":\"rule.mod\",\"version\":\"1\",\"rule\":\"sometimes\"}],\"clientPolicy\":{\"mode\":\"weird\",\"approved\":[");
        for (int i = 0; i < 130; i++) sb.Append(i > 0 ? "," : "").Append("{\"id\":\"app").Append(i).Append(".mod\"}");
        sb.Append("],\"unverified\":\"refuse\"}}");
        var skipped = new List<string>();
        Assert.True(JoinAdminConfig.TryParse(sb.ToString(), out var config, out var error, skipped), error);
        Assert.Equal(ClientModPolicyLimits.MaxRequired, config.Policy.Required.Count);
        Assert.Equal(ClientModPolicyLimits.MaxApproved, config.Policy.Approved.Count);
        Assert.Equal(ClientPolicyMode.Any, config.Policy.Mode);
        Assert.Equal(UnverifiedPolicy.Refuse, config.Policy.Unverified);
        Assert.Contains(skipped, s => s.Contains("more than 16 required client mods"));
        Assert.Contains(skipped, s => s.Contains("more than 128 approved client mods"));
        Assert.Contains(skipped, s => s.Contains("unknown rule \"sometimes\""));
        Assert.Contains(skipped, s => s.StartsWith("clientPolicy mode"));

        // A mod both recommended and required (a hand edit) stays required.
        Assert.True(JoinAdminConfig.TryParse("{\"schema\":2,\"recommended\":[{\"id\":\"both.mod\",\"reason\":\"x\"}],\"requiredClient\":[{\"id\":\"both.mod\"}]}",
            out var both, out _, skipped));
        Assert.Empty(both.Recommended);
        Assert.Single(both.Policy.Required);

        Assert.False(JoinAdminConfig.TryParse("{\"schema\":3}", out _, out var bad));
        Assert.Contains("schema 3", bad);
    }

    [Fact]
    public void A_file_that_is_not_json_is_set_aside_and_the_server_starts_with_no_policy()
    {
        File.WriteAllText(JoinAdminServer.FilePath, "{not json");
        JoinAdminServer.Reset();
        Assert.False(JoinAdminServer.CurrentPolicy.Active);
        Assert.Contains("set aside", JoinAdminServer.LoadNote);
        Assert.Single(Directory.GetFiles(_dir, JoinAdminServer.FileName + ".bad-*"));
    }

    // ---- the operations ------------------------------------------------------------------------------------------

    [Fact]
    public void Required_add_defaults_to_or_newer_with_a_version_and_any_without_and_moves_a_recommendation()
    {
        Assert.True(Apply(new JoinAdminRequest { Op = JoinAdminOps.RecommendAdd, Id = "robes.pack", Package = "Owner-Robes", Version = "1.2.0", Reason = "Robes." }).Ok);
        var r = Apply(Req(JoinAdminOps.RequiredAdd, "robes.pack", "Owner-Robes", "1.2.0", name: "Robe Pack"));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("Moved from Recommended to Required.", r.Note);
        Assert.Empty(r.State!.Recommended);
        var row = Assert.Single(r.State.RequiredClient);
        Assert.Equal(ClientModRules.WordAtLeast, row.Rule);
        Assert.Equal(JoinAdminLimits.PlaceholderReason, row.Reason);
        Assert.Equal("Robe Pack", row.Name);

        var any = Apply(Req(JoinAdminOps.RequiredAdd, package: "Owner-Ui"));
        Assert.True(any.Ok, any.Error);
        Assert.Equal(ClientModRules.WordAny, any.State!.RequiredClient[1].Rule);

        Assert.Equal("That mod is already required.", Apply(Req(JoinAdminOps.RequiredAdd, "robes.pack")).Error);
        Assert.StartsWith("Give a version", Apply(Req(JoinAdminOps.RequiredAdd, "x.mod", rule: "exact")).Error);
        Assert.StartsWith("Choose a version rule", Apply(Req(JoinAdminOps.RequiredAdd, "x.mod", version: "1.0", rule: "sometimes")).Error);
        Assert.StartsWith("The reason is longer", Apply(Req(JoinAdminOps.RequiredAdd, "x.mod", reason: new string('a', 121))).Error);
        Assert.StartsWith("A client mod row needs", Apply(Req(JoinAdminOps.RequiredAdd)).Error);
        // Recommending a required mod is refused.
        Assert.Equal("This server requires that mod, so it is not a recommendation.",
            Apply(new JoinAdminRequest { Op = JoinAdminOps.RecommendAdd, Package = "Owner-Ui", Reason = "x" }).Error);
    }

    [Fact]
    public void At_most_16_required_and_128_approved()
    {
        for (int i = 0; i < ClientModPolicyLimits.MaxRequired; i++) Assert.True(Apply(Req(JoinAdminOps.RequiredAdd, "req" + i + ".mod")).Ok);
        Assert.Equal("At most 16 client mods can be required.", Apply(Req(JoinAdminOps.RequiredAdd, "one.more")).Error);
        // Approved through bulks of 64 (the per-request cap), then single adds hit the cap.
        for (int b = 0; b < 2; b++)
        {
            var bulk = new JoinAdminRequest { Op = JoinAdminOps.ApproveBulk };
            for (int i = 0; i < 64; i++) bulk.Items.Add(new JoinAdminBulkItem { Id = "app" + b + "x" + i + ".mod" });
            var r = Apply(bulk);
            Assert.True(r.Ok, r.Error);
            Assert.StartsWith("Approved 64 mods.", r.Note);
        }

        Assert.Equal("At most 128 client mods can be approved.", Apply(Req(JoinAdminOps.ApproveAdd, "last.mod")).Error);
        Assert.Equal(128, JoinAdminServer.CurrentPolicy.Approved.Count);
    }

    [Fact]
    public void Reason_rule_mode_and_unverified_edits()
    {
        Assert.True(Apply(Req(JoinAdminOps.RequiredAdd, "robes.pack", version: "1.2.0")).Ok);
        Assert.True(Apply(Req(JoinAdminOps.RequiredReason, "robes.pack", reason: "Our robes.")).Ok);
        Assert.Equal("Unchanged.", Apply(Req(JoinAdminOps.RequiredReason, "robes.pack", reason: "Our robes.")).Note);
        var rule = Apply(Req(JoinAdminOps.RequiredRule, "robes.pack", rule: "exact"));
        Assert.True(rule.Ok, rule.Error);
        Assert.Equal("exact", rule.State!.RequiredClient[0].Rule);
        Assert.True(Apply(Req(JoinAdminOps.RequiredRule, "robes.pack", version: "1.3.0", rule: "minor")).Ok);
        var row = JoinAdminServer.CurrentPolicy.Required[0];
        Assert.Equal(("1.3.0", ClientModRule.Minor), (row.Version, row.Rule));
        Assert.Equal("That mod is not required.", Apply(Req(JoinAdminOps.RequiredReason, "nope.mod", reason: "x")).Error);

        Assert.Equal("Choose any or approved.", Apply(Req(JoinAdminOps.PolicyMode, value: "some")).Error);
        Assert.True(Apply(Req(JoinAdminOps.PolicyMode, value: "approved")).Ok);
        Assert.Equal("Unchanged.", Apply(Req(JoinAdminOps.PolicyMode, value: "approved")).Note);
        Assert.Equal("Choose allow or refuse.", Apply(Req(JoinAdminOps.Unverified, value: "maybe")).Error);
        var u = Apply(Req(JoinAdminOps.Unverified, value: "refuse"));
        Assert.True(u.Ok);
        Assert.Equal(("approved", "refuse"), (u.State!.PolicyMode, u.State.Unverified));
        Assert.True(JoinAdminServer.CurrentPolicy.RequiresAnswer);

        Assert.True(Apply(Req(JoinAdminOps.RequiredRemove, "robes.pack")).Ok);
        Assert.Empty(JoinAdminServer.CurrentPolicy.Required);
    }

    [Fact]
    public void Approve_add_remove_and_bulk_rules()
    {
        Assert.True(Apply(Req(JoinAdminOps.ApproveAdd, "cosmetic.mod", name: "Cosmetic")).Ok);
        Assert.Equal("That mod is already approved.", Apply(Req(JoinAdminOps.ApproveAdd, "cosmetic.mod")).Error);
        Assert.True(Apply(Req(JoinAdminOps.ApproveRemove, "cosmetic.mod")).Ok);
        Assert.Equal("That mod is not approved.", Apply(Req(JoinAdminOps.ApproveRemove, "cosmetic.mod")).Error);

        // Removing an approved mod that is also required is refused.
        Assert.True(Apply(Req(JoinAdminOps.RequiredAdd, "robes.pack")).Ok);
        Assert.True(Apply(Req(JoinAdminOps.ApproveAdd, "robes.pack")).Ok);
        Assert.Equal(ClientPolicyDecisions.RequiredAlwaysAllowed, Apply(Req(JoinAdminOps.ApproveRemove, "robes.pack")).Error);

        var bulk = new JoinAdminRequest { Op = JoinAdminOps.ApproveBulk };
        bulk.Items.Add(new JoinAdminBulkItem { Id = "a.mod", Name = "A" });
        bulk.Items.Add(new JoinAdminBulkItem { Id = "robes.pack" });
        bulk.Items.Add(new JoinAdminBulkItem { Id = "progenitor" });
        bulk.Items.Add(new JoinAdminBulkItem { Id = "bad/id" });
        var r = Apply(bulk);
        Assert.True(r.Ok, r.Error);
        Assert.Contains("Approved 1 mod.", r.Note);
        Assert.Contains("1 already approved.", r.Note);
        Assert.Contains("1 always allowed.", r.Note);
        Assert.Contains("Skipped bad/id", r.Note);

        var over = new JoinAdminRequest { Op = JoinAdminOps.ApproveBulk };
        for (int i = 0; i < 65; i++) over.Items.Add(new JoinAdminBulkItem { Id = "o" + i + ".mod" });
        Assert.Equal("At most 64 mods can be approved in one request.", Apply(over).Error);
        // The wire reads one row past the cap, so an over-long request is refused rather than cut.
        Assert.True(JoinAdminWire.TryDecodeRequest(JoinAdminWire.EncodeRequest(over), out var decoded));
        Assert.Equal(65, decoded.Items.Count);
    }

    [Fact]
    public void A_non_admin_is_refused_with_an_audit_line_and_no_state_and_edits_are_rate_limited()
    {
        var r = Apply(Req(JoinAdminOps.RequiredAdd, "robes.pack"), Player);
        Assert.False(r.Ok);
        Assert.Equal(JoinAdminPolicy.NotAdminError, r.Error);
        Assert.Null(r.State);
        Assert.Contains(_audit, l => l.Contains("JoinAdmin/required-add/robes.pack") && l.Contains(JoinAdminPolicy.NotAdminError));
        Assert.Equal(JoinAdminPolicy.HandshakeError, Apply(Req(JoinAdminOps.PolicyMode, value: "approved"), handshakeOk: false).Error);

        _audit.Clear();
        Assert.True(Apply(Req(JoinAdminOps.ApproveAdd, "cosmetic.mod")).Ok);
        Assert.Contains(_audit, l => l.Contains("JoinAdmin/approve-add/cosmetic.mod") && l.Contains("accepted"));

        // Ten edits per minute per SteamID, policy edits included.
        var flood = new RemoteInstallActor("Flood", 76561198000000009UL, isAdmin: true);
        int refused = 0;
        for (int i = 0; i < 12; i++)
            if (JoinAdminServer.Apply(Req(JoinAdminOps.Unverified, value: i % 2 == 0 ? "refuse" : "allow"), true, flood).Error == JoinAdminPolicy.RateLimitedError) refused++;
        Assert.Equal(2, refused);
    }

    // ---- the AtLeast rule ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1.2.0", "1.2.0", true)]
    [InlineData("1.2.0", "1.2.1", true)]
    [InlineData("1.2.0", "1.10.0", true)]
    [InlineData("1.2.0", "2.0", true)]
    [InlineData("1.2.0", "1.1.9", false)]
    [InlineData("1.2", "1.2.0", true)]
    [InlineData("1.2.0", "v1.2.0", true)]
    [InlineData("1.2.0.4", "1.2.0.3", false)]
    [InlineData("1.2.0.4", "1.2.0.5", true)]
    [InlineData("1.2.0", "1.2.0-beta", false)]
    [InlineData("1.2.0-beta", "1.2.0", true)]
    [InlineData("1.2.0-beta.2", "1.2.0-beta.10", true)]
    [InlineData("1.2.0-alpha", "1.2.0-beta", true)]
    [InlineData("1.2.0+build5", "1.2.0", true)]
    [InlineData("nightly", "nightly", true)]
    [InlineData("nightly", "NIGHTLY", true)]
    [InlineData("nightly", "1.0.0", false)]
    [InlineData("1.0.0", "nightly", false)]
    [InlineData("1.0.0", "", false)]
    public void AtLeast_follows_semantic_version_order_with_the_minor_rules_fallback(string required, string got, bool passes) =>
        Assert.Equal(passes, Handshake.Compare.AtLeast(required, got));

    [Fact]
    public void The_four_rules_through_one_check()
    {
        Assert.True(ClientModRules.Passes(ClientModRule.Exact, "1.2.0", "1.2.0"));
        Assert.False(ClientModRules.Passes(ClientModRule.Exact, "1.2.0", "1.2.1"));
        Assert.True(ClientModRules.Passes(ClientModRule.Minor, "1.2.0", "1.2.7"));
        Assert.False(ClientModRules.Passes(ClientModRule.Minor, "1.2.0", "1.3.0"));
        Assert.True(ClientModRules.Passes(ClientModRule.Any, "1.2.0", "0.1"));
        Assert.True(ClientModRules.Passes(ClientModRule.Exact, "", "anything"));
        Assert.Equal("Exact 1.2.0", ClientModRules.Label(ClientModRule.Exact, "1.2.0"));
        Assert.Equal("1.2.0 or newer", ClientModRules.Label(ClientModRule.AtLeast, "1.2.0"));
        Assert.Equal("Same minor as 1.2.0", ClientModRules.Label(ClientModRule.Minor, "1.2.0"));
        Assert.Equal("Any version", ClientModRules.Label(ClientModRule.AtLeast, ""));
    }

    // ---- the judgment --------------------------------------------------------------------------------------------

    private static ClientModReport Report(bool truncated = false, params (string Guid, string Version)[] mods)
    {
        var r = new ClientModReport { Truncated = truncated };
        foreach (var (g, v) in mods) r.Entries.Add(new ClientModEntry(g, g + " name", v, ClientModSources.Chainloader));
        return r;
    }

    private static ClientPolicyConfig Policy(bool required, bool approved, bool refuse)
    {
        var p = new ClientPolicyConfig { Mode = approved ? ClientPolicyMode.Approved : ClientPolicyMode.Any, Unverified = refuse ? UnverifiedPolicy.Refuse : UnverifiedPolicy.Allow };
        if (required) p.Required.Add(new ClientModRow("robes.pack", "Owner-Robes", "1.2.0", ClientModRule.AtLeast, "Robes.", "Robe Pack"));
        p.Approved.Add(new ClientModRow("cosmetic.mod", "", "", ClientModRule.Any));
        return p;
    }

    public static IEnumerable<object[]> Matrix()
    {
        // mode approved?, required?, refuse unverified?, report kind, expected outcome
        foreach (bool approved in new[] { false, true })
        foreach (bool required in new[] { false, true })
        foreach (bool refuse in new[] { false, true })
        foreach (string report in new[] { "none", "partial", "full", "extra" })
        {
            string expected;
            bool active = required || approved;
            if (!active) expected = "pass";
            else if (report == "none")
                expected = required || (approved && refuse) ? "refuse" : "pass-unchecked";
            else if (report == "partial")
                // A cut report that holds every listed mod: unchecked part applies only to Only approved.
                expected = approved && refuse ? "refuse" : approved ? "pass-unchecked" : "pass";
            else if (report == "full") expected = "pass";
            else expected = approved ? "refuse" : "pass"; // "extra": an unapproved mod
            yield return new object[] { approved, required, refuse, report, expected };
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Judgment_matrix(bool approved, bool required, bool refuse, string report, string expected)
    {
        var policy = Policy(required, approved, refuse);
        var facts = new ClientModServerFacts();
        facts.Runs["server.mod"] = ("1.0.0", NetworkVersionRule.Exact);
        var mods = new List<(string, string)> { ("progenitor", "2026.10.5"), ("server.mod", "1.0.0"), ("cosmetic.mod", "3.0") };
        if (required) mods.Add(("robes.pack", "1.3.0"));
        if (report == "extra") mods.Add(("cheaty.mod", "1.0"));
        ClientModReport? r = report == "none" ? null : Report(report == "partial", mods.ToArray());
        var v = ClientModJudge.Judge(policy, r, facts);
        Assert.Equal(expected, v.Word);
        if (expected == "refuse") Assert.False(string.IsNullOrEmpty(v.Reason));
        if (report == "extra" && approved) Assert.Equal("cheaty.mod", Assert.Single(v.NotAllowed).Key);
    }

    [Fact]
    public void Refusal_reasons_are_readable_ascii_and_fit_the_wire_message()
    {
        var policy = Policy(required: true, approved: true, refuse: false);
        var v = ClientModJudge.Judge(policy, Report(false, ("robes.pack", "1.1.0"), ("cheaty.mod", "1.0")), new ClientModServerFacts());
        Assert.Equal("refuse", v.Word);
        Assert.Equal("Mods needed: Robe Pack (1.2.0 or newer, you have 1.1.0). Mods not allowed on this server: cheaty.mod name.", v.Reason);
        Assert.Equal(ClientModProblem.KindOlder, Assert.Single(v.Required).Kind);
        Assert.Equal("Update needed", v.Required[0].Status);

        var missing = ClientModJudge.Judge(policy, null, new ClientModServerFacts());
        Assert.Equal("Mods needed: Robe Pack (1.2.0 or newer).", missing.Reason);
        Assert.Equal("Missing", missing.Required[0].Status);

        var newer = new ClientPolicyConfig();
        newer.Required.Add(new ClientModRow("x.mod", "", "1.2.0", ClientModRule.Exact, "", "Ünïcode — Mod"));
        var n = ClientModJudge.Judge(newer, Report(false, ("x.mod", "1.3.0")), new ClientModServerFacts());
        Assert.Equal(ClientModProblem.KindNewer, n.Required[0].Kind);
        Assert.Equal("Newer than allowed", n.Required[0].Status);
        Assert.All(n.Reason, c => Assert.InRange(c, ' ', '~'));

        var many = new ClientPolicyConfig();
        for (int i = 0; i < 16; i++) many.Required.Add(new ClientModRow("long.mod" + i, "", "1.0", ClientModRule.AtLeast, "", new string('N', 60)));
        string reason = ClientModJudge.Judge(many, null, new ClientModServerFacts()).Reason;
        Assert.Equal(Handshake.ResultMessageMaxChars, reason.Length);
        Assert.EndsWith(Handshake.ReasonEllipsis, reason);
        Assert.Equal(reason, Handshake.Compare.WireMessageText(Handshake.Compare.WireMessageBytes(reason)));
    }

    [Fact]
    public void Only_approved_allows_progenitor_bepinex_server_run_required_recommended_and_approved_mods()
    {
        var policy = new ClientPolicyConfig { Mode = ClientPolicyMode.Approved };
        policy.Required.Add(new ClientModRow("robes.pack", "", "1.2.0", ClientModRule.Exact));
        policy.Approved.Add(new ClientModRow("", "Owner-Pkg", "", ClientModRule.Any));
        policy.Approved.Add(new ClientModRow("pinned.mod", "", "2.0", ClientModRule.AtLeast));
        var facts = new ClientModServerFacts();
        facts.Runs["server.mod"] = ("1.0.0", NetworkVersionRule.Minor);
        facts.Runs["loaded.only"] = ("5", null);
        facts.Recommended.Add("rec.mod");
        var report = new ClientModReport();
        report.Entries.Add(new ClientModEntry("BepInEx.Something", "", "6.0", ClientModSources.Patcher));
        report.Entries.Add(new ClientModEntry("progenitor.preloader", "", "1.0.0", ClientModSources.Patcher));
        report.Entries.Add(new ClientModEntry("server.mod", "", "1.0.9", ClientModSources.Chainloader));
        report.Entries.Add(new ClientModEntry("loaded.only", "", "9", ClientModSources.Chainloader));
        report.Entries.Add(new ClientModEntry("rec.mod", "", "1", ClientModSources.Progenitor));
        report.Entries.Add(new ClientModEntry("by.package", "", "1", ClientModSources.Progenitor, "Owner-Pkg"));
        report.Entries.Add(new ClientModEntry("robes.pack", "", "1.2.0", ClientModSources.Chainloader));
        report.Entries.Add(new ClientModEntry("pinned.mod", "", "2.1", ClientModSources.Chainloader));
        Assert.Equal("pass", ClientModJudge.Judge(policy, report, facts).Word);

        // A server-run mod at a version its handshake rule refuses, and an approved row's rule, still bind.
        report.Entries.Add(new ClientModEntry("server.mod", "", "2.0", ClientModSources.Chainloader));
        var stale = new ClientModReport();
        stale.Entries.Add(new ClientModEntry("server.mod", "", "1.1.0", ClientModSources.Chainloader));
        stale.Entries.Add(new ClientModEntry("pinned.mod", "", "1.9", ClientModSources.Chainloader));
        var v = ClientModJudge.Judge(policy, stale, facts);
        Assert.Equal(new[] { "server.mod", "pinned.mod" }, v.NotAllowed.Select(p => p.Key));
        Assert.Equal("robes.pack", Assert.Single(v.Required).Key);
    }

    [Fact]
    public void The_no_answer_summary_names_the_policy_and_says_nothing_without_one()
    {
        Assert.Equal("none", ClientModJudge.NoAnswerSummary("none", new ClientPolicyConfig()));
        var p = Policy(required: true, approved: true, refuse: true);
        Assert.Equal("Mods needed: Robe Pack 1.2.0 or newer; client mods are checked (Progenitor needed)", ClientModJudge.NoAnswerSummary("none", p));
        Assert.StartsWith("a.mod 1.0 ServerMustHave/Exact; Mods needed:", ClientModJudge.NoAnswerSummary("a.mod 1.0 ServerMustHave/Exact", p));
        var allow = Policy(required: false, approved: true, refuse: false);
        Assert.False(allow.RequiresAnswer);
        Assert.True(allow.Active);
    }

    // ---- the refused-join record ---------------------------------------------------------------------------------

    [Fact]
    public void Refusals_record_the_admins_rows_and_only_valid_not_approved_ids()
    {
        var policy = Policy(required: true, approved: true, refuse: true);
        var report = Report(false, ("robes.pack", "1.0"), ("cheaty.mod", "1.0"));
        report.Entries.Add(new ClientModEntry("<b>Update now</b>", "", "1", ClientModSources.Chainloader));
        report.Entries.Add(new ClientModEntry("Install this now!", "", "1", ClientModSources.Chainloader));
        var v = ClientModJudge.Judge(policy, report, new ClientModServerFacts());
        var rows = ClientModJudge.RecordRows(v);
        Assert.Equal(new[] { ("robes.pack", RefusedJoinLog.KindClientOlder), ("cheaty.mod", RefusedJoinLog.KindNotApproved) },
            rows.Select(r => (r.Mod, r.Kind)));
        Assert.Equal(RefusedJoinLog.KindUnchecked, Assert.Single(ClientModJudge.RecordRows(ClientModJudge.Judge(Policy(false, true, true), null, new()))).Kind);

        var log = new RefusedJoinLog();
        for (int i = 0; i < 3; i++) log.Record(new[] { ("cheaty.mod", "", "", RefusedJoinLog.KindNotApproved) }, T0.AddMinutes(i));
        log.Record(new[] { ("robes.pack", "1.2.0", "", RefusedJoinLog.KindClientMissing) }, T0.AddMinutes(5));
        log.Record(new[] { ("robes.pack", "1.2.0", "1.0", RefusedJoinLog.KindClientOlder) }, T0.AddMinutes(6));
        log.Record(new[] { ("robes.pack", "1.2.0", "1.3", RefusedJoinLog.KindClientNewer) }, T0.AddMinutes(7));
        var (_, alert) = log.Aggregate(T0.AddHours(1), 10, id => id == "robes.pack" ? "Robe Pack" : null);
        Assert.Contains(alert, a => a.Text == "cheaty.mod: not approved (3 joins)");
        Assert.Contains(alert, a => a.Text == "Robe Pack: players don't have it (required by you) (1 join)");
        Assert.Contains(alert, a => a.Text == "Robe Pack: players have 1.0, you require 1.2.0 (1 join)");
        Assert.Contains(alert, a => a.Text == "Robe Pack: players have 1.3, newer than you allow (1.2.0) (1 join)");
    }

    [Fact]
    public void A_policy_refusal_is_recorded_once_per_connection_like_a_handshake_one()
    {
        RefusedJoins.NoteRows(4, new[] { ("robes.pack", "1.2.0", "", RefusedJoinLog.KindClientMissing) }, 76561198000000005UL);
        RefusedJoins.Commit(4);
        RefusedJoins.NoteRows(4, new[] { ("robes.pack", "1.2.0", "", RefusedJoinLog.KindClientMissing) }, 76561198000000005UL);
        RefusedJoins.Commit(4);
        Assert.Single(RefusedJoins.Log.Rows);
        RefusedJoins.Drop(4);
    }

    // ---- the report: chunks and caps -----------------------------------------------------------------------------

    private static List<ClientModEntry> Entries(int n, int nameLength = 20)
    {
        var list = new List<ClientModEntry>();
        for (int i = 0; i < n; i++)
            list.Add(new ClientModEntry("some.author.mod" + i, new string('n', nameLength), "1.2." + i, ClientModSources.Chainloader, "Owner-Mod" + i));
        return list;
    }

    [Fact]
    public void A_small_report_is_one_packet_and_reads_back()
    {
        var entries = Entries(10);
        var packets = ClientModReportWire.Encode(entries, false, 7);
        Assert.Single(packets);
        var asm = new ClientModReportAssembler();
        Assert.True(asm.Accept(3, packets[0], out var report, out var error), error);
        Assert.Equal(10, report!.Entries.Count);
        Assert.False(report.Truncated);
        Assert.Equal("Owner-Mod3", report.Entries[3].Package);
        Assert.Equal("1.2.3", report.FindGuid("SOME.AUTHOR.MOD3")!.Version);
    }

    [Fact]
    public void A_large_report_is_chunked_at_15_KiB_and_reassembled_in_any_order()
    {
        var entries = Entries(300, 60);
        var packets = ClientModReportWire.Encode(entries, false, 9);
        Assert.True(packets.Count > 1);
        Assert.All(packets, p => Assert.InRange(p.Length, 9, ClientModPolicyLimits.MaxReportPacketBytes));
        var asm = new ClientModReportAssembler();
        ClientModReport? report = null;
        for (int i = packets.Count - 1; i >= 0; i--)
        {
            bool done = asm.Accept(1, packets[i], out var r, out var error);
            Assert.Null(error);
            if (done) report = r;
            else Assert.Null(r);
            // A duplicate chunk changes nothing.
            if (i == packets.Count - 1) Assert.False(asm.Accept(1, packets[i], out _, out _));
        }

        Assert.NotNull(report);
        Assert.Equal(300, report!.Entries.Count);
        Assert.Equal(0, asm.InFlightCount);
    }

    [Fact]
    public void A_report_over_64_KiB_is_cut_and_marked_truncated()
    {
        var entries = Entries(1200, 100);
        byte[] body = ClientModReportWire.Body(entries, false, out int kept);
        Assert.InRange(body.Length, 1, ClientModPolicyLimits.MaxReportBytes);
        Assert.True(kept < 1200);
        var packets = ClientModReportWire.Encode(entries, false, 1);
        Assert.InRange(packets.Count, 2, ClientModPolicyLimits.MaxReportChunks);
        var asm = new ClientModReportAssembler();
        ClientModReport? report = null;
        foreach (var p in packets)
            if (asm.Accept(2, p, out var r, out _)) report = r;
        Assert.True(report!.Truncated);
        Assert.Equal(kept, report.Entries.Count);
    }

    [Fact]
    public void Bad_packets_and_bad_reports_are_refused()
    {
        var asm = new ClientModReportAssembler();
        Assert.False(asm.Accept(1, new byte[] { 1, 0, 0, 0, 0, 0 }, out _, out var e1));
        Assert.Equal("not a report packet", e1);
        Assert.False(asm.Accept(1, new byte[] { 2, 0, 1, 0, 0, 0, 0, 1, (byte)'{' }, out _, out _)); // version
        Assert.False(asm.Accept(1, new byte[] { 1, 0, 1, 0, 0, 0, 0, 6, (byte)'{' }, out _, out _)); // more than 5 chunks
        Assert.False(asm.Accept(1, new byte[] { 1, 0, 1, 0, 0, 0, 2, 2, (byte)'{' }, out _, out _)); // index past count
        var tooBig = new byte[ClientModPolicyLimits.MaxReportPacketBytes + 1];
        tooBig[0] = 1;
        tooBig[7] = 1;
        Assert.False(asm.Accept(1, tooBig, out _, out _));

        byte[] Packet(byte[] body)
        {
            var p = new byte[8 + body.Length];
            p[0] = 1;
            p[2] = 5;
            p[7] = 1;
            Buffer.BlockCopy(body, 0, p, 8, body.Length);
            return p;
        }

        Assert.False(asm.Accept(1, Packet(new byte[] { 0xC3, 0x28 }), out _, out var utf));
        Assert.StartsWith("the report is not valid JSON", utf);
        Assert.False(asm.Accept(1, Packet(Encoding.UTF8.GetBytes("[1,2]")), out _, out var shape));
        Assert.StartsWith("the report is not an object", shape);
        Assert.True(asm.Accept(1, Packet(Encoding.UTF8.GetBytes("{\"mods\":[{\"guid\":\"a\\u0007b\",\"version\":\"1\"},{\"name\":\"no id\"}]}")), out var ok, out _));
        Assert.Equal("ab", Assert.Single(ok!.Entries).Guid);
    }

    [Fact]
    public void The_inventory_counts_a_plugin_once_prefers_progenitor_and_reads_ledger_packages()
    {
        var list = ClientModInventory.Build(new[]
        {
            new ClientModObservation("hot.mod", "Hot", "1.0", ClientModSources.Chainloader),
            new ClientModObservation("hot.mod", "Hot", "1.1", ClientModSources.Progenitor),
            new ClientModObservation("plain.mod", "Plain", "2.0", ClientModSources.Chainloader),
            new ClientModObservation("progenitor.preloader", "Preloader", "1.0.0", ClientModSources.Patcher),
            new ClientModObservation(" ", "", "", ClientModSources.Chainloader),
        }, g => g == "hot.mod" ? "Owner-Hot" : g == "plain.mod" ? "not a package" : "");
        Assert.Equal(new[] { "plain.mod", "progenitor.preloader", "hot.mod" }, list.Select(e => e.Guid));
        var hot = list.Single(e => e.Guid == "hot.mod");
        Assert.Equal(("1.1", ClientModSources.Progenitor, "Owner-Hot"), (hot.Version, hot.Source, hot.Package));
        Assert.Equal("", list.Single(e => e.Guid == "plain.mod").Package);
        Assert.Equal("Owner-Hot", ClientModInventory.PackageFromLedger("package:Owner-Hot@1.2.3", "ignored"));
        Assert.Equal("Owner-Id", ClientModInventory.PackageFromLedger("", "Owner-Id"));
        Assert.Equal("", ClientModInventory.PackageFromLedger("file:x.dll", "x.dll"));
    }

    [Fact]
    public void The_client_sends_its_report_only_to_a_server_that_asked()
    {
        ClientModReportClient.TestObserved.Add(new ClientModObservation("a.mod", "A", "1.0", ClientModSources.Chainloader));
        ClientModReportClient.AfterAnswer("first answer");
        Assert.Empty(ClientModReportClient.TestPackets);

        ClientModReportClient.NoteServerRequest(true);
        ClientModReportClient.AfterAnswer("first answer");
        var packet = Assert.Single(ClientModReportClient.TestPackets);
        var asm = new ClientModReportAssembler();
        Assert.True(asm.Accept(0, packet, out var report, out _));
        Assert.Equal("a.mod", Assert.Single(report!.Entries).Guid);

        // An unchanged set is not re-sent on a resend; an answer always carries one.
        Assert.Equal(0, ClientModReportClient.Send("resend", force: false));
        ClientModReportClient.TestObserved.Add(new ClientModObservation("b.mod", "B", "1.0", ClientModSources.Progenitor));
        Assert.Equal(1, ClientModReportClient.Send("resend", force: false));
        ClientModReportClient.AfterAnswer("re-answer");
        Assert.Equal(3, ClientModReportClient.TestPackets.Count);

        // A server that does not ask (or a disconnect) stops it.
        ClientModReportClient.NoteServerRequest(false);
        ClientModReportClient.AfterAnswer("next server");
        Assert.Equal(3, ClientModReportClient.TestPackets.Count);
    }

    /// <summary>
    /// The live trigger order (live 2026-10-05: no report was ever sent). A new join's request carries the ClientPolicy
    /// bit; the client ends the previous join (the epoch change raises ServerContextCleared, which resets the report
    /// state), notes the ask, answers, and sends the report, which the server reassembles and judges.
    /// </summary>
    [Fact]
    public void A_new_joins_request_with_the_bit_leads_to_a_report_after_the_answer()
    {
        ClientModReportClient.Install();
        try
        {
            ClientModReportClient.TestObserved.Add(new ClientModObservation("test.robes.pack", "Robe Pack", "1.2.3", ClientModSources.Chainloader));
            // A previous server had asked: its state must not leak, and the new ask must survive the epoch change.
            ClientModReportClient.NoteServerRequest(true);
            var request = new List<Handshake.ModRecord> { Handshake.ModRecord.Undeclared("server.mod", "1.0.0") };
            Assert.True(Handshake.Compare.AppendCapabilityRow(request, Handshake.Capability.ClientPolicy));

            var rows = Handshake.TakeClientPolicyAsk(request, newJoin: true, out bool hadRow);
            Assert.True(hadRow);
            Assert.Equal("server.mod", Assert.Single(rows).Guid);
            Assert.True(ClientModReportClient.ServerAsks);

            ClientModReportClient.AfterAnswer("server asked about 1 mod(s)");
            var packet = Assert.Single(ClientModReportClient.TestPackets);
            var asm = new ClientModReportAssembler();
            Assert.True(asm.Accept(0, packet, out var report, out var error), error);
            var policy = new ClientPolicyConfig();
            policy.Required.Add(new ClientModRow("test.robes.pack", "", "1.2.0", ClientModRule.AtLeast, "", "Robe Pack"));
            Assert.Equal("pass", ClientModJudge.Judge(policy, report, new ClientModServerFacts()).Word);

            // A request without the bit (another server) stops the reports.
            var plain = new List<Handshake.ModRecord> { Handshake.ModRecord.Undeclared("server.mod", "1.0.0") };
            Handshake.TakeClientPolicyAsk(plain, newJoin: true, out hadRow);
            Assert.False(hadRow);
            Assert.False(ClientModReportClient.ServerAsks);
            ClientModReportClient.AfterAnswer("next");
            Assert.Single(ClientModReportClient.TestPackets);
        }
        finally
        {
            ClientModReportClient.ResetForTests();
        }
    }

    [Fact]
    public void The_bug_order_noting_before_the_epoch_change_loses_the_ask()
    {
        // Pins why the order matters: the context-cleared reset erases an ask noted before it.
        ClientModReportClient.Install();
        try
        {
            ClientModReportClient.NoteServerRequest(true);
            Handshake.NotifyHandshakeEpochChanged();
            Assert.False(ClientModReportClient.ServerAsks);
        }
        finally
        {
            ClientModReportClient.ResetForTests();
        }
    }

    // ---- the waiting join ----------------------------------------------------------------------------------------

    [Fact]
    public void A_waiting_join_is_decided_exactly_once_by_its_report_or_its_deadline()
    {
        var gate = new ClientPolicyGate();
        var decided = new List<string>();
        int g = gate.Begin(2, "TestPlayer", due: 20, (r, why) => decided.Add(why + ":" + (r == null ? "none" : r.Entries.Count.ToString())));
        Assert.True(gate.IsWaiting(2));
        Assert.Null(gate.TryExpire(2, g, now: 19));
        var w = gate.TakeOnReport(2);
        w!.Decide!(Report(false, ("a", "1")), "report");
        Assert.Null(gate.TryExpire(2, g, now: 25));
        Assert.Null(gate.TakeOnReport(2));
        Assert.Equal(new[] { "report:1" }, decided);

        int g2 = gate.Begin(3, "Other", due: 20, (r, why) => decided.Add(why));
        int g3 = gate.Begin(3, "Other", due: 40, (r, why) => decided.Add("second " + why));
        Assert.Null(gate.TryExpire(3, g2, now: 25)); // replaced by a newer wait
        gate.TryExpire(3, g3, now: 41)!.Decide!(null, "no-report");
        Assert.Equal("second no-report", decided[^1]);

        gate.Begin(4, "Gone", due: 20, (r, why) => decided.Add("gone"));
        gate.OnDisconnect(4);
        Assert.Null(gate.TakeOnReport(4));
        Assert.Equal(0, gate.Count);
    }

    [Fact]
    public void The_answer_watch_keeps_a_report_wait_from_being_re_armed()
    {
        var watch = new HandshakeAnswerWatch(() => 0);
        watch.Arm(1, "A", 20, skipUser: false, serverRequiresAnswer: true, "x");
        watch.OnAwaitingReport(1);
        Assert.Equal("awaiting-report", watch.Token(1));
        Assert.False(watch.HasAnswered(1));
        Assert.False(watch.Arm(1, "A", 20, false, true, "x").Scheduled);
        Assert.False(watch.TryExpire(1, 1, out _));
        watch.OnAnswer(1);
        Assert.Equal("ok", watch.Token(1));
    }

    [Fact]
    public void A_held_answer_cannot_time_out_and_an_already_ok_user_stays_ok()
    {
        // Mod compatibility C3: the answer arrived and its judgment waits for the feature table.
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        var arm = watch.Arm(1, "A", 20, skipUser: false, serverRequiresAnswer: true, "x");
        t = 19;
        watch.OnAnswerHeld(1);
        t = 21;
        Assert.False(watch.TryExpire(1, arm.Generation, out _));
        Assert.Equal("awaiting-report", watch.Token(1));
        watch.OnAnswer(1);
        Assert.Equal("ok", watch.Token(1));

        // A re-answer of a user already in the world is held too; its status is not touched.
        watch.OnAnswerHeld(1);
        Assert.Equal("ok", watch.Token(1));
    }

    [Fact]
    public void A_report_that_arrives_during_the_feature_table_hold_is_kept_for_the_gate()
    {
        // The client sends answer, report, table: the report must not be lost while the judgment waits.
        string policy = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "Handshake.ClientPolicy.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(policy, "private static void OnReportComplete("),
            "if (waiting == null && !midSession && TableHold.IsWaiting(userIndex))");
        SourcePin.AssertUncommented(SourcePin.MethodBody(policy, "private static bool GateOnClientPolicy("),
            "if (TakeReportDuringHold(userIndex, out var early, out string earlyHow))");
        string hs = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "Handshake.cs");
        SourcePin.AssertUncommented(hs, "ForgetReportDuringHold(user.Index);");
        SourcePin.AssertUncommented(hs, "ForgetReportDuringHold(ev.User.Index);");
        SourcePin.AssertUncommented(SourcePin.MethodBody(hs, "private static bool HoldForFeatureTable("), "AnswerWatch.OnAnswerHeld(userIndex);");
    }

    // ---- the signed section and the mods-changed hash ------------------------------------------------------------

    [Fact]
    public void The_policy_is_signed_into_the_join_section_and_verifies_after_a_round_trip()
    {
        JoinServerSettings.TestContent = new JoinSection("Hi", null, null, new JoinServerZone("UTC", 0));
        _ = ModManifest.Describe();
        string before = ModManifest.TestCatalogStamp;
        string canonicalWithout = JoinSectionJson.Canonical(ModManifest.LastEnvelope!.Join!);
        Assert.DoesNotContain("requiredClient", canonicalWithout);
        Assert.DoesNotContain("policyMode", canonicalWithout);
        Assert.DoesNotContain("approved", canonicalWithout);

        Assert.True(Apply(Req(JoinAdminOps.RequiredAdd, "robes.pack", "Owner-Robes", "1.2.0", reason: "Our robes.", name: "Robe Pack")).Ok);
        Assert.True(Apply(Req(JoinAdminOps.PolicyMode, value: "approved")).Ok);
        Assert.True(Apply(Req(JoinAdminOps.ApproveAdd, "cosmetic.mod", name: "Cosmetic")).Ok);
        Assert.NotEqual(before, ModManifest.TestCatalogStamp);
        var envelope = ModManifest.LastEnvelope!;
        var join = envelope.Join!;
        var req = Assert.Single(join.RequiredClient);
        Assert.Equal(("robes.pack", "Owner-Robes", "1.2.0", ClientModRule.AtLeast, "Our robes.", "Robe Pack"), (req.Id, req.Package, req.Version, req.Rule, req.Reason, req.Name));
        Assert.Equal(ClientPolicyMode.Approved, join.PolicyMode);
        Assert.Equal("Cosmetic", Assert.Single(join.Approved).Name);
        Assert.NotNull(JoinSectionJson.Verified(envelope, out var error));

        // What a client receives: the envelope as JSON, parsed and verified.
        Assert.True(ModManifestJson.TryParseEnvelope(ModManifestJson.CanonicalEnvelope(envelope), out var parsed, out var parseError), parseError);
        var verified = JoinSectionJson.Verified(parsed, out error);
        Assert.True(verified != null, error);
        Assert.Equal("Robe Pack", Assert.Single(verified!.RequiredClient).Name);
        Assert.Equal(ClientPolicyMode.Approved, verified.PolicyMode);

        // Any change to a signed policy field breaks the signature.
        var tampered = verified.WithPolicy(new[] { req.With(rule: ClientModRule.Any) }, verified.PolicyMode, verified.Approved);
        Assert.NotEqual(JoinSectionJson.Canonical(verified), JoinSectionJson.Canonical(tampered));
    }

    [Fact]
    public void The_mods_hash_moves_with_the_policy_and_not_without_one()
    {
        var plain = new JoinSection("Hi", null, null, new JoinServerZone("UTC", 0));
        JoinPageModel Model(JoinSection s) => JoinPageModel.Build(new JoinFacts
        {
            ServerName = "S", Asked = Array.Empty<Handshake.ModRecord>(), Provided = Array.Empty<Handshake.ModRecord>(), Section = s,
        });

        string none = JoinShowPolicy.ModsHash(JoinShowPolicy.ModsSnapshot(Model(plain)));
        Assert.Equal(none, JoinShowPolicy.ModsHash(JoinShowPolicy.ModsSnapshot(Model(plain.WithPolicy(null, ClientPolicyMode.Any, null)))));
        var row = new ClientModRow("robes.pack", "", "1.2.0", ClientModRule.AtLeast, "Robes.");
        string required = JoinShowPolicy.ModsHash(JoinShowPolicy.ModsSnapshot(Model(plain.WithPolicy(new[] { row }, ClientPolicyMode.Any, null))));
        Assert.NotEqual(none, required);
        string rule = JoinShowPolicy.ModsHash(JoinShowPolicy.ModsSnapshot(Model(plain.WithPolicy(new[] { row.With(rule: ClientModRule.Exact) }, ClientPolicyMode.Any, null))));
        Assert.NotEqual(required, rule);
        string reason = JoinShowPolicy.ModsHash(JoinShowPolicy.ModsSnapshot(Model(plain.WithPolicy(new[] { row.With(reason: "Other.") }, ClientPolicyMode.Any, null))));
        Assert.NotEqual(required, reason);
        string mode = JoinShowPolicy.ModsHash(JoinShowPolicy.ModsSnapshot(Model(plain.WithPolicy(null, ClientPolicyMode.Approved, null))));
        Assert.NotEqual(none, mode);
        string approved = JoinShowPolicy.ModsHash(JoinShowPolicy.ModsSnapshot(Model(plain.WithPolicy(null, ClientPolicyMode.Approved,
            new[] { new ClientModRow("cosmetic.mod", "", "", ClientModRule.Any) }))));
        Assert.NotEqual(mode, approved);
    }

    [Fact]
    public void A_section_breaking_a_policy_cap_or_rule_is_dropped()
    {
        var sb = new StringBuilder("{\"schema\":1,\"message\":\"\",\"requiredClient\":[");
        for (int i = 0; i < 17; i++) sb.Append(i > 0 ? "," : "").Append("{\"id\":\"r").Append(i).Append("\",\"rule\":\"any\"}");
        sb.Append("]}");
        using (var doc = System.Text.Json.JsonDocument.Parse(sb.ToString()))
        {
            Assert.False(JoinSectionJson.TryRead(doc.RootElement, out _, out var error));
            Assert.Contains("more than 16 requiredClient", error);
        }

        using (var doc = System.Text.Json.JsonDocument.Parse("{\"schema\":1,\"approved\":[{\"id\":\"a\",\"rule\":\"sometimes\"}]}"))
            Assert.False(JoinSectionJson.TryRead(doc.RootElement, out _, out _));
        using (var doc = System.Text.Json.JsonDocument.Parse("{\"schema\":1,\"policyMode\":\"strict\"}"))
            Assert.False(JoinSectionJson.TryRead(doc.RootElement, out _, out _));
    }

    // ---- the panel's wire and model ------------------------------------------------------------------------------

    [Fact]
    public void The_state_carries_the_policy_and_pages_a_long_approved_list()
    {
        var state = new JoinAdminState { PolicySupported = true, PolicyMode = "approved", Unverified = "refuse", ApprovedTotal = 128 };
        state.RequiredClient.Add(new JoinAdminClientRow { Id = "robes.pack", Version = "1.2.0", Rule = "exact", Reason = "Robes.", Name = "Robe Pack" });
        for (int i = 0; i < 128; i++)
            state.Approved.Add(new JoinAdminClientRow { Id = "some.long.author.prefix.mod" + i, Package = "SomeOwner-SomeLongPackageName" + i, Rule = "any", Name = "A readable name " + i });
        byte[] bytes = JoinAdminWire.EncodeResponse(new JoinAdminResponse { Op = "get", Ok = true, State = state });
        Assert.InRange(bytes.Length, 1, JoinAdminLimits.MaxPacketBytes);
        Assert.True(JoinAdminWire.TryDecodeResponse(bytes, out var back));
        var s = back.State!;
        Assert.True(s.PolicySupported);
        Assert.Equal(("approved", "refuse", 128), (s.PolicyMode, s.Unverified, s.ApprovedTotal));
        Assert.Equal("Robe Pack", Assert.Single(s.RequiredClient).Name);
        Assert.InRange(s.Approved.Count, 1, 127);
        Assert.Equal("some.long.author.prefix.mod0", s.Approved[0].Id);
        Assert.Equal(s.Approved.Count, ClientPolicyPanelModel.PageFrom(0, s.Approved.Count, 128, forward: true));
        Assert.Equal(0, ClientPolicyPanelModel.PageFrom(10, 40, 128, forward: false));
        Assert.Equal("1–" + s.Approved.Count + " of 128", ClientPolicyText.Pager(0, s.Approved.Count, 128));

        // An older server's state has no policy keys.
        var old = JoinAdminWire.EncodeResponse(new JoinAdminResponse { Op = "get", Ok = true, State = new JoinAdminState() });
        Assert.True(JoinAdminWire.TryDecodeResponse(old, out var o));
        Assert.False(o.State!.PolicySupported);

        // The server's state pages from approvedFrom.
        for (int i = 0; i < 3; i++) Assert.True(Apply(Req(JoinAdminOps.ApproveAdd, "p" + i + ".mod")).Ok);
        var page = Apply(new JoinAdminRequest { Op = JoinAdminOps.Get, ApprovedFrom = 2 });
        Assert.Equal((2, 3), (page.State!.ApprovedFrom, page.State.ApprovedTotal));
        Assert.Equal("p2.mod", Assert.Single(page.State.Approved).Id);
    }

    [Fact]
    public void Approve_everything_lists_this_pcs_mods_minus_the_always_allowed_and_the_approved_and_splits_into_requests()
    {
        var state = new JoinAdminState { PolicySupported = true };
        state.Approved.Add(new JoinAdminClientRow { Id = "already.mod" });
        var local = new List<ClientPolicyLocalMod>
        {
            new("progenitor", "Progenitor", "1", ""),
            new("BepInEx.Thing", "Thing", "1", ""),
            new("already.mod", "Already", "1", ""),
            new("cosmetic.mod", "Cosmetic", "1.0", "Owner-Cosmetic"),
            new("cosmetic.mod", "Cosmetic again", "1.0", ""),
            new("plain.mod", "plain.mod", "2", "not valid"),
        };
        var items = ClientPolicyPanelModel.ApproveEverything(local, state);
        Assert.Equal(new[] { "cosmetic.mod", "plain.mod" }, items.Select(i => i.Id));
        Assert.Equal(("Owner-Cosmetic", "Cosmetic"), (items[0].Package, items[0].Name));
        Assert.Equal(("", ""), (items[1].Package, items[1].Name));
        Assert.Equal("This approves 2 client mods loaded on this PC, at any version: Cosmetic, plain.mod.", ClientPolicyText.ConfirmBody(items));
        Assert.Equal(ClientPolicyText.NothingToApprove, ClientPolicyText.ConfirmBody(new List<JoinAdminBulkItem>()));

        var many = new List<JoinAdminBulkItem>();
        for (int i = 0; i < 150; i++) many.Add(new JoinAdminBulkItem { Id = "m" + i + ".mod", Package = "Owner-" + new string('P', 100) + i, Name = new string('N', 64) });
        var requests = JoinAdminWire.BulkRequests(many);
        Assert.Equal(150, requests.Sum(r => r.Items.Count));
        Assert.All(requests, r =>
        {
            Assert.InRange(r.Items.Count, 1, ClientModPolicyLimits.MaxBulk);
            Assert.InRange(JoinAdminWire.EncodeRequest(r).Length, 1, JoinAdminLimits.MaxPacketBytes);
        });
        Assert.Contains("and 126 more", ClientPolicyText.ConfirmBody(many));
    }

    [Fact]
    public void The_panel_lines_read_as_specified()
    {
        var req = new JoinAdminClientRow { Id = "robes.pack", Package = "Owner-Robes", Version = "1.2.0", Rule = "at-least", Name = "Robe Pack" };
        Assert.Equal("robes.pack · Owner-Robes · 1.2.0 or newer", ClientPolicyText.RequiredSecond(req));
        Assert.Equal("Robe Pack", ClientPolicyText.NameOf(req));
        var app = new JoinAdminClientRow { Package = "Owner-Cosmetic", Rule = "any" };
        Assert.Equal("Owner-Cosmetic · any version", ClientPolicyText.ApprovedSecond(app));
        Assert.Equal("Owner-Cosmetic", ClientPolicyText.NameOf(app));
        Assert.Equal("3 required", ClientPolicyText.RequiredCount(3));
        Assert.Equal("Approved client mods · 7", ClientPolicyText.ApprovedHeading(7));
        Assert.Equal("Also allowed: BepInEx, Progenitor, this server's required and recommended mods, and mods this server runs.", ClientPolicyText.AlwaysApproved);
        Assert.Equal("Players' own games report their mods. This keeps honest players to the house rules; it doesn't stop cheats.", ClientPolicyText.Honesty);
        Assert.Equal(ClientModRule.Exact, ClientModRules.Next(ClientModRule.AtLeast));
        Assert.Equal(ClientModRule.AtLeast, ClientModRules.Next(ClientModRule.Any));
        Assert.True(ClientPolicyPanelModel.IsRequired(new JoinAdminState { RequiredClient = { req } }, "", "owner-robes"));
    }
}
