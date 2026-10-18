#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Progenitor.Clock;
using Progenitor.Inputs;
using Progenitor.Networking;
using Progenitor.Ui;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Cameras;

/// <summary>
/// Photo mode: a built-in client feature, <b>off by default</b> (<c>[PhotoMode] Enabled</c> in
/// <c>progenitor.ui.cfg</c>, shown in the Mods settings panel). While enabled,
/// <b>Ctrl+Shift+F9</b> enters and leaves it:
/// <list type="bullet">
/// <item>a fly camera (<see cref="PhotoFlight"/>): WASD along the view, E up and Q down, right
/// mouse to look, R held fast, F held slow, the wheel for speed and Z / X to zoom the field of
/// view. It uses unscaled time, so it keeps flying while the world is paused;</item>
/// <item><b>H</b> hides and shows the game HUD (<see cref="PhotoHud"/>), nameplates included unless
/// <c>HideNameplates</c> is off; <c>HideHudOnEnter</c> hides it on entry;</item>
/// <item><b>P</b> pauses and resumes the whole world. The server checks the sender is an admin
/// (<see cref="PhotoPauseLedger"/>); a non-admin gets a notice and nothing changes.</item>
/// </list>
/// The character does not move while flying: photo mode holds an input lease on every channel.
/// Leaving (Ctrl+Shift+F9 again, the input release key, a disconnect or a Core unload) resumes the
/// world, restores the HUD and the field of view, and puts the top-down camera back. All three
/// keys are rebindable in the Mods settings panel. See <c>docs/modules.md</c>, "Photo mode".
///
/// <para><b>Input.</b> Every key goes through the keybind API: Ctrl+Shift+F9 is an ordinary gameplay
/// bind, chosen free of every sample's default (Ctrl+F9 is the Sample's log panel;
/// <c>FrameworkDefaultKeyTests</c> keeps framework defaults off mod defaults); the flight, HUD and pause keys are <see cref="KeybindScope.Photo"/> binds, which fire only
/// while photo mode is active (it holds the character's input then, so they never clash with a game
/// key) and are listed and rebindable like any other. Held keys set flags through their press and
/// release handlers. The pointer (right-button look, mouse movement, the wheel) comes from the
/// framework input layer's <see cref="UnityInputRuntime.TrySamplePointer"/>; this file reads no device.</para>
/// </summary>
internal static unsafe class PhotoMode
{
    internal const string Section = "PhotoMode";
    internal const string ToggleKeybindId = "PhotoMode";
    internal const string HudKeybindId = "PhotoModeHideHud";
    internal const string PauseKeybindId = "PhotoModePause";

    internal static readonly KeyChord DefaultToggleKey = new(UiKey.F9, KeyModifiers.Ctrl | KeyModifiers.Shift);
    internal static readonly KeyChord DefaultHudKey = new(UiKey.H);
    internal static readonly KeyChord DefaultPauseKey = new(UiKey.P);
    internal const string ExitKeybindId = "PhotoModeExit";
    internal const string LockKeybindId = "PhotoModeLockOn";
    /// <summary>Hold to target, release over a unit to lock, tap to unlock. The middle mouse button (a supported mouse key; free in the Photo scope).</summary>
    internal static readonly KeyChord DefaultLockKey = new(UiKey.MouseMiddle);
    /// <summary>Escape leaves photo mode (Photo scope; the game's Back / Menu_Close are taken by the exclusive mask, so the system menu does not open).</summary>
    internal static readonly KeyChord DefaultExitKey = new(UiKey.Escape);

    /// <summary>Photo-scope flight binds: id, default key, description.</summary>
    internal static readonly (FlightKey Key, string Id, UiKey Default, string Description)[] PhotoKeys =
    {
        (FlightKey.Forward, "PhotoModeForward", UiKey.W, "Fly forward along the view (hold)."),
        (FlightKey.Back, "PhotoModeBack", UiKey.S, "Fly back (hold)."),
        (FlightKey.Left, "PhotoModeLeft", UiKey.A, "Fly left (hold)."),
        (FlightKey.Right, "PhotoModeRight", UiKey.D, "Fly right (hold)."),
        (FlightKey.Up, "PhotoModeUp", UiKey.E, "Fly straight up (hold)."),
        (FlightKey.Down, "PhotoModeDown", UiKey.Q, "Fly straight down (hold)."),
        (FlightKey.Fast, "PhotoModeFast", UiKey.R, "Fly 4x faster (hold)."),
        (FlightKey.Slow, "PhotoModeSlow", UiKey.F, "Fly at a fifth of the speed (hold)."),
    };

    internal const string ZoomInKeybindId = "PhotoModeZoomIn";
    internal const string ZoomOutKeybindId = "PhotoModeZoomOut";
    internal static readonly KeyChord DefaultZoomInKey = new(UiKey.Z);
    internal static readonly KeyChord DefaultZoomOutKey = new(UiKey.X);

    private static readonly bool[] HeldKeys = new bool[PhotoFlight.FlightKeyCount];
    private static int _zoomNotches;

    private const string Holder = "progenitor.photo";

    private static readonly object Owner = new();
    private static readonly List<IKeybindHandle> Binds = new();
    private static IDisposable? _group;

    /// <summary>The Keybinds tab's "Photo mode" group: toggle, exit, move x4, up/down, fast/slow, zoom in/out, HUD, pause.</summary>
    internal static readonly string[] GroupOrder =
    {
        ToggleKeybindId, ExitKeybindId,
        "PhotoModeForward", "PhotoModeBack", "PhotoModeLeft", "PhotoModeRight",
        "PhotoModeUp", "PhotoModeDown", "PhotoModeFast", "PhotoModeSlow",
        ZoomInKeybindId, ZoomOutKeybindId, LockKeybindId, HudKeybindId, PauseKeybindId,
    };

    internal const string GroupId = "photo-mode";
    internal const string GroupTitle = "Photo mode";

    private static ConfigEntry<bool>? _enabled, _hideOnEnter, _hideNameplates;
    private static ConfigEntry<float>? _sensitivity;

    private static bool _active;
    private static CameraDirector? _director;
    private static InputLease? _lease;
    private static PhotoPose _pose;
    private static float _speed = PhotoFlight.DefaultSpeed;
    private static float _fov, _fovBefore;
    private static bool _fovChanged;
    private static long _world;

    private static bool _paused, _pausePending;
    private static int _pauseNonce;

    /// <summary>The exclusive gameplay-input hold (UiInputCapture.AcquireExclusive), kept until held keys come up after exit.</summary>
    private static PhotoInputHold? _hold;
    private static bool _drainHooked;

