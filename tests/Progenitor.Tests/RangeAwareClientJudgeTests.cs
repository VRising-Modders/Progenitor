using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C3, the client's copy of the server's judgment (<see cref="VersionMatching.ClientContextFor"/>): the
/// join screens' required rows, the refusal rows, the catalog comparison report and the Install tab's per-side status judge
/// a version as the server's join check did (its Version matching, per-mod rules and the newer side's range from its
/// catalog), not by the row's attribute rule. Game-free.
/// </summary>
public sealed class RangeAwareClientJudgeTests : IDisposable
{
    private const string Range = "progenitor.compattest.range";
    private const string Default = "progenitor.compattest.default";
    private const string RangePeers = ">=1.2.0 <2.0.0";

    private readonly ModManifestKey _key = ModManifestCrypto.Generate();

    public void Dispose() => _key.Dispose();

    private static Handshake.ModRecord Row(string id, string version) =>
        Handshake.ModRecord.CreateDeclared(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);

    private static ManifestMod Mod(string id, string version, string? peers, string? match = null) =>
        new(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true) { Peers = peers, Match = match };

    private ManifestBody Body(string? matching, params ManifestMod[] mods) =>
        new(1, "Compat test", "2026-10-10T12:00:00Z", _key.ToOperator("operator"), null, mods) { Matching = matching };

    /// <summary>
    /// The client's own effective ranges by (id, version): the range mod's loaded copy declares its Peers, the default mod
    /// nothing; any other version of the range mod is unknown (null: the default for that version).
    /// </summary>
    private static Func<string, string, string?> OwnRanges(string rangeLoaded) =>
        (id, version) => id == Range ? (version == rangeLoaded ? RangePeers : null) : CompatDeclaration.DefaultRange(version);

    private static JoinPageModel Refused(ManifestBody? catalog, string rangeHave = "1.3.0", string defaultHave = "1.5.0",
        string rangeServer = "1.2.0", string defaultServer = "1.4.0")
    {
        var provided = new[] { Row(Range, rangeHave), Row(Default, defaultHave) };
        return JoinPageModel.Build(new JoinFacts
        {
            ServerName = "Compat test",
            Asked = new[] { Row(Range, rangeServer), Row(Default, defaultServer) },
            Provided = provided,
            Catalog = catalog,
            Refused = true,
            OwnRangeOf = OwnRanges(rangeHave),
        });
    }

    private static JoinRequiredRow RowOf(JoinPageModel model, string id) => model.Required.Single(r => r.Id == id);

    [Fact]
    public void The_live_refusal_shows_the_range_accepted_mod_as_ready_and_only_the_refused_one_as_needing_attention()
    {
        // Live 2026-10-10: the refusal's catalog is narrowed to the refused row; it still carries the Version matching.
        var model = Refused(Body("range", Mod(Default, "1.4.0", CompatDeclaration.DefaultRange("1.4.0"))));

        var range = RowOf(model, Range);
        Assert.True(range.Ready);
        Assert.Equal(JoinRowStatus.Ready, range.Status);
        Assert.Equal(JoinRowAction.Ready, range.Action);
        Assert.Equal(JoinRowTags.None, range.Tags);

        var dflt = RowOf(model, Default);
        Assert.False(dflt.Ready);
        Assert.Equal(JoinRowStatus.Newer, dflt.Status);
        Assert.Equal(1, model.NeedAttention);
    }

    [Fact]
    public void Exact_version_for_all_still_needs_attention_for_a_newer_copy_the_range_would_accept()
    {
        var exact = Refused(Body("exact", Mod(Range, "1.2.0", RangePeers, "exact"), Mod(Default, "1.4.0", null, "exact")));
        Assert.Equal(JoinRowStatus.Newer, RowOf(exact, Range).Status);
        Assert.False(RowOf(exact, Range).Ready);
        Assert.Equal(2, exact.NeedAttention);

        // Exact for all with the range mod's row narrowed away: the mode itself decides.
        var narrowed = Refused(Body("exact", Mod(Default, "1.4.0", null, "exact")));
        Assert.False(RowOf(narrowed, Range).Ready);

        // An admin's per-mod Exact rule under declared ranges wins over the range.
        var rule = Refused(Body("range", Mod(Range, "1.2.0", RangePeers, "exact")));
        Assert.False(RowOf(rule, Range).Ready);

        // Same minor for all: 1.3.0 is not 1.2.x.
        Assert.False(RowOf(Refused(Body("minor")), Range).Ready);
    }

