using System;
using System.Threading;
using Il2CppInterop.Runtime;
using Progenitor.Access;
using Unity.Entities;

namespace Progenitor.Ecs;

/// <summary>
/// Raw reads of a chunk's per-type change version and of the store's global system version, the
/// numbers <c>ctx.Inventory.ChangeStamp</c> is built from. No native method is called: the walk is
/// the one the game's own <c>ArchetypeChunk.GetChangeVersion</c> takes, done with the metadata
/// offsets in <c>layout.txt</c> ("Ecs: chunk change versions"):
///
/// <code>
/// EntityManager.m_EntityDataAccess -> EntityComponentStore (embedded at offset 0)
///   m_VersionByEntity[e.Index] == e.Version          entity alive
///   m_EntityInChunkByEntity[e.Index]                 -> ChunkIndex, IndexInChunk
/// PerChunkData[chunk]                                -> Archetype*, EntityCount, ListIndex
/// Archetype.Chunks (ArchetypeChunkData).m_Data       ChunkIndex[Capacity], then uint[ComponentCount * Capacity]
///   m_Data[ListIndex] == chunk                       the slot really is this chunk
///   Types[pos] == typeIndex                          the slot really is this type
///   version = changeVersions[pos * Capacity + ListIndex]
/// </code>
///
/// <para><b>Every check that fails reports "unknown"</b> (false with a reason counted in
/// <see cref="Failures"/>), never a value: the caller turns unknown into a fresh stamp, which
/// reads as changed. A wrong number here is a false negative, a refusal is a false positive.</para>
///
/// <para><b>What is cached.</b> Only the type's position in each archetype's type list
/// (<see cref="TypePositionCache"/>), re-checked on every read. <c>m_Data</c>, <c>Capacity</c>,
/// <c>Count</c> and <c>ListIndex</c> are re-read each time: the game reallocates <c>m_Data</c>
/// when an archetype's chunk list grows and moves a chunk's <c>ListIndex</c> when another chunk
/// is removed.</para>
///
/// <para>The global <c>PerChunkData</c> array is a shared static
/// (<c>EntityComponentStore.PerChunkArray.StaticIdentifier.Ref</c>), read with
/// <c>il2cpp_field_static_get_value</c> into a byte buffer, so no interop wrapper of the generic
/// <c>SharedStatic&lt;PerChunkArray&gt;</c> is ever built.</para>
///
/// <para>Main thread only, and only between systems: a structural change on another thread while
/// this walks would move the chain under it.</para>
/// </summary>
internal static unsafe class ChunkVersions
{
    /// <summary>Why a read reported unknown. Order is the order of the walk.</summary>
    internal enum Failure
    {
        None = 0,
        NoStore,
        NoChunkTable,
        EntitySlot,
        EntityVersion,
        Chunk,
        Archetype,
        StoreOwner,
        IndexInChunk,
        ListSlot,
        ChunkSlot,
        Type,
        Threw,
    }

    /// <summary>What a batch of reads stands on: the store and the global chunk table.</summary>
    internal readonly struct Binding
    {
        internal Binding(IntPtr store, IntPtr perChunk, int maximumChunkCount)
        {
            Store = store;
            PerChunk = perChunk;
            MaximumChunkCount = maximumChunkCount;
        }

        internal IntPtr Store { get; }
        internal IntPtr PerChunk { get; }
        internal int MaximumChunkCount { get; }
        internal bool IsBound => Store != IntPtr.Zero && PerChunk != IntPtr.Zero && MaximumChunkCount > 0;
    }

    private static readonly long[] FailureCounts = new long[(int)Failure.Threw + 1];
    private static readonly TypePositionCache Positions = new();
    private static int _maximumChunkCount;
    private static IntPtr _refField;

