using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// P7 code area: the game-free model (lines, gutter, spans, markup escaping, errors, geometry) and the handle
/// through a fake runtime that hosts cell rows and records what the code area asks it to draw.
/// </summary>
public sealed class UiCodeAreaTests : IDisposable
{
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();

    public UiCodeAreaTests() => UiApi.ResetForTests();

    public void Dispose()
    {
        foreach (var p in _panels) p.Dispose();
        UiApi.ResetForTests();
    }

    // ---- lines ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", 1)]
    [InlineData("a", 1)]
    [InlineData("a\n", 2)]
    [InlineData("a\nb", 2)]
    [InlineData("a\r\nb", 2)]
    [InlineData("a\n\nb", 3)]
    [InlineData("\n\n\n", 4)]
    public void CountLines_counts_lf_and_crlf_as_one_break(string text, int expected) =>
        Assert.Equal(expected, UiCodeAreaModel.CountLines(text));

    [Fact]
    public void Normalize_turns_crlf_and_lone_cr_into_lf()
    {
        Assert.Equal("a\nb\nc", UiCodeAreaModel.Normalize("a\r\nb\rc"));
        Assert.Equal("", UiCodeAreaModel.Normalize(null));
        Assert.Equal(UiCodeAreaModel.CountLines("a\r\nb\r\n"), UiCodeAreaModel.CountLines(UiCodeAreaModel.Normalize("a\r\nb\r\n")));
    }

    [Fact]
    public void LineStarts_and_LineOfOffset_map_characters_to_lines()
    {
        const string text = "ab\ncd\r\n\nef";
        var starts = UiCodeAreaModel.LineStarts(text);
        Assert.Equal(new[] { 0, 3, 7, 8 }, starts);
        Assert.Equal(1, UiCodeAreaModel.LineOfOffset(starts, 0));
        Assert.Equal(1, UiCodeAreaModel.LineOfOffset(starts, 2));
        Assert.Equal(2, UiCodeAreaModel.LineOfOffset(starts, 3));
        Assert.Equal(3, UiCodeAreaModel.LineOfOffset(starts, 7));
        Assert.Equal(4, UiCodeAreaModel.LineOfOffset(starts, 9));
        Assert.Equal(4, UiCodeAreaModel.LineOfOffset(starts, 500));
        Assert.Equal(starts.Count, UiCodeAreaModel.CountLines(text));
    }

    // ---- gutter -----------------------------------------------------------------------------------

    [Fact]
    public void Gutter_numbers_every_line_and_the_marker_column_marks_only_error_lines()
    {
        Assert.Equal("1\n2\n3", UiCodeAreaModel.GutterText(3));
        Assert.Equal("1", UiCodeAreaModel.GutterText(0));
        var errors = new[] { new UiLineError(2, "x") };
        Assert.Equal("\n*\n", UiCodeAreaModel.MarkerText(3, errors, "*"));
        // Same number of lines as the gutter, so both columns sit on the text's lines.
        Assert.Equal(3, UiCodeAreaModel.MarkerText(3, errors, "*").Split('\n').Length);
    }

    [Fact]
    public void Gutter_width_holds_two_digits_and_grows_with_the_digit_count()
    {
        Assert.Equal(2, UiCodeAreaModel.Digits(1));
        Assert.Equal(2, UiCodeAreaModel.Digits(99));
        Assert.Equal(3, UiCodeAreaModel.Digits(100));
        Assert.Equal(UiCodeAreaModel.GutterWidth(1, 10f), UiCodeAreaModel.GutterWidth(99, 10f));
        float two = UiCodeAreaModel.GutterWidth(99, 10f);
        float three = UiCodeAreaModel.GutterWidth(100, 10f);
        Assert.Equal(10f, three - two, 0);
        Assert.True(two >= UiCodeAreaModel.MarkerWidth + 20f + UiCodeAreaModel.NumberGap + UiCodeAreaModel.TextGap);
        // An unmeasurable font falls back to the default digit width.
        Assert.Equal(UiCodeAreaModel.GutterWidth(5, UiCodeAreaModel.FallbackDigitAdvance), UiCodeAreaModel.GutterWidth(5, float.NaN));
        float w = UiCodeAreaModel.GutterWidth(5, 10f);
        Assert.InRange(UiCodeAreaModel.SeparatorX(w), w - UiCodeAreaModel.TextGap, w);
    }

