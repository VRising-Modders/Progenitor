using System;
using System.IO;
using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Job FUI20, defect 1: with the settings panel open, one Escape press closed the mod panel
/// <b>and</b> opened the vanilla system menu. Two independent paths saw the same press — Bootstrap
/// reads <c>Keyboard.current.escapeKey</c> and hides every shown panel, and the game's own pipeline
/// turned the press into <c>ButtonInputAction.Back</c> — and nothing stopped the second.
///
/// <para>
/// The fix is a mask that takes the close actions in every scope, and these tests are the part of
/// it that can be exercised without the game. What cannot be tested here is whether the game
/// honors the block; the evidence for that is in the dump citations on
/// <see cref="InputMaskPlan"/>, and the one source-level claim that <i>can</i> be checked — that
/// the native postfix actually blocks both actions unconditionally — is checked against the source
/// at the bottom of this file, the same way <c>PanelLayoutTests</c> checks the row builders it
/// cannot call.
/// </para>
/// </summary>
public class UiInputMaskTests
{
    // ---- the action ids, against the dump ----------------------------------------------------

    /// <summary>
    /// The two values the whole fix rests on, pinned to the lines they were read from. A game
    /// update that renumbers them silently turns Escape back into two actions.
    /// </summary>
    [Fact]
    public void The_close_actions_are_the_ones_the_dump_names()
    {
        Assert.Equal(17, ButtonInputActions.Back);          // client metadata
        Assert.Equal(1004, ButtonInputActions.MenuClose);   // client metadata
        Assert.Equal(500, ButtonInputActions.RotateCamera); // client metadata

        Assert.True(InputMaskPlan.IsCloseAction(ButtonInputActions.Back));
        Assert.True(InputMaskPlan.IsCloseAction(ButtonInputActions.MenuClose));
        Assert.False(InputMaskPlan.IsCloseAction(ButtonInputActions.RotateCamera));
        Assert.False(InputMaskPlan.IsCloseAction(0));
    }

    /// <summary>
    /// Both, not one. The Escape key carries the Unity usages <c>"Back"</c> and <c>"Cancel"</c>
    /// (client metadata) and ProjectM has a separate <c>Menu_Close</c> in the
    /// UI-navigation block, so a fix that names only <c>Back</c> is the kind that works on one
    /// machine.
    /// </summary>
    [Fact]
    public void Both_close_actions_are_taken_and_they_are_distinct()
    {
        int[] closes = InputMaskPlan.BlockedCloseActions();

        Assert.Equal(2, closes.Length);
        Assert.Contains(ButtonInputActions.Back, closes);
        Assert.Contains(ButtonInputActions.MenuClose, closes);
        Assert.Equal(closes.Length, closes.Distinct().Count());
    }