    /// <summary>What the connected server says about photo mode (<c>[PhotoMode] AllowOnThisServer</c>).</summary>
    private static PhotoServerAllows _serverState = PhotoServerAllows.NotInWorld;
    private static int _policyNonce;
    private static int _policyFrames;
    private const int PolicyCheckFrames = 30;

    internal static PhotoServerAllows ServerState => _serverState;

    // ---- aim hold and lock-on --------------------------------------------------------------------

    /// <summary>
    /// The character's aim is held at its value from entry through the input lease's Aim channel
    /// (the proven command patch that writes EntityInput.AimPosition / plane / direction / length and
    /// InputCommand.RotationInput), so the character stops turning toward the cursor.
    /// </summary>
    internal const string AimMethod = "input-lease aim channel (EntityInput.AimPosition + RotationInput command patch)";

    private static bool _aimHeld;
    private static int _aimEntryFrame;
    private static readonly PhotoLockState Lock = new();
    private static string _lockedName = "";
    private static string _previewName = "";

    internal static bool IsActive => _active;
    internal static bool IsPaused => _paused;

    internal static void Install()
    {
        if (Plugin.IsServerProcess)
        {
            PhotoPauseServer.Install();
            return;
        }

        var file = UiKeybindHost.CoreConfig;
        if (file == null) return;
        try
        {
            _enabled = file.Bind(Section, "Enabled", false,
                "Photo mode: Ctrl+Shift+F9 (rebindable) switches to a free-flying camera with the HUD hidden. "
                + "WASD moves, E up, Q down, right mouse looks, R fast, F slow, the wheel "
                + "sets speed and Z/X zoom. H hides the HUD, P pauses the world "
                + "(admins only). Off by default.");
            _hideOnEnter = file.Bind(Section, "HideHudOnEnter", true, "Hide the HUD when photo mode starts (H toggles it).");
            _hideNameplates = file.Bind(Section, "HideNameplates", true, "Hiding the HUD also hides nameplates, health bars and combat text.");
            _sensitivity = file.Bind(Section, "LookSensitivity", 1f, "Mouse-look speed in photo mode (1 = default).");

            Binds.Add(Keybinds.Register(typeof(PhotoMode), SettingsPanel.PluginId, ToggleKeybindId, file, DefaultToggleKey,
                "Enter or leave photo mode (turn on Photo mode > Enabled first).", Toggle, allowWhileCaptured: true));
            Binds.Add(PhotoBind(file, ExitKeybindId, DefaultExitKey, "Leave photo mode (instead of opening the game menu).",
                () => Exit(null), null));
            Binds.Add(PhotoBind(file, LockKeybindId, DefaultLockKey, "Lock on (hold to target, tap to release).", LockDown, LockUp));
            Binds.Add(PhotoBind(file, HudKeybindId, DefaultHudKey, "Hide or show the HUD.", ToggleHud, null));
            Binds.Add(PhotoBind(file, PauseKeybindId, DefaultPauseKey,
                "Pause the world (admins only).", TogglePause, null));
            Binds.Add(PhotoBind(file, ZoomInKeybindId, DefaultZoomInKey, "Zoom in (narrower field of view).", () => _zoomNotches++, null));
            Binds.Add(PhotoBind(file, ZoomOutKeybindId, DefaultZoomOutKey, "Zoom out (wider field of view).", () => _zoomNotches--, null));
            foreach (var k in PhotoKeys)
            {
                int slot = (int)k.Key;
                Binds.Add(PhotoBind(file, k.Id, new KeyChord(k.Default), k.Description,
                    () => HeldKeys[slot] = true, () => HeldKeys[slot] = false));
            }
            Keybinds.PhotoScopeOpen = () => _active;
            _group = KeybindGroups.Define(SettingsPanel.PluginId, GroupId, GroupTitle, GroupOrder, new[]
            {
                new KeybindInfoRow("PhotoModeLook", "Look around", "Hold right mouse",
                    "Hold the right mouse button and move the mouse to turn the camera.",
                    "Fixed: the right mouse button cannot be bound through the keybind API."),
                new KeybindInfoRow("PhotoModeSpeed", "Flying speed", "Mouse wheel",
                    "Wheel up flies faster, wheel down slower (x1.25 a notch, 0.25 to 100 m/s).",
                    "Fixed: the mouse wheel is not a key."),
            }, () => PhotoServerPolicy.UnavailableReason(_serverState));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PhotoMode: settings or keybinds could not be registered: " + e.Message);
        }

        PhotoPauseClient.Install(OnPauseReply);
        _hold = new PhotoInputHold(UiInputCapture.AcquireExclusive, UiInputCapture.ReleaseExclusive,
            UnityInputRuntime.AnyKeyOrButtonHeld, () => Environment.TickCount64);
        CustomEvents.OnReceive(PhotoPolicyMessage.EventId, OnPolicy);
        Plugin.Frame += PolicyTick;
    }

    // ---- the server's [PhotoMode] AllowOnThisServer ---------------------------------------------

    /// <summary>Every 30 frames: follow world presence and the handshake; ask a Progenitor server once.</summary>
    private static void PolicyTick()
    {
        if (++_policyFrames % PolicyCheckFrames != 0) return;
        try
        {
            var before = _serverState;
            var next = PhotoServerPolicy.AfterHandshake(before, Worlds.Client != null, Handshake.LastClientResult);
            if (next == before) return;
            SetServerState(next, "handshake");
            if (next == PhotoServerAllows.Pending)
                CustomEvents.SendToServer(PhotoPolicyMessage.EventId, PhotoPolicyMessage.Query(++_policyNonce));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("PhotoMode: server policy check threw: " + e.Message); }
    }

    private static void OnPolicy(int _, byte* payload, int length)
    {
        if (payload == null) return;
        if (!CustomEvents.TryRead<PhotoPolicyMessage>(payload, length, out var msg)) return;
        if (msg.Schema != PhotoPolicyMessage.CurrentSchema) return;
        if (Worlds.Client == null) return;
        SetServerState(msg.IsAllowed ? PhotoServerAllows.Allowed : PhotoServerAllows.Disallowed, "server");
    }

    /// <summary>Apply a new server state; leaves photo mode when the server now disallows it.</summary>
    internal static void SetServerState(PhotoServerAllows next, string why)
    {
        if (next == _serverState) return;
        _serverState = next;
        Plugin.Logger?.LogInfo("PhotoMode: server allows = " + PhotoServerPolicy.Source(next) + "/" + next + " (" + why + ").");
        // The Keybinds tab's Photo mode group note follows the server answer.
        try { Keybinds.NoteViewChanged(); } catch { /* the tab redraws on its next rebuild */ }
        if (PhotoServerPolicy.MustExit(_active, next))
        {
            Exit("disabled on this server");
            PhotoNotice.Show(PhotoServerPolicy.DisabledText + ".");
        }
    }

