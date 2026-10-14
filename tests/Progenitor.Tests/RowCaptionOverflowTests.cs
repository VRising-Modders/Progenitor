using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FUI25, R45 finding 10: <c>PanelLayout.MaxRowHeight</c> is only a boundary if the caption
/// inside the clamped rect stops at it, and that has to be true of <b>every</b> row type.
///
/// <para>
/// <b>Why this is a source read and not a behavior test.</b> The write is on a
/// <c>TextMeshProUGUI</c>, and <c>UnityUiRuntime</c> is compiled out of the test assembly for the
/// same reason <c>PanelLayout</c> is compiled into it: the arithmetic is game-free and the Unity
/// calls are not. The defect is also invisible to any headless path by construction — it only
/// appears when the vanilla <c>SimpleStunButton</c> clone succeeds, which is on the live client and
/// nowhere else. So the property is asserted where it lives, in the idiom
/// <c>UiInputMaskTests.The_pointer_flag_has_exactly_one_writer</c> established: the rule has one
/// place it is written and that place is the one every caption goes through.
/// </para>
///
/// <para>
/// The history is the argument for pinning it. Evidence 167 found that TMP defaults to
/// <c>TextOverflowModes.Overflow</c> and paints the whole string outside the clamped rect, over the
/// rows beneath it. <c>debd05d</c> fixed that in <c>MakeLabel</c> — four of the five caption
/// factories — and <c>e50bf94</c> then added the fifth after it, a button row whose caption is the
/// TMP its donor brought with it. One commit's rule, the next commit's new path, and the doc still
/// claiming the rule was universal. Writing it at <c>FitLabel</c> is what makes "every row type"
/// mean every row type.
/// </para>
/// </summary>
public class RowCaptionOverflowTests
{
    private static string Runtime() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"));

    /// <summary>
    /// The measured caption of every row is clamped by the same call, made from the one method
    /// every row's caption is measured by.
    /// </summary>
    [Fact]
    public void Every_measured_caption_is_clamped_by_the_method_that_measures_it()
    {
        string source = Runtime();

        // FitLabel is the funnel: PlaceRow calls it with the row's own label object, and PlaceRow
        // runs from AddRow and from every RunLayout pass.
        string fit = MethodBody(source, "private static float FitLabel(GameObject? label, in RowColumns columns, bool left = false)");
        SourcePin.AssertStatement(fit, "ClampCaptionText(label);");

        SourcePin.AssertStatement(source, "try { height = FitLabel(row.Label, columns, CaptionLeft(row)); }");

        string clamp = MethodBody(source, "private static void ClampCaptionText(GameObject? label)");
        SourcePin.AssertStatement(clamp, "tmp.overflowMode = TextOverflowModes.Ellipsis;");
    }

