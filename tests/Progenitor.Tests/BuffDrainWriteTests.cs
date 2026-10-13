using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Buffs;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job BUFFP5/BUFFP7. The <c>ProjectM.ModifyBloodDrainBuff</c> field-write decision, which used to
/// live inside the <c>#if !PROGENITOR_TESTS</c> raw-pointer path where nothing could see it.
///
/// <para>That is not an incidental detail. Because the decision was untestable, a write of
/// <b>both</b> the active and idle drain from a single <see cref="BuffStat"/> shipped, and it
/// registered two modifications against the same modifiable value. Live on 2026-09-13 one
/// <c>BloodDrain(Multiply, 0.1f)</c> took the wearer's drain from 0.005 to 5e-5 — 0.01x, not the
/// 0.1x asked for. These tests pin the corrected contract.</para>
/// </summary>
public class BuffDrainWriteTests
{
    private static IReadOnlyList<BuffFieldWrite> WritesFor(
        BuffStatOp op, float value, int priority = 0,
        BloodDrainScope scope = BloodDrainScope.Active) =>
        BuffDrainWrite.For(BuffStat.BloodDrain(op, value, priority, scope));

    private static bool Touches(IReadOnlyList<BuffFieldWrite> writes, int offset) =>
        writes.Any(w => w.Offset == offset);

    [Fact]
    public void Active_scope_writes_one_modification_and_clears_the_idle_affect_flag()
    {
        var writes = WritesFor(BuffStatOp.Multiply, 0.1f, priority: 5);

        Assert.True(Touches(writes, BuffDrainWrite.OffsetBloodValue));
        Assert.True(Touches(writes, BuffDrainWrite.OffsetModificationType));
        Assert.True(Touches(writes, BuffDrainWrite.OffsetModificationPriority));

        // The idle triple is left exactly as read: writing it is what created the second
        // modification.
        Assert.False(Touches(writes, BuffDrainWrite.OffsetBloodIdleValue));
        Assert.False(Touches(writes, BuffDrainWrite.OffsetModificationIdleType));
        Assert.False(Touches(writes, BuffDrainWrite.OffsetModificationIdlePriority));

        // AffectIdleBloodValue must be actively cleared, not merely skipped: a stale 1 left by an
        // earlier ActiveAndIdle write on the same clone would silently reinstate the compounding.
        var idleAffect = writes.Single(w => w.Offset == BuffDrainWrite.OffsetAffectIdleBloodValue);
        Assert.Equal(0, idleAffect.ByteValue);

        var affect = writes.Single(w => w.Offset == BuffDrainWrite.OffsetAffectBloodValue);
        Assert.Equal(1, affect.ByteValue);
    }

    [Fact]
    public void ActiveAndIdle_scope_writes_both_pairs()
    {
        var writes = WritesFor(BuffStatOp.Set, 0f, scope: BloodDrainScope.ActiveAndIdle);

        Assert.True(Touches(writes, BuffDrainWrite.OffsetBloodValue));
        Assert.True(Touches(writes, BuffDrainWrite.OffsetBloodIdleValue));
        Assert.True(Touches(writes, BuffDrainWrite.OffsetModificationType));
        Assert.True(Touches(writes, BuffDrainWrite.OffsetModificationIdleType));

        Assert.Equal(1, writes.Single(w => w.Offset == BuffDrainWrite.OffsetAffectBloodValue).ByteValue);
        Assert.Equal(1, writes.Single(w => w.Offset == BuffDrainWrite.OffsetAffectIdleBloodValue).ByteValue);
    }

    /// <summary>
    /// The direct regression for the live defect, phrased as the contract rather than as an
    /// implementation detail: one stat must not mean two modifications unless asked.
    /// </summary>
    [Fact]
    public void Multiply_does_not_default_to_two_modifications()
    {
        var writes = BuffDrainWrite.For(BuffStat.BloodDrain(BuffStatOp.Multiply, 0.1f));

        Assert.False(
            Touches(writes, BuffDrainWrite.OffsetBloodIdleValue),
            "Multiply(0.1) must register ONE modification. Writing the idle value too made it "
            + "0.1 x 0.1 = 0.01x live on 2026-09-13.");
        Assert.Equal(0, writes.Single(w => w.Offset == BuffDrainWrite.OffsetAffectIdleBloodValue).ByteValue);
    }

    [Theory]
    [InlineData(BuffStatOp.Set, BuffDrainWrite.ModificationTypeSet)]
    [InlineData(BuffStatOp.Add, BuffDrainWrite.ModificationTypeAdd)]
    [InlineData(BuffStatOp.Multiply, BuffDrainWrite.ModificationTypeMultiply)]
    public void The_op_maps_onto_the_games_modification_type(BuffStatOp op, byte expected)
    {
        Assert.Equal(expected, BuffDrainWrite.TypeOf(op));
        var writes = WritesFor(op, 1f);
        Assert.Equal(expected, writes.Single(w => w.Offset == BuffDrainWrite.OffsetModificationType).ByteValue);
    }

