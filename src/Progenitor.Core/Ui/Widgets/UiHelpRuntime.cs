using System;

namespace Progenitor.Ui;

/// <summary>
/// P7b help: what the term markers and the tip cards need from the runtime beyond <see cref="IWidgetRuntime"/>. A
/// separate interface so the help work does not widen the widget contract; <see cref="UiHelpRuntime.Current"/> answers null on
/// a runtime without it (the server, a test fake that does not draw), and a term then keeps its hover explanation and
/// state with no marker drawn, and a card keeps the framework's ordinary panel placement.
/// </summary>
internal interface IUiHelpRuntime
{
    /// <summary>
    /// Draw a term marker (the circle-i, <see cref="UiTermMarkerArt"/>) right after the text of <paramref name="widget"/>:
    /// its caption, or the cell <paramref name="cellKey"/>'s text when the widget is a cell row. The marker never takes
    /// the pointer, follows the text when it changes and never overlaps it: with <paramref name="reserveRoom"/> it widens
    /// the text's right margin by its room (<see cref="UiTermMarkerArt.Room"/>) while it shows, so a caption that fills
    /// its rect is shortened (its ellipsis) rather than covered; a widget whose layout already keeps that room (the chips'
    /// caption margin) passes false. Where it still does not fit inside the text's rect it is hidden, never pushed onto
    /// the text. Null when the runtime cannot find a text to follow.
    /// </summary>
    IUiTermMarkerView? AttachTermMarker(IRuntimeWidget widget, string? cellKey, bool reserveRoom = true);

    /// <summary>
    /// Place the shown tip card <paramref name="card"/> by <see cref="UiTipLayout.Place"/>: beside, below or above the
    /// panel it explains (<paramref name="anchor"/>, screen pixels; unknown for none), else the bottom-right corner,
    /// clamped onto the canvas. Asked after the show and once a frame while the card is up (a re-flow of the other
    /// panels may move it). False when the card cannot be placed (it keeps the framework's placement).
    /// </summary>
    bool PlaceTipCard(IRuntimePanel card, ScreenRect anchor);

    /// <summary>
    /// Draw the tip card's title (<paramref name="title"/>, the dialog title row) and its main answer's caption (the cell
    /// <paramref name="goldCell"/> of <paramref name="buttons"/>) in the game's gold (<c>MenuPalette.ChipOnBorder</c>).
    /// Asked with every placement, so a repaint of the row keeps it.
    /// </summary>
    void TintTipCard(IRuntimeWidget? title, IRuntimeCellRow? buttons, string goldCell) { }
}

/// <summary>One drawn term marker. Disposing it destroys the marker (it also goes with the control it follows).</summary>
internal interface IUiTermMarkerView : IDisposable
{
    /// <summary>The marker is drawn (the text's own visibility still applies).</summary>
    bool Shown { get; set; }
}

/// <summary>Resolves the current runtime's help half.</summary>
internal static class UiHelpRuntime
{
    internal static IUiHelpRuntime? Current
    {
        get
        {
            try { return Ui.CurrentRuntime() as IUiHelpRuntime; }
            catch { return null; }
        }
    }
}

/// <summary>
/// P7b: the term marker's art and place, game-free. <b>Why a drawn circle-i and not a font glyph:</b> the spec's ⓘ
/// (U+24D8, Enclosed Alphanumerics) is not a character game fonts usually carry, and every fallback a font surely has —
/// <c>(i)</c>, <c>i</c>, <c>?</c> — reads as debug text after a caption. A texture drawn once per Core generation, as the
/// Locate marker and the kit's radio circles are, looks the same at every resolution, can never become a missing-glyph
/// box, and is tinted the muted gold of the widgets' glyph buttons. It is sized from the text's font size and sits
/// <see cref="Gap"/> after the text's last character, vertically centered on the text.
/// </summary>
internal static class UiTermMarkerArt
{
    /// <summary>The texture's side in pixels (drawn once, scaled down; bilinear).</summary>
    internal const int TextureSize = 48;

    /// <summary>Space between the text's end and the marker, in canvas units.</summary>
    internal const float Gap = 4f;

    /// <summary>The marker's side for a text of <paramref name="fontSize"/>: about the height of a lowercase letter's ascender.</summary>
    internal static float SizeFor(float fontSize)
    {
        if (float.IsNaN(fontSize) || fontSize <= 0f) fontSize = 18f;
        float s = (float)Math.Round(fontSize * 0.72f);
        return s < 10f ? 10f : s > 22f ? 22f : s;
    }

    /// <summary>The room a marker takes after a text of <paramref name="fontSize"/>: its side and its gap.</summary>
    internal static float Room(float fontSize) => SizeFor(fontSize) + Gap;

    /// <summary>
    /// The marker's center x in the text rect's own coordinates: <see cref="Gap"/> after the text's right edge
    /// (<paramref name="textMaxX"/>). NaN (the marker is hidden) when the text has no known bounds (nothing to explain)
    /// or when the marker would leave the rect (<paramref name="rectMaxX"/>). It is never moved onto the text: a caption
    /// that fills its rect either has the room reserved (its right margin, so it ellipsizes first) or loses its marker.
    /// </summary>
    internal static float CenterAfter(float textMaxX, float rectMaxX, float size)
    {
        if (float.IsNaN(textMaxX) || float.IsInfinity(textMaxX)) return float.NaN;
        float half = size / 2f;
        float x = textMaxX + Gap + half;
        return x + half > rectMaxX + 0.5f ? float.NaN : x;
    }

