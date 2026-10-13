using System;
using System.Reflection;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C3 (docs/mod-compatibility.md, section 2): the range syntax, version order, the words players read,
/// the effective range of a declaration (declared, else the explicit rule, else the same minor), and reading the
/// declaration off a plugin type.
/// </summary>
public class CompatRangeTests
{
    private static CompatRange R(string text) => CompatRange.Parse(text);

    [Theory]
    [InlineData(">=1.2.0 <2.0.0", "1.2.0", true)]
    [InlineData(">=1.2.0 <2.0.0", "1.9.9", true)]
    [InlineData(">=1.2.0 <2.0.0", "2.0.0", false)]
    [InlineData(">=1.2.0 <2.0.0", "1.1.9", false)]
    [InlineData(">1.2.0", "1.2.0", false)]
    [InlineData(">1.2.0", "1.2.1", true)]
    [InlineData("<=1.2", "1.2.0", true)]
    [InlineData("<=1.2", "1.2.1", false)]
    [InlineData("=1.2.0", "1.2", true)]
    [InlineData("1.2.0", "1.2.0", true)]
    [InlineData("1.2.0", "1.2.1", false)]
    [InlineData("1.4.x", "1.4.0", true)]
    [InlineData("1.4.x", "1.4.17", true)]
    [InlineData("1.4.x", "1.5.0", false)]
    [InlineData("1.4.x", "1.3.9", false)]
    [InlineData("1.x", "1.9.0", true)]
    [InlineData("1.x", "2.0.0", false)]
    [InlineData("*", "anything-at-all", true)]
    [InlineData("1.2.x || 1.4.x", "1.4.2", true)]
    [InlineData("1.2.x || 1.4.x", "1.3.0", false)]
    [InlineData(">= 1.2.0 < 2.0.0", "1.5.0", true)]
    [InlineData(">=v1.2.0", "1.2.0", true)]
    [InlineData(">=1.2.0", "1.2.0+build.7", true)]
    public void Includes_follows_the_comparators(string range, string version, bool expected) =>
        Assert.Equal(expected, R(range).Includes(version));

