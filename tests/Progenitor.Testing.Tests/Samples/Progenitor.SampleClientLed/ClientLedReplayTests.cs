using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.SampleClientLed;
using Progenitor.Testing;
using Xunit;

namespace Progenitor.SampleTests.ClientLed;

/// <summary>
/// The client-led sample's messages (<c>samples/Progenitor.SampleClientLed/ClientLedMessages.cs</c>, linked as source)
/// against its fixtures: the 1.0.0 time request and answer, replayed through today's types with unknown fields added,
/// and its declared range <c>&gt;=1.0.0 &lt;2.0.0</c> checked against the recorded versions.
/// </summary>
public sealed class ClientLedReplayTests
{
    private const string Sample = "Progenitor.SampleClientLed";
    private const string Mod = "progenitor.sampleclientled";
    private const string Current = "1.0.0";

    /// <summary>The range <c>ClientLedDeclaration.Peers</c> declares (pinned against the source below).</summary>
    private const string Peers = ">=1.0.0 <2.0.0";

    /// <summary>What 1.0.0 sends: a request, and the answer to it (day 12, 14:30, a 30-minute day).</summary>
    private static readonly SampleMessage[] Sent =
    {
        new(Current, new TimeRequest { Sequence = 7 }, "to-server"),
        new(Current, new TimeAnswer
        {
            Sequence = 7, Day = 12, Hour = 14.5, DayLengthSeconds = 1800, SunriseHour = 6.5, SunsetHour = 20.25, UptimeMinutes = 95,
        }, "to-client"),
    };

    private static MessageFixtures Fixtures() => SampleFixtures.For(Sample, Mod, Current, Sent);

    [Fact]
    public void The_fixtures_are_what_1_0_0_sends()
    {
        var fixtures = Fixtures();
        Assert.Equal(new[] { Current }, fixtures.ModVersions.ToArray());
        Assert.All(fixtures.All, f => Assert.Equal("test", f.Source));
    }

    [Fact]
    public void The_recorded_request_and_answer_replay_with_every_value()
    {
        var fixtures = Fixtures();
        MessageCompat.Replay<TimeRequest>(fixtures, (m, f) => Assert.Equal(7, m.Sequence)).ThrowIfFailed();
        MessageCompat.Replay<TimeAnswer>(fixtures, (m, f) =>
        {
            Assert.Equal(7, m.Sequence);
            Assert.Equal(12, m.Day);
            Assert.Equal(14.5, m.Hour);
            Assert.Equal(1800, m.DayLengthSeconds);
            Assert.Equal(6.5, m.SunriseHour);
            Assert.Equal(20.25, m.SunsetHour);
            Assert.Equal(95, m.UptimeMinutes);
        }).ThrowIfFailed();
    }

    [Fact]
    public void Every_message_skips_fields_a_newer_version_adds()
    {
        MessageCompat.AssertSkipsUnknownFields(new TimeRequest { Sequence = int.MaxValue });
        MessageCompat.AssertSkipsUnknownFields(new TimeAnswer { Sequence = 1, Day = long.MaxValue, Hour = 23.99, DayLengthSeconds = 0.5 }, seed: 3);
    }

    [Fact]
    public void The_declared_range_is_backed_by_the_recorded_versions()
    {
        var types = MessageTypes.In(typeof(TimeRequest).Assembly, typeof(TimeRequest).Namespace);
        Assert.Equal(new[] { typeof(TimeAnswer), typeof(TimeRequest) }, types.ToArray());
        var fixtures = Fixtures();
        MessageCompat.ReplayAll(fixtures, types).ThrowIfFailed();
        // 1.0.0 is the first release: no older version exists to back the range yet, so it is allowed and said to be unproven.
        var range = MessageCompat.CheckRange(Peers, Current, fixtures, types, allowSelfOnly: true).ThrowIfFailed();
        Assert.Contains(range.Notes, n => n.Contains("not proven", StringComparison.Ordinal));
        Assert.False(MessageCompat.CheckRange(Peers, Current, fixtures, types).Ok);
        Assert.Equal(new[] { Current }, range.Versions.ToArray());

        string declaration = SampleFixtures.RepoFile("samples/Progenitor.SampleClientLed/ClientLedPolicy.cs");
        Assert.Contains("internal const string Peers = \"" + Peers + "\";", declaration, StringComparison.Ordinal);
        Assert.Contains("internal const string Version = \"" + Current + "\";", declaration, StringComparison.Ordinal);
    }
}
