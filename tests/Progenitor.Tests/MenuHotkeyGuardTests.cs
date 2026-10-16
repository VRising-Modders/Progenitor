using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job KEY2. Typing into either Progenitor search field must not also drive the fullscreen menu's
/// letter shortcuts: the user typed <c>p</c> into the Mods search and the menu switched to Social,
/// because <c>P</c> is the vanilla <c>ClanMenu</c> shortcut (client metadata,
/// value 23) and <c>ProjectM.MenuInputSystem</c> acted on it.
///
/// <para>
/// The block is a flag the already-registered input context's per-frame mask reads, so these tests
/// can cover the whole of it without a client: the action set, the focus rule, and every release
/// path. What is <i>not</i> reachable from here — that the native mask loop and the runtime's focus
/// writers actually read this type — is pinned over source text, the same way
/// <c>UiInputMaskTests</c> pins the close-action loop, because a guard nothing calls is the exact
/// regression that would leave the reported defect in place with every behavioral test still
/// green.
/// </para>
/// </summary>
public class MenuHotkeyGuardTests : IDisposable
{
    /// <summary>
    /// The guard is process-static, so a test that leaves it set would block the menu hotkeys for
    /// whatever runs next. Released after every case in this class.
    /// </summary>
    public void Dispose()
    {
        MenuHotkeyGuard.Release();

        // Job KEY3: the mask counters and the registration mirror are process-static too, so a case
        // that left masked>0 or registered=true would leak into whatever runs next — including the
        // readback-shape cases below, which assert on exact values.
        MenuHotkeyGuard.ResetCountersForTests();
    }

    /// <summary>
    /// The reported key, named on its own. <c>ClanMenu = 23</c> is
    /// the whole of the user's report and the one value a future edit must not quietly drop while
    /// keeping the list "about the right length".
    /// </summary>
    [Fact]
    public void The_social_hotkey_the_user_reported_is_in_the_blocked_set()
    {
        Assert.Contains(23, MenuHotkeyGuard.BlockedActions());
    }

    /// <summary>
    /// The full set, pinned by value against the client metadata each one was read from
    /// (the menu-open block and the
    /// in-menu tab cycling). Every other letter in the 18-26 block is the same defect waiting for a
    /// different keystroke, so the assertion is on the exact contents rather than on membership.
    /// </summary>
    [Fact]
    public void The_blocked_set_is_the_menu_open_and_tab_cycling_actions_from_the_dump()
    {
        Assert.Equal(
            new[] { 1, 18, 20, 21, 22, 23, 24, 25, 26, 1006, 1007, 1008, 1009 },
            MenuHotkeyGuard.BlockedActions());
    }

    [Fact]
    public void Typing_guards_container_interact_until_focus_is_released()
    {
        MenuHotkeyGuard.SetFocusedField(MenuHotkeyGuard.InstallSearch);
        Assert.True(MenuHotkeyGuard.HotkeysBlocked);
        Assert.Contains(1, MenuHotkeyGuard.BlockedActions());
        MenuHotkeyGuard.SetFocusedField(null);
        Assert.False(MenuHotkeyGuard.HotkeysBlocked);
    }

    /// <summary>
    /// And nothing else. This is the "never swallow keys globally" half of the brief as an
    /// assertion: movement, abilities and the close key are untouched, so the guard cannot grow
    /// into a blanket key block one well-meaning addition at a time. <c>Back = 17</c>
    /// is singled out because it is adjacent to the block in the enum and
    /// is already taken, in every scope, by <c>InputMaskPlan.BlockedCloseActions</c> — taking it
    /// here too would make two owners of the panel's close gesture.
    /// </summary>
    [Fact]
    public void The_blocked_set_takes_neither_the_close_key_nor_gameplay_actions()
    {
        int[] blocked = MenuHotkeyGuard.BlockedActions();
        Assert.DoesNotContain(ButtonInputActions.Back, blocked);
        Assert.DoesNotContain(ButtonInputActions.MenuClose, blocked);
        Assert.DoesNotContain(ButtonInputActions.RotateCamera, blocked);
        Assert.Equal(blocked.Length, blocked.Distinct().Count());
    }

    /// <summary>
    /// The array the mask iterates is the live one, not a copy per frame. The native callback runs
    /// once per frame per registered context and must not allocate there — the same contract
    /// <c>InputMaskPlan.BlockedCloseActions</c> documents.
    /// </summary>
    [Fact]
    public void The_blocked_set_is_handed_out_without_copying()
    {
        Assert.Same(MenuHotkeyGuard.BlockedActions(), MenuHotkeyGuard.BlockedActions());
    }

