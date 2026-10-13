#if !PROGENITOR_TESTS
using System;
using ProjectM;
using ProjectM.Behaviours;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Units;

/// <summary>
/// The live half of the behavior steps. Everything that decides <i>what</i> happens lives
/// in <see cref="UnitSequencer"/>; this file only performs the writes.
/// <para>
/// The rule the research settled and this file keeps: <b>never compile a behavior-tree
/// blob.</b> The unit runs a vanilla tree (its prefab's, or the one
/// <see cref="SelectBehaviourTree"/> bound on the spawn tick), the sequencer forces
/// <c>GenericEnemyState.Idle</c> so that tree neither wanders nor fights, and each step is
/// an ordinary component write on top.
/// </para>
/// </summary>
internal sealed unsafe partial class UnityUnitRuntime
{
    public bool Idle(UnitId unitId)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit))
            return false;
        return TryPostBtRequest(em, unit, GenericEnemyState.Idle);
    }

    public bool Face(UnitId unitId, UnitId targetId)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit))
            return false;
        if (!TryAlive(em, targetId, out var target))
        {
            LogRefuseOnce("Face.target", "Units: Face refused: the target entity is gone.");
            return false;
        }

        if (!UnitSpawnPlacement.TryReadTranslation(em, target, out var at))
        {
            LogRefuseOnce("Face.targetPos", "Units: Face refused: the target has no Translation.");
            return false;
        }

        return FaceWorld(em, unit, at);
    }

    public bool Face(UnitId unitId, UnitVec3 position)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit))
            return false;
        return FaceWorld(em, unit, new float3(position.X, position.Y, position.Z));
    }

    /// <summary>
    /// <b>Route taken: a typed <c>Rotation</c> write, not <c>AiMove_Server.ForceLookAtTarget</c>.</b>
    /// <para>
    /// <c>AiForceLookAtTarget</c> has exactly two non-None values,
    /// <c>Approach</c> and <c>Circle</c>, and the game's own tooltips describe them as
    /// "when moving towards or away from the target" and "when in circle pattern": it is a
    /// combat-approach facing <i>mode</i> read by
    /// <c>AiMoveSystem_Server.ShouldForceLookAtTarget(in AiMove_Server)</c>
    /// while the unit is being moved by the AI, not a "turn to face X now"
    /// command. A Face step turns a unit the sequencer has just parked with Idle, so there
    /// is no approach for that mode to modulate; the field that expresses the result is
    /// <c>Rotation</c>. It is also the safer of the two: <c>Rotation</c> is
    /// <c>Unity.Transforms.Rotation { quaternion Value }</c>, C#
    /// <c>unmanaged</c>, so it goes through the toolkit's typed <c>Write&lt;T&gt;</c> and
    /// <c>RawWriteGuard</c> vouches for the length from <c>sizeof</c> — no byte* overlay and
    /// no guessed blob, unlike the bool-carrying <c>AiMove_Server</c>.
    /// </para>
    /// <para>
    /// Not live-verified. If a live run shows the tree snapping the facing back, the next
    /// step is <c>AiMove_Server.FreezeRotationWhenStationary</c> (@0x4D) —
    /// which <see cref="TryWriteAiMoveApproachPosition"/> already clears on GoTo.
    /// </para>
    /// </summary>
    private static bool FaceWorld(EntityManager em, Entity unit, float3 at)
    {
        try
        {
            if (!unit.Has<Rotation>(em))
            {
                LogRefuseOnce("Face.missing", "Units: no Rotation on " + Fmt(unit) + "; Face refused.");
                return false;
            }

            if (!UnitSpawnPlacement.TryReadTranslation(em, unit, out var from))
                return false;

            float dx = at.x - from.x;
            float dz = at.z - from.z;
            if ((dx * dx) + (dz * dz) < 1e-6f)
                return true; // already on top of the target: the current facing is as good as any.

            // Yaw only. Unity's forward is +Z, so the heading is atan2(x, z); the quaternion
            // for a rotation of `yaw` about +Y is (0, sin(yaw/2), 0, cos(yaw/2)). Written out
            // rather than taken from a mathematics helper so nothing depends on which of those
            // statics this interop happens to expose.
            float yaw = MathF.Atan2(dx, dz);
            float half = yaw * 0.5f;
            var rotation = new Rotation { Value = new quaternion(0f, MathF.Sin(half), 0f, MathF.Cos(half)) };
            unit.Write(em, in rotation);
            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("Face.throw", "Units: Face refused on " + Fmt(unit) + ": " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// <b>No server-side route exists on this build, so this always refuses.</b> The
    /// sequencer turns that into <see cref="UnitStepOutcome.Skipped"/> and still holds the
    /// step for its duration, so an Animate in the middle of a patrol costs the timing it
    /// asked for and nothing else.
    /// <para>
    /// Why: the earlier research mapping says to write <c>AnimationState</c>
    /// with a <c>FixedString64Bytes</c> name at offset 0. <b>That is stale.</b> The game's metadata says
    /// <c>AnimationState</c> is <c>double StartTime</c> @0x0 and
    /// <c>AnimationNameHash</c> @0x8 — no string — and it is carried only by
    /// <c>AnimationStateBuffer</c>, which is declared
    /// <c>[ComponentInWorld(new[] { 4 })]</c>, i.e. <c>WorldType.Client</c>.
    /// Units is server-only, so there is no such buffer on the entity to
    /// write. Every other animation type in the metadata (<c>AnimationClipNames</c>,
    /// <c>AnimatorPlayAnimationBuffer</c>, <c>RuntimeAnimationData</c>, …) is Client world or
    /// a <c>MonoBehaviour</c> as well, and there is no behavior-tree leaf that plays a clip.
    /// Writing a bool-carrying struct into a buffer that is not there would be a guess into
    /// live memory, which is exactly what <c>RawWriteGuard</c> exists to stop.
    /// </para>
    /// <para>
    /// The real route is a client-side one and belongs to Effects / Buffs, not to a
    /// server-side unit step. Until it lands, the step is a documented timed no-op.
    /// </para>
    /// </summary>
    public bool Animate(UnitId unitId, string animationName, float seconds, out string? refusal)
    {
        _ = seconds;
        refusal = "Units: Animate('" + animationName
            + "') has no server-side route: AnimationStateBuffer is a Client-world buffer"
            + " (ComponentInWorld{Client} in the game's metadata) and no behavior-tree leaf plays a clip."
            + " The step is held for its duration and reported Skipped.";
        return false;
    }

    /// <summary>
    /// Point <c>BehaviourTreeBinding.PrefabGUID</c> at a vanilla tree.
    /// <para>
    /// <b>Spawn tick only.</b> <c>BehaviourTreeBindingSystem_Spawn</c> is
    /// <c>[UpdateInGroup(typeof(ReactToSpawnGroup))]</c> and its job
    /// reads <c>BehaviourTreeBinding</c> read-only while filling <c>BehaviourTreeInstance</c>
    /// and the <c>BlackboardElement</c> buffer. Units calls this
    /// from <c>SpawnCore</c> in the same call as <c>Instantiate</c>, while the entity still
    /// carries <c>SpawnTag</c> and before that group runs, so the binding the system resolves
    /// is ours. Re-binding a unit that is already past its spawn frame would mean re-adding
    /// <c>SpawnTag</c>, which re-runs <i>every</i> ReactToSpawn system on the entity — so this
    /// refuses instead, and says so. The log line carries <c>spawnTag=</c> so a live run shows
    /// which case it hit.
    /// </para>
    /// </summary>
    public bool SelectBehaviourTree(UnitId unitId, string treeName, int treeGuid, out string? refusal)
    {
        refusal = null;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated)
        {
            refusal = "Units: behavior tree '" + treeName + "' refused: no Server world.";
            return false;
        }

        var em = world.EntityManager;
        if (!TryAlive(em, unitId, out var unit))
        {
            refusal = "Units: behavior tree '" + treeName + "' refused: the unit is gone.";
            return false;
        }

        if (!TryResolveTree(world, em, treeName, treeGuid, out var guid, out refusal))
            return false;

        bool spawnTag;
        try { spawnTag = unit.Has<SpawnTag>(em); }
        catch { spawnTag = false; }

        if (!spawnTag)
        {
            refusal = "Units: behavior tree '" + treeName + "' refused on " + Fmt(unit)
                + ": the spawn frame is over (spawnTag=False) and BehaviourTreeBindingSystem_Spawn"
                + " only binds in ReactToSpawnGroup. Select the tree on Spawn.";
            return false;
        }

        try
        {
            if (!unit.Has<BehaviourTreeBinding>(em))
                unit.Add<BehaviourTreeBinding>(em);

            var binding = new BehaviourTreeBinding { PrefabGUID = guid };
            unit.Write(em, in binding);

            // Read the binding back before claiming anything. A write that did not stick — a
            // structural change under us, a component the archetype refused — used to be
            // reported as a bound tree (R55 finding 7).
            if (!unit.TryRead<BehaviourTreeBinding>(em, out var readBack)
                || readBack.PrefabGUID._Value != guid._Value)
            {
                refusal = "Units: behavior tree '" + treeName + "' guid=" + guid._Value
                    + " did not read back on " + Fmt(unit) + "; keeping the prefab's own tree.";
                return false;
            }

            Plugin.Logger?.LogInfo("Units: behavior tree '" + treeName + "' guid=" + guid._Value
                + " written on " + Fmt(unit) + " spawnTag=True");
            VerifyBindingNextFrame(unit, treeName, guid);
            return true;
        }
        catch (Exception e)
        {
            refusal = "Units: behavior tree '" + treeName + "' write refused on " + Fmt(unit) + ": " + e.Message;
            return false;
        }
    }

    /// <summary>
    /// One deferred read, the frame after the write, so the claim is observable rather than
    /// asserted (R55 finding 7). <c>BehaviourTreeBindingSystem_Spawn</c> fills
    /// <c>BehaviourTreeInstance.TreeEntity</c> from the binding while
    /// the entity is still in <c>ReactToSpawnGroup</c>; the <c>SpawnTag</c> gate above is a
    /// proxy for "that group has not run yet" and nothing in the metadata or the research
    /// establishes that the tag is removed by that group, so this is what tells a live run
    /// which case it actually hit. It only logs: the selection is a preference, and re-binding
    /// is refused anyway, so there is nothing to retry.
    /// </summary>
    private static void VerifyBindingNextFrame(Entity unit, string treeName, PrefabGUID guid)
    {
        try
        {
            Dev.MainThread.Post(() =>
            {
                try
                {
                    var world = Worlds.Server;
                    if (world == null || !world.IsCreated) return;
                    var em = world.EntityManager;
                    if (!em.Exists(unit)) return;

                    if (unit.TryRead<BehaviourTreeInstance>(em, out var instance)
                        && instance.TreeEntity != Entity.Null)
                    {
                        Plugin.Logger?.LogInfo("Units: behavior tree '" + treeName + "' guid=" + guid._Value
                            + " bound on " + Fmt(unit) + " (BehaviourTreeInstance.TreeEntity="
                            + instance.TreeEntity.Index + ")");
                        return;
                    }

                    Plugin.Logger?.LogWarning("Units: behavior tree '" + treeName + "' guid=" + guid._Value
                        + " was written but not bound on " + Fmt(unit)
                        + ": BehaviourTreeInstance.TreeEntity is still null one frame later, so"
                        + " BehaviourTreeBindingSystem_Spawn did not pick it up and the unit is on"
                        + " its prefab's own tree.");
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Units: behavior tree read-back threw: " + e.Message);
                }
            });
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: behavior tree read-back could not be queued: " + e.Message);
        }
    }

    /// <summary>
    /// Resolve the tree name / GUID and refuse anything whose prefab is not actually a tree.
    /// A <c>BehaviourTree</c> component
    /// (<c>BlobAssetReference&lt;BehaviourTreeBlob&gt;</c>, server world) is what makes a prefab
    /// one; binding a GUID without it would leave the unit with an instance pointing at
    /// nothing.
    /// </summary>
    private static bool TryResolveTree(
        World world, EntityManager em, string treeName, int treeGuid, out PrefabGUID guid, out string? refusal)
    {
        guid = default;
        refusal = null;
        bool haveGuid = false;

        if (!string.IsNullOrWhiteSpace(treeName)
            && !int.TryParse(treeName, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            try
            {
                if (PrefabLookup.TryGetGuidByName(world, treeName, out guid))
                    haveGuid = true;
            }
            catch (Exception e)
            {
                refusal = "Units: behavior tree lookup '" + treeName + "' threw: " + e.Message;
                return false;
            }
        }

        if (!haveGuid && treeGuid != 0)
        {
            guid = new PrefabGUID(treeGuid);
            haveGuid = true;
        }

        if (!haveGuid)
        {
            refusal = "Units: no behavior tree named '" + treeName + "' on this server; keeping the prefab's own tree.";
            return false;
        }

        Entity prefab;
        try
        {
            if (!PrefabLookup.TryGetEntity(world, guid, out prefab) || prefab == Entity.Null || !em.Exists(prefab))
            {
                refusal = "Units: behavior tree guid=" + guid._Value
                    + " is not in _PrefabGuidToEntityMap; keeping the prefab's own tree.";
                return false;
            }
        }
        catch (Exception e)
        {
            refusal = "Units: behavior tree guid=" + guid._Value + " lookup threw: " + e.Message;
            return false;
        }

        try
        {
            if (!prefab.Has<BehaviourTree>(em))
            {
                refusal = "Units: prefab '" + treeName + "' (guid=" + guid._Value
                    + ") has no BehaviourTree component; refusing to bind it as a tree.";
                return false;
            }
        }
        catch (Exception e)
        {
            refusal = "Units: behavior tree check on guid=" + guid._Value + " threw: " + e.Message;
            return false;
        }

        return true;
    }
}
#endif
