#if !PROGENITOR_TESTS
using System;
using Progenitor.Ui.GameArt;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// WINDOWS1: the standard windows' frame on a game window (<see cref="UiWindowLayout"/>): a wide frame, centered rather
/// than tiled, the ornamental header band (a darker ground behind the title, the game's title ornaments either side of
/// it, the game's divider under it) and the Tabbed window's info column (rows pinned <see cref="MenuRowPin.Side"/>, on a
/// darker ground, outside the scrolling content). Every object here is a child of the panel frame, so it goes with the
/// panel; nothing is shared or leased.
/// </summary>
internal sealed partial class UnityUiRuntime
{
    internal const string WindowBandName = "window-header-band";
    internal const string WindowOrnamentLeftName = "window-title-ornament-left";
    internal const string WindowOrnamentRightName = "window-title-ornament-right";
    internal const string WindowDividerName = "window-header-divider";
    internal const string WindowInfoGroundName = "window-info-ground";
    internal const string WindowInfoClipName = "window-info-clip";
    internal const string WindowShadeName = "window-shade";

    private static GameObject? _windowShade;

    /// <summary>
    /// WINDOWS1: the dim over the Mods workspace behind the standard windows (<see cref="UiWindowLayout.ShadeWorkspace"/>):
    /// one full-screen, non-interactive canvas above the workspace panes and every ordinary panel
    /// (<see cref="UiWindowLayout.ShadeSorting"/>) and under the windows and dialogs. It darkens and takes no clicks.
    /// </summary>
    public void WindowShade(bool on)
    {
        try
        {
            if (!on)
            {
                if (Alive(_windowShade)) _windowShade!.SetActive(false);
                return;
            }

            if (!Alive(_windowShade))
            {
                _windowShade = CreateHudCanvas(WindowShadeName, UiWindowLayout.ShadeSorting);
                if (_windowShade == null) return;
                var fill = new GameObject("fill");
                fill.transform.SetParent(_windowShade.transform, false);
                Stretch(fill);
                var image = TryAdd<Image>(fill);
                if (image != null)
                {
                    var c = GameSettingsLook.PopupBlocker;
                    image.color = new Color(c.R, c.G, c.B, UiWindowLayout.ShadeAlpha);
                    image.raycastTarget = false;
                }
            }

            _windowShade!.SetActive(true);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: window shade: " + e.Message);
        }
    }

    private sealed partial class UnityPanel
    {
        private UiWindowSpec? _windowSpec;
        private GameObject? _windowBand;
        private GameObject? _ornamentLeft;
        private GameObject? _ornamentRight;
        private GameObject? _windowDivider;
        private GameObject? _infoGround;

        /// <summary>The info column's top and the next side row's top, from the frame top, during a layout pass.</summary>
        private float _sideCursor;
        private float _sideTop;
        private bool _placingSide;

        /// <summary>The info column's lowest edge from the frame top after the last pass; 0 for none.</summary>
        private float _sideBottom;

        /// <summary>Each placed info row's bottom edge from the column's top, this pass (<see cref="UiWindowLayout.SnapClip"/>).</summary>
        private readonly System.Collections.Generic.List<float> _sideRowBottoms = new();

        /// <summary>WINDOWS1: this game window is a standard window of <paramref name="spec"/>. Idempotent per spec.</summary>
        public void MakeStandardWindow(UiWindowSpec spec)
        {
            if (Released || spec == null) return;
            if (!_gameWindow) MakeGameWindow();
            _windowSpec = spec;
            // WINDOWS1: windows draw above the workspace's dim and the ordinary panels, and under every dialog (a prompt a
            // window opens is drawn over it: the Rename prompt was drawn behind the Header window at an equal order).
            SetOverlaySorting(UiWindowLayout.WindowSorting);
            if (spec.HeaderBand) BuildHeaderBand();
            if (spec.InfoColumnWidth > 0f) BuildInfoGround();
            Relayout();
        }

        /// <summary>A standard window that is not a Mods pane (the only case the window geometry applies to).</summary>
        private bool IsStandardWindow => _windowSpec != null && _gameWindow && !_menuPane && !_dialog;

