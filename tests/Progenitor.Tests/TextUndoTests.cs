using System;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// TEXTUNDO1: per-field undo and redo (<see cref="UiTextUndoHistory"/>) — word-sized typing steps,
/// Backspace runs, paste and cut as their own steps, the caret restored with the text, redo dropped by
/// a new edit, the entry and character bounds, the filter and limit check on a restored text, and the
/// lifetime rules. Also the polish fixes that shipped with it: the commit trim (<see cref="UiCommitText"/>),
/// the read-only caret, and the editor bar's value/fraction mapping. Game-free: the edits run through
/// <see cref="UiTypedKeys"/> exactly as the shown-input pump runs them.
/// </summary>
public sealed class TextUndoTests
{
    private long _now = 10_000;

    private UiTextEditState _state = new("", 0, 0);

    private readonly UiTextUndoHistory _history = new();

    /// <summary>Type <paramref name="text"/> one character at a time, <paramref name="gapMs"/> apart.</summary>
    private void Type(string text, long gapMs = 50)
    {
        foreach (char c in text)
        {
            _now += gapMs;
            Key(UiTypedCommand.Insert(c));
        }
    }

    /// <summary>One decided key, recorded with the kind the pump records it with.</summary>
    private void Key(UiTypedCommand command, bool shift = false, string? clipboard = null, int maxLength = int.MaxValue)
    {
        var before = _state;
        var kind = UiTextUndoHistory.KindFor(command, before);
        var result = UiTypedKeys.Execute(before, command, shift, clipboard, maxLength);
        _history.Record(before, result.State, kind, _now);
        _state = result.State;
    }

    private void Paste(string clip)
    {
        _now += 50;
        var before = _state;
        var result = UiTextEdit.ApplyShortcut(before, 'v', clip);
        _history.Record(before, result.State, UiTextEditKind.Paste, _now);
        _state = result.State;
    }

    private bool Undo(Func<string, bool>? accepts = null)
    {
        bool ok = _history.TryUndo(_state, accepts, out var next);
        _state = next;
        return ok;
    }

    private bool Redo(Func<string, bool>? accepts = null)
    {
        bool ok = _history.TryRedo(_state, accepts, out var next);
        _state = next;
        return ok;
    }

    // ---- coalescing ---------------------------------------------------------------------------------

    [Fact]
    public void Typing_coalesces_into_words_with_their_trailing_space_and_punctuation()
    {
        Type("hello world, again");
        // "hello " / "world, " / "again"
        Assert.Equal(3, _history.UndoDepth);
        Assert.Equal("typing", _history.LastKind);

        Assert.True(Undo());
        Assert.Equal("hello world, ", _state.Text);
        Assert.True(Undo());
        Assert.Equal("hello ", _state.Text);
        Assert.True(Undo());
        Assert.Equal("", _state.Text);
        Assert.False(Undo());
        Assert.Equal(3, _history.RedoDepth);
    }

    [Fact]
    public void A_pause_of_a_second_starts_a_new_step()
    {
        Type("abc");
        Type("d", gapMs: UiTextUndoHistory.CoalesceMs);
        Type("ef");
        Assert.Equal(2, _history.UndoDepth);
        Type("g", gapMs: UiTextUndoHistory.CoalesceMs - 1);
        Assert.Equal(2, _history.UndoDepth);
        Assert.True(Undo());
        Assert.Equal("abc", _state.Text);
    }

    [Fact]
    public void A_caret_move_or_a_selection_ends_the_step()
    {
        Type("abc");
        Key(UiTypedCommand.Left);
        Key(UiTypedCommand.Right);
        Type("d");
        Assert.Equal(2, _history.UndoDepth);

        Key(UiTypedCommand.Shortcut('a'));
        _history.NoteCaret(_state, _state); // a no-op selection report changes nothing
        Type("x");
        // Typing over the selection is its own step and restores the whole text and the selection.
        Assert.Equal(3, _history.UndoDepth);
        Assert.Equal("x", _state.Text);
        Assert.True(Undo());
        Assert.Equal("abcd", _state.Text);
        Assert.Equal(4, _state.Caret);
        Assert.Equal(0, _state.Anchor);
    }

