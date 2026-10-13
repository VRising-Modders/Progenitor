using System;
using Progenitor.Units;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// ctx.Units.SetStats, game-free half (<see cref="UnitStatOverrides"/>, <see cref="UnitStatMath"/>,
/// <see cref="UnitStatState"/>). Values are the live defaults measured on 2026-09-25: Dunley's armored
/// skeleton (level 40, power 26.58, health 99.37) and the gear-level-91 player (physical 45.885,
/// spell 44.03, max health 948.3).
/// </summary>
public class UnitStatsTests
{
    // ---- validation ---------------------------------------------------------------------------

    [Fact]
    public void Empty_overrides_are_valid_and_empty()
    {
        var o = new UnitStatOverrides();
        Assert.True(o.IsEmpty);
        Assert.Null(o.Refusal());
        o.Validate();
    }

    [Fact]
    public void Gear_91_values_are_accepted()
    {
        var o = new UnitStatOverrides { Level = 91, MaxHealth = 948.3f, PhysicalPower = 45.885f, SpellPower = 44.03f };
        Assert.False(o.IsEmpty);
        Assert.Null(o.Refusal());
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(120, true)]
    [InlineData(0, false)]
    [InlineData(121, false)]
    [InlineData(-5, false)]
    public void Level_range_is_1_to_120(int level, bool ok)
    {
        var o = new UnitStatOverrides { Level = level };
        Assert.Equal(ok, o.Refusal() == null);
        if (!ok)
        {
            var e = Assert.Throws<ArgumentOutOfRangeException>(o.Validate);
            Assert.Contains("level", e.Message);
        }
    }

