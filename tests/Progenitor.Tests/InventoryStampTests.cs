using System;
using System.Collections.Generic;
using Progenitor.Ecs;
using Progenitor.Inventory;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of <c>ctx.Inventory.ChangeStamp</c> / <c>ChangeStamps</c> / <c>Clock</c> /
/// <c>AnyChangedSince</c>: stamp composition, the 32-to-64-bit version extension across a wrap,
/// the at-or-after rule, identity changes, the audit and its miss counter, and the rule that any
/// failed read is a fresh stamp (a false positive) rather than a repeated one (a false negative).
/// The raw reader's bounds and the type-position cache are covered through
/// <see cref="ChunkVersionMath"/> and <see cref="TypePositionCache"/>.
/// </summary>
public class InventoryStampTests
{
    private const long Arch1 = 0x1000;
    private const long Arch2 = 0x2000;

    // ---- composition -----------------------------------------------------------------------------

    [Fact]
    public void An_unchanged_inventory_keeps_its_stamp_and_a_version_bump_changes_it()
    {
        var w = new FakeStampWorld();
        var chest = w.Chest(10, chunk: 3, Arch1, version: 100);
        var s = w.Stamps();

        long a = s.Stamp(chest);
        Assert.NotEqual(0, a);
        w.Clock = 1500;
        Assert.Equal(a, s.Stamp(chest));
        Assert.Equal(a, s.Stamp(chest));

        w.Bump(10, 1400);
        long b = s.Stamp(chest);
        Assert.NotEqual(a, b);
        Assert.Equal(b, s.Stamp(chest));
    }

    [Fact]
    public void Chunk_archetype_epoch_and_entity_are_all_in_the_stamp()
    {
        var w = new FakeStampWorld();
        var chest = w.Chest(10, chunk: 3, Arch1, version: 100);
        var other = w.Chest(11, chunk: 3, Arch1, version: 100);
        var s = w.Stamps();
        long a = s.Stamp(chest);
        Assert.NotEqual(a, s.Stamp(other)); // same chunk and version, different entity

        w.Entities[(10, 1)].Slots = new ChunkVersionRead(4, Arch1, 100);
        long movedChunk = s.Stamp(chest);
        Assert.NotEqual(a, movedChunk);

        w.Entities[(10, 1)].Slots = new ChunkVersionRead(4, Arch2, 100);
        long movedArchetype = s.Stamp(chest);
        Assert.NotEqual(movedChunk, movedArchetype);

        // A new world: the old reference is stale, every call is fresh and none repeats.
        w.Epoch = 2;
        long f1 = s.Stamp(chest);
        long f2 = s.Stamp(chest);
        Assert.NotEqual(f1, f2);
        Assert.NotEqual(movedArchetype, f1);
        var renewed = new InventoryRef(10, 1, 0, 2);
        long r = s.Stamp(renewed);
        Assert.NotEqual(movedArchetype, r); // same entity, chunk and version; the epoch differs
    }

    [Fact]
    public void An_external_inventory_folds_in_the_owner_chunks_instance_list_version()
    {
        var w = new FakeStampWorld();
        var bag = w.Player(owner: 20, bagIndex: 21, ownerChunk: 7, slotsChunk: 8, Arch1, Arch2);
        var s = w.Stamps();
        long a = s.Stamp(bag);
        Assert.Equal(a, s.Stamp(bag));
        w.BumpInstances(20, 120);
        Assert.NotEqual(a, s.Stamp(bag));
    }

    [Fact]
    public void ChangeStamps_fills_the_span_in_one_batch_and_refuses_a_short_one()
    {
        var w = new FakeStampWorld();
        var a = w.Chest(10, 3, Arch1, 100);
        var b = w.Chest(11, 3, Arch1, 100);
        var c = w.Chest(12, 4, Arch1, 90);
        var s = w.Stamps();
        var refs = new[] { a, b, c };
        var into = new long[3];
        s.Stamps(refs, into);
        Assert.Equal(1, w.CompletedSlots[0]); // one batch completes the slot writers once
        Assert.Equal(s.Stamp(a), into[0]);
        Assert.Equal(s.Stamp(c), into[2]);
        Assert.Throws<ArgumentException>(() => s.Stamps(refs, new long[2]));
    }

    [Fact]
    public void A_stamp_is_never_zero()
    {
        var w = new FakeStampWorld();
        var s = w.Stamps();
        for (int i = 0; i < 200; i++)
        {
            var r = w.Chest(100 + i, i, Arch1, (uint)i);
            Assert.NotEqual(0, s.Stamp(r));
        }
        Assert.NotEqual(0, s.Stamp(InventoryRef.None));
    }

    // ---- failures are fresh ----------------------------------------------------------------------

