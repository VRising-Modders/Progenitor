using System;
using System.Threading;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Progenitor.Ui;

/// <summary>
/// Bootstrap-owned injected type that captures keys while a mod panel is shown.
/// FullName is <c>Progenitor.Ui.UiInputContext</c> and must stay that forever:
/// <c>ClassInjector.RegisterTypeInIl2Cpp</c> has no unregister and keys by FullName
/// (same rule as <c>Progenitor.Ticks.FrameTicker</c>).
///
/// The game's <c>IInputContext</c> is an Il2CppObjectBase class with
/// <c>GetConsumedInputs(ref BlockInputState)</c> — a by-ref struct that contains
/// NativeList fields. ClassInjector cannot implement that IL2CPP interface from
/// managed code (the interop wrapper is a class, not a C# interface, and a native
/// call into a managed override would see a zeroed BlockInputState). Fallback:
/// unlock the cursor (Menu_Normal equivalent; SetCursorSystem has no setter) and
/// forward Escape / Back from <c>Keyboard.current</c> to a Core slot. Gameplay
/// keys are not consumed; the camera still moves until a later seam exists.
/// </summary>
public sealed class UiInputContext : MonoBehaviour
{
    /// <summary>Core key handler. <paramref name="keyCode"/> is Unity <c>KeyCode</c> or <see cref="BackAction"/>.</summary>
    public delegate void KeyDispatch(int keyCode, bool down);

    /// <summary><c>ProjectM.ButtonInputAction.Back</c> (vanilla close / ESC).</summary>
    public const int BackAction = 17;

    public UiInputContext(IntPtr ptr) : base(ptr) { }

    private static bool _registered;
    private static bool _limitationLogged;
    private static KeyDispatch? _core;
    private static int _capture;
    private static CursorLockMode _savedLock = CursorLockMode.Locked;
    private static bool _savedVisible;
    private static bool _haveSavedCursor;

    /// <summary>
    /// Job FUI16 (R43 finding 1). Escape's press edge, derived from held state instead of read off
    /// <c>wasPressedThisFrame</c>. Static because it is Bootstrap state: a <c>.corereload</c>
    /// replaces the Core-side tracker and must not take Escape's sampling history with it. See
    /// <see cref="KeyPressEdge"/> for why this is not Core's <c>KeyEdgeTracker</c>.
    /// </summary>
    private static readonly Progenitor.Bootstrap.KeyPressEdge EscapeEdge = new();

    private static bool _edgeOnlyLogged;

    internal static void Install(BepInEx.Unity.IL2CPP.BasePlugin owner)
    {
        try
        {
            if (!_registered && !ClassInjector.IsTypeRegisteredInIl2Cpp<UiInputContext>())
                ClassInjector.RegisterTypeInIl2Cpp<UiInputContext>();
            _registered = true;
            owner.AddComponent<UiInputContext>();
            LogLimitationOnce();
            Progenitor.Bootstrap.ProcessHost.Log?.LogInfo("UiInputContext: injected (cursor-unlock fallback; IInputContext not implementable).");
        }
        catch (Exception e)
        {
            Progenitor.Bootstrap.ProcessHost.Log?.LogError("UiInputContext: install failed: " + e);
        }
    }

    /// <summary>Point the key trampoline at Core (or null it). KeepDelegate under the Core generation.</summary>
    public static bool TrySetKeyDispatch(Delegate? dispatcher)
    {
        try
        {
            KeyDispatch? bound = null;
            if (dispatcher != null)
            {
                bound = dispatcher is KeyDispatch typed
                    ? typed
                    : (KeyDispatch)Delegate.CreateDelegate(typeof(KeyDispatch), dispatcher.Target, dispatcher.Method);
                Progenitor.Bootstrap.NativePinRegistry.KeepDelegate(
                    bound, "ui.keys", Progenitor.Bootstrap.CoreLoader.CurrentPinGeneration);
            }

            Volatile.Write(ref _core, bound);
            return true;
        }
        catch (Exception e)
        {
            Progenitor.Bootstrap.ProcessHost.Log?.LogWarning("UiInputContext: TrySetKeyDispatch threw: " + e.Message);
            return false;
        }
    }

