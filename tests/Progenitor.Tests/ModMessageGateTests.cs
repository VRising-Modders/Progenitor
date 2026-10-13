using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C2 (docs/mod-compatibility.md, section 3): the send gate (no send without the bit or the mod), the
/// <c>'PrMs'</c> envelope, Core dropping a message for a mod or message it does not know, <c>MinReaderVersion</c>, and
/// hash collisions refused at registration.
/// </summary>
public class ModMessageGateTests
{
    [Theory]
    [InlineData(false, true, true, true, 10, SendResult.NotConnected)]
    [InlineData(true, false, true, true, 10, SendResult.PeerTooOld)]
    [InlineData(true, true, false, false, 10, SendResult.PeerNotReady)]
    [InlineData(true, true, true, false, 10, SendResult.PeerLacksMod)]
    [InlineData(true, true, true, true, 15 * 1024 - 15, SendResult.TooLarge)]
    [InlineData(false, false, false, false, 15 * 1024, SendResult.TooLarge)]
    [InlineData(true, true, true, true, 15 * 1024 - 16, SendResult.Sent)]
    [InlineData(true, true, true, true, 0, SendResult.Sent)]
    public void The_gate_sends_only_to_a_connected_peer_with_the_bit_whose_table_lists_the_mod(
        bool connected, bool bit, bool known, bool lists, int bytes, SendResult expected)
    {
        Assert.Equal(expected, ModMessageGate.Decide(new MessagePeerState(connected, bit, known, lists), bytes));
    }

    [Fact]
    public void The_envelope_round_trips_and_is_a_reserved_core_id()
    {
        Assert.Equal(0x50724D73, ModMessageEnvelope.EventId);
        Assert.True(CustomEventPolicy.IsReserved(ModMessageEnvelope.EventId));
        uint mod = ModMessageEnvelope.ModHash("Example.Trade");
        Assert.Equal(ModMessageEnvelope.ModHash("example.trade"), mod);
        byte[] p = ModMessageEnvelope.Pack(mod, ModMessageEnvelope.MessageHash("trade.offer"), 3, 2, new byte[] { 7, 8 });
        Assert.Equal(18, p.Length);
        Assert.Equal(1, p[0]);
        Assert.Equal(0, p[1]);
        Assert.True(ModMessageEnvelope.TryUnpack(p, out uint m, out uint h, out int v, out int min, out var payload, out _));
        Assert.Equal(mod, m);
        Assert.Equal(ModMessageEnvelope.MessageHash("trade.offer"), h);
        Assert.Equal(3, v);
        Assert.Equal(2, min);
        Assert.Equal(new byte[] { 7, 8 }, payload);
    }

    [Fact]
    public void Fnv1a_is_the_standard_32_bit_hash()
    {
        Assert.Equal(0x811C9DC5u, ModMessageEnvelope.Fnv1a(""));
        Assert.Equal(0xE40C292Cu, ModMessageEnvelope.Fnv1a("a"));
        Assert.Equal(0xBF9CF968u, ModMessageEnvelope.Fnv1a("foobar"));
    }

    [Theory]
    [InlineData(new byte[] { 1, 0, 0, 0 })]
    [InlineData(new byte[] { 2, 0, 1, 0, 0, 0, 2, 0, 0, 0, 1, 0, 1, 0, 0, 0 })]
    [InlineData(new byte[] { 1, 0, 1, 0, 0, 0, 2, 0, 0, 0, 1, 0, 1, 0, 5, 0 })]
    [InlineData(new byte[] { 1, 0, 1, 0, 0, 0, 2, 0, 0, 0, 0, 0, 1, 0, 0, 0 })]
    public void A_malformed_envelope_is_dropped(byte[] packet)
    {
        var router = new ModMessageRouter();
        Assert.False(router.TryRoute(packet, "peer", null, true, -1, out var routed, out var log));
        Assert.Null(routed);
        Assert.Contains("malformed", log);
    }

    private static byte[] Packet(string mod, string name, int version, int minReader, byte[]? payload = null) =>
        ModMessageEnvelope.Pack(ModMessageEnvelope.ModHash(mod), ModMessageEnvelope.MessageHash(name), version, minReader, payload ?? new byte[] { 1 });

    [Fact]
    public void Core_drops_a_message_for_a_mod_it_does_not_run_with_one_line_per_mod()
    {
        var router = new ModMessageRouter();
        Assert.False(router.TryRoute(Packet("other.mod", "x", 1, 1), "Player A", null, false, 3, out _, out var log));
        Assert.Contains("no mod with that id runs here", log);
        Assert.Contains("Player A", log);
        Assert.False(router.TryRoute(Packet("other.mod", "y", 1, 1), "Player A", null, false, 3, out _, out log));
        Assert.Null(log);
    }

    [Fact]
    public void Core_drops_a_message_it_has_no_handler_for_naming_it_when_it_knows_the_name()
    {
        var router = new ModMessageRouter();
        Assert.Null(router.Register("example.trade", "trade.offer", 1, (_, _, _) => { }, out _));
        Assert.Null(router.Claim("example.trade", "trade.cancel", out _, out _));
        Assert.False(router.TryRoute(Packet("example.trade", "trade.cancel", 1, 1), "the server", null, true, -1, out _, out var log));
        Assert.Contains("example.trade trade.cancel v1", log);
        Assert.Contains("no handler", log);
    }