    [Fact]
    public void A_failed_self_check_gives_a_fresh_stamp_never_a_repeat()
    {
        var w = new FakeStampWorld();
        var chest = w.Chest(10, 3, Arch1, 100);
        var s = w.Stamps();
        long good = s.Stamp(chest);

        w.Entities[(10, 1)].SlotsReadable = false; // the raw read reports unknown
        var seen = new HashSet<long> { good };
        for (int i = 0; i < 50; i++) Assert.True(seen.Add(s.Stamp(chest)));
        Assert.True(s.FreshStamps >= 50);

        w.Entities[(10, 1)].SlotsReadable = true;
        long back = s.Stamp(chest);
        Assert.Equal(good, back); // the same unchanged content reads equal again
    }

    [Fact]
    public void No_world_or_no_clock_is_fresh_and_AnyChangedSince_true()
    {
        var w = new FakeStampWorld();
        var chest = w.Chest(10, 3, Arch1, 100);
        var s = w.Stamps();
        long since = s.Clock;
        w.Bound = false;
        long a = s.Stamp(chest), b = s.Stamp(chest);
        Assert.NotEqual(a, b);
        Assert.True(s.AnyChangedSince(new[] { chest }, since));
    }

    [Fact]
    public void Two_generations_never_hand_out_the_same_fresh_stamp()
    {
        var w = new FakeStampWorld();
        var s1 = new InventoryStamps(w, freshSeed: 1);
        var s2 = new InventoryStamps(w, freshSeed: 2);
        Assert.NotEqual(s1.Stamp(InventoryRef.None), s2.Stamp(InventoryRef.None));
    }

    // ---- job completion ----------------------------------------------------------------------------

    [Fact]
    public void A_completion_that_throws_makes_the_whole_batch_fresh_and_the_next_batch_retries()
    {
        var w = new FakeStampWorld();
        var a = w.Chest(10, 3, Arch1, 100);
        var b = w.Chest(11, 4, Arch1, 100);
        var s = w.Stamps();
        var refs = new[] { a, b };
        var known = new long[2];
        s.Stamps(refs, known);
        w.Clock = 1100;
        long since = s.Clock;
        Assert.False(s.AnyChangedSince(refs, since));

        w.CompletionThrows = true;
        var fresh = new long[2];
        s.Stamps(refs, fresh);
        Assert.NotEqual(known[0], fresh[0]);
        Assert.NotEqual(known[1], fresh[1]);               // the second container is not trusted either
        Assert.True(s.AnyChangedSince(refs, since));
        Assert.True(s.CompletionFailures >= 2);

        w.CompletionThrows = false;
        var again = new long[2];
        s.Stamps(refs, again);
        Assert.Equal(known, again);                        // unchanged content reads equal again
        Assert.False(s.AnyChangedSince(refs, since));
    }

    [Fact]
    public void Completion_runs_on_an_entity_the_raw_read_just_confirmed_and_the_version_is_read_after_it()
    {
        var w = new FakeStampWorld { Clock = 1000 };
        var gone = w.Chest(10, 3, Arch1, 100);
        var live = w.Chest(11, 4, Arch1, 100);
        var refs = new[] { gone, live };
        var s = w.Stamps();
        s.AnyChangedSince(refs, 0);
        w.Clock = 1100;
        long since = s.Clock;
        long before = s.Stamp(live);

        // The first cached entity is destroyed; a writer job on the second is still running.
        w.Entities.Remove((10, 1));
        w.Owners.Remove((10, 1, 0));
        w.CompletedOn.Clear();
        w.PendingWrite = (11, 1150);
        w.Clock = 1200;
        Assert.True(s.AnyChangedSince(refs, since));
        Assert.Equal((11, false), Assert.Single(w.CompletedOn)); // never on the destroyed entity
        Assert.NotEqual(before, s.Stamp(live));                  // the post-completion version is the one read
        Assert.True(s.AnyChangedSince(new[] { live }, since));
    }

    // ---- 64-bit extension ------------------------------------------------------------------------

    [Fact]
    public void An_untouched_version_keeps_its_64_bit_value_however_far_the_clock_runs()
    {
        var w = new FakeStampWorld { Clock = 1000 };
        var chest = w.Chest(10, 3, Arch1, 900);
        var s = w.Stamps();
        long a = s.Stamp(chest);
        // Step the clock past 2^31 beyond the version in frames short enough for the extension.
        for (uint step = 0; step < 6; step++)
        {
            w.Clock = unchecked(w.Clock + 0x20000000u);
            s.Tick();
        }
        Assert.True(s.Clock > 1000 + (1L << 31));
        Assert.Equal(a, s.Stamp(chest));                   // without keeping, 900 would now extend "ahead"
        long since = s.Clock;
        Assert.False(s.AnyChangedSince(new[] { chest }, since));
        w.Bump(10, w.Clock);
        Assert.NotEqual(a, s.Stamp(chest));
        Assert.True(s.AnyChangedSince(new[] { chest }, since));
    }

