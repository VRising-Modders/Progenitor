using Progenitor.Units;

namespace Progenitor.Cameras;

/// <summary>A client-world entity named by index and version, game-free so the director is testable.</summary>
internal readonly struct CameraEntityRef
{
    public CameraEntityRef(int index, int version)
    {
        Index = index;
        Version = version;
    }

    public int Index { get; }
    public int Version { get; }
    public bool IsNull => Index == 0 && Version == 0;

    public override string ToString() => Index + ":" + Version;
}

/// <summary>What a free shot writes into <c>FreeCameraState</c> each frame, and the point it lights for.</summary>
internal readonly struct FreeShotPlan
{
    public FreeShotPlan(UnitVec3 position, float yawDegrees, float pitchDegrees, UnitVec3? focus = null)
    {
        Position = position;
        YawDegrees = yawDegrees;
        PitchDegrees = pitchDegrees;
        Focus = focus;
    }

    public UnitVec3 Position { get; }
    public float YawDegrees { get; }
    public float PitchDegrees { get; }

    /// <summary>
    /// The caller's focus point (the look-at point of <c>FreeShotLookAt</c>), or null to derive
    /// one from the view ray each frame (<see cref="ResolveFocus"/>).
    /// </summary>
    public UnitVec3? Focus { get; }

    /// <summary>Validates and clamps. Throws <see cref="System.ArgumentOutOfRangeException"/> on a bad value.</summary>
    public static FreeShotPlan Create(UnitVec3 position, float yawDegrees, float pitchDegrees, UnitVec3? focus = null)
    {
        CameraMath.RequirePosition(position, "position");
        CameraMath.RequireAngle(yawDegrees, "yawDegrees");
        CameraMath.RequireAngle(pitchDegrees, "pitchDegrees");
        if (focus is UnitVec3 f) CameraMath.RequirePosition(f, "focus");
        return new FreeShotPlan(position, CameraMath.WrapYaw(yawDegrees), CameraMath.ClampPitch(pitchDegrees), focus);
    }

    /// <summary>
    /// This frame's focus: <see cref="Focus"/> when the caller named one, otherwise
    /// <see cref="CameraMath.FreeFocus"/> of the camera's actual pose (the smoothed
    /// <c>Current</c> the game drew this frame, which differs from the plan while gliding) and
    /// the local player's floor height.
    /// </summary>
    public UnitVec3 ResolveFocus(UnitVec3 cameraPosition, float cameraYaw, float cameraPitch, float? floorY) =>
        Focus ?? CameraMath.FreeFocus(cameraPosition, cameraYaw, cameraPitch, floorY);

    public override string ToString() =>
        "free pos=" + CameraMath.Describe(Position) + " yaw=" + YawDegrees + " pitch=" + PitchDegrees
        + (Focus is UnitVec3 f ? " focus=" + CameraMath.Describe(f) : "");
}

/// <summary>What an orbit shot writes into <c>OrbitCameraState</c> each frame.</summary>
internal readonly struct OrbitShotPlan
{
    public OrbitShotPlan(CameraEntityRef target, UnitVec3 point, float distance, float yawDegrees, float pitchDegrees)
    {
        Target = target;
        Point = point;
        Distance = distance;
        YawDegrees = yawDegrees;
        PitchDegrees = pitchDegrees;
    }

    /// <summary>The entity to orbit; <see cref="CameraEntityRef.IsNull"/> means orbit <see cref="Point"/>.</summary>
    public CameraEntityRef Target { get; }

    /// <summary>The world point to orbit when <see cref="Target"/> is null.</summary>
    public UnitVec3 Point { get; }

    public bool OrbitsEntity => !Target.IsNull;
    public float Distance { get; }
    public float YawDegrees { get; }
    public float PitchDegrees { get; }

    public static OrbitShotPlan ForEntity(CameraEntityRef target, float distance, float yawDegrees, float pitchDegrees)
    {
        if (target.IsNull)
            throw new System.ArgumentException("orbit target is Entity.Null.", nameof(target));
        Validate(distance, yawDegrees, pitchDegrees);
        return new OrbitShotPlan(target, default, distance, CameraMath.WrapYaw(yawDegrees), CameraMath.ClampPitch(pitchDegrees));
    }

    public static OrbitShotPlan ForPoint(UnitVec3 point, float distance, float yawDegrees, float pitchDegrees)
    {
        CameraMath.RequirePosition(point, "point");
        Validate(distance, yawDegrees, pitchDegrees);
        return new OrbitShotPlan(default, point, distance, CameraMath.WrapYaw(yawDegrees), CameraMath.ClampPitch(pitchDegrees));
    }

    private static void Validate(float distance, float yawDegrees, float pitchDegrees)
    {
        CameraMath.RequireDistance(distance, "distance");
        CameraMath.RequireAngle(yawDegrees, "yawDegrees");
        CameraMath.RequireAngle(pitchDegrees, "pitchDegrees");
    }

    public override string ToString() =>
        "orbit " + (OrbitsEntity ? "entity=" + Target : "point=" + CameraMath.Describe(Point))
        + " dist=" + Distance + " yaw=" + YawDegrees + " pitch=" + PitchDegrees;
}

/// <summary>Outcome of one frame's write.</summary>
internal enum CameraWriteResult
{
    /// <summary>The camera state was written.</summary>
    Written,

    /// <summary>The camera for the wanted mode does not exist yet (the switch lands a frame later, or no world).</summary>
    NotReady,

    /// <summary>The write was refused or threw; the shot must stop.</summary>
    Failed,
}

/// <summary>
/// The game half of the camera module. <c>UnityCameraRuntime</c> is the live one; tests supply a
/// fake. Every call is on the main thread.
/// </summary>
internal interface ICameraRuntime
{
    /// <summary>Changes whenever the client world is created or rebuilt; 0 while there is none.</summary>
    long WorldToken { get; }

    /// <summary>The game's current camera type. Lags a request until the next <c>LocalUserSystem</c> update.</summary>
    CameraMode ReadMode();

    /// <summary>
    /// Asks <c>LocalUserSystem</c> for <paramref name="mode"/>. False when there is no system to ask;
    /// may throw from the game call. The orbit anchor is not passed: the shot's per-frame write
    /// sets the look-at target itself.
    /// </summary>
    bool Request(CameraMode mode);

    /// <summary>Writes a free shot, and on <paramref name="instant"/> its LastTarget and Current too.</summary>
    CameraWriteResult WriteFree(in FreeShotPlan plan, bool instant);

    /// <summary>Writes an orbit shot, widening the orbit limits as needed.</summary>
    CameraWriteResult WriteOrbit(in OrbitShotPlan plan, bool instant);

    /// <summary>
    /// Points the camera's lighting target (<c>CameraTarget.Target</c> and the helper entity it
    /// names) at the free shot's focus. Runs just before <c>HybridCameraSystem</c> updates. False
    /// when it could not (camera not up yet, helper missing); lighting is then the game's own for
    /// that frame and the shot itself carries on.
    /// </summary>
    bool FocusFree(in FreeShotPlan plan);

    /// <summary>The same for an orbit shot: the target is the orbited entity's position or the orbited point.</summary>
    bool FocusOrbit(in OrbitShotPlan plan);

    /// <summary>Make sure the per-frame tick and the pre-<c>HybridCameraSystem</c> focus hook are subscribed.</summary>
    void Arm();

    /// <summary>One log line.</summary>
    void Log(string line);
}
