using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.CompatTest;
using Progenitor.Networking;
using Progenitor.Testing;
using Xunit;

namespace Progenitor.SampleTests.CompatTest;

/// <summary>
/// The 1.0.0 build's <c>hello</c> (fields 1 and 2), frozen here: the test project links
/// <c>samples/Progenitor.CompatTest/CompatTestMessages.cs</c> with <c>COMPATTEST_V2</c> defined, so the linked
/// <see cref="Hello"/> is the 1.1.0 build's.
/// </summary>
[ModMessage("hello")]
public sealed class HelloV1
{
    [Field(1)] public int Seq;
    [Field(2)] public string Text = "";
}

/// <summary>A breaking <c>hello</c> (a build that raised <c>MinReaderVersion</c>): what a 1.x range must not admit.</summary>
[ModMessage("hello", Version = 3, MinReaderVersion = 3)]
public sealed class HelloBreaking
{
    [Field(1)] public int Seq;
    [Field(4)] public long Stamp;
}

/// <summary>
/// The C1/C2 live-test plugins' messages against fixtures of the builds the live test of 2026-10-09 ran: the default
/// build at 1.0.0 (<c>hello</c> v1), the <c>-p:CompatTestV2=true</c> build at 1.1.0 (<c>hello</c> v2, field 3
/// <c>Note</c>) and the <c>Other</c> plugin at 1.0.0 (<c>ping</c>). Each reader reads the other version's fixtures, as
/// the live test showed each side newer in turn.
/// </summary>
public sealed class CompatTestReplayTests
{
    private const string Sample = "Progenitor.CompatTest";
    private const string Mod = "progenitor.compattest";
    private const string OtherMod = "progenitor.compattest.other";
    private const string NoNote = "(no note: the sender predates field 3)";

    /// <summary>
    /// The range the README documents for the two builds under the compatibility contract (the plugin itself declares
    /// <c>NetworkVersionRule.None</c>, any version; the Range variant declares one but sends no message).
    /// </summary>
    internal const string Peers = ">=1.0.0 <2.0.0";

    /// <summary>What each build sends: the client's hello, the server's echo and greeting (Plugin.cs, <c>NewHello</c>).</summary>
    private static readonly SampleMessage[] Sent =
    {
        new("1.0.0", new HelloV1 { Seq = 1, Text = "from client 1.0.0" }, "to-server"),
        new("1.0.0", new HelloV1 { Seq = 1001, Text = "echo from server 1.0.0" }, "to-client"),
        new("1.0.0", new HelloV1 { Seq = 500, Text = "greeting from server 1.0.0" }, "to-client"),
        new("1.1.0", new Hello { Seq = 1, Text = "from client 1.1.0", Note = "note from 1.1.0" }, "to-server"),
        new("1.1.0", new Hello { Seq = 1001, Text = "echo from server 1.1.0", Note = "note from 1.1.0" }, "to-client"),
        new("1.1.0", new Hello { Seq = 500, Text = "greeting from server 1.1.0", Note = "note from 1.1.0" }, "to-client"),
    };

    private static readonly SampleMessage[] OtherSent =
    {
        new("1.0.0", new Ping { N = 1 }, "to-client"),
    };

    private static MessageFixtures Fixtures() => SampleFixtures.For(Sample, Mod, "1.1.0", Sent, subfolder: Mod);

    private static MessageFixtures OtherFixtures() => SampleFixtures.For(Sample, OtherMod, "1.0.0", OtherSent, subfolder: OtherMod);

    [Fact]
    public void The_fixtures_are_what_the_live_test_builds_send()
    {
        var fixtures = Fixtures();
        Assert.Equal(new[] { "1.0.0", "1.1.0" }, fixtures.ModVersions.ToArray());
        Assert.All(fixtures.For<Hello>().Where(f => f.ModVersion == "1.0.0"), f => Assert.Equal(1, f.Version));
        Assert.All(fixtures.For<Hello>().Where(f => f.ModVersion == "1.1.0"), f => Assert.Equal(2, f.Version));
        Assert.Equal(new[] { "1.0.0" }, OtherFixtures().ModVersions.ToArray());
    }

