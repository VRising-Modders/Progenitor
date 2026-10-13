using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Progenitor;
using Progenitor.Ai;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Core's ally-safe hit default, game-free half (<see cref="AllySafetyCodec"/>,
/// <see cref="AllySafetyRules"/>). The prepend is the one applied by hand on the isolated server on
/// 2026-09-24 to Frostmaw Ice Beam and Bishop of Dunley.
/// The walk rule and the
/// element layout are the verified ones: i = 0; r = check(i); i = r ? Success : Failure while i is
/// inside the array; result = last r.
/// </summary>
public class AllySafetyTests
{
    private const int Stride = 0x28;

    /// <summary>
    /// AB_Wendigo_MeleeAttack_Hit's hit condition, captured live 2026-09-24
    /// (scale/melee-hit-condition.hex): 17 elements, element 0 is NOT IsAlly(Target), two BlobStrings.
    /// </summary>
    private const string MeleeHex =
        "2000000011000000c00200001b000000d3020000220000000000000000000000080000000100000002000000000000000100000001000000000000000000000000000000000000000a00000003000000020000000000000001000000000000000000000000000000000000000000000028000000030000000a0000000000000001000000000000000000000700000000000000000000000009000000040000000a0000000000000001000000000000000000000000000000000000000000000007000000050000000a000000000000000100000001000000a0259e0400000000000000000000000007000000060000000a00000000000000010000000100000066f2117000000000000000000000000046000000070000000a000000000000000100000001000000000000000000000000000000000000002800000009000000080000000000000001000000010000000100000300000000000000000000000035000000090000000a0000000000000001000000010000000100000300000000000000000000000035000000110000000a00000000000000010000000100000005000003000000000000000000000000090000000b0000001100000000000000010000000000000000000000000000000000000000000000070000000c00000011000000000000000100000001000000a0259e04000000000000000000000000070000000d0000001100000000000000010000000100000066f21170000000000000000000000000460000000e0000001100000000000000010000000100000000000000000000000000000000000000060000000f00000011000000000000000100000000000000000a00000000000000000000000000004600000010000000110000000000000001000000010000000000000000000000000000000000000007000000110000001100000000000000010000000100000062d199ea00000000000000000000000041425f57656e6469676f5f4d656c656541747461636b5f4869740043726561746547616d65706c61794576656e744f6e486974417574686f72696e67000000000000000000000000";

    private const long FakePointer = 0x1C6986EFE80L;

    // ---- synthetic blobs ----------------------------------------------------------------------