    /// <summary>
    /// The policy every comment in this area claims and nothing asserted before: the game issues the
    /// three <c>ModificationId</c>s on the instance so its destroy system can reverse exactly this
    /// contribution, and <c>IgnoreIdleDrainWhileActive</c> is not something this repository has
    /// evidence about. Checked for every scope and op, not just the common one.
    /// </summary>
    [Fact]
    public void The_write_set_never_touches_the_modification_ids_or_the_idle_ignore_flag()
    {
        foreach (BloodDrainScope scope in Enum.GetValues(typeof(BloodDrainScope)))
        foreach (BuffStatOp op in Enum.GetValues(typeof(BuffStatOp)))
        {
            var writes = WritesFor(op, 0.5f, priority: 3, scope: scope);
            foreach (int forbidden in BuffDrainWrite.NeverWritten)
            {
                Assert.False(
                    Touches(writes, forbidden),
                    $"scope={scope} op={op} wrote forbidden offset {forbidden}");
            }
        }
    }

    /// <summary>
    /// Pins the layout against the live 36-byte readback.
    /// A drift fails here rather than inside a Burst job.
    /// </summary>
    [Fact]
    public void Offsets_match_the_documented_layout()
    {
        Assert.Equal(0, BuffDrainWrite.OffsetBloodValue);
        Assert.Equal(4, BuffDrainWrite.OffsetBloodIdleValue);
        Assert.Equal(8, BuffDrainWrite.OffsetModificationId);
        Assert.Equal(12, BuffDrainWrite.OffsetIgnoreIdleDrainModId);
        Assert.Equal(16, BuffDrainWrite.OffsetModificationIdleId);
        Assert.Equal(20, BuffDrainWrite.OffsetModificationType);
        Assert.Equal(21, BuffDrainWrite.OffsetModificationIdleType);
        Assert.Equal(24, BuffDrainWrite.OffsetModificationPriority);
        Assert.Equal(28, BuffDrainWrite.OffsetModificationIdlePriority);
        Assert.Equal(32, BuffDrainWrite.OffsetAffectBloodValue);
        Assert.Equal(33, BuffDrainWrite.OffsetAffectIdleBloodValue);
        Assert.Equal(34, BuffDrainWrite.OffsetIgnoreIdleDrainWhileActive);
        Assert.Equal(36, BuffDrainWrite.ComponentSize);
    }

    [Fact]
    public void Every_write_stays_inside_the_component()
    {
        foreach (BloodDrainScope scope in Enum.GetValues(typeof(BloodDrainScope)))
        {
            foreach (var w in WritesFor(BuffStatOp.Add, 2f, priority: 1, scope: scope))
            {
                Assert.True(w.Offset >= 0);
                Assert.True(w.Offset + w.Width <= BuffDrainWrite.ComponentSize,
                    "write " + w + " runs past the " + BuffDrainWrite.ComponentSize + "-byte component");
                Assert.True(w.Width == 1 || w.Width == 4);
            }
        }
    }

    [Fact]
    public void A_suppression_has_no_field_writes()
    {
        Assert.Throws<ArgumentException>(
            () => BuffDrainWrite.For(BuffStat.Suppress(BuffStatKind.MovementSpeed)));
    }

    // -----------------------------------------------------------------
    // Job BUFFP8: suppression of what the cloned base brings with it
    // -----------------------------------------------------------------

    [Fact]
    public void Suppress_movement_speed_names_exactly_the_two_haste_components()
    {
        var set = BuffStatSupport.SuppressionComponents(BuffStatKind.MovementSpeed);

        Assert.Equal(
            new[] { "ModifyMovementSpeedBuff", "ModifyMovementSpeedBuffModification" },
            set.ToArray());
    }

    /// <summary>
    /// The deny-list assertion. Suppression removes components, so scope creep here is the
    /// "blindly strip attached/native lifecycle components" mistake — a future addition to the set
    /// must fail loudly rather than quietly strand an attach or a lifetime.
    /// </summary>
    [Fact]
    public void The_suppression_set_contains_no_attach_or_lifecycle_component()
    {
        foreach (BuffStatKind kind in Enum.GetValues(typeof(BuffStatKind)))
        {
            var set = BuffStatSupport.SuppressionComponents(kind);
            foreach (string name in set)
            {
                Assert.DoesNotContain(name, BuffStatSupport.NeverSuppressed);
            }
        }
    }

