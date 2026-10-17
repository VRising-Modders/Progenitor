#if !PROGENITOR_TESTS
using System;
using System.Diagnostics;
using Progenitor.Dev;
using Progenitor.Ecs;
using Progenitor.Ui;
using Progenitor.Units;
using ProjectM;
using ProjectM.Network;
using Progenitor.WorldState;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Cameras;

public sealed partial class PluginCamera
{
    private CameraPreview? _preview;

    /// <summary>Brief fixed-anchor orbit. Dispose to return; Rotate pauses automatic rotation.
    /// This controls the camera only; the caller must block gameplay input and ensure the scene is loaded.
    /// <paramref name="seconds"/> is more than 0 and at most 10; call <see cref="CameraPreview.Hold"/> to keep it open.
    /// <paramref name="ended"/> runs once when the preview ends for any reason but unload
    /// (<see cref="CameraPreview.EndReason"/> says which). See <c>docs/modules.md</c>, "Camera preview".</summary>
    public CameraPreview Preview(UnitVec3 point, float yawDegrees, float seconds = 5, Action? ended = null, int compressedHeight = -1)
    {
        if (!float.IsFinite(seconds) || seconds <= 0 || seconds > CameraPreviewPolicy.MaxTimedSeconds) throw new ArgumentOutOfRangeException(nameof(seconds));
        return Preview(point, yawDegrees, new CameraPreviewOptions
        {
            Seconds = seconds,
            Ended = ended == null ? null : _ => ended(),
            CompressedHeight = compressedHeight,
        });
    }

    /// <summary>
    /// <see cref="Preview(UnitVec3, float, float, Action?, int)"/> with options: <see cref="CameraPreviewOptions.Hold"/>
    /// starts it held (no timeout, at most <see cref="CameraPreview.MaxHoldSeconds"/>), and
    /// <see cref="CameraPreviewOptions.Ended"/> receives the reason. A previous preview of this plugin ends
    /// (<see cref="CameraPreviewEndReason.Replaced"/>).
    /// </summary>
    public CameraPreview Preview(UnitVec3 point, float yawDegrees, CameraPreviewOptions options)
    {
        _ctx.EnsureAttached();
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (!float.IsFinite(yawDegrees)) throw new ArgumentOutOfRangeException(nameof(yawDegrees));
        if (!options.Hold && (!float.IsFinite(options.Seconds) || options.Seconds <= 0 || options.Seconds > CameraPreviewPolicy.MaxTimedSeconds))
            throw new ArgumentOutOfRangeException(nameof(options), "Seconds must be more than 0 and at most 10; use Hold for longer.");
        if (!CameraFraming.IsValidOffset(options.ScreenOffset))
            throw new ArgumentOutOfRangeException(nameof(options), "ScreenOffset must be finite and at most 0.4 either way.");
        _ = Director;
        _preview?.End(CameraPreviewEndReason.Replaced, notify: true);
        var preview = new CameraPreview(this, point, yawDegrees, options);
        _preview = preview;
        return preview;
    }

    /// <summary>
    /// A running <see cref="Preview(UnitVec3, float, CameraPreviewOptions)"/>. Every preview, timed or held, ends
    /// on Dispose, on Escape when it is the topmost thing on screen, when the player's character loses health,
    /// dies or moves more than <see cref="MoveLimitMeters"/>, when the client world ends or changes, when
    /// something else takes the camera, and on unload. <see cref="Hold"/> only replaces the short timeout.
    /// </summary>
    public sealed class CameraPreview : IDisposable
    {
        /// <summary>Longest a held preview runs after its last <see cref="Hold"/>, in seconds (ten minutes, the server lease's cap too).</summary>
        public const float MaxHoldSeconds = CameraPreviewPolicy.MaxHoldSeconds;

        /// <summary>How far the player's real character may move, in meters, before the preview ends.</summary>
        public const float MoveLimitMeters = CameraPreviewPolicy.MoveLimitMeters;

        private readonly PluginCamera _camera;
        private readonly UnitVec3 _point;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly CameraPreviewPolicy _policy;
        private Action<CameraPreviewEndReason>? _ended;
        private float _yaw;
        private readonly ClientObservation _observation;
        private readonly World? _world;
        private readonly int _epoch;
        private readonly Entity _character;
        private readonly float3 _standing;
        private double _previous;
        private bool _manual;
        private bool _ownsHud;
        private IDisposable? _escape;
        private bool _forwarding;
        private bool _closeKeyHeld;
        private float _screenOffset;
        private float _fov, _aspect;

        /// <summary>The orbit distance and pitch every preview uses.</summary>
        private const float Distance = 6, Pitch = 35;

        /// <summary>True until the preview ends.</summary>
        public bool Active { get; private set; } = true;

        /// <summary>True while the preview is held (no short timeout).</summary>
        public bool IsHeld => Active && _policy.IsHeld;

        /// <summary>Why it ended; <see cref="CameraPreviewEndReason.None"/> while <see cref="Active"/>.</summary>
        public CameraPreviewEndReason EndReason { get; private set; }