    // ---- the exclusive input hold ---------------------------------------------------------------

    private static void DrainTick()
    {
        var hold = _hold;
        if (hold == null || !hold.Draining || hold.Tick())
        {
            Plugin.Frame -= DrainTick;
            _drainHooked = false;
        }
    }

    /// <summary>A <see cref="KeybindScope.Photo"/> bind: fires only while photo mode is active.</summary>
    private static IKeybindHandle PhotoBind(ConfigFile file, string id, KeyChord chord, string description, Action onPressed, Action? onReleased) =>
        Keybinds.RegisterScoped(typeof(PhotoMode), SettingsPanel.PluginId, id, file, chord, description,
            onPressed, null, onReleased, allowWhileCaptured: true, allowWhenChatUnknown: false, KeybindScope.Photo);

    private static void ClearHeld()
    {
        Array.Clear(HeldKeys, 0, HeldKeys.Length);
        _zoomNotches = 0;
    }

    internal static void Uninstall()
    {
        if (Plugin.IsServerProcess)
        {
            PhotoPauseServer.Uninstall();
            return;
        }

        try { Exit("Core unloaded"); }
        catch (Exception e) { Plugin.Logger?.LogWarning("PhotoMode: exit on unload threw: " + e.Message); }
        foreach (var b in Binds)
        {
            try { b.Dispose(); } catch { /* keybinds may already be torn down */ }
        }
        Binds.Clear();
        try { _group?.Dispose(); } catch { /* the registry may already be reset */ }
        _group = null;
        try { _hold?.Drop(); } catch (Exception e) { Plugin.Logger?.LogWarning("PhotoMode: input hold drop threw: " + e.Message); }
        _hold = null;
        if (_drainHooked) { Plugin.Frame -= DrainTick; _drainHooked = false; }
        Plugin.Frame -= PolicyTick;
        CustomEvents.RemoveHandler(PhotoPolicyMessage.EventId, OnPolicy);
        _serverState = PhotoServerAllows.NotInWorld;
        Keybinds.PhotoScopeOpen = () => false;
        ClearHeld();
        PhotoPauseClient.Uninstall();
        PhotoNotice.Close();
        try { Dev.MainThread.DropOwnedBy(Owner, typeof(PhotoMode).Assembly); } catch { /* teardown */ }
        _enabled = _hideOnEnter = _hideNameplates = null;
        _sensitivity = null;
    }

    private static void Toggle()
    {
        if (_active) Exit(null);
        else Enter();
    }

    // ---- Dev bridge (photo op, through PhotoModeBridge) ------------------------------------------

    /// <summary>Bridge <c>on</c>: optionally turn <c>[PhotoMode] Enabled</c> on first, then enter. Null when active.</summary>
    internal static string? BridgeOn(bool enable)
    {
        if (Plugin.IsServerProcess) return "photo mode is client-side: send this op to the client bridge (27341).";
        if (_enabled == null) return "photo mode settings are not registered (no Core ui cfg).";
        if (_active) return null;
        if (enable && !_enabled.Value) _enabled.Value = true;
        return Enter();
    }

    /// <summary>Bridge <c>off</c>: leave photo mode (resumes a pause this client asked for). True when it was active.</summary>
    internal static bool BridgeOff()
    {
        if (!_active) return false;
        Exit("bridge");
        return true;
    }

    /// <summary>Bridge <c>status</c>.</summary>
    internal static Dictionary<string, object?> BridgeStatus() => new()
    {
        ["side"] = Plugin.IsServerProcess ? "server" : "client",
        ["enabled"] = _enabled?.Value,
        ["active"] = _active,
        ["paused"] = _paused,
        ["pausePending"] = _pausePending,
        ["hudHidden"] = PhotoHud.IsHidden,
        ["pose"] = _active ? _pose.ToString() : null,
        ["speed"] = _speed,
        ["fov"] = _active ? _fov : (float?)null,
        ["holdsInput"] = _lease != null && _lease.Active,
        ["serverPauses"] = Plugin.IsServerProcess ? PhotoPauseServer.HolderCount : (int?)null,
        ["serverAllows"] = Plugin.IsServerProcess ? PhotoPauseServer.Allowed : PhotoServerPolicy.Allows(_serverState),
        ["serverAllowsSource"] = Plugin.IsServerProcess ? "config" : PhotoServerPolicy.Source(_serverState),
        ["gameInputBlocked"] = !Plugin.IsServerProcess && UiInputCapture.ExclusiveCount > 0 && UiInputCapture.IsRegistered,
        ["inputHold"] = _hold == null ? "none" : _hold.Draining ? "draining" : _hold.Engaged ? "engaged" : "released",
        ["blockedCollections"] = UiInputCapture.ExclusiveCount > 0 ? PhotoInputBlockReport.Collections() : Array.Empty<string>(),
        ["inputCapture"] = UiInputCapture.Describe(),
        ["aimHeld"] = _active && _aimHeld,
        ["aimMethod"] = AimMethod,
        ["targeting"] = Lock.Targeting,
        ["preview"] = Lock.Preview is { } pv ? NameRow(pv) : null,
        ["lockedTarget"] = Lock.Locked is { } lk ? NameRow(lk) : null,
    };