    [Fact]
    public void Suppress_is_refused_for_blood_drain_with_a_reason()
    {
        var stat = BuffStat.Suppress(BuffStatKind.BloodDrain);

        Assert.False(stat.IsUsable(out string reason));
        Assert.Contains("not the same thing", reason);
        // It must not silently become BloodDrain(Set, 0f).
        Assert.Contains("BloodDrain(BuffStatOp.Set, 0f)", reason);
    }

    /// <summary>
    /// Suppression and writing ask different questions, and the answers are deliberately inverted
    /// for movement speed: the value cannot be authored (unknown curve and MultiplyAdd semantics)
    /// but the component can be removed, which needs none of that knowledge.
    /// </summary>
    [Fact]
    public void Movement_speed_is_refused_for_writes_and_allowed_for_suppression()
    {
        Assert.False(BuffStat.BloodDrain(BuffStatOp.Set, 0f).Kind == BuffStatKind.MovementSpeed);
        Assert.False(BuffStatSupport.IsSupported(BuffStatKind.MovementSpeed));
        Assert.False(BuffStat.Of(BuffStatKind.MovementSpeed, BuffStatOp.Set, 1f).IsUsable(out _));

        Assert.True(BuffStatSupport.MaySuppress(BuffStatKind.MovementSpeed, out string why));
        Assert.Equal("", why);
        Assert.True(BuffStat.Suppress(BuffStatKind.MovementSpeed).IsUsable(out _));
    }

    /// <summary>
    /// <c>BuffStats.RemoveByName</c> is a string switch behind <c>#if !PROGENITOR_TESTS</c>, so a
    /// name added to <see cref="BuffStatSupport.SuppressionComponents"/> without a matching arm
    /// would silently remove nothing — a suppression that reports success and does not suppress,
    /// which is the same shape of quiet failure as the two original blockers. Source guard, because
    /// that switch cannot be reached from the test assembly.
    /// </summary>
    [Fact]
    public void Every_suppressible_component_has_a_removal_arm()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Buffs", "BuffStats.cs");

        foreach (BuffStatKind kind in Enum.GetValues(typeof(BuffStatKind)))
        {
            foreach (string name in BuffStatSupport.SuppressionComponents(kind))
            {
                // Type-bound, not just name-and-call-present-somewhere: assert the arm maps THIS
                // name to THIS type on one line. Two separate "contains" checks would pass for
                //   "ModifyMovementSpeedBuff" => RemoveIfPresent<ModifyMovementSpeedBuffModification>
                // which silently removes the wrong component.
                string arm = "\"" + name + "\" => RemoveIfPresent<" + name + ">";
                Assert.True(
                    source.Contains(arm, StringComparison.Ordinal),
                    "BuffStatSupport.SuppressionComponents lists '" + name + "' for " + kind
                    + ", so BuffStats.RemoveByName must contain the arm `" + arm
                    + "`. Without it (or with it pointing at a different type) suppression reports "
                    + "success and removes nothing, or removes the wrong component.");
            }
        }
    }

    /// <summary>
    /// Suppression's one real safety property: it may only ever run against the <b>prefab</b>. On a
    /// live instance the `ModificationId`s in those components belong to the game, and removing them
    /// would strand a registered modification with nothing able to reverse it.
    ///
    /// <para>`SuppressStat` is behind `#if !PROGENITOR_TESTS`, so the gate cannot be exercised from
    /// the test assembly — the same situation as the `DestroyTag` prohibition, and it gets the same
    /// treatment. Without this, the single most consequential line in P8 is guarded by nothing.</para>
    /// </summary>
    [Fact]
    public void Suppression_is_gated_on_the_prefab_tag()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Buffs", "BuffStats.cs");
        string body = SourcePin.MethodBody(source, "private static void SuppressStat(");

        Assert.Contains("Has<Prefab>", body, StringComparison.Ordinal);
        Assert.Contains("return;", body, StringComparison.Ordinal);

        // The gate must come before any removal, not after it.
        int gate = body.IndexOf("Has<Prefab>", StringComparison.Ordinal);
        int remove = body.IndexOf("RemoveByName", StringComparison.Ordinal);
        Assert.True(gate >= 0 && remove > gate,
            "SuppressStat must check Has<Prefab> before it removes anything: on a live instance the "
            + "modification ids belong to the game.");
    }

    [Fact]
    public void A_suppression_prints_as_a_suppression()
    {
        Assert.Equal("Suppress(MovementSpeed)", BuffStat.Suppress(BuffStatKind.MovementSpeed).ToString());
    }

    [Fact]
    public void The_two_modification_scope_says_so_in_its_own_description()
    {
        string text = BuffStat.BloodDrain(BuffStatOp.Multiply, 0.1f, 0, BloodDrainScope.ActiveAndIdle)
            .ToString();
        Assert.Contains("two modifications", text);
    }
}
