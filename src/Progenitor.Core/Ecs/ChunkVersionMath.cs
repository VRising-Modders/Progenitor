using System;
using System.Collections.Generic;

namespace Progenitor.Ecs;

/// <summary>
/// One raw change-version read of one component type on one entity's chunk (<see cref="ChunkVersions"/>):
/// the chunk, its archetype (as a pointer value, an identity only) and the 32-bit change version.
/// </summary>
internal readonly struct ChunkVersionRead : IEquatable<ChunkVersionRead>
{
    internal ChunkVersionRead(int chunk, long archetype, uint version)
    {
        Chunk = chunk;
        Archetype = archetype;
        Version = version;
    }

    /// <summary>The entity's <c>ChunkIndex</c> value.</summary>
    internal int Chunk { get; }

    /// <summary>The chunk's archetype pointer, compared for identity and never dereferenced by game-free code.</summary>
    internal long Archetype { get; }

    /// <summary>The chunk's change version of the type, as the game stores it (wraps at 2^32).</summary>
    internal uint Version { get; }

    /// <summary>Same chunk and archetype, whatever the version.</summary>
    internal bool SamePlace(ChunkVersionRead other) => Chunk == other.Chunk && Archetype == other.Archetype;

    public bool Equals(ChunkVersionRead other) => SamePlace(other) && Version == other.Version;

    public override bool Equals(object? obj) => obj is ChunkVersionRead other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Chunk, Archetype, Version);
}

/// <summary>
/// The game-free half of <see cref="ChunkVersions"/>: the bounds each raw read checks before it
/// dereferences the next pointer, where a type's change version sits in an archetype's chunk data,
/// the 64-bit extension of the 32-bit clock, and the per-archetype type-position cache. Unit-tested;
/// the pointer walk itself lives in <see cref="ChunkVersions"/>.
/// </summary>
internal static class ChunkVersionMath
{
    /// <summary>
    /// The entity slot is inside the store's arrays and names a live-shaped entity (version above 0;
    /// <c>Entity.Null</c> is 0:0).
    /// </summary>
    internal static bool EntitySlotOk(int index, int version, int entitiesCapacity) =>
        index >= 0 && version > 0 && entitiesCapacity > 0 && index < entitiesCapacity;

    /// <summary>The chunk index is inside the global per-chunk array.</summary>
    internal static bool ChunkOk(int chunk, int maximumChunkCount) =>
        chunk >= 0 && maximumChunkCount > 0 && chunk < maximumChunkCount;

    /// <summary>
    /// The chunk's position in its archetype's chunk list is in use (below <c>Count</c>), and the
    /// list's own bookkeeping is consistent (<c>Count</c> at most <c>Capacity</c>).
    /// </summary>
    internal static bool ListSlotOk(int listIndex, int count, int capacity) =>
        capacity > 0 && count > 0 && count <= capacity && listIndex >= 0 && listIndex < count;

    /// <summary>The entity's index in its chunk is below the chunk's entity count.</summary>
    internal static bool IndexInChunkOk(int indexInChunk, int entityCount) =>
        indexInChunk >= 0 && indexInChunk < entityCount;

    /// <summary>
    /// The self-test's verdict: null when the raw read names the same chunk, archetype and store as
    /// the interop's <c>EntityManager.GetChunk</c>, else what differs.
    /// </summary>
    internal static string? SelfTestMismatch(int rawChunk, long rawArchetype, long rawStore, int chunk, long archetype, long store)
    {
        var parts = new List<string>(3);
        if (rawChunk != chunk) parts.Add("chunk " + rawChunk + " vs " + chunk);
        if (rawArchetype != archetype) parts.Add("archetype 0x" + rawArchetype.ToString("X") + " vs 0x" + archetype.ToString("X"));
        if (rawStore != store) parts.Add("store 0x" + rawStore.ToString("X") + " vs 0x" + store.ToString("X"));
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>
    /// Byte offset of the change version of the type at <paramref name="typePosition"/> for the
    /// chunk at <paramref name="listIndex"/>, from the start of <c>ArchetypeChunkData.m_Data</c>:
    /// the data starts with <c>ChunkIndex[Capacity]</c>, then one <c>uint[Capacity]</c> per type,
    /// type-major (the game's <c>GetChangeVersionArrayForType(i)[ListIndex]</c>).
    /// </summary>
    internal static long ChangeVersionByteOffset(int capacity, int typePosition, int listIndex, int chunkIndexStride) =>
        ((long)capacity * chunkIndexStride) + ((((long)typePosition * capacity) + listIndex) * sizeof(uint));

    /// <summary>
    /// Advance the 64-bit clock to a new 32-bit sample: the forward distance, so the clock never
    /// goes back. Valid while fewer than 2^32 versions pass between two samples.
    /// </summary>
    internal static long Advance(long clock64, uint sample32) =>
        clock64 + (uint)(sample32 - (uint)clock64);

    /// <summary>
    /// The 64-bit value of a 32-bit version read when the clock stood at <paramref name="clock64"/>:
    /// up to 2^31 behind the clock maps exactly, a version ahead of the clock (written after the
    /// sample) maps ahead, and a version more than 2^31 behind also maps ahead — newer, never older,
    /// so the result can only read as a change. Two different 32-bit versions never map to the same
    /// 64-bit value at one clock, and the 64-bit value is congruent to the 32-bit one mod 2^32, so a
    /// changed version always changes the extended one.
    /// </summary>
    internal static long Extend(long clock64, uint version32) =>
        clock64 + (int)(version32 - (uint)clock64);

    /// <summary>
    /// The clock after the component store it is read from was replaced (a new world): two eras
    /// ahead, keeping the low 32 bits equal to the new store's sample. Every version of the new store
    /// then extends past every value the old store's clock could have handed out.
    /// </summary>
    internal static long Rebase(long clock64, uint sample32) =>
        (((clock64 >> 32) + 2) << 32) | sample32;
}

/// <summary>
/// Where a type sits in an archetype's type list, cached by archetype pointer and type index. The
/// cached position is a hint only: the caller re-checks it against the archetype's live type list on
/// every read and rescans on a mismatch, so a freed and reused archetype address costs a rescan and
/// never a wrong slot.
/// </summary>
internal sealed class TypePositionCache
{
    /// <summary>Entries kept before the cache starts over (archetypes are few; this bounds a leak).</summary>
    internal const int MaxEntries = 4096;

    private readonly Dictionary<(long Archetype, int Type), int> _positions = new();

    internal int Count => _positions.Count;

    /// <summary>Rescans so far (diagnostics and tests).</summary>
    internal long Scans { get; private set; }

    internal void Clear() => _positions.Clear();

    /// <summary>
    /// The position of <paramref name="typeIndex"/> among the first <paramref name="limit"/> types of
    /// <paramref name="archetype"/>, reading slots with <paramref name="typeAt"/>; -1 when absent.
    /// </summary>
    internal int Find(long archetype, int typeIndex, int limit, Func<int, int> typeAt)
    {
        if (limit <= 0) return -1;
        var key = (archetype, typeIndex);
        if (_positions.TryGetValue(key, out int cached) && cached >= 0 && cached < limit && typeAt(cached) == typeIndex)
            return cached;

        Scans++;
        int found = -1;
        for (int i = 0; i < limit; i++)
        {
            if (typeAt(i) != typeIndex) continue;
            found = i;
            break;
        }

        if (found < 0)
        {
            _positions.Remove(key);
            return -1;
        }

        if (_positions.Count >= MaxEntries) _positions.Clear();
        _positions[key] = found;
        return found;
    }
}