        /// <summary>This panel's overlay canvas order (a standard window's, a dialog's); a failed write keeps the default.</summary>
        internal void SetOverlaySorting(int order)
        {
            try
            {
                var canvas = Overlay == null ? null : Overlay.GetComponent<Canvas>();
                if (canvas == null) return;
                canvas.overrideSorting = true;
                canvas.sortingOrder = order;
            }
            catch { /* the panel keeps the overlay order */ }
        }

        /// <summary>The tallest the window may be drawn at its place (<see cref="WindowPlacement.MaxHeight"/>).</summary>
        private float WindowMaxHeight(float canvasHeight) => WindowPlace(canvasHeight).MaxHeight;

        /// <summary>The cap this window was last laid out with, so a re-flow that changes it lays the window out again.</summary>
        private float _windowCap = -1f;

        private WindowPlacement WindowPlace(float canvasHeight)
        {
            float width = AvailableWidth();
            float? strip = Spot.WorkspaceOpen ? UiWindowLayout.WorkspaceStripBottom(ModsWorkspaceFrame.Last, width, canvasHeight) : null;
            return UiWindowLayout.Place(Spot, width, canvasHeight, strip);
        }

        /// <summary>
        /// WINDOWS1: placed by the window row (<see cref="UiWindowLayout.Place"/>): a standard window, or (review) a
        /// floating panel that shares the screen with one. Dialogs, self-placed and top-pinned panels never are.
        /// </summary>
        private bool InWindowRow => Spot.IsWindow && !_menuPane && !_dialog && !SelfPlaced && !TopAligned;

        /// <summary>Where a row panel <paramref name="frameHeight"/> tall goes in the layout pass: hung from its row's top line.</summary>
        private PanelOffset WindowOffset(float frameHeight, float canvasHeight)
        {
            var place = WindowPlace(canvasHeight);
            _windowCap = place.MaxHeight;
            return place.CenterFor(Math.Min(frameHeight, place.MaxHeight));
        }

        /// <summary>The re-flow's placement: a changed cap lays the panel out again, then the height is read afresh.</summary>
        private PanelOffset RepositionInRow(RectTransform rt, float canvasHeight)
        {
            var place = WindowPlace(canvasHeight);
            var at = UiWindowLayout.RepositionInRow(place, _windowCap, () => rt.sizeDelta.y, Relayout, out float cap);
            _windowCap = cap;
            return at;
        }

        /// <summary>The frame width a standard window is drawn at on this canvas; 0 when this is not one.</summary>
        private float StandardFrameWidth => IsStandardWindow ? UiWindowLayout.FrameWidthFor(_windowSpec!, AvailableWidth()) : 0f;

        /// <summary>The info column's width (its gap included) at the frame actually drawn; 0 for none.</summary>
        private float SideBand => IsStandardWindow ? UiWindowLayout.InfoColumnFor(_windowSpec!, StandardFrameWidth) : 0f;

        /// <summary>A standard window's content row width (or the info column's while a side row is placed).</summary>
        private float StandardRowWidth =>
            _placingSide ? Math.Max(1f, SideBand - UiWindowLayout.InfoColumnGap)
            : _placingTop ? UiWindowLayout.ContentRowWidth(StandardFrameWidth)
            : UiWindowLayout.ContentRowWidth(StandardFrameWidth, SideBand);

        /// <summary>While a pinned top row (the toolbar, the tab row) is placed: it spans the frame, info column included.</summary>
        private bool _placingTop;

