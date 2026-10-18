using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>Which screen edge a docked panel sits against (<see cref="UiPlacement.Dock(IUiPanel, UiDock, float)"/>).</summary>
public enum UiDock
{
    /// <summary>Not docked: the panel is placed with the other panels (the default).</summary>
    None = 0,
    /// <summary>Against the left edge of the screen.</summary>
    Left = 1,
    /// <summary>Against the right edge of the screen.</summary>
    Right = 2,
    /// <summary>Against the top edge of the screen: a strip that hangs from the top and grows down.</summary>
    Top = 3,
    /// <summary>Against the bottom edge of the screen: a strip that stands on the bottom and grows up.</summary>
    Bottom = 4,
}

/// <summary>
/// Where a docked panel sits along its edge. On the left and right edges: <see cref="Center"/>, <see cref="Top"/> (or
/// <see cref="Start"/>) and <see cref="End"/> (the bottom). On the top and bottom edges: <see cref="Start"/> (the left
/// end), <see cref="Center"/> and <see cref="End"/> (the right end).
/// </summary>
public enum UiDockAlign
{
    /// <summary>Centered along the edge (the default): vertically on a side edge, horizontally on the top or bottom edge.</summary>
    Center = 0,
    /// <summary>On a left or right edge: its top edge at the top of the screen, inside the margin; it grows downward.</summary>
    Top = 1,
    /// <summary>The start of the edge: the top of a left or right edge (as <see cref="Top"/>), the left end of a top or bottom edge.</summary>
    Start = 2,
    /// <summary>The end of the edge: the bottom of a left or right edge (it grows upward), the right end of a top or bottom edge.</summary>
    End = 3,
}

/// <summary>Options for <see cref="UiPlacement.Dock(IUiPanel, UiDock, UiDockOptions)"/>. The defaults dock a panel at its own width, centered.</summary>
public sealed class UiDockOptions
{
    /// <summary>Gap between the screen edge and the frame, in panel units (1920x1080 design space). Default 16, the panels' edge margin.</summary>
    public float Margin { get; set; } = UiDockLayout.DefaultMargin;

    /// <summary>Where along its edge the frame sits (<see cref="UiDockAlign"/>); centered by default.</summary>
    public UiDockAlign Align { get; set; } = UiDockAlign.Center;

    /// <summary>
    /// On a top or bottom edge: panel units right (negative: left) of the aligned place, to line a strip up with
    /// something beside the screen's center. The frame stays inside the screen. Must be 0 on a left or right edge.
    /// </summary>
    public float Offset { get; set; }

    /// <summary>
    /// The frame width in panel units, for a game window (<see cref="UiLook.GameWindow(IUiPanel)"/>); 0 keeps the window's own
    /// width. On a left or right edge the frame is never narrower than the kit's 640 frame; on a top or bottom edge a
    /// width under 640 makes a narrow strip (any floating panel): drawn that wide, at least 240, with no title band and
    /// as tall as its content, at least 48. Never wider than 90% of the screen.
    /// </summary>
    public float Width { get; set; }

    /// <summary>
    /// The frame width as a share of the screen's width (0.3 is 30%), for a game window; it follows a resolution change.
    /// More than 0 and at most 0.9; it wins over <see cref="Width"/>. The 640 floor still applies on a side; on a top or
    /// bottom edge a share under 640 units makes a narrow strip, as <see cref="Width"/> does.
    /// </summary>
    public float WidthFraction { get; set; }
}

/// <summary>Panel placement beyond the automatic layout: docking a panel to a screen edge.</summary>
public static class UiPlacement
{
    /// <summary>
    /// Dock <paramref name="panel"/> to the <paramref name="side"/> edge, centered along it, <paramref name="margin"/>
    /// panel units from the edge. A docked panel keeps its place across Show and Hide and follows a resolution change; it
    /// never joins the centered row of windows and panels, and that row keeps clear of a left or right dock. Panels docked
    /// to the same side line up from the edge inward, the one shown longest ago at the edge; panels docked to the top or
    /// bottom edge with one alignment line up along it. <see cref="UiDock.None"/> undocks it.
    /// Dock replaces <see cref="Ui.TopPanel"/>'s placement while it is docked. Returns the panel, so a call can chain.
    /// </summary>
    public static IUiPanel Dock(this IUiPanel panel, UiDock side, float margin = UiDockLayout.DefaultMargin) =>
        panel.Dock(side, new UiDockOptions { Margin = margin });

