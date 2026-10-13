using System;
using Progenitor.Abilities;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The game-free rules behind <c>ctx.Abilities.NoCooldowns</c> and <c>ForceCast</c>: when the loop
/// runs, which fields it writes, how holds nest and release. No game.
/// </summary>
public class AbilityCooldownPolicyTests
{
    [Fact]
    public void Due_runs_first_time_then_waits_for_the_interval()
    {
        Assert.True(AbilityCooldownPolicy.Due(1_000, AbilityCooldownPolicy.Never, 0.1));
        Assert.False(AbilityCooldownPolicy.Due(1_050, 1_000, 0.1));
        Assert.True(AbilityCooldownPolicy.Due(1_100, 1_000, 0.1));
        Assert.False(AbilityCooldownPolicy.Due(1_999, 1_000, 1.0));
        Assert.True(AbilityCooldownPolicy.Due(2_000, 1_000, 1.0));
    }

    [Fact]
    public void Due_treats_a_clock_going_backwards_as_due_so_the_loop_cannot_stall()
    {
        Assert.True(AbilityCooldownPolicy.Due(500, 1_000, 0.1));
    }

    [Fact]
    public void Intervals_are_the_documented_ones()
    {
        Assert.Equal(0.1, AbilityCooldownPolicy.ZeroIntervalSeconds);
        Assert.Equal(0.5, AbilityCooldownPolicy.FollowUpRebuildSeconds);
    }

    [Theory]
    [InlineData(-1, 41, true)]   // first look after a hold
    [InlineData(41, 41, false)]
    [InlineData(41, 42, true)]
    [InlineData(41, -1, false)]  // unreadable counter is not an advance
    [InlineData(int.MaxValue, int.MinValue + 1, false)] // negative after wrap reads as unreadable
    public void Counter_advance_is_any_readable_change(int last, int current, bool expected)
    {
        Assert.Equal(expected, AbilityCooldownPolicy.CounterAdvanced(last, current));
    }

    [Fact]
    public void Rebuild_happens_on_take_on_a_cast_and_once_after_never_on_a_timer()
    {
        long never = AbilityCooldownPolicy.Never;
        Assert.True(AbilityCooldownPolicy.NeedsRebuild(true, false, 0, never));
        Assert.True(AbilityCooldownPolicy.NeedsRebuild(false, true, 0, never));
        Assert.False(AbilityCooldownPolicy.NeedsRebuild(false, false, 10_000_000, never));
        Assert.False(AbilityCooldownPolicy.NeedsRebuild(false, false, 1_499, 1_500));
        Assert.True(AbilityCooldownPolicy.NeedsRebuild(false, false, 1_500, 1_500));
    }

    [Fact]
    public void A_hold_from_another_world_epoch_is_stale()
    {
        Assert.True(AbilityCooldownPolicy.SameEpoch(5, 5));
        Assert.False(AbilityCooldownPolicy.SameEpoch(5, 6));
    }

    [Theory]
    [InlineData(0.0, 0f, false)]
    [InlineData(1234.5, 0f, true)]
    [InlineData(0.0, 8f, true)]
    [InlineData(99.0, 8f, true)]
    [InlineData(double.NaN, 0f, true)]
    [InlineData(0.0, float.NaN, true)]
    public void Cooldown_is_written_only_when_not_already_zero(double end, float current, bool expected)
    {
        Assert.Equal(expected, AbilityCooldownPolicy.NeedsCooldownWrite(end, current));
    }

    [Theory]
    [InlineData(0, 0, -1)]   // no charges: leave it
    [InlineData(0, -1, -1)]  // garbage max: leave it
    [InlineData(2, 2, -1)]   // already full
    [InlineData(3, 2, -1)]   // above max (a buff raised it): never lowered
    [InlineData(0, 2, 2)]
    [InlineData(1, 3, 3)]
    [InlineData(0, 1000, 255)] // clamped to the synced range
    [InlineData(255, 1000, -1)]
    public void Charges_are_topped_up_to_max_and_never_lowered(int current, int max, int expected)
    {
        Assert.Equal(expected, AbilityCooldownPolicy.ChargesToWrite(current, max));
    }

