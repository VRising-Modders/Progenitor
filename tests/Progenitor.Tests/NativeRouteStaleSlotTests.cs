using System;
using System.Collections.Generic;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job NATIVE4 — the native MODS route was lost for the rest of the session after a Core hot swap
/// (<c>validate:competing-slot-7:views@7</c>), because the game keeps <c>FullscreenMenu.Views</c>
/// across the swap while the unload path could skip the <c>Views</c>/outer-list removal. These
/// qualify the two halves game-free: the unload's detach order (menu closed, menu open, owned page
/// destroyed under a live menu) and the attach's classification of whatever already occupies
/// state 7 — ours gets pruned, a foreign entry keeps the refusal.
/// </summary>
public sealed class NativeRouteStaleSlotTests
{
    private static readonly int[] Native = { 0, 1, 2, 3, 4, 5, 6 };
    private static readonly int[] NativePlusOwned = { 0, 1, 2, 3, 4, 5, 6, 7 };

    // ---- §1 unload always detaches -------------------------------------------------------------

    /// <summary>
    /// The live regression: the menu object and its <c>Views</c> array outlive the generation, but
    /// the owned page (a child of <c>ViewParent</c>) does not, so the composite <c>menuAlive</c>
    /// reads false. Removal must still run. Dropping <c>menuObjectAlive</c> from the plan is exactly
    /// what left the state-7 record in <c>Views</c> and pushed gen 3, 4 and every later generation
    /// to the imitation route.
    /// </summary>
    [Fact]
    public void A_dead_owned_page_under_a_live_menu_still_removes_the_record_and_the_list_entry()
    {
        var order = NativeOuterTabDetachPlan.Order(
            menuAlive: false, ownedSelected: false, workspaceOpen: false, mapperUsable: true, menuObjectAlive: true);
        Assert.Contains(NativeOuterTabDetachPhase.RemoveListEntry, order);
        Assert.Contains(NativeOuterTabDetachPhase.RestoreViews, order);
        Assert.Contains(NativeOuterTabDetachPhase.ClearStaleCurrentView, order);
        int views = Array.IndexOf(order, NativeOuterTabDetachPhase.RestoreViews);
        int destroy = Array.IndexOf(order, NativeOuterTabDetachPhase.DestroyOwned);
        int release = Array.IndexOf(order, NativeOuterTabDetachPhase.ReleaseClick);
        Assert.True(views < release && release < destroy);
        Assert.Equal(NativeOuterTabDetachPhase.Invalidate, order[^1]);
        // No SetState is attempted: the mapper no longer holds a page this registration owns.
        Assert.DoesNotContain(NativeOuterTabDetachPhase.RestoreSelection, order);
        Assert.DoesNotContain(NativeOuterTabDetachPhase.VerifySelection, order);
    }

    /// <summary>A destroyed menu object keeps the pre-NATIVE4 order: nothing is written into a dead menu.</summary>
    [Fact]
    public void A_destroyed_menu_object_writes_no_record_or_list()
    {
        var order = NativeOuterTabDetachPlan.Order(
            menuAlive: false, ownedSelected: true, workspaceOpen: false, mapperUsable: true, menuObjectAlive: false);
        Assert.DoesNotContain(NativeOuterTabDetachPhase.RemoveListEntry, order);
        Assert.DoesNotContain(NativeOuterTabDetachPhase.RestoreViews, order);
        Assert.Contains(NativeOuterTabDetachPhase.ResetSelectedIndex, order);
        // The four-argument overload is the same call with menuObjectAlive = menuAlive.
        Assert.Equal(
            order,
            NativeOuterTabDetachPlan.Order(menuAlive: false, ownedSelected: true, workspaceOpen: false, mapperUsable: true));
    }

