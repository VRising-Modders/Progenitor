using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>Where <see cref="VanillaTabStripLayout.Place"/> put a station window's tab strip, in the window root's local units (y up).</summary>
internal readonly struct VanillaTabStripPlacement
{
    internal VanillaTabStripPlacement(float top, float height, bool clamped)
    {
        Top = top;
        Height = height;
        Clamped = clamped;
    }

    /// <summary>The strip's top edge (its pivot is the top center).</summary>
    internal float Top { get; }

    internal float Height { get; }

    internal float Bottom => Top - Height;

    /// <summary>The top edge of a mounted custom panel: just under the strip.</summary>
    internal float PanelTop => Bottom - VanillaTabStripLayout.PanelGap;

    /// <summary>The preferred top was moved down to keep the strip inside the window's visible (unmasked) area.</summary>
    internal bool Clamped { get; }
}

/// <summary>What the self-check found for one strip: <c>ok</c>, or why its captions can be cut.</summary>
internal readonly struct VanillaTabStripCheck
{
    internal VanillaTabStripCheck(string state, string by, float visible)
    {
        State = state;
        By = by;
        Visible = visible;
    }

    /// <summary><c>ok</c>, <c>degenerate</c> (no measurable height), <c>clipped</c> (a parent mask cuts it) or <c>covered</c> (a sibling drawn later overlaps it).</summary>
    internal string State { get; }

    /// <summary>The mask or sibling responsible; empty when ok.</summary>
    internal string By { get; }

    /// <summary>The share of the strip's height inside every clipping mask, 0..1.</summary>
    internal float Visible { get; }

    internal bool Ok => State == "ok";
}

/// <summary>
/// The placement math of the <c>AttachPanelTabs</c> strip on a station window. Game-free: the
/// Unity half (<c>UnityVanillaPanelTabs</c>) measures the rects at runtime and passes them here.
///
/// <para>The strip is a copy of the character window's tab row. Its height is the donor tab's
/// measured height, but only once that height is plausible: a tab read before its layout ran
/// reports zero, and a zero-height strip draws no tab frames and centers its captions on its own
/// baseline trim, so only the lower half of each caption shows. Until the donor has a usable height
/// the strip uses a fallback that fits the caption.</para>
/// </summary>
internal static class VanillaTabStripLayout
{
    /// <summary>Gap between the bottom of the window's bonus row and the strip, in canvas units.</summary>
    internal const float BonusGap = 20f;

    /// <summary>Gap between the strip and a mounted custom panel.</summary>
    internal const float PanelGap = 6f;

    /// <summary>Vertical room around a caption inside its tab.</summary>
    internal const float CaptionPadding = 6f;

    /// <summary>The shortest tab accepted from the donor.</summary>
    internal const float MinTabHeight = 24f;

    /// <summary>The tallest tab accepted from the donor (a stretched or mis-read rect is rejected).</summary>
    internal const float MaxTabHeight = 96f;

    /// <summary>The tab height used while the donor has no usable height (the strip's no-donor default since it was added).</summary>
    internal const float FallbackTabHeight = 49f;

    /// <summary>Frames between donor height reads once the height came from the donor.</summary>
    internal const int HeightReadInterval = 30;

    /// <summary>Frames between self-checks while the strip stays put.</summary>
    internal const int CheckInterval = 30;

    /// <summary>The shortest gap between two self-checks triggered by the strip moving.</summary>
    internal const int MovedCheckInterval = 5;

    /// <summary>A rect edge within this many pixels of a mask edge still counts as inside.</summary>
    internal const float Tolerance = 1f;

    /// <summary>
    /// The tab height to draw: the donor's <paramref name="measured"/> height when it is finite and
    /// within [<see cref="MinTabHeight"/>, <see cref="MaxTabHeight"/>]; otherwise
    /// <see cref="FallbackTabHeight"/>, raised to fit the caption.
    /// </summary>
    internal static float TabHeight(float measured, float captionHeight, out bool fromDonor)
    {
        if (Finite(measured) && measured >= MinTabHeight && measured <= MaxTabHeight)
        {
            fromDonor = true;
            return measured;
        }

        if (!Finite(captionHeight) || captionHeight < 0f) captionHeight = 0f;
        fromDonor = false;
        return Math.Min(MaxTabHeight, Math.Max(FallbackTabHeight, captionHeight + 2f * CaptionPadding));
    }

    /// <summary>
    /// Whether a self-check is due at <paramref name="frame"/>: every <see cref="CheckInterval"/>
    /// frames, or after <see cref="MovedCheckInterval"/> frames when the strip moved.
    /// </summary>
    internal static bool CheckDue(bool moved, int frame, int lastCheck)
        => frame - lastCheck >= (moved ? MovedCheckInterval : CheckInterval);

    /// <summary>
    /// Place the strip in the window root's local units (y up). The preferred top is
    /// <see cref="BonusGap"/> under the bonus row's bottom (<paramref name="bonusBottom"/>), or the
    /// game's own tab row top (<paramref name="anchorTop"/>) when the window reports no bonus row.
    /// The top never rises above the visible area's top (<paramref name="visibleTop"/>: the
    /// tightest parent mask, or the root's own top), so the whole height is drawn.
    /// </summary>
    internal static VanillaTabStripPlacement Place(float anchorTop, float? bonusBottom, float? visibleTop, float height)
    {
        if (!Finite(height) || height < 0f) height = 0f;
        float top = bonusBottom.HasValue && Finite(bonusBottom.Value) ? bonusBottom.Value - BonusGap : anchorTop;
        if (!Finite(top)) top = 0f;
        bool clamped = false;
        if (visibleTop.HasValue && Finite(visibleTop.Value) && top > visibleTop.Value)
        {
            top = visibleTop.Value;
            clamped = true;
        }

        return new VanillaTabStripPlacement(top, height, clamped);
    }