    /// <summary>Enter photo mode. Null when entered; otherwise the refusal (also shown as a notice).</summary>
    private static string? Enter()
    {
        if (_enabled == null || !_enabled.Value)
        {
            return Refuse("Photo mode is off. Turn on Photo mode > Enabled in the Progenitor settings (F10).");
        }

        if (!PhotoServerPolicy.MayEnter(_serverState))
        {
            return Refuse(PhotoServerPolicy.DisabledText + ".");
        }

        var cam = UnityEngine.Camera.main;
        if (cam == null || Worlds.Client == null)
        {
            return Refuse("Photo mode needs you to be in a world.");
        }

        var acquired = InputLeases.TryAcquire(Holder,
            new InputLeaseOptions(InputChannels.All, UserPolicy.Override, preempt: true, reason: "photo mode"), out var lease);
        if (acquired != InputAcquireResult.Acquired || lease == null)
        {
            return Refuse("Photo mode could not take your character's input (" + acquired + ").");
        }

        var t = cam.transform;
        var p = t.position;
        var f = t.forward;
        var from = new Units.UnitVec3(p.x, p.y, p.z);
        var (yaw, pitch) = CameraMath.LookAngles(from, new Units.UnitVec3(p.x + f.x, p.y + f.y, p.z + f.z));
        _pose = new PhotoPose(p.x, p.y, p.z, yaw, pitch);
        _fovBefore = _fov = cam.fieldOfView;
        _fovChanged = false;

        var director = new CameraDirector(UnityCameraRuntime.Instance, "Photo mode");
        try { director.Free(FreeShotPlan.Create(from, _pose.Yaw, _pose.Pitch, null), instant: true); }
        catch (Exception e)
        {
            lease.Dispose();
            director.Release();
            return Refuse("Photo mode could not take the camera: " + e.Message);
        }

        _director = director;
        _lease = lease;
        // No gameplay input reaches the game from here on: the exclusive mask (every button except UI
        // navigation, every analog, camera rotation, the menu hotkeys, Back / Menu_Close). Held keys the
        // game had already seen stop counting as held, and the lease writes a stop for the character.
        try { _hold?.Enter(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("PhotoMode: input hold threw: " + e.Message); }
        lease.Stop();
        // The aim is taken on the first command after entry (Tick), at the value the game built from
        // the cursor then, and held there until the lease ends with photo mode.
        _aimHeld = false;
        _aimEntryFrame = lease.LastSent.Seen ? lease.LastSent.ClientFrame : int.MinValue;
        Lock.Reset();
        lease.Ended += OnLeaseEnded;
        _world = UnityCameraRuntime.Instance.WorldToken;
        ClearHeld();
        _active = true;
        _paused = _pausePending = false;
        Plugin.Frame += Tick;

        if (_hideOnEnter?.Value ?? true) PhotoHud.Hide(_hideNameplates?.Value ?? true);
        Plugin.Logger?.LogInfo("PhotoMode: entered at " + _pose);
        PhotoNotice.Show("Photo mode: WASD fly, E/Q up/down, right mouse look, R fast, F slow, wheel speed, Z/X zoom. "
            + "H HUD, P pause, Ctrl+Shift+F9 exit.");
        return null;
    }

    private static string Refuse(string text)
    {
        PhotoNotice.Show(text);
        return text;
    }

    private static void OnLeaseEnded(InputLeaseEnd reason)
    {
        // The release key (Ctrl+Shift+F12) or another lease preempting ours: leave cleanly next frame.
        if (_active) Dev.MainThread.PostDelayed(0, () => Exit("input released (" + reason + ")"), Owner);
    }

    private static void Exit(string? why)
    {
        if (!_active) return;
        _active = false;
        ClearHeld();
        Plugin.Frame -= Tick;
        // The lease (and its held aim) ends below; the lock goes with photo mode, silently.
        _aimHeld = false;
        Lock.Reset();
        _lockedName = _previewName = "";

        if (_paused || _pausePending) PhotoPauseClient.Send(++_pauseNonce, pause: false);
        _paused = _pausePending = false;

        PhotoHud.Restore();
        if (_fovChanged)
        {
            try { var cam = UnityEngine.Camera.main; if (cam != null) cam.fieldOfView = _fovBefore; } catch { /* camera gone with the world */ }
            _fovChanged = false;
        }

        try { _director?.Release(); } catch (Exception e) { Plugin.Logger?.LogWarning("PhotoMode: camera release threw: " + e.Message); }
        _director = null;

        var lease = _lease;
        _lease = null;
        if (lease != null)
        {
            lease.Ended -= OnLeaseEnded;
            try { if (lease.Active) lease.Dispose(); } catch { /* already ended */ }
        }

        // Keep the game's input masked until every key and button held during photo mode is released
        // (at most PhotoInputHold.MaxDrainMs), so no release reaches the game as an action.
        try
        {
            _hold?.Exit();
            if (_hold != null && _hold.Draining && !_drainHooked)
            {
                Plugin.Frame += DrainTick;
                _drainHooked = true;
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("PhotoMode: input hold release threw: " + e.Message); }

        Plugin.Logger?.LogInfo("PhotoMode: left" + (why != null ? " (" + why + ")" : "") + ".");
        if (why == null) PhotoNotice.Close();
    }

    private static void Tick()
    {
        if (!_active) return;
        try
        {
            if (UnityCameraRuntime.Instance.WorldToken != _world || Worlds.Client == null)
            {
                Exit("the world changed");
                return;
            }

            var input = Sample(out int notches);
            float dt = UnityEngine.Time.unscaledDeltaTime;
            int zoom = _zoomNotches;
            _zoomNotches = 0;
            if (zoom != 0)
            {
                _fov = PhotoFlight.NextFov(_fov, zoom);
                _fovChanged = true;
            }

            HoldAim();
            UpdateLock();
            if (Lock.Locked is { } locked && TryUnitFocus(locked, out var focus))
            {
                // Locked: the wheel is distance to the target, not flying speed (R / F still apply).
                _pose = PhotoOrbit.Step(_pose, focus, input, _speed, _sensitivity?.Value ?? 1f, dt, notches);
            }
            else
            {
                if (notches != 0) _speed = PhotoFlight.NextSpeed(_speed, notches);
                _pose = PhotoFlight.Step(_pose, input, _speed, _sensitivity?.Value ?? 1f, dt);
            }
            _director?.Free(FreeShotPlan.Create(new Units.UnitVec3(_pose.X, _pose.Y, _pose.Z), _pose.Yaw, _pose.Pitch, null), instant: true);

            if (_fovChanged)
            {
                var cam = UnityEngine.Camera.main;
                if (cam != null) cam.fieldOfView = _fov;
            }

            PhotoHud.Reassert();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PhotoMode: frame threw, leaving photo mode: " + e);
            Exit("error");
        }
    }

    /// <summary>
    /// One frame of flight input: the held keys from the Photo-scope binds (<see cref="HeldKeys"/>),
    /// and the pointer (right-button look, mouse movement, wheel notches) from the framework input layer.
    /// </summary>
    private static PhotoFlightInput Sample(out int notches)
    {
        var input = PhotoFlight.FromHeld(HeldKeys);
        notches = 0;
        if (Keybinds.PlayerIsTyping) return default;

        if (UnityInputRuntime.TrySamplePointer(out bool right, out float dx, out float dy, out float scrollY))
        {
            input.Looking = right;
            if (right)
            {
                input.LookX = dx;
                input.LookY = dy;
            }
            notches = PhotoFlight.Notches(scrollY);
        }

        return input;
    }

    private static void HoldAim()
    {
        var lease = _lease;
        if (_aimHeld || lease == null || !lease.Active) return;
        var last = lease.LastSent;
        if (!last.Seen || last.ClientFrame == _aimEntryFrame) return;
        lease.AimAt(last.AimPosition);
        _aimHeld = true;
        Plugin.Logger?.LogInfo("PhotoMode: aim held at " + last.AimPosition + " (" + AimMethod + ").");
    }

    /// <summary>The unit under the cursor: the game's own hover from the newest command, when it is a living unit.</summary>
    private static InputEntityRef? UnderCursor()
    {
        var lease = _lease;
        if (lease == null) return null;
        var hovered = lease.LastSent.HoveredEntity;
        if (hovered.IsNull) return null;
        return PhotoLockUnits.IsLivingUnit(hovered) ? hovered : null;
    }

    private static bool TryUnitFocus(InputEntityRef unit, out Units.UnitVec3 focus)
    {
        focus = default;
        if (!PhotoLockUnits.TryPosition(unit, out var feet)) return false;
        focus = PhotoOrbit.FocusOf(feet);
        return true;
    }

    private static void LockDown()
    {
        if (!_active) return;
        Lock.KeyDown(NowSeconds(), UnderCursor());
        ShowPreview();
    }

    private static void LockUp()
    {
        if (!_active) return;
        var ev = Lock.KeyUp(NowSeconds(), UnderCursor());
        _previewName = "";
        Announce(ev);
    }

    /// <summary>Per frame: the preview follows the cursor while held; a locked unit that is gone is lost.</summary>
    private static void UpdateLock()
    {
        if (Lock.Targeting)
        {
            var before = Lock.Preview;
            Lock.Hover(UnderCursor());
            if (!Equals(before, Lock.Preview)) ShowPreview();
        }

        if (Lock.Locked is { } locked && !PhotoLockUnits.IsLivingUnit(locked))
            Announce(Lock.Lost());
    }

    /// <summary>Bridge row: entity, display name, prefab name and where the name came from.</summary>
    private static Dictionary<string, object?> NameRow(InputEntityRef unit)
    {
        var n = PhotoLockUnits.Describe(unit);
        return new Dictionary<string, object?>
        {
            ["entity"] = unit.ToString(),
            ["name"] = n.Display,
            ["prefab"] = n.Prefab,
            ["nameSource"] = n.Source,
        };
    }

    private static void ShowPreview()
    {
        var p = Lock.Preview;
        string name = p == null ? "" : PhotoLockUnits.NameOf(p.Value);
        if (name == _previewName) return;
        _previewName = name;
        PhotoNotice.Show(name.Length == 0 ? "Target: (nothing under the cursor)" : "Target: " + name, 30f);
    }

    private static void Announce(PhotoLockEvent ev)
    {
        switch (ev)
        {
            case PhotoLockEvent.Locked:
                _lockedName = Lock.Locked == null ? "" : PhotoLockUnits.NameOf(Lock.Locked.Value);
                PhotoNotice.Show("Locked: " + _lockedName, 2f);
                break;
            case PhotoLockEvent.Unlocked:
                _lockedName = "";
                PhotoNotice.Show("Unlocked", 1f);
                break;
            case PhotoLockEvent.TargetLost:
                _lockedName = "";
                PhotoNotice.Show("Target lost", 1.5f);
                break;
            case PhotoLockEvent.Cancelled:
                PhotoNotice.Close();
                break;
        }
    }

    private static double NowSeconds() => Environment.TickCount64 / 1000.0;

    /// <summary>Bridge <c>photo lock</c>: lock a specific client entity. Null when locked; otherwise why not.</summary>
    internal static string? BridgeLock(InputEntityRef unit)
    {
        if (!_active) return "photo mode is not active";
        if (!PhotoLockUnits.IsLivingUnit(unit)) return "entity " + unit + " is not a living unit in the client world";
        Announce(Lock.LockDirect(unit));
        return null;
    }

    /// <summary>Bridge <c>photo unlock</c>. True when something was locked.</summary>
    internal static bool BridgeUnlock()
    {
        var ev = Lock.UnlockDirect();
        Announce(ev);
        return ev == PhotoLockEvent.Unlocked;
    }

    private static void ToggleHud()
    {
        if (!_active) return;
        if (PhotoHud.IsHidden) PhotoHud.Restore();
        else PhotoHud.Hide(_hideNameplates?.Value ?? true);
    }

    private static void TogglePause()
    {
        if (!_active || _pausePending) return;
        if (!string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal))
        {
            PhotoNotice.Show("This server does not run Progenitor, so it cannot pause the world.");
            return;
        }
        _pausePending = true;
        if (!PhotoPauseClient.Send(++_pauseNonce, pause: !_paused))
        {
            _pausePending = false;
            PhotoNotice.Show("The pause request could not be sent.");
        }
    }

    private static void OnPauseReply(PhotoPauseReply reply)
    {
        if (reply.Nonce != _pauseNonce) return;
        _pausePending = false;
        if (!_active)
        {
            // Left photo mode while the request was in flight: make sure the world is not left paused.
            if (reply.IsPaused) PhotoPauseClient.Send(++_pauseNonce, pause: false);
            return;
        }
        _paused = reply.IsPaused;
        PhotoNotice.Show(PhotoPauseLedger.Describe(reply));
    }
}

/// <summary>
/// Hides the game HUD's canvases (under <c>HUDCanvas(Clone)</c>) and puts each back as it was. One hidden canvas can be
/// left shown (<see cref="Keep"/>): the camera preview keeps the one that holds the game's container window while this
/// client's container preview owns it (<see cref="CameraPreviewHud"/>).
/// </summary>
internal static class PhotoHud
{
    /// <summary>Canvases that carry nameplates, health bars and floating combat text.</summary>
    private static readonly string[] NameplateCanvases = { "CharacterHUDsCanvas", "SCTCanvas" };

