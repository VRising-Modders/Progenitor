using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job NATIVE3 — the three live defects after the first successful native registration plus the
/// doubled outer list, qualified game-free: (1) the workspace frame source on the native route
/// (the chrome root stays a top-level overlay; the frame is the ViewParent/footer union read the
/// way the imitation route reads it), (2) the <c>mods_ui open site=top-tab</c> / <c>close</c>
/// paths over <see cref="NativeRouteOps"/>, (3) the menu-closing rule of the detach plan, and
/// (4) a doubled native list refused without a duplicate owned entry. Unity behavior is not
/// exercised here; the live expectations are in the job report.
/// </summary>
public sealed class NativeRouteMenuLifecycleTests : IDisposable
{
    private static readonly int[] Native = { 0, 1, 2, 3, 4, 5, 6 };
    private static readonly int[] Doubled = { 0, 1, 2, 3, 4, 5, 6, 0, 1, 2, 3, 4, 5, 6 };

    public void Dispose() => ModsWorkspaceFrame.ResetForTests();

    // ---- §1 frame source ----------------------------------------------------------------------

    /// <summary>
    /// Removing this flips the chrome root back under the owned page: a nested canvas loses its
    /// screen-driven size (the live 100×100 rect), SyncFrame reports <c>no-overlay</c> and lays
    /// out the fallback frame while the panes stay at their top-level positions.
    /// </summary>
    [Fact]
    public void Native_route_keeps_the_chrome_root_as_a_top_level_overlay()
    {
        Assert.False(NativeModsEntry.NativeRouteMountsChrome);
        Assert.Equal(NativeRouteTickAction.None, NativeRouteSelection.DecideTick(true, true, true, false));
    }

    /// <summary>
    /// The live gen-42 frame (3840×1080, canvas scale 1): ViewParent union footer = 1088,72
    /// 1664×916. On the native route the same top-level overlay read produces the same frame,
    /// so the Search field lands at 1108,208 (sidebar left + PadSide, strip row + header + PadTop).
    /// </summary>
    [Fact]
    public void Native_route_frame_is_the_ViewParent_footer_union_and_places_Search_at_1108_192()
    {
        const float screenW = 3840f, screenH = 1080f;
        var viewParent = NativeContentRect.FromClient(1088f, 72f, 1664f, 870f, screenW, screenH);
        var union = NativeContentRect.FromClient(1088f, 72f, 1664f, 916f, screenW, screenH);

        // As read: gen42 is the collapsed native layout; round 8 pins it down to the vanilla window.
        WorkspaceHostLayout.PinInGameFrame = false;
        var frame = ModsWorkspaceFrame.Resolve(screenW, screenH, "top-tab", viewParent, union);
        WorkspaceHostLayout.PinInGameFrame = true;
        Assert.Equal(ModsWorkspaceFrame.PathNative, frame.Path);
        Assert.True(ModsWorkspaceFrame.MatchesNative(frame, union));
        Assert.True(ModsWorkspaceFrame.MayPresent("top-tab", nativeFound: true, frame.Path));
        Assert.True(ModsWorkspaceFrame.MayLayout("top-tab", nativeFound: true, frame.Path));
        Assert.Equal("", ModsWorkspaceFrame.FallbackReason(viewParent, "top-tab"));

        // Center-anchored canvas space → client pixels (origin top-left).
        float left = screenW / 2f + frame.X - frame.Width / 2f;
        float top = screenH / 2f - frame.Y - frame.Height / 2f;
        Assert.Equal(1088f, left, 2);
        Assert.Equal(72f, top, 2);
        Assert.Equal(1664f, frame.Width, 2);
        Assert.Equal(916f, frame.Height, 2);

        var sidebar = MenuPanelLayout.ForFrame(frame, sidebar: true);
        float sidebarLeft = screenW / 2f + sidebar.X - sidebar.Width / 2f;
        float sidebarTop = screenH / 2f - sidebar.Y - sidebar.Height / 2f;
        float searchX = sidebarLeft + PanelLayout.PadSide;
        float searchY = sidebarTop + MenuPanelLayout.PinnedTopRowY(0);
        Assert.Equal(1108f, searchX, 2);
        Assert.Equal(192f, searchY, 2);
    }

