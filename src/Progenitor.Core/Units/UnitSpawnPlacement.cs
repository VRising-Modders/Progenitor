#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM;
using Progenitor.Ecs;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Units;

/// <summary>
/// Shared instantiate-placement writes. Dev <c>spawn</c> used to set Translation alone;
/// the entity then snapped to the prefab's authored pose (evidence 108: requested
/// (-2125, 5, -3167.6) ended at (-159.6, 0, -479.6)). Units already writes Translation,
/// LastTranslation, and SpawnTransform.Position (keeping Rotation) and rats stay put.
/// Both callers go through here so a third path cannot drop a field.
/// </summary>
public static class UnitSpawnPlacement
{
    /// <summary>
    /// Write Translation (required), then LastTranslation and SpawnTransform.Position
    /// (best-effort; missing components are skipped). False only when Translation cannot
    /// be written — the same gate <see cref="UnityUnitRuntime.Spawn"/> uses to despawn.
    /// </summary>
    public static bool TryPlace(EntityManager em, Entity entity, float3 position, string? logSource = null)
    {
        if (!TryWriteTranslation(em, entity, position, logSource))
            return false;
        TryWriteLastTranslation(em, entity, position, logSource);
        TryWriteSpawnTransform(em, entity, position, logSource);
        return true;
    }

    /// <summary>
    /// Proven spawn path (evidence §11 / 108): GetComponentData/SetComponentData of
    /// Translation, not a raw blob. TypeSize reads 0 through this interop.
    /// </summary>
    public static bool TryWriteTranslation(EntityManager em, Entity entity, float3 pos, string? logSource = null)
    {
        try
        {
            var translationType = ComponentIndex.Of<Translation>(ComponentType.AccessMode.ReadWrite);
            if (!em.HasComponent(entity, translationType))
                return false;
            var t = em.GetComponentData<Translation>(entity);
            t.Value = pos;
            em.SetComponentData(entity, t);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(Prefix(logSource) + "Translation write failed: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Interpolation / first-frame origin. OffsetTranslationOnSpawn and MoveEntity read
    /// LastTranslation; leaving the prefab copy lets the body
    /// slide back to the authored pose.
    /// </summary>
    public static void TryWriteLastTranslation(EntityManager em, Entity entity, float3 pos, string? logSource = null)
    {
        try
        {
            if (!entity.Has<LastTranslation>(em)) return;
            var v = new LastTranslation { Value = pos };
            entity.Write(em, in v);
        }
        catch (Exception e)
        {
            LogRefuseOnce("LastTranslation", Prefix(logSource) + "LastTranslation write refused: " + e.Message);
        }
    }

    /// <summary>
    /// Home pose. SpawnTransformSystem_OnSpawn copies Translation/Rotation into this
    /// component; the BT reads Position via SpawnPositionToBlackboard.
    /// Read-modify-write so Rotation stays the prefab facing.
    /// </summary>
    public static void TryWriteSpawnTransform(EntityManager em, Entity entity, float3 pos, string? logSource = null)
    {
        try
        {
            if (!entity.Has<SpawnTransform>(em)) return;
            if (!entity.TryRead(em, out SpawnTransform st))
            {
                LogRefuseOnce("SpawnTransformRead", Prefix(logSource) + UnitSpawnWrite.SpawnTransformReadFailed);
                return;
            }
            st.Position = pos;
            entity.Write(em, in st);
        }
        catch (Exception e)
        {
            LogRefuseOnce("SpawnTransform", Prefix(logSource) + "SpawnTransform write refused: " + e.Message);
        }
    }

    /// <summary>Read Translation.Value. False when the entity is gone or has no Translation.</summary>
    public static bool TryReadTranslation(EntityManager em, Entity entity, out float3 pos)
    {
        pos = default;
        try
        {
            if (!entity.Has<Translation>(em)) return false;
            if (entity.TryRead<Translation>(em, out var t))
            {
                pos = t.Value;
                return true;
            }

            var t2 = em.GetComponentData<Translation>(entity);
            pos = t2.Value;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: Translation read failed: " + e.Message);
            return false;
        }
    }

    private static string Prefix(string? logSource) =>
        string.IsNullOrEmpty(logSource) ? "Units: " : logSource + ": ";

    private static readonly object RefuseGate = new();
    private static readonly HashSet<string> LoggedRefusals = new(StringComparer.Ordinal);

    private static void LogRefuseOnce(string key, string message)
    {
        lock (RefuseGate)
        {
            if (!LoggedRefusals.Add(key)) return;
        }

        Plugin.Logger?.LogWarning(message);
    }
}
#endif
