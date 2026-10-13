using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Testing.Tests;

// Two versions of one message, as a mod's 1.0 and 1.1 builds would declare them.
[ModMessage("trade.offer")]
public sealed class OfferV1
{
    [Field(1)] public int ItemId;
    [Field(2)] public int Count;
}

[ModMessage("trade.offer", Version = 2)]
public sealed class OfferV2
{
    [Field(1)] public int ItemId;
    [Field(2)] public int Count;
    [Field(3, Since = 2)] public string Note = "(none)";
    [Field(4, Since = 2)] public Mood Mood = Mood.Calm;
}

public enum Mood { Calm = 0, Eager = 1 }

[ModMessage("trade.offer", Version = 3, MinReaderVersion = 3)]
public sealed class OfferV3Breaking
{
    [Field(1)] public int ItemId;
    [Field(5)] public long Total;
}

/// <summary>Field 2 retyped from int to string: the mistake the round trip must catch.</summary>
[ModMessage("trade.offer", Version = 2)]
public sealed class OfferRetyped
{
    [Field(1)] public int ItemId;
    [Field(2)] public string Count = "";
}

/// <summary>Field 2 narrowed from int to short: a value above 32767 does not survive.</summary>
[ModMessage("trade.offer", Version = 2)]
public sealed class OfferNarrowed
{
    [Field(1)] public int ItemId;
    [Field(2)] public short Count;
}

[ModMessage("trade.tick")]
public sealed class Tick
{
    [Field(1)] public List<int> Values = new();
}

/// <summary>Field 2 (Count) removed.</summary>
[ModMessage("trade.offer", Version = 2)]
public sealed class OfferRemoved
{
    [Field(1)] public int ItemId;
    [Field(3, Since = 2)] public string Note = "(none)";
    [Field(4, Since = 2)] public Mood Mood = Mood.Calm;
}

/// <summary>Count moved from field 2 to field 7.</summary>
[ModMessage("trade.offer", Version = 2)]
public sealed class OfferRenumbered
{
    [Field(1)] public int ItemId;
    [Field(7)] public int Count;
}

[ModMessage("trade.offer", Version = 2)]
public sealed class OfferUnsigned
{
    [Field(1)] public int ItemId;
    [Field(2)] public uint Count;
}

[ModMessage("trade.offer", Version = 2)]
public sealed class OfferEnumCount
{
    [Field(1)] public int ItemId;
    [Field(2)] public Mood Count;
}

[ModMessage("trade.offer", Version = 2)]
public sealed class OfferNoteBytes
{
    [Field(1)] public int ItemId;
    [Field(2)] public int Count;
    [Field(3)] public byte[] Note = Array.Empty<byte>();
}

public sealed class ItemV1
{
    [Field(1)] public int Id;
    [Field(2)] public int Count;
}

public sealed class ItemV2
{
    [Field(1)] public int Id;
    [Field(2)] public int Count;
    [Field(3)] public string Tag = "";
}

public sealed class ItemRenumbered
{
    [Field(1)] public int Id;
    [Field(5)] public int Count;
}

public sealed class ItemRetyped
{
    [Field(1)] public int Id;
    [Field(2)] public string Count = "";
}

[ModMessage("trade.basket")]
public sealed class BasketV1
{
    [Field(1)] public ItemV1? Item;
}

[ModMessage("trade.basket", Version = 2)]
public sealed class BasketV2
{
    [Field(1)] public ItemV2? Item;
}

[ModMessage("trade.basket", Version = 2)]
public sealed class BasketRenumbered
{
    [Field(1)] public ItemRenumbered? Item;
}

[ModMessage("trade.basket", Version = 2)]
public sealed class BasketRetyped
{
    [Field(1)] public ItemRetyped? Item;
}

public sealed class MessageCompatTests : IDisposable
{
    private const string Mod = "example.trade";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "progenitor-testing-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private MessageFixtures Record()
    {
        MessageFixtures.Save(_dir, Mod, "1.0.0", new OfferV1 { ItemId = 7, Count = 3 }, "to-server");
        MessageFixtures.Save(_dir, Mod, "1.1.0", new OfferV2 { ItemId = 8, Count = 4, Note = "hi", Mood = Mood.Eager }, "to-server");
        return MessageFixtures.Load(_dir);
    }