    /// <summary>
    /// The overlay-missing miss is what the nested mount produced live; a top-level overlay of
    /// real size is what the route now reads. The readiness gate is the same on both routes.
    /// </summary>
    [Fact]
    public void A_nested_100x100_canvas_would_not_pass_the_overlay_gate_but_the_screen_canvas_does()
    {
        Assert.True(ModsWorkspaceFrame.OverlayIsReady(3840f, 1080f));
        Assert.True(ModsWorkspaceFrame.OverlayIsReady(100f, 100f));
        Assert.False(ModsWorkspaceFrame.OverlayIsReady(0f, 0f));
        // A top-tab frame resolved against a missing native read is native-pending (never presented) and
        // names the miss — the path the live nested mount took; the native route no longer reaches it.
        var frame = ModsWorkspaceFrame.Resolve(1920f, 1080f, "top-tab", NativeContentRect.Missing("no-overlay"));
        Assert.Equal(ModsWorkspaceFrame.PathNativePending, frame.Path);
        Assert.False(ModsWorkspaceFrame.MayPresent("top-tab", false, frame.Path));
        Assert.Equal("no-overlay", ModsWorkspaceFrame.FallbackReason(NativeContentRect.Missing("no-overlay"), "top-tab"));
    }

    // ---- §2 open / close op paths -------------------------------------------------------------

    private sealed class FakeHost : INativeRouteHost
    {
        public bool WorkspaceOpen { get; set; }
        public bool WorkspaceSuspended { get; set; }
        public bool OwnedIsCurrent { get; set; }
        public int? CurrentNativeState { get; set; } = 3;
        public bool SelectOk { get; set; } = true;
        public bool ReturnOk { get; set; } = true;
        public List<string> Calls { get; } = new();

        public bool SelectOwned(out string why)
        {
            Calls.Add("SetState(7)");
            if (!SelectOk) { why = "requirement-refused"; return false; }
            OwnedIsCurrent = true;
            why = "";
            return true;
        }

        public void OpenWorkspace() { Calls.Add("open"); WorkspaceOpen = true; }
        public void CloseWorkspace() { Calls.Add("close"); WorkspaceOpen = false; }

        public bool ReturnSelection(out string why)
        {
            Calls.Add("SetState(" + NativeOuterTabShape.ReturnStateFor(CurrentNativeState) + ")");
            if (!ReturnOk) { why = "readback-state:7"; return false; }
            OwnedIsCurrent = false;
            why = "";
            return true;
        }
    }

    [Fact]
    public void Open_op_selects_the_owned_page_then_opens_the_workspace()
    {
        var host = new FakeHost();
        var result = NativeRouteOps.Open(host);
        Assert.True(result.Ok);
        Assert.Equal(new[] { NativeRouteOpStep.SelectOwned, NativeRouteOpStep.OpenWorkspace }, result.Steps);
        Assert.Equal(new[] { "SetState(7)", "open" }, host.Calls);
        Assert.True(host.WorkspaceOpen);
        Assert.True(host.OwnedIsCurrent);
    }

    /// <summary>A refused SetState(7) never opens the workspace on a page the mapper did not select.</summary>
    [Fact]
    public void Open_op_does_not_open_when_the_select_is_refused()
    {
        var host = new FakeHost { SelectOk = false };
        var result = NativeRouteOps.Open(host);
        Assert.False(result.Ok);
        Assert.Equal("requirement-refused", result.Why);
        Assert.Equal(new[] { NativeRouteOpStep.SelectOwned }, result.Steps);
        Assert.DoesNotContain("open", host.Calls);
        Assert.False(host.WorkspaceOpen);
    }

