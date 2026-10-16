using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// WINDOWS1: the three standard windows a mod opens with <c>ctx.Windows.Create</c> (or <see cref="Ui.Window"/>). Each
/// preset fixes the frame art, the title style and spacing, the version stamp, how the window takes input (and so whether
/// a controller navigates it) and what Escape does, so every mod's windows look and behave alike. See docs/ui.md
/// "Standard windows".
/// </summary>
public enum UiWindowPreset
{
    /// <summary>
    /// A plain framed window: the game's popup frame, the title centered in its band, rows on the settings grid. For a
    /// small tool used during play (a dropdown and a few buttons). Tiles with other windows; the game keeps the input
    /// (and the controller) while the pointer is elsewhere.
    /// </summary>
    Basic = 0,

    /// <summary>
    /// A medium window with the game's ornamental header band: the title between the game's side ornaments and over its
    /// divider, an optional toolbar of small buttons (with an optional Rename), an optional tab row, then the content.
    /// For one thing the player works on (a container, a station, a named set). Tiles with other windows, takes the input
    /// while shown, and a controller navigates it.
    /// </summary>
    Header = 1,

    /// <summary>
    /// A large window like the game's own big menus: the header band, a tab row across the top, an info column on the
    /// left and the main content on the right. Centered, not tiled, takes the input while shown, and a controller
    /// navigates it.
    /// </summary>
    Tabbed = 2,
}

/// <summary>WINDOWS1: one button of a window's toolbar (<see cref="UiWindowOptions.Tools"/>).</summary>
public sealed class UiWindowTool
{
    /// <summary>A toolbar button captioned <paramref name="caption"/> (one or two short words) with hover text <paramref name="tooltip"/>.</summary>
    public UiWindowTool(string caption, string tooltip = "")
    {
        Caption = caption ?? "";
        Tooltip = tooltip ?? "";
    }

    /// <summary>The caption: a verb or a short noun (<c>Sort</c>, <c>Locate</c>).</summary>
    public string Caption { get; }

    /// <summary>Hover text; empty for none.</summary>
    public string Tooltip { get; }
}

/// <summary>Options for a standard window. Every member has the preset's default; set only what differs.</summary>
public sealed class UiWindowOptions
{
    /// <summary>
    /// Draw the version stamp at the bottom (<c>&lt;mod name&gt; &lt;version&gt; · Progenitor &lt;version&gt;</c>). On by default.
    /// </summary>
    public bool VersionStamp { get; set; } = true;

    /// <summary>The stamp line to draw instead of the generated one; empty uses the generated line.</summary>
    public string VersionText { get; set; } = "";

    /// <summary>
    /// Header and Tabbed: a Rename button at the end of the toolbar, which asks for a new title in the game's prompt
    /// (<see cref="IUiWindow.OnRename"/>).
    /// </summary>
    public bool Renamable { get; set; }

    /// <summary>Header and Tabbed: the toolbar's buttons (at most six), in order; empty for none. Wire them with <see cref="IUiWindow.Tools"/>.</summary>
    public IReadOnlyList<UiWindowTool> Tools { get; set; } = Array.Empty<UiWindowTool>();

    /// <summary>
    /// Header and Tabbed: the tab captions (at most six short words); empty for none. A Tabbed window without tabs gets one
    /// tab named after its title. Add each tab's content with <see cref="IUiWindow.Add{T}"/>.
    /// </summary>
    public IReadOnlyList<string> Tabs { get; set; } = Array.Empty<string>();

    /// <summary>The tab selected when the window opens.</summary>
    public int SelectedTab { get; set; }

    /// <summary>The input mode; null keeps the preset's (Basic: <see cref="UiCaptureMode.WhenHovered"/>; Header and Tabbed: <see cref="UiCaptureMode.WhileShown"/>).</summary>
    public UiCaptureMode? CaptureMode { get; set; }
}