    [Fact]
    public void Encode_matches_the_hand_written_writer_byte_for_byte()
    {
        byte[] reflected = MessageCompat.Encode(new OfferV1 { ItemId = 7, Count = -3 });
        byte[] manual = new MessageWriter().Int(1, 7).Int(2, -3).ToArray();
        Assert.Equal(manual, reflected);
        var back = MessageCompat.Decode<OfferV1>(manual);
        Assert.Equal(7, back.ItemId);
        Assert.Equal(-3, back.Count);
    }

    [Fact]
    public void Save_and_Load_round_trip_with_the_sidecar_and_dedupe()
    {
        var a = MessageFixtures.Save(_dir, Mod, "1.0.0", new OfferV1 { ItemId = 7, Count = 3 });
        var b = MessageFixtures.Save(_dir, Mod, "1.0.0", new OfferV1 { ItemId = 7, Count = 3 });
        Assert.Equal(a.Path, b.Path);
        Assert.EndsWith(Path.Combine("1.0.0", "trade.offer@v1", Path.GetFileName(a.Path)), a.Path);
        var all = MessageFixtures.Load(_dir);
        var f = Assert.Single(all.All);
        Assert.Equal(Mod, f.Mod);
        Assert.Equal("1.0.0", f.ModVersion);
        Assert.Equal("trade.offer", f.Message);
        Assert.Equal(1, f.Version);
        Assert.Equal("test", f.Source);
        Assert.Equal(MessageCompat.Encode(new OfferV1 { ItemId = 7, Count = 3 }), f.Payload);
        Assert.Empty(MessageFixtures.Load(_dir, "other.mod").All);
        Assert.Empty(MessageFixtures.Load(Path.Combine(_dir, "missing")).All);
    }

    [Fact]
    public void A_damaged_fixture_fails_the_load_instead_of_passing_silently()
    {
        var f = MessageFixtures.Save(_dir, Mod, "1.0.0", new OfferV1 { ItemId = 7, Count = 3 });
        File.WriteAllBytes(f.Path, new byte[] { 8, 9 });
        var e = Assert.Throws<MessageCompatException>(() => MessageFixtures.Load(_dir));
        Assert.Contains("sha256", e.Message);
    }

    [Fact]
    public void An_older_type_reads_a_newer_fixture_and_skips_its_new_fields()
    {
        var fixtures = Record();
        var seen = new List<string>();
        var r = MessageCompat.Replay<OfferV1>(fixtures, (m, f) => seen.Add(f.ModVersion + ":" + m.ItemId + "x" + m.Count));
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(2, r.Checked);
        Assert.Equal(new[] { "1.0.0:7x3", "1.1.0:8x4" }, seen);
        Assert.Contains(r.Notes, n => n.Contains("field(s) 3, 4 unknown to OfferV1"));
    }

    [Fact]
    public void A_newer_type_reads_an_older_fixture_and_keeps_its_defaults()
    {
        var fixtures = Record();
        OfferV2? old = null;
        var r = MessageCompat.Replay<OfferV2>(fixtures, (m, f) => { if (f.ModVersion == "1.0.0") old = m; });
        Assert.True(r.Ok, r.ToString());
        Assert.NotNull(old);
        Assert.Equal("(none)", old!.Note);
        Assert.Equal(Mood.Calm, old.Mood);
    }

    [Fact]
    public void A_retyped_field_number_fails_the_replay()
    {
        var r = MessageCompat.Replay<OfferRetyped>(Record());
        Assert.False(r.Ok);
        Assert.Contains(r.Failures, f => f.Contains("field 2") && f.Contains("never retyped"));
    }

    [Fact]
    public void A_narrowed_field_fails_when_the_recorded_value_does_not_fit()
    {
        MessageFixtures.Save(_dir, Mod, "1.0.0", new OfferV1 { ItemId = 1, Count = 70000 });
        var r = MessageCompat.Replay<OfferNarrowed>(MessageFixtures.Load(_dir));
        Assert.False(r.Ok);
        Assert.Contains(r.Failures, f => f.Contains("did not survive"));
    }

    [Fact]
    public void A_handler_that_throws_fails_the_replay_and_names_the_fixture()
    {
        var r = MessageCompat.Replay<OfferV2>(Record(), (m, f) => { if (m.Count == 4) throw new InvalidOperationException("boom"); });
        var line = Assert.Single(r.Failures);
        Assert.Contains("1.1.0", line);
        Assert.Contains("boom", line);
        Assert.Throws<MessageCompatException>(() => r.ThrowIfFailed());
    }

    [Fact]
    public void Replaying_nothing_is_a_failure()
    {
        var r = MessageCompat.Replay<Tick>(Record());
        Assert.False(r.Ok);
        Assert.Contains("no fixture", r.Failures[0]);
    }

