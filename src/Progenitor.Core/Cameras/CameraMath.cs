using System;
using System.Globalization;
using Progenitor.Units;

namespace Progenitor.Cameras;

/// <summary>
/// Game-free validation and angle arithmetic for scripted camera shots.
///
/// <para><b>Units.</b> Every angle here is in degrees, because that is what the game stores:
/// <c>FreeCameraSystem.CameraUpdateJob.UpdateCamera</c> and
/// <c>OrbitCameraSystem.CameraUpdateJob.SetCameraPositionRotation</c> multiply the stored Yaw and
/// Pitch by <c>0.017453292</c> before building the rotation (read in the native code). Nothing converts to
/// radians on the way in.</para>
///
/// <para><b>Directions.</b> The game builds <c>quaternion.Euler(pitch, yaw, 0)</c>, so yaw 0 looks
/// along world +Z, yaw 90 along +X, and a positive pitch tilts the view <i>down</i>. An orbit
/// camera sits <c>distance</c> behind that direction from its anchor, so positive orbit pitch puts
/// the camera above the anchor looking down on it.</para>
/// </summary>
internal static class CameraMath
{
    /// <summary>
    /// <c>TopdownCameraSystem.MAX_REASONABLE_VALUE</c> (client metadata). The game
    /// rejects camera values at or above this; every position, angle and distance is held below it.
    /// </summary>
    public const float MaxReasonable = 100000f;

    /// <summary>
    /// Pitch is held inside ±89°. At ±90 the view looks straight along the up axis and yaw stops
    /// meaning anything. The free camera's own mouse input clamps at ±80
    /// (<c>FreeCameraSystem.CameraUpdateJob.MinimumRotationPitch</c> / <c>MaximumRotationPitch</c>,
    /// client metadata), but a scripted shot runs with that input switched off.
    /// </summary>
    public const float PitchLimit = 89f;

    /// <summary>Closest an orbit shot may sit to its anchor, in meters.</summary>
    public const float MinOrbitDistance = 0.1f;

    /// <summary>True when <paramref name="value"/> is finite and inside the game's guard.</summary>
    public static bool IsReasonable(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) < MaxReasonable;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> unless every component is reasonable.</summary>
    public static void RequirePosition(UnitVec3 position, string name)
    {
        if (!IsReasonable(position.X) || !IsReasonable(position.Y) || !IsReasonable(position.Z))
            throw new ArgumentOutOfRangeException(name, Describe(position),
                "camera position must be finite with every component below " + Fmt(MaxReasonable) + ".");
    }

    /// <summary>Throws unless <paramref name="degrees"/> is finite and inside the game's guard.</summary>
    public static void RequireAngle(float degrees, string name)
    {
        if (!IsReasonable(degrees))
            throw new ArgumentOutOfRangeException(name, degrees,
                "camera angle must be a finite number of degrees below " + Fmt(MaxReasonable) + ".");
    }

    /// <summary>Throws unless <paramref name="distance"/> is finite, at least <see cref="MinOrbitDistance"/>, and below the guard.</summary>
    public static void RequireDistance(float distance, string name)
    {
        if (!IsReasonable(distance) || distance < MinOrbitDistance)
            throw new ArgumentOutOfRangeException(name, distance,
                "orbit distance must be finite, at least " + Fmt(MinOrbitDistance) + " m and below " + Fmt(MaxReasonable) + ".");
    }

    /// <summary>Pitch clamped to ±<see cref="PitchLimit"/>. Call after <see cref="RequireAngle"/>.</summary>
    public static float ClampPitch(float degrees) =>
        degrees > PitchLimit ? PitchLimit : degrees < -PitchLimit ? -PitchLimit : degrees;

    /// <summary>Yaw wrapped into <c>(-180, 180]</c>.</summary>
    public static float WrapYaw(float degrees)
    {
        if (!IsReasonable(degrees)) return 0f;
        double wrapped = degrees % 360.0;
        if (wrapped <= -180.0) wrapped += 360.0;
        else if (wrapped > 180.0) wrapped -= 360.0;
        return (float)wrapped;
    }

