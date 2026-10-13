namespace Progenitor.Ui;

/// <summary>
/// The dialog kit's frame ground: the Mods palette's panel fill, fully opaque, so nothing under a popup reads through
/// it. Moved here from the removed consent panel's chrome (2026-10-05), which defined it first.
/// </summary>
internal static class DialogFrameColors
{
    /// <summary>The Mods palette's panel ground at alpha 1.</summary>
    internal static readonly PaletteColor Frame = MenuPalette.PaneFill.WithAlpha(1f);
}