    [Fact]
    public void A_breaking_fixture_is_dropped_by_an_older_reader_as_in_the_game()
    {
        MessageFixtures.Save(_dir, Mod, "2.0.0", new OfferV3Breaking { ItemId = 1, Total = 5 });
        var fixtures = Record();
        var r = MessageCompat.Replay<OfferV2>(fixtures);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(2, r.Checked);
        Assert.Contains(r.Notes, n => n.Contains("dropped by this reader"));
    }

    [Fact]
    public void An_unknown_enum_value_is_tolerated_and_noted()
    {
        var payload = new MessageWriter().Int(1, 1).Int(2, 1).String(3, "x").Int(4, 9).ToArray();
        var fixture = WriteRaw("1.2.0", "trade.offer", 2, 1, payload);
        var r = MessageCompat.Replay<OfferV2>(MessageFixtures.Load(_dir));
        Assert.True(r.Ok, r.ToString());
        Assert.Contains(r.Notes, n => n.Contains("unknown enum value"));
        _ = fixture;
    }

    [Fact]
    public void A_malformed_payload_fails_the_replay()
    {
        WriteRaw("1.0.0", "trade.offer", 1, 1, new byte[] { 0x0A, 0x05, 0x01 });
        var r = MessageCompat.Replay<OfferV1>(MessageFixtures.Load(_dir));
        Assert.Contains(r.Failures, f => f.Contains("could not be decoded"));
    }

    [Fact]
    public void Unknown_fields_are_skipped_by_every_message_type()
    {
        MessageCompat.AssertSkipsUnknownFields(new OfferV2 { ItemId = 1, Count = 2, Note = "n", Mood = Mood.Eager });
        MessageCompat.AssertSkipsUnknownFields(new Tick { Values = { 1, 2, 3 } }, seed: 7);
        byte[] with = MessageCompat.WithUnknownFields(MessageCompat.Encode(new OfferV1 { ItemId = 3 }));
        Assert.Equal(3, MessageCompat.Decode<OfferV1>(with).ItemId);
    }

    [Fact]
    public void ReplayAll_fails_a_fixture_no_type_reads()
    {
        var fixtures = Record();
        Assert.True(MessageCompat.ReplayAll(fixtures, new[] { typeof(OfferV2) }).Ok);
        WriteRaw("1.0.0", "trade.gone", 1, 1, Array.Empty<byte>());
        var r = MessageCompat.ReplayAll(MessageFixtures.Load(_dir), new[] { typeof(OfferV2), typeof(Tick) });
        Assert.Contains(r.Failures, f => f.Contains("no [ModMessage] type here reads 'trade.gone'"));
    }

    [Fact]
    public void Two_types_for_one_name_are_refused()
    {
        Assert.Throws<MessageCompatException>(() => MessageCompat.ReplayAll(Record(), new[] { typeof(OfferV1), typeof(OfferV2) }));
    }

    [Fact]
    public void CheckRange_replays_the_versions_inside_and_skips_the_rest()
    {
        var fixtures = Record();
        var r = MessageCompat.CheckRange(">=1.1.0 <2.0.0", "1.2.0", fixtures, new[] { typeof(OfferV2) });
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(new[] { "1.1.0" }, r.Versions.ToArray());
        Assert.Equal(new[] { "1.1.0" }, r.OtherVersions.ToArray());
        Assert.Contains(r.Notes, n => n.Contains("1.0.0 is outside"));
        Assert.True(MessageCompat.CheckRange(">=1.0.0 <2.0.0", "1.1.0", fixtures, new[] { typeof(OfferV2) }).Ok);
    }

    [Fact]
    public void CheckRange_fails_with_no_recorded_version_inside()
    {
        var r = MessageCompat.CheckRange(">=3.0.0", "3.0.0", Record(), new[] { typeof(OfferV2) });
        Assert.Contains(r.Failures, f => f.Contains("no recorded fixture comes from a version inside"));
        Assert.False(MessageCompat.CheckRange("not a range", "1.0.0", Record(), new[] { typeof(OfferV2) }).Ok);
    }

