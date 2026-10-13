using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// The game-free half of the <c>mouse</c> op (<see cref="MouseInjectOps"/>): argument parsing and the bytes of
/// the Input System mouse state event the op queues. Tested in <c>BridgeMouseInjectTests</c>.
/// </summary>
internal static class MouseInjectPlan
{
    internal enum Action
    {
        Status = 0,
        Click = 1,
        Move = 2,
        Release = 3,

        /// <summary>Press at (x, y), move to (toX, toY) over <c>steps</c> frames, release there.</summary>
        Drag = 4,

        /// <summary>
        /// WHEEL1: the pointer to (x, y), then one wheel event of <c>notches</c> (positive up, away from the player) and a
        /// zero reading the next frame: the 0 to non-zero crossing a real wheel notch makes.
        /// </summary>
        Wheel = 5,
    }

    /// <summary>The Input System's <c>MouseState.buttons</c> bits (client metadata: forwardButton is bit 3).</summary>
    internal enum Button
    {
        Left = 0,
        Right = 1,
        Middle = 2,
        Forward = 3,
        Back = 4,
    }

    internal const string UsageMessage =
        "mouse needs args.action: click (args.button left|right|middle|back|forward, default left; args.ms, default 80) "
        + "or move, each at args.x and args.y in screen pixels (origin bottom-left) or at args.target \"chat\" (the center of "
        + "the game's chat log); drag (args.toX, args.toY, args.steps); wheel (args.notches, -20 to 20, not 0: positive "
        + "scrolls up, at args.x and args.y); release (let go of every button this op holds); or status.";

    /// <summary>WHEEL1: one notch in the mouse state's scroll units (Windows' WHEEL_DELTA).</summary>
    internal const float WheelDeltaPerNotch = 120f;

    internal const int MaxWheelNotches = 20;

    internal const string TargetChat = "chat";

    internal const int ClickDefaultMs = 80;
    internal const int DragDefaultSteps = 20;
    internal const int MaxDragSteps = 600;
    internal const int MaxClickMs = 10_000;

    /// <summary>Frames between moving the pointer and pressing, so hover logic sees the pointer before the press.</summary>
    internal const int SettleDefaultFrames = 3;
    internal const int MaxSettleFrames = 120;

    internal sealed class Request
    {
        internal Action Action { get; init; }
        internal Button Button { get; init; }
        internal float X { get; init; }
        internal float Y { get; init; }

        /// <summary>Empty for explicit coordinates, else <see cref="TargetChat"/>.</summary>
        internal string Target { get; init; } = "";

        internal int Ms { get; init; }
        internal int SettleFrames { get; init; }

        /// <summary>Drag: where the button is released.</summary>
        internal float ToX { get; init; }
        internal float ToY { get; init; }

        /// <summary>Drag: frames between press and release, one move per frame.</summary>
        internal int Steps { get; init; }

        /// <summary>Opt-in: let the mouse take events while the game window is in the background.</summary>
        internal bool Background { get; init; }

        /// <summary>Wheel: notches, positive up.</summary>
        internal int Notches { get; init; }

        internal bool HasPoint => Action is Action.Click or Action.Move or Action.Drag or Action.Wheel;

        internal Dictionary<string, object?> Describe() => new(StringComparer.Ordinal)
        {
            ["action"] = Action.ToString().ToLowerInvariant(),
            ["button"] = Action == Action.Click ? Button.ToString().ToLowerInvariant() : null,
            ["target"] = Target.Length > 0 ? Target : null,
            ["x"] = HasPoint && Target.Length == 0 ? X : null,
            ["y"] = HasPoint && Target.Length == 0 ? Y : null,
            ["ms"] = Action == Action.Click ? Ms : null,
            ["toX"] = Action == Action.Drag ? ToX : null,
            ["toY"] = Action == Action.Drag ? ToY : null,
            ["steps"] = Action == Action.Drag ? Steps : null,
            ["notches"] = Action == Action.Wheel ? Notches : null,
            ["settleFrames"] = HasPoint ? SettleFrames : null,
            ["background"] = Background,
        };
    }

    /// <summary>Parse and validate. Throws <see cref="ArgumentException"/> naming the op and the usage.</summary>
    internal static Request Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException(UsageMessage);
        string? actionText = Text(args, "action");
        if (actionText == null) throw new ArgumentException(UsageMessage);
        Action action = actionText.ToLowerInvariant() switch
        {
            "status" => Action.Status,
            "click" => Action.Click,
            "move" => Action.Move,
            "release" => Action.Release,
            "drag" => Action.Drag,
            "wheel" => Action.Wheel,
            _ => throw new ArgumentException("mouse: args.action '" + actionText + "' is not known. " + UsageMessage),
        };

        bool background = Bool(args, "background");
        if (action is Action.Status or Action.Release)
        {
            foreach (var name in new[] { "x", "y", "target", "button", "ms", "settleFrames" })
                if (args.TryGetProperty(name, out _))
                    throw new ArgumentException("mouse: " + actionText + " takes no " + name + ". " + UsageMessage);
            return new Request { Action = action, Background = background };
        }

        string target = (Text(args, "target") ?? "").ToLowerInvariant();
        bool hasX = args.TryGetProperty("x", out var xEl);
        bool hasY = args.TryGetProperty("y", out var yEl);
        float x = 0, y = 0;
        if (target.Length > 0)
        {
            if (target != TargetChat) throw new ArgumentException("mouse: args.target '" + target + "' is not known; use \"chat\".");
            if (hasX || hasY) throw new ArgumentException("mouse: give args.target or args.x and args.y, not both.");
        }
        else
        {
            if (!hasX || !hasY) throw new ArgumentException("mouse: " + actionText + " needs args.x and args.y, or args.target. " + UsageMessage);
            x = Coordinate(xEl, "x");
            y = Coordinate(yEl, "y");
        }

