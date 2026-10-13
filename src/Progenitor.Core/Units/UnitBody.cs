#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Progenitor.Ai;
using ProjectM;
using Unity.Entities;

namespace Progenitor.Units;

/// <summary>
/// Live half of <c>ctx.Units.SetHitboxScale</c> / <c>SetMoveSpeed</c> (docs/modules.md, "Hitbox scale
/// and move speed"). Server only; every entry point runs on the main thread.
///
/// <para><b>Hitbox.</b> The unit's <c>PhysicsCollider</c> is repointed at a private copy of its own
/// collider blob in which the Hittable child is scaled and the BVH refitted
/// (<see cref="UnitHitboxCodec"/>). Copies are keyed by (original blob, scale), shared between units,
/// allocated Persistent through <see cref="AiRuntime.PrivateBlob(byte[], byte[])"/> and never freed:
/// physics jobs may still read one after its unit is gone. Scale 1 points the unit back at the
/// original, and only when it still points at one of these copies.</para>
///
/// <para><b>Speed.</b> <c>Movement.Speed</c> and the four <c>AiMoveSpeeds</c> values are scaled as a
/// ratio of the factor already applied (<see cref="UnitMoveSpeedMath"/>), tracked per handle.</para>
///
/// <para><b>Persistence.</b> <c>PhysicsCollider</c> is registered by the game's PersistenceV2 as
/// <c>ExcludeComponent(CopyDataFromPrefab)</c> (<c>PersistenceV2Utility..cctor</c>), so a saved unit
/// loads with its prefab's vanilla hitbox. <c>Movement</c> and <c>AiMoveSpeeds</c> carry no exclusion
/// and are saved as they are: a unit restored from a save keeps its scaled speed, with no handle.</para>
/// </summary>
internal static unsafe class UnitBody
{
    private sealed class State
    {
        public float HitboxScale = 1f;
        public readonly UnitMoveSpeedState Speed = new();
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<(long Original, int ScaleBits), long> CopyByKey = new();
    private static readonly Dictionary<long, (long Original, float Scale)> CopyInfo = new();
    private static readonly ConditionalWeakTable<IUnitHandle, State> States = new();

    /// <summary>Delays of the re-checks after a spawn-tick apply or a Set call, the faction write's schedule.</summary>
    internal static readonly double[] CheckDelays = { 1.0, 5.0 };

    private static State StateOf(IUnitHandle handle) => States.GetValue(handle, _ => new State());

    // ---- hitbox ---------------------------------------------------------------------------------

    /// <summary>Point the unit at a collider copy scaled by <paramref name="scale"/>; 1 restores the vanilla collider. Throws on refusal.</summary>
    internal static void SetHitboxScale(IUnitHandle handle, float scale)
    {
        if (!UnitHitboxCodec.IsValidScale(scale) && scale != 1f)
            throw new ArgumentOutOfRangeException(nameof(scale), UnitHitboxCodec.ScaleRangeMessage(scale));
        var (em, entity) = Resolve(handle, "SetHitboxScale");
        byte* field = ColliderField(em, entity);
        long current = *(long*)field;
        long original;
        lock (Gate) original = CopyInfo.TryGetValue(current, out var info) ? info.Original : current;
        if (original == 0) throw new InvalidOperationException("unit " + handle.Entity + " has no collider blob.");

        var state = StateOf(handle);
        if (scale == 1f)
        {
            if (current != original)
            {
                *(long*)field = original;
                Plugin.Logger?.LogInfo("Units: hitbox of " + handle.Entity + " restored to its vanilla collider.");
            }
            state.HitboxScale = 1f;
            return;
        }

        long copy = CopyFor(original, scale, handle.Entity.ToString());
        *(long*)field = copy;
        state.HitboxScale = scale;
    }

    /// <summary>The scale of the collider copy the unit points at now, or 1 when it points at anything else.</summary>
    internal static float GetHitboxScale(IUnitHandle handle)
    {
        var (em, entity) = Resolve(handle, "GetHitboxScale");
        long current = *(long*)ColliderField(em, entity, write: false);
        lock (Gate) return CopyInfo.TryGetValue(current, out var info) ? info.Scale : 1f;
    }

    private static byte* ColliderField(EntityManager em, Entity entity, bool write = true)
    {
        int need = Layout.PhysicsColliderValueOffset + sizeof(long);
        byte* component = write
            ? AiRuntime.RW<Unity.Physics.PhysicsCollider>(em, entity, need)
            : AiRuntime.RO<Unity.Physics.PhysicsCollider>(em, entity, need);
        if (component == null) throw new InvalidOperationException("unit " + AiRuntime.Fmt(entity) + " has no PhysicsCollider.");
        return component + Layout.PhysicsColliderValueOffset;
    }

