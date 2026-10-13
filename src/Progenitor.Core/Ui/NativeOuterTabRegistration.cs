using System;
using System.Collections.Generic;
#if !PROGENITOR_TESTS
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using ProjectM.UI;
using TMPro;
using UnityEngine;
#endif

namespace Progenitor.Ui;

/// <summary>
/// Shape invariants for the native MODS outer tab (job NATIVE1, research
/// <c>reviews/2026-09-12-three-concerns/native-prefab.md</c>). <c>FullscreenMenuMapper.SetState</c>
/// scans <c>Views</c> by <em>state</em> and stores the matched <em>view-array index</em> into
/// <c>_SelectedOuterTab</c>; gamepad navigation passes a list index as a state. The three numbers
/// therefore have to agree, and the only shape this build accepts is the seven native states
/// <c>0..6</c> untouched and in order in both <c>_OuterTabs</c> and <c>Views</c>, with the owned
/// entry appended as state = views index = list index = <see cref="OwnedState"/>. Anything else
/// keeps the imitation route and reports the reason — another mod's entries are never removed.
/// </summary>
internal static class NativeOuterTabShape
{
    internal const int OwnedState = 7;
    internal const int NativeStateCount = 7;
    /// <summary><c>FullscreenMenuState.System</c>: the return target when no saved native state is valid.</summary>
    internal const int SystemState = 6;

    internal const string ReasonOwnedSlot = "owned-slot:";
    internal const string ReasonListMissing = "outer-list-missing";
    internal const string ReasonViewsMissing = "views-missing";
    internal const string ReasonCompetingSlot = "competing-slot-7:";
    internal const string ReasonListCount = "outer-list-count:";
    internal const string ReasonViewsCount = "views-count:";
    internal const string ReasonListOrder = "outer-list-order:";
    internal const string ReasonViewsOrder = "views-order:";

    /// <summary>Null when the live shape is the one this build registers into; otherwise the reason.</summary>
    internal static string? Validate(IReadOnlyList<int>? listStates, IReadOnlyList<int>? viewsStates, int ownedSlot)
    {
        if (ownedSlot != OwnedState) return ReasonOwnedSlot + ownedSlot;
        if (listStates == null) return ReasonListMissing;
        if (viewsStates == null) return ReasonViewsMissing;
        for (int i = 0; i < listStates.Count; i++)
            if (listStates[i] == OwnedState) return ReasonCompetingSlot + "outer-list@" + i;
        for (int i = 0; i < viewsStates.Count; i++)
            if (viewsStates[i] == OwnedState) return ReasonCompetingSlot + "views@" + i;
        if (listStates.Count != NativeStateCount) return ReasonListCount + listStates.Count;
        if (viewsStates.Count != NativeStateCount) return ReasonViewsCount + viewsStates.Count;
        for (int i = 0; i < NativeStateCount; i++)
        {
            if (listStates[i] != i) return ReasonListOrder + i + "=" + listStates[i];
            if (viewsStates[i] != i) return ReasonViewsOrder + i + "=" + viewsStates[i];
        }
        return null;
    }

    /// <summary>True when the first seven entries are the native states in order (foreign appends tolerated after them).</summary>
    internal static bool NativePrefixIntact(IReadOnlyList<int>? states)
    {
        if (states == null || states.Count < NativeStateCount) return false;
        for (int i = 0; i < NativeStateCount; i++)
            if (states[i] != i) return false;
        return true;
    }

    internal static bool IsNativeState(int state) => state >= 0 && state < NativeStateCount;

    /// <summary>
    /// True when a native state <c>0..6</c> occurs more than once: the game's own entries were
    /// appended twice (a menu reopen through <c>OnStartRunning</c> on top of an earlier build).
    /// </summary>
    internal static bool RepeatsNativeState(IReadOnlyList<int>? states)
    {
        if (states == null) return false;
        int seen = 0;
        for (int i = 0; i < states.Count; i++)
        {
            int s = states[i];
            if (!IsNativeState(s)) continue;
            int bit = 1 << s;
            if ((seen & bit) != 0) return true;
            seen |= bit;
        }
        return false;
    }

    /// <summary>
    /// The state the mapper is moved to when the owned page must stop being current: the page
    /// that was current before the owned click when it is a native one, else System. Never
    /// the owned state and never an index outside the native range (a missing state is not a
    /// safe no-op for <c>SetState</c>).
    /// </summary>
    internal static int ReturnStateFor(int? saved) => saved is int s && IsNativeState(s) ? s : SystemState;
}

/// <summary>Who owns a competing state-7 entry found at attach time.</summary>
internal enum NativeOuterTabSlotOwner
{
    /// <summary>No competing entry at state 7.</summary>
    None = 0,
    /// <summary>A leftover of an earlier Progenitor generation: pruned, and the attach continues.</summary>
    Owned = 1,
    /// <summary>Someone else's state-7 entry: the attach is refused with the competing-slot reason.</summary>
    Foreign = 2,
}

/// <summary>
/// Job NATIVE4. A Core hot swap destroys this generation's objects but the game keeps
/// <c>FullscreenMenu.Views</c>, so an unload that failed to remove the owned <c>StateToView</c>
/// leaves a state-7 record behind and every later generation refuses with
/// <c>validate:competing-slot-7:views@7</c> — the workspace drops to the imitation route for the
/// rest of the process. A competing state-7 record is therefore classified before it is treated
/// as foreign:
/// <list type="bullet">
/// <item>its page object is named <see cref="OwnedPageName"/> (<c>NativeOuterTabRegistration.PageObjectName</c>,
/// the only name this build gives the owned page) — <see cref="ReasonPageName"/>;</item>
/// <item>its page object is destroyed (<see cref="ReasonPageDead"/>): the record cannot serve any
/// owner, and leaving it blocks slot 7 permanently. Progenitor is the only writer of state 7 this
/// build knows, and its page dies with the generation that made it, so a dead page instance is
/// read as our leftover rather than as a foreign registration to protect;</item>
/// <item>an outer-list entry is ours when its object is named <see cref="OwnedTabName"/>.</item>
/// </list>
/// Anything else — a live page object with a foreign name — keeps the refusal unchanged.
/// </summary>
internal static class NativeOuterTabStaleSlot
{
    /// <summary>The owned page object name (mirrors <c>NativeOuterTabRegistration.PageObjectName</c>, which is client-only).</summary>
    internal const string OwnedPageName = "progenitor-ui-core-mods-page";
    /// <summary>The owned outer-tab clone name (mirrors <c>NativeModsEntry.TopTabObjectName</c>).</summary>
    internal const string OwnedTabName = "progenitor-ui-core-mods-tab";

    internal const string ReasonPageName = "page-name";
    internal const string ReasonPageDead = "page-dead";
    internal const string ReasonTabName = "tab-name";
    internal const string ReasonTabDead = "tab-dead";

    /// <summary>Classify a competing <c>Views</c> record from its page object's name and liveness.</summary>
    internal static NativeOuterTabSlotOwner ClassifyViewsRecord(bool present, string? pageObjectName, bool pageAlive, out string reason)
    {
        reason = "";
        if (!present) return NativeOuterTabSlotOwner.None;
        if (!pageAlive) { reason = ReasonPageDead; return NativeOuterTabSlotOwner.Owned; }
        if (string.Equals(pageObjectName, OwnedPageName, StringComparison.Ordinal))
        {
            reason = ReasonPageName;
            return NativeOuterTabSlotOwner.Owned;
        }
        return NativeOuterTabSlotOwner.Foreign;
    }

    /// <summary>Classify a competing outer-list entry from its object's name and liveness.</summary>
    internal static NativeOuterTabSlotOwner ClassifyListEntry(bool present, string? objectName, bool objectAlive, out string reason)
    {
        reason = "";
        if (!present) return NativeOuterTabSlotOwner.None;
        if (!objectAlive) { reason = ReasonTabDead; return NativeOuterTabSlotOwner.Owned; }
        if (string.Equals(objectName, OwnedTabName, StringComparison.Ordinal))
        {
            reason = ReasonTabName;
            return NativeOuterTabSlotOwner.Owned;
        }
        return NativeOuterTabSlotOwner.Foreign;
    }

    /// <summary>The state list as it reads once the entry at <paramref name="index"/> is removed.</summary>
    internal static int[] Without(IReadOnlyList<int> states, int index)
    {
        if (states == null) throw new ArgumentNullException(nameof(states));
        if (index < 0 || index >= states.Count)
        {
            var copy = new int[states.Count];
            for (int i = 0; i < states.Count; i++) copy[i] = states[i];
            return copy;
        }
        var kept = new int[states.Count - 1];
        for (int i = 0, j = 0; i < states.Count; i++)
        {
            if (i == index) continue;
            kept[j++] = states[i];
        }
        return kept;
    }

    /// <summary>First index holding the owned state, or -1.</summary>
    internal static int IndexOfOwnedState(IReadOnlyList<int>? states)
    {
        if (states == null) return -1;
        for (int i = 0; i < states.Count; i++)
            if (states[i] == NativeOuterTabShape.OwnedState) return i;
        return -1;
    }

    /// <summary>The log line for one prune: <c>native route: pruned stale owned slot (views@7, page=…, gen=unknown)</c>.</summary>
    internal static string PruneNote(string where, int index, string pageName, string reason)
        => "native route: pruned stale owned slot (" + where + "@" + index
            + ", page=" + (string.IsNullOrEmpty(pageName) ? "destroyed" : pageName)
            + ", gen=unknown, why=" + reason + ")";

    // ---- NATIVE5: leftovers at any state ---------------------------------------------------

    /// <summary>
    /// Job NATIVE5. Keying the prune on state 7 alone was not enough: the leftover the live client
    /// refused on (<c>validate:outer-list-count:8</c>) was the previous generation's owned top-tab
    /// object, which the mapper picked up into <c>_OuterTabs</c> on menu init with a state that is
    /// not 7. It is ours by name, so it is removed whatever its <c>State</c>, and only then is the
    /// shape checked.
    /// </summary>
    internal static NativeOuterTabLeftover ClassifyListLeftover(in NativeOuterTabSlotCandidate candidate, out string reason)
    {
        reason = "";
        if (candidate.Ours) return NativeOuterTabLeftover.Keep;
        if (!candidate.Alive) { reason = ReasonTabDead; return NativeOuterTabLeftover.Prune; }
        if (string.Equals(candidate.ObjectName, OwnedTabName, StringComparison.Ordinal))
        {
            reason = ReasonTabName;
            return NativeOuterTabLeftover.Prune;
        }
        return candidate.State == NativeOuterTabShape.OwnedState
            ? NativeOuterTabLeftover.Refuse
            : NativeOuterTabLeftover.Keep;
    }

    /// <summary>
    /// The same rule for a <c>Views</c> record. A record whose page object carries the owned name
    /// goes whatever its state; a <em>dead</em> page is only read as ours at state 7, because a
    /// destroyed page under a native state belongs to the game's own record and removing it would
    /// shift every later view index.
    /// </summary>
    internal static NativeOuterTabLeftover ClassifyViewsLeftover(in NativeOuterTabSlotCandidate candidate, out string reason)
    {
        reason = "";
        if (candidate.Ours) return NativeOuterTabLeftover.Keep;
        if (candidate.Alive && string.Equals(candidate.ObjectName, OwnedPageName, StringComparison.Ordinal))
        {
            reason = ReasonPageName;
            return NativeOuterTabLeftover.Prune;
        }
        if (candidate.State != NativeOuterTabShape.OwnedState) return NativeOuterTabLeftover.Keep;
        if (!candidate.Alive) { reason = ReasonPageDead; return NativeOuterTabLeftover.Prune; }
        return NativeOuterTabLeftover.Refuse;
    }

