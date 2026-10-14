using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

#if !PROGENITOR_TESTS
using System.Runtime.InteropServices;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
#endif

namespace Progenitor.Dev.Ops;

/// <summary>
/// NAV1: the bridge's <c>nav</c> op (client, main thread). <c>state</c> reads where the controller focus is;
/// <c>press {button}</c> runs one navigation step through Core's <c>UiNavTestBridge</c>; <c>pad {button, ms}</c> presses
/// a button on a virtual gamepad the op adds to the Input System, so the step goes through the real device read
/// (<c>SampleNav</c>) and the game sees a controller; <c>pad_remove</c> removes that gamepad. Buttons: <c>up</c>,
/// <c>down</c>, <c>left</c>, <c>right</c>, <c>a</c>, <c>b</c>, <c>x</c>, <c>lt</c>, <c>rt</c>.
/// </summary>
internal static class NavOps
{
    internal const string ClientOnlyMessage = "nav runs on the client only: the server has no screens.";

    internal const string Buttons = "up, down, left, right, a, b, x, lt, rt";

    /// <summary>How long a virtual pad press is held by default (long enough for one frame at any rate, short of repeat).</summary>
    internal const int DefaultHoldMs = 120;

    private const string BridgeType = "Progenitor.Ui.UiNavTestBridge";

    /// <summary>A bridge button name, normalized, or null when it is not one.</summary>
    internal static string? Normalize(string? button)
    {
        string b = (button ?? "").Trim().ToLowerInvariant();
        return b is "up" or "down" or "left" or "right" or "a" or "b" or "x" or "lt" or "rt" ? b : null;
    }

