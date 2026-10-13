using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// The title screen's Collect logs tile (user-approved mockup, 2026-10-10): one more icon at the end of the game's
/// bottom-left social icon row (<c>MainMenuNewView.LinkButtonSet.LinksParentNode</c>), the row's own size and spacing,
/// the icons' own slate blue with a gold zip-file glyph (<see cref="ZipGlyph"/>). Hover shows the game's own tooltip box, placed as the
/// official icons place theirs; a click runs Collect logs and
/// the result shows on one line above the row for <see cref="MessageSeconds"/> s. This is the game-free half: names,
/// colors, texts and the message rule. <c>NativeModsEntry.LogsTile.cs</c> draws it.
/// </summary>
internal static class TitleLogsTile
{
    /// <summary>The tile's root name (an owned root: swept on Core unload and by the stale-root sweep).</summary>
    internal const string ObjectName = "progenitor-ui-core-logs-tile";

    internal const string Tooltip = "Collect logs for a help post";

#if !PROGENITOR_TESTS
    /// <summary>The pivot the game's link icons give their tooltip (<c>LinkButton.Update</c>: bottom left, 6).</summary>
    internal const ProjectM.UI.PivotPresets TooltipPivot = ProjectM.UI.PivotPresets.BottomLeft;
#endif

    internal const float MessageSeconds = 8f;

    /// <summary>
    /// The tile's color: the native icons' slate blue (user, 2026-10-10: "take the red out", match the official icons;
    /// measured on the title screen at 5120x1440 as rgb(33, 54, 73) on every icon's plain area). A native plain tile
    /// image already carries it and is drawn untinted (<see cref="NativeTileTint"/>); the drawn rounded tile is tinted
    /// with this. The glyph stays gold.
    /// </summary>
    internal static readonly float[] TileBlue = { 33f / 255f, 54f / 255f, 73f / 255f, 1f };

    /// <summary>A borrowed native tile image keeps its own color.</summary>
    internal static readonly float[] NativeTileTint = { 1f, 1f, 1f, 1f };

    /// <summary>The glyph's gold (the approved mockup).</summary>
    internal static readonly float[] Gold = { 0.86f, 0.70f, 0.36f, 1f };

    /// <summary>The glyph's inset inside the tile, as a fraction of the tile's size on each side.</summary>
    internal const float GlyphInset = 0.18f;

    /// <summary>True for the owned tile among the icon row's children (the MODS entry's clearance ignores it).</summary>
    internal static bool IsOwnTile(string? childName) => string.Equals(childName, ObjectName, StringComparison.Ordinal);

    /// <summary>
    /// The one line above the row: while collecting the collect's progress; for <see cref="MessageSeconds"/> after it
    /// finished its outcome; while hovered (and nothing else to say) the tooltip; else nothing.
    /// </summary>
    internal static string Line(float now, bool hovered, bool running, string runningLine, string status, float shownUntil)
    {
        if (running) return string.IsNullOrWhiteSpace(runningLine) ? Support.SupportActions.Collecting : runningLine;
        if (now < shownUntil && !string.IsNullOrWhiteSpace(status)) return status;
        return hovered ? Tooltip : "";
    }

    // ---- the background: a native plain tile, else a drawn rounded one -----------------------------------------------

    /// <summary>One <c>Image</c> of a native icon: its path under the icon ("" for the icon's own), whether it has a sprite, and its rect as a share of the icon's.</summary>
    internal readonly record struct IconImage(string Path, bool HasSprite, float WidthShare, float HeightShare);

    /// <summary>An image counts as the icon's whole tile at this share of its rect, each way.</summary>
    internal const float CoverShare = 0.9f;

    /// <summary>A glyph is an image at most this share of the icon's rect (one way at least).</summary>
    internal const float GlyphShare = 0.8f;