    [Fact]
    public void Line_geometry_maps_lines_to_tops_and_back()
    {
        const float top = 100f, pitch = 22f;
        Assert.Equal(100f, UiCodeAreaModel.LineTop(1, top, pitch));
        Assert.Equal(56f, UiCodeAreaModel.LineTop(3, top, pitch));
        for (int line = 1; line <= 10; line++)
        {
            float mid = UiCodeAreaModel.LineTop(line, top, pitch) - pitch / 2f;
            Assert.Equal(line, UiCodeAreaModel.LineAt(mid, top, pitch, 10));
        }
        Assert.Equal(0, UiCodeAreaModel.LineAt(top + 1f, top, pitch, 10));
        Assert.Equal(0, UiCodeAreaModel.LineAt(top - 10 * pitch - 1f, top, pitch, 10));
        // A bad pitch falls back to the editor's line pitch.
        Assert.Equal(top - UiTextExpandLayout.LinePitch, UiCodeAreaModel.LineTop(2, top, 0f));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(10, 10)]
    [InlineData(40, 40)]
    [InlineData(99, 40)]
    public void Height_is_the_editor_height_for_the_clamped_line_count(int asked, int lines)
    {
        float h = UiCodeAreaModel.HeightFor(asked);
        Assert.Equal(lines, UiTextExpandLayout.LinesFor(h));
    }

    // ---- spans ------------------------------------------------------------------------------------

    private static UiTextSpan S(int start, int length, string color) => new(start, length, color);

    [Fact]
    public void Span_color_must_be_hex_and_reads_back_uppercase()
    {
        Assert.Throws<ArgumentException>(() => new UiTextSpan(0, 1, "red"));
        Assert.Equal("#AABBCC", S(0, 1, "aabbcc").Color);
        Assert.Equal("#AABBCC80", S(0, 1, "#aabbcc80").Color);
        Assert.Equal("#AABBCC", S(0, 1, "#AABBCCFF").Color);
    }

    [Fact]
    public void Spans_are_cut_to_the_text_and_empty_or_outside_spans_are_dropped()
    {
        var runs = UiCodeAreaModel.NormalizeSpans(new[]
        {
            S(-3, 5, "#FF0000"),  // starts before the text: 0..2
            S(8, 10, "#00FF00"),  // past the end: 8..10
            S(10, 3, "#0000FF"),  // starts at the end: dropped
            S(4, 0, "#0000FF"),   // empty: dropped
            S(5, -2, "#0000FF"),  // negative: dropped
        }, 10);
        Assert.Equal(2, runs.Count);
        Assert.Equal((0, 2), (runs[0].Start, runs[0].Length));
        Assert.Equal((8, 2), (runs[1].Start, runs[1].Length));
        Assert.Empty(UiCodeAreaModel.NormalizeSpans(new[] { S(0, 5, "#FF0000") }, 0));
        Assert.Empty(UiCodeAreaModel.NormalizeSpans(null, 5));
    }

