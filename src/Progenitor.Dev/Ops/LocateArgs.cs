using System;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>A parsed <c>locate</c> request.</summary>
internal sealed record LocateRequest(string Action, float X, float Y, float Z, string Label, double Seconds,
    float Arrival, bool Maps);

/// <summary>
/// Game-free argument parsing for the debug-bridge <c>locate</c> op (client), so the test project compiles it.
/// Shapes: <c>{}</c> / <c>{"action":"status"}</c>; <c>{"action":"clear"}</c>;
/// <c>{"action":"pin","x":-1444,"y":5.02,"z":-740,"label":"Minerals","seconds":60,"arrival":3,"maps":true}</c>
/// (x, y up, z: the game's world meters, as <c>Translation</c>; seconds default 30, 0 = until cleared; arrival default
/// 0 = never; maps default false). <c>position</c> as <c>[x, y, z]</c> may replace x, y, z.
/// </summary>
internal static class LocateArgs
{
    internal const double DefaultSeconds = 30;

    public static LocateRequest Parse(JsonElement args)
    {
        string action = "status";
        float? x = null, y = null, z = null;
        string label = "";
        double seconds = DefaultSeconds;
        float arrival = 0f;
        bool maps = false;
        bool pinOnly = false;
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("action", out var a))
            {
                if (a.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(a.GetString()))
                    throw new ArgumentException("args.action must be one of status, pin, clear");
                action = a.GetString()!.Trim().ToLowerInvariant();
            }

            if (args.TryGetProperty("position", out var pos))
            {
                if (pos.ValueKind != JsonValueKind.Array || pos.GetArrayLength() != 3)
                    throw new ArgumentException("args.position must be [x, y, z]");
                x = Number(pos[0], "position[0]");
                y = Number(pos[1], "position[1]");
                z = Number(pos[2], "position[2]");
                pinOnly = true;
            }

            if (args.TryGetProperty("x", out var vx)) { x = Number(vx, "x"); pinOnly = true; }
            if (args.TryGetProperty("y", out var vy)) { y = Number(vy, "y"); pinOnly = true; }
            if (args.TryGetProperty("z", out var vz)) { z = Number(vz, "z"); pinOnly = true; }
            if (args.TryGetProperty("label", out var l))
            {
                if (l.ValueKind != JsonValueKind.String) throw new ArgumentException("args.label must be a string");
                label = l.GetString() ?? "";
                pinOnly = true;
            }

            if (args.TryGetProperty("seconds", out var s))
            {
                seconds = Number(s, "seconds");
                if (seconds < 0) throw new ArgumentException("args.seconds must be 0 (until cleared) or more");
                pinOnly = true;
            }

            if (args.TryGetProperty("arrival", out var ar))
            {
                arrival = Number(ar, "arrival");
                if (arrival < 0) throw new ArgumentException("args.arrival must be 0 or more meters");
                pinOnly = true;
            }

            if (args.TryGetProperty("maps", out var m))
            {
                if (m.ValueKind != JsonValueKind.True && m.ValueKind != JsonValueKind.False)
                    throw new ArgumentException("args.maps must be true or false");
                maps = m.ValueKind == JsonValueKind.True;
                pinOnly = true;
            }
        }

        if (action != "status" && action != "pin" && action != "clear")
            throw new ArgumentException("args.action '" + action + "' is not one of status, pin, clear");
        if (action != "pin")
        {
            if (pinOnly) throw new ArgumentException("position, label, seconds, arrival and maps apply to action pin only");
            return new LocateRequest(action, 0, 0, 0, "", 0, 0, false);
        }

        if (x == null || y == null || z == null)
            throw new ArgumentException("locate pin needs x, y and z (or position [x, y, z]): world meters, y up");
        return new LocateRequest(action, x.Value, y.Value, z.Value, label, seconds, arrival, maps);
    }

    private static float Number(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out double d) || double.IsNaN(d) || double.IsInfinity(d)
            || Math.Abs(d) > 1e6)
            throw new ArgumentException("args." + name + " must be a finite number");
        return (float)d;
    }
}