    [Fact]
    public void A_server_newer_copy_is_ready_when_the_servers_own_range_includes_the_clients()
    {
        var model = Refused(Body("range", Mod(Range, "1.3.0", RangePeers)), rangeHave: "1.2.0", rangeServer: "1.3.0");
        Assert.True(RowOf(model, Range).Ready);
    }

    [Fact]
    public void Without_a_catalog_that_carries_the_matching_the_request_rules_decide_as_before()
    {
        Assert.Equal(JoinRowStatus.Newer, RowOf(Refused(null), Range).Status);
        Assert.Equal(JoinRowStatus.Newer, RowOf(Refused(Body(null, Mod(Range, "1.2.0", null))), Range).Status);
    }

    [Fact]
    public void The_refusal_rows_drop_a_range_accepted_mod_once_the_catalog_says_how_the_server_judges()
    {
        var asked = new[] { Row(Range, "1.2.0"), Row(Default, "1.4.0") };
        var provided = new[] { Row(Range, "1.3.0"), Row(Default, "1.5.0") };
        var before = ModManifestDeny.BuildLocalRows(asked, provided);
        Assert.Contains(before, r => r.Id == Range);

        var judge = VersionMatching.ClientContextFor(Body("range"), asked, OwnRanges("1.3.0"));
        var after = ModManifestDeny.BuildLocalRows(asked, provided, null, judge);
        Assert.DoesNotContain(after, r => r.Id == Range);
        Assert.Contains(after, r => r.Id == Default);
    }