    [Fact]
    public void Backspace_and_Delete_runs_are_one_step_each_and_a_selection_delete_is_its_own()
    {
        Type("abcdef");
        Key(UiTypedCommand.Backspace);
        Key(UiTypedCommand.Backspace);
        Assert.Equal("abcd", _state.Text);
        Assert.Equal(2, _history.UndoDepth);
        Assert.Equal("backspace", _history.LastKind);

        Key(UiTypedCommand.Home);
        Key(UiTypedCommand.Delete);
        Key(UiTypedCommand.Delete);
        Assert.Equal("cd", _state.Text);
        Assert.Equal(3, _history.UndoDepth);
        Assert.Equal("delete", _history.LastKind);

        Key(UiTypedCommand.Shortcut('a'));
        Key(UiTypedCommand.Backspace);
        Assert.Equal("", _state.Text);
        Assert.Equal("delete-selection", _history.LastKind);

        Assert.True(Undo());
        Assert.Equal("cd", _state.Text);
        Assert.True(Undo());
        Assert.Equal("abcd", _state.Text);
        Assert.Equal(0, _state.Caret);
        Assert.True(Undo());
        Assert.Equal("abcdef", _state.Text);
        Assert.Equal(6, _state.Caret);
    }

    [Fact]
    public void Paste_and_cut_are_their_own_steps_and_never_merge_with_typing()
    {
        Type("ab");
        Paste("XYZ");
        Type("cd");
        Assert.Equal("abXYZcd", _state.Text);
        Assert.Equal(3, _history.UndoDepth);

        Key(UiTypedCommand.Shortcut('a'));
        var before = _state;
        var cut = UiTextEdit.Cut(before);
        _history.Record(before, cut.State, UiTextEditKind.Cut, _now += 50);
        _state = cut.State;
        Assert.Equal("cut", _history.LastKind);
        Assert.Equal(4, _history.UndoDepth);

        Assert.True(Undo());
        Assert.Equal("abXYZcd", _state.Text);
        Assert.True(Undo());
        Assert.True(Undo());
        Assert.Equal("ab", _state.Text);
        Assert.Equal(2, _state.Caret);
    }

    [Fact]
    public void Undo_restores_the_caret_and_selection_and_redo_restores_the_after_caret()
    {
        Type("one two");
        Key(UiTypedCommand.Home);
        Key(UiTypedCommand.Right, shift: true);
        Key(UiTypedCommand.Right, shift: true);
        Assert.Equal((2, 0), (_state.Caret, _state.Anchor));
        Type("X");
        Assert.Equal("Xe two", _state.Text);

        Assert.True(Undo());
        Assert.Equal("one two", _state.Text);
        Assert.Equal((2, 0), (_state.Caret, _state.Anchor));

        Assert.True(Redo());
        Assert.Equal("Xe two", _state.Text);
        Assert.Equal((1, 1), (_state.Caret, _state.Anchor));
    }

    [Fact]
    public void A_new_edit_after_an_undo_drops_the_redo_steps()
    {
        Type("alpha beta");
        Assert.True(Undo());
        Assert.Equal(1, _history.RedoDepth);
        Type("gamma");
        Assert.Equal(0, _history.RedoDepth);
        Assert.False(Redo());
        Assert.Equal("alpha gamma", _state.Text);
    }

    [Fact]
    public void Typing_after_an_undo_does_not_merge_into_the_step_below()
    {
        Type("abc ");
        Type("def");
        Assert.True(Undo());
        Type("x");
        Assert.Equal(2, _history.UndoDepth);
        Assert.True(Undo());
        Assert.Equal("abc ", _state.Text);
    }

    [Fact]
    public void Enter_in_a_text_area_is_a_word_boundary()
    {
        Type("line");
        var before = _state;
        var nl = UiTextEdit.Newline(before);
        _history.Record(before, nl.State, UiTextEditKind.Typing, _now += 50);
        _state = nl.State;
        Type("next");
        Assert.Equal(2, _history.UndoDepth);
        Assert.True(Undo());
        Assert.Equal("line\n", _state.Text);
    }

    // ---- bounds -------------------------------------------------------------------------------------