    /// <summary>
    /// The 28-byte <c>GamepadState</c> for one button held (client dump layout: uint buttons 0x0, Vector2 leftStick 0x4,
    /// Vector2 rightStick 0xC, float leftTrigger 0x14, float rightTrigger 0x18; bits D-pad up 0, down 1, left 2, right 3,
    /// East 5, South 6, West 7). Null is the released state.
    /// </summary>
    internal static byte[] PadState(string? button)
    {
        var state = new byte[PadStateBytes];
        uint bits = button switch
        {
            "up" => 1u << 0,
            "down" => 1u << 1,
            "left" => 1u << 2,
            "right" => 1u << 3,
            "b" => 1u << 5,
            "a" => 1u << 6,
            "x" => 1u << 7,
            _ => 0u,
        };
        BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(0x0), bits);
        if (button == "lt") BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(0x14), BitConverter.SingleToInt32Bits(1f));
        if (button == "rt") BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(0x18), BitConverter.SingleToInt32Bits(1f));
        return state;
    }

    private static Dictionary<string, object?> Bridge(string method, params object?[] args)
    {
        var type = typeof(ModsMenu).Assembly.GetType(BridgeType, throwOnError: false)
            ?? throw new InvalidOperationException("Core has no " + BridgeType + " (older than this op)");
        var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(BridgeType + "." + method + " not found");
        try
        {
            return mi.Invoke(null, args) as Dictionary<string, object?>
                ?? new Dictionary<string, object?> { ["ok"] = false, ["error"] = method + " returned nothing" };
        }
        catch (TargetInvocationException e) { throw new InvalidOperationException(e.InnerException?.Message ?? e.Message); }
    }

    internal const int PadStateBytes = 28;
    internal const int PadFormat = 0x47504144; // 'GPAD'
    internal const int PadEventBytes = 0x18 + PadStateBytes; // 52, already 4-byte aligned

    /// <summary>A whole gamepad state event for <paramref name="deviceId"/> (the <c>StateEvent</c> header the key op writes).</summary>
    internal static byte[] PadStateEvent(int deviceId, double time, byte[] state)
    {
        if (state.Length != PadStateBytes) throw new ArgumentException("a gamepad state is " + PadStateBytes + " bytes", nameof(state));
        if (deviceId < 0 || deviceId > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(deviceId));
        var buffer = new byte[PadEventBytes];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x0), KeyEventBytes.StateType);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0x4), (ushort)PadEventBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0x6), (ushort)deviceId);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0x8), BitConverter.DoubleToInt64Bits(time));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x10), 0);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x14), PadFormat);
        Array.Copy(state, 0, buffer, 0x18, PadStateBytes);
        return buffer;
    }

    private static string Action(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
            ? (a.GetString() ?? "").Trim().ToLowerInvariant()
            : "state";

    private static string? Button(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("button", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;

    private static bool Background(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("background", out var b) && b.ValueKind == JsonValueKind.True;

    private static int HoldMs(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("ms", out var ms) && ms.TryGetInt32(out int v) && v > 0 ? Math.Min(v, 5000) : DefaultHoldMs;

    internal static object Run(JsonElement args)
    {
        string action = Action(args);
#if !PROGENITOR_TESTS
        if (Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException(ClientOnlyMessage);
#endif
        switch (action)
        {
            case "state":
                return new Dictionary<string, object?> { ["ok"] = true, ["nav"] = Bridge("State"), ["pad"] = PadDescribe() };
            case "press":
            {
                var button = Normalize(Button(args)) ?? throw new ArgumentException("args.button is required: " + Buttons);
                var reply = Bridge("Press", button);
                reply["ok"] = true;
                return reply;
            }
            case "pad":
            {
                var button = Normalize(Button(args)) ?? throw new ArgumentException("args.button is required: " + Buttons);
                return PadPress(button, HoldMs(args), Background(args));
            }
            case "pad_remove":
                return PadRemove();
            case "hold":
            {
                // BRIDGEUI1: buttons held through NAV1's own per-frame sample (repeat included), no device.
                string buttons = HoldButtons(Button(args));
                return SyntheticHold("HoldNav", new object?[] { buttons, HoldMs(args) }, HoldMs(args));
            }
            case "stick":
            {
                // BRIDGEUI1: the right stick's vertical axis through the same sample.
                float y = StickY(args);
                return SyntheticHold("Stick", new object?[] { y, HoldMs(args) }, HoldMs(args));
            }
            case "focus":
            {
                // BRIDGEUI1: the focus straight onto a visible cell row (or one of its cells): into a content pane or a
                // row past the first screen without a walk of presses.
                string row = (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("row", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null)
                    ?? throw new ArgumentException("nav focus needs args.row (a cell row key) and optionally args.cell");
                string? cell = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("cell", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                return UiInputOps.Dict(UiInputOps.Bridge("NavFocus", row.Trim(), cell?.Trim()));
            }
            default:
                throw new ArgumentException("args.action is state, press, pad, pad_remove, hold, stick or focus");
        }
    }

    /// <summary>BRIDGEUI1: <c>hold</c>'s buttons: one or several of the nav buttons joined by <c>+</c> or <c>,</c>.</summary>
    internal static string HoldButtons(string? text)
    {
        var parts = (text ?? "").Split(new[] { '+', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("args.button is required: " + Buttons + " (several joined by +)");
        var list = new List<string>();
        foreach (var p in parts)
            list.Add(Normalize(p) ?? throw new ArgumentException("args.button '" + p + "' is not one of " + Buttons));
        return string.Join("+", list);
    }

    /// <summary>BRIDGEUI1: <c>stick</c>'s <c>y</c>, -1..1 (positive up), not 0.</summary>
    internal static float StickY(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("y", out var y) || y.ValueKind != JsonValueKind.Number
            || !y.TryGetDouble(out double v) || double.IsNaN(v) || v < -1 || v > 1 || v == 0)
            throw new ArgumentException("nav stick needs args.y, the right stick's vertical axis from -1 to 1 (positive up), not 0");
        return (float)v;
    }

    private static object SyntheticHold(string method, object?[] args, int ms)
    {
#if PROGENITOR_TESTS
        throw new InvalidOperationException(ClientOnlyMessage);
#else
        var held = UiInputOps.Dict(UiInputOps.Bridge(method, args));
        var deferred = new DeferredResult(ms + 5_000);
        MainThread.PostDelayed(ms / 1000.0 + 0.1, () =>
        {
            if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
            held["nav"] = Bridge("State");
            held["ok"] = true;
            deferred.SetResult(held);
        }, deferred);
        return deferred;
#endif
    }

#if PROGENITOR_TESTS
    private static object? PadDescribe() => null;

    private static object PadPress(string button, int ms, bool background) => throw new InvalidOperationException(ClientOnlyMessage);

    private static object PadRemove() => throw new InvalidOperationException(ClientOnlyMessage);

    internal static void Shutdown() { }
#else
    private static readonly object TickOwner = new();
    private static InputDevice? _pad;
    private static int _pressSeq;

    private static object? PadDescribe()
    {
        try
        {
            var pad = _pad;
            if (pad == null || !pad.added) return new Dictionary<string, object?> { ["added"] = false };
            var current = Gamepad.current;
            return new Dictionary<string, object?>
            {
                ["added"] = true,
                ["deviceId"] = pad.deviceId,
                ["current"] = current != null && current.deviceId == pad.deviceId,
            };
        }
        catch (Exception e) { return new Dictionary<string, object?> { ["error"] = e.Message }; }
    }

    private static object PadPress(string button, int ms, bool background)
    {
        var pad = EnsurePad();
        string? refused = PrepareBackground(pad, background);
        if (refused != null) return new Dictionary<string, object?> { ["ok"] = false, ["error"] = refused, ["pad"] = PadDescribe() };
        Queue(pad.deviceId, PadState(button));
        try { pad.MakeCurrent(); } catch { /* the press makes it current too */ }
        int id = pad.deviceId;
        // Only the newest press releases: an older press's release must not cut a later one short.
        int press = ++_pressSeq;
        MainThread.PostDelayed(ms / 1000.0, () =>
        {
            if (press != _pressSeq) return;
            try { if (_pad != null && _pad.added && _pad.deviceId == id) Queue(id, PadState(null)); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Dev nav: pad release threw: " + e.Message); }
            // As the key op does: the background setting goes back once the press is over (after the release applies).
            MainThread.PostDelayed(0.2, () => { if (press == _pressSeq) RestoreBackground(); }, TickOwner);
        }, TickOwner);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["queued"] = button,
            ["holdMs"] = ms,
            ["pad"] = PadDescribe(),
            ["readback"] = "next frames: read nav state after the hold",
        };
    }

    private static InputDevice EnsurePad()
    {
        if (_pad != null && _pad.added) return _pad;
        _pad = InputSystem.AddDevice("Gamepad", "ProgenitorBridgePad", null);
        Plugin.Logger?.LogInfo("Dev nav: added a virtual gamepad (device " + _pad.deviceId + "); nav pad_remove removes it.");
        return _pad;
    }

    private static object PadRemove()
    {
        bool had = _pad != null && _pad.added;
        if (had)
        {
            try { Queue(_pad!.deviceId, PadState(null)); } catch { /* */ }
            try { InputSystem.RemoveDevice(_pad!); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Dev nav: removing the virtual gamepad threw: " + e.Message); }
            Plugin.Logger?.LogInfo("Dev nav: removed the virtual gamepad.");
        }

        _pad = null;
        RestoreBackground();
        return new Dictionary<string, object?> { ["ok"] = true, ["removed"] = had };
    }

    /// <summary>The <c>backgroundBehavior</c> to restore after the press, when <c>background:true</c> changed it.</summary>
    private static InputSettings.BackgroundBehavior? _savedBehavior;

    /// <summary>
    /// With the window in the background the Input System may disable devices and drop their events (as for the key op).
    /// <c>background:true</c> lets it take them for this press (restored 0.2 s after the release); null when the press
    /// can go ahead.
    /// </summary>
    private static string? PrepareBackground(InputDevice pad, bool background)
    {
        bool focused;
        try { focused = UnityEngine.Application.isFocused; } catch { focused = true; }
        if (focused) return null;
        if (!background)
        {
            bool enabled;
            try { enabled = pad.enabled; } catch { enabled = true; }
            return enabled ? null
                : "the game window is in the background and the Input System disabled the virtual gamepad; pass background:true";
        }

        try
        {
            var settings = InputSystem.settings;
            if (settings != null && settings.backgroundBehavior != InputSettings.BackgroundBehavior.IgnoreFocus)
            {
                _savedBehavior ??= settings.backgroundBehavior;
                settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                Plugin.Logger?.LogInfo("Dev nav: InputSettings.backgroundBehavior " + _savedBehavior + " -> IgnoreFocus for this press (background:true)");
            }

            if (!pad.enabled) InputSystem.EnableDevice(pad);
        }
        catch (Exception e) { return "background:true could not prepare the virtual gamepad: " + e.Message; }

        return null;
    }

    private static void RestoreBackground()
    {
        if (_savedBehavior is not InputSettings.BackgroundBehavior saved) return;
        _savedBehavior = null;
        try
        {
            var settings = InputSystem.settings;
            if (settings != null) settings.backgroundBehavior = saved;
            Plugin.Logger?.LogInfo("Dev nav: InputSettings.backgroundBehavior restored to " + saved);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Dev nav: could not restore backgroundBehavior: " + e.Message); }
    }

    private static unsafe void Queue(int deviceId, byte[] state)
    {
        byte[] bytes = PadStateEvent(deviceId, CurrentTime(), state);
        IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            InputSystem.QueueEvent(new InputEventPtr((InputEvent*)buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static double CurrentTime()
    {
        try { return InputState.currentTime; }
        catch { return 0; }
    }

    /// <summary>The bridge is going: the virtual gamepad goes with it.</summary>
    internal static void Shutdown()
    {
        try { PadRemove(); } catch { /* best effort */ }
    }
#endif
}