    /// <summary>
    /// Unload with the menu open and MODS selected: the existing return-selection path runs first,
    /// and only then is the record removed — but a failed transition no longer holds the
    /// registration, because a Core unload has no later tick to retry it on.
    /// </summary>
    [Fact]
    public void Unload_with_the_menu_open_returns_the_selection_first_and_never_holds()
    {
        var order = NativeOuterTabDetachPlan.Order(
            menuAlive: true, ownedSelected: true, workspaceOpen: true, mapperUsable: true, menuObjectAlive: true);
        int restore = Array.IndexOf(order, NativeOuterTabDetachPhase.RestoreSelection);
        int verify = Array.IndexOf(order, NativeOuterTabDetachPhase.VerifySelection);
        int views = Array.IndexOf(order, NativeOuterTabDetachPhase.RestoreViews);
        Assert.True(restore >= 0 && restore < verify && verify < views);

        // Menu still running, transition failed: the tick path holds, the unload path does not.
        Assert.False(NativeOuterTabDetachPlan.MayRemoveRegistration(
            ownedSelected: true, selectionRestored: false, menuRunning: true, coreUnload: false));
        Assert.True(NativeOuterTabDetachPlan.MayRemoveRegistration(
            ownedSelected: true, selectionRestored: false, menuRunning: true, coreUnload: true));
        // A successful transition removes on either path, and the menu-closing rule still applies.
        Assert.True(NativeOuterTabDetachPlan.MayRemoveRegistration(true, true, true, false));
        Assert.True(NativeOuterTabDetachPlan.MayRemoveRegistration(true, false, false, false));
    }

    /// <summary>The unload's own detach reason is the one the client log must show across a Core swap.</summary>
    [Fact]
    public void The_unload_detach_reason_is_core_unload()
    {
        Assert.Equal("core-unload", NativeModsEntry.NativeRouteUnloadReason);
    }

    // ---- §2 attach adopts or prunes a stale owned slot -----------------------------------------

    /// <summary>The prune rule's two markers, and the names it mirrors from the live registration.</summary>
    [Fact]
    public void An_owned_state_seven_record_is_recognised_by_its_page_object()
    {
        Assert.Equal(
            NativeOuterTabSlotOwner.Owned,
            NativeOuterTabStaleSlot.ClassifyViewsRecord(true, NativeOuterTabStaleSlot.OwnedPageName, pageAlive: true, out var byName));
        Assert.Equal(NativeOuterTabStaleSlot.ReasonPageName, byName);

        // The page died with the generation that made it: the record cannot serve any owner and
        // keeping it blocks slot 7 for the rest of the process.
        Assert.Equal(
            NativeOuterTabSlotOwner.Owned,
            NativeOuterTabStaleSlot.ClassifyViewsRecord(true, null, pageAlive: false, out var dead));
        Assert.Equal(NativeOuterTabStaleSlot.ReasonPageDead, dead);

        Assert.Equal(
            NativeOuterTabSlotOwner.None,
            NativeOuterTabStaleSlot.ClassifyViewsRecord(false, null, pageAlive: false, out _));
    }

    /// <summary>Someone else's live state-7 page keeps today's refusal: foreign entries are never removed.</summary>
    [Fact]
    public void A_foreign_state_seven_record_is_never_pruned()
    {
        Assert.Equal(
            NativeOuterTabSlotOwner.Foreign,
            NativeOuterTabStaleSlot.ClassifyViewsRecord(true, "some-other-mod-page", pageAlive: true, out var why));
        Assert.Equal("", why);
        // The shape check still names it, with the index, exactly as before this job.
        Assert.Equal(
            NativeOuterTabShape.ReasonCompetingSlot + "views@7",
            NativeOuterTabShape.Validate(Native, NativePlusOwned, 7));
    }

    [Fact]
    public void An_owned_outer_list_entry_is_recognised_by_its_clone_name()
    {
        Assert.Equal(NativeOuterTabStaleSlot.OwnedTabName, NativeModsEntry.TopTabObjectName);
        Assert.Equal(
            NativeOuterTabSlotOwner.Owned,
            NativeOuterTabStaleSlot.ClassifyListEntry(true, NativeModsEntry.TopTabObjectName, objectAlive: true, out var byName));
        Assert.Equal(NativeOuterTabStaleSlot.ReasonTabName, byName);
        Assert.Equal(
            NativeOuterTabSlotOwner.Owned,
            NativeOuterTabStaleSlot.ClassifyListEntry(true, null, objectAlive: false, out var dead));
        Assert.Equal(NativeOuterTabStaleSlot.ReasonTabDead, dead);
        Assert.Equal(
            NativeOuterTabSlotOwner.Foreign,
            NativeOuterTabStaleSlot.ClassifyListEntry(true, "another-mods-tab", objectAlive: true, out _));
    }