    /// <summary>
    /// The native icon image to borrow as the plain tile background: in the first icon whose tile and glyph are separate
    /// images (one with a sprite covering the icon, <see cref="CoverShare"/>, and another smaller one,
    /// <see cref="GlyphShare"/>), the covering one. Null when every icon is one baked sprite (its glyph is part of the tile:
    /// live 2026-10-10, the borrowed mail icon drew its envelope behind the zip), so the tile is drawn (<see cref="RoundedTile"/>).
    /// </summary>
    internal static (int Icon, int Image)? PickBackground(IReadOnlyList<IReadOnlyList<IconImage>> icons)
    {
        if (icons == null) return null;
        for (int i = 0; i < icons.Count; i++)
        {
            var images = icons[i];
            if (images == null || images.Count < 2) continue;
            int cover = -1;
            bool glyph = false;
            for (int j = 0; j < images.Count; j++)
            {
                var image = images[j];
                bool covers = image.WidthShare >= CoverShare && image.HeightShare >= CoverShare;
                if (covers && image.HasSprite && cover < 0) cover = j;
                else if (!covers && (image.WidthShare <= GlyphShare || image.HeightShare <= GlyphShare) && image.WidthShare > 0f) glyph = true;
            }
            if (cover >= 0 && glyph) return (i, cover);
        }
        return null;
    }

    /// <summary>True when an axis's anchors differ (the rect stretches with its parent on that axis).</summary>
    internal static bool Stretched(float anchorMin, float anchorMax) => Math.Abs(anchorMax - anchorMin) > 0.0001f;

    /// <summary>
    /// A LayoutElement size for the tile: the template's own when it set one (&gt; 0), else the template's rect size when
    /// that axis is not stretched, else -1 (unset: a stretched rect's size says nothing about what the row should give it).
    /// </summary>
    internal static float LayoutAsk(float templateValue, float rectSize, bool stretched) =>
        templateValue > 0f ? templateValue : stretched ? -1f : rectSize;

    /// <summary>The drawn tile's corner radius as a share of the tile's height (measured on the live row: about 7.5 px of 48).</summary>
    internal const float CornerShare = 0.16f;

    // ---- the result line: short, left on the icon row, at most two lines ---------------------------------------------

    /// <summary>The line's second sentence after a save.</summary>
    internal const string DragLine = "Drag the zip into your #technical-support post.";

    /// <summary>
    /// What the line says after a collect (live 2026-10-10: the Get help status, one long line, ran 1000 px past the row
    /// into the pillarbox; then the file name made it too long for the spot). After a save, two short lines:
    /// <c>Logs saved to your Desktop (1.1 MB).</c> (or the folder's name, or the support folder on a fallback; no file name)
    /// and <see cref="DragLine"/>, which shows only when it fits (<see cref="FitLines"/>). Anything else (a failure, a
    /// refusal) is the status's first line alone, already redacted by the collect.
    /// </summary>
    internal static string ResultText(bool saved, string? fileName, long bytes, string? folderLabel, bool fellBack, string? status)
    {
        if (saved && !string.IsNullOrWhiteSpace(fileName))
            return "Logs saved to " + Where(folderLabel, fellBack) + " (" + Support.SupportBundle.FormatBytes(bytes) + ").\n" + DragLine;
        string s = (status ?? "").Trim();
        int nl = s.IndexOf('\n');
        return nl >= 0 ? s.Substring(0, nl).TrimEnd() : s;
    }

    /// <summary><see cref="ResultText"/> for the collect that finished last (<see cref="Support.SupportActions.Status"/> and its result).</summary>
    internal static string CurrentResultText()
    {
        string status = Support.SupportActions.Status;
        var result = Support.SupportActions.LastResult;
        bool saved = result != null && result.Ok && status.StartsWith("Saved ", StringComparison.Ordinal);
        return ResultText(saved, result?.FileName, result?.Bytes ?? 0, result?.FolderLabel, result?.FellBack ?? false, status);
    }

    /// <summary><c>your Desktop</c>, the support folder, or the last segment of a configured folder (never a full path).</summary>
    internal static string Where(string? folderLabel, bool fellBack)
    {
        string fallback = "BepInEx/" + Support.SupportBundle.FolderName;
        if (fellBack) return fallback;
        string label = (folderLabel ?? "").Trim().TrimEnd('/', '\\');
        if (label.Length == 0) return fallback;
        if (label == Support.SupportBundle.DesktopLabel || label == fallback) return label;
        int cut = Math.Max(label.LastIndexOf('/'), label.LastIndexOf('\\'));
        return cut >= 0 && cut < label.Length - 1 ? label.Substring(cut + 1) : label;
    }

