using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job NATIVE1 — game-free qualification of the native MODS outer-tab helper
/// (<c>NativeOuterTabRegistration.cs</c>): the shape invariants, the attach transaction's
/// rollback, state/index alignment, the detach order, foreign-list preservation, rebuild
/// detection with callback ownership, route selection, and the tick state machine. These
/// qualify the helper logic, not Unity; the live acceptance list is in the job report.
/// </summary>
public sealed class NativeOuterTabRegistrationTests
{
    private static readonly int[] Native = { 0, 1, 2, 3, 4, 5, 6 };

    // ---- invariants -------------------------------------------------------------------------

    [Fact]
    public void Pristine_seven_native_states_in_both_lists_validate()
    {
        Assert.Null(NativeOuterTabShape.Validate(Native, Native, 7));
    }

    /// <summary>
    /// Removing the owned-slot check lets a caller register state 8 at index 7: SetState(7)
    /// would then find nothing and gamepad index 7 would select a different page.
    /// </summary>
    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(-1)]
    public void Owned_slot_must_be_seven(int slot)
    {
        string? reason = NativeOuterTabShape.Validate(Native, Native, slot);
        Assert.NotNull(reason);
        Assert.StartsWith(NativeOuterTabShape.ReasonOwnedSlot, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_lists_are_named()
    {
        Assert.Equal(NativeOuterTabShape.ReasonListMissing, NativeOuterTabShape.Validate(null, Native, 7));
        Assert.Equal(NativeOuterTabShape.ReasonViewsMissing, NativeOuterTabShape.Validate(Native, null, 7));
    }

    [Fact]
    public void A_missing_native_state_is_a_count_failure_that_names_the_list()
    {
        int[] six = { 0, 1, 2, 3, 4, 5 };
        Assert.Equal(NativeOuterTabShape.ReasonListCount + "6", NativeOuterTabShape.Validate(six, Native, 7));
        Assert.Equal(NativeOuterTabShape.ReasonViewsCount + "6", NativeOuterTabShape.Validate(Native, six, 7));
    }

    [Fact]
    public void An_extra_foreign_state_is_a_count_failure_not_a_removal()
    {
        int[] eight = { 0, 1, 2, 3, 4, 5, 6, 9 };
        Assert.Equal(NativeOuterTabShape.ReasonListCount + "8", NativeOuterTabShape.Validate(eight, Native, 7));
        Assert.Equal(NativeOuterTabShape.ReasonViewsCount + "8", NativeOuterTabShape.Validate(Native, eight, 7));
    }

    /// <summary>
    /// Reordered native slots: gamepad navigation wraps by list count and skips slot 4 by
    /// index, so a swapped list is not a shape this build registers into.
    /// </summary>
    [Fact]
    public void Reordered_native_states_fail_and_name_the_index()
    {
        int[] swapped = { 0, 1, 3, 2, 4, 5, 6 };
        Assert.Equal(NativeOuterTabShape.ReasonListOrder + "2=3", NativeOuterTabShape.Validate(swapped, Native, 7));
        Assert.Equal(NativeOuterTabShape.ReasonViewsOrder + "2=3", NativeOuterTabShape.Validate(Native, swapped, 7));
    }

    /// <summary>Another registrant already at 7 (or a stale one of ours) is reported before any count check.</summary>
    [Fact]
    public void A_competing_slot_seven_is_reported_wherever_it_sits()
    {
        int[] listWithSeven = { 0, 1, 2, 3, 4, 5, 6, 7 };
        int[] viewsWithSeven = { 7, 0, 1, 2, 3, 4, 5, 6 };
        Assert.Equal(NativeOuterTabShape.ReasonCompetingSlot + "outer-list@7", NativeOuterTabShape.Validate(listWithSeven, Native, 7));
        Assert.Equal(NativeOuterTabShape.ReasonCompetingSlot + "views@0", NativeOuterTabShape.Validate(Native, viewsWithSeven, 7));
    }

    [Fact]
    public void Native_prefix_check_tolerates_foreign_appends_but_not_reorders()
    {
        Assert.True(NativeOuterTabShape.NativePrefixIntact(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
        Assert.True(NativeOuterTabShape.NativePrefixIntact(Native));
        Assert.False(NativeOuterTabShape.NativePrefixIntact(new[] { 0, 1, 2, 3, 4, 5 }));
        Assert.False(NativeOuterTabShape.NativePrefixIntact(new[] { 1, 0, 2, 3, 4, 5, 6 }));
        Assert.False(NativeOuterTabShape.NativePrefixIntact(null));
    }

    // ---- state / index alignment -------------------------------------------------------------

    [Fact]
    public void Owned_state_equals_native_count_so_state_views_index_and_list_index_agree()
    {
        Assert.Equal(7, NativeOuterTabShape.OwnedState);
        Assert.Equal(7, NativeOuterTabShape.NativeStateCount);
        Assert.Equal(6, NativeOuterTabShape.SystemState);
    }

    /// <summary>
    /// The return target is never the owned state and never outside 0..6: SetState of a
    /// missing state destroys the old view-state entity and hides the old page without
    /// selecting a new one.
    /// </summary>
    [Theory]
    [InlineData(null, 6)]
    [InlineData(2, 2)]
    [InlineData(0, 0)]
    [InlineData(7, 6)]
    [InlineData(-1, 6)]
    [InlineData(42, 6)]
    public void Return_state_is_the_saved_native_page_or_System(int? saved, int expected)
    {
        Assert.Equal(expected, NativeOuterTabShape.ReturnStateFor(saved));
    }

    // ---- transaction rollback ----------------------------------------------------------------

    private sealed class RecordingSteps : INativeOuterTabAttachSteps
    {
        private readonly NativeOuterTabAttachStep? _failAt;
        private readonly bool _throw;
        internal readonly List<NativeOuterTabAttachStep> Ran = new();
        internal readonly List<NativeOuterTabAttachStep> RolledBack = new();

        internal RecordingSteps(NativeOuterTabAttachStep? failAt, bool throwInstead = false)
        {
            _failAt = failAt;
            _throw = throwInstead;
        }

        public string? Run(NativeOuterTabAttachStep step)
        {
            Ran.Add(step);
            if (step != _failAt) return null;
            if (_throw) throw new InvalidOperationException("step blew up");
            return "refused";
        }

        public void Rollback(NativeOuterTabAttachStep step) => RolledBack.Add(step);
    }

    [Fact]
    public void All_seven_steps_run_in_order_and_nothing_rolls_back_on_success()
    {
        var steps = new RecordingSteps(null);
        var result = NativeOuterTabTransaction.Run(steps);
        Assert.True(result.Ok);
        Assert.Null(result.Reason);
        Assert.Null(result.FailedAt);
        Assert.Equal(NativeOuterTabTransaction.Order, steps.Ran);
        Assert.Empty(steps.RolledBack);
        Assert.Equal(NativeOuterTabAttachStep.Expose, NativeOuterTabTransaction.Order[^1]);
    }

    /// <summary>
    /// A failure at any step rolls back exactly the completed steps, in reverse order, names
    /// the step in the reason, and never reaches Expose. Removing the rollback loop leaves a
    /// half-registered tab (a cloned entry with no page, or a published record with no click).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void A_failure_rolls_back_every_completed_step_in_reverse(int failAtIndex)
    {
        var failAt = NativeOuterTabTransaction.Order[failAtIndex];
        var steps = new RecordingSteps(failAt);
        var result = NativeOuterTabTransaction.Run(steps);
        Assert.False(result.Ok);
        Assert.Equal(failAt, result.FailedAt);
        Assert.Equal(NativeOuterTabTransaction.StepName(failAt) + ":refused", result.Reason);
        var completed = NativeOuterTabTransaction.Order.TakeWhile(s => s != failAt).ToArray();
        Assert.Equal(Enumerable.Reverse(completed), steps.RolledBack);
        Assert.Equal(Enumerable.Reverse(completed), result.RolledBack);
        Assert.DoesNotContain(failAt, steps.RolledBack);
        if (failAt != NativeOuterTabAttachStep.Expose)
            Assert.DoesNotContain(NativeOuterTabAttachStep.Expose, steps.Ran);
    }

    [Fact]
    public void A_step_that_throws_is_a_failure_with_the_message_not_an_escape()
    {
        var steps = new RecordingSteps(NativeOuterTabAttachStep.WireClick, throwInstead: true);
        var result = NativeOuterTabTransaction.Run(steps);
        Assert.False(result.Ok);
        Assert.Equal("wire-click:threw:step blew up", result.Reason);
        Assert.Equal(
            new[] { NativeOuterTabAttachStep.AppendViews, NativeOuterTabAttachStep.CreatePage, NativeOuterTabAttachStep.CloneDonor, NativeOuterTabAttachStep.Validate },
            steps.RolledBack);
    }

    [Fact]
    public void Step_names_are_the_kebab_case_reason_prefixes()
    {
        Assert.Equal("validate", NativeOuterTabTransaction.StepName(NativeOuterTabAttachStep.Validate));
        Assert.Equal("clone-donor", NativeOuterTabTransaction.StepName(NativeOuterTabAttachStep.CloneDonor));
        Assert.Equal("create-page", NativeOuterTabTransaction.StepName(NativeOuterTabAttachStep.CreatePage));
        Assert.Equal("append-views", NativeOuterTabTransaction.StepName(NativeOuterTabAttachStep.AppendViews));
        Assert.Equal("wire-click", NativeOuterTabTransaction.StepName(NativeOuterTabAttachStep.WireClick));
        Assert.Equal("publish", NativeOuterTabTransaction.StepName(NativeOuterTabAttachStep.Publish));
        Assert.Equal("expose", NativeOuterTabTransaction.StepName(NativeOuterTabAttachStep.Expose));
        Assert.Equal("publish:views-changed", NativeOuterTabTransaction.FailureReason(NativeOuterTabAttachStep.Publish, "views-changed"));
    }

    // ---- detach order ------------------------------------------------------------------------

    [Fact]
    public void Detach_while_selected_restores_and_verifies_before_removing_anything()
    {
        var order = NativeOuterTabDetachPlan.Order(menuAlive: true, ownedSelected: true, workspaceOpen: true);
        Assert.Equal(
            new[]
            {
                NativeOuterTabDetachPhase.DisableActivation,
                NativeOuterTabDetachPhase.CloseWorkspace,
                NativeOuterTabDetachPhase.RestoreSelection,
                NativeOuterTabDetachPhase.VerifySelection,
                NativeOuterTabDetachPhase.ClearStaleCurrentView,
                NativeOuterTabDetachPhase.ResetSelectedIndex,
                NativeOuterTabDetachPhase.RemoveListEntry,
                NativeOuterTabDetachPhase.RestoreViews,
                NativeOuterTabDetachPhase.ReleaseClick,
                NativeOuterTabDetachPhase.DestroyOwned,
                NativeOuterTabDetachPhase.Invalidate,
            },
            order);
        int verify = Array.IndexOf(order, NativeOuterTabDetachPhase.VerifySelection);
        Assert.True(verify < Array.IndexOf(order, NativeOuterTabDetachPhase.RemoveListEntry));
        Assert.True(verify < Array.IndexOf(order, NativeOuterTabDetachPhase.RestoreViews));
        Assert.True(verify < Array.IndexOf(order, NativeOuterTabDetachPhase.DestroyOwned));
    }

    [Fact]
    public void Detach_when_not_selected_skips_the_transition_but_still_resets_the_remembered_index()
    {
        var order = NativeOuterTabDetachPlan.Order(menuAlive: true, ownedSelected: false, workspaceOpen: false);
        Assert.DoesNotContain(NativeOuterTabDetachPhase.RestoreSelection, order);
        Assert.DoesNotContain(NativeOuterTabDetachPhase.VerifySelection, order);
        Assert.DoesNotContain(NativeOuterTabDetachPhase.CloseWorkspace, order);
        Assert.Contains(NativeOuterTabDetachPhase.ResetSelectedIndex, order);
        Assert.Equal(NativeOuterTabDetachPhase.DisableActivation, order[0]);
        Assert.Equal(NativeOuterTabDetachPhase.Invalidate, order[^1]);
    }

    /// <summary>A destroyed HUD/world never gets its stale mapper called: no SetState, no list or Views writes.</summary>
    [Fact]
    public void Detach_with_a_dead_menu_touches_no_mapper_state()
    {
        var order = NativeOuterTabDetachPlan.Order(menuAlive: false, ownedSelected: true, workspaceOpen: true, mapperUsable: false);
        Assert.Equal(
            new[]
            {
                NativeOuterTabDetachPhase.CloseWorkspace,
                NativeOuterTabDetachPhase.ReleaseClick,
                NativeOuterTabDetachPhase.DestroyOwned,
                NativeOuterTabDetachPhase.Invalidate,
            },
            order);
    }

    /// <summary>
    /// NATIVE3 §3: the mapper outlives the menu object. With the menu gone but the mapper still
    /// the live client's, the only mapper state touched is the typed index write that takes a
    /// remembered 7 back to a native index — no SetState, no list or Views writes.
    /// </summary>
    [Fact]
    public void Detach_with_a_dead_menu_and_a_live_mapper_only_resets_the_remembered_index()
    {
        var order = NativeOuterTabDetachPlan.Order(menuAlive: false, ownedSelected: true, workspaceOpen: false, mapperUsable: true);
        Assert.Equal(
            new[]
            {
                NativeOuterTabDetachPhase.ResetSelectedIndex,
                NativeOuterTabDetachPhase.ReleaseClick,
                NativeOuterTabDetachPhase.DestroyOwned,
                NativeOuterTabDetachPhase.Invalidate,
            },
            order);
        // The three-argument overload keeps the pre-NATIVE3 meaning of a usable mapper.
        Assert.Equal(order, NativeOuterTabDetachPlan.Order(menuAlive: false, ownedSelected: true, workspaceOpen: false));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void Removal_waits_for_a_verified_move_off_the_owned_page(bool selected, bool restored, bool mayRemove)
    {
        Assert.Equal(mayRemove, NativeOuterTabDetachPlan.MayRemoveRegistration(selected, restored));
    }

    [Fact]
    public void Views_restore_puts_the_original_back_only_when_the_live_array_is_still_ours()
    {
        Assert.Equal(NativeOuterTabViewsRestore.RestoreOriginal, NativeOuterTabDetachPlan.DecideViewsRestore(true, true, true));
        Assert.Equal(NativeOuterTabViewsRestore.FilterOwned, NativeOuterTabDetachPlan.DecideViewsRestore(true, false, true));
        Assert.Equal(NativeOuterTabViewsRestore.Skip, NativeOuterTabDetachPlan.DecideViewsRestore(true, false, false));
        Assert.Equal(NativeOuterTabViewsRestore.Skip, NativeOuterTabDetachPlan.DecideViewsRestore(false, true, true));
    }

    /// <summary>
    /// OnUpdate re-selects <c>_SelectedOuterTab</c> when no view is current, so a stopped mapper
    /// that remembers 7 is pointed at the return state; a native index is left alone.
    /// </summary>
    [Theory]
    [InlineData(7, 2, 2)]
    [InlineData(7, null, 6)]
    [InlineData(3, 2, 3)]
    [InlineData(0, null, 0)]
    public void Remembered_index_seven_is_replaced_by_the_return_state(int selected, int? returnState, int expected)
    {
        Assert.Equal(expected, NativeOuterTabDetachPlan.SelectedIndexAfterDetach(selected, returnState));
    }

    // ---- reconcile: rebuild detection, foreign preservation, callback ownership ---------------

    private static NativeOuterTabReconcileInput Input(
        bool alive = true, int[]? list = null, int ownedList = 7, int ownedViews = 7, bool present = true, bool ours = true)
        => new(alive, list ?? new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, ownedList, ownedViews, present, ours);

    [Fact]
    public void Intact_registration_needs_nothing()
    {
        var (action, reason) = NativeOuterTabReconcile.Decide(Input());
        Assert.Equal(NativeOuterTabReconcileAction.Intact, action);
        Assert.Equal("", reason);
    }

    /// <summary>
    /// A native rebuild re-discovers the active owned clone and installs its own
    /// <c>SetState(item.State)</c> closure; that closure is correct for state 7, so no second
    /// callback is wired. Removing this branch double-wires the tab after every rebuild.
    /// </summary>
    [Fact]
    public void A_native_closure_on_the_owned_entry_is_adopted_not_duplicated()
    {
        var (action, reason) = NativeOuterTabReconcile.Decide(Input(present: true, ours: false));
        Assert.Equal(NativeOuterTabReconcileAction.AdoptNativeClosure, action);
        Assert.Equal(NativeOuterTabReconcile.ReasonCallbackNative, reason);
    }

    [Fact]
    public void An_empty_click_field_is_rewired_with_the_leased_action()
    {
        var (action, reason) = NativeOuterTabReconcile.Decide(Input(present: false, ours: false));
        Assert.Equal(NativeOuterTabReconcileAction.Rewire, action);
        Assert.Equal(NativeOuterTabReconcile.ReasonCallbackMissing, reason);
    }

    [Fact]
    public void A_rebuilt_list_with_the_seven_native_entries_gets_the_owned_entry_appended_again()
    {
        var (action, reason) = NativeOuterTabReconcile.Decide(Input(list: Native, ownedList: -1));
        Assert.Equal(NativeOuterTabReconcileAction.Reappend, action);
        Assert.Equal(NativeOuterTabReconcile.ReasonListRebuilt, reason);
    }

    /// <summary>A foreign entry in the rebuilt list is preserved: we detach rather than edit around it.</summary>
    [Fact]
    public void A_rebuilt_list_with_foreign_entries_detaches_and_preserves_them()
    {
        var (action, reason) = NativeOuterTabReconcile.Decide(Input(list: new[] { 0, 1, 2, 3, 4, 5, 6, 9 }, ownedList: -1));
        Assert.Equal(NativeOuterTabReconcileAction.Detach, action);
        Assert.StartsWith(NativeOuterTabReconcile.ReasonListForeign, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_owned_entry_at_any_index_but_seven_detaches()
    {
        var (action, reason) = NativeOuterTabReconcile.Decide(Input(list: new[] { 0, 1, 2, 3, 4, 5, 7, 6 }, ownedList: 6));
        Assert.Equal(NativeOuterTabReconcileAction.Detach, action);
        Assert.Equal(NativeOuterTabReconcile.ReasonListIndex + "6", reason);
    }

    [Fact]
    public void A_lost_or_moved_views_record_detaches_before_the_list_is_even_read()
    {
        var (action, reason) = NativeOuterTabReconcile.Decide(Input(list: null, ownedViews: -1) with { ListStates = null });
        Assert.Equal(NativeOuterTabReconcileAction.Detach, action);
        Assert.Equal(NativeOuterTabReconcile.ReasonViewsLost + "-1", reason);
        (action, reason) = NativeOuterTabReconcile.Decide(Input(ownedViews: 3));
        Assert.Equal(NativeOuterTabReconcileAction.Detach, action);
        Assert.Equal(NativeOuterTabReconcile.ReasonViewsLost + "3", reason);
    }

    [Fact]
    public void A_dead_menu_detaches_without_reading_lists()
    {
        var (action, reason) = NativeOuterTabReconcile.Decide(new NativeOuterTabReconcileInput(false, null, -1, -1, false, false));
        Assert.Equal(NativeOuterTabReconcileAction.Detach, action);
        Assert.Equal(NativeOuterTabReconcile.ReasonHudDead, reason);
    }

    [Fact]
    public void A_reordered_native_prefix_under_the_owned_entry_detaches()
    {
        var (action, reason) = NativeOuterTabReconcile.Decide(Input(list: new[] { 1, 0, 2, 3, 4, 5, 6, 7 }));
        Assert.Equal(NativeOuterTabReconcileAction.Detach, action);
        Assert.Equal(NativeOuterTabReconcile.ReasonListForeign + "order", reason);
    }

    // ---- route selection ---------------------------------------------------------------------

    public static IEnumerable<object[]> EveryFailureReason()
    {
        yield return new object[] { NativeRouteSelection.ReasonAdapterUnavailable };
        yield return new object[] { NativeRouteSelection.ReasonMapperMissing };
        yield return new object[] { NativeOuterTabShape.Validate(null, Native, 7)! };
        yield return new object[] { NativeOuterTabShape.Validate(Native, null, 7)! };
        yield return new object[] { NativeOuterTabShape.Validate(new[] { 0, 1, 2, 3, 4, 5 }, Native, 7)! };
        yield return new object[] { NativeOuterTabShape.Validate(Native, new[] { 0, 1, 2, 3, 4, 5, 6, 8 }, 7)! };
        yield return new object[] { NativeOuterTabShape.Validate(new[] { 0, 2, 1, 3, 4, 5, 6 }, Native, 7)! };
        yield return new object[] { NativeOuterTabShape.Validate(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, Native, 7)! };
        yield return new object[] { NativeOuterTabShape.Validate(Native, Native, 6)! };
        foreach (var step in NativeOuterTabTransaction.Order)
            yield return new object[] { NativeOuterTabTransaction.FailureReason(step, "refused") };
        yield return new object[] { NativeOuterTabTransaction.FailureReason(NativeOuterTabAttachStep.CreatePage, "page-lists-null") };
        yield return new object[] { NativeOuterTabTransaction.FailureReason(NativeOuterTabAttachStep.WireClick, "adapter-lease-failed") };
        yield return new object[] { NativeOuterTabTransaction.FailureReason(NativeOuterTabAttachStep.Validate, "donor-missing") };
        yield return new object[] { NativeOuterTabTransaction.FailureReason(NativeOuterTabAttachStep.Validate, "view-parent-dead") };
        yield return new object[] { NativeOuterTabTransaction.FailureReason(NativeOuterTabAttachStep.Validate, "mapper-menu-mismatch") };
    }

    /// <summary>Every failure reason keeps the imitation route; only a null reason selects native.</summary>
    [Theory]
    [MemberData(nameof(EveryFailureReason))]
    public void Every_failure_reason_falls_back_to_the_imitation_route(string reason)
    {
        Assert.False(string.IsNullOrEmpty(reason));
        Assert.Equal(ModsEntryRoute.Imitation, NativeRouteSelection.Decide(reason));
    }

    [Fact]
    public void Only_a_clean_attach_selects_the_native_route()
    {
        Assert.Equal(ModsEntryRoute.Native, NativeRouteSelection.Decide(null));
        Assert.Equal(ModsEntryRoute.Imitation, NativeRouteSelection.Decide(""));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void The_native_route_is_attempted_only_with_a_Bootstrap_adapter(int capability, bool tried)
    {
        Assert.Equal(tried, NativeRouteSelection.ShouldTryNative(capability));
    }

    // ---- tick state machine ------------------------------------------------------------------

    [Theory]
    // NATIVE3 §1: the chrome root is a top-level overlay on this route, so the steady state has nothing to remount.
    [InlineData(true, true, true, false, (int)NativeRouteTickAction.None)]
    [InlineData(true, false, true, false, (int)NativeRouteTickAction.CloseWorkspace)]
    [InlineData(true, false, false, false, (int)NativeRouteTickAction.SelectOwned)]
    [InlineData(true, true, false, false, (int)NativeRouteTickAction.SelectOwned)]
    [InlineData(false, true, true, false, (int)NativeRouteTickAction.ReturnSelection)]
    [InlineData(false, false, true, false, (int)NativeRouteTickAction.ReturnSelection)]
    [InlineData(false, true, false, false, (int)NativeRouteTickAction.OpenWorkspace)]
    [InlineData(false, false, false, false, (int)NativeRouteTickAction.None)]
    public void Tick_couples_native_selection_and_the_workspace(
        bool open, bool ownedCurrent, bool selectedByUs, bool returnFailed, int expected)
    {
        Assert.Equal((NativeRouteTickAction)expected, NativeRouteSelection.DecideTick(open, ownedCurrent, selectedByUs, returnFailed));
    }

    /// <summary>
    /// A failed return transition keeps <c>selectedByUs</c> and retries; it never reopens the
    /// workspace from the still-current owned page (the loop this guard exists for).
    /// </summary>
    [Fact]
    public void A_failed_return_retries_and_never_reopens()
    {
        Assert.Equal(NativeRouteTickAction.ReturnSelection, NativeRouteSelection.DecideTick(false, true, true, true));
        Assert.Equal(NativeRouteTickAction.None, NativeRouteSelection.DecideTick(false, true, false, true));
    }

    // ---- status ------------------------------------------------------------------------------

    [Fact]
    public void Native_route_status_reports_the_three_aligned_numbers()
    {
        var status = NativeRouteStatus.Native(7, 7, "ours", selected: true, mounted: true, adapter: 1);
        Assert.Equal("native", status.RouteName);
        var d = status.Describe();
        Assert.Equal(7, d["state"]);
        Assert.Equal(7, d["viewsIndex"]);
        Assert.Equal(7, d["listIndex"]);
        Assert.Equal("", d["reason"]);
        Assert.Equal("ours", d["callback"]);
        Assert.Equal(true, d["selected"]);
        Assert.Equal(true, d["mounted"]);
        Assert.Equal(1, d["adapter"]);
    }

    [Fact]
    public void Imitation_status_carries_the_reason_and_no_indices()
    {
        var status = NativeRouteStatus.Imitation("validate:donor-missing", adapter: 1);
        Assert.Equal("imitation", status.RouteName);
        var d = status.Describe();
        Assert.Equal(-1, d["viewsIndex"]);
        Assert.Equal(-1, d["listIndex"]);
        Assert.Equal("validate:donor-missing", d["reason"]);
        Assert.Equal("none", d["callback"]);
        Assert.Equal(false, d["selected"]);
    }

    [Fact]
    public void The_test_build_is_the_imitation_route_and_the_owned_snapshot_is_unchanged()
    {
        var route = NativeModsEntry.CurrentRoute();
        Assert.Equal(ModsEntryRoute.Imitation, route.Route);
        Assert.Equal(NativeRouteSelection.ReasonTestBuild, route.Reason);
        Assert.Equal("imitation", NativeModsEntry.RouteName());
        Assert.False(NativeModsEntry.NativeRouteActive);
        Assert.Equal("none", NativeModsEntry.DescribeOwned().Site);
        Assert.Contains(NativeModsEntry.NativePageObjectName, NativeModsEntry.EnumerateOwnedRootNames());
    }

    [Fact]
    public void mods_ui_status_and_rects_carry_route_and_nativeRoute()
    {
        var status = ModsUiResult.WithRoute(ModsUiResult.EmptyStatus(), "native", NativeModsEntry.DescribeNativeRoute());
        Assert.Equal("native", status["route"]);
        var native = Assert.IsType<Dictionary<string, object?>>(status["nativeRoute"]);
        Assert.Equal(7, native["state"]);
        Assert.Equal(NativeRouteSelection.ReasonTestBuild, native["reason"]);

        var fallback = ModsUiResult.WithRoute(new Dictionary<string, object?>(), null, null);
        Assert.Equal("imitation", fallback["route"]);
        var missing = Assert.IsType<Dictionary<string, object?>>(fallback["nativeRoute"]);
        Assert.Equal("unavailable", missing["reason"]);
        Assert.Equal(-1, missing["viewsIndex"]);
        Assert.Equal(-1, missing["listIndex"]);
    }

    // ---- source pins: the route is wired where the research says, and the fallback is unchanged

    [Fact]
    public void The_native_route_is_tried_first_and_the_imitation_hold_never_runs_beside_it()
    {
        string entry = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        string tick = SourcePin.MethodBody(entry, "private static void TickBody()");
        SourcePin.AssertUncommented(tick, "TryAttachNativeRoute(found)");
        SourcePin.AssertStatement(tick, "NativeRouteTick();");
        // The imitation attach and its native-tab observers are still the fallback, unchanged.
        SourcePin.AssertStatement(tick, "Attach(group.transform, label, true);");
        SourcePin.AssertUncommented(tick, "ObserveNativeTabs(group.gameObject)");
        SourcePin.AssertUncommented(tick, "CaptureOrPark(");
        SourcePin.AssertUncommented(tick, "UpdateHost(");
        // HoldHostedViewIfTopTab has a test-build twin above the production body; pin the gate
        // on the production one by the line that follows its liveness check.
        int production = entry.LastIndexOf("internal static void HoldHostedViewIfTopTab()", StringComparison.Ordinal);
        Assert.True(production > 0);
        string hold = entry.Substring(production, Math.Min(900, entry.Length - production));
        SourcePin.AssertStatementExactly(hold, "if (!Alive(_menu)) return;");
        SourcePin.AssertStatementExactly(hold, "if (NativeRouteActive) return;");
        Assert.True(hold.IndexOf("if (NativeRouteActive) return;", StringComparison.Ordinal)
            < hold.IndexOf("GameObject? go = null;", StringComparison.Ordinal));
        string drop = SourcePin.MethodBody(entry, "private static void Drop()");
        SourcePin.AssertUncommented(drop, "DetachNativeRoute(\"drop\")");
        string uninstall = SourcePin.MethodBody(entry, "internal static void Uninstall()");
        SourcePin.AssertStatementExactly(uninstall, "NativeOuterTabRegistration.ClearDispatch();");
        string click = SourcePin.MethodBody(entry, "private static void OnNativeTabClicked()");
        SourcePin.AssertUncommented(click, "DecideOwnedButtonClick(");
        // NATIVE3 §2: the click and `mods_ui open site=top-tab` share one path (NativeRouteOps.Open
        // over the live host): typed SetState(7) with readback, then the stored open.
        SourcePin.AssertUncommented(click, "TryOpenViaNativeRoute(");
        string select = SourcePin.MethodBody(entry, "public bool SelectOwned(out string why)");
        SourcePin.AssertUncommented(select, "TrySelectOwned(");
        string open = SourcePin.MethodBody(entry, "public void OpenWorkspace()");
        SourcePin.AssertUncommented(open, "InvokeStoredOpen(EntryOpenUserButton)");
        string ret = SourcePin.MethodBody(entry, "public bool ReturnSelection(out string why)");
        SourcePin.AssertUncommented(ret, "TryReturnSelection(");
        // NATIVE3 §3: the menu-closing rule runs in the tick before the host drop, with the HUD
        // menu type as the detach's `menuRunning` input.
        SourcePin.AssertUncommented(tick, "ShouldDetachOnMenuClosed(NativeRouteActive, Alive(found))");
        SourcePin.AssertUncommented(tick, "DetachNativeRoute(\"menu-closed\")");
        string detach = SourcePin.MethodBody(entry, "private static bool DetachNativeRoute(string why)");
        SourcePin.AssertUncommented(detach, "menuRunning: HudMenuIsFullscreen()");
    }

    /// <summary>
    /// Typed interop only: no MonoBehaviour constructor, no injected Core subclass, no raw
    /// native call, no per-frame scene walk in the registration helper.
    /// </summary>
    [Fact]
    public void The_registration_helper_uses_no_forbidden_seams()
    {
        string helper = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeOuterTabRegistration.cs");
        Assert.DoesNotContain("new FullscreenMenuView(", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("ClassInjector", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("FindObjectsOfType", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("DelegateSupport", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("il2cpp_runtime_invoke", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Initalize(", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("InitializeMenu(", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("_ViewStateEntity", helper, StringComparison.Ordinal);
        SourcePin.AssertUncommented(helper, "AddComponent<FullscreenMenuView>()");
        SourcePin.AssertUncommented(helper, "BootstrapLink.TryLeaseUiAction(");
        SourcePin.AssertUncommented(helper, "Requirement = OuterTabRequirement.None");
        SourcePin.AssertUncommented(helper, "CheckOuterTabRequirement(OuterTabRequirement.None)");
    }
}
