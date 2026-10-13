using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using Progenitor.Ui;

#if !PROGENITOR_TESTS
using System.Runtime.InteropServices;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using GameKey = UnityEngine.InputSystem.Key;
#endif

namespace Progenitor.Dev.Ops;

/// <summary>
/// <c>key</c>: pass keystrokes into the game's own input, focus-free, so the game (and Progenitor's
/// keybinds) react as if the player pressed them. Client only, main thread. Dev bridge only: this is a
/// test tool, and it writes input, it never reads keys for an action of its own.
///
/// <para><b>How.</b> Each change of the held set is queued as one whole-keyboard <c>StateEvent</c> (format
/// <c>KEYS</c>) for <c>Keyboard.current</c> with <c>InputSystem.QueueEvent(InputEventPtr)</c>,
/// the non-generic entry the Input System's own <c>QueueStateEvent&lt;T&gt;</c> ends in.
/// The event is built in unmanaged memory (<see cref="KeyEventBytes"/>); <c>QueueEvent</c> copies it into the
/// native event buffer, and the next input update applies it like a device report: controls, actions, the
/// game's <c>InputActionSystem</c> and Core's keybind pump all see it. <c>type</c> queues <c>TextEvent</c>s, which
/// reach <c>Keyboard.onTextInput</c> listeners (the UI kit's fields). No OS input (SendInput/PostMessage) and no
/// generic interop instantiation.</para>
///
/// <para><b>Releases.</b> Every press is matched by a release (<see cref="KeyHoldLedger"/>): <c>tap</c>/<c>hold</c>
/// release after <c>ms</c> and reply after the release; <c>down</c> releases on <c>up</c>, <c>release</c> or after
/// its <c>ms</c> (default 10 s); Dev unload and a Core swap release everything.</para>
///
/// <para><b>Focus.</b> The Input System's default <c>InputSettings.backgroundBehavior</c>
/// (<c>ResetAndDisableNonBackgroundDevices</c>) disables and resets the keyboard while the window is in the
/// background, and a disabled device's events are dropped. The op reads the window focus, the setting and the
/// keyboard's <c>enabled</c>/<c>canRunInBackground</c>, refuses a press it knows will be dropped, and says so.
/// <c>background:true</c> opts in to switching the setting to <c>IgnoreFocus</c> and enabling the keyboard for the
/// op's duration (restored when the last held key is released). Not live-verified.</para>
/// </summary>
internal static class KeyInjectOps
{
    internal const string ClientOnlyMessage = "key runs on the client only: the server has no keyboard.";

#if PROGENITOR_TESTS
    internal static object Run(JsonElement args)
    {
        KeyInjectPlan.Parse(args);
        throw new InvalidOperationException(ClientOnlyMessage);
    }

    internal static void Shutdown() { }
#else
    private static readonly KeyHoldLedger Ledger = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object TickOwner = new();

    /// <summary>Keys seen pressed on the device while held (the readback that the event was applied).</summary>
    private static readonly HashSet<UiKey> Reached = new();

    private static int _deviceId = -1;
    private static bool _tickScheduled;

    /// <summary>The <c>backgroundBehavior</c> to restore, when <c>background:true</c> changed it.</summary>
    private static InputSettings.BackgroundBehavior? _savedBehavior;

    private static long NowMs => Clock.ElapsedMilliseconds;

