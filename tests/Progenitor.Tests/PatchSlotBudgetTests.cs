using System;
using System.Reflection;
using Progenitor.Bootstrap;
using Progenitor.Patches;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FHARM7, R41 finding 2. The pool is fixed at 32, <c>Bind</c> is first-come, and Core arms
/// <em>lazily</em> — god mode on the first guard, the world-load epoch postfix on the first world
/// load, the event fallbacks only against a Bootstrap that does not own their sites. Without a
/// reservation a plugin that patched 32 things at load could decide, hours later and silently,
/// that god mode cannot arm (its three prefixes are an atomic group, so two free slots are as
/// good as none). Quarantine made it worse: one-way attrition against the same 32, so a long
/// process could eventually disarm a Core seam at the next Core install with nothing said.
///
/// <para>These tests drive <c>PatchSlots</c> directly, as <c>PatchSlotTests</c> does.</para>
/// </summary>
[Collection("PluginLogger")]
public class PatchSlotBudgetTests : IDisposable
{
    private const string Core = "progenitor.core";
    private const string Plugin = "com.example.hog";

    public PatchSlotBudgetTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        PatchSlots.ReleaseAll();
        PatchSlots.ClearQuarantineForTests();
        PatchSlots.TakeWarning();
    }

    private static PatchSlots.Body Body() => (_, _, _) => null;

    private static int FillWith(string owner, int count)
    {
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            if (PatchSlots.Bind(PatchSlots.KindPrefix, Body(), owner) >= 0) n++;
        }
        return n;
    }

    /// <summary>
    /// Job FHARM8: the reserve is sized against the enumerated list rather than against a
    /// comment. Adding a Core-owned slot registration means adding it to
    /// <see cref="CorePatchBudget.Sites"/>, and this fails the build until
    /// <see cref="PatchSlots.CoreReserve"/> is raised to pay for it — because the alternative is
    /// a live server that refuses a lazily-armed Core guard (god mode's atomic group of three,
    /// the world-load epoch postfix) hours into a session with nothing said at the moment the
    /// budget was overrun.
    /// </summary>
    [Fact]
    public void The_reserve_covers_every_core_owned_patch_that_is_routed_through_the_pool()
    {
        Assert.True(
            PatchSlots.CoreReserve >= CorePatchBudget.Count,
            "CoreReserve (" + PatchSlots.CoreReserve + ") must cover every Core-owned slot "
            + "registration (" + CorePatchBudget.Count + ", see CorePatchBudget.Sites). Raising it "
            + "is a Bootstrap change: restart both.");
        Assert.Equal(PatchSlots.Capacity - PatchSlots.CoreReserve, PatchSlots.NonCoreCapacity);
        Assert.True(PatchSlots.NonCoreCapacity > 0, "the reserve must not swallow the whole pool");
    }

    /// <summary>
    /// The list itself, per module, so that deleting a Core patch is as visible as adding one.
    /// world-load (1) + god mode (3) + EventHooks (3) + FHARM8's four: DeserializeLoadFile's
    /// prefix and postfix (2), SetLanguage (1), BeginCreateServerWorld (1); NODESCCHAT's join-chat prefix (1).
    /// </summary>
    [Fact]
    public void The_core_patch_budget_lists_every_slot_routed_core_site()
    {
        Assert.Equal(12, CorePatchBudget.Count);
        Assert.Equal(1, CorePatchBudget.CountFor("world"));
        Assert.Equal(3, CorePatchBudget.CountFor("godmode"));
        Assert.Equal(3, CorePatchBudget.CountFor(CorePatchFallbackPolicy.ModuleEvents));
        Assert.Equal(2, CorePatchBudget.CountFor(CorePatchFallbackPolicy.ModuleContent));
        Assert.Equal(1, CorePatchBudget.CountFor(CorePatchFallbackPolicy.ModuleLocalization));
        // TriggerSave is deliberately absent: it needs __result, so it is refused, not slotted.
        Assert.Equal(1, CorePatchBudget.CountFor(CorePatchFallbackPolicy.ModulePersistence));
        Assert.Equal(0, CorePatchBudget.CountFor(CorePatchFallbackPolicy.ModuleHosted));
        Assert.Equal(0, CorePatchBudget.CountFor(CorePatchFallbackPolicy.ModuleSystemHooks));
        Assert.Equal(1, CorePatchBudget.CountFor("serverlist"));
        Assert.Contains("core-patches: 12", CorePatchBudget.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The reserve is not merely large enough on paper: Core can actually take every slot the
    /// budget lists after a plugin has filled the pool to its cap.
    /// </summary>
    [Fact]
    public void Core_can_take_its_whole_budget_after_a_plugin_has_filled_the_pool()
    {
        FillWith(Plugin, PatchSlots.Capacity);

        Assert.Equal(CorePatchBudget.Count, FillWith(Core, CorePatchBudget.Count));
    }

    [Fact]
    public void A_plugin_cannot_take_the_last_reserved_slots()
    {
        int taken = FillWith(Plugin, PatchSlots.Capacity);

        Assert.Equal(PatchSlots.NonCoreCapacity, taken);
        Assert.Equal(0, PatchSlots.NonCoreFree);
        Assert.Equal(PatchSlots.CoreReserve, PatchSlots.Free);
        Assert.Equal(PatchSlots.NoSlot, PatchSlots.Bind(PatchSlots.KindPrefix, Body(), Plugin));
    }

    [Fact]
    public void Core_still_gets_its_slots_after_a_plugin_has_taken_everything_it_may()
    {
        FillWith(Plugin, PatchSlots.Capacity);

        int core = FillWith(Core, PatchSlots.CoreReserve);

        Assert.Equal(PatchSlots.CoreReserve, core);
        Assert.Equal(PatchSlots.CoreReserve, PatchSlots.CoreInUse);
        Assert.Equal(0, PatchSlots.Free);
    }

    [Fact]
    public void A_refusal_caused_by_the_reserve_is_distinguishable_from_real_exhaustion()
    {
        FillWith(Plugin, PatchSlots.Capacity);
        Assert.True(PatchSlots.ReservedForCore(Plugin));
        Assert.False(PatchSlots.ReservedForCore(Core));

        // Once Core has taken its reserve too, the pool really is empty and the reason changes.
        FillWith(Core, PatchSlots.CoreReserve);
        Assert.False(PatchSlots.ReservedForCore(Plugin));
        Assert.NotEqual(PatchSitePolicy.ReasonSlotsReserved, PatchSitePolicy.ReasonSlotsExhausted);
    }

    [Fact]
    public void Releasing_a_core_slot_gives_the_reserve_back()
    {
        long token = PatchSlots.Bind(PatchSlots.KindPrefix, Body(), Core);
        Assert.Equal(1, PatchSlots.CoreInUse);

        Assert.True(PatchSlots.Release(token, Core));
        Assert.Equal(0, PatchSlots.CoreInUse);
        Assert.Equal(PatchSlots.NonCoreCapacity, PatchSlots.NonCoreFree);
    }

    [Fact]
    public void ReleaseOwner_and_ReleaseAll_keep_the_core_count_straight()
    {
        FillWith(Core, 3);
        FillWith(Plugin, 2);
        Assert.Equal(3, PatchSlots.CoreInUse);

        PatchSlots.ReleaseOwner(Core);
        Assert.Equal(0, PatchSlots.CoreInUse);
        Assert.Equal(2, PatchSlots.InUse);

        FillWith(Core, 2);
        PatchSlots.ReleaseAll();
        Assert.Equal(0, PatchSlots.CoreInUse);
        Assert.Equal(0, PatchSlots.InUse);
    }

    [Fact]
    public void Quarantine_attrition_is_charged_to_the_plugin_budget_before_the_core_reserve()
    {
        long token = PatchSlots.Bind(PatchSlots.KindPrefix, Body(), Plugin);
        Assert.True(PatchSlots.Quarantine(token, Plugin));

        Assert.Equal(1, PatchSlots.QuarantineCount);
        Assert.True(PatchSlots.CoreReserveIntact);
        // One slot fewer for plugins; the reserve is untouched.
        Assert.Equal(PatchSlots.NonCoreCapacity - 1, PatchSlots.NonCoreFree);
        Assert.Equal(PatchSlots.Capacity - 1, PatchSlots.Free);
    }

    [Fact]
    public void Every_quarantine_warns_and_the_line_says_whether_the_reserve_is_still_intact()
    {
        long token = PatchSlots.Bind(PatchSlots.KindPrefix, Body(), Plugin);
        PatchSlots.Quarantine(token, Plugin);

        string? warning = PatchSlots.TakeWarning();
        Assert.NotNull(warning);
        Assert.Contains("out of service for the life of the process", warning!, StringComparison.Ordinal);
        Assert.Contains("core reserve is intact", warning!, StringComparison.Ordinal);
        // Drained: it is one warning, not a sticky one.
        Assert.Null(PatchSlots.TakeWarning());
    }

    [Fact]
    public void Attrition_past_the_plugin_budget_says_it_has_reached_the_core_reserve()
    {
        string? last = null;
        for (int i = 0; i < PatchSlots.NonCoreCapacity + 1; i++)
        {
            long token = PatchSlots.Bind(PatchSlots.KindPrefix, Body(), Plugin);
            if (token < 0) token = PatchSlots.Bind(PatchSlots.KindPrefix, Body(), Core);
            Assert.True(token >= 0, "the pool should still have had a slot at " + i);
            Assert.True(PatchSlots.Quarantine(token, null));
            last = PatchSlots.TakeWarning() ?? last;
        }

        Assert.False(PatchSlots.CoreReserveIntact);
        Assert.NotNull(last);
        Assert.Contains("ATTRITION HAS REACHED THE CORE RESERVE", last!, StringComparison.Ordinal);
        Assert.Contains("RESERVE-BREACHED", PatchSlots.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Job FHARM8, R42 finding 2. The threshold used to be measured against total <c>Free</c>,
    /// which sits <em>above</em> <c>CoreReserve</c> — so a plugin was refused before the warning
    /// that exists to precede that refusal could fire, and this test had to fill with Core to
    /// construct its own fixture, which was the finding stated as a fixture requirement. The
    /// crossing is now the plugin budget, and plugins alone can reach it.
    /// </summary>
    [Fact]
    public void The_plugin_budget_warns_before_the_first_plugin_refusal_and_once_per_crossing()
    {
        FillWith(Plugin, PatchSlots.NonCoreCapacity - PatchSlots.LowFreeWarnThreshold - 1);
        Assert.Null(PatchSlots.TakeWarning());

        // The bind that takes the plugin budget down to the threshold warns - and no plugin has
        // been refused yet, which is the property the warning exists for.
        FillWith(Plugin, 1);
        string? first = PatchSlots.TakeWarning();
        Assert.NotNull(first);
        Assert.Contains("patch-slots: WARNING plugin budget", first!, StringComparison.Ordinal);
        Assert.Contains("slots-exhausted", first!, StringComparison.Ordinal);
        Assert.True(PatchSlots.NonCoreFree > 0, "the warning must precede the refusal, not follow it");

        // ...and the next bind below it does not warn again.
        FillWith(Plugin, 1);
        Assert.Null(PatchSlots.TakeWarning());

        // Releasing back above the threshold re-arms the warning for the next crossing.
        PatchSlots.ReleaseOwner(Plugin);
        Assert.True(PatchSlots.NonCoreFree > PatchSlots.LowFreeWarnThreshold);
        FillWith(Plugin, PatchSlots.Capacity);
        Assert.NotNull(PatchSlots.TakeWarning());
    }

    /// <summary>
    /// R42 finding 2, the other half: <c>Bind</c>'s reservation guard returned before the
    /// accounting, so a refused bind raised nothing at all. A pool filled in one burst — no
    /// intermediate bind lands on the threshold — must still say something at the refusal.
    /// </summary>
    [Fact]
    public void A_refused_plugin_bind_raises_the_warning_even_when_no_successful_bind_crossed()
    {
        FillWith(Plugin, PatchSlots.NonCoreCapacity);
        PatchSlots.TakeWarning(); // drain whatever the fill itself raised

        Assert.Equal(PatchSlots.NoSlot, PatchSlots.Bind(PatchSlots.KindPrefix, Body(), Plugin));

        string? warning = PatchSlots.TakeWarning();
        Assert.NotNull(warning);
        Assert.Contains("plugin budget 0/" + PatchSlots.NonCoreCapacity, warning!, StringComparison.Ordinal);
    }

    /// <summary>
    /// R42 finding 2, the sharper edge. A plugin starved purely by <em>another</em> plugin's
    /// failed unpatches was refused with <c>slots-reserved</c>, whose remedy is "unload a plugin
    /// or raise the capacity" — and neither frees a quarantined slot. The two states are now
    /// distinct, and only the quarantine one says "restart".
    /// </summary>
    [Fact]
    public void Quarantine_starvation_is_a_different_refusal_from_the_core_reserve()
    {
        for (int i = 0; i < PatchSlots.NonCoreCapacity; i++)
        {
            long token = PatchSlots.Bind(PatchSlots.KindPrefix, Body(), Plugin);
            Assert.True(token >= 0);
            Assert.True(PatchSlots.Quarantine(token, null));
        }

        // No plugin holds anything, yet the plugin budget is gone.
        Assert.Equal(0, PatchSlots.NonCoreInUse);
        Assert.Equal(0, PatchSlots.NonCoreFree);
        Assert.True(PatchSlots.Free > 0);

        Assert.True(PatchSlots.LostToQuarantine(Plugin));
        Assert.False(PatchSlots.ReservedForCore(Plugin));
        Assert.False(PatchSlots.LostToQuarantine(Core));

        string remedy = PatchSitePolicy.SlotRefusalRemedy(PatchSitePolicy.ReasonSlotsLostToQuarantine);
        Assert.Contains("Restart the process", remedy, StringComparison.Ordinal);
        Assert.Contains("will NOT free them", remedy, StringComparison.Ordinal);
        Assert.DoesNotContain("Restart the process", PatchSitePolicy.SlotRefusalRemedy(PatchSitePolicy.ReasonSlotsReserved), StringComparison.Ordinal);
    }

    /// <summary>
    /// The plugin budget gone with plugins actually holding it is still <c>slots-reserved</c>,
    /// whose remedy (unload a plugin) is the right one.
    /// </summary>
    [Fact]
    public void A_plugin_starved_by_other_plugins_is_still_told_to_unload_one()
    {
        FillWith(Plugin, PatchSlots.NonCoreCapacity);

        Assert.True(PatchSlots.ReservedForCore(Plugin));
        Assert.False(PatchSlots.LostToQuarantine(Plugin));
        Assert.Contains("Unload a plugin", PatchSitePolicy.SlotRefusalRemedy(PatchSitePolicy.ReasonSlotsReserved), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_states_the_budget_so_a_status_line_can_be_read_without_the_source()
    {
        FillWith(Core, 2);
        FillWith(Plugin, 3);

        string line = PatchSlots.Describe();
        Assert.Contains("in-use=5/32", line, StringComparison.Ordinal);
        Assert.Contains("core=2", line, StringComparison.Ordinal);
        Assert.Contains("plugin-free=" + PatchSlots.NonCoreFree + "/" + PatchSlots.NonCoreCapacity, line, StringComparison.Ordinal);
        Assert.Contains("reserve=" + PatchSlots.CoreReserve, line, StringComparison.Ordinal);
        Assert.DoesNotContain("RESERVE-BREACHED", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_core_owner_is_recognised_by_the_suffix_the_registry_actually_uses()
    {
        Assert.Equal(PatchSitePolicy.CoreOwnerSuffix, PatchSlots.CoreOwnerSuffix);
        Assert.True(PatchSlots.IsCoreOwner("progenitor" + PatchSitePolicy.CoreOwnerSuffix));
        Assert.False(PatchSlots.IsCoreOwner("com.example.core.plugin"));
        Assert.False(PatchSlots.IsCoreOwner(""));
        Assert.False(PatchSlots.IsCoreOwner(null));
    }
}

/// <summary>
/// Job FHARM7, R41. The <c>__args</c> adaptation every slot-routed body performs: resolve a
/// parameter by <b>name</b> at registration time (a slot body is handed <c>__args</c>
/// positionally, so the binding Harmony would have done has to happen up front), read it, and —
/// for a by-ref parameter — write the changed value back.
///
/// <para>R41 noted that the fail-closed claim behind this had no test, because it was written
/// privately inside <c>GodMode</c>, which references <c>ProjectM</c> and cannot be linked here.
/// The mechanism now lives in <see cref="SlotArgs"/> / <see cref="PatchRegistry.ArgIndex"/> and
/// is tested; <c>GodMode</c>'s private copies are a follow-up (that file is outside FHARM7's
/// allowed set).</para>
/// </summary>
public class SlotArgsTests
{
    private struct Health
    {
        public float Value;
    }

    private struct Ent
    {
        public int Index;
    }

    // Stands in for a game method: what matters is the parameter names and order.
    private static void KillEntity(Ent entity, ref Health health, bool killed) { _ = entity; _ = health; _ = killed; }

    private static MethodInfo Target => typeof(SlotArgsTests).GetMethod(
        nameof(KillEntity), BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void An_argument_is_found_by_its_name_not_its_position()
    {
        Assert.Equal(0, PatchRegistry.ArgIndex(Target, "entity"));
        Assert.Equal(1, PatchRegistry.ArgIndex(Target, "health"));
        Assert.Equal(2, PatchRegistry.ArgIndex(Target, "killed"));
    }

    [Fact]
    public void A_name_that_is_not_on_this_build_is_minus_one_and_never_a_guessed_position()
    {
        Assert.Equal(-1, PatchRegistry.ArgIndex(Target, "renamedInAPatch"));
        Assert.Equal(-1, PatchRegistry.ArgIndex(Target, "Entity"));   // case-sensitive on purpose
        Assert.Equal(-1, PatchRegistry.ArgIndex(Target, ""));
        Assert.Equal(-1, PatchRegistry.ArgIndex(null, "entity"));
    }

    [Fact]
    public void Reading_an_argument_of_the_expected_type_succeeds()
    {
        object?[] args = { new Ent { Index = 5 }, new Health { Value = 12f }, true };

        Assert.True(SlotArgs.TryArg<Ent>(args, 0, out var e));
        Assert.Equal(5, e.Index);
        Assert.True(SlotArgs.TryArg<Health>(args, 1, out var h));
        Assert.Equal(12f, h.Value);
    }

    [Fact]
    public void Reading_the_wrong_type_or_a_bad_index_fails_closed_and_yields_default()
    {
        object?[] args = { new Ent { Index = 5 }, null, true };

        Assert.False(SlotArgs.TryArg<Health>(args, 0, out var wrongType));
        Assert.Equal(default, wrongType.Value);
        Assert.False(SlotArgs.TryArg<Health>(args, 1, out _));      // null argument
        Assert.False(SlotArgs.TryArg<Ent>(args, 9, out _));         // past the end
        Assert.False(SlotArgs.TryArg<Ent>(args, -1, out _));        // an unresolved ArgIndex
        Assert.False(SlotArgs.TryArg<Ent>(null, 0, out _));         // no args at all
    }

    [Fact]
    public void A_by_ref_parameter_is_propagated_by_writing_it_back_into_args()
    {
        object?[] args = { new Ent(), new Health { Value = -3f }, true };
        int health = PatchRegistry.ArgIndex(Target, "health");

        Assert.True(SlotArgs.TryArg<Health>(args, health, out var h));
        h.Value = 1f;                                    // what a clamp does
        Assert.True(SlotArgs.WriteBack(args, health, h));

        Assert.True(SlotArgs.TryArg<Health>(args, health, out var after));
        Assert.Equal(1f, after.Value);
    }

    [Fact]
    public void A_write_back_to_an_index_that_does_not_exist_reports_failure_rather_than_throwing()
    {
        object?[] args = { new Health() };

        Assert.False(SlotArgs.WriteBack(args, -1, new Health { Value = 1f }));
        Assert.False(SlotArgs.WriteBack(args, 4, new Health { Value = 1f }));
        Assert.False(SlotArgs.WriteBack(null, 0, new Health { Value = 1f }));
    }

    [Fact]
    public void TryNamedArg_does_both_halves_and_fails_closed_on_a_renamed_parameter()
    {
        object?[] args = { new Ent { Index = 7 }, new Health(), true };

        Assert.True(SlotArgs.TryNamedArg<Ent>(Target, args, "entity", out var e));
        Assert.Equal(7, e.Index);
        Assert.False(SlotArgs.TryNamedArg<Ent>(Target, args, "renamedInAPatch", out _));
    }

    [Fact]
    public void The_two_warning_lines_name_the_method_the_parameter_and_what_is_lost()
    {
        string missing = SlotArgs.FormatMissingParameter(
            "KillEntity", "health", "the whole kill-path group is refused");
        Assert.Contains("has no parameter named 'health'", missing, StringComparison.Ordinal);
        Assert.Contains("the whole kill-path group is refused", missing, StringComparison.Ordinal);
        Assert.Contains("guessed position", missing, StringComparison.Ordinal);

        string wrong = SlotArgs.FormatWrongArgumentType("godmode.killentity", "entity", 1, "oops", typeof(Ent));
        Assert.Contains("argument 'entity' at position 1", wrong, StringComparison.Ordinal);
        Assert.Contains("System.String", wrong, StringComparison.Ordinal);
        Assert.Contains("allowed through", wrong, StringComparison.Ordinal);
    }
}
