using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;

#if !PROGENITOR_TESTS
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
#endif

namespace Progenitor.Dev.Ops;

/// <summary>
/// <c>mouse</c>: move the pointer and click inside the game's own input, focus-free, so the game, uGUI and
/// Progenitor react as if the player used the mouse. Client only, main thread. Dev bridge only: a test tool that
/// writes input and never reads it for an action of its own.
///
/// <para><b>How.</b> Each step queues one whole <c>MouseState</c> event (format <c>MOUS</c>, see
/// <see cref="MouseEventBytes"/>) for <c>Mouse.current</c> through the non-generic <c>InputSystem.QueueEvent</c>,
/// exactly as the <c>key</c> op does for the keyboard. A click is: the pointer at the point with no button, then
/// <c>settleFrames</c> frames later the button down, then after <c>ms</c> the button up, all at the same point, so
/// hover logic sees the pointer before the press. No OS input; the real cursor is not moved, and the next physical
/// mouse report replaces the injected position.</para>
///
/// <para><b>Releases.</b> A held button is released by the click's own timer, by <c>release</c>, and on Dev unload
/// (<see cref="Shutdown"/>).</para>
/// </summary>
internal static class MouseInjectOps
{
    internal const string ClientOnlyMessage = "mouse runs on the client only: the server has no mouse.";

#if PROGENITOR_TESTS
    internal static object Run(JsonElement args)
    {
        MouseInjectPlan.Parse(args);
        throw new InvalidOperationException(ClientOnlyMessage);
    }

    internal static void Shutdown() { }
#else
    private static readonly object TickOwner = new();
    private static int _deviceId = -1;
    private static ushort _held;
    private static float _x;
    private static float _y;

    /// <summary>The <c>backgroundBehavior</c> to restore, when <c>background:true</c> changed it.</summary>
    private static InputSettings.BackgroundBehavior? _savedBehavior;