    [Fact]
    public void Overlapping_spans_later_wins_and_splits_the_earlier()
    {
        // A comment over the whole line, a keyword inside it laid later.
        var runs = UiCodeAreaModel.NormalizeSpans(new[] { S(0, 10, "#808080"), S(3, 2, "#FFCC00") }, 10);
        Assert.Equal(3, runs.Count);
        Assert.Equal((0, 3, "#808080"), (runs[0].Start, runs[0].Length, runs[0].Color));
        Assert.Equal((3, 2, "#FFCC00"), (runs[1].Start, runs[1].Length, runs[1].Color));
        Assert.Equal((5, 5, "#808080"), (runs[2].Start, runs[2].Length, runs[2].Color));

        // Laid the other way round, the broad span covers the keyword.
        var covered = UiCodeAreaModel.NormalizeSpans(new[] { S(3, 2, "#FFCC00"), S(0, 10, "#808080") }, 10);
        Assert.Single(covered);
        Assert.Equal((0, 10), (covered[0].Start, covered[0].Length));
    }

    [Fact]
    public void Adjacent_spans_of_one_color_merge_and_runs_never_overlap()
    {
        var runs = UiCodeAreaModel.NormalizeSpans(new[] { S(0, 3, "#112233"), S(3, 3, "#112233"), S(8, 1, "#445566") }, 12);
        Assert.Equal(2, runs.Count);
        Assert.Equal((0, 6), (runs[0].Start, runs[0].Length));
        for (int i = 1; i < runs.Count; i++) Assert.True(runs[i].Start >= runs[i - 1].End);
    }

    // ---- markup -----------------------------------------------------------------------------------

    /// <summary>The characters the markup draws: the area's own tags removed, the escaped less-than restored.</summary>
    private static string Drawn(string markup)
    {
        string s = markup.Replace("<noparse><</noparse>", "\u0001");
        s = Regex.Replace(s, "<color=#[0-9A-F]{8}>", "");
        s = s.Replace("</color>", "");
        Assert.DoesNotContain("<", s);
        return s.Replace("\u0001", "<");
    }

    [Fact]
    public void Markup_escapes_user_text_so_it_never_becomes_tags()
    {
        const string text = "if a <b>x</b> </noparse><size=999>big</color>";
        string plain = UiCodeAreaModel.Markup(text, null);
        Assert.Equal(text, Drawn(plain));
        string colored = UiCodeAreaModel.Markup(text, new[] { S(0, 2, "#FFCC00"), S(5, 3, "#FF0000"), S(30, 8, "#00FF00") });
        Assert.Equal(text, Drawn(colored));
        Assert.StartsWith("<color=#FFCC00FF>if</color>", colored);
        Assert.Contains("<color=#FF0000FF><noparse><</noparse>b></color>", colored);
    }

    [Fact]
    public void Markup_keeps_line_breaks_inside_spans_and_drops_spans_past_the_end()
    {
        const string text = "a\nb";
        string m = UiCodeAreaModel.Markup(text, new[] { S(0, 3, "#FF0000"), S(7, 2, "#00FF00") });
        Assert.Equal("<color=#FF0000FF>a\nb</color>", m);
        Assert.Equal("", UiCodeAreaModel.Markup("", new[] { S(0, 3, "#FF0000") }));
    }

    // ---- errors -----------------------------------------------------------------------------------

    [Fact]
    public void Errors_are_sorted_merged_per_line_deduplicated_and_clamped()
    {
        var shown = UiCodeAreaModel.NormalizeErrors(new[]
        {
            new UiLineError(5, "late"),
            new UiLineError(2, "first"),
            new UiLineError(2, "second"),
            new UiLineError(2, "first"),
            new UiLineError(0, "no line"),
            new UiLineError(-4, "negative"),
            new UiLineError(99, "past the end"),
        }, lineCount: 6);
        Assert.Equal(new[] { 2, 5, 6 }, shown.Select(e => e.Line));
        Assert.Equal("first\nsecond", shown[0].Message);
        Assert.Equal("past the end", shown[2].Message);
        Assert.Empty(UiCodeAreaModel.NormalizeErrors(null, 3));
        // Two errors past the end land on the last line together.
        var merged = UiCodeAreaModel.NormalizeErrors(new[] { new UiLineError(7, "a"), new UiLineError(9, "b") }, 3);
        Assert.Single(merged);
        Assert.Equal((3, "a\nb"), (merged[0].Line, merged[0].Message));
    }

