using System;
using Unity.Entities;
using Unity.Mathematics;

namespace Progenitor.Ui;

public sealed partial class PluginHud
{
    /// <summary>
    /// Ping <paramref name="target"/>, an entity of the client world (a chest, a station, a unit): a gold diamond
    /// marker floats over it with "<paramref name="label"/> · 14 m" (the distance from the player's character), and
    /// while it is off screen or behind the camera an arrow on the screen edge points the way to turn. The marker
    /// pulses gently for its first second. <paramref name="seconds"/> is its lifetime (0 or less: until disposed). It
    /// also ends when the entity no longer exists, the client world changes, or this plugin unloads. At most eight
    /// pings show at once; a ninth ends the oldest. Client only; on the server it logs once and returns an inactive ping.
    ///
    /// <para><b>Status: experimental</b> (built and unit-tested; not yet verified live).</para>
    /// </summary>
    public IUiLocatePing Locate(Entity target, string label = "", double seconds = 10)
    {
        if (ServerSide("Locate")) return UiLocatePingHandle.Inert();
        if (target == Entity.Null)
        {
            _ctx.Log.LogWarning("Ui: ctx.Hud.Locate was given Entity.Null; nothing is pinged.");
            return UiLocatePingHandle.Inert();
        }

        return UnityUiRuntime.HudLocate(_ctx.Owner, UiLocateTarget.Entity(target.Index, target.Version), label ?? "", seconds);
    }

    /// <summary>
    /// Ping a fixed world <paramref name="position"/> (meters, the game's world space): the same marker, label,
    /// distance and edge arrow as <see cref="Locate(Entity, string, double)"/>, ending on its time, a world change or
    /// this plugin's unload. <b>Status: experimental.</b>
    /// </summary>
    public IUiLocatePing Locate(float3 position, string label = "", double seconds = 10)
    {
        if (ServerSide("Locate")) return UiLocatePingHandle.Inert();
        if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z))
        {
            _ctx.Log.LogWarning("Ui: ctx.Hud.Locate was given a position that is not a number; nothing is pinged.");
            return UiLocatePingHandle.Inert();
        }

        return UnityUiRuntime.HudLocate(_ctx.Owner, UiLocateTarget.Point(position.x, position.y, position.z), label ?? "", seconds);
    }

    static partial void DropPings(object owner)
    {
        try { UnityUiRuntime.HudDropPings(owner); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: ctx.Hud pings of an unloading plugin were not ended: " + e.Message); }
    }
}
