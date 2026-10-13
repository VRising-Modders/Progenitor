using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C3 (docs/mod-compatibility.md, "Evaluation at the join" and "Server rules: version matching"): the
/// join's version judgment under the three modes, per-mod rules, the newer side's range, the default for undeclared mods,
/// optional mods, the request rows a strict server sends, the capability bits and the bounded feature table hold. The
/// back-compat cases (an older client against this server, this client against an older server) are at the end.
/// </summary>
public class VersionMatchingTests
{
    private const string Trade = "example.trade";
    private const string Cosmetic = "example.cosmetic";

    private static Handshake.ModRecord Server(string id, string version,
        NetworkCompatibilityLevel level = NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule rule = NetworkVersionRule.Exact) =>
        Handshake.ModRecord.CreateDeclared(id, version, level, rule);

    private static Handshake.ModRecord Client(string id, string version,
        NetworkCompatibilityLevel level = NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule rule = NetworkVersionRule.Exact) =>
        Handshake.ModRecord.CreateDeclared(id, version, level, rule);

    private static VersionJudgeContext Ctx(
        VersionMatchingMode mode = VersionMatchingMode.Range,
        Dictionary<string, string>? serverRanges = null,
        Dictionary<string, string>? clientRanges = null,
        Dictionary<string, string>? serverDeclared = null,
        Dictionary<string, ClientModRule>? rules = null) =>
        new()
        {
            Settings = new VersionMatchSettings(mode, rules),
            ServerRange = g => serverRanges != null && serverRanges.TryGetValue(g, out var r) ? r : null,
            ServerDeclaredRange = g => serverDeclared != null && serverDeclared.TryGetValue(g, out var r) ? r : null,
            ClientRanges = clientRanges,
        };

    private static List<string> Judge(Handshake.ModRecord server, Handshake.ModRecord client, VersionJudgeContext ctx) =>
        Handshake.Compare.Evaluate(new[] { server }, new[] { client }, out _, ctx);

    // ---- the newer side's range (declared ranges, a client that sent its table) ----------------------------------

    [Fact]
    public void Equal_versions_always_pass_under_every_mode()
    {
        foreach (VersionMatchingMode mode in Enum.GetValues(typeof(VersionMatchingMode)))
        {
            var ctx = Ctx(mode, clientRanges: new Dictionary<string, string>());
            Assert.Empty(Judge(Server(Trade, "1.2.0"), Client(Trade, "1.2.0"), ctx));
            Assert.Empty(Judge(Server(Trade, "1.2.0"), Client(Trade, "1.2.0"), Ctx(mode)));
        }
    }

    [Fact]
    public void A_newer_server_decides_with_its_own_range()
    {
        var ranges = new Dictionary<string, string> { [Trade] = ">=1.2.0 <2.0.0" };
        var clientRanges = new Dictionary<string, string> { [Trade] = "1.2.x" };

        var ctx = Ctx(serverRanges: ranges, clientRanges: clientRanges);
        Assert.Empty(Judge(Server(Trade, "1.4.0"), Client(Trade, "1.2.3"), ctx));
        Assert.Contains(ctx.Accepted, l => l == "example.trade client 1.2.3 accepted by server 1.4.0's range >=1.2.0 <2.0.0");

        var refused = Judge(Server(Trade, "1.4.0"), Client(Trade, "1.1.0"), Ctx(serverRanges: ranges, clientRanges: clientRanges));
        Assert.Equal("example.trade: this server's 1.4.0 works with 1.2.0 or newer, below 2.0.0; you have 1.1.0", Assert.Single(refused));
    }