    private ManifestEnvelope Signed(ManifestBody body) =>
        new(body, _key.Sign(ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(body))));

    private static LoadedModInfo Loaded(string id, string version) =>
        new(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true);

    private ManifestDiff Classify(string? matching, string? match, string rangeHave = "1.3.0", string defaultHave = "1.4.1")
    {
        var body = Body(matching,
            Mod(Range, "1.2.0", matching == null ? null : RangePeers, match),
            Mod(Default, "1.4.0", matching == null ? null : CompatDeclaration.DefaultRange("1.4.0"), match));
        var client = new[] { Loaded(Range, rangeHave), Loaded(Default, defaultHave) };
        return ModManifestCompare.Classify(Signed(body), client, trust: null, host: null,
            ownRangeOf: OwnRanges(rangeHave));
    }

    [Fact]
    public void The_catalog_report_lists_versions_the_server_accepts_as_present_not_mismatch()
    {
        var diff = Classify("range", null);
        Assert.True(diff.SignatureValid);
        Assert.Empty(diff.Mismatches);
        Assert.Empty(diff.RestartRequired);
        Assert.Contains(diff.Present, m => m.Id == Range);
        Assert.Contains(diff.Present, m => m.Id == Default);
        Assert.Equal(2, diff.Accepted.Count);

        var block = ModManifestCompare.FormatLogBlock(Signed(Body("range")), diff);
        Assert.Contains("mismatch (0):", block);
        Assert.Contains(Default + " 1.4.1 (server 1.4.0, accepted by the client's range ", block);
        Assert.Contains(Range + " 1.3.0 (server 1.2.0, accepted by the client's range " + RangePeers + ")", block);
        Assert.DoesNotContain("(Exact)", block);
    }

    [Fact]
    public void The_catalog_report_still_lists_a_version_the_server_refuses_as_a_mismatch_with_its_reason()
    {
        var exact = Classify("exact", "exact");
        Assert.Equal(2, exact.Mismatches.Count);
        Assert.Empty(exact.Accepted);
        var block = ModManifestCompare.FormatLogBlock(Signed(Body("exact")), exact);
        Assert.Contains(Range + " have 1.3.0 want 1.2.0 (this server requires exactly 1.2.0 (you have 1.3.0))", block);

        // Declared ranges, but the client's 1.5.0 works only with 1.5.x: refused, as the server's join check refused it.
        var refused = Classify("range", null, defaultHave: "1.5.0");
        Assert.Contains(refused.Mismatches, m => m.Wanted.Id == Default && m.Have == "1.5.0");
        Assert.DoesNotContain(refused.Mismatches, m => m.Wanted.Id == Range);

        // An older server's catalog (no matching): the row's rule decides, as before.
        var legacy = Classify(null, null);
        Assert.Equal(2, legacy.Mismatches.Count);
        Assert.Contains("(Exact)", ModManifestCompare.FormatLogBlock(Signed(Body(null)), legacy));
    }

    [Fact]
    public void A_turned_off_copy_at_another_version_is_never_judged_with_the_loaded_copys_range()
    {
        // Review 2026-10-10: loaded 1.1.0 declares >=1.0.0 <2.0.0; the server's 1.4.0 (peers 1.4.x) refuses it. A turned-off
        // 1.5.0 has its own range (unknown here: the default 1.5.x), which does not include 1.4.0, so it is not "Turn on".
        var asked = new[] { Row(Range, "1.4.0") };
        var provided = new[] { Row(Range, "1.1.0") };
        var judge = VersionMatching.ClientContextFor(Body("range", Mod(Range, "1.4.0", CompatDeclaration.DefaultRange("1.4.0"))), asked,
            (id, version) => version == "1.1.0" ? ">=1.0.0 <2.0.0" : null);
        Assert.NotNull(Handshake.Compare.EnforcedKickReason(Range, asked, provided, judge));
        Assert.False(JoinPageModel.OffCopySatisfies(Range, new JoinOffCopy { Version = "1.5.0" }, asked, provided, judge));
        // A turned-off 1.4.2 is inside the server's 1.4.x: Turn on.
        Assert.True(JoinPageModel.OffCopySatisfies(Range, new JoinOffCopy { Version = "1.4.2" }, asked, provided, judge));

        // Through the page: the 1.5.0 copy keeps the row needing attention.
        var model = JoinPageModel.Build(new JoinFacts
        {
            Asked = asked,
            Provided = provided,
            Catalog = Body("range", Mod(Range, "1.4.0", CompatDeclaration.DefaultRange("1.4.0"))),
            Refused = true,
            OwnRangeOf = (id, version) => version == "1.1.0" ? ">=1.0.0 <2.0.0" : null,
            OffCopyOf = id => new JoinOffCopy { Version = "1.5.0" },
        });
        Assert.NotEqual(JoinRowStatus.TurnedOff, RowOf(model, Range).Status);
        Assert.False(RowOf(model, Range).Ready);
    }

    [Fact]
    public void The_own_range_is_the_loaded_copys_only_at_its_version()
    {
        const string id = "progenitor.compattest.ownrange";
        var previous = VersionMatching.DeclarationSource;
        VersionMatching.DeclarationSource = g => g == id
            ? new CompatDeclaration("1.3.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, false, RangePeers)
            : null;
        try
        {
            Assert.Equal(RangePeers, VersionMatching.OwnRangeOf(id, "1.3.0"));
            Assert.Null(VersionMatching.OwnRangeOf(id, "1.5.0"));

            // The Install tab's probe: a pending 1.5.0 (no range of its own known) against the server's 1.2.0 with peers is
            // judged with no range, so the request rule's cap applies and it is a version mismatch, not accepted.
            var req = new ServerRequirement { ModId = id, Version = "1.2.0", Rule = NetworkVersionRule.Exact, Peers = RangePeers };
            Assert.False(ServerVersionCheck.Accepts(req, "1.5.0", VersionMatching.OwnRangeOf(id, "1.5.0")));
            Assert.True(ServerVersionCheck.Accepts(req, "1.3.0", VersionMatching.OwnRangeOf(id, "1.3.0")));
        }
        finally
        {
            VersionMatching.DeclarationSource = previous;
        }
    }

    [Fact]
    public void The_install_tabs_side_status_is_not_a_version_mismatch_when_the_server_accepts_the_version()
    {
        var route = new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.None);
        var refused = SideStatusRules.Decide(new SideInputs(ClientHalfState.Installed, "1.3.0", ServerHalfState.Installed, "1.2.0",
            ServerHalfSource.Handshake, needsBoth: true, route));
        Assert.Equal(SideStatusRules.VersionMismatch, refused.Label);
        Assert.Equal(SideAction.UpdateServer, refused.Action);

        var accepted = SideStatusRules.Decide(new SideInputs(ClientHalfState.Installed, "1.3.0", ServerHalfState.Installed, "1.2.0",
            ServerHalfSource.Handshake, needsBoth: true, route, versionsAccepted: true));
        Assert.Equal(SideStatusRules.Installed, accepted.Label);
        Assert.Equal(SideAction.None, accepted.Action);
        Assert.False(accepted.Mismatch);
        Assert.False(accepted.Warn);
        Assert.Contains("the server accepts this client's version", accepted.Sentence);
    }
}