/// <summary>WINDOWS1: what a preset fixes, in panel units (the game's 1920x1080 design space). Game-free and tested.</summary>
internal sealed class UiWindowSpec
{
    internal UiWindowSpec(
        UiWindowPreset preset, float frameWidth, bool centered, bool headerBand, bool toolbarAllowed, bool tabsAllowed,
        float infoColumnWidth, UiCaptureMode capture)
    {
        Preset = preset;
        FrameWidth = frameWidth;
        Centered = centered;
        HeaderBand = headerBand;
        ToolbarAllowed = toolbarAllowed;
        TabsAllowed = tabsAllowed;
        InfoColumnWidth = infoColumnWidth;
        CaptureMode = capture;
    }

    internal UiWindowPreset Preset { get; }

    /// <summary>The frame's width asked for (capped at 90% of the screen for a wide window).</summary>
    internal float FrameWidth { get; }

    /// <summary>True: centered on screen, never tiled with other windows.</summary>
    internal bool Centered { get; }

    /// <summary>True: the ornamental header band behind the title.</summary>
    internal bool HeaderBand { get; }

    /// <summary>True: tools and Rename are drawn in a pinned toolbar row.</summary>
    internal bool ToolbarAllowed { get; }

    /// <summary>True: <see cref="UiWindowOptions.Tabs"/> draws a pinned tab row.</summary>
    internal bool TabsAllowed { get; }

    /// <summary>The left info column's width including its gap; 0 for none.</summary>
    internal float InfoColumnWidth { get; }

    /// <summary>The default input mode.</summary>
    internal UiCaptureMode CaptureMode { get; }

    /// <summary>A controller navigates the window by default (NAV1 navigates only windows that take the input while shown).</summary>
    internal bool ControllerNavigates => CaptureMode == UiCaptureMode.WhileShown;

    /// <summary>
    /// Basic: a window opened while the player navigates with a controller or keys takes the input while shown, so the
    /// controller reaches it (<see cref="UiWindowLayout.CaptureOnShow"/>).
    /// </summary>
    internal bool ControllerHandoff => CaptureMode == UiCaptureMode.WhenHovered;

    /// <summary>The title band at the top of the frame.</summary>
    internal float TitleBand => PanelLayout.HeaderHeight;

    /// <summary>The width of a content row.</summary>
    internal float ContentRowWidth => UiWindowLayout.ContentRowWidth(FrameWidth, InfoColumnWidth);

    /// <summary>The width of a row in the info column.</summary>
    internal float InfoRowWidth => InfoColumnWidth <= 0f ? 0f : InfoColumnWidth - UiWindowLayout.InfoColumnGap;
}

/// <summary>WINDOWS1: the presets' numbers and rules. Game-free: the runtime draws from these and the tests pin them.</summary>
internal static class UiWindowLayout
{
    /// <summary>The ordinary window frame (the same 640 as every panel).</summary>
    internal const float BasicFrameWidth = UnityUiRuntimeMetrics.PanelWidth;

    /// <summary>The large window's frame.</summary>
    internal const float TabbedFrameWidth = 960f;

    /// <summary>The large window's info column, its gap included.</summary>
    internal const float InfoColumnWidth = 280f;

    /// <summary>The air between the info column and the content.</summary>
    internal const float InfoColumnGap = 20f;

    /// <summary>The ornaments either side of the title: the largest box each is fitted into (aspect kept).</summary>
    internal const float OrnamentMaxWidth = 96f;

    /// <inheritdoc cref="OrnamentMaxWidth"/>
    internal const float OrnamentMaxHeight = 24f;

    /// <summary>The air between the title text and each ornament.</summary>
    internal const float OrnamentGap = 16f;

    /// <summary>
    /// Whether a title <paramref name="titleWidth"/> wide leaves the ornaments their room inside the 560-wide title rect
    /// (at most 336); a longer title is drawn without them rather than under them.
    /// </summary>
    internal static bool OrnamentsFit(float titleWidth) =>
        !float.IsNaN(titleWidth) && titleWidth * 0.5f + OrnamentGap + OrnamentMaxWidth <= UnityUiRuntimeMetrics.RowWidth * 0.5f;

    /// <summary>The title's center line from the frame top (the title band's title rect is 18..58).</summary>
    internal const float TitleCenterY = 38f;

    /// <summary>The header divider's center line from the frame top.</summary>
    internal const float DividerY = 64f;

    /// <summary>The header divider's height when the game's art is drawn (the kit's fallback rule is 1).</summary>
    internal const float DividerHeight = 12f;