    [Fact]
    public void Summary_names_the_first_error_and_counts_the_rest()
    {
        Assert.Equal("", UiCodeAreaModel.Summary(Array.Empty<UiLineError>()));
        Assert.Equal("(!) Line 4: expected \"to\" after \"rest\".",
            UiCodeAreaModel.Summary(new[] { new UiLineError(4, "expected \"to\" after \"rest\".") }));
        Assert.Equal("(!) Line 2: a (+1 more)", UiCodeAreaModel.Summary(new[] { new UiLineError(2, "a\nb"), new UiLineError(7, "c") }));
        Assert.Equal("Line 4", UiCodeAreaModel.TooltipTitle(4));
    }

    // ---- handle -----------------------------------------------------------------------------------

    private IUiPanel NewPanel(CellRowTests.PlainRuntime runtime)
    {
        UiApi.SetRuntimeForTests(runtime);
        var panel = UiApi.Panel("Code", _owner, "example.codearea");
        _panels.Add(panel);
        return panel;
    }

    [Fact]
    public void On_a_runtime_that_cannot_draw_the_area_keeps_its_state()
    {
        var panel = NewPanel(new CellRowTests.PlainRuntime());
        var area = panel.CodeArea("", "a\r\nb", lines: 4);
        Assert.False(area.Enabled);
        Assert.Equal("a\nb", area.Value);
        Assert.Equal(2, area.LineCount);
        area.Value = "x\ny\nz";
        Assert.Equal(3, area.LineCount);
        area.SetErrors(new[] { new UiLineError(9, "bad") });
        Assert.Equal(3, Assert.Single(area.Errors).Line);
        var d = area.Describe();
        Assert.Equal("codearea", d["kind"]);
        Assert.Equal(4, d["lines"]);
        Assert.Null(d["view"]);
        Assert.Equal("(!) Line 3: bad", d["summary"]);
        area.Dispose();
        Assert.True(area.IsDisposed);
        Assert.Equal(UiHandleState.Disposed, area.State);
        area.Value = "ignored";
        Assert.Equal("x\ny\nz", area.Value);
    }

    [Fact]
    public void Area_is_an_editor_input_cell_on_the_value_box_with_a_summary_row_under_it()
    {
        var runtime = new CodeRuntime();
        var panel = NewPanel(runtime);
        var area = panel.CodeArea("Rules", "one", lines: 6);
        Assert.True(area.Enabled);
        var row = runtime.Rows[0];
        var cell = Assert.Single(row.Cells());
        Assert.Equal(CellKind.Input, cell.Kind);
        Assert.Equal(6, cell.EditorLines);
        Assert.Equal(CellSkin.ValueBox, cell.Skin);
        Assert.Equal(UiCodeAreaModel.HeightFor(6), cell.HeightOverride);
        Assert.Equal(CellTone.Orange, runtime.Rows[1].Cells()[0].Tone);
        Assert.Equal(UiCodeAreaModel.CellKey, runtime.Attached.Single().Key);
        Assert.Same(row, runtime.Attached.Single().Row);
        var view = runtime.Views.Single();
        Assert.Equal(1, view.Frames.Last().LineCount);
        Assert.Equal("Rules", area.Describe()["caption"]);
        Assert.Equal(true, ((Dictionary<string, object?>)area.Describe()["view"]!)["fake"]);
    }