    /// <summary><see cref="Dock(IUiPanel, UiDock, float)"/> with options: the alignment, the offset and the frame width.</summary>
    public static IUiPanel Dock(this IUiPanel panel, UiDock side, UiDockOptions options)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        if (options == null) throw new ArgumentNullException(nameof(options));
        var refused = UiDockLayout.Refuse(side, options);
        if (refused != null) throw new ArgumentOutOfRangeException(nameof(options), refused);
        if (panel is not UiPanelHandle host || host.IsDisposed) return panel;
        UiDockSpec? spec = side == UiDock.None ? null : UiDockSpec.From(side, options);
        host.Dock = spec;
        if (host.Enabled && host.Runtime is IRuntimePanel runtimePanel)
        {
            try { Ui.CurrentRuntime().DockPanel(runtimePanel, spec); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dock panel: " + e.Message); }
        }

        if (host.IsShownPanel) Ui.ReflowPanels();
        return panel;
    }

    /// <summary>The edge <paramref name="panel"/> is docked to, or <see cref="UiDock.None"/>.</summary>
    public static UiDock DockSide(this IUiPanel panel) =>
        panel is UiPanelHandle host && host.Dock is { } d ? d.Side : UiDock.None;
}

/// <summary>One docked panel's request, in panel units. Game-free.</summary>
internal readonly struct UiDockSpec : IEquatable<UiDockSpec>
{
    internal UiDockSpec(UiDock side, UiDockAlign align, float margin, float width, float fraction, float offset = 0f)
    {
        Side = side;
        Align = align;
        Margin = margin;
        Width = width;
        Fraction = fraction;
        Offset = offset;
    }

    internal static UiDockSpec From(UiDock side, UiDockOptions o) =>
        new(side, o.Align, o.Margin, o.Width, o.WidthFraction, o.Offset);

    internal UiDock Side { get; }
    internal UiDockAlign Align { get; }
    internal float Margin { get; }
    internal float Width { get; }
    internal float Fraction { get; }
    internal float Offset { get; }

    /// <summary>Docked to the top or bottom edge (a strip), not to a side.</summary>
    internal bool Horizontal => Side is UiDock.Top or UiDock.Bottom;

    /// <summary>A width (units or share) was asked for.</summary>
    internal bool HasWidth => Fraction > 0f || Width > 0f;

    /// <summary>The same dock with this frame width instead (a share of 0 and <paramref name="width"/> units; 0 is the kit frame).</summary>
    internal UiDockSpec WithWidth(float width) => new(Side, Align, Margin, Math.Max(0f, width), 0f, Offset);

    public bool Equals(UiDockSpec other) =>
        Side == other.Side && Align == other.Align && Margin.Equals(other.Margin) && Width.Equals(other.Width)
        && Fraction.Equals(other.Fraction) && Offset.Equals(other.Offset);

    public override bool Equals(object? obj) => obj is UiDockSpec other && Equals(other);

    public override int GetHashCode() => HashCode.Combine((int)Side, (int)Align, Margin, Width, Fraction, Offset);
}

/// <summary>How a docked frame sits in its area's height: centered, hung from the top, or standing on the bottom.</summary>
internal enum DockHang
{
    Center = 0,
    Top = 1,
    Bottom = 2,
}