    /// <summary>
    /// Plan one list's prune: the indices to remove in <em>descending</em> order (so the caller can
    /// remove them one by one without re-indexing), the marker of the last recognized leftover, and
    /// the index of the first foreign state-7 entry when the attach has to be refused instead.
    /// </summary>
    internal static int[] PlanPrune(
        IReadOnlyList<NativeOuterTabSlotCandidate>? entries, bool views, out string staleReason, out int refusalIndex)
    {
        staleReason = "";
        refusalIndex = -1;
        if (entries == null || entries.Count == 0) return Array.Empty<int>();
        var plan = new List<int>();
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var candidate = entries[i];
            var action = views
                ? ClassifyViewsLeftover(candidate, out var vw)
                : ClassifyListLeftover(candidate, out vw);
            if (action == NativeOuterTabLeftover.Refuse) refusalIndex = i;
            else if (action == NativeOuterTabLeftover.Prune)
            {
                plan.Add(i);
                if (string.IsNullOrEmpty(staleReason)) staleReason = vw;
            }
        }
        // A foreign state-7 owner is the verdict; nothing is removed for it.
        if (refusalIndex >= 0) { staleReason = ""; return Array.Empty<int>(); }
        return plan.ToArray();
    }

    /// <summary>The state list as it reads once every index in <paramref name="descending"/> is removed.</summary>
    internal static int[] WithoutMany(IReadOnlyList<int> states, IReadOnlyList<int>? descending)
    {
        if (states == null) throw new ArgumentNullException(nameof(states));
        int[] kept = Without(states, -1);
        if (descending == null) return kept;
        for (int i = 0; i < descending.Count; i++) kept = Without(kept, descending[i]);
        return kept;
    }

    /// <summary>
    /// Unload/Drop rule (NATIVE5 deliverable 2): an entry the mapper holds for an object of ours is
    /// removed from <c>_OuterTabs</c> as well as destroyed, so the next generation starts from the
    /// seven native entries. Only a live object bearing the owned name qualifies — Drop runs while
    /// the menu itself may be tearing down, and a dead entry there is not evidence of ownership.
    /// </summary>
    internal static bool DropRemovesListEntry(string? objectName, bool alive)
        => alive && string.Equals(objectName, OwnedTabName, StringComparison.Ordinal);
}

/// <summary>What the NATIVE5 prune planner decides about one live list entry.</summary>
internal enum NativeOuterTabLeftover
{
    /// <summary>Not ours (or our own current entry): left untouched.</summary>
    Keep = 0,
    /// <summary>Ours from an earlier generation: entry and object removed.</summary>
    Prune = 1,
    /// <summary>A live foreign owner of state 7: the attach is refused, nothing is removed.</summary>
    Refuse = 2,
}

/// <summary>One live <c>_OuterTabs</c> / <c>Views</c> entry as the NATIVE5 prune planner reads it.</summary>
internal readonly record struct NativeOuterTabSlotCandidate(
    int State,
    string? ObjectName,
    bool Alive,
    /// <summary>True for this registration's own clone/page: never a leftover.</summary>
    bool Ours = false);

/// <summary>What one reconcile pass decides about a registration that is supposed to be live.</summary>
internal enum NativeOuterTabReconcileAction
{
    /// <summary>Owned entry at 7 in both lists with our click route: nothing to do.</summary>
    Intact = 0,
    /// <summary>
    /// A native rebuild (<c>Initalize</c> / <c>OnStartRunning</c>) re-discovered the owned clone
    /// and installed the game's own <c>SetState(item.State)</c> closure. That closure is correct
    /// for state 7; keep it and do not add a second callback.
    /// </summary>
    AdoptNativeClosure = 1,
    /// <summary>Owned entry present but its click field is empty: assign the leased action again.</summary>
    Rewire = 2,
    /// <summary>The outer list was rebuilt without the owned entry while the seven native entries are intact: append again.</summary>
    Reappend = 3,
    /// <summary>The registration is no longer sound: detach and fall back.</summary>
    Detach = 4,
}

internal readonly record struct NativeOuterTabReconcileInput(
    bool MenuAlive,
    IReadOnlyList<int>? ListStates,
    int OwnedListIndex,
    int OwnedViewsIndex,
    bool CallbackPresent,
    bool CallbackIsOurs);

/// <summary>
/// Rebuild detection for a live registration. Reads are the caller's (pointer identity of the
/// owned entry / record in the live lists, pointer identity of the click field against the
/// leased action); this decides.
/// </summary>
internal static class NativeOuterTabReconcile
{
    internal const string ReasonHudDead = "hud-dead";
    internal const string ReasonViewsLost = "views-lost:";
    internal const string ReasonListMissing = "outer-list-missing";
    internal const string ReasonListRebuilt = "outer-list-rebuilt";
    internal const string ReasonListForeign = "outer-list-foreign:";
    internal const string ReasonListIndex = "outer-list-index:";
    internal const string ReasonCallbackNative = "callback-native";
    internal const string ReasonCallbackMissing = "callback-missing";
    /// <summary>
    /// NATIVE3 §4: the game re-appended its seven entries on a menu reopen (<c>OnStartRunning</c>
    /// appends on its missing/dead-menu branch without clearing; <c>Initalize</c> clears then
    /// re-adds), so the live list repeats native states after the owned slot. That list is a
    /// foreign change: only the owned entry is removed, the route refuses with this reason, and
    /// a doubled list is never re-appended into.
    /// </summary>
    internal const string ReasonListDoubled = "outer-list-doubled:";

    internal static (NativeOuterTabReconcileAction Action, string Reason) Decide(in NativeOuterTabReconcileInput input)
    {
        if (!input.MenuAlive) return (NativeOuterTabReconcileAction.Detach, ReasonHudDead);
        if (input.OwnedViewsIndex != NativeOuterTabShape.OwnedState)
            return (NativeOuterTabReconcileAction.Detach, ReasonViewsLost + input.OwnedViewsIndex);
        if (input.ListStates == null) return (NativeOuterTabReconcileAction.Detach, ReasonListMissing);
        if (input.OwnedListIndex < 0)
        {
            // Reappend only into an exact native rebuild (seven entries, in order). A doubled or
            // otherwise foreign list is refused; appending into it would leave a second MODS entry
            // the moment the game re-discovers the clone.
            if (input.ListStates.Count == NativeOuterTabShape.NativeStateCount
                && NativeOuterTabShape.NativePrefixIntact(input.ListStates))
                return (NativeOuterTabReconcileAction.Reappend, ReasonListRebuilt);
            if (NativeOuterTabShape.RepeatsNativeState(input.ListStates))
                return (NativeOuterTabReconcileAction.Detach, ReasonListDoubled + input.ListStates.Count);
            return (NativeOuterTabReconcileAction.Detach, ReasonListForeign + input.ListStates.Count);
        }
        if (input.OwnedListIndex != NativeOuterTabShape.OwnedState)
            return (NativeOuterTabReconcileAction.Detach, ReasonListIndex + input.OwnedListIndex);
        if (!NativeOuterTabShape.NativePrefixIntact(input.ListStates))
            return (NativeOuterTabReconcileAction.Detach, ReasonListForeign + "order");
        if (NativeOuterTabShape.RepeatsNativeState(input.ListStates))
            return (NativeOuterTabReconcileAction.Detach, ReasonListDoubled + input.ListStates.Count);
        if (input.CallbackIsOurs) return (NativeOuterTabReconcileAction.Intact, "");
        if (input.CallbackPresent) return (NativeOuterTabReconcileAction.AdoptNativeClosure, ReasonCallbackNative);
        return (NativeOuterTabReconcileAction.Rewire, ReasonCallbackMissing);
    }
}

/// <summary>The attach transaction's steps, in the order they run. Each step is rolled back in reverse on a later failure.</summary>
internal enum NativeOuterTabAttachStep
{
    Validate = 0,
    CloneDonor = 1,
    CreatePage = 2,
    AppendViews = 3,
    WireClick = 4,
    Publish = 5,
    Expose = 6,
}

/// <summary>
/// One attach step runner. <see cref="Run"/> returns null on success or the failure reason; a
/// step that fails must have undone its own partial work before returning. <see cref="Rollback"/>
/// undoes a step that completed.
/// </summary>
internal interface INativeOuterTabAttachSteps
{
    string? Run(NativeOuterTabAttachStep step);
    void Rollback(NativeOuterTabAttachStep step);
}

internal readonly record struct NativeOuterTabAttachResult(
    bool Ok,
    string? Reason,
    NativeOuterTabAttachStep? FailedAt,
    IReadOnlyList<NativeOuterTabAttachStep> RolledBack);

/// <summary>
/// The smallest guarded attach transaction: validate → clone the donor while unexposed → create
/// the page → append the <c>StateToView</c> → wire the click through the Bootstrap adapter →
/// publish → expose. A failure at any step rolls back every completed step in reverse order and
/// the tab is never exposed to input half-registered.
/// </summary>
internal static class NativeOuterTabTransaction
{
    internal static readonly NativeOuterTabAttachStep[] Order =
    {
        NativeOuterTabAttachStep.Validate,
        NativeOuterTabAttachStep.CloneDonor,
        NativeOuterTabAttachStep.CreatePage,
        NativeOuterTabAttachStep.AppendViews,
        NativeOuterTabAttachStep.WireClick,
        NativeOuterTabAttachStep.Publish,
        NativeOuterTabAttachStep.Expose,
    };

    internal static string StepName(NativeOuterTabAttachStep step) => step switch
    {
        NativeOuterTabAttachStep.Validate => "validate",
        NativeOuterTabAttachStep.CloneDonor => "clone-donor",
        NativeOuterTabAttachStep.CreatePage => "create-page",
        NativeOuterTabAttachStep.AppendViews => "append-views",
        NativeOuterTabAttachStep.WireClick => "wire-click",
        NativeOuterTabAttachStep.Publish => "publish",
        NativeOuterTabAttachStep.Expose => "expose",
        _ => "unknown",
    };

    /// <summary>Failure reasons are <c>step:detail</c> so a status line names both.</summary>
    internal static string FailureReason(NativeOuterTabAttachStep step, string detail) => StepName(step) + ":" + detail;

    internal static NativeOuterTabAttachResult Run(INativeOuterTabAttachSteps steps)
    {
        if (steps == null) throw new ArgumentNullException(nameof(steps));
        var done = new List<NativeOuterTabAttachStep>(Order.Length);
        for (int i = 0; i < Order.Length; i++)
        {
            var step = Order[i];
            string? reason;
            try { reason = steps.Run(step); }
            catch (Exception e) { reason = "threw:" + (string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message); }
            if (reason != null)
            {
                var rolled = new List<NativeOuterTabAttachStep>(done.Count);
                for (int j = done.Count - 1; j >= 0; j--)
                {
                    try { steps.Rollback(done[j]); }
                    catch { /* every remaining step must still roll back */ }
                    rolled.Add(done[j]);
                }
                return new NativeOuterTabAttachResult(false, FailureReason(step, reason), step, rolled);
            }
            done.Add(step);
        }
        return new NativeOuterTabAttachResult(true, null, null, Array.Empty<NativeOuterTabAttachStep>());
    }
}

/// <summary>Detach phases, in order. Removal never precedes a verified move off the owned state.</summary>
internal enum NativeOuterTabDetachPhase
{
    DisableActivation = 0,
    CloseWorkspace = 1,
    RestoreSelection = 2,
    VerifySelection = 3,
    ResetSelectedIndex = 4,
    RemoveListEntry = 5,
    RestoreViews = 6,
    ReleaseClick = 7,
    DestroyOwned = 8,
    Invalidate = 9,
    /// <summary>
    /// NATIVE3 §3: <c>OnStopRunning</c> nulls the mapper's <c>_CurrentView</c> but leaves
    /// <c>menu.CurrentView</c>; a record this detach destroys is never left there.
    /// </summary>
    ClearStaleCurrentView = 10,
}

internal enum NativeOuterTabViewsRestore
{
    /// <summary><c>menu.Views</c> is still exactly the array this registration installed: put the original back.</summary>
    RestoreOriginal = 0,
    /// <summary>Something replaced the array after us: remove only the owned record from the live array, preserving foreign edits.</summary>
    FilterOwned = 1,
    /// <summary>The menu is dead or the owned record is already gone: nothing to write.</summary>
    Skip = 2,
}