    internal static object Run(JsonElement args)
    {
        var request = MouseInjectPlan.Parse(args);
        if (Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException(ClientOnlyMessage);

        var reply = new Dictionary<string, object?>(StringComparer.Ordinal) { ["request"] = request.Describe() };
        if (request.Action == MouseInjectPlan.Action.Status)
        {
            reply["ok"] = true;
            reply["input"] = Environment();
            reply["held"] = _held;
            reply["chat"] = ChatRect(out _, out _);
            return reply;
        }

        if (request.Action == MouseInjectPlan.Action.Release)
        {
            reply["ok"] = true;
            reply["released"] = _held;
            ReleaseAll("release");
            return reply;
        }

        float x = request.X, y = request.Y;
        if (request.Target == MouseInjectPlan.TargetChat)
        {
            var chat = ChatRect(out x, out y);
            reply["chat"] = chat;
            if (chat["shown"] is not true)
            {
                reply["ok"] = false;
                reply["error"] = "the chat log is not on screen (" + chat["why"] + "); send a chat line first or open chat";
                return reply;
            }
        }

        var mouse = CurrentMouse();
        string? refusal = Prepare(mouse, request.Background, out var env);
        reply["input"] = env;
        if (refusal != null)
        {
            Plugin.Logger?.LogInfo("Dev mouse " + request.Action.ToString().ToLowerInvariant() + ": refused (" + refusal + ")");
            reply["ok"] = false;
            reply["error"] = refusal;
            return reply;
        }

        _deviceId = mouse!.deviceId;
        _x = x;
        _y = y;
        reply["x"] = x;
        reply["y"] = y;
        QueueState(_held);
        Plugin.Logger?.LogInfo("Dev mouse " + request.Action.ToString().ToLowerInvariant() + ": pointer to ("
            + x.ToString("F0") + ", " + y.ToString("F0") + ")");

        var deferred = new DeferredResult(request.Action switch
        {
            MouseInjectPlan.Action.Move => 5_000,
            MouseInjectPlan.Action.Wheel => 5_000,
            MouseInjectPlan.Action.Drag => request.Steps * 50 + 5_000,
            _ => request.Ms + 5_000,
        });
        ushort bit = MouseEventBytes.Bit(request.Button);
        int frames = 0;
        int pressedFrame = -1;
        int wheelFrame = -1;
        bool wheelSeen = false;
        bool reachedPosition = false;
        bool reachedPress = false;
        var press = new Stopwatch();

        void Finish(string? error)
        {
            reply["ok"] = error == null;
            if (error != null) reply["error"] = error;
            reply["positionReached"] = reachedPosition;
            if (request.Action == MouseInjectPlan.Action.Click) reply["pressReached"] = reachedPress;
            reply["framesWaited"] = frames;
            reply["held"] = _held;
            RestoreBackgroundIfIdle();
            deferred.SetResult(reply);
        }

        void Step()
        {
            try
            {
                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
                frames++;
                var m = CurrentMouse();
                if (m != null && !reachedPosition)
                {
                    var p = m.position.ReadValue();
                    reachedPosition = Mathf.Abs(p.x - x) < 1.5f && Mathf.Abs(p.y - y) < 1.5f;
                }

                if (request.Action == MouseInjectPlan.Action.Move)
                {
                    if (frames >= Math.Max(1, request.SettleFrames)) { Finish(reachedPosition ? null : PositionError()); return; }
                }
                else if (request.Action == MouseInjectPlan.Action.Wheel)
                {
                    // WHEEL1: settle, one event with the notches, then a zero reading on the next frame.
                    if (wheelFrame < 0 && frames >= Math.Max(1, request.SettleFrames))
                    {
                        QueueScroll(request.Notches * MouseInjectPlan.WheelDeltaPerNotch);
                        wheelFrame = frames;
                        Plugin.Logger?.LogInfo("Dev mouse wheel: " + request.Notches + " notch(es)");
                    }
                    else if (wheelFrame >= 0)
                    {
                        if (m != null && !wheelSeen && Mathf.Abs(m.scroll.ReadValue().y) > 0.5f) wheelSeen = true;
                        if (frames > wheelFrame)
                        {
                            QueueScroll(0f);
                            reply["wheelSeen"] = wheelSeen;
                            Finish(!reachedPosition ? PositionError()
                                : wheelSeen ? null : "the mouse never reported the wheel; the Input System dropped the event");
                            return;
                        }
                    }
                }
                else if (request.Action == MouseInjectPlan.Action.Drag && pressedFrame >= 0)
                {
                    int moved = frames - pressedFrame;
                    if (m != null && !reachedPress && Control(m, request.Button) is { } dc && dc.isPressed) reachedPress = true;
                    if (moved <= request.Steps)
                    {
                        float t = moved / (float)request.Steps;
                        _x = x + (request.ToX - x) * t;
                        _y = y + (request.ToY - y) * t;
                        QueueState(_held);
                    }
                    else
                    {
                        _held = (ushort)(_held & ~bit);
                        QueueState(_held);
                        Plugin.Logger?.LogInfo("Dev mouse drag: released at (" + _x.ToString("F0") + ", " + _y.ToString("F0") + ")");
                        Finish(reachedPress ? null : "the mouse never reported the button pressed");
                        return;
                    }
                }
                else if (pressedFrame < 0)
                {
                    if (frames >= request.SettleFrames)
                    {
                        _held |= bit;
                        QueueState(_held);
                        pressedFrame = frames;
                        press.Start();
                        Plugin.Logger?.LogInfo("Dev mouse click: " + request.Button.ToString().ToLowerInvariant() + " down for " + request.Ms + " ms");
                    }
                }
                else
                {
                    if (m != null && !reachedPress && Control(m, request.Button) is { } c && c.isPressed) reachedPress = true;
                    if (press.ElapsedMilliseconds >= request.Ms)
                    {
                        _held = (ushort)(_held & ~bit);
                        QueueState(_held);
                        Plugin.Logger?.LogInfo("Dev mouse click: " + request.Button.ToString().ToLowerInvariant() + " up");
                        Finish(reachedPress ? null
                            : "the mouse never reported the button pressed; the Input System dropped the event (see input: focused, backgroundBehavior, mouseEnabled)");
                        return;
                    }
                }

                if (frames > 5_000) { ReleaseAll("timeout"); Finish("timed out"); return; }
                MainThread.PostDelayed(0, Step, deferred);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Dev mouse: " + e.Message + "; releasing");
                ReleaseAll("error");
                if (!deferred.Completion.Task.IsCompleted) Finish(e.Message);
            }
        }

        MainThread.PostDelayed(0, Step, deferred);
        return deferred;
    }

    private static string PositionError() =>
        "the mouse never reported the injected position; the Input System dropped the event or a physical mouse report replaced it";