/// <summary>
/// Where a docked panel goes (<see cref="UiDockLayout.Place"/>), in canvas units from the canvas center: the frame's
/// center x, the area's top and bottom edges (upward positive), the tallest it may be, and how it hangs in the area.
/// </summary>
internal readonly struct DockPlacement
{
    internal DockPlacement(float x, float top, float bottom, float maxHeight, DockHang hang)
    {
        X = x;
        Top = top;
        Bottom = bottom;
        MaxHeight = maxHeight;
        Hang = hang;
    }

    internal float X { get; }
    internal float Top { get; }
    internal float Bottom { get; }
    internal float MaxHeight { get; }
    internal DockHang Hang { get; }

    /// <summary>The frame's center offset for a frame <paramref name="height"/> tall: hung from the top, standing on the bottom, or centered in the area.</summary>
    internal PanelOffset CenterFor(float height)
    {
        float h = Math.Max(0f, Math.Min(height, MaxHeight));
        float y = Hang switch
        {
            DockHang.Top => Top - h * 0.5f,
            DockHang.Bottom => Bottom + h * 0.5f,
            _ => (Top + Bottom) * 0.5f,
        };
        // The top edge never above the area's top (under the workspace's tab strip).
        if (y + h * 0.5f > Top) y = Top - h * 0.5f;
        return new PanelOffset(X, y);
    }
}

/// <summary>
/// DOCK1: the docked-panel arithmetic, game-free. A docked panel sits against its edge; several on one side line up
/// inward in slot order with <see cref="PanelFlow.ColumnGap"/> between them. Strips on the top or bottom edge with one
/// alignment line up along that edge, the same gap apart (<see cref="PlaceStrip"/>). Every other panel (the window row
/// and the ordinary panel flow) is laid out on the canvas the side docks leave (<see cref="FreeArea"/>), so nothing lands
/// on a side-docked panel; a strip reserves nothing. A pure function of the docked set and the canvas: using a panel
/// never moves it.
/// </summary>
internal static class UiDockLayout
{
    /// <summary>The default gap between the screen edge and a docked frame: the panels' own edge margin.</summary>
    internal const float DefaultMargin = PanelFlow.EdgeMargin;

    /// <summary>The largest margin a dock takes (a quarter of the design width).</summary>
    internal const float MaxMargin = 480f;

    /// <summary>The largest share of the screen a docked frame's width may ask for.</summary>
    internal const float MaxFraction = DialogLayout.WideScreenShare;

    /// <summary>The largest offset along a top or bottom edge, either way (the design width).</summary>
    internal const float MaxOffset = 1920f;

    /// <summary>Why these dock options are refused, or null.</summary>
    internal static string? Refuse(UiDock side, UiDockOptions o)
    {
        if (side is not (UiDock.None or UiDock.Left or UiDock.Right or UiDock.Top or UiDock.Bottom))
            return "Dock side must be None, Left, Right, Top or Bottom.";
        bool strip = side is UiDock.Top or UiDock.Bottom;
        if (strip && o.Align is not (UiDockAlign.Start or UiDockAlign.Center or UiDockAlign.End))
            return "A top or bottom dock's alignment must be Start, Center or End.";
        if (!strip && o.Align is not (UiDockAlign.Center or UiDockAlign.Top or UiDockAlign.Start or UiDockAlign.End))
            return "A side dock's alignment must be Center, Top, Start or End.";
        if (!float.IsFinite(o.Offset) || Math.Abs(o.Offset) > MaxOffset) return "Dock offset must be -" + MaxOffset + " to " + MaxOffset + " units.";
        if (!strip && side != UiDock.None && o.Offset != 0f) return "Dock offset applies to a top or bottom dock; it must be 0 on a side.";
        if (!float.IsFinite(o.Margin) || o.Margin < 0f || o.Margin > MaxMargin) return "Dock margin must be 0 to " + MaxMargin + " units.";
        if (!float.IsFinite(o.Width) || o.Width < 0f) return "Dock width must be 0 (the panel's own) or a positive number of units.";
        if (!float.IsFinite(o.WidthFraction) || o.WidthFraction < 0f || o.WidthFraction > MaxFraction)
            return "Dock width fraction must be 0 (unused) or more than 0 and at most " + MaxFraction + ".";
        return null;
    }

    private static float Sane(float value, float fallback) => float.IsNaN(value) || value <= 0f ? fallback : value;