        internal CameraPreview(PluginCamera camera, UnitVec3 point, float yaw, CameraPreviewOptions options)
        {
            _camera = camera; _point = point; _yaw = yaw; _ended = options.Ended; _screenOffset = options.ScreenOffset;
            _policy = new CameraPreviewPolicy(options.Hold ? CameraPreviewPolicy.MaxTimedSeconds : options.Seconds, options.Hold, 0);
            _world = Worlds.Client ?? throw new InvalidOperationException("No client world.");
            _epoch = WorldEpoch.Current;
            if (!TryLocalCharacter(_world, out _character, out _standing)) throw new InvalidOperationException("No local character.");
            _observation = new ClientObservation(camera._ctx, point, options.CompressedHeight);
            try
            {
                camera.Orbit(Framed(), Distance, yaw, Pitch, instant: true);
                _ownsHud = !PhotoHud.IsHidden; if (_ownsHud) PhotoHud.Hide(true);
                // Escape reaches the framework only while Bootstrap forwards it (a shown panel or this hold).
                Ui.Ui.CurrentRuntime().SetCapture(true); _forwarding = true;
                _escape = Ui.Ui.PushEscape(OnEscape);
                HoldCloseKey(CameraPreviewEscape.HoldsCloseKey(EscapeOwnerNow()));
            }
            catch
            {
                Active = false;
                ReleaseInput();
                try { _observation.Dispose(); }
                finally
                {
                    if (_ownsHud) PhotoHud.Restore();
                    try { if (camera.Director.Owns) camera.Director.Reset(); } catch { /* the original failure is the one to report */ }
                }
                throw;
            }
            MainThread.PostDelayed(0.033, Tick, camera._ctx.Owner);
        }

        /// <summary>Rotate by degrees; subsequent automatic rotation stops.</summary>
        public void Rotate(float degrees)
        {
            if (!float.IsFinite(degrees)) throw new ArgumentOutOfRangeException(nameof(degrees));
            if (!Active || !_camera.IsShotActive) return;
            _manual = true; _yaw += degrees;
            _camera.Orbit(Framed(), Distance, _yaw, Pitch);
        }

        /// <summary>Where the previewed point sits on screen, as a share of the screen width right of center (<see cref="CameraPreviewOptions.ScreenOffset"/>).</summary>
        public float ScreenOffset => _screenOffset;

        /// <summary>
        /// Move the previewed point to <paramref name="offset"/> of the screen's width right of center (0 is the center, at
        /// most 0.4 either way), for instance when a docked panel opens or closes beside it. The camera keeps orbiting the
        /// point. Ignored once the preview ended.
        /// </summary>
        public void SetScreenOffset(float offset)
        {
            if (!CameraFraming.IsValidOffset(offset)) throw new ArgumentOutOfRangeException(nameof(offset), "ScreenOffset must be finite and at most 0.4 either way.");
            if (!Active) return;
            _screenOffset = offset;
            if (_camera.IsShotActive) _camera.Orbit(Framed(), Distance, _yaw, Pitch);
        }

        /// <summary>
        /// The orbit anchor this frame: the point itself, or with a screen offset the point moved sideways against the
        /// camera's current yaw so it projects at the offset (<see cref="CameraFraming"/>). The game camera's field of view
        /// and aspect are read each time; an unreadable camera keeps the last good reading, and with none yet the point is
        /// framed at the center.
        /// </summary>
        private UnitVec3 Framed()
        {
            if (_screenOffset == 0f) return _point;
            try
            {
                var cam = UnityEngine.Camera.main;
                if (cam != null)
                {
                    float fov = cam.fieldOfView, aspect = cam.aspect;
                    if (float.IsFinite(fov) && fov > 0f && float.IsFinite(aspect) && aspect > 0f) { _fov = fov; _aspect = aspect; }
                }
            }
            catch { /* keep the last reading */ }
            return CameraFraming.Anchor(_point, _yaw, CameraFraming.LateralShift(_screenOffset, Distance, _fov, _aspect));
        }

        /// <summary>
        /// Keep the preview open with no timeout, keeping its current orbit and rotation (a "Keep open" button).
        /// It still ends on every other rule, and after <see cref="MaxHoldSeconds"/>; calling Hold again restarts
        /// that limit. Returns false when the preview already ended.
        /// </summary>
        public bool Hold()
        {
            if (!Active) return false;
            _policy.Hold(_clock.Elapsed.TotalSeconds);
            return true;
        }