    [Fact]
    public void A_message_above_the_reader_s_version_is_dropped_once_per_name()
    {
        var router = new ModMessageRouter();
        int ran = 0;
        router.Register("example.trade", "trade.offer", 2, (_, _, _) => ran++, out _);
        Assert.False(router.TryRoute(Packet("example.trade", "trade.offer", 3, 3), "Player A", null, false, 1, out _, out var log));
        Assert.Equal("Messages: dropped trade.offer v3 from Player A: needs reader 3, this mod reads 2", log);
        Assert.False(router.TryRoute(Packet("example.trade", "trade.offer", 4, 3), "Player A", null, false, 1, out _, out log));
        Assert.Null(log);

        // A newer sender that keeps MinReaderVersion low is read.
        Assert.True(router.TryRoute(Packet("example.trade", "trade.offer", 3, 2), "Player A", g => "1.4.0", false, 1, out var routed, out _));
        routed!.Dispatch(null, routed.Payload, routed.Info);
        Assert.Equal(1, ran);
        Assert.Equal(3, routed.Info.SenderVersion);
        Assert.Equal("1.4.0", routed.Info.SenderModVersion);
        Assert.Equal("trade.offer", routed.Info.Name);
        Assert.False(routed.Info.FromServer);
        Assert.Equal(1, routed.Info.UserIndex);
    }

    [Fact]
    public void One_handler_per_message_and_unregister_needs_the_owner_token()
    {
        var router = new ModMessageRouter();
        Assert.Null(router.Register("m", "a", 1, (_, _, _) => { }, out var token));
        Assert.Contains("already handles", router.Register("m", "a", 1, (_, _, _) => { }, out _));
        Assert.False(router.Unregister("m", "a", new object()));
        Assert.True(router.Unregister("m", "a", token));
        Assert.Null(router.Register("m", "a", 1, (_, _, _) => { }, out _));
    }

    [Fact]
    public void A_forgotten_mod_s_messages_are_dropped()
    {
        var router = new ModMessageRouter();
        router.Register("m", "a", 1, (_, _, _) => { }, out _);
        Assert.True(router.TryRoute(Packet("m", "a", 1, 1), "p", null, true, -1, out _, out _));
        router.ForgetMod("m");
        Assert.False(router.TryRoute(Packet("m", "a", 1, 1), "p", null, true, -1, out _, out var log));
        Assert.Contains("no mod with that id", log);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Trade")]
    [InlineData(".x")]
    [InlineData("a b")]
    public void A_bad_message_name_is_refused(string name)
    {
        var router = new ModMessageRouter();
        Assert.NotNull(router.Claim("m", name, out _, out _));
        Assert.NotNull(router.Claim("m", new string('a', 65), out _, out _));
        Assert.Null(router.Claim("m", new string('a', 64), out _, out _));
    }

    /// <summary>Two different valid names with the same FNV-1a 32 hash (found by a birthday search, deterministic).</summary>
    private static (string A, string B) Collision(Func<string, uint> hash)
    {
        var seen = new Dictionary<uint, string>();
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        for (int i = 0; ; i++)
        {
            int n = i;
            var chars = new char[6];
            for (int k = 0; k < chars.Length; k++)
            {
                chars[k] = alphabet[n % alphabet.Length];
                n /= alphabet.Length;
            }

            string s = "n" + new string(chars);
            uint h = hash(s);
            if (seen.TryGetValue(h, out var other)) return (other, s);
            seen[h] = s;
        }
    }

    [Fact]
    public void A_message_name_hash_collision_is_refused_with_both_names()
    {
        var (a, b) = Collision(ModMessageEnvelope.MessageHash);
        Assert.NotEqual(a, b);
        Assert.Equal(ModMessageEnvelope.MessageHash(a), ModMessageEnvelope.MessageHash(b));
        var router = new ModMessageRouter();
        Assert.Null(router.Register("example.trade", a, 1, (_, _, _) => { }, out _));
        string? refusal = router.Claim("example.trade", b, out _, out _);
        Assert.NotNull(refusal);
        Assert.Contains(a, refusal);
        Assert.Contains(b, refusal);
        Assert.NotNull(router.Register("example.trade", b, 1, (_, _, _) => { }, out _));
        // The same name in another mod is fine: names are scoped to the mod.
        Assert.Null(router.Claim("other.mod", b, out _, out _));
    }

    [Fact]
    public void A_mod_guid_hash_collision_is_refused_with_both_guids()
    {
        var (a, b) = Collision(ModMessageEnvelope.ModHash);
        var router = new ModMessageRouter();
        Assert.Null(router.Claim(a, "hello", out _, out _));
        string? refusal = router.Claim(b, "hello", out _, out _);
        Assert.NotNull(refusal);
        Assert.Contains(a, refusal);
        Assert.Contains(b, refusal);
        // The same GUID in another case is the same mod.
        Assert.Null(router.Claim(a.ToUpperInvariant(), "hello", out _, out _));
    }

    [Fact]
    public void Reader_versions_are_bounded()
    {
        var router = new ModMessageRouter();
        Assert.NotNull(router.Register("m", "a", 0, (_, _, _) => { }, out _));
        Assert.NotNull(router.Register("m", "a", 65536, (_, _, _) => { }, out _));
    }
}