    [Fact]
    public void An_edit_raises_OnChanged_once_reruns_the_highlighter_and_redraws()
    {
        var runtime = new CodeRuntime();
        var area = NewPanel(runtime).CodeArea("", "let", lines: 4);
        int runs = 0;
        area.SetHighlighter(text =>
        {
            runs++;
            return text.StartsWith("let", StringComparison.Ordinal) ? new[] { S(0, 3, "#FFCC00") } : Array.Empty<UiTextSpan>();
        });
        Assert.Equal(1, runs);
        var seen = new List<string>();
        using var lease = area.OnChanged(seen.Add);

        Type(runtime, "let x\n= 1");
        Assert.Equal(new[] { "let x\n= 1" }, seen);
        Assert.Equal(2, runs);
        Assert.Equal(2, area.LineCount);
        var frame = runtime.Views.Single().Frames.Last();
        Assert.Equal(2, frame.LineCount);
        Assert.StartsWith("<color=#FFCC00FF>let</color>", frame.Markup);

        // The same text again (the field's echo) is not an edit.
        Type(runtime, "let x\n= 1");
        Assert.Single(seen);
    }

    [Fact]
    public void A_programmatic_write_redraws_without_raising_OnChanged_and_its_echo_is_ignored()
    {
        var runtime = new CodeRuntime();
        var area = NewPanel(runtime).CodeArea("", "", lines: 4);
        int changes = 0;
        area.OnChanged(_ => changes++);
        int frames = runtime.Views.Single().Frames.Count;
        area.Value = "a\r\nb\r\nc";
        Assert.Equal("a\nb\nc", area.Value);
        Assert.Equal("a\nb\nc", runtime.Rows[0].Cells()[0].Text);
        Assert.True(runtime.Views.Single().Frames.Count > frames);
        Type(runtime, "a\nb\nc");
        Assert.Equal(0, changes);
        area.Text = "z";
        Assert.Equal("z", area.Value);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void A_pasted_crlf_or_lone_cr_is_stored_as_lf_and_written_back_with_one_OnChanged()
    {
        var runtime = new CodeRuntime();
        var area = NewPanel(runtime).CodeArea("", "", lines: 4);
        var seen = new List<string>();
        area.OnChanged(seen.Add);

        Type(runtime, "a\r\nb\rc");
        Assert.Equal(new[] { "a\nb\nc" }, seen);
        Assert.Equal("a\nb\nc", area.Value);
        Assert.Equal(3, area.LineCount);
        // The field gets the normalized text back; its echo is not a second edit.
        Assert.Equal("a\nb\nc", runtime.Rows[0].Cells()[0].Text);
        Assert.Equal(1, area.Describe()["normalizedWrites"]);
        Type(runtime, "a\nb\nc");
        Assert.Single(seen);
        Assert.DoesNotContain('\r', runtime.Views.Single().Frames.Last().Markup);

        // An edit that only adds a \r (the same text once normalized) is written back but is not a change.
        Type(runtime, "a\r\nb\nc");
        Assert.Single(seen);
        Assert.Equal("a\nb\nc", runtime.Rows[0].Cells()[0].Text);
        Assert.Equal(2, area.Describe()["normalizedWrites"]);

        // A plain edit writes nothing back.
        Type(runtime, "a\nb\nc\nd");
        Assert.Equal(2, seen.Count);
        Assert.Equal(2, area.Describe()["normalizedWrites"]);
    }

    [Fact]
    public void A_throwing_highlighter_keeps_the_previous_colors()
    {
        var runtime = new CodeRuntime();
        var area = NewPanel(runtime).CodeArea("", "abc", lines: 4);
        area.SetSpans(new[] { S(0, 1, "#FF0000") });
        area.SetHighlighter(_ => throw new InvalidOperationException("boom"));
        Type(runtime, "abcd");
        Assert.Equal(1, area.Describe()["spans"]);
        Assert.StartsWith("<color=#FF0000FF>a</color>", runtime.Views.Single().Frames.Last().Markup);
        // Removing it leaves the spans; SetSpans replaces them.
        area.SetHighlighter(null);
        area.SetSpans(Array.Empty<UiTextSpan>());
        Assert.Equal(0, area.Describe()["spans"]);
    }

    [Fact]
    public void Errors_reach_the_view_and_the_summary_row_and_follow_the_line_count()
    {
        var runtime = new CodeRuntime();
        var area = NewPanel(runtime).CodeArea("", "a\nb\nc\nd", lines: 4);
        area.SetErrors(new[] { new UiLineError(4, "expected \"to\""), new UiLineError(2, "unknown name") });
        var frame = runtime.Views.Single().Frames.Last();
        Assert.Equal(new[] { 2, 4 }, frame.Errors.Select(e => e.Line));
        Assert.Equal("(!) Line 2: unknown name (+1 more)", runtime.Rows[1].Cells()[0].Text);
        var described = (List<Dictionary<string, object?>>)area.Describe()["errors"]!;
        Assert.Equal(2, described.Count);
        Assert.Equal("unknown name", described[0]["message"]);

        // The text shrinks: the marker past the end moves to the last line.
        Type(runtime, "a\nb");
        Assert.Equal(new[] { 2 }, runtime.Views.Single().Frames.Last().Errors.Select(e => e.Line));
        // Messages that land on one line keep the order the caller gave them.
        Assert.Equal("expected \"to\"\nunknown name", area.Errors.Single().Message);

        area.SetErrors(Array.Empty<UiLineError>());
        Assert.Empty(runtime.Views.Single().Frames.Last().Errors);
        Assert.Equal("", runtime.Rows[1].Cells()[0].Text);
    }

    [Fact]
    public void Dispose_releases_the_view_the_rows_and_the_handlers()
    {
        var runtime = new CodeRuntime();
        var area = NewPanel(runtime).CodeArea("Caption", "x", lines: 3);
        int changes = 0;
        area.OnChanged(_ => changes++);
        ushort id = runtime.Rows[0].WiredIdOf(UiCodeAreaModel.CellKey);
        area.Dispose();
        area.Dispose();
        Assert.True(runtime.Views.Single().Disposed);
        Assert.True(runtime.Rows[0].Released);
        Assert.True(runtime.Rows[1].Released);
        UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Text, 0f, false, "after");
        Assert.Equal(0, changes);
        Assert.Equal(UiHandleState.Disposed, area.State);
    }