    public static void ClearKeyDispatch() => TrySetKeyDispatch(null);

    /// <summary>
    /// Enable or disable capture. True unlocks the cursor and starts forwarding Escape;
    /// false restores the cursor snapshotted on this capture's 0→1 transition (not the
    /// first capture of the process). Nested Show calls from different panels are counted.
    /// </summary>
    public static void SetCapture(bool capture)
    {
        try
        {
            if (capture)
            {
                int n = Interlocked.Increment(ref _capture);
                if (n == 1) ApplyCursorUnlock();
            }
            else
            {
                int n = Interlocked.Decrement(ref _capture);
                if (n <= 0)
                {
                    Volatile.Write(ref _capture, 0);
                    RestoreCursor();
                }
            }
        }
        catch (Exception e)
        {
            Progenitor.Bootstrap.ProcessHost.Log?.LogWarning("UiInputContext: SetCapture(" + capture + ") threw: " + e.Message);
        }
    }

    public static bool IsCapturing => Volatile.Read(ref _capture) > 0;

    /// <summary>Force capture off (Core Uninstall). Restores the cursor.</summary>
    public static void ClearCapture()
    {
        Volatile.Write(ref _capture, 0);
        RestoreCursor();
    }

    public static string DescribePins() =>
        "pin-diag UiInputContext capture=" + (IsCapturing ? "True" : "False")
        + " keys=" + (Volatile.Read(ref _core) != null ? "True" : "False")
        // Job FUI16: nonzero means Escape presses are arriving with the Input System's flag
        // already false and are only reaching Core because the edge is derived (evidence 161).
        + " escapeEdgeOnly=" + EscapeEdge.EdgeOnlyPresses;

