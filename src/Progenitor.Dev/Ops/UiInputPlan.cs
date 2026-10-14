using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// BRIDGEUI1: the game-free half of the <c>ui_input</c> op (<see cref="UiInputOps"/>): argument parsing, target
/// descriptions and the frame timeline each action runs. Tested in <c>BridgeUiInputTests</c>.
/// </summary>
internal static class UiInputPlan
{
    internal enum Action
    {
        Status,
        Move,
        Click,
        Drag,
        Wheel,
        Key,
        Type,
        Focus,
        Release,

        /// <summary>Let go of the buttons a <c>drag</c> with <c>keepDown</c> left held (a click lands if over the press).</summary>
        Up,
    }

    internal const string UsageMessage =
        "ui_input needs args.action: move (alias hover), click, drag, up, wheel, key, type, focus, status or release. "
        + "A target is args.row (a cell row key, with args.cell for one of its cells), args.panel (a shown panel's title) "
        + "or args.x and args.y (screen pixels, origin bottom-left); args.fx / args.fy (0..1 inside the target, default "
        + "the center) and args.dx / args.dy (pixels) adjust the point. click: args.button left|right|middle, "
        + "args.modifiers [\"shift\",\"ctrl\"], args.holdFrames. drag: args.to {row, cell, panel, x, y, fx, fy, dx, dy}, "
        + "args.steps, args.keepDown (leave the button held; then action up). wheel: args.notches (-20..20, not 0; positive up). key: args.key Escape|Enter|Tab|Backspace|… "
        + "type: args.text into the focused kit field. focus: give the kit field at the target the keyboard.";

    internal const int DefaultSettleFrames = 3;
    internal const int MaxSettleFrames = 120;
    internal const int DefaultHoldFrames = 3;
    internal const int MaxHoldFrames = 600;
    internal const int DefaultDragSteps = 12;
    internal const int MaxDragSteps = 600;
    internal const int MaxNotches = 20;
    internal const int AfterFrames = 2;
    internal const int DefaultKeyFrames = 2;
    internal const int MaxTypedChars = 2000;

    /// <summary>Modifier mask bits, as Core's <c>UiClickModifiers</c>.</summary>
    internal const int Shift = 1;

    internal const int Ctrl = 2;

    internal sealed class Target
    {
        internal string? Row { get; init; }
        internal string? Cell { get; init; }
        internal string? Panel { get; init; }
        internal float X { get; init; }
        internal float Y { get; init; }
        internal bool HasPoint { get; init; }
        internal float Fx { get; init; } = 0.5f;
        internal float Fy { get; init; } = 0.5f;
        internal float Dx { get; init; }
        internal float Dy { get; init; }

        internal bool IsEmpty => Row == null && Panel == null && !HasPoint;

        internal Dictionary<string, object?> Describe() => new(StringComparer.Ordinal)
        {
            ["row"] = Row,
            ["cell"] = Cell,
            ["panel"] = Panel,
            ["x"] = HasPoint ? X : null,
            ["y"] = HasPoint ? Y : null,
            ["fx"] = Fx,
            ["fy"] = Fy,
            ["dx"] = Dx,
            ["dy"] = Dy,
        };
    }

    internal sealed class Request
    {
        internal Action Action { get; init; }
        internal Target? At { get; init; }
        internal Target? To { get; init; }

        /// <summary>1 left, 2 right, 4 middle (Core's synthetic button mask).</summary>
        internal int Button { get; init; } = 1;

        internal int Modifiers { get; init; }
        internal int SettleFrames { get; init; } = DefaultSettleFrames;
        internal int HoldFrames { get; init; } = DefaultHoldFrames;
        internal int Steps { get; init; } = DefaultDragSteps;
        internal int Notches { get; init; }
        internal string? Key { get; init; }
        internal int KeyFrames { get; init; } = DefaultKeyFrames;
        internal string? Text { get; init; }
        internal int LeaseMs { get; init; } = 30_000;

        /// <summary>End the lease when the action finishes (the pointer leaves: hovers end, tooltips close).</summary>
        internal bool ReleaseAfter { get; init; }

