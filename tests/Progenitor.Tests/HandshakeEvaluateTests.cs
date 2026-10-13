using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the two-list network-compatibility evaluation and the Minor version
/// parser. <see cref="Handshake.Compare.FindProblems"/> stays the original required-list walk;
/// this is the function the live handshake calls for declared rows and for the reverse direction.
/// Same collection as <see cref="HandshakeAnswerWatchTests"/>: both mutate Handshake tables
/// and the re-arm flag, and xunit otherwise runs classes in parallel.
/// </summary>
[Collection("Handshake")]
public class HandshakeEvaluateTests : IDisposable
{
    public HandshakeEvaluateTests() => Handshake.ResetTables();

    public void Dispose() => Handshake.ResetTables();

    [Fact]
    public void Empty_lists_are_ok()
    {
        Assert.Empty(Handshake.Compare.Evaluate(Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>()));
        Assert.Empty(Handshake.Compare.Evaluate(null, null));
    }

    [Fact]
    public void Undeclared_server_row_requires_exact_client_version()
    {
        var server = new[] { U("my.mod", "1.0") };
        var client = new[] { U("my.mod", "1.0") };
        Assert.Empty(Eval(server, client));
    }

    [Fact]
    public void Undeclared_server_row_missing_on_client_is_expected_got_missing()
    {
        var problems = Eval(new[] { U("my.mod", "1.0") }, Array.Empty<Handshake.ModRecord>());
        Assert.Equal(new[] { "my.mod: the server needs it (1.0), but your game doesn't have it. Install it from the join screens" }, problems);
    }

    [Fact]
    public void Undeclared_server_row_empty_client_version_is_missing()
    {
        var problems = Eval(new[] { U("my.mod", "1.0") }, new[] { U("my.mod", "") });
        Assert.Equal(new[] { "my.mod: the server needs it (1.0), but your game doesn't have it. Install it from the join screens" }, problems);
    }

    [Fact]
    public void Undeclared_server_row_version_mismatch_names_expected_and_got()
    {
        var problems = Eval(new[] { U("my.mod", "1.0") }, new[] { U("my.mod", "0.9") });
        Assert.Equal(new[] { "my.mod: expected 1.0, got 0.9" }, problems);
    }

    [Fact]
    public void Undeclared_client_extras_are_ignored()
    {
        var server = new[] { U("keep", "1.0") };
        var client = new[] { U("keep", "1.0"), U("bonus.mod", "9.9") };
        Assert.Empty(Eval(server, client));
    }

    [Fact]
    public void Matching_versions_are_case_insensitive()
    {
        var server = new[] { U("My.Mod", "1.0-Beta") };
        var client = new[] { U("my.mod", "1.0-beta") };
        Assert.Empty(Eval(server, client));
    }

