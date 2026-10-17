#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// The Dev bridge's <c>locate</c> op. Public only because Core grants <c>Progenitor.Dev</c> no
/// <c>InternalsVisibleTo</c>; not a plugin API. Client, main thread. <c>pin</c> starts a <c>ctx.Hud.Locate</c> ping at
/// a world point (owned by the bridge), <c>status</c> reads every live ping back with what its last drawn frame
/// computed (screen position, label, distance, the character position it was measured from, the camera), and
/// <c>clear</c> ends the bridge's pings.
/// </summary>
public static class HudLocateBridge
{
    private static readonly object Owner = new BridgeOwner();

    private sealed class BridgeOwner
    {
        public override string ToString() => "dev-bridge";
    }

    /// <summary>
    /// Run <paramref name="action"/> (<c>status</c>, <c>pin</c>, <c>clear</c>). <c>pin</c> takes the point
    /// (<paramref name="x"/>, <paramref name="y"/> up, <paramref name="z"/>), its label, lifetime, arrival distance and
    /// whether it shows on the maps. Every action answers with the status after it.
    /// </summary>
    public static Dictionary<string, object?> Run(string action, float x, float y, float z, string? label, double seconds,
        float arrivalDistance, bool showOnMaps)
    {
        if (Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("locate runs on the client (pings are client UI); send it to the client bridge (27341)");
        long? pinned = null;
        string? refused = null;
        int cleared = 0;
        // Every locate request turns the per-frame readback on for a few seconds; production pings never record it.
        UnityUiRuntime.HudLocateWantFrames();
        switch (action)
        {
            case "pin":
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                    throw new ArgumentException("pin needs finite x, y and z (world meters, y up)");
                if (!float.IsFinite(arrivalDistance) || arrivalDistance < 0)
                    throw new ArgumentException("arrival must be 0 or more meters");
                if (double.IsNaN(seconds) || seconds < 0)
                    throw new ArgumentException("seconds must be 0 (until cleared) or more");
                // A ninth ping ends the oldest; a test pin must never push out a plugin's ping.
                if (UnityUiRuntime.HudPingCount >= UiLocateLook.MaxPings)
                {
                    refused = "cap reached (" + UiLocateLook.MaxPings + " pings live); clear or wait for one to end";
                    break;
                }

                var ping = UnityUiRuntime.HudLocate(Owner, UiLocateTarget.Point(x, y, z), label ?? "", seconds);
                ping.ArrivalDistance = arrivalDistance;
                ping.ShowOnMaps = showOnMaps;
                pinned = ping.Serial;
                break;
            case "clear":
                cleared = UnityUiRuntime.HudDropPings(Owner);
                break;
            case "status":
                break;
            default:
                throw new ArgumentException("locate action must be status, pin or clear");
        }

        var result = Status();
        result["action"] = action;
        result["ok"] = refused == null;
        result["refused"] = refused;
        result["pinned"] = pinned;
        result["cleared"] = cleared;
        if (pinned != null)
            result["note"] = "the ping draws from the next frame on; read status again for its screen position and label";
        return result;
    }

    /// <summary>End the bridge's pings (the Dev bridge's unload). Plugin pings are untouched. Main thread.</summary>
    public static int Clear() => Progenitor.Plugin.IsServerProcess ? 0 : UnityUiRuntime.HudDropPings(Owner);

    /// <summary>Every live ping and its last drawn frame.</summary>
    public static Dictionary<string, object?> Status()
    {
        var rows = new List<object?>();
        foreach (var p in UnityUiRuntime.HudPings.Snapshot())
            rows.Add(Row(p));
        return new Dictionary<string, object?>
        {
            ["count"] = rows.Count,
            ["pings"] = rows,
            ["heightOffset"] = UiLocateLook.HeightOffset,
            ["now"] = UnityUiRuntime.HudNow(),
        };
    }

    private static Dictionary<string, object?> Row(UiLocatePingHandle p)
    {
        var t = p.Target;
        var row = new Dictionary<string, object?>
        {
            ["serial"] = p.Serial,
            ["owner"] = p.Owner == null ? null : ReferenceEquals(p.Owner, Owner) ? "dev-bridge" : p.Owner.ToString(),
            ["label"] = p.Label,
            ["target"] = t.IsEntity
                ? new Dictionary<string, object?> { ["entity"] = t.EntityIndex + ":" + t.EntityVersion }
                : new Dictionary<string, object?> { ["x"] = t.X, ["y"] = t.Y, ["z"] = t.Z },
            ["seconds"] = double.IsInfinity(p.Seconds) ? null : p.Seconds,
            ["arrivalDistance"] = p.ArrivalDistance,
            ["showOnMaps"] = p.ShowOnMaps,
        };
        var f = p.LastFrame;
        if (f == null)
        {
            row["drawn"] = false;
            return row;
        }

        row["drawn"] = true;
        row["drawnAt"] = f.At;
        row["labelText"] = f.LabelText;
        row["meters"] = Math.Round(f.Meters, 2);
        row["from"] = new Dictionary<string, object?> { ["x"] = f.FromX, ["y"] = f.FromY, ["z"] = f.FromZ };
        row["character"] = f.CharacterIndex + ":" + f.CharacterVersion;
        row["onScreen"] = f.OnScreen;
        row["screen"] = new Dictionary<string, object?> { ["x"] = Math.Round(f.ScreenX, 1), ["y"] = Math.Round(f.ScreenY, 1) };
        row["viewport"] = new Dictionary<string, object?> { ["x"] = Math.Round(f.ViewportX, 4), ["y"] = Math.Round(f.ViewportY, 4), ["depth"] = Math.Round(f.Depth, 2) };
        row["angleDegrees"] = Math.Round(f.AngleDegrees, 1);
        row["screenSize"] = f.ScreenWidth + "x" + f.ScreenHeight;
        row["cameraPixels"] = f.CameraPixelWidth + "x" + f.CameraPixelHeight;
        row["camera"] = new Dictionary<string, object?>
        {
            ["name"] = f.Camera,
            ["x"] = f.CameraX, ["y"] = f.CameraY, ["z"] = f.CameraZ,
            ["fov"] = f.FieldOfView,
            ["aspect"] = f.Aspect,
        };

        // A plain pinhole projection of the same anchor from the camera's pose: far from the camera's own viewport
        // point means the camera's matrices are not a plain perspective of its transform (or the wrong camera).
        if (!t.IsEntity)
        {
            var pin = UiLocateProjection.PinholeViewport(
                new System.Numerics.Vector3(f.CameraX, f.CameraY, f.CameraZ),
                new System.Numerics.Vector3(f.CameraForwardX, f.CameraForwardY, f.CameraForwardZ),
                new System.Numerics.Vector3(f.CameraUpX, f.CameraUpY, f.CameraUpZ),
                f.FieldOfView, f.Aspect,
                new System.Numerics.Vector3(t.X, t.Y + UiLocateLook.HeightOffset, t.Z));
            row["pinholeViewport"] = new Dictionary<string, object?>
            {
                ["x"] = Math.Round(pin.X, 4), ["y"] = Math.Round(pin.Y, 4), ["depth"] = Math.Round(pin.Z, 2),
            };
        }

        return row;
    }
}
#endif