    private static readonly List<(UnityEngine.Canvas Canvas, bool WasEnabled)> Hidden = new();

    /// <summary>The index in <see cref="Hidden"/> left shown, or -1.</summary>
    private static int _kept = -1;

    internal static bool IsHidden => Hidden.Count > 0;

    internal static void Hide(bool nameplates)
    {
        if (IsHidden) return;
        _kept = -1;
        foreach (var o in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.Canvas>()))
        {
            var c = o.TryCast<UnityEngine.Canvas>();
            if (c == null || !c.isRootCanvas) continue;
            var go = c.gameObject;
            if (!go.scene.IsValid() || !go.activeInHierarchy) continue;
            if (!c.transform.root.name.StartsWith("HUDCanvas", StringComparison.Ordinal)) continue;
            if (!nameplates && Array.IndexOf(NameplateCanvases, go.name) >= 0) continue;
            Hidden.Add((c, c.enabled));
            c.enabled = false;
        }
        Plugin.Logger?.LogInfo("PhotoMode: HUD hidden (" + Hidden.Count + " canvases" + (nameplates ? ", nameplates included" : "") + ").");
    }

    /// <summary>
    /// Leave the hidden canvas that holds <paramref name="part"/> as it was before the hide (null: keep none, so a
    /// previously kept canvas is hidden again). Returns the kept canvas's name, or null.
    /// </summary>
    internal static string? Keep(UnityEngine.Transform? part)
    {
        if (!IsHidden) return null;
        var holds = new bool[Hidden.Count];
        if (part != null)
        {
            for (int i = 0; i < Hidden.Count; i++)
            {
                try { var c = Hidden[i].Canvas; holds[i] = c != null && part.IsChildOf(c.transform); }
                catch { holds[i] = false; }
            }
        }
        int want = CameraPreviewHud.CanvasToKeep(part != null, holds);
        if (want != _kept)
        {
            int was = _kept;
            _kept = want;
            if (was >= 0 && was < Hidden.Count)
            {
                try { var c = Hidden[was].Canvas; if (c != null) c.enabled = false; } catch { /* destroyed */ }
                Plugin.Logger?.LogInfo("PhotoMode: HUD canvas '" + NameAt(was) + "' hidden again (no container window to keep).");
            }
            if (want >= 0)
            {
                try { var c = Hidden[want].Canvas; if (c != null) c.enabled = Hidden[want].WasEnabled; } catch { /* destroyed */ }
                Plugin.Logger?.LogInfo("PhotoMode: HUD canvas '" + NameAt(want) + "' left shown (it holds the container window).");
            }
        }
        return want >= 0 ? NameAt(want) : null;
    }