    // The current read's type list, for the cache's slot reader (one delegate, no capture per read).
    private static byte* _types;
    private static readonly Func<int, int> TypeAt = i => *(int*)(_types + ((long)i * Layout.ComponentTypeInArchetypeStride) + Layout.ComponentTypeInArchetypeTypeIndexOffset);

    /// <summary>Failed reads by reason since Core loaded (the dev bridge reports these).</summary>
    internal static long Failures(Failure reason) => Interlocked.Read(ref FailureCounts[(int)reason]);

    /// <summary>Rescans of an archetype's type list (a cache miss or a stale hint).</summary>
    internal static long TypeScans => Positions.Scans;

    /// <summary>Drop the type-position hints (Core unload).</summary>
    internal static void Reset()
    {
        Positions.Clear();
        _refField = IntPtr.Zero;
        _maximumChunkCount = 0;
        Array.Clear(FailureCounts);
    }

    /// <summary>
    /// Bind to <paramref name="em"/>'s component store and the global chunk table. False (counted)
    /// when either cannot be read; every read then reports unknown.
    /// </summary>
    internal static bool TryBind(EntityManager em, out Binding binding)
    {
        binding = default;
        try
        {
            // Raw, at the metadata offset, cross-checked against the interop struct's own field.
            byte* access = *(byte**)((byte*)&em + Layout.EntityManagerEntityDataAccessOffset);
            if (access == null || access != (byte*)em.m_EntityDataAccess) return Fail(Failure.NoStore);
            byte* store = access + Layout.EntityDataAccessEntityComponentStoreOffset;

            byte* perChunk = PerChunkTable();
            if (perChunk == null) return Fail(Failure.NoChunkTable);

            int max = _maximumChunkCount;
            if (max <= 0)
            {
                max = EntityComponentStore.ChunkStore.kMaximumChunkCount;
                if (max <= 0) return Fail(Failure.NoChunkTable);
                _maximumChunkCount = max;
            }

            binding = new Binding((IntPtr)store, (IntPtr)perChunk, max);
            return true;
        }
        catch
        {
            return Fail(Failure.Threw);
        }
    }

    /// <summary>The store's global system version, the clock every change version is stamped from.</summary>
    internal static bool TryReadClock(in Binding binding, out uint clock)
    {
        clock = 0;
        if (binding.Store == IntPtr.Zero) return Fail(Failure.NoStore);
        clock = *(uint*)((byte*)binding.Store + Layout.EntityComponentStoreGlobalSystemVersionOffset);
        return true;
    }

