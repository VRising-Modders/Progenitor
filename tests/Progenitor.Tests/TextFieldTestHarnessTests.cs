using System;
using System.Linq;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// TEXTTEST1 / TEXTFILTER1 / TEXTAREA2, game-free: the bridge's text-test argument parsing, chord
/// parsing and generated paste text; the per-field character filters; and the TMP-line caret
/// helpers (line lookup, layout freshness, scroll-to-caret).
/// </summary>
public class TextFieldTestHarnessTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // ---- mods_ui argument parsing ----

    [Fact]
    public void Text_actions_are_listed_and_need_the_workspace_except_perf()
    {
        foreach (var a in new[] { "field_focus", "field_blur", "field_submit", "type", "paste", "perf" })
            Assert.Contains(a, ModsUiArgs.Actions);
        Assert.True(ModsUiArgs.RequiresOpenWorkspace("type"));
        Assert.True(ModsUiArgs.RequiresOpenWorkspace("field_focus"));
        Assert.False(ModsUiArgs.RequiresOpenWorkspace("perf"));
        Assert.True(ModsUiArgs.IsLightRead("perf", null));
        Assert.False(ModsUiArgs.IsLightRead("type", null));
    }

    [Fact]
    public void Field_focus_needs_a_label_and_a_known_pane()
    {
        var r = ModsUiArgs.Parse(Json("{\"action\":\"field_focus\",\"label\":\" Notes \",\"pane\":\"Content\"}"));
        Assert.Equal("Notes", r.Label);
        Assert.Equal("content", r.Pane);
        Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"field_focus\"}")));
        Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"field_focus\",\"label\":\"x\",\"pane\":\"left\"}")));
    }

    [Fact]
    public void Type_parses_os_text_and_keys()
    {
        var text = ModsUiArgs.Parse(Json("{\"action\":\"type\",\"text\":\"adsf\"}"));
        Assert.Equal("os-text", text.Via);
        Assert.Equal("adsf", text.Text);
        Assert.Equal(1, text.PerFrame);

        var keys = ModsUiArgs.Parse(Json("{\"action\":\"type\",\"via\":\"keys\",\"keys\":[\"H\",\"Shift+1\",\"Enter\"],\"perFrame\":3}"));
        Assert.Equal("keys", keys.Via);
        Assert.Equal(new[] { "H", "Shift+1", "Enter" }, keys.Keys);
        Assert.Equal(3, keys.PerFrame);

        Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"type\"}")));
        Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"type\",\"via\":\"keys\"}")));
        Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"type\",\"via\":\"ime\",\"text\":\"a\"}")));
        Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"type\",\"text\":\"a\",\"perFrame\":0}")));
        Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"type\",\"via\":\"keys\",\"keys\":[1]}")));
    }

    [Fact]
    public void Paste_takes_text_repeat_or_length_and_measure_frames()
    {
        var gen = ModsUiArgs.Parse(Json("{\"action\":\"paste\",\"length\":5000,\"measureFrames\":120}"));
        Assert.Equal(5000, gen.Length);
        Assert.Equal(120, gen.Frames);
        var rep = ModsUiArgs.Parse(Json("{\"action\":\"paste\",\"text\":\"ab\",\"repeat\":3}"));
        Assert.Equal(3, rep.Repeat);
        Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"paste\"}")));
        Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"paste\",\"length\":2000000}")));
        Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"paste\",\"text\":\"a\",\"repeat\":0}")));
        Assert.Equal(ModsUiArgs.MaxPasteLength, UiTextTestInput.MaxGeneratedLength);
    }

    [Fact]
    public void Perf_frames_default_and_bounds()
    {
        Assert.Equal(ModsUiArgs.DefaultPerfFrames, ModsUiArgs.Parse(Json("{\"action\":\"perf\"}")).Frames);
        Assert.Equal(300, ModsUiArgs.Parse(Json("{\"action\":\"perf\",\"frames\":300}")).Frames);
        Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"perf\",\"frames\":0}")));
        Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"perf\",\"frames\":999999}")));
    }

    // ---- chords ----

    [Theory]
    [InlineData("H", UiKey.H, false, false, false)]
    [InlineData("h", UiKey.H, false, false, false)]
    [InlineData("Shift+1", UiKey.Digit1, false, false, true)]
    [InlineData("Ctrl+A", UiKey.A, true, false, false)]
    [InlineData("ctrl+shift+v", UiKey.V, true, false, true)]
    [InlineData("Enter", UiKey.Enter, false, false, false)]
    [InlineData("Backspace", UiKey.Backspace, false, false, false)]
    [InlineData("Left", UiKey.LeftArrow, false, false, false)]
    [InlineData("Numpad5", UiKey.Numpad5, false, false, false)]
    [InlineData("Shift+/", UiKey.Slash, false, false, true)]
    [InlineData("Shift++", UiKey.Equals, false, false, true)]
    [InlineData("Alt+Ctrl+Q", UiKey.Q, true, true, false)]
    [InlineData("Space", UiKey.Space, false, false, false)]
    public void Chords_parse(string text, UiKey key, bool ctrl, bool alt, bool shift)
    {
        Assert.True(UiTextTestInput.TryParseChord(text, out var chord, out string error), error);
        Assert.Equal(key, chord.Key);
        Assert.Equal(ctrl, chord.Ctrl);
        Assert.Equal(alt, chord.Alt);
        Assert.Equal(shift, chord.Shift);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Hyper+A")]
    [InlineData("NotAKey")]
    [InlineData("42")]
    public void Bad_chords_are_refused(string text)
    {
        Assert.False(UiTextTestInput.TryParseChord(text, out _, out string error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Shift_1_through_the_us_map_types_an_exclamation_mark()
    {
        Assert.True(UiTextTestInput.TryParseChord("Shift+1", out var chord, out _));
        var command = UiTypedKeys.Decide(chord.Key, chord.Ctrl, chord.Alt, chord.Meta, chord.Shift);
        Assert.Equal('!', command.Char);
    }

    // ---- generated text ----

    [Fact]
    public void Generated_text_is_exact_length_deterministic_and_has_words_and_lines()
    {
        string a = UiTextTestInput.Generate(5000, 7);
        Assert.Equal(5000, a.Length);
        Assert.Equal(a, UiTextTestInput.Generate(5000, 7));
        Assert.NotEqual(a, UiTextTestInput.Generate(5000, 8));
        Assert.Contains('\n', a);
        Assert.Contains(' ', a);
        Assert.True(a.Count(c => c == '\n') > 20);
        Assert.All(a, c => Assert.True(c == ' ' || c == '.' || c == '\n' || (c >= 'a' && c <= 'z')));
        Assert.Equal("", UiTextTestInput.Generate(0));
        Assert.Equal(UiTextTestInput.MaxGeneratedLength, UiTextTestInput.Generate(int.MaxValue).Length);
    }

    [Fact]
    public void Repeat_and_hash()
    {
        Assert.Equal("ababab", UiTextTestInput.Repeat("ab", 3));
        Assert.True(UiTextTestInput.Repeat("abc", int.MaxValue).Length <= UiTextTestInput.MaxGeneratedLength);
        Assert.Equal(UiTextTestInput.Hash("x"), UiTextTestInput.Hash("x"));
        Assert.NotEqual(UiTextTestInput.Hash("x"), UiTextTestInput.Hash("y"));
        Assert.Equal(16, UiTextTestInput.Hash("").Length);
    }

    [Fact]
    public void Injection_clear_drops_queued_input_and_the_test_clipboard()
    {
        UiTextInjection.Text.Enqueue('a');
        UiTextInjection.Keys.Enqueue(new UiTestChord(UiKey.V, true, false, false, false));
        UiTextInjection.Clipboard = "secret";
        UiTextInjection.PerFrame = 9;
        Assert.Equal(2, UiTextInjection.Pending);
        UiTextInjection.Clear();
        Assert.Equal(0, UiTextInjection.Pending);
        Assert.Null(UiTextInjection.Clipboard);
        Assert.Equal(1, UiTextInjection.PerFrame);
    }

    // ---- character filters ----

    [Theory]
    [InlineData((int)UiCharFilter.Integer, "", 'a', false)]
    [InlineData((int)UiCharFilter.Integer, "", '5', true)]
    [InlineData((int)UiCharFilter.Integer, "", '-', true)]
    [InlineData((int)UiCharFilter.Integer, "", '+', true)]
    [InlineData((int)UiCharFilter.Integer, "1", '-', false)]
    [InlineData((int)UiCharFilter.Integer, "12", '.', false)]
    [InlineData((int)UiCharFilter.Decimal, "12", '.', true)]
    [InlineData((int)UiCharFilter.Decimal, "1.2", '.', false)]
    [InlineData((int)UiCharFilter.Decimal, "", 'e', false)]
    [InlineData((int)UiCharFilter.Hex, "", '#', true)]
    [InlineData((int)UiCharFilter.Hex, "#", '#', false)]
    [InlineData((int)UiCharFilter.Hex, "#B0", 'f', true)]
    [InlineData((int)UiCharFilter.Hex, "#B0", 'G', false)]
    [InlineData((int)UiCharFilter.None, "", 'x', true)]
    public void Filter_accepts_at_the_end(int filter, string text, char c, bool expected)
    {
        Assert.Equal(expected, UiCharFilters.Accepts((UiCharFilter)filter, text, text.Length, text.Length, c));
    }

    [Fact]
    public void A_sign_or_hash_only_leads()
    {
        // A digit typed in front of an existing sign would put the sign second.
        Assert.False(UiCharFilters.Accepts(UiCharFilter.Integer, "-5", 0, 0, '3'));
        Assert.True(UiCharFilters.Accepts(UiCharFilter.Integer, "-5", 1, 1, '3'));
        // Replacing the whole text (Select All) frees the first position again.
        Assert.True(UiCharFilters.Accepts(UiCharFilter.Integer, "18", 0, 2, '-'));
        Assert.False(UiCharFilters.Accepts(UiCharFilter.Hex, "#AB", 0, 0, 'C'));
    }

    [Fact]
    public void Filter_on_a_paste_keeps_what_a_typist_could_type()
    {
        string kept = UiCharFilters.Filter(UiCharFilter.Integer, "", 0, 0, "-12abc3-4", out int rejected);
        Assert.Equal("-1234", kept);
        Assert.Equal(4, rejected);
        Assert.Equal("#B03030", UiCharFilters.Filter(UiCharFilter.Hex, "", 0, 0, "#B03030zz", out int dropped));
        Assert.Equal(2, dropped);
        Assert.Equal("3.14", UiCharFilters.Filter(UiCharFilter.Decimal, "", 0, 0, "3.1.4", out _));
    }

    [Fact]
    public void Value_types_pick_a_filter()
    {
        Assert.Equal(UiCharFilter.Integer, UiCharFilters.ForValueType(typeof(int)));
        Assert.Equal(UiCharFilter.Decimal, UiCharFilters.ForValueType(typeof(float)));
        Assert.Equal(UiCharFilter.None, UiCharFilters.ForValueType(typeof(string)));
        Assert.Equal(UiCharFilter.Integer, UiCharFilters.ForDecimals(0));
        Assert.Equal(UiCharFilter.Decimal, UiCharFilters.ForDecimals(2));
        Assert.Equal(7, UiCharFilters.HexMaxLength(false));
        Assert.Equal(9, UiCharFilters.HexMaxLength(true));
    }

    // ---- TMP-line caret helpers ----

    [Fact]
    public void Tmp_layout_matches_the_text_or_the_text_plus_one_zero_width_space()
    {
        Assert.True(UiCaretLayout.TmpLayoutMatches("abc", 3, 1));
        Assert.True(UiCaretLayout.TmpLayoutMatches("abc", 4, 1));
        Assert.False(UiCaretLayout.TmpLayoutMatches("abcd", 3, 1));
        Assert.False(UiCaretLayout.TmpLayoutMatches("abc", 3, 0));
        Assert.False(UiCaretLayout.TmpLayoutMatches("a😀", 3, 1));
    }

    [Fact]
    public void Tmp_line_of_a_caret_uses_line_starts()
    {
        // Lines start at 0, 10, 25 (wrap), 30 (after a line break).
        int[] firsts = { 0, 10, 25, 30 };
        int Line(int caret) => UiCaretLayout.TmpLineOf(caret, firsts.Length, i => firsts[i]);
        Assert.Equal(0, Line(0));
        Assert.Equal(0, Line(9));
        Assert.Equal(1, Line(10));
        Assert.Equal(2, Line(29));
        Assert.Equal(3, Line(30));
        Assert.Equal(3, Line(2895));
        Assert.Equal(-1, UiCaretLayout.TmpLineOf(5, 0, i => 0));
    }

    [Theory]
    [InlineData(10f, 30f, 0f, 70f, 0f)]
    [InlineData(-20f, 0f, 0f, 70f, 20f)]
    [InlineData(60f, 80f, 0f, 70f, -10f)]
    public void Scroll_to_show_moves_the_text_by_the_overflow(float bottom, float top, float viewBottom, float viewTop, float expected)
    {
        Assert.Equal(expected, UiCaretLayout.ScrollToShow(bottom, top, viewBottom, viewTop));
    }
}