    [Fact]
    public void A_new_store_drops_every_cached_reference()
    {
        var w = new FakeStampWorld();
        var chest = w.Chest(10, 3, Arch1, 100);
        var s = w.Stamps();
        s.Stamp(chest);
        Assert.Equal(1, w.Resolves);
        w.Store = 2;
        s.Stamp(chest);
        Assert.Equal(2, w.Resolves);
    }

    [Fact]
    public void The_audit_runs_only_while_a_stamp_user_other_than_watch_exists()
    {
        var w = new FakeStampWorld();
        var chest = w.Chest(10, 3, Arch1, 100);
        var s = w.Stamps();
        s.Stamp(chest, user: false);                       // the watch hub's path
        s.Tick();
        Assert.False(s.HasUser);
        Assert.Equal(0, s.Audits);
        s.Stamp(chest);                                    // a plugin
        s.Tick();
        Assert.True(s.HasUser);
        Assert.Equal(1, s.Audits);
        for (int i = 0; i < InventoryStamps.EvictAfterFrames + 2; i++) s.Tick();
        Assert.False(s.HasUser);
    }

    [Fact]
    public void With_no_stamp_user_the_frame_tick_reads_nothing()
    {
        var w = new FakeStampWorld();
        var s = w.Stamps();
        for (int i = 0; i < 500; i++) s.Tick();
        Assert.Equal(0, w.Binds);
        Assert.Equal(0, w.ClockReads);
        Assert.False(s.Active);
    }

    [Fact]
    public void After_the_idle_window_the_tick_stops_and_the_next_call_resumes_with_everything_changed()
    {
        var w = new FakeStampWorld { Clock = 1000 };
        var chest = w.Chest(10, 3, Arch1, 900);
        var refs = new[] { chest };
        var s = w.Stamps();
        long a = s.Stamp(chest);
        long since = s.Clock;
        for (int i = 0; i < 10; i++) s.Tick();
        Assert.True(w.ClockReads > 10);                // sampled while in use

        for (int i = 0; i < InventoryStamps.EvictAfterFrames + 5; i++) s.Tick();
        Assert.False(s.Active);
        Assert.Equal(0, s.Entries);
        int reads = w.ClockReads;
        for (int i = 0; i < 1000; i++) s.Tick();
        Assert.Equal(reads, w.ClockReads);             // idle: no raw read at all

        // Meanwhile the clock ran unsampled (any distance, a 32-bit wrap included); the chest is untouched.
        w.Clock = 2000;
        long resumes = s.Resumes;
        Assert.True(s.AnyChangedSince(refs, since));   // resumed: everything reads as changed once
        Assert.Equal(resumes + 1, s.Resumes);
        Assert.True(s.Clock > since + uint.MaxValue);  // never behind a since handed out earlier
        Assert.NotEqual(a, s.Stamp(chest));            // a false positive, never a false negative
        long now = s.Clock;
        w.Clock = 2100;
        long later = s.Clock;
        Assert.False(s.AnyChangedSince(refs, later));  // back to normal once resumed
        Assert.True(later > now);
    }

    [Fact]
    public void The_archetype_list_is_walked_only_with_a_sane_length_and_capacity()
    {
        Assert.True(ChunkVersionMath.ArchetypeListOk(true, 10, 16, 1 << 20));
        Assert.True(ChunkVersionMath.ArchetypeListOk(true, 16, 16, 1 << 20));
        Assert.False(ChunkVersionMath.ArchetypeListOk(false, 10, 16, 1 << 20));
        Assert.False(ChunkVersionMath.ArchetypeListOk(true, 0, 16, 1 << 20));
        Assert.False(ChunkVersionMath.ArchetypeListOk(true, 17, 16, 1 << 20)); // length past capacity
        Assert.False(ChunkVersionMath.ArchetypeListOk(true, -1, 16, 1 << 20));
        Assert.False(ChunkVersionMath.ArchetypeListOk(true, 10, (1 << 20) + 1, 1 << 20));
        Assert.Equal(3, ChunkVersionMath.SelfTestThrowLimit);
    }

    [Fact]
    public void The_self_test_names_what_differs_from_GetChunk()
    {
        Assert.Null(ChunkVersionMath.SelfTestMismatch(3, 0x1000, 0x5000, 3, 0x1000, 0x5000));
        Assert.Contains("chunk 3 vs 4", ChunkVersionMath.SelfTestMismatch(3, 0x1000, 0x5000, 4, 0x1000, 0x5000));
        Assert.Contains("archetype 0x1000 vs 0x2000", ChunkVersionMath.SelfTestMismatch(3, 0x1000, 0x5000, 3, 0x2000, 0x5000));
        Assert.Contains("store", ChunkVersionMath.SelfTestMismatch(3, 0x1000, 0x5000, 3, 0x1000, 0x6000));
    }

