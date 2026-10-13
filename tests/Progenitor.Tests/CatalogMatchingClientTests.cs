using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C3, the re-sign hookup on the client: a verified catalog's <c>matching</c> (the server's Version
/// matching when it signed) replaces what the join's request bits announced, while connected and in the remembered
/// requirements the next pre-join prompt reads. Game-free.
/// </summary>
[Collection("ClientUpdates")]
public sealed class CatalogMatchingClientTests : IDisposable
{
    private const string Mod = "team.chat";

    public CatalogMatchingClientTests() => ClientUpdates.Reset();

    public void Dispose() => ClientUpdates.Reset();

    private static Handshake.ModRecord[] Rows() => new[]
    {
        Handshake.ModRecord.CreateDeclared(Mod, "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
    };

    private static ManifestBody Body(string? matching, string? match = null) =>
        new(1, "Night Garden", "2026-10-09T12:00:00Z", new ManifestOperator("operator", "", ""), null, new[]
        {
            new ManifestMod(Mod, "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, false)
            {
                Peers = ">=1.0.0 <2.0.0",
                Match = match,
            },
        })
        { Matching = matching };

    [Theory]
    [InlineData("range", "")]
    [InlineData("minor", "minor")]
    [InlineData("exact", "exact")]
    [InlineData(null, null)]
    [InlineData("future-mode", null)]
    public void The_catalog_word_maps_to_the_remembered_matching(string? word, string? expected) =>
        Assert.Equal(expected, ServerRequirements.CatalogMatching(word));

    [Fact]
    public void A_re_signed_catalog_that_switched_to_ranges_lifts_the_exact_cap_while_connected()
    {
        // The join was under Exact for all (rule byte rewritten to Exact, the catalog's row said exact).
        ClientUpdates.NoteServerCatalog(Body("exact", match: "exact"));
        var exact = ClientUpdates.ConnectedRequirements(Rows()).Single();
        Assert.Equal("exact", ServerVersionCheck.EffectiveMatch(exact));
        Assert.Equal("the server you are on requires exactly 1.2.0",
            ServerVersionCheck.Reason(exact, "1.3.0", ">=1.0.0", ServerVersionCheck.ServerYouAreOn));

        // The admin switched to declared ranges: the re-signed catalog's row has no match and the body says range, so
        // the stale request bits no longer decide and the ranges do.
        ClientUpdates.NoteServerCatalog(Body("range"));
        var ranges = ClientUpdates.ConnectedRequirements(Rows()).Single();
        Assert.Equal("", ranges.Matching);
        Assert.Equal("", ServerVersionCheck.EffectiveMatch(ranges));
        Assert.Equal("", ServerVersionCheck.Reason(ranges, "1.3.0", ">=1.0.0", ServerVersionCheck.ServerYouAreOn));

        // An older server's catalog (no matching) leaves the request bits in force.
        ClientUpdates.NoteServerCatalog(Body(null));
        Assert.Equal(ServerRequirements.MatchingWord(ClientUpdates.ServerMatchingMode()),
            ClientUpdates.ConnectedRequirements(Rows()).Single().Matching);
    }

    [Fact]
    public void The_hook_and_the_memory_get_the_catalogs_matching_even_without_row_facts()
    {
        var seen = new List<(int Rows, string? Matching)>();
        ClientUpdates.ServerCatalog = (facts, matching) => seen.Add((facts.Count, matching));
        ClientUpdates.NoteServerCatalog(new ManifestBody(1, "s", "2026-10-09T12:00:00Z", new ManifestOperator("", "", ""), null,
            Array.Empty<ManifestMod>()) { Matching = "minor" });
        Assert.Equal((0, "minor"), seen.Single());

        // The memory: remembered under Exact from the request bits, then a re-signed catalog says range.
        var memory = new ServerRequirementMemory();
        memory.Note("k", ServerRequirements.Of(Rows(), VersionMatchingMode.Exact), DateTime.UtcNow);
        Assert.Equal("exact", memory.Find("k")!.Single().Matching);
        Assert.True(memory.NoteCatalog("k", Array.Empty<ServerRequirement>(), ""));
        var back = ServerRequirementMemory.Parse(memory.ToJson()).Find("k")!.Single();
        Assert.Equal("", back.Matching);
        Assert.False(memory.NoteCatalog("k", Array.Empty<ServerRequirement>(), ""));
        Assert.False(memory.NoteCatalog("k", null, null));
    }
}