/// <summary>
/// Detach order from the research plan, and the decisions inside it. NATIVE3 §3 added the
/// menu-closing rule: <paramref name="ownedSelected"/> is the <em>mapper's</em> current view
/// (its <c>_CurrentView</c> record is the owned one), not <c>menu.CurrentView</c>, because
/// <c>OnStopRunning</c> nulls only the former; the typed <c>SetState(previous)</c> transition
/// runs only while the mapper still holds the owned page, and a failed transition holds the
/// registration only while the FullscreenMenu is still the HUD's current menu. Once the menu is
/// closing or gone, <c>_SelectedOuterTab</c> is reset through the typed field so the next
/// <c>OnUpdate</c> / menu open lands on a native page and never on 7 with a dead instance.
/// </summary>
internal static class NativeOuterTabDetachPlan
{
    internal static NativeOuterTabDetachPhase[] Order(bool menuAlive, bool ownedSelected, bool workspaceOpen)
        => Order(menuAlive, ownedSelected, workspaceOpen, mapperUsable: true);

    /// <param name="mapperUsable">
    /// The live client world still runs the mapper this registration wrote into. With the menu
    /// dead, this is the only mapper state touched: one typed field write that takes a remembered
    /// index 7 back to a native index (the mapper outlives the menu object).
    /// </param>
    internal static NativeOuterTabDetachPhase[] Order(bool menuAlive, bool ownedSelected, bool workspaceOpen, bool mapperUsable)
        => Order(menuAlive, ownedSelected, workspaceOpen, mapperUsable, menuObjectAlive: menuAlive);

    /// <param name="menuObjectAlive">
    /// NATIVE4: the <c>FullscreenMenu</c> object itself is alive, independently of whether this
    /// registration is still whole. <c>menuAlive</c> is the composite (menu <em>and</em> owned page
    /// <em>and</em> mapper); when the game destroyed only the owned page — a HUD rebuild takes the
    /// children of <c>ViewParent</c> with it — the menu still holds the owned <c>StateToView</c>,
    /// so the record and the list entry must still be removed. Skipping them is what left
    /// <c>views@7</c> occupied across a Core swap and pushed every later generation to the
    /// imitation route with <c>competing-slot-7:views@7</c>.
    /// </param>
    internal static NativeOuterTabDetachPhase[] Order(
        bool menuAlive, bool ownedSelected, bool workspaceOpen, bool mapperUsable, bool menuObjectAlive)
    {
        var phases = new List<NativeOuterTabDetachPhase>(11);
        if (!menuAlive)
        {
            // A destroyed HUD/world: never call its stale mapper. Release what is ours and invalidate.
            if (workspaceOpen) phases.Add(NativeOuterTabDetachPhase.CloseWorkspace);
            if (mapperUsable) phases.Add(NativeOuterTabDetachPhase.ResetSelectedIndex);
            if (mapperUsable && menuObjectAlive) phases.Add(NativeOuterTabDetachPhase.RemoveListEntry);
            if (menuObjectAlive)
            {
                phases.Add(NativeOuterTabDetachPhase.ClearStaleCurrentView);
                phases.Add(NativeOuterTabDetachPhase.RestoreViews);
            }
            phases.Add(NativeOuterTabDetachPhase.ReleaseClick);
            phases.Add(NativeOuterTabDetachPhase.DestroyOwned);
            phases.Add(NativeOuterTabDetachPhase.Invalidate);
            return phases.ToArray();
        }
        phases.Add(NativeOuterTabDetachPhase.DisableActivation);
        if (workspaceOpen) phases.Add(NativeOuterTabDetachPhase.CloseWorkspace);
        if (ownedSelected)
        {
            phases.Add(NativeOuterTabDetachPhase.RestoreSelection);
            phases.Add(NativeOuterTabDetachPhase.VerifySelection);
        }
        phases.Add(NativeOuterTabDetachPhase.ClearStaleCurrentView);
        phases.Add(NativeOuterTabDetachPhase.ResetSelectedIndex);
        phases.Add(NativeOuterTabDetachPhase.RemoveListEntry);
        phases.Add(NativeOuterTabDetachPhase.RestoreViews);
        phases.Add(NativeOuterTabDetachPhase.ReleaseClick);
        phases.Add(NativeOuterTabDetachPhase.DestroyOwned);
        phases.Add(NativeOuterTabDetachPhase.Invalidate);
        return phases.ToArray();
    }

    internal static NativeOuterTabViewsRestore DecideViewsRestore(bool menuAlive, bool viewsStillInstalledArray, bool viewsHoldsOwned)
    {
        if (!menuAlive) return NativeOuterTabViewsRestore.Skip;
        if (viewsStillInstalledArray) return NativeOuterTabViewsRestore.RestoreOriginal;
        return viewsHoldsOwned ? NativeOuterTabViewsRestore.FilterOwned : NativeOuterTabViewsRestore.Skip;
    }

    /// <summary>
    /// <c>OnUpdate</c> re-selects <c>_SelectedOuterTab</c> when there is no current view, and
    /// <c>SetState</c> of a state that no longer exists is not a no-op. A stopped mapper that
    /// still remembers index 7 is therefore pointed at the return state before the record goes.
    /// </summary>
    internal static int SelectedIndexAfterDetach(int selectedOuterTab, int? returnState)
        => selectedOuterTab == NativeOuterTabShape.OwnedState ? NativeOuterTabShape.ReturnStateFor(returnState) : selectedOuterTab;

    /// <summary>Removal of the owned record is allowed only once the mapper verifiably no longer selects it.</summary>
    internal static bool MayRemoveRegistration(bool ownedSelected, bool selectionRestored) => !ownedSelected || selectionRestored;

    /// <summary>
    /// NATIVE3 §3: a failed transition holds the registration only while the FullscreenMenu is
    /// still the HUD's current menu (a live page the player can still leave by clicking a native
    /// tab). While the menu is closing or gone the hold would leave the mapper on index 7 with no
    /// page to show on the next open, so removal proceeds and the index is reset instead.
    /// </summary>
    internal static bool MayRemoveRegistration(bool ownedSelected, bool selectionRestored, bool menuRunning)
        => MayRemoveRegistration(ownedSelected, selectionRestored) || !menuRunning;

    /// <summary>
    /// NATIVE4: on Core unload the registration is removed whatever the menu state. A hold exists
    /// so a live page the player can still leave is not destroyed under the mapper; there is no
    /// later tick to retry it on when this generation is going away, and a held registration would
    /// leave the owned <c>StateToView</c> at state 7 for every later generation to refuse. The
    /// return transition is still attempted first while the mapper holds the owned page; only the
    /// hold on its failure is dropped.
    /// </summary>
    internal static bool MayRemoveRegistration(bool ownedSelected, bool selectionRestored, bool menuRunning, bool coreUnload)
        => coreUnload || MayRemoveRegistration(ownedSelected, selectionRestored, menuRunning);

    /// <summary>
    /// The owned page counts as selected for the transition only when the mapper still holds it
    /// as <c>_CurrentView</c> (so <c>SetState</c> deactivates the owned instance and activates
    /// the return page like any native transition). After <c>OnStopRunning</c> that field is null
    /// and a <c>SetState</c> would activate a page under a closed menu: skip it and reset the index.
    /// </summary>
    internal static bool OwnedSelectedForDetach(bool menuAlive, bool mapperCurrentIsOwned) => menuAlive && mapperCurrentIsOwned;
}

/// <summary>
/// NATIVE3 §2: the native-route <c>mods_ui open site=top-tab</c> / <c>close</c> paths run the
/// same sequence a tab click does, expressed over a host so the order is testable game-free:
/// open = typed <c>SetState(7)</c> through the registration (<c>TrySelectOwned</c>), then the
/// stored workspace open; close = the owned <c>ModsMenu.Close</c>, then the typed return
/// transition (<c>ReturnSelection</c>) when the mapper still selects the owned page.
/// </summary>
internal interface INativeRouteHost
{
    bool WorkspaceOpen { get; }
    bool WorkspaceSuspended { get; }
    /// <summary>The mapper's current page is the owned one.</summary>
    bool OwnedIsCurrent { get; }
    /// <summary>The native page to return to, read before the owned selection.</summary>
    int? CurrentNativeState { get; }
    bool SelectOwned(out string why);
    void OpenWorkspace();
    void CloseWorkspace();
    bool ReturnSelection(out string why);
}

internal enum NativeRouteOpStep
{
    SelectOwned = 0,
    OpenWorkspace = 1,
    CloseWorkspace = 2,
    ReturnSelection = 3,
}

internal readonly record struct NativeRouteOpResult(bool Ok, string Why, IReadOnlyList<NativeRouteOpStep> Steps)
{
    internal const string WhyAlreadyOpen = "already-open";
    internal const string WhySuspended = "suspended";
    internal const string WhyAlreadyClosed = "already-closed";
    internal const string WhyNotSelected = "not-selected";
}

internal static class NativeRouteOps
{
    /// <summary>The click path: refuse while open/suspended (like the imitation button), select, then open.</summary>
    internal static NativeRouteOpResult Open(INativeRouteHost host)
    {
        if (host == null) throw new ArgumentNullException(nameof(host));
        var steps = new List<NativeRouteOpStep>(2);
        if (host.WorkspaceSuspended) return new NativeRouteOpResult(false, NativeRouteOpResult.WhySuspended, steps);
        if (host.WorkspaceOpen) return new NativeRouteOpResult(false, NativeRouteOpResult.WhyAlreadyOpen, steps);
        steps.Add(NativeRouteOpStep.SelectOwned);
        if (!host.SelectOwned(out var why)) return new NativeRouteOpResult(false, why ?? "", steps);
        steps.Add(NativeRouteOpStep.OpenWorkspace);
        host.OpenWorkspace();
        return new NativeRouteOpResult(true, "", steps);
    }

    /// <summary>
    /// The close path: close the workspace through its lifecycle, then move the mapper back to
    /// the saved native page while it still selects the owned one. Ok is the close; a failed
    /// return is reported in <c>Why</c> and retried by the tick.
    /// </summary>
    internal static NativeRouteOpResult Close(INativeRouteHost host)
    {
        if (host == null) throw new ArgumentNullException(nameof(host));
        var steps = new List<NativeRouteOpStep>(2);
        if (!host.WorkspaceOpen)
        {
            if (!host.OwnedIsCurrent) return new NativeRouteOpResult(true, NativeRouteOpResult.WhyAlreadyClosed, steps);
        }
        else
        {
            steps.Add(NativeRouteOpStep.CloseWorkspace);
            host.CloseWorkspace();
        }
        if (!host.OwnedIsCurrent) return new NativeRouteOpResult(true, NativeRouteOpResult.WhyNotSelected, steps);
        steps.Add(NativeRouteOpStep.ReturnSelection);
        bool returned = host.ReturnSelection(out var why);
        return new NativeRouteOpResult(true, returned ? "" : why ?? "", steps);
    }
}

internal enum ModsEntryRoute
{
    Imitation = 0,
    Native = 1,
}

/// <summary>Per-tick decision for the native route's selection/workspace coupling.</summary>
internal enum NativeRouteTickAction
{
    None = 0,
    /// <summary>The workspace is open (shortcut / reattach) and the mapper does not select the owned page yet: <c>SetState(7)</c>.</summary>
    SelectOwned = 1,
    /// <summary>The mapper selected the owned page without a workspace (native closure, gamepad): open it.</summary>
    OpenWorkspace = 2,
    /// <summary>The mapper moved off the owned page while the workspace is open: close it through its lifecycle.</summary>
    CloseWorkspace = 3,
    /// <summary>The workspace closed on its own while the owned page is current: move the mapper to the return state.</summary>
    ReturnSelection = 4,
    /// <summary>Steady: keep the workspace chrome mounted under the owned page.</summary>
    Remount = 5,

    /// <summary>
    /// F10SETTLE: the mapper moved off the owned page while a menu the shortcut raised is still
    /// settling (it selects its remembered tab once it finishes opening): select the owned page again
    /// instead of closing (<see cref="ModsShortcutRoute.SettleReselectAllowed"/>).
    /// </summary>
    ReselectOwned = 6,
}

/// <summary>Route selection and the native route's tick state machine (pure).</summary>
internal static class NativeRouteSelection
{
    internal const string ReasonAdapterUnavailable = "adapter-unavailable";
    internal const string ReasonMapperMissing = "mapper-missing";
    internal const string ReasonTestBuild = "test-build";
    internal const string ReasonNotAttempted = "not-attempted";

    internal static bool ShouldTryNative(int adapterCapability) => adapterCapability >= 1;