    private static string NameAt(int i)
    {
        try { var c = Hidden[i].Canvas; return c == null ? "(destroyed)" : c.gameObject.name ?? ""; }
        catch { return "(destroyed)"; }
    }

    /// <summary>The game turns some canvases back on as panels open; keep them off while hidden (all but the kept one).</summary>
    internal static void Reassert()
    {
        for (int i = 0; i < Hidden.Count; i++)
        {
            if (!CameraPreviewHud.Reasserts(i, _kept)) continue;
            var c = Hidden[i].Canvas;
            if (c != null && c.enabled) c.enabled = false;
        }
    }

    internal static void Restore()
    {
        foreach (var (c, was) in Hidden)
        {
            try { if (c != null) c.enabled = was; } catch { /* destroyed with the world */ }
        }
        if (Hidden.Count > 0) Plugin.Logger?.LogInfo("PhotoMode: HUD restored.");
        Hidden.Clear();
        _kept = -1;
    }

    /// <summary>The readback: every canvas the hide took, whether it is enabled now, and which one is left shown.</summary>
    internal static Dictionary<string, object?> Describe()
    {
        var rows = new List<Dictionary<string, object?>>();
        for (int i = 0; i < Hidden.Count; i++)
        {
            bool? enabled = null;
            try { var c = Hidden[i].Canvas; if (c != null) enabled = c.enabled; } catch { /* destroyed */ }
            rows.Add(new Dictionary<string, object?>
            {
                ["name"] = NameAt(i),
                ["wasEnabled"] = Hidden[i].WasEnabled,
                ["enabled"] = enabled,
                ["kept"] = i == _kept,
            });
        }
        return new Dictionary<string, object?>
        {
            ["hidden"] = IsHidden,
            ["canvasCount"] = Hidden.Count,
            ["canvases"] = rows,
            ["kept"] = _kept >= 0 && _kept < Hidden.Count ? NameAt(_kept) : null,
        };
    }
}

/// <summary>A short-lived, non-modal Progenitor notice for photo mode (closes itself).</summary>
internal static class PhotoNotice
{
    private const float Seconds = 4f;
    private static readonly object Owner = new();
    private static readonly OwnedPanel Owned = new();
    private static int _generation;

    internal static void Show(string text, float seconds = Seconds)
    {
        Plugin.Logger?.LogInfo("PhotoMode: " + text);
        try
        {
            Close();
            var panel = Ui.Ui.Panel("Photo mode", Owner, SettingsPanel.PluginId);
            var stale = Owned.Adopt(panel);
            if (stale != null && !ReferenceEquals(stale, panel)) stale.Dispose();
            if (panel.Enabled) panel.Label(text);
            Ui.Ui.Show(panel);
            int generation = ++_generation;
            Dev.MainThread.PostDelayed(seconds, () => { if (generation == _generation) Close(); }, Owner);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PhotoMode: notice threw: " + e.Message);
        }
    }

    internal static void Close()
    {
        var panel = Owned.Take();
        if (panel == null) return;
        try { panel.Dispose(); } catch { /* teardown */ }
    }
}

/// <summary>The client half of the pause channel (<see cref="PhotoPauseLedger.EventId"/>).</summary>
internal static unsafe class PhotoPauseClient
{
    private static Action<PhotoPauseReply>? _onReply;

    internal static void Install(Action<PhotoPauseReply> onReply)
    {
        _onReply = onReply;
        CustomEvents.OnReceive(PhotoPauseLedger.EventId, OnReply);
    }

    internal static void Uninstall()
    {
        CustomEvents.RemoveHandler(PhotoPauseLedger.EventId, OnReply);
        _onReply = null;
    }

    internal static bool Send(int nonce, bool pause) =>
        CustomEvents.SendToServer(PhotoPauseLedger.EventId, PhotoPauseRequest.For(nonce, pause));

    private static void OnReply(int _, byte* payload, int length)
    {
        if (payload == null) return;
        if (!CustomEvents.TryRead<PhotoPauseReply>(payload, length, out var reply)) return;
        if (reply.Schema != PhotoPauseReply.CurrentSchema) return;
        _onReply?.Invoke(reply);
    }
}

/// <summary>
/// The server half: admin check, one world-speed freeze per user through <see cref="TimeScaleRuntime"/>,
/// and a sweep that ends the freeze of anyone who disconnected or lost admin.
/// </summary>
internal static unsafe class PhotoPauseServer
{
    private const int SweepFrames = 30;

    private static PhotoPauseLedger? _ledger;
    private static readonly Dictionary<int, Entity> Users = new();

    /// <summary>Users holding a photo-mode pause now (bridge status).</summary>
    internal static int HolderCount => _ledger?.Holders.Count ?? 0;
    /// <summary>The requester's name while <see cref="PhotoPauseLedger.Handle"/> runs, for the lease's owner label.</summary>
    private static string _requester = "";
    private static int _frames;