    /// <summary>
    /// The self-check. <paramref name="strip"/> is the strip's screen rect; <paramref name="masks"/>
    /// the screen rects of the enabled parent masks (name, rect); <paramref name="above"/> the
    /// visible rects drawn after the strip (later siblings, override-sorted canvases), with names.
    /// A strip shorter than <paramref name="minHeight"/> pixels is <c>degenerate</c>.
    /// </summary>
    internal static VanillaTabStripCheck Check(ScreenRect strip, IReadOnlyList<(string Name, ScreenRect Rect)> masks,
        IReadOnlyList<(string Name, ScreenRect Rect)> above, float minHeight)
    {
        if (!strip.IsKnown) return new VanillaTabStripCheck("degenerate", "unreadable rect", 0f);
        float h = strip.MaxY - strip.MinY;
        if (h < minHeight) return new VanillaTabStripCheck("degenerate", "height " + Math.Round(h, 1), 0f);

        float visible = 1f;
        string clippedBy = "";
        if (masks != null)
            foreach (var (name, rect) in masks)
            {
                if (!rect.IsKnown) continue;
                float share = VisibleShare(strip, rect);
                if (share >= visible) continue;
                visible = share;
                clippedBy = name;
            }

        if (clippedBy.Length > 0) return new VanillaTabStripCheck("clipped", clippedBy, visible);

        if (above != null)
            foreach (var (name, rect) in above)
                if (rect.IsKnown && Overlaps(strip, rect)) return new VanillaTabStripCheck("covered", name, visible);

        return new VanillaTabStripCheck("ok", "", visible);
    }

    /// <summary>The share of <paramref name="strip"/>'s height and width inside <paramref name="mask"/> (the smaller), 0..1, edges within <see cref="Tolerance"/> count as inside.</summary>
    internal static float VisibleShare(ScreenRect strip, ScreenRect mask)
    {
        float h = strip.MaxY - strip.MinY, w = strip.MaxX - strip.MinX;
        if (h <= 0f || w <= 0f) return 0f;
        float y = Math.Max(0f, Math.Min(strip.MaxY, mask.MaxY + Tolerance) - Math.Max(strip.MinY, mask.MinY - Tolerance));
        float x = Math.Max(0f, Math.Min(strip.MaxX, mask.MaxX + Tolerance) - Math.Max(strip.MinX, mask.MinX - Tolerance));
        return Math.Min(Math.Min(1f, y / h), Math.Min(1f, x / w));
    }

    /// <summary>The two rects share more than <see cref="Tolerance"/> pixels both ways (touching edges do not count).</summary>
    internal static bool Overlaps(ScreenRect a, ScreenRect b)
    {
        float y = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
        float x = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
        return y > Tolerance && x > Tolerance;
    }

    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}

/// <summary>
/// The strip's tab height over time: read from the donor every frame while it is a fallback (a
/// donor read before its layout ran is zero tall), then every <see cref="VanillaTabStripLayout.HeightReadInterval"/> frames.
/// </summary>
internal sealed class VanillaTabStripHeight
{
    private int _lastRead = int.MinValue / 2;

    internal VanillaTabStripHeight(float measured, float captionHeight, int frame)
    {
        CaptionHeight = captionHeight;
        Height = VanillaTabStripLayout.TabHeight(measured, captionHeight, out bool fromDonor);
        FromDonor = fromDonor;
        _lastRead = frame;
    }

    internal float Height { get; private set; }

    internal bool FromDonor { get; private set; }

    internal float CaptionHeight { get; }

    /// <summary>A read is due: always while on the fallback, otherwise at the read interval.</summary>
    internal bool Due(int frame) => !FromDonor || frame - _lastRead >= VanillaTabStripLayout.HeightReadInterval;

    /// <summary>Take a donor reading; true when the height to draw changed by half a unit or more.</summary>
    internal bool Read(float measured, int frame)
    {
        _lastRead = frame;
        float height = VanillaTabStripLayout.TabHeight(measured, CaptionHeight, out bool fromDonor);
        FromDonor = fromDonor;
        if (Math.Abs(height - Height) < .5f) return false;
        Height = height;
        return true;
    }
}

/// <summary>
/// Logs a strip's failed self-check once: only when two checks in a row agree (the game lays a
/// window out over its first frames), and only the first such state per strip.
/// </summary>
internal sealed class VanillaTabStripLogGate
{
    private string _pending = "";
    private bool _logged;

    internal bool ShouldLog(VanillaTabStripCheck check)
    {
        string state = check.State + "|" + check.By;
        bool repeated = state == _pending;
        _pending = check.Ok ? "" : state;
        if (check.Ok || _logged || !repeated) return false;
        _logged = true;
        return true;
    }

    internal void Reset()
    {
        _pending = "";
        _logged = false;
    }
}