    internal static object Run(JsonElement args)
    {
        var request = KeyInjectPlan.Parse(args);
        if (Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException(ClientOnlyMessage);

        var reply = new Dictionary<string, object?>(StringComparer.Ordinal) { ["request"] = request.Describe() };
        switch (request.Action)
        {
            case KeyInjectPlan.Action.Status:
                reply["ok"] = true;
                reply["input"] = Environment();
                reply["held"] = KeyInjectPlan.Join(Ledger.Held);
                return reply;
            case KeyInjectPlan.Action.Release:
            {
                var up = ReleaseAndQueue(Ledger.ReleaseAll(), "release");
                reply["ok"] = true;
                reply["released"] = KeyInjectPlan.Join(up);
                return reply;
            }
            case KeyInjectPlan.Action.Up:
            {
                var notHeld = new List<UiKey>();
                foreach (var k in request.Keys) if (!Ledger.IsHeld(k)) notHeld.Add(k);
                var up = ReleaseAndQueue(Ledger.Release(request.Keys), "up");
                reply["ok"] = true;
                reply["released"] = KeyInjectPlan.Join(up);
                if (notHeld.Count > 0) reply["notHeld"] = KeyInjectPlan.Join(notHeld);
                reply["held"] = KeyInjectPlan.Join(Ledger.Held);
                return reply;
            }
        }

        var missing = UnmappedKeys(request.Keys);
        if (missing.Count > 0)
        {
            reply["ok"] = false;
            reply["error"] = "no Input System key for " + KeyInjectPlan.Join(missing);
            return reply;
        }

        var keyboard = CurrentKeyboard();
        string? refusal = Prepare(keyboard, request.Background, out var env);
        reply["input"] = env;
        if (refusal != null)
        {
            Plugin.Logger?.LogInfo("Dev key " + request.Action.ToString().ToLowerInvariant() + ": refused (" + refusal + ")");
            reply["ok"] = false;
            reply["error"] = refusal;
            return reply;
        }

        if (request.Action == KeyInjectPlan.Action.Type)
        {
            int queued = 0;
            foreach (char c in request.Text)
            {
                Queue(KeyEventBytes.TextEvent(keyboard!.deviceId, CurrentTime(), c));
                queued++;
            }
            Plugin.Logger?.LogInfo("Dev key type: queued " + queued + " text event(s)");
            // The events apply on the next input update; restore an override after that, not before.
            if (_savedBehavior != null) MainThread.PostDelayed(0.2, RestoreBackgroundIfIdle, TickOwner);
            reply["ok"] = true;
            reply["queued"] = queued;
            return reply;
        }

        // tap, hold, down
        long releaseAt = NowMs + request.Ms;
        foreach (var k in request.Keys) Reached.Remove(k);
        Press(keyboard!, request.Keys, releaseAt, request.Action.ToString().ToLowerInvariant(), request.Ms);

        var keys = request.Keys;
        var deferred = new DeferredResult(request.Action == KeyInjectPlan.Action.Down ? 2_000 : request.Ms + 2_000);
        int frames = 0;
        void Finish()
        {
            var reached = new List<UiKey>();
            var notReached = new List<UiKey>();
            foreach (var k in keys) (Reached.Contains(k) ? reached : notReached).Add(k);
            reply["ok"] = notReached.Count == 0;
            reply["reached"] = KeyInjectPlan.Join(reached);
            if (notReached.Count > 0)
            {
                reply["notReached"] = KeyInjectPlan.Join(notReached);
                reply["error"] = "the keyboard never reported " + KeyInjectPlan.Join(notReached)
                    + " pressed; the Input System dropped the event (see input: focused, backgroundBehavior, keyboardEnabled)";
            }
            reply["held"] = KeyInjectPlan.Join(Ledger.Held);
            reply["framesWaited"] = frames;
            deferred.SetResult(reply);
        }
        void Poll()
        {
            try
            {
                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
                frames++;
                bool done = request.Action == KeyInjectPlan.Action.Down
                    ? frames >= 3
                    : AllReleased(keys);
                if (done || frames > 2_000)
                {
                    Finish();
                    return;
                }
                MainThread.PostDelayed(0, Poll, deferred);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Dev key: " + e.Message);
                if (!deferred.Completion.Task.IsCompleted) Finish();
            }
        }
        MainThread.PostDelayed(0, Poll, deferred);
        return deferred;
    }

