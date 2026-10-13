#if !PROGENITOR_TESTS
using System;
using Progenitor.Plugins;
using Progenitor.Units;
using Unity.Entities;

namespace Progenitor.Cameras;

/// <summary>
/// <see cref="PluginContext.Camera"/>: the local camera, for cinematic shots. <b>Client only</b>:
/// every member throws <see cref="InvalidOperationException"/> in a server context.
///
/// <para>A shot (<see cref="Orbit(Entity, float, float, float, bool)"/>,
/// <see cref="Orbit(UnitVec3, float, float, float, bool)"/>, <see cref="FreeShot(UnitVec3, float, float, bool)"/>) is written
/// every frame until <see cref="Reset"/>, <see cref="SetMode"/>, another shot, another plugin
/// taking the camera, or unload. While it runs the player's mouse, keyboard and wheel do not move
/// the camera. Angles are degrees; yaw 0 looks along world +Z, yaw 90 along +X, and positive pitch
/// looks down. Calling a shot again retargets it; without <c>instant</c> the game's own smoothing
/// glides to the new values.</para>
///
/// <para>Unload, for every reason, resets the camera to top-down if this plugin still owns it, so
/// a player is never left in the free camera. The last plugin to call a shot or
/// <see cref="SetMode"/> owns the camera. See <c>docs/modules.md</c>, "Camera".</para>
/// </summary>
public sealed class PluginCamera
{
    private readonly PluginContext _ctx;
    private readonly CameraDirector? _director;

    internal PluginCamera(PluginContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        if (!ctx.IsClient) return;

        _director = new CameraDirector(UnityCameraRuntime.Instance, ctx.Name);
        var director = _director;
        ctx.Track(new ActionDisposable(director.Release));
    }

    /// <summary>
    /// The game's current mode, <see cref="CameraMode.None"/> with no client world. A switch
    /// shows here after the next <c>LocalUserSystem</c> update, not in the frame it is requested.
    /// </summary>
    public CameraMode Mode => Director.Mode;

    /// <summary>True while this plugin's shot is being written every frame.</summary>
    public bool IsShotActive => Director.IsShotActive && Director.Owns;

    /// <summary>
    /// Switch to <paramref name="mode"/> and stop any scripted shot; the game's own input then
    /// drives the camera. <see cref="CameraMode.TopDown"/> is the same as <see cref="Reset"/>
    /// for this plugin. Throws before the client world exists.
    /// </summary>
    public void SetMode(CameraMode mode)
    {
        _ctx.EnsureAttached();
        Director.SetMode(mode);
    }

    /// <summary>
    /// Orbit a <b>client-world</b> entity (a server entity id is not valid here) at
    /// <paramref name="distance"/> meters. The anchor follows the entity; the orbit limits are
    /// widened to admit the shot. Switching to a new entity is always a cut.
    /// </summary>
    public void Orbit(Entity target, float distance, float yawDegrees, float pitchDegrees, bool instant = false)
    {
        _ctx.EnsureAttached();
        var director = Director;
        if (!UnityCameraRuntime.ClientEntityExists(target))
            throw new ArgumentException("orbit target " + target.Index + ":" + target.Version + " does not exist in the client world.", nameof(target));
        director.Orbit(OrbitShotPlan.ForEntity(new CameraEntityRef(target.Index, target.Version), distance, yawDegrees, pitchDegrees), instant);
    }

    /// <summary>Orbit a fixed world point at <paramref name="distance"/> meters.</summary>
    public void Orbit(UnitVec3 point, float distance, float yawDegrees, float pitchDegrees, bool instant = false)
    {
        _ctx.EnsureAttached();
        var director = Director;
        director.Orbit(OrbitShotPlan.ForPoint(point, distance, yawDegrees, pitchDegrees), instant);
    }

    /// <summary>
    /// Place the free camera at <paramref name="position"/> looking along
    /// <paramref name="yawDegrees"/> / <paramref name="pitchDegrees"/>. <paramref name="instant"/>
    /// cuts; otherwise the camera glides there with the game's smoothing. The scene is lit for
    /// the point where the view ray meets the floor the local player stands on (or 15 m along the
    /// ray); use the <c>focus</c> overload to name the subject instead.
    /// </summary>
    public void FreeShot(UnitVec3 position, float yawDegrees, float pitchDegrees, bool instant = false)
        => FreeShot(position, yawDegrees, pitchDegrees, null, instant);

    /// <summary>
    /// <see cref="FreeShot(UnitVec3, float, float, bool)"/> with the point the shot is about.
    /// The game anchors its lighting volume and the <c>_g_CameraZoom</c> shader value on the
    /// camera's target, and for a free camera that target is where the view ray meets y = 0, so
    /// any floor away from sea level renders too bright and too blue. The shot puts the target at
    /// <paramref name="focus"/> instead; null derives it as the other overload does. See
    /// <c>docs/modules.md</c>, "Lighting under free shots".
    /// </summary>
    public void FreeShot(UnitVec3 position, float yawDegrees, float pitchDegrees, UnitVec3? focus, bool instant = false)
    {
        _ctx.EnsureAttached();
        var director = Director;
        director.Free(FreeShotPlan.Create(position, yawDegrees, pitchDegrees, focus), instant);
    }

    /// <summary>
    /// A free shot aimed from <paramref name="position"/> at <paramref name="lookAt"/>, which is
    /// also its focus: the scene is lit as the game camera would light it looking at that point.
    /// Pitch is held inside ±89°.
    /// </summary>
    public void FreeShotLookAt(UnitVec3 position, UnitVec3 lookAt, bool instant = false)
    {
        _ctx.EnsureAttached();
        var director = Director;
        CameraMath.RequirePosition(lookAt, nameof(lookAt));
        CameraMath.RequirePosition(position, nameof(position));
        var (yaw, pitch) = CameraMath.LookAngles(position, lookAt);
        director.Free(FreeShotPlan.Create(position, yaw, pitch, lookAt), instant);
    }

    /// <summary>
    /// Stop any shot and return to the top-down camera, whichever plugin owned it. With no client
    /// world it only clears state. Throws on the server, before attach, and after this plugin's
    /// unload released the camera.
    /// </summary>
    public void Reset()
    {
        _ctx.EnsureAttached();
        Director.Reset();
    }

    private CameraDirector Director =>
        _director ?? throw new InvalidOperationException("ctx.Camera is client-only; this plugin context is on the server.");
}
#endif