    /// <summary>
    /// Focus on either field turns the block on and names the field. Both fields, because the
    /// Install toolbar input is armed through the same call site as the sidebar search and the
    /// report covered both.
    /// </summary>
    [Theory]
    [InlineData(MenuHotkeyGuard.ModsSearch)]
    [InlineData(MenuHotkeyGuard.InstallSearch)]
    [InlineData(MenuHotkeyGuard.BuildSearch)]
    public void Focus_on_a_field_blocks_the_hotkeys_and_names_it(string field)
    {
        MenuHotkeyGuard.SetFocusedField(field);

        Assert.True(MenuHotkeyGuard.HotkeysBlocked);
        Assert.Equal(field, MenuHotkeyGuard.FocusedField);
    }

    /// <summary>
    /// Every way focus can end. The pump recomputes focus each frame and pushes the answer, so
    /// Escape, a click elsewhere, the field being destroyed by a rebuild and the tab changing all
    /// arrive here as the same <c>null</c>; <c>Release</c> is what the pump unsubscribing, the
    /// workspace closing and <c>GameInputBlocker</c>'s teardown call. <b>What removal breaks:</b>
    /// lose any of these and the menu's tab keys stay dead after the player clicks away from the
    /// field — the opposite defect, and a worse one, because there is then no field to click out
    /// of to get the menu back.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(MenuHotkeyGuard.NoField)]
    [InlineData("some-field-a-later-job-added")]
    public void Focus_off_by_any_path_releases_the_hotkeys(string? field)
    {
        MenuHotkeyGuard.SetFocusedField(MenuHotkeyGuard.ModsSearch);
        Assert.True(MenuHotkeyGuard.HotkeysBlocked);

        MenuHotkeyGuard.SetFocusedField(field);

        Assert.False(MenuHotkeyGuard.HotkeysBlocked);
        Assert.Equal(MenuHotkeyGuard.NoField, MenuHotkeyGuard.FocusedField);
    }

    /// <summary>
    /// <c>Release</c> is the named teardown path and reaches the same state. Called from the
    /// shown-input pump's unsubscribe, from <c>ReleaseInputPumps</c> (workspace close, Core
    /// unload) and from <c>GameInputBlocker.UninstallMask</c>, so the flag cannot outlive the Core
    /// generation that set it.
    /// </summary>
    [Fact]
    public void Release_clears_the_block()
    {
        MenuHotkeyGuard.SetFocusedField(MenuHotkeyGuard.InstallSearch);
        MenuHotkeyGuard.Release();

        Assert.False(MenuHotkeyGuard.HotkeysBlocked);
        Assert.Equal(MenuHotkeyGuard.NoField, MenuHotkeyGuard.FocusedField);
    }

    /// <summary>
    /// An unrecognised id fails <i>open</i>. A later job that adds a third field and forgets to
    /// teach the guard about it gets today's defect back — the key types and also acts — rather
    /// than a menu whose tab keys are permanently dead because an id nobody recognizes was treated
    /// as "something is focused".
    /// </summary>
    [Fact]
    public void An_unknown_field_id_leaves_the_menu_alone()
    {
        MenuHotkeyGuard.SetFocusedField("settings-text-row");

        Assert.False(MenuHotkeyGuard.HotkeysBlocked);
    }

    // ---- readback ---------------------------------------------------------------------------

    /// <summary>
    /// <c>mods_ui status.focus</c>: <c>{field, hotkeysBlocked, blockedActions[]}</c> with the
    /// actions listed while the block is on.
    /// </summary>
    [Fact]
    public void The_focus_readback_reports_the_field_and_the_actions_being_taken()
    {
        var row = ModsMenu.FocusStatus(MenuHotkeyGuard.ModsSearch, blocked: true);

        Assert.Equal(MenuHotkeyGuard.ModsSearch, row["field"]);
        Assert.Equal(true, row["hotkeysBlocked"]);
        var actions = Assert.IsType<List<object?>>(row["blockedActions"]);
        Assert.Equal(MenuHotkeyGuard.BlockedActions().Length, actions.Count);
        Assert.Contains(23, actions.Cast<object?>());
    }

    /// <summary>
    /// Unblocked, the row lists nothing: <c>blockedActions</c> says what is being taken, not what
    /// would be taken if something were focused. A missing field reads as <c>none</c> rather than
    /// as an absent key, so a caller never has to handle two shapes.
    /// </summary>
    [Fact]
    public void The_focus_readback_lists_no_actions_while_nothing_is_focused()
    {
        var row = ModsMenu.FocusStatus(null, blocked: false);

        Assert.Equal(MenuHotkeyGuard.NoField, row["field"]);
        Assert.Equal(false, row["hotkeysBlocked"]);
        Assert.Empty(Assert.IsType<List<object?>>(row["blockedActions"]));
    }