    /// <summary>
    /// The attach's decision chain on the live evidence: <c>Views</c> holds our record at 7, the
    /// outer list was rebuilt without our clone. Pruning the record leaves the pristine shape, so
    /// validation passes and the route is native again.
    /// </summary>
    [Fact]
    public void Pruning_the_stale_record_leaves_the_shape_the_attach_registers_into()
    {
        int index = NativeOuterTabStaleSlot.IndexOfOwnedState(NativePlusOwned);
        Assert.Equal(7, index);
        Assert.Equal(-1, NativeOuterTabStaleSlot.IndexOfOwnedState(Native));
        Assert.Equal(-1, NativeOuterTabStaleSlot.IndexOfOwnedState(null));

        var pruned = NativeOuterTabStaleSlot.Without(NativePlusOwned, index);
        Assert.Equal(Native, pruned);
        Assert.Null(NativeOuterTabShape.Validate(Native, pruned, 7));
        Assert.Equal(ModsEntryRoute.Native, NativeRouteSelection.Decide(NativeOuterTabShape.Validate(Native, pruned, 7)));
    }

    /// <summary>A stale owned entry anywhere in the list is removed by index, and foreign appends stay put.</summary>
    [Theory]
    [InlineData(7)]
    [InlineData(3)]
    public void Without_removes_only_the_named_index(int index)
    {
        int[] states = { 0, 1, 2, 3, 4, 5, 6, 7 };
        states[index] = 7;
        var kept = NativeOuterTabStaleSlot.Without(states, index);
        Assert.Equal(states.Length - 1, kept.Length);
        for (int i = 0, j = 0; i < states.Length; i++)
        {
            if (i == index) continue;
            Assert.Equal(states[i], kept[j++]);
        }
        // An index outside the list is a copy, never a throw during an attach.
        Assert.Equal(states, NativeOuterTabStaleSlot.Without(states, 99));
        Assert.Equal(states, NativeOuterTabStaleSlot.Without(states, -1));
        Assert.Throws<ArgumentNullException>(() => NativeOuterTabStaleSlot.Without(null!, 0));
    }

    [Fact]
    public void The_prune_line_names_the_slot_the_page_and_the_marker()
    {
        string note = NativeOuterTabStaleSlot.PruneNote("views", 7, NativeOuterTabStaleSlot.OwnedPageName, NativeOuterTabStaleSlot.ReasonPageName);
        Assert.Equal(
            "native route: pruned stale owned slot (views@7, page=progenitor-ui-core-mods-page, gen=unknown, why=page-name)",
            note);
        // A destroyed page object has no name to print; the line still says which slot went.
        Assert.Contains("page=destroyed", NativeOuterTabStaleSlot.PruneNote("views", 7, "", NativeOuterTabStaleSlot.ReasonPageDead));
    }

    // ---- §3 a stale record discovered later ---------------------------------------------------

    /// <summary>
    /// The menu was closed at swap time and reopens later: the retry's read-only rehearsal
    /// classifies the stale record instead of refusing, so the attempt counter does not spend a
    /// failure on it and the real attach prunes. A foreign record still refuses, and that refusal
    /// is not a retryable one (no mismatch), so it stays a verdict for the host.
    /// </summary>
    [Fact]
    public void A_stale_record_found_on_reopen_is_pruned_rather_than_counted_as_a_failure()
    {
        string? staleReason = NativeOuterTabShape.Validate(Native, NativeOuterTabStaleSlot.Without(NativePlusOwned, 7), 7);
        Assert.Null(staleReason);
        Assert.False(NativeRouteRetry.ShouldArm(staleReason));

        string? foreign = NativeOuterTabShape.Validate(Native, NativePlusOwned, 7);
        Assert.Equal(ModsEntryRoute.Imitation, NativeRouteSelection.Decide(foreign));
        Assert.False(NativeRouteRetry.ShouldArm(foreign));
    }