        /// <summary>Drag: leave the button held at the destination (read or capture the held state, then <c>up</c>).</summary>
        internal bool KeepDown { get; init; }

        internal Dictionary<string, object?> Describe() => new(StringComparer.Ordinal)
        {
            ["action"] = Action.ToString().ToLowerInvariant(),
            ["at"] = At?.Describe(),
            ["to"] = To?.Describe(),
            ["button"] = Action is Action.Click or Action.Drag ? ButtonName(Button) : null,
            ["modifiers"] = ModifierNames(Modifiers),
            ["settleFrames"] = SettleFrames,
            ["holdFrames"] = Action == Action.Click ? HoldFrames : null,
            ["steps"] = Action == Action.Drag ? Steps : null,
            ["notches"] = Action == Action.Wheel ? Notches : null,
            ["key"] = Key,
            ["textLength"] = Text?.Length,
            ["leaseMs"] = LeaseMs,
            ["release"] = ReleaseAfter,
            ["keepDown"] = Action == Action.Drag ? KeepDown : null,
        };
    }

    internal static string ButtonName(int mask) => mask switch { 2 => "right", 4 => "middle", _ => "left" };

    internal static List<string> ModifierNames(int mask)
    {
        var list = new List<string>();
        if ((mask & Shift) != 0) list.Add("shift");
        if ((mask & Ctrl) != 0) list.Add("ctrl");
        return list;
    }

    internal static Request Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException(UsageMessage);
        string? actionText = Text(args, "action");
        if (actionText == null) throw new ArgumentException(UsageMessage);
        Action action = actionText.Trim().ToLowerInvariant() switch
        {
            "status" => Action.Status,
            "move" or "hover" => Action.Move,
            "click" => Action.Click,
            "drag" => Action.Drag,
            "wheel" => Action.Wheel,
            "key" => Action.Key,
            "type" => Action.Type,
            "focus" => Action.Focus,
            "release" => Action.Release,
            "up" => Action.Up,
            _ => throw new ArgumentException("ui_input: args.action '" + actionText + "' is not known. " + UsageMessage),
        };

        int leaseMs = OptInt(args, "leaseMs", 500, 300_000) ?? 30_000;
        bool release = OptBool(args, "release");
        if (action is Action.Status or Action.Release or Action.Up) return new Request { Action = action, LeaseMs = leaseMs, ReleaseAfter = release };
        bool keepDown = OptBool(args, "keepDown");
        if (keepDown && action != Action.Drag) throw new ArgumentException("ui_input: args.keepDown is for drag.");

        Target? at = null;
        if (action is Action.Move or Action.Click or Action.Drag or Action.Wheel or Action.Focus)
        {
            at = ParseTarget(args, "ui_input");
            if (at.IsEmpty) throw new ArgumentException("ui_input: " + actionText + " needs a target (args.row, args.panel, or args.x and args.y). " + UsageMessage);
        }

        int modifiers = ParseModifiers(args);
        if (modifiers != 0 && action is not (Action.Click or Action.Drag or Action.Move))
            throw new ArgumentException("ui_input: args.modifiers is for click, drag and move.");

        int button = 1;
        string? buttonText = Text(args, "button");
        if (buttonText != null)
        {
            if (action is not (Action.Click or Action.Drag)) throw new ArgumentException("ui_input: args.button is for click and drag.");
            button = buttonText.Trim().ToLowerInvariant() switch
            {
                "left" => 1,
                "right" => 2,
                "middle" => 4,
                _ => throw new ArgumentException("ui_input: args.button '" + buttonText + "' is not left, right or middle."),
            };
        }

        int settle = OptInt(args, "settleFrames", 0, MaxSettleFrames) ?? DefaultSettleFrames;
        int hold = OptInt(args, "holdFrames", 1, MaxHoldFrames) ?? DefaultHoldFrames;

