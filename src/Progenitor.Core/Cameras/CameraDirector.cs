using System;

namespace Progenitor.Cameras;

/// <summary>
/// Game-free state machine behind <c>ctx.Camera</c>: one per plugin context, with a single
/// process-wide owner because there is one local camera.
///
/// <para><b>Ownership.</b> The last director whose mode request <i>succeeded</i> owns the camera.
/// Ownership moves only after <c>Request</c> returns true, so a refused request (no
/// <c>LocalUserSystem</c>) leaves the previous owner, its shot and its reset duty exactly as they
/// were. A request that <i>throws</i> leaves the camera state unknown; the caller then takes
/// ownership with no shot but marked as having changed the mode, so its unload or
/// <see cref="Reset"/> still asks for top-down. Taking ownership clears the previous owner's shot
/// (it does not reset the camera: the new owner has just set it). Only the owner is ticked, and
/// only the owner resets the camera when its plugin unloads, so plugin A unloading does not yank
/// a shot plugin B is running. <see cref="Reset"/> from any director returns the camera to
/// top-down and leaves it unowned.</para>
///
/// <para><b>Shots.</b> A shot is re-written every frame (<see cref="Tick"/>), because the camera
/// systems rewrite their state every frame. A shot keeps the current mode only when this director
/// owns it, asked for a compatible mode in this world, and the game either reports that mode or
/// the request is still pending; otherwise it requests the mode again. The camera entity for a
/// requested mode exists after the next <c>LocalUserSystem</c> update, and until then the write
/// reports <see cref="CameraWriteResult.NotReady"/>. Every <see cref="RetryFrames"/> not-ready
/// frames the request is sent again, up to <see cref="MaxRetries"/> times; after that the shot
/// stops (logged once) and the director keeps ownership so unload still resets. An instant cut is
/// applied on the first write that lands.</para>
///
/// <para><b>Safety.</b> Every unload reason reaches <see cref="Release"/> through the context's
/// tracked disposable, and Core uninstall reaches <see cref="ReleaseAll"/>, so a plugin that set
/// a non-top-down mode always puts the top-down camera back. A client world change drops the shot
/// without a request: the rebuilt world starts on its own default camera, and any entity the shot
/// named belongs to the old world. A failed write stops the shot and resets to top-down.</para>
///
/// <para><b>Focus.</b> Every shot also has a focus point the game's lighting is anchored on
/// (<see cref="Focus"/>, called just before <c>HybridCameraSystem</c> each frame): a free shot's
/// named focus or the floor point its view ray meets, an orbit shot's orbited entity or point.
/// A focus that cannot be written leaves that frame's lighting to the game and never stops the
/// shot; the first such failure after the shot's camera is up is logged once. See
/// docs/modules.md, "Lighting under free shots".</para>
/// </summary>
internal sealed class CameraDirector
{
    /// <summary>Not-ready frames between re-requests of the wanted mode.</summary>
    internal const int RetryFrames = 60;

    /// <summary>Re-requests before the shot gives up.</summary>
    internal const int MaxRetries = 3;

    private static CameraDirector? s_owner;

    private readonly ICameraRuntime _rt;
    private readonly string _name;
    private ShotKind _kind;
    private FreeShotPlan _free;
    private OrbitShotPlan _orbit;
    private bool _instantPending;
    private bool _changedMode;
    private bool _released;
    private bool _requestPending;
    private CameraMode _requested;
    private long _world;
    private int _notReadyFrames;
    private int _retries;
    private bool _landed;
    private bool _focusWarned;

    internal CameraDirector(ICameraRuntime runtime, string name)
    {
        _rt = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _name = string.IsNullOrEmpty(name) ? "plugin" : name;
    }

    private enum ShotKind
    {
        None,
        Free,
        Orbit,
    }

    /// <summary>The director that currently owns the local camera, if any.</summary>
    internal static CameraDirector? Owner => s_owner;

    /// <summary>True while this director owns the camera.</summary>
    internal bool Owns => ReferenceEquals(s_owner, this);

    /// <summary>True while a free or orbit shot is being written every frame.</summary>
    internal bool IsShotActive => _kind != ShotKind.None;

    /// <summary>The mode this director last asked for successfully.</summary>
    internal CameraMode RequestedMode => _requested;

    /// <summary>The game's current mode (lags a switch until the next <c>LocalUserSystem</c> update).</summary>
    internal CameraMode Mode => _rt.ReadMode();