    /// <summary>Dev unload and Core swap: release every held button and restore the background setting.</summary>
    internal static void Shutdown()
    {
        try { ReleaseAll("unload"); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Dev mouse: release on unload threw: " + e.Message); }
        finally
        {
            _held = 0;
            RestoreBackgroundIfIdle();
        }
    }

    private static void ReleaseAll(string why)
    {
        if (_held == 0) return;
        _held = 0;
        QueueState(0);
        Plugin.Logger?.LogInfo("Dev mouse: released every button (" + why + ")");
    }

    private static ButtonControl? Control(Mouse mouse, MouseInjectPlan.Button button) => button switch
    {
        MouseInjectPlan.Button.Left => mouse.leftButton,
        MouseInjectPlan.Button.Right => mouse.rightButton,
        MouseInjectPlan.Button.Middle => mouse.middleButton,
        MouseInjectPlan.Button.Forward => mouse.forwardButton,
        MouseInjectPlan.Button.Back => mouse.backButton,
        _ => null,
    };

    private static void QueueState(ushort buttons)
    {
        if (_deviceId < 0) return;
        Queue(MouseEventBytes.MouseStateEvent(_deviceId, CurrentTime(), _x, _y, buttons));
    }

    /// <summary>WHEEL1: a state event at the current point with the held buttons and a wheel reading.</summary>
    private static void QueueScroll(float scrollY)
    {
        if (_deviceId < 0) return;
        Queue(MouseEventBytes.MouseStateEvent(_deviceId, CurrentTime(), _x, _y, _held, scrollY: scrollY));
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

    private static Mouse? CurrentMouse()
    {
        try { return Mouse.current; }
        catch { return null; }
    }

    /// <summary>
    /// The game's chat log in screen pixels (the rect its own hover test uses, overlay canvas, null camera), and its
    /// center through <paramref name="cx"/>/<paramref name="cy"/>.
    /// </summary>
    private static Dictionary<string, object?> ChatRect(out float cx, out float cy)
    {
        cx = cy = 0;
        var d = new Dictionary<string, object?>(StringComparer.Ordinal) { ["shown"] = false };
        try
        {
            var world = Progenitor.Worlds.Client;
            if (world == null || !world.IsCreated) { d["why"] = "no client world"; return d; }
            var system = world.GetExistingSystemManaged<ProjectM.UI.ClientChatSystem>();
            var window = system?._ChatWindow;
            if (window == null) { d["why"] = "no chat window"; return d; }
            d["state"] = system!._CurrentState.ToString();
            var log = window.ScrollViewGraphic;
            if (log == null) { d["why"] = "no chat log graphic"; return d; }
            var rect = log.rectTransform;
            var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            rect.GetWorldCorners(corners);
            Vector2 min = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
            Vector2 max = RectTransformUtility.WorldToScreenPoint(null, corners[2]);
            cx = (min.x + max.x) / 2f;
            cy = (min.y + max.y) / 2f;
            d["minX"] = min.x; d["minY"] = min.y; d["maxX"] = max.x; d["maxY"] = max.y;
            d["centerX"] = cx; d["centerY"] = cy;
            float alpha = window.ContentCanvasGroup != null ? window.ContentCanvasGroup.alpha : 1f;
            d["alpha"] = alpha;
            bool shown = window.gameObject.activeInHierarchy && alpha > 0.01f && system._CurrentState != ProjectM.UI.ChatWindowState.Close;
            d["shown"] = shown;
            if (!shown) d["why"] = "state " + system._CurrentState + ", alpha " + alpha.ToString("F2");
        }
        catch (Exception e) { d["why"] = "read threw: " + e.Message; }
        return d;
    }

    /// <summary>
    /// Whether a mouse event can reach the game now; null when it can, else the sentence. With
    /// <paramref name="background"/>, an unfocused window gets <c>IgnoreFocus</c> and an enabled mouse first.
    /// </summary>
    private static string? Prepare(Mouse? mouse, bool background, out Dictionary<string, object?> env)
    {
        env = Environment();
        if (mouse == null) return "no mouse device (Mouse.current is null)";
        if (env["focused"] is true) return null;
        if (!background)
        {
            if (env["mouseEnabled"] is not true)
                return "the game window is in the background and the Input System disabled the mouse "
                    + "(InputSettings.backgroundBehavior=" + env["backgroundBehavior"] + "), so it drops queued mouse events; "
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
                Plugin.Logger?.LogInfo("Dev mouse: InputSettings.backgroundBehavior " + _savedBehavior + " -> IgnoreFocus for this op (background:true)");
            }
            if (!mouse.enabled)
            {
                InputSystem.EnableDevice(mouse);
                Plugin.Logger?.LogInfo("Dev mouse: enabled the mouse while the window is in the background (background:true)");
            }
        }
        catch (Exception e)
        {
            RestoreBackgroundIfIdle();
            return "background:true could not prepare the mouse: " + e.Message;
        }
        env = Environment();
        return env["mouseEnabled"] is true ? null : "background:true: the mouse is still disabled";
    }

    private static void RestoreBackgroundIfIdle()
    {
        if (_savedBehavior is not InputSettings.BackgroundBehavior saved || _held != 0) return;
        _savedBehavior = null;
        try
        {
            var settings = InputSystem.settings;
            if (settings != null) settings.backgroundBehavior = saved;
            Plugin.Logger?.LogInfo("Dev mouse: InputSettings.backgroundBehavior restored to " + saved);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Dev mouse: could not restore backgroundBehavior: " + e.Message); }
    }

    private static Dictionary<string, object?> Environment()
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        try { d["focused"] = Application.isFocused; } catch { d["focused"] = null; }
        try { d["backgroundBehavior"] = InputSystem.settings?.backgroundBehavior.ToString(); } catch { d["backgroundBehavior"] = null; }
        var mouse = CurrentMouse();
        d["mouse"] = mouse != null;
        if (mouse != null)
        {
            try { d["mouseId"] = mouse.deviceId; } catch { d["mouseId"] = null; }
            try { d["mouseEnabled"] = mouse.enabled; } catch { d["mouseEnabled"] = null; }
            try
            {
                var p = mouse.position.ReadValue();
                d["pointerX"] = p.x;
                d["pointerY"] = p.y;
            }
            catch { /* */ }
        }
        try { d["screenWidth"] = Screen.width; d["screenHeight"] = Screen.height; } catch { /* */ }
        d["backgroundOverride"] = _savedBehavior != null;
        return d;
    }
#endif
}