    // ---- 64-bit extension (math) -------------------------------------------------------------

    [Fact]
    public void Extension_maps_behind_exactly_and_ahead_or_very_old_as_newer()
    {
        long clock = (5L << 32) | 1000;
        Assert.Equal(clock, ChunkVersionMath.Extend(clock, 1000));
        Assert.Equal(clock - 400, ChunkVersionMath.Extend(clock, 600));
        Assert.Equal(clock + 7, ChunkVersionMath.Extend(clock, 1007)); // written after the sample
        // Behind across the low-word wrap: 1000 - 3000 wraps to a high 32-bit value.
        Assert.Equal(clock - 3000, ChunkVersionMath.Extend(clock, unchecked(1000u - 3000u)));
        // More than 2^31 behind cannot be told from ahead: it maps newer, never older.
        long old = ChunkVersionMath.Extend(clock, unchecked(1000u - 0x80000001u));
        Assert.True(old > clock);
    }

    [Fact]
    public void A_changed_32_bit_version_always_changes_the_64_bit_one()
    {
        var rng = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            long c1 = rng.NextInt64(0, long.MaxValue / 2);
            long c2 = c1 + rng.Next(0, int.MaxValue);
            uint v1 = (uint)rng.Next(), v2 = (uint)rng.Next();
            if (v1 == v2) continue;
            Assert.NotEqual(ChunkVersionMath.Extend(c1, v1), ChunkVersionMath.Extend(c2, v2));
            Assert.Equal(v1, (uint)ChunkVersionMath.Extend(c1, v1));
        }
    }

    [Fact]
    public void The_clock_advances_across_a_32_bit_wrap_and_rebases_on_a_new_store()
    {
        Assert.Equal((1L << 32) + 0x10, ChunkVersionMath.Advance(0xFFFFFFF0L, 0x10));
        long rebased = ChunkVersionMath.Rebase((3L << 32) | 77, 5);
        Assert.Equal((5L << 32) | 5, rebased);
        Assert.True(ChunkVersionMath.Extend(rebased, 0x7FFFFFFF + 5u) > ((3L << 32) | 77));

        var w = new FakeStampWorld { Clock = 0xFFFFFFF0 };
        var chest = w.Chest(10, 3, Arch1, 0xFFFFFF00);
        var s = w.Stamps();
        long c0 = s.Clock;
        long a = s.Stamp(chest);
        w.Clock = 0x20; // wrapped
        long c1 = s.Clock;
        Assert.True(c1 > c0);
        Assert.Equal((1L << 32) | 0x20, c1);
        Assert.Equal(a, s.Stamp(chest)); // version unchanged across the wrap: same stamp
        w.Bump(10, 0x10);                // a write after the wrap
        Assert.NotEqual(a, s.Stamp(chest));

        long before = s.Clock;
        w.Store = 2; w.Clock = 3;        // a new world's store starts its own clock
        long after = s.Clock;
        Assert.True(after > before + uint.MaxValue);
    }

    [Fact]
    public void The_clock_survives_a_reload_through_its_encoded_state()
    {
        var w = new FakeStampWorld { Clock = 500 };
        var s1 = w.Stamps();
        long c1 = s1.Clock;
        string slot = s1.EncodeClock();

        w.Clock = 900;
        var s2 = w.Stamps();
        Assert.True(s2.TrySeedClock(slot));
        long c2 = s2.Clock;
        Assert.True(c2 > c1 + uint.MaxValue); // a reload resumes with a rebase: never behind, every since reads as changed
        Assert.Equal(900u, (uint)c2);
        Assert.False(w.Stamps().TrySeedClock("garbage"));
        Assert.Equal("", new InventoryStamps(new FakeStampWorld { Bound = false }).EncodeClock());
    }

    // ---- AnyChangedSince -------------------------------------------------------------------------

    [Fact]
    public void AnyChangedSince_is_at_or_after_and_first_sight_counts_as_a_change()
    {
        var w = new FakeStampWorld { Clock = 1000 };
        var chest = w.Chest(10, 3, Arch1, 900);
        var refs = new[] { chest };
        var s = w.Stamps();

        long since = s.Clock;
        Assert.True(s.AnyChangedSince(refs, since));  // first sight
        Assert.True(s.AnyChangedSince(refs, since));  // ... counts as a change at that clock value

        w.Clock = 1100;
        since = s.Clock;
        Assert.False(s.AnyChangedSince(refs, since)); // known, version 900 and first sight 1000 < 1100

        w.Bump(10, 1100);                              // a write in the same step as the clock read
        Assert.True(s.AnyChangedSince(refs, since));
        w.Clock = 1500;
        since = s.Clock;
        Assert.False(s.AnyChangedSince(refs, since));
        w.Bump(10, 1600);
        w.Clock = 1700;
        Assert.True(s.AnyChangedSince(refs, since));
        Assert.True(s.AnyChangedSince(refs, s.Clock + 10)); // a since this clock never handed out
    }

    [Fact]
    public void AnyChangedSince_sees_an_owner_that_now_resolves_to_another_inventory()
    {
        var w = new FakeStampWorld { Clock = 1000 };
        var bag = w.Player(owner: 20, bagIndex: 21, ownerChunk: 7, slotsChunk: 8, Arch1, Arch2);
        var refs = new[] { bag };
        var s = w.Stamps();
        s.AnyChangedSince(refs, 0);
        w.Clock = 1050;
        long since = s.Clock;
        Assert.False(s.AnyChangedSince(refs, since));
        long stamp = s.Stamp(bag);

        // The owner's instance list now names a different bag entity (same chunk, old versions).
        w.Entities[(22, 1)] = new FakeStampWorld.Ent { Slots = new ChunkVersionRead(8, Arch2, 10) };
        w.Owners[(20, 1, 0)] = (22, 1);
        w.BumpInstances(20, 950); // the row changed under an older version: the move still shows
        w.Clock = 1100;
        Assert.True(s.AnyChangedSince(refs, since));
        Assert.NotEqual(stamp, s.Stamp(bag));
    }

    [Fact]
    public void A_failed_read_makes_AnyChangedSince_true()
    {
        var w = new FakeStampWorld();
        var a = w.Chest(10, 3, Arch1, 100);
        var b = w.Chest(11, 3, Arch1, 100);
        var s = w.Stamps();
        s.AnyChangedSince(new[] { a, b }, 0);
        w.Clock = 1050;
        long since = s.Clock;
        Assert.False(s.AnyChangedSince(new[] { a, b }, since));
        w.Entities[(11, 1)].SlotsReadable = false;
        Assert.True(s.AnyChangedSince(new[] { a, b }, since));
    }

    // ---- identity cache ----------------------------------------------------------------------------

    [Fact]
    public void The_resolution_is_cached_while_the_raw_reads_agree()
    {
        var w = new FakeStampWorld();
        var bag = w.Player(owner: 20, bagIndex: 21, ownerChunk: 7, slotsChunk: 8, Arch1, Arch2);
        var s = w.Stamps();
        for (int i = 0; i < 20; i++) s.Stamp(bag);
        Assert.Equal(1, w.Resolves);

        w.BumpInstances(20, 130); // the owner's instance list chunk changed: resolve again
        s.Stamp(bag);
        Assert.Equal(2, w.Resolves);

        w.Entities.Remove((21, 1)); // the bag entity is gone and the owner has no other
        w.Owners.Remove((20, 1, 0));
        long f1 = s.Stamp(bag), f2 = s.Stamp(bag);
        Assert.NotEqual(f1, f2);
        Assert.Equal(0, s.Entries);
    }

    // ---- audit -------------------------------------------------------------------------------------

    [Fact]
    public void The_audit_counts_a_content_change_the_version_missed_and_folds_it_into_the_stamp()
    {
        var w = new FakeStampWorld();
        var chest = w.Chest(10, 3, Arch1, 100);
        var errors = new List<string>();
        var s = w.Stamps(errors.Add);
        long a = s.Stamp(chest);
        s.Tick();                                 // first audit: records the hash
        Assert.Equal(1, s.Audits);
        Assert.Equal(a, s.Stamp(chest));

        w.Entities[(10, 1)].Content = 2;          // a write that did not bump the version
        s.Tick();
        Assert.Equal(1, s.Misses);
        Assert.Single(errors);
        Assert.Contains("CHANGE STAMP MISS", errors[0]);
        long b = s.Stamp(chest);
        Assert.NotEqual(a, b);                    // the consumer still sees the change

        w.Entities[(10, 1)].Content = 3;
        s.Tick();
        Assert.Equal(2, s.Misses);
        Assert.Single(errors);                    // logged once, counted always

        w.Entities[(10, 1)].Content = 4;          // a normal write: content and version together
        w.Bump(10, 160);
        s.Tick();
        Assert.Equal(2, s.Misses);
    }

    [Fact]
    public void The_audit_visits_a_few_references_per_frame_round_robin_and_unused_ones_are_dropped()
    {
        var w = new FakeStampWorld();
        var s = w.Stamps();
        var refs = new List<InventoryRef>();
        for (int i = 0; i < 10; i++) refs.Add(w.Chest(100 + i, i, Arch1, 5));
        foreach (var r in refs) s.Stamp(r);

        s.Tick();
        Assert.Equal(InventoryStamps.AuditPerFrame, w.Hashes);
        s.Tick();
        s.Tick();
        Assert.Equal(3 * InventoryStamps.AuditPerFrame, w.Hashes);
        Assert.Equal(10, w.HashedDistinct.Count);   // 12 visits covered all 10

        for (int i = 0; i < InventoryStamps.EvictAfterFrames + 1; i++) s.Tick();
        Assert.Equal(0, s.Entries);
    }

    // ---- row hash ----------------------------------------------------------------------------------

    [Fact]
    public void The_row_hash_sees_every_field_and_ignores_padding()
    {
        var rows = new byte[2 * Layout.InventoryBufferStride];
        ulong h0 = InventoryStamps.RowHash(rows, 2);
        foreach (int at in new[]
                 {
                     Layout.InventoryBufferItemEntityOffset, Layout.InventoryBufferItemEntityOffset + 4,
                     Layout.InventoryBufferItemEntityOffset + Layout.NetworkedEntityWaitingForSyncOffset,
                     Layout.InventoryBufferItemTypeOffset, Layout.InventoryBufferAmountOffset,
                     Layout.InventoryBufferMaxAmountOverrideOffset,
                 })
        {
            var copy = (byte[])rows.Clone();
            copy[Layout.InventoryBufferStride + at] = 1;
            Assert.NotEqual(h0, InventoryStamps.RowHash(copy, 2));
        }

        // The three bytes between the sync flag and the item type are padding.
        var padded = (byte[])rows.Clone();
        padded[Layout.NetworkedEntityWaitingForSyncOffset + 1] = 0xAB;
        Assert.Equal(h0, InventoryStamps.RowHash(padded, 2));
        Assert.NotEqual(h0, InventoryStamps.RowHash(rows, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => InventoryStamps.RowHash(rows, 3));
    }

    // ---- raw reader checks ---------------------------------------------------------------------

    [Fact]
    public void The_raw_reader_bounds_refuse_every_out_of_range_link()
    {
        Assert.True(ChunkVersionMath.EntitySlotOk(5, 1, 10));
        Assert.False(ChunkVersionMath.EntitySlotOk(10, 1, 10));
        Assert.False(ChunkVersionMath.EntitySlotOk(-1, 1, 10));
        Assert.False(ChunkVersionMath.EntitySlotOk(0, 0, 10)); // Entity.Null

        Assert.True(ChunkVersionMath.ChunkOk(0, 16));
        Assert.False(ChunkVersionMath.ChunkOk(16, 16));
        Assert.False(ChunkVersionMath.ChunkOk(-3, 16));

        Assert.True(ChunkVersionMath.ListSlotOk(2, 3, 4));
        Assert.False(ChunkVersionMath.ListSlotOk(3, 3, 4));  // ListIndex >= Count
        Assert.False(ChunkVersionMath.ListSlotOk(0, 5, 4));  // Count > Capacity
        Assert.False(ChunkVersionMath.ListSlotOk(-1, 3, 4));

        Assert.True(ChunkVersionMath.IndexInChunkOk(0, 1));
        Assert.False(ChunkVersionMath.IndexInChunkOk(1, 1));
    }

    [Fact]
    public void The_change_version_sits_after_the_chunk_index_array_type_major()
    {
        // m_Data: ChunkIndex[Capacity] (4 bytes each), then uint[Capacity] per type.
        Assert.Equal(16 * 4, ChunkVersionMath.ChangeVersionByteOffset(16, 0, 0, Layout.ChunkIndexStride));
        Assert.Equal((16 * 4) + (((2 * 16) + 5) * 4), ChunkVersionMath.ChangeVersionByteOffset(16, 2, 5, Layout.ChunkIndexStride));
    }

    [Fact]
    public void The_type_position_cache_is_a_hint_rechecked_on_every_read()
    {
        var cache = new TypePositionCache();
        var types = new[] { 11, 22, 33 };
        Assert.Equal(2, cache.Find(Arch1, 33, types.Length, i => types[i]));
        Assert.Equal(2, cache.Find(Arch1, 33, types.Length, i => types[i]));
        Assert.Equal(1, cache.Scans);

        // The address was reused by an archetype with another order: the hint fails and it rescans.
        var reordered = new[] { 33, 11, 22 };
        Assert.Equal(0, cache.Find(Arch1, 33, reordered.Length, i => reordered[i]));
        Assert.Equal(2, cache.Scans);
        Assert.Equal(-1, cache.Find(Arch1, 44, reordered.Length, i => reordered[i]));
        Assert.Equal(-1, cache.Find(Arch1, 33, 0, i => reordered[i]));
        // A limit below the cached position (fewer change-version columns) refuses the hint.
        Assert.Equal(-1, cache.Find(Arch2, 33, 2, i => types[i]));
    }

    // ---- the watch hub skips fingerprints on an unchanged stamp ----------------------------------

    [Fact]
    public void A_watch_skips_the_fingerprint_while_the_stamp_is_unchanged_and_still_fingerprints_every_eighth()
    {
        var b = InventoryEngineTests.NewBackend();
        var a = b.AddContainer(10, 2);
        long stamp = 5;
        var hub = new InventoryWatchHub(b, null, _ => stamp);
        var seen = new List<InventoryChange>();
        var w = hub.Add(a, seen.Add, TimeSpan.FromMilliseconds(100), 0);

        hub.Tick(100);                    // first due check fingerprints (no stamp recorded yet)
        Assert.Equal(0, hub.Skipped);
        for (int t = 2; t <= 8; t++) hub.Tick(t * 100);
        Assert.Equal(7, hub.Skipped);
        hub.Tick(900);                    // the 8th since the fingerprint: fingerprints anyway
        Assert.Equal(7, hub.Skipped);

        // A change the stamp missed is still reported, within FingerprintEvery intervals.
        b.Put(a, 0, InventoryEngineTests.IronOre, 3);
        for (int t = 10; t <= 17; t++) hub.Tick(t * 100);
        Assert.Single(seen);

        // A stamp change fingerprints at once.
        b.Put(a, 0, InventoryEngineTests.IronOre, 4);
        stamp = 6;
        hub.Tick(1800);
        Assert.Equal(2, seen.Count);

        // A poke fingerprints even with an unchanged stamp.
        b.Put(a, 0, InventoryEngineTests.IronOre, 5);
        w.Poke();
        hub.Tick(1801);
        Assert.Equal(3, seen.Count);
    }

    // ---- sample and bridge surface ----------------------------------------------------------------

    [Fact]
    public void The_sample_stamp_reply_says_equal_or_differs_and_what_AnyChangedSince_said()
    {
        Assert.True(Progenitor.Sample.SampleInventory.Handles("STAMP"));
        Assert.Contains("stamp <c>", Progenitor.Sample.SampleInventory.UsageReply);
        string first = Progenitor.Sample.SampleInventory.StampReply("#1", 0x2A, 1000, 0, 0, false);
        Assert.Contains("stamp=0x000000000000002A clock=1000", first);
        Assert.Contains("first stamp", first);
        string same = Progenitor.Sample.SampleInventory.StampReply("#1", 0x2A, 1100, 0x2A, 1000, false);
        Assert.Contains("since clock 1000: stamp equal (slots unchanged), AnyChangedSince=false", same);
        string moved = Progenitor.Sample.SampleInventory.StampReply("#1", 0x2B, 1200, 0x2A, 1100, true);
        Assert.Contains("differs (slots may have changed), AnyChangedSince=true", moved);
    }

    [Fact]
    public void The_bridge_op_parses_containers_with_instances_and_an_optional_since()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{\"containers\":[\"123:1\",\"456:2#1\",\"789:3/out\"],\"since\":4242}");
        var req = Progenitor.Dev.Ops.InventoryStampArgs.Parse(doc.RootElement);
        Assert.Equal(new[] { (123, 1, 0), (456, 2, 1), (789, 3, 1) }, req.Containers);
        Assert.Equal(4242, req.Since);

        using var comma = System.Text.Json.JsonDocument.Parse("{\"containers\":\"1:1, 2:5\"}");
        var c = Progenitor.Dev.Ops.InventoryStampArgs.Parse(comma.RootElement);
        Assert.Equal(2, c.Containers.Count);
        Assert.Null(c.Since);

        foreach (var bad in new[] { "{}", "{\"containers\":[]}", "{\"containers\":[\"x\"]}", "{\"containers\":[\"1:0\"]}",
                     "{\"containers\":[\"1:1#2\"]}", "{\"containers\":[\"1:1\"],\"since\":1.5}", "{\"containers\":[5]}" })
        {
            using var d = System.Text.Json.JsonDocument.Parse(bad);
            Assert.Throws<ArgumentException>(() => Progenitor.Dev.Ops.InventoryStampArgs.Parse(d.RootElement));
        }
    }

    [Fact]
    public void A_watch_without_a_stamp_or_with_a_zero_stamp_always_fingerprints()
    {
        var b = InventoryEngineTests.NewBackend();
        var a = b.AddContainer(10, 2);
        var hub = new InventoryWatchHub(b, null, _ => 0);
        hub.Add(a, _ => { }, TimeSpan.FromMilliseconds(100), 0);
        for (int t = 1; t <= 5; t++) hub.Tick(t * 100);
        Assert.Equal(0, hub.Skipped);
        Assert.Equal(5, hub.Checks);
    }
}