    /// <summary>Switch mode and stop any scripted shot. The game's own input drives the new camera.</summary>
    internal void SetMode(CameraMode mode)
    {
        EnsureUsable();
        if (mode is not (CameraMode.TopDown or CameraMode.Orbit or CameraMode.Free or CameraMode.Static))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "camera mode must be TopDown, Orbit, Free or Static.");
        long world = RequireWorld();

        RequestOrThrow(mode, world);
        ClearShot();
        if (mode == CameraMode.TopDown)
        {
            // Top-down is the rest state: nothing left to undo, so nobody owns it.
            _changedMode = false;
            s_owner = null;
        }
    }

    /// <summary>Start or retarget a free shot.</summary>
    internal void Free(in FreeShotPlan plan, bool instant)
    {
        EnsureUsable();
        long world = RequireWorld();
        bool compatible = _kind == ShotKind.Free || _requested is CameraMode.Free or CameraMode.Static;
        bool live = Mode is CameraMode.Free or CameraMode.Static;
        if (!KeepMode(world, compatible, live)) RequestOrThrow(CameraMode.Free, world);

        _kind = ShotKind.Free;
        _free = plan;
        _orbit = default;
        _instantPending |= instant;
        _rt.Arm();
    }

    /// <summary>Start or retarget an orbit shot.</summary>
    internal void Orbit(in OrbitShotPlan plan, bool instant)
    {
        EnsureUsable();
        long world = RequireWorld();
        bool compatible = _kind == ShotKind.Orbit || _requested == CameraMode.Orbit;
        bool live = Mode == CameraMode.Orbit;
        if (!KeepMode(world, compatible, live)) RequestOrThrow(CameraMode.Orbit, world);

        _kind = ShotKind.Orbit;
        _orbit = plan;
        _free = default;
        _instantPending |= instant;
        _rt.Arm();
    }

    /// <summary>
    /// Stop any shot and return the camera to top-down, whoever owned it. With no client world
    /// there is no camera to strand, so it only clears state. Throws after <see cref="Release"/>.
    /// </summary>
    internal void Reset()
    {
        EnsureUsable();
        if (s_owner is { } owner && !ReferenceEquals(owner, this)) owner.Supersede();
        s_owner = null;
        ClearShot();
        _changedMode = false;
        _requested = CameraMode.TopDown;
        if (_rt.WorldToken != 0) TryRequest(CameraMode.TopDown);
    }

    /// <summary>
    /// Plugin unload (every reason). The owner puts top-down back if it changed the mode; a director
    /// that no longer owns the camera leaves it alone. Idempotent.
    /// </summary>
    internal void Release()
    {
        if (Owns)
        {
            if (_changedMode || _kind != ShotKind.None) TryRequest(CameraMode.TopDown);
            s_owner = null;
        }

        ClearShot();
        _changedMode = false;
        _released = true;
    }

    /// <summary>One frame for the owner. Returns false when nothing is left to write, so the caller can unsubscribe.</summary>
    internal bool Tick()
    {
        if (!Owns || _kind == ShotKind.None) return false;

        long world = _rt.WorldToken;
        if (world != _world)
        {
            _rt.Log("Camera: the client world changed; " + _name + "'s scripted shot was dropped.");
            ClearShot();
            _changedMode = false;
            s_owner = null;
            return false;
        }

        var result = _kind == ShotKind.Free
            ? _rt.WriteFree(_free, _instantPending)
            : _rt.WriteOrbit(_orbit, _instantPending);

        switch (result)
        {
            case CameraWriteResult.Written:
                _landed = true;
                _instantPending = false;
                _requestPending = false;
                _notReadyFrames = 0;
                _retries = 0;
                return true;

            case CameraWriteResult.NotReady:
                _notReadyFrames++;
                if (_notReadyFrames % RetryFrames != 0) return true;
                if (_retries < MaxRetries)
                {
                    _retries++;
                    _rt.Log("Camera: " + _requested + " camera not up after " + _notReadyFrames + " frames; requesting it again (" + _retries + "/" + MaxRetries + ").");
                    TryRequest(_requested);
                    return true;
                }

                // Give up on the shot but keep ownership and _changedMode: whatever mode the game is
                // in now was asked for by this director, so its unload or Reset still restores top-down.
                _rt.Log("Camera: the " + _requested + " camera never came up; " + _name + "'s shot stopped.");
                ClearShot();
                return false;

            default:
                _rt.Log("Camera: " + _name + "'s shot write failed; resetting to the top-down camera.");
                ClearShot();
                _changedMode = false;
                s_owner = null;
                TryRequest(CameraMode.TopDown);
                return false;
        }
    }

    /// <summary>
    /// Point the lighting target at this shot's focus. Called every frame just before
    /// <c>HybridCameraSystem</c> updates, after the camera systems have moved the camera. Does
    /// nothing unless this director owns the camera, has a shot, and is still in the world it
    /// asked in. Returns whether a focus was written.
    /// </summary>
    internal bool Focus()
    {
        if (!Owns || _kind == ShotKind.None || _rt.WorldToken != _world) return false;

        bool ok = _kind == ShotKind.Free ? _rt.FocusFree(_free) : _rt.FocusOrbit(_orbit);
        if (!ok && _landed && !_focusWarned)
        {
            _focusWarned = true;
            _rt.Log("Camera: could not move the lighting target to " + _name + "'s shot focus; the game's own target lights this shot.");
        }

        return ok;
    }

    /// <summary>Focus the current owner's shot. False when there is none or nothing was written.</summary>
    internal static bool FocusOwner()
    {
        var owner = s_owner;
        return owner != null && owner.Focus();
    }

    /// <summary>Tick the current owner. False when there is none or it has nothing left to write.</summary>
    internal static bool TickOwner()
    {
        var owner = s_owner;
        return owner != null && owner.Tick();
    }

    /// <summary>Core uninstall: the owner releases (and so resets) the camera.</summary>
    internal static void ReleaseAll()
    {
        var owner = s_owner;
        if (owner == null) return;
        try { owner.Release(); }
        finally { s_owner = null; }
    }

    /// <summary>Tests only: forget the process-wide owner.</summary>
    internal static void ResetForTests() => s_owner = null;

    private bool KeepMode(long world, bool compatible, bool live) =>
        Owns && _world == world && compatible && (live || _requestPending);

    private void Supersede()
    {
        ClearShot();
        _changedMode = false;
        _requestPending = false;
    }

    private void TakeOwnership()
    {
        if (s_owner is { } owner && !ReferenceEquals(owner, this)) owner.Supersede();
        s_owner = this;
    }

    private void ClearShot()
    {
        _kind = ShotKind.None;
        _free = default;
        _orbit = default;
        _instantPending = false;
        _notReadyFrames = 0;
        _retries = 0;
        _landed = false;
        _focusWarned = false;
    }

    /// <summary>
    /// Request <paramref name="mode"/> and take ownership only once the request succeeded. A false
    /// return changes nothing. A throw takes ownership with no shot and <c>_changedMode</c> set, so
    /// the unknown camera state is still reset on unload.
    /// </summary>
    private void RequestOrThrow(CameraMode mode, long world)
    {
        bool ok;
        try
        {
            ok = _rt.Request(mode);
        }
        catch (Exception e)
        {
            TakeOwnership();
            ClearShot();
            _changedMode = true;
            _requestPending = false;
            _world = world;
            throw new InvalidOperationException("the camera request for " + mode + " threw: " + e.Message, e);
        }

        if (!ok)
            throw new InvalidOperationException("the client camera is not available (LocalUserSystem is missing).");

        TakeOwnership();
        _requested = mode;
        _world = world;
        _requestPending = true;
        _changedMode = mode != CameraMode.TopDown;
        _notReadyFrames = 0;
        _retries = 0;
        _landed = false;
        _focusWarned = false;
    }

    private void TryRequest(CameraMode mode)
    {
        try
        {
            if (_rt.Request(mode))
                _requestPending = mode != CameraMode.TopDown;
            else
                _rt.Log("Camera: could not request " + mode + " (no LocalUserSystem).");
        }
        catch (Exception e)
        {
            _rt.Log("Camera: request " + mode + " threw: " + e.Message);
        }
    }

    private long RequireWorld()
    {
        long world = _rt.WorldToken;
        if (world == 0)
            throw new InvalidOperationException("the client world does not exist yet; the camera is available once in game.");
        return world;
    }

    private void EnsureUsable()
    {
        if (_released)
            throw new InvalidOperationException("this plugin's camera was released at unload.");
    }
}