    [Fact]
    public void CheckRange_backed_only_by_the_build_under_test_is_not_proven()
    {
        MessageFixtures.Save(_dir, Mod, "1.1.0", new OfferV2 { ItemId = 8, Count = 4 });
        var fixtures = MessageFixtures.Load(_dir);
        var r = MessageCompat.CheckRange(">=1.0.0 <2.0.0", "1.1.0", fixtures, new[] { typeof(OfferV2) });
        Assert.Contains(r.Failures, f => f.Contains("not proven"));
        Assert.Empty(r.OtherVersions);
        var first = MessageCompat.CheckRange(">=1.0.0 <2.0.0", "1.1.0", fixtures, new[] { typeof(OfferV2) }, allowSelfOnly: true);
        Assert.True(first.Ok, first.ToString());
        Assert.Contains(first.Notes, n => n.Contains("not proven") && n.Contains("first release"));
    }

    [Fact]
    public void CheckRange_fails_a_breaking_type_against_an_older_peer_in_the_range()
    {
        var r = MessageCompat.CheckRange(">=1.0.0 <3.0.0", "2.0.0", Record(), new[] { typeof(OfferV3Breaking) });
        Assert.Contains(r.Failures, f => f.Contains("needs reader 3") && f.Contains("drops what this build sends"));
    }

    [Fact]
    public void CheckRange_fails_a_newer_breaking_peer_inside_the_range()
    {
        MessageFixtures.Save(_dir, Mod, "2.0.0", new OfferV3Breaking { ItemId = 1, Total = 5 });
        var r = MessageCompat.CheckRange(">=1.0.0 <3.0.0", "1.1.0", Record(), new[] { typeof(OfferV2) });
        Assert.Contains(r.Failures, f => f.Contains("2.0.0") && f.Contains("drops it"));
    }

    [Fact]
    public void CheckRange_fails_a_field_an_older_version_in_the_range_still_sends_and_reads()
    {
        var fixtures = Record();
        var r = MessageCompat.CheckRange(">=1.0.0 <2.0.0", "1.2.0", fixtures, new[] { typeof(OfferRemoved) });
        Assert.Contains(r.Failures, f => f.Contains("field 2") && f.Contains("major change"));
        // Outside a range check an older fixture's extra field is only a note: Replay does not know which versions count.
        var replay = MessageCompat.Replay<OfferRemoved>(fixtures);
        Assert.True(replay.Ok, replay.ToString());
        Assert.Contains(replay.Notes, n => n.Contains("field 2") && n.Contains("missing from OfferRemoved"));
    }

    [Fact]
    public void CheckRange_names_a_renumbered_field()
    {
        var r = MessageCompat.CheckRange(">=1.0.0 <2.0.0", "1.2.0", Record(), new[] { typeof(OfferRenumbered) });
        Assert.Contains(r.Failures, f => f.Contains("Count is field 7 now: renumbered"));
    }

    [Fact]
    public void An_int_read_as_uint_is_a_retype_although_the_varint_survives()
    {
        MessageFixtures.Save(_dir, Mod, "1.0.0", new OfferV1 { ItemId = 1, Count = -5 });
        var r = MessageCompat.Replay<OfferUnsigned>(MessageFixtures.Load(_dir));
        Assert.Contains(r.Failures, f => f.Contains("field 2") && f.Contains("was int") && f.Contains("is uint"));
    }

    [Fact]
    public void An_int_read_as_an_enum_is_a_retype()
    {
        MessageFixtures.Save(_dir, Mod, "1.0.0", new OfferV1 { ItemId = 1, Count = 1 });
        var r = MessageCompat.Replay<OfferEnumCount>(MessageFixtures.Load(_dir));
        Assert.Contains(r.Failures, f => f.Contains("field 2") && f.Contains("was int") && f.Contains("enum:int"));
    }

    [Fact]
    public void A_string_read_as_bytes_is_a_retype()
    {
        MessageFixtures.Save(_dir, Mod, "1.1.0", new OfferV2 { ItemId = 1, Count = 1, Note = "hi" });
        var r = MessageCompat.Replay<OfferNoteBytes>(MessageFixtures.Load(_dir));
        Assert.Contains(r.Failures, f => f.Contains("field 3") && f.Contains("was string") && f.Contains("is bytes"));
    }

    [Fact]
    public void A_renumbered_field_inside_a_nested_message_fails_the_range()
    {
        MessageFixtures.Save(_dir, Mod, "1.0.0", new BasketV1 { Item = new ItemV1 { Id = 3, Count = 9 } });
        var fixtures = MessageFixtures.Load(_dir);
        var r = MessageCompat.CheckRange(">=1.0.0 <2.0.0", "1.1.0", fixtures, new[] { typeof(BasketRenumbered) });
        Assert.Contains(r.Failures, f => f.Contains("field 1.2") && f.Contains("renumbered"));
        var ok = MessageCompat.CheckRange(">=1.0.0 <2.0.0", "1.1.0", fixtures, new[] { typeof(BasketV2) });
        Assert.True(ok.Ok, ok.ToString());
    }