    // ---- §4 readback --------------------------------------------------------------------------

    [Fact]
    public void The_readback_carries_pruned_and_the_stale_reason_on_both_shapes()
    {
        var native = NativeRouteStatus
            .Native(7, 7, "ours", selected: false, mounted: false, adapter: 1, reason: "", attempts: 1,
                pruned: 1, staleReason: NativeOuterTabStaleSlot.ReasonPageDead)
            .Describe();
        Assert.Equal(1, native["pruned"]);
        Assert.Equal(NativeOuterTabStaleSlot.ReasonPageDead, native["staleReason"]);

        var clean = NativeRouteStatus.Native(7, 7, "ours", false, false, 1).Describe();
        Assert.Equal(0, clean["pruned"]);
        Assert.Equal("", clean["staleReason"]);

        var imitation = NativeRouteStatus.Imitation("validate:competing-slot-7:views@7", 1, attempts: 1).Describe();
        Assert.Equal(0, imitation["pruned"]);
        Assert.Equal("", imitation["staleReason"]);

        // The Dev pass-through's fallback shape carries the same keys, so a caller reading
        // nativeRoute.pruned never has to tell a missing key from a zero.
        var fallback = Assert.IsType<Dictionary<string, object?>>(
            ModsUiResult.WithRoute(new Dictionary<string, object?>(), "imitation", null)["nativeRoute"]);
        Assert.Equal(0, fallback["pruned"]);
        Assert.Equal("", fallback["staleReason"]);

        var passed = Assert.IsType<Dictionary<string, object?>>(
            ModsUiResult.WithRoute(new Dictionary<string, object?>(), "native", native)["nativeRoute"]);
        Assert.Equal(1, passed["pruned"]);
    }

    // ---- §5 NATIVE5: leftovers at any state, the drop sweep and the shape retry ----------------

    private static NativeOuterTabSlotCandidate[] SevenNativeTabs()
    {
        var entries = new NativeOuterTabSlotCandidate[7];
        for (int i = 0; i < 7; i++) entries[i] = new NativeOuterTabSlotCandidate(i, "OuterTabEntry" + i, true);
        return entries;
    }

    /// <summary>
    /// The live gen-5 refusal: the eighth <c>_OuterTabs</c> entry was the previous generation's
    /// owned top-tab object, which the mapper had collected with a state that is not 7, so the
    /// state-keyed NATIVE4 prune could not see it and the count check refused
    /// <c>outer-list-count:8</c>. Recognized by name now, whatever its state, and the attach
    /// validates against the pristine seven. Removing the name arm of
    /// <see cref="NativeOuterTabStaleSlot.ClassifyListLeftover"/> restores that refusal exactly.
    /// </summary>
    [Fact]
    public void An_eighth_entry_that_is_ours_by_name_with_a_foreign_state_is_pruned_and_the_attach_passes()
    {
        var live = new List<NativeOuterTabSlotCandidate>(SevenNativeTabs())
        {
            new(State: 3, ObjectName: NativeModsEntry.TopTabObjectName, Alive: true),
        };
        int[] listStates = { 0, 1, 2, 3, 4, 5, 6, 3 };
        Assert.Equal(
            NativeOuterTabShape.ReasonListCount + "8",
            NativeOuterTabShape.Validate(listStates, Native, 7));

        var plan = NativeOuterTabStaleSlot.PlanPrune(live, views: false, out var why, out int refusal);
        Assert.Equal(new[] { 7 }, plan);
        Assert.Equal(NativeOuterTabStaleSlot.ReasonTabName, why);
        Assert.Equal(-1, refusal);

        var kept = NativeOuterTabStaleSlot.WithoutMany(listStates, plan);
        Assert.Equal(Native, kept);
        Assert.Null(NativeOuterTabShape.Validate(kept, Native, 7));
        Assert.Equal(ModsEntryRoute.Native, NativeRouteSelection.Decide(NativeOuterTabShape.Validate(kept, Native, 7)));
    }