    /// <summary>The header band's ground behind the title: a darker wash over the popup frame, as the game's container windows have.</summary>
    internal static readonly (float R, float G, float B, float A) HeaderBandColor = (0.02f, 0.02f, 0.03f, 0.55f);

    /// <summary>The info column's ground: a faint darker wash so the column reads as its own area.</summary>
    internal static readonly (float R, float G, float B, float A) InfoColumnColor = (0f, 0f, 0f, 0.28f);

    /// <summary>The divider's kit stand-in when the game's <c>Divider_Big_02</c> is not loaded (the dialog title rule's).</summary>
    internal static readonly (float R, float G, float B, float A) DividerKitColor = (0.36f, 0.48f, 0.6f, 0.6f);

    /// <summary>The most tabs a window takes; more are refused.</summary>
    internal const int MaxTabs = 6;

    /// <summary>The most toolbar buttons a window takes (Rename not counted); more are refused.</summary>
    internal const int MaxTools = 6;

    /// <summary>The longest toolbar caption; longer captions are refused.</summary>
    internal const int MaxToolCaption = 16;

    /// <summary>The Rename button's caption (a word, not a pencil: the game's font has no pencil glyph).</summary>
    internal const string RenameCaption = "Rename";

    private static readonly UiWindowSpec BasicSpec = new(
        UiWindowPreset.Basic, BasicFrameWidth, centered: false, headerBand: false, toolbarAllowed: false, tabsAllowed: false,
        infoColumnWidth: 0f, UiCaptureMode.WhenHovered);

    private static readonly UiWindowSpec HeaderSpec = new(
        UiWindowPreset.Header, BasicFrameWidth, centered: false, headerBand: true, toolbarAllowed: true, tabsAllowed: true,
        infoColumnWidth: 0f, UiCaptureMode.WhileShown);

    private static readonly UiWindowSpec TabbedSpec = new(
        UiWindowPreset.Tabbed, TabbedFrameWidth, centered: true, headerBand: true, toolbarAllowed: true, tabsAllowed: true,
        infoColumnWidth: InfoColumnWidth, UiCaptureMode.WhileShown);

    /// <summary>The spec of <paramref name="preset"/>; an undefined value throws.</summary>
    internal static UiWindowSpec Spec(UiWindowPreset preset) => preset switch
    {
        UiWindowPreset.Basic => BasicSpec,
        UiWindowPreset.Header => HeaderSpec,
        UiWindowPreset.Tabbed => TabbedSpec,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown UiWindowPreset."),
    };

    /// <summary>
    /// A content row's width in a frame <paramref name="frameWidth"/> wide with an info column <paramref name="infoColumn"/>
    /// wide: the ordinary frame keeps its 560 row; a wider frame keeps the ordinary 40-unit side margins, less the column.
    /// </summary>
    internal static float ContentRowWidth(float frameWidth, float infoColumn = 0f)
    {
        float frame = float.IsNaN(frameWidth) || frameWidth <= 0f ? BasicFrameWidth : frameWidth;
        float row = frame <= BasicFrameWidth
            ? UnityUiRuntimeMetrics.RowWidth
            : frame - (BasicFrameWidth - UnityUiRuntimeMetrics.RowWidth);
        if (infoColumn > 0f) row -= infoColumn;
        return Math.Max(1f, row);
    }

    /// <summary>
    /// The frame a window of <paramref name="spec"/> is drawn at on a screen <paramref name="availableWidth"/> wide: the
    /// ordinary 640, or a wide window's wish capped at 90% of the screen and never below 640.
    /// </summary>
    internal static float FrameWidthFor(UiWindowSpec spec, float availableWidth) =>
        spec.FrameWidth <= BasicFrameWidth ? BasicFrameWidth : DialogLayout.FrameWidthFor(spec.FrameWidth, availableWidth);

    /// <summary>
    /// The info column's width in a frame drawn at <paramref name="frameWidth"/> instead of the wished width: shrunk with
    /// the frame so the content keeps at least the ordinary 560 row, and 0 when even that does not fit.
    /// </summary>
    internal static float InfoColumnFor(UiWindowSpec spec, float frameWidth)
    {
        if (spec.InfoColumnWidth <= 0f) return 0f;
        float room = ContentRowWidth(frameWidth) - UnityUiRuntimeMetrics.RowWidth;
        if (room < 160f) return 0f;
        return Math.Min(spec.InfoColumnWidth, room);
    }