    [Fact]
    public void ServerMustHave_Exact_matches_like_undeclared_but_is_declared()
    {
        var server = new[] { D("my.mod", "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) };
        var client = new[] { D("my.mod", "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) };
        Assert.Empty(Eval(server, client));
    }

    [Fact]
    public void ServerMustHave_Exact_rejects_1_0_versus_1_0_0()
    {
        var server = new[] { D("my.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) };
        var client = new[] { D("my.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) };
        Assert.Equal(new[] { "my.mod: expected 1.0, got 1.0.0" }, Eval(server, client));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.9")]
    [InlineData("1.2.3", "1.2.0")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2.0", "1.2")]
    [InlineData("1.2.3+build", "1.2.1")]
    [InlineData("v1.2.3", "1.2.0")]
    [InlineData("V1.2.0", "1.2.99")]
    [InlineData("01.02.03", "1.2.3")]
    [InlineData("10.20.30", "10.20.1")]
    [InlineData("0.1.0", "0.1.9")]
    [InlineData("1.0-Beta", "1.0.0-Beta")]
    [InlineData("1.0-Beta", "1.0.5-beta")]
    [InlineData("1.2.3-rc.1", "1.2.99-rc.1")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.2.3.4", "1.2.9.1")]
    [InlineData("1.2.3.4", "1.2.3")]
    [InlineData("1.2.3", "1.2.3.4")]
    public void ServerMustHave_Minor_accepts_same_major_minor(string serverVer, string clientVer)
    {
        var server = new[] { D("my.mod", serverVer, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor) };
        var client = new[] { D("my.mod", clientVer, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor) };
        Assert.Empty(Eval(server, client));
    }

    [Theory]
    [InlineData("1.2.3", "1.3.0")]
    [InlineData("1.2.3", "2.2.3")]
    [InlineData("1.2.3", "1.1.9")]
    [InlineData("0.1.0", "0.2.0")]
    [InlineData("2.0.0", "1.9.9")]
    [InlineData("1.2.3", "10.2.3")]
    [InlineData("1.0-Beta", "1.0.5")]
    [InlineData("1.2.3-rc.1", "1.2.99")]
    [InlineData("1.0.5", "1.0-Beta")]
    [InlineData("1.0-Beta", "1.0-RC")]
    [InlineData("1", "1.0.0")]
    [InlineData("1.0.0", "1")]
    public void ServerMustHave_Minor_rejects_different_major_or_minor(string serverVer, string clientVer)
    {
        var server = new[] { D("my.mod", serverVer, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor) };
        var client = new[] { D("my.mod", clientVer, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor) };
        var problems = Eval(server, client);
        Assert.Single(problems);
        Assert.Equal($"my.mod: expected {serverVer} (minor), got {clientVer}", problems[0]);
    }

    [Fact]
    public void ServerMustHave_Minor_unparseable_peer_version_is_a_mismatch()
    {
        var server = new[] { D("my.mod", "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor) };
        var client = new[] { D("my.mod", "not-a-version", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor) };
        Assert.Equal(new[] { "my.mod: expected 1.2.0 (minor), got not-a-version" }, Eval(server, client));
    }

    [Fact]
    public void ServerMustHave_Minor_unparseable_own_version_is_a_mismatch()
    {
        var server = new[] { D("my.mod", "??", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor) };
        var client = new[] { D("my.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor) };
        Assert.Equal(new[] { "my.mod: expected ?? (minor), got 1.0.0" }, Eval(server, client));
    }

    [Fact]
    public void ServerMustHave_Minor_still_requires_presence()
    {
        var server = new[] { D("my.mod", "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor) };
        Assert.Equal(new[] { "my.mod: the server needs it (1.2.0 (minor)), but your game doesn't have it. Install it from the join screens" }, Eval(server, Array.Empty<Handshake.ModRecord>()));
    }

    [Fact]
    public void ServerMustHave_None_accepts_any_version_but_requires_presence()
    {
        var server = new[] { D("my.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.None) };
        Assert.Empty(Eval(server, new[] { D("my.mod", "9.9.9", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.None) }));
        Assert.Equal(new[] { "my.mod: the server needs it (1.0 (any)), but your game doesn't have it. Install it from the join screens" }, Eval(server, Array.Empty<Handshake.ModRecord>()));
    }

    [Fact]
    public void NoNeedForSync_Exact_ok_when_client_lacks_it()
    {
        var server = new[] { D("opt.mod", "1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact) };
        Assert.Empty(Eval(server, Array.Empty<Handshake.ModRecord>()));
    }

    [Fact]
    public void NoNeedForSync_Exact_ok_when_both_match()
    {
        var server = new[] { D("opt.mod", "1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact) };
        var client = new[] { D("opt.mod", "1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact) };
        Assert.Empty(Eval(server, client));
    }

    [Fact]
    public void NoNeedForSync_Exact_fails_when_both_present_and_differ()
    {
        var server = new[] { D("opt.mod", "1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact) };
        var client = new[] { D("opt.mod", "2.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact) };
        Assert.Equal(new[] { "opt.mod: expected 1.0, got 2.0" }, Eval(server, client));
    }

    [Fact]
    public void NoNeedForSync_Minor_accepts_same_minor_when_both_present()
    {
        var server = new[] { D("opt.mod", "1.2.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor) };
        var client = new[] { D("opt.mod", "1.2.8", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor) };
        Assert.Empty(Eval(server, client));
    }

    [Fact]
    public void NoNeedForSync_Minor_rejects_different_minor_when_both_present()
    {
        var server = new[] { D("opt.mod", "1.2.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor) };
        var client = new[] { D("opt.mod", "1.3.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor) };
        Assert.Equal(new[] { "opt.mod: expected 1.2.0 (minor), got 1.3.0" }, Eval(server, client));
    }

    [Fact]
    public void NoNeedForSync_None_is_ok_missing_or_present_at_any_version()
    {
        var server = new[] { D("opt.mod", "1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None) };
        Assert.Empty(Eval(server, Array.Empty<Handshake.ModRecord>()));
        Assert.Empty(Eval(server, new[] { D("opt.mod", "99.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None) }));
    }

    [Fact]
    public void ClientOnly_on_server_is_ignored_even_if_client_lacks_it()
    {
        var server = new[] { D("ui.mod", "1.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Exact) };
        Assert.Empty(Eval(server, Array.Empty<Handshake.ModRecord>()));
    }

    [Fact]
    public void ClientOnly_on_server_ignores_version_when_client_has_a_different_one()
    {
        var server = new[] { D("ui.mod", "1.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Exact) };
        var client = new[] { D("ui.mod", "9.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Exact) };
        Assert.Empty(Eval(server, client));
    }

    [Fact]
    public void Vice_versa_client_ServerMustHave_missing_on_server_is_a_kick()
    {
        var client = new[] { D("client.mod", "1.4.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) };
        Assert.Equal(
            new[] { "client.mod: your game needs it on the server (1.4.0), but the server doesn't have it. Turn it off, or ask the admins to install it" },
            Eval(Array.Empty<Handshake.ModRecord>(), client));
    }

    [Fact]
    public void Vice_versa_client_ServerMustHave_present_on_server_is_ok()
    {
        var server = new[] { U("client.mod", "1.4.0") };
        var client = new[] { D("client.mod", "1.4.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) };
        Assert.Empty(Eval(server, client));
    }

    [Fact]
    public void Vice_versa_client_NoNeedForSync_missing_on_server_is_ok()
    {
        var client = new[] { D("opt.mod", "1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact) };
        Assert.Empty(Eval(Array.Empty<Handshake.ModRecord>(), client));
    }

    [Fact]
    public void Vice_versa_client_ClientOnly_missing_on_server_is_ok()
    {
        var client = new[] { D("ui.mod", "1.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Exact) };
        Assert.Empty(Eval(Array.Empty<Handshake.ModRecord>(), client));
    }

    [Fact]
    public void Vice_versa_does_not_fire_for_undeclared_client_extras()
    {
        var client = new[] { U("sneaky.mod", "1.0") };
        Assert.Empty(Eval(Array.Empty<Handshake.ModRecord>(), client));
    }

    [Fact]
    public void One_reason_per_failing_guid_when_both_sides_would_report()
    {
        var server = new[] { D("my.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) };
        var client = new[] { D("my.mod", "2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) };
        var problems = Eval(server, client);
        Assert.Equal(new[] { "my.mod: expected 1.0, got 2.0" }, problems);
    }

    [Fact]
    public void Client_rule_still_applies_when_server_level_did_not_fail()
    {
        var server = new[] { D("my.mod", "1.2.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None) };
        var client = new[] { D("my.mod", "1.3.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) };
        Assert.Equal(new[] { "my.mod: expected 1.3.0, got 1.2.0" }, Eval(server, client));
    }

    [Fact]
    public void Multiple_failures_one_string_each()
    {
        var server = new[]
        {
            D("a.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
            D("b.mod", "2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        var client = new[]
        {
            D("a.mod", "1.1", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
            D("c.mod", "3.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        var problems = Eval(server, client);
        Assert.Equal(3, problems.Count);
        Assert.Contains("a.mod: expected 1.0, got 1.1", problems);
        Assert.Contains("b.mod: the server needs it (2.0), but your game doesn't have it. Install it from the join screens", problems);
        Assert.Contains("c.mod: your game needs it on the server (3.0), but the server doesn't have it. Turn it off, or ask the admins to install it", problems);
    }

    [Fact]
    public void Duplicate_guids_in_a_list_last_wins()
    {
        var server = new[] { U("my.mod", "1.0"), U("my.mod", "2.0") };
        var client = new[] { U("my.mod", "2.0") };
        Assert.Empty(Eval(server, client));
    }

    [Fact]
    public void Empty_guid_rows_are_skipped()
    {
        var server = new[] { U("", "1.0"), U("ok.mod", "1.0") };
        var client = new[] { U("ok.mod", "1.0") };
        Assert.Empty(Eval(server, client));
    }

    [Fact]
    public void Unknown_level_is_a_readable_failure()
    {
        var server = new[]
        {
            new Handshake.ModRecord("x.mod", "1.0", (NetworkCompatibilityLevel)9, NetworkVersionRule.Exact, declared: true),
        };
        var problems = Eval(server, new[] { D("x.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) });
        Assert.Equal(new[] { "x.mod: expected 1.0, got unknown compatibility level 9" }, problems);
    }

    [Fact]
    public void Unknown_version_rule_is_a_readable_failure()
    {
        var server = new[]
        {
            new Handshake.ModRecord("x.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, (NetworkVersionRule)9, declared: true),
        };
        var client = new[] { D("x.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) };
        var problems = Eval(server, client);
        Assert.Equal(new[] { "x.mod: expected 1.0, got unknown version rule 9" }, problems);
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("1.2.3-beta", 1, 2, 3)]
    [InlineData("1.0-Beta", 1, 0, 0)]
    [InlineData("1.2.3+build", 1, 2, 3)]
    [InlineData("01.02.03", 1, 2, 3)]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("1.2.3.4", 1, 2, 3)]
    [InlineData("1.2.3.4.5", 1, 2, 3)]
    [InlineData("1.2.3.4-beta", 1, 2, 3)]
    public void TryParseVersion_reads_major_minor_patch(string value, int major, int minor, int patch)
    {
        Assert.True(Handshake.Compare.TryParseVersion(value, out int maj, out int min, out int pat));
        Assert.Equal(major, maj);
        Assert.Equal(minor, min);
        Assert.Equal(patch, pat);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("beta")]
    [InlineData("v")]
    [InlineData("1.")]
    [InlineData(".")]
    [InlineData("x1.2.3")]
    [InlineData("1")]
    [InlineData("1.2.3foo")]
    [InlineData("1.2.3-")]
    public void TryParseVersion_rejects_unparseable(string? value)
    {
        Assert.False(Handshake.Compare.TryParseVersion(value!, out _, out _, out _));
    }

    [Fact]
    public void TryParseVersion_rejects_overflow()
    {
        Assert.False(Handshake.Compare.TryParseVersion("99999999999.0.0", out _, out _, out _));
    }

    [Fact]
    public void VersionsMatch_Exact_is_case_insensitive_string_equality()
    {
        Assert.True(Handshake.Compare.VersionsMatch("1.0-Beta", "1.0-beta", NetworkVersionRule.Exact));
        Assert.False(Handshake.Compare.VersionsMatch("1.0", "1.0.0", NetworkVersionRule.Exact));
    }

    [Fact]
    public void VersionsMatch_None_is_always_true()
    {
        Assert.True(Handshake.Compare.VersionsMatch("1.0", "nope", NetworkVersionRule.None));
        Assert.True(Handshake.Compare.VersionsMatch("", "", NetworkVersionRule.None));
    }

    [Fact]
    public void VersionsMatch_Minor_uses_SameMinor()
    {
        Assert.True(Handshake.Compare.VersionsMatch("1.2.3", "1.2.9", NetworkVersionRule.Minor));
        Assert.False(Handshake.Compare.VersionsMatch("1.2.3", "1.3.0", NetworkVersionRule.Minor));
        Assert.False(Handshake.Compare.VersionsMatch("??", "1.0", NetworkVersionRule.Minor));
        Assert.True(Handshake.Compare.VersionsMatch("1.2.3.4", "1.2.3.4", NetworkVersionRule.Minor));
        Assert.True(Handshake.Compare.VersionsMatch("??", "??", NetworkVersionRule.Minor));
        Assert.True(Handshake.Compare.SameMinor("Not-A-Version", "not-a-version"));
    }

    [Fact]
    public void PackMeta_undeclared_is_zeros_so_a_v1_slot_stays_v1()
    {
        var slot = new byte[24];
        slot[0] = (byte)'1';
        Handshake.Compare.PackMeta(slot, declared: false, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);
        Assert.Equal(0, slot[Handshake.MetaFlagOffset]);
        Assert.Equal(0, slot[Handshake.MetaRuleOffset]);
        Handshake.Compare.UnpackMeta(slot, out bool declared, out var level, out var rule);
        Assert.False(declared);
        Assert.Equal(NetworkCompatibilityLevel.ServerMustHave, level);
        Assert.Equal(NetworkVersionRule.Exact, rule);
    }

    [Theory]
    [InlineData(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)]
    [InlineData(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor)]
    [InlineData(NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None)]
    [InlineData(NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Exact)]
    public void PackMeta_round_trips_declared_level_and_rule(NetworkCompatibilityLevel level, NetworkVersionRule rule)
    {
        var slot = new byte[24];
        for (int i = 0; i < 5; i++) slot[i] = (byte)"1.2.0"[i];
        Handshake.Compare.PackMeta(slot, declared: true, level, rule);
        Assert.Equal(0, slot[Handshake.MetaFlagOffset - 1]);
        Assert.Equal(Handshake.MetaPresentFlag, slot[Handshake.MetaFlagOffset] & Handshake.MetaPresentFlag);
        Handshake.Compare.UnpackMeta(slot, out bool declared, out var gotLevel, out var gotRule);
        Assert.True(declared);
        Assert.Equal(level, gotLevel);
        Assert.Equal(rule, gotRule);
    }

    [Fact]
    public void PackMeta_declared_ServerMustHave_is_distinguishable_from_undeclared()
    {
        var declared = new byte[24];
        var undeclared = new byte[24];
        Handshake.Compare.PackMeta(declared, true, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);
        Handshake.Compare.PackMeta(undeclared, false, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);
        Assert.NotEqual(declared[Handshake.MetaFlagOffset], undeclared[Handshake.MetaFlagOffset]);
        Handshake.Compare.UnpackMeta(declared, out bool d1, out _, out _);
        Handshake.Compare.UnpackMeta(undeclared, out bool d2, out _, out _);
        Assert.True(d1);
        Assert.False(d2);
    }

    [Fact]
    public void ProtocolVersion_is_2_and_packet_layout_constants_are_stable()
    {
        Assert.Equal(2, Handshake.ProtocolVersion);
        Assert.Equal(16, Handshake.MaxMods);
        Assert.Equal(63, Handshake.MaxGuidChars);
        Assert.Equal(23, Handshake.MaxVersionChars);
        Assert.Equal(21, Handshake.InSlotVersionChars);
        Assert.Equal(0x80, Handshake.MetaPresentFlag);
        Assert.Equal(22, Handshake.MetaFlagOffset);
        Assert.Equal(23, Handshake.MetaRuleOffset);
        Assert.Equal(479, Handshake.ResultMessageMaxChars);
        Assert.Equal("...", Handshake.ReasonEllipsis);
        Assert.Equal("progenitor.hs.meta", Handshake.MetaRecordGuid);
        Assert.Equal("progenitor.hs.overflow", Handshake.OverflowGuid);
    }

    [Fact]
    public void TryParseVersion_exposes_prerelease_and_ignores_build_metadata()
    {
        Assert.True(Handshake.Compare.TryParseVersion("1.2.3-rc.1+githash", out _, out _, out _, out var pre));
        Assert.Equal("rc.1", pre);
        Assert.True(Handshake.Compare.TryParseVersion("1.2.3+build", out _, out _, out _, out var none));
        Assert.Equal("", none);
    }

    [Fact]
    public void PackMeta_does_not_truncate_a_23_character_version()
    {
        var slot = new byte[24];
        for (int i = 0; i < 23; i++) slot[i] = (byte)'v';
        Handshake.Compare.PackMeta(slot, declared: true, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor);
        for (int i = 0; i < 23; i++)
            Assert.Equal((byte)'v', slot[i]);
        Handshake.Compare.UnpackMeta(slot, out bool declared, out _, out _);
        Assert.False(declared);
    }

    [Fact]
    public void Dedicated_meta_record_round_trips_a_23_character_declared_version()
    {
        var version = new string('1', Handshake.MaxVersionChars);
        var original = new[]
        {
            D("long.mod", version, NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor),
        };
        var withMeta = new List<Handshake.ModRecord> { original[0], Handshake.Compare.CreateMetaRecord(original) };
        var restored = Handshake.Compare.ApplyMetaRecord(withMeta);
        Assert.Single(restored);
        Assert.Equal("long.mod", restored[0].Guid);
        Assert.Equal(version, restored[0].Version);
        Assert.True(restored[0].Declared);
        Assert.Equal(NetworkCompatibilityLevel.NoNeedForSync, restored[0].Level);
        Assert.Equal(NetworkVersionRule.Minor, restored[0].VersionRule);
    }

    [Fact]
    public void Evaluate_applies_dedicated_meta_so_long_versions_keep_their_rule()
    {
        var version = new string('a', 23);
        var serverRows = new[]
        {
            D("long.mod", version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        var clientWire = new List<Handshake.ModRecord>
        {
            new Handshake.ModRecord("long.mod", version),
            Handshake.Compare.CreateMetaRecord(serverRows),
        };
        Assert.Empty(Handshake.Compare.Evaluate(serverRows, clientWire));
    }

    [Fact]
    public void PackRequest_of_an_empty_list_is_empty_not_skipped()
    {
        var packed = Handshake.Compare.PackRequest(Array.Empty<Handshake.ModRecord>());
        Assert.Empty(packed.Rows);
        Assert.False(packed.DeclaredDropped);
    }

    [Fact]
    public void PackRequest_skips_ClientOnly_so_it_does_not_consume_a_slot()
    {
        var required = new List<Handshake.ModRecord>
        {
            D("ui.mod", "1.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Exact),
            D("need.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        var packed = Handshake.Compare.PackRequest(required);
        Assert.Contains(packed.Rows, r => r.Guid == "need.mod");
        Assert.DoesNotContain(packed.Rows, r => r.Guid == "ui.mod");
    }

    [Fact]
    public void PackRequest_ClientOnly_does_not_block_sixteen_declared_rows()
    {
        var required = new List<Handshake.ModRecord>();
        for (int i = 0; i < Handshake.MaxMods; i++)
            required.Add(D($"need.{i}", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact));
        required.Add(D("ui.mod", "1.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Exact));
        var packed = Handshake.Compare.PackRequest(required);
        Assert.Equal(Handshake.MaxMods, packed.Rows.Count);
        Assert.DoesNotContain(packed.Rows, r => r.Guid == "ui.mod");
        Assert.Empty(packed.Dropped);
        Assert.False(packed.DeclaredDropped);
    }

    [Fact]
    public void PackReply_empty_ask_still_sends_declared_client_extras()
    {
        var provided = new[]
        {
            D("client.mod", "1.4.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        var packed = Handshake.Compare.PackReply(Array.Empty<Handshake.ModRecord>(), provided);
        Assert.Contains(packed.Rows, r => r.Guid == "client.mod");
        Assert.False(packed.DeclaredDropped);
    }

    [Fact]
    public void PackReply_prefers_declared_ServerMustHave_extras_and_fail_closes_when_one_drops()
    {
        var asked = new List<Handshake.ModRecord>();
        for (int i = 0; i < Handshake.MaxMods; i++)
            asked.Add(U($"asked.{i}", "1.0"));
        var provided = new List<Handshake.ModRecord>(asked)
        {
            D("extra.must", "2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
            D("extra.opt", "2.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact),
        };
        var packed = Handshake.Compare.PackReply(asked, provided);
        Assert.True(packed.DeclaredDropped);
        Assert.Contains(packed.Dropped, r => r.Guid == "extra.must");
        Assert.Contains(packed.Dropped, r => r.Guid == "extra.opt");
        Assert.Contains(packed.Rows, r => r.Guid == Handshake.OverflowGuid);
        Assert.Contains("extra.must", Handshake.Compare.CapExceededReason(packed.Dropped));
        Assert.Equal(1, Handshake.Compare.CountDeclaredServerMustHave(packed.Dropped));
        // Asked answers stay on the wire except the one slot the overflow marker must occupy.
        for (int i = 0; i < Handshake.MaxMods - 1; i++)
            Assert.Contains(packed.Rows, r => r.Guid == $"asked.{i}");
    }

    [Fact]
    public void PackReply_dropped_NoNeedForSync_extra_does_not_fail_closed_or_overwrite_asked()
    {
        var asked = new List<Handshake.ModRecord>();
        for (int i = 0; i < Handshake.MaxMods; i++)
            asked.Add(U($"asked.{i}", "1.0"));
        var provided = new List<Handshake.ModRecord>(asked)
        {
            D("extra.opt", "2.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact),
        };
        var packed = Handshake.Compare.PackReply(asked, provided);
        Assert.False(packed.DeclaredDropped);
        Assert.Contains(packed.Dropped, r => r.Guid == "extra.opt");
        Assert.DoesNotContain(packed.Rows, r => r.Guid == Handshake.OverflowGuid);
        for (int i = 0; i < Handshake.MaxMods; i++)
            Assert.Contains(packed.Rows, r => r.Guid == $"asked.{i}");
    }

    [Fact]
    public void PackReply_sixteen_long_declared_ServerMustHave_fail_closes_when_meta_cannot_fit()
    {
        var version = new string('v', Handshake.MaxVersionChars);
        var provided = new List<Handshake.ModRecord>();
        for (int i = 0; i < Handshake.MaxMods; i++)
            provided.Add(D($"long.{i}", version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact));
        var packed = Handshake.Compare.PackReply(Array.Empty<Handshake.ModRecord>(), provided);
        Assert.True(packed.DeclaredDropped);
        Assert.Contains(packed.Rows, r => r.Guid == Handshake.OverflowGuid);
    }

    [Fact]
    public void PackReply_skips_ClientOnly_extras()
    {
        var packed = Handshake.Compare.PackReply(
            Array.Empty<Handshake.ModRecord>(),
            new[] { D("ui.mod", "1.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Exact) });
        Assert.DoesNotContain(packed.Rows, r => r.Guid == "ui.mod");
    }

    [Fact]
    public void Empty_server_with_client_declared_ServerMustHave_is_still_a_kick()
    {
        var packed = Handshake.Compare.PackReply(
            Array.Empty<Handshake.ModRecord>(),
            new[] { D("client.mod", "1.4.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) });
        var problems = Handshake.Compare.Evaluate(Array.Empty<Handshake.ModRecord>(), packed.Rows);
        Assert.Equal(new[] { "client.mod: your game needs it on the server (1.4.0), but the server doesn't have it. Turn it off, or ask the admins to install it" }, problems);
    }

    [Theory]
    [InlineData("short", "short")]
    [InlineData("exactly-479-would-be-padded-in-the-test-body", "exactly-479-would-be-padded-in-the-test-body")]
    public void TruncateReason_keeps_a_short_string(string input, string expected)
    {
        Assert.Equal(expected, Handshake.Compare.TruncateReason(input));
    }

    [Fact]
    public void TruncateReason_cuts_to_the_wire_limit_with_an_ellipsis()
    {
        var full = new string('x', Handshake.ResultMessageMaxChars + 50);
        var wire = Handshake.Compare.TruncateReason(full);
        Assert.Equal(Handshake.ResultMessageMaxChars, wire.Length);
        Assert.EndsWith(Handshake.ReasonEllipsis, wire);
        Assert.Equal(new string('x', Handshake.ResultMessageMaxChars - Handshake.ReasonEllipsis.Length) + Handshake.ReasonEllipsis, wire);
        Assert.Equal("abc", Handshake.Compare.TruncateReason("abcdef", 3));
        Assert.Equal("ab...", Handshake.Compare.TruncateReason("abcdef", 5));
    }

    [Fact]
    public void Require_prefers_declared_rows_and_refuses_a_17th_without_banning_joins()
    {
        Handshake.ResetTables();
        try
        {
            for (int i = 0; i < Handshake.MaxMods; i++)
                Handshake.Require($"u.{i}", "1.0");
            Handshake.Require("keep.me", "2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);
            var afterEvict = Handshake.CopyRequired();
            Assert.Contains(afterEvict, r => r.Guid == "keep.me" && r.Declared);
            Assert.Equal(Handshake.MaxMods, afterEvict.Length);
            Assert.Empty(Handshake.CopyDeclaredCapDropped());

            Handshake.ResetTables();
            for (int i = 0; i < Handshake.MaxMods; i++)
                Handshake.Require($"d.{i}", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);
            Handshake.Require("also.declared", "3.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);
            Assert.Contains(Handshake.CopyDeclaredCapDropped(), g => g == "also.declared");
            Assert.DoesNotContain(Handshake.CopyRequired(), r => r.Guid == "also.declared");

            var packed = Handshake.Compare.PackRequest(Handshake.CopyRequired());
            Assert.False(packed.DeclaredDropped);
            Assert.DoesNotContain(packed.Rows, r => r.Guid == Handshake.OverflowGuid);
            Assert.Empty(Handshake.Compare.Evaluate(Handshake.CopyRequired(), Handshake.CopyRequired()));
        }
        finally
        {
            Handshake.ResetTables();
        }
    }

    [Fact]
    public void Unrequire_retries_a_previously_refused_declared_row()
    {
        Handshake.ResetTables();
        try
        {
            for (int i = 0; i < Handshake.MaxMods; i++)
                Handshake.Require($"d.{i}", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);
            Handshake.Require("also.declared", "3.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);
            Assert.Contains(Handshake.CopyDeclaredCapDropped(), g => g == "also.declared");

            Handshake.Unrequire("d.0");
            Assert.Contains(Handshake.CopyRequired(), r => r.Guid == "also.declared");
            Assert.DoesNotContain(Handshake.CopyDeclaredCapDropped(), g => g == "also.declared");
            Assert.DoesNotContain(Handshake.CopyRequired(), r => r.Guid == "d.0");
        }
        finally
        {
            Handshake.ResetTables();
        }
    }

    [Fact]
    public void Require_ClientOnly_does_not_consume_a_slot()
    {
        Handshake.ResetTables();
        try
        {
            Handshake.Require("ui.mod", "1.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Exact);
            for (int i = 0; i < Handshake.MaxMods; i++)
                Handshake.Require($"need.{i}", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);
            var required = Handshake.CopyRequired();
            Assert.Equal(Handshake.MaxMods + 1, required.Length);
            Assert.Contains(required, r => r.Guid == "ui.mod");
            var packed = Handshake.Compare.PackRequest(required);
            Assert.DoesNotContain(packed.Rows, r => r.Guid == "ui.mod");
            Assert.Equal(Handshake.MaxMods, Handshake.Compare.MaterializeWire(packed.Rows).Count);
        }
        finally
        {
            Handshake.ResetTables();
        }
    }

    private static List<string> Eval(IReadOnlyList<Handshake.ModRecord> server, IReadOnlyList<Handshake.ModRecord> client) =>
        Handshake.Compare.Evaluate(server, client);

    private static Handshake.ModRecord U(string guid, string version) => Handshake.ModRecord.Undeclared(guid, version);

    private static Handshake.ModRecord D(string guid, string version, NetworkCompatibilityLevel level, NetworkVersionRule rule) =>
        Handshake.ModRecord.CreateDeclared(guid, version, level, rule);
}