    private static ConfigEntry<bool>? _allow;
    /// <summary>Users whose client asked for the photo-mode policy (Progenitor clients): every change is pushed to them.</summary>
    private static readonly HashSet<int> Askers = new();

    /// <summary>The server's <c>[PhotoMode] AllowOnThisServer</c> (true until the shared cfg binds).</summary>
    internal static bool Allowed => _allow?.Value ?? true;

    internal static void Install()
    {
        _ledger = new PhotoPauseLedger(Freeze, () => Allowed);
        CustomEvents.OnReceiveFromClient(PhotoPauseLedger.EventId, OnRequest);
        CustomEvents.OnReceiveFromClient(PhotoPolicyMessage.EventId, OnPolicyQuery);
        Plugin.Frame += Sweep;
        try
        {
            Config.SharedConfig.BindWhenAvailable(
                "[" + PhotoServerPolicy.ConfigSection + "] " + PhotoServerPolicy.ConfigKey,
                file =>
                {
                    _allow = Config.ConfigHelper.Bind(file, PhotoServerPolicy.ConfigSection, PhotoServerPolicy.ConfigKey,
                        true, PhotoServerPolicy.ConfigDescription);
                    Config.ConfigHelper.AddSettingChanged(file, OnSettingChanged);
                    Plugin.Logger?.LogInfo("PhotoMode: [" + PhotoServerPolicy.ConfigSection + "] " + PhotoServerPolicy.ConfigKey + " = " + _allow.Value + ".");
                });
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("PhotoMode: binding AllowOnThisServer threw: " + e.Message + "; photo mode stays allowed."); }
    }

    private static void OnSettingChanged(object? sender, BepInEx.Configuration.SettingChangedEventArgs args)
    {
        var def = args?.ChangedSetting?.Definition;
        if (def == null || !string.Equals(def.Section, PhotoServerPolicy.ConfigSection, StringComparison.Ordinal)
            || !string.Equals(def.Key, PhotoServerPolicy.ConfigKey, StringComparison.Ordinal)) return;
        Dev.MainThread.PostDelayed(0, PushPolicy, typeof(PhotoPauseServer));
    }