/// <summary>A fake store: entities with chunk reads, owners mapping to inventory entities, a clock.</summary>
internal sealed class FakeStampWorld : IInventoryStampSource
{
    internal sealed class Ent
    {
        internal ChunkVersionRead Slots;
        internal ChunkVersionRead? Instances;
        internal bool SlotsReadable = true;
        internal int Content = 1;
    }

    internal readonly Dictionary<(int, int), Ent> Entities = new();
    internal readonly Dictionary<(int, int, int), (int, int)> Owners = new();
    internal readonly int[] CompletedSlots = new int[1];
    internal readonly HashSet<int> HashedDistinct = new();
    internal int Epoch = 1;
    internal uint Clock = 1000;
    internal long Store = 1;
    internal bool Bound = true;
    internal int Resolves;
    internal int Hashes;

    internal InventoryStamps Stamps(Action<string>? log = null) => new(this, log, freshSeed: 12345);

    internal InventoryRef Chest(int index, int chunk, long archetype, uint version)
    {
        Entities[(index, 1)] = new Ent { Slots = new ChunkVersionRead(chunk, archetype, version) };
        Owners[(index, 1, 0)] = (index, 1);
        return new InventoryRef(index, 1, 0, Epoch);
    }

    internal InventoryRef Player(int owner, int bagIndex, int ownerChunk, int slotsChunk, long ownerArchetype, long bagArchetype)
    {
        Entities[(owner, 1)] = new Ent { Instances = new ChunkVersionRead(ownerChunk, ownerArchetype, 50) };
        Entities[(bagIndex, 1)] = new Ent { Slots = new ChunkVersionRead(slotsChunk, bagArchetype, 60) };
        Owners[(owner, 1, 0)] = (bagIndex, 1);
        return new InventoryRef(owner, 1, 0, Epoch);
    }