    [Fact]
    public void A_newer_client_decides_with_its_range_even_when_the_older_server_would_refuse()
    {
        // The server (1.2.0, nothing declared) would accept only 1.2.x; the newer client vouches for it.
        var ctx = Ctx(clientRanges: new Dictionary<string, string> { [Trade] = ">=1.2.0 <2.0.0" });
        Assert.Empty(Judge(Server(Trade, "1.2.0"), Client(Trade, "1.4.0"), ctx));
        Assert.Contains(ctx.Accepted, l => l.Contains("accepted by the client's range >=1.2.0 <2.0.0", StringComparison.Ordinal));

        // And the newer client's range can refuse an older server even when the server's range would have taken it.
        var refused = Judge(Server(Trade, "1.2.0"), Client(Trade, "1.4.0"),
            Ctx(serverRanges: new Dictionary<string, string> { [Trade] = "*" }, clientRanges: new Dictionary<string, string> { [Trade] = "1.4.x" }));
        Assert.Equal("example.trade: your 1.4.0 works with any 1.4.x version; this server has 1.2.0", Assert.Single(refused));
    }

    [Fact]
    public void A_side_that_declared_nothing_gets_the_same_minor()
    {
        // Server newer, no range from either side: the server's default is 1.4.x.
        var ctx = Ctx(clientRanges: new Dictionary<string, string>());
        Assert.Empty(Judge(Server(Trade, "1.4.1"), Client(Trade, "1.4.0"), ctx));
        var refused = Judge(Server(Trade, "1.5.0"), Client(Trade, "1.4.0"), Ctx(clientRanges: new Dictionary<string, string>()));
        Assert.Equal("example.trade: this server's 1.5.0 works with any 1.5.x version; you have 1.4.0", Assert.Single(refused));

        // Client newer and its table has no row for the mod: its default, 1.5.x.
        refused = Judge(Server(Trade, "1.4.0"), Client(Trade, "1.5.0"), Ctx(clientRanges: new Dictionary<string, string>()));
        Assert.Equal("example.trade: your 1.5.0 works with any 1.5.x version; this server has 1.4.0", Assert.Single(refused));
    }

    [Fact]
    public void Versions_that_cannot_be_compared_are_refused_in_words()
    {
        var refused = Judge(Server(Trade, "nightly-a"), Client(Trade, "nightly-b"), Ctx(clientRanges: new Dictionary<string, string>()));
        Assert.Equal("example.trade: this server has nightly-a and you have nightly-b, which can't be compared", Assert.Single(refused));
        Assert.Empty(Judge(Server(Trade, "Nightly-A"), Client(Trade, "nightly-a"), Ctx(clientRanges: new Dictionary<string, string>())));
    }

    // ---- server-wide modes and per-mod rules ----------------------------------------------------------------------

    [Fact]
    public void Exact_for_all_ignores_every_declared_range()
    {
        var ctx = Ctx(VersionMatchingMode.Exact,
            serverRanges: new Dictionary<string, string> { [Trade] = "*" },
            clientRanges: new Dictionary<string, string> { [Trade] = "*" });
        var refused = Judge(Server(Trade, "1.2.0"), Client(Trade, "1.3.0"), ctx);
        Assert.Equal("example.trade: this server requires exactly 1.2.0 (you have 1.3.0)", Assert.Single(refused));
        // The same for a client that sent no ranges.
        refused = Judge(Server(Trade, "1.2.0"), Client(Trade, "1.2.1"), Ctx(VersionMatchingMode.Exact));
        Assert.Equal("example.trade: this server requires exactly 1.2.0 (you have 1.2.1)", Assert.Single(refused));
    }

    [Fact]
    public void Same_minor_for_all_needs_the_servers_major_and_minor()
    {
        var ctx = Ctx(VersionMatchingMode.Minor, serverRanges: new Dictionary<string, string> { [Trade] = "*" },
            clientRanges: new Dictionary<string, string> { [Trade] = "*" });
        Assert.Empty(Judge(Server(Trade, "1.2.0"), Client(Trade, "1.2.7"), ctx));
        var refused = Judge(Server(Trade, "1.2.0"), Client(Trade, "1.3.0"), ctx);
        Assert.Equal("example.trade: this server requires the same minor version as 1.2.0 (1.2.x; you have 1.3.0)", Assert.Single(refused));
    }

