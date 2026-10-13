using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C1 + C2 end to end, game-free: two fake peers, each with its own offer book, peer tables and
/// message router, exchange <c>'PrFt'</c> tables and <c>'PrMs'</c> envelopes as byte packets. One runs version 1.0.0 of a
/// mod, the other 1.1.0, which adds a message field and a feature. Both directions are checked: the newer side's added
/// field is skipped by the older reader, and the older side's message reads on the newer side with the new field at
/// its initializer. The same pieces run in the game (ModCompatNet.cs); only the transport is faked here.
/// </summary>
public class ModCompatEndToEndTests
{
    private const string ModId = "progenitor.test.compat";

    [ModMessage("hello", Version = 1)]
    public sealed class HelloV1
    {
        [Field(1)] public int Seq;
        [Field(2)] public string Text = "";
    }

    [ModMessage("hello", Version = 2)]
    public sealed class HelloV2
    {
        [Field(1)] public int Seq;
        [Field(2)] public string Text = "";
        [Field(3, Since = 2)] public string Note = "no note (sender predates it)";
    }

    [ModMessage("hello", Version = 3, MinReaderVersion = 3)]
    public sealed class HelloV3Breaking
    {
        [Field(1)] public long Seq;
    }

    /// <summary>One process: what it offers, what it knows of the other side, and its handlers.</summary>
    private sealed class Peer
    {
        internal readonly FeatureOfferBook Offers = new();
        internal readonly PeerFeatureBook Tables = new();
        internal readonly ModMessageRouter Router = new();
        internal readonly string ModVersion;
        internal readonly Handshake.Capability Caps;
        internal readonly List<string> Log = new();
        internal readonly List<object> Received = new();

        internal Peer(string modVersion, Handshake.Capability caps)
        {
            ModVersion = modVersion;
            Caps = caps;
        }

        internal FeatureTable Local(bool runsMod = true) => FeatureTableBuilder.Build(
            runsMod ? new[] { (ModId, ModVersion), ("progenitor.other", "3.0.0") } : new[] { ("progenitor.other", "3.0.0") },
            Offers, "2026.10.12", "0123abcd", 5);

        internal IPeerView View => new PeerTableView(ModId, () => Tables.Get(0));

        internal void On<T>(int readerVersion) where T : class, new()
        {
            var schema = MessageSchema.For(typeof(T));
            var attr = schema.Message!;
            Assert.Null(Router.Register(ModId, attr.Name, readerVersion, (_, payload, info) => Received.Add((schema.Decode(payload), info)), out _));
        }
    }

    /// <summary>Send <paramref name="from"/>'s full table to <paramref name="to"/>, only when the receiver declared ModFeatures.</summary>
    private static void SendTable(Peer from, Peer to, bool runsMod = true)
    {
        if ((to.Caps & Handshake.Capability.ModFeatures) == 0) return;
        byte[] body = FeatureTableWire.FullBody(from.Local(runsMod), out _);
        foreach (var packet in FeatureTableWire.Pack(FeatureTableWire.KindFull, body))
        {
            to.Tables.Receive(0, packet, out var log);
            if (log != null) to.Log.Add(log);
        }
    }

    /// <summary>The send path: encode, gate on what <paramref name="from"/> knows of <paramref name="to"/>, envelope, route, run.</summary>
    private static SendResult Send<T>(Peer from, Peer to, T message) where T : class
    {
        var schema = MessageSchema.For(typeof(T));
        var attr = schema.Message!;
        byte[] payload = schema.Encode(message);
        var table = from.Tables.Get(0);
        var state = new MessagePeerState(true, (to.Caps & Handshake.Capability.ModMessages) != 0, table != null, table != null && table.Mods.ContainsKey(ModId));
        var result = ModMessageGate.Decide(state, payload.Length);
        if (result != SendResult.Sent) return result;
        Assert.Null(from.Router.Claim(ModId, attr.Name, out uint modHash, out uint msgHash));
        byte[] packet = ModMessageEnvelope.Pack(modHash, msgHash, attr.Version, attr.MinReaderVersion, payload);
        if (to.Router.TryRoute(packet, "peer", g => to.Tables.Get(0)?.Mods[g].Version, true, -1, out var routed, out var log))
            routed!.Dispatch(null, routed.Payload, routed.Info);
        else if (log != null) to.Log.Add(log);
        return result;
    }

    private static (Peer Old, Peer New) Pair()
    {
        var all = Handshake.SelfCapabilities;
        var older = new Peer("1.0.0", all);
        older.Offers.Add(ModId, "hello");
        older.On<HelloV1>(1);
        var newer = new Peer("1.1.0", all);
        newer.Offers.Add(ModId, "hello", 2);
        newer.Offers.Add(ModId, "notes");
        newer.On<HelloV2>(2);
        SendTable(older, newer);
        SendTable(newer, older);
        return (older, newer);
    }

