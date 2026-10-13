using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C2 (docs/mod-compatibility.md, section 3): the tagged binary codec. Every supported type, nested
/// classes and lists, unknown fields skipped, missing fields defaulted, unknown enum values, the size limits, malformed
/// input, and the hand-written reader and writer against the reflected codec.
/// </summary>
public class ModMessageCodecTests
{
    public enum Color { Red = 1, Green = 2 }

    [Flags]
    public enum Bits { None = 0, A = 1, B = 2 }

    public enum Wide : ulong { Max = ulong.MaxValue }

    public sealed class Inner
    {
        [Field(1)] public int X;
        [Field(2)] public string Label = "inner-default";
    }

    public struct FakeGuid
    {
        public int Hash;
    }

    [ModMessage("every.type", Version = 1)]
    public sealed class Everything
    {
        [Field(1)] public bool Flag;
        [Field(2)] public sbyte S8;
        [Field(3)] public byte U8;
        [Field(4)] public short S16;
        [Field(5)] public ushort U16;
        [Field(6)] public int S32;
        [Field(7)] public uint U32;
        [Field(8)] public long S64;
        [Field(9)] public ulong U64;
        [Field(10)] public float F32;
        [Field(11)] public double F64;
        [Field(12)] public string Text = "";
        [Field(13)] public byte[]? Blob;
        [Field(14)] public Color Color = Color.Red;
        [Field(15)] public Bits Bits;
        [Field(16)] public Inner? Nested;
        [Field(17)] public List<int> Ints = new();
        [Field(18)] public string[] Words = Array.Empty<string>();
        [Field(19)] public List<Inner> Inners = new();
        [Field(20)] public char Letter;
        [Field(21)] public Wide Wide;
        [Field(22)] public int Prop { get; set; }
        [Field(23)] public FakeGuid Guid;
    }

    [ModMessage("versioned", Version = 1)]
    public sealed class V1
    {
        [Field(1)] public int Seq;
        [Field(2)] public string Text = "";
    }

    [ModMessage("versioned", Version = 2)]
    public sealed class V2
    {
        [Field(1)] public int Seq;
        [Field(2)] public string Text = "";
        [Field(3, Since = 2)] public string Note = "note-default";
        [Field(4, Since = 2)] public List<int> Extra = new() { 9 };
        [Field(5, Since = 2)] public Inner Child = new() { X = -1 };
    }

    private static T RoundTrip<T>(T value) where T : class
    {
        var schema = MessageSchema.For(typeof(T));
        return (T)schema.Decode(schema.Encode(value));
    }

    // Everything carries a registered scalar (the role PrefabGUID plays in the game). xUnit builds a new instance per test.
    public ModMessageCodecTests() =>
        MessageSchema.RegisterScalar(typeof(FakeGuid), o => ((FakeGuid)o).Hash, v => new FakeGuid { Hash = (int)v });

