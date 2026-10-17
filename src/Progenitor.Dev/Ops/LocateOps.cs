using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>locate</c> (client, main thread): a <c>ctx.Hud.Locate</c> ping at a world point through
/// <see cref="HudLocateBridge"/> (the same presenter plugins use), and the readback of every live ping's last drawn
/// frame (screen position, label text, distance and the character position it was measured from, the camera).
/// Arguments: <see cref="LocateArgs"/>.
/// </summary>
internal static class LocateOps
{
    public static object Run(JsonElement args)
    {
        var r = LocateArgs.Parse(args);
        return HudLocateBridge.Run(r.Action, r.X, r.Y, r.Z, r.Label, r.Seconds, r.Arrival, r.Maps);
    }
}