        private void BuildHeaderBand()
        {
            if (_windowBand != null) return;
            try
            {
                _windowBand = new GameObject(WindowBandName);
                _windowBand.transform.SetParent(GameObject.transform, false);
                _windowBand.transform.SetSiblingIndex(0);
                var ground = TryAdd<Image>(_windowBand);
                if (ground != null)
                {
                    ground.color = Rgba(UiWindowLayout.HeaderBandColor);
                    ground.raycastTarget = false;
                }

                _ornamentLeft = NewOrnament(WindowOrnamentLeftName, mirrored: false);
                _ornamentRight = NewOrnament(WindowOrnamentRightName, mirrored: true);

                _windowDivider = new GameObject(WindowDividerName);
                _windowDivider.transform.SetParent(GameObject.transform, false);
                var rule = TryAdd<Image>(_windowDivider);
                if (rule != null)
                {
                    PaintPart(rule, SettingsArtPart.DetailsOrnament, Color.white, Rgba(UiWindowLayout.DividerKitColor));
                    rule.raycastTarget = false;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: window header band: " + e.Message);
            }
        }

        private GameObject? NewOrnament(string name, bool mirrored)
        {
            try
            {
                var go = new GameObject(name);
                go.transform.SetParent(GameObject.transform, false);
                var image = TryAdd<Image>(go);
                if (image == null) return go;
                image.raycastTarget = false;
                // The kit has no stand-in for the ornament: without the game's sprite nothing is drawn.
                bool drawn = PaintPart(image, SettingsArtPart.WindowTitleOrnament, Color.white, new Color(0f, 0f, 0f, 0f));
                // As the game draws it: the 227x17 sprite stretched into its 171x17 rect (probe D1), not aspect-fitted.
                image.preserveAspect = false;
                if (mirrored) go.transform.localScale = new Vector3(-1f, 1f, 1f);
                go.SetActive(drawn);
                return go;
            }
            catch { return null; }
        }

        private void BuildInfoGround()
        {
            if (_infoGround != null) return;
            try
            {
                _infoGround = new GameObject(WindowInfoGroundName);
                _infoGround.transform.SetParent(GameObject.transform, false);
                _infoGround.transform.SetSiblingIndex(0);
                var ground = TryAdd<Image>(_infoGround);
                if (ground != null)
                {
                    ground.color = Rgba(UiWindowLayout.InfoColumnColor);
                    ground.raycastTarget = false;
                }

                // The info rows' parent: a rectangular clip (RectMask2D, as the viewport) over the column's area, so a
                // column longer than the frame is cut at the frame's bottom pad, never drawn over the stamp or past the frame.
                _infoClip = new GameObject(WindowInfoClipName);
                _infoClip.transform.SetParent(GameObject.transform, false);
                TryAdd<RectTransform>(_infoClip);
                TryAdd<RectMask2D>(_infoClip);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: window info column: " + e.Message);
            }
        }

        private GameObject? _infoClip;

        /// <summary>Where a row pinned to the info column is parented: the column's clip, or the frame without one.</summary>
        internal GameObject SideParent => _infoClip != null && UnityUiRuntime.Alive(_infoClip) ? _infoClip : GameObject;

        /// <summary>Start of a layout pass: the info column starts under the pinned top rows.</summary>
        private void BeginSidePass(float viewportTop)
        {
            _sideTop = viewportTop;
            _sideCursor = viewportTop;
            _sideRowBottoms.Clear();
        }

        /// <summary>Place one info-column row at the column's next free line; false when the row is not a side row.</summary>
        private bool PlaceSideRow(in PanelRow row)
        {
            if (row.Pin != MenuRowPin.Side) return false;
            float band = SideBand;
            if (row.Go == null) return true;
            // A frame too narrow for the column (a small screen): the whole clip is switched off rather than drawn over the
            // content, and on again on the first layout with room (PlaceWindowChrome). The rows' own state (a tab's) is not touched.
            if (band <= 0f || !RowActive(row)) return true;

            _placingSide = true;
            try
            {
                var columns = RowColumnsFor(row);
                float height;
                // A cell row (a fact: name and value) is its fixed band; every other row is its measured caption.
                bool cells = _cellRows.TryGetValue(row.Go, out var cellRow);
                if (cells) height = cellRow.Height;
                else
                {
                    try { height = FitLabel(row.Label, columns, CaptionLeft(row)); }
                    catch { height = RowHeight; }
                }

                height = Math.Max(InlineHostHeight(row, height), PanelLayout.MinRowHeight);
                float extraTop = string.Equals(row.Kind, "heading", StringComparison.Ordinal) ? SettingsAlign.HeadingExtraTop : 0f;
                float width = Math.Max(1f, band - UiWindowLayout.InfoColumnGap);
                bool clipped = _infoClip != null && row.Go.transform.parent != null
                    && row.Go.transform.parent.Pointer == _infoClip.transform.Pointer;
                // In the clip: from its top-center. On the frame (no clip): from the frame's top-center at the column's x.
                float x = clipped ? 0f : -StandardFrameWidth * 0.5f + PanelLayout.PadSide + width * 0.5f;
                float y = clipped ? _sideCursor - _sideTop : _sideCursor;
                PlaceTop(row.Go, x, y + extraTop, width, height);
                PlaceRowControl(row, columns);
                PlaceHeadingRule(row, height);
                PlaceInlineRow(row);
                PlacePageButtons(row, height);
                if (cells) PlaceCellRowCells(row);
                _sideRowBottoms.Add(_sideCursor - _sideTop + extraTop + height);
                _sideCursor += extraTop + height + PanelLayout.RowGap;
            }
            catch { /* the row keeps its last place */ }
            finally { _placingSide = false; }

            return true;
        }

        /// <summary>End of a layout pass: the info column's lowest edge, for the frame height.</summary>
        private void EndSidePass() => _sideBottom = _sideCursor > _sideTop ? _sideCursor - PanelLayout.RowGap : 0f;

        /// <summary>Place the header band, the ornaments, the divider and the info column's ground for this pass.</summary>
        private void PlaceWindowChrome(float panelHeight, float footerBand)
        {
            if (!IsStandardWindow) return;
            float frameWidth = StandardFrameWidth;
            try
            {
                if (_windowBand != null)
                    PlaceTop(_windowBand, 0f, 0f, Math.Max(1f, frameWidth - 8f), PanelLayout.HeaderHeight);
                if (_windowDivider != null)
                {
                    float w = Math.Min(UiWindowLayout.ContentRowWidth(frameWidth), frameWidth - PanelLayout.PadSide * 2f);
                    bool art = HasSprite(_windowDivider);
                    float h = art ? UiWindowLayout.DividerHeight : 1f;
                    PlaceTop(_windowDivider, 0f, UiWindowLayout.DividerY - h * 0.5f, w, h);
                }

                // The art may not have been ready when the window was built (the first open, a Core swap): paint again
                // until it is, as the game buttons do.
                RepaintIfMissing(_windowDivider, SettingsArtPart.DetailsOrnament, Rgba(UiWindowLayout.DividerKitColor), keepWhenMissing: true);
                RepaintIfMissing(_ornamentLeft, SettingsArtPart.WindowTitleOrnament, new Color(0f, 0f, 0f, 0f), keepWhenMissing: false);
                RepaintIfMissing(_ornamentRight, SettingsArtPart.WindowTitleOrnament, new Color(0f, 0f, 0f, 0f), keepWhenMissing: false);

                float titleWidth = TitleTextWidth();
                // The game's popup title: an ornament either side of the words, as wide as the row leaves (at most the
                // game's 171), on the title's center line. A title too long to leave them room drops them rather than
                // drawing them over the words.
                float ornament = UiWindowLayout.OrnamentWidth(titleWidth);
                float half = titleWidth * 0.5f + UiWindowLayout.OrnamentGap + ornament * 0.5f;
                float y = UiWindowLayout.TitleCenterY - UiWindowLayout.OrnamentHeight * 0.5f;
                PlaceOrnament(_ornamentLeft, -half, y, ornament);
                PlaceOrnament(_ornamentRight, half, y, ornament);

                if (_infoGround != null)
                {
                    float band = SideBand;
                    bool show = band > 0f && _sideBottom > 0f;
                    if (_infoGround.activeSelf != show) _infoGround.SetActive(show);
                    if (show)
                    {
                        float width = band - UiWindowLayout.InfoColumnGap * 0.5f;
                        float top = _sideTop - PanelLayout.RowGap;
                        float height = Math.Max(1f, panelHeight - top - PanelLayout.PadBottom - footerBand);
                        PlaceTop(_infoGround, -frameWidth * 0.5f + PanelLayout.PadSide * 0.5f + width * 0.5f, top, width, height);
                    }
                }

                if (_infoClip != null)
                {
                    float band = SideBand;
                    bool on = band > 0f;
                    if (_infoClip.activeSelf != on) _infoClip.SetActive(on);
                    if (on)
                    {
                        var clip = UiWindowLayout.InfoClip(frameWidth, band, _sideTop, panelHeight, footerBand);
                        // A row the clip would cut is masked whole: the column ends on a row's bottom edge, above the stamp.
                        float clipHeight = UiWindowLayout.SnapClip(_sideRowBottoms, clip.Height);
                        PlaceTop(_infoClip, clip.CenterX, clip.Top, clip.Width, clipHeight);
                    }
                }
            }
            catch { /* chrome that cannot be placed leaves the window usable */ }
        }

        private static void PlaceOrnament(GameObject? go, float x, float y, float width)
        {
            if (go == null) return;
            try
            {
                bool show = width > 0f && HasSprite(go);
                if (go.activeSelf != show) go.SetActive(show);
                if (show) PlaceTop(go, x, y, width, UiWindowLayout.OrnamentHeight);
            }
            catch { /* an ornament that cannot be placed is left out */ }
        }

        /// <summary>Paint <paramref name="go"/>'s image with <paramref name="part"/> when it has no sprite yet.</summary>
        private static void RepaintIfMissing(GameObject? go, SettingsArtPart part, Color kit, bool keepWhenMissing)
        {
            if (go == null || HasSprite(go)) return;
            try
            {
                var image = go.GetComponent<Image>();
                if (image == null) return;
                bool drawn = PaintPart(image, part, Color.white, kit);
                if (drawn) image.preserveAspect = false;
                if (!keepWhenMissing && !drawn && go.activeSelf) go.SetActive(false);
            }
            catch { /* the next pass tries again */ }
        }

        private static bool HasSprite(GameObject go)
        {
            try
            {
                var image = go.GetComponent<Image>();
                return image != null && image.sprite != null;
            }
            catch { return false; }
        }

        /// <summary>The title's drawn width (its text's preferred width, capped at the title rect).</summary>
        private float TitleTextWidth()
        {
            try
            {
                var tmp = Title == null ? null : Title.GetComponent<TextMeshProUGUI>();
                if (tmp == null) return UnityUiRuntimeMetrics.RowWidth * 0.5f;
                tmp.ForceMeshUpdate();
                float w = tmp.preferredWidth;
                if (float.IsNaN(w) || w <= 0f) return UnityUiRuntimeMetrics.RowWidth * 0.5f;
                return Math.Min(w, UnityUiRuntimeMetrics.RowWidth);
            }
            catch { return UnityUiRuntimeMetrics.RowWidth * 0.5f; }
        }

        /// <summary>
        /// The height a standard window's frame is sized for below its title band: the pinned top rows and the content,
        /// or the info column when it reaches lower.
        /// </summary>
        private float StandardBodyHeight(float contentHeight, float topChrome)
        {
            float body = (topChrome > 0f ? topChrome : 0f) + Math.Max(0f, contentHeight);
            if (_sideBottom > 0f) body = Math.Max(body, _sideBottom + PanelLayout.PadBottom - PanelLayout.HeaderHeight);
            return body;
        }

        private int OverlaySortingNow()
        {
            try
            {
                var canvas = Overlay == null ? null : Overlay.GetComponent<Canvas>();
                return canvas == null ? 0 : canvas.sortingOrder;
            }
            catch { return 0; }
        }

        /// <summary>WINDOWS1 readback: the frame and which art parts drew.</summary>
        internal System.Collections.Generic.Dictionary<string, object?>? DescribeStandardWindow()
        {
            if (!IsStandardWindow) return null;
            return new System.Collections.Generic.Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["preset"] = _windowSpec!.Preset.ToString(),
                ["frameWidth"] = StandardFrameWidth,
                ["infoColumn"] = SideBand,
                ["place"] = Spot.IsWindow ? Spot.Rank + "/" + Spot.Count : "none",
                ["cap"] = _windowCap,
                ["ornament"] = _ornamentLeft != null && HasSprite(_ornamentLeft),
                ["divider"] = _windowDivider != null && HasSprite(_windowDivider),
                ["infoBottom"] = _sideBottom,
                ["sorting"] = OverlaySortingNow(),
            };
        }
    }
}
#endif
