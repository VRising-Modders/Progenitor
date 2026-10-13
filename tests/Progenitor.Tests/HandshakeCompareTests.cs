using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the handshake's required-vs-answered comparison and the Count clamp
/// that stops a malicious ModList.Count from walking off the 16-slot wire buffer.
/// </summary>
public class HandshakeCompareTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(16, 16)]
    [InlineData(-1, 0)]
    [InlineData(-100, 0)]
    [InlineData(17, 16)]
    [InlineData(1_000_000, 16)]
    [InlineData(int.MaxValue, 16)]
    [InlineData(int.MinValue, 0)]
    public void ClampCount_pins_to_0_through_MaxMods(int claimed, int expected)
    {
        Assert.Equal(expected, Handshake.Compare.ClampCount(claimed));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(16, false)]
    [InlineData(-1, true)]
    [InlineData(17, true)]
    [InlineData(1_000_000, true)]
    [InlineData(int.MaxValue, true)]
    [InlineData(int.MinValue, true)]
    public void IsMalformedCount_is_true_outside_0_MaxMods(int claimed, bool malformed)
    {
        Assert.Equal(malformed, Handshake.Compare.IsMalformedCount(claimed));
    }

    [Fact]
    public void Empty_answer_reports_every_required_mod_as_missing()
    {
        var required = Required(("my.mod", "1.0"), ("other.mod", "2.0"));
        var problems = Handshake.Compare.FindProblems(required, Array.Empty<(string, string)>(), claimedCount: 0);

        Assert.Equal(2, problems.Count);
        Assert.Contains("my.mod missing", problems);
        Assert.Contains("other.mod missing", problems);
    }

    [Fact]
    public void Answer_that_omits_a_required_guid_is_missing_not_ok()
    {
        var required = Required(("keep", "1.0"), ("drop", "1.0"));
        var answered = new[] { ("keep", "1.0") };

        var problems = Handshake.Compare.FindProblems(required, answered, claimedCount: 1);

        Assert.Equal(new[] { "drop missing" }, problems);
    }

    [Fact]
    public void Empty_version_in_the_answer_is_missing()
    {
        var required = Required(("my.mod", "1.0"));
        var answered = new[] { ("my.mod", "") };

        var problems = Handshake.Compare.FindProblems(required, answered, claimedCount: 1);

        Assert.Equal(new[] { "my.mod missing" }, problems);
    }

    [Fact]
    public void Version_mismatch_names_have_and_need()
    {
        var required = Required(("my.mod", "1.0"));
        var answered = new[] { ("my.mod", "0.9") };

        var problems = Handshake.Compare.FindProblems(required, answered, claimedCount: 1);

        Assert.Equal(new[] { "my.mod version 0.9, need 1.0" }, problems);
    }

    [Fact]
    public void Extra_answer_entries_are_ignored()
    {
        var required = Required(("my.mod", "1.0"));
        var answered = new[] { ("my.mod", "1.0"), ("bonus.mod", "9.9") };

        var problems = Handshake.Compare.FindProblems(required, answered, claimedCount: 2);

        Assert.Empty(problems);
    }

    [Fact]
    public void Matching_versions_are_case_insensitive()
    {
        var required = Required(("My.Mod", "1.0-Beta"));
        var answered = new[] { ("my.mod", "1.0-beta") };

        var problems = Handshake.Compare.FindProblems(required, answered, claimedCount: 1);

        Assert.Empty(problems);
    }

    [Fact]
    public void No_required_mods_is_ok_even_with_an_answer()
    {
        var answered = new[] { ("anything", "1.0") };
        var problems = Handshake.Compare.FindProblems(
            new Dictionary<string, string>(), answered, claimedCount: 1);
        Assert.Empty(problems);
    }

    [Fact]
    public void ClaimedCount_above_MaxMods_only_reads_the_first_16_answer_slots()
    {
        var required = Required(("need.me", "1.0"));
        var answered = new List<(string guid, string version)>(20);
        for (int i = 0; i < 16; i++)
            answered.Add(($"extra.{i}", "1.0"));
        answered.Add(("need.me", "1.0"));

        var problems = Handshake.Compare.FindProblems(required, answered, claimedCount: 1_000_000);

        Assert.Equal(16, Handshake.Compare.ClampCount(1_000_000));
        Assert.True(Handshake.Compare.IsMalformedCount(1_000_000));
        Assert.Equal(new[] { "need.me missing" }, problems);
    }

    [Fact]
    public void Negative_claimedCount_clamps_to_empty_and_every_required_mod_is_missing()
    {
        var required = Required(("my.mod", "1.0"));
        var answered = new[] { ("my.mod", "1.0") };

        var problems = Handshake.Compare.FindProblems(required, answered, claimedCount: -5);

        Assert.Equal(0, Handshake.Compare.ClampCount(-5));
        Assert.Equal(new[] { "my.mod missing" }, problems);
    }

    [Fact]
    public void ClaimedCount_shorter_than_the_list_ignores_the_tail()
    {
        var required = Required(("tail.mod", "1.0"));
        var answered = new[] { ("head.mod", "1.0"), ("tail.mod", "1.0") };

        var problems = Handshake.Compare.FindProblems(required, answered, claimedCount: 1);

        Assert.Equal(new[] { "tail.mod missing" }, problems);
    }

    [Theory]
    [InlineData("ok.mod", "1.0")]
    [InlineData("a", "v")]
    public void TryValidateEntry_accepts_in_range_ascii(string guid, string version)
    {
        Assert.True(Handshake.Compare.TryValidateEntry(guid, version, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void TryValidateEntry_accepts_max_length_guid_and_version()
    {
        var guid = new string('g', Handshake.MaxGuidChars);
        var version = new string('1', Handshake.MaxVersionChars);
        Assert.True(Handshake.Compare.TryValidateEntry(guid, version, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null, "1.0", "guid")]
    [InlineData("", "1.0", "guid")]
    [InlineData("ok.mod", null, "version")]
    [InlineData("ok.mod", "", "version")]
    public void TryValidateEntry_rejects_empty(string? guid, string? version, string which)
    {
        Assert.False(Handshake.Compare.TryValidateEntry(guid!, version!, out var error));
        Assert.Contains(which, error);
    }

    [Fact]
    public void TryValidateEntry_rejects_guid_of_64_chars()
    {
        var guid = new string('a', Handshake.MaxGuidChars + 1);
        Assert.False(Handshake.Compare.TryValidateEntry(guid, "1.0", out var error));
        Assert.Contains("exceeds", error);
    }

    [Fact]
    public void TryValidateEntry_rejects_version_of_24_chars()
    {
        var version = new string('1', Handshake.MaxVersionChars + 1);
        Assert.False(Handshake.Compare.TryValidateEntry("ok.mod", version, out var error));
        Assert.Contains("exceeds", error);
    }

    [Fact]
    public void TryValidateEntry_rejects_non_ascii()
    {
        Assert.False(Handshake.Compare.TryValidateEntry("mod.é", "1.0", out var error));
        Assert.Contains("ASCII", error);
    }

    private static Dictionary<string, string> Required(params (string guid, string version)[] entries)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (guid, version) in entries)
            d[guid] = version;
        return d;
    }
}