    /// <summary>
    /// The readback's two halves come from two places on purpose, so it can report the
    /// disagreement that is the defect: a field focused with the hotkeys still live. A row that
    /// derived <c>hotkeysBlocked</c> from <c>field</c> could never say so.
    /// </summary>
    [Fact]
    public void The_focus_readback_can_report_a_focused_field_whose_block_is_not_on()
    {
        var row = ModsMenu.FocusStatus(MenuHotkeyGuard.InstallSearch, blocked: false);

        Assert.Equal(MenuHotkeyGuard.InstallSearch, row["field"]);
        Assert.Equal(false, row["hotkeysBlocked"]);
    }

    /// <summary>
    /// The per-row <c>focused</c> keys, pinned over source text: <c>ModsUiOps</c>'s live half is
    /// behind <c>#if !PROGENITOR_TESTS</c> (it needs the game), so the readback wiring cannot be
    /// called from here. The sidebar search rect row and every <c>input</c> cell reachable in a
    /// <c>widget_read</c> row are stamped against their own field id — not against "anything is
    /// focused", because with both fields armed the sidebar being focused must not make the
    /// Install row read true. <b>What removal breaks:</b> a caller watching a single field has to
    /// fall back to the one global <c>status.focus.field</c> and cannot tell two armed fields apart.
    /// </summary>
    [Fact]
    public void Both_input_rows_are_stamped_with_their_own_focus()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Progenitor.Dev", "Ops", "ModsUiOps.cs"));

        SourcePin.AssertStatementExactly(
            source, "row[\"focused\"] = FocusedFieldIs(ModsMenu.MenuHotkeyModsSearch);");
        SourcePin.AssertStatementExactly(
            source, "StampInputFocus(substituted[i], ModsMenu.MenuHotkeyInstallSearch);");
        SourcePin.AssertStatementExactly(source, "dict[\"focused\"] = FocusedFieldIs(expectedField);");