        Target? to = null;
        int steps = DefaultDragSteps;
        if (action == Action.Drag)
        {
            if (args.TryGetProperty("to", out var toEl))
            {
                if (toEl.ValueKind != JsonValueKind.Object) throw new ArgumentException("ui_input: args.to is an object {row, cell, panel, x, y, fx, fy, dx, dy}.");
                to = ParseTarget(toEl, "ui_input to");
            }
            else if (args.TryGetProperty("toX", out _) || args.TryGetProperty("toY", out _))
            {
                to = new Target { X = Num(args, "toX", 0, 100_000), Y = Num(args, "toY", 0, 100_000), HasPoint = true, Fx = 0.5f, Fy = 0.5f };
            }

            if (to == null || to.IsEmpty) throw new ArgumentException("ui_input: drag needs args.to (a target) or args.toX and args.toY.");
            steps = OptInt(args, "steps", 1, MaxDragSteps) ?? DefaultDragSteps;
        }

        int notches = 0;
        if (action == Action.Wheel)
        {
            notches = OptInt(args, "notches", -MaxNotches, MaxNotches)
                ?? throw new ArgumentException("ui_input: wheel needs args.notches (-20..20, not 0; positive scrolls up).");
            if (notches == 0) throw new ArgumentException("ui_input: args.notches is not 0.");
        }

        string? key = null;
        int keyFrames = DefaultKeyFrames;
        if (action == Action.Key)
        {
            key = Text(args, "key");
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("ui_input: key needs args.key (Escape, Enter, Tab, Backspace, Delete, Left, Right, Up, Down, Home, End, Space or a letter).");
            keyFrames = OptInt(args, "frames", 1, 120) ?? DefaultKeyFrames;
        }

        string? text = null;
        if (action == Action.Type)
        {
            text = Text(args, "text");
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("ui_input: type needs args.text.");
            if (text!.Length > MaxTypedChars) throw new ArgumentException("ui_input: args.text is at most " + MaxTypedChars + " characters (mods_ui paste takes more).");
        }