    [Fact]
    public void A_retyped_field_inside_a_nested_message_fails_the_replay()
    {
        MessageFixtures.Save(_dir, Mod, "1.0.0", new BasketV1 { Item = new ItemV1 { Id = 3, Count = 9 } });
        var r = MessageCompat.Replay<BasketRetyped>(MessageFixtures.Load(_dir));
        Assert.Contains(r.Failures, f => f.Contains("field 1.2") && f.Contains("was int") && f.Contains("is string"));
    }

    [Fact]
    public void A_fixture_without_a_schema_is_checked_by_its_wire_types_and_says_so()
    {
        WriteRaw("1.0.0", "trade.offer", 1, 1, new MessageWriter().Int(1, 1).Int(2, -5).ToArray());
        var r = MessageCompat.Replay<OfferUnsigned>(MessageFixtures.Load(_dir));
        Assert.True(r.Ok, r.ToString());
        Assert.Contains(r.Notes, n => n.Contains("no field schema recorded"));
    }

    [Fact]
    public void Saved_fixtures_carry_the_field_schema()
    {
        var f = MessageFixtures.Save(_dir, Mod, "1.0.0", new BasketV1 { Item = new ItemV1 { Id = 1 } });
        Assert.True(f.HasSchema);
        string json = File.ReadAllText(Path.ChangeExtension(f.Path, ".json"));
        Assert.Contains("\"type\": \"message\"", json);
        Assert.Contains("\"name\": \"Count\"", json);
        Assert.True(MessageFixtures.Load(_dir).All[0].HasSchema);
    }

    [Fact]
    public void SaveRelease_never_rewrites_a_released_version()
    {
        var saved = MessageFixtures.SaveRelease(_dir, Mod, "1.0.0", new object[] { new OfferV1 { ItemId = 1 }, new Tick { Values = { 1 } } },
            m => m is Tick ? "to-client" : "to-server");
        Assert.Equal(2, saved.Count);
        Assert.Equal("to-client", saved[1].Direction);
        var e = Assert.Throws<MessageCompatException>(() => MessageFixtures.SaveRelease(_dir, Mod, "1.0.0", new object[] { new OfferV1 { ItemId = 2 } }));
        Assert.Contains("never rewritten", e.Message);
        Assert.Equal(2, MessageFixtures.Load(_dir).All.Count);
    }

    [Fact]
    public void A_hand_written_message_replays_through_the_reader()
    {
        WriteRaw("1.0.0", "trade.tick", 1, 1, new MessageWriter().Int(1, 4).Int(1, 5).ToArray());
        var got = new List<int>();
        var r = MessageCompat.Replay("trade.tick", MessageFixtures.Load(_dir), (reader, f) => got.AddRange(reader.Ints(1)));
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(new[] { 4, 5, 4, 5 }, got); // once plain, once with unknown fields added
    }

    [Fact]
    public void MessageTypes_finds_every_message_class_in_an_assembly()
    {
        var types = MessageTypes.In(typeof(OfferV1).Assembly, typeof(OfferV1).Namespace);
        Assert.Contains(typeof(OfferV1), types);
        Assert.Contains(typeof(Tick), types);
        Assert.DoesNotContain(typeof(MessageCompatTests), types);
    }

    [Fact]
    public void A_message_over_the_envelope_limit_is_refused_as_the_game_would()
    {
        var big = new OfferV2 { Note = new string('x', 5000) };
        Assert.Throws<MessageCompatException>(() => MessageCompat.Encode(big));
    }

    private string WriteRaw(string modVersion, string message, int version, int minReader, byte[] payload)
    {
        string folder = Path.Combine(_dir, modVersion, message + "@v" + version);
        Directory.CreateDirectory(folder);
        string stem = Path.Combine(folder, "raw" + Guid.NewGuid().ToString("n").Substring(0, 6));
        File.WriteAllBytes(stem + ".bin", payload);
        File.WriteAllText(stem + ".json",
            "{\"format\":1,\"mod\":\"" + Mod + "\",\"modVersion\":\"" + modVersion + "\",\"message\":\"" + message + "\",\"version\":" + version
            + ",\"minReader\":" + minReader + ",\"direction\":\"any\",\"source\":\"test\"}");
        return stem + ".bin";
    }
}
