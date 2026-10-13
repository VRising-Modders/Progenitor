using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of native-tab observer construction cleanup: an EventTrigger added to a
/// native object must be released on a failed ObserveNativeTabs path, and only when this
/// construction created it.
/// </summary>
public sealed class NativeModsEntryTests
{
    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, false)]
    public void Owned_trigger_is_destroyed_only_when_we_created_it_and_it_is_empty(
        bool createdByUs, int remainingEntries, bool destroy)
    {
        Assert.Equal(destroy, NativeModsEntry.ShouldDestroyOwnedTrigger(createdByUs, remainingEntries));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public void Added_trigger_is_safe_only_when_the_observer_lease_already_owns_it(
        bool createdByUs, bool observerLeaseTaken, bool safe)
    {
        Assert.Equal(safe, NativeModsEntry.OwnedTriggerConstructionIsSafe(createdByUs, observerLeaseTaken));
    }

    [Fact]
    public void DescribeOwned_is_the_empty_bookkeeping_snapshot_in_tests()
    {
        var owned = NativeModsEntry.DescribeOwned();
        Assert.Equal("none", owned.Site);
        Assert.False(owned.Curtain);
        Assert.Equal(0, owned.Observers);
    }

    [Fact]
    public void FullscreenMenu_request_is_a_no_op_in_the_test_build()
    {
        Assert.False(NativeModsEntry.TryRequestFullscreenMenu());
        Assert.False(NativeModsEntry.TryRestoreFullscreenMenu());
        Assert.Equal(120, NativeModsEntry.FullscreenMenuAttachFrames);
        Assert.Equal("none", NativeModsEntry.DescribeHudMenuType());
    }

    [Fact]
    public void Failed_construction_disposes_the_observer_before_owner_transfer()
    {
        var resource = new CountingDisposable();
        try
        {
            using var lease = new NativeModsEntry.ObserverLease<CountingDisposable>(resource);
            throw new InvalidOperationException("Native tab return callback unavailable");
        }
        catch (InvalidOperationException)
        {
            Assert.Equal(1, resource.Disposals);
        }
    }

    [Fact]
    public void Transferred_observer_survives_lease_dispose_and_is_released_by_the_owner_sweep()
    {
        var resource = new CountingDisposable();
        var owner = new List<CountingDisposable>();
        using (var lease = new NativeModsEntry.ObserverLease<CountingDisposable>(resource))
        {
            owner.Add(lease.Transfer());
            Assert.True(lease.Transferred);
        }
        Assert.Equal(0, resource.Disposals);
        Assert.Single(owner);
        Assert.Same(resource, owner[0]);
        foreach (var item in owner) item.Dispose();
        owner.Clear();
        Assert.Equal(1, resource.Disposals);
    }

    [Fact]
    public void Lease_dispose_is_idempotent_and_a_second_transfer_throws()
    {
        var resource = new CountingDisposable();
        var lease = new NativeModsEntry.ObserverLease<CountingDisposable>(resource);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, resource.Disposals);
        Assert.False(lease.Transferred);
        Assert.Throws<InvalidOperationException>(() => lease.Transfer());
    }

    [Fact]
    public void Server_context_cleared_drops_curtain_observers_and_resets_the_entry_site()
    {
        Handshake.ResetTables();
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 7);
        NativeModsEntry.SetHoldForTests(held: true, savedView: 2);
        NativeModsEntry.InstallLifecycleWatch();
        try
        {
            var owned = NativeModsEntry.DescribeOwned();
            Assert.Equal("top-tab", owned.Site);
            Assert.True(owned.Curtain);
            Assert.Equal(7, owned.Observers);
            Assert.True(NativeModsEntry.TestHold.Held);
            Assert.Equal(2, NativeModsEntry.TestHold.SavedView);

            Handshake.NotifyClientConnectionEnded();

            owned = NativeModsEntry.DescribeOwned();
            Assert.Equal("none", owned.Site);
            Assert.False(owned.Curtain);
            Assert.Equal(0, owned.Observers);
            Assert.False(NativeModsEntry.TestHold.Held);
            string json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
            Assert.Contains("\"site\":\"none\"", json);
            Assert.DoesNotContain("\"site\":\"shortcut\"", json);
        }
        finally
        {
            NativeModsEntry.UninstallLifecycleWatch();
            Handshake.ResetTables();
        }
    }

    [Fact]
    public void Uninstall_clears_a_held_native_tab_selection()
    {
        NativeModsEntry.SetHoldForTests(held: true, savedView: 1);
        NativeModsEntry.InstallLifecycleWatch();
        try
        {
            Assert.True(NativeModsEntry.TestHold.Held);
            NativeModsEntry.Uninstall();
            Assert.False(NativeModsEntry.TestHold.Held);
        }
        finally
        {
            NativeModsEntry.UninstallLifecycleWatch();
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Hosted_view_check_is_silent_until_settled_with_a_curtain_and_a_change(
        bool settled, bool curtain, bool changed)
    {
        Assert.Equal(NativeModsEntry.HostedViewChange.None, NativeModsEntry.DecideHostedViewChange(
            settled, curtain, changed, workspaceSuspended: false));
    }

    [Fact]
    public void Hosted_view_identity_change_closes_when_not_suspended()
    {
        Assert.Equal(NativeModsEntry.HostedViewChange.Close, NativeModsEntry.DecideHostedViewChange(
            settled: true, curtainPresent: true, hostedViewChanged: true,
            workspaceSuspended: false));
    }

    [Fact]
    public void Hosted_view_identity_change_while_suspended_suspends_instead_of_closing()
    {
        Assert.Equal(NativeModsEntry.HostedViewChange.Suspend, NativeModsEntry.DecideHostedViewChange(
            settled: true, curtainPresent: true, hostedViewChanged: true,
            workspaceSuspended: true));
    }

    [Fact]
    public void Suspended_workspace_keeps_curtain_ignores_toggle_and_keeps_native_selection_parked()
    {
        Assert.True(NativeModsEntry.ShouldShowCurtain(workspaceOpen: true, suspended: true));
        Assert.True(NativeModsEntry.ShouldShowCurtain(workspaceOpen: true, suspended: false));
        Assert.False(NativeModsEntry.ShouldShowCurtain(workspaceOpen: false, suspended: false));
        Assert.False(NativeModsEntry.ShouldShowCurtain(workspaceOpen: false, suspended: true));

        Assert.False(NativeModsEntry.ShouldRequestFullscreenMenu(workspaceOpen: true, suspended: true));
        Assert.True(NativeModsEntry.ShouldRequestFullscreenMenu(workspaceOpen: true, suspended: false));

        Assert.True(NativeModsEntry.ShouldIgnoreEntryToggle(suspended: true));
        Assert.False(NativeModsEntry.ShouldIgnoreEntryToggle(suspended: false));

        Assert.False(NativeModsEntry.ShouldReleaseParkedSelection(workspaceOpen: true, suspended: true));
        Assert.False(NativeModsEntry.ShouldReleaseParkedSelection(workspaceOpen: true, suspended: false));
        Assert.True(NativeModsEntry.ShouldReleaseParkedSelection(workspaceOpen: false, suspended: false));
        Assert.False(NativeModsEntry.ShouldReleaseParkedSelection(workspaceOpen: false, suspended: true));
    }

    [Fact]
    public void Suspended_curtain_covers_the_native_top_bar_and_the_prompt_sorts_above_it()
    {
        Assert.Equal(0f, NativeModsEntry.CurtainTopOffset(fullscreenMenu: true, suspended: true));
        Assert.Equal(NativeModsEntry.FullscreenMenuTopBarInset,
            NativeModsEntry.CurtainTopOffset(fullscreenMenu: true, suspended: false));
        Assert.Equal(0f, NativeModsEntry.CurtainTopOffset(fullscreenMenu: false, suspended: false));
        Assert.Equal(ModsCanvasBand.Blocker, NativeModsEntry.CurtainCanvasSortingOrder);
        Assert.Equal(UiOwnership.OverlaySorting, ModsCanvasBand.Overlay);
        Assert.True(ModsCanvasBand.LayerOrderIsResolved(
            NativeModsEntry.CurtainCanvasSortingOrder,
            ModsCanvasBand.Backdrop,
            ModsCanvasBand.Panels,
            ModsCanvasBand.Overlay));
        Assert.True(ModsCanvasBand.Overlay > NativeModsEntry.CurtainCanvasSortingOrder);
    }

    [Fact]
    public void Owned_button_open_is_the_only_stored_open_invoker()
    {
        Assert.Equal(NativeModsEntry.EntryClickAction.Open,
            NativeModsEntry.DecideOwnedButtonClick(suspended: false, workspaceOpen: false));
        Assert.Equal(NativeModsEntry.EntryClickAction.Ignore,
            NativeModsEntry.DecideOwnedButtonClick(suspended: false, workspaceOpen: true));
        Assert.Equal(NativeModsEntry.EntryClickAction.Ignore,
            NativeModsEntry.DecideOwnedButtonClick(suspended: true, workspaceOpen: false));
        Assert.Equal(NativeModsEntry.EntryClickAction.Ignore,
            NativeModsEntry.DecideOwnedButtonClick(suspended: true, workspaceOpen: true));

        Assert.Equal(NativeModsEntry.HostedViewChange.Close, NativeModsEntry.DecideHostedViewChange(
            settled: true, curtainPresent: true, hostedViewChanged: true,
            workspaceSuspended: false));
        Assert.NotEqual(NativeModsEntry.HostedViewChange.None,
            NativeModsEntry.DecideHostedViewChange(
                settled: true, curtainPresent: true, hostedViewChanged: true,
                workspaceSuspended: false));
    }

    [Fact]
    public void Stored_open_logs_the_invoker_reason_instead_of_unknown()
    {
        NativeModsEntry.InstallLifecycleWatch();
        try
        {
            NativeModsEntry.InvokeStoredOpen(NativeModsEntry.EntryOpenUserButton);
            Assert.Equal(ModsMenu.ReasonUserButton, NativeModsEntry.TestLastOpenReason);
            NativeModsEntry.InvokeStoredOpen(NativeModsEntry.EntryOpenReattach);
            Assert.Equal(NativeModsEntry.EntryOpenReattach, NativeModsEntry.TestLastOpenReason);
            NativeModsEntry.InvokeStoredOpen(NativeModsEntry.EntryOpenSettle);
            Assert.Equal(NativeModsEntry.EntryOpenSettle, NativeModsEntry.TestLastOpenReason);
            NativeModsEntry.InvokeStoredOpen("");
            Assert.Equal(ModsMenu.ReasonUnknown, NativeModsEntry.TestLastOpenReason);
            Assert.Equal(
                "Ui: Mods entry open reason=user-button selected=none",
                NativeModsEntry.FormatEntryOpenDiagnostic(ModsMenu.ReasonUserButton, null));
        }
        finally
        {
            NativeModsEntry.UninstallLifecycleWatch();
        }
    }

    [Fact]
    public void In_game_curtain_is_a_transparent_click_blocker_not_an_opaque_blackout()
    {
        var look = NativeModsEntry.CurtainLookForHost(fullscreenMenu: true);
        Assert.Equal(0f, look.R);
        Assert.Equal(0f, look.G);
        Assert.Equal(0f, look.B);
        Assert.Equal(0f, look.A);
        Assert.True(look.RaycastTarget);
        Assert.True(NativeModsEntry.CurtainLookBlocksPointer(look));
        Assert.False(NativeModsEntry.CurtainLookBlacksOutTheWorld(look));
        Assert.Equal(look, NativeModsEntry.InGameCurtain);
        Assert.Equal(look, NativeModsEntry.CurtainLookForSite("top-tab"));
        Assert.Equal(look, NativeModsEntry.CurtainLookForSite("shortcut"));
        // Previous production color (0.018, 0.022, 0.031, 1) would fail A == 0
        // and CurtainLookBlacksOutTheWorld.
        Assert.NotEqual(1f, look.A);
        Assert.True(look.A < 0.01f);
    }

    [Fact]
    public void Main_menu_curtain_is_a_transparent_click_blocker_not_a_haze()
    {
        var look = NativeModsEntry.CurtainLookForHost(fullscreenMenu: false);
        Assert.Equal(0f, look.R);
        Assert.Equal(0f, look.G);
        Assert.Equal(0f, look.B);
        Assert.Equal(0f, look.A);
        Assert.True(look.RaycastTarget);
        Assert.True(NativeModsEntry.CurtainLookBlocksPointer(look));
        Assert.False(NativeModsEntry.CurtainLookBlacksOutTheWorld(look));
        Assert.False(NativeModsEntry.MainMenuCurtainPaintsHaze(look));
        Assert.Equal(look, NativeModsEntry.MainMenuCurtain);
        Assert.Equal(look, NativeModsEntry.InGameCurtain);
        Assert.Equal(look, NativeModsEntry.CurtainLookForSite("main-menu"));
        // Previous haze (0.09, 0.08, 0.10, 0.58) would fail A == 0 and PaintsHaze.
        Assert.True(look.A < 0.01f);
        Assert.NotEqual(0.58f, look.A);
        var frame = ModsWorkspaceFrame.Fallback(3840f, 1080f);
        Assert.False(ModsWorkspaceFrame.CoversOutsideFrame(
            0f, 0f, 3840f, 1080f, look.A, frame.X, frame.Y, frame.Width, frame.Height));
        Assert.True(ModsWorkspaceFrame.CoversOutsideFrame(
            0f, 0f, 3840f, 1080f, 0.58f, 1282f, 210f, 1276f, 800f));
    }

    [Fact]
    public void Consent_suspend_stretches_the_curtain_without_changing_its_look()
    {
        var open = NativeModsEntry.CurtainLookForHost(fullscreenMenu: true, suspended: false);
        var suspended = NativeModsEntry.CurtainLookForHost(fullscreenMenu: true, suspended: true);
        Assert.Equal(open, suspended);
        Assert.True(NativeModsEntry.CurtainLookBlocksPointer(suspended));
        Assert.False(NativeModsEntry.CurtainLookBlacksOutTheWorld(suspended));
        Assert.Equal(0f, NativeModsEntry.CurtainTopOffset(fullscreenMenu: true, suspended: true));
        Assert.Equal(NativeModsEntry.FullscreenMenuTopBarInset,
            NativeModsEntry.CurtainTopOffset(fullscreenMenu: true, suspended: false));

        var titleOpen = NativeModsEntry.CurtainLookForHost(fullscreenMenu: false, suspended: false);
        var titleSuspended = NativeModsEntry.CurtainLookForHost(fullscreenMenu: false, suspended: true);
        Assert.Equal(titleOpen, titleSuspended);
        Assert.True(NativeModsEntry.CurtainLookBlocksPointer(titleSuspended));
        Assert.False(NativeModsEntry.CurtainLookBlacksOutTheWorld(titleSuspended));
        Assert.Equal(0f, NativeModsEntry.CurtainTopOffset(fullscreenMenu: false, suspended: true));
    }

    [Fact]
    public void Main_menu_entry_is_parented_to_the_native_list_above_continue_when_the_container_exists()
    {
        var plan = NativeModsEntry.DecideMainMenuEntry(
            buttonsParentPresent: true,
            NativeModsEntry.TypicalTitleSiblings,
            donorAvailable: true,
            NativeModsEntry.NativeListLayout.VerticalGroup,
            listHasRoom: true);

        Assert.True(plan.ParentsToNativeList);
        Assert.False(plan.UsesCornerAnchors);
        Assert.Equal(NativeModsEntry.MainMenuEntryParent.NativeList, plan.Parent);
        Assert.Equal(NativeModsEntry.MainMenuEntryChrome.HandBuiltInline, plan.Chrome);
        Assert.Equal(NativeModsEntry.NativeListLayout.VerticalGroup, plan.Layout);
        // CONTINUE, PLAY, LOAD GAME, CINEMATICS, OPTIONS, EXIT — insert above CONTINUE.
        Assert.Equal(0, plan.SiblingIndex);
        Assert.Equal(0, NativeModsEntry.ModsSiblingIndex(NativeModsEntry.TypicalTitleSiblings));
        Assert.Equal(4, plan.CloneDonorIndex);
        Assert.Equal(
            "Ui: Mods main menu entry attached inline sibling=0 chrome=hand-built layout=vertical.",
            NativeModsEntry.FormatMainMenuAttachLog(plan));
        Assert.Equal("MODS", NativeModsEntry.MainMenuEntryLabel);
    }

    [Fact]
    public void Main_menu_entry_stays_above_the_first_button_when_continue_is_hidden_or_a_divider_is_present()
    {
        var withoutContinue = new[]
        {
            NativeModsEntry.NativeMenuSiblingKind.Button, // PLAY
            NativeModsEntry.NativeMenuSiblingKind.Button, // LOAD GAME
            NativeModsEntry.NativeMenuSiblingKind.Button, // CINEMATICS
            NativeModsEntry.NativeMenuSiblingKind.Button, // OPTIONS
            NativeModsEntry.NativeMenuSiblingKind.Button, // EXIT
        };
        Assert.Equal(0, NativeModsEntry.ModsSiblingIndex(withoutContinue));

        var dividerBeforeExit = new[]
        {
            NativeModsEntry.NativeMenuSiblingKind.Button,
            NativeModsEntry.NativeMenuSiblingKind.Button,
            NativeModsEntry.NativeMenuSiblingKind.Button,
            NativeModsEntry.NativeMenuSiblingKind.Button,
            NativeModsEntry.NativeMenuSiblingKind.Divider,
            NativeModsEntry.NativeMenuSiblingKind.Button, // EXIT
        };
        Assert.Equal(0, NativeModsEntry.ModsSiblingIndex(dividerBeforeExit));

        var dividerThenContinue = new[]
        {
            NativeModsEntry.NativeMenuSiblingKind.Divider,
            NativeModsEntry.NativeMenuSiblingKind.Button, // CONTINUE
            NativeModsEntry.NativeMenuSiblingKind.Button,
        };
        Assert.Equal(1, NativeModsEntry.ModsSiblingIndex(dividerThenContinue));

        var empty = Array.Empty<NativeModsEntry.NativeMenuSiblingKind>();
        Assert.Equal(0, NativeModsEntry.ModsSiblingIndex(empty));
        Assert.Equal(-1, NativeModsEntry.CloneDonorIndex(empty));
    }

    [Fact]
    public void Insert_above_continue_does_not_treat_the_subtitle_as_a_list_sibling()
    {
        // AdditionalText lives on the CONTINUE MenuButton.
        // TypicalTitleSiblings is six Button kinds; a subtitle is not a seventh.
        Assert.Equal(6, NativeModsEntry.TypicalTitleSiblings.Length);
        Assert.Equal(0, NativeModsEntry.ModsSiblingIndex(NativeModsEntry.TypicalTitleSiblings));
        Assert.Equal(NativeModsEntry.NativeMenuSiblingKind.Button, NativeModsEntry.TypicalTitleSiblings[0]);
    }

    [Fact]
    public void Main_menu_corner_fallback_is_used_only_when_the_native_list_is_missing()
    {
        var plan = NativeModsEntry.DecideMainMenuEntry(
            buttonsParentPresent: false,
            NativeModsEntry.TypicalTitleSiblings,
            donorAvailable: true,
            NativeModsEntry.NativeListLayout.VerticalGroup);

        Assert.False(plan.ParentsToNativeList);
        Assert.True(plan.UsesCornerAnchors);
        Assert.Equal(NativeModsEntry.MainMenuEntryParent.ViewCorner, plan.Parent);
        Assert.Equal(NativeModsEntry.MainMenuEntryChrome.HandBuiltCorner, plan.Chrome);
        Assert.Equal(-1, plan.SiblingIndex);
        Assert.True(NativeModsEntry.CornerFallbackIsUsable(NativeModsEntry.CornerFallbackGeometry));
        var g = NativeModsEntry.CornerFallbackGeometry;
        Assert.Equal(240f, g.Width);
        Assert.Equal(48f, g.Height);
        Assert.True(g.RaycastTarget);
        Assert.Equal(1f, g.AnchorMinX);
        Assert.Equal(1f, g.AnchorMaxY);
        Assert.Equal(-40f, g.AnchoredX);
        Assert.Equal(-40f, g.AnchoredY);
        Assert.Equal(
            "Ui: Mods main menu entry attached (corner fallback; native list not found).",
            NativeModsEntry.FormatMainMenuAttachLog(plan));
        Assert.False(NativeModsEntry.CornerFallbackIsUsable(g with { Width = 0f }));
        Assert.False(NativeModsEntry.CornerFallbackIsUsable(g with { RaycastTarget = false }));
        Assert.False(NativeModsEntry.CornerFallbackIsUsable(g with { AnchorMinX = 0f }));
    }

    [Fact]
    public void Main_menu_builds_an_inline_row_instead_of_cloning_a_native_button()
    {
        var intended = NativeModsEntry.DecideMainMenuEntry(
            buttonsParentPresent: true,
            NativeModsEntry.TypicalTitleSiblings,
            donorAvailable: true,
            NativeModsEntry.NativeListLayout.VerticalGroup,
            listHasRoom: true);
        Assert.Equal(NativeModsEntry.MainMenuEntryChrome.HandBuiltInline, intended.Chrome);

        // A clone plan that then fails Instantiate still stays inline as a hand-built
        // row; the self-check, not the clone attempt, is what may send it to the corner.
        var clonePlan = intended with { Chrome = NativeModsEntry.MainMenuEntryChrome.ClonedNative };
        var after = NativeModsEntry.AfterCloneAttempt(clonePlan, cloneSucceeded: false);
        Assert.True(after.ParentsToNativeList);
        Assert.Equal(NativeModsEntry.MainMenuEntryChrome.HandBuiltInline, after.Chrome);
        Assert.Equal(intended.SiblingIndex, after.SiblingIndex);
        Assert.Equal(
            "Ui: Mods main menu entry attached inline sibling=0 chrome=hand-built layout=vertical.",
            NativeModsEntry.FormatMainMenuAttachLog(after));

        var noDonor = NativeModsEntry.DecideMainMenuEntry(
            buttonsParentPresent: true,
            NativeModsEntry.TypicalTitleSiblings,
            donorAvailable: false,
            NativeModsEntry.NativeListLayout.None,
            listHasRoom: true);
        Assert.Equal(NativeModsEntry.MainMenuEntryChrome.HandBuiltInline, noDonor.Chrome);
        Assert.True(noDonor.ParentsToNativeList);
        Assert.Equal(
            "Ui: Mods main menu entry attached inline sibling=0 chrome=hand-built layout=none.",
            NativeModsEntry.FormatMainMenuAttachLog(noDonor));
    }

    [Fact]
    public void Main_menu_entry_re_attaches_inline_after_the_view_is_destroyed_and_recreated()
    {
        var first = NativeModsEntry.DecideMainMenuEntry(
            buttonsParentPresent: true,
            NativeModsEntry.TypicalTitleSiblings,
            donorAvailable: true,
            NativeModsEntry.NativeListLayout.VerticalGroup);
        Assert.True(NativeModsEntry.ShouldDropMainMenuHost(
            hostReported: true, hostAlive: false, hostActiveInHierarchy: false, clientWorldPresent: false));
        Assert.False(NativeModsEntry.ShouldDiscoverMainMenu(hostAlive: true, scanCount: 30));
        Assert.False(NativeModsEntry.ShouldDiscoverMainMenu(hostAlive: false, scanCount: 29));
        Assert.True(NativeModsEntry.ShouldDiscoverMainMenu(hostAlive: false, scanCount: 30));

        var second = NativeModsEntry.DecideMainMenuEntry(
            buttonsParentPresent: true,
            NativeModsEntry.TypicalTitleSiblings,
            donorAvailable: true,
            NativeModsEntry.NativeListLayout.VerticalGroup);
        Assert.True(second.ParentsToNativeList);
        Assert.Equal(0, first.SiblingIndex);
        Assert.Equal(first.SiblingIndex, second.SiblingIndex);
        Assert.Equal(first.Chrome, second.Chrome);
        Assert.Equal(NativeModsEntry.MainMenuButtonName, "progenitor-ui-core-main-mods");

        var recreatedWithoutList = NativeModsEntry.DecideMainMenuEntry(
            buttonsParentPresent: false,
            Array.Empty<NativeModsEntry.NativeMenuSiblingKind>(),
            donorAvailable: false,
            NativeModsEntry.NativeListLayout.None);
        Assert.True(recreatedWithoutList.UsesCornerAnchors);
        Assert.True(NativeModsEntry.CornerFallbackIsUsable(NativeModsEntry.CornerFallbackGeometry));
        Assert.True(NativeModsEntry.ShouldDropMainMenuHost(
            hostReported: true, hostAlive: true, hostActiveInHierarchy: true, clientWorldPresent: true));
        Assert.False(NativeModsEntry.ShouldDropMainMenuHost(
            hostReported: false, hostAlive: false, hostActiveInHierarchy: false, clientWorldPresent: false));
        // We hid the view: inactive is expected, not a drop. Current-build
        // ShouldDropMainMenuHost(inactive) returned true and destroyed the MODS entry.
        Assert.False(NativeModsEntry.ShouldDropMainMenuHost(
            hostReported: true, hostAlive: true, hostActiveInHierarchy: false,
            clientWorldPresent: false, titleViewHeldByUs: true));
        Assert.True(NativeModsEntry.ShouldDropMainMenuHost(
            hostReported: true, hostAlive: false, hostActiveInHierarchy: false,
            clientWorldPresent: false, titleViewHeldByUs: true));
    }

    [Fact]
    public void Title_view_hold_hides_on_open_and_restores_on_every_exit_path()
    {
        var present = NativeModsEntry.TitleViewHold.Hide(default, viewPresent: true, viewActive: true);
        Assert.True(present.Held);
        Assert.True(present.RestoreActive);
        Assert.True(present.ContentHidden);
        Assert.True(present.BackgroundKept);
        Assert.True(present.VideoKeptPlaying);
        Assert.True(present.RootKeptActive);
        Assert.True(NativeModsEntry.TitleViewHold.ShouldDeactivate(default, present));
        Assert.False(NativeModsEntry.TitleViewHold.ShouldDeactivateRoot(default, present));

        var closed = NativeModsEntry.TitleViewHold.AfterClose(present);
        Assert.False(closed.Held);
        Assert.True(NativeModsEntry.TitleViewHold.ShouldReactivate(present, closed));

        var escaped = NativeModsEntry.TitleViewHold.AfterEscape(present);
        Assert.False(escaped.Held);
        Assert.True(NativeModsEntry.TitleViewHold.ShouldReactivate(present, escaped));

        var threw = NativeModsEntry.TitleViewHold.AfterExceptionDuringOpen(present);
        Assert.False(threw.Held);
        Assert.True(NativeModsEntry.TitleViewHold.ShouldReactivate(present, threw));

        foreach (Progenitor.Plugins.PluginUnloadReason reason in Enum.GetValues(typeof(Progenitor.Plugins.PluginUnloadReason)))
        {
            var after = NativeModsEntry.TitleViewHold.AfterUnload(present, reason);
            Assert.False(after.Held);
            Assert.True(NativeModsEntry.TitleViewHold.ShouldReactivate(present, after));
        }

        // Missing view: hide is a no-op so we cannot leave a blank title by accident.
        var missing = NativeModsEntry.TitleViewHold.Hide(default, viewPresent: false, viewActive: false);
        Assert.False(missing.Held);
    }

    [Fact]
    public void Title_view_hold_hides_content_children_and_never_stops_the_video()
    {
        // Removing RootKeptActive / KeepTitleBackgroundChild (or SetActive(false)
        // on MainMenu_V2) stops VideoPlayer; reactivation does not Play, and the
        // title stays black until a native Play→Back.
        Assert.Equal("BackgroundImage", NativeModsEntry.TitleViewHold.BackgroundChildName);
        Assert.Equal("Content", NativeModsEntry.TitleViewHold.ContentChildName);
        Assert.Equal("SLS logo", NativeModsEntry.TitleViewHold.LogoChildName);
        Assert.Equal("HomeViewInteractables", NativeModsEntry.TitleViewHold.ModsEntryParentName);
        Assert.True(NativeModsEntry.TitleViewHold.KeepTitleBackgroundChild(
            NativeModsEntry.TitleViewHold.BackgroundChildName, false, false));
        Assert.False(NativeModsEntry.TitleViewHold.HideTitleContentChild(
            NativeModsEntry.TitleViewHold.BackgroundChildName, false, false));
        Assert.True(NativeModsEntry.TitleViewHold.HideTitleContentChild(
            NativeModsEntry.TitleViewHold.ContentChildName, false, false));
        Assert.True(NativeModsEntry.TitleViewHold.HideTitleContentChild(
            NativeModsEntry.TitleViewHold.LogoChildName, false, false));
        Assert.True(NativeModsEntry.TitleViewHold.HideTitleContentChild("PopupNode", false, false));
        Assert.True(NativeModsEntry.TitleViewHold.KeepTitleBackgroundChild(
            "RawImage", hostsVideoImage: true, hostsVideoPlayer: true));
        Assert.False(NativeModsEntry.TitleViewHold.HideTitleContentChild(
            "RawImage", hostsVideoImage: true, hostsVideoPlayer: true));

        NativeModsEntry.SetOwnedForTests("main-menu", curtain: false, observers: 0);
        NativeModsEntry.HoldTitleViewIfMainMenu();
        Assert.True(NativeModsEntry.TitleViewIsHidden);
        Assert.Equal(true, NativeModsEntry.TitleVideoPlaying);
        Assert.Equal(true, ModsMenu.TitleVideoPlaying);

        NativeModsEntry.RestoreTitleView();
        Assert.False(NativeModsEntry.TitleViewIsHidden);
        Assert.Equal(true, NativeModsEntry.TitleVideoPlaying);
        Assert.Equal(true, ModsMenu.TitleVideoPlaying);

        NativeModsEntry.HoldTitleViewIfMainMenu();
        var escaped = NativeModsEntry.TitleViewHold.AfterEscape(NativeModsEntry.TestTitleHold);
        NativeModsEntry.RestoreTitleView();
        Assert.False(escaped.Held);
        Assert.Equal(true, NativeModsEntry.TitleVideoPlaying);

        foreach (Progenitor.Plugins.PluginUnloadReason reason in Enum.GetValues(typeof(Progenitor.Plugins.PluginUnloadReason)))
        {
            NativeModsEntry.HoldTitleViewIfMainMenu();
            Assert.Equal(true, NativeModsEntry.TitleVideoPlaying);
            var after = NativeModsEntry.TitleViewHold.AfterUnload(NativeModsEntry.TestTitleHold, reason);
            NativeModsEntry.RestoreTitleView();
            Assert.False(after.Held);
            Assert.Equal(true, NativeModsEntry.TitleVideoPlaying);
        }

        NativeModsEntry.HoldTitleViewIfMainMenu();
        var held = NativeModsEntry.TestTitleHold;
        var threw = NativeModsEntry.TitleViewHold.AfterExceptionDuringOpen(held);
        NativeModsEntry.RestoreTitleView();
        Assert.False(threw.Held);
        Assert.Equal(true, NativeModsEntry.TitleVideoPlaying);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        Assert.Null(NativeModsEntry.TitleVideoPlaying);
    }

    [Fact]
    public void Title_view_hold_returns_the_mods_entry_above_continue_after_restore()
    {
        NativeModsEntry.SetOwnedForTests("main-menu", curtain: true, observers: 0);
        NativeModsEntry.HoldTitleViewIfMainMenu();
        Assert.True(NativeModsEntry.TitleViewIsHidden);
        Assert.False(NativeModsEntry.ShouldDropMainMenuHost(
            hostReported: true, hostAlive: true, hostActiveInHierarchy: false,
            clientWorldPresent: false, titleViewHeldByUs: NativeModsEntry.TitleViewIsHidden));

        NativeModsEntry.RestoreTitleView();
        Assert.False(NativeModsEntry.TitleViewIsHidden);
        var plan = NativeModsEntry.DecideMainMenuEntry(
            buttonsParentPresent: true,
            NativeModsEntry.TypicalTitleSiblings,
            donorAvailable: true,
            NativeModsEntry.NativeListLayout.VerticalGroup);
        Assert.Equal(0, plan.SiblingIndex);
        Assert.True(plan.ParentsToNativeList);
        Assert.Equal(NativeModsEntry.MainMenuEntryChrome.HandBuiltInline, plan.Chrome);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
    }

    [Fact]
    public void Top_tab_site_does_not_hide_the_title_view()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.HoldTitleViewIfMainMenu();
        Assert.False(NativeModsEntry.TitleViewIsHidden);
        Assert.Equal(NativeModsEntry.InGameCurtain, NativeModsEntry.CurtainLookForSite("top-tab"));
        Assert.Equal(0f, NativeModsEntry.InGameCurtain.A);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
    }

    [Fact]
    public void Hosted_view_hold_hides_on_top_tab_open_and_restores_on_every_exit_path()
    {
        var present = NativeModsEntry.HostedViewHold.Hide(default, viewPresent: true, viewActive: true);
        Assert.True(present.Held);
        Assert.True(present.RestoreActive);
        Assert.True(NativeModsEntry.HostedViewHold.ShouldDeactivate(default, present));
        Assert.True(NativeModsEntry.HostedViewHold.ShouldClearNativeHighlight(present));

        var closed = NativeModsEntry.HostedViewHold.AfterClose(present);
        Assert.False(closed.Held);
        Assert.True(NativeModsEntry.HostedViewHold.ShouldReactivate(present, closed));

        var escaped = NativeModsEntry.HostedViewHold.AfterEscape(present);
        Assert.False(escaped.Held);
        Assert.True(NativeModsEntry.HostedViewHold.ShouldReactivate(present, escaped));

        var threw = NativeModsEntry.HostedViewHold.AfterExceptionDuringOpen(present);
        Assert.False(threw.Held);
        Assert.True(NativeModsEntry.HostedViewHold.ShouldReactivate(present, threw));

        foreach (Progenitor.Plugins.PluginUnloadReason reason in Enum.GetValues(typeof(Progenitor.Plugins.PluginUnloadReason)))
        {
            var after = NativeModsEntry.HostedViewHold.AfterUnload(present, reason);
            Assert.False(after.Held);
            Assert.True(NativeModsEntry.HostedViewHold.ShouldReactivate(present, after));
        }

        var missing = NativeModsEntry.HostedViewHold.Hide(default, viewPresent: false, viewActive: false);
        Assert.False(missing.Held);
        Assert.False(NativeModsEntry.HostedViewHold.ShouldClearNativeHighlight(missing));

        Assert.True(NativeModsEntry.HostedViewHold.IsDeactivationTarget(
            isHostedInstance: true, isHeaderStrip: false));
        Assert.True(NativeModsEntry.HostedViewHold.IsDeactivationTarget(
            isHostedInstance: false, isHeaderStrip: true));
        Assert.False(NativeModsEntry.HostedViewHold.IsDeactivationTarget(
            isHostedInstance: false, isHeaderStrip: false));
        Assert.False(NativeModsEntry.HostedViewHold.ShouldDeactivate(default, present)
            && NativeModsEntry.HostedViewHold.IsDeactivationTarget(false, false));
        // Revert: deactivating menu-root footer chrome (ConnectionInfo / bug / FAQ,
        // unnamed FullscreenMenu children) with the hosted hold blacks the strip.
    }

    [Fact]
    public void Top_tab_open_hides_the_hosted_view_and_main_menu_does_not()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.HoldHostedViewIfTopTab();
        Assert.True(NativeModsEntry.HostedViewIsHidden);
        Assert.False(NativeModsEntry.TitleViewIsHidden);
        Assert.True(NativeModsEntry.OuterTabVisualIsCleared);
        Assert.Equal("SpellBook", NativeModsEntry.DescribeOuterTabVisual().SavedTab);
        Assert.True(NativeModsEntry.DescribeOuterTabVisual().Cleared);
        Assert.Equal("SpellBook:cleared", NativeModsEntry.DescribeOuterTabVisual().Detail);
        Assert.Equal("SpellBook:cleared", ModsMenu.OuterTabVisual);
        NativeModsEntry.RestoreHostedView();
        Assert.False(NativeModsEntry.HostedViewIsHidden);
        Assert.False(NativeModsEntry.OuterTabVisualIsCleared);
        Assert.Equal("", NativeModsEntry.DescribeOuterTabVisual().Detail);
        Assert.Equal("", ModsMenu.OuterTabVisual);

        NativeModsEntry.SetOwnedForTests("main-menu", curtain: true, observers: 0);
        NativeModsEntry.HoldHostedViewIfTopTab();
        Assert.False(NativeModsEntry.HostedViewIsHidden);
        Assert.False(NativeModsEntry.OuterTabVisualIsCleared);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
    }

    [Fact]
    public void TickBody_hold_from_spellbook_leaves_only_mods_selected_and_restores_on_close()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("SpellBook", headerActive: true);
        NativeModsEntry.HoldHostedViewIfTopTab();
        Assert.True(NativeModsEntry.OuterTabVisualIsCleared);
        Assert.Equal("SpellBook", NativeModsEntry.DescribeOuterTabVisual().SavedTab);
        Assert.Equal("SpellBook:cleared", NativeModsEntry.DescribeOuterTabVisual().Detail);

        var native = new[]
        {
            new NativeTabSelection.TabMark { Name = "Emotes", Selected = false },
            new NativeTabSelection.TabMark { Name = "VampirePowers", Selected = false },
            new NativeTabSelection.TabMark { Name = "SpellBook", Selected = true },
            new NativeTabSelection.TabMark { Name = "Map", Selected = false },
            new NativeTabSelection.TabMark { Name = "VBlood", Selected = false },
            new NativeTabSelection.TabMark { Name = "Social", Selected = false },
            new NativeTabSelection.TabMark { Name = "System", Selected = false },
            new NativeTabSelection.TabMark { Name = NativeTabSelection.ModsTabName, Selected = true },
        };
        var afterTick = NativeModsEntry.TickHeldVisualForTests(native);
        Assert.Equal(1, NativeTabSelection.SelectedCount(afterTick));
        Assert.Equal(NativeTabSelection.ModsTabName, NativeTabSelection.SelectedName(afterTick));
        for (int i = 0; i < 7; i++)
            Assert.False(afterTick[i].Selected);

        NativeModsEntry.RestoreHostedView();
        Assert.False(NativeModsEntry.OuterTabVisualIsCleared);
        Assert.Equal("", NativeModsEntry.DescribeOuterTabVisual().Detail);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
    }

    [Fact]
    public void TickBody_hold_with_no_native_page_reports_none_cleared()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests(selectedTab: null, headerActive: false);
        NativeModsEntry.HoldHostedViewIfTopTab();
        Assert.True(NativeModsEntry.OuterTabVisualIsCleared);
        Assert.Null(NativeModsEntry.DescribeOuterTabVisual().SavedTab);
        Assert.Equal("none:cleared", NativeModsEntry.DescribeOuterTabVisual().Detail);
        Assert.NotEqual("Emotes:cleared", NativeModsEntry.DescribeOuterTabVisual().Detail);

        var native = new[]
        {
            new NativeTabSelection.TabMark { Name = "Emotes", Selected = false },
            new NativeTabSelection.TabMark { Name = "VampirePowers", Selected = false },
            new NativeTabSelection.TabMark { Name = "SpellBook", Selected = false },
            new NativeTabSelection.TabMark { Name = "Map", Selected = false },
            new NativeTabSelection.TabMark { Name = "VBlood", Selected = false },
            new NativeTabSelection.TabMark { Name = "Social", Selected = false },
            new NativeTabSelection.TabMark { Name = "System", Selected = false },
        };
        var afterTick = NativeModsEntry.TickHeldVisualForTests(native);
        Assert.Equal(NativeTabSelection.ModsTabName, NativeTabSelection.SelectedName(afterTick));
        Assert.Equal(1, NativeTabSelection.SelectedCount(afterTick));
        Assert.False(afterTick[0].Selected);

        NativeModsEntry.RestoreHostedView();
        Assert.Equal("", NativeModsEntry.DescribeOuterTabVisual().Detail);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
    }

    [Fact]
    public void Hold_from_spellbook_drives_drawing_state_off_and_nativeTabsLit_reads_back()
    {
        // Live gen-14: outerTabVisual=none:cleared while every native still drew
        // red+underline. DescribeOuterTabVisual must read HighlightImage /
        // ForceSelect / ForceHighlight / IsSelected, not Visual.Cleared.
        // Removal of TickHeldDrawForTests' ReapplyWhileHeldDraw, or of
        // NativeTabsLit's object readback, is that miss.
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("SpellBook", headerActive: true);
        NativeModsEntry.HoldHostedViewIfTopTab();

        var native = new[]
        {
            NativeTabSelection.ClearedDraw("Emotes"),
            NativeTabSelection.ClearedDraw("VampirePowers"),
            NativeTabSelection.LitDraw("SpellBook"),
            NativeTabSelection.ClearedDraw("Map"),
            NativeTabSelection.ClearedDraw("VBlood"),
            NativeTabSelection.ClearedDraw("Social"),
            NativeTabSelection.ClearedDraw("System"),
            NativeTabSelection.LitDraw(NativeTabSelection.ModsTabName),
        };
        Assert.Equal(1, NativeTabSelection.NativeTabsLit(native));

        var afterTick = NativeModsEntry.TickHeldDrawForTests(native);
        Assert.Equal(0, NativeModsEntry.NativeTabsLit);
        Assert.Equal(0, NativeModsEntry.DescribeOuterTabVisual().NativeTabsLit);
        Assert.True(NativeModsEntry.DescribeOuterTabVisual().Cleared);
        Assert.Equal("SpellBook:cleared", NativeModsEntry.DescribeOuterTabVisual().Detail);
        Assert.Equal(NativeTabSelection.ModsTabName, afterTick[afterTick.Length - 1].Name);
        Assert.True(afterTick[afterTick.Length - 1].DrawsSelected);
        for (int i = 0; i < 7; i++)
            Assert.False(afterTick[i].DrawsSelected);

        var relit = NativeTabSelection.MapperOnUpdateDraw(
            afterTick, NativeTabSelection.MapperIndexDraw(afterTick, NativeModsEntry.TestVisualHold));
        Assert.Equal(7, NativeTabSelection.NativeTabsLit(relit));
        var undone = NativeModsEntry.TickHeldDrawForTests(relit);
        Assert.Equal(0, NativeModsEntry.NativeTabsLit);
        for (int i = 0; i < 7; i++)
            Assert.False(undone[i].DrawsSelected);

        NativeModsEntry.RestoreHostedView();
        Assert.Equal(1, NativeModsEntry.NativeTabsLit);
        Assert.Equal("", NativeModsEntry.DescribeOuterTabVisual().Detail);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
    }

    [Fact]
    public void DescribeOuterTabs_reads_draw_snapshot_without_changing_hold()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("SpellBook", headerActive: true);
        NativeModsEntry.HoldHostedViewIfTopTab();
        var native = new[]
        {
            NativeTabSelection.LitDraw("SpellBook"),
            NativeTabSelection.ClearedDraw("Emotes"),
            NativeTabSelection.LitDraw(NativeTabSelection.ModsTabName),
        };
        NativeModsEntry.TickHeldDrawForTests(native);

        var described = NativeModsEntry.DescribeOuterTabs();
        Assert.Equal(0, described["nativeTabsLit"]);
        Assert.Equal("SpellBook:cleared", described["outerTabVisual"]);
        Assert.Equal(true, described["holdHeld"]);
        Assert.Equal(true, described["holdCleared"]);
        var rows = Assert.IsType<System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>(described["native"]);
        Assert.Equal(2, rows.Count);
        Assert.Equal("SpellBook", rows[0]["name"]);
        Assert.Equal(false, rows[0]["isSelected"]);
        Assert.Equal("SpellBook", described["selectedOuterTab"]);
        Assert.Equal(true, rows[0]["holdCleared"]);
        var owned = Assert.IsType<System.Collections.Generic.Dictionary<string, object?>>(described["owned"]);
        Assert.Equal(NativeTabSelection.ModsTabName, owned["name"]);
        Assert.Equal(true, owned["owned"]);
        Assert.Equal(true, owned["isSelected"]);
        Assert.Equal(false, owned["holdCleared"]);
        var ownedHighlight = Assert.IsType<System.Collections.Generic.Dictionary<string, object?>>(owned["highlightImage"]);
        Assert.True((float)ownedHighlight["colourAlpha"]! > 0f);
        var ownedTmp = Assert.IsType<System.Collections.Generic.Dictionary<string, object?>>(owned["tmp"]);
        Assert.Equal(NativeTabSelection.NativeSelectedTmpA, ownedTmp["colourAlpha"]);
        var ownedGraphics = Assert.IsType<System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>(owned["graphics"]);
        Assert.Equal(NativeTabSelection.OwnedGlowChildName, ownedGraphics[0]["path"]);
        Assert.Equal("Image", ownedGraphics[0]["type"]);
        Assert.Equal(NativeTabSelection.OwnedHighlightChildName, ownedGraphics[1]["path"]);
        Assert.False(NativeTabSelection.OwnedLookHasStar(ownedGraphics));
        var nativeGraphics = Assert.IsType<System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>>(rows[0]["graphics"]);
        Assert.Equal(NativeTabSelection.GlowPath, nativeGraphics[0]["path"]);
        Assert.True(Assert.IsType<bool>(rows[0]["interactable"]));
        Assert.True(Assert.IsType<bool>(rows[0]["raycastTarget"]));
        Assert.NotNull(nativeGraphics[0]["canvasRendererColor"]);
        Assert.NotNull(nativeGraphics[0]["crossFadeAlpha"]);
        Assert.NotNull(nativeGraphics[0]["visibleAlpha"]);
        Assert.NotNull(ownedGraphics[0]["visibleAlpha"]);
        Assert.Equal(
            NativeTabSelection.VisibleAlpha(1f, NativeTabSelection.NativeSelectedGlowAlpha),
            ownedGraphics[0]["visibleAlpha"]);
        Assert.True(NativeModsEntry.TestVisualHold.Held);

        NativeModsEntry.RestoreHostedView();
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
    }

    [Fact]
    public void Hold_from_fresh_join_nativeTabsLit_is_zero_then_prior_count_after_close()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests(selectedTab: null, headerActive: false);
        NativeModsEntry.HoldHostedViewIfTopTab();
        Assert.Equal("none:cleared", NativeModsEntry.DescribeOuterTabVisual().Detail);

        var native = new[]
        {
            NativeTabSelection.ClearedDraw("Emotes"),
            NativeTabSelection.ClearedDraw("VampirePowers"),
            NativeTabSelection.ClearedDraw("SpellBook"),
            NativeTabSelection.ClearedDraw("Map"),
            NativeTabSelection.ClearedDraw("VBlood"),
            NativeTabSelection.ClearedDraw("Social"),
            NativeTabSelection.ClearedDraw("System"),
        };
        var afterTick = NativeModsEntry.TickHeldDrawForTests(native);
        Assert.Equal(0, NativeModsEntry.NativeTabsLit);
        Assert.Equal("none:cleared", NativeModsEntry.DescribeOuterTabVisual().Detail);
        Assert.NotEqual("Emotes:cleared", NativeModsEntry.DescribeOuterTabVisual().Detail);
        Assert.False(afterTick[0].DrawsSelected);
        Assert.True(afterTick[afterTick.Length - 1].DrawsSelected);

        NativeModsEntry.RestoreHostedView();
        Assert.Equal(0, NativeModsEntry.NativeTabsLit);
        Assert.Equal("", NativeModsEntry.DescribeOuterTabVisual().Detail);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
    }

    [Fact]
    public void Late_hold_clears_pointer_hover_tint_after_mapper_in_the_same_frame()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("SpellBook", headerActive: true);
        NativeModsEntry.HoldHostedViewIfTopTab();
        var native = new[]
        {
            NativeTabSelection.ClearedDraw("Emotes"),
            NativeTabSelection.ClearedDraw("VampirePowers"),
            NativeTabSelection.ClearedDraw("SpellBook"),
            NativeTabSelection.ClearedDraw("Map"),
            NativeTabSelection.ClearedDraw("VBlood"),
            NativeTabSelection.ClearedDraw("Social"),
            NativeTabSelection.ClearedDraw("System"),
            NativeTabSelection.LitDraw(NativeTabSelection.ModsTabName),
        };
        var after = NativeModsEntry.TickHeldDrawAfterHoverForTests(native, hoverNativeIndex: 6);
        Assert.Equal(0, NativeModsEntry.NativeTabsLit);
        for (int i = 0; i < 7; i++)
        {
            Assert.False(after[i].DrawsSelected);
            Assert.False(after[i].ForceHighlight);
            Assert.False(after[i].GraphicsHighlighted);
            Assert.False(after[i].FontSwapped);
            Assert.False(after[i].SpriteSwapped);
            Assert.False(after[i].HoverObjectsActive);
            Assert.True(after[i].Interactable);
            Assert.True(after[i].RaycastTarget);
            Assert.False(after[i].CrossFadeRunning);
            Assert.Equal(0f, after[i].GlowCanvasAlpha);
        }

        Assert.True(after[after.Length - 1].HighlightColourAlpha > 0f);
        Assert.Equal(NativeTabSelection.NativeSelectedGlowAlpha, after[after.Length - 1].GlowAlpha);
        Assert.Equal(NativeTabSelection.NativeSelectedLineAlpha, after[after.Length - 1].LineAlpha);
        Assert.False(after[after.Length - 1].HasStar);
        NativeModsEntry.RestoreHostedView();
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        string entry = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        SourcePin.AssertUncommented(entry, "OnPointerExit(");
        SourcePin.AssertUncommented(entry, "ApplyNormalGraphicTargets(");
        SourcePin.AssertUncommented(entry, "ReapplyHeldOuterTabVisualAfterMapper(");
        SourcePin.AssertUncommented(entry, "TrySetLateFrame(OnLateFrame)");
        SourcePin.AssertUncommented(entry, "ApplyOwnedNativeSelectedLook(");
        SourcePin.AssertUncommented(entry, "overrideSprite = null");
        SourcePin.AssertUncommented(entry, "ApplyHoverLock(");
        SourcePin.AssertUncommented(entry, "FindOwnedPainterChild(");
        SourcePin.AssertUncommented(entry, "canvasRenderer.SetColor(");
        SourcePin.AssertNoStatementContaining(entry, "LockRootRaycast(");
        SourcePin.AssertUncommented(entry, "ApplyHeldPainters(");
        SourcePin.AssertUncommented(entry, "OwnedGlowChildName");
        SourcePin.AssertUncommented(entry, "IsHeldPainterPath(");
        SourcePin.AssertUncommented(entry, "RepairGen22RootIfPainted(");
        SourcePin.AssertUncommented(entry, "GetAlpha()");
        SourcePin.AssertUncommented(entry, "CopyNativeEntryLayout(");
        SourcePin.AssertUncommented(entry, "ApplyOwnedEntryLayout(");
        SourcePin.AssertUncommented(entry, "SetAsLastSibling()");
        SourcePin.AssertUncommented(entry, "ForceRebuildLayoutImmediate(");
        SourcePin.AssertUncommented(entry, "TryReapplyOwnedPainterLayoutOnce(");
        SourcePin.AssertUncommented(entry, "OwnedLineUnderCaptionSiblingIndex");
        SourcePin.AssertUncommented(entry, "OwnedCaptionChildName");
        SourcePin.AssertUncommented(entry, "localScale");
        SourcePin.AssertUncommented(entry, "SetSiblingIndex(");
        SourcePin.AssertUncommented(entry, "ReapplyOwnedPaintersFromCache(");
        SourcePin.AssertUncommented(entry, "ApplyOwnedPainterCanvas(");
        SourcePin.AssertUncommented(entry, "CrossFadeAlpha(");
        SourcePin.AssertUncommented(entry, "canvasRenderer.SetAlpha(");
        SourcePin.AssertUncommented(entry, "OnClick(OnOwnedButtonClicked)");
        string ownedClick = SourcePin.MethodBody(entry, "private static void OnOwnedButtonClicked()");
        SourcePin.AssertUncommented(ownedClick, "DecideOwnedButtonClick(");
        SourcePin.AssertUncommented(ownedClick, "InvokeStoredOpen(");
        SourcePin.AssertNoStatementContaining(ownedClick, "ModsMenu.Close(");
        string decide = SourcePin.MethodBody(entry, "internal static EntryClickAction DecideOwnedButtonClick(");
        SourcePin.AssertUncommented(decide, "workspaceOpen");
        SourcePin.AssertNoStatementContaining(decide, "EntryClickAction.Close");
        SourcePin.AssertUncommented(entry, "Color.white");
        SourcePin.AssertUncommented(entry, "visibleAlpha");
        string setPainter = SourcePin.MethodBody(
            entry, "private static void SetOwnedPainterVisible(GameObject go, string childName, bool selected, float selectedAlpha)");
        SourcePin.AssertUncommented(setPainter, "ApplyOwnedPainterCanvas(");
        SourcePin.AssertUncommented(setPainter, "CacheOwnedPainterImage(");
        SourcePin.AssertNoStatementContaining(setPainter, "GetComponentsInChildren");
        string canvas = SourcePin.MethodBody(entry, "private static void ApplyOwnedPainterCanvas(Image image, float a)");
        SourcePin.AssertUncommented(canvas, "image.color = Color.white");
        SourcePin.AssertUncommented(canvas, "canvasRenderer.SetColor(Color.white)");
        SourcePin.AssertUncommented(canvas, "canvasRenderer.SetAlpha(");
        SourcePin.AssertUncommented(canvas, "CrossFadeAlpha(");
        string late = SourcePin.MethodBody(entry, "private static void ReapplyHeldOuterTabVisualAfterMapper()");
        SourcePin.AssertUncommented(late, "ApplyHeldPainters(");
        SourcePin.AssertUncommented(late, "ReapplyOwnedPaintersFromCache(");
        SourcePin.AssertNoStatementContaining(late, "FindOwnedPainterChild(");
        SourcePin.AssertNoStatementContaining(late, "GetComponentsInChildren");
        string cacheReapply = SourcePin.MethodBody(entry, "private static void ReapplyOwnedPaintersFromCache()");
        SourcePin.AssertUncommented(cacheReapply, "ReapplyOneCachedOwnedPainter(ref _ownedGlowImage");
        SourcePin.AssertUncommented(cacheReapply, "ReapplyOneCachedOwnedPainter(ref _ownedLineImage");
        SourcePin.AssertNoStatementContaining(cacheReapply, "FindOwnedPainterChild(");
        string applyLayout = SourcePin.MethodBody(entry, "private static void ApplyOwnedEntryLayout(GameObject go)");
        SourcePin.AssertNoStatementContaining(applyLayout, "anchoredPosition");
        SourcePin.AssertNoStatementContaining(applyLayout, "anchorMin");
        SourcePin.AssertNoStatementContaining(applyLayout, "anchorMax");
        SourcePin.AssertNoStatementContaining(applyLayout, "rt.pivot");
        string copyLayout = SourcePin.MethodBody(entry, "private static void CopyNativeEntryLayout(GameObject owned, Transform parent)");
        SourcePin.AssertNoStatementContaining(copyLayout, "anchoredPosition");
        SourcePin.AssertUncommented(copyLayout, "CaptureOwnedEntryLayout(");
        SourcePin.AssertNoStatementContaining(entry, "BakeChildRectIntoEntry(");
        SourcePin.AssertNoStatementContaining(entry, "CopyNativeEntryRect(");
        // Revert: skipping canvasRenderer.SetColor or raycast lock leaves System highlighted (gen 20).
        // Revert: copying Stunlock_Icon_NewStar onto highlight is the gen-20 star.
        // Revert: snapping GraphicTargets unfiltered (root "") is gen 22 white boxes.
        // Revert: writing anchoredPosition/anchors onto the owned entry is gen 24 MODS-over-EMOTES.
        // Revert: world-space underline bake is gen 24 highlight w 0 h 0.
    }

    [Fact]
    public void Late_hold_snaps_running_crossfade_and_blocks_on_pointer_enter()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("SpellBook", headerActive: true);
        NativeModsEntry.HoldHostedViewIfTopTab();
        var native = new[]
        {
            NativeTabSelection.ClearedDraw("Emotes"),
            NativeTabSelection.ClearedDraw("VampirePowers"),
            NativeTabSelection.ClearedDraw("SpellBook"),
            NativeTabSelection.ClearedDraw("Map"),
            NativeTabSelection.ClearedDraw("VBlood"),
            NativeTabSelection.ClearedDraw("Social"),
            NativeTabSelection.ClearedDraw("System"),
            NativeTabSelection.LitDraw(NativeTabSelection.ModsTabName),
        };
        var after = NativeModsEntry.TickHeldDrawAfterCrossFadeForTests(native, hoverNativeIndex: 6);
        Assert.Equal(0, NativeModsEntry.NativeTabsLit);
        for (int i = 0; i < 7; i++)
        {
            Assert.False(after[i].ForceHighlight);
            Assert.False(after[i].CrossFadeRunning);
            Assert.True(after[i].RaycastTarget);
            Assert.Equal(0f, after[i].GlowCanvasAlpha);
            Assert.Equal(0f, after[i].LineCanvasAlpha);
            Assert.Equal(NativeTabSelection.NativeUnselectedTmpR, after[i].TmpR);
        }
        Assert.False(after[after.Length - 1].HasStar);
        Assert.Equal(
            NativeTabSelection.NativeSelectedGlowAlpha,
            after[after.Length - 1].GlowCanvasAlpha);
        Assert.Equal(
            NativeTabSelection.NativeSelectedLineAlpha,
            after[after.Length - 1].LineCanvasAlpha);
        NativeModsEntry.RestoreHostedView();
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        // Revert: SimulateRunningCrossFade without ApplyNormalPainters leaves Glow canvas 0.5.
        // Revert: skipping owned CanvasRenderer SetAlpha is gen 26 red px 0.
        // Revert: writing raycastTarget=false is the gen-30 click miss.
    }

    [Fact]
    public void Held_native_click_closes_mods_and_late_pass_keeps_hover_idle_without_raycast_lock()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("SpellBook", headerActive: true);
        NativeModsEntry.HoldHostedViewIfTopTab();
        var native = new[]
        {
            NativeTabSelection.ClearedDraw("Emotes"),
            NativeTabSelection.ClearedDraw("VampirePowers"),
            NativeTabSelection.LitDraw("SpellBook"),
            NativeTabSelection.ClearedDraw("Map"),
            NativeTabSelection.ClearedDraw("VBlood"),
            NativeTabSelection.ClearedDraw("Social"),
            NativeTabSelection.ClearedDraw("System"),
            NativeTabSelection.LitDraw(NativeTabSelection.ModsTabName),
        };
        var held = NativeModsEntry.TickHeldDrawAfterHoverForTests(native, hoverNativeIndex: 3);
        Assert.Equal(0, NativeModsEntry.NativeTabsLit);
        Assert.True(held[3].RaycastTarget);
        Assert.False(held[3].ForceHighlight);
        Assert.Equal(0f, held[3].GlowCanvasAlpha);
        Assert.True(NativeTabSelection.HeldNativeEventTriggerFires(
            held[3].RaycastTarget, held[3].Interactable));

        NativeModsEntry.SetCurrentViewStateForTests("Map");
        NativeModsEntry.RestoreHostedView();
        Assert.False(NativeModsEntry.TestVisualHold.Held);
        Assert.Equal(1, NativeModsEntry.NativeTabsLit);
        var described = NativeModsEntry.DescribeOuterTabs();
        Assert.Equal("Map", described["selectedOuterTab"]);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);

        string entry = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        string hoverLock = SourcePin.MethodBody(entry, "private static void ApplyHoverLock");
        SourcePin.AssertNoStatementContaining(hoverLock, "raycastTarget");
        string applyHeld = SourcePin.MethodBody(entry, "private static void ApplyHeldPainters");
        SourcePin.AssertNoStatementContaining(applyHeld, "raycastTarget");
        SourcePin.AssertUncommented(entry, "RepairDamagedNativeBase(");
        SourcePin.AssertUncommented(entry, "BeginAdoptHoldCaptures(");
        SourcePin.AssertUncommented(entry, "FormatRepairedBaseLog(");
        // Revert: LockRootRaycast is the user-reported Spellbook-does-not-click defect.
    }

    [Fact]
    public void Owned_painter_late_pass_restores_renderer_alpha_from_idle_clone()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("SpellBook", headerActive: true);
        NativeModsEntry.HoldHostedViewIfTopTab();
        var native = new[]
        {
            NativeTabSelection.ClearedDraw("Emotes"),
            NativeTabSelection.ClearedDraw("VampirePowers"),
            NativeTabSelection.ClearedDraw("SpellBook"),
            NativeTabSelection.ClearedDraw("Map"),
            NativeTabSelection.ClearedDraw("VBlood"),
            NativeTabSelection.ClearedDraw("Social"),
            NativeTabSelection.ClearedDraw("System"),
            NativeTabSelection.LitDraw(NativeTabSelection.ModsTabName) with
            {
                GlowCanvasAlpha = 0f,
                LineCanvasAlpha = 0f,
            },
        };
        var after = NativeModsEntry.TickHeldDrawForTests(native);
        Assert.Equal(
            NativeTabSelection.NativeSelectedGlowAlpha,
            after[after.Length - 1].GlowCanvasAlpha);
        Assert.Equal(
            NativeTabSelection.NativeSelectedLineAlpha,
            after[after.Length - 1].LineCanvasAlpha);
        var glow = NativeTabSelection.ShowOwnedPainter(isGlow: true);
        var line = NativeTabSelection.ShowOwnedPainter(isGlow: false);
        Assert.Equal(NativeTabSelection.NativeSelectedGlowAlpha, glow.CanvasA);
        Assert.Equal(1f, glow.ColorA);
        Assert.Equal(NativeTabSelection.NativeSelectedLineAlpha, line.CanvasA);
        var hidden = NativeTabSelection.HideOwnedPainter(glow);
        Assert.Equal(0f, hidden.CanvasA);
        Assert.False(hidden.Enabled);
        NativeModsEntry.RestoreHostedView();
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        // Revert: TickLateWhileHeldDraw without ApplyOwnedPainterShowToDraw
        // leaves a FIX8 re-clone at canvasRenderer alpha 0.
    }

    [Fact]
    public void Close_from_stuck_map_social_leaves_only_the_selected_native_lit()
    {
        // Gen 28: Map and Social stayed selected-look after close. Canonical
        // release heals that on the next close even when the snapshot starts
        // with two extra natives lit and IsSelected false on every tab.
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("System", headerActive: false);
        NativeModsEntry.HoldHostedViewIfTopTab();
        var stuck = new[]
        {
            NativeTabSelection.ClearedDraw("Emotes") with { IsSelected = false },
            NativeTabSelection.ClearedDraw("VampirePowers") with { IsSelected = false },
            NativeTabSelection.ClearedDraw("SpellBook") with { IsSelected = false },
            NativeTabSelection.LitDraw("Map") with { IsSelected = false },
            NativeTabSelection.ClearedDraw("VBlood") with { IsSelected = false },
            NativeTabSelection.LitDraw("Social") with { IsSelected = false },
            NativeTabSelection.LitDraw("System") with { IsSelected = false },
            NativeTabSelection.LitDraw(NativeTabSelection.ModsTabName),
        };
        NativeModsEntry.SetDrawTabsForTests(stuck);
        NativeModsEntry.RestoreHostedView();
        Assert.Equal(1, NativeModsEntry.NativeTabsLit);

        var described = NativeModsEntry.DescribeOuterTabs();
        Assert.Equal("System", described["selectedOuterTab"]);
        var rows = Assert.IsType<List<Dictionary<string, object?>>>(described["native"]);
        int selectedCount = 0;
        string? selectedName = null;
        for (int i = 0; i < rows.Count; i++)
        {
            if (Assert.IsType<bool>(rows[i]["isSelected"]))
            {
                selectedCount++;
                selectedName = Assert.IsType<string>(rows[i]["name"]);
            }
        }
        Assert.Equal(1, selectedCount);
        Assert.Equal("System", selectedName);
        Assert.Equal(false, rows[3]["isSelected"]);
        Assert.Equal(false, rows[5]["isSelected"]);
        var systemDraw = NativeModsEntry.DescribeOuterTabs();
        var nativeRows = Assert.IsType<List<Dictionary<string, object?>>>(systemDraw["native"]);
        var systemStun = Assert.IsType<Dictionary<string, object?>>(nativeRows[6]["simpleStunButton"]);
        Assert.Equal(false, systemStun["forceSelect"]);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);

        string entry = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        string restoreVis = SourcePin.MethodBody(entry, "private static void RestoreOuterTabVisual");
        SourcePin.AssertUncommented(restoreVis, "ReleaseNativeOverridesThenRefresh(");
        SourcePin.AssertUncommented(restoreVis, "LogReleaseDrive(");
        SourcePin.AssertNoStatementContaining(restoreVis, "RestoreHeldPainters(");
        SourcePin.AssertNoStatementContaining(restoreVis, "ApplyNativeOuterTabVisual(");
        SourcePin.AssertNoStatementContaining(restoreVis, "ApplyOneOuterTab(");
        SourcePin.AssertNoStatementContaining(restoreVis, "InitInstantHighlight(");
        string releaseBody = SourcePin.MethodBody(
            entry, "private static (int idle, int litAfter, int forceSelectCleared) ReleaseNativeOverridesThenRefresh");
        SourcePin.AssertUncommented(releaseBody, "ForceSelect = false");
        SourcePin.AssertUncommented(releaseBody, "tab.Refresh(");
        SourcePin.AssertUncommented(releaseBody, "OnPointerExit(");
        SourcePin.AssertUncommented(releaseBody, "OnDeselect(");
        SourcePin.AssertNoStatementContaining(releaseBody, "InitInstantHighlight(");
        SourcePin.AssertNoStatementContaining(releaseBody, "ApplyCanonicalPainters(");
        string log = SourcePin.MethodBody(entry, "private static void LogReleaseDrive");
        SourcePin.AssertUncommented(log, "NativeTabSelection.FormatReleaseLog(");
        SourcePin.AssertUncommented(log, "forceSelectCleared");
        // Revert: ApplyNativeOuterTabVisual(selected:true) injects ForceSelect
        // the mapper never clears (gen 29 Spellbook stays lit).
    }

    [Fact]
    public void Close_during_native_click_selects_the_clicked_page_not_the_saved_tab()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("SpellBook", headerActive: true);
        NativeModsEntry.HoldHostedViewIfTopTab();
        var opened = new[]
        {
            NativeTabSelection.ClearedDraw("Emotes"),
            NativeTabSelection.ClearedDraw("VampirePowers"),
            NativeTabSelection.LitDraw("SpellBook"),
            NativeTabSelection.ClearedDraw("Map"),
            NativeTabSelection.ClearedDraw("VBlood"),
            NativeTabSelection.ClearedDraw("Social"),
            NativeTabSelection.ClearedDraw("System"),
            NativeTabSelection.LitDraw(NativeTabSelection.ModsTabName),
        };
        NativeModsEntry.TickHeldDrawForTests(opened);
        NativeModsEntry.SetCurrentViewStateForTests("System");
        NativeModsEntry.RestoreHostedView();
        Assert.False(NativeModsEntry.TestVisualHold.Held);
        Assert.Equal(1, NativeModsEntry.NativeTabsLit);
        var described = NativeModsEntry.DescribeOuterTabs();
        Assert.Equal("System", described["selectedOuterTab"]);
        var rows = Assert.IsType<List<Dictionary<string, object?>>>(described["native"]);
        Assert.Equal(false, rows[2]["isSelected"]);
        Assert.Equal(true, rows[6]["isSelected"]);
        var spellStun = Assert.IsType<Dictionary<string, object?>>(rows[2]["simpleStunButton"]);
        Assert.Equal(false, spellStun["forceSelect"]);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        // Revert: SavedTab-first CloseOverDraw ForceSelects Spellbook after a System click.
    }

    [Fact]
    public void Second_hold_recaptures_painters_and_late_pass_runs_without_cleared()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("SpellBook", headerActive: true);
        NativeModsEntry.HoldHostedViewIfTopTab();
        Assert.True(NativeModsEntry.TestHeldCapture.Painters > 0);
        Assert.True(NativeModsEntry.TestHeldCapture.Buttons > 0);
        var native = new[]
        {
            NativeTabSelection.ClearedDraw("Emotes"),
            NativeTabSelection.ClearedDraw("VampirePowers"),
            NativeTabSelection.LitDraw("SpellBook"),
            NativeTabSelection.ClearedDraw("Map"),
            NativeTabSelection.ClearedDraw("VBlood"),
            NativeTabSelection.ClearedDraw("Social"),
            NativeTabSelection.ClearedDraw("System"),
            NativeTabSelection.LitDraw(NativeTabSelection.ModsTabName),
        };
        NativeModsEntry.TickHeldDrawForTests(native);
        var described = NativeModsEntry.DescribeOuterTabs();
        var hold = Assert.IsType<Dictionary<string, object?>>(described["hold"]);
        Assert.Equal(true, hold["held"]);
        Assert.Equal(true, hold["cleared"]);
        Assert.True(Assert.IsType<int>(hold["tickRuns"]) > 0);
        Assert.True(Assert.IsType<int>(hold["lateRuns"]) > 0);
        Assert.Equal("", hold["lateSkippedWhy"]);
        Assert.Equal(21, hold["painters"]);
        Assert.Equal(7, hold["buttons"]);
        Assert.True(Assert.IsType<int>(hold["lastCaptureFrame"]) > 0);

        NativeModsEntry.RestoreHostedView();
        Assert.Equal(0, NativeModsEntry.TestHeldCapture.Painters);
        NativeModsEntry.HoldHostedViewIfTopTab();
        Assert.True(NativeModsEntry.TestHeldCapture.Painters > 0);
        Assert.True(NativeModsEntry.TestHeldCapture.Buttons > 0);
        NativeModsEntry.RebuildHeldHudForTests();
        NativeModsEntry.HoldHostedViewIfTopTab();
        Assert.True(NativeModsEntry.TestHeldCapture.Painters > 0);

        NativeModsEntry.RestoreHostedView();
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);

        string entry = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        string capture = SourcePin.MethodBody(entry, "private static void CaptureHeldPainters");
        SourcePin.AssertNoStatementContaining(capture, "_heldPainters.Count > 0");
        string late = SourcePin.MethodBody(entry, "private static void ReapplyHeldOuterTabVisualAfterMapper()");
        SourcePin.AssertUncommented(late, "ShouldReapplyLateWhileHeld(");
        SourcePin.AssertUncommented(late, "ApplyHeldPainters(");
        string sel = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeTabSelection.cs");
        string gate = SourcePin.MethodBody(sel, "internal static bool ShouldReapplyLateWhileHeld");
        SourcePin.AssertUncommented(gate, "hold.Held");
        SourcePin.AssertNoStatementContaining(gate, "ShouldReapplyWhileHeld");
        // Revert: CaptureHeldPainters Count>0 early-return or late gate on Cleared is gen 32.
    }

    [Fact]
    public void Hosted_restore_leaves_switched_page_inactive_and_reactivates_same_page()
    {
        object systemPage = new object();
        object socialPage = new object();
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("System", headerActive: false);
        NativeModsEntry.SetHostedInstanceForTests(systemPage, active: true);
        NativeModsEntry.SetCurrentViewInstanceForTests(systemPage);
        NativeModsEntry.HoldHostedViewIfTopTab();
        Assert.False(NativeModsEntry.TestHeldHostedActive);

        NativeModsEntry.SetCurrentViewStateForTests("Social");
        NativeModsEntry.SetCurrentViewInstanceForTests(socialPage);
        NativeModsEntry.RestoreHostedView();
        Assert.False(NativeModsEntry.TestHeldHostedActive);
        Assert.Equal(
            "Ui: tabdiag hosted-restore skipped current=Social",
            NativeModsEntry.TestHostedRestoreSkippedLog);

        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("System", headerActive: false);
        NativeModsEntry.SetHostedInstanceForTests(systemPage, active: true);
        NativeModsEntry.SetCurrentViewInstanceForTests(systemPage);
        NativeModsEntry.SetCurrentViewStateForTests("System");
        NativeModsEntry.HoldHostedViewIfTopTab();
        NativeModsEntry.RestoreHostedView();
        Assert.True(NativeModsEntry.TestHeldHostedActive);
        Assert.Null(NativeModsEntry.TestHostedRestoreSkippedLog);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);

        string entry = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        SourcePin.AssertUncommented(entry, "ShouldRestoreHeldHostedInstance(");
        SourcePin.AssertUncommented(entry, "FormatHostedRestoreSkippedLog(");
        // Revert: SetActive(true) without the current-instance check is System under Social.
    }

    [Fact]
    public void Dead_host_during_release_clears_the_visual_hold()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("SpellBook", headerActive: true);
        NativeModsEntry.HoldHostedViewIfTopTab();
        Assert.True(NativeModsEntry.TestVisualHold.Held);
        NativeModsEntry.SetDrawTabsForTests(null);
        NativeModsEntry.RestoreHostedView();
        Assert.False(NativeModsEntry.TestVisualHold.Held);
        Assert.Null(NativeModsEntry.TestReleaseWarning);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
    }

    [Fact]
    public void Exception_mid_restore_still_clears_ownership_and_logs_a_warning()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("SpellBook", headerActive: true);
        NativeModsEntry.HoldHostedViewIfTopTab();
        NativeModsEntry.SetReleaseFaultForTests(new InvalidOperationException("mid-restore"));
        NativeModsEntry.RestoreHostedView();
        Assert.False(NativeModsEntry.TestVisualHold.Held);
        Assert.Equal("mid-restore", NativeModsEntry.TestReleaseWarning);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        // Revert: catch that still logs the success release line reports a lie.
    }

    [Fact]
    public void Repeated_release_is_idempotent()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("System", headerActive: false);
        NativeModsEntry.HoldHostedViewIfTopTab();
        var native = new[]
        {
            NativeTabSelection.ClearedDraw("Emotes"),
            NativeTabSelection.ClearedDraw("VampirePowers"),
            NativeTabSelection.ClearedDraw("SpellBook"),
            NativeTabSelection.ClearedDraw("Map"),
            NativeTabSelection.ClearedDraw("VBlood"),
            NativeTabSelection.ClearedDraw("Social"),
            NativeTabSelection.LitDraw("System"),
            NativeTabSelection.LitDraw(NativeTabSelection.ModsTabName),
        };
        NativeModsEntry.TickHeldDrawForTests(native);
        NativeModsEntry.RestoreHostedView();
        Assert.False(NativeModsEntry.TestVisualHold.Held);
        Assert.Equal(1, NativeModsEntry.NativeTabsLit);
        NativeModsEntry.RestoreHostedView();
        Assert.False(NativeModsEntry.TestVisualHold.Held);
        Assert.Null(NativeModsEntry.TestReleaseWarning);
        Assert.Equal(1, NativeModsEntry.NativeTabsLit);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
    }

    [Fact]
    public void Title_view_hold_restores_when_an_exception_is_thrown_mid_open()
    {
        NativeModsEntry.SetOwnedForTests("main-menu", curtain: false, observers: 0);
        NativeModsEntry.HoldTitleViewIfMainMenu();
        Assert.True(NativeModsEntry.TitleViewIsHidden);
        try
        {
            throw new InvalidOperationException("mid-open");
        }
        catch (InvalidOperationException)
        {
            NativeModsEntry.RestoreTitleView();
        }
        Assert.False(NativeModsEntry.TitleViewIsHidden);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
    }

    [Fact]
    public void Play_backdrop_prefers_prefab_then_live_clone_then_none()
    {
        Assert.Equal(
            NativeModsEntry.PlayBackdropHold.SourcePrefab,
            NativeModsEntry.PlayBackdropHold.Decide(prefabReachable: true, liveInstanceReachable: true));
        Assert.Equal(
            NativeModsEntry.PlayBackdropHold.SourcePrefab,
            NativeModsEntry.PlayBackdropHold.Decide(prefabReachable: true, liveInstanceReachable: false));
        Assert.Equal(
            NativeModsEntry.PlayBackdropHold.SourceLiveClone,
            NativeModsEntry.PlayBackdropHold.Decide(prefabReachable: false, liveInstanceReachable: true));
        Assert.Equal(
            NativeModsEntry.PlayBackdropHold.SourceNone,
            NativeModsEntry.PlayBackdropHold.Decide(prefabReachable: false, liveInstanceReachable: false));
        Assert.True(NativeModsEntry.PlayBackdropHold.Present(NativeModsEntry.PlayBackdropHold.SourcePrefab));
        Assert.True(NativeModsEntry.PlayBackdropHold.Present(NativeModsEntry.PlayBackdropHold.SourceLiveClone));
        Assert.False(NativeModsEntry.PlayBackdropHold.Present(NativeModsEntry.PlayBackdropHold.SourceNone));
        Assert.False(NativeModsEntry.PlayBackdropHold.Present(null));
        // Removing Decide's prefab-first arm would report live-clone (or none) while
        // MenuViews+TryGetAsset can still reach PlaySelectMenuView_V2.
    }

    [Fact]
    public void Play_backdrop_sits_below_panels_and_workspace_art()
    {
        Assert.True(NativeModsEntry.PlayBackdropHold.SitsBelowPanels(
            ModsCanvasBand.Backdrop, ModsCanvasBand.Panels));
        Assert.False(NativeModsEntry.PlayBackdropHold.SitsBelowPanels(
            ModsCanvasBand.Panels, ModsCanvasBand.Panels));
        Assert.False(NativeModsEntry.PlayBackdropHold.SitsBelowPanels(
            ModsCanvasBand.Panels, ModsCanvasBand.Backdrop));
        Assert.True(NativeModsEntry.PlayBackdropHold.SitsBelowWorkspaceArt(cloneSibling: 0, artSibling: 1));
        Assert.False(NativeModsEntry.PlayBackdropHold.SitsBelowWorkspaceArt(cloneSibling: 1, artSibling: 0));
        Assert.True(ModsCanvasBand.LayerOrderIsResolved(
            ModsCanvasBand.Blocker, ModsCanvasBand.Backdrop, ModsCanvasBand.Panels, ModsCanvasBand.Overlay));
        // Removing SitsBelowPanels (or parenting the clone onto Panels) puts fog over
        // the workspace art the player still needs to read.
    }

    [Fact]
    public void Play_backdrop_hold_restores_on_every_exit_path()
    {
        var present = NativeModsEntry.PlayBackdropHold.Hide(
            default, siteIsMainMenu: true, NativeModsEntry.PlayBackdropHold.SourcePrefab)
            .WithAssets("MenuBG", "MenuFogMat", "UI/MenuFog");
        Assert.True(present.Held);
        Assert.Equal(NativeModsEntry.PlayBackdropHold.SourcePrefab, present.Source);
        Assert.False(NativeModsEntry.PlayBackdropHold.ShouldDestroy(default, present));
        Assert.True(NativeModsEntry.PlayBackdropHold.ShouldDestroy(present, default));

        var closed = NativeModsEntry.PlayBackdropHold.AfterClose(present);
        Assert.False(closed.Held);
        Assert.True(NativeModsEntry.PlayBackdropHold.ShouldDestroy(present, closed));

        var escaped = NativeModsEntry.PlayBackdropHold.AfterEscape(present);
        Assert.False(escaped.Held);
        Assert.True(NativeModsEntry.PlayBackdropHold.ShouldDestroy(present, escaped));

        var threw = NativeModsEntry.PlayBackdropHold.AfterExceptionDuringOpen(present);
        Assert.False(threw.Held);
        Assert.True(NativeModsEntry.PlayBackdropHold.ShouldDestroy(present, threw));

        foreach (Progenitor.Plugins.PluginUnloadReason reason in Enum.GetValues(typeof(Progenitor.Plugins.PluginUnloadReason)))
        {
            var after = NativeModsEntry.PlayBackdropHold.AfterUnload(present, reason);
            Assert.False(after.Held);
            Assert.True(NativeModsEntry.PlayBackdropHold.ShouldDestroy(present, after));
        }

        var missing = NativeModsEntry.PlayBackdropHold.Hide(
            default, siteIsMainMenu: true, NativeModsEntry.PlayBackdropHold.SourceNone);
        Assert.False(missing.Held);
        Assert.Equal(NativeModsEntry.PlayBackdropHold.SourceNone, missing.Source);

        var otherSite = NativeModsEntry.PlayBackdropHold.Hide(
            default, siteIsMainMenu: false, NativeModsEntry.PlayBackdropHold.SourcePrefab);
        Assert.False(otherSite.Held);
        // Removing AfterUnload / AfterEscape / AfterClose / AfterExceptionDuringOpen
        // (or RestoreTitleView's ReleaseDisplayedPlayBackdrop) leaves the clone
        // across a Core swap — a leak. A dead object is tolerated: Restore is default.
    }

    [Fact]
    public void Play_backdrop_describe_names_source_and_assets()
    {
        var held = NativeModsEntry.PlayBackdropHold.Hide(
            default, siteIsMainMenu: true, NativeModsEntry.PlayBackdropHold.SourceLiveClone)
            .WithAssets("FogSprite", "FogMat", "VRising/UI/MenuBackground");
        var doc = NativeModsEntry.PlayBackdropHold.Describe(held);
        Assert.Equal(true, doc["present"]);
        Assert.Equal(NativeModsEntry.PlayBackdropHold.SourceLiveClone, doc["source"]);
        Assert.Equal("FogSprite", doc["sprite"]);
        Assert.Equal("FogMat", doc["material"]);
        Assert.Equal("VRising/UI/MenuBackground", doc["shader"]);

        var empty = NativeModsEntry.PlayBackdropHold.Describe(default);
        Assert.Equal(false, empty["present"]);
        Assert.Equal(NativeModsEntry.PlayBackdropHold.SourceNone, empty["source"]);
    }

    [Fact]
    public void Main_menu_open_holds_the_play_backdrop_and_top_tab_does_not()
    {
        NativeModsEntry.SetOwnedForTests("main-menu", curtain: true, observers: 0);
        NativeModsEntry.SetPlayBackdropAcquisitionForTests(
            NativeModsEntry.PlayBackdropHold.SourcePrefab, "MenuBG", "MenuMat", "UI/Fog");
        NativeModsEntry.HoldTitleViewIfMainMenu();
        Assert.True(NativeModsEntry.PlayBackdropIsHeld);
        Assert.True(NativeModsEntry.TitleViewIsHidden);
        var described = NativeModsEntry.DescribePlayBackdrop();
        Assert.Equal(true, described["present"]);
        Assert.Equal(NativeModsEntry.PlayBackdropHold.SourcePrefab, described["source"]);
        Assert.Equal("MenuBG", described["sprite"]);
        NativeModsEntry.RestoreTitleView();
        Assert.False(NativeModsEntry.PlayBackdropIsHeld);
        Assert.Equal(false, NativeModsEntry.DescribePlayBackdrop()["present"]);

        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.HoldTitleViewIfMainMenu();
        Assert.False(NativeModsEntry.PlayBackdropIsHeld);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
    }

    [Fact]
    public void Cloned_native_entry_still_reads_the_donor_label_after_reapply()
    {
        // MenuButton.LocalizationKey + Update write
        // the donor string onto ButtonText every frame. Assigning "MODS" then reading
        // the TMP is what MENU1 asserted; the resolved caption is still OPTIONS.
        string resolved = NativeModsEntry.ResolveLabelAfterReapply(
            assignedText: NativeModsEntry.MainMenuEntryLabel,
            localizationKeyText: "OPTIONS",
            nativeReapplies: true);
        Assert.Equal("OPTIONS", resolved);
        Assert.False(NativeModsEntry.LabelReadsMods(resolved));

        string built = NativeModsEntry.ResolveLabelAfterReapply(
            assignedText: NativeModsEntry.MainMenuEntryLabel,
            localizationKeyText: "OPTIONS",
            nativeReapplies: false);
        Assert.Equal("MODS", built);
        Assert.True(NativeModsEntry.LabelReadsMods(built));
        Assert.True(NativeModsEntry.LabelReadsMods("mods"));
    }

    [Fact]
    public void Inline_self_check_accepts_a_resolved_enabled_row_inside_the_list()
    {
        var enabled = NativeModsEntry.MenuColor.NativeEnabled;
        var container = new NativeModsEntry.MenuRect { X = 0, Y = 0, Width = 320, Height = 400 };
        var entry = new NativeModsEntry.MenuRect { X = 0, Y = 48, Width = 320, Height = 48 };
        var exit = new NativeModsEntry.MenuRect { X = 0, Y = 0, Width = 320, Height = 48 };
        var icons = new NativeModsEntry.MenuRect { X = 0, Y = -80, Width = 280, Height = 40 };

        Assert.Equal(NativeModsEntry.InlineCheckFailure.None, NativeModsEntry.EvaluateInlineEntry(
            "MODS", interactable: true, enabled, enabled, entry, container, exit, icons));
    }

    [Theory]
    [InlineData("OPTIONS", true, 1f, 1)]
    [InlineData("MODS", false, 1f, 2)]
    [InlineData("MODS", true, 0.4f, 3)]
    public void Inline_self_check_falls_back_when_label_interactable_or_colour_is_wrong(
        string label, bool interactable, float alpha, int expectedFailure)
    {
        var expected = (NativeModsEntry.InlineCheckFailure)expectedFailure;
        var sibling = NativeModsEntry.MenuColor.NativeEnabled;
        var entryColor = sibling with { A = alpha };
        var container = new NativeModsEntry.MenuRect { X = 0, Y = 0, Width = 320, Height = 400 };
        var entry = new NativeModsEntry.MenuRect { X = 0, Y = 48, Width = 320, Height = 48 };
        var exit = new NativeModsEntry.MenuRect { X = 0, Y = 0, Width = 320, Height = 48 };

        var failure = NativeModsEntry.EvaluateInlineEntry(
            label, interactable, entryColor, sibling, entry, container, exit, iconRow: null);
        Assert.Equal(expected, failure);

        var intended = NativeModsEntry.DecideMainMenuEntry(
            buttonsParentPresent: true,
            NativeModsEntry.TypicalTitleSiblings,
            donorAvailable: true,
            NativeModsEntry.NativeListLayout.VerticalGroup,
            listHasRoom: true);
        var after = NativeModsEntry.AfterInlineCheck(intended, failure);
        Assert.True(after.UsesCornerAnchors);
        Assert.Equal(NativeModsEntry.MainMenuEntryChrome.HandBuiltCorner, after.Chrome);
        Assert.Equal(expected, after.FallbackReason);
        Assert.Equal(
            "Ui: Mods main menu entry attached (corner fallback; "
            + NativeModsEntry.FormatInlineFailure(expected) + ").",
            NativeModsEntry.FormatMainMenuAttachLog(after));
    }

    [Fact]
    public void Inline_self_check_falls_back_when_exit_overlaps_the_social_icon_row()
    {
        // Live 3840×1080 title: a seventh native-height row grew the vertical stack
        // and pushed EXIT down into LinkButtonSet. This AABB overlap is the assertion
        // MENU1 lacked.
        var enabled = NativeModsEntry.MenuColor.NativeEnabled;
        var container = new NativeModsEntry.MenuRect { X = 0, Y = -8, Width = 320, Height = 336 };
        var mods = new NativeModsEntry.MenuRect { X = 0, Y = 40, Width = 320, Height = 48 };
        var exit = new NativeModsEntry.MenuRect { X = 0, Y = -8, Width = 320, Height = 48 };
        var icons = new NativeModsEntry.MenuRect { X = 0, Y = 0, Width = 280, Height = 40 };

        Assert.True(exit.Overlaps(icons));
        Assert.False(mods.Overlaps(exit));
        Assert.True(container.Contains(mods));

        var failure = NativeModsEntry.EvaluateInlineEntry(
            "MODS", interactable: true, enabled, enabled, mods, container, exit, icons);
        Assert.Equal(NativeModsEntry.InlineCheckFailure.FollowingOverlapsIcons, failure);

        var after = NativeModsEntry.AfterInlineCheck(
            NativeModsEntry.DecideMainMenuEntry(
                buttonsParentPresent: true,
                NativeModsEntry.TypicalTitleSiblings,
                donorAvailable: true,
                NativeModsEntry.NativeListLayout.VerticalGroup,
                listHasRoom: true),
            failure);
        Assert.True(after.UsesCornerAnchors);
        Assert.Equal(NativeModsEntry.InlineCheckFailure.FollowingOverlapsIcons, after.FallbackReason);
        Assert.Equal(
            "Ui: Mods main menu entry attached (corner fallback; inline entry failed: following-overlaps-icons).",
            NativeModsEntry.FormatMainMenuAttachLog(after));
    }

    [Fact]
    public void Inline_self_check_falls_back_when_the_entry_overlaps_exit_or_leaves_the_container()
    {
        var enabled = NativeModsEntry.MenuColor.NativeEnabled;
        var container = new NativeModsEntry.MenuRect { X = 0, Y = 0, Width = 320, Height = 288 };
        var overlapping = new NativeModsEntry.MenuRect { X = 0, Y = 20, Width = 320, Height = 48 };
        var exit = new NativeModsEntry.MenuRect { X = 0, Y = 0, Width = 320, Height = 48 };
        Assert.Equal(NativeModsEntry.InlineCheckFailure.OverlapsFollowing, NativeModsEntry.EvaluateInlineEntry(
            "MODS", true, enabled, enabled, overlapping, container, exit, iconRow: null));

        var outside = new NativeModsEntry.MenuRect { X = 0, Y = -40, Width = 320, Height = 48 };
        var exitInside = new NativeModsEntry.MenuRect { X = 0, Y = 40, Width = 320, Height = 48 };
        Assert.Equal(NativeModsEntry.InlineCheckFailure.OutsideContainer, NativeModsEntry.EvaluateInlineEntry(
            "MODS", true, enabled, enabled, outside, container, exitInside, iconRow: null));
    }

    [Fact]
    public void Tight_parent_box_cannot_fit_seven_rows_but_no_room_is_not_a_corner_reason()
    {
        // Six 48px rows fill 288px. Icons sit immediately below (top at y=40).
        // Available height from list top (328) down to icon top (40) minus the
        // 40px MinIconClearance is 248 — a seventh 48px row cannot fit in that
        // tight parent box. The live title is not this box: CONTINUE sits well
        // below the RISING logo, and that view headroom is what the attach
        // path lifts into. "No room" is not a corner reason.
        var container = new NativeModsEntry.MenuRect { X = 0, Y = 40, Width = 320, Height = 288 };
        var icons = new NativeModsEntry.MenuRect { X = 0, Y = 0, Width = 280, Height = 40 };
        float available = NativeModsEntry.AvailableListHeight(container, icons);
        Assert.Equal(248f, available);
        Assert.Equal(40f, NativeModsEntry.MinIconClearance);
        Assert.Equal(53f, NativeModsEntry.TargetIconClearance);
        Assert.False(NativeModsEntry.NativeListHasRoom(
            available, countAfterInsert: 7, rowHeight: 48f, spacing: 0f, padding: 0f));
        Assert.Null(NativeModsEntry.SpacingThatFits(
            available, countAfterInsert: 7, rowHeight: 48f, padding: 0f, currentSpacing: 8f));

        var plan = NativeModsEntry.DecideMainMenuEntry(
            buttonsParentPresent: true,
            NativeModsEntry.TypicalTitleSiblings,
            donorAvailable: true,
            NativeModsEntry.NativeListLayout.VerticalGroup,
            listHasRoom: false);
        Assert.True(plan.ParentsToNativeList);
        Assert.False(plan.UsesCornerAnchors);
        Assert.Equal(0, plan.SiblingIndex);
        Assert.Equal(NativeModsEntry.InlineCheckFailure.None, plan.FallbackReason);
        Assert.Equal(
            "Ui: Mods main menu entry attached inline sibling=0 chrome=hand-built layout=vertical.",
            NativeModsEntry.FormatMainMenuAttachLog(plan));
    }

    [Fact]
    public void Spacing_shrink_makes_room_when_the_stack_has_spare_gap()
    {
        // 7×48 = 336; available 384 leaves 48px to split across 6 gaps → 8px.
        Assert.True(NativeModsEntry.NativeListHasRoom(
            availableHeight: 384f, countAfterInsert: 7, rowHeight: 48f, spacing: 8f, padding: 0f));
        Assert.Equal(8f, NativeModsEntry.SpacingThatFits(384f, 7, 48f, 0f, 8f));
        Assert.Equal(4f, NativeModsEntry.SpacingThatFits(360f, 7, 48f, 0f, 12f));
        Assert.Null(NativeModsEntry.SpacingThatFits(320f, 7, 48f, 0f, 12f));
    }

    [Fact]
    public void Broken_clone_self_check_tears_the_inline_entry_down()
    {
        // MENU1's clone: donor caption after re-apply, disabled-looking alpha,
        // EXIT overlapping the icon row. Any one of those corners; all three
        // together still corners, and the first failure (wrong label) wins.
        var dim = NativeModsEntry.MenuColor.NativeEnabled with { A = 0.35f };
        var sibling = NativeModsEntry.MenuColor.NativeEnabled;
        var container = new NativeModsEntry.MenuRect { X = 0, Y = -8, Width = 320, Height = 336 };
        var mods = new NativeModsEntry.MenuRect { X = 0, Y = 40, Width = 320, Height = 48 };
        var exit = new NativeModsEntry.MenuRect { X = 0, Y = -8, Width = 320, Height = 48 };
        var icons = new NativeModsEntry.MenuRect { X = 0, Y = 0, Width = 280, Height = 40 };

        string resolved = NativeModsEntry.ResolveLabelAfterReapply("MODS", "OPTIONS", nativeReapplies: true);
        var failure = NativeModsEntry.EvaluateInlineEntry(
            resolved, interactable: false, dim, sibling, mods, container, exit, icons);
        Assert.Equal(NativeModsEntry.InlineCheckFailure.WrongLabel, failure);

        var clonePlan = new NativeModsEntry.MainMenuEntryPlan
        {
            Parent = NativeModsEntry.MainMenuEntryParent.NativeList,
            Chrome = NativeModsEntry.MainMenuEntryChrome.ClonedNative,
            Layout = NativeModsEntry.NativeListLayout.VerticalGroup,
            SiblingIndex = 0,
            CloneDonorIndex = 4,
        };
        var after = NativeModsEntry.AfterInlineCheck(clonePlan, failure);
        Assert.False(after.ParentsToNativeList);
        Assert.Equal(NativeModsEntry.MainMenuEntryChrome.HandBuiltCorner, after.Chrome);
        Assert.Equal(
            "Ui: Mods main menu entry attached (corner fallback; inline entry failed: wrong-label).",
            NativeModsEntry.FormatMainMenuAttachLog(after));
    }

    [Fact]
    public void Overlaps_alone_misses_a_near_collision_that_the_clearance_check_rejects()
    {
        // MENU2's FollowingOverlapsIcons path called MenuRect.Overlaps (epsilon 0.5):
        // an 8-pixel gap is not an AABB cross, so the self-check passed while EXIT
        // sat on the icon row. MinIconClearance is 40 screen pixels.
        var exit = new NativeModsEntry.MenuRect { X = 0, Y = 48, Width = 320, Height = 48 };
        var icons = new NativeModsEntry.MenuRect { X = 0, Y = 0, Width = 280, Height = 40 };
        Assert.Equal(8f, exit.ClearanceAbove(icons));
        Assert.False(exit.Overlaps(icons));
        Assert.True(NativeModsEntry.FollowingTooCloseToIcons(exit, icons));
        Assert.Equal(40f, NativeModsEntry.MinIconClearance);
        Assert.Equal(0.5f, NativeModsEntry.FontSizeTolerance);
        Assert.Equal(1f, NativeModsEntry.SpacingTolerance);
    }

    [Fact]
    public void Inline_self_check_tears_down_a_near_collision_without_hard_overlap()
    {
        var enabled = NativeModsEntry.MenuColor.NativeEnabled;
        var container = new NativeModsEntry.MenuRect { X = 0, Y = 48, Width = 320, Height = 336 };
        var mods = new NativeModsEntry.MenuRect { X = 0, Y = 336, Width = 320, Height = 48 };
        var continueRow = new NativeModsEntry.MenuRect { X = 0, Y = 256, Width = 320, Height = 68 };
        var exit = new NativeModsEntry.MenuRect { X = 0, Y = 48, Width = 320, Height = 48 };
        var icons = new NativeModsEntry.MenuRect { X = 0, Y = 0, Width = 280, Height = 40 };

        Assert.False(exit.Overlaps(icons));
        Assert.False(mods.Overlaps(continueRow));

        var failure = NativeModsEntry.EvaluateInlineEntry(
            "MODS", interactable: true, enabled, enabled, mods, container, continueRow, icons,
            lastEntry: exit);
        Assert.Equal(NativeModsEntry.InlineCheckFailure.FollowingOverlapsIcons, failure);

        var after = NativeModsEntry.AfterInlineCheck(
            NativeModsEntry.DecideMainMenuEntry(
                buttonsParentPresent: true,
                NativeModsEntry.TypicalTitleSiblings,
                donorAvailable: true,
                NativeModsEntry.NativeListLayout.VerticalGroup,
                listHasRoom: true),
            failure);
        Assert.True(after.UsesCornerAnchors);
        Assert.Equal(NativeModsEntry.InlineCheckFailure.FollowingOverlapsIcons, after.FallbackReason);
    }

    [Fact]
    public void Inline_self_check_uses_exit_not_the_following_continue_row_for_icon_clearance()
    {
        // After Mods sits above CONTINUE, TryReadFollowingRect returns CONTINUE
        // (far from the icons). Checking only that sibling is how a top insert
        // would re-ship the EXIT/icon collision.
        var enabled = NativeModsEntry.MenuColor.NativeEnabled;
        var container = new NativeModsEntry.MenuRect { X = 0, Y = 40, Width = 320, Height = 400 };
        var mods = new NativeModsEntry.MenuRect { X = 0, Y = 380, Width = 320, Height = 48 };
        var continueRow = new NativeModsEntry.MenuRect { X = 0, Y = 300, Width = 320, Height = 68 };
        var exit = new NativeModsEntry.MenuRect { X = 0, Y = 40, Width = 320, Height = 48 };
        var icons = new NativeModsEntry.MenuRect { X = 0, Y = 0, Width = 280, Height = 40 };

        Assert.False(NativeModsEntry.FollowingTooCloseToIcons(continueRow, icons));
        Assert.True(NativeModsEntry.FollowingTooCloseToIcons(exit, icons));

        var failure = NativeModsEntry.EvaluateInlineEntry(
            "MODS", true, enabled, enabled, mods, container, continueRow, icons, lastEntry: exit);
        Assert.Equal(NativeModsEntry.InlineCheckFailure.FollowingOverlapsIcons, failure);
    }

    [Fact]
    public void Inline_self_check_tears_down_a_deliberately_mis_sized_entry()
    {
        var enabled = NativeModsEntry.MenuColor.NativeEnabled;
        var container = new NativeModsEntry.MenuRect { X = 0, Y = 80, Width = 320, Height = 400 };
        var mods = new NativeModsEntry.MenuRect { X = 0, Y = 400, Width = 320, Height = 48 };
        var continueRow = new NativeModsEntry.MenuRect { X = 0, Y = 320, Width = 320, Height = 68 };
        var exit = new NativeModsEntry.MenuRect { X = 0, Y = 80, Width = 320, Height = 48 };
        var icons = new NativeModsEntry.MenuRect { X = 0, Y = 0, Width = 280, Height = 40 };
        var sibling = new NativeModsEntry.MenuTypography
        {
            FontAssetName = "Nocturne Serif Regular SDF",
            FontSize = 36f,
            CharacterSpacing = 12f,
            WordSpacing = 0f,
            ScaleX = 1f,
            ScaleY = 1f,
        };
        var smaller = sibling with { FontSize = 18f, CharacterSpacing = 0f };

        Assert.False(NativeModsEntry.TypographyMatchesSibling(smaller, sibling));
        Assert.True(exit.ClearanceAbove(icons) >= NativeModsEntry.MinIconClearance);

        var failure = NativeModsEntry.EvaluateInlineEntry(
            "MODS", true, enabled, enabled, mods, container, continueRow, icons,
            smaller, sibling, lastEntry: exit);
        Assert.Equal(NativeModsEntry.InlineCheckFailure.TypographyMismatch, failure);

        var after = NativeModsEntry.AfterInlineCheck(
            NativeModsEntry.DecideMainMenuEntry(
                buttonsParentPresent: true,
                NativeModsEntry.TypicalTitleSiblings,
                donorAvailable: true,
                NativeModsEntry.NativeListLayout.VerticalGroup,
                listHasRoom: true),
            failure);
        Assert.True(after.UsesCornerAnchors);
        Assert.Equal(NativeModsEntry.InlineCheckFailure.TypographyMismatch, after.FallbackReason);
        Assert.Equal(
            "Ui: Mods main menu entry attached (corner fallback; inline entry failed: typography-mismatch).",
            NativeModsEntry.FormatMainMenuAttachLog(after));
    }

    [Fact]
    public void Inline_self_check_accepts_matching_resolved_typography_and_icon_clearance()
    {
        // Passing floor: 40 Unity screen pixels of glyph-to-icon-graphic gap,
        // not a layout-slot pair. MENU5's 40-unit slot test used these numbers
        // as canvas world corners and called them clearance; the live glyphs
        // were 3 screen pixels above the icons (see the regression below).
        var enabled = NativeModsEntry.MenuColor.NativeEnabled;
        var container = new NativeModsEntry.MenuRect { X = 0, Y = 80, Width = 320, Height = 400 };
        var mods = new NativeModsEntry.MenuRect { X = 0, Y = 400, Width = 320, Height = 48 };
        var continueRow = new NativeModsEntry.MenuRect { X = 0, Y = 320, Width = 320, Height = 68 };
        var exit = new NativeModsEntry.MenuRect { X = 0, Y = 80, Width = 320, Height = 48 };
        var icons = new NativeModsEntry.MenuRect { X = 0, Y = 0, Width = 280, Height = 40 };
        var type = new NativeModsEntry.MenuTypography
        {
            FontAssetName = "Nocturne Serif Regular SDF",
            FontSize = 36f,
            CharacterSpacing = 12f,
            WordSpacing = 0f,
            ScaleX = 1f,
            ScaleY = 1f,
        };

        Assert.Equal(40f, exit.ClearanceAbove(icons));
        Assert.False(NativeModsEntry.FollowingTooCloseToIcons(exit, icons));
        Assert.Equal(NativeModsEntry.InlineCheckFailure.None, NativeModsEntry.EvaluateInlineEntry(
            "MODS", true, enabled, enabled, mods, container, continueRow, icons,
            type, type, lastEntry: exit));
    }

    [Fact]
    public void Live_3840x1080_three_pixel_exit_to_icon_gap_fails_the_self_check()
    {
        // Coordinator scan of 32-title-gen10.png, menu column x 1130-1500,
        // screenshot Y (top-down): EXIT glyphs 975..997, icon row top 1000.
        // Converted with ScreenshotYToUnityScreen (1080 - y) — the same flip
        // WorldToScreenPoint's Y needs before it matches a PNG row. MENU5's
        // 40-unit slot pair (exit Y=80 vs icons Y=0) is not this quantity.
        const float screenH = NativeModsEntry.TitleScreenHeightPx;
        var exitGlyphs = NativeModsEntry.MenuRect.FromScreenshot(
            left: 1130f, screenshotTop: 975f, width: 370f, screenshotBottom: 997f, screenH);
        var icons = NativeModsEntry.MenuRect.FromScreenshot(
            left: 1130f, screenshotTop: 1000f, width: 370f, screenshotBottom: 1040f, screenH);

        Assert.Equal(3840f, NativeModsEntry.TitleScreenWidthPx);
        Assert.Equal(1080f - 997f, exitGlyphs.Bottom);
        Assert.Equal(1080f - 975f, exitGlyphs.Top);
        Assert.Equal(1080f - 1000f, icons.Top);
        Assert.Equal(3f, exitGlyphs.ClearanceAbove(icons));
        Assert.True(exitGlyphs.ClearanceAbove(icons) < NativeModsEntry.MinIconClearance);
        Assert.True(NativeModsEntry.FollowingTooCloseToIcons(exitGlyphs, icons));
        Assert.False(NativeModsEntry.MeetsInlineAndClearance(0, exitGlyphs, icons));
        Assert.Equal(37f, NativeModsEntry.ExtraLiftForIconClearance(3f));
        Assert.Equal(50f, NativeModsEntry.ExtraLiftForTargetClearance(3f));
        Assert.Equal(50f, NativeModsEntry.PreferredSeventhRowLift(screenH));
        Assert.Equal(50f, NativeModsEntry.SeventhRowLiftPx);

        var slotExit = new NativeModsEntry.MenuRect { X = 0, Y = 80, Width = 320, Height = 48 };
        var slotIcons = new NativeModsEntry.MenuRect { X = 0, Y = 0, Width = 280, Height = 40 };
        Assert.Equal(40f, slotExit.ClearanceAbove(slotIcons));
        Assert.False(NativeModsEntry.FollowingTooCloseToIcons(slotExit, slotIcons));

        var enabled = NativeModsEntry.MenuColor.NativeEnabled;
        var mods = NativeModsEntry.MenuRect.FromScreenshot(1130f, 400f, 370f, 448f, screenH);
        var continueRow = NativeModsEntry.MenuRect.FromScreenshot(1130f, 470f, 370f, 538f, screenH);
        var container = NativeModsEntry.MenuRect.FromScreenshot(1130f, 400f, 370f, 997f, screenH);
        var failure = NativeModsEntry.EvaluateInlineEntry(
            "MODS", true, enabled, enabled, mods, container, continueRow, icons,
            lastEntry: exitGlyphs);
        Assert.Equal(NativeModsEntry.InlineCheckFailure.FollowingOverlapsIcons, failure);

        var after = NativeModsEntry.AfterInlineCheck(
            NativeModsEntry.DecideMainMenuEntry(
                buttonsParentPresent: true,
                NativeModsEntry.TypicalTitleSiblings,
                donorAvailable: true,
                NativeModsEntry.NativeListLayout.VerticalGroup,
                listHasRoom: true),
            failure);
        Assert.True(after.UsesCornerAnchors);
        Assert.Equal(NativeModsEntry.InlineCheckFailure.FollowingOverlapsIcons, after.FallbackReason);
    }

    [Fact]
    public void List_lift_restores_icon_clearance_on_the_live_three_pixel_gap()
    {
        // Post-insert live gap 3px. The owned row costs 50px (gen-10 EXIT
        // 997 vs gen-11/12 EXIT 947), not the 74px secondary pitch. A 50px
        // ButtonsParent offset puts EXIT back at 947. The 160px
        // RISING–CONTINUE band is the slack. A tight parent with no spacing
        // cannot reach the 40px floor — the planner reports that, but it is
        // not a corner reason.
        Assert.Equal(37f, NativeModsEntry.ExtraLiftForIconClearance(3f));
        Assert.Equal(50f, NativeModsEntry.ExtraLiftForTargetClearance(3f));
        Assert.Equal(74f, NativeModsEntry.NativeTitlePitchPx);
        Assert.Equal(50f, NativeModsEntry.SeventhRowLiftPx);

        var lift = NativeModsEntry.PlanClearanceRoom(
            gapAfterInsert: 3f, roomAbove: 160f, spacing: 8f, gapCount: 6);
        Assert.True(lift.HasRoom);
        Assert.Equal(8f, lift.Spacing);
        Assert.Equal(50f, lift.ContainerLift);

        var noRoom = NativeModsEntry.PlanClearanceRoom(
            gapAfterInsert: 3f, roomAbove: 10f, spacing: 0f, gapCount: 6);
        Assert.False(noRoom.HasRoom);
        Assert.Equal(10f, noRoom.ContainerLift);

        var shrink = NativeModsEntry.PlanClearanceRoom(
            gapAfterInsert: 3f, roomAbove: 0f, spacing: 8f, gapCount: 6);
        Assert.True(shrink.HasRoom);
        Assert.Equal(0f, shrink.ContainerLift);
        Assert.Equal(0f, shrink.Spacing);

        var stack = NativeModsEntry.PlanInsertRoom(
            availableHeight: 400f, roomAbove: 160f, stackAfterInsert: 400f,
            addedRowHeight: 48f, spacing: 8f, gapCount: 6);
        var combined = NativeModsEntry.CombineRoomPlans(stack, lift);
        Assert.True(combined.HasRoom);
        Assert.Equal(50f, combined.ContainerLift);
        Assert.Equal(8f, combined.Spacing);
    }

    [Fact]
    public void View_headroom_above_a_tight_parent_is_what_the_list_lifts_into()
    {
        // Immediate parent wraps the 288px list (zero local slack). The view
        // still has the title-to-CONTINUE band — generation 11 CONTINUE sits
        // hundreds of pixels below the RISING logo at 3840×1080.
        var container = new NativeModsEntry.MenuRect { X = 0, Y = 40, Width = 320, Height = 288 };
        var tightParent = container;
        var view = new NativeModsEntry.MenuRect { X = 0, Y = 0, Width = 3840, Height = 1080 };
        float room = NativeModsEntry.RoomAboveFromAncestors(new[] { tightParent, view }, container);
        Assert.Equal(0f, NativeModsEntry.RoomAboveContainer(tightParent, container));
        Assert.Equal(1080f - 328f, room);

        var plan = NativeModsEntry.PlanClearanceRoom(
            gapAfterInsert: 3f, roomAbove: room, spacing: 8f, gapCount: 6);
        Assert.True(plan.HasRoom);
        Assert.Equal(8f, plan.Spacing);
        Assert.Equal(50f, plan.ContainerLift);
    }

    [Fact]
    public void Clearance_lift_is_not_vetoed_by_a_stack_that_cannot_fit_the_reserved_height()
    {
        // MENU6: AvailableListHeight reserved another 40px inside a container
        // that already stopped above the icons, so PlanInsertRoom said no
        // room and CombineRoomPlans cornered — even though the view had
        // hundreds of pixels above CONTINUE.
        var stack = NativeModsEntry.PlanInsertRoom(
            availableHeight: 248f, roomAbove: 0f, stackAfterInsert: 336f,
            addedRowHeight: 48f, spacing: 8f, gapCount: 6);
        Assert.False(stack.HasRoom);
        var clearance = NativeModsEntry.PlanClearanceRoom(
            gapAfterInsert: 3f, roomAbove: 400f, spacing: 8f, gapCount: 6);
        Assert.True(clearance.HasRoom);
        Assert.Equal(8f, clearance.Spacing);
        Assert.Equal(50f, clearance.ContainerLift);
        var combined = NativeModsEntry.CombineRoomPlans(stack, clearance);
        Assert.True(combined.HasRoom);
        Assert.Equal(8f, combined.Spacing);
        Assert.Equal(50f, combined.ContainerLift);
    }

    [Fact]
    public void Caption_hover_copies_the_sibling_hover_font_and_clears_on_exit_and_rebuild()
    {
        var sibling = new NativeModsEntry.MenuTypography
        {
            FontAssetName = "Nocturne Serif Regular SDF",
            FontSize = 36f,
            CharacterSpacing = 12f,
            WordSpacing = 0f,
            ScaleX = 1f,
            ScaleY = 1f,
        };
        var normal = NativeModsEntry.NormalLookFromSibling(sibling.FontAssetName, sibling);
        var highlighted = NativeModsEntry.HighlightedLookFromSibling("Nocturne Serif Hover SDF", sibling);

        var state = NativeModsEntry.CaptionHoverState.Normal;
        var look = NativeModsEntry.ResolveCaptionLook(state, normal, highlighted);
        Assert.True(NativeModsEntry.HoverLookMatchesSibling(look, normal));
        Assert.False(NativeModsEntry.HoverLookMatchesSibling(look, highlighted));

        state = NativeModsEntry.AfterPointer(state, enter: true);
        look = NativeModsEntry.ResolveCaptionLook(state, normal, highlighted);
        Assert.Equal(NativeModsEntry.CaptionHoverState.Highlighted, state);
        Assert.True(NativeModsEntry.HoverLookMatchesSibling(look, highlighted));
        Assert.Equal("Nocturne Serif Hover SDF", look.FontAssetName);
        Assert.Equal(36f, look.FontSize);
        Assert.Equal(12f, look.CharacterSpacing);

        state = NativeModsEntry.AfterPointer(state, enter: false);
        look = NativeModsEntry.ResolveCaptionLook(state, normal, highlighted);
        Assert.Equal(NativeModsEntry.CaptionHoverState.Normal, state);
        Assert.True(NativeModsEntry.HoverLookMatchesSibling(look, normal));

        state = NativeModsEntry.AfterPointer(state, enter: true);
        state = NativeModsEntry.AfterHostDestroyed(state);
        look = NativeModsEntry.ResolveCaptionLook(state, normal, highlighted);
        Assert.Equal(NativeModsEntry.CaptionHoverState.Normal, state);
        Assert.True(NativeModsEntry.HoverLookMatchesSibling(look, normal));
    }

    [Fact]
    public void Plan_insert_room_counts_continue_subtitle_height_and_lifts_the_container()
    {
        // CONTINUE 68 + five 48-unit rows + MODS 48, six 8-unit gaps.
        var rows = new[] { 68f, 48f, 48f, 48f, 48f, 48f };
        float current = NativeModsEntry.StackHeightFromRows(rows, spacing: 8f, padding: 0f);
        Assert.Equal(68f + 5 * 48f + 5 * 8f, current);
        float stackAfter = current + 48f + 8f;
        float uniform = NativeModsEntry.StackHeight(7, 48f, 8f, 0f);
        Assert.True(stackAfter > uniform);

        var lift = NativeModsEntry.PlanInsertRoom(
            availableHeight: stackAfter - 20f,
            roomAbove: 80f,
            stackAfterInsert: stackAfter,
            addedRowHeight: 48f,
            spacing: 8f,
            gapCount: 6);
        Assert.True(lift.HasRoom);
        Assert.Equal(8f, lift.Spacing);
        Assert.Equal(20f, lift.ContainerLift);

        var noRoom = NativeModsEntry.PlanInsertRoom(
            availableHeight: 200f,
            roomAbove: 0f,
            stackAfterInsert: stackAfter,
            addedRowHeight: 48f,
            spacing: 8f,
            gapCount: 6);
        Assert.False(noRoom.HasRoom);
    }

    [Fact]
    public void Generation_11_geometry_with_seven_entries_passes_inline_and_clearance()
    {
        // Coordinator 3840×1080: EXIT glyph bottom y=947, icon row top y=1000,
        // gap 53px — the native six-row gap, restored by lifting the measured
        // 50px row cost so a seventh (MODS) row sits above CONTINUE.
        // Screenshot Y (top-down) converted with ScreenshotYToUnityScreen
        // (1080 - y).
        const float screenH = NativeModsEntry.TitleScreenHeightPx;
        var exitGlyphs = NativeModsEntry.MenuRect.FromScreenshot(
            left: 1130f,
            screenshotTop: NativeModsEntry.TitleScreenExitBottomGen11Px - 22f,
            width: 370f,
            screenshotBottom: NativeModsEntry.TitleScreenExitBottomGen11Px,
            screenH);
        var icons = NativeModsEntry.MenuRect.FromScreenshot(
            left: 1130f,
            screenshotTop: NativeModsEntry.TitleScreenIconTopPx,
            width: 370f,
            screenshotBottom: NativeModsEntry.TitleScreenIconTopPx + 40f,
            screenH);
        var mods = NativeModsEntry.MenuRect.FromScreenshot(
            1130f,
            NativeModsEntry.TitleScreenModsTopPx,
            370f,
            NativeModsEntry.TitleScreenModsBottomPx,
            screenH);
        var continueRow = NativeModsEntry.MenuRect.FromScreenshot(
            1130f,
            NativeModsEntry.TitleScreenContinueTopPx,
            370f,
            NativeModsEntry.TitleScreenContinueBottomPx,
            screenH);
        var rising = NativeModsEntry.MenuRect.FromScreenshot(
            1130f,
            NativeModsEntry.TitleScreenRisingTopPx,
            370f,
            NativeModsEntry.TitleScreenRisingBottomPx,
            screenH);
        var container = NativeModsEntry.MenuRect.FromScreenshot(
            1130f, NativeModsEntry.TitleScreenModsTopPx, 370f, 947f, screenH);
        var enabled = NativeModsEntry.MenuColor.NativeEnabled;

        Assert.Equal(53f, exitGlyphs.ClearanceAbove(icons));
        Assert.Equal(NativeModsEntry.TargetIconClearance, exitGlyphs.ClearanceAbove(icons));
        Assert.Equal(54f, rising.ClearanceAbove(mods));
        Assert.True(rising.ClearanceAbove(mods) >= NativeModsEntry.MinRisingClearance);
        Assert.True(NativeModsEntry.MeetsInlineAndClearance(0, exitGlyphs, icons, mods, rising));
        Assert.False(NativeModsEntry.FollowingTooCloseToIcons(exitGlyphs, icons));
        Assert.False(NativeModsEntry.TitleEntryTooCloseToWordmark(rising, mods));
        Assert.Equal(NativeModsEntry.InlineCheckFailure.None, NativeModsEntry.EvaluateInlineEntry(
            "MODS", true, enabled, enabled, mods, container, continueRow, icons,
            lastEntry: exitGlyphs, siblingIndex: 0));
        Assert.Equal(
            "Ui: Mods main menu entry attached inline sibling=0 chrome=hand-built layout=vertical.",
            NativeModsEntry.FormatMainMenuAttachLog(NativeModsEntry.DecideMainMenuEntry(
                buttonsParentPresent: true,
                NativeModsEntry.TypicalTitleSiblings,
                donorAvailable: true,
                NativeModsEntry.NativeListLayout.VerticalGroup)));
    }

    [Fact]
    public void Generation_10_geometry_fails_inline_and_clearance()
    {
        // Coordinator 3840×1080: EXIT glyph bottom y=997, icon row top y=1000,
        // gap 3px — seventh row inline, no lift. Sibling 0 alone is not enough;
        // both conditions must hold.
        const float screenH = NativeModsEntry.TitleScreenHeightPx;
        var exitGlyphs = NativeModsEntry.MenuRect.FromScreenshot(
            left: 1130f,
            screenshotTop: NativeModsEntry.TitleScreenExitBottomGen10Px - 22f,
            width: 370f,
            screenshotBottom: NativeModsEntry.TitleScreenExitBottomGen10Px,
            screenH);
        var icons = NativeModsEntry.MenuRect.FromScreenshot(
            left: 1130f,
            screenshotTop: NativeModsEntry.TitleScreenIconTopPx,
            width: 370f,
            screenshotBottom: NativeModsEntry.TitleScreenIconTopPx + 40f,
            screenH);
        var mods = NativeModsEntry.MenuRect.FromScreenshot(1130f, 400f, 370f, 448f, screenH);
        var continueRow = NativeModsEntry.MenuRect.FromScreenshot(1130f, 470f, 370f, 538f, screenH);
        var container = NativeModsEntry.MenuRect.FromScreenshot(1130f, 400f, 370f, 997f, screenH);
        var enabled = NativeModsEntry.MenuColor.NativeEnabled;

        Assert.Equal(3f, exitGlyphs.ClearanceAbove(icons));
        Assert.False(NativeModsEntry.MeetsInlineAndClearance(0, exitGlyphs, icons));
        Assert.True(NativeModsEntry.FollowingTooCloseToIcons(exitGlyphs, icons));
        Assert.Equal(NativeModsEntry.InlineCheckFailure.FollowingOverlapsIcons, NativeModsEntry.EvaluateInlineEntry(
            "MODS", true, enabled, enabled, mods, container, continueRow, icons,
            lastEntry: exitGlyphs, siblingIndex: 0));
        Assert.False(NativeModsEntry.MeetsInlineAndClearance(1, exitGlyphs, icons));
        var rising = NativeModsEntry.MenuRect.FromScreenshot(
            1130f,
            NativeModsEntry.TitleScreenRisingTopPx,
            370f,
            NativeModsEntry.TitleScreenRisingBottomPx,
            screenH);
        // Icon collision already fails the two-sided check; sibling 0 is not enough.
        Assert.False(NativeModsEntry.MeetsInlineAndClearance(0, exitGlyphs, icons, mods, rising));
    }

    [Fact]
    public void Seven_entries_at_native_pitch_starting_one_row_higher_pass_inline_and_clearance()
    {
        // Coordinator 3840×1080 gen-12 (41-title-gen12.png), menu column
        // x1150–1460, screenshot Y (top-down):
        //   RISING 310..330, CONTINUE 470..498 (160px), PLAY 584, LOAD GAME
        //   663, CINEMATICS 737, OPTIONS 811, EXIT 925..947 (114 from OPTIONS),
        //   icons 1000. The owned row costs 50px (gen-10 EXIT 997 − gen-12
        //   EXIT 947), not the 74px secondary pitch. Insert MODS and lift
        //   ButtonsParent offsetMin/offsetMax by 50 so EXIT stays at 947
        //   and MODS sits at 384..406 (54px below RISING). Conversion:
        //   Unity screen Y = 1080 - pngY (ScreenshotYToUnityScreen) — the
        //   same flip WorldToScreenPoint needs before it matches a PNG row.
        const float screenH = NativeModsEntry.TitleScreenHeightPx;
        const float lift = NativeModsEntry.SeventhRowLiftPx;
        var mods = NativeModsEntry.MenuRect.FromScreenshot(
            1150f,
            NativeModsEntry.TitleScreenModsTopPx,
            310f,
            NativeModsEntry.TitleScreenModsBottomPx,
            screenH);
        var continueRow = NativeModsEntry.MenuRect.FromScreenshot(
            1150f,
            NativeModsEntry.TitleScreenContinueTopPx,
            310f,
            NativeModsEntry.TitleScreenContinueBottomPx,
            screenH);
        var exitGlyphs = NativeModsEntry.MenuRect.FromScreenshot(
            1150f,
            NativeModsEntry.TitleScreenExitTopPx,
            310f,
            NativeModsEntry.TitleScreenExitBottomGen11Px,
            screenH);
        var icons = NativeModsEntry.MenuRect.FromScreenshot(
            1150f,
            NativeModsEntry.TitleScreenIconTopPx,
            310f,
            NativeModsEntry.TitleScreenIconTopPx + 40f,
            screenH);
        var rising = NativeModsEntry.MenuRect.FromScreenshot(
            1150f,
            NativeModsEntry.TitleScreenRisingTopPx,
            310f,
            NativeModsEntry.TitleScreenRisingBottomPx,
            screenH);
        var container = NativeModsEntry.MenuRect.FromScreenshot(
            1150f,
            NativeModsEntry.TitleScreenModsTopPx,
            310f,
            NativeModsEntry.TitleScreenExitBottomGen11Px,
            screenH);
        var enabled = NativeModsEntry.MenuColor.NativeEnabled;

        Assert.Equal(50f, lift);
        Assert.Equal(74f, NativeModsEntry.NativeTitlePitchPx);
        Assert.Equal(48f, NativeModsEntry.MinRisingClearance);
        Assert.Equal(40f, NativeModsEntry.MinIconClearance);
        Assert.Equal(160f, NativeModsEntry.NativeRisingToContinuePx);
        Assert.Equal(384f, NativeModsEntry.TitleScreenModsTopPx);
        Assert.Equal(406f, NativeModsEntry.TitleScreenModsBottomPx);
        Assert.Equal(470f, NativeModsEntry.TitleScreenContinueTopPx);
        Assert.Equal(925f, NativeModsEntry.TitleScreenExitTopPx);
        Assert.Equal(947f, NativeModsEntry.PredictExitScreenshotY(947f, lift, lift));
        Assert.True(NativeModsEntry.TitleScreenExitBottomGen11Px <= NativeModsEntry.MaxExitScreenshotY);
        Assert.Equal(960f, NativeModsEntry.MaxExitScreenshotY);
        Assert.Equal(screenH - 947f, exitGlyphs.Bottom);
        Assert.Equal(screenH - 1000f, icons.Top);
        Assert.Equal(53f, exitGlyphs.ClearanceAbove(icons));
        Assert.Equal(54f, rising.ClearanceAbove(mods));
        Assert.True(exitGlyphs.ClearanceAbove(icons) >= NativeModsEntry.MinIconClearance);
        Assert.True(rising.ClearanceAbove(mods) >= NativeModsEntry.MinRisingClearance);
        Assert.False(NativeModsEntry.TitleEntryTooCloseToWordmark(rising, mods));
        Assert.True(NativeModsEntry.MeetsInlineAndClearance(0, exitGlyphs, icons, mods, rising));
        Assert.Equal(NativeModsEntry.InlineCheckFailure.None, NativeModsEntry.EvaluateInlineEntry(
            "MODS", true, enabled, enabled, mods, container, continueRow, icons,
            lastEntry: exitGlyphs, siblingIndex: 0));
        Assert.Equal(
            "Ui: Mods main menu entry attached inline sibling=0 chrome=hand-built layout=vertical.",
            NativeModsEntry.FormatMainMenuAttachLog(NativeModsEntry.DecideMainMenuEntry(
                buttonsParentPresent: true,
                NativeModsEntry.TypicalTitleSiblings,
                donorAvailable: true,
                NativeModsEntry.NativeListLayout.VerticalGroup)));
    }

    [Fact]
    public void A_fifty_pixel_lift_keeps_exit_at_nine_forty_seven_because_the_row_costs_fifty()
    {
        // MENU8 justified a 74px lift with 947+74−50=971 → 29px gap, assuming
        // the seventh row costs a full native pitch. Measured evidence says
        // otherwise: gen-10 (row present) EXIT 997, gen-11/12 (no row) EXIT
        // 947, so the row costs 50px. Insert 50 and lift 50 keeps EXIT at
        // 947 (53px above the icons). The discredited 74-as-insert formula
        // is recorded so it is not reinstated.
        const float screenH = NativeModsEntry.TitleScreenHeightPx;
        const float rowCost = NativeModsEntry.SeventhRowLiftPx;
        Assert.Equal(50f, rowCost);
        Assert.Equal(971f, NativeModsEntry.PredictExitScreenshotY(947f, 74f, 50f));
        Assert.Equal(947f, NativeModsEntry.PredictExitScreenshotY(947f, rowCost, rowCost));
        Assert.Equal(923f, NativeModsEntry.PredictExitScreenshotY(947f, rowCost, 74f));

        var icons = NativeModsEntry.MenuRect.FromScreenshot(
            1150f, 1000f, 310f, 1040f, screenH);
        var rising = NativeModsEntry.MenuRect.FromScreenshot(
            1150f,
            NativeModsEntry.TitleScreenRisingTopPx,
            310f,
            NativeModsEntry.TitleScreenRisingBottomPx,
            screenH);

        float kept = NativeModsEntry.PredictExitScreenshotY(947f, rowCost, rowCost);
        Assert.True(kept <= NativeModsEntry.MaxExitScreenshotY);
        var keptGlyphs = NativeModsEntry.MenuRect.FromScreenshot(
            1150f, NativeModsEntry.TitleScreenExitTopPx, 310f, kept, screenH);
        var keptMods = NativeModsEntry.MenuRect.FromScreenshot(
            1150f,
            NativeModsEntry.TitleScreenModsTopPx,
            310f,
            NativeModsEntry.TitleScreenModsBottomPx,
            screenH);
        Assert.Equal(53f, keptGlyphs.ClearanceAbove(icons));
        Assert.Equal(54f, rising.ClearanceAbove(keptMods));
        Assert.True(NativeModsEntry.MeetsInlineAndClearance(0, keptGlyphs, icons, keptMods, rising));

        // The MENU8 arithmetic (insert 74, lift 50) would put EXIT at 971,
        // 29px above the icons — below MinIconClearance. That formula is
        // what a 74px pitch premise produces; the row is not that tall.
        float shortExit = NativeModsEntry.PredictExitScreenshotY(947f, 74f, 50f);
        var shortGlyphs = NativeModsEntry.MenuRect.FromScreenshot(
            1150f, shortExit - 22f, 310f, shortExit, screenH);
        Assert.Equal(29f, shortGlyphs.ClearanceAbove(icons));
        Assert.True(NativeModsEntry.FollowingTooCloseToIcons(shortGlyphs, icons));
        Assert.False(NativeModsEntry.MeetsInlineAndClearance(0, shortGlyphs, icons, keptMods, rising));

        // Generation 13: 74px lift of a 50px row. EXIT at 923 is clear of
        // the icons (77px) but MODS sits 30px under RISING — the defect
        // being fixed. Icon clearance alone would pass; both bounds fail.
        float overLiftedExit = NativeModsEntry.PredictExitScreenshotY(947f, rowCost, 74f);
        Assert.Equal(923f, overLiftedExit);
        var overExit = NativeModsEntry.MenuRect.FromScreenshot(
            1150f, 901f, 310f, overLiftedExit, screenH);
        var overMods = NativeModsEntry.MenuRect.FromScreenshot(
            1150f, 360f, 310f, 382f, screenH);
        Assert.Equal(77f, overExit.ClearanceAbove(icons));
        Assert.False(NativeModsEntry.FollowingTooCloseToIcons(overExit, icons));
        Assert.True(NativeModsEntry.MeetsInlineAndClearance(0, overExit, icons));
        Assert.Equal(30f, rising.ClearanceAbove(overMods));
        Assert.True(NativeModsEntry.TitleEntryTooCloseToWordmark(rising, overMods));
        Assert.False(NativeModsEntry.MeetsInlineAndClearance(0, overExit, icons, overMods, rising));
    }

    [Fact]
    public void Seventh_row_lift_is_offset_translate_of_one_native_pitch_into_the_rising_gap()
    {
        Assert.Equal(160f, NativeModsEntry.TitleListHeadroom(0f, NativeModsEntry.TitleScreenHeightPx));
        Assert.Equal(200f, NativeModsEntry.TitleListHeadroom(200f, NativeModsEntry.TitleScreenHeightPx));
        Assert.Equal(50f, NativeModsEntry.PreferredSeventhRowLift(NativeModsEntry.TitleScreenHeightPx));
        Assert.Equal(50f * 720f / 1080f, NativeModsEntry.PreferredSeventhRowLift(720f));

        float nativeGap = NativeModsEntry.TitleScreenIconTopPx
            - NativeModsEntry.TitleScreenExitBottomGen11Px;
        Assert.Equal(53f, nativeGap);
        float gapAfter = nativeGap - NativeModsEntry.SeventhRowLiftPx;
        var plan = NativeModsEntry.PlanClearanceRoom(
            gapAfter,
            NativeModsEntry.TitleListHeadroom(0f, NativeModsEntry.TitleScreenHeightPx),
            spacing: 0f,
            gapCount: 6);
        Assert.True(plan.HasRoom);
        Assert.Equal(50f, plan.ContainerLift);
        Assert.Equal(0f, plan.Spacing);

        float minY = -400f;
        float maxY = 0f;
        NativeModsEntry.TranslateOffsetsUp(ref minY, ref maxY, plan.ContainerLift);
        Assert.Equal(-350f, minY);
        Assert.Equal(50f, maxY);

        var failedStack = NativeModsEntry.PlanInsertRoom(
            availableHeight: 248f, roomAbove: 0f, stackAfterInsert: 336f,
            addedRowHeight: 48f, spacing: 0f, gapCount: 6);
        Assert.False(failedStack.HasRoom);
        var combined = NativeModsEntry.CombineRoomPlans(failedStack, plan);
        Assert.True(combined.HasRoom);
        Assert.Equal(50f, combined.ContainerLift);
    }

    [Fact]
    public void Inline_self_check_requires_sibling_zero_even_when_icon_clearance_holds()
    {
        const float screenH = NativeModsEntry.TitleScreenHeightPx;
        var exitGlyphs = NativeModsEntry.MenuRect.FromScreenshot(
            left: 1130f, screenshotTop: 925f, width: 370f, screenshotBottom: 947f, screenH);
        var icons = NativeModsEntry.MenuRect.FromScreenshot(
            left: 1130f, screenshotTop: 1000f, width: 370f, screenshotBottom: 1040f, screenH);
        var mods = NativeModsEntry.MenuRect.FromScreenshot(1130f, 350f, 370f, 398f, screenH);
        var continueRow = NativeModsEntry.MenuRect.FromScreenshot(1130f, 420f, 370f, 488f, screenH);
        var container = NativeModsEntry.MenuRect.FromScreenshot(1130f, 350f, 370f, 947f, screenH);
        var enabled = NativeModsEntry.MenuColor.NativeEnabled;

        Assert.True(exitGlyphs.ClearanceAbove(icons) >= NativeModsEntry.MinIconClearance);
        Assert.False(NativeModsEntry.MeetsInlineAndClearance(1, exitGlyphs, icons));
        Assert.Equal(NativeModsEntry.InlineCheckFailure.NotAboveContinue, NativeModsEntry.EvaluateInlineEntry(
            "MODS", true, enabled, enabled, mods, container, continueRow, icons,
            lastEntry: exitGlyphs, siblingIndex: 1));
    }

    [Fact]
    public void Two_attach_calls_on_one_host_yield_one_owned_tab()
    {
        // Removing TopTabAttachKind.Reuse (DecideTopTabAttach always Create) makes
        // the second Attach increment AliveCount — that is the live double-MODS leak.
        var host = NativeModsEntry.OwnedTopTabState.Attach(NativeModsEntry.OwnedTopTabState.Empty);
        host = NativeModsEntry.OwnedTopTabState.Attach(host);
        Assert.Equal(1, host.AliveCount);
        Assert.True(host.HandleHeld);
        Assert.True(host.Tracked);
        Assert.Equal(
            NativeModsEntry.TopTabAttachKind.Reuse,
            NativeModsEntry.DecideTopTabAttach(
                ownedTabAlive: true, ownedTabTracked: true, aliveByNameCount: 1));
    }

    [Fact]
    public void Untracked_leftover_by_name_is_destroyed_before_attaching()
    {
        var leftover = NativeModsEntry.OwnedTopTabState.UntrackedLeftover(2);
        Assert.Equal(
            NativeModsEntry.TopTabAttachKind.Replace,
            NativeModsEntry.DecideTopTabAttach(
                ownedTabAlive: leftover.AliveCount > 0,
                ownedTabTracked: leftover.Tracked && leftover.HandleHeld,
                aliveByNameCount: leftover.AliveCount));
        var host = NativeModsEntry.OwnedTopTabState.Attach(leftover);
        Assert.Equal(1, host.AliveCount);
        Assert.True(host.Tracked);
        Assert.True(host.HandleHeld);
    }

    [Fact]
    public void Drop_and_Uninstall_leave_zero_owned_tabs()
    {
        // Removing Drop's zeroing leaves AliveCount standing across Uninstall —
        // that is the one leaked MODS entry per Core swap.
        var attached = NativeModsEntry.OwnedTopTabState.Attach(NativeModsEntry.OwnedTopTabState.Empty);
        Assert.Equal(1, attached.AliveCount);
        Assert.Equal(0, NativeModsEntry.OwnedTopTabState.Drop(attached).AliveCount);
        Assert.Equal(0, NativeModsEntry.OwnedTopTabState.Uninstall(attached).AliveCount);
        Assert.False(NativeModsEntry.OwnedTopTabState.Drop(attached).HandleHeld);
        Assert.False(NativeModsEntry.OwnedTopTabState.Uninstall(attached).Tracked);
    }

    [Theory]
    [InlineData(true, true, true, true, 15, false)]
    [InlineData(false, true, true, true, 0, false)]
    [InlineData(false, false, true, true, 99, false)]
    [InlineData(true, false, true, true, 15, false)]
    public void Reattach_trigger_does_not_fire_while_the_owned_tab_is_alive(
        bool menuHeld, bool buttonHeld, bool ownedTabAlive, bool ownedTabTracked, int scan, bool attach)
    {
        Assert.Equal(
            attach,
            NativeModsEntry.ShouldAttachTopTab(menuHeld, buttonHeld, ownedTabAlive, ownedTabTracked, scan));
    }

    [Fact]
    public void Reattach_rescan_still_runs_when_the_owned_tab_is_gone()
    {
        Assert.False(NativeModsEntry.ShouldAttachTopTab(
            menuHeld: true, buttonHeld: false, ownedTabAlive: false, ownedTabTracked: false, scanCount: 14));
        Assert.True(NativeModsEntry.ShouldAttachTopTab(
            menuHeld: true, buttonHeld: false, ownedTabAlive: false, ownedTabTracked: false, scanCount: 15));
        Assert.True(NativeModsEntry.ShouldAttachTopTab(
            menuHeld: false, buttonHeld: false, ownedTabAlive: false, ownedTabTracked: false, scanCount: 0));
        Assert.True(NativeModsEntry.ShouldAttachTopTab(
            menuHeld: false, buttonHeld: false, ownedTabAlive: true, ownedTabTracked: false, scanCount: 0));
        Assert.Equal(15, NativeModsEntry.TopTabRescanFrames);
    }

    [Fact]
    public void First_host_discovery_attaches_on_that_tick()
    {
        // TickBody used to assign _menu before the gate, so menuHeld was already
        // true on first discovery and this path never fired. Passing
        // menuHeldBeforeAssign: true here (the post-assignment value) makes a
        // join after Core is loaded wait for the scan threshold — and with the
        // old else-reset it never attached at all.
        int scan = 0;
        Assert.True(NativeModsEntry.AdvanceTopTabAttachScan(
            ref scan,
            menuHeldBeforeAssign: false,
            buttonHeld: false,
            ownedTabAlive: false,
            ownedTabTracked: false));
        Assert.Equal(0, scan);
    }

    [Fact]
    public void Scan_reaches_the_threshold_when_the_immediate_path_is_not_taken()
    {
        // TickBody's else _scan = 0 on the gate-false path kept scan at 1 forever.
        // Zeroing scan whenever attach is false makes ticks 1..15 never reach
        // TopTabRescanFrames, so a held host with no button never attaches.
        int scan = 0;
        for (int tick = 1; tick <= NativeModsEntry.TopTabRescanFrames; tick++)
        {
            bool attach = NativeModsEntry.AdvanceTopTabAttachScan(
                ref scan,
                menuHeldBeforeAssign: true,
                buttonHeld: false,
                ownedTabAlive: false,
                ownedTabTracked: false);
            if (tick < NativeModsEntry.TopTabRescanFrames)
            {
                Assert.False(attach);
                Assert.Equal(tick, scan);
            }
            else
            {
                Assert.True(attach);
                Assert.Equal(0, scan);
            }
        }
    }

    [Fact]
    public void Waiting_scan_resets_when_the_owned_tab_is_alive_and_tracked()
    {
        int scan = 7;
        Assert.False(NativeModsEntry.AdvanceTopTabAttachScan(
            ref scan,
            menuHeldBeforeAssign: true,
            buttonHeld: true,
            ownedTabAlive: true,
            ownedTabTracked: true));
        Assert.Equal(0, scan);
    }

    [Fact]
    public void Host_wrapper_change_does_not_drop_while_the_owned_tab_is_alive()
    {
        Assert.False(NativeModsEntry.ShouldDropTopTabHost(
            menuHeld: true, foundAlive: true, sameHost: false, ownedTabAliveUnderFound: true));
        Assert.False(NativeModsEntry.ShouldDropTopTabHost(
            menuHeld: true, foundAlive: true, sameHost: true, ownedTabAliveUnderFound: true));
        Assert.True(NativeModsEntry.ShouldDropTopTabHost(
            menuHeld: true, foundAlive: false, sameHost: false, ownedTabAliveUnderFound: false));
        Assert.True(NativeModsEntry.ShouldDropTopTabHost(
            menuHeld: true, foundAlive: true, sameHost: false, ownedTabAliveUnderFound: false));
        Assert.False(NativeModsEntry.ShouldDropTopTabHost(
            menuHeld: false, foundAlive: true, sameHost: false, ownedTabAliveUnderFound: false));
    }

    [Fact]
    public void TickBody_uses_the_owned_tab_guard_instead_of_the_menu_null_rescan()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        string tick = SourcePin.MethodBody(src, "private static void TickBody()");
        SourcePin.AssertStatement(tick, "if (AdvanceTopTabAttachScan(");
        SourcePin.AssertStatement(tick, "bool menuHeldBeforeHostAssign = _menu != null;");
        SourcePin.AssertStatement(tick, "if (ShouldDropTopTabHost(");
        SourcePin.AssertUncommented(tick, "TryReapplyOwnedPainterLayoutOnce(");
        SourcePin.AssertUncommented(tick, "menuHeldBeforeAssign: menuHeldBeforeHostAssign");
        SourcePin.AssertNoStatementContaining(tick, "_menu == null || (_button == null && ++_scan >= 15)");
        SourcePin.AssertNoStatementContaining(tick, "ShouldAttachTopTab(");
        string drop = SourcePin.MethodBody(src, "private static void Drop()");
        SourcePin.AssertStatement(drop, "DestroyOwnedTopTabsIn(group);");
        SourcePin.AssertStatement(drop, "DestroyOwnedTopTabsByName();");
        SourcePin.AssertStatement(drop, "_scan = 0;");
        string attach = SourcePin.MethodBody(src, "private static void Attach(Transform parent, TextMeshProUGUI donor, bool tab)");
        SourcePin.AssertStatement(attach, "RebindOwnedTopTab(ours, donor);");
        SourcePin.AssertNoStatementContaining(attach, "new GameObject(tab ? \"progenitor-ui-core-mods-tab\"");
        Assert.Equal("progenitor-ui-core-mods-tab", NativeModsEntry.TopTabObjectName);
        SourcePin.AssertStatement(tick, "if (ShouldConsultPlayBackdropCache(");
        SourcePin.AssertUncommented(tick, "TryResolveFullscreenMenu(");
        string count = SourcePin.MethodBody(src, "private static int CountOwnedTopTabsIn(Transform? parent)");
        SourcePin.AssertUncommented(count, "OwnedTopTabCache.Resolve");
        SourcePin.AssertNoStatementContaining(count, "GetComponentsInChildren");
    }

    [Fact]
    public void ModsMenu_owned_top_tab_count_is_the_facade_for_rects()
    {
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        Assert.Equal(0, NativeModsEntry.OwnedTopTabCount);
        Assert.Equal(0, ModsMenu.OwnedTopTabCount);
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        Assert.Equal(1, NativeModsEntry.OwnedTopTabCount);
        Assert.Equal(1, ModsMenu.OwnedTopTabCount);
        NativeModsEntry.SetOwnedTopTabCountForTests(3);
        Assert.Equal(3, ModsMenu.OwnedTopTabCount);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        Assert.Equal(0, ModsMenu.OwnedTopTabCount);
    }

    [Fact]
    public void Close_drops_the_curtain_the_same_frame_on_each_path()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        Assert.True(NativeModsEntry.DescribeOwned().Curtain);
        Assert.True(NativeModsEntry.TestCurtainRaycast);
        ModsMenu.Close();
        Assert.False(ModsMenu.IsOpen);
        Assert.False(NativeModsEntry.DescribeOwned().Curtain);
        Assert.False(NativeModsEntry.TestCurtainRaycast);
        Assert.False(NativeModsEntry.TestCurtainActive);
        Assert.Equal(
            NativeModsEntry.FormatCurtainDroppedLog(NativeModsEntry.CurtainDropClose),
            NativeModsEntry.TestCurtainDroppedLog);

        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.InstallLifecycleWatch();
        try
        {
            Handshake.ResetTables();
            Handshake.NotifyClientConnectionEnded();
            Assert.False(NativeModsEntry.DescribeOwned().Curtain);
            Assert.Equal(
                NativeModsEntry.FormatCurtainDroppedLog(NativeModsEntry.CurtainDropLifecycle),
                NativeModsEntry.TestCurtainDroppedLog);
        }
        finally
        {
            NativeModsEntry.UninstallLifecycleWatch();
            Handshake.ResetTables();
        }

        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("System", headerActive: false);
        NativeModsEntry.HoldHostedViewIfTopTab();
        NativeModsEntry.RestoreHostedView();
        Assert.False(NativeModsEntry.DescribeOwned().Curtain);
        Assert.Equal(
            NativeModsEntry.FormatCurtainDroppedLog(NativeModsEntry.CurtainDropRelease),
            NativeModsEntry.TestCurtainDroppedLog);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        // Revert: Close waiting for Tick UpdateHost:4420 leaves the gen-34 blocker.
    }

    [Fact]
    public void Lingering_backdrop_self_heals_on_the_next_tick()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        Assert.False(ModsMenu.IsOpen);
        Assert.True(NativeModsEntry.DescribeOwned().Curtain);
        NativeModsEntry.TickCurtainForTests();
        Assert.True(NativeModsEntry.DescribeOwned().Curtain);
        NativeModsEntry.TickCurtainForTests();
        Assert.False(NativeModsEntry.DescribeOwned().Curtain);
        Assert.False(NativeModsEntry.TestCurtainRaycast);
        Assert.Equal(NativeModsEntry.FormatCurtainSelfHealLog(), NativeModsEntry.TestCurtainSelfHealLog);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
        // Revert: TickBody returning at 2892 without UpdateHost never DropBackdrop.
    }

    [Fact]
    public void Release_throwing_still_drops_the_curtain_and_next_generation_sweeps_leftovers()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetOuterTabCaptureForTests("System", headerActive: false);
        NativeModsEntry.HoldHostedViewIfTopTab();
        NativeModsEntry.SetReleaseFaultForTests(new InvalidOperationException("release-threw"));
        NativeModsEntry.RestoreHostedView();
        Assert.Equal("release-threw", NativeModsEntry.TestReleaseWarning);
        Assert.False(NativeModsEntry.DescribeOwned().Curtain);
        Assert.False(NativeModsEntry.TestCurtainRaycast);
        Assert.Equal(
            NativeModsEntry.FormatCurtainDroppedLog(NativeModsEntry.CurtainDropRelease),
            NativeModsEntry.TestCurtainDroppedLog);

        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        NativeModsEntry.SetReleaseFaultForTests(new InvalidOperationException("uninstall-threw"));
        NativeModsEntry.Uninstall();
        Assert.False(NativeModsEntry.DescribeOwned().Curtain);
        Assert.Equal(
            NativeModsEntry.FormatCurtainDroppedLog(NativeModsEntry.CurtainDropRelease),
            NativeModsEntry.TestCurtainDroppedLog);

        var leftover = new IntPtr(0x51BE);
        Assert.True(NativeModsEntry.ShouldDestroyStaleOwnedRoot(leftover, IntPtr.Zero));
        Assert.False(NativeModsEntry.ShouldDestroyStaleOwnedRoot(leftover, leftover));
        Assert.Equal(1, NativeModsEntry.SweepStaleOwnedRootsForTests(
            leftover, IntPtr.Zero, NativeModsEntry.BackdropObjectName));
        Assert.Equal(
            NativeModsEntry.FormatStaleOwnedRootDestroyedLog(NativeModsEntry.BackdropObjectName),
            NativeModsEntry.TestStaleOwnedRootLog);
        var names = NativeModsEntry.EnumerateOwnedRootNames();
        Assert.Contains(NativeModsEntry.BackdropObjectName, names);
        Assert.Contains(NativeModsEntry.MainMenuButtonName, names);
        Assert.Contains(NativeModsEntry.TopTabObjectName, names);
        Assert.Contains(NativeModsEntry.PlayBackdropHold.OwnedObjectName, names);
        Assert.Contains(NativeModsEntry.PlayBackdropHold.TemplateObjectName, names);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);

        string entry = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        SourcePin.AssertUncommented(entry, "DropCurtain(CurtainDropRelease);");
        SourcePin.AssertUncommented(entry, "SilenceOwnedRaycast(");
        string destroy = SourcePin.MethodBody(entry, "private static void DestroyOwned(GameObject? go)");
        SourcePin.AssertUncommented(destroy, "SilenceOwnedRaycast(");
        string tick = SourcePin.MethodBody(entry, "private static void TickBody()");
        SourcePin.AssertUncommented(tick, "TickCurtainLifetime(");
        SourcePin.AssertUncommented(tick, "SweepStaleOwnedRootsIfHostChanged(");
        string install = SourcePin.MethodBody(entry, "internal static void Install(Action<string> open)");
        SourcePin.AssertUncommented(install, "SweepStaleOwnedRoots(");
        string close = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        SourcePin.AssertUncommented(close, "DropCurtainOnWorkspaceClose(");
        // Revert: Uninstall release-then-Drop without finally leaks the native GO across Core swap.
    }

    [Fact]
    public void Curtain_readback_shape_has_present_and_raycast()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        var status = NativeModsEntry.DescribeCurtainStatusLive();
        Assert.Equal(true, status["present"]);
        Assert.Equal(true, status["raycastTarget"]);
        var rect = NativeModsEntry.DescribeCurtainRectLive();
        foreach (var key in new[] { "present", "active", "raycastTarget", "alpha", "sortingOrder", "x", "y", "width", "height" })
            Assert.True(rect.ContainsKey(key), key);
        Assert.Equal(true, rect["present"]);
        Assert.Equal(true, rect["raycastTarget"]);
        Assert.Equal(true, rect["active"]);
        NativeModsEntry.DropCurtain(NativeModsEntry.CurtainDropClose);
        status = NativeModsEntry.DescribeCurtainStatusLive();
        Assert.Equal(false, status["present"]);
        Assert.Equal(false, status["raycastTarget"]);
        bool active = true, raycast = true;
        NativeModsEntry.SilenceCurtainState(ref active, ref raycast);
        Assert.False(active);
        Assert.False(raycast);
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
    }

    /// <summary>
    /// TITLEHL1 — the user's "Mods no longer highlights on the title page", and the constrained
    /// alternative NATIVEHL1 recorded as an open blocker rather than guessing at it.
    ///
    /// <para>NATIVEHL1 measured the title caption as "ALREADY pure white at rest, so there is no
    /// color headroom" and concluded the highlight had to be a glow child. TITLEHL1 found that
    /// premise false — the caption read <c>rgba [0.604, 0.604, 0.604, 1]</c> — and made the
    /// highlight a swap between the fullscreen inner-tab palette's two ends.</para>
    ///
    /// <para><b>TITLEREST corrects that, and the correction is a pixel measurement, not an
    /// argument.</b> 0.604 gray is the <i>inner tab</i> unselected color; the title list is a
    /// different control whose six native rows all rest at the same near-white. Captured on this
    /// build at 3840x1080: with the pointer away, the brightest glyph pixel of PLAY / LOAD GAME /
    /// CINEMATICS / OPTIONS / EXIT is rgb(186,217,222) while MODS was rgb(154,154,154) — exactly
    /// 0.604 x 255 — so the owned row was the one visibly dim item in the list and had no headroom
    /// left to highlight <i>into</i>. With a real pointer on a native row, that row goes to
    /// rgb(255,255,255) inside a dark red halo and its band mean more than doubles
    /// (PLAY 13.486 → 28.914).</para>
    ///
    /// <para>So the title site rests at <c>MenuColor.NativeTitleRest</c>, highlights to white, and
    /// toggles copies of the donor's own hover graphic. <c>MenuButtonWidget</c> is inside
    /// <c>#if !PROGENITOR_TESTS</c>, so the decisions are pinned as source. What removal would
    /// break, in order: no highlight at all; an entry left stuck bright after the pointer leaves
    /// (the unhovered end must be the resting color, NOT the constructor color —
    /// <c>FinishAttach</c> constructs this widget while the caption is still white); a selected
    /// entry dimmed by a pointer exit; and the in-game MODS tab losing the inner-tab palette that
    /// is still correct <i>there</i>.</para>
    /// </summary>
    [Fact]
    public void The_title_entry_highlights_on_hover_and_returns_to_the_resting_grey()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        string hovered = SourcePin.MethodBody(source, "internal void SetHovered(bool hovered)");

        // Both ends, through the site-aware chooser rather than the inner-tab pair directly.
        SourcePin.AssertUncommented(hovered, "_text.color = CaptionColor(hovered || _selected);");
        SourcePin.AssertUncommented(hovered, "ShowTitleGlow(hovered || _selected);");
        // The trap this fix walked into once: restoring whatever the caption was at construction.
        SourcePin.AssertNoStatementContaining(hovered, "_text.color = _normalColor");
        SourcePin.AssertUncommented(hovered, "_hovered = hovered;");

        // The chooser keeps the two sites apart: the title list rests at the native title color
        // and lights to white; the in-game MODS tab keeps the inner-tab pair unchanged.
        SourcePin.AssertUncommented(source, "private Color CaptionColor(bool lit) => _titleEntry");
        SourcePin.AssertUncommented(source, "? (lit ? TitleHighlight : _titleRest)");
        SourcePin.AssertUncommented(
            source, ": (lit ? NativeModsEntry.OwnedSelectedTmpColor : NativeModsEntry.OwnedUnselectedTmpColor);");

        // A deselect under the pointer keeps the hover look — and the caption color is written
        // AFTER the painters are hidden, because `SetOwnedHighlightVisible` writes that color
        // itself and this deselect runs every frame the title screen is up. Getting this order
        // wrong is invisible in a screenshot and was only caught by the readback.
        string selected = SourcePin.MethodBody(source, "internal void SetSelected(bool selected)");
        SourcePin.AssertUncommented(selected, "_selected = selected;");
        SourcePin.AssertUncommented(selected, "_text.color = CaptionColor(_hovered);");
        // The per-frame deselect keeps the hover font while the pointer is on the entry; writing
        // the standard font here reverted the native red hover glyphs every frame.
        SourcePin.AssertUncommented(selected, "ApplyCaptionFont(_hovered);");
        SourcePin.AssertNoStatementContaining(selected, "_text.font = _standardFont");
        Assert.True(
            selected.IndexOf("SetOwnedHighlightVisible(_go, selected: false);", StringComparison.Ordinal)
                < selected.IndexOf("_text.color = CaptionColor(_hovered);", StringComparison.Ordinal),
            "the painters must be hidden before the caption color is decided, or the per-frame "
            + "deselect paints over the hover every frame");

        // And the state is readable, because a luminance capture cannot distinguish "the highlight
        // is broken" from "the pointer never arrived" without one.
        SourcePin.AssertUncommented(source, "internal bool Hovered => _hovered;");

        // And the containment tick compares against what the widget is SHOWING, not against its own
        // mirror — two hover sources with separate state is how a dropped PointerExit left the
        // entry lit for good (gen 9: isHovered true with the pointer at the bottom of the screen).
        string tick = SourcePin.MethodBody(source, "private static void TickOwnedEntryHover()");
        SourcePin.AssertUncommented(tick, "showing = widget.Hovered;");
        SourcePin.AssertUncommented(tick, "bool changed = inside != showing;");
        SourcePin.AssertNoStatementContaining(tick, "bool changed = inside != _entryHovered;");
        SourcePin.AssertUncommented(
            SourcePin.MethodBody(source, "private static Dictionary<string, object?>? DescribeOwnedModsTab(bool holdHeld, bool holdCleared)"),
            "isHovered = _widget?.Hovered;");

        // A PRECONDITION, not a pin of this change: neither lane touches these two palette
        // constants, but the in-game tab's highlight is "swap between them", so if they were ever
        // made equal the fix above would keep passing its source pins and draw nothing.
        Assert.NotEqual(
            (NativeTabSelection.NativeSelectedTmpR, NativeTabSelection.NativeSelectedTmpG, NativeTabSelection.NativeSelectedTmpB),
            (NativeTabSelection.NativeUnselectedTmpR, NativeTabSelection.NativeUnselectedTmpG, NativeTabSelection.NativeUnselectedTmpB));
        Assert.True(
            NativeTabSelection.NativeSelectedTmpR > NativeTabSelection.NativeUnselectedTmpR,
            "the hovered end has to be the brighter one");

        // TITLEREST — the same precondition for the title site, where the two ends are the measured
        // native resting color and white. The resting color must be dimmer than the highlight (or
        // there is no highlight) and must NOT be the inner-tab gray (or the row is the one dim item
        // in the list again, which is the defect the user reported).
        float restLum = 0.299f * NativeModsEntry.MenuColor.NativeTitleRest.R
            + 0.587f * NativeModsEntry.MenuColor.NativeTitleRest.G
            + 0.114f * NativeModsEntry.MenuColor.NativeTitleRest.B;
        Assert.True(restLum < 1f, "the title rest color has to leave headroom to highlight into");
        Assert.True(
            restLum > 0.299f * NativeTabSelection.NativeUnselectedTmpR
                + 0.587f * NativeTabSelection.NativeUnselectedTmpG
                + 0.114f * NativeTabSelection.NativeUnselectedTmpB,
            "the title list rests far brighter than an unselected inner tab");
    }

    private sealed class CountingDisposable : IDisposable
    {
        internal int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
}
