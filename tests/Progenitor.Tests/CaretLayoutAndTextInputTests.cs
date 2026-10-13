using System;
using Progenitor.Bootstrap;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// TEXTAREA1 caret geometry (<see cref="UiCaretLayout"/>) and TEXTINPUT1 typed-text sources
/// (<see cref="UiTypedKeys"/> fallback map, <see cref="UiTextInputMerge"/>, Bootstrap's
/// <see cref="TextInputRing"/>). Fixed pitch: every character is 10 px wide.
/// </summary>
public class CaretLayoutAndTextInputTests
{
    private const float Pitch = 10f;
    private const float Advance = 22f;

    private static float Measure(string s) => s.Length * Pitch;

    private static UiCaretSpot Locate(
        string text, int caret, float width = 300f, float height = 66f, bool wrap = true,
        UiTextHAlign h = UiTextHAlign.Left, UiTextVAlign v = UiTextVAlign.Top) =>
        UiCaretLayout.Locate(text, caret, wrap, width, height, Advance, h, v, Measure);

    [Fact]
    public void Text_area_caret_sits_after_the_last_character_on_the_first_line()
    {
        // The user's report: "adsf" on line 1, the bar drawn on line 2 at the rect's center.
        var spot = Locate("adsf", 4);
        Assert.Equal(0, spot.Line);
        Assert.Equal(40f, spot.X);
        Assert.Equal(Advance * 0.5f, spot.CenterYDown);
        Assert.NotEqual(66f * 0.5f, spot.CenterYDown);
    }

    [Fact]
    public void After_Enter_the_caret_is_at_column_zero_of_the_next_line()
    {
        var spot = Locate("adsf\n", 5);
        Assert.Equal(1, spot.Line);
        Assert.Equal(0, spot.Column);
        Assert.Equal(0f, spot.X);
        Assert.Equal(Advance * 1.5f, spot.CenterYDown);
    }

    [Fact]
    public void Caret_before_a_line_break_stays_on_its_line()
    {
        var spot = Locate("ab\ncd", 2);
        Assert.Equal(0, spot.Line);
        Assert.Equal(20f, spot.X);

        var second = Locate("ab\ncdef", 5);
        Assert.Equal(1, second.Line);
        Assert.Equal(2, second.Column);
        Assert.Equal(20f, second.X);
    }

    [Fact]
    public void Empty_fields_put_the_caret_at_the_alignment_origin()
    {
        Assert.Equal(0f, Locate("", 0, v: UiTextVAlign.Middle, wrap: false).X);
        Assert.Equal(33f, Locate("", 0, v: UiTextVAlign.Middle, wrap: false).CenterYDown);
        Assert.Equal(11f, Locate("", 0).CenterYDown);
        Assert.Equal(50f, Locate("", 0, width: 100f, h: UiTextHAlign.Center, v: UiTextVAlign.Middle, wrap: false).X);
        Assert.Equal(100f, Locate("", 0, width: 100f, h: UiTextHAlign.Right, v: UiTextVAlign.Middle, wrap: false).X);
    }

    [Fact]
    public void Single_line_middle_aligned_field_keeps_the_rect_center()
    {
        // The search box rule that was verified live: one line, middle aligned, center of the rect.
        var spot = Locate("hello", 5, height: 20f, wrap: false, v: UiTextVAlign.Middle);
        Assert.Equal(10f, spot.CenterYDown);
        Assert.Equal(50f, spot.X);
    }

    [Theory]
    [InlineData(0, 35f)]
    [InlineData(3, 65f)]
    [InlineData(1, 45f)]
    public void Centered_number_box_offsets_the_caret_by_half_the_slack(int caret, float expected)
    {
        var spot = Locate("123", caret, width: 100f, wrap: false, h: UiTextHAlign.Center, v: UiTextVAlign.Middle);
        Assert.Equal(expected, spot.X);
    }

    [Fact]
    public void Right_aligned_text_ends_at_the_right_edge()
    {
        var spot = Locate("abc", 3, width: 100f, wrap: false, h: UiTextHAlign.Right, v: UiTextVAlign.Middle);
        Assert.Equal(100f, spot.X);
    }