    private static long CopyFor(long original, float scale, string unit)
    {
        var key = (original, BitConverter.SingleToInt32Bits(scale));
        lock (Gate)
            if (CopyByKey.TryGetValue(key, out long existing)) return existing;

        byte* data = (byte*)original;
        byte* header = data - Layout.BlobAssetHeaderBytes;
        if (*(long*)(header + Layout.BlobAssetHeaderValidationPtrOffset) != original)
            throw new InvalidOperationException("the collider blob of " + unit + " has no valid header; refusing to copy it.");
        byte[] bytes = AiRuntime.CopyOut(data);
        if (!UnitHitboxCodec.TryScale(bytes, scale, out var report, out string why))
            throw new InvalidOperationException("hitbox of " + unit + " cannot be scaled: " + why);
        byte[] newHeader = new ReadOnlySpan<byte>(header, Layout.BlobAssetHeaderBytes).ToArray();
        UnitHitboxCodec.PerturbHash(newHeader, scale);
        long copy = (long)AiRuntime.PrivateBlob(bytes, newHeader);

        lock (Gate)
        {
            CopyByKey[key] = copy;
            CopyInfo[copy] = (original, scale);
        }
        Plugin.Logger?.LogInfo("Units: hitbox copy x" + scale.ToString("0.###", CultureInfo.InvariantCulture)
            + " of collider 0x" + original.ToString("X", CultureInfo.InvariantCulture) + " (" + bytes.Length + " bytes) "
            + report + " (kept for the process; never freed)");
        return copy;
    }

    // ---- move speed -----------------------------------------------------------------------------

    /// <summary>
    /// Scale <c>Movement.Speed</c> and <c>AiMoveSpeeds</c> to <paramref name="multiplier"/> x vanilla.
    /// Returns false when the unit cannot move right now (a value is 0) and the write was deferred to
    /// the next check. Throws when the unit has no <c>Movement</c>.
    /// </summary>
    internal static bool SetMoveSpeed(IUnitHandle handle, float multiplier)
    {
        if (!UnitMoveSpeedMath.IsValidMultiplier(multiplier))
            throw new ArgumentOutOfRangeException(nameof(multiplier), UnitMoveSpeedMath.MultiplierRangeMessage(multiplier));
        var state = StateOf(handle);
        state.Speed.Requested = multiplier;
        return ApplySpeed(handle, state.Speed, reapplyOnly: false);
    }

    /// <summary>The multiplier last requested for this unit (1 when none was).</summary>
    internal static float GetMoveSpeed(IUnitHandle handle) => StateOf(handle).Speed.Requested;

    private static bool ApplySpeed(IUnitHandle handle, UnitMoveSpeedState speed, bool reapplyOnly)
    {
        var (em, entity) = Resolve(handle, "SetMoveSpeed");
        var world = Worlds.Server!;
        byte* movement = AiRuntime.RW<Movement>(em, entity, Layout.MovementSpeedOffset + Layout.ModifiableFloatValueOffset + sizeof(float));
        byte* ai = HasAiMoveSpeeds(em, entity)
            ? AiRuntime.RW<AiMoveSpeeds>(em, entity, Layout.AiMoveSpeedsReturnOffset + Layout.ModifiableFloatValueOffset + sizeof(float))
            : null;

        Entity prefab = AiRuntime.PrefabEntity(world, AiRuntime.PrefabGuidOf(em, entity));
        byte* prefabMovement = prefab == Entity.Null ? null
            : AiRuntime.RO<Movement>(em, prefab, Layout.MovementSpeedOffset + Layout.ModifiableFloatValueOffset + sizeof(float));
        byte* prefabAi = prefab == Entity.Null || ai == null ? null
            : AiRuntime.RO<AiMoveSpeeds>(em, prefab, Layout.AiMoveSpeedsReturnOffset + Layout.ModifiableFloatValueOffset + sizeof(float));

        int count = ai != null ? 5 : 1;
        float** slots = stackalloc float*[5];
        slots[0] = (float*)(movement + Layout.MovementSpeedOffset + Layout.ModifiableFloatValueOffset);
        if (ai != null)
        {
            slots[1] = (float*)(ai + Layout.AiMoveSpeedsWalkOffset + Layout.ModifiableFloatValueOffset);
            slots[2] = (float*)(ai + Layout.AiMoveSpeedsRunOffset + Layout.ModifiableFloatValueOffset);
            slots[3] = (float*)(ai + Layout.AiMoveSpeedsCircleOffset + Layout.ModifiableFloatValueOffset);
            slots[4] = (float*)(ai + Layout.AiMoveSpeedsReturnOffset + Layout.ModifiableFloatValueOffset);
        }
        Span<float> current = stackalloc float[count];
        Span<float> bases = stackalloc float[count];
        for (int i = 0; i < count; i++) current[i] = *slots[i];
        bases[0] = prefabMovement == null ? -1f : *(float*)(prefabMovement + Layout.MovementSpeedOffset + Layout.ModifiableFloatValueOffset);
        for (int i = 1; i < count; i++)
        {
            int offset = i switch
            {
                1 => Layout.AiMoveSpeedsWalkOffset,
                2 => Layout.AiMoveSpeedsRunOffset,
                3 => Layout.AiMoveSpeedsCircleOffset,
                _ => Layout.AiMoveSpeedsReturnOffset,
            };
            bases[i] = prefabAi == null ? -1f : *(float*)(prefabAi + offset + Layout.ModifiableFloatValueOffset);
        }

        Span<float> write = stackalloc float[count];
        Span<bool> doWrite = stackalloc bool[count];
        if (reapplyOnly && !speed.Pending)
        {
            if (!UnitMoveSpeedMath.PlanReapply(current, bases, speed.Applied, write, doWrite)) return true;
            for (int i = 0; i < count; i++) if (doWrite[i]) *slots[i] = write[i];
            Plugin.Logger?.LogWarning("Units: move speed of " + handle.Entity + " was reset after it was set; writing x"
                + speed.Applied.ToString("0.###", CultureInfo.InvariantCulture) + " again.");
            return true;
        }

        var outcome = UnitMoveSpeedMath.Plan(current, bases, speed.Applied, speed.Requested, write, doWrite);
        if (outcome == UnitMoveSpeedOutcome.Deferred) return false;
        if (outcome == UnitMoveSpeedOutcome.Write)
        {
            for (int i = 0; i < count; i++) if (doWrite[i]) *slots[i] = write[i];
            Plugin.Logger?.LogInfo("Units: move speed of " + handle.Entity + " x"
                + speed.Applied.ToString("0.###", CultureInfo.InvariantCulture) + " -> x"
                + speed.Requested.ToString("0.###", CultureInfo.InvariantCulture) + " (Movement.Speed "
                + current[0].ToString("0.###", CultureInfo.InvariantCulture) + " -> "
                + write[0].ToString("0.###", CultureInfo.InvariantCulture) + (ai != null ? ", AiMoveSpeeds too" : "") + ").");
        }
        speed.Applied = speed.Requested;
        return true;
    }

