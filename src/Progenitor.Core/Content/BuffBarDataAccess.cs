using System;
using ProjectM;
using Unity.Entities;

namespace Progenitor.Content;

/// <summary>
/// Shared client read of the managed <c>ShowOnBuffBarData</c> component. Icon apply and
/// name/description apply both need it; the closed IL2CPP instantiation is
/// <c>GetComponentObject&lt;Il2CppSystem.Object&gt;(Entity, ComponentType)</c> then
/// <c>TryCast</c> (evidence 102: the managed <c>object</c> form throws in its type initializer).
/// </summary>
internal static class BuffBarDataAccess
{
    /// <summary>
    /// Locate <c>ShowOnBuffBarData</c> on <paramref name="clone"/> and return the managed
    /// instance. <paramref name="missReason"/> is set when the component is missing, the
    /// object read fails, or <c>Entries</c> is empty — the caller logs that exact string.
    /// </summary>
    public static bool TryGet(
        EntityManager em,
        Entity clone,
        out ShowOnBuffBarData? data,
        out string missReason)
    {
        data = null;
        missReason = PrefabIconPath.NoBuffBarDataReason;
        if (clone == Entity.Null || !em.Exists(clone))
            return false;

        var dataType = Ecs.ComponentIndex.Of<ShowOnBuffBarData>(ComponentType.AccessMode.ReadWrite);
        if (!em.HasComponent(clone, dataType))
        {
            if (!TryFindComponentByName(em, clone, "ShowOnBuffBarData", out var scanned))
                return false;
            Plugin.Logger?.LogInfo(
                "PrefabRegistry: ShowOnBuffBarData resolved from the clone's component list (TypeIndex "
                + scanned.TypeIndex.Value + "; cached lookup gave " + dataType.TypeIndex.Value + ").");
            dataType = scanned;
        }

        data = TryGetBuffBarData(em, clone, dataType);
        var entries = data?.Entries;
        if (data == null)
        {
            missReason = BuffBarText.UnreadableReason;
            return false;
        }
        if (entries == null || entries.Count == 0)
        {
            missReason = BuffBarText.NoEntriesReason;
            return false;
        }

        missReason = "";
        return true;
    }

    /// <summary>
    /// Copy client HUD fields (icon, header, description, UseParent) from the clone's
    /// <c>ShowOnBuffBarData</c> onto a live instance. The instance holds its own managed
    /// copy made at spawn (evidence 112: a new clone sprite does not reach that copy).
    /// Never removes buffers.
    /// </summary>
    public static void TryCopyHud(EntityManager em, Entity from, Entity to)
    {
        try
        {
            if (!TryGet(em, from, out var src, out _) || src == null) return;
            if (!TryGet(em, to, out var dst, out _) || dst == null) return;
            var srcEntries = src.Entries;
            var dstEntries = dst.Entries;
            if (srcEntries == null || dstEntries == null) return;
            int n = srcEntries.Count;
            if (dstEntries.Count < n) n = dstEntries.Count;
            for (int i = 0; i < n; i++)
            {
                var s = srcEntries[i];
                var d = dstEntries[i];
                if (s == null || d == null) continue;
                d.Icon = s.Icon;
                d.Header = s.Header;
                d.Description = s.Description;
                d.UseParentNameAndDescription = s.UseParentNameAndDescription;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: ShowOnBuffBarData copy threw: " + e.Message);
        }
    }

    /// <summary>
    /// Find a component on <paramref name="entity"/> by its managed type name, the way the
    /// inspection spike does (GetComponentTypes + GetManagedType). Used when the cached
    /// TypeIndex lookup disagrees with the archetype for a managed component.
    /// </summary>
    public static bool TryFindComponentByName(EntityManager em, Entity entity, string name, out ComponentType found)
    {
        found = default;
        try
        {
            var types = em.GetComponentTypes(entity, Unity.Collections.Allocator.Temp);
            try
            {
                for (int i = 0; i < types.Length; i++)
                {
                    string? n = null;
                    try { n = types[i].GetManagedType()?.Name; } catch { /* keep scanning */ }
                    if (n == name)
                    {
                        found = types[i];
                        return true;
                    }
                }
            }
            finally { types.Dispose(); }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: component scan for " + name + " threw: " + e.Message);
        }
        return false;
    }

    private static ShowOnBuffBarData? TryGetBuffBarData(EntityManager em, Entity clone, ComponentType dataType)
    {
        try
        {
            // The closed interop instantiation is GetComponentObject<Il2CppSystem.Object>(Entity, ComponentType);
            // the managed System.Object form has no IL2CPP method store (evidence 102: type initializer throws).
            var boxed = em.GetComponentObject<Il2CppSystem.Object>(clone, dataType);
            if (boxed == null) return null;
            return boxed.TryCast<ShowOnBuffBarData>();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: GetComponentObject(ShowOnBuffBarData) threw: " + e.Message);
            return null;
        }
    }
}