        Button button = Button.Left;
        string? buttonText = Text(args, "button");
        if (buttonText != null)
        {
            if (action is not (Action.Click or Action.Drag)) throw new ArgumentException("mouse: args.button is for click and drag.");
            button = buttonText.ToLowerInvariant() switch
            {
                "left" => Button.Left,
                "right" => Button.Right,
                "middle" => Button.Middle,
                "forward" => Button.Forward,
                "back" => Button.Back,
                _ => throw new ArgumentException("mouse: args.button '" + buttonText + "' is not left, right, middle, back or forward."),
            };
        }

        int ms = ClickDefaultMs;
        if (args.TryGetProperty("ms", out var msEl))
        {
            if (action != Action.Click) throw new ArgumentException("mouse: args.ms is for click.");
            ms = Int(msEl, "ms", 1, MaxClickMs);
        }

        int settle = SettleDefaultFrames;
        if (args.TryGetProperty("settleFrames", out var sEl)) settle = Int(sEl, "settleFrames", 0, MaxSettleFrames);

        float toX = 0, toY = 0;
        int steps = 0;
        if (action == Action.Drag)
        {
            if (!args.TryGetProperty("toX", out var tx) || !args.TryGetProperty("toY", out var ty))
                throw new ArgumentException("mouse: drag needs args.toX and args.toY.");
            toX = Coordinate(tx, "toX");
            toY = Coordinate(ty, "toY");
            steps = DragDefaultSteps;
            if (args.TryGetProperty("steps", out var st)) steps = Int(st, "steps", 1, MaxDragSteps);
        }
        else if (args.TryGetProperty("toX", out _) || args.TryGetProperty("toY", out _) || args.TryGetProperty("steps", out _))
            throw new ArgumentException("mouse: args.toX, args.toY and args.steps are for drag.");

        int notches = 0;
        if (action == Action.Wheel)
        {
            if (!args.TryGetProperty("notches", out var nEl)) throw new ArgumentException("mouse: wheel needs args.notches.");
            notches = Int(nEl, "notches", -MaxWheelNotches, MaxWheelNotches);
            if (notches == 0) throw new ArgumentException("mouse: args.notches is not 0.");
        }
        else if (args.TryGetProperty("notches", out _))
            throw new ArgumentException("mouse: args.notches is for wheel.");

        return new Request
        {
            Action = action, Button = button, X = x, Y = y, Target = target, Ms = ms, SettleFrames = settle, Background = background,
            ToX = toX, ToY = toY, Steps = steps, Notches = notches,
        };
    }

    private static string? Text(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("mouse: args." + name + " is true or false.");
        return v.GetBoolean();
    }

    private static float Coordinate(JsonElement v, string name)
    {
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || double.IsNaN(d) || double.IsInfinity(d)
            || d < 0 || d > 100_000)
            throw new ArgumentException("mouse: args." + name + " is a number of screen pixels from 0.");
        return (float)d;
    }

    private static int Int(JsonElement v, string name, int min, int max)
    {
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i) || i < min || i > max)
            throw new ArgumentException("mouse: args." + name + " is a whole number from "
                + min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture) + ".");
        return i;
    }
}

/// <summary>
/// The Input System mouse state event (client metadata, <c>UnityEngine.InputSystem.LowLevel.MouseState</c>):
/// <c>position</c> (Vector2, 0x0), <c>delta</c> (0x8), <c>scroll</c> (0x10), <c>buttons</c> (ushort, 0x18),
/// <c>displayIndex</c> (0x1A), <c>clickCount</c> (0x1C); 30 bytes, format <c>MOUS</c>. Built in managed bytes and
/// queued through the non-generic <c>InputSystem.QueueEvent</c>, as the <c>key</c> op does for the keyboard.
/// </summary>
internal static class MouseEventBytes
{
    internal const int MouseFormat = 0x4D4F5553; // 'MOUS'
    internal const int MouseStateBytes = 30;
    internal const int MouseEventSize = KeyEventBytes.StateHeaderBytes + MouseStateBytes; // 54

    /// <summary>The buffer a mouse state event is built in (rounded up to the Input System's 4-byte alignment).</summary>
    internal const int MouseBufferBytes = (MouseEventSize + 3) & ~3; // 56

    /// <summary>A whole mouse state event: the pointer at (<paramref name="x"/>, <paramref name="y"/>), no motion, and <paramref name="buttons"/> held.</summary>
    internal static byte[] MouseStateEvent(int deviceId, double time, float x, float y, ushort buttons, ushort clickCount = 0, float scrollY = 0f)
    {
        if (deviceId < 0 || deviceId > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(deviceId));
        var buffer = new byte[MouseBufferBytes];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x0), KeyEventBytes.StateType);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0x4), (ushort)MouseEventSize);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0x6), (ushort)deviceId);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0x8), BitConverter.DoubleToInt64Bits(time));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x10), 0);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x14), MouseFormat);
        int s = KeyEventBytes.StateHeaderBytes;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(s + 0x0), x);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(s + 0x4), y);
        // delta stays zero; scroll.y is the wheel (WHEEL1), scroll.x zero
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(s + 0x14), scrollY);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(s + 0x18), buttons);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(s + 0x1A), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(s + 0x1C), clickCount);
        return buffer;
    }

    internal static ushort Bit(MouseInjectPlan.Button button) => (ushort)(1 << (int)button);
}