    private static bool HasAiMoveSpeeds(EntityManager em, Entity entity) =>
        em.HasComponent(entity, Ecs.ComponentIndex.Of<AiMoveSpeeds>(ComponentType.AccessMode.ReadOnly));

    // ---- re-checks ------------------------------------------------------------------------------

    /// <summary>
    /// Check again at 1 s and 5 s that the hitbox and speed this handle asked for are still on the unit:
    /// a spawn system may rewrite either, and a deferred speed write is retried. Owned by
    /// <paramref name="owner"/>, so plugin unload drops the posts.
    /// </summary>
    internal static void ScheduleChecks(IUnitHandle handle, object owner)
    {
        int epoch = WorldState.WorldEpoch.Current;
        foreach (double delay in CheckDelays)
        {
            bool last = delay == CheckDelays[CheckDelays.Length - 1];
            Dev.MainThread.PostDelayed(delay, () => Check(handle, epoch, last), owner);
        }
    }

    private static void Check(IUnitHandle handle, int epoch, bool last)
    {
        try
        {
            if (handle.IsDisposed || !handle.Enabled) return;
            // A world reload re-numbers entities: the id may name another unit now.
            if (WorldState.WorldEpoch.IsStale(epoch)) return;
            var world = Worlds.Server;
            if (world == null || !world.IsCreated || !world.EntityManager.Exists(handle.Entity.ToEntity())) return;
            if (!States.TryGetValue(handle, out var state)) return;

            if (state.HitboxScale != 1f && GetHitboxScale(handle) != state.HitboxScale)
            {
                Plugin.Logger?.LogWarning("Units: hitbox of " + handle.Entity + " changed after it was set; writing x"
                    + state.HitboxScale.ToString("0.###", CultureInfo.InvariantCulture) + " again.");
                SetHitboxScale(handle, state.HitboxScale);
            }

            if (state.Speed.Pending || state.Speed.Applied != 1f)
            {
                bool applied = ApplySpeed(handle, state.Speed, reapplyOnly: true);
                if (!applied && last)
                    Plugin.Logger?.LogWarning("Units: move speed x" + state.Speed.Requested.ToString("0.###", CultureInfo.InvariantCulture)
                        + " for " + handle.Entity + " is still waiting: the unit's speed has read 0 since it was set. It stays pending until the next SetMoveSpeed.");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: hitbox/speed re-check of " + handle.Entity + " threw: " + e.Message);
        }
    }

    // ---- shared ---------------------------------------------------------------------------------

    private static (EntityManager Em, Entity Entity) Resolve(IUnitHandle handle, string op)
    {
        Units.EnsureMainThread(op);
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        if (handle.IsDisposed) throw new InvalidOperationException("the unit handle is disposed.");
        if (!handle.Enabled) throw new InvalidOperationException("the unit handle is disabled.");
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) throw new InvalidOperationException(op + " is server-only and there is no server world.");
        var em = world.EntityManager;
        var entity = handle.Entity.ToEntity();
        if (!em.Exists(entity)) throw new InvalidOperationException("unit " + handle.Entity + " no longer exists.");
        return (em, entity);
    }

    /// <summary>Collider copies this generation made (pin-diag / tests of the live side).</summary>
    internal static int CopyCount
    {
        get { lock (Gate) return CopyInfo.Count; }
    }
}
#endif