    [Theory]
    [InlineData(true, false, NativeRouteOpResult.WhyAlreadyOpen)]
    [InlineData(false, true, NativeRouteOpResult.WhySuspended)]
    [InlineData(true, true, NativeRouteOpResult.WhySuspended)]
    public void Open_op_is_a_no_op_while_open_or_suspended_like_the_imitation_button(bool open, bool suspended, string why)
    {
        var host = new FakeHost { WorkspaceOpen = open, WorkspaceSuspended = suspended };
        var result = NativeRouteOps.Open(host);
        Assert.False(result.Ok);
        Assert.Equal(why, result.Why);
        Assert.Empty(result.Steps);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Close_op_closes_the_workspace_then_returns_the_selection_natively()
    {
        var host = new FakeHost { WorkspaceOpen = true, OwnedIsCurrent = true, CurrentNativeState = 2 };
        var result = NativeRouteOps.Close(host);
        Assert.True(result.Ok);
        Assert.Equal("", result.Why);
        Assert.Equal(new[] { NativeRouteOpStep.CloseWorkspace, NativeRouteOpStep.ReturnSelection }, result.Steps);
        Assert.Equal(new[] { "close", "SetState(2)" }, host.Calls);
        Assert.False(host.WorkspaceOpen);
        Assert.False(host.OwnedIsCurrent);
    }

    /// <summary>A failed return still counts as a close; the reason is reported for the tick to retry.</summary>
    [Fact]
    public void Close_op_reports_a_failed_return_without_failing_the_close()
    {
        var host = new FakeHost { WorkspaceOpen = true, OwnedIsCurrent = true, ReturnOk = false };
        var result = NativeRouteOps.Close(host);
        Assert.True(result.Ok);
        Assert.Equal("readback-state:7", result.Why);
        Assert.Equal(new[] { NativeRouteOpStep.CloseWorkspace, NativeRouteOpStep.ReturnSelection }, result.Steps);
        Assert.False(host.WorkspaceOpen);
    }

    [Fact]
    public void Close_op_never_calls_SetState_when_the_mapper_is_already_on_a_native_page()
    {
        var host = new FakeHost { WorkspaceOpen = true, OwnedIsCurrent = false };
        var result = NativeRouteOps.Close(host);
        Assert.True(result.Ok);
        Assert.Equal(NativeRouteOpResult.WhyNotSelected, result.Why);
        Assert.Equal(new[] { NativeRouteOpStep.CloseWorkspace }, result.Steps);
        Assert.Equal(new[] { "close" }, host.Calls);
    }

    [Fact]
    public void Close_op_on_a_closed_workspace_returns_the_selection_only_when_the_owned_page_is_still_current()
    {
        var idle = new FakeHost { WorkspaceOpen = false, OwnedIsCurrent = false };
        var idleResult = NativeRouteOps.Close(idle);
        Assert.True(idleResult.Ok);
        Assert.Equal(NativeRouteOpResult.WhyAlreadyClosed, idleResult.Why);
        Assert.Empty(idle.Calls);

        var stranded = new FakeHost { WorkspaceOpen = false, OwnedIsCurrent = true, CurrentNativeState = null };
        var strandedResult = NativeRouteOps.Close(stranded);
        Assert.True(strandedResult.Ok);
        Assert.Equal(new[] { NativeRouteOpStep.ReturnSelection }, strandedResult.Steps);
        Assert.Equal(new[] { "SetState(" + NativeOuterTabShape.SystemState + ")" }, stranded.Calls);
    }

    [Fact]
    public void Ops_route_the_top_tab_open_through_the_native_path_only_on_the_native_route()
    {
        Assert.True(ModsUiArgs.ShouldOpenThroughNativeRoute("top-tab", routeIsNative: true));
        Assert.False(ModsUiArgs.ShouldOpenThroughNativeRoute("top-tab", routeIsNative: false));
        Assert.False(ModsUiArgs.ShouldOpenThroughNativeRoute("main-menu", routeIsNative: true));
        Assert.Equal(ModsUiArgs.TopTabNativeHandler, ModsUiArgs.TopTabHandler(routeIsNative: true));
        Assert.Equal(ModsUiArgs.TopTabOnClickHandler, ModsUiArgs.TopTabHandler(routeIsNative: false));
        Assert.Contains("TrySelectOwned", ModsUiArgs.TopTabNativeHandler, StringComparison.Ordinal);
        Assert.Contains("ReturnSelection", ModsUiArgs.CloseNativeHandler, StringComparison.Ordinal);
    }

    /// <summary>The test build has no registration: the public shims refuse with the route reason and never throw.</summary>
    [Fact]
    public void Public_native_route_shims_refuse_without_a_registration()
    {
        Assert.False(ModsMenu.RouteIsNative);
        Assert.False(ModsMenu.TryOpenNativeRoute(out string openWhy, out string openSteps));
        Assert.Equal("route-not-native", openWhy);
        Assert.Equal("", openSteps);
        Assert.False(ModsMenu.TryCloseNativeRoute(out string closeWhy, out _));
        Assert.Equal("route-not-native", closeWhy);
    }

    // ---- §3 menu-closing rule -----------------------------------------------------------------

    [Fact]
    public void A_registration_with_no_found_menu_is_detached_by_the_tick()
    {
        Assert.True(NativeRouteSelection.ShouldDetachOnMenuClosed(nativeActive: true, foundMenuAlive: false));
        Assert.False(NativeRouteSelection.ShouldDetachOnMenuClosed(nativeActive: true, foundMenuAlive: true));
        Assert.False(NativeRouteSelection.ShouldDetachOnMenuClosed(nativeActive: false, foundMenuAlive: false));
    }

    /// <summary>
    /// The transition runs only while the mapper still holds the owned record as its own
    /// <c>_CurrentView</c> (it deactivates the owned page and activates the return page); after
    /// <c>OnStopRunning</c> that field is null and a SetState would activate a page under a
    /// closed menu, so the plan skips it and resets the index instead.
    /// </summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Owned_is_selected_for_the_transition_only_while_the_mapper_holds_it(bool alive, bool mapperCurrentOwned, bool selected)
    {
        Assert.Equal(selected, NativeOuterTabDetachPlan.OwnedSelectedForDetach(alive, mapperCurrentOwned));
    }

    [Fact]
    public void While_the_menu_is_up_the_transition_runs_before_the_index_reset_and_a_failure_holds()
    {
        var order = NativeOuterTabDetachPlan.Order(menuAlive: true, ownedSelected: true, workspaceOpen: false, mapperUsable: true);
        int restore = Array.IndexOf(order, NativeOuterTabDetachPhase.RestoreSelection);
        int verify = Array.IndexOf(order, NativeOuterTabDetachPhase.VerifySelection);
        int clear = Array.IndexOf(order, NativeOuterTabDetachPhase.ClearStaleCurrentView);
        int reset = Array.IndexOf(order, NativeOuterTabDetachPhase.ResetSelectedIndex);
        Assert.True(restore >= 0 && restore < verify);
        Assert.True(verify < clear && clear < reset);
        Assert.True(reset < Array.IndexOf(order, NativeOuterTabDetachPhase.DestroyOwned));
        // Menu still running + transition failed → hold (the record stays alive under a live page).
        Assert.False(NativeOuterTabDetachPlan.MayRemoveRegistration(ownedSelected: true, selectionRestored: false, menuRunning: true));
        Assert.True(NativeOuterTabDetachPlan.MayRemoveRegistration(ownedSelected: true, selectionRestored: true, menuRunning: true));
    }

    /// <summary>
    /// Escape / HUD None with MODS selected: the menu is closing, so a failed transition never
    /// holds; removal proceeds and <c>_SelectedOuterTab</c> is reset to the saved native page.
    /// Removing the <c>menuRunning</c> clause reproduces the live <c>detach-held:readback-state:7</c>
    /// with the P key raising nothing afterwards.
    /// </summary>
    [Fact]
    public void While_the_menu_is_closing_a_failed_transition_does_not_hold()
    {
        Assert.True(NativeOuterTabDetachPlan.MayRemoveRegistration(ownedSelected: true, selectionRestored: false, menuRunning: false));
        var order = NativeOuterTabDetachPlan.Order(menuAlive: true, ownedSelected: false, workspaceOpen: false, mapperUsable: true);
        Assert.DoesNotContain(NativeOuterTabDetachPhase.RestoreSelection, order);
        Assert.Contains(NativeOuterTabDetachPhase.ClearStaleCurrentView, order);
        Assert.Contains(NativeOuterTabDetachPhase.ResetSelectedIndex, order);
        Assert.Contains(NativeOuterTabDetachPhase.RestoreViews, order);
        Assert.Contains(NativeOuterTabDetachPhase.RemoveListEntry, order);
    }

    /// <summary>After any detach the remembered index is a native one: never 7, whatever was saved.</summary>
    [Theory]
    [InlineData(7, 0, 0)]
    [InlineData(7, 6, 6)]
    [InlineData(7, 7, 6)]
    [InlineData(7, -1, 6)]
    [InlineData(7, null, 6)]
    [InlineData(4, null, 4)]
    public void The_remembered_index_after_detach_is_always_a_native_page(int selected, int? saved, int expected)
    {
        int next = NativeOuterTabDetachPlan.SelectedIndexAfterDetach(selected, saved);
        Assert.Equal(expected, next);
        Assert.True(NativeOuterTabShape.IsNativeState(next));
        Assert.NotEqual(NativeOuterTabShape.OwnedState, next);
    }

    [Fact]
    public void Native_tab_op_without_the_menu_is_a_no_op_with_a_reason()
    {
        Assert.True(ModsUiArgs.NativeTabRequiresMenu(fullscreenMenuUp: false));
        Assert.False(ModsUiArgs.NativeTabRequiresMenu(fullscreenMenuUp: true));
        var detail = ModsUiArgs.NativeTabNoMenuDetail("None");
        Assert.Equal(ModsUiArgs.NativeTabNoMenuReason, detail["reason"]);
        Assert.Equal(true, detail["noop"]);
        Assert.Equal("None", detail["menu"]);
        Assert.Equal(ModsUiArgs.NativeTabNoMenuError, detail["error"]);
    }

    // ---- §4 doubled outer list ----------------------------------------------------------------

    [Fact]
    public void A_doubled_native_list_is_refused_at_attach_with_the_count()
    {
        Assert.Equal(NativeOuterTabShape.ReasonListCount + "14", NativeOuterTabShape.Validate(Doubled, Native, 7));
        Assert.True(NativeOuterTabShape.RepeatsNativeState(Doubled));
        Assert.False(NativeOuterTabShape.RepeatsNativeState(Native));
        Assert.False(NativeOuterTabShape.RepeatsNativeState(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
        Assert.False(NativeOuterTabShape.RepeatsNativeState(new[] { 0, 1, 2, 3, 4, 5, 6, 8, 9 }));
    }

    /// <summary>
    /// The game re-appended its seven entries on reopen and the owned entry is gone: this is a
    /// foreign list, never an <c>outer-list-rebuilt</c> Reappend (which would put a MODS entry
    /// at index 14 while state 7 = index 7 no longer holds). Detach with a reason; no exception.
    /// </summary>
    [Fact]
    public void A_doubled_list_without_the_owned_entry_detaches_and_is_never_reappended_into()
    {
        var (action, reason) = NativeOuterTabReconcile.Decide(new NativeOuterTabReconcileInput(
            MenuAlive: true, ListStates: Doubled, OwnedListIndex: -1, OwnedViewsIndex: 7, CallbackPresent: false, CallbackIsOurs: false));
        Assert.Equal(NativeOuterTabReconcileAction.Detach, action);
        Assert.Equal(NativeOuterTabReconcile.ReasonListDoubled + "14", reason);
    }

    [Theory]
    [InlineData(7, "outer-list-doubled:15")]
    [InlineData(14, "outer-list-index:14")]
    public void A_doubled_list_with_the_owned_entry_detaches_wherever_the_entry_sits(int ownedIndex, string expectedReason)
    {
        var states = ownedIndex == 7
            ? new[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6 }
            : new[] { 0, 1, 2, 3, 4, 5, 6, 0, 1, 2, 3, 4, 5, 6, 7 };
        var (action, reason) = NativeOuterTabReconcile.Decide(new NativeOuterTabReconcileInput(
            MenuAlive: true, ListStates: states, OwnedListIndex: ownedIndex, OwnedViewsIndex: 7, CallbackPresent: true, CallbackIsOurs: true));
        Assert.Equal(NativeOuterTabReconcileAction.Detach, action);
        Assert.Equal(expectedReason, reason);
    }

    /// <summary>An exact native rebuild (seven, in order) is still the one shape that is re-appended into.</summary>
    [Fact]
    public void An_exact_native_rebuild_is_still_reappended()
    {
        var (action, reason) = NativeOuterTabReconcile.Decide(new NativeOuterTabReconcileInput(
            MenuAlive: true, ListStates: Native, OwnedListIndex: -1, OwnedViewsIndex: 7, CallbackPresent: false, CallbackIsOurs: false));
        Assert.Equal(NativeOuterTabReconcileAction.Reappend, action);
        Assert.Equal(NativeOuterTabReconcile.ReasonListRebuilt, reason);
    }

    /// <summary>
    /// A refusal on a doubled list rolls nothing back (validate is the first step) and leaves the
    /// menu openable: no Views write, no list write, the index untouched by us.
    /// </summary>
    [Fact]
    public void A_doubled_list_refusal_writes_nothing()
    {
        var steps = new RefusingSteps(NativeOuterTabShape.ReasonListCount + "14");
        var result = NativeOuterTabTransaction.Run(steps);
        Assert.False(result.Ok);
        Assert.Equal("validate:outer-list-count:14", result.Reason);
        Assert.Equal(NativeOuterTabAttachStep.Validate, result.FailedAt);
        Assert.Empty(result.RolledBack);
        Assert.Equal(new[] { NativeOuterTabAttachStep.Validate }, steps.Ran);
    }

    private sealed class RefusingSteps : INativeOuterTabAttachSteps
    {
        private readonly string _reason;
        internal RefusingSteps(string reason) => _reason = reason;
        internal List<NativeOuterTabAttachStep> Ran { get; } = new();
        public string? Run(NativeOuterTabAttachStep step) { Ran.Add(step); return step == NativeOuterTabAttachStep.Validate ? _reason : null; }
        public void Rollback(NativeOuterTabAttachStep step) => throw new InvalidOperationException("nothing to roll back");
    }
}