    /// <summary>
    /// The text's right margin while a marker with reserved room shows: the widget's own margin
    /// (<paramref name="baseRight"/>) plus <see cref="Room"/>; just the widget's own while it is hidden.
    /// </summary>
    internal static float RightMargin(float baseRight, float fontSize, bool shown) => baseRight + (shown ? Room(fontSize) : 0f);

    /// <summary>
    /// Coverage (0..1) of the circle-i at texture pixel (<paramref name="x"/>, <paramref name="y"/>), y bottom-up: a ring,
    /// the dot of the i and its stem, each edge smoothed over one pixel.
    /// </summary>
    internal static float Alpha(int x, int y)
    {
        float n = TextureSize;
        float px = x + 0.5f, py = y + 0.5f, c = n / 2f;
        float dx = px - c, dy = py - c;
        float r = (float)Math.Sqrt(dx * dx + dy * dy);
        float outer = n / 2f - 1f;
        float thick = n * 0.085f;
        float ring = Smooth(outer - r) * Smooth(r - (outer - thick));

        // The i: a dot above a stem, centered.
        float dotY = c + n * 0.20f, dotR = n * 0.075f;
        float ddx = px - c, ddy = py - dotY;
        float dot = Smooth(dotR - (float)Math.Sqrt(ddx * ddx + ddy * ddy));
        float stemHalf = n * 0.055f, stemBottom = c - n * 0.25f, stemTop = c + n * 0.07f;
        float stem = Smooth(stemHalf - Math.Abs(px - c)) * Smooth(py - stemBottom) * Smooth(stemTop - py);
        float a = Math.Max(ring, Math.Max(dot, stem));
        return a < 0f ? 0f : a > 1f ? 1f : a;
    }

    private static float Smooth(float d) => d <= -0.5f ? 0f : d >= 0.5f ? 1f : d + 0.5f;
}

/// <summary>
/// P7b: where a tip card goes, in its canvas's units (origin bottom-left), game-free and tested at 1920x1080,
/// 2560x1440, 3840x2160 and 3440x1440.
///
/// <para>Rules: the card never covers the panel it explains when there is room. In order: right of the anchor panel
/// (top edges aligned), left of it, under it (left edges aligned), over it; the first that fits <see cref="Margin"/>
/// inside the canvas wins. With no room around the panel (the Mods workspace fills a 16:9 screen), inside the panel's
/// bottom-right corner (<see cref="InsideInset"/> from its right edge, <see cref="InsideBottom"/> above its bottom edge),
/// so it never covers another panel beside it (the Mods list). With no anchor, or a panel smaller than the card, the
/// screen's bottom-right corner, <see cref="Margin"/> from the edges. Always clamped onto the canvas.</para>
/// </summary>
internal static class UiTipLayout
{
    /// <summary>Between the card and its anchor panel.</summary>
    internal const float Gap = 16f;

    /// <summary>From the canvas edges.</summary>
    internal const float Margin = 24f;

    /// <summary>Which rule placed the card (readback and tests).</summary>
    internal enum Side
    {
        Right,
        Left,
        Below,
        Above,
        Inside,
        Corner,
    }

    /// <summary>Inside the anchor (no room around it): from the anchor's right edge.</summary>
    internal const float InsideInset = 24f;

    /// <summary>Inside the anchor: from its bottom edge, clear of a panel's 24-unit version footer band.</summary>
    internal const float InsideBottom = 40f;

    /// <summary>The card's bottom-left corner. <paramref name="hasAnchor"/> false ignores the anchor rect.</summary>
    internal static (float X, float Y, Side Side) Place(
        bool hasAnchor, float minX, float minY, float maxX, float maxY, float width, float height, float canvasWidth, float canvasHeight)
    {
        width = Finite(width);
        height = Finite(height);
        float xMax = canvasWidth - Margin - width;
        float yMax = canvasHeight - Margin - height;
        if (hasAnchor && !float.IsNaN(minX) && !float.IsNaN(maxY) && maxX > minX && maxY > minY)
        {
            float right = maxX + Gap;
            if (right <= xMax) return (right, Clamp(maxY - height, Margin, yMax), Side.Right);
            float left = minX - Gap - width;
            if (left >= Margin) return (left, Clamp(maxY - height, Margin, yMax), Side.Left);
            float below = minY - Gap - height;
            if (below >= Margin) return (Clamp(minX, Margin, xMax), below, Side.Below);
            float above = maxY + Gap;
            if (above <= yMax) return (Clamp(minX, Margin, xMax), above, Side.Above);

            // No room around the panel (the Mods workspace at 16:9 fills the screen): inside it, in its bottom-right
            // corner above its footer, the part of a page its rows reach last. Never over another panel beside it.
            float insideX = maxX - InsideInset - width;
            if (insideX >= minX + InsideInset && maxY - minY >= height + InsideBottom + InsideInset)
                return (Clamp(insideX, Margin, xMax), Clamp(minY + InsideBottom, Margin, yMax), Side.Inside);
        }

        return (Clamp(xMax, Margin, xMax), Clamp(Margin, Margin, yMax), Side.Corner);
    }

    private static float Clamp(float v, float lo, float hi) => hi < lo ? lo : v < lo ? lo : v > hi ? hi : v;

    private static float Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) || v < 0f ? 0f : v;
}
