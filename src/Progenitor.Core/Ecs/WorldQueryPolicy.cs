namespace Progenitor.Ecs;

/// <summary>What releasing a cached <c>EntityQuery</c> must do.</summary>
internal enum CachedQueryRelease
{
    /// <summary>Nothing was cached.</summary>
    None,

    /// <summary>The owning world is alive and is still the world the query was built in: dispose it.</summary>
    Dispose,

    /// <summary>
    /// The owning world is gone, was replaced, or cannot be identified: drop the handle without
    /// touching it. <c>World.Dispose</c> already released the query's native state, and
    /// <c>EntityQuery.Dispose</c> on that state is an access violation inside GameAssembly that
    /// takes the whole process down (live 2026-10-02 client crash, core-swap-crash-2026-10-02.md).
    /// </summary>
    Forget,
}

/// <summary>
/// Game-free decisions for an <c>EntityQuery</c> cached across frames. A cached query belongs to the
/// one world it was built in, identified by <c>World.SequenceNumber</c> rather than by the interop
/// wrapper (Il2CppInterop can hand back a new wrapper for the same native world). An unreadable
/// sequence number counts as a different world: a wrong rebuild costs one query, a wrong reuse or a
/// wrong dispose costs the process.
/// </summary>
internal static class WorldQueryPolicy
{
    /// <summary>True when a cached query may be used for the world whose sequence is <paramref name="current"/>.</summary>
    internal static bool CanReuse(bool built, ulong? builtSeq, ulong? current)
    {
        if (!built) return false;
        if (builtSeq == null || current == null) return false;
        return builtSeq.Value == current.Value;
    }

    /// <summary>
    /// Release decision for a cached query.
    /// <paramref name="ownerAlive"/> is <c>World.IsCreated</c> on the wrapper the query was built from,
    /// read now; <paramref name="ownerSeqNow"/> is that wrapper's sequence number read now (null when
    /// unreadable or when the world is not alive).
    /// </summary>
    internal static CachedQueryRelease Release(bool built, ulong? builtSeq, bool ownerAlive, ulong? ownerSeqNow)
    {
        if (!built) return CachedQueryRelease.None;
        if (!ownerAlive) return CachedQueryRelease.Forget;
        if (builtSeq == null || ownerSeqNow == null) return CachedQueryRelease.Forget;
        return builtSeq.Value == ownerSeqNow.Value ? CachedQueryRelease.Dispose : CachedQueryRelease.Forget;
    }
}