    /// <summary>A 1.0.0 side reads what 1.1.0 sends: field 3 is skipped, fields 1 and 2 arrive.</summary>
    [Fact]
    public void The_1_0_0_reader_reads_1_1_0_and_skips_field_3()
    {
        var seen = new List<string>();
        var r = MessageCompat.Replay<HelloV1>(Fixtures(), (m, f) => seen.Add(f.ModVersion + ":" + m.Seq + ":" + m.Text)).ThrowIfFailed();
        Assert.Contains("1.1.0:1:from client 1.1.0", seen);
        Assert.Contains("1.1.0:1001:echo from server 1.1.0", seen);
        Assert.Contains(r.Notes, n => n.Contains("field(s) 3 unknown to HelloV1", StringComparison.Ordinal));
        Assert.Equal(new[] { "1.0.0", "1.1.0" }, r.Versions.ToArray());
    }

    /// <summary>A 1.1.0 side reads what 1.0.0 sends: <c>Note</c> keeps its initializer, as the live log showed.</summary>
    [Fact]
    public void The_1_1_0_reader_reads_1_0_0_and_keeps_the_notes_default()
    {
        var notes = new Dictionary<string, HashSet<string>>();
        MessageCompat.Replay<Hello>(Fixtures(), (m, f) =>
        {
            if (!notes.TryGetValue(f.ModVersion, out var set)) notes[f.ModVersion] = set = new HashSet<string>();
            set.Add(m.Note);
        }).ThrowIfFailed();
        Assert.Equal(new[] { NoNote }, notes["1.0.0"].ToArray());
        Assert.Equal(new[] { "note from 1.1.0" }, notes["1.1.0"].ToArray());
    }

    [Fact]
    public void The_other_plugins_ping_replays()
    {
        MessageCompat.Replay<Ping>(OtherFixtures(), (m, f) => Assert.Equal(1, m.N)).ThrowIfFailed();
    }

    [Fact]
    public void Every_message_skips_fields_a_newer_version_adds()
    {
        MessageCompat.AssertSkipsUnknownFields(new Hello { Seq = 3, Text = "t", Note = "n" });
        MessageCompat.AssertSkipsUnknownFields(new HelloV1 { Seq = -4, Text = "t" }, seed: 2);
        MessageCompat.AssertSkipsUnknownFields(new Ping { N = 9 }, seed: 3);
    }

    /// <summary>
    /// The README's range holds for both builds' types: every recorded version inside it reads and stays readable.
    /// A breaking <c>hello</c> (a raised <c>MinReaderVersion</c>) fails it, which is why such a build is 2.0.0.
    /// </summary>
    [Fact]
    public void The_documented_range_holds_and_a_breaking_message_breaks_it()
    {
        var fixtures = Fixtures();
        var types = MessageTypes.In(typeof(Hello).Assembly, typeof(Hello).Namespace);
        Assert.Equal(new[] { typeof(Hello), typeof(Ping) }, types.ToArray());
        var today = MessageCompat.CheckRange(Peers, "1.1.0", fixtures, types).ThrowIfFailed();
        Assert.Equal(new[] { "1.0.0", "1.1.0" }, today.Versions.ToArray());
        Assert.Equal(new[] { "1.0.0" }, today.OtherVersions.ToArray());
        Assert.Equal(new[] { "1.1.0" }, MessageCompat.CheckRange(Peers, "1.0.0", fixtures, new[] { typeof(HelloV1) }).ThrowIfFailed().OtherVersions.ToArray());
        // The other plugin has one recorded version only: allowed as its first release, and said to be unproven.
        var other = MessageCompat.CheckRange(Peers, "1.0.0", OtherFixtures(), types, allowSelfOnly: true).ThrowIfFailed();
        Assert.Contains(other.Notes, n => n.Contains("not proven", StringComparison.Ordinal));
        Assert.False(MessageCompat.CheckRange(Peers, "1.0.0", OtherFixtures(), types).Ok);

        var breaking = MessageCompat.CheckRange(Peers, "2.0.0", fixtures, new[] { typeof(HelloBreaking) });
        Assert.False(breaking.Ok);
        Assert.Contains(breaking.Failures, f => f.Contains("needs reader 3", StringComparison.Ordinal));

        string readme = SampleFixtures.RepoFile("samples/Progenitor.CompatTest/README.md");
        Assert.Contains("`" + Peers + "`", readme, StringComparison.Ordinal);
    }
}