    [Fact]
    public void Repeated_large_pastes_stay_inside_the_character_bound_oldest_first()
    {
        var history = new UiTextUndoHistory(maxEntries: 100, maxChars: 64 * 1024);
        var state = new UiTextEditState("", 0, 0);
        for (int i = 0; i < 40; i++)
        {
            // Ctrl+A, then a 5,000-character paste that the 4,096 limit cuts: a full replacement each time.
            var all = UiTextEdit.SelectAll(state);
            var pasted = UiTextEdit.Paste(all, new string((char)('a' + i % 26), 5000), maxLength: 4096).State;
            Assert.Equal(4096, pasted.Text.Length);
            history.Record(all, pasted, UiTextEditKind.Paste, 1000 + i * 10);
            state = pasted;
            Assert.True(history.TotalChars <= history.MaxChars, "over the bound after paste " + i);
        }

        Assert.True(history.UndoDepth >= 7, "a 4,096-character field keeps several full replacements");
        Assert.True(history.Evicted > 0);
        // The newest steps survive; undo walks back through them in order.
        int depth = history.UndoDepth;
        for (int i = 0; i < depth; i++) Assert.True(history.TryUndo(state, null, out state));
        Assert.False(history.TryUndo(state, null, out _));
        Assert.Equal(4096, state.Text.Length);
    }

    [Fact]
    public void The_entry_bound_drops_the_oldest_step()
    {
        var history = new UiTextUndoHistory(maxEntries: 3);
        var state = new UiTextEditState("", 0, 0);
        for (int i = 0; i < 5; i++)
        {
            var next = UiTextEdit.Paste(state, "w" + i).State;
            history.Record(state, next, UiTextEditKind.Paste, i);
            state = next;
        }

        Assert.Equal(3, history.UndoDepth);
        Assert.Equal(2, history.Evicted);
        while (history.TryUndo(state, null, out state)) { }
        Assert.Equal("w0w1", state.Text);
    }

    [Fact]
    public void An_edit_larger_than_the_character_bound_empties_the_history()
    {
        var history = new UiTextUndoHistory(maxChars: 100);
        var state = new UiTextEditState("", 0, 0);
        var small = UiTextEdit.Paste(state, "ok").State;
        history.Record(state, small, UiTextEditKind.Paste, 0);
        var huge = UiTextEdit.Paste(small, new string('h', 500)).State;
        history.Record(small, huge, UiTextEditKind.Paste, 1);
        Assert.Equal(0, history.UndoDepth);
        Assert.Equal(0, history.TotalChars);
        Assert.False(history.TryUndo(huge, null, out _));
    }

    // ---- filters, limits, read-only and lifetime ----------------------------------------------------

    [Theory]
    [InlineData("abcde", 5, true)]
    [InlineData("abcdef", 5, false)]
    [InlineData("abcdef", 0, true)]
    [InlineData("a\nb", 0, true)]
    public void A_restored_text_must_fit_the_fields_current_limit(string text, int max, bool accepted) =>
        Assert.Equal(accepted, UiTextUndoHistory.FieldAccepts(text, max == 0 ? int.MaxValue : max));

    [Fact]
    public void Filtered_typing_only_ever_records_states_the_filter_accepted_so_undo_restores_one()
    {
        // A number box: the filter drops "a" before the edit, so no step holds it, and every state the
        // history can restore is one the field held. The starting value is restored as it was, even one the
        // filter would not accept as typed (a stored "1e3"), rather than leaving Ctrl+Z stuck on it.
        _state = new UiTextEditState("1e3", 3, 3);
        foreach (char c in "-12a3")
        {
            if (!UiCharFilters.Accepts(UiCharFilter.Integer, _state.Text, _state.SelStart, _state.SelEnd, c)) continue;
            _now += 50;
            Key(UiTypedCommand.Insert(c));
        }

        Assert.Equal("1e3123", _state.Text);
        Assert.Equal(1, _history.UndoDepth);
        Assert.True(Undo(t => UiTextUndoHistory.FieldAccepts(t, 16)));
        Assert.Equal("1e3", _state.Text);
        Assert.True(Redo());
        Assert.Equal("1e3123", _state.Text);
    }

