using System;
using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Owned text-input path: caret, selection, Backspace, replace, shortcut suppression,
/// and exactly-once change notification. Game-free — UnityUiRuntime is compiled out of
/// this assembly, so these tests drive <see cref="UiTextEdit"/> / <see cref="UiTextChange"/>
/// rather than pinning that <c>ApplyTypedChar</c> exists.
/// </summary>
public class UiTextEditTests
{
    [Theory]
    [InlineData(false, "Test-admin")]
    [InlineData(true, "Test_admin")]
    public void Minus_respects_shift_in_a_mod_author_search(bool shift, string expected)
    {
        var result = UiTypedKeys.Apply(new UiTextEditState("Testadmin", 4, 4),
            UiKey.Minus, ctrl: false, alt: false, meta: false, shift: shift, clipboard: null);
        Assert.Equal(expected, result.State.Text);
        Assert.Equal(5, result.State.Caret);
    }
    [Fact]
    public void Mid_string_backspace_deletes_the_character_before_the_caret_not_the_last_character()
    {
        // "abcd" with the caret after "b" (index 2). The broken path yielded "abc".
        var before = new UiTextEditState("abcd", caret: 2, anchor: 2);
        var after = UiTextEdit.Backspace(before);
        Assert.Equal("acd", after.Text);
        Assert.Equal(1, after.Caret);
        Assert.Equal(1, after.Anchor);
        Assert.False(after.HasSelection);
        Assert.NotEqual("abc", after.Text);
    }

    [Fact]
    public void Backspace_on_a_selection_deletes_the_selection_not_the_last_character()
    {
        var before = new UiTextEditState("abcd", caret: 3, anchor: 1); // "bc"
        var after = UiTextEdit.Backspace(before);
        Assert.Equal("ad", after.Text);
        Assert.Equal(1, after.Caret);
        Assert.False(after.HasSelection);
    }

    [Fact]
    public void Printable_insertion_replaces_an_active_selection()
    {
        var before = new UiTextEditState("abcd", caret: 3, anchor: 1); // "bc"
        var after = UiTextEdit.Insert(before, "X");
        Assert.Equal("aXd", after.Text);
        Assert.Equal(2, after.Caret);
        Assert.False(after.HasSelection);

        var typed = UiTextEdit.TypeChar(before, 'z');
        Assert.Equal("azd", typed.State.Text);
        Assert.True(typed.TextChanged);
    }

    [Fact]
    public void Insertion_at_the_caret_without_a_selection_does_not_jump_to_the_end()
    {
        var before = new UiTextEditState("acd", caret: 1, anchor: 1);
        var after = UiTextEdit.Insert(before, "b");
        Assert.Equal("abcd", after.Text);
        Assert.Equal(2, after.Caret);
    }

    [Fact]
    public void Ctrl_Alt_or_Meta_suppresses_printable_insertion()
    {
        Assert.True(UiTextEdit.SuppressPrintable(ctrl: true, alt: false, meta: false));
        Assert.True(UiTextEdit.SuppressPrintable(ctrl: false, alt: true, meta: false));
        Assert.True(UiTextEdit.SuppressPrintable(ctrl: false, alt: false, meta: true));
        Assert.False(UiTextEdit.SuppressPrintable(ctrl: false, alt: false, meta: false));
        Assert.False(UiTextEdit.SuppressPrintable(ctrl: false, alt: false, meta: false));
        // Shift alone is how capitals are typed.
        Assert.False(UiTextEdit.SuppressPrintable(false, false, false));
    }

    [Fact]
    public void Ctrl_A_selects_all_and_does_not_insert_a()
    {
        var before = new UiTextEditState("ab", caret: 2, anchor: 2);
        var result = UiTextEdit.ApplyShortcut(before, 'a', clipboard: null);
        Assert.Equal("ab", result.State.Text);
        Assert.True(result.State.HasSelection);
        Assert.Equal(0, result.State.Anchor);
        Assert.Equal(2, result.State.Caret);
        Assert.False(result.TextChanged);
        Assert.True(result.Consumed);
    }

