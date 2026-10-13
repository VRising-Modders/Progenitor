using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.SampleServerLed;
using Progenitor.Testing;
using Xunit;

namespace Progenitor.SampleTests.ServerLed;

/// <summary>
/// The server-led sample's messages (<c>samples/Progenitor.SampleServerLed/ServerLedMessages.cs</c>, linked as source)
/// against its fixtures: what 1.0.0 sends, replayed through today's types and validation, with unknown fields
/// added, and its declared range <c>&gt;=1.0.0 &lt;2.0.0</c> checked against the recorded versions.
/// </summary>
public sealed class ServerLedReplayTests
{
    private const string Sample = "Progenitor.SampleServerLed";
    private const string Mod = "progenitor.sampleserverled";
    private const string Current = "1.0.0";

    /// <summary>The range <c>ServerLedDeclaration.Peers</c> declares (pinned against the source below).</summary>
    private const string Peers = ">=1.0.0 <2.0.0";

    /// <summary>What 1.0.0 sends: preferences (both deliveries, announcements off), the goodbye and one announcement.</summary>
    private static readonly SampleMessage[] Sent =
    {
        new(Current, new PrefsMessage { Kind = ServerLedMessages.KindPrefs, Receive = true, Delivery = ServerLedMessages.WirePopup }, "to-server"),
        new(Current, new PrefsMessage { Kind = ServerLedMessages.KindPrefs, Receive = true, Delivery = ServerLedMessages.WireChat }, "to-server"),
        new(Current, new PrefsMessage { Kind = ServerLedMessages.KindPrefs, Receive = false, Delivery = ServerLedMessages.WireChat }, "to-server"),
        new(Current, new PrefsMessage { Kind = ServerLedMessages.KindGone }, "to-server"),
        new(Current, new AnnounceMessage { Sequence = 3, Text = "Welcome. This server runs the server-led sample. · 2 players online · server up 1 h 5 min." }, "to-client"),
    };

    private static MessageFixtures Fixtures() => SampleFixtures.For(Sample, Mod, Current, Sent);

    [Fact]
    public void The_fixtures_are_what_1_0_0_sends()
    {
        var fixtures = Fixtures();
        Assert.Equal(new[] { Current }, fixtures.ModVersions.ToArray());
        Assert.All(fixtures.All, f => Assert.Equal("test", f.Source));
    }

    /// <summary>Every recorded preference passes the server's value check; the kinds and deliveries all arrive.</summary>
    [Fact]
    public void Recorded_preferences_replay_through_the_servers_validation()
    {
        var seen = new List<string>();
        MessageCompat.Replay<PrefsMessage>(Fixtures(), (m, f) =>
        {
            Assert.True(ServerLedMessages.IsValid(m), f + ": refused by the value check");
            seen.Add(m.Kind + "/" + m.Receive + "/" + m.Delivery);
        }).ThrowIfFailed();
        Assert.Contains(ServerLedMessages.KindGone + "/True/0", seen);
        Assert.Contains("0/True/" + ServerLedMessages.WirePopup, seen);
        Assert.Contains("0/False/0", seen);
    }

    /// <summary>The announcement keeps the chat line's text whole, also with unknown fields around it.</summary>
    [Fact]
    public void Recorded_announcements_replay_with_their_text()
    {
        var texts = new List<string>();
        MessageCompat.Replay<AnnounceMessage>(Fixtures(), (m, f) => texts.Add(m.Text)).ThrowIfFailed();
        Assert.All(texts, t => Assert.StartsWith("Welcome. This server runs the server-led sample.", t, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_message_skips_fields_a_newer_version_adds()
    {
        MessageCompat.AssertSkipsUnknownFields(new PrefsMessage { Receive = false, Delivery = ServerLedMessages.WirePopup });
        MessageCompat.AssertSkipsUnknownFields(new PrefsMessage { Kind = ServerLedMessages.KindGone }, seed: 7);
        MessageCompat.AssertSkipsUnknownFields(new AnnounceMessage { Sequence = 9, Text = "Grüße · 1 player online." });
    }

    /// <summary>
    /// Every recorded message has a type here, and the declared range is backed: each fixture from a version inside it
    /// reads with today's types and today's messages stay readable by it.
    /// </summary>
    [Fact]
    public void The_declared_range_is_backed_by_the_recorded_versions()
    {
        var types = MessageTypes.In(typeof(PrefsMessage).Assembly, typeof(PrefsMessage).Namespace);
        Assert.Equal(new[] { typeof(AnnounceMessage), typeof(PrefsMessage) }, types.ToArray());
        var fixtures = Fixtures();
        MessageCompat.ReplayAll(fixtures, types).ThrowIfFailed();
        // 1.0.0 is the first release: no older version exists to back the range yet, so it is allowed and said to be unproven.
        var range = MessageCompat.CheckRange(Peers, Current, fixtures, types, allowSelfOnly: true).ThrowIfFailed();
        Assert.Contains(range.Notes, n => n.Contains("not proven", StringComparison.Ordinal));
        Assert.False(MessageCompat.CheckRange(Peers, Current, fixtures, types).Ok);
        Assert.Equal(new[] { Current }, range.Versions.ToArray());

        string declaration = SampleFixtures.RepoFile("samples/Progenitor.SampleServerLed/ServerLedPolicy.cs");
        Assert.Contains("internal const string Peers = \"" + Peers + "\";", declaration, StringComparison.Ordinal);
        Assert.Contains("internal const string Version = \"" + Current + "\";", declaration, StringComparison.Ordinal);
    }
}
