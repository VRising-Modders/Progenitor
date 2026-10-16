#if !PROGENITOR_TESTS
using System;
using System.Diagnostics;
using Progenitor.Dev;
using Progenitor.Units;

namespace Progenitor.Cameras;

public sealed partial class PluginCamera
{
    private CameraPreview? _preview;

    /// <summary>Brief fixed-anchor orbit. Dispose to return; Rotate pauses automatic rotation.
    /// This controls the camera only; the caller must block gameplay input and ensure the scene is loaded.</summary>
    public CameraPreview Preview(UnitVec3 point, float yawDegrees, float seconds = 5, Action? ended = null, int compressedHeight = -1)
    {
        _ctx.EnsureAttached();
        if (!float.IsFinite(seconds) || seconds <= 0 || seconds > 10) throw new ArgumentOutOfRangeException(nameof(seconds));
        _preview?.Dispose();
        var preview = new CameraPreview(this, point, yawDegrees, seconds, ended, compressedHeight);
        _preview = preview;
        return preview;
    }

    public sealed class CameraPreview : IDisposable
    {
        private readonly PluginCamera _camera;
        private readonly UnitVec3 _point;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly float _seconds;
        private Action? _ended;
        private float _yaw;
        private readonly ClientObservation _observation;
        private double _previous;
        private bool _manual;
        private bool _ownsHud;
        public bool Active { get; private set; } = true;

        internal CameraPreview(PluginCamera camera, UnitVec3 point, float yaw, float seconds, Action? ended, int compressedHeight)
        {
            _camera = camera; _point = point; _yaw = yaw; _seconds = seconds; _ended = ended;
            _observation = new ClientObservation(camera._ctx, point, compressedHeight);
            try { camera.Orbit(point, 6, yaw, 35, instant: true); _ownsHud = !PhotoHud.IsHidden; if (_ownsHud) PhotoHud.Hide(true); }
            catch { try { _observation.Dispose(); } finally { if (_ownsHud) PhotoHud.Restore(); } throw; }
            MainThread.PostDelayed(0.033, Tick, camera._ctx.Owner);
        }

        /// <summary>Rotate by degrees; subsequent automatic rotation stops.</summary>
        public void Rotate(float degrees)
        {
            if (!float.IsFinite(degrees)) throw new ArgumentOutOfRangeException(nameof(degrees));
            if (!Active || !_camera.IsShotActive) return;
            _manual = true; _yaw += degrees;
            _camera.Orbit(_point, 6, _yaw, 35);
        }

        private void Tick()
        {
            if (!Active) return;
            if (_ownsHud) PhotoHud.Reassert();
            var elapsed = _clock.Elapsed.TotalSeconds;
            if (!_camera.IsShotActive) { End(false); return; }
            if (elapsed >= _seconds) { Dispose(); return; }
            if (Progenitor.Inputs.UnityInputRuntime.TrySamplePointer(out var right, out var dx, out _, out _) && right && dx != 0)
            { _manual = true; _yaw += dx * 0.15f; }
            if (!_manual) _yaw += (float)(elapsed - _previous) * 8;
            _previous = elapsed;
            try { _camera.Orbit(_point, 6, _yaw, 35); }
            catch { Dispose(); return; }
            MainThread.PostDelayed(0.033, Tick, _camera._ctx.Owner);
        }

        public void Dispose() => End(true);

        internal void End(bool notify)
        {
            if (!Active) return;
            Active = false;
            try { _observation.Dispose(); }
            finally
            {
                if (_ownsHud) PhotoHud.Restore();
                if (_camera.Director.Owns) _camera.Director.Reset();
            }
            var ended = _ended; _ended = null;
            if (notify) ended?.Invoke();
        }
    }
}
#endif