    private void Update()
    {
        // TEXTINPUT1: keep the typed-character queue subscribed to the current keyboard. Cheap
        // when nothing changed (one device-id compare); runs whether or not a panel is shown.
        try { Progenitor.Bootstrap.TextInputQueue.Pump(); } catch { /* the physical-key map remains */ }
        if (Volatile.Read(ref _capture) <= 0) return;
        // _core is nulled on Core Uninstall / NullRuntime. A leftover Update of the
        // teardown frame (or the flush frame) must not call into collectible Core.
        if (Volatile.Read(ref _core) == null) return;
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (EscapeWentDown(kb))
            {
                InvokeKey((int)KeyCode.Escape, down: true);
                InvokeKey(BackAction, down: true);
            }
        }
        catch (Exception e)
        {
            Progenitor.Bootstrap.ProcessHost.Log?.LogWarning("UiInputContext: Update threw: " + e.Message);
        }
    }

    /// <summary>
    /// Job FUI16 (R43 finding 1, research evidence 161). Escape's one press test. Reads the
    /// control's held state and the Input System's edge flag and lets <see cref="KeyPressEdge"/>
    /// decide, so a press whose input update step this <c>Update</c> never sampled is still seen
    /// exactly once. The bare <c>escapeKey.wasPressedThisFrame</c> this replaced dropped about
    /// half of them, which is why closing a panel took two presses of Escape on the panel that
    /// already opened on one press of F10.
    ///
    /// <para>
    /// Every read is guarded: this runs in a MonoBehaviour <c>Update</c> in an IL2CPP process, so
    /// a throw here is a per-frame log flood. A control that cannot be read is not a press, which
    /// is the answer the old code gave too.
    /// </para>
    /// </summary>
    private static bool EscapeWentDown(Keyboard keyboard)
    {
        // An unreadable frame number is *not* frame zero: a fabricated number fed to the
        // contiguity test would either fake adjacency or latch. NoFrame makes the tracker fall
        // back to the raw flag alone, which is never worse than the pre-FUI16 behavior.
        int frame;
        try { frame = Time.frameCount; }
        catch { frame = Progenitor.Bootstrap.KeyPressEdge.NoFrame; }

        bool held, raw;
        try
        {
            var escape = keyboard.escapeKey;
            if (escape == null) return false;
            held = escape.isPressed;
            raw = escape.wasPressedThisFrame;
        }
        catch
        {
            return false;
        }

        // Held state carried across a keyboard swap would make the contiguity test compare
        // readings from two different devices.
        try { EscapeEdge.NoteDevice(keyboard.deviceId); }
        catch { /* An unreadable id keeps whatever state we have; worst case is one edge. */ }

        bool down = EscapeEdge.Observe(frame, held, raw);
        if (down && !raw) NoteEdgeOnlyOnce();
        return down;
    }

    /// <summary>
    /// Say once, with the setting that explains it, that Escape had to be recovered from held
    /// state. Silent on a build where the rendered frame and the input update step agree, so the
    /// line's presence is the evidence that they do not. It names <c>escape</c> because
    /// <c>UnityKeyInputSource</c> logs the same recovery for keybinds; the two must be
    /// distinguishable in one log.
    /// </summary>
    private static void NoteEdgeOnlyOnce()
    {
        if (_edgeOnlyLogged) return;
        _edgeOnlyLogged = true;

        string mode;
        try { mode = InputSystem.settings.updateMode.ToString(); }
        catch (Exception e) { mode = "unreadable (" + e.Message + ")"; }

        Progenitor.Bootstrap.ProcessHost.Log?.LogInfo(
            "UiInputContext: an escape press arrived with wasPressedThisFrame already false and was "
            + "recovered from the key's held state (Input System updateMode=" + mode + "). This "
            + "Update samples once per rendered frame and the input update step advanced more than "
            + "once between two samples, so the step that saw the press was never observed. Without "
            + "the recovery that press is dropped, which is the 'press escape twice to close the "
            + "panel' half of evidence 161. Logged once per process.");
    }

    private static void InvokeKey(int keyCode, bool down)
    {
        var core = Volatile.Read(ref _core);
        if (core == null) return;
        try { core(keyCode, down); }
        catch (Exception e)
        {
            Progenitor.Bootstrap.ProcessHost.Log?.LogWarning("UiInputContext: key " + keyCode + " threw: " + e.Message);
        }
    }

    private static void ApplyCursorUnlock()
    {
        try
        {
            _savedLock = Cursor.lockState;
            _savedVisible = Cursor.visible;
            _haveSavedCursor = true;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        catch (Exception e)
        {
            Progenitor.Bootstrap.ProcessHost.Log?.LogWarning("UiInputContext: cursor unlock threw: " + e.Message);
        }
    }

    private static void RestoreCursor()
    {
        try
        {
            if (!_haveSavedCursor) return;
            Cursor.lockState = _savedLock;
            Cursor.visible = _savedVisible;
            _haveSavedCursor = false;
        }
        catch (Exception e)
        {
            Progenitor.Bootstrap.ProcessHost.Log?.LogWarning("UiInputContext: cursor restore threw: " + e.Message);
        }
    }

    private static void LogLimitationOnce()
    {
        if (_limitationLogged) return;
        _limitationLogged = true;
        Progenitor.Bootstrap.ProcessHost.Log?.LogInfo(
            "UiInputContext: IInputContext is an Il2CppObjectBase class with GetConsumedInputs(ref BlockInputState); "
            + "ClassInjector cannot implement it from managed code. Falling back to Cursor.unlock + Keyboard.current Escape. "
            + "Gameplay keys are not consumed (camera still moves). BlockInputState.BlockAllKeys is only valid inside GetConsumedInputs.");
    }
}
