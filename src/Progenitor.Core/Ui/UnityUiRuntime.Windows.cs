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

        /// <summary>WINDOWS1: this game window is a standard window of <paramref name="spec"/>. Idempotent per spec.</summary>
        public void MakeStandardWindow(UiWindowSpec spec)
        {
            if (Released || spec == null) return;
            if (!_gameWindow) MakeGameWindow();
            _windowSpec = spec;
            if (spec.HeaderBand) BuildHeaderBand();
            if (spec.InfoColumnWidth > 0f) BuildInfoGround();
            Relayout();
        }

        /// <summary>A standard window that is not a Mods pane (the only case the window geometry applies to).</summary>
        private bool IsStandardWindow => _windowSpec != null && _gameWindow && !_menuPane && !_dialog;

        /// <summary>A centered standard window (Tabbed): placed like a dialog, never ranked with the tiled panels.</summary>
        private bool WindowCentred => IsStandardWindow && _windowSpec!.Centered;

        /// <summary>The frame width a standard window is drawn at on this canvas; 0 when this is not one.</summary>
        private float StandardFrameWidth => IsStandardWindow ? UiWindowLayout.FrameWidthFor(_windowSpec!, AvailableWidth()) : 0f;

        /// <summary>The info column's width (its gap included) at the frame actually drawn; 0 for none.</summary>
        private float SideBand => IsStandardWindow ? UiWindowLayout.InfoColumnFor(_windowSpec!, StandardFrameWidth) : 0f;

        /// <summary>A standard window's content row width (or the info column's while a side row is placed).</summary>
        private float StandardRowWidth =>
            _placingSide ? Math.Max(1f, SideBand - UiWindowLayout.InfoColumnGap)
            : UiWindowLayout.ContentRowWidth(StandardFrameWidth, SideBand);

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
                image.preserveAspect = true;
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
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: window info column: " + e.Message);
            }
        }

        /// <summary>Start of a layout pass: the info column starts under the pinned top rows.</summary>
        private void BeginSidePass(float viewportTop)
        {
            _sideTop = viewportTop;
            _sideCursor = viewportTop;
        }

        /// <summary>Place one info-column row at the column's next free line; false when the row is not a side row.</summary>
        private bool PlaceSideRow(in PanelRow row)
        {
            if (row.Pin != MenuRowPin.Side) return false;
            float band = SideBand;
            if (row.Go == null) return true;
            if (band <= 0f || !RowActive(row))
            {
                // A frame too narrow for the column (a small screen): its rows are not drawn rather than drawn over the content.
                try { if (band <= 0f && row.Go.activeSelf) row.Go.SetActive(false); } catch { /* */ }
                return true;
            }

            _placingSide = true;
            try
            {
                var columns = RowColumnsFor(row);
                float height;
                try { height = FitLabel(row.Label, columns, CaptionLeft(row)); }
                catch { height = RowHeight; }
                height = Math.Max(InlineHostHeight(row, height), PanelLayout.MinRowHeight);
                float extraTop = string.Equals(row.Kind, "heading", StringComparison.Ordinal) ? SettingsAlign.HeadingExtraTop : 0f;
                float width = Math.Max(1f, band - UiWindowLayout.InfoColumnGap);
                float frameWidth = StandardFrameWidth;
                float x = -frameWidth * 0.5f + PanelLayout.PadSide + width * 0.5f;
                PlaceTop(row.Go, x, _sideCursor + extraTop, width, height);
                PlaceRowControl(row, columns);
                PlaceHeadingRule(row, height);
                PlaceInlineRow(row);
                PlacePageButtons(row, height);
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

                float titleWidth = TitleTextWidth();
                float half = titleWidth * 0.5f + UiWindowLayout.OrnamentGap + UiWindowLayout.OrnamentMaxWidth * 0.5f;
                float y = UiWindowLayout.TitleCenterY - UiWindowLayout.OrnamentMaxHeight * 0.5f;
                if (_ornamentLeft != null)
                    PlaceTop(_ornamentLeft, -half, y, UiWindowLayout.OrnamentMaxWidth, UiWindowLayout.OrnamentMaxHeight);
                if (_ornamentRight != null)
                    PlaceTop(_ornamentRight, half, y, UiWindowLayout.OrnamentMaxWidth, UiWindowLayout.OrnamentMaxHeight);

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
            }
            catch { /* chrome that cannot be placed leaves the window usable */ }
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
                return Math.Min(w, UnityUiRuntimeMetrics.RowWidth - (UiWindowLayout.OrnamentMaxWidth + UiWindowLayout.OrnamentGap) * 2f);
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

        /// <summary>WINDOWS1 readback: the frame and which art parts drew.</summary>
        internal System.Collections.Generic.Dictionary<string, object?>? DescribeStandardWindow()
        {
            if (!IsStandardWindow) return null;
            return new System.Collections.Generic.Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["preset"] = _windowSpec!.Preset.ToString(),
                ["frameWidth"] = StandardFrameWidth,
                ["infoColumn"] = SideBand,
                ["centered"] = WindowCentred,
                ["ornament"] = _ornamentLeft != null && HasSprite(_ornamentLeft),
                ["divider"] = _windowDivider != null && HasSprite(_windowDivider),
                ["infoBottom"] = _sideBottom,
            };
        }
    }
}
#endif
