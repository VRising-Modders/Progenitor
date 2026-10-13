using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>
/// TEXTEXPAND1: the expanded text editor's geometry and wording. Game-free, so the dialog the runtime
/// draws and the numbers the tests pin are the same.
///
/// <para>The editor is a framework dialog (<see cref="UiDialog"/>) in the game's popup frame — the same
/// window the Install tab's README reader uses — titled with the setting's name: a multi-line field as
/// wide as the dialog's content and <see cref="EditorHeight"/> tall, a live counter under it, and
/// <b>Cancel</b> / <b>Done</b> (a read-only value: <b>Close</b> only). Escape is Cancel; Enter starts
/// a line inside the editor.</para>
/// </summary>
internal static class UiTextExpandLayout
{
    /// <summary>The line pitch of the 18 px field text (the text area's own, <c>UnityUiRuntime.TextAreaHeight</c>).</summary>
    internal const float LinePitch = 22f;

    /// <summary>
    /// The editor's height: about 23 lines. With the title, the counter, the error line, the buttons and
    /// the footer the dialog stays inside 80 % of the 1,080-unit canvas (<see cref="DialogFits"/>).
    /// </summary>
    internal const float EditorHeight = 520f;

    /// <summary>The counter's caption size (muted, right-aligned under the editor).</summary>
    internal const float CounterFontSize = 15f;

    /// <summary>The counter row's height.</summary>
    internal const float CounterRowHeight = 24f;

    /// <summary>Lines one wheel notch moves a text area or the editor (the game's own lists move about this much).</summary>
    internal const int WheelLinesPerNotch = 3;

    /// <summary>Room the editor's text keeps free at its right for the scroll bar (bar plus air).</summary>
    internal const float ScrollBarGutter = CellLayout.ScrollBarWidth + 8f;

    /// <summary>The expand button's glyph, when the game font has it.</summary>
    internal const string Glyph = "⤢";

    /// <summary>The glyph used when the font lacks <see cref="Glyph"/> (an ellipsis: "there is more").</summary>
    internal const string FallbackGlyph = "…";

    /// <summary>The expand button's size, square, in the text area's bottom-right corner.</summary>
    internal const float ButtonSize = 22f;

    /// <summary>Its inset from the field's right and bottom edges.</summary>
    internal const float ButtonInset = 3f;

    internal const string Tooltip = "Expand";
    internal const string DoneCaption = "Done";
    internal const string CancelCaption = "Cancel";
    internal const string CloseCaption = "Close";

    /// <summary>The dialog's kind (its readback and the bridge's name).</summary>
    internal const string DialogKind = "text-expand";

    /// <summary>The dialog's tag (its button row is <c>text-expand-buttons</c>).</summary>
    internal const string DialogTag = "text-expand";

    /// <summary>How many lines an editor <paramref name="height"/> tall shows (the field's padding taken off).</summary>
    internal static int LinesFor(float height) =>
        Math.Max(2, (int)Math.Floor((Math.Max(0f, height) - 12f) / LinePitch));

    /// <summary>
    /// The counter: <c>1,234 / 4,096</c> for an editor with a cap, <c>1,234 characters</c> for a read-only
    /// value or one without a cap.
    /// </summary>
    internal static string CounterText(int length, int max, bool readOnly)
    {
        string n = Math.Max(0, length).ToString("N0", CultureInfo.InvariantCulture);
        if (readOnly || max <= 0) return n + (length == 1 ? " character" : " characters");
        string counter = n + " / " + max.ToString("N0", CultureInfo.InvariantCulture);
        // A stored value already past the limit (a hand-edited cfg) says by how much.
        return length > max ? counter + " (" + (length - max).ToString("N0", CultureInfo.InvariantCulture) + " over)" : counter;
    }

    /// <summary>
    /// True when a dialog holding the editor fits the canvas: a title of up to <paramref name="titleLines"/>
    /// lines, the editor, the counter, an error line, the button row and the footer, inside 80 % of
    /// <see cref="DialogLayout.CanvasHeight"/>.
    /// </summary>
    internal static bool DialogFits(int titleLines = 1, float editorHeight = EditorHeight)
    {
        float title = DialogLayout.TextRowHeight(Math.Max(1, Math.Min(titleLines, DialogLayout.MaxTitleLines)), DialogLayout.TitleLineHeight);
        float rows = title + editorHeight + CounterRowHeight + UnityUiRuntimeMetrics.RowHeight + DialogLayout.ButtonRowHeight;
        float gaps = 6 * PanelLayout.RowGap;
        float frame = DialogLayout.FrameTopPad + PanelLayout.PadTop + PanelFooterLayout.BandHeight + 16f;
        return rows + gaps + frame <= DialogLayout.CanvasHeight * 0.8f;
    }
}