    [Fact]
    public void Every_type_round_trips()
    {
        MessageSchema.RegisterScalar(typeof(FakeGuid), o => ((FakeGuid)o).Hash, v => new FakeGuid { Hash = (int)v });
        try
        {
            var m = new Everything
            {
                Flag = true, S8 = -5, U8 = 250, S16 = -30000, U16 = 65000, S32 = int.MinValue, U32 = uint.MaxValue,
                S64 = long.MinValue, U64 = ulong.MaxValue, F32 = 1.5f, F64 = -2.25e100, Text = "héllo ✓", Blob = new byte[] { 0, 1, 255 },
                Color = Color.Green, Bits = Bits.A | Bits.B, Nested = new Inner { X = 42, Label = "n" }, Ints = new() { -1, 0, 1, int.MaxValue },
                Words = new[] { "a", "", "c" }, Inners = new() { new Inner { X = 1 }, new Inner { X = 2, Label = "" } }, Letter = 'Z', Wide = Wide.Max,
                Prop = 77, Guid = new FakeGuid { Hash = -1234567 },
            };
            var b = RoundTrip(m);
            Assert.True(b.Flag);
            Assert.Equal(-5, b.S8);
            Assert.Equal(250, b.U8);
            Assert.Equal(-30000, b.S16);
            Assert.Equal(65000, b.U16);
            Assert.Equal(int.MinValue, b.S32);
            Assert.Equal(uint.MaxValue, b.U32);
            Assert.Equal(long.MinValue, b.S64);
            Assert.Equal(ulong.MaxValue, b.U64);
            Assert.Equal(1.5f, b.F32);
            Assert.Equal(-2.25e100, b.F64);
            Assert.Equal("héllo ✓", b.Text);
            Assert.Equal(new byte[] { 0, 1, 255 }, b.Blob);
            Assert.Equal(Color.Green, b.Color);
            Assert.Equal(Bits.A | Bits.B, b.Bits);
            Assert.Equal(42, b.Nested!.X);
            Assert.Equal("n", b.Nested.Label);
            Assert.Equal(new[] { -1, 0, 1, int.MaxValue }, b.Ints);
            Assert.Equal(new[] { "a", "", "c" }, b.Words);
            Assert.Equal(2, b.Inners.Count);
            Assert.Equal("inner-default", b.Inners[0].Label);
            Assert.Equal("", b.Inners[1].Label);
            Assert.Equal('Z', b.Letter);
            Assert.Equal(Wide.Max, b.Wide);
            Assert.Equal(77, b.Prop);
            Assert.Equal(-1234567, b.Guid.Hash);
        }
        finally { MessageSchema.UnregisterScalar(typeof(FakeGuid)); }
    }

    [Fact]
    public void An_unregistered_struct_is_not_a_field_type()
    {
        MessageSchema.UnregisterScalar(typeof(FakeGuid));
        var e = Assert.Throws<InvalidOperationException>(() => MessageSchema.For(typeof(Everything)));
        Assert.Contains("FakeGuid", e.Message);
    }

    [Fact]
    public void A_newer_writer_s_unknown_fields_are_skipped_by_an_older_reader()
    {
        var v2 = new V2 { Seq = 7, Text = "hi", Note = "added in v2", Extra = new() { 1, 2 }, Child = new Inner { X = 5 } };
        byte[] wire = MessageSchema.For(typeof(V2)).Encode(v2);
        var v1 = (V1)MessageSchema.For(typeof(V1)).Decode(wire);
        Assert.Equal(7, v1.Seq);
        Assert.Equal("hi", v1.Text);
    }

    [Fact]
    public void An_older_writer_s_missing_fields_keep_the_newer_reader_s_initializers()
    {
        byte[] wire = MessageSchema.For(typeof(V1)).Encode(new V1 { Seq = 3, Text = "old" });
        var v2 = (V2)MessageSchema.For(typeof(V2)).Decode(wire);
        Assert.Equal(3, v2.Seq);
        Assert.Equal("old", v2.Text);
        Assert.Equal("note-default", v2.Note);
        Assert.Equal(new[] { 9 }, v2.Extra);
        Assert.Equal(-1, v2.Child.X);
    }

    [Fact]
    public void Default_and_null_values_still_decode_and_null_references_are_omitted()
    {
        var m = new Everything { Text = null!, Blob = null, Nested = null, Words = null! };
        byte[] wire = MessageSchema.For(typeof(Everything)).Encode(m);
        var b = (Everything)MessageSchema.For(typeof(Everything)).Decode(wire);
        Assert.Equal("", b.Text);
        Assert.Null(b.Blob);
        Assert.Null(b.Nested);
        Assert.Empty(b.Words);
    }

    [Fact]
    public void A_known_number_with_another_wire_type_is_skipped_not_misread()
    {
        // Field 1 written as a string where V1 expects a varint: V1.Seq keeps its default, field 2 still reads.
        byte[] wire = new MessageWriter().String(1, "retyped").String(2, "ok").ToArray();
        var v1 = (V1)MessageSchema.For(typeof(V1)).Decode(wire);
        Assert.Equal(0, v1.Seq);
        Assert.Equal("ok", v1.Text);
    }