    [Fact]
    public void Key_distinguishes_a_reused_index_by_version()
    {
        Assert.NotEqual(AbilityCooldownPolicy.Key(42, 1), AbilityCooldownPolicy.Key(42, 2));
        Assert.NotEqual(AbilityCooldownPolicy.Key(1, 42), AbilityCooldownPolicy.Key(42, 1));
        Assert.Equal(AbilityCooldownPolicy.Key(7, 3), AbilityCooldownPolicy.Key(7, 3));
        Assert.NotEqual(AbilityCooldownPolicy.Key(0, -1), AbilityCooldownPolicy.Key(-1, 0));
    }

    private sealed class Holder
    {
        public string Owner = "";
    }

    [Fact]
    public void Holds_nest_and_the_character_is_released_only_by_the_last_holder()
    {
        var holds = new AbilityCooldownPolicy.Holds<Holder>();
        long key = AbilityCooldownPolicy.Key(10, 1);
        var a = new Holder { Owner = "a" };
        var b = new Holder { Owner = "b" };

        Assert.True(holds.Add(key, a));   // first holder starts the character
        Assert.False(holds.Add(key, b));  // second joins
        Assert.False(holds.Add(key, a));  // the same holder twice is not a second hold
        Assert.Equal(1, holds.CharacterCount);

        Assert.False(holds.Remove(key, a));
        Assert.True(holds.IsHeld(key));
        Assert.True(holds.IsHeldBy(key, h => h.Owner == "b"));
        Assert.False(holds.IsHeldBy(key, h => h.Owner == "a"));

        Assert.True(holds.Remove(key, b));  // last one out stops the writes
        Assert.False(holds.IsHeld(key));
        Assert.Equal(0, holds.CharacterCount);
    }

    [Fact]
    public void Releasing_twice_or_an_unknown_holder_is_a_no_op()
    {
        var holds = new AbilityCooldownPolicy.Holds<Holder>();
        long key = AbilityCooldownPolicy.Key(10, 1);
        var a = new Holder();
        Assert.False(holds.Remove(key, a));
        holds.Add(key, a);
        Assert.True(holds.Remove(key, a));
        Assert.False(holds.Remove(key, a));
    }

    [Fact]
    public void Drop_releases_every_holder_of_one_character_and_leaves_the_others()
    {
        var holds = new AbilityCooldownPolicy.Holds<Holder>();
        long k1 = AbilityCooldownPolicy.Key(1, 1), k2 = AbilityCooldownPolicy.Key(2, 1);
        holds.Add(k1, new Holder());
        holds.Add(k1, new Holder());
        holds.Add(k2, new Holder());

        Assert.Equal(2, holds.Drop(k1).Count);
        Assert.False(holds.IsHeld(k1));
        Assert.True(holds.IsHeld(k2));
        Assert.Empty(holds.Drop(k1));

        Assert.Single(holds.DropAll());
        Assert.Equal(0, holds.CharacterCount);
        Assert.Empty(holds.Keys());
    }

    [Fact]
    public void Add_refuses_a_null_holder()
    {
        var holds = new AbilityCooldownPolicy.Holds<Holder>();
        Assert.Throws<ArgumentNullException>(() => holds.Add(1, null!));
    }

    [Fact]
    public void Text_names_the_character_and_the_evidence_to_watch()
    {
        Assert.Contains("12:3", AbilityCooldownPolicy.MissingCharacterRefusal(12, 3));
        Assert.Contains("ServerScriptMapper", AbilityCooldownPolicy.NotReadyRefusal());
        string cast = AbilityCooldownPolicy.FormatForceCast(-1204819086, 12, 3, 41);
        Assert.Contains("-1204819086", cast);
        Assert.Contains("castStartedCounter was 41", cast);
        Assert.Contains("resume normally", AbilityCooldownPolicy.FormatRelease(12, 3, "disposed"));
        Assert.Contains("keeping the previous lists", AbilityCooldownPolicy.FormatRebuildFailed("boom"));
    }
}