    [Fact]
    public void A_per_mod_rule_wins_over_the_server_wide_setting()
    {
        var rules = new Dictionary<string, ClientModRule> { [Cosmetic] = ClientModRule.Any, [Trade] = ClientModRule.AtLeast };
        var server = new[] { Server(Trade, "1.2.0"), Server(Cosmetic, "3.0.0") };

        // Exact for all, but Any version for the cosmetic pack and "or newer" for trade.
        var ctx = Ctx(VersionMatchingMode.Exact, rules: rules);
        var problems = Handshake.Compare.Evaluate(server, new[] { Client(Trade, "1.5.0"), Client(Cosmetic, "1.0.0") }, out _, ctx);
        Assert.Empty(problems);
        problems = Handshake.Compare.Evaluate(server, new[] { Client(Trade, "1.1.0"), Client(Cosmetic, "1.0.0") }, out var failed, Ctx(VersionMatchingMode.Exact, rules: rules));
        Assert.Equal("example.trade: this server requires 1.2.0 or newer (you have 1.1.0)", Assert.Single(problems));
        Assert.Equal(Trade, Assert.Single(failed));

        // Declared ranges, but an Exact rule on one mod.
        var exact = new Dictionary<string, ClientModRule> { [Trade] = ClientModRule.Exact };
        problems = Judge(Server(Trade, "1.2.0"), Client(Trade, "1.2.1"),
            Ctx(serverRanges: new Dictionary<string, string> { [Trade] = "*" }, clientRanges: new Dictionary<string, string>(), rules: exact));
        Assert.Equal("example.trade: this server requires exactly 1.2.0 (you have 1.2.1)", Assert.Single(problems));
    }

    [Fact]
    public void An_optional_mod_both_sides_run_is_checked_and_a_missing_one_is_not()
    {
        var optional = Server(Cosmetic, "2.0.0", NetworkCompatibilityLevel.NoNeedForSync);
        var refused = Judge(optional, Client(Cosmetic, "2.1.0", NetworkCompatibilityLevel.NoNeedForSync), Ctx(VersionMatchingMode.Exact));
        Assert.Equal("example.cosmetic: this server requires exactly 2.0.0 (you have 2.1.0)", Assert.Single(refused));
        Assert.Empty(Handshake.Compare.Evaluate(new[] { optional }, Array.Empty<Handshake.ModRecord>(), out _, Ctx(VersionMatchingMode.Exact)));
    }

    [Fact]
    public void A_mod_both_sides_declare_is_judged_once()
    {
        var problems = Handshake.Compare.Evaluate(
            new[] { Server(Trade, "1.2.0") },
            new[] { Client(Trade, "1.3.0") },
            out var failed,
            Ctx(VersionMatchingMode.Exact));
        Assert.Single(problems);
        Assert.Equal(new[] { Trade }, failed);
    }

    [Fact]
    public void Missing_mods_keep_their_presence_reasons()
    {
        var problems = Handshake.Compare.Evaluate(new[] { Server(Trade, "1.2.0") }, Array.Empty<Handshake.ModRecord>(), out _, Ctx(VersionMatchingMode.Exact));
        Assert.Contains("the server needs it", Assert.Single(problems), StringComparison.Ordinal);
    }

    // ---- the request a strict server sends -----------------------------------------------------------------------