    /// <summary>The native route is selected only when the attach transaction reported no reason at all.</summary>
    internal static ModsEntryRoute Decide(string? attachReason)
        => attachReason == null ? ModsEntryRoute.Native : ModsEntryRoute.Imitation;

    /// <summary>
    /// NATIVE3 §3: the tick resolves the FullscreenMenu through the HUD's current menu type, so a
    /// registration with no found menu means the menu is closing or gone (Escape, HUD None, a
    /// rebuilt HUD). The registration is then detached on the menu-closing rule before anything
    /// else runs — including a registration an earlier detach held while the menu was still up.
    /// </summary>
    internal static bool ShouldDetachOnMenuClosed(bool nativeActive, bool foundMenuAlive) => nativeActive && !foundMenuAlive;

    internal static NativeRouteTickAction DecideTick(bool workspaceOpen, bool ownedIsCurrent, bool selectedByUs, bool returnFailed)
        => DecideTick(workspaceOpen, ownedIsCurrent, selectedByUs, returnFailed, settling: false);

    /// <summary>
    /// As above; <paramref name="settling"/> (F10SETTLE) turns the close on a native tab change into a
    /// re-selection of the owned page while a raised menu is still finishing its open.
    /// </summary>
    internal static NativeRouteTickAction DecideTick(bool workspaceOpen, bool ownedIsCurrent, bool selectedByUs, bool returnFailed, bool settling)
    {
        if (workspaceOpen)
        {
            if (!selectedByUs) return NativeRouteTickAction.SelectOwned;
            if (!ownedIsCurrent) return settling ? NativeRouteTickAction.ReselectOwned : NativeRouteTickAction.CloseWorkspace;
            // NATIVE3 §1: the chrome root is a top-level overlay canvas on this route (the panes
            // are top-level canvases placed from the same screen-space frame), so the steady
            // state has nothing to remount; the workspace follows CurrentView through this tick.
            return NativeModsEntry.NativeRouteMountsChrome ? NativeRouteTickAction.Remount : NativeRouteTickAction.None;
        }
        if (selectedByUs) return NativeRouteTickAction.ReturnSelection;
        if (ownedIsCurrent && !returnFailed) return NativeRouteTickAction.OpenWorkspace;
        return NativeRouteTickAction.None;
    }
}

/// <summary>
/// Job NATIVE2: the first live client refused the native route at its first invariant with a
/// bare <c>validate:mapper-menu-mismatch</c>. The reason now carries both sides so the record
/// can classify the case from <c>status.nativeRoute.reason</c> alone:
/// <c>mapper-menu-mismatch:mapper=&lt;0x…|null&gt;,found=&lt;0x…&gt;,mapperPath=&lt;path|n/a|dead&gt;,foundPath=&lt;path&gt;,mapperRunning=&lt;bool&gt;</c>.
/// (a) <c>mapper=null</c>: the mapper has not run <c>InitializeMenu</c> for this HUD yet — a
/// timing gap, retried while the menu is up. (b) two live pointers: the mapper's own menu is the
/// registration authority. (c) equal pointers never reach this reason.
/// </summary>
internal static class NativeOuterTabMismatch
{
    internal const string Prefix = "mapper-menu-mismatch";
    internal const string NotAvailable = "n/a";
    internal const string Dead = "dead";

    internal static string Hex(IntPtr ptr) => "0x" + ptr.ToString("x");

    internal static string Format(IntPtr? mapperMenu, IntPtr foundMenu, string? mapperPath, string foundPath, bool mapperRunning)
    {
        string mapper = mapperMenu is IntPtr m && m != IntPtr.Zero ? Hex(m) : "null";
        string mp = string.IsNullOrEmpty(mapperPath) ? NotAvailable : mapperPath!;
        string fp = string.IsNullOrEmpty(foundPath) ? NotAvailable : foundPath;
        return Prefix + ":mapper=" + mapper + ",found=" + Hex(foundMenu)
            + ",mapperPath=" + Sanitize(mp) + ",foundPath=" + Sanitize(fp)
            + ",mapperRunning=" + (mapperRunning ? "true" : "false");
    }

    /// <summary>Path segments never carry the field separator, so the reason stays parseable.</summary>
    private static string Sanitize(string value) => value.Replace(',', ';');

    /// <summary>True for a bare or rich mismatch reason, with or without the <c>validate:</c> step prefix.</summary>
    internal static bool IsMismatch(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return false;
        string r = reason!;
        if (r.StartsWith("validate:", StringComparison.Ordinal)) r = r.Substring("validate:".Length);
        return r == Prefix || r.StartsWith(Prefix + ":", StringComparison.Ordinal);
    }

    /// <summary>
    /// Case (a): the mapper holds no live menu yet. Only this case is retried; a mismatch of
    /// two live objects is resolved by authority at attach and a bare reason is not retried.
    /// </summary>
    internal static bool IsRetryable(string? reason)
    {
        if (!IsMismatch(reason)) return false;
        return reason!.Contains(":mapper=null,", StringComparison.Ordinal)
            || reason.Contains(",mapperPath=" + Dead + ",", StringComparison.Ordinal);
    }
}

/// <summary>Which <c>FullscreenMenu</c> a registration validates and registers against.</summary>
internal enum NativeOuterTabMenuAuthority
{
    /// <summary>The mapper's menu is the found HUD menu: register against it.</summary>
    Found = 0,
    /// <summary>The mapper holds a different live menu: that one drives <c>SetState</c>, so it is the authority.</summary>
    Mapper = 1,
    /// <summary>The mapper holds no live menu yet: refuse now, retry while the found menu is up.</summary>
    None = 2,
}

/// <summary>
/// The authority rule (NATIVE2): the mapper's <c>_Menu</c> is what <c>SetState</c>, <c>OnUpdate</c>
/// and the click closures act on, so when it is alive it is the menu to register into even if the
/// HUD lookup returned another instance. A null/dead mapper menu is a timing gap, not a verdict.
/// </summary>
internal static class NativeOuterTabMenuChoice
{
    internal const string AuthorityNotePrefix = "menu-authority:mapper";

    internal static NativeOuterTabMenuAuthority Decide(IntPtr mapperMenu, bool mapperMenuAlive, IntPtr foundMenu)
    {
        if (mapperMenu == IntPtr.Zero || !mapperMenuAlive) return NativeOuterTabMenuAuthority.None;
        return mapperMenu == foundMenu ? NativeOuterTabMenuAuthority.Found : NativeOuterTabMenuAuthority.Mapper;
    }

    /// <summary>Readback note kept on the native status while the mapper's menu was chosen over the found one.</summary>
    internal static string AuthorityNote(string? mapperPath, string? foundPath)
        => AuthorityNotePrefix + ",mapperPath=" + (string.IsNullOrEmpty(mapperPath) ? NativeOuterTabMismatch.NotAvailable : mapperPath!.Replace(',', ';'))
            + ",foundPath=" + (string.IsNullOrEmpty(foundPath) ? NativeOuterTabMismatch.NotAvailable : foundPath!.Replace(',', ';'));
}

/// <summary>
/// Retry cadence for a retryable refusal (NATIVE2): every <see cref="IntervalFrames"/> frames
/// while the found menu is alive, until <see cref="MaxAttempts"/> attach attempts (the first
/// one included) have run on this host. The imitation route stays attached meanwhile; a retry
/// never runs while the workspace is open or suspended (it would be dropped mid-use).
/// </summary>
internal static class NativeRouteRetry
{
    internal const int IntervalFrames = 30;
    internal const int MaxAttempts = 20;

    /// <summary>NATIVE5: how many shape/count refusals are retried before the imitation route is final.</summary>
    internal const int MaxShapeAttempts = 5;

    internal const string NextAttemptMenuOpen = "menu-open";
    internal const string NextAttemptNone = "none";

    /// <summary>
    /// NATIVE5. A refusal about the <em>shape</em> of the mapper's lists — a count, an order, or an
    /// owned-slot index — describes leftovers that a later menu open can clear (the imitation
    /// attach's own sweep, this build's prune, or the game rebuilding its list). It is retried.
    /// </summary>
    internal static bool IsShapeRefusal(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return false;
        string r = reason!;
        if (r.StartsWith("validate:", StringComparison.Ordinal)) r = r.Substring("validate:".Length);
        return r.StartsWith(NativeOuterTabShape.ReasonListCount, StringComparison.Ordinal)
            || r.StartsWith(NativeOuterTabShape.ReasonViewsCount, StringComparison.Ordinal)
            || r.StartsWith(NativeOuterTabShape.ReasonListOrder, StringComparison.Ordinal)
            || r.StartsWith(NativeOuterTabShape.ReasonViewsOrder, StringComparison.Ordinal)
            || r.StartsWith(NativeOuterTabShape.ReasonOwnedSlot, StringComparison.Ordinal)
            || r == NativeOuterTabShape.ReasonListMissing
            || r == NativeOuterTabShape.ReasonViewsMissing;
    }

    /// <summary>
    /// Terminal for the process: no click adapter, and a live foreign owner of state 7. Neither is
    /// cleared by reopening the menu, and the foreign entry is never removed.
    /// </summary>
    internal static bool IsTerminal(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return false;
        string r = reason!;
        if (r.StartsWith("validate:", StringComparison.Ordinal)) r = r.Substring("validate:".Length);
        return r.StartsWith(NativeRouteSelection.ReasonAdapterUnavailable, StringComparison.Ordinal)
            || r.StartsWith(NativeOuterTabShape.ReasonCompetingSlot, StringComparison.Ordinal);
    }

    internal static bool ShouldArm(string? attachReason)
        => !IsTerminal(attachReason)
            && (NativeOuterTabMismatch.IsRetryable(attachReason) || IsShapeRefusal(attachReason));

    /// <summary>The budget that applies to this reason: shape refusals get the smaller one.</summary>
    internal static int BudgetFor(string? attachReason) => IsShapeRefusal(attachReason) ? MaxShapeAttempts : MaxAttempts;

    /// <summary>
    /// <c>nativeRoute.nextAttemptOn</c>: <c>menu-open</c> while a further attempt is due the next
    /// time a <c>FullscreenMenu</c> is up (after the imitation attach's sweep in that same open),
    /// else <c>none</c>.
    /// </summary>
    internal static string NextAttemptOn(bool armed) => armed ? NextAttemptMenuOpen : NextAttemptNone;

    /// <summary>One frame of the retry counter; true when an attempt is due this frame.</summary>
    internal static bool Advance(ref int frames, bool armed, bool menuAlive, bool nativeActive, bool workspaceBusy, int attempts, int budget = MaxAttempts)
    {
        if (!armed || !menuAlive || nativeActive || attempts >= budget)
        {
            frames = 0;
            return false;
        }
        if (workspaceBusy) return false;
        if (++frames < IntervalFrames) return false;
        frames = 0;
        return true;
    }
}

/// <summary><c>mods_ui status.nativeRoute</c> / <c>rects.detail.nativeRoute</c> shape.</summary>
internal sealed record NativeRouteStatus(
    ModsEntryRoute Route,
    int State,
    int ViewsIndex,
    int ListIndex,
    string Reason,
    string Callback,
    bool Selected,
    bool Mounted,
    int Adapter,
    int Attempts = 0,
    /// <summary>NATIVE4: stale owned state-7 entries this attach pruned (Views record + outer-list clone).</summary>
    int Pruned = 0,
    /// <summary>NATIVE4: how the pruned slot was recognized (<c>page-name</c>, <c>page-dead</c>, <c>tab-name</c>), else empty.</summary>
    string StaleReason = "",
    /// <summary>NATIVE5: whether a further native attach attempt is due for this reason.</summary>
    bool Retryable = false,
    /// <summary>NATIVE5: <c>menu-open</c> or <c>none</c>.</summary>
    string NextAttemptOn = NativeRouteRetry.NextAttemptNone)
{
    internal static NativeRouteStatus Imitation(string reason, int adapter, int attempts = 0, int pruned = 0, string staleReason = "", bool retryable = false)
        => new(ModsEntryRoute.Imitation, NativeOuterTabShape.OwnedState, -1, -1, reason ?? "", "none", false, false, adapter, attempts, pruned, staleReason ?? "",
            retryable, NativeRouteRetry.NextAttemptOn(retryable));

    internal static NativeRouteStatus Native(int viewsIndex, int listIndex, string callback, bool selected, bool mounted, int adapter, string reason = "", int attempts = 0, int pruned = 0, string staleReason = "")
        => new(ModsEntryRoute.Native, NativeOuterTabShape.OwnedState, viewsIndex, listIndex, reason ?? "", callback, selected, mounted, adapter, attempts, pruned, staleReason ?? "",
            false, NativeRouteRetry.NextAttemptNone);

    internal string RouteName => Route == ModsEntryRoute.Native ? "native" : "imitation";

    internal Dictionary<string, object?> Describe() => new()
    {
        ["state"] = State,
        ["viewsIndex"] = ViewsIndex,
        ["listIndex"] = ListIndex,
        ["reason"] = Reason,
        ["callback"] = Callback,
        ["selected"] = Selected,
        ["mounted"] = Mounted,
        ["adapter"] = Adapter,
        ["attempts"] = Attempts,
        ["pruned"] = Pruned,
        ["staleReason"] = StaleReason,
        ["retryable"] = Retryable,
        ["nextAttemptOn"] = NextAttemptOn,
    };
}

