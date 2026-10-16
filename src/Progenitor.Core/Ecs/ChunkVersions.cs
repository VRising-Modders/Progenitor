using System;
using System.Collections.Generic;
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
///   Archetype* is one of the store's m_Archetypes    before it is dereferenced
/// Archetype.Chunks (ArchetypeChunkData).m_Data       ChunkIndex[Capacity], then uint[ComponentCount * Capacity]
///   m_Data[ListIndex] == chunk                       the slot really is this chunk
///   Types[pos] == typeIndex                          the slot really is this type
///   version = changeVersions[pos * Capacity + ListIndex]
/// </code>
///
/// <para><b>Order.</b> Each pointer is dereferenced only after every bound that makes it safe has
/// been checked: the store's arrays are non-null and the index is below their capacity before
/// they are indexed; the chunk is below the global table's size before the table is read; the
/// archetype pointer is found in the store's own archetype list before anything is read through it.</para>
///
/// <para><b>Every check that fails reports "unknown"</b> (false, counted in <see cref="Failures"/>),
/// never a value: the caller turns unknown into a fresh stamp, which reads as changed.</para>
///
/// <para><b>Self-test.</b> The first successful read after Core loads is compared with the interop's
/// own <c>EntityManager.GetChunk</c> (chunk index, archetype pointer, store pointer). A mismatch
/// disables the raw reads for the rest of the process (for this layout; a Core with other layout
/// declarations tries again): every stamp is then fresh, and one error line says why.</para>
///
/// <para><b>What is cached.</b> The type's position per archetype (<see cref="TypePositionCache"/>,
/// re-checked on every read) and the set of the store's archetype pointers (rebuilt whenever the
/// list's pointer or length changes; archetypes are never removed). <c>m_Data</c>,
/// <c>Capacity</c>, <c>Count</c> and <c>ListIndex</c> are re-read each time.</para>
///
/// <para>The global <c>PerChunkData</c> array is a shared static
/// (<c>EntityComponentStore.PerChunkArray.StaticIdentifier.Ref</c>), read with
/// <c>il2cpp_field_static_get_value</c> into a byte buffer, so no interop wrapper of the generic
/// <c>SharedStatic&lt;PerChunkArray&gt;</c> is ever built.</para>
///
/// <para>Main thread only. On the server, Core's frame tick runs inside the postfix of
/// <c>ServerBootstrapSystem.OnUpdate</c> on the main thread, where no structural change runs
/// concurrently; never call it from a job or from inside a native inventory hook.</para>
/// </summary>
internal static unsafe class ChunkVersions
{
    /// <summary>Why a read reported unknown. Order is the order of the walk.</summary>
    internal enum Failure
    {
        None = 0,
        Disabled,
        NoStore,
        NoChunkTable,
        EntitySlot,
        EntityVersion,
        Chunk,
        IndexInChunk,
        Archetype,
        UnknownArchetype,
        StoreOwner,
        ListSlot,
        ChunkSlot,
        Type,
        SelfTest,
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

    /// <summary>Most archetypes the membership set accepts (a world has a few thousand).</summary>
    private const int MaxArchetypes = 1 << 20;

    /// <summary>Process slot naming why the raw reads were disabled, keyed by the layout declarations.</summary>
    private static string DisabledKey => "progenitor.chunkversions.disabled." + Layout.DeclarationsHash;

    private static readonly long[] FailureCounts = new long[(int)Failure.Threw + 1];
    private static readonly TypePositionCache Positions = new();
    private static readonly HashSet<long> ArchetypeSet = new();
    private static IntPtr _archetypeListStore;
    private static IntPtr _archetypeListPtr;
    private static int _archetypeListLength = -1;
    private static int _maximumChunkCount;
    private static IntPtr _refField;
    private static bool _selfTested;
    private static string? _disabled;

    // The current read's type list, for the cache's slot reader (one delegate, no capture per read).
    private static byte* _types;
    private static readonly Func<int, int> TypeAt = i => *(int*)(_types + ((long)i * Layout.ComponentTypeInArchetypeStride) + Layout.ComponentTypeInArchetypeTypeIndexOffset);

    /// <summary>Failed reads by reason since Core loaded (the dev bridge reports these).</summary>
    internal static long Failures(Failure reason) => Interlocked.Read(ref FailureCounts[(int)reason]);

    /// <summary>Rescans of an archetype's type list (a cache miss or a stale hint).</summary>
    internal static long TypeScans => Positions.Scans;

    /// <summary>Why the raw reads are off for this process, or null while they are on.</summary>
    internal static string? DisabledReason
    {
        get
        {
            if (_disabled != null) return _disabled;
            try { _disabled = AppDomain.CurrentDomain.GetData(DisabledKey) as string; }
            catch { /* no slot: on */ }
            return _disabled;
        }
    }

    /// <summary>Whether the first read was compared with the interop's <c>GetChunk</c> yet.</summary>
    internal static bool SelfTested => _selfTested;

    /// <summary>Drop the caches (Core unload). The process-wide disable stays.</summary>
    internal static void Reset()
    {
        Positions.Clear();
        ArchetypeSet.Clear();
        _archetypeListStore = _archetypeListPtr = IntPtr.Zero;
        _archetypeListLength = -1;
        _refField = IntPtr.Zero;
        _maximumChunkCount = 0;
        _selfTested = false;
        _disabled = null;
        Array.Clear(FailureCounts);
    }