    /// <summary>The line's size in 1080p canvas units, its gap above the icons and its width cap.</summary>
    internal const float LineFontSize = 17f;
    internal const float LineHeight = 22f;
    internal const float LineGap = 6f;
    /// <summary>The width when the icon row cannot be read, and the cap on twice the row's width.</summary>
    internal const float LineFallbackWidth = 520f;
    internal const float LineMaxWidth = 900f;

    /// <summary>Where the line goes, in screen pixels (Y up): its left and bottom edges, its width and how many lines fit.</summary>
    internal readonly record struct LinePlace(float Left, float Bottom, float Width, int MaxLines);

    /// <summary>
    /// The line's place: left-aligned with the native icon row (<paramref name="iconsLeft"/>), <see cref="LineGap"/> above
    /// its top, twice the row's width (the saved line, about 75 characters, fits on one line; at most
    /// <see cref="LineMaxWidth"/>, <see cref="LineFallbackWidth"/> when the row's width is unknown), two lines when they fit under the menu's
    /// last entry (<paramref name="exitBottom"/>, EXIT's drawn glyphs: the same rect the MODS entry's clearance measures),
    /// else one. <paramref name="pxPerUnit"/> is the canvas scale (screen pixels per 1080p unit).
    /// </summary>
    internal static LinePlace PlaceLine(float iconsLeft, float iconsTop, float iconsWidth, float? exitBottom, float pxPerUnit,
        float lineHeight = LineHeight)
    {
        if (float.IsNaN(lineHeight) || float.IsInfinity(lineHeight) || lineHeight <= 1f) lineHeight = LineHeight;
        if (float.IsNaN(pxPerUnit) || float.IsInfinity(pxPerUnit) || pxPerUnit <= 0f) pxPerUnit = 1f;
        float gap = LineGap * pxPerUnit;
        float width = iconsWidth > 1f && !float.IsNaN(iconsWidth) ? Math.Min(iconsWidth * 2f, LineMaxWidth * pxPerUnit) : LineFallbackWidth * pxPerUnit;
        float bottom = iconsTop + gap;
        int lines = 2;
        if (exitBottom is float exit && !float.IsNaN(exit))
        {
            float room = exit - gap - bottom;
            lines = room >= 2f * lineHeight * pxPerUnit ? 2 : 1;
        }
        return new LinePlace(iconsLeft, bottom, width, lines);
    }

    /// <summary>
    /// The line centered over the tile (user, 2026-10-10: the hover line read as the row's, not the tile's): as wide as its
    /// text (<paramref name="textWidth"/>, at most <paramref name="place"/>'s width), centered on
    /// <paramref name="tileCenterX"/>, and moved back inside the game's content (<paramref name="contentLeft"/>..
    /// <paramref name="contentRight"/>, the 16:9 area: <see cref="ContentSpan"/>) when it would cross it. The bottom and the
    /// line count stay <paramref name="place"/>'s, which keep it under EXIT and the MODS entry.
    /// </summary>
    internal static LinePlace CenterOverTile(LinePlace place, float tileCenterX, float textWidth, float contentLeft, float contentRight)
    {
        if (float.IsNaN(tileCenterX) || float.IsInfinity(tileCenterX)) return place;
        // The measured text decides the width (FitLines has already kept it inside the content); without one, the row allowance.
        float width = float.IsNaN(textWidth) || textWidth <= 0f ? place.Width : textWidth;
        if (!float.IsNaN(contentLeft) && !float.IsNaN(contentRight) && contentRight > contentLeft)
            width = Math.Min(width, contentRight - contentLeft);
        float left = tileCenterX - width / 2f;
        if (!float.IsNaN(contentRight) && left + width > contentRight) left = contentRight - width;
        if (!float.IsNaN(contentLeft) && left < contentLeft) left = contentLeft;
        return place with { Left = left, Width = width };
    }

    /// <summary>What the line shows and how wide it is (screen pixels), and whether it is cut with an ellipsis.</summary>
    internal readonly record struct LineFit(string Text, float Width, int Lines, bool Ellipsis);