    /// <summary>
    /// A condition blob shaped like the game's: Conditionals {offset, count} at 0, a ConditionInfo
    /// with one BlobString at 0x8, the element array at 0x20, then the string bytes.
    /// </summary>
    private static byte[] Blob(params (int Source, int Success, int Failure)[] elements)
    {
        const int arrayAt = 0x20;
        byte[] text = Encoding.ASCII.GetBytes("AB_Test_Hit\0");
        int stringAt = arrayAt + elements.Length * Stride;
        var data = new byte[stringAt + text.Length + 3];
        PutI32(data, 0, arrayAt);
        PutI32(data, 4, elements.Length);
        PutI32(data, 0x8, stringAt - 0x8);
        PutI32(data, 0xC, text.Length);
        for (int i = 0; i < elements.Length; i++)
        {
            int at = arrayAt + i * Stride;
            PutI32(data, at, elements[i].Source);
            PutI32(data, at + 4, elements[i].Success);
            PutI32(data, at + 8, elements[i].Failure);
            PutI32(data, at + 0x10, 0x11 + i); // union payload, to prove it is copied verbatim
            PutI32(data, at + 0x14, 0x22 + i);
        }
        text.CopyTo(data, stringAt);
        return data;
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);
    private static void PutI32(byte[] b, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at, 4), v);
    private static int I32(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at, 4));
    private static long I64(byte[] b, int at) => BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(at, 8));
    private static int ArrayStart(byte[] b) => I32(b, 0);
    private static int Count(byte[] b) => I32(b, 4);

    private static string BlobString(byte[] blob, int field)
    {
        int at = field + I32(blob, field);
        int len = I32(blob, field + 4);
        return Encoding.ASCII.GetString(blob, at, Math.Max(0, len - 1));
    }

    // A team-blind condition like the Ice Beam channel's, with a forward success, a forward failure
    // and terminal indices both equal to and past the count.
    private static byte[] Blind() => Blob((7, 1, 3), (10, 2, 7), (9, 3, 3));

    // ---- layout ---------------------------------------------------------------------------------

    [Fact]
    public void Strides_and_offsets_are_the_verified_ones()
    {
        Assert.Equal(0x20, AllySafetyCodec.HitRowStride);
        Assert.Equal(0x10, Layout.CreateGameplayEventsOnHitConditionOffset);
        Assert.Equal(Stride, ConditionCodec.Stride);
        Assert.Equal(0x4, Layout.ConditionElementSuccessOffset);
        Assert.Equal(8, Layout.ConditionSourceIsAlly);
        Assert.Equal(1, Layout.ConditionTargetTarget);
        Assert.Equal(0x20, AllySafetyCodec.FreshHeaderBytes);
        Assert.Equal(7, Layout.ConditionSourceHasBuff);
        Assert.Equal(5, Layout.ConditionTargetMinionMaster);
        Assert.Equal(0, Layout.ConditionTargetOwner);
        Assert.Equal(0x8, Layout.TargetPrefabConditionPrefabOffset);
        Assert.Equal(0x10, Layout.BlobAssetHeaderHashOffset);
    }

    // ---- which hit slots deal damage -----------------------------------------------------------------

    /// <summary>Frostmaw Ice Beam channel's hit slot EventId, live 2026-09-24: bytes 00000000E09AE9F6.</summary>
    private static readonly byte[] IceBeamEventId = Hex("00000000E09AE9F6");

    private static byte[] HitRow(byte[] eventId)
    {
        var row = new byte[AllySafetyCodec.HitRowStride];
        eventId.CopyTo(row, 0);
        return row;
    }

    private static byte[] Listeners(params (int EventType, byte[] EventId)[] rows)
    {
        int stride = AllySafetyCodec.ListenerStride;
        var data = new byte[rows.Length * stride];
        for (int i = 0; i < rows.Length; i++)
        {
            PutI32(data, i * stride, i);                // EventIdIndex
            PutI32(data, i * stride + 0x10, rows[i].EventType);
            rows[i].EventId.CopyTo(data, i * stride + 0x14);
        }
        return data;
    }

    private static byte[] EventId(int type, int id)
    {
        var b = new byte[8];
        PutI32(b, 0, type);
        PutI32(b, 4, id);
        return b;
    }

    [Fact]
    public void Listener_layout_is_the_dump_one()
    {
        Assert.Equal(0x20, AllySafetyCodec.ListenerStride);
        Assert.Equal(0x10, Layout.GameplayEventListenersEventTypeOffset);
        Assert.Equal(0x14, Layout.GameplayEventListenersEventIdOffset);
        Assert.Equal(0x0, Layout.CreateGameplayEventsOnHitEventIdOffset);
        Assert.Equal(23, Layout.GameplayEventTypeEnumDealDamage);
        Assert.Equal((0, unchecked((int)0xF6E99AE0)), AllySafetyCodec.ReadEventId(IceBeamEventId, 0));
    }

    [Fact]
    public void A_slot_is_damaging_when_a_DealDamage_listener_hears_its_event()
    {
        var listeners = Listeners((1, EventId(0, 7)), (23, IceBeamEventId), (12, EventId(0, 9)));
        var damage = AllySafetyCodec.DamageEvents(listeners, 3);
        Assert.Single(damage);
        Assert.True(AllySafetyCodec.IsDamagingSlot(HitRow(IceBeamEventId), damage));
    }

    [Fact]
    public void A_slot_whose_event_only_buffs_or_plays_effects_is_not_damaging()
    {
        var listeners = Listeners((1, IceBeamEventId), (11, IceBeamEventId), (23, EventId(0, 42)));
        var damage = AllySafetyCodec.DamageEvents(listeners, 3);
        Assert.False(AllySafetyCodec.IsDamagingSlot(HitRow(IceBeamEventId), damage));
        Assert.True(AllySafetyCodec.IsDamagingSlot(HitRow(EventId(0, 42)), damage));
    }

    [Fact]
    public void Both_event_id_fields_must_match()
    {
        // Same id, different GameplayEventType (External vs Local): not the same event.
        var damage = AllySafetyCodec.DamageEvents(Listeners((23, EventId(1, unchecked((int)0xF6E99AE0)))), 1);
        Assert.False(AllySafetyCodec.IsDamagingSlot(HitRow(IceBeamEventId), damage));
    }

    [Fact]
    public void Chains_through_branch_delay_or_iterate_are_not_followed()
    {
        // 20 Branch, 26 Delay, 21 Iterate: the slot's event reaches a listener that is not DealDamage.
        var listeners = Listeners((20, IceBeamEventId), (26, IceBeamEventId), (21, IceBeamEventId), (23, EventId(0, 5)));
        Assert.False(AllySafetyCodec.IsDamagingSlot(HitRow(IceBeamEventId), AllySafetyCodec.DamageEvents(listeners, 4)));
    }

    [Fact]
    public void No_listeners_or_a_short_buffer_means_no_damage()
    {
        Assert.Empty(AllySafetyCodec.DamageEvents(Array.Empty<byte>(), 0));
        var listeners = Listeners((23, IceBeamEventId));
        Assert.Empty(AllySafetyCodec.DamageEvents(listeners.AsSpan(0, 0x18), 1)); // count past the bytes is ignored
    }

    // ---- classification ---------------------------------------------------------------------------

    [Fact]
    public void Frostmaw_melee_is_team_aware()
    {
        var melee = Hex(MeleeHex);
        Assert.Equal(HitSlotKind.TeamAware, AllySafetyCodec.Classify(false, melee));
        Assert.False(AllySafetyCodec.TryGuard(melee, out _)); // starts with NOT IsAlly, but it is vanilla
    }

    [Theory]
    [InlineData(8)]
    [InlineData(41)]
    [InlineData(66)]
    [InlineData(89)]
    public void Any_team_source_makes_a_slot_team_aware(int source)
    {
        Assert.Equal(HitSlotKind.TeamAware, AllySafetyCodec.Classify(false, Blob((7, 1, 2), (source, 2, 2))));
    }

    [Fact]
    public void A_condition_without_a_team_source_is_team_blind()
    {
        Assert.Equal(HitSlotKind.TeamBlind, AllySafetyCodec.Classify(false, Blind()));
        Assert.Equal(HitSlotKind.TeamBlind, AllySafetyCodec.Classify(false, Blob())); // 0 elements: always true
    }

    [Fact]
    public void Null_and_unreadable_conditions_are_told_apart()
    {
        Assert.Equal(HitSlotKind.NullCondition, AllySafetyCodec.Classify(true, default));
        var broken = Blind();
        PutI32(broken, 0, 0x7000); // array outside the blob
        Assert.Equal(HitSlotKind.Unreadable, AllySafetyCodec.Classify(false, broken));
    }

    // ---- the caster-scoped prepend -----------------------------------------------------------------

    private const int Marker = 0x5EED1234;

    /// <summary>
    /// One hit as the copy's walk sees it: element 0 HasBuff(Owner, marker), 1 HasBuff(MinionMaster,
    /// marker), 2 NOT IsAlly(Target), then the original chain from index 3 (or the always-true tail).
    /// </summary>
    private static Func<int, bool> Hit(bool ownerMarked, bool masterMarked, bool targetIsAlly, Func<int, bool> original, List<int>? seen = null) =>
        i =>
        {
            seen?.Add(i);
            return i switch
            {
                0 => ownerMarked,
                1 => masterMarked,
                2 => !targetIsAlly,
                _ => original(i - 3),
            };
        };

    private static IEnumerable<byte[]> Originals() => new[] { Blind(), Hex(MeleeHex), Blob((7, 1, 1)) };

    private static IEnumerable<Func<int, bool>> Masks(int count)
    {
        for (int mask = 0; mask < (1 << Math.Min(count, 10)); mask++)
        {
            int m = mask;
            yield return i => ((m >> (i % 10)) & 1) != 0;
        }
    }

    [Fact]
    public void The_scoped_guard_is_three_elements_then_the_shifted_original()
    {
        var original = Blind();
        var copy = AllySafetyCodec.Prepend(original, FakePointer, Marker);

        int arrayAt = (original.Length + 7) / 8 * 8;
        Assert.Equal(arrayAt, ArrayStart(copy));
        Assert.Equal(6, Count(copy));
        Assert.Equal(arrayAt + 6 * Stride + AllySafetyCodec.TrailerBytes, copy.Length);

        // e0 HasBuff(Owner, marker) 2/1; e1 HasBuff(MinionMaster, marker) 2/3; e2 NOT IsAlly(Target) 3/6.
        int e0 = arrayAt, e1 = arrayAt + Stride, e2 = arrayAt + 2 * Stride;
        Assert.Equal(new[] { 7, 2, 1, 0, 0, Marker }, new[] { I32(copy, e0), I32(copy, e0 + 4), I32(copy, e0 + 8), I32(copy, e0 + 0x10), (int)copy[e0 + 0x14], I32(copy, e0 + 0x18) });
        Assert.Equal(new[] { 7, 2, 3, 5, 0, Marker }, new[] { I32(copy, e1), I32(copy, e1 + 4), I32(copy, e1 + 8), I32(copy, e1 + 0x10), (int)copy[e1 + 0x14], I32(copy, e1 + 0x18) });
        Assert.Equal(new[] { 8, 3, 6, 1, 1 }, new[] { I32(copy, e2), I32(copy, e2 + 4), I32(copy, e2 + 8), I32(copy, e2 + 0x10), (int)copy[e2 + 0x14] });

        // (7, 1, 3) -> (7, 4, 6); (10, 2, 7) -> (10, 5, 6); (9, 3, 3) -> (9, 6, 6)
        int[][] expected = { new[] { 7, 4, 6 }, new[] { 10, 5, 6 }, new[] { 9, 6, 6 } };
        for (int i = 0; i < 3; i++)
        {
            int at = arrayAt + (3 + i) * Stride;
            Assert.Equal(expected[i], new[] { I32(copy, at), I32(copy, at + 4), I32(copy, at + 8) });
            Assert.Equal(0x11 + i, I32(copy, at + 0x10));
            Assert.Equal(0x22 + i, I32(copy, at + 0x14));
        }
    }

    [Fact]
    public void A_marked_caster_never_hits_an_ally()
    {
        foreach (var original in Originals())
        foreach (var check in Masks(Count(original)))
        {
            var copy = AllySafetyCodec.Prepend(original, FakePointer, Marker);
            Assert.False(AllySafetyCodec.Evaluate(copy, Hit(ownerMarked: true, masterMarked: false, targetIsAlly: true, check)));
            Assert.False(AllySafetyCodec.Evaluate(copy, Hit(ownerMarked: true, masterMarked: true, targetIsAlly: true, check)));
        }
    }

    [Fact]
    public void A_marked_caster_hitting_an_enemy_runs_the_original_chain()
    {
        foreach (var original in Originals())
        foreach (var check in Masks(Count(original)))
        {
            var copy = AllySafetyCodec.Prepend(original, FakePointer, Marker);
            bool vanilla = AllySafetyCodec.Evaluate(original, check);
            Assert.Equal(vanilla, AllySafetyCodec.Evaluate(copy, Hit(ownerMarked: true, masterMarked: false, targetIsAlly: false, check)));
        }
    }

    [Fact]
    public void A_summon_of_a_marked_master_is_guarded_like_its_master()
    {
        foreach (var original in Originals())
        foreach (var check in Masks(Count(original)))
        {
            var copy = AllySafetyCodec.Prepend(original, FakePointer, Marker);
            Assert.False(AllySafetyCodec.Evaluate(copy, Hit(ownerMarked: false, masterMarked: true, targetIsAlly: true, check)));
            Assert.Equal(AllySafetyCodec.Evaluate(original, check),
                AllySafetyCodec.Evaluate(copy, Hit(ownerMarked: false, masterMarked: true, targetIsAlly: false, check)));
        }
    }

    [Fact]
    public void An_unmarked_caster_runs_the_original_chain_unchanged_and_never_asks_about_allies()
    {
        foreach (var original in Originals())
        foreach (var check in Masks(Count(original)))
        foreach (bool ally in new[] { true, false })
        {
            var copy = AllySafetyCodec.Prepend(original, FakePointer, Marker);
            var seen = new List<int>();
            var originalSeen = new List<int>();
            bool vanilla = AllySafetyCodec.Evaluate(original, i => { originalSeen.Add(i); return check(i); });
            Assert.Equal(vanilla, AllySafetyCodec.Evaluate(copy, Hit(false, false, ally, check, seen)));
            Assert.Equal(new[] { 0, 1 }, seen.Take(2));
            Assert.DoesNotContain(2, seen);
            // The same original elements, in the same order, shifted by three.
            Assert.Equal(originalSeen.Select(i => i + 3), seen.Skip(2));
        }
    }

    [Fact]
    public void An_empty_original_gets_the_always_true_tail()
    {
        var copy = AllySafetyCodec.Prepend(Blob(), FakePointer, Marker);
        Assert.Equal(4, Count(copy));
        int tail = ArrayStart(copy) + 3 * Stride;
        // HasBuff(Target, 0) inverted, both branches terminal.
        Assert.Equal(new[] { 7, 4, 4, 1, 1, 0 }, new[] { I32(copy, tail), I32(copy, tail + 4), I32(copy, tail + 8), I32(copy, tail + 0x10), (int)copy[tail + 0x14], I32(copy, tail + 0x18) });
        Func<int, bool> noBuffZero = _ => true; // "not HasBuff(Target, 0)" is true
        Assert.True(AllySafetyCodec.Evaluate(copy, Hit(false, false, true, noBuffZero)));
        Assert.True(AllySafetyCodec.Evaluate(copy, Hit(true, false, false, noBuffZero)));
        Assert.False(AllySafetyCodec.Evaluate(copy, Hit(true, false, true, noBuffZero)));
    }

    [Fact]
    public void A_null_condition_becomes_the_guard_and_the_tail()
    {
        var fresh = AllySafetyCodec.BuildNullCondition(Marker);
        Assert.Equal(0x20, ArrayStart(fresh));
        Assert.Equal(4, Count(fresh));
        Assert.Equal(0x20 + 4 * Stride + 16, fresh.Length);
        Assert.True(fresh.AsSpan(8, 0x18).ToArray().All(b => b == 0)); // ConditionInfo: empty strings, no flags

        Assert.True(AllySafetyCodec.TryGuard(fresh, out long original, out int format, out int marker));
        Assert.Equal(0, original); // restore writes null back
        Assert.Equal(2, format);
        Assert.Equal(Marker, marker);

        Func<int, bool> noBuffZero = _ => true;
        // Vanilla null is always true; an unmarked caster still is.
        Assert.True(AllySafetyCodec.Evaluate(Array.Empty<byte>(), _ => false));
        Assert.True(AllySafetyCodec.Evaluate(fresh, Hit(false, false, true, noBuffZero)));
        Assert.True(AllySafetyCodec.Evaluate(fresh, Hit(false, false, false, noBuffZero)));
        Assert.True(AllySafetyCodec.Evaluate(fresh, Hit(true, false, false, noBuffZero)));
        Assert.False(AllySafetyCodec.Evaluate(fresh, Hit(true, false, true, noBuffZero)));
        Assert.False(AllySafetyCodec.Evaluate(fresh, Hit(false, true, true, noBuffZero)));
    }

    [Fact]
    public void Prepend_keeps_the_original_bytes_and_their_strings()
    {
        var melee = Hex(MeleeHex);
        var copy = AllySafetyCodec.Prepend(melee, FakePointer, Marker);
        // Everything but the Conditionals header is byte-identical, so self-relative strings still resolve.
        Assert.True(copy.AsSpan(8, melee.Length - 8).SequenceEqual(melee.AsSpan(8)));
        Assert.Equal("AB_Wendigo_MeleeAttack_Hit", BlobString(copy, 0x8));
        Assert.Equal("CreateGameplayEventOnHitAuthoring", BlobString(copy, 0x10));
        Assert.Equal(20, Count(copy));
    }

    [Fact]
    public void Prepend_refuses_a_negative_index()
    {
        Assert.Throws<ArgumentException>(() => AllySafetyCodec.Prepend(Blob((7, -1, 1)), FakePointer, Marker));
    }

    [Fact]
    public void A_copy_is_recognised_and_names_its_original_and_marker()
    {
        var copy = AllySafetyCodec.Prepend(Blind(), FakePointer, Marker);
        Assert.Equal(HitSlotKind.Guarded, AllySafetyCodec.Classify(false, copy));
        Assert.True(AllySafetyCodec.TryGuard(copy, out long original, out int format, out int marker));
        Assert.Equal(FakePointer, original);
        Assert.Equal(2, format);
        Assert.Equal(Marker, marker);
        Assert.Equal(AllySafetyCodec.MagicV2, I64(copy, copy.Length - 16));
    }

    /// <summary>A copy in the first, unconditional format (one NOT IsAlly element, "PGNALLY1").</summary>
    private static byte[] FirstFormatCopy(byte[] original, long pointer)
    {
        int count = Count(original), first = ArrayStart(original);
        int arrayAt = (original.Length + 7) / 8 * 8, newCount = count + 1;
        var data = new byte[arrayAt + newCount * Stride + 16];
        original.CopyTo(data, 0);
        PutI32(data, arrayAt, 8);
        PutI32(data, arrayAt + 4, 1);
        PutI32(data, arrayAt + 8, newCount);
        PutI32(data, arrayAt + 0x10, 1);
        data[arrayAt + 0x14] = 1;
        for (int i = 0; i < count; i++)
        {
            int to = arrayAt + (i + 1) * Stride;
            Array.Copy(original, first + i * Stride, data, to, Stride);
            int s = I32(data, to + 4), f = I32(data, to + 8);
            PutI32(data, to + 4, s >= count ? newCount : s + 1);
            PutI32(data, to + 8, f >= count ? newCount : f + 1);
        }
        PutI32(data, 0, arrayAt);
        PutI32(data, 4, newCount);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(data.Length - 16, 8), AllySafetyCodec.Magic);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(data.Length - 8, 8), pointer);
        return data;
    }

    [Fact]
    public void A_first_format_copy_is_still_recognised_for_restore()
    {
        var old = FirstFormatCopy(Blind(), FakePointer);
        Assert.Equal(HitSlotKind.Guarded, AllySafetyCodec.Classify(false, old));
        Assert.True(AllySafetyCodec.TryGuard(old, out long original, out int format, out int marker));
        Assert.Equal(FakePointer, original);
        Assert.Equal(1, format);
        Assert.Equal(0, marker);
    }

    [Fact]
    public void A_trailer_alone_does_not_make_a_copy()
    {
        var copy = AllySafetyCodec.Prepend(Blind(), FakePointer, Marker);
        var tampered = (byte[])copy.Clone();
        PutI32(tampered, ArrayStart(copy) + 2 * Stride + 8, 2); // ally guard failure no longer terminal
        Assert.False(AllySafetyCodec.TryGuard(tampered, out _));
        tampered = (byte[])copy.Clone();
        PutI32(tampered, ArrayStart(copy) + Stride + 0x18, Marker + 1); // the two HasBuff elements disagree
        Assert.False(AllySafetyCodec.TryGuard(tampered, out _));
        tampered = (byte[])copy.Clone();
        tampered[copy.Length - 16] ^= 0xFF; // magic broken
        Assert.False(AllySafetyCodec.TryGuard(tampered, out _));
        var v2MagicOnV1 = FirstFormatCopy(Blind(), FakePointer);
        BinaryPrimitives.WriteInt64LittleEndian(v2MagicOnV1.AsSpan(v2MagicOnV1.Length - 16, 8), AllySafetyCodec.MagicV2);
        Assert.False(AllySafetyCodec.TryGuard(v2MagicOnV1, out _));
    }

    [Fact]
    public void Guarding_a_copy_again_is_never_needed()
    {
        // A pass classifies a copy as Guarded before it looks at sources, so it never re-prepends.
        var copy = AllySafetyCodec.Prepend(Blind(), FakePointer, Marker);
        Assert.NotEqual(HitSlotKind.TeamBlind, AllySafetyCodec.Classify(false, copy));
        Assert.NotEqual(HitSlotKind.NullCondition, AllySafetyCodec.Classify(false, copy));
    }

    // ---- the header ----------------------------------------------------------------------------------

    [Fact]
    public void A_copy_header_keeps_the_allocator_and_perturbs_the_hash()
    {
        var template = new byte[32];
        BinaryPrimitives.WriteInt64LittleEndian(template.AsSpan(0, 8), FakePointer);
        PutI32(template, 8, 784);
        PutI32(template, 0xC, 0x00010007);
        BinaryPrimitives.WriteUInt64LittleEndian(template.AsSpan(0x10, 8), 0x1122334455667788UL);

        var header = AllySafetyCodec.CopyHeader(template);
        Assert.Equal(0x00010007, I32(header, 0xC));
        Assert.Equal(0x1122334455667788UL ^ AllySafetyCodec.HashSalt, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x10, 8)));

        AllySafetyCodec.SealHeader(header, 0x2000, 1024);
        Assert.Equal(0x2000, I64(header, 0));
        Assert.Equal(1024, I32(header, 8));
        Assert.Equal(0x00010007, I32(header, 0xC));
    }

    [Fact]
    public void A_fresh_header_is_persistent_with_a_content_hash()
    {
        var fresh = AllySafetyCodec.BuildNullCondition(Marker);
        ulong hash = AllySafetyCodec.ContentHash(fresh);
        var header = AllySafetyCodec.NewHeader(hash);
        Assert.Equal(32, header.Length);
        Assert.Equal(AllySafetyCodec.PersistentAllocatorHandle, I32(header, 0xC));
        Assert.Equal(hash, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x10, 8)));
        Assert.NotEqual(0UL, hash);
        Assert.Equal(0, I64(header, 0x18));
    }

    // ---- exemptions ------------------------------------------------------------------------------------

    /// <summary>The 37 team-blind or null-condition damaging prefabs of the 2026-09-24 server scan (teamscan.tsv).</summary>
    private static readonly string[] Exempt =
    {
        "EH_Mines_ExplosiveBarrel_ActivatedBuff_Standard_T02",
        "EH_Mines_ExplosiveBarrel_ActivatedBuff_Fast",
        "EH_Mines_ExplosiveBarrel_ActivatedBuff_Standard_T01",
        "EH_Mines_ExplosiveBarrel_ActivatedBuff",
        "EH_Monster_EnergyBeam_Active",
        "Buff_Ocean_Blowfish_DamageArea",
        "Buff_BearTrap_Immobilize",
        "AB_Mech_Activate_Explosive_Trigger",
        "AB_Lucie_PlayerAbility_LiquidFirePotion_Throw_Throw",
        "AB_Lucie_PlayerAbility_LiquidFirePotion_Drink_FirebreathBuff",
        "AB_Storm_BallLightning_Projectile",
        "AB_Spear_AThousandSpears_Stab_BallLightning",
    };

    private static readonly string[] Guarded =
    {
        "AB_Manticore_FrostBreath_Channel",
        "AB_Blackfang_CarverBoss_MegaCleave_Hit02",
        "AB_Blackfang_Morgana_SummonCorruptedVines_Throw",
        "AB_Cursed_Witch_ExplodeMosquito_Buff",
        "AB_Bandit_Fisherman_SerpentFeed_Hard_Throw",
        "AB_CarverBoss_Whirlwind_Hit",
        "AB_Wendigo_IceBeam_First_Channel",
        "AB_Vampire_Dracula_WolfAttack_Hit",
        "AB_Gloomrot_Pyro_FlameStrafer_Buff",
        "AB_Gloomrot_SentryTurret_ShockField_Area",
        "AB_Blackfang_CarverBoss_MegaCleave_Hit",
        "AB_Iva_BurningRingOfFire_Spinner",
        "AB_HighLordSword_SelfStun_Projectile",
        "AB_ChurchOfLight_Priest_HealBomb_Buff",
        "AB_Gloomrot_AceIncinerator_FlameSpreader_Spinner",
        "AB_Iva_Burn_Channel",
        "AB_ArchMage_FlameSphere_Projectile",
        "AB_Militia_BishopOfDunley_Projectile_Hard_Area",
        "AB_Gloomrot_SpiderTank_LightningRod_HeavyBeam_Channel",
        "AB_Militia_BishopOfDunley_HolyBeam_Channel",
        "AB_Militia_BishopOfDunley_HolyCircles_Throw",
        "AB_Wendigo_IceBeam_Second_Channel",
        "AB_Bandit_Fisherman_SerpentFeed_Throw",
        "AB_Militia_BishopOfDunley_Projectile_Area",
        "AB_Blackfang_Livith_RoundKick_LaunchBuff",
    };

    [Fact]
    public void The_server_scan_splits_into_hazards_and_player_abilities_versus_npc_attacks()
    {
        foreach (var name in Exempt) Assert.NotNull(AllySafetyRules.BuiltInExemption(name));
        foreach (var name in Guarded) Assert.Null(AllySafetyRules.BuiltInExemption(name));
        Assert.Equal(37, Exempt.Length + Guarded.Length);
    }

    [Theory]
    [InlineData("AB_Vampire_Axe_Primary_MeleeAttack_Hit01", "player weapon ability")]
    [InlineData("AB_Vampire_Unarmed_Primary_Mounted_Hit", "player weapon ability")]
    [InlineData("AB_Pistols_ExplosiveShot_Shot_Projectile", "player weapon skill")]
    [InlineData("AB_Frost_CrystalLance_Projectile", "player spell")]
    [InlineData("AB_Unholy_SummonFallenAngel_Throw", "player spell")]
    [InlineData("Storm_Vampire_Static_ChainLightning", "player spell passive")]
    [InlineData("AB_Vampire_VeilOfFrost_TriggerBonusEffects", "player kit")]
    [InlineData("AB_Shapeshift_Wolf_Bite_Hit", "player kit")]
    [InlineData("EH_UnholyChasmEruption_Throw", "environmental hazard")]
    public void Player_kits_and_hazards_are_exempt(string name, string reason)
    {
        Assert.StartsWith(reason, AllySafetyRules.BuiltInExemption(name));
    }

    [Theory]
    [InlineData("AB_Vampire_Dracula_ShockwaveSlash_Projectile01")] // the boss, not a weapon
    [InlineData("AB_Vampire_BloodKnight_Hit")]
    [InlineData("AB_Blackfang_Morgana_TravelingOrbBarrage_ThrowToPlayer")] // "Player", not "PlayerAbility"
    [InlineData("AB_HighLord_SwordPrimary_MeleeAttack_Hit01")]
    [InlineData("AB_Stormwing_Hit")] // prefix must end in '_'
    [InlineData("ab_storm_balllightning_projectile")] // the game's spelling only
    [InlineData("")]
    [InlineData(null)]
    public void Npc_attacks_and_unknown_names_are_guarded(string? name)
    {
        Assert.Null(AllySafetyRules.BuiltInExemption(name));
    }

    [Fact]
    public void A_hit_that_also_heals_is_exempt()
    {
        Assert.Equal(AllySafetyRules.HealsOnHitReason, AllySafetyRules.BuiltInExemption("AB_ChurchOfLight_Priest_HealBomb_Buff", healsOnHit: true));
        Assert.Equal(AllySafetyRules.HealsOnHitReason, AllySafetyRules.BuiltInExemption(null, healsOnHit: true));
        Assert.StartsWith("environmental hazard", AllySafetyRules.BuiltInExemption("EH_Heal", healsOnHit: true));
    }

    [Fact]
    public void Exempt_beats_protect_which_beats_the_defaults()
    {
        // enabled, builtIn, exempt, protect
        Assert.True(AllySafetyRules.ShouldGuard(true, null, 0, 0));
        Assert.False(AllySafetyRules.ShouldGuard(true, "hazard", 0, 0));
        Assert.False(AllySafetyRules.ShouldGuard(false, null, 0, 0));
        Assert.True(AllySafetyRules.ShouldGuard(false, "hazard", 0, 1));
        Assert.True(AllySafetyRules.ShouldGuard(true, "hazard", 0, 2));
        Assert.False(AllySafetyRules.ShouldGuard(true, null, 1, 0));
        Assert.False(AllySafetyRules.ShouldGuard(true, null, 1, 3));

        Assert.Equal(AllySafetyState.ExemptByPlugin, AllySafetyRules.UnguardedState("hazard", 1));
        Assert.Equal(AllySafetyState.ExemptBuiltIn, AllySafetyRules.UnguardedState("hazard", 0));
        Assert.Equal(AllySafetyState.Disabled, AllySafetyRules.UnguardedState(null, 0));
    }

    [Fact]
    public void The_summary_line_carries_every_count()
    {
        var report = new AllySafetyReport
        {
            Enabled = true,
            DamagingPrefabs = 1553,
            TeamAwarePrefabs = 1516,
            ProtectedPrefabs = 24,
            GuardedSlots = 26,
            GuardedNullSlots = 0,
            NewlyGuardedSlots = 26,
            ExemptBuiltIn = 13,
            Failures = 0,
        };
        string line = report.Summary();
        Assert.Contains("1553 prefab(s) with hit and damage buffers", line);
        Assert.Contains("damaging hit slot(s)", line);
        Assert.Contains("guarded 24 prefab(s) / 26 slot(s)", line);
        Assert.Contains("exempt 13 (built-in 13, plugin 0, config 0)", line);
        Assert.Contains("0 failure(s)", line);
    }
}