#if !PROGENITOR_TESTS
/// <summary>
/// One owned registration in the game's <c>FullscreenMenuMapper</c>: an eighth
/// <c>FullscreenMenu_OuterTabEntry</c> cloned from a pristine native donor, an owned page under
/// <c>ViewParent</c> bearing the game's existing concrete <c>FullscreenMenuView</c>, and an owned
/// <c>StateToView</c> appended to <c>Views</c>. Typed interop only. The click route is the
/// Bootstrap <c>UiActionSlot</c> trampoline (a process-lifetime <c>Il2CppSystem.Action</c>) —
/// never a Core method converted into a native delegate. <see cref="NativeModsEntry"/> owns
/// the lifecycle; this type owns the transaction, reconcile and detach order.
/// </summary>
internal sealed class NativeOuterTabRegistration : INativeOuterTabAttachSteps
{
    internal const string PageObjectName = "progenitor-ui-core-mods-page";
    internal const string Caption = "MODS";

    private static readonly object Gate = new();
    private static readonly Dictionary<ushort, NativeOuterTabRegistration> Live = new();
    private static ushort _nextSlot = 1;
    private static bool _dispatchInstalled;

    /// <summary>
    /// NATIVE5 readback that outlives one attempt: what the last attach attempt pruned, and how it
    /// recognized the leftover. A refused transaction throws its registration away, so these are
    /// where <see cref="NativeModsEntry"/> picks the counters up for <c>mods_ui status</c>.
    /// </summary>
    internal static int LastAttachPruned;
    internal static string LastAttachStaleReason = "";

    private readonly FullscreenMenu _menu;
    private readonly FullscreenMenuMapper _mapper;
    private readonly Action _onClick;
    private FullscreenMenu_OuterTabEntry? _donor;
    private GameObject? _clone;
    private FullscreenMenu_OuterTabEntry? _entry;
    private GameObject? _pageGo;
    private FullscreenMenuView? _page;
    private FullscreenMenu.StateToView? _record;
    private Il2CppReferenceArray<FullscreenMenu.StateToView>? _originalViews;
    private Il2CppReferenceArray<FullscreenMenu.StateToView>? _installedViews;
    private ushort _slotId;
    private Il2CppSystem.Action? _leased;
    private IntPtr _leasedPtr;
    private string _callback = "none";
    private bool _exposed;
    private bool _published;
    private bool _detached;
    private GameObject? _mounted;
    private int _pruned;
    private string _staleReason = "";
    /// <summary>False for <see cref="Probe"/>: the rehearsal classifies a stale slot but writes nothing.</summary>
    private bool _pruneAllowed = true;

    private NativeOuterTabRegistration(FullscreenMenu menu, FullscreenMenuMapper mapper, Action onClick)
    {
        _menu = menu;
        _mapper = mapper;
        _onClick = onClick;
    }

    internal GameObject? Clone => Alive(_clone) ? _clone : null;
    internal GameObject? Page => Alive(_pageGo) ? _pageGo : null;
    internal bool Detached => _detached;
    internal string CallbackOwner => _callback;
    /// <summary>NATIVE4: stale owned state-7 entries this attach pruned (readback <c>nativeRoute.pruned</c>).</summary>
    internal int Pruned => _pruned;
    /// <summary>NATIVE4: how the pruned slot was recognized (readback <c>nativeRoute.staleReason</c>).</summary>
    internal string StaleReason => _staleReason;
    internal bool Mounted => Alive(_mounted);
    /// <summary>The menu this registration registered into (the mapper's menu when it differed from the found one).</summary>
    internal FullscreenMenu Menu => _menu;
    /// <summary>
    /// Non-null when the mapper's menu was chosen over the found HUD menu (NATIVE2 case (b)):
    /// <c>menu-authority:mapper,mapperPath=…,foundPath=…</c>, kept on the native readback for the record.
    /// </summary>
    internal string? AuthorityNote { get; private set; }

    /// <summary>
    /// Pre-transaction checks shared by <see cref="TryAttach"/> and <see cref="Probe"/>: adapter,
    /// mapper, found menu, then the authority rule. Returns the menu to register into or null
    /// with <paramref name="reason"/>. Typed reads only: one field read (<c>_Menu</c>), two
    /// transform-path walks bounded at 64 parents, one <c>Enabled</c> read.
    /// </summary>
    private static FullscreenMenu? ResolveMenu(
        FullscreenMenu? found, FullscreenMenuMapper? mapper, out string? reason, out string? note)
    {
        note = null;
        if (!NativeRouteSelection.ShouldTryNative(BootstrapLink.UiActionCapability))
        {
            reason = NativeRouteSelection.ReasonAdapterUnavailable;
            return null;
        }
        if (mapper == null || mapper.Pointer == IntPtr.Zero)
        {
            reason = NativeRouteSelection.ReasonMapperMissing;
            return null;
        }
        if (found == null || !Alive(found))
        {
            reason = NativeOuterTabTransaction.FailureReason(NativeOuterTabAttachStep.Validate, "menu-dead");
            return null;
        }
        FullscreenMenu? mapperMenu = null;
        try { mapperMenu = mapper._Menu; } catch { mapperMenu = null; }
        bool mapperAlive = Alive(mapperMenu);
        IntPtr mapperPtr = mapperMenu != null ? mapperMenu.Pointer : IntPtr.Zero;
        switch (NativeOuterTabMenuChoice.Decide(mapperPtr, mapperAlive, found!.Pointer))
        {
            case NativeOuterTabMenuAuthority.Found:
                reason = null;
                return found;
            case NativeOuterTabMenuAuthority.Mapper:
                note = NativeOuterTabMenuChoice.AuthorityNote(MenuPath(mapperMenu), MenuPath(found));
                reason = null;
                return mapperMenu;
            default:
                reason = NativeOuterTabTransaction.FailureReason(
                    NativeOuterTabAttachStep.Validate, MismatchReason(mapper, mapperMenu, found));
                return null;
        }
    }

    /// <summary>The NATIVE2 rich mismatch reason (both pointers, both paths, the mapper's <c>Enabled</c>).</summary>
    private static string MismatchReason(FullscreenMenuMapper mapper, FullscreenMenu? mapperMenu, FullscreenMenu found)
    {
        IntPtr? mapperPtr = mapperMenu == null ? null : mapperMenu.Pointer;
        string? mapperPath = mapperMenu == null ? null
            : mapperMenu.Pointer == IntPtr.Zero ? NativeOuterTabMismatch.Dead
            : MenuPath(mapperMenu);
        bool running = false;
        try { running = mapper.Enabled; } catch { running = false; }
        return NativeOuterTabMismatch.Format(mapperPtr, found.Pointer, mapperPath, MenuPath(found), running);
    }