    internal void Bump(int index, uint version)
    {
        var e = Entities[(index, 1)];
        e.Slots = new ChunkVersionRead(e.Slots.Chunk, e.Slots.Archetype, version);
    }

    internal void BumpInstances(int index, uint version)
    {
        var e = Entities[(index, 1)];
        var i = e.Instances!.Value;
        e.Instances = new ChunkVersionRead(i.Chunk, i.Archetype, version);
    }

    public int CurrentEpoch => Epoch;

    internal int Binds;
    internal int ClockReads;

    public bool BeginBatch()
    {
        Binds++;
        return Bound;
    }

    public bool TryReadClock(out uint clock, out long store)
    {
        ClockReads++;
        clock = Clock;
        store = Store;
        return Bound;
    }

    public bool TryResolve(InventoryRef container, out int inventoryIndex, out int inventoryVersion)
    {
        Resolves++;
        inventoryIndex = inventoryVersion = 0;
        if (!Owners.TryGetValue((container.OwnerIndex, container.OwnerVersion, container.Instance), out var inv)) return false;
        if (!Entities.ContainsKey(inv)) return false;
        (inventoryIndex, inventoryVersion) = inv;
        return true;
    }

    public bool TryReadSlotsVersion(int index, int version, out ChunkVersionRead read)
    {
        read = default;
        if (!Entities.TryGetValue((index, version), out var e) || !e.SlotsReadable || e.Slots.Archetype == 0) return false;
        read = e.Slots;
        return true;
    }