    [Fact]
    public void Ctrl_V_pastes_and_does_not_insert_v()
    {
        var before = new UiTextEditState("ab", caret: 2, anchor: 2);
        var result = UiTextEdit.ApplyShortcut(before, 'v', clipboard: "cd");
        Assert.Equal("abcd", result.State.Text);
        Assert.DoesNotContain("v", result.State.Text);
        Assert.True(result.TextChanged);

        var selected = UiTextEdit.SelectAll(before);
        var replaced = UiTextEdit.ApplyShortcut(selected, 'v', clipboard: "xy");
        Assert.Equal("xy", replaced.State.Text);
    }

    [Fact]
    public void Ctrl_C_and_Ctrl_X_use_the_selection()
    {
        var before = new UiTextEditState("abcd", caret: 3, anchor: 1);
        var copy = UiTextEdit.Copy(before);
        Assert.Equal("bc", copy.ClipboardSet);
        Assert.Equal("abcd", copy.State.Text);

        var cut = UiTextEdit.Cut(before);
        Assert.Equal("bc", cut.ClipboardSet);
        Assert.Equal("ad", cut.State.Text);
        Assert.True(cut.TextChanged);
    }

    [Fact]
    public void An_unknown_ctrl_letter_is_consumed_and_does_not_type()
    {
        var before = new UiTextEditState("ab", caret: 2, anchor: 2);
        var result = UiTextEdit.ApplyShortcut(before, 'q', clipboard: null);
        Assert.Equal("ab", result.State.Text);
        Assert.True(result.Consumed);
        Assert.False(result.TextChanged);
    }

    [Fact]
    public void Change_callback_runs_exactly_once_when_the_setter_already_raises()
    {
        int count = 0;
        string last = "";
        void OnChanged(string v)
        {
            count++;
            last = v;
        }

        int n = UiTextChange.Apply(
            "abcd",
            "acd",
            setText: v => OnChanged(v),
            invokeChanged: OnChanged,
            raiseCount: () => count);

        Assert.Equal(1, n);
        Assert.Equal(1, count);
        Assert.Equal("acd", last);
    }

    [Fact]
    public void Change_callback_runs_exactly_once_when_the_setter_is_silent()
    {
        int count = 0;
        string held = "abcd";
        int n = UiTextChange.Apply(
            "abcd",
            "acd",
            setText: v => held = v,
            invokeChanged: v =>
            {
                count++;
                held = v;
            },
            raiseCount: () => count);

        Assert.Equal(1, n);
        Assert.Equal(1, count);
        Assert.Equal("acd", held);
    }

    [Fact]
    public void Unchanged_text_does_not_raise_even_if_the_setter_is_called()
    {
        int count = 0;
        int sets = 0;
        int n = UiTextChange.Apply(
            "ab",
            "ab",
            setText: _ => sets++,
            invokeChanged: _ => count++,
            raiseCount: () => count);

        Assert.Equal(0, n);
        Assert.Equal(0, count);
        Assert.Equal(1, sets);
    }

