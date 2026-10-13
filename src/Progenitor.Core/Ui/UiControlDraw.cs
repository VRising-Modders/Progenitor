namespace Progenitor.Ui;

/// <summary>
/// Drawability recipes for the settings-kit primitives. A control that exists
/// with <c>text</c> assigned and <c>meshCount = 0</c> draws nothing — that was a
/// live-confirmed failure on the search placeholder. These predicates take the
/// same inputs a live probe can read (assigned, enabled, alpha, rect, mesh).
/// </summary>
internal static class UiControlDraw
{
    internal const float MinAlpha = 0.85f;
    internal const float MinRect = 1f;

    /// <summary>ASCII affordances that this build's font is expected to contain.</summary>
    internal const string ChoiceGlyphPreferred = "v";
    internal const string ChoiceGlyphAlt = ">";
    /// <summary>The selector's arrows: ASCII, so every game font draws them.</summary>
    internal const string ChoicePrevious = "<";
    internal const string ChoiceNext = ">";

    /// <summary>Width of one selector arrow inside the control column.</summary>
    internal const float ChoiceArrowWidth = 28f;

    /// <summary>The gap between an arrow and the value's own area; the value never draws outside that area.</summary>
    internal const float ChoiceValuePad = 6f;

    /// <summary>The value's size, and the least it shrinks to before an ellipsis.</summary>
    internal const float ChoiceValueFontSize = 16f;
    internal const float ChoiceValueMinFontSize = 11f;

    /// <summary>
    /// The value's own width inside a selector <paramref name="controlWidth"/> wide: the control less both arrows and
    /// their gaps (never negative). Game-free.
    /// </summary>
    internal static float ChoiceValueAreaWidth(float controlWidth) =>
        System.Math.Max(0f, controlWidth - (2f * (ChoiceArrowWidth + ChoiceValuePad)));

    internal const string StepperMinus = "-";
    internal const string StepperPlus = "+";

    internal static string PickGlyph(string preferred, string alt, System.Func<string, bool>? fontHas)
    {
        if (fontHas == null) return "";
        if (fontHas(preferred)) return preferred;
        if (fontHas(alt)) return alt;
        return "";
    }

    internal static bool GraphicIsDrawable(
        bool assigned,
        bool active,
        bool graphicEnabled,
        float alpha,
        float rectW,
        float rectH,
        int meshCount)
    {
        return assigned
            && active
            && graphicEnabled
            && alpha >= MinAlpha
            && rectW > MinRect
            && rectH > MinRect
            && meshCount > 0;
    }

    /// <summary>
    /// Dropdown caption. <paramref name="meshCount"/> is TMP characterCount of the
    /// chosen label, not the option list.
    /// </summary>
    internal static bool ChoiceLabelIsDrawable(
        string label,
        bool assigned,
        bool active,
        bool graphicEnabled,
        float alpha,
        float rectW,
        float rectH,
        int meshCount) =>
        !string.IsNullOrEmpty(label)
        && GraphicIsDrawable(assigned, active, graphicEnabled, alpha, rectW, rectH, meshCount);

    /// <summary>Numeric field text, including a stepper's committed value.</summary>
    internal static bool NumericTextIsDrawable(
        string text,
        bool assigned,
        bool active,
        bool graphicEnabled,
        float alpha,
        float rectW,
        float rectH,
        int meshCount) =>
        !string.IsNullOrEmpty(text)
        && GraphicIsDrawable(assigned, active, graphicEnabled, alpha, rectW, rectH, meshCount);

    /// <summary>
    /// Slider fill and handle. Each needs its own rect; a slider with a value and
    /// no fillRect draws nothing to grab.
    /// </summary>
    internal static bool SliderPartsAreDrawable(
        bool fillAssigned,
        bool handleAssigned,
        float fillW,
        float fillH,
        float handleW,
        float handleH,
        int fillMesh,
        int handleMesh) =>
        fillAssigned
        && handleAssigned
        && fillW > MinRect
        && fillH > MinRect
        && handleW > MinRect
        && handleH > MinRect
        && fillMesh > 0
        && handleMesh > 0;

    /// <summary>
    /// Color swatch. An Image with a quad is <paramref name="meshCount"/> 1.
    /// Alpha of the graphic must be high enough to separate from the row fill;
    /// the color's own A channel is the authored value, not a visibility fail.
    /// </summary>
    internal static bool SwatchIsDrawable(
        bool assigned,
        bool active,
        bool graphicEnabled,
        float graphicAlpha,
        float rectW,
        float rectH,
        int meshCount) =>
        GraphicIsDrawable(assigned, active, graphicEnabled, graphicAlpha, rectW, rectH, meshCount);

    internal static bool KeybindChordIsDrawable(
        string chordText,
        bool assigned,
        bool active,
        bool graphicEnabled,
        float alpha,
        float rectW,
        float rectH,
        int meshCount) =>
        !string.IsNullOrEmpty(chordText)
        && GraphicIsDrawable(assigned, active, graphicEnabled, alpha, rectW, rectH, meshCount);
}