    /// <summary>
    /// The change version of <paramref name="typeIndex"/> (the full <c>TypeIndex</c> value) on the
    /// chunk holding entity <paramref name="index"/>:<paramref name="version"/>. False when any link
    /// of the chain fails its check, including an entity that is gone or no longer has the type.
    /// </summary>
    internal static bool TryRead(in Binding binding, int index, int version, int typeIndex, out ChunkVersionRead read)
    {
        read = default;
        if (!binding.IsBound) return Fail(Failure.NoStore);
        byte* store = (byte*)binding.Store;

        int capacity = *(int*)(store + Layout.EntityComponentStoreEntitiesCapacityOffset);
        if (!ChunkVersionMath.EntitySlotOk(index, version, capacity)) return Fail(Failure.EntitySlot);
        int* versions = *(int**)(store + Layout.EntityComponentStoreVersionByEntityOffset);
        byte* inChunk = *(byte**)(store + Layout.EntityComponentStoreEntityInChunkByEntityOffset);
        if (versions == null || inChunk == null) return Fail(Failure.NoStore);
        if (versions[index] != version) return Fail(Failure.EntityVersion);

        byte* slot = inChunk + ((long)index * Layout.EntityInChunkStride);
        int chunk = *(int*)(slot + Layout.EntityInChunkChunkOffset);
        int indexInChunk = *(int*)(slot + Layout.EntityInChunkIndexInChunkOffset);
        if (!ChunkVersionMath.ChunkOk(chunk, binding.MaximumChunkCount)) return Fail(Failure.Chunk);

        byte* perChunk = (byte*)binding.PerChunk + ((long)chunk * Layout.PerChunkDataStride);
        byte* archetype = *(byte**)(perChunk + Layout.PerChunkDataArchetypeOffset);
        if (archetype == null) return Fail(Failure.Archetype);
        if (*(byte**)(archetype + Layout.ArchetypeEntityComponentStoreOffset) != store) return Fail(Failure.StoreOwner);
        int entityCount = *(int*)(perChunk + Layout.PerChunkDataEntityCountOffset);
        if (!ChunkVersionMath.IndexInChunkOk(indexInChunk, entityCount)) return Fail(Failure.IndexInChunk);
        int listIndex = *(int*)(perChunk + Layout.PerChunkDataListIndexOffset);

        byte* chunks = archetype + Layout.ArchetypeChunksOffset;
        byte* data = *(byte**)(chunks + Layout.ArchetypeChunkDataDataOffset);
        int listCapacity = *(int*)(chunks + Layout.ArchetypeChunkDataCapacityOffset);
        int listCount = *(int*)(chunks + Layout.ArchetypeChunkDataCountOffset);
        int componentCount = *(int*)(chunks + Layout.ArchetypeChunkDataComponentCountOffset);
        if (data == null || !ChunkVersionMath.ListSlotOk(listIndex, listCount, listCapacity)) return Fail(Failure.ListSlot);
        if (*(int*)(data + ((long)listIndex * Layout.ChunkIndexStride)) != chunk) return Fail(Failure.ChunkSlot);

        byte* types = *(byte**)(archetype + Layout.ArchetypeTypesOffset);
        int typesCount = *(int*)(archetype + Layout.ArchetypeTypesCountOffset);
        if (types == null || typesCount <= 0) return Fail(Failure.Type);
        _types = types;
        int position = Positions.Find((long)archetype, typeIndex, Math.Min(typesCount, componentCount), TypeAt);
        _types = null;
        if (position < 0) return Fail(Failure.Type);

        uint changeVersion = *(uint*)(data + ChunkVersionMath.ChangeVersionByteOffset(listCapacity, position, listIndex, Layout.ChunkIndexStride));
        read = new ChunkVersionRead(chunk, (long)archetype, changeVersion);
        return true;
    }

    /// <summary>
    /// <c>PerChunkArray.ChunkData</c> without the getter: the shared static's storage pointer, then
    /// the <c>PerChunkData*</c> at its start. Null when the field or the table is not there yet.
    /// </summary>
    private static byte* PerChunkTable()
    {
        if (_refField == IntPtr.Zero)
        {
            const string declared = "Unity.Entities.EntityComponentStore.PerChunkArray.StaticIdentifier";
            var interop = typeof(EntityComponentStore.PerChunkArray.StaticIdentifier);
            // The copy below is as wide as the runtime says the field is; refuse anything that would
            // not fit the buffer it is copied into.
            int width = AccessResolver.FieldWidth(interop, declared, "Ref");
            if (width <= 0 || width > 16 || Layout.SharedStaticBufferOffset + sizeof(IntPtr) > width) return null;
            if (!AccessResolver.FieldIsClassified(interop, declared, "Ref") || AccessResolver.FieldIsReference(interop, declared, "Ref")) return null;
            _refField = AccessResolver.FieldInfo(interop, declared, "Ref");
        }

        byte* shared = stackalloc byte[16];
        IL2CPP.il2cpp_field_static_get_value(_refField, shared);
        byte* storage = *(byte**)(shared + Layout.SharedStaticBufferOffset);
        if (storage == null) return null;
        return *(byte**)(storage + Layout.PerChunkArrayChunkDataOffset);
    }

    private static bool Fail(Failure reason)
    {
        Interlocked.Increment(ref FailureCounts[(int)reason]);
        return false;
    }
}