        /// <summary>
        /// One frame of the preview. The whole body is guarded: a tick that throws ends the preview (Failed) instead of
        /// stopping the reschedule and leaving a held preview open with no safety checks.
        /// </summary>
        private void Tick()
        {
            if (!Active) return;
            try
            {
                if (_ownsHud) PhotoHud.Reassert();
                var elapsed = _clock.Elapsed.TotalSeconds;
                var reason = _policy.Decide(elapsed, Sample());
                if (reason != CameraPreviewEndReason.None) { End(reason, notify: true); return; }
                HoldCloseKey(CameraPreviewEscape.HoldsCloseKey(EscapeOwnerNow()));
                if (Progenitor.Inputs.UnityInputRuntime.TrySamplePointer(out var right, out var dx, out _, out _) && right && dx != 0)
                { _manual = true; _yaw += dx * 0.15f; }
                if (!_manual) _yaw += (float)(elapsed - _previous) * 8;
                _previous = elapsed;
                _camera.Orbit(Framed(), Distance, _yaw, Pitch);
                MainThread.PostDelayed(0.033, Tick, _camera._ctx.Owner);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Camera: preview tick threw: " + e.Message);
                End(CameraPreviewEndReason.Failed, notify: true);
            }
        }

        private CameraPreviewSample Sample()
        {
            var s = new CameraPreviewSample { CameraOwned = _camera.IsShotActive };
            var world = Worlds.Client;
            s.SameWorld = world != null && ReferenceEquals(world, _world) && world.IsCreated && WorldEpoch.Current == _epoch;
            if (!s.SameWorld) return s;
            var em = world!.EntityManager;
            if (!em.Exists(_character) || !_character.TryRead<Translation>(em, out var at) || !_character.TryRead<Health>(em, out var health))
                return s;
            s.CharacterRead = true;
            s.Dead = health.IsDead || _character.Has<Dead>(em);
            s.Health = health.Value;
            s.MaxHealth = health.MaxHealth.Value;
            s.MovedMeters = math.distance(at.Value.xz, _standing.xz);
            return s;
        }

        private static bool TryLocalCharacter(World world, out Entity character, out float3 position)
        {
            character = Entity.Null; position = default;
            var em = world.EntityManager;
            var locals = Query.Entities(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly));
            if (locals.Count == 0 || !em.Exists(locals[0]) || !locals[0].TryRead<Translation>(em, out var at)) return false;
            character = locals[0]; position = at.Value;
            return true;
        }

        private bool OnEscape()
        {
            if (!Active) return false;
            if (EscapeOwnerNow() != CameraPreviewEscapeOwner.Preview) return false;
            End(CameraPreviewEndReason.Escape, notify: true);
            return true;
        }

        private CameraPreviewEscapeOwner EscapeOwnerNow()
        {
            bool dialog = false, capture = false, chat = false;
            try { dialog = UiDialog.Current != null || UiChoiceHandle.AnyOpen; } catch { /* */ }
            try { capture = Keybinds.EscapeBelongsToCapture; } catch { /* */ }
            try { chat = Keybinds.ChatFocusNow == ChatFocus.Focused; } catch { /* */ }
            bool menu = UiWorldPickEscape.GameMenuOpen(WorldPickBackend.MenuTypeNow());
            return CameraPreviewEscape.Decide(Active, dialog, capture, chat, menu);
        }

        private void HoldCloseKey(bool want)
        {
            if (want == _closeKeyHeld) return;
            _closeKeyHeld = want;
            try { if (want) UiInputCapture.AcquireCloseKey(); else UiInputCapture.ReleaseCloseKey(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Camera: preview close-key hold threw: " + e.Message); }
        }

        private void ReleaseInput(bool afterEscape = false)
        {
            if (afterEscape && _closeKeyHeld)
            {
                // The press that ended the preview must not reach the game's Back on a later frame and open the
                // system menu: keep the close key a moment longer. Core-owned, so a plugin unload cannot drop it.
                _closeKeyHeld = false;
                MainThread.PostDelayed(0.25, () =>
                {
                    try { UiInputCapture.ReleaseCloseKey(); } catch { /* Core unload already dropped every hold */ }
                });
            }
            HoldCloseKey(false);
            var escape = _escape; _escape = null;
            try { escape?.Dispose(); } catch { /* */ }
            if (_forwarding)
            {
                _forwarding = false;
                try { Ui.Ui.CurrentRuntime().SetCapture(false); } catch { /* */ }
            }
        }

        /// <summary>End the preview and return the player camera (<see cref="CameraPreviewEndReason.Disposed"/>).</summary>
        public void Dispose() => End(CameraPreviewEndReason.Disposed, notify: true);

        internal void End(CameraPreviewEndReason reason, bool notify)
        {
            if (!Active) return;
            Active = false;
            EndReason = reason;
            if (ReferenceEquals(_camera._preview, this)) _camera._preview = null;
            try { ReleaseInput(afterEscape: reason == CameraPreviewEndReason.Escape); }
            finally
            {
                try { _observation.Dispose(); }
                finally
                {
                    if (_ownsHud) PhotoHud.Restore();
                    if (_camera.Director.Owns) _camera.Director.Reset();
                }
            }
            Plugin.Logger?.LogInfo("Camera: preview ended (" + reason + (_policy.IsHeld ? ", held" : "") + ").");
            var ended = _ended; _ended = null;
            if (notify && ended != null)
            {
                try { ended(reason); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Camera: preview ended callback threw: " + e.Message); }
            }
        }
    }
}
#endif