    [Fact]
    public void A_dead_view_is_dropped_and_the_area_keeps_its_state()
    {
        var runtime = new CodeRuntime();
        var area = NewPanel(runtime).CodeArea("", "x", lines: 3);
        runtime.Views.Single().IsAlive = false;
        area.Value = "y\nz";
        Assert.Equal(2, area.LineCount);
        Assert.Null(area.Describe()["view"]);
    }

    private static void Type(CodeRuntime runtime, string text)
    {
        ushort id = runtime.Rows[0].WiredIdOf(UiCodeAreaModel.CellKey);
        Assert.NotEqual(0, id);
        UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Text, 0f, false, text);
    }

    // ---- fakes ------------------------------------------------------------------------------------

    private sealed class CodeRuntime : CellRowTests.PlainRuntime, ICodeAreaRuntime
    {
        public readonly List<CellRowTests.FakeCellRow> Rows = new();
        public readonly List<(IRuntimeCellRow Row, string Key)> Attached = new();
        public readonly List<FakeView> Views = new();

        public override IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header)
        {
            var row = new CellRowTests.FakeCellRow(null, spec, header);
            Rows.Add(row);
            return row;
        }

        public ICodeAreaView? AttachCodeArea(IRuntimeCellRow row, string cellKey, object? owner)
        {
            Attached.Add((row, cellKey));
            var view = new FakeView();
            Views.Add(view);
            return view;
        }
    }

    private sealed class FakeView : ICodeAreaView
    {
        public readonly List<CodeAreaFrame> Frames = new();
        public bool Disposed;
        public bool IsAlive = true;
        public bool Alive => IsAlive && !Disposed;
        public void Render(CodeAreaFrame frame) => Frames.Add(frame);
        public Dictionary<string, object?> Describe() => new() { ["fake"] = true };
        public void Dispose() => Disposed = true;
    }
}
