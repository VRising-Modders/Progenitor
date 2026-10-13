using System;
using System.Globalization;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>What the bridge's <c>time</c> op was asked to do.</summary>
internal enum TimeAction
{
    /// <summary>Read the world speed, the pause flag and every live lease with its owner.</summary>
    Status,

    /// <summary>Hold the world at <see cref="TimeRequest.Value"/> (replaces the bridge's lease).</summary>
    Speed,

    /// <summary>Hold the world frozen (replaces the bridge's lease).</summary>
    Freeze,

    /// <summary>Drop the bridge's lease; the same as <see cref="Release"/>.</summary>
    Resume,

    /// <summary>Drop the bridge's lease. Other owners' leases are untouched.</summary>
    Release,
}

/// <summary>A parsed <c>time</c> request.</summary>
internal readonly struct TimeRequest
{
    public TimeRequest(TimeAction action, float value)
    {
        Action = action;
        Value = value;
    }

    public TimeAction Action { get; }

    /// <summary>The <c>speed</c> value; 0 for every other action.</summary>
    public float Value { get; }
}

/// <summary>
/// Game-free argument parsing for the debug-bridge <c>time</c> op (world speed), so the test project
/// compiles it. Shapes: <c>{}</c> or <c>{"action":"status"}</c>; <c>{"action":"speed","value":0.25}</c>;
/// <c>{"action":"freeze"}</c>; <c>{"action":"resume"}</c>; <c>{"action":"release"}</c>.
/// <c>speed</c> takes [0.001, 1] like <c>ctx.Time.SlowMotion</c>; 0 is refused (use <c>freeze</c>).
/// </summary>
internal static class TimeArgs
{
    public const float MinSpeed = 0.001f;
    public const float MaxSpeed = 1f;

    public static TimeRequest Parse(JsonElement args)
    {
        string action = "status";
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("action", out var a))
        {
            if (a.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(a.GetString()))
                throw new ArgumentException("args.action must be one of status, speed, freeze, resume, release");
            action = a.GetString()!.Trim().ToLowerInvariant();
        }

        switch (action)
        {
            case "status": return new TimeRequest(TimeAction.Status, 0f);
            case "freeze": return new TimeRequest(TimeAction.Freeze, 0f);
            case "resume": return new TimeRequest(TimeAction.Resume, 0f);
            case "release": return new TimeRequest(TimeAction.Release, 0f);
            case "speed": return new TimeRequest(TimeAction.Speed, ReadSpeed(args));
            default:
                throw new ArgumentException("args.action '" + action + "' is not one of status, speed, freeze, resume, release");
        }
    }

    private static float ReadSpeed(JsonElement args)
    {
        if (!args.TryGetProperty("value", out var v))
            throw new ArgumentException("time speed needs args.value in [0.001, 1] (0.25 is quarter speed); use action freeze for 0");
        double d;
        if (v.ValueKind == JsonValueKind.Number) d = v.GetDouble();
        else if (v.ValueKind == JsonValueKind.String
                 && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) d = parsed;
        else throw new ArgumentException("args.value must be a number in [0.001, 1]");
        float f = (float)d;
        if (double.IsNaN(d) || double.IsInfinity(d) || f < MinSpeed || f > MaxSpeed)
            throw new ArgumentException("args.value " + d.ToString(CultureInfo.InvariantCulture)
                + " is outside [0.001, 1]; use action freeze to stop time");
        return f;
    }
}
