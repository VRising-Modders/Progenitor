using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Castles;
using Progenitor.Dev.Ops;
using Progenitor.Servants;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The servant success cap, game-free: the forced-failure probability (overall success equals the
/// cap), the cap book's keys and persistence shape, the restore bookkeeping, the hold timeout and
/// the bridge arguments. Every id, GUID and number is a placeholder.
/// </summary>
public class ServantCapTests
{
    // ---- the forced-failure probability -------------------------------------------------------------

    [Theory]
    [InlineData(1.0f, 0.75f)]
    [InlineData(0.9f, 0.5f)]
    [InlineData(0.6f, 0.3f)]
    [InlineData(1.0f, 0.0f)]
    [InlineData(0.8333f, 0.25f)]
    public void Overall_success_equals_the_cap_when_the_chance_is_above_it(float chance, float cap)
    {
        Assert.Equal(cap, ServantCapMath.Overall(chance, cap), 6);
        Assert.Equal(1.0 - (double)cap / chance, ServantCapMath.VetoProbability(chance, cap), 9);
    }

    [Theory]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.4f, 0.75f)]
    [InlineData(1.0f, 1.0f)]
    [InlineData(0.0f, 0.5f)]
    [InlineData(0.0f, 0.0f)]
    public void A_cap_at_or_above_the_chance_never_vetoes(float chance, float cap)
    {
        Assert.Equal(0.0, ServantCapMath.VetoProbability(chance, cap));
        Assert.Equal(chance, ServantCapMath.Overall(chance, cap), 6);
        Assert.False(ServantCapMath.Veto(chance, cap, 0.0));
    }

    [Fact]
    public void A_zero_cap_always_vetoes_a_positive_chance()
    {
        Assert.Equal(1.0, ServantCapMath.VetoProbability(0.3f, 0f));
        Assert.True(ServantCapMath.Veto(0.3f, 0f, 0.999999));
    }

    [Fact]
    public void Simulated_returns_succeed_at_the_cap()
    {
        // The game's roll (success when u <= c) after Progenitor's veto (u < q), both uniform.
        var rng = new Random(12345);
        const int n = 200_000;
        foreach (var (chance, cap) in new[] { (1.0f, 0.75f), (0.9f, 0.5f), (0.7f, 0.7f), (0.4f, 0.6f) })
        {
            int success = 0;
            for (int i = 0; i < n; i++)
            {
                if (ServantCapMath.Veto(chance, cap, rng.NextDouble())) continue;
                if (rng.NextDouble() <= chance) success++;
            }
            double expected = Math.Min(chance, cap);
            Assert.InRange(success / (double)n, expected - 0.006, expected + 0.006);
        }
    }

    [Fact]
    public void The_forced_gear_level_drives_the_game_chance_to_zero()
    {
        float proficiency = ServantLogic.HuntProficiency(ServantCapMath.ForcedGearLevel, 2f);
        Assert.True(proficiency < -99000);
        Assert.Equal(0f, ServantChanceLogic.Chance(proficiency, 60, 300, 50, 1f));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0f, true)]
    [InlineData(1f, true)]
    [InlineData(0.5f, true)]
    [InlineData(-0.01f, false)]
    [InlineData(1.01f, false)]
    [InlineData(float.NaN, false)]
    public void Valid_caps_are_null_or_zero_to_one(float? cap, bool valid) => Assert.Equal(valid, ServantCapMath.IsValidCap(cap));

    // ---- the cap book -------------------------------------------------------------------------------

    private static CapRecord Record(int mission = -77, long start = 638_000_000_000_000_000L, int territory = 4, float cap = 0.75f, float length = 7200f) => new()
    {
        MissionGuid = mission,
        StartTicks = start,
        LengthSeconds = length,
        Territory = territory,
        MaxSuccess = cap,
        RecordedTicks = start,
    };

    [Fact]
    public void Caps_are_keyed_by_mission_and_start_with_a_territory_cross_check()
    {
        var book = new ServantCapBook { World = "w1" };
        string key = book.Add(Record());
        Assert.Equal("cap:w1:-77:638000000000000000", key);
        Assert.Equal("-77:638000000000000000", ServantCapBook.FromStoreKey(key, "w1"));
        Assert.Null(ServantCapBook.FromStoreKey(key, "w2")); // another world's cap is never loaded
        Assert.Null(ServantCapBook.FromStoreKey("restore:w1:1:1:5", "w1"));
        Assert.Null(ServantCapBook.FromStoreKey(key, ""));

        Assert.True(book.TryGet(-77, 638_000_000_000_000_000L, 4, out var r, out bool mismatch));
        Assert.Equal(0.75f, r!.MaxSuccess);
        Assert.False(mismatch);
        Assert.True(book.TryGet(-77, 638_000_000_000_000_000L, -1, out _, out _)); // territory unknown: no cross-check
        Assert.False(book.TryGet(-77, 638_000_000_000_000_000L, 9, out r, out mismatch));
        Assert.True(mismatch);
        Assert.Null(r);
        Assert.False(book.TryGet(-77, 638_000_000_000_000_001L, 4, out _, out _));
        Assert.False(book.TryGet(-78, 638_000_000_000_000_000L, 4, out _, out _));

        Assert.True(book.Remove(-77, 638_000_000_000_000_000L));
        Assert.Equal(0, book.Count);
    }

    [Fact]
    public void A_cap_record_survives_the_store_json()
    {
        var json = JsonSerializer.Serialize(Record(cap: 0.5f));
        var back = JsonSerializer.Deserialize<CapRecord>(json)!;
        Assert.Equal(-77, back.MissionGuid);
        Assert.Equal(638_000_000_000_000_000L, back.StartTicks);
        Assert.Equal(0.5f, back.MaxSuccess);
        Assert.Equal(4, back.Territory);
        Assert.Equal(7200f, back.LengthSeconds);

        var restore = new GearRestoreRecord { Process = "12:34", Epoch = 3, Index = 5, Version = 1, NetworkId = GearRestoreRecord.Hex(new byte[] { 1, 0xAB }), Original = 12.5f, Written = ServantCapMath.ForcedGearLevel };
        var r2 = JsonSerializer.Deserialize<GearRestoreRecord>(JsonSerializer.Serialize(restore))!;
        Assert.Equal("01ab", r2.NetworkId);
        Assert.Equal(12.5f, r2.Original);
        Assert.Equal("restore:w1:5:1:99", GearRestoreRecord.StoreKey("w1", new CastleId(5, 1), 99));
        Assert.True(GearRestoreRecord.IsKeyOf("restore:w1:5:1:99", "w1"));
        Assert.False(GearRestoreRecord.IsKeyOf("restore:w1:5:1:99", "w10"));
        Assert.False(GearRestoreRecord.IsKeyOf("restore:w1:5:1:99", ""));
    }

    [Fact]
    public void Only_caps_of_long_finished_hunts_no_heart_holds_expire()
    {
        var book = new ServantCapBook();
        long start = 1_000_000_000_000L;
        book.Add(Record(mission: 1, start: start, length: 60f));
        book.Add(Record(mission: 2, start: start, length: 60f));
        book.Add(Record(mission: 3, start: start, length: 60f));
        long end = start + 60L * ServantLogic.TicksPerSecond;
        Assert.Empty(book.Expired(end + ServantCapBook.ExpireGraceTicks - 1, (_, _) => false));
        var expired = book.Expired(end + ServantCapBook.ExpireGraceTicks + 1, (m, _) => m == 2);
        Assert.Equal(2, expired.Count);
        Assert.DoesNotContain(expired, r => r.MissionGuid == 2); // still out (ended during downtime, not resolved yet)
    }

    // ---- the restore bookkeeping --------------------------------------------------------------------

    private static TransientWrite Gear(int index, float original = 12.5f) => new()
    {
        Kind = TransientKind.GearLevel,
        Target = new CastleId(index, 1),
        Epoch = 2,
        NetworkId = new byte[] { 1, 2, 3 },
        Original = original,
        Written = ServantCapMath.ForcedGearLevel,
    };

    [Fact]
    public void A_restore_writes_only_the_value_it_wrote_on_the_entity_it_wrote()
    {
        var w = Gear(5);
        Assert.Equal(RestoreAction.Write, TransientJournal.Decide(w, true, true, ServantCapMath.ForcedGearLevel));
        Assert.Equal(RestoreAction.AlreadyOriginal, TransientJournal.Decide(w, true, true, 12.5f));
        Assert.Equal(RestoreAction.ChangedElsewhere, TransientJournal.Decide(w, true, true, 14f));
        Assert.Equal(RestoreAction.TargetGone, TransientJournal.Decide(w, false, true, 0f));
        Assert.Equal(RestoreAction.IdentityMismatch, TransientJournal.Decide(w, true, false, ServantCapMath.ForcedGearLevel));
    }

    [Fact]
    public void The_journal_drains_newest_first_and_counts_late_restores()
    {
        var j = new TransientJournal();
        j.Add(Gear(1));
        j.Add(Gear(2));
        var on = j.Drain(late: false);
        Assert.Equal(new CastleId(2, 1), on[0].Target);
        Assert.Equal(0, j.Count);
        Assert.Equal(2, j.Restores);
        Assert.Equal(0, j.LateRestores);

        j.Add(Gear(3));
        j.Drain(late: true);
        Assert.Equal(3, j.Restores);
        Assert.Equal(1, j.LateRestores);
        Assert.Empty(j.Drain(late: true)); // nothing outstanding: nothing counted
        Assert.Equal(1, j.LateRestores);
    }

    [Fact]
    public void A_restore_that_throws_is_retried_a_bounded_number_of_times()
    {
        var j = new TransientJournal();
        var w = Gear(1);
        for (int i = 1; i < TransientJournal.MaxAttempts; i++)
        {
            Assert.True(j.Retry(w));
            Assert.Single(j.Drain(late: true));
        }
        Assert.False(j.Retry(w));
        Assert.Equal(0, j.Count);
    }

    [Fact]
    public void A_foreign_record_matches_one_forced_servant_by_castle_and_name_or_none()
    {
        var rec = new GearRestoreRecord { Territory = 4, Name = "Alda", Written = ServantCapMath.ForcedGearLevel, Original = 12f };
        var forced = new List<(int, string, float)>
        {
            (4, "Bren", ServantCapMath.ForcedGearLevel),
            (4, "Alda", ServantCapMath.ForcedGearLevel),
            (5, "Alda", ServantCapMath.ForcedGearLevel),
        };
        Assert.Equal(1, rec.MatchForeign(forced));
        forced.Add((4, "Alda", ServantCapMath.ForcedGearLevel));
        Assert.Equal(-1, rec.MatchForeign(forced)); // ambiguous: never a guess
        Assert.Equal(-1, rec.MatchForeign(new List<(int, string, float)> { (4, "Alda", 3f) }));
    }

    // ---- holds while the book is not readable --------------------------------------------------------

    [Fact]
    public void A_row_is_held_until_the_timeout_then_released_once()
    {
        var holds = new HoldTracker();
        var heart = new CastleId(9, 1);
        var key = (-77, 5L, new CastleId(1, 1));
        Assert.Equal(HoldDecision.Hold, holds.Decide(heart, key, 0, out _));
        Assert.Equal(HoldDecision.Hold, holds.Decide(heart, key, HoldTracker.TimeoutTicks - 1, out _));
        Assert.Equal(HoldDecision.Release, holds.Decide(heart, key, HoldTracker.TimeoutTicks, out bool first));
        Assert.True(first);
        Assert.Equal(HoldDecision.Release, holds.Decide(heart, key, HoldTracker.TimeoutTicks + 1, out first));
        Assert.False(first);
        Assert.Equal(1, holds.Held);
        Assert.Equal(1, holds.Released);

        holds.Clear();
        Assert.Equal(HoldDecision.Hold, holds.Decide(heart, key, HoldTracker.TimeoutTicks + 2, out _));
        Assert.Equal(1, holds.Active);
    }

    // ---- the bridge arguments -----------------------------------------------------------------------

    [Fact]
    public void The_bridge_takes_a_cap_on_send_and_has_a_caps_action()
    {
        var r = ServantArgs.Parse(JsonDocument.Parse("{\"action\":\"send\",\"territory\":3,\"maxSuccess\":0.5}").RootElement);
        Assert.Equal(0.5f, r.MaxSuccess);
        Assert.Null(ServantArgs.Parse(JsonDocument.Parse("{\"action\":\"send\",\"territory\":3}").RootElement).MaxSuccess);
        Assert.Throws<ArgumentException>(() => ServantArgs.Parse(JsonDocument.Parse("{\"action\":\"send\",\"territory\":3,\"maxSuccess\":2}").RootElement));
        Assert.Equal(ServantArgs.Action.Caps, ServantArgs.Parse(JsonDocument.Parse("{\"action\":\"caps\"}").RootElement).Action);
    }
}
