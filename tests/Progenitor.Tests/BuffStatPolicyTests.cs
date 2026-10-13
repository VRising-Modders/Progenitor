using System;
using System.Linq;
using Progenitor.Buffs;
using Progenitor.TimedBuffExample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The supported-stat-modifier boundary and the example plugin's own policy. Both are game-free by
/// construction, which is the point: what the API refuses and the sentence it refuses with are
/// pinned here rather than only in a document.
/// </summary>
public sealed class BuffStatPolicyTests
{
    [Fact]
    public void Blood_drain_is_the_supported_modifier_on_this_build()
    {
        Assert.True(BuffStatSupport.IsSupported(BuffStatKind.BloodDrain));
        Assert.Equal("", BuffStatSupport.Reason(BuffStatKind.BloodDrain));
        Assert.Equal("ProjectM.ModifyBloodDrainBuff", BuffStatSupport.ComponentName(BuffStatKind.BloodDrain));
    }

    [Fact]
    public void Movement_speed_is_refused_and_names_the_missing_evidence()
    {
        Assert.False(BuffStatSupport.IsSupported(BuffStatKind.MovementSpeed));

        string reason = BuffStatSupport.Reason(BuffStatKind.MovementSpeed);
        Assert.Contains("CurveReference", reason);
        Assert.Contains("never been measured", reason);
        Assert.Contains("docs/buffs.md", reason);
    }

    [Fact]
    public void Every_declared_kind_either_is_supported_or_says_why_not()
    {
        foreach (BuffStatKind kind in Enum.GetValues(typeof(BuffStatKind)))
        {
            if (BuffStatSupport.IsSupported(kind))
            {
                Assert.Equal("", BuffStatSupport.Reason(kind));
                continue;
            }

            string reason = BuffStatSupport.Reason(kind);
            Assert.False(string.IsNullOrWhiteSpace(reason), kind + " is refused with no reason");
            Assert.True(reason.Length > 60, kind + " is refused without naming what is missing");
        }
    }