    public enum Small { One = 1 }

    public sealed class WithEnum
    {
        [Field(1)] public Small Value = Small.One;
        [Field(2)] public int After;
    }

    [Fact]
    public void An_unknown_enum_value_keeps_the_default_and_is_reported()
    {
        var reports = new List<string>();
        var schema = MessageSchema.For(typeof(WithEnum));
        schema.UnknownEnum = reports.Add;
        byte[] wire = new MessageWriter().Int(1, 99).Int(2, 5).ToArray();
        var m = (WithEnum)schema.Decode(wire);
        Assert.Equal(Small.One, m.Value);
        Assert.Equal(5, m.After);
        Assert.Single(reports);
        Assert.Contains("99", reports[0]);
    }

    public sealed class Loop
    {
        [Field(1)] public int Depth;
        [Field(2)] public Loop? Next;
    }

    [Fact]
    public void A_self_referencing_type_builds_and_nesting_is_capped()
    {
        var root = new Loop();
        var cur = root;
        for (int i = 1; i <= 10; i++) cur = cur.Next = new Loop { Depth = i };
        var back = RoundTrip(root);
        Assert.Equal(10, back.Next!.Next!.Next!.Next!.Next!.Next!.Next!.Next!.Next!.Next!.Depth);

        var deep = new Loop();
        cur = deep;
        for (int i = 1; i <= 30; i++) cur = cur.Next = new Loop { Depth = i };
        Assert.Throws<InvalidOperationException>(() => MessageSchema.For(typeof(Loop)).Encode(deep));
    }

    [Fact]
    public void A_string_over_4_KiB_is_refused_on_both_sides()
    {
        var m = new V1 { Text = new string('x', 4097) };
        Assert.Throws<MessageTooLargeException>(() => MessageSchema.For(typeof(V1)).Encode(m));
        Assert.Throws<ArgumentException>(() => new MessageWriter().String(2, new string('x', 4097)));
        var ok = MessageSchema.For(typeof(V1)).Encode(new V1 { Text = new string('x', 4096) });
        Assert.Equal(4096, ((V1)MessageSchema.For(typeof(V1)).Decode(ok)).Text.Length);

        // A hostile peer's 5000-byte string: the message does not decode.
        byte[] big = new MessageWriter().Bytes(2, new byte[5000]).ToArray();
        Assert.Throws<FormatException>(() => MessageSchema.For(typeof(V1)).Decode(big));
    }

    [Fact]
    public void The_envelope_payload_limit_is_one_packet_minus_the_16_byte_header()
    {
        Assert.Equal(16, ModMessageEnvelope.HeaderBytes);
        Assert.Equal(15 * 1024 - 16, ModMessageEnvelope.MaxPayloadBytes);
        Assert.Throws<MessageTooLargeException>(() => ModMessageEnvelope.Pack(1, 2, 1, 1, new byte[ModMessageEnvelope.MaxPayloadBytes + 1]));
        byte[] p = ModMessageEnvelope.Pack(1, 2, 1, 1, new byte[ModMessageEnvelope.MaxPayloadBytes]);
        Assert.Equal(15 * 1024, p.Length);
    }