    [Fact]
    public void A_second_successful_invoke_does_not_throw_and_is_still_a_second_callback()
    {
        int count = 0;
        Action<string> onChanged = _ => count++;
        onChanged("a");
        onChanged("a");
        Assert.Equal(2, count);

        count = 0;
        int n = UiTextChange.Apply(
            "a",
            "b",
            setText: v => onChanged(v),
            invokeChanged: onChanged,
            raiseCount: () => count);
        Assert.Equal(1, n);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Left_and_right_collapse_a_selection_without_shift()
    {
        var selected = new UiTextEditState("abcd", caret: 3, anchor: 1);
        var left = UiTextEdit.Left(selected, extend: false);
        Assert.Equal(1, left.Caret);
        Assert.False(left.HasSelection);
        var right = UiTextEdit.Right(selected, extend: false);
        Assert.Equal(3, right.Caret);
        Assert.False(right.HasSelection);
    }

    [Fact]
    public void Character_limit_truncates_an_insert_rather_than_growing_past_it()
    {
        var before = new UiTextEditState("ab", caret: 2, anchor: 2);
        var after = UiTextEdit.Insert(before, "cdef", maxLength: 3);
        Assert.Equal("abc", after.Text);
        Assert.Equal(3, after.Caret);
    }

    [Fact]
    public void Typing_BloodPool_one_key_at_a_time_yields_blood_not_doubled_or_replaced()
    {
        // Live client reversed this into "doolb" because field.text snaps the native caret
        // to 0 and the next key re-read that 0. The owned session is what production reads.
        UiTextEditState? owned = null;
        string text = "";
        int changes = 0;
        var keys = new[] { UiKey.B, UiKey.L, UiKey.O, UiKey.O, UiKey.D };
        foreach (var key in keys)
        {
            var result = UiFieldCaret.Apply(
                text, owned, key, ctrl: false, alt: false, meta: false, shift: false, clipboard: null,
                int.MaxValue, out var nativeAfterAssign);
            Assert.True(result.TextChanged);
            Assert.Equal(1, result.State.Text.Length - text.Length);
            Assert.Equal(0, nativeAfterAssign.Caret);
            Assert.Equal(0, nativeAfterAssign.Anchor);
            Assert.NotEqual(0, result.State.Caret);
            Assert.Equal(result.State.Text.Length, result.State.Caret);
            Assert.False(result.State.Text.Length == 2 && result.State.Text[0] == result.State.Text[1]);
            text = result.State.Text;
            owned = result.State;
            changes++;
        }

        Assert.Equal("blood", text);
        Assert.Equal(5, owned!.Value.Caret);
        Assert.Equal(5, changes);
        Assert.NotEqual("doolb", text);
        Assert.NotEqual("bb", text);
        Assert.NotEqual("ll", text);
        Assert.NotEqual("dd", text);
        Assert.True(UiTypedKeys.NativeTmpProcessingDisabled);
    }

    [Fact]
    public void Re_reading_the_native_caret_after_each_assign_reverses_blood_into_doolb()
    {
        // The live field: each field.text assign pins the caret at 0, so the next
        // character is inserted in front. A string-only assertion on a palindrome
        // would miss this; "blood" is not symmetric.
        string text = "";
        var keys = new[] { UiKey.B, UiKey.L, UiKey.O, UiKey.O, UiKey.D };
        foreach (var key in keys)
        {
            var pinned = UiFieldCaret.NativeAfterTextAssign(text);
            Assert.Equal(0, pinned.Caret);
            var result = UiTypedKeys.Apply(
                pinned, key, ctrl: false, alt: false, meta: false, shift: false, clipboard: null);
            text = result.State.Text;
        }

        Assert.Equal("doolb", text);
        Assert.NotEqual("blood", text);
    }

    [Fact]
    public void Typing_into_the_middle_inserts_at_the_owned_caret_and_advances_it()
    {
        UiTextEditState? owned = new UiTextEditState("", 0, 0);
        string text = "";
        foreach (var key in new[] { UiKey.A, UiKey.B, UiKey.C, UiKey.D })
        {
            var result = UiFieldCaret.Apply(
                text, owned, key, false, false, false, false, null, int.MaxValue, out _);
            text = result.State.Text;
            owned = result.State;
        }

        Assert.Equal("abcd", text);
        Assert.Equal(4, owned!.Value.Caret);

        var left = UiFieldCaret.Apply(
            text, owned, UiKey.LeftArrow, false, false, false, false, null, int.MaxValue, out var nativeLeft);
        Assert.Equal(0, nativeLeft.Caret);
        Assert.Equal("abcd", left.State.Text);
        Assert.Equal(3, left.State.Caret);
        text = left.State.Text;
        owned = left.State;

        var left2 = UiFieldCaret.Apply(
            text, owned, UiKey.LeftArrow, false, false, false, false, null, int.MaxValue, out _);
        text = left2.State.Text;
        owned = left2.State;
        Assert.Equal(2, owned.Value.Caret);

        var insert = UiFieldCaret.Apply(
            text, owned, UiKey.X, false, false, false, shift: true, null, int.MaxValue, out var nativeInsert);
        Assert.Equal(0, nativeInsert.Caret);
        Assert.Equal("abXcd", insert.State.Text);
        Assert.Equal(3, insert.State.Caret);
        Assert.False(insert.State.HasSelection);
    }

    [Fact]
    public void Backspace_after_b_in_abcd_yields_acd_with_the_caret_after_a()
    {
        var state = new UiTextEditState("abcd", caret: 2, anchor: 2);
        var result = UiTypedKeys.Apply(
            state, UiKey.Backspace, ctrl: false, alt: false, meta: false, shift: false, clipboard: null);
        Assert.Equal("acd", result.State.Text);
        Assert.Equal(1, result.State.Caret);
        Assert.False(result.State.HasSelection);
        Assert.True(result.TextChanged);
    }

    [Fact]
    public void Backspace_at_position_0_does_nothing()
    {
        var state = new UiTextEditState("abcd", caret: 0, anchor: 0);
        var result = UiTypedKeys.Apply(
            state, UiKey.Backspace, ctrl: false, alt: false, meta: false, shift: false, clipboard: null);
        Assert.Equal("abcd", result.State.Text);
        Assert.Equal(0, result.State.Caret);
        Assert.False(result.TextChanged);
        Assert.True(result.Consumed);
    }

    [Fact]
    public void Backspace_with_an_active_selection_deletes_the_selection()
    {
        var state = new UiTextEditState("abcd", caret: 3, anchor: 1);
        var result = UiTypedKeys.Apply(
            state, UiKey.Backspace, ctrl: false, alt: false, meta: false, shift: false, clipboard: null);
        Assert.Equal("ad", result.State.Text);
        Assert.Equal(1, result.State.Caret);
        Assert.False(result.State.HasSelection);
        Assert.True(result.TextChanged);
    }

    [Fact]
    public void Delete_is_wired_and_removes_the_character_after_the_caret()
    {
        var state = new UiTextEditState("abcd", caret: 1, anchor: 1);
        var command = UiTypedKeys.Decide(UiKey.Delete, false, false, false, false);
        Assert.Equal(UiTypedCommandKind.Delete, command.Kind);
        var result = UiTypedKeys.Apply(
            state, UiKey.Delete, ctrl: false, alt: false, meta: false, shift: false, clipboard: null);
        Assert.Equal("acd", result.State.Text);
        Assert.Equal(1, result.State.Caret);
    }

    [Fact]
    public void Escape_clears_a_focused_non_empty_search_field_leaves_an_empty_one_and_closes_when_unfocused()
    {
        Assert.Equal(UiSearchEscape.Decision.Clear, UiSearchEscape.Decide(searchFocused: true, "blood"));
        // SEARCHCLEAR1: the second Escape leaves the field; the third closes.
        Assert.Equal(UiSearchEscape.Decision.Leave, UiSearchEscape.Decide(searchFocused: true, ""));
        Assert.Equal(UiSearchEscape.Decision.Close, UiSearchEscape.Decide(searchFocused: false, "blood"));
        Assert.Equal(UiSearchEscape.Decision.Close, UiSearchEscape.Decide(searchFocused: false, ""));
        Assert.Equal(27, UiSearchEscape.EscapeKeyCode);
        Assert.Equal(17, UiSearchEscape.BackAction);
        Assert.Equal(
            "Ui: search-escape branch=clear focused=1 length=5 reason=focused-nonempty",
            UiSearchEscape.Format(UiSearchEscape.Decision.Clear, focused: true, length: 5, alreadyCleared: false));
        Assert.Equal(
            "Ui: search-escape branch=leave focused=1 length=0 reason=empty",
            UiSearchEscape.Format(UiSearchEscape.Decision.Leave, focused: true, length: 0, alreadyCleared: false));
        Assert.Equal(
            "Ui: search-escape branch=close focused=0 length=5 reason=unfocused",
            UiSearchEscape.Format(UiSearchEscape.Decision.Close, focused: false, length: 5, alreadyCleared: false));
    }

    [Fact]
    public void Escape_sweep_survives_the_paired_back_action_and_resets_only_on_release()
    {
        var sweep = new UiEscapeSweep();
        Assert.True(sweep.TryConsume(UiSearchEscape.Decision.Clear));
        Assert.True(sweep.Cleared);
        // NotifyHiddenByEscape used to Release here. The paired BackAction then
        // saw an empty field and closed — one press both cleared and hid.
        Assert.True(sweep.TryConsume(UiSearchEscape.Decision.Close));
        Assert.True(sweep.Cleared);
        sweep.Release();
        Assert.False(sweep.TryConsume(UiSearchEscape.Decision.Close));
        Assert.False(sweep.Cleared);
        Assert.True(sweep.ShouldLog());
        Assert.False(sweep.ShouldLog());
        sweep.Release();
        Assert.True(sweep.ShouldLog());
    }

    [Fact]
    public void Two_writers_would_double_a_character_the_owned_path_inserts_once()
    {
        var empty = new UiTextEditState("", 0, 0);
        var pump = UiTextEdit.TypeChar(empty, 'b');
        Assert.Equal("b", pump.State.Text);
        var nativeAsWell = UiTextEdit.TypeChar(pump.State, 'b');
        Assert.Equal("bb", nativeAsWell.State.Text);
        Assert.True(UiTypedKeys.NativeTmpProcessingDisabled);
    }

    [Fact]
    public void Ctrl_alone_does_not_type_5_or_any_character()
    {
        // Unity Key.Digit0 = 50, LeftCtrl = 55. Digit0+i with '0'+i maps Ctrl to '5'.
        const int unityDigit0 = 50;
        const int unityLeftCtrl = 55;
        char trapped = (char)('0' + (unityLeftCtrl - unityDigit0));
        Assert.Equal('5', trapped);

        var before = new UiTextEditState("ab", caret: 2, anchor: 2);
        var result = UiTypedKeys.Apply(
            before, UiKey.LeftCtrl, ctrl: true, alt: false, meta: false, shift: false, clipboard: null);
        Assert.Equal("ab", result.State.Text);
        Assert.False(result.TextChanged);
        Assert.True(result.Consumed);
        Assert.Equal(UiTypedCommandKind.Modifier, UiTypedKeys.Decide(UiKey.LeftCtrl, true, false, false, false).Kind);
        Assert.Equal(UiTypedCommandKind.Insert, UiTypedKeys.Decide(UiKey.Digit5, false, false, false, false).Kind);
        Assert.Equal('5', UiTypedKeys.Decide(UiKey.Digit5, false, false, false, false).Char);
    }

    [Fact]
    public void Shift_alone_does_not_type_a_character()
    {
        var before = new UiTextEditState("ab", caret: 2, anchor: 2);
        var result = UiTypedKeys.Apply(
            before, UiKey.LeftShift, ctrl: false, alt: false, meta: false, shift: true, clipboard: null);
        Assert.Equal("ab", result.State.Text);
        Assert.False(result.TextChanged);
        Assert.Equal(UiTypedCommandKind.Modifier, UiTypedKeys.Decide(UiKey.LeftShift, false, false, false, true).Kind);
    }

    [Fact]
    public void Ctrl_A_selects_all_through_the_key_map_and_does_not_type_a()
    {
        var before = new UiTextEditState("ab", caret: 2, anchor: 2);
        var result = UiTypedKeys.Apply(
            before, UiKey.A, ctrl: true, alt: false, meta: false, shift: false, clipboard: null);
        Assert.Equal("ab", result.State.Text);
        Assert.True(result.State.HasSelection);
        Assert.False(result.TextChanged);
        Assert.DoesNotContain("a", result.State.Text.Replace("ab", ""));
    }

    [Fact]
    public void Ctrl_V_pastes_through_the_supported_clipboard_seam()
    {
        Assert.True(UiTypedKeys.ClipboardPasteWired);
        var before = new UiTextEditState("ab", caret: 2, anchor: 2);
        var result = UiTypedKeys.Apply(
            before, UiKey.V, ctrl: true, alt: false, meta: false, shift: false, clipboard: "cd");
        Assert.Equal("abcd", result.State.Text);
        Assert.DoesNotContain("v", result.State.Text);
    }

    [Fact]
    public void Delete_removes_the_character_after_the_caret()
    {
        var before = new UiTextEditState("abcd", caret: 1, anchor: 1);
        var after = UiTextEdit.Delete(before);
        Assert.Equal("acd", after.Text);
        Assert.Equal(1, after.Caret);
    }

    [Fact]
    public void Home_and_end_move_the_caret()
    {
        var mid = new UiTextEditState("abcd", caret: 2, anchor: 2);
        var home = UiTextEdit.Home(mid, extend: false);
        Assert.Equal(0, home.Caret);
        Assert.False(home.HasSelection);
        var end = UiTextEdit.End(mid, extend: false);
        Assert.Equal(4, end.Caret);
    }

    [Fact]
    public void Owned_caret_local_x_tracks_the_owned_index_from_characterInfo_metrics()
    {
        // characterInfo: origin of glyph i, xAdvance after it. Uniform 8-unit glyphs.
        var origins = new[] { 0f, 8f, 16f, 24f, 32f };
        var advances = new[] { 8f, 16f, 24f, 32f, 40f };
        Assert.Equal(0f, UiOwnedCaret.LocalX(0, 0, origins, advances, emptyOrigin: 0f));
        Assert.Equal(0f, UiOwnedCaret.LocalX(0, 5, origins, advances, emptyOrigin: -3f));
        Assert.Equal(8f, UiOwnedCaret.LocalX(1, 5, origins, advances, 0f));
        Assert.Equal(16f, UiOwnedCaret.LocalX(2, 5, origins, advances, 0f));
        Assert.Equal(24f, UiOwnedCaret.LocalX(3, 5, origins, advances, 0f));
        Assert.Equal(32f, UiOwnedCaret.LocalX(4, 5, origins, advances, 0f));
        Assert.Equal(40f, UiOwnedCaret.LocalX(5, 5, origins, advances, 0f));
        Assert.Equal(40f, UiOwnedCaret.LocalX(9, 5, origins, advances, 0f));
        Assert.NotEqual(0f, UiOwnedCaret.LocalX(5, 5, origins, advances, 0f));
        Assert.True(UiOwnedCaret.LocalX(2, 5, origins, advances, 0f)
            > UiOwnedCaret.LocalX(1, 5, origins, advances, 0f));
    }

    [Fact]
    public void Owned_caret_blink_is_solid_after_an_edit_then_cycles_on_and_off()
    {
        Assert.Equal(1000, UiOwnedCaret.BlinkPeriodMs);
        Assert.Equal(500, UiOwnedCaret.BlinkOnMs);
        Assert.True(UiOwnedCaret.RestartBlinkOnEdit);
        Assert.False(UiOwnedCaret.IsLit(focused: false, nowMs: 0, cycleOriginMs: 0));
        Assert.True(UiOwnedCaret.IsLit(focused: true, nowMs: 0, cycleOriginMs: 0));
        Assert.True(UiOwnedCaret.IsLit(focused: true, nowMs: UiOwnedCaret.BlinkOnMs - 1, cycleOriginMs: 0));
        Assert.False(UiOwnedCaret.IsLit(focused: true, nowMs: UiOwnedCaret.BlinkOnMs, cycleOriginMs: 0));
        Assert.False(UiOwnedCaret.IsLit(focused: true, nowMs: UiOwnedCaret.BlinkPeriodMs - 1, cycleOriginMs: 0));
        Assert.True(UiOwnedCaret.IsLit(focused: true, nowMs: UiOwnedCaret.BlinkPeriodMs, cycleOriginMs: 0));

        // Typing reset: a later keystroke restarts the cycle at on.
        const long edit = 800;
        Assert.True(UiOwnedCaret.IsLit(true, edit, cycleOriginMs: edit));
        Assert.False(UiOwnedCaret.IsLit(true, edit + UiOwnedCaret.BlinkOnMs, cycleOriginMs: edit));
    }

    [Fact]
    public void Owned_caret_inverse_transform_as_anchored_position_hangs_below_a_top_pivoted_field()
    {
        // UIC5 PlaceOwnedCaret: InverseTransformPoint of the text-line center
        // written straight into anchoredPosition. Field PlaceControl pivot is
        // (0.5, 1); caret anchors are (0.5, 0.5). Parent-local y of the line is
        // −14; the center-anchor point is also −14; the bar's center lands on
        // the bottom edge and half of HeightPx hangs out. Drawable-only passes.
        const float fieldH = UiOwnedCaret.FieldHeightPx;
        const float rectYMin = -fieldH;
        const float rectYMax = 0f;
        float lineLocalY = -fieldH * 0.5f;
        float brokenCenter = UiOwnedCaret.CenterFromAnchored(rectYMin, rectYMax, 0.5f, lineLocalY);
        Assert.Equal(-fieldH, brokenCenter);
        float brokenTop = UiOwnedCaret.TopDownFromParentCenter(brokenCenter, UiOwnedCaret.HeightPx);
        Assert.False(UiOwnedCaret.ContainedVertically(
            brokenTop, UiOwnedCaret.HeightPx, fieldTop: 0f, fieldHeight: fieldH));
        Assert.False(UiOwnedCaret.OnTextLine(
            brokenTop, UiOwnedCaret.HeightPx, lineCenter: fieldH * 0.5f, UiOwnedCaret.LineCenterTolerancePx));
        Assert.False(ModsMenu.OwnedCaretContainedVertically(
            brokenTop, UiOwnedCaret.HeightPx, 0f, fieldH));

        UiOwnedCaret.AnchoredFromParentLocal(
            0f, lineLocalY, -150f, rectYMin, 150f, rectYMax, 0.5f, 0.5f,
            out float anchoredX, out float anchoredY);
        Assert.Equal(0f, anchoredX);
        Assert.Equal(0f, anchoredY);
        float fixedCenter = UiOwnedCaret.CenterFromAnchored(rectYMin, rectYMax, 0.5f, anchoredY);
        Assert.Equal(lineLocalY, fixedCenter);
        float fixedTop = UiOwnedCaret.TopDownFromParentCenter(fixedCenter, UiOwnedCaret.HeightPx);
        Assert.Equal((float)UiOwnedCaret.PadYPx, fixedTop);
        Assert.True(UiOwnedCaret.ContainedVertically(
            fixedTop, UiOwnedCaret.HeightPx, 0f, fieldH));
        Assert.True(UiOwnedCaret.OnTextLine(
            fixedTop, UiOwnedCaret.HeightPx, fieldH * 0.5f, UiOwnedCaret.LineCenterTolerancePx));
        Assert.True(ModsMenu.OwnedCaretOnTextLine(
            fixedTop, UiOwnedCaret.HeightPx, fieldH * 0.5f));
    }

    [Fact]
    public void Owned_caret_line_center_y_is_the_text_rect_midpoint_not_the_glyph_baseline()
    {
        Assert.Equal(UiOwnedCaret.FieldHeightPx - 2 * UiOwnedCaret.PadYPx, UiOwnedCaret.HeightPx);
        Assert.Equal(0f, UiOwnedCaret.LineCenterY(-10f, 10f));
        Assert.Equal(-4f, UiOwnedCaret.LineCenterY(-14f, 6f));
        Assert.NotEqual(-10f, UiOwnedCaret.LineCenterY(-10f, 10f));
        Assert.Equal(20f, UiOwnedCaret.ClampHeight(40f, clipHeight: 20f));
        Assert.Equal(20f, UiOwnedCaret.ClampHeight(0f, clipHeight: 20f));
        Assert.Equal(0f, UiOwnedCaret.ClampCenter(0f, 20f, clipMin: -10f, clipMax: 10f));
        Assert.Equal(0f, UiOwnedCaret.ClampCenter(-8f, 20f, clipMin: -10f, clipMax: 10f));
    }

    [Fact]
    public void Owned_caret_is_drawable_only_when_focused_with_a_real_graphic()
    {
        Assert.True(UiOwnedCaret.IsDrawable(
            focused: true, assigned: true, active: true, graphicEnabled: true,
            alpha: 1f, rectW: UiOwnedCaret.WidthPx, rectH: UiOwnedCaret.HeightPx,
            siblingIndex: 1, fillSiblingIndex: -1, meshCount: 1));
        Assert.False(UiOwnedCaret.IsDrawable(
            focused: false, assigned: true, active: true, graphicEnabled: true,
            alpha: 1f, rectW: UiOwnedCaret.WidthPx, rectH: UiOwnedCaret.HeightPx,
            siblingIndex: 1, fillSiblingIndex: -1, meshCount: 1));
        Assert.False(UiOwnedCaret.IsDrawable(
            focused: true, assigned: true, active: true, graphicEnabled: true,
            alpha: 0f, rectW: UiOwnedCaret.WidthPx, rectH: UiOwnedCaret.HeightPx,
            siblingIndex: 1, fillSiblingIndex: -1, meshCount: 1));
        Assert.False(UiOwnedCaret.IsDrawable(
            focused: true, assigned: true, active: true, graphicEnabled: true,
            alpha: 1f, rectW: 0f, rectH: UiOwnedCaret.HeightPx,
            siblingIndex: 1, fillSiblingIndex: -1, meshCount: 1));
        Assert.False(UiOwnedCaret.IsDrawable(
            focused: true, assigned: true, active: true, graphicEnabled: true,
            alpha: 1f, rectW: UiOwnedCaret.WidthPx, rectH: UiOwnedCaret.HeightPx,
            siblingIndex: 1, fillSiblingIndex: -1, meshCount: 0));
        Assert.False(UiOwnedCaret.IsDrawable(
            focused: true, assigned: true, active: false, graphicEnabled: true,
            alpha: 1f, rectW: UiOwnedCaret.WidthPx, rectH: UiOwnedCaret.HeightPx,
            siblingIndex: 1, fillSiblingIndex: -1, meshCount: 1));
    }
}