    /// <summary>
    /// The yaw equal to <paramref name="target"/> modulo 360 that is closest to
    /// <paramref name="current"/>. The game smooths yaw as a plain float, so writing 10 while the
    /// camera reads 350 would swing it 340° the long way round; this writes 370 instead. A
    /// <paramref name="current"/> that is not reasonable (an uninitialized camera) returns the
    /// wrapped target.
    /// </summary>
    public static float NearestYaw(float current, float target)
    {
        float wrapped = WrapYaw(target);
        if (!IsReasonable(current)) return wrapped;
        double delta = WrapYaw((float)(wrapped - WrapYaw(current)));
        double result = current + delta;
        return IsReasonable((float)result) ? (float)result : wrapped;
    }

    /// <summary>
    /// Yaw and pitch, in degrees, that point a camera at <paramref name="from"/> toward
    /// <paramref name="to"/> under the game's convention (yaw 0 = +Z, positive pitch looks down).
    /// Throws when the two points coincide.
    /// </summary>
    public static (float Yaw, float Pitch) LookAngles(UnitVec3 from, UnitVec3 to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double dz = to.Z - from.Z;
        double horizontal = Math.Sqrt((dx * dx) + (dz * dz));
        if (horizontal < 1e-4 && Math.Abs(dy) < 1e-4)
            throw new ArgumentException("the camera position and the look-at point are the same.");
        double yaw = horizontal < 1e-4 ? 0.0 : Math.Atan2(dx, dz) * (180.0 / Math.PI);
        double pitch = Math.Atan2(-dy, horizontal) * (180.0 / Math.PI);
        return ((float)yaw, ClampPitch((float)pitch));
    }

    /// <summary>
    /// Meters along the view ray used as a free shot's focus when the ray is near level
    /// (<see cref="NearLevelPitch"/>), points up, or there is no floor height. About the distance
    /// from the game's top-down camera to the player it frames (<c>_g_CameraZoom</c> read 14.9
    /// live, 2026-09-25).
    /// </summary>
    public const float FallbackFocusDistance = 15f;

    /// <summary>
    /// Views pitched down less than this many degrees use <see cref="FallbackFocusDistance"/>: a
    /// near-level ray meets the floor hundreds of meters out, and anchoring the lighting there
    /// would reproduce the bug the focus exists to fix. At 5 degrees a camera 5 m above the floor
    /// meets it 57 m away.
    /// </summary>
    public const float NearLevelPitch = 5f;

    /// <summary>
    /// Closest the derived focus may sit to the camera, along the ray, so it never collapses onto
    /// the camera (a camera at or just above the floor plane).
    /// </summary>
    public const float MinFocusDistance = 2f;

    /// <summary>
    /// Farthest the derived focus may sit along a steep ray. A floor hit beyond this (a very high
    /// crane shot) is clamped along the ray rather than dropped, so the anchor stays in the
    /// direction of the floor instead of jumping to mid-air 15 m from the camera.
    /// </summary>
    public const float MaxFocusDistance = 1000f;

    /// <summary>
    /// Unit view direction for <paramref name="yawDegrees"/> / <paramref name="pitchDegrees"/>
    /// under the game's convention: <c>quaternion.Euler(pitch, yaw, 0)</c> applied to +Z, so yaw 0
    /// looks along +Z, yaw 90 along +X, and positive pitch looks down. The inverse of
    /// <see cref="LookAngles"/>.
    /// </summary>
    public static UnitVec3 Forward(float yawDegrees, float pitchDegrees)
    {
        double yaw = yawDegrees * (Math.PI / 180.0);
        double pitch = pitchDegrees * (Math.PI / 180.0);
        double cosPitch = Math.Cos(pitch);
        return new UnitVec3(
            (float)(Math.Sin(yaw) * cosPitch),
            (float)-Math.Sin(pitch),
            (float)(Math.Cos(yaw) * cosPitch));
    }

    /// <summary>
    /// Where the ray from <paramref name="origin"/> along <paramref name="direction"/> meets the
    /// horizontal plane <c>y = <paramref name="planeY"/></c>. False when the ray is parallel to
    /// the plane, points away from it, or meets it farther than <paramref name="maxDistance"/>
    /// (measured along <paramref name="direction"/>, which need not be unit length: the distance
    /// is scaled by its length). A ray starting on the plane hits at its origin.
    /// </summary>
    public static bool TryRayPlaneY(UnitVec3 origin, UnitVec3 direction, float planeY, float maxDistance, out UnitVec3 hit)
    {
        hit = default;
        double length = Math.Sqrt((direction.X * (double)direction.X) + (direction.Y * (double)direction.Y) + (direction.Z * (double)direction.Z));
        if (length < 1e-6 || Math.Abs(direction.Y) < 1e-6 * length) return false;
        double t = (planeY - origin.Y) / (double)direction.Y;
        if (t < 0.0 || t * length > maxDistance) return false;
        hit = new UnitVec3(
            (float)(origin.X + (direction.X * t)),
            planeY,
            (float)(origin.Z + (direction.Z * t)));
        return true;
    }