        // status.focus itself, on both exits of ReadStatus - the good one and the caught-throw one.
        SourcePin.AssertStatementExactly(source, "try { target[\"focus\"] = ModsMenu.DescribeFocusStatus(); }");
        Assert.Equal(2, CountCalls(source, "ModsUiResult.WithFocus("));
    }

    private static int CountCalls(string source, string fragment) => source
        .Split('\n')
        .Select(l => l.Trim())
        .Where(l => !l.StartsWith("//", StringComparison.Ordinal))
        .Count(l => l.Contains(fragment, StringComparison.Ordinal));

    // ---- the wiring, over source text ---------------------------------------------------------

    /// <summary>
    /// The native mask reads this guard, blocks <b>both</b> button lists, and iterates the array
    /// rather than respelling it. Both lists matters: a menu shortcut is a press, so
    /// <c>BlockedNewButtonInputActions</c> is the list it is read
    /// from, and whether <c>BlockAllKeys</c> fills that one cannot be read offline. <b>What removal
    /// breaks:</b> drop the <c>BlockKeyDown</c> line and every test in this file still passes while
    /// <c>p</c> opens Social again.
    /// </summary>
    [Fact]
    public void The_native_mask_blocks_the_guard_actions_in_both_button_lists()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Progenitor.Core", "Ui", "GameInputBlocker.cs"));

        // Photo mode's Exclusive scope takes the same set whether or not a field is focused.
        SourcePin.AssertStatementExactly(source, "if (MenuHotkeyGuard.HotkeysBlocked || plan.BlockMenuHotkeys)");
        SourcePin.AssertStatementExactly(source, "int[] hotkeys = MenuHotkeyGuard.BlockedActions();");
        SourcePin.AssertStatementExactly(source, "state.BlockKey((ButtonInputAction)hotkeys[i]);");
        SourcePin.AssertStatementExactly(source, "state.BlockKeyDown((ButtonInputAction)hotkeys[i]);");

        // And the teardown that no other path can cover: a Core generation going away with a field
        // focused must not leave the next one a menu whose tab keys are dead.
        SourcePin.AssertStatementExactly(source, "MenuHotkeyGuard.Release();");
    }

    /// <summary>
    /// The runtime pushes focus at the guard from the shown-input pump — the one writer that runs
    /// every frame, and therefore the one that covers Escape, a click elsewhere and a rebuild that
    /// destroyed the field — and releases it on both teardown paths. <b>What removal breaks:</b>
    /// without the pump line the block is only ever as fresh as the last click, so Escape leaves
    /// the hotkeys taken; without the teardown lines the workspace can close with them still taken.
    /// </summary>
    [Fact]
    public void The_runtime_pushes_focus_at_the_guard_every_frame_and_releases_it_on_teardown()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"));

        SourcePin.AssertStatementExactly(source, "SyncMenuHotkeyGuard(field);");
        SourcePin.AssertStatementExactly(
            source, "try { MenuHotkeyGuard.SetFocusedField(FocusedFieldId(field)); }");

        int releases = source
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal))
            .Count(l => l.Contains("MenuHotkeyGuard.Release();", StringComparison.Ordinal));

        // The pump unsubscribe, ReleaseInputPumps, and (BUILDSEARCH1) the last panel hiding while an
        // external field keeps the pump armed. Exact, not "at least one": each covers a path the
        // others do not, and a refactor that collapses them is a claim worth re-checking.
        Assert.Equal(3, releases);
    }

    /// <summary>
    /// The Install toolbar input arms exactly like the sidebar search: one
    /// <c>ArmTextField</c> call from the <c>CellKind.Input</c> build, which is what puts it in the
    /// armed list that <c>FocusedArmedField</c> — and therefore this guard, the owned caret and the
    /// typed-key pump — works from. <b>What removal breaks:</b> the Install search takes text
    /// through TMP with no owned focus, so <c>status.focus</c> reads <c>none</c> while the player
    /// types and every letter also drives the menu.
    /// </summary>
    [Fact]
    public void The_install_toolbar_input_is_armed_like_the_sidebar_search()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"));

        SourcePin.AssertStatementExactly(source, "ArmTextField(host, field);");
        SourcePin.AssertStatementExactly(source, "var host = new GameObject(\"cell-input\");");
    }

    // ---- job KEY3: the order, and why it is not 599 -------------------------------------------

    /// <summary>
    /// The guard's order is 498 and it is <b>strictly below</b> the order
    /// <c>FullscreenMenuMapper</c> registers itself at — <c>HUDMenu = 502</c>
    /// (<c>AddInputContext(this, world, 0x1f6)</c> in the game's native
    /// <c>ProjectM.UI.FullscreenMenuMapper.OnStartRunning</c>). This is the whole of
    /// why KEY3 exists: <c>InputActionSystem.OnUpdate</c> walks the list ascending and each context
    /// subtracts from the later ones, so KEY2's mask at 599 was walked after the mapper had already
    /// read the key.
    ///
    /// <para>
    /// <b>What removal breaks:</b> set this to anything ≥ 502 and every behavioral test in this
    /// file still passes while <c>P</c> switches the fullscreen menu to Social again — which is
    /// exactly what KEY2 shipped and what the live run on 2026-09-12 found.
    /// </para>
    /// </summary>
    [Fact]
    public void The_guard_context_is_ordered_strictly_ahead_of_the_fullscreen_menu_mapper()
    {
        Assert.Equal(498, UiInputCapture.HotkeyGuardOrder);
        Assert.Equal(502, UiInputCapture.HudMenuOrder);
        Assert.True(
            UiInputCapture.HotkeyGuardOrder < UiInputCapture.HudMenuOrder,
            "the guard must be walked before the mapper or it cannot subtract anything from it");

        // And still behind the 599 context, which keeps every ordering FUI20 documented: the guard
        // is an addition ahead of the HUD menus, not a relocation of Progenitor's input mask.
        Assert.True(UiInputCapture.HotkeyGuardOrder < UiInputCapture.Order);
        Assert.Equal(599, UiInputCapture.Order);
    }

    /// <summary>
    /// And it ties with no named order. <c>AddInputContext</c> inserts at the first index whose order
    /// is ≥ the new one, so two entries at an equal order are processed in registration order — for
    /// the mapper, "whenever its system started running". That is the unspecified tie-break job FUI20
    /// moved off 600 to escape, and it would be worse here because the ordering <i>is</i> the fix.
    /// 496-498 are the only free integers below the mapper (<c>InputContextOrder</c> in the client metadata).
    ///
    /// <para><b>What removal breaks:</b> a future tidy-up to 495 or 499 reintroduces a tie whose
    /// outcome depends on system start order and cannot be reproduced offline.</para>
    /// </summary>
    [Fact]
    public void The_guard_order_ties_with_no_named_input_context_order()
    {
        // HUDElement 495, GenericPopup 499, DebugMenu 500, HUDSubMenu 501, HUDMenu 502,
        // MenuInput 600 — InputContextOrder in the client metadata.
        int[] named = { 495, 499, 500, 501, 502, 600 };
        Assert.DoesNotContain(UiInputCapture.HotkeyGuardOrder, named);
        Assert.DoesNotContain(UiInputCapture.Order, named);
    }

    /// <summary>
    /// The blocked set is exactly what <c>FullscreenMenuMapper.HandleInput</c> polls, plus the two
    /// order-600 menu-open actions KEY2 was aimed at, and none of the three ids that have another
    /// owner. Source-pinned by value against the <c>ButtonInputActions</c> values in the client
    /// metadata and against the mapper's own polls in the native <c>HandleInput</c> (the letters on
    /// <c>InputsUp</c>; the tab actions on <c>InputsDown</c>).
    ///
    /// <para>
    /// The three exclusions are the point. <c>Back = 17</c> (<c>0x11</c>)
    /// and <c>Menu_Close = 1004</c> (<c>0x3ec</c>)
    /// are read by the mapper and are already taken in every scope by
    /// <c>InputMaskPlan.BlockedCloseActions</c> — taking them here as well would make two owners of
    /// the panel's close gesture. <c>SelectProgression = 19</c> is not a menu
    /// at all, which is why the 18-26 block has a hole in it.
    /// </para>
    ///
    /// <para><b>What removal breaks:</b> the brief proposed adding <c>0x11</c> and <c>0x3ec</c> to
    /// this set; without this test both would go in, Escape would have two owners, and the readback
    /// would report a set that no longer matches the one consumer it was derived from.</para>
    /// </summary>
    [Fact]
    public void The_blocked_set_is_the_mapper_read_actions_and_excludes_the_ids_with_another_owner()
    {
        int[] blocked = MenuHotkeyGuard.BlockedActions();

        // Every letter the mapper polls from InputsUp.
        foreach (int id in new[] { 18, 22, 23, 24, 25, 26 })
            Assert.Contains(id, blocked);

        // Every tab action the mapper polls from InputsDown.
        foreach (int id in new[] { 1006, 1007, 1008, 1009 })
            Assert.Contains(id, blocked);

        // The three with another owner, or no menu behind them.
        Assert.DoesNotContain(17, blocked);    // Back  — InputMaskPlan.BlockedCloseActions
        Assert.DoesNotContain(1004, blocked);  // Menu_Close — same
        Assert.DoesNotContain(19, blocked);    // SelectProgression — not a menu
        Assert.Equal(ButtonInputActions.Back, 17);
        Assert.Equal(ButtonInputActions.MenuClose, 1004);
    }

    // ---- job KEY3: the mask branch -------------------------------------------------------------

    /// <summary>
    /// One process-lifetime detour, two contexts, and the instance pointer decides which recipe runs.
    /// <c>GameInputBlocker.Mask</c> fires for every <c>RotateCameraBlocker</c> in the process, so
    /// this branch is both the authorization and the selection.
    ///
    /// <para><b>What removal breaks:</b> collapse the two pointers into one "is this ours" check and
    /// the 498 context gets the full 599 recipe — <c>BlockAllKeys</c>, Move/Aim, zoom and the close
    /// actions — ahead of every HUD menu in the game, which is a vastly larger change than the defect
    /// asked for and would make the vanilla HUD menus unusable while a mod panel is open.</para>
    /// </summary>
    [Fact]
    public void The_mask_branches_on_the_instance_pointer()
    {
        var ours = new IntPtr(0x1000);
        var guard = new IntPtr(0x2000);

        Assert.Equal(HotkeyMaskTarget.Ours, MenuHotkeyGuard.TargetFor(ours, ours, guard));
        Assert.Equal(HotkeyMaskTarget.Guard, MenuHotkeyGuard.TargetFor(guard, ours, guard));
        Assert.Equal(
            HotkeyMaskTarget.None, MenuHotkeyGuard.TargetFor(new IntPtr(0x3000), ours, guard));
    }

    /// <summary>
    /// Zero never matches, on any of the three. The pointers are zeroed on every drop and teardown
    /// path precisely so a frame already inside the detour touches nothing, and a native callback can
    /// be handed a null instance.
    ///
    /// <para><b>What removal breaks:</b> drop the zero checks and every release path becomes an
    /// "everything matches" path — a torn-down generation would apply the full mask to the game's own
    /// contexts, with no owner left to stop it.</para>
    /// </summary>
    [Fact]
    public void A_zero_pointer_matches_nothing()
    {
        var p = new IntPtr(0x1000);

        Assert.Equal(HotkeyMaskTarget.None, MenuHotkeyGuard.TargetFor(IntPtr.Zero, p, p));
        Assert.Equal(HotkeyMaskTarget.None, MenuHotkeyGuard.TargetFor(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        Assert.Equal(HotkeyMaskTarget.None, MenuHotkeyGuard.TargetFor(p, IntPtr.Zero, IntPtr.Zero));
    }

    /// <summary>
    /// An impossible collision resolves to the 599 recipe, which is the superset: the 498 context
    /// would over-block rather than the 599 context under-block and let Escape through.
    /// </summary>
    [Fact]
    public void A_pointer_collision_resolves_to_the_fuller_recipe()
    {
        var p = new IntPtr(0x1000);

        Assert.Equal(HotkeyMaskTarget.Ours, MenuHotkeyGuard.TargetFor(p, p, p));
    }

    // ---- job KEY3 deliverable 3: the readback that settles the InputsUp question ---------------

    /// <summary>
    /// The counter's one-line-log contract: <c>NoteMaskApplied</c> returns <c>true</c> exactly once
    /// per process, so the caller needs no second flag beside it.
    /// </summary>
    [Fact]
    public void The_first_mask_application_is_announced_exactly_once()
    {
        Assert.True(MenuHotkeyGuard.NoteMaskApplied(1_000));
        Assert.False(MenuHotkeyGuard.NoteMaskApplied(1_001));
        Assert.False(MenuHotkeyGuard.NoteMaskApplied(9_999_999));
        Assert.True(MenuHotkeyGuard.EverApplied);
    }

    /// <summary>
    /// <c>masked</c> counts applications inside a one-second window and a stale window reads zero.
    /// Stale-reads-as-zero is the point: a reader taking <c>status.focus.guard</c> a minute after the
    /// last press must see <c>masked: 0</c>, not the count from whenever the guard last ran.
    ///
    /// <para><b>What removal breaks:</b> a running total would answer "has the guard ever run since
    /// the client started", which cannot distinguish the live outcome the fallback decision turns on
    /// — the guard running on the very frames the tab moves.</para>
    /// </summary>
    [Fact]
    public void The_mask_counter_reports_the_last_second_and_then_forgets()
    {
        MenuHotkeyGuard.NoteMaskApplied(10_000);
        MenuHotkeyGuard.NoteMaskApplied(10_016);
        MenuHotkeyGuard.NoteMaskApplied(10_032);

        Assert.Equal(3, MenuHotkeyGuard.MaskedThisSecond(10_040));

        // The window has gone stale: nothing has been masked in the last second.
        Assert.Equal(0, MenuHotkeyGuard.MaskedThisSecond(10_000 + MenuHotkeyGuard.MaskWindowMs));
        Assert.Equal(0, MenuHotkeyGuard.MaskedThisSecond(60_000));

        // A fresh application starts a fresh window rather than adding to the stale one.
        MenuHotkeyGuard.NoteMaskApplied(60_000);
        Assert.Equal(1, MenuHotkeyGuard.MaskedThisSecond(60_100));
    }

    /// <summary>
    /// Nothing masked, nothing counted. The default state of the readback has to be honest or
    /// <c>masked: 0</c> while a field is focused stops being evidence of anything.
    /// </summary>
    [Fact]
    public void The_mask_counter_starts_at_zero()
    {
        Assert.Equal(0, MenuHotkeyGuard.MaskedThisSecond(0));
        Assert.Equal(0, MenuHotkeyGuard.MaskedThisSecond(500_000));
        Assert.False(MenuHotkeyGuard.EverApplied);
    }

    /// <summary>
    /// A clock that went backwards restarts the window instead of reporting a negative-age one as
    /// live. <c>Environment.TickCount64</c> does not wrap in any session length that matters, but a
    /// counter whose only job is to be trusted live must not depend on that being true.
    /// </summary>
    [Fact]
    public void A_backwards_clock_restarts_the_window()
    {
        MenuHotkeyGuard.NoteMaskApplied(50_000);
        Assert.Equal(0, MenuHotkeyGuard.MaskedThisSecond(10_000));

        MenuHotkeyGuard.NoteMaskApplied(10_000);
        Assert.Equal(1, MenuHotkeyGuard.MaskedThisSecond(10_010));
    }

    /// <summary>
    /// <c>status.focus.guard</c>: <c>{registered, order, masked}</c>. The order is reported rather
    /// than assumed, so a reader never has to trust that the constant in the docs is the constant in
    /// the build.
    /// </summary>
    [Fact]
    public void The_guard_readback_reports_registration_order_and_the_masked_count()
    {
        MenuHotkeyGuard.GuardRegistered = true;
        MenuHotkeyGuard.NoteMaskApplied(20_000);
        MenuHotkeyGuard.NoteMaskApplied(20_010);

        var row = ModsMenu.GuardStatus(20_020);

        Assert.Equal(true, row["registered"]);
        Assert.Equal(UiInputCapture.HotkeyGuardOrder, row["order"]);
        Assert.Equal(2, row["masked"]);
    }

    /// <summary>
    /// And it hangs off <c>status.focus</c>, so one <c>mods_ui status</c> read carries the field, the
    /// flag, the action list and whether the context that can act on them is registered. The three
    /// live outcomes this separates are named in <c>ModsMenu.GuardStatus</c>.
    /// </summary>
    [Fact]
    public void The_focus_readback_carries_the_guard_row()
    {
        var row = ModsMenu.FocusStatus(MenuHotkeyGuard.InstallSearch, blocked: true);

        var guard = Assert.IsType<Dictionary<string, object?>>(row["guard"]);
        Assert.Equal(UiInputCapture.HotkeyGuardOrder, guard["order"]);
        Assert.Equal(false, guard["registered"]);
        Assert.Equal(0, guard["masked"]);
    }

    // ---- job KEY3 deliverable 3: the fallback, both branches -----------------------------------

    /// <summary>
    /// <b>The fallback ships switched off.</b> The ordering fix goes live alone; this constant is the
    /// second half, pre-built so a coordinator who sees <c>status.focus.guard.masked</c> incrementing
    /// while the tab still moves can flip it without another job.
    ///
    /// <para><b>What removal breaks:</b> ship it <c>true</c> untested live and a wrong condition
    /// strands the fullscreen menu on a tab — a prefix on the mapper's own state machine is a much
    /// bigger commitment than a mask.</para>
    /// </summary>
    [Fact]
    public void The_set_state_prefix_is_off_until_the_live_check_says_otherwise()
    {
        Assert.False(MenuHotkeyGuard.UseSetStatePrefix);

        // With the flag off nothing is ever suppressed, whatever the other terms say — which is what
        // makes shipping it off a no-op rather than a promise.
        Assert.False(MenuHotkeyGuard.ShouldSuppressSetState(
            MenuHotkeyGuard.UseSetStatePrefix,
            hotkeysBlocked: true, requested: 3, current: 7, progenitorCall: false));
    }

    /// <summary>
    /// The enabled branch, one term at a time. Suppression needs the flag on, a Progenitor field
    /// focused, a call that is not Progenitor's own, and a real state change.
    ///
    /// <para><b>What removal breaks, per term:</b> drop the focus term and the menu's tab keys are
    /// dead for a player who is not typing; drop the <c>progenitorCall</c> bypass and the workspace
    /// cannot open its own page (<c>NativeOuterTabRegistration.cs:1216</c> drives it with a typed
    /// <c>SetState(7)</c>, and <c>:1246</c> the return transition) from the very field whose focus
    /// armed the guard; drop the <c>requested != current</c> term and the prefix becomes a general
    /// brake on a state machine whose <c>SetState</c> is a destroy-and-rebuild refresh, not an
    /// idempotent setter.</para>
    /// </summary>
    [Theory]
    // flag, focused, requested, current, ours  -> suppressed
    [InlineData(true, true, 3, 7, false, true)]    // the defect: a typing player's P
    [InlineData(false, true, 3, 7, false, false)]  // flag off
    [InlineData(true, false, 3, 7, false, false)]  // nothing focused: vanilla behavior
    [InlineData(true, true, 3, 7, true, false)]    // Progenitor's own typed SetState
    [InlineData(true, true, 7, 7, false, false)]   // same state: a refresh, not a tab change
    [InlineData(true, true, 7, 3, true, false)]    // the owned page opening, bypassed
    public void The_set_state_prefix_suppresses_only_a_real_foreign_tab_change_while_typing(
        bool usePrefix, bool hotkeysBlocked, int requested, int current, bool progenitorCall, bool expected)
    {
        Assert.Equal(
            expected,
            MenuHotkeyGuard.ShouldSuppressSetState(
                usePrefix, hotkeysBlocked, requested, current, progenitorCall));
    }

    // ---- job KEY3: the wiring, over source text ------------------------------------------------

    /// <summary>
    /// The guard context's lifecycle, pinned over <c>GameInputBlocker.cs</c> — which is behind
    /// <c>#if !PROGENITOR_TESTS</c> (it needs the game), so none of it is callable from here. It is
    /// pushed with <c>AddInputContext</c> at the guard order, verified with
    /// <c>IsContextRegistered</c> rather than "nothing threw" (R31 finding 4: an add refused under
    /// <c>_DenyModificationsOfInputContexts</c> logs and returns without adding), taken out with
    /// <c>RemoveInputContext</c> rather than <c>Deregister</c> (the instance's own <c>_Registered</c>
    /// guard was never set because <c>Register</c> was never called), and dropped on <c>Dispose</c>.
    ///
    /// <para><b>What removal breaks:</b> without the verification a refused add caches a context that
    /// is not in the list and every later register is a silent no-op behind that instance's own guard;
    /// without the <c>Dispose</c> and <c>UninstallMask</c> lines a Core generation can go away leaving
    /// a 498 entry in the game's list with a mask pointer belonging to a collected ALC.</para>
    /// </summary>
    [Fact]
    public void The_guard_context_is_added_verified_removed_and_dropped()
    {
        string source = BlockerSource();

        SourcePin.AssertStatementExactly(source, "RegisterGuardLocked(system, world);");
        SourcePin.AssertStatementExactly(
            source, "new IInputContext(guard.Pointer), world, UiInputCapture.HotkeyGuardOrder);");
        SourcePin.AssertStatementExactly(source, "if (!StillRegistered(system, guard))");
        SourcePin.AssertStatementExactly(
            source, "try { system.RemoveInputContext(new IInputContext(guard.Pointer)); }");

        // Removed on every path Remove() can take, and on the teardown path Dispose() reaches
        // unconditionally.
        SourcePin.AssertStatementExactly(source, "RemoveGuardLocked(system);");
        SourcePin.AssertStatementExactly(source, "RemoveGuardLocked(null);");
        SourcePin.AssertStatementExactly(source, "if (guard != null) DropGuard(guard);");
        SourcePin.AssertStatementExactly(source, "if (guard != null) ClearGuardCameraBlock(guard);");
    }

    /// <summary>
    /// The mask's two halves. <c>Mask</c> branches through the pure <c>TargetFor</c> and hands the
    /// guard target to <c>MaskGuard</c>, which writes the hotkey set into <b>both</b> button lists and
    /// counts the application — and does nothing at all unless a field is focused.
    ///
    /// <para><b>What removal breaks:</b> drop the <c>HotkeysBlocked</c> line in <c>MaskGuard</c> and
    /// the menu's letter hotkeys are dead for the whole time any mod panel is open; drop the
    /// <c>NoteMaskApplied</c> line and the one live measurement that decides whether the
    /// <c>UseSetStatePrefix</c> fallback is needed is gone, leaving "it still does not work" with no
    /// way to tell an ordering bug from an <c>InputsUp</c> one.</para>
    /// </summary>
    [Fact]
    public void The_guard_mask_is_the_hotkey_set_alone_and_is_counted()
    {
        string source = BlockerSource();

        SourcePin.AssertStatementExactly(source, "if (target == HotkeyMaskTarget.Guard)");
        SourcePin.AssertStatementExactly(source, "MaskGuard(blockInputState);");
        // KEYS3 polish (live P2, 2026-09-30): the guard also runs while the close hold is on.
        SourcePin.AssertStatementExactly(source, "if (!hotkeysBlocked && !closeHeld) return;");
        SourcePin.AssertStatementExactly(source, "if (!hotkeysBlocked) return;");
        SourcePin.AssertStatementExactly(
            source, "if (MenuHotkeyGuard.NoteMaskApplied(Environment.TickCount64))");

        // The guard context must never acquire the persistent camera flag (R31 finding 8), and the
        // pointer must be zeroed with _ours so a frame inside the detour during teardown matches
        // neither.
        SourcePin.AssertStatementExactly(source, "Volatile.Write(ref _guard, IntPtr.Zero);");
        SourcePin.AssertStatementExactly(source, "try { guard._BlockRotateCamera = false; }");

        // And the guard's mask is the hotkey set, plus (KEYS3 polish) the close actions only while
        // MenuHotkeyGuard.CloseHeld: no scope, no BlockAllKeys, no analog. Pinned over the MaskGuard body alone.
        string body = source[source.IndexOf("private static void MaskGuard(", StringComparison.Ordinal)..];
        Assert.DoesNotContain("BlockAllKeys", body, StringComparison.Ordinal);
        Assert.DoesNotContain("BlockedAnalogInputActions", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InputMaskPlan.For", body, StringComparison.Ordinal);
        int held = body.IndexOf("if (closeHeld)", StringComparison.Ordinal);
        int closes = body.IndexOf("InputMaskPlan.BlockedCloseActions()", StringComparison.Ordinal);
        Assert.True(held >= 0 && closes > held, "the close actions are taken only under the close hold");
    }

    /// <summary>
    /// The guard row reaches Dev on both exits of <c>ReadStatus</c>, like the focus row itself, and
    /// the Dev side takes the order from <c>ModsMenu</c> rather than spelling <c>498</c> — which is
    /// how the readback and the registration would come to disagree about the order being reported.
    ///
    /// <para><b>What removal breaks:</b> a caller reading <c>status.focus.guard.registered</c> after a
    /// throw gets a missing key instead of <c>false</c> and has to handle two shapes.</para>
    /// </summary>
    [Fact]
    public void The_guard_row_is_on_both_status_exits_and_takes_its_order_from_core()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Progenitor.Dev", "Ops", "ModsUiOps.cs"));

        SourcePin.AssertStatementExactly(source, "[\"order\"] = ModsMenu.MenuHotkeyGuardOrder,");
        Assert.DoesNotContain("[\"order\"] = 498", source, StringComparison.Ordinal);

        string core = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Progenitor.Core", "Ui", "ModsMenu.cs"));
        SourcePin.AssertStatementExactly(
            core, "[\"guard\"] = GuardStatus(System.Environment.TickCount64),");
    }

    private static string BlockerSource() => File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "Progenitor.Core", "Ui", "GameInputBlocker.cs"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Progenitor.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