    [Fact]
    public void Strict_modes_and_per_mod_rules_rewrite_the_request_rule_bytes()
    {
        var undeclared = Handshake.ModRecord.Undeclared(Trade, "1.2.0");
        var optional = Server(Cosmetic, "2.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None);
        var clientOnly = Server("example.ui", "1.0.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.None);

        var exact = VersionMatchJudge.RequestRows(new[] { undeclared, optional, clientOnly }, new VersionMatchSettings(VersionMatchingMode.Exact));
        Assert.Equal(NetworkVersionRule.Exact, exact[0].VersionRule);
        Assert.True(exact[0].Declared);
        Assert.Equal(NetworkCompatibilityLevel.ServerMustHave, exact[0].Level);
        Assert.Equal(NetworkVersionRule.Exact, exact[1].VersionRule);
        Assert.Equal(NetworkCompatibilityLevel.NoNeedForSync, exact[1].Level);
        Assert.Equal(NetworkVersionRule.None, exact[2].VersionRule);

        var minor = VersionMatchJudge.RequestRow(optional, new VersionMatchSettings(VersionMatchingMode.Minor));
        Assert.Equal(NetworkVersionRule.Minor, minor.VersionRule);

        var rules = new Dictionary<string, ClientModRule> { [Cosmetic] = ClientModRule.Any, [Trade] = ClientModRule.AtLeast };
        var mixed = VersionMatchJudge.RequestRows(new[] { undeclared, optional }, new VersionMatchSettings(VersionMatchingMode.Exact, rules));
        // "Or newer" has no wire rule: written as None, so no client pauses or swaps away a newer copy the server accepts.
        Assert.Equal(NetworkVersionRule.None, mixed[0].VersionRule);
        Assert.True(mixed[0].Declared);
        Assert.Equal(NetworkVersionRule.None, mixed[1].VersionRule);

        // Declared ranges: the rows are the attribute's.
        var range = VersionMatchJudge.RequestRow(undeclared, VersionMatchSettings.Default);
        Assert.Equal(undeclared.VersionRule, range.VersionRule);
        Assert.False(range.Declared);
    }

    [Fact]
    public void Capability_bits_are_new_and_this_core_declares_ranges_only()
    {
        Assert.Equal(8388608u, (uint)Handshake.Capability.VersionRanges);
        Assert.Equal(16777216u, (uint)Handshake.Capability.MatchSameMinor);
        Assert.Equal(33554432u, (uint)Handshake.Capability.MatchExact);
        Assert.NotEqual(Handshake.Capability.None, Handshake.SelfCapabilities & Handshake.Capability.VersionRanges);
        Assert.Equal(Handshake.Capability.None, Handshake.SelfCapabilities & (Handshake.Capability.MatchExact | Handshake.Capability.MatchSameMinor));
        Assert.NotEqual(Handshake.Capability.None, ModCompatCaps.RequestBits & Handshake.Capability.VersionRanges);

        foreach (VersionMatchingMode mode in Enum.GetValues(typeof(VersionMatchingMode)))
        {
            var bits = VersionMatchJudge.ModeBits(new VersionMatchSettings(mode));
            Assert.Equal(mode, VersionMatchJudge.ModeOf(bits | Handshake.Capability.ModFeatures));
        }

        Assert.Equal(Handshake.Capability.None, VersionMatchJudge.ModeBits(VersionMatchSettings.Default));
        // The bits survive the capability row.
        var row = Handshake.Compare.CreateCapabilityRecord(ModCompatCaps.RequestBits | Handshake.Capability.MatchExact);
        Assert.Equal(VersionMatchingMode.Exact, VersionMatchJudge.ModeOf(Handshake.Compare.ReadCapabilityBlob(row.Version)));
    }

    [Theory]
    [InlineData("range", 0)]
    [InlineData("declared", 0)]
    [InlineData("Same minor", 1)]
    [InlineData("minor", 1)]
    [InlineData("EXACT", 2)]
    [InlineData("exact-version-for-all", 2)]
    public void Mode_words_parse(string word, int modeValue)
    {
        var mode = (VersionMatchingMode)modeValue;
        Assert.True(VersionMatchingText.TryParse(word, out var parsed));
        Assert.Equal(mode, parsed);
        Assert.True(VersionMatchingText.TryParse(VersionMatchingText.Word(mode), out var back));
        Assert.Equal(mode, back);
    }

    [Fact]
    public void Unknown_mode_words_are_refused_and_the_labels_are_the_designed_ones()
    {
        Assert.False(VersionMatchingText.TryParse("strict", out _));
        Assert.Equal("Use each mod's declared range", VersionMatchingText.Label(VersionMatchingMode.Range));
        Assert.Equal("Same minor for all", VersionMatchingText.Label(VersionMatchingMode.Minor));
        Assert.Equal("Exact version for all", VersionMatchingText.Label(VersionMatchingMode.Exact));
        Assert.Equal(3, VersionMatchingText.Options.Count);
        Assert.Equal("exact (1 mod rule)", new VersionMatchSettings(VersionMatchingMode.Exact,
            new Dictionary<string, ClientModRule> { [Trade] = ClientModRule.Any }).Describe());
    }

    [Fact]
    public void The_servers_effective_range_comes_from_the_declaration_else_the_row()
    {
        // No registry wired (game-free): an explicit rule on the row, else the same minor.
        Assert.Equal("=1.2.0", VersionMatching.EffectiveRangeOf(Server(Trade, "1.2.0", rule: NetworkVersionRule.Exact)));
        Assert.Equal("*", VersionMatching.EffectiveRangeOf(Server(Trade, "1.2.0", rule: NetworkVersionRule.None)));
        Assert.Equal("1.2.x", VersionMatching.EffectiveRangeOf(Handshake.ModRecord.Undeclared(Trade, "1.2.0")));
    }

    // ---- the join hold -------------------------------------------------------------------------------------------

    [Fact]
    public void The_hold_waits_for_a_table_newer_than_the_answer_and_runs_once()
    {
        var hold = new FeatureTableHold();
        var runs = new List<string>();
        int g = hold.Begin(7, tablesAtAnswer: 2, judge: t => runs.Add(t));
        Assert.True(hold.IsWaiting(7));
        Assert.Null(hold.OnTable(7, tablesNow: 2));     // the table from before this answer does not count
        Assert.Null(hold.OnTable(8, tablesNow: 9));     // another user
        hold.OnTable(7, tablesNow: 3)!("table");
        Assert.Equal(new[] { "table" }, runs);
        Assert.Null(hold.TryExpire(7, g));               // already judged: the expiry does nothing
        Assert.False(hold.IsWaiting(7));
    }

    [Fact]
    public void The_hold_expires_and_a_new_answer_replaces_it()
    {
        var hold = new FeatureTableHold();
        var runs = new List<string>();
        int first = hold.Begin(3, 0, t => runs.Add("first " + t));
        int second = hold.Begin(3, 0, t => runs.Add("second " + t));
        Assert.Null(hold.TryExpire(3, first));          // replaced
        hold.TryExpire(3, second)!("no-table");
        Assert.Equal(new[] { "second no-table" }, runs);

        hold.Begin(4, 0, t => runs.Add("gone"));
        hold.Forget(4);                                  // disconnected
        Assert.Null(hold.OnTable(4, 1));
        Assert.True(FeatureTableHold.WaitSeconds > 0 && FeatureTableHold.WaitSeconds <= 5);
    }

    // ---- back-compat ----------------------------------------------------------------------------------------------

    [Fact]
    public void An_older_client_is_judged_exactly_as_before_when_the_server_declares_no_range()
    {
        var server = new[]
        {
            Server(Trade, "1.4.0", rule: NetworkVersionRule.Exact),
            Server(Cosmetic, "2.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor),
            Handshake.ModRecord.Undeclared("example.plain", "3.0.0"),
        };
        var client = new[]
        {
            Client(Trade, "1.4.1"),
            Client(Cosmetic, "2.1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor),
            Handshake.ModRecord.Undeclared("example.plain", "3.0.1"),
        };

        var before = Handshake.Compare.Evaluate(server, client, out var beforeIds);
        var after = Handshake.Compare.Evaluate(server, client, out var afterIds, Ctx(clientRanges: null));
        Assert.Equal(before, after);
        Assert.Equal(beforeIds, afterIds);
        Assert.Contains("example.trade: expected 1.4.0, got 1.4.1", after);
        Assert.Contains("example.cosmetic: expected 2.0.0 (minor), got 2.1.0", after);
    }

    [Fact]
    public void An_older_client_gets_the_servers_declared_range_when_there_is_one()
    {
        var declared = new Dictionary<string, string> { [Trade] = ">=1.2.0 <2.0.0" };
        var ctx = Ctx(serverDeclared: declared, clientRanges: null);
        Assert.Empty(Judge(Server(Trade, "1.4.0"), Client(Trade, "1.2.0"), ctx));
        Assert.Contains(ctx.Accepted, l => l.Contains("accepted by server 1.4.0's range", StringComparison.Ordinal));
        var refused = Judge(Server(Trade, "1.4.0"), Client(Trade, "2.1.0"), Ctx(serverDeclared: declared, clientRanges: null));
        Assert.Equal("example.trade: this server's 1.4.0 works with 1.2.0 or newer, below 2.0.0; you have 2.1.0", Assert.Single(refused));
    }

    [Fact]
    public void An_older_client_on_the_newer_build_keeps_its_own_rule_in_the_reverse_pass()
    {
        // Pre-C3 the reverse pass judged the client's declared row by the client's own rule. The client is the newer side
        // and sent no range, so its Exact still decides: 1.0.1 (Exact) against the server's 1.0.0 (optional, Minor).
        var problems = Judge(Server(Trade, "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor),
            Client(Trade, "1.0.1"), Ctx(clientRanges: null));
        Assert.Equal("example.trade: expected 1.0.1, got 1.0.0", Assert.Single(problems));

        // A server-wide strict mode or an admin rule still decides through the judgment, not the client's rule.
        var anyRule = new Dictionary<string, ClientModRule> { [Trade] = ClientModRule.Any };
        Assert.Empty(Judge(Server(Trade, "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor),
            Client(Trade, "1.0.1"), Ctx(rules: anyRule, clientRanges: null)));
    }

    [Fact]
    public void An_older_client_is_held_to_a_strict_mode_too()
    {
        var refused = Judge(Server(Trade, "1.4.0", rule: NetworkVersionRule.None), Client(Trade, "1.4.1"), Ctx(VersionMatchingMode.Exact, clientRanges: null));
        Assert.Equal("example.trade: this server requires exactly 1.4.0 (you have 1.4.1)", Assert.Single(refused));
    }

    [Fact]
    public void This_client_answers_an_older_server_with_the_attribute_rule_bytes_it_always_sent()
    {
        // A mod that declares only a level (or nothing) still puts Exact on its row, so an older server's walk is unchanged.
        var levelOnly = CompatDeclaration.From(typeof(LevelOnlyPlugin), "1.4.1")!;
        var row = Handshake.ModRecord.CreateDeclared("example.levelonly", "1.4.1", levelOnly.Level, levelOnly.Rule);
        Assert.Equal(NetworkVersionRule.Exact, row.VersionRule);
        var olderServer = new[] { Server("example.levelonly", "1.4.0") };
        var problems = Handshake.Compare.Evaluate(olderServer, new[] { row }, out _);
        Assert.Equal("example.levelonly: expected 1.4.0, got 1.4.1", Assert.Single(problems));

        // The answer's capability row carries VersionRanges; an older reader keeps the bits it knows and ignores the rest.
        var cap = Handshake.Compare.CreateCapabilityRecord(Handshake.SelfCapabilities);
        var read = Handshake.Compare.ReadCapabilityBlob(cap.Version);
        Assert.Equal(Handshake.SelfCapabilities, read);
        Assert.NotEqual(Handshake.Capability.None, read & Handshake.Capability.ModFeatures);
    }

    [Fact]
    public void A_c3_feature_table_still_parses_for_a_c1_reader_and_carries_every_range()
    {
        var offers = new FeatureOfferBook();
        var table = FeatureTableBuilder.Build(
            new List<(string, string, string?)> { (Trade, "1.4.0", ">=1.2.0 <2.0.0"), (Cosmetic, "2.0.0", "2.0.x"), ("example.long", "1.0.0", new string('1', 70)) },
            offers, "2026.10.12", "ab", 5);
        byte[] body = FeatureTableWire.FullBody(table, out int kept);
        Assert.Equal(3, kept);
        Assert.True(FeatureTableWire.TryParse(body, out var parsed, out _, out var error), error);
        Assert.Equal(">=1.2.0 <2.0.0", parsed.Mods[Trade].Peers);
        Assert.Equal("2.0.x", parsed.Mods[Cosmetic].Peers);
        Assert.Null(parsed.Mods["example.long"].Peers);   // over 64 characters: left out, the default applies
    }

    [NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave)]
    private sealed class LevelOnlyPlugin { }
}