    /// <summary>
    /// Fit <paramref name="text"/> (one or two lines) to <paramref name="available"/> pixels (the 16:9 content's width) and
    /// <paramref name="maxLines"/>, from each line's measured single-line width (the final font and size, in screen pixels).
    /// The second line is kept only when both lines fit and it is not wider than the room; the first is never wrapped and is
    /// cut with an ellipsis only when it is truly wider than the room. The width is the wider kept line's.
    /// </summary>
    internal static LineFit FitLines(string? text, float firstWidth, float secondWidth, float available, int maxLines)
    {
        string t = text ?? "";
        int nl = t.IndexOf('\n');
        string first = nl >= 0 ? t.Substring(0, nl) : t;
        string second = nl >= 0 ? t.Substring(nl + 1) : "";
        if (float.IsNaN(available) || available <= 0f) available = float.MaxValue;
        bool keepSecond = second.Length > 0 && maxLines >= 2 && !float.IsNaN(secondWidth) && secondWidth <= available
            && firstWidth <= available;
        float width = Math.Max(firstWidth, keepSecond ? secondWidth : 0f);
        bool ellipsis = firstWidth > available;
        if (float.IsNaN(width) || width <= 0f) width = Math.Min(available, LineFallbackWidth);
        width = Math.Min(width, available);
        return new LineFit(keepSecond ? first + "\n" + second : first, width, keepSecond ? 2 : 1, ellipsis);
    }

    /// <summary>The game's 16:9 content across a <paramref name="screenWidth"/>×<paramref name="screenHeight"/> screen, centered (pillarboxed on an ultrawide).</summary>
    internal static (float Left, float Right) ContentSpan(float screenWidth, float screenHeight)
    {
        if (screenWidth <= 0f || screenHeight <= 0f) return (float.NaN, float.NaN);
        float w = Math.Min(screenWidth, screenHeight * 16f / 9f);
        float left = (screenWidth - w) / 2f;
        return (left, left + w);
    }
}

/// <summary>
/// The title tile's drawn background when no native plain tile can be borrowed: a white rounded rectangle with an
/// anti-aliased edge, drawn once at <see cref="TextureSize"/> and 9-sliced at <see cref="Radius"/> so the corners keep
/// their radius at any tile size; the Image tints it the icons' blue (<see cref="TitleLogsTile.TileBlue"/>). Game-free.
/// </summary>
internal static class RoundedTile
{
    internal const int TextureSize = 64;

    /// <summary>The corner radius in texture pixels (also the 9-slice border).</summary>
    internal const int Radius = 16;

    /// <summary>Coverage (0..1) at pixel (<paramref name="x"/>, <paramref name="y"/>) of the <paramref name="size"/>² texture with corners of <paramref name="radius"/> px.</summary>
    internal static float Alpha(int x, int y, int size, int radius)
    {
        if (size <= 0) return 0f;
        float r = Math.Max(0, Math.Min(radius, size / 2));
        float px = x + 0.5f, py = y + 0.5f;
        // Distance into the nearest corner's circle; inside the straight edges it is plain coverage.
        float cx = px < r ? r : px > size - r ? size - r : px;
        float cy = py < r ? r : py > size - r ? size - r : py;
        float dx = px - cx, dy = py - cy;
        float d = (float)Math.Sqrt(dx * dx + dy * dy);
        return Math.Max(0f, Math.Min(1f, r - d + 0.5f));
    }

    /// <summary>
    /// The Image's <c>pixelsPerUnitMultiplier</c> that draws the <see cref="Radius"/>-pixel corner at
    /// <see cref="TitleLogsTile.CornerShare"/> of a <paramref name="tileHeight"/>-unit tile (sprite at 100 pixels per unit, the
    /// canvas's reference): 16 px / (0.16 × 48) ≈ 2.08. A degenerate height answers 1.
    /// </summary>
    internal static float SliceMultiplier(float tileHeight)
    {
        if (float.IsNaN(tileHeight) || float.IsInfinity(tileHeight) || tileHeight <= 1f) return 1f;
        return Radius / (TitleLogsTile.CornerShare * tileHeight);
    }
}