    /// <summary>
    /// The focus point of a free shot at <paramref name="position"/> looking along
    /// <paramref name="yawDegrees"/> / <paramref name="pitchDegrees"/> when the caller named none.
    /// A view pitched down at least <see cref="NearLevelPitch"/> focuses where its ray meets the
    /// floor plane <c>y = <paramref name="floorY"/></c> (the height of the floor the local player
    /// stands on), with the distance along the ray clamped to
    /// [<see cref="MinFocusDistance"/>, <see cref="MaxFocusDistance"/>]. A near-level or upward view,
    /// a floor plane behind the ray (camera below the floor), or no floor height focuses
    /// <see cref="FallbackFocusDistance"/> meters along the ray. The game's own free camera uses
    /// the plane <c>y = 0</c> instead (<c>FreeCameraSystem.RayCastTarget_Job</c>), which is what
    /// makes free shots light wrongly on any floor that is not at sea level; see docs/modules.md,
    /// "Lighting under free shots".
    /// </summary>
    public static UnitVec3 FreeFocus(UnitVec3 position, float yawDegrees, float pitchDegrees, float? floorY)
    {
        var forward = Forward(yawDegrees, pitchDegrees);
        float distance = FallbackFocusDistance;
        if (floorY is float y && IsReasonable(y) && pitchDegrees >= NearLevelPitch
            && TryRayPlaneY(position, forward, y, float.MaxValue, out var hit))
        {
            double dx = hit.X - position.X, dy = hit.Y - position.Y, dz = hit.Z - position.Z;
            double along = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (along >= MinFocusDistance && along <= MaxFocusDistance) return hit;
            distance = (float)Math.Min(Math.Max(along, MinFocusDistance), MaxFocusDistance);
        }

        return new UnitVec3(
            position.X + (forward.X * distance),
            position.Y + (forward.Y * distance),
            position.Z + (forward.Z * distance));
    }

    /// <summary>
    /// Orbit limits widened just enough to admit <paramref name="distance"/> and
    /// <paramref name="pitch"/>. <c>OrbitCameraSystem</c> clamps the target distance to
    /// <c>[MinZoom, MaxZoom]</c> and the pitch to <c>[MinPitch, MaxPitch]</c> every frame,
    /// input or not, so a shot outside the vanilla limits would be pulled back without this.
    /// Limits that already contain the shot are returned unchanged.
    /// </summary>
    public static OrbitLimits Widen(OrbitLimits have, float distance, float pitch) =>
        new(
            Math.Min(have.MinPitch, pitch),
            Math.Max(have.MaxPitch, pitch),
            Math.Min(have.MinZoom, distance),
            Math.Max(have.MaxZoom, distance));

    internal static string Describe(UnitVec3 v) =>
        "(" + Fmt(v.X) + ", " + Fmt(v.Y) + ", " + Fmt(v.Z) + ")";

    private static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>The four <c>OrbitCamera</c> limits the orbit system clamps against each frame.</summary>
internal readonly struct OrbitLimits : IEquatable<OrbitLimits>
{
    public OrbitLimits(float minPitch, float maxPitch, float minZoom, float maxZoom)
    {
        MinPitch = minPitch;
        MaxPitch = maxPitch;
        MinZoom = minZoom;
        MaxZoom = maxZoom;
    }

    public float MinPitch { get; }
    public float MaxPitch { get; }
    public float MinZoom { get; }
    public float MaxZoom { get; }

    public bool Equals(OrbitLimits other) =>
        MinPitch.Equals(other.MinPitch) && MaxPitch.Equals(other.MaxPitch)
        && MinZoom.Equals(other.MinZoom) && MaxZoom.Equals(other.MaxZoom);

    public override bool Equals(object? obj) => obj is OrbitLimits other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(MinPitch, MaxPitch, MinZoom, MaxZoom);
}