    [Fact]
    public void An_unsupported_modifier_is_not_usable_and_carries_its_reason()
    {
        var stat = BuffStat.Of(BuffStatKind.MovementSpeed, BuffStatOp.Multiply, 1.5f);

        Assert.False(stat.IsUsable(out string reason));
        Assert.Equal(BuffStatSupport.Reason(BuffStatKind.MovementSpeed), reason);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void A_non_finite_value_is_refused_even_for_a_supported_modifier(float value)
    {
        var stat = BuffStat.BloodDrain(BuffStatOp.Set, value);

        Assert.False(stat.IsUsable(out string reason));
        Assert.Contains("finite", reason);
    }

    [Fact]
    public void A_supported_modifier_with_a_finite_value_is_usable()
    {
        Assert.True(BuffStat.BloodDrain(BuffStatOp.Set, 0f).IsUsable(out _));
        Assert.True(BuffStat.BloodDrain(BuffStatOp.Multiply, 0.5f, priority: 10).IsUsable(out _));
    }

    // -----------------------------------------------------------------
    // Which definition may carry a plugin's stat modifiers
    // -----------------------------------------------------------------

    [Fact]
    public void A_buff_this_plugin_registered_may_carry_stats()
    {
        Assert.True(BuffStatSupport.MayCarryStats(
            "example.rested", found: true, isBuff: true, ownedByCaller: true, "Buff", out string reason));
        Assert.Equal("", reason);
    }

    [Fact]
    public void An_unregistered_id_names_the_call_that_registers_it()
    {
        Assert.False(BuffStatSupport.MayCarryStats(
            "nobody.knows", found: false, isBuff: false, ownedByCaller: false, "", out string reason));
        Assert.Contains("ctx.Content.DefineBuff(\"nobody.knows\")", reason);
    }

    [Fact]
    public void An_item_definition_is_refused_and_says_which_kind_it_is()
    {
        Assert.False(BuffStatSupport.MayCarryStats(
            "sample.ember", found: true, isBuff: false, ownedByCaller: true, "Item", out string reason));
        Assert.Contains("registered as Item", reason);
        Assert.Contains("DefineBuff", reason);
    }

    [Fact]
    public void Another_plugin_s_clone_is_refused()
    {
        Assert.False(BuffStatSupport.MayCarryStats(
            "sample.nodrain", found: true, isBuff: true, ownedByCaller: false, "Buff", out string reason));
        Assert.Contains("belongs to another plugin", reason);
    }

    [Fact]
    public void The_two_stats_log_lines_name_the_buff_and_the_reason()
    {
        Assert.Contains("example.rested", BuffStatSupport.StatsRefusedLine("example.rested", "because"));
        Assert.Contains("because", BuffStatSupport.StatsRefusedLine("example.rested", "because"));
        Assert.Contains("nothing supported to write", BuffStatSupport.NothingToWriteLine("example.rested"));
    }

    // -----------------------------------------------------------------
    // The example plugin's own policy
    // -----------------------------------------------------------------

    [Fact]
    public void The_example_buff_modifies_a_supported_stat()
    {
        var stat = TimedBuffPolicy.Stat();

        Assert.True(stat.IsUsable(out _));
        Assert.Equal(BuffStatKind.BloodDrain, stat.Kind);
        Assert.Equal(BuffStatOp.Set, stat.Op);
        Assert.Equal(0f, stat.Value);
    }

    /// <summary>
    /// Job BUFFP6/BUFFP9. The example promises "your blood does not drain while this lasts". Live on
    /// 2026-09-13 it also granted +50% run speed, inherited from the <c>Buff_General_Haste</c> base
    /// it clones and mentioned nowhere. Its promise and its declared behavior are pinned together
    /// here so they cannot drift apart again.
    /// </summary>
    [Fact]
    public void The_example_buff_suppresses_the_movement_speed_it_inherits_from_its_base()
    {
        var stats = TimedBuffPolicy.Stats();

        Assert.Contains(stats, s => s.IsSuppression && s.Kind == BuffStatKind.MovementSpeed);
        foreach (var stat in stats) Assert.True(stat.IsUsable(out _));
    }

    /// <summary>
    /// "Does not drain" includes idle drain, so this example asks for
    /// <see cref="BloodDrainScope.ActiveAndIdle"/> <b>explicitly</b> — it is not the default any
    /// more. Safe here only because the op is <c>Set</c>, where writing the same value twice is
    /// idempotent; that is the reasoning being pinned, not just the value.
    /// </summary>
    [Fact]
    public void The_example_buff_asks_for_idle_drain_explicitly_and_only_under_Set()
    {
        var drain = TimedBuffPolicy.Stats().Single(s => !s.IsSuppression);

        Assert.Equal(BloodDrainScope.ActiveAndIdle, drain.Scope);
        Assert.Equal(BuffStatOp.Set, drain.Op);
    }

    /// <summary>
    /// The fresh-agent example, pinned beside the canonical one. Its <c>Multiply 0.1</c> must take
    /// the single-modification default — that is the whole point of BUFFP5, and it is what the agent
    /// meant by "drain a tenth as fast".
    /// </summary>
    [Fact]
    public void The_exercise_buff_multiplies_once_and_suppresses_inherited_movement_speed()
    {
        var stats = Exercise.TimedBuff.AdrenalinePolicy.Stats();

        var drain = stats.Single(s => !s.IsSuppression);
        Assert.Equal(BuffStatKind.BloodDrain, drain.Kind);
        Assert.Equal(BuffStatOp.Multiply, drain.Op);
        Assert.Equal(0.1f, drain.Value);
        Assert.Equal(BloodDrainScope.Active, drain.Scope);

        Assert.Contains(stats, s => s.IsSuppression && s.Kind == BuffStatKind.MovementSpeed);
        foreach (var stat in stats) Assert.True(stat.IsUsable(out _));
    }

    [Fact]
    public void The_example_defaults_to_its_documented_duration_when_given_no_argument()
    {
        Assert.True(TimedBuffPolicy.TryParseSeconds(null, out float seconds, out _));
        Assert.Equal(TimedBuffPolicy.DefaultSeconds, seconds);

        Assert.True(TimedBuffPolicy.TryParseSeconds("", out seconds, out _));
        Assert.Equal(TimedBuffPolicy.DefaultSeconds, seconds);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("100000")]
    public void The_example_refuses_a_duration_it_cannot_honor(string argument)
    {
        Assert.False(TimedBuffPolicy.TryParseSeconds(argument, out _, out string reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void The_example_stays_inside_the_api_s_own_duration_limit()
    {
        Assert.True(TimedBuffPolicy.MaxSeconds <= BuffApplicationPolicy.MaxDurationSeconds);
        Assert.True(TimedBuffPolicy.MinSeconds > 0f);
        Assert.InRange(
            TimedBuffPolicy.DefaultSeconds, TimedBuffPolicy.MinSeconds, TimedBuffPolicy.MaxSeconds);
    }

    [Fact]
    public void The_example_never_calls_a_requested_apply_an_active_one()
    {
        string requested = TimedBuffPolicy.RequestedReply(30f);

        Assert.Contains("not on yet", requested);
        Assert.NotEqual(TimedBuffPolicy.ActiveReply(30f), requested);
    }

    [Fact]
    public void The_example_has_a_sentence_for_every_terminal_state()
    {
        foreach (BuffApplicationState state in Enum.GetValues(typeof(BuffApplicationState)))
        {
            if (!BuffApplicationPolicy.IsTerminal(state)) continue;
            string text = TimedBuffPolicy.EndedReply(state);
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }
}