    /// <summary>An eighth entry with a foreign name is another mod's: nothing is removed and the count still refuses.</summary>
    [Fact]
    public void An_eighth_entry_with_a_foreign_name_is_never_pruned()
    {
        var live = new List<NativeOuterTabSlotCandidate>(SevenNativeTabs())
        {
            new(State: 3, ObjectName: "some-other-mod-tab", Alive: true),
        };
        var plan = NativeOuterTabStaleSlot.PlanPrune(live, views: false, out var why, out int refusal);
        Assert.Empty(plan);
        Assert.Equal("", why);
        Assert.Equal(-1, refusal);
        int[] listStates = { 0, 1, 2, 3, 4, 5, 6, 3 };
        Assert.Equal(
            NativeOuterTabShape.ReasonListCount + "8",
            NativeOuterTabShape.Validate(NativeOuterTabStaleSlot.WithoutMany(listStates, plan), Native, 7));
    }

    /// <summary>A live foreign owner of state 7 is the verdict: the plan removes nothing and names the index.</summary>
    [Fact]
    public void A_live_foreign_state_seven_entry_refuses_and_cancels_the_plan()
    {
        var live = new List<NativeOuterTabSlotCandidate>(SevenNativeTabs())
        {
            new(State: 3, ObjectName: NativeModsEntry.TopTabObjectName, Alive: true),
            new(State: 7, ObjectName: "some-other-mod-tab", Alive: true),
        };
        var plan = NativeOuterTabStaleSlot.PlanPrune(live, views: false, out var why, out int refusal);
        Assert.Empty(plan);
        Assert.Equal("", why);
        Assert.Equal(8, refusal);
        // Our own current entry is never a leftover.
        Assert.Equal(
            NativeOuterTabLeftover.Keep,
            NativeOuterTabStaleSlot.ClassifyListLeftover(
                new NativeOuterTabSlotCandidate(7, NativeModsEntry.TopTabObjectName, true, Ours: true), out _));
    }

    /// <summary>
    /// The same rule for <c>Views</c>: a record whose page carries the owned name goes at any state,
    /// while a destroyed page under a native state stays — removing it would shift every later view
    /// index, which is what <c>SetState</c> stores in <c>_SelectedOuterTab</c>.
    /// </summary>
    [Fact]
    public void A_views_record_is_ours_by_page_name_at_any_state_but_a_dead_native_page_stays()
    {
        Assert.Equal(
            NativeOuterTabLeftover.Prune,
            NativeOuterTabStaleSlot.ClassifyViewsLeftover(
                new NativeOuterTabSlotCandidate(2, NativeOuterTabStaleSlot.OwnedPageName, true), out var byName));
        Assert.Equal(NativeOuterTabStaleSlot.ReasonPageName, byName);

        Assert.Equal(
            NativeOuterTabLeftover.Prune,
            NativeOuterTabStaleSlot.ClassifyViewsLeftover(new NativeOuterTabSlotCandidate(7, null, false), out var dead));
        Assert.Equal(NativeOuterTabStaleSlot.ReasonPageDead, dead);

        Assert.Equal(
            NativeOuterTabLeftover.Keep,
            NativeOuterTabStaleSlot.ClassifyViewsLeftover(new NativeOuterTabSlotCandidate(4, null, false), out _));
        Assert.Equal(
            NativeOuterTabLeftover.Refuse,
            NativeOuterTabStaleSlot.ClassifyViewsLeftover(new NativeOuterTabSlotCandidate(7, "other-mod-page", true), out _));
    }

    /// <summary>
    /// Drop/Uninstall removes the owned top-tab object from the mapper's list as well as destroying
    /// it, so the next generation starts from seven entries. Only a live owned name qualifies: Drop
    /// also runs while the menu is tearing down, and removing dead entries there would rewrite the
    /// game's own list.
    /// </summary>
    [Fact]
    public void Drop_removes_only_the_owned_live_entry_from_the_mapper_list()
    {
        Assert.True(NativeOuterTabStaleSlot.DropRemovesListEntry(NativeModsEntry.TopTabObjectName, alive: true));
        Assert.False(NativeOuterTabStaleSlot.DropRemovesListEntry(NativeModsEntry.TopTabObjectName, alive: false));
        Assert.False(NativeOuterTabStaleSlot.DropRemovesListEntry("OuterTabEntry3", alive: true));
        Assert.False(NativeOuterTabStaleSlot.DropRemovesListEntry(null, alive: true));
    }