    /// <summary>
    /// The info column's clip rect, from the frame's top-center: the column's x and width, from <paramref name="top"/> (under
    /// the pinned rows) down to the frame's bottom pad above the stamp band. Rows below its bottom are cut, never drawn over
    /// the stamp or past the frame.
    /// </summary>
    internal static (float CenterX, float Top, float Width, float Height) InfoClip(
        float frameWidth, float band, float top, float panelHeight, float footerBand)
    {
        float width = Math.Max(1f, band - InfoColumnGap);
        float centerX = -frameWidth * 0.5f + PanelLayout.PadSide + width * 0.5f;
        float height = Math.Max(0f, panelHeight - top - PanelLayout.PadBottom - Math.Max(0f, footerBand));
        return (centerX, top, width, height);
    }

    /// <summary>How many info rows of <paramref name="heights"/> (stacked with the row gap) are wholly inside a clip <paramref name="clipHeight"/> tall.</summary>
    internal static int InfoRowsShown(IReadOnlyList<float> heights, float clipHeight)
    {
        float y = 0f;
        int shown = 0;
        foreach (float h in heights)
        {
            if (y + h > clipHeight + 0.01f) break;
            shown++;
            y += h + PanelLayout.RowGap;
        }

        return shown;
    }

    /// <summary>
    /// The version stamp line: <c>&lt;mod name&gt; &lt;version&gt; · Progenitor &lt;version&gt;</c> (the line a mod's
    /// dialogs carry); the mod's name is shortened with an ellipsis before either version when the line is too wide.
    /// <paramref name="enabled"/> false: empty (no stamp). No name and no version: the framework's line alone.
    /// </summary>
    internal static string StampText(string? modName, string? modVersion, string frameworkFooter, bool enabled = true,
        float frameWidth = BasicFrameWidth)
    {
        if (!enabled) return "";
        return DialogLayout.ModFooter(modName, modVersion, frameworkFooter ?? "", PanelFooterLayout.TextWidth(frameWidth));
    }

    /// <summary>Why <paramref name="tabs"/> cannot be a window's tab row, or null when they can.</summary>
    internal static string? RefuseTabs(IReadOnlyList<string>? tabs)
    {
        if (tabs == null || tabs.Count == 0) return null;
        if (tabs.Count > MaxTabs) return "A window takes at most " + MaxTabs + " tabs; " + tabs.Count + " were given.";
        for (int i = 0; i < tabs.Count; i++)
            if (string.IsNullOrWhiteSpace(tabs[i])) return "Tab " + i + " has no caption.";
        return null;
    }

    /// <summary>
    /// Why <paramref name="tools"/> (and Rename when <paramref name="renamable"/>) cannot be a window's toolbar, or null when
    /// they can: at most <see cref="MaxTools"/> captions of at most <see cref="MaxToolCaption"/> characters, and their buttons'
    /// measured widths (the game's compact button, <see cref="PageButtonLayout.Width"/>, and the gaps between them) within
    /// <paramref name="rowWidth"/>, so the toolbar never runs past the frame. <paramref name="rowWidth"/> 0 or less checks
    /// only the counts and captions.
    /// </summary>
    internal static string? RefuseTools(IReadOnlyList<UiWindowTool>? tools, bool renamable = false, float rowWidth = 0f)
    {
        int count = tools?.Count ?? 0;
        if (count == 0 && !renamable) return null;
        if (count > MaxTools) return "A window takes at most " + MaxTools + " toolbar buttons; " + count + " were given.";
        for (int i = 0; i < count; i++)
        {
            string caption = tools![i]?.Caption ?? "";
            if (string.IsNullOrWhiteSpace(caption)) return "Toolbar button " + i + " has no caption.";
            if (caption.Trim().Length > MaxToolCaption)
                return "Toolbar button " + i + " is too long: one or two short words, at most " + MaxToolCaption + " characters.";
        }

        if (rowWidth > 0f)
        {
            float width = ToolbarWidth(tools, renamable);
            if (width > rowWidth + 0.5f)
                return "The toolbar's buttons need " + Math.Round(width).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " units and the window's row is " + Math.Round(rowWidth).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ": use fewer or shorter captions" + (renamable ? " (Rename counts)" : "") + ".";
        }

        return null;
    }

