using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C3 (Tie-in with auto-update): the pre-join prompt, the connected "not now" check and the kept-copy
/// choice reason from the server's remembered match and the declared ranges (<see cref="ServerVersionCheck"/>), and the
/// requirement memory remembers what that needs. Game-free.
/// </summary>
public sealed class PreJoinRangeTests
{
    private const string Mod = "team.chat";

    private static UpdateReport Update(string installed, string latest) => new(
        new[]
        {
            new UpdateItem
            {
                Package = "Team-Chat",
                Name = "Chat",
                Installed = installed,
                Latest = latest,
                PluginIds = new[] { Mod },
            },
        },
        Array.Empty<UpdateSkip>(), 1, new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc), "");

    private static PreJoinDecision Decide(UpdateReport report, ServerRequirement req, string? candidateRange = null) =>
        PreJoinOffers.Decide(report, new[] { req }, UpdateMode.Notify, promptOn: true, "s", null,
            (item, version) => candidateRange);

    private static ServerRequirement Req(string version, NetworkVersionRule rule = NetworkVersionRule.Exact, string match = "",
        string matching = "", string peers = "") =>
        new() { ModId = Mod, Version = version, Rule = rule, Match = match, Matching = matching, Peers = peers };

    [Fact]
    public void Exact_match_never_offers_an_update_past_the_servers_version_even_when_the_new_range_includes_it()
    {
        var d = Decide(Update("1.2.0", "1.3.0"), Req("1.2.0", match: "exact"), candidateRange: ">=1.0.0");
        Assert.Empty(d.Offers);
        Assert.Equal("this server requires exactly 1.2.0", d.Held.Single().Held);
        Assert.Contains("Not offered: Chat (this server requires exactly 1.2.0).", PreJoinOffers.Body(d));

        // Exact version for all, known from the request bits: the rewritten rule byte is the server's Exact.
        var bits = Decide(Update("1.2.0", "1.3.0"), Req("1.2.0", NetworkVersionRule.Exact, matching: "exact"), ">=1.0.0");
        Assert.Equal("this server requires exactly 1.2.0", bits.Held.Single().Held);

        // The server's own version is still offered.
        Assert.Single(Decide(Update("1.1.0", "1.2.0"), Req("1.2.0", match: "exact")).Offers);
    }

    [Fact]
    public void Minor_match_offers_only_the_same_minor()
    {
        Assert.Single(Decide(Update("1.2.0", "1.2.9"), Req("1.2.0", match: "minor")).Offers);
        var d = Decide(Update("1.2.0", "1.3.0"), Req("1.2.0", match: "minor"), candidateRange: "*");
        Assert.Equal("this server requires 1.2.x", d.Held.Single().Held);

        // Same minor for all from the request bits: the rule byte the server rewrote to Minor.
        Assert.Single(Decide(Update("1.2.0", "1.2.5"), Req("1.2.0", NetworkVersionRule.Minor, matching: "minor")).Offers);
    }

    [Fact]
    public void At_least_match_offers_the_servers_version_or_newer_and_any_offers_anything()
    {
        Assert.Single(Decide(Update("1.2.0", "2.0.0"), Req("1.2.0", match: "at-least"), candidateRange: "2.0.x").Offers);
        var older = Decide(Update("1.0.0", "1.1.0"), Req("1.2.0", match: "at-least"));
        Assert.Equal("this server requires 1.2.0 or newer", older.Held.Single().Held);

        Assert.Single(Decide(Update("1.0.0", "5.0.0"), Req("1.2.0", match: "any"), candidateRange: "=5.0.0").Offers);
        // A per-mod Any version under Exact for all comes as a None rule byte.
        Assert.Single(Decide(Update("1.0.0", "5.0.0"), Req("1.2.0", NetworkVersionRule.None, matching: "exact")).Offers);
    }

    [Fact]
    public void Under_declared_ranges_a_newer_version_whose_own_range_includes_the_server_is_offered_past_an_exact_attribute_rule()
    {
        // The server's attribute rule was Exact (the row byte), but the newer build vouches for the older server (once
        // the server's catalog facts for the mod are known: its peers).
        var d = Decide(Update("1.2.0", "1.4.0"), Req("1.2.0", NetworkVersionRule.Exact, peers: "1.2.x"), candidateRange: ">=1.2.0 <2.0.0");
        Assert.Equal("1.4.0", d.Offers.Single().Version);

        // Its range leaves the server out: held, saying whose range decided.
        var held = Decide(Update("1.2.0", "1.4.0"), Req("1.2.0", NetworkVersionRule.Exact, peers: "1.2.x"), candidateRange: "1.4.x");
        Assert.Equal("1.4.0 works with any 1.4.x version, not this server's 1.2.0", held.Held.Single().Held);
    }

    [Fact]
    public void Under_declared_ranges_an_older_version_needs_the_servers_range()
    {
        var ok = Decide(Update("1.0.0", "1.1.0"), Req("1.3.0", NetworkVersionRule.Exact, peers: ">=1.1.0 <2.0.0"));
        Assert.Single(ok.Offers);
        var held = Decide(Update("1.0.0", "1.1.0"), Req("1.3.0", NetworkVersionRule.Exact, peers: "1.3.x"));
        Assert.Equal("this server's 1.3.0 works with any 1.3.x version", held.Held.Single().Held);
    }

    [Fact]
    public void Before_inspection_the_prompt_falls_back_to_todays_version_cap()
    {
        // No range for the new version and no server range: the remembered rule caps as in phase 1.
        var d = Decide(Update("1.2.0", "1.3.0"), Req("1.2.0", NetworkVersionRule.Exact));
        Assert.Equal("this server needs exactly 1.2.0", d.Held.Single().Held);
        var minor = Decide(Update("1.2.0", "1.3.0"), Req("1.2.0", NetworkVersionRule.Minor));
        Assert.Equal("this server needs 1.2.x", minor.Held.Single().Held);
        Assert.Single(Decide(Update("1.2.0", "1.2.4"), Req("1.2.0", NetworkVersionRule.Minor)).Offers);
        Assert.Single(Decide(Update("1.2.0", "9.0.0"), Req("1.2.0", NetworkVersionRule.None)).Offers);
    }

    [Fact]
    public void The_connected_check_uses_the_same_evaluation_with_its_own_subject()
    {
        var item = Update("1.2.0", "1.3.0").Items[0];
        Assert.Equal("the server you are on requires exactly 1.2.0",
            PreJoinOffers.BlockedBy(item, new[] { Req("1.2.0", match: "exact") }, (i, v) => "*"));
        Assert.Equal("", PreJoinOffers.BlockedBy(item, new[] { Req("1.2.0", peers: "1.2.x") }, (i, v) => ">=1.2.0"));
        Assert.Equal("the server you are on needs exactly 1.2.0", PreJoinOffers.BlockedBy(item, new[] { Req("1.2.0") }));
    }

    [Fact]
    public void The_memory_keeps_peers_match_and_matching_and_older_files_still_load()
    {
        var memory = new ServerRequirementMemory();
        var rows = new[] { Handshake.ModRecord.CreateDeclared(Mod, "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) };
        memory.Note("k", ServerRequirements.Of(rows, VersionMatchingMode.Exact), DateTime.UtcNow);
        var catalog = new[]
        {
            new ManifestMod(Mod, "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, false) { Peers = "1.2.x", Match = "exact" },
            new ManifestMod("other.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, false),
        };
        Assert.True(memory.NoteCatalog("k", ServerRequirements.FromCatalog(catalog)));
        Assert.False(memory.NoteCatalog("unknown", ServerRequirements.FromCatalog(catalog)));

        var back = ServerRequirementMemory.Parse(memory.ToJson()).Find("k")!.Single();
        Assert.Equal("1.2.x", back.Peers);
        Assert.Equal("exact", back.Match);
        Assert.Equal("exact", back.Matching);

        // The next request (before this session's catalog) keeps the facts while the version is unchanged...
        memory.Note("k", ServerRequirements.Of(rows), DateTime.UtcNow);
        Assert.Equal("1.2.x", memory.Find("k")!.Single().Peers);
        // ...and drops them for another server version.
        memory.Note("k", ServerRequirements.Of(new[] { Handshake.ModRecord.CreateDeclared(Mod, "1.3.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) }), DateTime.UtcNow);
        Assert.Equal("", memory.Find("k")!.Single().Peers);

        // A file written before C3 loads as before.
        const string old = "{\"schema\":1,\"servers\":{\"steamipv4://1.2.3.4:9876\":{\"seen\":\"2026-10-01T00:00:00Z\",\"mods\":[{\"id\":\"team.chat\",\"version\":\"1.2.0\",\"rule\":\"minor\"}]}}}";
        var legacy = ServerRequirementMemory.Parse(old).Find("steamipv4://1.2.3.4:9876")!.Single();
        Assert.Equal(NetworkVersionRule.Minor, legacy.Rule);
        Assert.Equal("", legacy.Peers);
        Assert.Equal("", legacy.Match);
        Assert.Equal("", legacy.Matching);
    }

    // ---- the kept-copy choice ------------------------------------------------------------------------

    private static Handshake.ModRecord Rec(string version, NetworkVersionRule rule = NetworkVersionRule.Exact) =>
        Handshake.ModRecord.CreateDeclared(Mod, version, NetworkCompatibilityLevel.ServerMustHave, rule);

    private static ModVersionEntry Entry(params string[] versions)
    {
        var e = new ModVersionEntry { Id = Mod };
        foreach (var v in versions)
            e.Parked.Add(new ParkedVersion { Version = v, Dir = v, File = "a.dll", SourceDetail = "Team-Chat@" + v });
        return e;
    }

    [Fact]
    public void A_kept_copy_is_chosen_by_the_same_evaluation()
    {
        var asked = new[] { Rec("1.2.0") };
        var provided = new[] { Rec("1.4.0") };

        // Without range data: today's check (the Exact row refuses 1.4.0; the kept 1.2.0 is picked).
        Assert.Equal("1.2.0", ModVersionSelect.PickFor(Mod, asked, provided, Entry("1.2.0"))!.Version);

        // The active 1.4.0 declares it works with 1.2.0, and the server's catalog facts are known: nothing is swapped.
        Func<string, ServerRequirement?> known = id => new ServerRequirement { ModId = Mod, Version = "1.2.0", Peers = "1.2.x" };
        var vouches = new KeptCopyRanges { Server = known, CopyRange = (id, v, src) => v == "1.4.0" ? ">=1.2.0 <2.0.0" : null };
        Assert.Null(ModVersionSelect.PickFor(Mod, asked, provided, Entry("1.2.0"), vouches));

        // Exact version for all: the server's copy is picked although 1.4.0's range includes 1.2.0.
        var exact = new KeptCopyRanges { Mode = VersionMatchingMode.Exact, Server = known, CopyRange = vouches.CopyRange };
        Assert.Equal("1.2.0", ModVersionSelect.PickFor(Mod, asked, provided, Entry("1.3.0", "1.2.0"), exact)!.Version);

        // A kept newer copy whose own range includes the server is acceptable (the preferred order picks it first).
        var newer = new KeptCopyRanges { Server = known, CopyRange = (id, v, src) => src == "Team-Chat@1.3.0" ? ">=1.2.0" : v == "1.4.0" ? "1.4.x" : null };
        Assert.Equal("1.3.0", ModVersionSelect.PickFor(Mod, asked, provided, Entry("1.3.0", "1.2.0"), newer)!.Version);

        // The server's remembered match (at least) for its version accepts the active copy.
        var atLeast = new KeptCopyRanges { Server = id => new ServerRequirement { ModId = Mod, Version = "1.2.0", Match = "at-least" } };
        Assert.Null(ModVersionSelect.PickFor(Mod, asked, provided, Entry("1.2.0"), atLeast));
        // ...but not when the remembered facts belong to another server version.
        var stale = new KeptCopyRanges { Server = id => new ServerRequirement { ModId = Mod, Version = "1.1.0", Match = "any" } };
        Assert.Equal("1.2.0", ModVersionSelect.PickFor(Mod, asked, provided, Entry("1.2.0"), stale)!.Version);
        // ...nor when the request now judges the mod another way (an admin set a rule since: the row byte changed).
        var changed = new KeptCopyRanges { Server = id => new ServerRequirement { ModId = Mod, Version = "1.2.0", Rule = NetworkVersionRule.None, Match = "any" } };
        Assert.Equal("1.2.0", ModVersionSelect.PickFor(Mod, asked, provided, Entry("1.2.0"), changed)!.Version);
    }

    [Fact]
    public void Without_the_servers_catalog_facts_ranges_do_not_decide_and_the_request_rule_caps()
    {
        // A first join, an older server (judges by its rule bytes), or a per-mod rule written as the row's rule byte under
        // declared ranges: the client's own range cannot vouch, the request's rule is the evidence.
        Assert.Equal("this server needs exactly 1.2.0", ServerVersionCheck.Reason(Req("1.2.0", NetworkVersionRule.Exact), "1.4.0", ">=1.0.0"));
        Assert.Equal("", ServerVersionCheck.Reason(Req("1.2.0", NetworkVersionRule.Exact, peers: "1.2.x"), "1.4.0", ">=1.0.0"));

        // An exact match compares the strings, as the server does: build metadata makes another version.
        Assert.Equal("this server requires exactly 1.2.0+abc", ServerVersionCheck.Reason(Req("1.2.0+abc", match: "exact"), "1.2.0+def", null));
        Assert.Equal("", ServerVersionCheck.Reason(Req("1.2.0", match: "at-least"), "1.2", null));
    }
}