    /// <summary>
    /// The retry gap this job closes: before NATIVE5 only a mapper-mismatch reason armed the retry,
    /// so <c>validate:outer-list-count:8</c> was a verdict for the whole process and the workspace
    /// stayed on the imitation route. A shape/count refusal is now retried while a menu is up,
    /// bounded by <see cref="NativeRouteRetry.MaxShapeAttempts"/> opens.
    /// </summary>
    [Fact]
    public void A_shape_refusal_is_retryable_and_the_terminal_reasons_are_not()
    {
        Assert.True(NativeRouteRetry.IsShapeRefusal("validate:" + NativeOuterTabShape.ReasonListCount + "8"));
        Assert.True(NativeRouteRetry.IsShapeRefusal(NativeOuterTabShape.ReasonViewsCount + "8"));
        Assert.True(NativeRouteRetry.IsShapeRefusal("validate:" + NativeOuterTabShape.ReasonListOrder + "3=7"));
        Assert.True(NativeRouteRetry.IsShapeRefusal("validate:" + NativeOuterTabShape.ReasonViewsOrder + "3=7"));
        Assert.True(NativeRouteRetry.IsShapeRefusal("validate:" + NativeOuterTabShape.ReasonOwnedSlot + "8"));
        Assert.True(NativeRouteRetry.ShouldArm("validate:" + NativeOuterTabShape.ReasonListCount + "8"));
        Assert.Equal(NativeRouteRetry.MaxShapeAttempts, NativeRouteRetry.BudgetFor("validate:outer-list-count:8"));
        Assert.Equal(
            NativeRouteRetry.NextAttemptMenuOpen,
            NativeRouteRetry.NextAttemptOn(NativeRouteRetry.ShouldArm("validate:outer-list-count:8")));

        // Terminal: no click adapter, and a live foreign state-7 owner (never removed, never retried).
        foreach (string terminal in new[]
                 {
                     "validate:" + NativeOuterTabShape.ReasonCompetingSlot + "views@7",
                     NativeOuterTabShape.ReasonCompetingSlot + "outer-list@7",
                     NativeRouteSelection.ReasonAdapterUnavailable,
                 })
        {
            Assert.True(NativeRouteRetry.IsTerminal(terminal));
            Assert.False(NativeRouteRetry.ShouldArm(terminal));
            Assert.Equal(NativeRouteRetry.NextAttemptNone, NativeRouteRetry.NextAttemptOn(NativeRouteRetry.ShouldArm(terminal)));
        }
        Assert.False(NativeRouteRetry.IsShapeRefusal(null));
        Assert.False(NativeRouteRetry.IsTerminal("donor-missing"));
    }

    /// <summary>
    /// The bounded cadence: an armed shape refusal fires an attempt every
    /// <see cref="NativeRouteRetry.IntervalFrames"/> frames while the menu is up and the workspace
    /// is idle, and stops once the five-open budget is spent.
    /// </summary>
    [Fact]
    public void The_shape_retry_fires_on_the_cadence_and_stops_at_the_budget()
    {
        int frames = 0;
        int budget = NativeRouteRetry.BudgetFor("validate:outer-list-count:8");
        bool Fire(int attempts)
        {
            for (int i = 1; i < NativeRouteRetry.IntervalFrames; i++)
                Assert.False(NativeRouteRetry.Advance(ref frames, true, true, false, false, attempts, budget));
            return NativeRouteRetry.Advance(ref frames, true, true, false, false, attempts, budget);
        }
        Assert.True(Fire(1));
        Assert.True(Fire(NativeRouteRetry.MaxShapeAttempts - 1));
        // Budget spent, and never while the workspace is open or the native route already attached.
        Assert.False(NativeRouteRetry.Advance(ref frames, true, true, false, false, NativeRouteRetry.MaxShapeAttempts, budget));
        Assert.False(NativeRouteRetry.Advance(ref frames, true, true, true, false, 1, budget));
        Assert.False(NativeRouteRetry.Advance(ref frames, true, false, false, false, 1, budget));
        frames = NativeRouteRetry.IntervalFrames - 1;
        Assert.False(NativeRouteRetry.Advance(ref frames, true, true, false, true, 1, budget));
    }