        return new Request
        {
            Action = action,
            At = at,
            To = to,
            Button = button,
            Modifiers = modifiers,
            SettleFrames = settle,
            HoldFrames = hold,
            Steps = steps,
            Notches = notches,
            Key = key?.Trim(),
            KeyFrames = keyFrames,
            Text = text,
            LeaseMs = leaseMs,
            ReleaseAfter = release,
            KeepDown = keepDown,
        };
    }

    internal static Target ParseTarget(JsonElement el, string op)
    {
        string? row = Text(el, "row");
        string? cell = Text(el, "cell");
        string? panel = Text(el, "panel");
        bool hasX = el.TryGetProperty("x", out _);
        bool hasY = el.TryGetProperty("y", out _);
        if (hasX != hasY) throw new ArgumentException(op + ": give both x and y.");
        int kinds = (string.IsNullOrWhiteSpace(row) ? 0 : 1) + (string.IsNullOrWhiteSpace(panel) ? 0 : 1) + (hasX ? 1 : 0);
        if (kinds > 1) throw new ArgumentException(op + ": give one of row, panel, or x and y.");
        if (!string.IsNullOrWhiteSpace(cell) && string.IsNullOrWhiteSpace(row)) throw new ArgumentException(op + ": cell needs row.");
        return new Target
        {
            Row = string.IsNullOrWhiteSpace(row) ? null : row!.Trim(),
            Cell = string.IsNullOrWhiteSpace(cell) ? null : cell!.Trim(),
            Panel = string.IsNullOrWhiteSpace(panel) ? null : panel!.Trim(),
            HasPoint = hasX,
            X = hasX ? Num(el, "x", 0, 100_000) : 0f,
            Y = hasY ? Num(el, "y", 0, 100_000) : 0f,
            Fx = el.TryGetProperty("fx", out _) ? Num(el, "fx", 0, 1) : 0.5f,
            Fy = el.TryGetProperty("fy", out _) ? Num(el, "fy", 0, 1) : 0.5f,
            Dx = el.TryGetProperty("dx", out _) ? Num(el, "dx", -100_000, 100_000) : 0f,
            Dy = el.TryGetProperty("dy", out _) ? Num(el, "dy", -100_000, 100_000) : 0f,
        };
    }

    /// <summary><c>modifiers</c>: an array or a <c>+</c>/<c>,</c>-joined string of <c>shift</c> and <c>ctrl</c>.</summary>
    internal static int ParseModifiers(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("modifiers", out var el)) return 0;
        var names = new List<string>();
        if (el.ValueKind == JsonValueKind.String) names.AddRange((el.GetString() ?? "").Split(new[] { '+', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw new ArgumentException("args.modifiers is a list of \"shift\" and \"ctrl\".");
                names.Add(item.GetString() ?? "");
            }
        }
        else throw new ArgumentException("args.modifiers is a list of \"shift\" and \"ctrl\".");

        int mask = 0;
        foreach (var n in names)
        {
            mask |= n.Trim().ToLowerInvariant() switch
            {
                "shift" => Shift,
                "ctrl" or "control" => Ctrl,
                _ => throw new ArgumentException("args.modifiers: '" + n + "' is not shift or ctrl (Alt and Meta change no click in the kit)."),
            };
        }

        return mask;
    }

    // ---- Timeline ----------------------------------------------------------------------------------

    internal enum StepKind
    {
        Move,
        Down,
        Up,
        Wheel,
        Done,
    }

    internal readonly struct Step
    {
        internal Step(int frame, StepKind kind, float x = 0f, float y = 0f)
        {
            Frame = frame;
            Kind = kind;
            X = x;
            Y = y;
        }

        internal int Frame { get; }
        internal StepKind Kind { get; }
        internal float X { get; }
        internal float Y { get; }

        public override string ToString() => Frame + ":" + Kind + (Kind == StepKind.Move ? "(" + X.ToString("F0", CultureInfo.InvariantCulture) + "," + Y.ToString("F0", CultureInfo.InvariantCulture) + ")" : "");
    }

    /// <summary>
    /// The pointer steps of a move, click, drag or wheel, one frame apart where it matters: the pointer settles over the
    /// target before a press (hover logic sees it first), a click holds for <c>HoldFrames</c>, a drag moves one step a
    /// frame and releases a frame after the last, and the reply waits <see cref="AfterFrames"/> after the last step.
    /// </summary>
    internal static List<Step> Timeline(Request r, float x, float y, float toX = 0f, float toY = 0f)
    {
        var steps = new List<Step> { new(0, StepKind.Move, x, y) };
        int f = Math.Max(1, r.SettleFrames);
        switch (r.Action)
        {
            case Action.Move:
                steps.Add(new Step(f, StepKind.Done));
                break;
            case Action.Click:
                steps.Add(new Step(f, StepKind.Down));
                steps.Add(new Step(f + r.HoldFrames, StepKind.Up));
                steps.Add(new Step(f + r.HoldFrames + AfterFrames, StepKind.Done));
                break;
            case Action.Drag:
                steps.Add(new Step(f, StepKind.Down));
                for (int i = 1; i <= r.Steps; i++)
                {
                    float t = i / (float)r.Steps;
                    steps.Add(new Step(f + i, StepKind.Move, x + (toX - x) * t, y + (toY - y) * t));
                }

                if (!r.KeepDown) steps.Add(new Step(f + r.Steps + 1, StepKind.Up));
                steps.Add(new Step(f + r.Steps + 1 + AfterFrames, StepKind.Done));
                break;
            case Action.Wheel:
                steps.Add(new Step(f, StepKind.Wheel));
                // Done waits for the pump to take every notch (each takes two samples); see UiInputOps.
                steps.Add(new Step(f + 2 * Math.Abs(r.Notches) + AfterFrames, StepKind.Done));
                break;
            default:
                steps.Add(new Step(f, StepKind.Done));
                break;
        }

        return steps;
    }

    // ---- Argument helpers --------------------------------------------------------------------------

    internal static string? Text(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static bool OptBool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("args." + name + " is true or false.");
        return v.GetBoolean();
    }

    internal static int? OptInt(JsonElement args, string name, int min, int max)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n) || n < min || n > max)
            throw new ArgumentException("args." + name + " is a whole number from " + min + " to " + max + ".");
        return n;
    }

    internal static float Num(JsonElement args, string name, float min, float max)
    {
        if (!args.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d)
            || double.IsNaN(d) || double.IsInfinity(d) || d < min || d > max)
            throw new ArgumentException("args." + name + " is a number from " + min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture) + ".");
        return (float)d;
    }
}
