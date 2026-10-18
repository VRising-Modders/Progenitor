#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using GameKey = UnityEngine.InputSystem.Key;

namespace Progenitor.Ui;

/// <summary>
/// The one place Core reads the keyboard. See <see cref="IKeyInputSource"/> for the metadata
/// evidence and for why this is <c>Keyboard.current</c> rather than
/// <c>UnityEngine.Input.GetKeyDown</c> or a vanilla <c>ButtonInputAction</c>.
///
/// <para>
/// Two things it never does. It never touches <c>InputActionSystem</c>: reading a device control
/// is not a context mutation, so none of <c>_DenyModificationsOfInputContexts</c>' rules apply and
/// this can run on any frame. And it never keeps a <c>KeyControl</c> across frames — the map from
/// our <see cref="UiKey"/> to the game's <c>Key</c> is cached (it is a value), the controls are
/// re-fetched from the live <c>Keyboard.current</c>, because the device is replaced when the
/// player unplugs a keyboard or the Steam overlay re-initializes input.
/// </para>
/// </summary>
internal sealed class UnityKeyInputSource : IKeyInputSource
{
    private static readonly Dictionary<UiKey, GameKey> Map = BuildMap();
    private static readonly UiKey[] Candidates = BuildCandidates();

    /// <summary>KEYS2: the Windows keys, read by the capture only (never a bind's main key: <see cref="ReservedChords"/>).</summary>
    private static readonly UiKey[] MetaKeys = { UiKey.LeftMeta, UiKey.RightMeta };

    /// <summary>
    /// Frames between two <c>Resources.FindObjectsOfTypeAll</c> attempts while no window is
    /// cached. The scan walks every loaded object of the type and allocates an array; on the main
    /// menu and on every loading screen there is legitimately nothing to find, and the old code
    /// paid for that scan at frame rate (R34 finding 16). One attempt per second is fast enough:
    /// the HUD coming up costs at most that long before chat suppression is exact again, and the
    /// gate fails closed in the meantime.
    /// </summary>
    private const int ResolveIntervalFrames = 60;

    private bool _chatWindowMissingLogged;
    private bool _textFieldProbeFailedLogged;
    private bool _edgeOnlyLogged;
    private int _nextResolveFrame;
    private ProjectM.UI.HUDChatWindow? _chatWindow;

    /// <summary>
    /// The press detector. <c>wasPressedThisFrame</c> alone dropped half the presses on this
    /// build — see <see cref="KeyEdgeTracker"/> for the mechanism and evidence 161 — so both
    /// entry points below go through it rather than reading the flag directly.
    /// </summary>
    private readonly KeyEdgeTracker _edges = new();

    /// <summary>KEYS4: the mouse buttons' own edge state (another device, another id).</summary>
    private readonly KeyEdgeTracker _mouseEdges = new();

    /// <summary>
    /// KEYS4: the mouse buttons a capture offers, in this order. KEYS8 added the left and right buttons;
    /// <see cref="Keybinds"/> decides what a click on one means (the click that started the capture, a bare
    /// click on the panel, or a chord).
    /// </summary>
    private static readonly UiKey[] MouseCandidates =
        { UiKey.MouseMiddle, UiKey.MouseBack, UiKey.MouseForward, UiKey.MouseLeft, UiKey.MouseRight };

    /// <summary>
    /// KEYS4: the mouse button behind a <see cref="UiKey"/>: <c>Mouse.middleButton</c>, <c>backButton</c>,
    /// <c>forwardButton</c> and (KEYS8) <c>leftButton</c> / <c>rightButton</c> on <c>Mouse.current</c>, as the
    /// client's metadata lists them; <c>UnityInputRuntime</c> already reads the left and right controls for
    /// user activity. Null for a keyboard key or when there is no mouse.
    /// </summary>
    private static UnityEngine.InputSystem.Controls.ButtonControl? MouseButton(Mouse? mouse, UiKey key)
    {
        if (mouse == null) return null;
        try
        {
            return key switch
            {
                UiKey.MouseMiddle => mouse.middleButton,
                UiKey.MouseBack => mouse.backButton,
                UiKey.MouseForward => mouse.forwardButton,
                UiKey.MouseLeft => mouse.leftButton,
                UiKey.MouseRight => mouse.rightButton,
                _ => null,
            };
        }
        catch { return null; }
    }