    /// <summary>Tell every client that asked; a disallow also ends every held pause.</summary>
    private static void PushPolicy()
    {
        bool allowed = Allowed;
        Plugin.Logger?.LogInfo("PhotoMode: AllowOnThisServer = " + allowed + "; telling " + Askers.Count + " client(s).");
        if (!allowed)
        {
            try { _ledger?.ReleaseAll(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("PhotoMode: releasing pauses threw: " + e.Message); }
            Users.Clear();
        }

        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return;
        var msg = PhotoPolicyMessage.Answer(0, allowed);
        foreach (int index in new List<int>(Askers))
        {
            try { CustomEvents.SendToUser(bootstrap, index, PhotoPolicyMessage.EventId, in msg); }
            catch (Exception e) { Plugin.Logger?.LogWarning("PhotoMode: policy push to user " + index + " threw: " + e.Message); }
        }
    }

    private static void OnPolicyQuery(int _, Entity userEntity, User user, byte* payload, int length)
    {
        if (payload == null) return;
        if (!CustomEvents.TryRead<PhotoPolicyMessage>(payload, length, out var query)) return;
        if (query.Schema != PhotoPolicyMessage.CurrentSchema) return;
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity)) return;
            var current = world.EntityManager.GetComponentData<User>(userEntity);
            if (!current.IsConnected) return;
            Askers.Add(current.Index);
            var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
            var answer = PhotoPolicyMessage.Answer(query.Nonce, Allowed);
            if (bootstrap != null) CustomEvents.SendToUser(bootstrap, current.Index, PhotoPolicyMessage.EventId, in answer);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("PhotoMode: policy query threw: " + e.Message); }
    }

    internal static void Uninstall()
    {
        CustomEvents.RemoveClientHandler(PhotoPauseLedger.EventId, OnRequest);
        CustomEvents.RemoveClientHandler(PhotoPolicyMessage.EventId, OnPolicyQuery);
        Askers.Clear();
        _allow = null;
        Plugin.Frame -= Sweep;
        try { _ledger?.ReleaseAll(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("PhotoMode: releasing pauses threw: " + e.Message); }
        _ledger = null;
        Users.Clear();
    }

    private static IDisposable? Freeze()
    {
        // One ordinary ctx.Time world-speed lease per user (no second writer to TimeScale), owned by a
        // tag that names the user in the bridge's `time status`.
        var owner = new TimeScaleOwnerTag("photo mode: " + (_requester.Length > 0 ? _requester : "unknown user"));
        var lease = TimeScaleRuntime.Take(Worlds.Server, TimeScaleOverrideKind.Freeze, 0f, owner, out string why);
        if (lease == null) Plugin.Logger?.LogWarning("PhotoMode: pause refused: " + why);
        return lease;
    }

    private static void OnRequest(int _, Entity userEntity, User user, byte* payload, int length)
    {
        var ledger = _ledger;
        if (payload == null || ledger == null) return;
        if (!CustomEvents.TryRead<PhotoPauseRequest>(payload, length, out var request)) return;
        if (request.Schema != PhotoPauseRequest.CurrentSchema) return;
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity)) return;
            // Re-read rather than trust the resolved copy, as the stop-server channel does.
            var current = world.EntityManager.GetComponentData<User>(userEntity);
            if (!current.IsConnected) return;
            int index = current.Index;
            _requester = current.CharacterName.ToString();
            PhotoPauseReply reply;
            try { reply = ledger.Handle(index, Progenitor.Install.ServerAdminList.IsProgenitorAdmin(current.PlatformId, current.IsAdmin), in request); }
            finally { _requester = ""; }
            if (ledger.Holds(index)) Users[index] = userEntity;
            else Users.Remove(index);
            Plugin.Logger?.LogInfo("PhotoMode: " + current.CharacterName + (request.Want != 0 ? " asked to pause" : " asked to resume")
                + " -> " + (reply.IsPaused ? "paused" : "not paused") + (reply.Reason != PhotoPauseReason.None ? " (" + reply.Reason + ")" : "") + ".");
            var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap != null) CustomEvents.SendToUser(bootstrap, index, PhotoPauseLedger.EventId, in reply);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PhotoMode: pause request threw: " + e.Message);
        }
    }

    private static void Sweep()
    {
        var ledger = _ledger;
        if (ledger == null || ledger.Holders.Count == 0) return;
        if (++_frames % SweepFrames != 0) return;
        var world = Worlds.Server;
        int ended = ledger.Sweep(index =>
        {
            if (world == null || !world.IsCreated || !Users.TryGetValue(index, out var e)) return false;
            var em = world.EntityManager;
            if (!em.Exists(e) || !em.HasComponent<User>(e)) return false;
            var u = em.GetComponentData<User>(e);
            return u.IsConnected && Progenitor.Install.ServerAdminList.IsProgenitorAdmin(u.PlatformId, u.IsAdmin) && u.Index == index;
        });
        if (ended > 0) Plugin.Logger?.LogInfo("PhotoMode: ended " + ended + " pause(s) whose holder left or lost admin.");
    }
}
/// <summary>Client-world reads for lock-on: is a unit alive, where is it, what is it called.</summary>
internal static unsafe class PhotoLockUnits
{
    internal static bool IsLivingUnit(InputEntityRef unit)
    {
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated || unit.IsNull) return false;
            var em = world.EntityManager;
            var e = new Entity { Index = unit.Index, Version = unit.Version };
            if (!em.Exists(e) || e.Has<Dead>(em) || !e.Has<Health>(em) || !e.Has<Unity.Transforms.Translation>(em)) return false;
            byte* h = (byte*)em.GetComponentDataRawRO(e, ComponentIndex.IndexOf<Health>());
            // Health.Value @0x10, IsDead @0x18 (the same read UnityUnitRuntime.IsLivingUnit makes).
            return h != null && h[0x18] == 0 && *(float*)(h + 0x10) > 0f;
        }
        catch { return false; }
    }

    internal static bool TryPosition(InputEntityRef unit, out Units.UnitVec3 position)
    {
        position = default;
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated || unit.IsNull) return false;
            var em = world.EntityManager;
            var e = new Entity { Index = unit.Index, Version = unit.Version };
            if (!em.Exists(e) || !e.TryRead<Unity.Transforms.Translation>(em, out var tr)) return false;
            position = new Units.UnitVec3(tr.Value.x, tr.Value.y, tr.Value.z);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Display name for a unit (<see cref="PhotoUnitNames"/>); the display string only.</summary>
    internal static string NameOf(InputEntityRef unit) => Describe(unit).Display;

    /// <summary>Localized names by prefab hash, for this client world (cleared on a world change).</summary>
    private static readonly Dictionary<int, string> Localized = new();
    private static World? _namesWorld;

    /// <summary>
    /// A player's <c>PlayerCharacter.Name</c>; else the localized <c>ManagedCharacterHUD.Name</c> of the
    /// unit's prefab (the name the game's hover HUD and target info panel show), read through
    /// <c>GameDataSystem.ManagedDataRegistry</c> and the game's string table; else a prettified prefab name.
    /// </summary>
    internal static PhotoUnitName Describe(InputEntityRef unit)
    {
        string? player = null, localized = null, prefab = null;
        try
        {
            var world = Worlds.Client;
            if (world != null && world.IsCreated)
            {
                if (!ReferenceEquals(world, _namesWorld))
                {
                    Localized.Clear();
                    _namesWorld = world;
                }

                var em = world.EntityManager;
                var e = new Entity { Index = unit.Index, Version = unit.Version };
                if (em.Exists(e))
                {
                    if (e.Has<PlayerCharacter>(em))
                        player = em.GetComponentData<PlayerCharacter>(e).Name.ToString();
                    if (e.TryRead<PrefabGUID>(em, out var guid))
                    {
                        if (PrefabLookup.TryGetName(world, guid, out var name)) prefab = name;
                        localized = LocalizedName(world, guid);
                    }
                }
            }
        }
        catch { /* fall through to what resolved */ }
        return PhotoUnitNames.Choose(player, localized, prefab, unit.ToString());
    }

    private static string? LocalizedName(World world, PrefabGUID guid)
    {
        if (Localized.TryGetValue(guid.GuidHash, out var cached)) return cached.Length == 0 ? null : cached;
        string text = "";
        try
        {
            var gameData = world.GetExistingSystemManaged<GameDataSystem>();
            var registry = gameData?.ManagedDataRegistry;
            if (registry != null && registry.TryGet<ManagedCharacterHUD>(guid, out var hud) && hud != null
                && Localization.Localization.TryGetGameString(hud.Name, out var s))
                text = s;
        }
        catch { text = ""; }
        Localized[guid.GuidHash] = text;
        return text.Length == 0 ? null : text;
    }
}

/// <summary>What the exclusive input mask takes, in words, for the bridge's <c>photo status</c>.</summary>
internal static class PhotoInputBlockReport
{
    internal static string[] Collections() => new[]
    {
        "buttons: every ButtonInputAction except ButtonInputActionCollection.UINavigationActions (BlockAllKeys(false))",
        "close: Back (17), Menu_Close (1004)",
        "camera: RotateCamera (500) blocked and not allowed back",
        "menu hotkeys: 18, 20-26, 1006-1009 (both button lists)",
        "analog: All (move, aim, zoom, camera rotate/move, radial, map, menu sticks)",
    };
}

/// <summary>
/// The Dev bridge's <c>photo</c> op. Public only because Core grants <c>Progenitor.Dev</c> no
/// <c>InternalsVisibleTo</c>; not a plugin API. Client side for <c>on</c> / <c>off</c>; <c>status</c>
/// answers on either side (the server reports how many users hold a pause). Main thread.
/// </summary>
public static class PhotoModeBridge
{
    /// <summary>Run <paramref name="action"/> (<c>status</c>, <c>on</c>, <c>off</c>); every action answers with the status after it.</summary>
    public static Dictionary<string, object?> Run(string action, bool enable) => Run(action, enable, 0, 0);

    /// <summary>As above; <c>lock</c> takes the client entity <paramref name="index"/>:<paramref name="version"/>, <c>unlock</c> drops the lock.</summary>
    public static Dictionary<string, object?> Run(string action, bool enable, int index, int version)
    {
        string? refused = null;
        bool? changed = null;
        switch (action)
        {
            case "on":
                refused = PhotoMode.BridgeOn(enable);
                changed = refused == null;
                break;
            case "off":
                changed = PhotoMode.BridgeOff();
                break;
            case "lock":
                refused = PhotoMode.BridgeLock(new InputEntityRef(index, version));
                changed = refused == null;
                break;
            case "unlock":
                changed = PhotoMode.BridgeUnlock();
                break;
        }

        var status = PhotoMode.BridgeStatus();
        status["action"] = action;
        status["ok"] = refused == null;
        status["refused"] = refused;
        status["changed"] = changed;
        return status;
    }
}
#endif
