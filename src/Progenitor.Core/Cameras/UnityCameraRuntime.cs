#if !PROGENITOR_TESTS
using System;
using Progenitor.Ai;
using Progenitor.Ecs;
using Progenitor.Hooks;
using Progenitor.Units;
using Progenitor.WorldState;
using ProjectM;
using ProjectM.Network;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Cameras;

/// <summary>
/// Live <see cref="ICameraRuntime"/>: mode requests through <c>ProjectM.LocalUserSystem</c>
/// (managed <c>SystemBase</c>, client world) and per-frame field pokes on the camera entity it
/// names.
///
/// <para><b>Mode switch (verified live 2026-09-25, retail client, via scratch).</b>
/// <c>SetFreeCamera</c>, <c>SetOrbitCamera(Nullable_Unboxed&lt;Entity&gt;,
/// Nullable_Unboxed&lt;Translation&gt;)</c>, <c>SetStaticCamera</c> and <c>SetTopDownCamera</c>
/// post a <c>CameraRequest</c>; <c>LocalUserSystem.UpdateCamera</c> then instantiates the mode's
/// prefab (<c>RootPrefabCollection.TopDownCameraPrefab</c> / <c>OrbitCameraPrefab</c> /
/// <c>FreeCameraPrefab</c>, the free one for both Free and Static), unassigns the old camera, and
/// stores the new instance in <c>_CameraEntity</c> (0x60) and the type in <c>_CameraType</c>
/// (0x68). Both read the previous values until that update has run (the native
/// <c>LocalUserSystem.UpdateCamera</c>). Because every switch makes a fresh instance from the
/// prefab, the limits and sensitivities this runtime changes on a camera die with it: the next
/// mode switch, including the reset to top-down, discards them without a restore.</para>
///
/// <para><b>Writes (metadata + native read, not live-verified).</b> Offsets come from
/// <see cref="Layout"/> (client metadata). The pokes go through the
/// <c>GetComponentDataRawRW</c> overlay (<see cref="AiRuntime.RW{T}"/>), never a sized memcpy.
/// Free: <c>FreeCameraState.Target</c> (and <c>LastTarget</c> / <c>Current</c> on an instant cut),
/// and <c>StaticCamera = true</c>, which makes <c>UpdateInputs</c> skip mouse and keyboard entirely
/// — the same flag the game's own Static mode sets. Orbit: <c>UseCustomLookAtTarget</c> with the
/// shot's entity (or <c>Entity.Null</c> so the anchor falls back to <c>Target.AnchorPosition</c>),
/// <c>FollowTargetObjectRotation = false</c>, the <c>Target</c> distance / yaw / pitch, the
/// <c>OrbitCamera</c> limits widened to admit them, and every input sensitivity plus
/// <c>AutomaticRotationSpeed</c> zeroed so the player's mouse and wheel cannot move the shot.</para>
///
/// <para><b>Focus (native read + metadata; the free-camera case measured live 2026-09-25 through a
/// plugin prototype, this code not yet live-verified).</b> <c>HybridCameraSystem.OnUpdate</c> runs
/// <c>UpdateVolumeAnchor</c> (moves <c>CameraManager._VolumeAnchor</c>, the HDRP volume anchor,
/// to <c>CameraTarget.Target</c>'s <c>Translation</c>) and <c>WriteCameraShaderProperties</c>
/// (<c>_g_CameraZoom</c> = distance from <c>HybridCameraData.Position</c> to that Translation).
/// The free camera's <c>RayCastTarget_Job</c> points <c>CameraTarget</c> at
/// <c>FreeCamera.BonusCustomTargetEntity</c> and moves that helper to where the view ray meets
/// y = 0; <c>LocalUserSystem.UpdateCamera</c> does the same with
/// <c>OrbitCamera.BonusCustomTargetEntity</c> for a point orbit. <see cref="FocusFree"/> and the
/// point case of <see cref="FocusOrbit"/> repeat exactly that with the shot's focus, just before
/// <c>HybridCameraSystem</c>: the camera's own helper (which must carry
/// <c>CustomCameraTarget</c>, so no real entity's Translation is ever moved) gets the focus as its
/// Translation, and <c>CameraTarget.Target</c> names it. An entity orbit only names the orbited
/// entity in <c>CameraTarget.Target</c>, as the game's <c>SetOrbitCamera(entity)</c> does.
/// <c>CameraTarget</c> also feeds the presentation graphs, not only lighting.</para>
/// </summary>
internal sealed unsafe class UnityCameraRuntime : ICameraRuntime
{
    internal static readonly UnityCameraRuntime Instance = new();