    /// <summary>
    /// And nothing else in the file decides a caption's overflow mode behind that call's back.
    /// Five writes, each pinned with the reason it is the mode it is: the panel title, which never
    /// goes through a row measurement; a field's own text component, which is clipped by a
    /// <c>RectMask2D</c> and must not ellipsize what the player typed; the search placeholder,
    /// which is hint text not a caption and must Overflow so a 20 px box at fontSize 18 still
    /// generates a mesh; KIT1's wrapping cell, which is a paragraph inside a row sized for its
    /// lines and therefore cannot take the one-line clamp; and the clamp itself.
    /// </summary>
    [Fact]
    public void The_file_has_exactly_four_overflow_writers_and_the_caption_one_is_the_clamp()
    {
        string[] writes = Runtime()
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("///", StringComparison.Ordinal))
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal))
            .Where(l => l.Contains("overflowMode =", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            new[]
            {
                // NOPROGMSG1, SetInputPlaceholder: a settings field's empty hint (the default an empty value stands for),
                // not a row caption: a text area's hint wraps and is truncated at the box, a one-line field's ellipsized.
                "tmp.overflowMode = multi ? TextOverflowModes.Truncate : TextOverflowModes.Ellipsis;",
                // RADIOCLICK3, FitRadioMark: the settings grid's radio circle is one glyph in its own box, drawn whole;
                // under the line-wide caption clamp no circle showed (live 2026-10-06).
                "try { mark.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }",
                // FitInsideArea (2026-10-09, user report "< ilobal (Automatic >"): a choice's value caption, the dropdown
                // bar's caption and each list option stay inside their own area between the arrows; a control's value,
                // never a row caption.
                "try { tmp.overflowMode = TextOverflowModes.Ellipsis; } catch { /* */ }",
                // MakeInput's own text component, behind a RectMask2D: a config value is data and
                // must not be ellipsized while the player is typing it. Never a row's caption.
                "try { tmp.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }",
                // AttachPlaceholder: hint text inside the field, not a row caption and not the
                // typed value. Ellipsis in this 20 px inset at fontSize 18 drops the only line
                // (mesh empty, text still "Search"). Overflow matches the typed-text TMP.
                "try { tmp.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }",
                // MakeLabel, which still owns the panel title — the one TMP this code makes that
                // is never measured as a row.
                "try { tmp.overflowMode = TextOverflowModes.Ellipsis; } catch { /* */ }",
                // ClampCaptionText, reached from FitLabel on every row on every layout pass.
                "tmp.overflowMode = TextOverflowModes.Ellipsis;",
                // KIT1, UnityCellRow.PrepareText: a CellWrap.Wrap cell is a paragraph, not a
                // caption. ClampCaptionText would force it to one line, so the mode is set here
                // instead — still Ellipsis, so a paragraph longer than the row it was sized for is
                // truncated rather than painted over the section beneath it. The row's own height
                // comes from HeadBlockLayout.WrappedRowHeight, so this is the overflow of last
                // resort and not the thing that decides the layout.
                "try { tmp.overflowMode = TextOverflowModes.Ellipsis; } catch { /* */ }",
                // RADIOCLICK2, UnityCellRow.PrepareText: a CellWrap.Glyph cell (a radio circle) is one glyph, not a
                // caption. In its narrow column the ellipsis clamp replaced the circle with nothing (live 2026-10-06).
                "try { tmp.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }",
                // INSTALLSIDES1, UnityCellRow.FillScrollLines: a scroll block's line is a paragraph
                // whose height is measured and stacked inside a scrolling content rect, so it must
                // draw every line it wraps to; the block's viewport (a RectMask2D) clips it.
                "try { tmp.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }",
                // P7b chips, PaintQuiet: a CellWrap.Glyph quiet button (a chip's remove ×) is one glyph in a 20-unit target,
                // not a caption; the ellipsis clamp drew nothing there (live 2026-10-14).
                "try { cell.Tmp.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }",
            },
            writes);
    }

    /// <summary>
    /// The path that was missed, named so it cannot be missed again: a button row's caption is the
    /// GameObject <c>SetButtonText</c> hands back — the cloned vanilla donor's own TMP when there is
    /// one — and that GameObject is what the row is built with and therefore what
    /// <c>FitLabel</c> is given.
    /// </summary>
    [Fact]
    public void A_button_rows_caption_is_the_donors_own_text_and_is_measured_as_a_caption()
    {
        string source = Runtime();

        string setter = MethodBody(source, "private static GameObject? SetButtonText(GameObject go, string text)");
        SourcePin.AssertStatement(setter, "return tmp.gameObject;");

        // Job FUI30: the caption is still written from the resolved control and the row is still
        // registered with it, through ControlBuildPlan's clone-or-build. Pinned exactly rather than
        // by Assert.Contains, so a commented-out or conditionalized line fails here (R47).
        SourcePin.AssertStatementExactly(
            MethodBody(source, "private static GameObject? CloneOrCreateButton("),
            "caption = SetButtonText(built, text);");
        SourcePin.AssertStatementExactly(
            MethodBody(source, "public IRuntimeWidget? AddButton(IRuntimePanel panel, string text)"),
            "host.AddRow(go, caption, hasControl: false, kind: \"button\", source: source);");
    }

    private static string MethodBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "signature not found: " + signature);

        int open = source.IndexOf('{', at + signature.Length);
        Assert.True(open >= 0, "no body after " + signature);

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source.Substring(open, i - open + 1);
        }

        throw new InvalidOperationException("unbalanced braces after " + signature);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Progenitor.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }
}