    private static Mouse? CurrentMouse()
    {
        try { return Mouse.current; }
        catch { return null; }
    }

    /// <summary>PAD1: the gamepad buttons' own edge state (another device, another id).</summary>
    private readonly KeyEdgeTracker _padEdges = new();

    private static Gamepad? CurrentGamepad()
    {
        try { return Gamepad.current; }
        catch { return null; }
    }

    /// <summary>
    /// PAD1: the control behind a gamepad <see cref="UiKey"/> on <c>Gamepad.current</c>, as the client's metadata lists
    /// them (<c>buttonSouth</c> ... <c>selectButton</c>, <c>dpad.up</c> ...; the triggers are <c>ButtonControl</c>s
    /// with the Input System's press point). Null for any other key or when there is no gamepad. Re-fetched per read:
    /// a pad that is unplugged and plugged back is a new device.
    /// </summary>
    private static UnityEngine.InputSystem.Controls.ButtonControl? PadButton(Gamepad? pad, UiKey key)
    {
        if (pad == null) return null;
        try
        {
            return key switch
            {
                UiKey.GamepadSouth => pad.buttonSouth,
                UiKey.GamepadEast => pad.buttonEast,
                UiKey.GamepadWest => pad.buttonWest,
                UiKey.GamepadNorth => pad.buttonNorth,
                UiKey.GamepadLeftShoulder => pad.leftShoulder,
                UiKey.GamepadRightShoulder => pad.rightShoulder,
                UiKey.GamepadLeftTrigger => pad.leftTrigger,
                UiKey.GamepadRightTrigger => pad.rightTrigger,
                UiKey.GamepadDpadUp => pad.dpad?.up,
                UiKey.GamepadDpadDown => pad.dpad?.down,
                UiKey.GamepadDpadLeft => pad.dpad?.left,
                UiKey.GamepadDpadRight => pad.dpad?.right,
                UiKey.GamepadLeftStick => pad.leftStickButton,
                UiKey.GamepadRightStick => pad.rightStickButton,
                UiKey.GamepadStart => pad.startButton,
                UiKey.GamepadSelect => pad.selectButton,
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>PAD1: every gamepad button held now, as <see cref="KeyChord.GamepadBit"/> bits. 0 when unreadable.</summary>
    private static uint PadHeld(Gamepad pad)
    {
        uint held = 0;
        foreach (var key in KeyChord.GamepadKeys)
        {
            var button = PadButton(pad, key);
            try
            {
                if (button != null && button.isPressed) held |= KeyChord.GamepadBit(key);
            }
            catch { /* a control read mid-teardown is not held */ }
        }

        return held;
    }

    public bool GamepadPresent => CurrentGamepad() != null;

    public bool TryReadGamepadHeld(out uint held)
    {
        held = 0;
        var pad = CurrentGamepad();
        if (pad == null) return false;
        held = PadHeld(pad);
        return true;
    }

    public bool IsAvailable
    {
        get
        {
            try { return Keyboard.current != null; }
            catch { return false; }
        }
    }

    public ChatFocus ChatFocus
    {
        get
        {
            var cached = _chatWindow;
            if (cached != null)
            {
                try { return cached.IsChatFocused ? ChatFocus.Focused : ChatFocus.NotFocused; }
                catch
                {
                    // The window was destroyed under us (world change, leave to menu). Drop it and
                    // fall through to one re-find; an unreadable window is Unknown, never false.
                    _chatWindow = null;
                }
            }

            var found = ResolveChatWindow();
            if (found == null) return ChatFocus.Unknown;
            try { return found.IsChatFocused ? ChatFocus.Focused : ChatFocus.NotFocused; }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: HUDChatWindow.IsChatFocused read threw: " + e.Message);
                _chatWindow = null;
                return ChatFocus.Unknown;
            }
        }
    }

    /// <summary>
    /// The generic "the player is typing somewhere" probe (R36 finding 5). See
    /// <see cref="IKeyInputSource.TextFieldFocused"/> for why the chat probe is not enough and for
    /// the metadata evidence.
    ///
    /// <para>
    /// The read is three hops and no scan: <c>EventSystem.current</c> (a static), its
    /// <c>currentSelectedGameObject</c>, and a <c>GetComponent</c> for
    /// <c>TMP_InputField</c> / <c>UnityEngine.UI.InputField</c> on that one object. Both component
    /// types are in the shipped interop (<c>BepInEx/interop/Unity.TextMeshPro.dll</c>,
    /// <c>UnityEngine.UI.dll</c>) and <c>UnityUiRuntime</c> already builds and reads a
    /// <c>TMP_InputField</c> through them, so this path is proven on this build. There is no
    /// <c>Resources.FindObjectsOfTypeAll</c> here and no cache: the selection changes per click,
    /// and one component lookup per polled frame is cheaper than the chat window's scan.
    /// </para>
    ///
    /// <para>
    /// Selection alone is not focus — a button stays selected after a click, and a field can be
    /// selected without an active caret — so the component's own <c>isFocused</c> is what decides.
    /// A selected object with neither component is not a text field and answers false.
    /// </para>
    /// </summary>
    public bool TextFieldFocused
    {
        get
        {
            try
            {
                var system = UnityEngine.EventSystems.EventSystem.current;
                if (system == null) return false;

                var selected = system.currentSelectedGameObject;
                if (selected == null) return false;

                var tmp = selected.GetComponent<TMPro.TMP_InputField>();
                if (tmp != null) return tmp.isFocused;

                var legacy = selected.GetComponent<UnityEngine.UI.InputField>();
                if (legacy != null) return legacy.isFocused;

                return false;
            }
            catch (Exception e)
            {
                // Answering false rather than true is deliberate: a read that throws every frame
                // would suppress every bind for the session, the settings opener included, which
                // is the failure R35 finding 5 was about. The chat gate keeps the fail-closed
                // guarantee in a world; this probe is the extra one for the states it cannot see.
                if (!_textFieldProbeFailedLogged)
                {
                    _textFieldProbeFailedLogged = true;
                    Plugin.Logger?.LogWarning(
                        "Ui: the focused-text-field probe threw (" + e.Message
                        + "); keybinds are no longer suppressed while a game text field has focus. "
                        + "Chat suppression is unaffected. Logged once per source.");
                }

                return false;
            }
        }
    }

    public bool WasPressedThisFrame(KeyChord chord, out bool refusedAsAltGr)
    {
        refusedAsAltGr = false;
        if (!chord.IsBound) return false;
        if (chord.IsGamepad)
        {
            // PAD1: a gamepad button, with exactly the chord's held button (KeyChord.MatchesGamepadHeld).
            var pad = CurrentGamepad();
            var padButton = PadButton(pad, chord.Key);
            if (padButton == null || !WentDown(pad!, _padEdges, chord.Key, padButton)) return false;
            return chord.MatchesGamepadHeld(PadHeld(pad!));
        }

        var keyboard = Keyboard.current;
        if (KeyChord.IsMouseKey(chord.Key))
        {
            // KEYS4: a mouse button, with the keyboard's modifiers (none when there is no keyboard).
            var mouse = CurrentMouse();
            var button = MouseButton(mouse, chord.Key);
            if (button == null || !WentDown(mouse!, _mouseEdges, chord.Key, button)) return false;
            var held = keyboard != null ? HeldSides(keyboard) : ModifierSides.None;
            return chord.MatchesHeld(held);
        }

        if (keyboard == null) return false;

        // HOLDMOD1: a lone modifier goes down on either side's key, and fires when it is the only modifier held
        // (KeyChord.MatchesHeld). Both sides are sampled every time so each key's edge state stays contiguous.
        if (chord.IsLoneModifier)
        {
            bool left = SideWentDown(keyboard, KeyChord.LeftKeyOf(chord.LoneFlag));
            bool right = SideWentDown(keyboard, KeyChord.RightKeyOf(chord.LoneFlag));
            return (left || right) && chord.MatchesHeld(HeldSides(keyboard));
        }

        if (!Map.TryGetValue(chord.Key, out var key)) return false;

        var control = keyboard[key];
        if (control == null || !WentDown(keyboard, _edges, chord.Key, control)) return false;

        var sides = HeldSides(keyboard);
        if (chord.MatchesHeld(sides)) return true;

        // The key went down and the side-agnostic modifiers were right, so the only rule that can
        // have refused it is the AltGr pair. Say so through the out parameter rather than letting
        // the caller see a bare false it cannot explain (R38 finding 4).
        refusedAsAltGr = chord.IsAltGrShaped && KeyChord.IsAltGrPair(sides)
            && KeyChord.ModifiersOf(sides) == chord.Modifiers;
        return false;
    }

    /// <summary>KEYS1: the main key's live held state (<c>ButtonControl.isPressed</c>), for while-held and release handlers.</summary>
    public bool IsHeld(KeyChord chord)
    {
        if (!chord.IsBound) return false;
        try
        {
            if (chord.IsGamepad)
            {
                var padButton = PadButton(CurrentGamepad(), chord.Key);
                return padButton != null && padButton.isPressed;
            }

            if (KeyChord.IsMouseKey(chord.Key))
            {
                var button = MouseButton(CurrentMouse(), chord.Key);
                return button != null && button.isPressed;
            }

            var keyboard = Keyboard.current;
            if (keyboard == null) return false;
            // HOLDMOD1: a lone modifier is held while either side's key is down.
            if (chord.IsLoneModifier)
                return SideHeld(keyboard, KeyChord.LeftKeyOf(chord.LoneFlag)) || SideHeld(keyboard, KeyChord.RightKeyOf(chord.LoneFlag));
            if (!Map.TryGetValue(chord.Key, out var key)) return false;
            var control = keyboard[key];
            return control != null && control.isPressed;
        }
        catch { return false; }
    }

    /// <summary>HOLDMOD1: one modifier key's press edge, through the same tracker as every other key.</summary>
    private bool SideWentDown(Keyboard keyboard, UiKey side)
    {
        if (!Map.TryGetValue(side, out var key)) return false;
        var control = keyboard[key];
        return control != null && WentDown(keyboard, _edges, side, control);
    }

    private static bool SideHeld(Keyboard keyboard, UiKey side)
    {
        if (!Map.TryGetValue(side, out var key)) return false;
        var control = keyboard[key];
        return control != null && control.isPressed;
    }

    /// <summary>
    /// HOLDMOD1: the six modifier keys and the Windows keys held now, and those whose press edge
    /// (<see cref="KeyEdgeTracker"/>, the Input System's own flag included) is on this frame, for the key capture's
    /// lone-modifier rule (press and release Alt alone records Alt, even within one frame). False when there is no
    /// keyboard.
    /// </summary>
    public bool TryReadModifiers(out ModifierSides held, out ModifierSides pressed)
    {
        held = ModifierSides.None;
        pressed = ModifierSides.None;
        Keyboard? keyboard;
        try { keyboard = Keyboard.current; }
        catch { return false; }
        if (keyboard == null) return false;
        held = HeldSides(keyboard);
        foreach (var (key, side) in ModifierEdgeKeys)
        {
            try { if (SideWentDown(keyboard, key)) pressed |= side; }
            catch { /* a control read mid-teardown is not a press */ }
        }

        return true;
    }

    /// <summary>PICKMOD1: the modifier keys held now, held state only (no edge is read).</summary>
    public bool TryReadHeldModifiers(out ModifierSides held)
    {
        held = ModifierSides.None;
        Keyboard? keyboard;
        try { keyboard = Keyboard.current; }
        catch { return false; }
        if (keyboard == null) return false;
        try { held = HeldSides(keyboard); }
        catch { return false; }
        return true;
    }

    private static readonly (UiKey Key, ModifierSides Side)[] ModifierEdgeKeys =
    {
        (UiKey.LeftCtrl, ModifierSides.LeftCtrl), (UiKey.RightCtrl, ModifierSides.RightCtrl),
        (UiKey.LeftShift, ModifierSides.LeftShift), (UiKey.RightShift, ModifierSides.RightShift),
        (UiKey.LeftAlt, ModifierSides.LeftAlt), (UiKey.RightAlt, ModifierSides.RightAlt),
        (UiKey.LeftMeta, ModifierSides.LeftMeta), (UiKey.RightMeta, ModifierSides.RightMeta),
    };

    /// <summary>HOLDMOD1 review: <c>Application.isFocused</c>; null when it cannot be read.</summary>
    public bool? WindowFocused
    {
        get
        {
            try { return UnityEngine.Application.isFocused; }
            catch { return null; }
        }
    }

    public bool TryReadPressedChord(out KeyChord chord, out ModifierSides sides)
    {
        chord = KeyChord.None;
        sides = ModifierSides.None;
        var keyboard = Keyboard.current;
        if (keyboard == null) return false;

        for (int i = 0; i < Candidates.Length; i++)
        {
            var candidate = Candidates[i];
            if (!Map.TryGetValue(candidate, out var key)) continue;
            var control = keyboard[key];
            if (control == null || !WentDown(keyboard, _edges, candidate, control)) continue;

            // The physical sides go out with the chord, refusable or not. This used to `continue`
            // past an AltGr-shaped press, which left the panel in "press a key" with nothing
            // logged — the truthful outcome for an AltGr keystroke and a broken panel for the
            // US-layout player holding LeftCtrl+RightAlt on purpose (R38 finding 4). Keybinds
            // decides now, and tells the player either way.
            sides = HeldSides(keyboard);
            chord = new KeyChord(candidate, KeyChord.ModifiersOf(sides));
            return true;
        }

        // KEYS2: the Windows key on its own is not a candidate (it is a modifier), but a capture must
        // still see it, so the player is told why it cannot be used instead of the capture ignoring it.
        foreach (var meta in MetaKeys)
        {
            if (!Map.TryGetValue(meta, out var key)) continue;
            var control = keyboard[key];
            if (control == null || !WentDown(keyboard, _edges, meta, control)) continue;
            sides = HeldSides(keyboard);
            chord = new KeyChord(meta, KeyChord.ModifiersOf(sides));
            return true;
        }

        // KEYS4: the mouse's buttons can be the new key. KEYS8: left and right too; Keybinds ignores the click
        // that started the capture and refuses a bare click, which stays the panel's.
        var mouse = CurrentMouse();
        foreach (var candidate in MouseCandidates)
        {
            var button = MouseButton(mouse, candidate);
            if (button == null || !WentDown(mouse!, _mouseEdges, candidate, button)) continue;
            sides = HeldSides(keyboard);
            chord = new KeyChord(candidate, KeyChord.ModifiersOf(sides));
            return true;
        }

        return false;
    }

    /// <summary>
    /// The one press test both entry points use. Reads the control's live held state and the
    /// Input System's own edge flag, hands both to <see cref="KeyEdgeTracker"/>, and lets it
    /// decide — so a press whose input update step we never sampled is still seen exactly once.
    ///
    /// <para>
    /// The device id goes in first: held state carried across a keyboard swap would make the
    /// tracker's contiguity test compare readings from two different devices.
    /// </para>
    /// </summary>
    private bool WentDown(InputDevice device, KeyEdgeTracker edges, UiKey uiKey, UnityEngine.InputSystem.Controls.ButtonControl control)
    {
        int frame;
        bool frameKnown;
        if (Plugin.TryDispatchFrame(out frame))
        {
            // Inside Core's frame dispatch (the keybind pump): the frame Core already read, not a
            // fresh Time.frameCount per key (each read boxes an int on the IL2CPP heap).
            frameKnown = true;
        }
        else
        {
            try
            {
                frame = UnityEngine.Time.frameCount;
                frameKnown = true;
            }
            catch
            {
                frame = 0;
                frameKnown = false;
            }
        }

        bool held, raw;
        try
        {
            held = control.isPressed;
            raw = control.wasPressedThisFrame;
        }
        catch
        {
            // A control read mid-teardown is not a press. Same answer as the old code gave.
            return false;
        }

        // R43 finding 2. A fabricated frame number must never reach the tracker. It caches one
        // verdict per key per frame, so feeding it the same made-up number on every call makes
        // that cache permanent: whatever the first failed read decided is returned forever, for
        // every key, ignoring even the Input System's own flag — every keybind silently dead (or
        // silently firing) for the rest of the session. An unreadable frame is instead a frame we
        // cannot reason about, so this falls back to exactly what the code did before the tracker
        // existed: trust the raw flag alone, and leave the tracker's state untouched.
        if (!frameKnown) return raw;

        try { edges.NoteDevice(DeviceId(device)); }
        catch { /* An unreadable id keeps whatever state we have; worst case is one edge. */ }

        bool down = edges.Observe(uiKey, frame, held, raw);
        if (down && !raw) NoteEdgeOnly();
        return down;
    }

    private IntPtr _deviceIdPtr;
    private int _deviceId;
    private IntPtr _deviceIdPtr2;
    private int _deviceId2;
    private IntPtr _deviceIdPtr3;
    private int _deviceId3;

    /// <summary>
    /// <c>InputDevice.deviceId</c>, cached per device object (keyboard and mouse: two slots).
    /// <c>deviceId</c> boxes its int return on the IL2CPP heap and the pump asked once per bind per
    /// frame. A different device object (a keyboard swap) has a different pointer and is read
    /// afresh, which is all <see cref="KeyEdgeTracker.NoteDevice"/> needs to see.
    /// </summary>
    private int DeviceId(InputDevice device)
    {
        IntPtr ptr = device.Pointer;
        if (ptr != IntPtr.Zero)
        {
            if (ptr == _deviceIdPtr) return _deviceId;
            if (ptr == _deviceIdPtr2) return _deviceId2;
            if (ptr == _deviceIdPtr3) return _deviceId3;
        }
        int id = device.deviceId;
        if (ptr == IntPtr.Zero) return id;
        // Three slots, oldest replaced: keyboard, mouse and (PAD1) gamepad alternate without evicting each other.
        _deviceIdPtr3 = _deviceIdPtr2;
        _deviceId3 = _deviceId2;
        _deviceIdPtr2 = _deviceIdPtr;
        _deviceId2 = _deviceId;
        _deviceIdPtr = ptr;
        _deviceId = id;
        return id;
    }

    /// <summary>
    /// Say once, with the setting that explains it, that a press had to be recovered from the
    /// held state. Silent on a build where the pump and the input update step agree — so the
    /// line's presence is the evidence that they do not.
    /// </summary>
    private void NoteEdgeOnly()
    {
        if (_edgeOnlyLogged) return;
        _edgeOnlyLogged = true;

        string mode;
        try { mode = InputSystem.settings.updateMode.ToString(); }
        catch (Exception e) { mode = "unreadable (" + e.Message + ")"; }

        Plugin.Logger?.LogInfo(
            "Ui: a keybind press arrived with wasPressedThisFrame already false and was recovered "
            + "from the key's held state (Input System updateMode=" + mode + "). The pump samples "
            + "once per rendered frame and the input update step advanced more than once between "
            + "two samples, so the step that saw the press was never observed. Without the "
            + "recovery that press is dropped, which is the 'press the key twice' bug "
            + "(evidence 161). Logged once per source.");
    }

    /// <summary>
    /// The six side-specific modifier controls, read live off <c>Keyboard.current</c>:
    /// <c>leftCtrlKey</c> / <c>rightCtrlKey</c> / <c>leftShiftKey</c> / <c>rightShiftKey</c> /
    /// <c>leftAltKey</c> / <c>rightAltKey</c>, through <c>ButtonControl.isPressed</c>.
    ///
    /// <para>
    /// This replaced the three side-agnostic <c>ctrlKey</c> / <c>shiftKey</c> / <c>altKey</c>
    /// summary controls because those cannot tell
    /// <c>LeftCtrl+RightAlt</c> — which is what Windows delivers when a player presses
    /// <c>AltGr</c> — from a deliberate <c>Ctrl+Alt</c>, and there is no separate AltGr control to
    /// consult: <c>Key.AltGr</c> is declared as an alias of <c>rightAlt</c> on the same bit 54.
    /// R37 finding 1.
    /// </para>
    /// </summary>
    private static ModifierSides HeldSides(Keyboard keyboard)
    {
        var held = ModifierSides.None;
        try
        {
            if (keyboard.leftCtrlKey != null && keyboard.leftCtrlKey.isPressed) held |= ModifierSides.LeftCtrl;
            if (keyboard.rightCtrlKey != null && keyboard.rightCtrlKey.isPressed) held |= ModifierSides.RightCtrl;
            if (keyboard.leftShiftKey != null && keyboard.leftShiftKey.isPressed) held |= ModifierSides.LeftShift;
            if (keyboard.rightShiftKey != null && keyboard.rightShiftKey.isPressed) held |= ModifierSides.RightShift;
            if (keyboard.leftAltKey != null && keyboard.leftAltKey.isPressed) held |= ModifierSides.LeftAlt;
            if (keyboard.rightAltKey != null && keyboard.rightAltKey.isPressed) held |= ModifierSides.RightAlt;
            // KEYS2: the Windows keys, so a Win+X shortcut is never read as X (leftMetaKey / rightMetaKey
            // in the client metadata).
            if (keyboard.leftMetaKey != null && keyboard.leftMetaKey.isPressed) held |= ModifierSides.LeftMeta;
            if (keyboard.rightMetaKey != null && keyboard.rightMetaKey.isPressed) held |= ModifierSides.RightMeta;
        }
        catch
        {
            // A device mid-teardown: treat as no modifiers rather than fire nothing forever.
        }

        return held;
    }

    /// <summary>
    /// <c>ProjectM.UI.HUDChatWindow</c> is a scene MonoBehaviour with no static accessor, so it is
    /// found once and cached; a destroyed instance is re-found. The "not found" line is logged at
    /// most once per source, because on the main menu it is legitimately absent for minutes.
    ///
    /// <para>
    /// Two rules the first version did not have (R34 findings 7 and 16).
    /// <b>Assets are not instances:</b> <c>Resources.FindObjectsOfTypeAll</c> returns prefabs and
    /// editor-hidden objects as well as scene objects, and a prefab's <c>IsChatFocused</c> reads
    /// false forever — caching one turns the chat gate permanently off. Anything whose GameObject
    /// is not in a loaded scene, or is hidden in the hierarchy, is skipped.
    /// <b>The scan is rate-limited:</b> at most one attempt every
    /// <see cref="ResolveIntervalFrames"/> frames while nothing is cached, so the main menu and
    /// loading screens do not pay for a full type scan per frame.
    /// </para>
    /// </summary>
    private ProjectM.UI.HUDChatWindow? ResolveChatWindow()
    {
        int frame;
        try { frame = UnityEngine.Time.frameCount; }
        catch { frame = 0; }

        if (frame != 0 && frame < _nextResolveFrame) return null;
        _nextResolveFrame = frame + ResolveIntervalFrames;

        ProjectM.UI.HUDChatWindow? found = null;
        try
        {
            var type = Il2CppInterop.Runtime.Il2CppType.Of<ProjectM.UI.HUDChatWindow>();
            if (type == null) return null;
            var all = UnityEngine.Resources.FindObjectsOfTypeAll(type);
            if (all == null) return null;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                var window = all[i].TryCast<ProjectM.UI.HUDChatWindow>();
                if (window == null) continue;
                if (!IsSceneInstance(window)) continue;
                found = window;
                break;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: HUDChatWindow lookup threw: " + e.Message);
            return null;
        }

        if (found == null)
        {
            if (!_chatWindowMissingLogged)
            {
                _chatWindowMissingLogged = true;
                Plugin.Logger?.LogInfo(
                    "Ui: no HUDChatWindow yet (main menu or loading); keybinds are not suppressed for chat "
                    + "until one exists. It is looked up again on later frames.");
            }

            return null;
        }

        _chatWindow = found;
        return found;
    }

    /// <summary>
    /// True only for a live scene object: a prefab or an asset has an invalid
    /// <c>GameObject.scene</c> (no name, no build index, <c>IsValid()</c> false), and an object
    /// the editor or the loader hid is not one the player can type into either. A read that
    /// throws answers false, so an object we cannot classify is never cached.
    /// </summary>
    private static bool IsSceneInstance(ProjectM.UI.HUDChatWindow window)
    {
        try
        {
            var go = window.gameObject;
            if (go == null) return false;
            if ((go.hideFlags & UnityEngine.HideFlags.HideInHierarchy) != 0) return false;
            if ((go.hideFlags & UnityEngine.HideFlags.HideAndDontSave) == UnityEngine.HideFlags.HideAndDontSave)
                return false;
            return go.scene.IsValid();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: HUDChatWindow scene check threw: " + e.Message);
            return false;
        }
    }

    private static Dictionary<UiKey, GameKey> BuildMap()
    {
        var map = new Dictionary<UiKey, GameKey>();
        foreach (UiKey key in Enum.GetValues(typeof(UiKey)))
        {
            if (key == UiKey.None) continue;
            // By name, never by number: our enum's numbers are ours (see UiKey).
            if (Enum.TryParse(key.ToString(), ignoreCase: false, out GameKey game))
                map[key] = game;
        }

        return map;
    }

    private static UiKey[] BuildCandidates()
    {
        var list = new List<UiKey>();
        foreach (UiKey key in Enum.GetValues(typeof(UiKey)))
        {
            if (key == UiKey.None) continue;
            if (KeyChord.IsModifierKey(key)) continue;
            // KEYS4: the mouse buttons are read from the mouse, after the keyboard.
            if (KeyChord.IsMouseKey(key)) continue;
            // PAD1: the gamepad buttons are read from the gamepad, and only by the gamepad capture.
            if (KeyChord.IsGamepadKey(key)) continue;
            list.Add(key);
        }

        return list.ToArray();
    }
}
#endif