    [Fact]
    public void Each_side_sees_the_other_s_features_and_version()
    {
        var (older, newer) = Pair();
        Assert.True(older.View.Known);
        Assert.True(older.View.Has("hello", 2));
        Assert.True(older.View.Has("notes"));
        Assert.Equal("1.1.0", older.View.ModVersion(ModId));
        Assert.Equal("2026.10.12", older.View.CoreVersion);

        Assert.True(newer.View.Has("hello"));
        Assert.False(newer.View.Has("hello", 2));
        Assert.False(newer.View.Has("notes"));
        Assert.Equal("1.0.0", newer.View.ModVersion(ModId));
        Assert.Equal("3.0.0", newer.View.ModVersion("progenitor.other"));
    }

    [Fact]
    public void A_newer_sender_s_added_field_is_skipped_by_the_older_reader()
    {
        var (older, newer) = Pair();
        Assert.Equal(SendResult.Sent, Send(newer, older, new HelloV2 { Seq = 1, Text = "from 1.1.0", Note = "only 1.1.0 knows this" }));
        var (msg, info) = ((object, MessageInfo))older.Received.Single();
        var hello = Assert.IsType<HelloV1>(msg);
        Assert.Equal(1, hello.Seq);
        Assert.Equal("from 1.1.0", hello.Text);
        Assert.Equal(2, info.SenderVersion);
        Assert.Equal("1.1.0", info.SenderModVersion);
        Assert.Empty(older.Log);
    }

    [Fact]
    public void An_older_sender_s_message_reads_on_the_newer_side_with_the_new_field_at_its_initializer()
    {
        var (older, newer) = Pair();
        Assert.Equal(SendResult.Sent, Send(older, newer, new HelloV1 { Seq = 2, Text = "from 1.0.0" }));
        var (msg, info) = ((object, MessageInfo))newer.Received.Single();
        var hello = Assert.IsType<HelloV2>(msg);
        Assert.Equal(2, hello.Seq);
        Assert.Equal("from 1.0.0", hello.Text);
        Assert.Equal("no note (sender predates it)", hello.Note);
        Assert.Equal(1, info.SenderVersion);
        Assert.Equal("1.0.0", info.SenderModVersion);
    }

    [Fact]
    public void A_breaking_message_is_dropped_by_the_older_reader_with_one_line()
    {
        var (older, newer) = Pair();
        Assert.Equal(SendResult.Sent, Send(newer, older, new HelloV3Breaking { Seq = 5 }));
        Assert.Empty(older.Received);
        Assert.Equal("Messages: dropped hello v3 from peer: needs reader 3, this mod reads 1", older.Log.Single());
    }

    [Fact]
    public void Nothing_is_sent_to_a_peer_without_the_bits_or_without_the_mod()
    {
        var oldCore = Handshake.SelfCapabilities & ~(Handshake.Capability.ModFeatures | Handshake.Capability.ModMessages);
        var sender = new Peer("1.1.0", Handshake.SelfCapabilities);
        var tooOld = new Peer("1.0.0", oldCore);
        tooOld.On<HelloV1>(1);
        SendTable(tooOld, sender);   // the sender declared the bit, so it would read one; an old Core never sends it
        sender.Tables.Forget(0);
        SendTable(sender, tooOld);   // dropped: the receiver did not declare ModFeatures
        Assert.False(tooOld.View.Known);
        Assert.Equal(SendResult.PeerTooOld, Send(sender, tooOld, new HelloV2()));
        Assert.Empty(tooOld.Received);

        var lacks = new Peer("", Handshake.SelfCapabilities);
        var sender2 = new Peer("1.1.0", Handshake.SelfCapabilities);
        Assert.Equal(SendResult.PeerNotReady, Send(sender2, lacks, new HelloV2()));
        SendTable(lacks, sender2, runsMod: false);
        Assert.Equal(SendResult.PeerLacksMod, Send(sender2, lacks, new HelloV2()));
        Assert.False(sender2.View.Has("hello"));
        Assert.Equal("3.0.0", sender2.View.ModVersion("progenitor.other"));
    }

    [Fact]
    public void A_hot_reload_on_one_side_reaches_the_other_as_one_delta()
    {
        var (older, newer) = Pair();
        var schedule = new FeatureDeltaSchedule();
        schedule.Baseline(newer.Local());
        int changes = 0;
        older.Tables.Changed += _ => changes++;

        // The newer side's plugin unloads (its offers are withdrawn) and loads again with one more feature, inside a second.
        var all = newer.Offers.Mods().SelectMany(m => newer.Offers.Of(m).Keys).ToList();
        Assert.Contains("notes", all);
        var fresh = new Peer("1.1.0", newer.Caps);
        fresh.Offers.Add(ModId, "hello", 2);
        fresh.Offers.Add(ModId, "notes");
        fresh.Offers.Add(ModId, "pins", 3);
        schedule.MarkDirty(50.0);
        schedule.MarkDirty(50.6);
        Assert.False(schedule.Due(50.9));
        Assert.True(schedule.Due(51.0));
        Assert.True(schedule.TakeDelta(fresh.Local(), 51.0, out var changed, out var gone));
        byte[] body = FeatureTableWire.DeltaBody(fresh.Local(), changed, gone, out _);
        foreach (var p in FeatureTableWire.Pack(FeatureTableWire.KindDelta, body)) older.Tables.Receive(0, p, out _);

        Assert.Equal(1, changes);
        Assert.True(older.View.Has("pins", 3));
        Assert.True(older.View.Has("hello", 2));
    }
}
