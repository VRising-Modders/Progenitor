using System;
using Unity.Entities;

namespace Progenitor.Ecs;

/// <summary>
/// Looks up singleton entities without <c>EntityQuery.GetSingleton&lt;T&gt;</c>, which is the
/// generic that hard-crashed the prefab spike on <c>ItemKeyLookup</c>. The safe path is a
/// query with <see cref="EntityQueryOptions.IncludeSystems"/> (unmanaged-system singletons live
/// on system entities), <see cref="EntityQuery.GetSingletonEntity"/> when the count is exactly
/// one, then raw component access by <see cref="TypeIndex"/>.
/// </summary>
public static unsafe class Singletons
{
    /// <summary>
    /// Finds the unique entity that has <paramref name="componentType"/>, including system
    /// entities and disabled entities. Returns false when the count is not exactly one.
    /// Generic-instantiation-safe: the query is built from a <see cref="ComponentType"/> value
    /// and resolved with non-generic <see cref="EntityQuery.GetSingletonEntity"/>.
    /// </summary>
    public static bool TryGetEntity(EntityManager em, ComponentType componentType, out Entity entity)
    {
        entity = default;
        EntityQuery query = default;
        bool created = false;
        try
        {
            query = Query.Build(em, includeDisabled: true, includeSystems: true, componentType);
            created = true;
            if (query.CalculateEntityCount() != 1)
                return false;
            entity = query.GetSingletonEntity();
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Singletons.TryGetEntity({componentType}) failed: {e}");
            entity = default;
            return false;
        }
        finally
        {
            if (created)
                query.Dispose();
        }
    }

    /// <summary>
    /// Pointer to the singleton's component bytes, valid until the next structural change.
    /// Generic-instantiation-safe: <see cref="TryGetEntity"/> plus
    /// <see cref="EntityManager.GetComponentDataRawRW"/>. Null pointer or a missing singleton
    /// is false, not a crash.
    ///
    /// Routes through <see cref="RawWriteGuard.OverlayMemcpyBudget"/>: TypeSize 0 yields a
    /// budget of 0 (poke fields; do not memcpy a guessed sizeof). The pointer is still
    /// returned — overlays such as ItemKeyTable need it. Callers that want to copy bytes
    /// must use <see cref="TryGetRawRW(EntityManager, TypeIndex, out IntPtr, out int)"/> and
    /// <see cref="RawWriteGuard.EnsureOverlayMemcpy"/>.
    /// </summary>
    public static bool TryGetRawRW(EntityManager em, TypeIndex typeIndex, out IntPtr ptr)
    {
        if (!TryGetRawRW(em, typeIndex, out ptr, out int ecsTypeSize))
            return false;

        int budget = RawWriteGuard.OverlayMemcpyBudget(ecsTypeSize);
        if (budget <= 0)
        {
            Plugin.Logger?.LogWarning(
                $"Singletons.TryGetRawRW(TypeIndex {typeIndex.Value}): TypeSize={ecsTypeSize}; " +
                "overlay pointer only — poke fields, do not memcpy a guessed sizeof " +
                "(RawWriteGuard.OverlayMemcpyBudget / EnsureOverlayMemcpy).");
        }

        return true;
    }

    /// <summary>
    /// Same as <see cref="TryGetRawRW(EntityManager, TypeIndex, out IntPtr)"/> and also
    /// returns the TypeManager TypeSize so a caller cannot pretend they have a memcpy length.
    /// <paramref name="ecsTypeSize"/> is 0 when the interop TypeSize is garbage; the pointer
    /// is still valid for field pokes. Use
    /// <see cref="RawWriteGuard.EnsureOverlayMemcpy"/> before copying bytes.
    /// </summary>
    public static bool TryGetRawRW(EntityManager em, TypeIndex typeIndex, out IntPtr ptr, out int ecsTypeSize)
    {
        ptr = IntPtr.Zero;
        ecsTypeSize = 0;

        // A zero-sized (tag) component has no bytes in the chunk, so GetComponentDataRawRW throws
        // ArgumentException("This operation can not be called with a zero sized component") —
        // evidence 208, .ecs singleton UserMuteSingleton. Decide it here from the TypeIndex's own
        // ZeroSizeInChunkTypeFlag rather than round-tripping an engine exception. Deliberately not
        // a TypeSize test: TypeSize reads 0 for ordinary components through this interop (trap 7),
        // and that path must keep working.
        if (SingletonsZeroSize.IsZeroSized(typeIndex.Value))
        {
            Plugin.Logger?.LogWarning(SingletonsZeroSize.RefusalMessage(typeIndex.Value, TryGetComponentName(typeIndex)));
            return false;
        }

        try
        {
            try
            {
                ecsTypeSize = TypeManager.GetTypeInfo(typeIndex).TypeSize;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError(
                    $"Singletons.TryGetRawRW(TypeIndex {typeIndex.Value}): TypeManager.GetTypeInfo failed: {e}");
                return false;
            }

            _ = RawWriteGuard.OverlayMemcpyBudget(ecsTypeSize);

            var componentType = ComponentType.FromTypeIndex(typeIndex);
            componentType.AccessModeType = ComponentType.AccessMode.ReadWrite;
            if (!TryGetEntity(em, componentType, out var entity))
                return false;
            void* raw = em.GetComponentDataRawRW(entity, typeIndex);
            if (raw == null)
                return false;
            ptr = (IntPtr)raw;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Singletons.TryGetRawRW(TypeIndex {typeIndex.Value}) failed: {e}");
            ptr = IntPtr.Zero;
            ecsTypeSize = 0;
            return false;
        }
    }

    /// <summary>
    /// Best-effort managed name for a log line. <c>TypeManager.GetType(TypeIndex)</c>
    /// (server metadata) is a plain static that returns an object pointer, not a
    /// struct copy, so it does not go through the TypeInfo marshal that reads garbage (trap 7).
    /// Null when it does not resolve; the caller then names the TypeIndex.
    /// </summary>
    private static string? TryGetComponentName(TypeIndex typeIndex)
    {
        try
        {
            return TypeManager.GetType(typeIndex)?.FullName;
        }
        catch
        {
            return null;
        }
    }
}
