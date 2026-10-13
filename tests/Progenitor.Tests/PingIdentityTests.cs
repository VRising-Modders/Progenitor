using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Content;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Pure half of ping's <c>gameIdentity</c> and <c>images</c> fields. Composition is Core's
/// <c>PrefabEditBuildIdentity.Compose</c> invoked from Dev (the type is internal; Dev has no
/// InternalsVisibleTo). Image shaping is the JSON row list ping serializes.
/// <c>ResolveIdentity</c> / <c>SelectActiveCore</c> are the production selection used by
/// <c>TryLiveIdentity</c>: an old retained Core cannot supply <c>gameIdentity</c>. Live
/// fact-reading and reload-table walks need a process with Core/Bootstrap and are not
/// exercised here.
/// </summary>
public class PingIdentityTests
{
    private const int Major = 1;
    private const int Minor = 1;
    private const int Patch = 13;
    private const int Revision = 0;
    private const int VcsRevision = 99712;
    private const int Build = 17;
    private const int NetworkVersion = 25;

    private static PingIdentity.GameVersionFacts ClientFacts(int network = NetworkVersion) =>
        new("VRising", Major, Minor, Patch, Revision, VcsRevision, Build, 1779722780L, network);

    private static PingIdentity.GameVersionFacts ServerFacts(int network = NetworkVersion) =>
        new("VRisingServer", Major, Minor, Patch, Revision, VcsRevision, Build, 1779728998L, network);

    private static PrefabGameVersionFacts CoreFacts(PingIdentity.GameVersionFacts facts) =>
        new(facts.Name, facts.Major, facts.Minor, facts.Patch, facts.Revision,
            facts.VcsRevision, facts.Build, facts.UnixTimestamp, facts.NetworkVersion);