/// <summary>
/// TEXTEXPAND1: scrolling a multi-line field's text inside its viewport — by the wheel, by the scroll
/// bar, and the readback of which lines are in view. Pure arithmetic over two numbers the runtime
/// measures: <c>above</c>, the text height hidden above the viewport, and <c>below</c>, the height
/// hidden below it (both 0 when the text fits). A positive shift moves the text up (shows later lines).
/// </summary>
internal static class UiTextScroll
{
    /// <summary>
    /// The shift one wheel event asks for: <paramref name="lines"/> lines of <paramref name="pitch"/> per
    /// notch, toward the end for a notch toward the player (negative <paramref name="deltaY"/>, Windows),
    /// clamped so the text never leaves the viewport. 0 when the text cannot move that way (the caller
    /// then lets the notch scroll the list behind it).
    /// </summary>
    internal static float WheelShift(float deltaY, float above, float below, float pitch = UiTextExpandLayout.LinePitch,
        int lines = UiTextExpandLayout.WheelLinesPerNotch)
    {
        if (float.IsNaN(deltaY) || Math.Abs(deltaY) < 0.0001f) return 0f;
        above = Clean(above);
        below = Clean(below);
        float step = Math.Max(1, lines) * (pitch > 0f ? pitch : UiTextExpandLayout.LinePitch);
        // One event may carry several notches (a fast wheel); keep their count, at least one. Windows
        // notches arrive as ±120; values already in notches are used as they are (MenuPanelLayout's rule).
        float ticks = Math.Abs(deltaY);
        if (ticks > 20f) ticks /= 120f;
        float notches = Math.Max(1f, (float)Math.Round(ticks));
        float want = step * notches;
        return deltaY < 0f ? Math.Min(want, below) : -Math.Min(want, above);
    }

    /// <summary>
    /// The scroll bar for this extent: hidden when the text fits; otherwise the handle's share of the
    /// track (<c>size</c>, at least <see cref="MinHandle"/>) and its position for a bottom-to-top bar
    /// (<c>value</c> 1 at the top of the text, 0 at its end).
    /// </summary>
    internal static (bool Visible, float Size, float Value) Bar(float above, float below, float viewHeight)
    {
        above = Clean(above);
        below = Clean(below);
        float overflow = above + below;
        if (overflow < 0.5f || viewHeight <= 0f) return (false, 1f, 1f);
        float size = Math.Max(MinHandle, Math.Min(1f, viewHeight / (viewHeight + overflow)));
        float value = 1f - above / overflow;
        return (true, size, Clamp01(value));
    }

    /// <summary>The smallest handle, as a share of the track, so a 4,096-character text keeps a handle to grab.</summary>
    internal const float MinHandle = 0.06f;

    /// <summary>
    /// TEXTUNDO1 polish: the editor's bar is a uGUI <c>Scrollbar</c> with <c>direction = BottomToTop</c>, so
    /// its <c>value</c> is 1 with the handle at the top (the start of the text in view) and 0 at the bottom
    /// (the end in view): <c>value = 1 − </c><see cref="Fraction"/>.
    /// </summary>
    internal const string BarDirection = "bottom-to-top";

    /// <summary>The bar value for a view <paramref name="fraction"/> of the way down the text (<c>1 − fraction</c>).</summary>
    internal static float BarValueForFraction(float fraction) => 1f - Clamp01(float.IsNaN(fraction) ? 0f : fraction);

    /// <summary>The shift that puts the text where a bottom-to-top bar at <paramref name="value"/> says.</summary>
    internal static float ShiftForBarValue(float value, float above, float below)
    {
        above = Clean(above);
        below = Clean(below);
        float overflow = above + below;
        if (overflow < 0.5f || float.IsNaN(value)) return 0f;
        float wantAbove = (1f - Clamp01(value)) * overflow;
        return wantAbove - above;
    }

    /// <summary>How far down the text the view is: 0 at the top, 1 at the end (1 when everything fits).</summary>
    internal static float Fraction(float above, float below)
    {
        above = Clean(above);
        below = Clean(below);
        float overflow = above + below;
        return overflow < 0.5f ? 1f : Clamp01(above / overflow);
    }

    /// <summary>
    /// The first and last line in view: a line counts when its middle is inside the viewport. Lines are
    /// given top to bottom as their top and bottom edges in the viewport's space (y up), with the
    /// viewport from <paramref name="viewBottom"/> to <paramref name="viewTop"/>. (-1, -1) for none.
    /// </summary>
    internal static (int First, int Last) VisibleLines(IReadOnlyList<float> tops, IReadOnlyList<float> bottoms, float viewTop, float viewBottom)
    {
        int first = -1, last = -1;
        if (tops == null || bottoms == null) return (first, last);
        int n = Math.Min(tops.Count, bottoms.Count);
        for (int i = 0; i < n; i++)
        {
            float mid = (tops[i] + bottoms[i]) * 0.5f;
            if (mid > viewTop + 0.5f || mid < viewBottom - 0.5f) continue;
            if (first < 0) first = i;
            last = i;
        }

        return (first, last);
    }

    private static float Clean(float v) => float.IsNaN(v) || float.IsInfinity(v) || v < 0f ? 0f : v;

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