    [Fact]
    public void An_undo_the_field_would_now_reject_is_refused_and_the_step_kept()
    {
        Type("abcdef");
        Key(UiTypedCommand.Shortcut('a'));
        Key(UiTypedCommand.Backspace);
        // The field's limit dropped to 4 since (a server value's limit, say): restoring 6 is refused.
        Func<string, bool> accepts = t => UiTextUndoHistory.FieldAccepts(t, 4);
        Assert.False(Undo(accepts));
        Assert.Equal("", _state.Text);
        Assert.Equal(1, _history.Refused);
        Assert.Equal(2, _history.UndoDepth);
        Assert.True(Undo());
        Assert.Equal("abcdef", _state.Text);
    }

    [Fact]
    public void A_value_replaced_from_outside_clears_the_history()
    {
        Type("draft text");
        Assert.Equal(2, _history.UndoDepth);
        // A sync, a reset, a server echo or a commit's trimming writes the field.
        _state = new UiTextEditState("server value", 12, 12);
        Assert.True(_history.Observe(_state.Text));
        Assert.Equal(0, _history.UndoDepth);
        Assert.Equal("external", _history.LastClear);
        Assert.False(Undo());
        Assert.Equal("server value", _state.Text);

        // The next edit starts a fresh history against the new text.
        Type("!");
        Assert.True(Undo());
        Assert.Equal("server value", _state.Text);
    }

    [Fact]
    public void A_trailing_space_trimmed_by_the_commit_clears_the_history()
    {
        Type("word ");
        string committed = UiCommitText.Trim(_state.Text);
        Assert.Equal("word", committed);
        Assert.True(_history.Observe(committed));
        Assert.Equal(0, _history.UndoDepth);
    }

    [Fact]
    public void Blur_clears_and_the_same_text_keeps_it()
    {
        Type("abc");
        Assert.False(_history.Observe("abc"));
        Assert.Equal(1, _history.UndoDepth);
        _history.Clear("blur");
        Assert.Equal(0, _history.UndoDepth);
        Assert.Equal(0, _history.TotalChars);
        Assert.Equal("blur", _history.LastClear);
    }

    // ---- shortcuts ----------------------------------------------------------------------------------

    [Theory]
    [InlineData('z', false, (int)UiUndoShortcut.Undo)]
    [InlineData('Z', false, (int)UiUndoShortcut.Undo)]
    [InlineData('z', true, (int)UiUndoShortcut.Redo)]
    [InlineData('y', false, (int)UiUndoShortcut.Redo)]
    [InlineData('y', true, (int)UiUndoShortcut.Redo)]
    [InlineData('a', false, (int)UiUndoShortcut.None)]
    [InlineData('v', true, (int)UiUndoShortcut.None)]
    public void Ctrl_Z_undoes_and_Ctrl_Y_or_Ctrl_Shift_Z_redo(char letter, bool shift, int expected) =>
        Assert.Equal((UiUndoShortcut)expected, UiTextUndoHistory.ShortcutFor(letter, shift));

    [Fact]
    public void The_undo_chords_reach_the_shortcut_path_and_type_nothing()
    {
        Assert.Equal(UiTypedCommandKind.Shortcut, UiTypedKeys.Decide(UiKey.Z, ctrl: true, alt: false, meta: false, shift: false).Kind);
        Assert.Equal(UiTypedCommandKind.Shortcut, UiTypedKeys.Decide(UiKey.Z, ctrl: true, alt: false, meta: false, shift: true).Kind);
        var y = UiTypedKeys.Decide(UiKey.Y, ctrl: true, alt: false, meta: false, shift: false);
        Assert.Equal(UiTypedCommandKind.Shortcut, y.Kind);
        Assert.Equal('y', y.Char);
        // AltGr (Ctrl+Alt) is not the shortcut.
        Assert.Equal(UiTypedCommandKind.None, UiTypedKeys.Decide(UiKey.Z, ctrl: true, alt: true, meta: false, shift: false).Kind);
        // The OS text queue's control characters for Ctrl+Z and Ctrl+Y are dropped, never typed.
        Assert.False(UiTextInputMerge.IsTypedText('\u001A'));
        Assert.False(UiTextInputMerge.IsTypedText('\u0019'));
        // Reaching Execute directly (no history) is a consumed no-op.
        var r = UiTypedKeys.Execute(new UiTextEditState("ab", 2, 2), UiTypedCommand.Shortcut('z'), false, null);
        Assert.True(r.Consumed);
        Assert.Equal("ab", r.State.Text);
    }