    /// <summary>The width <paramref name="dock"/> asks for on a canvas <paramref name="canvasWidth"/> wide, before the frame's limits; 0 for none.</summary>
    internal static float AskedWidth(in UiDockSpec dock, float canvasWidth)
    {
        float canvas = Sane(canvasWidth, PanelFlow.FallbackCanvasWidth);
        return dock.Fraction > 0f ? dock.Fraction * canvas : dock.Width;
    }

    /// <summary>
    /// The frame width a dock is drawn at: the kit's 640 frame at least, a wide ask capped at 90% of the canvas. A top or
    /// bottom strip that asks for less than 640 is drawn at its ask, never below <see cref="MinStripWidth"/>
    /// (<see cref="IsNarrowStrip"/>).
    /// </summary>
    internal static float FrameWidth(in UiDockSpec dock, float canvasWidth)
    {
        float canvas = Sane(canvasWidth, PanelFlow.FallbackCanvasWidth);
        float asked = AskedWidth(dock, canvas);
        if (IsNarrowAsk(dock, asked)) return Math.Max(MinStripWidth, asked);
        return UiWindowLayout.FrameWidthFor(asked, canvas);
    }

    /// <summary>The narrowest a top or bottom strip is drawn.</summary>
    internal const float MinStripWidth = 240f;

    /// <summary>The shortest a narrow strip is drawn (one line of text and its pads).</summary>
    internal const float MinStripHeight = 48f;

    private static bool IsNarrowAsk(in UiDockSpec dock, float asked) =>
        dock.Horizontal && asked > 0f && asked < UiWindowLayout.BasicFrameWidth - 0.01f;

    /// <summary>
    /// A top or bottom strip whose width ask (<c>Width</c> or <c>WidthFraction</c> of the canvas) is under the kit's 640
    /// frame: it is drawn at that width (at least <see cref="MinStripWidth"/>), with no title band and as tall as its
    /// content (<see cref="StripFrameHeight"/>). A side dock, and a strip that asks for nothing or for 640 and more, keep
    /// the ordinary frame rules.
    /// </summary>
    internal static bool IsNarrowStrip(in UiDockSpec dock, float canvasWidth) =>
        IsNarrowAsk(dock, AskedWidth(dock, canvasWidth));

    /// <summary>
    /// A narrow strip's frame height: its content (the rows and their pads, <see cref="PanelLayout.ContentHeight"/>) and
    /// its footer band, at least <see cref="MinStripHeight"/>. The dock's area cap applies after.
    /// </summary>
    internal static float StripFrameHeight(float contentHeight, float footerBand)
    {
        float content = float.IsNaN(contentHeight) || contentHeight < 0f ? 0f : contentHeight;
        float band = float.IsNaN(footerBand) || footerBand < 0f ? 0f : footerBand;
        return Math.Max(MinStripHeight, content + band);
    }

    /// <summary>A narrow strip's row width: the frame less the viewport's side insets (the kit's 40 in all).</summary>
    internal static float StripRowWidth(float frameWidth) => DialogLayout.ContentRowWidth(Math.Max(MinStripWidth, frameWidth));

    /// <summary>
    /// How far from the screen edge dock <paramref name="index"/> of <paramref name="docks"/> starts (its outer edge) and
    /// ends (its inner edge), its own frame <paramref name="ownWidth"/> wide (0: from its spec). The first dock on a side
    /// sits its margin from the edge; each later one a column gap inside the one before it. A top or bottom strip takes
    /// no width from a side: (0, 0).
    /// </summary>
    internal static (float Outer, float Inner) Edges(IReadOnlyList<UiDockSpec> docks, int index, float canvasWidth, float ownWidth = 0f)
    {
        var side = docks[index].Side;
        if (docks[index].Horizontal) return (0f, 0f);
        float inner = -1f;
        for (int i = 0; i < index; i++)
        {
            if (docks[i].Side != side) continue;
            float outerI = inner < 0f ? docks[i].Margin : inner + PanelFlow.ColumnGap;
            inner = outerI + FrameWidth(docks[i], canvasWidth);
        }

        float outer = inner < 0f ? docks[index].Margin : inner + PanelFlow.ColumnGap;
        float w = ownWidth > 0f ? ownWidth : FrameWidth(docks[index], canvasWidth);
        return (outer, outer + w);
    }