    /// <summary>The toolbar's measured width: each button's compact width side by side with the inline gap, Rename last.</summary>
    internal static float ToolbarWidth(IReadOnlyList<UiWindowTool>? tools, bool renamable)
    {
        var widths = new List<float>();
        if (tools != null)
            foreach (var t in tools) widths.Add(PageButtonLayout.Width((t?.Caption ?? "").Trim()));
        if (renamable) widths.Add(PageButtonLayout.Width(RenameCaption));
        if (widths.Count == 0) return 0f;
        float sum = (widths.Count - 1) * PageButtonLayout.Gap;
        foreach (float w in widths) sum += w;
        return sum;
    }

    /// <summary>The width the toolbar row is drawn at: the frame's content row without the info column (it spans the frame).</summary>
    internal static float ToolbarRowWidth(UiWindowSpec spec) => ContentRowWidth(spec.FrameWidth);

    /// <summary>The tab captions a window draws: none where the preset has no tab row; a Tabbed window always one or more.</summary>
    internal static IReadOnlyList<string> TabsFor(UiWindowPreset preset, string title, UiWindowOptions? options)
    {
        var spec = Spec(preset);
        if (!spec.TabsAllowed) return Array.Empty<string>();
        var tabs = options?.Tabs ?? Array.Empty<string>();
        if (tabs.Count > 0) return tabs;
        return preset == UiWindowPreset.Tabbed
            ? new[] { string.IsNullOrWhiteSpace(title) ? "General" : title.Trim() }
            : Array.Empty<string>();
    }

    /// <summary>Whether a window with these options draws a toolbar row.</summary>
    internal static bool HasToolbar(UiWindowPreset preset, UiWindowOptions? options) =>
        Spec(preset).ToolbarAllowed && options != null && (options.Renamable || (options.Tools?.Count ?? 0) > 0);

    /// <summary>
    /// The pinned rows above the content, from the top, and each one's top edge from the frame top: the toolbar (when it
    /// has buttons or Rename), then the tab row (when it has tabs). Rows are 40 tall with an 8 gap under the title band.
    /// </summary>
    internal static IReadOnlyList<(string Row, float Top)> PinnedRows(bool toolbar, bool tabs)
    {
        var rows = new List<(string, float)>();
        float top = PanelLayout.HeaderHeight + PanelLayout.PadTop;
        if (toolbar)
        {
            rows.Add(("toolbar", top));
            top += UnityUiRuntimeMetrics.RowHeight + PanelLayout.RowGap;
        }

        if (tabs) rows.Add(("tabs", top));
        return rows;
    }

    /// <summary>Where the content (and the info column) start, from the frame top, under the pinned rows.</summary>
    internal static float ContentTop(bool toolbar, bool tabs)
    {
        int pinned = (toolbar ? 1 : 0) + (tabs ? 1 : 0);
        return PanelLayout.HeaderHeight + PanelLayout.PadTop + pinned * (UnityUiRuntimeMetrics.RowHeight + PanelLayout.RowGap);
    }

    /// <summary>
    /// The input mode a window of <paramref name="spec"/> is shown with: the preset's, except that a Basic window opened
    /// while the player is navigating (<paramref name="navigating"/>: the last input was the controller or the navigation
    /// keys) is shown <see cref="UiCaptureMode.WhileShown"/>, so the controller moves into it.
    /// </summary>
    internal static UiCaptureMode CaptureOnShow(UiWindowSpec spec, bool navigating) =>
        spec.ControllerHandoff && navigating ? UiCaptureMode.WhileShown : spec.CaptureMode;