    [Theory]
    [InlineData(0.001f, true)]
    [InlineData(10_000_000f, true)]
    [InlineData(0f, false)]
    [InlineData(-1f, false)]
    [InlineData(10_000_001f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void Max_health_is_above_0_and_at_most_ten_million(float health, bool ok)
    {
        var o = new UnitStatOverrides { MaxHealth = health };
        Assert.Equal(ok, o.Refusal() == null);
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(10_000f, true)]
    [InlineData(-0.01f, false)]
    [InlineData(10_000.5f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(float.NegativeInfinity, false)]
    public void Powers_are_0_to_10000(float power, bool ok)
    {
        Assert.Equal(ok, new UnitStatOverrides { PhysicalPower = power }.Refusal() == null);
        Assert.Equal(ok, new UnitStatOverrides { SpellPower = power }.Refusal() == null);
    }

    [Fact]
    public void Refusal_names_the_field()
    {
        Assert.Contains("physical power", new UnitStatOverrides { PhysicalPower = -1f }.Refusal());
        Assert.Contains("spell power", new UnitStatOverrides { SpellPower = 20_000f }.Refusal());
        Assert.Contains("max health", new UnitStatOverrides { MaxHealth = 0f }.Refusal());
    }

    [Fact]
    public void Clone_is_independent()
    {
        var o = new UnitStatOverrides { Level = 91, PhysicalPower = 45.885f };
        var c = o.Clone();
        o.Level = 10;
        Assert.Equal(91, c.Level);
        Assert.Equal(45.885f, c.PhysicalPower);
        Assert.Null(c.MaxHealth);
    }

    // ---- request bookkeeping ------------------------------------------------------------------

    [Fact]
    public void Request_merges_set_fields_and_keeps_pending_ones()
    {
        var state = new UnitStatState();
        Assert.False(state.AnyPending);
        state.Request(new UnitStatOverrides { Level = 91, MaxHealth = 5000f });
        state.Request(new UnitStatOverrides { MaxHealth = 6000f, SpellPower = 44.03f });
        Assert.True(state.AnyPending);
        Assert.Equal(91f, state[UnitStatKind.Level].Requested);
        Assert.Equal(6000f, state[UnitStatKind.MaxHealth].Requested);
        Assert.Null(state[UnitStatKind.PhysicalPower].Requested);
        Assert.Equal(44.03f, state[UnitStatKind.SpellPower].Requested);
        Assert.False(state.AnyApplied);
    }

    // ---- spawn computation and death ----------------------------------------------------------

    [Fact]
    public void Spawn_computed_once_max_health_is_above_0()
    {
        Assert.False(UnitStatMath.SpawnComputed(0f));
        Assert.False(UnitStatMath.SpawnComputed(float.NaN));
        Assert.True(UnitStatMath.SpawnComputed(99.37f));
    }

    [Fact]
    public void Dead_or_downed_refuses_max_health()
    {
        Assert.Null(UnitStatMath.DeadOrDownedRefusal("1:1", false, false, 99.37f, 99.37f));
        Assert.Contains("dead", UnitStatMath.DeadOrDownedRefusal("1:1", true, false, 50f, 99.37f));
        Assert.Contains("dead", UnitStatMath.DeadOrDownedRefusal("1:1", false, true, 50f, 99.37f));
        Assert.Contains("downed", UnitStatMath.DeadOrDownedRefusal("1:1", false, false, 0f, 99.37f));
        // Before the spawn computation both read 0: not downed, the request waits.
        Assert.Null(UnitStatMath.DeadOrDownedRefusal("1:1", false, false, 0f, 0f));
    }

    // ---- write planning -----------------------------------------------------------------------

    [Fact]
    public void First_write_is_the_requested_value()
    {
        Assert.Equal(45.885f, UnitStatMath.PlanWrite(UnitStatKind.PhysicalPower, 26.58f, null, 45.885f));
        Assert.Equal(5000f, UnitStatMath.PlanWrite(UnitStatKind.MaxHealth, 99.37f, null, 5000f));
        Assert.Equal(91f, UnitStatMath.PlanWrite(UnitStatKind.Level, 40f, null, 91f));
    }

    [Fact]
    public void Unmodified_value_takes_the_new_request_exactly()
    {
        Assert.Equal(60f, UnitStatMath.PlanWrite(UnitStatKind.SpellPower, 44.03f, 44.03f, 60f));
        Assert.Equal(70f, UnitStatMath.PlanWrite(UnitStatKind.Level, 91f, 91f, 70f));
    }

    [Fact]
    public void Multiplicative_modifier_is_kept_on_a_float()
    {
        // Applied 40, a +50% buff reads 60; asking 80 writes 120 so the buff's end lands on 80.
        float write = UnitStatMath.PlanWrite(UnitStatKind.PhysicalPower, 60f, 40f, 80f);
        Assert.Equal(120f, write, 3);
        Assert.Equal(80f, write / 1.5f, 3);
    }

    [Fact]
    public void Additive_modifier_is_kept_on_the_level()
    {
        // Applied 40, a +5 level buff reads 45; asking 91 writes 96.
        Assert.Equal(96f, UnitStatMath.PlanWrite(UnitStatKind.Level, 45f, 40f, 91f));
    }

    [Fact]
    public void Level_write_is_rounded_and_never_below_1()
    {
        Assert.Equal(1f, UnitStatMath.PlanWrite(UnitStatKind.Level, 3f, 10f, 1f)); // 3 + (1 - 10) = -6 -> 1
        Assert.Equal(92f, UnitStatMath.PlanWrite(UnitStatKind.Level, 91.6f, null, 91.6f));
    }

    [Fact]
    public void Zero_current_or_applied_writes_the_request()
    {
        Assert.Equal(30f, UnitStatMath.PlanWrite(UnitStatKind.SpellPower, 0f, 44f, 30f));
        Assert.Equal(30f, UnitStatMath.PlanWrite(UnitStatKind.SpellPower, 5f, 0f, 30f));
        Assert.Equal(0f, UnitStatMath.PlanWrite(UnitStatKind.PhysicalPower, 60f, 40f, 0f));
    }

    // ---- re-apply -----------------------------------------------------------------------------

    [Fact]
    public void Reapply_writes_again_only_when_reset_to_the_displaced_value()
    {
        Assert.True(UnitStatMath.PlanReapply(26.58f, 45.885f, 26.58f, out float w));
        Assert.Equal(45.885f, w);
        Assert.False(UnitStatMath.PlanReapply(45.885f, 45.885f, 26.58f, out _)); // still ours
        Assert.False(UnitStatMath.PlanReapply(68.8f, 45.885f, 26.58f, out _));   // under a modifier
        Assert.False(UnitStatMath.PlanReapply(26.58f, 26.58f, 26.58f, out _));   // we asked for the default
    }

    [Fact]
    public void Health_after_reset_keeps_a_full_pool_full()
    {
        Assert.Equal(5000f, UnitStatMath.HealthAfterReset(99.37f, 99.37f, 5000f));
        Assert.Equal(50f, UnitStatMath.HealthAfterReset(50f, 99.37f, 5000f));
        Assert.Equal(40f, UnitStatMath.HealthAfterReset(60f, 99.37f, 40f));
        Assert.Equal(0f, UnitStatMath.HealthAfterReset(0f, 99.37f, 5000f));
    }
}