    /// <summary>How much of the canvas, from each edge, the docked panels on that side take (0 when none is docked there).</summary>
    internal static (float Left, float Right) Reserved(IReadOnlyList<UiDockSpec>? docks, float canvasWidth)
    {
        float left = 0f, right = 0f;
        if (docks == null) return (0f, 0f);
        for (int i = 0; i < docks.Count; i++)
        {
            if (docks[i].Horizontal) continue;
            float inner = Edges(docks, i, canvasWidth).Inner;
            if (docks[i].Side == UiDock.Left) left = Math.Max(left, inner);
            else if (docks[i].Side == UiDock.Right) right = Math.Max(right, inner);
        }

        return (left, right);
    }

    /// <summary>
    /// The canvas the other panels are laid out on while panels are docked: its width, and how far its center sits right
    /// of the canvas center. A column gap separates the area from a docked frame (the area keeps its own edge margin, so
    /// the gap is counted once). When the area left could not hold one 640 frame, the docks are ignored and the whole
    /// canvas is used: a panel over a docked one beats a panel off the screen. Top and bottom strips take nothing.
    /// </summary>
    internal static (float Width, float Shift) FreeArea(IReadOnlyList<UiDockSpec>? docks, float canvasWidth)
    {
        float canvas = Sane(canvasWidth, PanelFlow.FallbackCanvasWidth);
        if (docks == null || docks.Count == 0) return (canvas, 0f);
        var (resL, resR) = Reserved(docks, canvas);
        float cutL = resL > 0f ? Math.Max(0f, resL + PanelFlow.ColumnGap - PanelFlow.EdgeMargin) : 0f;
        float cutR = resR > 0f ? Math.Max(0f, resR + PanelFlow.ColumnGap - PanelFlow.EdgeMargin) : 0f;
        float width = canvas - cutL - cutR;
        if (width - 2f * PanelFlow.EdgeMargin < UiWindowLayout.BasicFrameWidth - 0.01f) return (canvas, 0f);
        return (width, (cutL - cutR) * 0.5f);
    }

    /// <summary>
    /// Where dock <paramref name="index"/> of the shown <paramref name="docks"/> goes, its frame <paramref name="ownWidth"/>
    /// wide (0: from its spec): against its edge, inside the screen; vertically in the area between the margins (under the
    /// Mods workspace's tab strip while <paramref name="stripBottom"/> is given), at most as tall as a window row's frame.
    /// A top or bottom strip is <see cref="PlaceStrip"/>.
    /// </summary>
    internal static DockPlacement Place(
        IReadOnlyList<UiDockSpec> docks, int index, float canvasWidth, float canvasHeight, float ownWidth = 0f, float? stripBottom = null)
    {
        float canvasW = Sane(canvasWidth, PanelFlow.FallbackCanvasWidth);
        float canvasH = Sane(canvasHeight, PanelLayout.FallbackScreenHeight);
        var spec = docks[index];
        if (spec.Horizontal) return PlaceStrip(docks, index, canvasW, canvasH, ownWidth, stripBottom);
        var (outer, inner) = Edges(docks, index, canvasW, ownWidth);
        float w = inner - outer;
        float x = spec.Side == UiDock.Right ? canvasW * 0.5f - outer - w * 0.5f : -canvasW * 0.5f + outer + w * 0.5f;
        // Inside the screen: a stack that ran out of room is clamped to the far edge (it overlaps the dock beside it).
        float limit = Math.Max(0f, (canvasW - w) * 0.5f);
        x = Math.Max(-limit, Math.Min(limit, x));

        float margin = PanelFlow.EdgeMargin;
        float top = canvasH * 0.5f - margin;
        if (stripBottom.HasValue && !float.IsNaN(stripBottom.Value)) top = Math.Min(top, stripBottom.Value - UiWindowLayout.StripClearance);
        float bottom = -canvasH * 0.5f + margin;
        float cap = Math.Max(PanelLayout.MinPanelHeight, Math.Min(top - bottom, PanelLayout.PanelHeightFor(float.MaxValue, canvasH)));
        var hang = spec.Align switch
        {
            UiDockAlign.Top or UiDockAlign.Start => DockHang.Top,
            UiDockAlign.End => DockHang.Bottom,
            _ => DockHang.Center,
        };
        return new DockPlacement(x, top, bottom, cap, hang);
    }