    [Fact]
    public void Word_wrap_breaks_after_spaces_and_the_caret_follows()
    {
        // 85 px fits eight characters, so "aaaa bbbb" (nine) does not fit on one line; the greedy
        // rule gives "aaaa " | "bbbb " | "cccc".
        var starts = UiCaretLayout.LineStarts("aaaa bbbb cccc", wrap: true, width: 85f, Measure);
        Assert.Equal(new[] { 0, 5, 10 }, starts);

        var end = Locate("aaaa bbbb cccc", 14, width: 85f);
        Assert.Equal(2, end.Line);
        Assert.Equal(4, end.Column);
        Assert.Equal(40f, end.X);
        Assert.Equal(Advance * 2.5f, end.CenterYDown);

        // A caret on a soft-wrap boundary is drawn at the start of the next line.
        var boundary = Locate("aaaa bbbb cccc", 5, width: 85f);
        Assert.Equal(1, boundary.Line);
        Assert.Equal(0f, boundary.X);
    }

    [Fact]
    public void Trailing_spaces_hang_and_do_not_force_a_wrap()
    {
        var starts = UiCaretLayout.LineStarts("abcd    ", wrap: true, width: 40f, Measure);
        Assert.Equal(new[] { 0 }, starts);
    }

    [Fact]
    public void A_word_wider_than_the_line_breaks_between_characters()
    {
        var starts = UiCaretLayout.LineStarts("abcdefghijkl", wrap: true, width: 50f, Measure);
        Assert.Equal(new[] { 0, 5, 10 }, starts);
        var spot = Locate("abcdefghijkl", 12, width: 50f);
        Assert.Equal(2, spot.Line);
        Assert.Equal(20f, spot.X);
    }

    [Fact]
    public void No_wrap_keeps_one_line_per_paragraph()
    {
        var starts = UiCaretLayout.LineStarts("aaaa bbbb cccc\nx", wrap: false, width: 30f, Measure);
        Assert.Equal(new[] { 0, 15 }, starts);
    }

    [Fact]
    public void Middle_alignment_centers_the_whole_block()
    {
        var spot = Locate("a\nb", 3, v: UiTextVAlign.Middle);
        Assert.Equal(1, spot.Line);
        Assert.Equal(2, spot.LineCount);
        // Block of two lines (44 px) centered in 66 px starts at 11; line 1's center is 11 + 33.
        Assert.Equal(44f, spot.CenterYDown);
    }

    [Fact]
    public void Top_alignment_does_not_lay_out_text_after_the_caret_paragraph()
    {
        int calls = 0;
        float Counting(string s) { calls++; return s.Length * Pitch; }
        string later = new string('x', 500) + " " + new string('y', 500);
        UiCaretLayout.Locate("ab\n" + later, 1, true, 50f, 66f, Advance, UiTextHAlign.Left, UiTextVAlign.Top, Counting);
        Assert.True(calls < 5, "measured the later paragraph: " + calls);
    }

    [Fact]
    public void A_caret_below_the_visible_lines_is_reported_beyond_view()
    {
        // 66 px at 22 px per line shows three lines; lay out at most four.
        var spot = Locate("1\n2\n3\n4\n5\n6", 11);
        Assert.True(spot.BeyondView);
        Assert.Equal(4, spot.Line);
        var visible = Locate("1\n2\n3\n4\n5\n6", 3);
        Assert.False(visible.BeyondView);
        Assert.Equal(1, visible.Line);
    }

    [Fact]
    public void A_huge_paste_costs_a_bounded_number_of_measures()
    {
        int calls = 0;
        long chars = 0;
        float Counting(string s) { calls++; chars += s.Length; return s.Length * Pitch; }

        // One 1,000,000-character word and a million characters of words, caret at the end.
        foreach (string text in new[] { new string('x', 1_000_000), string.Concat(System.Linq.Enumerable.Repeat("word ", 200_000)) })
        {
            calls = 0;
            chars = 0;
            var spot = UiCaretLayout.Locate(text, text.Length, true, 300f, 66f, Advance, UiTextHAlign.Left, UiTextVAlign.Top, Counting);
            Assert.True(spot.BeyondView);
            Assert.True(calls < 2_000, "measure calls: " + calls);
            Assert.True(chars < 200_000, "measured characters: " + chars);
        }
    }

    [Fact]
    public void Caret_bar_is_never_taller_than_a_line()
    {
        Assert.Equal(UiOwnedCaret.HeightPx, UiCaretLayout.BarHeight(30f));
        Assert.Equal(16f, UiCaretLayout.BarHeight(16f));
        Assert.Equal(UiOwnedCaret.HeightPx, UiCaretLayout.BarHeight(0f));
    }

    // ---- TEXTINPUT1: physical US fallback map ----