    public bool TryReadInstancesVersion(int index, int version, out ChunkVersionRead read)
    {
        read = default;
        if (!Entities.TryGetValue((index, version), out var e) || e.Instances == null) return false;
        read = e.Instances.Value;
        return true;
    }

    /// <summary>Entities completion ran through, in order.</summary>
    internal readonly List<(int Index, bool Instances)> CompletedOn = new();

    /// <summary>When set, CompleteWriters throws (the game's GetBufferRawRO refusing the entity).</summary>
    internal bool CompletionThrows;

    /// <summary>A writer job still running: its version lands on the slots of this entity when completion runs.</summary>
    internal (int Index, uint Version)? PendingWrite;

    public bool CompleteWriters(int index, int version, bool instances)
    {
        if (CompletionThrows) throw new InvalidOperationException("entity does not have the buffer");
        CompletedOn.Add((index, instances));
        if (!instances) CompletedSlots[0]++;
        if (!instances && PendingWrite is { } p)
        {
            Bump(p.Index, p.Version);
            PendingWrite = null;
        }
        return true;
    }

    public bool TryContentHash(int index, int version, out ulong hash)
    {
        hash = 0;
        if (!Entities.TryGetValue((index, version), out var e)) return false;
        Hashes++;
        HashedDistinct.Add(index);
        hash = (ulong)e.Content * 0x9E3779B97F4A7C15UL;
        return true;
    }
}