    /// <summary>
    /// Job FUI27, R45 finding 28, half one: <c>IsCloseAction</c> and
    /// <c>BlockedCloseActions</c> are one list, not two statements of it. They used to be a
    /// hand-written <c>||</c> and a hand-written array literal — agreeing today, and free to
    /// disagree the moment a third action is added to one of them.
    ///
    /// <para>
    /// The equivalence over the id range is the property; the source assertion under it is what
    /// makes the test fail when the predicate goes back to restating the list, because two
    /// statements of a two-element list agree until the day someone adds a third — which is
    /// precisely the day a test that only compared behavior would still be green.
    /// </para>
    /// </summary>
    [Fact]
    public void The_close_action_predicate_and_the_blocked_list_are_the_same_list()
    {
        int[] closes = InputMaskPlan.BlockedCloseActions();

        for (int action = 0; action <= 1100; action++)
            Assert.Equal(closes.Contains(action), InputMaskPlan.IsCloseAction(action));

        string body = MethodBody(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Progenitor.Core", "Ui", "UiInputMask.cs")),
            "internal static bool IsCloseAction(int action)");

        SourcePin.AssertUncommented(body, "CloseActions");
        Assert.DoesNotContain("ButtonInputActions.Back", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ButtonInputActions.MenuClose", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Job FUI27, R45 finding 28, half two: the accessor hands back the <b>live</b> array, not a
    /// fresh copy per call. Two reasons, and both are load-bearing. The caller that matters is
    /// <c>GameInputBlocker.Mask</c>, a native postfix that runs once per frame per context and must
    /// not allocate on the game's input path. And "the documented list is the one that executes" is
    /// a claim about identity: a copy would be a third statement of the list, which is the shape of
    /// the defect.
    /// </summary>
    [Fact]
    public void The_blocked_close_action_list_is_one_array_and_not_a_copy_per_call()
    {
        Assert.Same(InputMaskPlan.BlockedCloseActions(), InputMaskPlan.BlockedCloseActions());
    }

    // ---- the plan --------------------------------------------------------------------------

    /// <summary>
    /// The defect, as a property. The close key is taken in <b>every</b> scope, including the one
    /// where the pointer is nowhere near the panel — that is the whole point: Escape has to close
    /// the panel from anywhere, and before this the context was not even registered when the
    /// pointer was elsewhere. A scope added later fails this test rather than quietly reopening
    /// the defect.
    /// </summary>
    [Fact]
    public void Every_scope_takes_the_panel_close_key()
    {
        foreach (UiMaskScope scope in Enum.GetValues(typeof(UiMaskScope)))
        {
            var plan = InputMaskPlan.For(scope);
            Assert.Equal(scope, plan.Scope);
            Assert.Equal(2, InputMaskPlan.BlockedCloseActions().Length);
        }
    }

    /// <summary>
    /// <c>BlockAllKeys</c>'s parameter is <c>includeUINavigation</c> (client
    /// metadata), and passing <c>true</c> takes
    /// <c>ButtonInputActionCollection.UINavigationActions</c> with it — the set that keeps uGUI
    /// alive under a chat box and under our panel. It stays <c>false</c> in every scope, which is
    /// exactly why the close actions have to be named individually instead of hoped for.
    /// </summary>
    [Fact]
    public void UI_navigation_is_never_blocked_wholesale()
    {
        foreach (UiMaskScope scope in Enum.GetValues(typeof(UiMaskScope)))
            Assert.False(InputMaskPlan.For(scope).IncludeUiNavigation);
    }

    /// <summary>
    /// The FUI19 promise, kept: off the panel, the wheel zooms, the character moves, the click
    /// swings. Only the two close actions are taken.
    /// </summary>
    [Fact]
    public void The_close_key_scope_takes_nothing_else()
    {
        var plan = InputMaskPlan.For(UiMaskScope.CloseKeyOnly);

        Assert.False(plan.BlockAllKeys);
        Assert.False(plan.BlockMoveAndAim);
        Assert.False(plan.BlockZoom);
        Assert.False(plan.BlockCameraRotationFallback);
    }

    /// <summary>Over the panel: chat's recipe, plus the wheel evidence 167 added.</summary>
    [Fact]
    public void The_full_scope_is_chats_recipe_plus_the_wheel()
    {
        var plan = InputMaskPlan.For(UiMaskScope.Full);

        Assert.True(plan.BlockAllKeys);
        Assert.True(plan.BlockMoveAndAim);
        Assert.True(plan.BlockZoom);
        Assert.True(plan.BlockCameraRotationFallback);
    }

    /// <summary>
    /// The workspace live mask must still consume ZoomCamera so the camera does not steal
    /// the wheel. Blocking this analog does not zero the mouse device and is not a scroll
    /// apply; the shown-input pump is the list mover and uGUI is inert.
    /// </summary>
    [Fact]
    public void The_workspace_live_mask_does_not_retain_the_wheel_action()
    {
        Assert.Equal(16, AnalogInputActions.ZoomCamera);
        Assert.True(InputMaskPlan.For(UiMaskScope.Full).BlockZoom);
        Assert.False(InputMaskPlan.For(UiMaskScope.CloseKeyOnly).BlockZoom);

        Assert.True(UiShownInput.ShouldArm(2));
        Assert.False(UiShownInput.ShouldArm(0));
        Assert.Equal("pump", MenuPanelLayout.WheelMover);
        Assert.Equal(0f, MenuPanelLayout.UguiScrollSensitivity);
        Assert.Equal(
            MenuPanelLayout.PumpPixelsPerNotch,
            MenuPanelLayout.PixelsForWheelEvent(
                MenuPanelLayout.UguiScrollSensitivity * MenuPanelLayout.UguiNotchUnits,
                MenuPanelLayout.PumpPixelsPerNotch,
                uguiConsumed: false));
    }

    /// <summary>
    /// A player with a panel open still wants to look around, in either scope — chat does the same
    /// thing (<c>ChatFocusedInputContext.GetConsumedInputs</c>).
    /// </summary>
    [Fact]
    public void Camera_rotation_survives_on_keyboard_and_mouse_in_both_scopes()
    {
        // Both panel scopes. The Exclusive scope (photo mode owns all gameplay input) is the one
        // that deliberately does not allow it back: PhotoModeInputTests.
        foreach (var scope in new[] { UiMaskScope.CloseKeyOnly, UiMaskScope.Full })
            Assert.True(InputMaskPlan.For(scope).AllowCameraRotationOnKeyboardAndMouse);
        Assert.False(InputMaskPlan.For(UiMaskScope.Exclusive).AllowCameraRotationOnKeyboardAndMouse);
    }

    /// <summary>
    /// A cast integer is a legal enum value in C# and would otherwise reach the mask as a scope it
    /// has no rule for. Fail closed the way <c>HoverRouter</c> does for an unreadable pointer: the
    /// full mask, which is the pre-FUI19 behavior, and never "block nothing".
    /// </summary>
    [Fact]
    public void An_unknown_scope_falls_back_to_the_full_mask()
    {
        var plan = InputMaskPlan.For((UiMaskScope)77);

        Assert.Equal(UiMaskScope.Full, plan.Scope);
        Assert.True(plan.BlockAllKeys);
    }

    /// <summary>The pointer gate's two answers map onto the two scopes, and nowhere else.</summary>
    [Fact]
    public void The_pointer_gate_chooses_the_scope()
    {
        Assert.Equal(UiMaskScope.Full, InputMaskPlan.ScopeFor(true));
        Assert.Equal(UiMaskScope.CloseKeyOnly, InputMaskPlan.ScopeFor(false));
    }

    // ---- the plumbing, through the real handles ----------------------------------------------

    /// <summary>
    /// End to end through <see cref="UiInputCapture"/>: the pointer decides the scope and never
    /// the registration. This is the structural half of the fix — the previous code removed the
    /// context when the pointer left, so there was nothing in the game's list to take Escape with.
    /// </summary>
    [Fact]
    public void The_pointer_changes_the_scope_and_never_the_registration()
    {
        UiApi.ResetForTests();
        try
        {
            UiInputCapture.Acquire();
            Assert.True(UiInputCapture.IsBlocking);
            Assert.Equal(UiMaskScope.Full, UiInputCapture.MaskScope);

            UiInputCapture.SetPointerBlock(false);
            Assert.True(UiInputCapture.IsBlocking);
            Assert.Equal(UiMaskScope.CloseKeyOnly, UiInputCapture.MaskScope);

            UiInputCapture.SetPointerBlock(true);
            Assert.Equal(UiMaskScope.Full, UiInputCapture.MaskScope);

            // The refcount is still the only thing that decides whether a context is wanted.
            UiInputCapture.Release();
            Assert.False(UiInputCapture.IsBlocking);
        }
        finally
        {
            UiApi.ResetForTests();
        }
    }

    /// <summary>
    /// The master switch still wins over the scope: <c>Ui.CaptureInput(false)</c> removes the
    /// context, and a removed context takes nothing at all — including Escape. That is the
    /// documented meaning of the opt-out and job FUI20 does not quietly change it.
    /// </summary>
    [Fact]
    public void CaptureInput_false_still_removes_the_context_entirely()
    {
        UiApi.ResetForTests();
        try
        {
            UiInputCapture.Acquire();
            Assert.True(UiInputCapture.IsBlocking);

            UiInputCapture.SetEnabled(false);
            Assert.False(UiInputCapture.IsBlocking);

            UiInputCapture.SetPointerBlock(false);
            UiInputCapture.SetPointerBlock(true);
            Assert.False(UiInputCapture.IsBlocking);

            UiInputCapture.SetEnabled(true);
            Assert.True(UiInputCapture.IsBlocking);
        }
        finally
        {
            UiApi.ResetForTests();
        }
    }

    // ---- the two copies of the pointer answer (job FUI23) ------------------------------------

    /// <summary>
    /// R44 finding 1, at the module level: <b>close a panel with the pointer away, reopen it with
    /// the pointer on it, and the mask has to be on.</b>
    ///
    /// <para>
    /// There are two copies of the pointer answer — <c>UiInputCapture._pointerBlock</c> and the
    /// blocker's own scope — and <c>Release</c> used to write the first one directly on the last
    /// 1→0 without telling the blocker. So: open with the pointer elsewhere (scope goes to
    /// <see cref="UiMaskScope.CloseKeyOnly"/>), close (module resets its flag to blocked, blocker
    /// keeps CloseKeyOnly), reopen under the pointer (the hover router had already reset to
    /// "blocked", so it reports no transition and never pushes). The panel is hovered, the module
    /// says <see cref="UiMaskScope.Full"/>, and the blocker is masking nothing but the close key:
    /// every wheel notch scrolls the list <i>and</i> zooms the camera, which is evidence 167's
    /// defect 2 restored.
    /// </para>
    ///
    /// <para>
    /// The assertion is on the blocker, deliberately. Asserting
    /// <see cref="UiInputCapture.MaskScope"/> alone would have passed throughout the defect — that
    /// is finding 4, and it is why this test says <c>Applied</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void A_panel_reopened_under_the_pointer_after_a_release_masks_everything()
    {
        UiApi.ResetForTests();
        var blocker = new RecordingBlocker();
        UiApi.SetInputBlockerForTests(blocker);
        try
        {
            // 1. A panel opens with the pointer somewhere else.
            UiInputCapture.Acquire();
            UiInputCapture.SetPointerBlock(false);
            Assert.Equal(UiMaskScope.CloseKeyOnly, blocker.Applied);

            // 2. The player closes it, mouse still off it. The pointer gate goes back to its
            //    fail-closed default — that is what step 3 depends on — but the scope pushed for
            //    that reset is CloseKeyOnly, because with no panel open there is nothing to mask
            //    and the removal may not land on this pass (job FUI27, R45 finding 8). The two
            //    copies still agree, which is the property finding 1 is about, and the value they
            //    agree on is the minimal one.
            UiInputCapture.Release();
            Assert.True(UiInputCapture.PointerBlock);
            Assert.Equal(UiMaskScope.CloseKeyOnly, blocker.Applied);
            Assert.Equal(UiInputCapture.WantedMaskScope, blocker.Applied);

            // 3. It reopens under the pointer. The gate sees no transition, so nothing else will
            //    repair the scope — it has to be right already.
            UiInputCapture.Acquire();

            Assert.Equal(UiMaskScope.Full, blocker.Applied);
            Assert.Equal(UiMaskScope.Full, UiInputCapture.MaskScope);
            Assert.Equal(UiInputCapture.WantedMaskScope, UiInputCapture.MaskScope);
            Assert.True(InputMaskPlan.For(blocker.Applied).BlockZoom);
        }
        finally
        {
            UiApi.ResetForTests();
        }
    }

    /// <summary>
    /// The same defect from the other end: a push is decided against the <b>blocker's</b> state,
    /// not against the module's copy of it. A blocker whose scope drifted — which is what finding 1
    /// produced — is repaired by the next ordinary pointer decision instead of being permanently
    /// skipped by an equality guard that compares the module to itself.
    /// </summary>
    [Fact]
    public void A_blocker_whose_scope_drifted_is_repaired_by_the_next_decision()
    {
        UiApi.ResetForTests();
        var blocker = new RecordingBlocker();
        UiApi.SetInputBlockerForTests(blocker);
        try
        {
            UiInputCapture.Acquire();
            Assert.Equal(UiMaskScope.Full, blocker.Applied);

            // Something outside this module moved the blocker's scope: a Core swap, a game call
            // that reset it, a future writer that forgets to tell us. The module still says Full.
            blocker.ForceScope(UiMaskScope.CloseKeyOnly);
            Assert.Equal(UiMaskScope.Full, UiInputCapture.WantedMaskScope);

            // The next decision restates the answer, and it lands, because the redundancy check
            // asks the blocker rather than the module's shadow of it.
            UiInputCapture.SetPointerBlock(true);
            Assert.Equal(UiMaskScope.Full, blocker.Applied);
        }
        finally
        {
            UiApi.ResetForTests();
        }
    }

    /// <summary>
    /// R44 finding 4: the pin diagnostic reports the copy that <i>can</i> be wrong, and says so
    /// when it is. In the state finding 1 produced, <c>Ui.DescribePins</c> printed
    /// <c>scope=Full</c> — from module state — while the native mask applied
    /// <see cref="UiMaskScope.CloseKeyOnly"/>. There was no accessor anywhere for the value the
    /// game reads, so no log line, no pin dump and no live diagnostic could tell a healthy mask
    /// from a desynced one.
    /// </summary>
    [Fact]
    public void The_pin_line_reports_the_blockers_scope_and_names_a_desync()
    {
        UiApi.ResetForTests();
        var blocker = new RecordingBlocker();
        UiApi.SetInputBlockerForTests(blocker);
        try
        {
            UiInputCapture.Acquire();
            Assert.Contains("scope=Full", UiInputCapture.Describe(), StringComparison.Ordinal);
            Assert.DoesNotContain("DESYNC", UiInputCapture.Describe(), StringComparison.Ordinal);

            blocker.ForceScope(UiMaskScope.CloseKeyOnly);

            string line = UiInputCapture.Describe();
            Assert.Contains("scope=CloseKeyOnly", line, StringComparison.Ordinal);
            Assert.Contains("wanted=Full", line, StringComparison.Ordinal);
            Assert.Contains("DESYNC", line, StringComparison.Ordinal);
        }
        finally
        {
            UiApi.ResetForTests();
        }
    }

    // ---- the removal window (job FUI27, R45 finding 8) ---------------------------------------

    /// <summary>
    /// <b>The finding, as the player feels it: no panel open, and unable to move.</b>
    ///
    /// <para>
    /// <see cref="UiInputCapture.Release"/> sends its scope push before <c>Apply</c> runs the
    /// removal. With the scope taken from the pointer gate alone, the last panel closing pushed
    /// <see cref="UiMaskScope.Full"/> — <c>BlockAllKeys</c>, Move/Aim, zoom, and
    /// <c>_BlockRotateCamera</c> written on the context object — and only the subsequent
    /// <c>Remove()</c> cleared any of it. When the deny flag is set, <c>Execute</c> returns
    /// <c>Deferred</c> <i>without calling <c>Remove</c> at all</i>, and the module's own
    /// <c>GaveUpLog</c> says that path waits without a limit. So: no panel, full mask, camera
    /// pinned, for as long as the game stays inside a context callback.
    /// </para>
    ///
    /// <para>
    /// Before FUI23 the same stuck removal left the blocker wherever the pointer had put it — at
    /// worst <see cref="UiMaskScope.CloseKeyOnly"/>, which costs the player Escape and nothing
    /// else. This test pins that ceiling: whatever else happens to a removal, the window it leaves
    /// is never masked harder than the minimum.
    /// </para>
    /// </summary>
    [Fact]
    public void A_removal_the_deny_flag_defers_leaves_the_player_no_more_masked_than_the_close_key()
    {
        UiApi.ResetForTests();
        var blocker = new RecordingBlocker();
        UiApi.SetInputBlockerForTests(blocker);
        try
        {
            // A panel is open under the pointer: the full mask, and the persistent camera flag set.
            UiInputCapture.Acquire();
            Assert.Equal(UiMaskScope.Full, blocker.Applied);
            Assert.True(blocker.BlocksCameraRotation);
            Assert.True(blocker.IsRegistered);

            // The game is inside a context callback when the last panel closes.
            blocker.DenyModifications = true;
            UiInputCapture.Release();

            // Remove was never even called, and the context is still in the game's list.
            Assert.Equal(0, blocker.RemoveCalls);
            Assert.True(blocker.IsRegistered);
            Assert.True(UiInputCapture.HasPendingChange);

            // What the player is left carrying, with nothing on screen.
            Assert.Equal(UiMaskScope.CloseKeyOnly, blocker.Applied);
            Assert.False(blocker.BlocksCameraRotation);

            var plan = InputMaskPlan.For(blocker.Applied);
            Assert.False(plan.BlockAllKeys);
            Assert.False(plan.BlockMoveAndAim);
            Assert.False(plan.BlockZoom);
            Assert.False(plan.BlockCameraRotationFallback);

            // And it does not creep back up while the deferral waits.
            UiInputCapture.Pump();
            UiInputCapture.Pump();
            Assert.Equal(UiMaskScope.CloseKeyOnly, blocker.Applied);
            Assert.False(blocker.BlocksCameraRotation);
        }
        finally
        {
            UiApi.ResetForTests();
        }
    }

    /// <summary>
    /// The other unbounded path (<c>FoldRefusedRemoveLocked</c> retries every frame with no bound):
    /// <c>RemoveInputContext</c> ran, the entry is still listed, and the module will keep asking
    /// for as long as the client runs. Same ceiling, and it holds across every retry — a per-frame
    /// pass that re-pushed the module's decision would put the full mask back on frame two.
    /// </summary>
    [Fact]
    public void A_removal_the_game_refuses_leaves_the_player_no_more_masked_than_the_close_key()
    {
        UiApi.ResetForTests();
        var blocker = new RecordingBlocker();
        UiApi.SetInputBlockerForTests(blocker);
        try
        {
            UiInputCapture.Acquire();
            Assert.Equal(UiMaskScope.Full, blocker.Applied);
            Assert.True(blocker.BlocksCameraRotation);

            blocker.RefuseRemove = true;
            UiInputCapture.Release();

            Assert.Equal(1, blocker.RemoveCalls);
            Assert.True(blocker.IsRegistered);
            Assert.True(UiInputCapture.PumpArmedForTests);

            for (int frame = 0; frame < 10; frame++)
            {
                Assert.Equal(UiMaskScope.CloseKeyOnly, blocker.Applied);
                Assert.False(blocker.BlocksCameraRotation);
                UiInputCapture.Pump();
            }

            Assert.True(blocker.RemoveCalls > 1, "the refused removal is retried");
            Assert.Equal(UiMaskScope.CloseKeyOnly, blocker.Applied);
        }
        finally
        {
            UiApi.ResetForTests();
        }
    }

    /// <summary>
    /// The scope is now derived from three terms rather than one, so the pairing R44 finding 1
    /// established — a write and its push cannot be separated — has to widen: every mutation of the
    /// panel count and of the enable flag owes the blocker a push too, and a later edit that adds a
    /// mutation without one puts the two copies back out of step.
    ///
    /// <para>
    /// A grep cannot state that. This walks <b>every</b> sequence of the public mutators to
    /// depth five and after each single call asserts the two properties the design rests on: the
    /// blocker is at the scope the module decided (no drift, ever, from any reachable state), and
    /// <see cref="UiMaskScope.Full"/> is reached only when there is actually a panel open, enabled,
    /// under the pointer. Deleting the push from any one mutator, or dropping the wanted term from
    /// the derivation, fails this within the first few sequences.
    /// </para>
    ///
    /// <para>
    /// <b>All six of them, since job FUI28 (R46 finding 5).</b> <c>EffectiveScopeLocked</c>'s doc
    /// names six mutators that owe the blocker a push and cites this walk as the proof; the walk
    /// held four of them. <see cref="UiInputCapture.Use"/> was the harness's own setup call and
    /// <see cref="UiInputCapture.Uninstall"/> was in the whole suite exactly once, in an off-thread
    /// teardown test that asserts nothing about a scope — so the two pushes a Core swap actually
    /// makes were the two nothing checked. Both are ops now. Each installs a <b>fresh</b>
    /// <see cref="RecordingBlocker"/> afterwards, which is what makes their pushes observable at
    /// all: a fresh blocker's field starts at <see cref="UiMaskScope.Full"/>, exactly as
    /// <c>GameInputBlocker</c>'s does before anything has told it otherwise, and correcting that
    /// is the entire job of <c>Use</c>'s push. <c>Uninstall</c>'s is asserted against the
    /// <i>doomed</i> blocker inside the op, before the module has forgotten it — a hand-off no
    /// later assertion could see, because the module is holding a different object by then.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_reachable_sequence_leaves_the_blocker_at_the_scope_the_module_decided()
    {
        var blocker = new RecordingBlocker();

        var ops = new (string Name, Action Run)[]
        {
            ("acquire", UiInputCapture.Acquire),
            ("release", UiInputCapture.Release),
            ("enable", () => UiInputCapture.SetEnabled(true)),
            ("disable", () => UiInputCapture.SetEnabled(false)),
            ("pointer-on", () => UiInputCapture.SetPointerBlock(true)),
            ("pointer-off", () => UiInputCapture.SetPointerBlock(false)),
            ("use", () =>
            {
                // A blocker installed mid-session, which is what Use is for: the new one defaults
                // to Full and only Use's push can bring it to the scope the module decided.
                blocker = new RecordingBlocker();
                UiInputCapture.Use(blocker);
            }),
            ("uninstall", () =>
            {
                var doomed = blocker;
                UiInputCapture.Uninstall();

                // The teardown hand-off, asserted where it lands. _count is zeroed under Gate and
                // the pointer gate is written through the one writer, so the doomed blocker is
                // handed CloseKeyOnly on its way out and the persistent camera flag - the half that
                // does not lapse on its own - is cleared with it.
                Assert.Equal(UiMaskScope.CloseKeyOnly, doomed.Applied);
                Assert.False(doomed.BlocksCameraRotation);

                blocker = new RecordingBlocker();
                UiInputCapture.Use(blocker);
            }),
        };

        UiApi.ResetForTests();
        try
        {
            const int depth = 5;
            int total = (int)Math.Pow(ops.Length, depth);
            var trail = new string[depth];

            for (int seq = 0; seq < total; seq++)
            {
                blocker = new RecordingBlocker();
                UiInputCapture.ResetForTests();
                UiInputCapture.Use(blocker);

                int n = seq;
                for (int step = 0; step < depth; step++)
                {
                    var op = ops[n % ops.Length];
                    n /= ops.Length;
                    trail[step] = op.Name;
                    op.Run();

                    string where = string.Join(" > ", trail, 0, step + 1);

                    // One answer, two copies, and they may never drift — R44 finding 1's property,
                    // now over three terms instead of one.
                    Assert.True(
                        blocker.Applied == UiInputCapture.WantedMaskScope,
                        where + ": blocker=" + blocker.Applied + " module="
                        + UiInputCapture.WantedMaskScope);

                    // R45 finding 8: the full mask is reachable only with a panel actually open.
                    bool mayBeFull = UiInputCapture.Count > 0
                        && UiInputCapture.Enabled
                        && UiInputCapture.PointerBlock;
                    Assert.True(
                        (blocker.Applied == UiMaskScope.Full) == mayBeFull,
                        where + ": applied=" + blocker.Applied + " count=" + UiInputCapture.Count
                        + " enabled=" + UiInputCapture.Enabled
                        + " pointerBlock=" + UiInputCapture.PointerBlock);

                    // And the persistent half follows it, which is the half that does not lapse.
                    Assert.Equal(
                        InputMaskPlan.For(blocker.Applied).BlockCameraRotationFallback,
                        blocker.BlocksCameraRotation);

                    Assert.DoesNotContain("DESYNC", UiInputCapture.Describe(), StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            UiApi.ResetForTests();
        }
    }

    /// <summary>
    /// Job FUI28, found by extending the walk above to cover <c>Use</c>: <b>a blocker installed
    /// while a panel is open is told its scope, even though it already claims to be at it.</b>
    ///
    /// <para>
    /// <c>Use</c>'s comment has said since FUI20 that a blocker installed mid-session must not
    /// start at whatever its own field defaulted to, and that re-stating the flag is what tells it.
    /// It did not tell it. <see cref="UiInputCapture"/> pushes through <c>ScopePush.Send</c>, which
    /// asks the blocker what it is applying and returns when it matches — and that question has no
    /// per-instance answer: <c>GameInputBlocker</c>'s backing <c>_scope</c> is a <b>static</b>
    /// volatile int (<c>GameInputBlocker.cs:147</c>, initialized to <see cref="UiMaskScope.Full"/>
    /// and read by a native callback once a frame), while <c>_cameraBlocked</c> — the persistent
    /// half, the one that does not lapse — is a field on the instance. A blocker constructed while
    /// the module wants <see cref="UiMaskScope.Full"/> therefore answers Full, matches, and is
    /// handed nothing, with its own camera flag still false. <see cref="RecordingBlocker"/> models
    /// both fields the way production has them, which is why the walk fails on this state rather
    /// than passing over it.
    /// </para>
    ///
    /// <para>
    /// Out of reach on the shipped path — <c>Ui.Install</c> installs its blocker with no panel
    /// open, so the wanted scope is <see cref="UiMaskScope.CloseKeyOnly"/> and differs from the
    /// static — and fixed anyway, because a redundancy check whose doc says every push is
    /// self-correcting may not be right only for the pushes that happen to have been made already.
    /// </para>
    /// </summary>
    [Fact]
    public void A_blocker_installed_while_a_panel_is_open_is_told_the_scope_it_already_claims()
    {
        UiApi.ResetForTests();
        try
        {
            var first = new RecordingBlocker();
            UiInputCapture.Use(first);
            UiInputCapture.Acquire();

            Assert.Equal(UiMaskScope.Full, first.Applied);
            Assert.True(first.BlocksCameraRotation);

            // The state a fresh GameInputBlocker is really in: the scope says Full because the
            // field behind it is static, and the camera flag says false because that one is not.
            var second = new RecordingBlocker();
            Assert.Equal(UiMaskScope.Full, second.AppliedScope);
            Assert.False(second.BlocksCameraRotation);

            UiInputCapture.Use(second);

            Assert.Equal(1, second.ScopeCalls);
            Assert.Equal(UiMaskScope.Full, second.Applied);
            Assert.True(second.BlocksCameraRotation);
            Assert.Equal(UiInputCapture.WantedMaskScope, second.Applied);
        }
        finally
        {
            UiApi.ResetForTests();
        }
    }

    // ---- the diagnostic's own blind spot (job FUI27, R45 finding 21) --------------------------

    /// <summary>
    /// <see cref="UiInputCapture.MaskScope"/> degrades to the module's own decision when the
    /// blocker's field read throws, which is right for a caller that needs an answer and wrong for
    /// the one line whose job is to say whether the two copies agree: <see cref="Describe"/> then
    /// printed <c>scope=Full wanted=Full</c> and no <c>DESYNC</c> — the second value wearing the
    /// first value's label — in the one state where nothing at all is known about the copy the game
    /// reads. A reader greps for <c>DESYNC</c>, does not find it, and concludes agreement.
    ///
    /// <para>
    /// The instrument built to end that disease, with the disease in its own fallback. "I could not
    /// tell" is not "it is false".
    /// </para>
    /// </summary>
    [Fact]
    public void The_pin_line_reports_an_unreadable_scope_as_unreadable_and_as_a_desync()
    {
        UiApi.ResetForTests();
        UiApi.SetInputBlockerForTests(new UnreadableScopeBlocker());
        try
        {
            UiInputCapture.Acquire();

            string line = UiInputCapture.Describe();

            Assert.Contains("scope=UNREADABLE", line, StringComparison.Ordinal);
            Assert.Contains("wanted=Full", line, StringComparison.Ordinal);
            Assert.Contains("DESYNC", line, StringComparison.Ordinal);

            // The label is not the value: nothing in the line may claim the blocker is at Full.
            Assert.DoesNotContain("scope=Full", line, StringComparison.Ordinal);

            // And the accessor says so to a caller that asks, while MaskScope still degrades for
            // the callers that only need an answer.
            Assert.False(UiInputCapture.TryGetAppliedScope(out UiMaskScope scope));
            Assert.Equal(UiInputCapture.WantedMaskScope, scope);
            Assert.Equal(UiInputCapture.WantedMaskScope, UiInputCapture.MaskScope);
        }
        finally
        {
            UiApi.ResetForTests();
        }
    }

    /// <summary>
    /// The structural half, and the reason findings 1 and 4 should not recur: the field is written
    /// in exactly one place. Three methods used to write it and only one of them told the blocker,
    /// which is a defect nobody can see in a diff of any one of the three. A grep is the check the
    /// review said was one step from catching it, so the grep is the test.
    /// </summary>
    [Fact]
    public void The_pointer_flag_has_exactly_one_writer()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Progenitor.Core", "Ui", "UiInputCapture.cs"));

        string[] writes = source
            .Split('\n')
            .Select(l => l.Trim())
            // Not the declaration, and not the doc comments that talk about it — a write is a
            // statement.
            .Where(l => !l.StartsWith("///", StringComparison.Ordinal))
            .Where(l => !l.StartsWith("private static bool _pointerBlock", StringComparison.Ordinal))
            .Where(l => l.Contains("_pointerBlock =", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(new[] { "_pointerBlock = blocked;" }, writes);

        // And that one writer is the one that hands back a push, so a write and a push cannot be
        // separated by an edit that only touches one of them.
        //
        // AssertStatementExactly, not Assert.Contains (job FUI28, the defect five jobs hit today):
        // a pin over source text is satisfied by the pinned line commented out and by the pinned
        // call wrapped in a ternary, and both of those are the edit these pins exist to catch.
        string writer = MethodBody(
            source, "private static ScopePush WritePointerBlockLocked(bool blocked, bool force = false)");
        SourcePin.AssertStatementExactly(writer, "_pointerBlock = blocked;");
        SourcePin.AssertStatementExactly(writer, "return ScopeLocked(force);");

        // The declaration itself does not count as a write, and this is what makes the assertion
        // above meaningful rather than accidental.
        SourcePin.AssertStatement(source, "private static bool _pointerBlock = true;");

        // Job FUI27. The scope is no longer ScopeFor(_pointerBlock): it is derived from the pointer
        // gate AND the wanted state, so that no caller — in this file or in UiHoverGate — can hand
        // the blocker the full mask at a moment when no panel is open (R45 finding 8). That is a
        // property of the derivation only while there is exactly one derivation, so the same grep
        // idiom pins it: one call to ScopeFor in this file, inside the method that names the terms.
        string[] derivations = source
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("///", StringComparison.Ordinal))
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal))
            .Where(l => l.Contains("InputMaskPlan.ScopeFor(", StringComparison.Ordinal))
            .ToArray();

        // P7b: the term is the panels' share of the wanted state (PanelsWantedLocked), so a close-key hold — which also
        // registers the context — can never be what asks for the full mask.
        Assert.Equal(
            new[] { "InputMaskPlan.ScopeFor(_pointerBlock && PanelsWantedLocked());" },
            derivations);
        SourcePin.AssertStatement(source, "private static bool PanelsWantedLocked() => _enabled && _count > 0;");

        SourcePin.AssertStatement(source, "private static ScopePush ScopeLocked(bool force = false) =>");
        SourcePin.AssertStatementExactly(source, "new(_blocker, EffectiveScopeLocked(), force);");

        // Job FUI28. Exactly one push in this file skips ScopePush.Send's redundancy check, and it
        // is Use's: AppliedScope reads GameInputBlocker's STATIC _scope, so it cannot say whether
        // the instance being installed has been told anything, and the one call Use exists to make
        // was the one the check swallowed. The doc on Send says "the only forced push in the file",
        // so that is a grep the suite runs rather than a sentence it hopes stays true.
        string useBody = MethodBody(source, "public static void Use(IUiInputBlocker? blocker)");
        SourcePin.AssertStatementExactly(
            useBody, "push = WritePointerBlockLocked(_pointerBlock, force: true);");

        string[] forced = source
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("///", StringComparison.Ordinal))
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal))
            .Where(l => l.Contains("force: true", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            new[] { "push = WritePointerBlockLocked(_pointerBlock, force: true);" },
            forced);
    }

    /// <summary>
    /// Records what it was told, and can be pushed out of step on purpose. The production
    /// <c>GameInputBlocker</c> answers <c>AppliedScope</c> from the same volatile int its native
    /// mask reads, which is the whole point: the fake and the real one are honest about the same
    /// quantity.
    /// </summary>
    private sealed class RecordingBlocker : IUiInputBlocker
    {
        public bool IsRegistered { get; private set; }

        /// <summary>
        /// <b>Modeled from the scope, exactly as production does</b> (job FUI27, R45 finding 8).
        /// <c>GameInputBlocker.SetMaskScope</c> ends in
        /// <c>SetCameraBlock(context, InputMaskPlan.For(scope).BlockCameraRotationFallback)</c>,
        /// which writes the context object's <i>persistent</i> <c>_BlockRotateCamera</c>. A fake
        /// that answered a constant <c>false</c> here — as this one used to — cannot represent the
        /// half of a scope push that does not lapse on its own, which is precisely the half finding
        /// 8 is about and the reason the standing rule says never to short-circuit on it.
        /// </summary>
        public bool BlocksCameraRotation { get; private set; }

        /// <summary>
        /// The game's <c>_DenyModificationsOfInputContexts</c>. Settable, because the window
        /// finding 8 is about is reachable only when a removal does <b>not</b> land on the same
        /// pass, and this is one of the two ways that happens.
        /// </summary>
        public bool DenyModifications { get; set; }

        /// <summary>The other way: <c>RemoveInputContext</c> ran and the entry is still listed.</summary>
        public bool RefuseRemove { get; set; }

        public UiMaskScope Applied { get; private set; } = UiMaskScope.Full;

        public UiMaskScope AppliedScope => Applied;

        public bool MaskLive => true;

        public int ScopeCalls { get; private set; }

        public int RemoveCalls { get; private set; }

        /// <summary>Move the blocker's scope without telling the module — the desynced state.</summary>
        public void ForceScope(UiMaskScope scope) => Applied = scope;

        public bool Register()
        {
            if (DenyModifications) return false;
            IsRegistered = true;
            BlocksCameraRotation = InputMaskPlan.For(Applied).BlockCameraRotationFallback;
            return true;
        }

        public bool Remove()
        {
            RemoveCalls++;
            if (RefuseRemove)
            {
                // The contract: a refused removal leaves the still-listed entry inert.
                BlocksCameraRotation = false;
                return false;
            }

            IsRegistered = false;
            BlocksCameraRotation = false;
            return true;
        }

        public void SetMaskScope(UiMaskScope scope)
        {
            ScopeCalls++;
            Applied = scope;
            BlocksCameraRotation = InputMaskPlan.For(scope).BlockCameraRotationFallback;
        }

        public void Dispose() => IsRegistered = false;
    }

    /// <summary>
    /// A blocker whose <see cref="IUiInputBlocker.AppliedScope"/> throws — the state R45 finding 21
    /// is about. Everything else answers, so the only thing the diagnostic cannot see is the one
    /// value it exists to report.
    /// </summary>
    private sealed class UnreadableScopeBlocker : IUiInputBlocker
    {
        public bool IsRegistered { get; private set; }

        public bool BlocksCameraRotation => false;

        public bool DenyModifications => false;

        public UiMaskScope AppliedScope => throw new InvalidOperationException("scope unreadable");

        public bool MaskLive => true;

        public bool Register()
        {
            IsRegistered = true;
            return true;
        }

        public bool Remove()
        {
            IsRegistered = false;
            return true;
        }

        public void SetMaskScope(UiMaskScope scope)
        {
        }

        public void Dispose() => IsRegistered = false;
    }

    // ---- the one claim only the source can carry ---------------------------------------------

    /// <summary>
    /// <c>GameInputBlocker.Mask</c> is a native postfix in <c>#if !PROGENITOR_TESTS</c> code that
    /// this assembly cannot call, and it is the place the plan is actually applied. The rule that
    /// matters — both close actions blocked, on every path through the method, not inside the
    /// scope conditionals — is therefore checked against the source, in the idiom
    /// <c>PanelLayoutTests</c> established for the row builders.
    /// </summary>
    [Fact]
    public void The_native_mask_blocks_both_close_actions_unconditionally()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Progenitor.Core", "Ui", "GameInputBlocker.cs"));

        string body = MethodBody(source, "private static void Mask(");

        // Unconditional means: at the method's own brace depth, not nested inside an `if`. The
        // depth of the Mask body itself is 1, so the loop that issues the BlockKey calls — and the
        // read of the list it walks — must sit at exactly that depth.
        foreach (string line in new[]
                 {
                     "int[] closes = InputMaskPlan.BlockedCloseActions();",
                     "for (int i = 0; i < closes.Length; i++)",
                 })
        {
            int at = body.IndexOf(line, StringComparison.Ordinal);
            Assert.True(at > 0, line + " is missing");
            Assert.Equal(2, Depth(body, at));
        }

        SourcePin.AssertStatement(body, "state.BlockKey((ButtonInputAction)closes[i]);");

        // BlockAllKeys is the one that is conditional, and it never asks for UI navigation.
        SourcePin.AssertStatement(body, "state.BlockAllKeys(plan.IncludeUiNavigation);");
        Assert.DoesNotContain("BlockAllKeys(true)", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Job FUI27, R45 finding 28: <c>BlockedCloseActions</c> was documented as the protection
    /// against a future scope quietly dropping the close key, and no production code read it —
    /// <c>Mask</c> spelled the two <c>BlockKey</c> calls by hand. A third close action added to the
    /// documented list would not have been blocked, and the only test that would have noticed was
    /// an <c>Assert.Equal(2, …Length)</c>, which fails for the array's length rather than for the
    /// behavior. A documented guarantee with no enforcement is worse than no claim, because it is
    /// believed.
    ///
    /// <para>
    /// So the enforcement is that the hard-coded spellings are <b>gone</b>, not merely that a loop
    /// exists beside them. This is the assertion that fails if the fix is reverted.
    /// </para>
    /// </summary>
    [Fact]
    public void The_native_mask_iterates_the_documented_close_action_list()
    {
        string body = MethodBody(
            File.ReadAllText(
                Path.Combine(FindRepoRoot(), "src", "Progenitor.Core", "Ui", "GameInputBlocker.cs")),
            "private static void Mask(");

        SourcePin.AssertUncommented(body, "InputMaskPlan.BlockedCloseActions()");
        Assert.DoesNotContain("state.BlockKey(ButtonInputAction.Back)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ButtonInputActions.MenuClose", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of finding 28: <c>Back</c> used to be spelled as the <b>interop enum
    /// member</b> and <c>Menu_Close</c> as a cast of our own dump-read constant, so if a game update
    /// renumbered <c>ButtonInputAction.Back</c> the mask blocked whatever the interop said while
    /// <see cref="The_close_actions_are_the_ones_the_dump_names"/> went on asserting 17 — and if it
    /// renumbered <c>Menu_Close</c> the mask blocked a stale 1004 and that test still passed. One of
    /// the two values the whole of defect 1 rests on was cross-checked and the other was not, and
    /// which was which was invisible. The mask is uniformly the constants now, so the one comparison
    /// that can still be made has to be made somewhere and said out loud.
    /// </summary>
    [Fact]
    public void The_interop_enum_is_cross_checked_against_the_dump_read_constant_at_install()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Progenitor.Core", "Ui", "GameInputBlocker.cs"));

        string check = MethodBody(source, "private static void CheckCloseActionIdsLocked()");
        SourcePin.AssertUncommented(check, "(int)ButtonInputAction.Back");
        SourcePin.AssertUncommented(check, "ButtonInputActions.Back");
        SourcePin.AssertUncommented(check, "LogWarning");

        // And it is actually called, from the one place that runs once per detour install. CHAT1 split the
        // install out of EnsureMask so the chat click guard can arm it too; EnsureMask goes through it.
        SourcePin.AssertStatement(MethodBody(source, "private static void EnsureDetourLocked()"), "CheckCloseActionIdsLocked();");
        SourcePin.AssertStatement(MethodBody(source, "private static void EnsureMask(RotateCameraBlocker context)"), "EnsureDetourLocked();");
    }

    /// <summary>
    /// The registration half: the context is pushed at our own order rather than through
    /// <c>RotateCameraBlocker.Register</c>'s hard-coded 600, and removed with the matching call —
    /// <c>Deregister</c> would be a silent no-op behind a <c>_Registered</c> flag we never set,
    /// which would leak a context into the game's list.
    /// </summary>
    [Fact]
    public void The_context_is_pushed_and_popped_with_the_calls_that_match()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Progenitor.Core", "Ui", "GameInputBlocker.cs"));

        string register = MethodBody(source, "public bool Register()");
        SourcePin.AssertUncommented(register, "system.AddInputContext(");
        SourcePin.AssertUncommented(register, "UiInputCapture.Order");
        Assert.DoesNotContain("context.Register(", register, StringComparison.Ordinal);

        string remove = MethodBody(source, "public bool Remove()");
        SourcePin.AssertUncommented(remove, "system.RemoveInputContext(");
        Assert.DoesNotContain("context.Deregister(", remove, StringComparison.Ordinal);
    }

    private static int Depth(string source, int index)
    {
        int depth = 0;
        for (int i = 0; i < index; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}') depth--;
        }

        return depth;
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "not found: " + signature);
        int open = source.IndexOf('{', start);
        Assert.True(open > start, "no body for " + signature);

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(open, i - open + 1);
            }
        }

        Assert.Fail("unbalanced braces after " + signature);
        return string.Empty;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Progenitor.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