    [Theory]
    [InlineData(new byte[] { 0x08 })]
    [InlineData(new byte[] { 0x12, 0x05, 0x01 })]
    [InlineData(new byte[] { 0x0B, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x00 })]
    [InlineData(new byte[] { 0x09, 1, 2, 3 })]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80 })]
    public void A_malformed_payload_is_a_format_error(byte[] wire)
    {
        Assert.Throws<FormatException>(() => MessageSchema.For(typeof(V1)).Decode(wire));
    }

    [Fact]
    public void Invalid_string_utf8_is_a_format_error()
    {
        byte[] wire = new MessageWriter().Bytes(2, new byte[] { 0xC3, 0x28 }).ToArray();
        Assert.Throws<FormatException>(() => MessageSchema.For(typeof(V1)).Decode(wire));
    }

    public sealed class Dup
    {
        [Field(1)] public int A;
        [Field(1)] public int B;
    }

    public sealed class Ro
    {
        [Field(1)] public readonly int A;
    }

    public sealed class NoCtor
    {
        public NoCtor(int x) => X = x;
        [Field(1)] public int X;
    }

    public sealed class ListOfLists
    {
        [Field(1)] public List<List<int>> X = new();
    }

    public sealed class ObjField
    {
        [Field(1)] public object? X;
    }

    [Theory]
    [InlineData(typeof(Dup))]
    [InlineData(typeof(Ro))]
    [InlineData(typeof(NoCtor))]
    [InlineData(typeof(ListOfLists))]
    [InlineData(typeof(ObjField))]
    public void An_invalid_message_type_is_refused_when_its_schema_is_built(Type type)
    {
        Assert.Throws<InvalidOperationException>(() => MessageSchema.For(type));
    }

    [Fact]
    public void The_hand_written_codec_and_the_reflected_one_read_each_other()
    {
        var w = new MessageWriter().Int(1, -42).String(2, "hand").String(3, "note").Int(4, 1).Int(4, 2)
            .Message(5, new MessageWriter().Int(1, 8)).Double(99, 1.5).Bool(100, true).UInt(101, 7).Float(102, 2.5f).Long(103, -1L << 40);
        var v2 = (V2)MessageSchema.For(typeof(V2)).Decode(w.ToArray());
        Assert.Equal(-42, v2.Seq);
        Assert.Equal("hand", v2.Text);
        Assert.Equal("note", v2.Note);
        Assert.Equal(new[] { 1, 2 }, v2.Extra);
        Assert.Equal(8, v2.Child.X);

        var r = new MessageReader(w.ToArray());
        Assert.Equal(-42, r.Int(1));
        Assert.Equal("hand", r.String(2));
        Assert.Equal(2, r.Int(4));
        Assert.Equal(new[] { 1, 2 }, r.Ints(4));
        Assert.Equal(8, r.Message(5)!.Int(1));
        Assert.Equal(1.5, r.Double(99));
        Assert.True(r.Bool(100));
        Assert.Equal(7u, r.UInt(101));
        Assert.Equal(2.5f, r.Float(102));
        Assert.Equal(-1L << 40, r.Long(103));
        Assert.Equal("fallback", r.String(50, "fallback"));
        Assert.Equal(11, r.Int(2, 11));
        Assert.False(r.Has(50));
        Assert.Null(r.Message(50));

        var back = new MessageReader(MessageSchema.For(typeof(V2)).Encode(new V2 { Seq = 1, Text = "t" }));
        Assert.Equal(1, back.Int(1));
        Assert.Equal(new[] { 9 }, back.Ints(4));
        Assert.Equal(new[] { "t" }, back.Strings(2));
    }

    [Fact]
    public void The_writer_refuses_field_numbers_out_of_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MessageWriter().Int(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MessageWriter().Int(1 << 29, 1));
    }

    [Fact]
    public void Zigzag_and_varint_cover_the_extremes()
    {
        foreach (long v in new[] { 0L, -1L, 1L, long.MinValue, long.MaxValue, int.MinValue })
            Assert.Equal(v, WireFormat.UnZigZag(WireFormat.ZigZag(v)));
        var r = new MessageReader(new MessageWriter().UInt64(1, ulong.MaxValue).Long(2, long.MinValue).ToArray());
        Assert.Equal(ulong.MaxValue, r.UInt64(1));
        Assert.Equal(long.MinValue, r.Long(2));
    }

    [Fact]
    public void The_attribute_carries_name_version_and_min_reader()
    {
        var a = MessageSchema.For(typeof(V2)).Message!;
        Assert.Equal("versioned", a.Name);
        Assert.Equal(2, a.Version);
        Assert.Equal(1, a.MinReaderVersion);
        Assert.Null(MessageSchema.For(typeof(Inner)).Message);
    }
}