    // ---- polish: commit trim, read-only caret, bar mapping ------------------------------------------

    [Theory]
    [InlineData("hello ", "hello")]
    [InlineData("  hello", "hello")]
    [InlineData(" hi　", "hi")]
    [InlineData("a b", "a b")]
    [InlineData("line\n", "line\n")]
    [InlineData("\ttabbed\t", "\ttabbed\t")]
    [InlineData(" \nx\n ", "\nx\n")]
    [InlineData("   ", "")]
    [InlineData("", "")]
    public void The_commit_trim_drops_what_the_cfg_read_drops_and_keeps_escaped_breaks_and_tabs(string text, string trimmed) =>
        Assert.Equal(trimmed, UiCommitText.Trim(text));

    [Fact]
    public void The_trimmed_value_survives_the_cfg_round_trip_unchanged()
    {
        foreach (string text in new[] { "notice ", " a\nb ", "x\t", " wide " })
        {
            string committed = UiCommitText.Trim(text);
            // BepInEx Save writes "key = " + escape(value); Reload trims the line and the value.
            string line = "Notice = " + Progenitor.Networking.ServerSettingsLimits.TomlEscape(committed);
            string read = Progenitor.Networking.ServerSettingsLimits.TomlUnescape(line.Trim().Split(new[] { '=' }, 2)[1].Trim());
            Assert.Equal(committed, read);
        }
    }

    [Fact]
    public void A_read_only_field_never_lights_its_caret()
    {
        for (long t = 0; t < UiOwnedCaret.BlinkPeriodMs; t += 100)
            Assert.False(UiOwnedCaret.IsLit(focused: true, nowMs: t, cycleOriginMs: 0, readOnly: true));
        Assert.True(UiOwnedCaret.IsLit(focused: true, nowMs: 0, cycleOriginMs: 0, readOnly: false));
    }

    [Theory]
    [InlineData(0f, 3000f)]
    [InlineData(600f, 2400f)]
    [InlineData(1500f, 1500f)]
    [InlineData(3000f, 0f)]
    public void The_bar_value_is_one_minus_the_fraction_and_a_drag_to_it_moves_nothing(float above, float below)
    {
        var (visible, _, value) = UiTextScroll.Bar(above, below, 500f);
        Assert.True(visible);
        float fraction = UiTextScroll.Fraction(above, below);
        Assert.Equal(UiTextScroll.BarValueForFraction(fraction), value, 4);
        Assert.Equal(1f - fraction, value, 4);
        Assert.Equal(0f, UiTextScroll.ShiftForBarValue(value, above, below), 2);
        Assert.Equal("bottom-to-top", UiTextScroll.BarDirection);
    }

    [Fact]
    public void Top_five_notches_and_end_read_one_then_falling_then_zero()
    {
        // The live 4,096-character Notes: 98 lines, 24 in view. The readbacks must be 1 at the top,
        // between at five notches, 0 at the end (the live readback was one step behind: 0.0005, 1, 0.79).
        float pitch = UiTextExpandLayout.LinePitch;
        float overflow = (98 - 24) * pitch;
        float above = 0f, below = overflow;
        Assert.Equal(1f, UiTextScroll.Bar(above, below, 24 * pitch).Value, 3);
        for (int i = 0; i < 5; i++)
        {
            float s = UiTextScroll.WheelShift(-120f, above, below);
            above += s;
            below -= s;
        }

        float mid = UiTextScroll.Bar(above, below, 24 * pitch).Value;
        Assert.InRange(mid, 0.75f, 0.85f);
        Assert.Equal(1f - UiTextScroll.Fraction(above, below), mid, 4);
        Assert.Equal(0f, UiTextScroll.Bar(overflow, 0f, 24 * pitch).Value, 3);
    }
}