    [Fact]
    public void Compose_matches_Core_and_is_the_prefab_edit_identity_string()
    {
        var facts = ClientFacts();
        string? ping = PingIdentity.Compose(facts);
        string core = PrefabEditBuildIdentity.Compose(CoreFacts(facts));

        Assert.Equal("vrising/1.1.13.0+r99712+b17/net25", ping);
        Assert.Equal(core, ping);
        Assert.Equal(ping, PingIdentity.Compose(ServerFacts()));
        Assert.DoesNotContain("Server", ping, StringComparison.Ordinal);
        Assert.DoesNotContain("1779722780", ping, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_changes_when_any_used_field_changes()
    {
        string? baseline = PingIdentity.Compose(ClientFacts());
        Assert.NotNull(baseline);

        Assert.NotEqual(baseline, PingIdentity.Compose(
            new PingIdentity.GameVersionFacts("VRising", 1, 1, 14, 0, VcsRevision, Build, 0, NetworkVersion)));
        Assert.NotEqual(baseline, PingIdentity.Compose(
            new PingIdentity.GameVersionFacts("VRising", 1, 1, 13, 1, VcsRevision, Build, 0, NetworkVersion)));
        Assert.NotEqual(baseline, PingIdentity.Compose(
            new PingIdentity.GameVersionFacts("VRising", 1, 1, 13, 0, 99713, Build, 0, NetworkVersion)));
        Assert.NotEqual(baseline, PingIdentity.Compose(
            new PingIdentity.GameVersionFacts("VRising", 1, 1, 13, 0, VcsRevision, 18, 0, NetworkVersion)));
        Assert.NotEqual(baseline, PingIdentity.Compose(ClientFacts(NetworkVersion + 1)));
    }

    [Fact]
    public void Compose_returns_null_when_Core_would_answer_unknown()
    {
        var none = new PingIdentity.GameVersionFacts("", 0, 0, 0, 0, 0, 0, 0, 0);
        var negative = new PingIdentity.GameVersionFacts("VRising", 1, 1, 13, 0, -1, 17, 0, 25);

        Assert.Equal(PrefabEditBuildIdentity.Unknown, PrefabEditBuildIdentity.Compose(CoreFacts(none)));
        Assert.Equal(PrefabEditBuildIdentity.Unknown, PrefabEditBuildIdentity.Compose(CoreFacts(negative)));
        Assert.Null(PingIdentity.Compose(none));
        Assert.Null(PingIdentity.Compose(negative));
    }

    [Fact]
    public void ShapeImages_writes_generation_only_on_Core_and_nulls_unknown_hashes()
    {
        var rows = new PingIdentity.Image[]
        {
            new("Core", "abc123def4567890", generation: 3, version: "2026.10.3+abc1234"),
            new("Progenitor.Dev", "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
            new("Progenitor.Sample", sha256: null),
            new("rewritten", ""),
            new("", "deadbeef"),
        };

        var shaped = PingIdentity.ShapeImages(rows);
        Assert.Equal(4, shaped.Count);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(shaped));
        var arr = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(4, arr.GetArrayLength());

        var core = arr[0];
        Assert.Equal("Core", core.GetProperty("name").GetString());
        Assert.Equal("abc123def4567890", core.GetProperty("sha256").GetString());
        Assert.Equal(3, core.GetProperty("generation").GetInt32());
        Assert.Equal("2026.10.3+abc1234", core.GetProperty("version").GetString());

        var dev = arr[1];
        Assert.Equal("Progenitor.Dev", dev.GetProperty("name").GetString());
        Assert.Equal(64, dev.GetProperty("sha256").GetString()!.Length);
        Assert.False(dev.TryGetProperty("generation", out _));

        var sample = arr[2];
        Assert.Equal("Progenitor.Sample", sample.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, sample.GetProperty("sha256").ValueKind);
        Assert.False(sample.TryGetProperty("generation", out _));
        Assert.Equal(JsonValueKind.Null, sample.GetProperty("version").ValueKind);

        var rewritten = arr[3];
        Assert.Equal("rewritten", rewritten.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, rewritten.GetProperty("sha256").ValueKind);
    }

    [Fact]
    public void InformationalVersion_reads_the_build_stamp_and_tolerates_null()
    {
        Assert.Null(PingIdentity.InformationalVersion(null));
        string? v = PingIdentity.InformationalVersion(typeof(PingIdentity).Assembly);
        Assert.False(string.IsNullOrEmpty(v));
        Assert.Matches(@"^\d+\.\d+\.\d+", v);
    }

    [Fact]
    public void ShapeImages_empty_input_is_an_empty_array_not_null()
    {
        Assert.Empty(PingIdentity.ShapeImages(Array.Empty<PingIdentity.Image>()));
        Assert.Empty(PingIdentity.ShapeImages(new List<PingIdentity.Image>()));
    }

    [Fact]
    public void Live_readers_do_not_throw_without_Core_or_Bootstrap()
    {
        Assert.Null(PingIdentity.TryLiveIdentity());
        Assert.Null(PingIdentity.TryLiveImages());
    }

    [Fact]
    public void ResolveIdentity_returns_only_the_active_generation_when_an_old_one_is_retained()
    {
        var candidates = new PingIdentity.CoreCandidate[]
        {
            new("progenitor-core:1", "vrising/old-generation"),
            new("progenitor-core:2", "vrising/new-generation"),
        };

        Assert.Equal(
            "vrising/new-generation",
            PingIdentity.ResolveIdentity(candidates, "progenitor-core:2"));
        Assert.Equal(
            "vrising/old-generation",
            PingIdentity.ResolveIdentity(candidates, "progenitor-core:1"));
        Assert.Equal(
            "vrising/new-generation",
            PingIdentity.SelectActiveCore(candidates, "progenitor-core:2")!.Value.Identity);
    }

    [Fact]
    public void ResolveIdentity_returns_null_when_the_active_generation_is_unknown()
    {
        var candidates = new PingIdentity.CoreCandidate[]
        {
            new("progenitor-core:1", "vrising/old-generation"),
            new("progenitor-core:2", "vrising/new-generation"),
        };

        Assert.Null(PingIdentity.ResolveIdentity(candidates, activeAlcName: null));
        Assert.Null(PingIdentity.ResolveIdentity(candidates, ""));
        Assert.Null(PingIdentity.SelectActiveCore(candidates, activeAlcName: null));
    }

    [Fact]
    public void ResolveIdentity_returns_the_single_active_generation()
    {
        var candidates = new PingIdentity.CoreCandidate[]
        {
            new("progenitor-core:4", "vrising/1.1.13.0+r99712+b17/net25"),
        };

        Assert.Equal(
            "vrising/1.1.13.0+r99712+b17/net25",
            PingIdentity.ResolveIdentity(candidates, "progenitor-core:4"));
    }

    [Fact]
    public void ResolveIdentity_does_not_use_an_old_generation_when_the_active_one_is_absent_or_unknown()
    {
        var oldOnly = new PingIdentity.CoreCandidate[]
        {
            new("progenitor-core:1", "vrising/old-generation"),
        };

        Assert.Null(PingIdentity.ResolveIdentity(oldOnly, "progenitor-core:2"));
        Assert.Null(PingIdentity.ResolveIdentity(
            new[] { new PingIdentity.CoreCandidate("progenitor-core:2", "") },
            "progenitor-core:2"));
        Assert.Null(PingIdentity.ResolveIdentity(
            new[] { new PingIdentity.CoreCandidate("progenitor-core:2", identity: null) },
            "progenitor-core:2"));
        Assert.Null(PingIdentity.ResolveIdentity(
            new PingIdentity.CoreCandidate[]
            {
                new("progenitor-core:2", "vrising/from-bound"),
                new("progenitor-core:2", "vrising/from-loader"),
            },
            "progenitor-core:2"));
        Assert.Equal(
            "vrising/same",
            PingIdentity.ResolveIdentity(
                new PingIdentity.CoreCandidate[]
                {
                    new("progenitor-core:2", "vrising/same"),
                    new("progenitor-core:2", "vrising/same"),
                },
                "progenitor-core:2"));
    }

    [Fact]
    public void Sha256Hex_is_lowercase_64_and_stable()
    {
        byte[] bytes = { 1, 2, 3, 4 };
        string hex = PingIdentity.Sha256Hex(bytes);
        Assert.Equal(64, hex.Length);
        Assert.Equal(hex, PingIdentity.Sha256Hex(bytes));
        Assert.Matches("^[0-9a-f]{64}$", hex);
        Assert.NotEqual(hex, PingIdentity.Sha256Hex(new byte[] { 1, 2, 3, 5 }));
    }
}