    /// <summary>
    /// Bind to <paramref name="em"/>'s component store and the global chunk table. False (counted)
    /// when either cannot be read or the raw reads are disabled; every read then reports unknown.
    /// </summary>
    internal static bool TryBind(EntityManager em, out Binding binding)
    {
        binding = default;
        if (DisabledReason != null) return Fail(Failure.Disabled);
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
        if (!binding.IsBound) return Fail(Failure.NoStore);
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
        if (_disabled != null) return Fail(Failure.Disabled);
        byte* store = (byte*)binding.Store;

        // 1. The store's entity arrays: both pointers set, the index inside them, the entity alive.
        int capacity = *(int*)(store + Layout.EntityComponentStoreEntitiesCapacityOffset);
        int* versions = *(int**)(store + Layout.EntityComponentStoreVersionByEntityOffset);
        byte* inChunk = *(byte**)(store + Layout.EntityComponentStoreEntityInChunkByEntityOffset);
        if (versions == null || inChunk == null) return Fail(Failure.NoStore);
        if (!ChunkVersionMath.EntitySlotOk(index, version, capacity)) return Fail(Failure.EntitySlot);
        if (versions[index] != version) return Fail(Failure.EntityVersion);

        // 2. The chunk inside the global table, then the table's row for it.
        byte* slot = inChunk + ((long)index * Layout.EntityInChunkStride);
        int chunk = *(int*)(slot + Layout.EntityInChunkChunkOffset);
        int indexInChunk = *(int*)(slot + Layout.EntityInChunkIndexInChunkOffset);
        if (!ChunkVersionMath.ChunkOk(chunk, binding.MaximumChunkCount)) return Fail(Failure.Chunk);
        byte* perChunk = (byte*)binding.PerChunk + ((long)chunk * Layout.PerChunkDataStride);
        byte* archetype = *(byte**)(perChunk + Layout.PerChunkDataArchetypeOffset);
        int entityCount = *(int*)(perChunk + Layout.PerChunkDataEntityCountOffset);
        int listIndex = *(int*)(perChunk + Layout.PerChunkDataListIndexOffset);
        if (!ChunkVersionMath.IndexInChunkOk(indexInChunk, entityCount) || listIndex < 0) return Fail(Failure.IndexInChunk);

        // 3. The archetype: set, and one of this store's own, before it is read through.
        if (archetype == null) return Fail(Failure.Archetype);
        if (!IsStoreArchetype(store, (long)archetype)) return Fail(Failure.UnknownArchetype);
        if (*(byte**)(archetype + Layout.ArchetypeEntityComponentStoreOffset) != store) return Fail(Failure.StoreOwner);

        // 4. The chunk's slot in the archetype's chunk list, the type's slot, the version.
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
    /// The first read after Core loads, compared with the interop's <c>EntityManager.GetChunk</c>:
    /// the same chunk index, archetype pointer and store pointer. Done once per Core load on the
    /// first entity that reads; a mismatch disables the raw reads process-wide and logs why. False
    /// when the reads are (now) disabled.
    /// </summary>
    internal static bool SelfTest(EntityManager em, in Binding binding, Entity entity, ChunkVersionRead raw, Action<string>? logError)
    {
        if (_selfTested) return _disabled == null;
        string? mismatch;
        try
        {
            var chunk = em.GetChunk(entity);
            long archetype = (long)chunk.Archetype.Archetype;
            long chunkStore = (long)chunk.m_EntityComponentStore;
            mismatch = ChunkVersionMath.SelfTestMismatch(raw.Chunk, raw.Archetype, (long)binding.Store, chunk.m_Chunk.Value, archetype, chunkStore);
        }
        catch (Exception e)
        {
            // No answer is not a pass: try again on the next read.
            Fail(Failure.SelfTest);
            logError?.Invoke("ChunkVersions: the self-test's GetChunk on " + entity.Index + ":" + entity.Version + " threw (" + e.Message + "); retrying on the next read.");
            return false;
        }

        _selfTested = true;
        if (mismatch == null) return true;
        Disable("the raw chunk read disagreed with EntityManager.GetChunk on " + entity.Index + ":" + entity.Version + ": " + mismatch, logError);
        return false;
    }

    private static void Disable(string reason, Action<string>? logError)
    {
        Fail(Failure.SelfTest);
        _disabled = reason;
        try { AppDomain.CurrentDomain.SetData(DisabledKey, reason); }
        catch { /* the generation-local flag still holds */ }
        logError?.Invoke("ChunkVersions: raw change-version reads DISABLED for this process (" + reason
            + "). ctx.Inventory.ChangeStamp now returns a fresh value every call (always \"changed\").");
    }

    /// <summary>
    /// Whether <paramref name="archetype"/> is one of the store's own archetypes
    /// (<c>EntityComponentStore.m_Archetypes</c>). The set is rebuilt when the list's pointer or
    /// length changes.
    /// </summary>
    private static bool IsStoreArchetype(byte* store, long archetype)
    {
        byte* list = store + Layout.EntityComponentStoreArchetypesOffset;
        byte* ptr = *(byte**)(list + Layout.UntypedUnsafeListPtrOffset);
        int length = *(int*)(list + Layout.UntypedUnsafeListLengthOffset);
        if (ptr == null || length <= 0 || length > MaxArchetypes) return false;
        if ((IntPtr)store != _archetypeListStore || (IntPtr)ptr != _archetypeListPtr || length != _archetypeListLength)
        {
            ArchetypeSet.Clear();
            for (int i = 0; i < length; i++) ArchetypeSet.Add((long)((byte**)ptr)[i]);
            _archetypeListStore = (IntPtr)store;
            _archetypeListPtr = (IntPtr)ptr;
            _archetypeListLength = length;
        }

        return ArchetypeSet.Contains(archetype);
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
            // not fit the buffer it is copied into, and anything that is not a value.
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