    /// <summary>
    /// The readback the client is judged on: a prune recorded by an attempt whose registration was
    /// later thrown away is still reported, together with whether another attempt is due. The live
    /// gen-5 status lost <c>pruned</c> exactly this way (<c>pruned 0</c> beside a prune line in the log).
    /// </summary>
    [Fact]
    public void The_readback_keeps_pruned_after_a_later_failure_and_names_the_next_attempt()
    {
        var refused = NativeRouteStatus
            .Imitation("validate:outer-list-count:8", adapter: 1, attempts: 1, pruned: 1,
                staleReason: NativeOuterTabStaleSlot.ReasonTabName, retryable: true)
            .Describe();
        Assert.Equal(1, refused["pruned"]);
        Assert.Equal(NativeOuterTabStaleSlot.ReasonTabName, refused["staleReason"]);
        Assert.Equal(true, refused["retryable"]);
        Assert.Equal(NativeRouteRetry.NextAttemptMenuOpen, refused["nextAttemptOn"]);

        var terminal = NativeRouteStatus.Imitation("validate:competing-slot-7:views@7", 1, attempts: 1).Describe();
        Assert.Equal(false, terminal["retryable"]);
        Assert.Equal(NativeRouteRetry.NextAttemptNone, terminal["nextAttemptOn"]);

        // A native route is attached: nothing further is due.
        var native = NativeRouteStatus.Native(7, 7, "ours", false, false, 1, attempts: 2, pruned: 1,
            staleReason: NativeOuterTabStaleSlot.ReasonTabName).Describe();
        Assert.Equal(false, native["retryable"]);
        Assert.Equal(NativeRouteRetry.NextAttemptNone, native["nextAttemptOn"]);
        Assert.Equal(1, native["pruned"]);

        // The Dev fallback shape carries the two new keys, so a caller never reads a missing key.
        var fallback = Assert.IsType<Dictionary<string, object?>>(
            ModsUiResult.WithRoute(new Dictionary<string, object?>(), "imitation", null)["nativeRoute"]);
        Assert.Equal(false, fallback["retryable"]);
        Assert.Equal(NativeRouteRetry.NextAttemptNone, fallback["nextAttemptOn"]);
    }

    /// <summary>
    /// End to end on the fakes: refusal, then the sweep/prune clears the leftover and the next
    /// attempt in the same or the next open validates. <c>WithoutMany</c> with an empty plan is the
    /// identity, so a clean host reports <c>pruned 0</c>.
    /// </summary>
    [Fact]
    public void The_count_refusal_is_retried_and_succeeds_once_the_leftover_is_gone()
    {
        int[] before = { 0, 1, 2, 3, 4, 5, 6, 3 };
        string? first = NativeOuterTabShape.Validate(before, Native, 7);
        Assert.Equal(NativeOuterTabShape.ReasonListCount + "8", first);
        Assert.True(NativeRouteRetry.ShouldArm(first));

        // Next open: Drop removed the owned entry from the group and the mapper list.
        var clean = NativeOuterTabStaleSlot.PlanPrune(SevenNativeTabs(), views: false, out var why, out int refusal);
        Assert.Empty(clean);
        Assert.Equal("", why);
        Assert.Equal(-1, refusal);
        Assert.Equal(Native, NativeOuterTabStaleSlot.WithoutMany(Native, clean));
        Assert.Null(NativeOuterTabShape.Validate(Native, Native, 7));
        Assert.False(NativeRouteRetry.ShouldArm(NativeOuterTabShape.Validate(Native, Native, 7)));
        Assert.Throws<ArgumentNullException>(() => NativeOuterTabStaleSlot.WithoutMany(null!, clean));
    }
}