    /// <summary>
    /// The spots of the shown panels (in slot order). Without a centered wide window this is the ordinary rank of the whole
    /// set. With one, the centered window gets 0 of 1 and the count of tiled panels beside it, and every tiled panel is
    /// ranked among the tiled panels only, carrying the centered window's width so its placement keeps clear of it.
    /// Workspace panes keep their place in the whole set (they draw from their own geometry).
    /// </summary>
    internal static List<(T Panel, PanelSpot Spot)> Spots<T>(IReadOnlyList<T> shown, Func<T, float> centeredWidth, Func<T, bool> workspace)
    {
        var result = new List<(T, PanelSpot)>(shown.Count);
        float centered = 0f;
        var tiled = new List<T>();
        foreach (var p in shown)
        {
            float w = centeredWidth(p);
            if (w > 0f) centered = Math.Max(centered, w);
            else if (!workspace(p)) tiled.Add(p);
        }

        for (int i = 0; i < shown.Count; i++)
        {
            var p = shown[i];
            if (centered <= 0f || workspace(p)) result.Add((p, new PanelSpot(i, shown.Count)));
            else if (centeredWidth(p) > 0f) result.Add((p, new PanelSpot(0, 1, centeredWidth(p), tiled.Count)));
            else result.Add((p, new PanelSpot(tiled.IndexOf(p), tiled.Count, centered, 0)));
        }

        return result;
    }

    /// <summary>
    /// Where a panel <paramref name="panelWidth"/> wide goes on a canvas <paramref name="canvasWidth"/> x
    /// <paramref name="canvasHeight"/>. A centered window alone is dead center. With tiled panels beside it, the centered
    /// window and the tiled panels' columns are laid side by side as one centered group, the centered window on the left;
    /// the tiled panels tile (and, past their columns, cascade) inside the area right of it, so none ever lands under it.
    /// When the screen is too narrow for both, the tiled area keeps its own width at the right edge.
    /// </summary>
    internal static PanelOffset Place(PanelSpot spot, float panelWidth, float canvasWidth, float canvasHeight, bool centered)
    {
        float canvasW = float.IsNaN(canvasWidth) || canvasWidth <= 0f ? PanelFlow.FallbackCanvasWidth : canvasWidth;
        if (centered)
        {
            if (spot.Tiled <= 0) return PanelOffset.Centre;
            var g = Group(spot.CenteredWidth > 0f ? spot.CenteredWidth : panelWidth, BasicFrameWidth, spot.Tiled, canvasW);
            return new PanelOffset(g.CenteredX, 0f);
        }

        if (spot.CenteredWidth <= 0f) return PanelFlow.Offset(spot, panelWidth, canvasW, canvasHeight);
        var group = Group(spot.CenteredWidth, panelWidth, spot.Count, canvasW);
        var inArea = PanelFlow.Offset(spot, panelWidth, group.AreaWidth, canvasHeight);
        return new PanelOffset(inArea.X + group.AreaCenterX, inArea.Y);
    }

    private static (float CenteredX, float AreaWidth, float AreaCenterX) Group(float centeredWidth, float tiledWidth, int tiled, float canvasW)
    {
        float gap = PanelFlow.ColumnGap;
        float margin = PanelFlow.EdgeMargin;
        // The tiled area: what is left of the canvas right of the centered window, at least one tiled frame.
        float area = Math.Max(tiledWidth + 2f * margin, canvasW - centeredWidth - gap);
        int columns = PanelFlow.Columns(area, tiledWidth);
        int occupied = Math.Max(1, Math.Min(tiled, columns));
        float tiledSpan = occupied * tiledWidth + (occupied - 1) * gap;
        float groupWidth = centeredWidth + gap + tiledSpan;
        float left = -groupWidth * 0.5f;
        // Too wide for the screen: the tiled columns stay whole at the right edge, the centered window as far left as it fits.
        float rightEdge = canvasW * 0.5f - margin;
        if (left + groupWidth > rightEdge) left = rightEdge - groupWidth;
        float centeredX = left + centeredWidth * 0.5f;
        float tiledLeft = left + centeredWidth + gap;
        // PanelFlow centers the occupied columns at 0 inside the area; shift that center onto the tiled span's center.
        return (centeredX, area, tiledLeft + tiledSpan * 0.5f);
    }

    /// <summary>What Escape and a controller's B do in every preset. Fixed, so every mod's windows answer alike.</summary>
    internal const string EscapeRule =
        "Escape or B first closes an open list, dialog or key capture; with none open, it closes the window.";
}