    /// <summary>
    /// A top or bottom strip's place. Strips on one edge with one alignment form a row in slot order,
    /// <see cref="PanelFlow.ColumnGap"/> apart: <see cref="UiDockAlign.Start"/> from the left edge margin rightward,
    /// <see cref="UiDockAlign.End"/> from the right edge margin leftward (the longest-open at the edge), and
    /// <see cref="UiDockAlign.Center"/> centered on the screen as one row. Each frame then moves by its own
    /// <see cref="UiDockSpec.Offset"/> and is clamped inside the screen. Vertically a top strip hangs <c>Margin</c> below
    /// the top edge (and under the Mods workspace's tab strip while <paramref name="stripBottom"/> is given) and grows
    /// down; a bottom strip stands <c>Margin</c> above the bottom edge and grows up. Either is at most as tall as a window
    /// row's frame. Canvas units: the canvas follows the game's reference size, so only the width differs between 16:9
    /// and 32:9, and a centered strip stays on the screen's center line at every aspect.
    /// </summary>
    internal static DockPlacement PlaceStrip(
        IReadOnlyList<UiDockSpec> docks, int index, float canvasWidth, float canvasHeight, float ownWidth = 0f, float? stripBottom = null)
    {
        float canvasW = Sane(canvasWidth, PanelFlow.FallbackCanvasWidth);
        float canvasH = Sane(canvasHeight, PanelLayout.FallbackScreenHeight);
        var spec = docks[index];
        float edge = PanelFlow.EdgeMargin;
        float gap = PanelFlow.ColumnGap;
        float w = ownWidth > 0f ? ownWidth : FrameWidth(spec, canvasW);
        float before = 0f, after = 0f;
        for (int i = 0; i < docks.Count; i++)
        {
            if (i == index || docks[i].Side != spec.Side || docks[i].Align != spec.Align) continue;
            float step = FrameWidth(docks[i], canvasW) + gap;
            if (i < index) before += step;
            else after += step;
        }

        float x = spec.Align switch
        {
            UiDockAlign.Start => -canvasW * 0.5f + edge + before + w * 0.5f,
            UiDockAlign.End => canvasW * 0.5f - edge - before - w * 0.5f,
            // The whole row centered on the screen: it starts half its width left of the center line.
            _ => -(before + w + after) * 0.5f + before + w * 0.5f,
        };
        x += spec.Offset;
        float limit = Math.Max(0f, (canvasW - w) * 0.5f);
        x = Math.Max(-limit, Math.Min(limit, x));

        float top = canvasH * 0.5f - edge;
        float bottom = -canvasH * 0.5f + edge;
        if (spec.Side == UiDock.Top) top = canvasH * 0.5f - spec.Margin;
        else bottom = -canvasH * 0.5f + spec.Margin;
        if (stripBottom.HasValue && !float.IsNaN(stripBottom.Value)) top = Math.Min(top, stripBottom.Value - UiWindowLayout.StripClearance);
        float cap = Math.Max(PanelLayout.MinPanelHeight, Math.Min(top - bottom, PanelLayout.PanelHeightFor(float.MaxValue, canvasH)));
        return new DockPlacement(x, top, bottom, cap, spec.Side == UiDock.Top ? DockHang.Top : DockHang.Bottom);
    }

    /// <summary>A docked panel's placement when it has no dock spot yet (the first layout of a show): docked alone.</summary>
    internal static DockPlacement PlaceAlone(in UiDockSpec dock, float canvasWidth, float canvasHeight, float ownWidth = 0f, float? stripBottom = null) =>
        Place(new[] { dock }, 0, canvasWidth, canvasHeight, ownWidth, stripBottom);
}
