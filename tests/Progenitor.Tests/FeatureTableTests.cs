using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C1 (docs/mod-compatibility.md, section 1): the <c>'PrFt'</c> feature table, its limits, the
/// <c>more</c> truncation, unknown keys, deltas and their schedule, <c>Known</c>, and withdrawal on unload.
/// </summary>
public class FeatureTableTests
{
    private static FeatureTable Table(params (string Id, string Ver, (string Name, int Level)[] F)[] mods)
    {
        var t = new FeatureTable { Core = "2026.10.12", CoreSha = "ab12", BootstrapProtocol = 5 };
        foreach (var (id, ver, f) in mods)
        {
            var row = new FeatureTableMod(id, ver);
            foreach (var (name, level) in f) row.Features[name] = level;
            t.Mods[id] = row;
        }

        return t;
    }

    private static (FeatureTable Table, List<string> Gone) RoundTrip(byte kind, byte[] body)
    {
        var book = new PeerFeatureBook();
        var asm = new FeatureTableAssembler();
        byte[]? whole = null;
        foreach (var p in FeatureTableWire.Pack(kind, body))
            if (asm.Add(p, out byte k, out var b, out _))
            {
                Assert.Equal(kind, k);
                whole = b;
            }

        Assert.NotNull(whole);
        Assert.True(FeatureTableWire.TryParse(whole!, out var table, out var gone, out var error), error);
        return (table, gone);
    }

    [Fact]
    public void Capability_bits_are_the_designed_values_and_this_core_declares_both()
    {
        Assert.Equal(32768u, (uint)Handshake.Capability.ModFeatures);
        Assert.Equal(65536u, (uint)Handshake.Capability.ModMessages);
        Assert.Equal(Handshake.Capability.ModFeatures | Handshake.Capability.ModMessages,
            Handshake.SelfCapabilities & (Handshake.Capability.ModFeatures | Handshake.Capability.ModMessages));
        Assert.Equal(0x50724674, FeatureTableWire.EventId);
        Assert.True(CustomEventPolicy.IsReserved(FeatureTableWire.EventId));
    }

    [Fact]
    public void Merge_adds_the_bits_to_an_existing_row_and_appends_one_when_there_is_none()
    {
        var rows = new List<Handshake.ModRecord>();
        Assert.True(Handshake.Compare.MergeCapabilityBits(rows, ModCompatCaps.RequestBits));
        Assert.Equal(ModCompatCaps.RequestBits, Handshake.Compare.ReadCapabilities(rows));

        var policy = new List<Handshake.ModRecord>();
        Handshake.Compare.AppendCapabilityRow(policy, Handshake.Capability.ClientPolicy | Handshake.Capability.PolicyRecheck);
        Assert.True(Handshake.Compare.MergeCapabilityBits(policy, ModCompatCaps.RequestBits));
        Assert.Single(policy);
        Assert.Equal(Handshake.Capability.ClientPolicy | Handshake.Capability.PolicyRecheck | ModCompatCaps.RequestBits,
            Handshake.Compare.ReadCapabilities(policy));
    }

    [Fact]
    public void Merge_does_not_drop_a_mod_row_when_the_request_is_full()
    {
        var rows = new List<Handshake.ModRecord>();
        for (int i = 0; i < Handshake.MaxMods; i++) rows.Add(Handshake.ModRecord.Undeclared("mod" + i, "1.0.0"));
        Assert.False(Handshake.Compare.MergeCapabilityBits(rows, ModCompatCaps.RequestBits));
        Assert.Equal(Handshake.MaxMods, rows.Count);
        Assert.Equal(Handshake.Capability.None, Handshake.Compare.ReadCapabilities(rows));
    }