    [Fact]
    public void A_prerelease_sorts_below_its_release()
    {
        Assert.True(CompatVersion.Compare("1.2.0-beta", "1.2.0") < 0);
        Assert.True(CompatVersion.Compare("1.2.0-beta.2", "1.2.0-beta.10") < 0);
        Assert.True(CompatVersion.Compare("1.2.0-1", "1.2.0-alpha") < 0);
        Assert.Equal(0, CompatVersion.Compare("1.2", "1.2.0"));
        Assert.Equal(0, CompatVersion.Compare("v1.2.0", "1.2.0+abc"));
        Assert.True(CompatVersion.Compare("1.10.0", "1.9.0") > 0);
        Assert.Null(CompatVersion.Compare("abc", "1.0.0"));

        // A same-minor range takes in its own prereleases and keeps out the next minor's.
        Assert.True(R("1.2.x").Includes("1.2.0-beta"));
        Assert.False(R("1.2.x").Includes("1.3.0-beta"));
        Assert.False(R(">=1.2.0").Includes("1.2.0-beta"));
        Assert.True(R("<2.0.0").Includes("2.0.0-rc.1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(">=")]
    [InlineData(">=abc")]
    [InlineData("1.2.0 ||")]
    [InlineData("|| 1.2.0")]
    [InlineData(">=1.x")]
    [InlineData("* >=1.0.0")]
    [InlineData("1.2.3.x")]
    [InlineData("=1.2.0é")]
    public void Invalid_ranges_are_refused_with_a_reason(string text)
    {
        Assert.False(CompatRange.TryParse(text, out var range, out var error));
        Assert.Null(range);
        Assert.False(string.IsNullOrEmpty(error));
        var e = Assert.Throws<ArgumentException>(() => CompatRange.Parse(text));
        Assert.Contains("is not a valid compatibility range", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_range_is_at_most_64_characters()
    {
        string ok = ">=1.2.0 <2.0.0 || >=3.0.0 <4.0.0 || >=5.0.0 <6.0.0 || 7.x";
        Assert.True(ok.Length <= CompatRange.MaxChars);
        Assert.True(CompatRange.IsValid(ok));
        string tooLong = ok + " || 8.x || 9.x";
        Assert.True(tooLong.Length > CompatRange.MaxChars);
        Assert.False(CompatRange.TryParse(tooLong, out _, out var error));
        Assert.Contains("64", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(">=1.2.0 <2.0.0", "1.2.0 or newer, below 2.0.0")]
    [InlineData("=1.2.0", "exactly 1.2.0")]
    [InlineData("1.2.0", "exactly 1.2.0")]
    [InlineData("1.4.x", "any 1.4.x version")]
    [InlineData("1.x", "any 1.x version")]
    [InlineData("*", "any version")]
    [InlineData(">1.0 <=1.5", "newer than 1.0, 1.5 or older")]
    [InlineData("1.2.x || 1.4.x", "any 1.2.x version or any 1.4.x version")]
    public void Describe_reads_as_a_sentence_part(string range, string words) =>
        Assert.Equal(words, R(range).Describe());

    [Fact]
    public void Same_minor_and_exact_helpers()
    {
        Assert.Equal("1.4.x", CompatRange.SameMinorAs("1.4.3")!.Text);
        Assert.Equal("1.4.x", CompatRange.SameMinorAs("v1.4.3-beta")!.Text);
        Assert.Equal("=custom", CompatRange.SameMinorAs("custom")!.Text);
        Assert.Null(CompatRange.SameMinorAs(""));
        Assert.Equal("=1.2.0", CompatRange.Exactly("1.2.0")!.Text);
        Assert.Null(CompatRange.Exactly("has space"));
        Assert.True(CompatRange.Any.IsAny);
        Assert.Equal("1.4", CompatVersion.MinorOf("1.4.3"));
        Assert.Equal("2.0", CompatVersion.MinorOf("2"));
    }

    [Fact]
    public void The_effective_range_is_declared_then_the_explicit_rule_then_the_same_minor()
    {
        // Declared wins over everything.
        Assert.Equal(">=1.2.0 <2.0.0", CompatRanges.EffectiveText("1.4.0", ">=1.2.0 <2.0.0", ruleDeclared: true, NetworkVersionRule.Exact));
        // An explicit rule.
        Assert.Equal("=1.4.0", CompatRanges.EffectiveText("1.4.0", null, ruleDeclared: true, NetworkVersionRule.Exact));
        Assert.Equal("1.4.x", CompatRanges.EffectiveText("1.4.0", null, ruleDeclared: true, NetworkVersionRule.Minor));
        Assert.Equal("*", CompatRanges.EffectiveText("1.4.0", null, ruleDeclared: true, NetworkVersionRule.None));
        // Nothing declared: the cautious default, the same minor (C3 decision 3; it was Exact before C3).
        Assert.Equal("1.4.x", CompatRanges.EffectiveText("1.4.0", null, ruleDeclared: false, NetworkVersionRule.Exact));
        Assert.Equal("1.4.x", CompatDeclaration.DefaultRange("1.4.0"));
        // An invalid declared range is ignored.
        Assert.Equal("=1.4.0", CompatRanges.EffectiveText("1.4.0", ">=nope", ruleDeclared: true, NetworkVersionRule.Exact));
        Assert.Equal("1.2.0 or newer, below 2.0.0", CompatRanges.Words(">=1.2.0 <2.0.0"));
        Assert.Equal("not a range", CompatRanges.Words("not a range"));
    }

    [NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, Peers = ">=1.2.0 <2.0.0")]
    private sealed class DeclaresRange { }

    [NetworkCompatibility(NetworkCompatibilityLevel.NoNeedForSync)]
    private sealed class DeclaresLevelOnly { }

    [NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)]
    private sealed class DeclaresExact { }

    [NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor, Peers = "not-a-range")]
    private sealed class DeclaresBadRange { }

    private sealed class DeclaresNothing { }

    [Fact]
    public void The_attribute_keeps_the_wire_rule_for_older_peers()
    {
        var levelOnly = typeof(DeclaresLevelOnly).GetCustomAttribute<NetworkCompatibilityAttribute>()!;
        Assert.False(levelOnly.VersionRuleDeclared);
        Assert.Equal(NetworkVersionRule.Exact, levelOnly.VersionRule);
        var exact = typeof(DeclaresExact).GetCustomAttribute<NetworkCompatibilityAttribute>()!;
        Assert.True(exact.VersionRuleDeclared);
        Assert.Null(exact.Peers);
    }

    [Fact]
    public void Declarations_are_read_by_attribute_name()
    {
        var range = CompatDeclaration.From(typeof(DeclaresRange), "1.4.0")!;
        Assert.Equal(">=1.2.0 <2.0.0", range.DeclaredPeers);
        Assert.Equal(">=1.2.0 <2.0.0", range.EffectiveRange);
        Assert.Equal(NetworkCompatibilityLevel.ServerMustHave, range.Level);
        Assert.False(range.RuleDeclared);

        var levelOnly = CompatDeclaration.From(typeof(DeclaresLevelOnly), "1.4.0")!;
        Assert.False(levelOnly.RuleDeclared);
        Assert.Equal("1.4.x", levelOnly.EffectiveRange);
        Assert.Equal(NetworkCompatibilityLevel.NoNeedForSync, levelOnly.Level);

        var exact = CompatDeclaration.From(typeof(DeclaresExact), "1.4.0")!;
        Assert.True(exact.RuleDeclared);
        Assert.Equal("=1.4.0", exact.EffectiveRange);

        var bad = CompatDeclaration.From(typeof(DeclaresBadRange), "1.4.0")!;
        Assert.True(bad.PeersInvalid);
        Assert.Equal("not-a-range", bad.InvalidPeers);
        Assert.Equal("", bad.DeclaredPeers);
        Assert.Equal("1.4.x", bad.EffectiveRange);

        Assert.Null(CompatDeclaration.From(typeof(DeclaresNothing), "1.4.0"));
        Assert.Null(CompatDeclaration.From(null, "1.4.0"));
    }
}