    [Theory]
    [InlineData(UiKey.Digit1, '!')]
    [InlineData(UiKey.Digit2, '@')]
    [InlineData(UiKey.Digit3, '#')]
    [InlineData(UiKey.Digit4, '$')]
    [InlineData(UiKey.Digit5, '%')]
    [InlineData(UiKey.Digit6, '^')]
    [InlineData(UiKey.Digit7, '&')]
    [InlineData(UiKey.Digit8, '*')]
    [InlineData(UiKey.Digit9, '(')]
    [InlineData(UiKey.Digit0, ')')]
    public void Shift_digit_types_the_us_symbol(UiKey key, char expected)
    {
        var command = UiTypedKeys.Decide(key, ctrl: false, alt: false, meta: false, shift: true);
        Assert.Equal(UiTypedCommandKind.Insert, command.Kind);
        Assert.Equal(expected, command.Char);
    }

    [Theory]
    [InlineData(UiKey.Backquote, '`', '~')]
    [InlineData(UiKey.Minus, '-', '_')]
    [InlineData(UiKey.Equals, '=', '+')]
    [InlineData(UiKey.LeftBracket, '[', '{')]
    [InlineData(UiKey.RightBracket, ']', '}')]
    [InlineData(UiKey.Backslash, '\\', '|')]
    [InlineData(UiKey.Semicolon, ';', ':')]
    [InlineData(UiKey.Quote, '\'', '"')]
    [InlineData(UiKey.Comma, ',', '<')]
    [InlineData(UiKey.Period, '.', '>')]
    [InlineData(UiKey.Slash, '/', '?')]
    [InlineData(UiKey.Space, ' ', ' ')]
    public void Punctuation_follows_the_us_shift_table(UiKey key, char plain, char shifted)
    {
        Assert.Equal(plain, UiTypedKeys.Decide(key, false, false, false, false).Char);
        Assert.Equal(shifted, UiTypedKeys.Decide(key, false, false, false, true).Char);
        Assert.Equal(UiTypedCommandKind.None, UiTypedKeys.Decide(key, ctrl: true, alt: false, meta: false, shift: false).Kind);
    }

    [Theory]
    [InlineData(UiKey.Numpad0, '0')]
    [InlineData(UiKey.Numpad7, '7')]
    [InlineData(UiKey.NumpadDivide, '/')]
    [InlineData(UiKey.NumpadMultiply, '*')]
    [InlineData(UiKey.NumpadPlus, '+')]
    [InlineData(UiKey.NumpadMinus, '-')]
    [InlineData(UiKey.NumpadPeriod, '.')]
    [InlineData(UiKey.NumpadEquals, '=')]
    public void Numpad_keys_type_their_characters(UiKey key, char expected)
    {
        var command = UiTypedKeys.Decide(key, false, false, false, false);
        Assert.Equal(UiTypedCommandKind.Insert, command.Kind);
        Assert.Equal(expected, command.Char);
    }

    [Fact]
    public void Numpad_enter_is_not_a_character()
    {
        Assert.Equal(UiTypedCommandKind.None, UiTypedKeys.Decide(UiKey.NumpadEnter, false, false, false, false).Kind);
        Assert.True(UiTextEdit.EnterStartsLine(UiKey.NumpadEnter, false, false, multiline: true));
        Assert.False(UiTextEdit.EnterStartsLine(UiKey.Enter, false, false, multiline: false));
    }

    // ---- TEXTINPUT1: queue merge ----

    [Fact]
    public void Without_a_queue_the_physical_map_types()
    {
        var merge = new UiTextInputMerge();
        Assert.False(merge.SuppressPhysicalInserts(queueAvailable: false, queuedText: 0));
        Assert.False(merge.SuppressPhysicalInserts(queueAvailable: false, queuedText: 3));
        Assert.False(merge.Proven);
    }

    [Fact]
    public void Queue_text_proves_the_queue_and_replaces_physical_inserts_from_then_on()
    {
        var merge = new UiTextInputMerge();
        // Subscribed but nothing delivered yet: the physical map still types.
        Assert.False(merge.SuppressPhysicalInserts(true, 0));
        // First delivery: that frame's physical inserts are dropped (no double letter).
        Assert.True(merge.SuppressPhysicalInserts(true, 1));
        Assert.True(merge.Proven);
        // Later frames with no queued text (a held modifier, a key with no character) stay suppressed.
        Assert.True(merge.SuppressPhysicalInserts(true, 0));
    }

    [Theory]
    [InlineData('a', true)]
    [InlineData('é', true)]
    [InlineData('@', true)]
    [InlineData('<', true)]
    [InlineData('\r', false)]
    [InlineData('\n', false)]
    [InlineData('\t', false)]
    [InlineData('\b', false)]
    [InlineData('\u0001', false)]
    [InlineData('\u007f', false)]
    public void Only_printable_queue_text_is_inserted(char c, bool expected)
    {
        Assert.Equal(expected, UiTextInputMerge.IsTypedText(c));
    }