    private static string MenuPath(FullscreenMenu? menu)
    {
        if (menu == null || menu.Pointer == IntPtr.Zero) return NativeOuterTabMismatch.Dead;
        try
        {
            var parts = new List<string>();
            Transform? cur = menu.transform;
            int guard = 0;
            while (cur != null && guard++ < 64)
            {
                string name;
                try { name = cur.gameObject.name ?? ""; } catch { name = "?"; }
                parts.Add(name);
                cur = cur.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
        catch
        {
            return NativeOuterTabMismatch.NotAvailable;
        }
    }

    /// <summary>
    /// Read-only rehearsal of the pre-checks and the validate step (NATIVE2 retry): nothing is
    /// cloned, created or written. True when an attach would pass validation now.
    /// </summary>
    internal static bool Probe(FullscreenMenu? found, FullscreenMenuMapper? mapper, out string? reason)
    {
        var menu = ResolveMenu(found, mapper, out reason, out _);
        if (menu == null) return false;
        var rehearsal = new NativeOuterTabRegistration(menu, mapper!, static () => { }) { _pruneAllowed = false };
        string? validate;
        try { validate = rehearsal.StepValidate(); }
        catch (Exception e) { validate = "threw:" + e.Message; }
        rehearsal._donor = null;
        rehearsal._detached = true;
        if (validate != null)
        {
            reason = NativeOuterTabTransaction.FailureReason(NativeOuterTabAttachStep.Validate, validate);
            return false;
        }
        reason = null;
        return true;
    }

    /// <summary>
    /// Run the attach transaction. Null with <paramref name="reason"/> set when any invariant
    /// failed; every completed step was rolled back and nothing owned is left in the HUD.
    /// The registration target is the mapper's own menu when it is alive and differs from
    /// <paramref name="menu"/> (<see cref="AuthorityNote"/> records both paths).
    /// </summary>
    internal static NativeOuterTabRegistration? TryAttach(
        FullscreenMenu? menu, FullscreenMenuMapper? mapper, Action onClick, out string? reason)
    {
        if (onClick == null) throw new ArgumentNullException(nameof(onClick));
        LastAttachPruned = 0;
        LastAttachStaleReason = "";
        var target = ResolveMenu(menu, mapper, out reason, out var note);
        if (target == null) return null;
        var registration = new NativeOuterTabRegistration(target, mapper!, onClick) { AuthorityNote = note };
        var result = NativeOuterTabTransaction.Run(registration);
        if (!result.Ok)
        {
            registration._detached = true;
            reason = result.Reason;
            return null;
        }
        reason = null;
        return registration;
    }

    // ---- transaction steps ------------------------------------------------------------------

    public string? Run(NativeOuterTabAttachStep step) => step switch
    {
        NativeOuterTabAttachStep.Validate => StepValidate(),
        NativeOuterTabAttachStep.CloneDonor => StepCloneDonor(),
        NativeOuterTabAttachStep.CreatePage => StepCreatePage(),
        NativeOuterTabAttachStep.AppendViews => StepAppendViews(),
        NativeOuterTabAttachStep.WireClick => StepWireClick(),
        NativeOuterTabAttachStep.Publish => StepPublish(),
        NativeOuterTabAttachStep.Expose => StepExpose(),
        _ => "unknown-step",
    };

    public void Rollback(NativeOuterTabAttachStep step)
    {
        switch (step)
        {
            case NativeOuterTabAttachStep.Expose:
                DisableActivation();
                break;
            case NativeOuterTabAttachStep.Publish:
                RemoveListEntry();
                RestoreViews();
                break;
            case NativeOuterTabAttachStep.WireClick:
                ReleaseClick();
                break;
            case NativeOuterTabAttachStep.AppendViews:
                _installedViews = null;
                _originalViews = null;
                _record = null;
                break;
            case NativeOuterTabAttachStep.CreatePage:
                DestroyPage();
                break;
            case NativeOuterTabAttachStep.CloneDonor:
                DestroyClone();
                break;
        }
    }

    private string? StepValidate()
    {
        if (!Alive(_menu)) return "menu-dead";
        FullscreenMenu? mapperMenu = null;
        try { mapperMenu = _mapper._Menu; } catch { /* reported below */ }
        // The target was chosen by the authority rule; a mismatch here is a change since then.
        if (mapperMenu == null || mapperMenu.Pointer != _menu.Pointer) return MismatchReason(_mapper, mapperMenu, _menu);
        if (!Alive(_menu.ViewParent)) return "view-parent-dead";
        if (!Alive(_menu.OuterTabGroup)) return "outer-group-dead";
        var list = _mapper._OuterTabs;
        var views = _menu.Views;
        var listStates = ReadListStates(list);
        var viewsStates = ReadViewStates(views);
        // NATIVE4: an earlier generation's owned slot is pruned here, before the shape check turns
        // it into competing-slot-7. A foreign state-7 entry still refuses, unchanged.
        string? competing = ResolveCompetingSlots(list, views, ref listStates, ref viewsStates);
        if (competing != null) return competing;
        string? shape = NativeOuterTabShape.Validate(listStates, viewsStates, NativeOuterTabShape.OwnedState);
        if (shape != null) return shape;
        _donor = FindDonor(list!);
        if (_donor == null) return "donor-missing";
        return null;
    }

    /// <summary>
    /// NATIVE4. Classify whatever already occupies state 7 and, when it is this integration's
    /// leftover from an earlier generation, remove it so the attach can continue: the outer-list
    /// clone (and its object), then the <c>Views</c> record (and its page object). The state lists
    /// are rewritten to their post-prune shape so the shape check sees what the live lists now hold.
    /// Returns the competing-slot refusal for a foreign entry, null otherwise. <see cref="Probe"/>
    /// classifies without writing: the rehearsal reports "prunable" so a retry does not count the
    /// stale record as a failure, and the real attach performs the removal.
    /// </summary>
    private string? ResolveCompetingSlots(
        Il2CppSystem.Collections.Generic.List<FullscreenMenu_OuterTabEntry>? list,
        Il2CppReferenceArray<FullscreenMenu.StateToView>? views,
        ref int[]? listStates,
        ref int[]? viewsStates)
    {
        // NATIVE5: every owned leftover, not only a record that still says state 7. The live
        // refusal was validate:outer-list-count:8 on an owned top-tab object the mapper had
        // picked up with a foreign state, which the state-keyed prune could not see.
        var listCandidates = ReadListCandidates(list, listStates);
        var listPlan = NativeOuterTabStaleSlot.PlanPrune(listCandidates, views: false, out var listWhy, out int listRefusal);
        if (listRefusal >= 0) return NativeOuterTabShape.ReasonCompetingSlot + "outer-list@" + listRefusal;
        if (listPlan.Length > 0)
        {
            if (_pruneAllowed)
            {
                for (int i = 0; i < listPlan.Length; i++)
                {
                    int index = listPlan[i];
                    var candidate = listCandidates![index];
                    GameObject? go = null;
                    try { go = list != null && index < list.Count ? list[index]?.gameObject : null; } catch { go = null; }
                    try { list?.RemoveAt(index); } catch { /* the shape check refuses below if it stayed */ }
                    if (Alive(go)) DestroyOwnedObject(go);
                    RecordPrune("outer-list", index, candidate.ObjectName ?? "", listWhy);
                }
            }
            if (listStates != null) listStates = NativeOuterTabStaleSlot.WithoutMany(listStates, listPlan);
        }

        var viewsCandidates = ReadViewsCandidates(views, viewsStates);
        var viewsPlan = NativeOuterTabStaleSlot.PlanPrune(viewsCandidates, views: true, out var viewsWhy, out int viewsRefusal);
        if (viewsRefusal >= 0) return NativeOuterTabShape.ReasonCompetingSlot + "views@" + viewsRefusal;
        if (viewsPlan.Length > 0)
        {
            if (_pruneAllowed)
            {
                for (int i = 0; i < viewsPlan.Length; i++)
                {
                    int index = viewsPlan[i];
                    var candidate = viewsCandidates![index];
                    var live = _menu.Views;
                    GameObject? pageGo = OwnedPageObject(live, index);
                    if (!TryFilterViews(live, index)) return NativeOuterTabShape.ReasonCompetingSlot + "views@" + index;
                    if (Alive(pageGo)) DestroyOwnedObject(pageGo);
                    RecordPrune("views", index, candidate.ObjectName ?? "", viewsWhy);
                }
            }
            if (viewsStates != null) viewsStates = NativeOuterTabStaleSlot.WithoutMany(viewsStates, viewsPlan);
        }

        // Leftovers the mapper never listed: an owned object still parented under OuterTabGroup
        // (the imitation route's own clone from an earlier generation) would be picked up on the
        // next menu init and refuse the count all over again.
        if (_pruneAllowed) PruneOwnedGroupChildren();
        return null;
    }

    /// <summary>Read <c>_OuterTabs</c> as prune candidates: state, object name, liveness, and whether the entry is ours now.</summary>
    private NativeOuterTabSlotCandidate[]? ReadListCandidates(
        Il2CppSystem.Collections.Generic.List<FullscreenMenu_OuterTabEntry>? list, int[]? listStates)
    {
        if (list == null || listStates == null) return null;
        int n = Math.Min(list.Count, listStates.Length);
        var candidates = new NativeOuterTabSlotCandidate[n];
        for (int i = 0; i < n; i++)
        {
            GameObject? go = null;
            try { go = list[i]?.gameObject; } catch { go = null; }
            bool alive = Alive(go);
            string? name = null;
            if (alive) { try { name = go!.name; } catch { name = null; } }
            bool ours = false;
            try { ours = _entry != null && list[i] != null && list[i].Pointer == _entry.Pointer; } catch { ours = false; }
            candidates[i] = new NativeOuterTabSlotCandidate(listStates[i], name, alive, ours);
        }
        return candidates;
    }

    private NativeOuterTabSlotCandidate[]? ReadViewsCandidates(
        Il2CppReferenceArray<FullscreenMenu.StateToView>? views, int[]? viewsStates)
    {
        if (views == null || viewsStates == null) return null;
        int n = Math.Min(views.Length, viewsStates.Length);
        var candidates = new NativeOuterTabSlotCandidate[n];
        for (int i = 0; i < n; i++)
        {
            var pageGo = OwnedPageObject(views, i);
            bool alive = Alive(pageGo);
            string? name = null;
            if (alive) { try { name = pageGo!.name; } catch { name = null; } }
            bool ours = false;
            try { ours = _record != null && views[i] != null && views[i].Pointer == _record.Pointer; } catch { ours = false; }
            candidates[i] = new NativeOuterTabSlotCandidate(viewsStates[i], name, alive, ours);
        }
        return candidates;
    }

    private static GameObject? OwnedPageObject(Il2CppReferenceArray<FullscreenMenu.StateToView>? views, int index)
    {
        try
        {
            var record = views != null && index >= 0 && index < views.Length ? views[index] : null;
            var instance = record?.Instance ?? record?.View;
            if (instance != null && instance.Pointer != IntPtr.Zero) return instance.gameObject;
        }
        catch { /* unreadable record: treated as a dead page */ }
        return null;
    }

    /// <summary>Destroy owned top-tab objects still parented under <c>OuterTabGroup</c> that are not this registration's clone.</summary>
    private void PruneOwnedGroupChildren()
    {
        Transform? group = null;
        try { group = _menu.OuterTabGroup?.transform; } catch { group = null; }
        if (group == null) return;
        try
        {
            for (int i = group.childCount - 1; i >= 0; i--)
            {
                var child = group.GetChild(i);
                var go = child != null ? child.gameObject : null;
                if (!Alive(go)) continue;
                string name;
                try { name = go!.name ?? ""; } catch { continue; }
                if (!string.Equals(name, NativeOuterTabStaleSlot.OwnedTabName, StringComparison.Ordinal)) continue;
                if (_clone != null && Alive(_clone) && go!.GetInstanceID() == _clone.GetInstanceID()) continue;
                DestroyOwnedObject(go);
                RecordPrune("outer-group", i, name, NativeOuterTabStaleSlot.ReasonTabName);
            }
        }
        catch { /* a dead group has nothing to prune */ }
    }

    /// <summary>Write <c>menu.Views</c> back without the record at <paramref name="index"/>. False when the write failed.</summary>
    private bool TryFilterViews(Il2CppReferenceArray<FullscreenMenu.StateToView>? views, int index)
    {
        try
        {
            if (views == null || index < 0 || index >= views.Length) return false;
            var kept = new Il2CppReferenceArray<FullscreenMenu.StateToView>(views.Length - 1);
            for (int i = 0, j = 0; i < views.Length; i++)
            {
                if (i == index) continue;
                kept[j++] = views[i];
            }
            _menu.Views = kept;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// One prune: counted on this registration and on the static NATIVE5 readback, so
    /// <c>nativeRoute.pruned</c>/<c>staleReason</c> survive a registration that is discarded by a
    /// later failed step (the live client lost the prune count exactly that way).
    /// </summary>
    private void RecordPrune(string where, int index, string name, string why)
    {
        _pruned++;
        _staleReason = why;
        LastAttachPruned++;
        LastAttachStaleReason = why;
        LogPrune(where, index, name, why);
    }

    private static void LogPrune(string where, int index, string pageName, string why)
    {
        try { Plugin.Logger?.LogInfo("Ui: " + NativeOuterTabStaleSlot.PruneNote(where, index, pageName, why)); }
        catch { /* the prune stands with or without its line */ }
    }

    private string? StepCloneDonor()
    {
        var group = _menu.OuterTabGroup?.transform;
        if (group == null || _donor == null) return "outer-group-dead";
        GameObject? clone = null;
        try
        {
            clone = UnityEngine.Object.Instantiate(_donor.gameObject, group, false);
            if (!Alive(clone)) { DestroyOwnedObject(clone); return "clone-failed"; }
            _clone = clone;
            clone!.SetActive(false);
            clone.name = NativeModsEntry.TopTabObjectName;
            try { clone.transform.SetAsLastSibling(); } catch { /* layout group places it */ }
            var entry = clone.GetComponent<FullscreenMenu_OuterTabEntry>();
            if (entry == null) { DestroyClone(); return "clone-entry-missing"; }
            _entry = entry;
            entry.State = (FullscreenMenuState)NativeOuterTabShape.OwnedState;
            entry.Requirement = OuterTabRequirement.None;
            entry._RequirementsMet_k__BackingField = true;
            entry.IsSelected = false;
            entry.IsHovered = false;
            entry.IsDisabled = false;
            ClearDonorCallbacks(entry);
            StripLocalization(clone);
            ApplyCaption(clone, _donor.gameObject);
            return null;
        }
        catch (Exception e)
        {
            DestroyClone();
            return "threw:" + e.Message;
        }
    }

    private string? StepCreatePage()
    {
        GameObject? go = null;
        try
        {
            go = new GameObject(PageObjectName);
            _pageGo = go;
            go.SetActive(false);
            go.transform.SetParent(_menu.ViewParent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            // The game's own concrete view type through Unity's component creation: no injected
            // Core subclass, no MonoBehaviour constructor. Its native ctor allocates both lists.
            var page = go.AddComponent<FullscreenMenuView>();
            if (page == null || page.Pointer == IntPtr.Zero) { DestroyPage(); return "page-component-missing"; }
            _page = page;
            var tabs = page.TabDatas;
            var contexts = page.KeyBindingContexts;
            if (tabs == null || contexts == null) { DestroyPage(); return "page-lists-null"; }
            if (tabs.Count != 0 || contexts.Count != 0) { DestroyPage(); return "page-lists-not-empty"; }
            page.BlockExit = false;
            return null;
        }
        catch (Exception e)
        {
            DestroyPage();
            return "threw:" + e.Message;
        }
    }

    private string? StepAppendViews()
    {
        var old = _menu.Views;
        if (old == null || old.Length != NativeOuterTabShape.NativeStateCount) return "views-changed";
        if (_page == null) return "page-missing";
        var arr = new Il2CppReferenceArray<FullscreenMenu.StateToView>(old.Length + 1);
        for (int i = 0; i < old.Length; i++) arr[i] = old[i];
        var record = new FullscreenMenu.StateToView
        {
            State = (FullscreenMenuState)NativeOuterTabShape.OwnedState,
            View = _page,
            Instance = _page,
        };
        arr[NativeOuterTabShape.OwnedState] = record;
        _record = record;
        _originalViews = old;
        _installedViews = arr;
        return null;
    }

    private string? StepWireClick()
    {
        if (_entry == null) return "entry-missing";
        EnsureDispatchInstalled();
        ushort id;
        lock (Gate)
        {
            id = _nextSlot;
            if (id == 0) id = 1;
            _nextSlot = (ushort)(id + 1);
            if (_nextSlot == 0) _nextSlot = 1;
        }
        if (!BootstrapLink.TryLeaseUiAction(id, out var action) || action is not Il2CppSystem.Action leased || leased.Pointer == IntPtr.Zero)
        {
            BootstrapLink.ReleaseUiAction(id);
            return "adapter-lease-failed";
        }
        _slotId = id;
        _leased = leased;
        _leasedPtr = leased.Pointer;
        lock (Gate) Live[id] = this;
        _entry.OnLeftClicked = leased;
        _callback = "ours";
        return null;
    }

    private string? StepPublish()
    {
        if (_installedViews == null || _originalViews == null || _entry == null) return "not-prepared";
        var current = _menu.Views;
        if (current == null || current.Pointer != _originalViews.Pointer) return "views-changed";
        var list = _mapper._OuterTabs;
        if (list == null || list.Count != NativeOuterTabShape.NativeStateCount) return "outer-list-changed";
        _menu.Views = _installedViews;
        _published = true;
        list.Add(_entry);
        try { _entry.Refresh(false, true, false); } catch { /* look-only */ }
        return null;
    }

    private string? StepExpose()
    {
        if (!Alive(_clone)) return "clone-dead";
        _clone!.SetActive(true);
        _exposed = true;
        return null;
    }

    // ---- reconcile ---------------------------------------------------------------------------

    /// <summary>
    /// Rebuild detection against the live lists (pointer identity, no scene walk). Applies
    /// <see cref="NativeOuterTabReconcileAction.Rewire"/> and
    /// <see cref="NativeOuterTabReconcileAction.Reappend"/> itself; the caller detaches on
    /// <see cref="NativeOuterTabReconcileAction.Detach"/>.
    /// </summary>
    internal NativeOuterTabReconcileAction Reconcile(out string reason)
    {
        var input = ReadReconcileInput();
        var (action, why) = NativeOuterTabReconcile.Decide(input);
        reason = why;
        try
        {
            switch (action)
            {
                case NativeOuterTabReconcileAction.AdoptNativeClosure:
                    _callback = "native";
                    break;
                case NativeOuterTabReconcileAction.Rewire:
                    if (_entry != null && _leased != null)
                    {
                        _entry.OnLeftClicked = _leased;
                        _callback = "ours";
                    }
                    break;
                case NativeOuterTabReconcileAction.Reappend:
                    var list = _mapper._OuterTabs;
                    if (list != null && _entry != null)
                    {
                        list.Add(_entry);
                        try { _entry.Refresh(false, true, false); } catch { /* */ }
                    }
                    break;
            }
        }
        catch (Exception e)
        {
            reason = "reconcile-threw:" + e.Message;
            return NativeOuterTabReconcileAction.Detach;
        }
        return action;
    }

    private NativeOuterTabReconcileInput ReadReconcileInput()
    {
        bool alive = Alive(_menu) && Alive(_clone) && Alive(_pageGo) && _entry != null && _record != null;
        if (!alive) return new NativeOuterTabReconcileInput(false, null, -1, -1, false, false);
        IReadOnlyList<int>? listStates = null;
        int ownedList = -1;
        int ownedViews = -1;
        bool present = false, ours = false;
        try
        {
            var list = _mapper._OuterTabs;
            listStates = ReadListStates(list);
            if (list != null)
            {
                int n = list.Count;
                for (int i = 0; i < n; i++)
                {
                    var e = list[i];
                    if (e != null && e.Pointer == _entry!.Pointer) { ownedList = i; break; }
                }
            }
            var views = _menu.Views;
            if (views != null)
            {
                for (int i = 0; i < views.Length; i++)
                {
                    var v = views[i];
                    if (v != null && v.Pointer == _record!.Pointer) { ownedViews = i; break; }
                }
            }
            var cb = _entry!.OnLeftClicked;
            present = cb != null && cb.Pointer != IntPtr.Zero;
            ours = present && cb!.Pointer == _leasedPtr;
        }
        catch
        {
            return new NativeOuterTabReconcileInput(false, null, -1, -1, false, false);
        }
        return new NativeOuterTabReconcileInput(true, listStates, ownedList, ownedViews, present, ours);
    }

    // ---- selection ---------------------------------------------------------------------------

    internal bool MenuAlive => Alive(_menu) && Alive(_pageGo);

    /// <summary><c>menu.CurrentView.State == 7</c>.</summary>
    internal bool OwnedIsCurrent
    {
        get
        {
            try
            {
                if (!Alive(_menu)) return false;
                var cur = _menu.CurrentView;
                return cur != null && (int)cur.State == NativeOuterTabShape.OwnedState;
            }
            catch { return false; }
        }
    }

    /// <summary>The current native page (0..6) or null when none / owned.</summary>
    internal int? CurrentNativeState
    {
        get
        {
            try
            {
                if (!Alive(_menu)) return null;
                var cur = _menu.CurrentView;
                if (cur == null) return null;
                int s = (int)cur.State;
                return NativeOuterTabShape.IsNativeState(s) ? s : null;
            }
            catch { return null; }
        }
    }

    internal int SelectedOuterTab
    {
        get { try { return _mapper._SelectedOuterTab; } catch { return -1; } }
    }

    /// <summary>
    /// The mapper's own <c>_CurrentView</c> is the owned record (NATIVE3 §3). Unlike
    /// <c>menu.CurrentView</c> this is what <c>OnStopRunning</c> nulls, so it says whether a
    /// typed <c>SetState</c> would deactivate the owned page like a native transition (true) or
    /// activate a page under a closed menu (false). One typed field read, pointer identity.
    /// </summary>
    internal bool MapperCurrentIsOwned
    {
        get
        {
            try
            {
                if (_record == null || _mapper.Pointer == IntPtr.Zero) return false;
                var cur = _mapper._CurrentView;
                return cur != null && cur.Pointer == _record.Pointer;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// The mapper's <c>_SelectedOuterTab</c> is the owned index while no view is current: the
    /// case the menu-closing rule exists for (<c>OnUpdate</c> would <c>SetState(7)</c> on reopen).
    /// </summary>
    internal bool RemembersOwnedIndex => SelectedOuterTab == NativeOuterTabShape.OwnedState;

    /// <summary>
    /// Typed <c>SetState(7)</c> with the mapper's own requirement check first, then the readback
    /// both <c>CurrentView</c> pointers must satisfy: state 7 and the owned page as Instance.
    /// </summary>
    internal bool TrySelectOwned(out string why)
    {
        why = "";
        try
        {
            if (!MenuAlive) { why = "menu-dead"; return false; }
            if (!_mapper.CheckOuterTabRequirement(OuterTabRequirement.None)) { why = "requirement-refused"; return false; }
            _mapper.SetState((FullscreenMenuState)NativeOuterTabShape.OwnedState);
            var cur = _menu.CurrentView;
            if (cur == null || (int)cur.State != NativeOuterTabShape.OwnedState)
            {
                why = "readback-state:" + (cur == null ? "none" : ((int)cur.State).ToString());
                return false;
            }
            var instance = cur.Instance;
            if (instance == null || _page == null || instance.Pointer != _page.Pointer)
            {
                why = "readback-instance";
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            why = "select-threw:" + e.Message;
            return false;
        }
    }

    /// <summary>Typed <c>SetState</c> to the return state, verified by readback.</summary>
    internal bool TryReturnSelection(int? saved, out int target, out string why)
    {
        target = NativeOuterTabShape.ReturnStateFor(saved);
        why = "";
        try
        {
            if (!Alive(_menu)) { why = "menu-dead"; return false; }
            _mapper.SetState((FullscreenMenuState)target);
            var cur = _menu.CurrentView;
            if (cur == null || (int)cur.State != target)
            {
                why = "readback-state:" + (cur == null ? "none" : ((int)cur.State).ToString());
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            why = "return-threw:" + e.Message;
            return false;
        }
    }

    // ---- workspace mount ---------------------------------------------------------------------

    /// <summary>
    /// Parent the workspace chrome root under the owned page (world position kept) so the
    /// mapper's activation of the page shows and hides it. Idempotent per tick: one parent read.
    /// </summary>
    internal bool Mount(GameObject? root)
    {
        if (!Alive(root) || !Alive(_pageGo)) return false;
        try
        {
            var page = _pageGo!.transform;
            var parent = root!.transform.parent;
            if (parent != null && parent.Pointer == page.Pointer)
            {
                _mounted = root;
                return true;
            }
            root.transform.SetParent(page, true);
            _mounted = root;
            return true;
        }
        catch { return false; }
    }

    internal void Unmount()
    {
        var root = _mounted;
        _mounted = null;
        if (!Alive(root)) return;
        try
        {
            root!.transform.SetParent(null, true);
            UnityEngine.Object.DontDestroyOnLoad(root);
        }
        catch { /* the chrome owns its root; a failed unparent is harmless once the page is gone */ }
    }

    // ---- detach ------------------------------------------------------------------------------

    /// <summary>
    /// Detach in the research order. <paramref name="returnState"/> is the native page to
    /// select when the owned page is current. Returns true when the registration is fully
    /// removed; false with <paramref name="reason"/> when the mapper still selects the owned
    /// page after a failed transition — the record and instance are then kept alive rather than
    /// leaving the mapper pointing at a destroyed page. Idempotent: a second call is a no-op.
    /// </summary>
    internal bool Detach(int? returnState, Action? closeWorkspace, bool workspaceOpen, out string reason)
        => Detach(returnState, closeWorkspace, workspaceOpen, menuRunning: true, out reason);

    /// <param name="menuRunning">
    /// The FullscreenMenu is still the HUD's current menu. False on the menu-closing path
    /// (NATIVE3 §3): a failed return transition then never holds the registration; the index is
    /// reset through the typed field and the owned record removed so the next open lands on a
    /// native page.
    /// </param>
    internal bool Detach(int? returnState, Action? closeWorkspace, bool workspaceOpen, bool menuRunning, out string reason)
        => Detach(returnState, closeWorkspace, workspaceOpen, menuRunning, coreUnload: false, out reason);

    /// <param name="coreUnload">
    /// NATIVE4: this detach runs from Core unload / <c>Uninstall</c>, so it always completes.
    /// A failed return transition no longer holds the registration (there is no later tick), and
    /// the <c>Views</c> record plus the owned objects go even when only the page died with a HUD
    /// rebuild — the case that left <c>views@7</c> occupied across a Core swap.
    /// </param>
    internal bool Detach(int? returnState, Action? closeWorkspace, bool workspaceOpen, bool menuRunning, bool coreUnload, out string reason)
    {
        reason = "";
        if (_detached) return true;
        bool mapperUsable = MapperUsable();
        bool menuObjectAlive = Alive(_menu);
        bool alive = MenuAlive && mapperUsable;
        bool selected = NativeOuterTabDetachPlan.OwnedSelectedForDetach(alive, MapperCurrentIsOwned);
        var phases = NativeOuterTabDetachPlan.Order(alive, selected, workspaceOpen, mapperUsable, menuObjectAlive);
        bool restored = !selected;
        for (int i = 0; i < phases.Length; i++)
        {
            switch (phases[i])
            {
                case NativeOuterTabDetachPhase.DisableActivation:
                    DisableActivation();
                    break;
                case NativeOuterTabDetachPhase.CloseWorkspace:
                    Unmount();
                    try { closeWorkspace?.Invoke(); } catch { /* the detach continues */ }
                    break;
                case NativeOuterTabDetachPhase.RestoreSelection:
                    restored = TryReturnSelection(returnState, out _, out reason);
                    break;
                case NativeOuterTabDetachPhase.VerifySelection:
                    if (!NativeOuterTabDetachPlan.MayRemoveRegistration(selected, restored, menuRunning, coreUnload))
                    {
                        // Keep the record and instance alive; the caller reports recovery status.
                        // The tick re-runs this detach on the menu-closing rule once the menu goes.
                        _callback = "held";
                        return false;
                    }
                    break;
                case NativeOuterTabDetachPhase.ClearStaleCurrentView:
                    ClearStaleCurrentView();
                    break;
                case NativeOuterTabDetachPhase.ResetSelectedIndex:
                    ResetSelectedIndex(returnState);
                    break;
                case NativeOuterTabDetachPhase.RemoveListEntry:
                    RemoveListEntry();
                    break;
                case NativeOuterTabDetachPhase.RestoreViews:
                    RestoreViews();
                    break;
                case NativeOuterTabDetachPhase.ReleaseClick:
                    ReleaseClick();
                    break;
                case NativeOuterTabDetachPhase.DestroyOwned:
                    Unmount();
                    DestroyPage();
                    DestroyClone();
                    break;
                case NativeOuterTabDetachPhase.Invalidate:
                    _record = null;
                    _installedViews = null;
                    _originalViews = null;
                    _donor = null;
                    _detached = true;
                    break;
            }
        }
        return true;
    }

    private bool MapperUsable()
    {
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return false;
            var live = world.GetExistingSystemManaged<FullscreenMenuMapper>();
            return live != null && live.Pointer == _mapper.Pointer;
        }
        catch { return false; }
    }

    private void DisableActivation()
    {
        _exposed = false;
        try { if (Alive(_clone)) _clone!.SetActive(false); } catch { /* */ }
    }

    private void ResetSelectedIndex(int? returnState)
    {
        try
        {
            int current = _mapper._SelectedOuterTab;
            int next = NativeOuterTabDetachPlan.SelectedIndexAfterDetach(current, returnState);
            if (next != current) _mapper._SelectedOuterTab = next;
        }
        catch { /* a stopped mapper that cannot be read keeps its index */ }
    }

    /// <summary>
    /// <c>menu.CurrentView</c> is left by <c>OnStopRunning</c>; when it is the record this detach
    /// destroys, null it (a fresh menu before its first <c>SetState</c> has the same null). The
    /// mapper's own <c>_CurrentView</c> is never written: it is null after <c>OnStopRunning</c>
    /// and was moved by the verified transition otherwise.
    /// </summary>
    private void ClearStaleCurrentView()
    {
        try
        {
            if (_record == null || !Alive(_menu)) return;
            var cur = _menu.CurrentView;
            if (cur != null && cur.Pointer == _record.Pointer) _menu.CurrentView = null;
        }
        catch { /* the menu is going away */ }
    }

    private void RemoveListEntry()
    {
        if (!_published && _entry == null) return;
        try
        {
            var list = _mapper._OuterTabs;
            if (list == null || _entry == null) return;
            // Remove only the owned instance, wherever a rebuild put it; foreign entries stay.
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var e = list[i];
                if (e != null && e.Pointer == _entry.Pointer) list.RemoveAt(i);
            }
        }
        catch { /* dead list */ }
        _published = false;
    }

    private void RestoreViews()
    {
        if (_installedViews == null) return;
        try
        {
            if (!Alive(_menu)) return;
            var current = _menu.Views;
            bool stillOurs = current != null && current.Pointer == _installedViews.Pointer;
            int ownedIndex = -1;
            if (!stillOurs && current != null)
            {
                for (int i = 0; i < current.Length; i++)
                {
                    var v = current[i];
                    if (v == null) continue;
                    if (_record != null && v.Pointer == _record.Pointer) { ownedIndex = i; break; }
                    // NATIVE4: a rebuilt array can hold the owned record without our wrapper
                    // identity. A state-7 record whose page is ours (name) or destroyed is ours.
                    if ((int)v.State != NativeOuterTabShape.OwnedState) continue;
                    if (IsOwnedPageRecord(v)) { ownedIndex = i; break; }
                }
            }
            bool holdsOwned = ownedIndex >= 0;
            switch (NativeOuterTabDetachPlan.DecideViewsRestore(true, stillOurs, holdsOwned))
            {
                case NativeOuterTabViewsRestore.RestoreOriginal:
                    if (_originalViews != null) _menu.Views = _originalViews;
                    break;
                case NativeOuterTabViewsRestore.FilterOwned:
                    var kept = new List<FullscreenMenu.StateToView>(current!.Length);
                    for (int i = 0; i < current.Length; i++)
                    {
                        if (i != ownedIndex) kept.Add(current[i]);
                    }
                    _menu.Views = new Il2CppReferenceArray<FullscreenMenu.StateToView>(kept.ToArray());
                    break;
            }
        }
        catch { /* the menu is going away; nothing to preserve */ }
        _installedViews = null;
    }

    /// <summary>
    /// NATIVE4: a state-7 <c>Views</c> record is this integration's when its page instance is our
    /// own page object, carries <see cref="PageObjectName"/>, or is destroyed (see
    /// <see cref="NativeOuterTabStaleSlot"/> for why a dead page counts as ours).
    /// </summary>
    private bool IsOwnedPageRecord(FullscreenMenu.StateToView record)
    {
        try
        {
            var instance = record.Instance ?? record.View;
            if (instance == null || instance.Pointer == IntPtr.Zero) return true;
            if (_page != null && instance.Pointer == _page.Pointer) return true;
            var go = instance.gameObject;
            if (!Alive(go)) return true;
            return NativeOuterTabStaleSlot.ClassifyViewsRecord(true, go!.name, true, out _) == NativeOuterTabSlotOwner.Owned;
        }
        catch { return false; }
    }

    private void ReleaseClick()
    {
        try { if (_entry != null && _leased != null && _entry.Pointer != IntPtr.Zero) _entry.OnLeftClicked = null; }
        catch { /* dead entry */ }
        if (_slotId != 0)
        {
            lock (Gate) Live.Remove(_slotId);
            BootstrapLink.ReleaseUiAction(_slotId);
            _slotId = 0;
        }
        _leased = null;
        _leasedPtr = IntPtr.Zero;
        _callback = "none";
    }

    private void DestroyPage()
    {
        var go = _pageGo;
        _pageGo = null;
        _page = null;
        DestroyOwnedObject(go);
    }

    private void DestroyClone()
    {
        var go = _clone;
        _clone = null;
        _entry = null;
        _exposed = false;
        DestroyOwnedObject(go);
    }

    // ---- status ------------------------------------------------------------------------------

    internal NativeRouteStatus Describe(bool selectedByUs, string reason, int attempts = 0, int pruned = -1, string? staleReason = null)
    {
        var input = ReadReconcileInput();
        string note = string.IsNullOrEmpty(reason) ? AuthorityNote ?? "" : reason;
        // NATIVE5: the caller's counters win when it has them, so a prune from an earlier refused
        // attempt on this host is still reported once a later attempt succeeds.
        int prunedOut = pruned >= 0 ? Math.Max(pruned, _pruned) : _pruned;
        string staleOut = string.IsNullOrEmpty(staleReason) ? _staleReason : staleReason!;
        return NativeRouteStatus.Native(
            input.OwnedViewsIndex, input.OwnedListIndex, _callback,
            selectedByUs, Mounted, BootstrapLink.UiActionCapability, note, attempts, prunedOut, staleOut);
    }

    // ---- dispatch ----------------------------------------------------------------------------

    private static void EnsureDispatchInstalled()
    {
        if (_dispatchInstalled) return;
        if (BootstrapLink.TrySetUiActionDispatch((BootstrapLink.UiActionDispatch)OnDispatch))
            _dispatchInstalled = true;
    }

    /// <summary>Core Uninstall: drop the dispatcher and every lease this generation holds.</summary>
    internal static void ClearDispatch()
    {
        lock (Gate) Live.Clear();
        _dispatchInstalled = false;
        BootstrapLink.ClearAllUiActions();
        BootstrapLink.ClearUiActionDispatch();
    }

    private static void OnDispatch(ushort slotId)
    {
        NativeOuterTabRegistration? target;
        lock (Gate) Live.TryGetValue(slotId, out target);
        if (target == null || target._detached || !target._exposed) return;
        try { target._onClick(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: native Mods tab click threw: " + e.Message);
        }
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static int[]? ReadListStates(Il2CppSystem.Collections.Generic.List<FullscreenMenu_OuterTabEntry>? list)
    {
        if (list == null) return null;
        int n = list.Count;
        var states = new int[n];
        for (int i = 0; i < n; i++)
        {
            var e = list[i];
            states[i] = e == null ? int.MinValue : (int)e.State;
        }
        return states;
    }

    private static int[]? ReadViewStates(Il2CppReferenceArray<FullscreenMenu.StateToView>? views)
    {
        if (views == null) return null;
        var states = new int[views.Length];
        for (int i = 0; i < views.Length; i++)
        {
            var v = views[i];
            states[i] = v == null ? int.MinValue : (int)v.State;
        }
        return states;
    }

    /// <summary>First live native entry with no progression requirement (System in this build).</summary>
    private static FullscreenMenu_OuterTabEntry? FindDonor(Il2CppSystem.Collections.Generic.List<FullscreenMenu_OuterTabEntry> list)
    {
        int n = list.Count;
        for (int i = 0; i < n; i++)
        {
            var e = list[i];
            if (e == null || e.Pointer == IntPtr.Zero) continue;
            try
            {
                if (e.Requirement != OuterTabRequirement.None) continue;
                if (!Alive(e.gameObject)) continue;
                return e;
            }
            catch { /* next */ }
        }
        return null;
    }

    private static void ClearDonorCallbacks(GridSelectionEntry entry)
    {
        entry.OnLeftClicked = null;
        entry.OnRightClicked = null;
        entry.OnMiddleClicked = null;
        entry.OnBeginDragAction = null;
        entry.OnDragAction = null;
        entry.OnDropAction = null;
        entry.OnEndDragAction = null;
        entry.OnSelectAction = null;
        entry.OnDeselectAction = null;
        entry.OnSubmitAction = null;
    }

    /// <summary>The wrapper goes, the TMP stays (same rule as the map site clone): a locale bump must not rename MODS.</summary>
    private static void StripLocalization(GameObject clone)
    {
        try
        {
            foreach (var localized in clone.GetComponentsInChildren<LocalizedText>(true))
            {
                try { UnityEngine.Object.DestroyImmediate(localized); } catch { /* */ }
            }
        }
        catch { /* no localization on this donor */ }
    }

    /// <summary>
    /// TABS2 (live 2026-09-29): the caption follows the native siblings' text case (their localized
    /// strings are mixed case and the donor's TMP draws them in small caps, so an all-caps "MODS"
    /// rendered as plain full capitals), and the donor's TMP style is re-applied to the clone's.
    /// </summary>
    private static void ApplyCaption(GameObject clone, GameObject? donor)
    {
        TextMeshProUGUI? sibling = null;
        string? siblingText = null;
        try
        {
            sibling = donor != null ? donor.GetComponentInChildren<TextMeshProUGUI>(true) : null;
            siblingText = sibling != null ? sibling.text : null;
        }
        catch { sibling = null; }
        string caption = NativeTabCaption.MatchSiblingCase(siblingText, Caption);
        try
        {
            foreach (var tmp in clone.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (tmp == null) continue;
                try { tmp.text = caption; } catch { /* */ }
                if (sibling != null) CopyTextStyle(sibling, tmp);
            }
        }
        catch { /* caption is cosmetic; the entry still registers */ }
    }

    private static void CopyTextStyle(TextMeshProUGUI from, TextMeshProUGUI to)
    {
        // Font asset and material are not copied: the clone already carries the donor's, and the
        // button's FontState swaps them per selection state - the donor may be the selected tab, so
        // its current font would be the selected one.
        try { to.fontStyle = from.fontStyle; } catch { /* */ }
        try { to.enableAutoSizing = from.enableAutoSizing; } catch { /* */ }
        try { to.fontSizeMin = from.fontSizeMin; to.fontSizeMax = from.fontSizeMax; } catch { /* */ }
        try { to.fontSize = from.fontSize; } catch { /* */ }
        try { to.characterSpacing = from.characterSpacing; } catch { /* */ }
        try { to.wordSpacing = from.wordSpacing; } catch { /* */ }
        try { to.alignment = from.alignment; } catch { /* */ }
    }

    private static void DestroyOwnedObject(GameObject? go)
    {
        if (go == null) return;
        try
        {
            if (!Alive(go)) return;
            if (BootstrapLink.MayDestroyUiImmediate()) UnityEngine.Object.DestroyImmediate(go);
            else UnityEngine.Object.Destroy(go);
        }
        catch (Exception e)
        {
            try { Plugin.Logger?.LogWarning("Ui: native tab destroy threw: " + e.Message); } catch { /* */ }
        }
    }

    private static bool Alive(UnityEngine.Object? value) => UnityAlive.Of(value);
}
#endif
