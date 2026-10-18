using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.SampleServerLed;
using Progenitor.Testing;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.SampleTests.ServerLed;

/// <summary>
/// Frozen 1.0.0 fixtures: the announcement still uses the production type. Preference packets
/// replay through a test-only legacy schema, not a runtime handler. This does not verify the new
/// Core personal-settings protocol or claim old clients can edit the new server preferences.
/// </summary>
public sealed class ServerLedReplayTests
{
    private const string Sample = "Progenitor.SampleServerLed";
    private const string Mod = "progenitor.sampleserverled";
    private const string Current = "1.0.0";

    /// <summary>The archived release range, used only for its frozen replay evidence.</summary>
    private const string Peers = ">=1.0.0 <2.0.0";

    /// <summary>What 1.0.0 sends: preferences (both deliveries, announcements off), the goodbye and one announcement.</summary>
    private static readonly SampleMessage[] Sent =
    {
        new(Current, new LegacyPrefsMessage { Kind = LegacyPrefsWire.KindPrefs, Receive = true, Delivery = LegacyPrefsWire.WirePopup }, "to-server"),
        new(Current, new LegacyPrefsMessage { Kind = LegacyPrefsWire.KindPrefs, Receive = true, Delivery = LegacyPrefsWire.WireChat }, "to-server"),
        new(Current, new LegacyPrefsMessage { Kind = LegacyPrefsWire.KindPrefs, Receive = false, Delivery = LegacyPrefsWire.WireChat }, "to-server"),
        new(Current, new LegacyPrefsMessage { Kind = LegacyPrefsWire.KindGone }, "to-server"),
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

    /// <summary>Archived preferences still satisfy their historical value check.</summary>
    [Fact]
    public void Archived_preferences_replay_through_the_legacy_value_check()
    {
        var seen = new List<string>();
        MessageCompat.Replay<LegacyPrefsMessage>(Fixtures(), (m, f) =>
        {
            Assert.True(LegacyPrefsWire.IsValid(m), f + ": refused by the value check");
            seen.Add(m.Kind + "/" + m.Receive + "/" + m.Delivery);
        }).ThrowIfFailed();
        Assert.Contains(LegacyPrefsWire.KindGone + "/True/0", seen);
        Assert.Contains("0/True/" + LegacyPrefsWire.WirePopup, seen);
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
        MessageCompat.AssertSkipsUnknownFields(new LegacyPrefsMessage { Receive = false, Delivery = LegacyPrefsWire.WirePopup });
        MessageCompat.AssertSkipsUnknownFields(new LegacyPrefsMessage { Kind = LegacyPrefsWire.KindGone }, seed: 7);
        MessageCompat.AssertSkipsUnknownFields(new AnnounceMessage { Sequence = 9, Text = "Grüße · 1 player online." });
    }

    /// <summary>
    /// Every archived message still has a decoder here. This checks the frozen release's
    /// contract; only AnnounceMessage remains a production message.
    /// </summary>
    [Fact]
    public void The_archived_1_0_0_contract_remains_readable()
    {
        // The production sample only owns announcements now; legacy preferences stay in tests.
        var productionTypes = MessageTypes.In(typeof(AnnounceMessage).Assembly, typeof(AnnounceMessage).Namespace);
        Assert.Equal(new[] { typeof(AnnounceMessage) }, productionTypes.ToArray());
        var types = new[] { typeof(AnnounceMessage), typeof(LegacyPrefsMessage) };
        var fixtures = Fixtures();
        MessageCompat.ReplayAll(fixtures, types).ThrowIfFailed();
        // 1.0.0 is the first release: no older version exists to back the range yet, so it is allowed and said to be unproven.
        var range = MessageCompat.CheckRange(Peers, Current, fixtures, types, allowSelfOnly: true).ThrowIfFailed();
        Assert.Contains(range.Notes, n => n.Contains("not proven", StringComparison.Ordinal));
        Assert.False(MessageCompat.CheckRange(Peers, Current, fixtures, types).Ok);
        Assert.Equal(new[] { Current }, range.Versions.ToArray());


    }
}

// Frozen historical wire shape. Never shipped by the sample or registered by Plugin.cs.
[ModMessage("prefs")]
internal sealed class LegacyPrefsMessage
{
    [Field(1)] public int Kind;
    [Field(2)] public bool Receive = true;
    [Field(3)] public int Delivery;
}

internal static class LegacyPrefsWire
{
    internal const int KindPrefs = 0, KindGone = 1, WireChat = 0, WirePopup = 1;
    internal static bool IsValid(LegacyPrefsMessage? message) => message != null
        && message.Kind is KindPrefs or KindGone
        && (message.Kind == KindGone || message.Delivery is WireChat or WirePopup);
}