    [Fact]
    public void Drain_floor_keeps_the_previous_frame_only_when_it_was_drained()
    {
        Assert.Equal(99, UiTextInputMerge.MinFrame(100, 99));
        Assert.Equal(100, UiTextInputMerge.MinFrame(100, 50));
        Assert.Equal(100, UiTextInputMerge.MinFrame(100, int.MinValue));
    }

    [Fact]
    public void Ring_drains_in_order_and_discards_stale_frames()
    {
        var ring = new TextInputRing(8);
        ring.Push('w', 10);
        ring.Push('a', 10);
        ring.Push('h', 20);
        ring.Push('i', 20);
        var buffer = new char[8];
        int n = ring.Drain(buffer, minFrame: 20);
        Assert.Equal("hi", new string(buffer, 0, n));
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Ring_overwrites_the_oldest_when_full_and_keeps_what_does_not_fit()
    {
        var ring = new TextInputRing(3);
        foreach (char c in "abcd") ring.Push(c, 1);
        Assert.Equal(1, ring.Overwritten);
        var small = new char[2];
        Assert.Equal("bc", new string(small, 0, ring.Drain(small, 0)));
        Assert.Equal("d", new string(small, 0, ring.Drain(small, 0)));
    }

    // ---- TEXTAREA1: rich-text escaping for cells ----

    [Fact]
    public void Literal_gives_every_less_than_its_own_noparse_run()
    {
        Assert.Equal("", UiRichText.Literal(null));
        Assert.Equal("plain", UiRichText.Literal("plain"));
        Assert.Equal("<noparse><</noparse>b>", UiRichText.Literal("<b>"));
        // The old wrapper was closed early by this text, after which <size=999> applied.
        string attack = "x</noparse><size=999>big";
        string escaped = UiRichText.Literal(attack);
        Assert.Equal("x<noparse><</noparse>/noparse><noparse><</noparse>size=999>big", escaped);
        // No '<' survives outside a noparse run: removing the runs leaves no tag opener.
        Assert.DoesNotContain("<", escaped.Replace("<noparse><</noparse>", ""));
    }

    [Theory]
    [InlineData("plain text", false)]
    [InlineData("two\nlines\r\nand\ttab", false)]
    [InlineData("<size=999>big</size>", false)]
    [InlineData("nul\0", true)]
    [InlineData("esc\u001b[31m", true)]
    [InlineData("del\u007f", true)]
    public void Server_wide_text_refuses_control_characters_but_not_line_breaks(string value, bool refused)
    {
        Assert.Equal(refused, Progenitor.Networking.ServerSettings.HasDisallowedControl(value));
    }

    [Fact]
    public void Audit_cleaner_escapes_backslash_so_a_typed_backslash_n_is_not_a_line_break()
    {
        string typed = Progenitor.Install.SettingsAuditLog.Clean("a\\nb");
        string newline = Progenitor.Install.SettingsAuditLog.Clean("a\nb");
        Assert.Equal("a\\\\nb", typed);
        Assert.Equal("a\\nb", newline);
        Assert.NotEqual(typed, newline);
        Assert.DoesNotContain("\n", Progenitor.Install.SettingsAuditLog.Clean("x\ny\rz"));
    }

    // ---- TEXTAREA1: paste ----

    [Fact]
    public void Text_area_paste_keeps_line_breaks_as_lf()
    {
        var state = new UiTextEditState("", 0, 0);
        var result = UiTextEdit.Paste(state, "one\r\ntwo\rthree\nfour", multiline: true);
        Assert.Equal("one\ntwo\nthree\nfour", result.State.Text);
        Assert.Equal(result.State.Text.Length, result.State.Caret);
    }

    [Fact]
    public void Single_line_paste_strips_line_breaks_and_control_characters()
    {
        var state = new UiTextEditState("", 0, 0);
        Assert.Equal("onetwo\tx", UiTextEdit.Paste(state, "one\r\ntwo\u001b\0\tx").State.Text);
        Assert.Equal("a\nb", UiTextEdit.CleanPaste("a\n\u0007b", multiline: true));
    }

    [Fact]
    public void Paste_stops_at_the_field_limit()
    {
        var state = new UiTextEditState("ab", 2, 2);
        var result = UiTextEdit.Paste(state, new string('x', 1_000_000), maxLength: 10, multiline: true);
        Assert.Equal(10, result.State.Text.Length);
    }
}
