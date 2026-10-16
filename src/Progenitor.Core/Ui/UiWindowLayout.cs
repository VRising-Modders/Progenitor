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
    /// small tool used during play (a dropdown and a few buttons). Opens beside the other windows; the game keeps the
    /// input (and the controller) while the pointer is elsewhere.
    /// </summary>
    Basic = 0,

    /// <summary>
    /// A medium window with the game's ornamental header band: the title between the game's side ornaments and over its
    /// divider, an optional toolbar of small buttons (with an optional Rename), an optional tab row, then the content.
    /// For one thing the player works on (a container, a station, a named set). Opens beside the other windows, takes the
    /// input while shown, and a controller navigates it.
    /// </summary>
    Header = 1,

    /// <summary>
    /// A large window like the game's own big menus: the header band, a tab row across the top, an info column on the
    /// left and the main content on the right. Opens beside the other windows (alone, it is centered), takes the input
    /// while shown, and a controller navigates it.
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
        UiWindowPreset preset, float frameWidth, bool headerBand, bool toolbarAllowed, bool tabsAllowed,
        float infoColumnWidth, UiCaptureMode capture)
    {
        Preset = preset;
        FrameWidth = frameWidth;
        HeaderBand = headerBand;
        ToolbarAllowed = toolbarAllowed;
        TabsAllowed = tabsAllowed;
        InfoColumnWidth = infoColumnWidth;
        CaptureMode = capture;
    }

    internal UiWindowPreset Preset { get; }

    /// <summary>The frame's width asked for (capped at 90% of the screen for a wide window).</summary>
    internal float FrameWidth { get; }

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

    /// <summary>
    /// The large window's frame. 1040 (live fix): the info column gets a 340-unit row, wide enough for a name and its
    /// value on one line, and the content keeps a 600-unit row (the 960 frame left the column 260 and wrapped its facts).
    /// </summary>
    internal const float TabbedFrameWidth = 1040f;

    /// <summary>The large window's info column, its gap included (a 340-unit row: about a third of the window's rows).</summary>
    internal const float InfoColumnWidth = 360f;

    /// <summary>The air between the info column and the content.</summary>
    internal const float InfoColumnGap = 20f;

    /// <summary>
    /// The ornaments either side of the title (<c>Window_TitleSideOrnament</c>, 227x17): the game's popups draw each at
    /// 171x17 beside the title text in a 512-unit title row (probe D1, the Forfeit popup), and so does the kit, at most.
    /// </summary>
    internal const float OrnamentMaxWidth = 171f;

    /// <summary>The ornament's height (the sprite's own 17, as the game draws it).</summary>
    internal const float OrnamentHeight = 17f;

    /// <summary>The narrowest ornament worth drawing; a title that leaves less room is drawn without them.</summary>
    internal const float OrnamentMinWidth = 48f;

    /// <summary>The air between the title text and each ornament.</summary>
    internal const float OrnamentGap = 12f;

    /// <summary>
    /// Each ornament's width beside a title <paramref name="titleWidth"/> wide: what is left of the 560-wide title row on
    /// that side, less the gap, at most <see cref="OrnamentMaxWidth"/>; 0 (none drawn) when that is below
    /// <see cref="OrnamentMinWidth"/>, so a long title is never drawn over.
    /// </summary>
    internal static float OrnamentWidth(float titleWidth)
    {
        if (float.IsNaN(titleWidth) || titleWidth < 0f) return 0f;
        float room = UnityUiRuntimeMetrics.RowWidth * 0.5f - titleWidth * 0.5f - OrnamentGap;
        if (room < OrnamentMinWidth) return 0f;
        return Math.Min(OrnamentMaxWidth, room);
    }

    /// <summary>Whether a title <paramref name="titleWidth"/> wide leaves the ornaments room (<see cref="OrnamentWidth"/>).</summary>
    internal static bool OrnamentsFit(float titleWidth) => OrnamentWidth(titleWidth) > 0f;

    /// <summary>
    /// How far the version stamp sits above the frame's bottom edge in a standard window. The game's popup frame has a
    /// thick border; on the bare band the 13-unit line sat about 4 units from the edge (live 1080 capture).
    /// </summary>
    internal const float StampLift = 10f;

    /// <summary>The bottom band a standard window reserves for its stamp: the stamp's band and its lift; 0 without a stamp.</summary>
    internal static float StampBand(string? stamp) =>
        PanelFooterLayout.BandFor(stamp) > 0f ? PanelFooterLayout.BandFor(stamp) + StampLift : 0f;

    /// <summary>
    /// The style an info-column fact's value is drawn in: <see cref="UiTextStyle.Number"/> when it holds a figure (so the
    /// figures line up), else <see cref="UiTextStyle.Body"/>.
    /// </summary>
    internal static UiTextStyle InfoValueStyle(string? value) => LooksNumeric(value) ? UiTextStyle.Number : UiTextStyle.Body;

    /// <summary>
    /// A value that reads as figures: at least one digit, and otherwise only separators, signs and a unit sign
    /// (<c>3,212</c>, <c>14 / 40</c>, <c>12:04</c>, <c>-5</c>, <c>48%</c>, <c>x3</c>). Words with a figure in them
    /// (<c>Level 3</c>, <c>4 min</c>) are text.
    /// </summary>
    internal static bool LooksNumeric(string? value)
    {
        string v = (value ?? "").Trim();
        bool digit = false;
        for (int i = 0; i < v.Length; i++)
        {
            char c = v[i];
            if (c >= '0' && c <= '9') { digit = true; continue; }
            if (" ,./:+-%\u2212\u00D7".IndexOf(c) >= 0) continue;
            // A multiplier sign before or after the figures (x3, 3x), never a word.
            if ((c == 'x' || c == 'X') && (i == 0 || i == v.Length - 1)) continue;
            return false;
        }

        return digit;
    }

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
        UiWindowPreset.Basic, BasicFrameWidth, headerBand: false, toolbarAllowed: false, tabsAllowed: false,
        infoColumnWidth: 0f, UiCaptureMode.WhenHovered);

    private static readonly UiWindowSpec HeaderSpec = new(
        UiWindowPreset.Header, BasicFrameWidth, headerBand: true, toolbarAllowed: true, tabsAllowed: true,
        infoColumnWidth: 0f, UiCaptureMode.WhileShown);

    private static readonly UiWindowSpec TabbedSpec = new(
        UiWindowPreset.Tabbed, TabbedFrameWidth, headerBand: true, toolbarAllowed: true, tabsAllowed: true,
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
    internal static float FrameWidthFor(UiWindowSpec spec, float availableWidth) => FrameWidthFor(spec.FrameWidth, availableWidth);

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

    /// <summary>
    /// The info column's clip height snapped to the last row that fits whole: <paramref name="rowBottoms"/> are the rows'
    /// bottom edges from the column's top, in order. A row the clip would cut through is masked entirely (with the rows
    /// after it), so the column ends on a clean line above the stamp instead of half a row (live 1440 capture).
    /// </summary>
    internal static float SnapClip(IReadOnlyList<float> rowBottoms, float clipHeight)
    {
        if (rowBottoms == null || rowBottoms.Count == 0) return Math.Max(0f, clipHeight);
        float last = 0f;
        foreach (float b in rowBottoms)
        {
            if (b > clipHeight + 0.01f) return last;
            last = b;
        }

        return Math.Max(0f, clipHeight);
    }

    /// <summary>How far the info column can scroll: its content past its clip; 0 when it fits.</summary>
    internal static float InfoScrollMax(float contentHeight, float clipHeight) =>
        float.IsNaN(contentHeight) || float.IsNaN(clipHeight) ? 0f : Math.Max(0f, contentHeight - clipHeight);

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
    /// The spots of the shown panels (in slot order). While a standard window (<paramref name="windowWidth"/> above 0) is
    /// shown, the windows and every ordinary floating panel that flows (<paramref name="flows"/>: not a dialog, not
    /// self-placed, not pinned to the top, not a wide centered game window) are ranked in <b>one</b> set, in slot order, a
    /// floating panel counting as an ordinary 640 frame, so <see cref="Place"/> lays them out side by side and none covers
    /// another (the review's case: the Gallery's floating panel and a Basic window both centered at x 0). The spots also
    /// say whether the Mods workspace is on screen. With no standard window shown, every panel keeps the ordinary rank of
    /// the panel flow, exactly as before the presets existed. Workspace panes, dialogs and self-placed panels never join
    /// the row (they draw from their own geometry or stay centered).
    /// </summary>
    internal static List<(T Panel, PanelSpot Spot)> Spots<T>(
        IReadOnlyList<T> shown, Func<T, float> windowWidth, Func<T, bool> workspace, Func<T, bool>? flows = null,
        Func<T, long>? shownSequence = null)
    {
        var result = new List<(T, PanelSpot)>(shown.Count);
        bool workspaceOpen = false;
        bool anyWindow = false;
        for (int i = 0; i < shown.Count; i++)
        {
            bool pane = workspace(shown[i]);
            if (pane) workspaceOpen = true;
            else if (windowWidth(shown[i]) > 0f) anyWindow = true;
        }

        var row = new List<int>();
        var others = new List<int>();
        for (int i = 0; i < shown.Count; i++)
        {
            var p = shown[i];
            bool inRow = !workspace(p) && (windowWidth(p) > 0f || (anyWindow && (flows?.Invoke(p) ?? false)));
            if (inRow) row.Add(i);
            else others.Add(i);
        }

        var widths = new float[row.Count];
        for (int w = 0; w < row.Count; w++)
        {
            float asked = windowWidth(shown[row[w]]);
            widths[w] = asked > 0f ? asked : BasicFrameWidth;
        }

        // Draw layers: by the order the row's panels were shown, the last on top (and the first Escape closes).
        var byShow = new List<int>(row.Count);
        for (int w = 0; w < row.Count; w++) byShow.Add(w);
        if (shownSequence != null)
            byShow.Sort((a, b) =>
            {
                int c = shownSequence(shown[row[a]]).CompareTo(shownSequence(shown[row[b]]));
                return c != 0 ? c : a.CompareTo(b);
            });
        var layer = new int[row.Count];
        for (int l = 0; l < byShow.Count; l++) layer[byShow[l]] = l;
        for (int w = 0; w < row.Count; w++) result.Add((shown[row[w]], new PanelSpot(w, widths, workspaceOpen, layer[w])));
        for (int o = 0; o < others.Count; o++) result.Add((shown[others[o]], new PanelSpot(o, others.Count)));
        return result;
    }

    /// <summary>The air kept between the workspace's tab strip and a window's top edge.</summary>
    internal const float StripClearance = 16f;

    /// <summary>
    /// The top of the area windows may use while the Mods workspace is shown: the bottom edge of its tab strip (canvas
    /// units, upward from the canvas center), from the workspace frame the panes are laid out on. A frame not read yet
    /// (width 0) uses the frame the workspace falls back to on this canvas.
    /// </summary>
    internal static float WorkspaceStripBottom(in ModsWorkspaceFrame frame, float canvasWidth, float canvasHeight)
    {
        var f = frame.Width > 1f && frame.Height > 1f ? frame : ModsWorkspaceFrame.Fallback(canvasWidth, canvasHeight);
        var strip = MenuPanelLayout.ForStrip(f);
        return strip.Y - strip.Height * 0.5f;
    }

    /// <summary>
    /// The frame a window asking for <paramref name="wished"/> is drawn at on a canvas <paramref name="canvasWidth"/> wide:
    /// the ordinary 640, or a wide window's wish capped at 90% of the canvas and never below 640.
    /// </summary>
    internal static float FrameWidthFor(float wished, float canvasWidth) =>
        float.IsNaN(wished) || wished <= BasicFrameWidth ? BasicFrameWidth : DialogLayout.FrameWidthFor(wished, canvasWidth);

    /// <summary>
    /// WINDOWS1 placement rule. The windows open in a row, in the order they were opened (oldest at the left), the row
    /// centered on the screen with <see cref="PanelFlow.ColumnGap"/> between frames, so none covers another. Every window's
    /// top edge is on one line: just under the Mods workspace's tab strip while the workspace is shown (never over its
    /// tabs), else where the tallest frame the screen allows would be centered; a window grows downward, so its title never
    /// moves. The row takes as many windows as fit the screen's width less <see cref="PanelFlow.EdgeMargin"/> each side.
    /// When space runs out, each further window cascades over a window of the row, from the right: one title band
    /// (<see cref="PanelFlow.CascadeStep"/>) right and down per layer, so the title of the window under it stays visible,
    /// clamped inside the screen; the step shrinks only when the screen is too short for whole bands. Every window's
    /// height is capped so its bottom stays on screen (<see cref="WindowPlacement.MaxHeight"/>). A pure function of the
    /// set and the canvas: using a window never moves it or the others.
    /// </summary>
    internal static WindowPlacement Place(PanelSpot spot, float canvasWidth, float canvasHeight, float? stripBottom = null)
    {
        float canvasW = float.IsNaN(canvasWidth) || canvasWidth <= 0f ? PanelFlow.FallbackCanvasWidth : canvasWidth;
        float canvasH = float.IsNaN(canvasHeight) || canvasHeight <= 0f ? PanelLayout.FallbackScreenHeight : canvasHeight;
        float margin = PanelFlow.EdgeMargin;
        float gap = PanelFlow.ColumnGap;
        float top = canvasH * 0.5f - margin;
        if (spot.WorkspaceOpen && stripBottom.HasValue && !float.IsNaN(stripBottom.Value))
            top = Math.Min(top, stripBottom.Value - StripClearance);
        float bottom = -canvasH * 0.5f + margin;
        float cap = Math.Max(PanelLayout.MinPanelHeight, Math.Min(top - bottom, PanelLayout.PanelHeightFor(float.MaxValue, canvasH)));
        // The row's top: as high as the area allows, but no higher than where the tallest frame would be centered in it.
        float rowTop = Math.Min(top, (top + bottom) * 0.5f + cap * 0.5f);
        if (!spot.IsWindow || !spot.IsPlaced)
            return new WindowPlacement(0f, rowTop, cap, 0);

        var asked = spot.WindowWidths!;
        int n = asked.Length;
        var widths = new float[n];
        float usable = Math.Max(1f, canvasW - 2f * margin);
        for (int i = 0; i < n; i++) widths[i] = Math.Min(FrameWidthFor(asked[i], canvasW), usable);

        // The row: the longest run of windows from the first that fits side by side.
        int fit = 1;
        float span = widths[0];
        while (fit < n && span + gap + widths[fit] <= usable + 0.01f)
        {
            span += gap + widths[fit];
            fit++;
        }

        var rowX = new float[fit];
        float x = -span * 0.5f;
        for (int i = 0; i < fit; i++)
        {
            rowX[i] = x + widths[i] * 0.5f;
            x += widths[i] + gap;
        }

        int rank = spot.Rank;
        if (rank < fit) return new WindowPlacement(rowX[rank], rowTop, cap, 0);

        // Out of room: cascade over the row, the rightmost window first. Layer d is d steps right and down from the
        // row window it covers.
        int k = rank - fit;
        int slot = fit - 1 - k % fit;
        int depth = k / fit + 1;
        int deepest = (n - fit - 1) / fit + 1;
        float step = Math.Min(PanelFlow.CascadeStep, Math.Max(0f, (rowTop - bottom - PanelLayout.MinPanelHeight) / deepest));
        float w = widths[rank];
        float right = canvasW * 0.5f - margin - w * 0.5f;
        float left = -canvasW * 0.5f + margin + w * 0.5f;
        float cx = Math.Max(left, Math.Min(right, rowX[slot] + depth * step));
        float t = rowTop - depth * step;
        return new WindowPlacement(cx, t, Math.Max(PanelLayout.MinPanelHeight, Math.Min(cap, t - bottom)), depth);
    }

    /// <summary>
    /// The re-flow's placement of a panel in the window row. When the set changed its height cap (<paramref name="place"/>
    /// against <paramref name="lastCap"/>, a negative one meaning never laid out), <paramref name="relayout"/> lays it out
    /// again first (sizing it to the new cap), and the frame's height is read <i>after</i> that, so the panel hangs from
    /// its top line at the height it now has. Reading the height before the re-layout left a frame whose cap grew
    /// (the windows in front of it closed) half the growth too high, over the workspace's tab strip.
    /// </summary>
    internal static PanelOffset RepositionInRow(
        WindowPlacement place, float lastCap, Func<float> frameHeight, Action relayout, out float cap)
    {
        cap = place.MaxHeight;
        if (lastCap >= 0f && Math.Abs(place.MaxHeight - lastCap) > 0.5f) relayout();
        return place.CenterFor(Math.Min(frameHeight(), place.MaxHeight));
    }

    /// <summary>
    /// The topmost standard window Escape (or a controller's B) closes: the one shown last of <paramref name="shown"/>
    /// (<paramref name="isWindow"/> true, the highest <paramref name="sequence"/>); default when none is shown. One press
    /// closes one window; the next press goes on to the next window, then to the Mods workspace.
    /// </summary>
    internal static T? EscapeTarget<T>(IReadOnlyList<T> shown, Func<T, bool> isWindow, Func<T, long> sequence) where T : class
    {
        T? best = null;
        long seq = long.MinValue;
        foreach (var p in shown)
        {
            if (p == null || !isWindow(p)) continue;
            long s = sequence(p);
            if (best == null || s > seq)
            {
                best = p;
                seq = s;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether the Mods workspace behind the shown windows is dimmed: while the workspace is on screen and at least one
    /// shown standard window takes the input while shown (Header, Tabbed, or a Basic window opened with a controller).
    /// The dim is the game's popup blocker color at <see cref="ShadeAlpha"/>; it only darkens, it takes no clicks, so the
    /// workspace stays usable beside a window. A Basic window that leaves the input to the pointer does not dim it.
    /// </summary>
    internal static bool ShadeWorkspace(bool workspaceOpen, IEnumerable<UiCaptureMode> windowModes)
    {
        if (!workspaceOpen) return false;
        foreach (var m in windowModes)
            if (m == UiCaptureMode.WhileShown) return true;
        return false;
    }

    /// <summary>The workspace dim's opacity under a window (the game's own popup blocker is 0.8 under a modal popup).</summary>
    internal const float ShadeAlpha = 0.6f;

    /// <summary>
    /// The draw order, bottom to top: the Mods workspace panes (<see cref="ModsCanvasBand.Panels"/>), the ordinary panels
    /// (<see cref="UiOwnership.OverlaySorting"/>), the workspace dim, the standard windows, then every dialog, so a prompt a
    /// window opens is drawn over that window with its own dim over it. The notice band, open lists and tooltips stay above.
    /// </summary>
    internal static int ShadeSorting => UiOwnership.OverlaySorting + 1;

    /// <inheritdoc cref="ShadeSorting"/>
    internal static int WindowSorting => UiOwnership.OverlaySorting + 2;

    /// <summary>The highest order a window layer takes (<see cref="LayerSorting"/>); further layers share it.</summary>
    internal static int WindowSortingMax => UiOwnership.OverlaySorting + 15;

    /// <inheritdoc cref="ShadeSorting"/>
    internal static int DialogSorting => UiOwnership.OverlaySorting + 16;

    /// <summary>
    /// WINDOWS1 live fix: the canvas order of a row panel on draw layer <paramref name="layer"/> (0: shown longest ago).
    /// Every window had the same order, so which one drew on top was left to Unity: the first-created window of a fresh
    /// set drew under the others although it was shown last. Now the panel shown last draws on top.
    /// </summary>
    /// <remarks>Layers past <c>WindowSortingMax - WindowSorting</c> (14 row panels at once) share the top order; deliberately left so.</remarks>
    internal static int LayerSorting(int layer) => Math.Min(WindowSortingMax, WindowSorting + Math.Max(0, layer));

    /// <summary>
    /// WINDOWS1 live fix: a standard window's height while it stays open: never lower than it has been since it was
    /// shown (switching to a shorter tab does not shrink it), never above <paramref name="cap"/>.
    /// </summary>
    internal static float StableHeight(float tallestSoFar, float wanted, float cap)
    {
        float h = Math.Max(float.IsNaN(tallestSoFar) ? 0f : tallestSoFar, wanted);
        return cap > 0f ? Math.Min(cap, h) : h;
    }

    /// <summary>What Escape and a controller's B do in every preset. Fixed, so every mod's windows answer alike.</summary>
    internal const string EscapeRule =
        "Escape or B first closes an open list, dialog or key capture; with none open, it closes the topmost window, one window per press, and only then the Mods workspace.";
}

/// <summary>
/// WINDOWS1: where one standard window goes (<see cref="UiWindowLayout.Place"/>), in canvas units from the canvas center:
/// the frame's center x, its top edge (upward positive), the tallest it may be drawn so its bottom stays on screen, and
/// its cascade layer (0 in the row).
/// </summary>
internal readonly struct WindowPlacement
{
    internal WindowPlacement(float x, float top, float maxHeight, int depth)
    {
        X = x;
        Top = top;
        MaxHeight = maxHeight;
        Depth = depth;
    }

    internal float X { get; }

    internal float Top { get; }

    internal float MaxHeight { get; }

    internal int Depth { get; }

    /// <summary>The frame's center offset for a frame <paramref name="height"/> tall (it hangs from <see cref="Top"/>).</summary>
    internal PanelOffset CenterFor(float height) => new(X, Top - Math.Max(0f, height) * 0.5f);
}