    private UnityCameraRuntime() { }

    public long WorldToken
    {
        get
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return 0;
            return ((long)WorldEpoch.Current << 1) | 1L;
        }
    }

    public CameraMode ReadMode()
    {
        var system = LocalUser(out _);
        return system == null ? CameraMode.None : (CameraMode)(int)system._CameraType;
    }

    public bool Request(CameraMode mode)
    {
        var system = LocalUser(out var world);
        if (system == null || world == null) return false;
        switch (mode)
        {
            case CameraMode.TopDown:
                system.SetTopDownCamera();
                return true;
            case CameraMode.Free:
                system.SetFreeCamera();
                return true;
            case CameraMode.Static:
                system.SetStaticCamera();
                return true;
            case CameraMode.Orbit:
                // No target and no point: the shot's per-frame write sets
                // OrbitCameraState.CustomLookAtTarget (entity) or Target.AnchorPosition (point), which
                // overrides whatever the request anchored on. The parameters are value Nullables, so C#
                // null does not compile; both are the empty (HasValue false) form. The game ships both
                // instantiations in the client metadata: Nullable<Entity>..ctor and Nullable<Translation>
                // ..ctor. Whether the live scratch check used this exact empty pair is not
                // recorded; the live test must confirm the orbit camera comes up with it.
                system.SetOrbitCamera(
                    new Il2CppSystem.Nullable_Unboxed<Entity>(),
                    new Il2CppSystem.Nullable_Unboxed<Translation>());
                return true;
            default:
                return false;
        }
    }

    public CameraWriteResult WriteFree(in FreeShotPlan plan, bool instant)
    {
        try
        {
            if (!Camera(CameraMode.Free, out var em, out var camera) || !camera.Has<FreeCameraState>(em))
                return CameraWriteResult.NotReady;

            int need = Layout.FreeCameraStateCurrentOffset + Layout.FreeCameraLerpStride;
            byte* state = AiRuntime.RW<FreeCameraState>(em, camera, need);

            float currentYaw = *(float*)(state + Layout.FreeCameraStateCurrentOffset + Layout.FreeCameraLerpYawOffset);
            float yaw = CameraMath.NearestYaw(currentYaw, plan.YawDegrees);

            *(state + Layout.FreeCameraStateStaticCameraOffset) = 1;
            WriteFreeLerp(state + Layout.FreeCameraStateTargetOffset, plan, yaw);
            if (instant)
            {
                WriteFreeLerp(state + Layout.FreeCameraStateLastTargetOffset, plan, yaw);
                WriteFreeLerp(state + Layout.FreeCameraStateCurrentOffset, plan, yaw);
            }

            return CameraWriteResult.Written;
        }
        catch (Exception e)
        {
            Log("Camera: free shot write failed: " + e.Message);
            return CameraWriteResult.Failed;
        }
    }

    public CameraWriteResult WriteOrbit(in OrbitShotPlan plan, bool instant)
    {
        try
        {
            if (!Camera(CameraMode.Orbit, out var em, out var camera)
                || !camera.Has<OrbitCamera>(em) || !camera.Has<OrbitCameraState>(em))
                return CameraWriteResult.NotReady;

            byte* limits = AiRuntime.RW<OrbitCamera>(em, camera, Layout.OrbitCameraMouseRotationSensitivityOffset + sizeof(float));
            var have = new OrbitLimits(
                *(float*)(limits + Layout.OrbitCameraMinPitchOffset),
                *(float*)(limits + Layout.OrbitCameraMaxPitchOffset),
                *(float*)(limits + Layout.OrbitCameraMinZoomOffset),
                *(float*)(limits + Layout.OrbitCameraMaxZoomOffset));
            var want = CameraMath.Widen(have, plan.Distance, plan.PitchDegrees);
            if (!want.Equals(have))
            {
                *(float*)(limits + Layout.OrbitCameraMinPitchOffset) = want.MinPitch;
                *(float*)(limits + Layout.OrbitCameraMaxPitchOffset) = want.MaxPitch;
                *(float*)(limits + Layout.OrbitCameraMinZoomOffset) = want.MinZoom;
                *(float*)(limits + Layout.OrbitCameraMaxZoomOffset) = want.MaxZoom;
            }

            // Input: OrbitCameraSystem.CameraUpdateJob.UpdateInputs scales every input by these.
            *(float*)(limits + Layout.OrbitCameraZoomSpeedOffset) = 0f;
            *(float*)(limits + Layout.OrbitCameraOverTheShoulderSensitivityOffset) = 0f;
            *(float*)(limits + Layout.OrbitCameraAnchorHeightSensitivityOffset) = 0f;
            *(float*)(limits + Layout.OrbitCameraAutoRotationSensitivityOffset) = 0f;
            *(float*)(limits + Layout.OrbitCameraMouseRotationSensitivityOffset) = 0f;

            int need = Layout.OrbitCameraStateCurrentOffset + Layout.OrbitCameraLerpStride;
            byte* state = AiRuntime.RW<OrbitCameraState>(em, camera, need);

            *(float*)(state + Layout.OrbitCameraStateAutomaticRotationSpeedOffset) = 0f;
            *(state + Layout.OrbitCameraStateUseCustomLookAtTargetOffset) = 1;
            var target = plan.OrbitsEntity
                ? new Entity { Index = plan.Target.Index, Version = plan.Target.Version }
                : Entity.Null;
            AiRuntime.WriteEntity(state + Layout.OrbitCameraStateCustomLookAtTargetOffset, target);
            *(state + Layout.OrbitCameraStateFollowTargetRotationOffset) = 0;

            byte* lerpTarget = state + Layout.OrbitCameraStateTargetOffset;
            float currentYaw = *(float*)(state + Layout.OrbitCameraStateCurrentOffset + Layout.OrbitCameraLerpYawOffset);
            *(float*)(lerpTarget + Layout.OrbitCameraLerpDistanceOffset) = plan.Distance;
            *(float*)(lerpTarget + Layout.OrbitCameraLerpYawOffset) = CameraMath.NearestYaw(currentYaw, plan.YawDegrees);
            *(float*)(lerpTarget + Layout.OrbitCameraLerpPitchOffset) = plan.PitchDegrees;
            if (!plan.OrbitsEntity)
            {
                // With no look-at entity the system anchors on Target.AnchorPosition (+ AnchorHeightOffset)
                // and composes Target.AnchorRotation with the orbit angles; identity keeps yaw world-relative.
                float* anchor = (float*)(lerpTarget + Layout.OrbitCameraLerpAnchorPositionOffset);
                anchor[0] = plan.Point.X;
                anchor[1] = plan.Point.Y;
                anchor[2] = plan.Point.Z;
                float* rotation = (float*)(lerpTarget + Layout.OrbitCameraLerpAnchorRotationOffset);
                rotation[0] = 0f;
                rotation[1] = 0f;
                rotation[2] = 0f;
                rotation[3] = 1f;
            }

            if (instant)
            {
                Buffer.MemoryCopy(lerpTarget, state + Layout.OrbitCameraStateLastTargetOffset, Layout.OrbitCameraLerpStride, Layout.OrbitCameraLerpStride);
                Buffer.MemoryCopy(lerpTarget, state + Layout.OrbitCameraStateCurrentOffset, Layout.OrbitCameraLerpStride, Layout.OrbitCameraLerpStride);
            }

            return CameraWriteResult.Written;
        }
        catch (Exception e)
        {
            Log("Camera: orbit shot write failed: " + e.Message);
            return CameraWriteResult.Failed;
        }
    }

    public bool FocusFree(in FreeShotPlan plan)
    {
        try
        {
            if (!Camera(CameraMode.Free, out var em, out var camera)) return false;

            UnitVec3 focus;
            if (plan.Focus is UnitVec3 named)
            {
                focus = named;
            }
            else
            {
                // The camera's actual pose this frame: FreeCameraSystem has already smoothed Current.
                var position = plan.Position;
                float yaw = plan.YawDegrees, pitch = plan.PitchDegrees;
                byte* state = AiRuntime.RO<FreeCameraState>(em, camera, Layout.FreeCameraStateCurrentOffset + Layout.FreeCameraLerpStride);
                if (state != null)
                {
                    byte* current = state + Layout.FreeCameraStateCurrentOffset;
                    float cy = *(float*)(current + Layout.FreeCameraLerpYawOffset);
                    float cp = *(float*)(current + Layout.FreeCameraLerpPitchOffset);
                    float* cpos = (float*)(current + Layout.FreeCameraLerpPositionOffset);
                    var p = new UnitVec3(cpos[0], cpos[1], cpos[2]);
                    if (CameraMath.IsReasonable(cy) && CameraMath.IsReasonable(cp) && IsReasonable(p))
                    {
                        position = p;
                        yaw = cy;
                        pitch = cp;
                    }
                }

                focus = plan.ResolveFocus(position, yaw, pitch, LocalFloorY(em));
            }

            byte* free = AiRuntime.RO<FreeCamera>(em, camera, Layout.FreeCameraBonusCustomTargetEntityOffset + 8);
            if (free == null) return false;
            return PointTarget(em, camera, AiRuntime.ReadEntity(free + Layout.FreeCameraBonusCustomTargetEntityOffset), focus);
        }
        catch (Exception e)
        {
            FocusThrew("free", e);
            return false;
        }
    }

    public bool FocusOrbit(in OrbitShotPlan plan)
    {
        try
        {
            if (!Camera(CameraMode.Orbit, out var em, out var camera)) return false;

            if (plan.OrbitsEntity)
            {
                // What the game's own SetOrbitCamera(entity) leaves behind: CameraTarget names the
                // orbited entity itself, and no Translation is written. CameraTarget also feeds the
                // presentation graphs (PresentationGraphSystem_Update.CameraTargetEntity;
                // UpdatePresentationPostGraphSystem's cameraTargetSingleton and zoom
                // modifiers), so it must name the real subject.
                var target = new Entity { Index = plan.Target.Index, Version = plan.Target.Version };
                if (target == Entity.Null || !em.Exists(target) || !target.Has<Translation>(em)) return false;
                return SetCameraTarget(em, camera, target);
            }

            byte* orbit = AiRuntime.RO<OrbitCamera>(em, camera, Layout.OrbitCameraBonusCustomTargetEntityOffset + 8);
            if (orbit == null) return false;
            return PointTarget(em, camera, AiRuntime.ReadEntity(orbit + Layout.OrbitCameraBonusCustomTargetEntityOffset), plan.Point);
        }
        catch (Exception e)
        {
            FocusThrew("orbit", e);
            return false;
        }
    }

    public void Arm() => CameraFrame.Arm();

    public void Log(string line) => Plugin.Logger?.LogInfo(line);

    /// <summary>True when the client-world entity exists. The orbit target must be a client entity.</summary>
    internal static bool ClientEntityExists(Entity entity)
    {
        var world = Worlds.Client;
        return world != null && world.IsCreated && entity != Entity.Null && world.EntityManager.Exists(entity);
    }

    // ---- focus helpers ---------------------------------------------------------------------------

    private static bool s_focusThrewLogged;
    private static Entity s_localCharacter;
    private static long s_localCharacterWorld;
    private static int s_localCharacterRetry;

    /// <summary>Re-enable the one-shot focus exception log (called when the hook is armed).</summary>
    internal static void ResetFocusLog() => s_focusThrewLogged = false;

    private static bool IsReasonable(UnitVec3 v) =>
        CameraMath.IsReasonable(v.X) && CameraMath.IsReasonable(v.Y) && CameraMath.IsReasonable(v.Z);

    private static void FocusThrew(string kind, Exception e)
    {
        if (s_focusThrewLogged) return;
        s_focusThrewLogged = true;
        Plugin.Logger?.LogWarning("Camera: " + kind + " shot focus write threw (logged once): " + e.Message);
    }

    /// <summary>
    /// Move the camera's own lighting helper to <paramref name="focus"/> and name it in
    /// <c>CameraTarget.Target</c>, as the game's free-camera ray cast and point orbit do. Refuses a
    /// helper that is not tagged <c>CustomCameraTarget</c>, so a real entity is never moved.
    /// <b>To confirm live:</b> that the helpers carry the tag. The free camera's did (live
    /// 2026-09-25: its <c>CameraTarget.Target</c>, 70751:7, had <c>Translation</c>,
    /// <c>CustomCameraTarget</c> and <c>Simulate</c>); the orbit camera's is unconfirmed, and if it
    /// lacks the tag a point orbit keeps the game's own target (logged once).
    /// </summary>
    private static bool PointTarget(EntityManager em, Entity camera, Entity helper, UnitVec3 focus)
    {
        if (!IsReasonable(focus)) return false;
        if (helper == Entity.Null || !em.Exists(helper)
            || !helper.Has<CustomCameraTarget>(em) || !helper.Has<Translation>(em))
            return false;
        if (AiRuntime.RO<CameraTarget>(em, camera, Layout.CameraTargetTargetOffset + 8) == null) return false;

        helper.Write(em, new Translation { Value = new float3(focus.X, focus.Y, focus.Z) });
        return SetCameraTarget(em, camera, helper);
    }

    /// <summary>Name <paramref name="target"/> in the camera's <c>CameraTarget.Target</c>, writing only on a change.</summary>
    private static bool SetCameraTarget(EntityManager em, Entity camera, Entity target)
    {
        byte* ro = AiRuntime.RO<CameraTarget>(em, camera, Layout.CameraTargetTargetOffset + 8);
        if (ro == null) return false;
        if (AiRuntime.ReadEntity(ro + Layout.CameraTargetTargetOffset) != target)
        {
            byte* rw = AiRuntime.RW<CameraTarget>(em, camera, Layout.CameraTargetTargetOffset + 8);
            AiRuntime.WriteEntity(rw + Layout.CameraTargetTargetOffset, target);
        }

        return true;
    }

    /// <summary>
    /// Height of the local player character's <c>Translation</c> (the floor she stands on), or null
    /// with no local character. The entity is cached per client world. Losing it re-queries at
    /// once; a query that finds nothing waits 60 focus calls (about a second) before the next.
    /// </summary>
    private static float? LocalFloorY(EntityManager em)
    {
        long world = Instance.WorldToken;
        if (world != s_localCharacterWorld)
        {
            s_localCharacterWorld = world;
            s_localCharacter = Entity.Null;
            s_localCharacterRetry = 0;
        }

        if (s_localCharacter != Entity.Null && !em.Exists(s_localCharacter))
        {
            // Lost a character we had (respawn, form change): look again at once.
            s_localCharacter = Entity.Null;
            s_localCharacterRetry = 0;
        }

        if (s_localCharacter == Entity.Null)
        {
            if (s_localCharacterRetry > 0)
            {
                s_localCharacterRetry--;
                return null;
            }

            s_localCharacterRetry = 60;
            var found = Query.Entities(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly));
            if (found.Count == 0) return null;
            s_localCharacter = found[0];
        }

        return s_localCharacter.TryRead(em, out Translation t) && CameraMath.IsReasonable(t.Value.y)
            ? t.Value.y
            : null;
    }

    private static void WriteFreeLerp(byte* lerp, in FreeShotPlan plan, float yaw)
    {
        *(float*)(lerp + Layout.FreeCameraLerpYawOffset) = yaw;
        *(float*)(lerp + Layout.FreeCameraLerpPitchOffset) = plan.PitchDegrees;
        float* position = (float*)(lerp + Layout.FreeCameraLerpPositionOffset);
        position[0] = plan.Position.X;
        position[1] = plan.Position.Y;
        position[2] = plan.Position.Z;
    }

    /// <summary>
    /// The live camera entity when the game reports <paramref name="mode"/> (Free also accepts
    /// Static: both are the free-camera prefab).
    /// </summary>
    private static bool Camera(CameraMode mode, out EntityManager em, out Entity camera)
    {
        em = default;
        camera = Entity.Null;
        var system = LocalUser(out var world);
        if (system == null || world == null) return false;

        var type = (CameraMode)(int)system._CameraType;
        bool matches = mode == CameraMode.Free
            ? type is CameraMode.Free or CameraMode.Static
            : type == mode;
        if (!matches) return false;

        camera = system._CameraEntity;
        em = world.EntityManager;
        return camera != Entity.Null && em.Exists(camera);
    }

    private static LocalUserSystem? LocalUser(out World? world)
    {
        world = Worlds.Client;
        if (world == null || !world.IsCreated) return null;
        try
        {
            return world.GetExistingSystemManaged<LocalUserSystem>();
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// The one <see cref="Plugin.Frame"/> subscription behind every scripted shot. Subscribed while
/// the owning director has a shot, unsubscribed when it returns false, and on Core uninstall.
///
/// <para><b>Timing.</b> <see cref="Plugin.Frame"/> is raised from <c>ProcessHost.Tick</c>, which
/// runs from Bootstrap's <c>FrameTicker.Update</c> (a MonoBehaviour <c>Update</c>) or from the
/// bootstrap systems' update postfixes — in either case inside Unity's <c>Update</c> phase. The
/// camera systems run later in the same frame: <c>CameraPresentationGroup</c> is
/// <c>[UpdateInGroup(PresentationSystemGroup, OrderFirst = true)]</c> (client
/// metadata), and <c>PresentationSystemGroup</c> runs in <c>PreLateUpdate</c>. So a
/// shot written here is the <c>Target</c> the free / orbit job reads that same frame: the job
/// applies input to it (none, with input suppressed), smooths <c>Current</c> toward it, copies it
/// into <c>LastTarget</c>, and <c>HybridCameraSystem</c> hands the result to the Unity camera. A
/// write after the camera systems (the LateFrame slot) would be one frame late and would race
/// the smoothing it is meant to feed. <b>Uncertainty:</b> the ordering is read from attributes and
/// Bootstrap's tick sources, not measured; if a build ever raised <see cref="Plugin.Frame"/> from
/// <c>LateUpdate</c> the shot would lag by a frame but still converge.</para>
///
/// <para><b>Focus hook.</b> Armed together with the frame subscription: a
/// <see cref="SystemHooks"/> <c>Before&lt;HybridCameraSystem&gt;</c> hook that points the lighting
/// target at the shot's focus. <c>FreeCameraSystem</c> and <c>OrbitCameraSystem</c> are
/// <c>[UpdateBefore(HybridCameraSystem)]</c> in <c>CameraPresentationGroup</c> (client
/// metadata), so the hook runs after the free camera's
/// <c>RayCastTarget_Job</c> has moved the helper to y = 0 and before <c>HybridCameraSystem</c>
/// reads it. The game moves the free helper again next frame, so nothing is restored when the
/// hook goes; an orbit shot's camera instance is replaced by the mode request that ends it. The
/// hook exists only while a shot is active and is disposed with the frame subscription.</para>
/// </summary>
internal static class CameraFrame
{
    private static bool _armed;
    private static IDisposable? _focusHook;

    internal static void Arm()
    {
        if (_armed) return;
        Plugin.Frame += OnFrame;
        _armed = true;
        UnityCameraRuntime.ResetFocusLog();
        try
        {
            _focusHook = SystemHooks.Before<HybridCameraSystem>(OnBeforeHybridCamera);
        }
        catch (Exception e)
        {
            _focusHook = null;
            Plugin.Logger?.LogWarning("Camera: the lighting focus hook could not be registered; shots keep the game's own lighting target: " + e.Message);
        }
    }

    internal static void Disarm()
    {
        if (!_armed) return;
        try { Plugin.Frame -= OnFrame; } catch { /* Frame may already be null on uninstall */ }
        try { _focusHook?.Dispose(); } catch { /* hooks may already be torn down on uninstall */ }
        _focusHook = null;
        _armed = false;
    }

    private static void OnBeforeHybridCamera(SystemHookContext hook)
    {
        try
        {
            CameraDirector.FocusOwner();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Camera: focus hook threw: " + e.Message);
        }
    }

    /// <summary>Core uninstall: the owner resets the camera, then the subscription goes.</summary>
    internal static void Uninstall()
    {
        try { CameraDirector.ReleaseAll(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Camera: release on uninstall failed: " + e.Message); }
        Disarm();
    }

    private static void OnFrame()
    {
        bool keep;
        try
        {
            keep = CameraDirector.TickOwner();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Camera: frame tick threw; resetting to the top-down camera: " + e.Message);
            try { CameraDirector.Owner?.Reset(); } catch { /* nothing left to try */ }
            keep = false;
        }

        if (!keep) Disarm();
    }
}
#endif
