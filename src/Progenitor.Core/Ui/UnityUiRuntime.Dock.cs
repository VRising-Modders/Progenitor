#if !PROGENITOR_TESTS
using System;
using UnityEngine;

namespace Progenitor.Ui;

/// <summary>
/// DOCK1: a floating panel docked to a screen edge (<see cref="UiPlacement"/>). The arithmetic is
/// <see cref="UiDockLayout"/>; this half applies it to the frame, and lays the panel out again when the canvas changes
/// size, so a docked panel stays against its edge after a resolution change.
/// </summary>
internal sealed partial class UnityUiRuntime
{
    /// <summary>DOCK1 (<see cref="IUiRuntime.DockPanel"/>).</summary>
    public void DockPanel(IRuntimePanel panel, UiDockSpec? dock)
    {
        if (panel is UnityPanel host && host.Alive) host.SetDock(dock);
    }

    private sealed partial class UnityPanel
    {
        /// <summary>The dock asked for (null: not docked).</summary>
        private UiDockSpec? _dock;

        /// <summary>The canvas size the docked panel was last laid out on (the resize watch compares against it).</summary>
        private float _dockCanvasWidth = -1f, _dockCanvasHeight = -1f;

        /// <summary>The height cap the docked panel was last laid out with (-1: never).</summary>
        private float _dockCap = -1f;

        private bool _dockWatching;
        private int _dockWatchFrames;

        /// <summary>Placed by its dock: a docked floating panel (never a workspace pane, a dialog or a self-placed panel).</summary>
        private bool Docked => _dock.HasValue && !_menuPane && !_dialog && !SelfPlaced && _embeddedWidth <= 0;

        /// <summary>
        /// The width a wide game-window frame asks for: a docked game window's dock width when it asked one, else the width
        /// <see cref="UiLook.GameWindow(IUiPanel, float)"/> asked (<see cref="_dialogFrameWidth"/>).
        /// </summary>
        private float WideAsk =>
            Docked && _gameWindow && !IsStandardWindow && _dock!.Value.HasWidth
                ? UiDockLayout.AskedWidth(_dock.Value, AvailableWidth())
                : _dialogFrameWidth;

        /// <summary>A game window drawn wider than the kit frame (centered unless docked; height capped at 70%).</summary>
        private bool WideGameWindow => _gameWindow && WideAsk > PanelWidth;

        internal void SetDock(UiDockSpec? dock)
        {
            if (Released) return;
            bool changed = !Nullable.Equals(_dock, dock);
            _dock = dock;
            _dockCap = -1f;
            if (dock.HasValue && !_dockWatching)
            {
                _dockWatching = true;
                Plugin.Frame += DockResizeTick;
            }
            else if (!dock.HasValue) StopDockWatch();
            if (changed) Relayout();
        }

        private void StopDockWatch()
        {
            if (!_dockWatching) return;
            _dockWatching = false;
            Plugin.Frame -= DockResizeTick;
        }

        /// <summary>Where this docked panel goes on a canvas <paramref name="canvasHeight"/> tall.</summary>
        private DockPlacement DockPlace(float canvasHeight)
        {
            float width = AvailableWidth();
            float? strip = Spot.WorkspaceOpen ? UiWindowLayout.WorkspaceStripBottom(ModsWorkspaceFrame.Last, width, canvasHeight) : null;
            _dockCanvasWidth = width;
            _dockCanvasHeight = canvasHeight;
            return Spot.IsDock
                ? UiDockLayout.Place(Spot.Docks!, Spot.DockIndex, width, canvasHeight, FrameWidth, strip)
                : UiDockLayout.PlaceAlone(_dock!.Value, width, canvasHeight, FrameWidth, strip);
        }

        /// <summary>The tallest the docked frame may be (its area between the margins).</summary>
        private float DockMaxHeight(float canvasHeight) => DockPlace(canvasHeight).MaxHeight;

        /// <summary>The layout pass's placement of a docked frame <paramref name="frameHeight"/> tall.</summary>
        private PanelOffset DockOffset(float frameHeight, float canvasHeight)
        {
            var place = DockPlace(canvasHeight);
            _dockCap = place.MaxHeight;
            return place.CenterFor(frameHeight);
        }

        /// <summary>The re-flow's placement: a changed cap (the workspace opened or closed) lays the panel out again first.</summary>
        private PanelOffset RepositionDocked(RectTransform rt, float canvasHeight)
        {
            var place = DockPlace(canvasHeight);
            if (_dockCap >= 0f && Math.Abs(place.MaxHeight - _dockCap) > 0.5f)
            {
                Relayout();
                place = DockPlace(canvasHeight);
            }

            _dockCap = place.MaxHeight;
            return place.CenterFor(rt.sizeDelta.y);
        }

        /// <summary>
        /// Every 15 frames while a docked panel is on screen: when the canvas changed size, lay it out again (its width may
        /// be a share of the screen, and its edge moved) and re-flow the others, which keep clear of it.
        /// </summary>
        private void DockResizeTick()
        {
            if (Released || !Alive || !_dock.HasValue)
            {
                StopDockWatch();
                return;
            }

            if (++_dockWatchFrames < 15) return;
            _dockWatchFrames = 0;
            if (_canvasOff || !Visible || _dockCanvasWidth < 0f) return;
            try
            {
                float width = AvailableWidth(), height = AvailableHeight();
                if (Math.Abs(width - _dockCanvasWidth) < .5f && Math.Abs(height - _dockCanvasHeight) < .5f) return;
                Relayout();
                Ui.ReflowPanels();
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: docked panel resize: " + e.Message); }
        }
    }
}
#endif