    /// <summary>Dev unload and Core swap: release every held key and restore the background setting.</summary>
    internal static void Shutdown()
    {
        try
        {
            var up = Ledger.ReleaseAll();
            if (up.Count > 0)
            {
                QueueHeldState(_deviceId);
                Plugin.Logger?.LogInfo("Dev key: released " + KeyInjectPlan.Join(up) + " on unload");
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Dev key: release on unload threw: " + e.Message); }
        finally
        {
            Reached.Clear();
            _tickScheduled = false;
            RestoreBackgroundIfIdle();
        }
    }

    private static void Press(Keyboard keyboard, IReadOnlyList<UiKey> keys, long releaseAt, string verb, int ms)
    {
        if (_deviceId >= 0 && _deviceId != keyboard.deviceId && Ledger.Count > 0)
        {
            // A keyboard swap while keys are held: let go on the old device first.
            var stale = Ledger.ReleaseAll();
            QueueHeldState(_deviceId);
            Plugin.Logger?.LogInfo("Dev key: released " + KeyInjectPlan.Join(stale) + " on the previous keyboard");
        }
        _deviceId = keyboard.deviceId;

        // Modifiers go down one event before the main key, as a player presses them.
        var modifiers = new List<UiKey>();
        foreach (var k in keys) if (KeyChord.IsModifierKey(k)) modifiers.Add(k);
        if (modifiers.Count > 0 && modifiers.Count < keys.Count)
        {
            Ledger.Press(modifiers, releaseAt);
            QueueHeldState(_deviceId);
        }
        Ledger.Press(keys, releaseAt);
        QueueHeldState(_deviceId);
        Plugin.Logger?.LogInfo("Dev key " + verb + ": down " + KeyInjectPlan.Join(keys)
            + (verb == "down" ? " (released by up, release or after " + ms + " ms)" : " for " + ms + " ms"));
        ScheduleTick();
    }

    private static IReadOnlyList<UiKey> ReleaseAndQueue(IReadOnlyList<UiKey> up, string verb)
    {
        if (up.Count == 0) return up;
        // Main key first, modifiers one event later.
        var mains = new List<UiKey>();
        foreach (var k in up) if (!KeyChord.IsModifierKey(k)) mains.Add(k);
        if (mains.Count > 0 && mains.Count < up.Count)
        {
            var partial = new List<UiKey>(Ledger.Held);
            foreach (var k in up) if (KeyChord.IsModifierKey(k)) partial.Add(k);
            QueueState(_deviceId, partial);
        }
        QueueHeldState(_deviceId);
        Plugin.Logger?.LogInfo("Dev key " + verb + ": up " + KeyInjectPlan.Join(up));
        // The release applies on the next input update: restore a background override after it, not before.
        if (_savedBehavior != null && Ledger.Count == 0) MainThread.PostDelayed(0.2, RestoreBackgroundIfIdle, TickOwner);
        return up;
    }

    private static bool AllReleased(IReadOnlyList<UiKey> keys)
    {
        foreach (var k in keys) if (Ledger.IsHeld(k)) return false;
        return true;
    }

    private static void ScheduleTick()
    {
        if (_tickScheduled) return;
        _tickScheduled = true;
        MainThread.PostDelayed(0, Tick, TickOwner);
    }

    /// <summary>Each frame while keys are held: note which the device reports pressed, and release expired ones.</summary>
    private static void Tick()
    {
        _tickScheduled = false;
        try
        {
            var keyboard = CurrentKeyboard();
            if (keyboard != null)
            {
                foreach (var k in Ledger.Held)
                {
                    if (Reached.Contains(k) || !Map.TryGetValue(k, out var gameKey)) continue;
                    try
                    {
                        var control = keyboard[gameKey];
                        if (control != null && control.isPressed) Reached.Add(k);
                    }
                    catch { /* a device mid-teardown reads as not reached */ }
                }
            }
            ReleaseAndQueue(Ledger.Expired(NowMs), "release-after-ms");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Dev key: tick threw (" + e.Message + "); releasing every held key");
            try { ReleaseAndQueue(Ledger.ReleaseAll(), "release-on-error"); } catch { /* best effort */ }
        }
        if (Ledger.Count > 0) ScheduleTick();
    }

    private static void QueueHeldState(int deviceId) => QueueState(deviceId, Ledger.Held);

    private static void QueueState(int deviceId, IReadOnlyList<UiKey> held)
    {
        if (deviceId < 0) return;
        var bits = new List<int>(held.Count);
        foreach (var k in held) if (Map.TryGetValue(k, out var gameKey)) bits.Add((int)gameKey);
        Queue(KeyEventBytes.KeyboardStateEvent(deviceId, CurrentTime(), KeyEventBytes.KeyboardState(bits)));
    }

    /// <summary>Copy the event into unmanaged memory and hand it to <c>InputSystem.QueueEvent</c>, which copies it again.</summary>
    private static unsafe void Queue(byte[] eventBytes)
    {
        IntPtr buffer = Marshal.AllocHGlobal(eventBytes.Length);
        try
        {
            Marshal.Copy(eventBytes, 0, buffer, eventBytes.Length);
            InputSystem.QueueEvent(new InputEventPtr((InputEvent*)buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static double CurrentTime()
    {
        try { return InputState.currentTime; }
        catch { return 0; }
    }

    /// <summary>The keyboard as the Input System reports it; the device the events are queued for.</summary>
    private static Keyboard? CurrentKeyboard()
    {
        try { return Keyboard.current; }
        catch { return null; }
    }

    /// <summary>
    /// Whether a press can reach the game now; null when it can, else the sentence. With <paramref name="background"/>,
    /// an unfocused window gets <c>IgnoreFocus</c> and an enabled keyboard first.
    /// </summary>
    private static string? Prepare(Keyboard? keyboard, bool background, out Dictionary<string, object?> env)
    {
        env = Environment();
        if (keyboard == null) return "no keyboard device (Keyboard.current is null)";
        bool focused = env["focused"] is true;
        if (focused) return null;

        bool enabled = env["keyboardEnabled"] is true;
        if (!background)
        {
            if (!enabled)
                return "the game window is in the background and the Input System disabled the keyboard "
                    + "(InputSettings.backgroundBehavior=" + env["backgroundBehavior"] + "), so it drops queued key events; "
                    + "pass background:true to let it take them for this op";
            return null;
        }

        try
        {
            var settings = InputSystem.settings;
            if (settings != null && settings.backgroundBehavior != InputSettings.BackgroundBehavior.IgnoreFocus)
            {
                _savedBehavior ??= settings.backgroundBehavior;
                settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                Plugin.Logger?.LogInfo("Dev key: InputSettings.backgroundBehavior " + _savedBehavior + " -> IgnoreFocus for this op (background:true)");
            }
            if (!keyboard.enabled)
            {
                InputSystem.EnableDevice(keyboard);
                Plugin.Logger?.LogInfo("Dev key: enabled the keyboard while the window is in the background (background:true)");
            }
        }
        catch (Exception e)
        {
            RestoreBackgroundIfIdle();
            return "background:true could not prepare the keyboard: " + e.Message;
        }
        env = Environment();
        return env["keyboardEnabled"] is true ? null : "background:true: the keyboard is still disabled";
    }

    private static void RestoreBackgroundIfIdle()
    {
        if (_savedBehavior is not InputSettings.BackgroundBehavior saved || Ledger.Count > 0) return;
        _savedBehavior = null;
        try
        {
            var settings = InputSystem.settings;
            if (settings != null) settings.backgroundBehavior = saved;
            Plugin.Logger?.LogInfo("Dev key: InputSettings.backgroundBehavior restored to " + saved);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Dev key: could not restore backgroundBehavior: " + e.Message); }
    }

    /// <summary>What decides whether a queued event is applied: focus, the background rule and the keyboard.</summary>
    private static Dictionary<string, object?> Environment()
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        try { d["focused"] = UnityEngine.Application.isFocused; } catch { d["focused"] = null; }
        try { d["runInBackground"] = UnityEngine.Application.runInBackground; } catch { d["runInBackground"] = null; }
        try { d["backgroundBehavior"] = InputSystem.settings?.backgroundBehavior.ToString(); } catch { d["backgroundBehavior"] = null; }
        try { d["updateMode"] = InputSystem.settings?.updateMode.ToString(); } catch { d["updateMode"] = null; }
        var keyboard = CurrentKeyboard();
        d["keyboard"] = keyboard != null;
        if (keyboard != null)
        {
            try { d["keyboardId"] = keyboard.deviceId; } catch { d["keyboardId"] = null; }
            try { d["keyboardEnabled"] = keyboard.enabled; } catch { d["keyboardEnabled"] = null; }
            try { d["keyboardCanRunInBackground"] = keyboard.canRunInBackground; } catch { d["keyboardCanRunInBackground"] = null; }
        }
        d["backgroundOverride"] = _savedBehavior != null;
        return d;
    }

    private static List<UiKey> UnmappedKeys(IReadOnlyList<UiKey> keys)
    {
        var missing = new List<UiKey>();
        foreach (var k in keys) if (!Map.ContainsKey(k)) missing.Add(k);
        return missing;
    }

    /// <summary>Our <see cref="UiKey"/> to the Input System's <c>Key</c>, by name (never by number).</summary>
    private static readonly Dictionary<UiKey, GameKey> Map = BuildMap();

    private static Dictionary<UiKey, GameKey> BuildMap()
    {
        var map = new Dictionary<UiKey, GameKey>();
        foreach (UiKey key in Enum.GetValues(typeof(UiKey)))
        {
            if (key == UiKey.None || KeyChord.IsMouseKey(key)) continue;
            if (Enum.TryParse(key.ToString(), ignoreCase: false, out GameKey game)
                && (int)game > 0 && (int)game < KeyEventBytes.KeyboardStateBits)
                map[key] = game;
        }
        return map;
    }
#endif
}