    [Fact]
    public void A_full_table_round_trips()
    {
        var t = Table(("example.trade", "1.4.0", new[] { ("trade", 2), ("map-pins", 1) }), ("other.mod", "0.1.0", Array.Empty<(string, int)>()));
        byte[] body = FeatureTableWire.FullBody(t, out int kept);
        Assert.Equal(2, kept);
        var (back, gone) = RoundTrip(FeatureTableWire.KindFull, body);
        Assert.Empty(gone);
        Assert.Equal("2026.10.12", back.Core);
        Assert.Equal("ab12", back.CoreSha);
        Assert.Equal(5, back.BootstrapProtocol);
        Assert.False(back.More);
        Assert.Equal("1.4.0", back.Mods["EXAMPLE.TRADE"].Version);
        Assert.Equal(2, back.Mods["example.trade"].Features["trade"]);
        Assert.Equal(1, back.Mods["example.trade"].Features["map-pins"]);
        Assert.Empty(back.Mods["other.mod"].Features);
        string json = Encoding.UTF8.GetString(body);
        Assert.DoesNotContain("\"f\":{}", json);
        Assert.DoesNotContain("\"more\"", json);
    }

    [Fact]
    public void Unknown_keys_are_ignored_and_bad_rows_names_and_levels_are_skipped()
    {
        string json = "{\"v\":7,\"core\":\"x\",\"future\":{\"a\":[1,2]},\"mods\":["
                      + "{\"id\":\"a.mod\",\"ver\":\"1.0\",\"newKey\":true,\"f\":{\"ok\":3,\"Bad\":1,\"neg\":0,\"big\":70000,\"str\":\"1\"}},"
                      + "{\"ver\":\"no id\"},5,"
                      + "{\"id\":\"b.mod\",\"ver\":\"2.0\",\"f\":[1]}]}";
        Assert.True(FeatureTableWire.TryParse(Encoding.UTF8.GetBytes(json), out var t, out _, out var error), error);
        Assert.Equal(2, t.Mods.Count);
        Assert.Equal(new Dictionary<string, int> { ["ok"] = 3 }, t.Mods["a.mod"].Features);
        Assert.Empty(t.Mods["b.mod"].Features);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"mods\":3}")]
    [InlineData("[1,2]")]
    public void A_body_that_is_not_a_table_fails_closed(string json)
    {
        Assert.False(FeatureTableWire.TryParse(Encoding.UTF8.GetBytes(json), out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Over_256_mods_are_cut_and_marked_more()
    {
        var mods = Enumerable.Range(0, 300).Select(i => ("m" + i.ToString("D3"), "1.0.0", Array.Empty<(string, int)>())).ToArray();
        byte[] body = FeatureTableWire.FullBody(Table(mods), out int kept);
        Assert.Equal(ModFeatureLimits.MaxModsPerTable, kept);
        var (back, _) = RoundTrip(FeatureTableWire.KindFull, body);
        Assert.True(back.More);
        Assert.Equal(256, back.Mods.Count);
    }

    [Fact]
    public void A_table_over_32_KiB_is_cut_to_fit_marked_more_and_spans_at_most_three_packets()
    {
        var feats = Enumerable.Range(0, 32).Select(i => ("feature-name-that-is-long-" + i.ToString("D2"), 65535)).ToArray();
        var mods = Enumerable.Range(0, 200).Select(i => ("some.rather.long.mod.guid." + i.ToString("D3"), "1.0.0-beta.7", feats)).ToArray();
        byte[] body = FeatureTableWire.FullBody(Table(mods), out int kept);
        Assert.True(body.Length <= ModFeatureLimits.MaxTableBytes);
        Assert.True(kept < 200);
        var packets = FeatureTableWire.Pack(FeatureTableWire.KindFull, body);
        Assert.InRange(packets.Count, 2, 3);
        Assert.All(packets, p => Assert.True(p.Length <= ModFeatureLimits.MaxPacketBytes));
        var (back, _) = RoundTrip(FeatureTableWire.KindFull, body);
        Assert.True(back.More);
        Assert.Equal(kept, back.Mods.Count);
    }

    [Fact]
    public void Pack_refuses_a_body_over_the_cap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FeatureTableWire.Pack(FeatureTableWire.KindFull, new byte[ModFeatureLimits.MaxTableBytes + 1]));
    }

    [Fact]
    public void An_out_of_order_or_foreign_packet_drops_the_partial_table()
    {
        var body = new byte[20000];
        for (int i = 0; i < body.Length; i++) body[i] = (byte)'a';
        var packets = FeatureTableWire.Pack(FeatureTableWire.KindFull, body);
        Assert.Equal(2, packets.Count);
        var asm = new FeatureTableAssembler();
        Assert.False(asm.Add(packets[1], out _, out _, out var error));
        Assert.NotNull(error);
        Assert.False(asm.Add(new byte[] { 9, 1, 0, 0, 1, 0, 0, 0 }, out _, out _, out error));
        Assert.NotNull(error);
        Assert.False(asm.Add(packets[0], out _, out _, out _));
        Assert.True(asm.Add(packets[1], out _, out var whole, out _));
        Assert.Equal(body.Length, whole.Length);
    }

    [Fact]
    public void Known_is_false_before_the_table_arrives_and_for_a_peer_that_never_sends_one()
    {
        var book = new PeerFeatureBook();
        var view = new PeerTableView("example.trade", () => book.Get(0));
        Assert.False(view.Known);
        Assert.False(view.Has("trade"));
        Assert.Null(view.CoreVersion);
        Assert.Null(view.ModVersion("example.trade"));
        Assert.False(PeerTableView.Unknown.Known);
        Assert.False(PeerTableView.Unknown.Has("x", "y"));

        // A peer without the bit is never sent a table and never sends one: its key stays unknown while another's fills.
        foreach (var p in FeatureTableWire.Pack(FeatureTableWire.KindFull, FeatureTableWire.FullBody(Table(("example.trade", "1.4.0", new[] { ("trade", 2) })), out _)))
            book.Receive(7, p, out _);
        Assert.False(view.Known);
        var seven = new PeerTableView("example.trade", () => book.Get(7));
        Assert.True(seven.Known);
        Assert.True(seven.Has("trade", 2));
        Assert.False(seven.Has("trade", 3));
        Assert.True(seven.Has("example.trade", "trade"));
        Assert.Equal("1.4.0", seven.ModVersion("example.trade"));
        Assert.Equal("2026.10.12", seven.CoreVersion);
    }

    [Fact]
    public void ModVersion_falls_back_to_the_handshake_rows_only_for_a_mod_the_table_does_not_list()
    {
        var t = Table(("listed", "2.0.0", Array.Empty<(string, int)>()));
        var view = new PeerTableView("me", () => t, g => g == "rowonly" ? "1.1.0" : null);
        Assert.Equal("2.0.0", view.ModVersion("listed"));
        Assert.Equal("1.1.0", view.ModVersion("rowonly"));
        Assert.Null(view.ModVersion("nowhere"));
    }

    [Fact]
    public void A_delta_replaces_listed_rows_removes_gone_ids_and_is_ignored_before_a_full_table()
    {
        var book = new PeerFeatureBook();
        int changes = 0;
        book.Changed += _ => changes++;
        var delta = Table(("a", "2.0", new[] { ("x", 2) }));
        Assert.False(book.ApplyDelta(0, delta, Array.Empty<string>(), out var log));
        Assert.NotNull(log);
        Assert.False(book.Known(0));
        Assert.Equal(0, changes);

        book.ApplyFull(0, Table(("a", "1.0", new[] { ("x", 1), ("y", 1) }), ("b", "1.0", Array.Empty<(string, int)>())));
        byte[] body = FeatureTableWire.DeltaBody(delta, delta.Mods.Values.ToList(), new[] { "b" }, out _);
        foreach (var p in FeatureTableWire.Pack(FeatureTableWire.KindDelta, body)) book.Receive(0, p, out _);
        var t = book.Get(0)!;
        Assert.Equal("2.0", t.Mods["a"].Version);
        Assert.Equal(new Dictionary<string, int> { ["x"] = 2 }, t.Mods["a"].Features);
        Assert.False(t.Mods.ContainsKey("b"));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Forget_clears_a_peer_and_raises_changed_only_when_it_had_a_table()
    {
        var book = new PeerFeatureBook();
        var raised = new List<int>();
        book.Changed += raised.Add;
        book.Forget(3);
        Assert.Empty(raised);
        book.ApplyFull(3, Table());
        book.Forget(3);
        Assert.Equal(new[] { 3, 3 }, raised);
        Assert.False(book.Known(3));
    }

    [Theory]
    [InlineData("trade", 1, true)]
    [InlineData("map-pins.v2_x", 65535, true)]
    [InlineData("", 1, false)]
    [InlineData("Trade", 1, false)]
    [InlineData("-trade", 1, false)]
    [InlineData("tr ade", 1, false)]
    [InlineData("trade", 0, false)]
    [InlineData("trade", 65536, false)]
    public void Offer_validates_name_and_level(string name, int level, bool ok)
    {
        var book = new FeatureOfferBook();
        if (ok) Assert.NotNull(book.Add("m", name, level));
        else Assert.Throws<ArgumentException>(() => book.Add("m", name, level));
    }

    [Fact]
    public void Offer_refuses_a_name_over_48_characters_and_a_33rd_feature_naming_the_limit()
    {
        var book = new FeatureOfferBook();
        Assert.NotNull(book.Add("m", new string('a', 48)));
        Assert.Throws<ArgumentException>(() => book.Add("m", new string('a', 49)));
        for (int i = 1; i < 32; i++) book.Add("m", "f" + i);
        Assert.NotNull(book.Add("m", "f1", 2));
        var e = Assert.Throws<ArgumentException>(() => book.Add("m", "f33"));
        Assert.Contains("32", e.Message);
        Assert.NotNull(book.Add("other", "f33"));
    }

    [Fact]
    public void The_level_is_the_highest_live_offer_and_dispose_withdraws_it()
    {
        var book = new FeatureOfferBook();
        var changed = new List<string>();
        book.Changed += changed.Add;
        var one = book.Add("example.trade", "trade");
        var two = book.Add("example.trade", "trade", 2);
        Assert.Equal(2, book.Of("example.trade")["trade"]);
        two.Dispose();
        Assert.Equal(1, book.Of("example.trade")["trade"]);
        one.Dispose();
        one.Dispose();
        Assert.Empty(book.Of("example.trade"));
        Assert.Empty(book.Mods());
        Assert.Equal(4, changed.Count);
    }

    [Fact]
    public void Unloading_a_mod_withdraws_its_features_and_the_next_delta_carries_it()
    {
        var offers = new FeatureOfferBook();
        var schedule = new FeatureDeltaSchedule();
        var loaded = new List<(string, string)> { ("example.trade", "1.4.0"), ("other.mod", "1.0") };
        var handle = offers.Add("example.trade", "trade", 2);
        schedule.Baseline(FeatureTableBuilder.Build(loaded, offers, "c", "s", 5));

        handle.Dispose();
        loaded.RemoveAt(0);
        schedule.MarkDirty(10);
        Assert.True(schedule.TakeDelta(FeatureTableBuilder.Build(loaded, offers, "c", "s", 5), 11, out var changed, out var gone));
        Assert.Empty(changed);
        Assert.Equal(new[] { "example.trade" }, gone);

        // The reload offers again: one changed row.
        loaded.Add(("example.trade", "1.4.0"));
        offers.Add("example.trade", "trade", 2);
        schedule.MarkDirty(20);
        Assert.True(schedule.TakeDelta(FeatureTableBuilder.Build(loaded, offers, "c", "s", 5), 21, out changed, out gone));
        Assert.Single(changed);
        Assert.Equal(2, changed[0].Features["trade"]);
        Assert.Empty(gone);
    }

    [Fact]
    public void Deltas_are_coalesced_for_one_second_and_at_most_one_per_second()
    {
        var s = new FeatureDeltaSchedule();
        Assert.False(s.Due(100));
        s.MarkDirty(100);
        s.MarkDirty(100.4);
        s.MarkDirty(100.9);
        Assert.False(s.Due(100.5));
        Assert.True(s.Due(101.0));
        var t = Table(("a", "1", new[] { ("x", 1) }));
        Assert.True(s.TakeDelta(t, 101.0, out _, out _));
        Assert.False(s.Dirty);

        s.MarkDirty(101.2);
        Assert.False(s.Due(101.9));
        Assert.True(s.Due(102.2));

        // Nothing differs: no delta, and the mark is spent.
        Assert.False(s.TakeDelta(t, 102.2, out var changed, out var gone));
        Assert.Empty(changed);
        Assert.Empty(gone);
        Assert.False(s.Dirty);
    }

    [Fact]
    public void A_baseline_from_a_full_send_is_not_needed_for_correctness_rows_are_idempotent()
    {
        // A peer that joined after the baseline gets rows it already has; applying them twice is the same table.
        var book = new PeerFeatureBook();
        var full = Table(("a", "1", new[] { ("x", 1) }));
        book.ApplyFull(0, full.Copy());
        book.ApplyDelta(0, full.Copy(), Array.Empty<string>(), out _);
        Assert.True(book.Get(0)!.Mods["a"].SameAs(full.Mods["a"]));
    }

    [Fact]
    public void The_builder_lists_every_loaded_mod_and_mods_that_only_have_offers()
    {
        var offers = new FeatureOfferBook();
        offers.Add("loading.mod", "early");
        var t = FeatureTableBuilder.Build(new[] { ("a", "1.0"), ("b", "2.0") }, offers, "2026.10.12", "sha", 5);
        Assert.Equal(new[] { "a", "b", "loading.mod" }, t.Mods.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(1, t.Mods["loading.mod"].Features["early"]);
        Assert.Equal("2026.10.12", t.Core);
    }

    [Fact]
    public void Raw_send_warning_fires_once_per_mod_and_id_only_when_versions_may_differ()
    {
        var w = new RawSendWarnings();
        Assert.Null(w.Check("example.trade", 0x54724F66, "1.0", null, true));
        Assert.Null(w.Check("example.trade", 0x54724F66, "1.0", "1.0", false));
        string? line = w.Check("example.trade", 0x54724F66, "1.0", "1.0", true);
        Assert.Equal("Messages: example.trade sends raw ctx.Net messages (id TrOf); peers on another version may misread them. Use ctx.Messages.", line);
        Assert.Null(w.Check("example.trade", 0x54724F66, "1.0", "1.2", true));
        Assert.NotNull(w.Check("example.trade", 0x54724F67, "1.0", "1.2", false));
    }

    [Fact]
    public void Live_wiring_is_where_the_design_puts_it()
    {
        string handshake = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "Handshake.cs");
        Assert.Contains("ModCompatCaps.NoteServerCaps(serverCaps);", handshake);
        Assert.Contains("ModCompatNet.AfterClientAnswer();", handshake);
        Assert.Contains("ModCompatNet.OnHandshakeOk(world, userEntity, clientCaps);", handshake);
        string policy = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "Handshake.ClientPolicy.cs");
        Assert.Contains("Compare.MergeCapabilityBits(rows, ModCompatCaps.RequestBitsNow());", policy);
        string refresh = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "HandshakeRefresh.cs");
        Assert.Contains("Compare.MergeCapabilityBits(refreshRows, ModCompatCaps.RequestBitsNow());", refresh);
        string net = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "ModCompatNet.cs");
        // Core-owned ids through the existing owned-id path (CustomEvents registration), no Bootstrap change.
        Assert.Contains("CustomEvents.OnReceiveFromClient(FeatureTableWire.EventId, OnServerTable);", net);
        Assert.Contains("CustomEvents.OnReceive(ModMessageEnvelope.EventId, OnClientMessage);", net);
    }
}
