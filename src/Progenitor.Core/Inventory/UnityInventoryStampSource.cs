using System;
using ProjectM;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Inventory;

/// <summary>
/// The game half of <see cref="InventoryStamps"/>: raw chunk change versions through
/// <see cref="ChunkVersions"/>, the owner-to-inventory resolution of
/// <see cref="UnityInventoryBackend.TryInventoryEntity"/>, job completion through
/// <c>EntityManager.GetBufferRawRO</c> (the verified non-generic path; it completes the in-flight
/// writers of that type only) and the audit's raw-row hash over the same pointer. Server, main thread.
/// </summary>
internal sealed unsafe class UnityInventoryStampSource : IInventoryStampSource
{
    /// <summary>Largest inventory the audit hashes (a player bag has tens of slots).</summary>
    private const int MaxRows = 1024;

    private ChunkVersions.Binding _binding;
    private TypeIndex _slotsType;
    private TypeIndex _instancesType;
    private bool _types;

    public int CurrentEpoch => Progenitor.WorldState.WorldEpoch.Current;

    public bool BeginBatch()
    {
        _binding = default;
        if (!TryWorld(out var world)) return false;
        if (!_types)
        {
            _slotsType = ComponentIndex.IndexOf<InventoryBuffer>();
            _instancesType = ComponentIndex.IndexOf<InventoryInstanceElement>();
            _types = true;
        }

        return ChunkVersions.TryBind(world.EntityManager, out _binding);
    }

    public bool TryReadClock(out uint clock, out long store)
    {
        store = (long)_binding.Store;
        return ChunkVersions.TryReadClock(_binding, out clock);
    }

    public bool TryResolve(InventoryRef container, out int inventoryIndex, out int inventoryVersion)
    {
        inventoryIndex = inventoryVersion = 0;
        if (!TryWorld(out var world)) return false;
        try
        {
            if (!UnityInventoryBackend.TryInventoryEntity(world.EntityManager, container.Owner, container.Instance, out var inv, out _))
                return false;
            inventoryIndex = inv.Index;
            inventoryVersion = inv.Version;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryReadSlotsVersion(int index, int version, out ChunkVersionRead read) =>
        Read(index, version, _slotsType.Value, out read);

    public bool TryReadInstancesVersion(int index, int version, out ChunkVersionRead read) =>
        Read(index, version, _instancesType.Value, out read);

    /// <summary>A raw read; the first one after Core loads is also checked against the interop's <c>GetChunk</c>.</summary>
    private bool Read(int index, int version, int typeIndex, out ChunkVersionRead read)
    {
        if (!ChunkVersions.TryRead(_binding, index, version, typeIndex, out read)) return false;
        if (ChunkVersions.SelfTested) return ChunkVersions.DisabledReason == null;
        if (!TryWorld(out var world)) return false;
        return ChunkVersions.SelfTest(world.EntityManager, _binding, new Entity { Index = index, Version = version }, read,
            line => Plugin.Logger?.LogError(line));
    }

    public bool CompleteWriters(int index, int version, bool instances)
    {
        if (!TryWorld(out var world)) return false;
        try
        {
            // The caller's raw read has just confirmed this entity alive with the buffer.
            world.EntityManager.GetBufferRawRO(new Entity { Index = index, Version = version }, instances ? _instancesType : _slotsType);
            return true;
        }
        catch
        {
            // Not completed: the stamp service treats the whole batch as unknown.
            return false;
        }
    }

    public bool TryContentHash(int index, int version, out ulong hash)
    {
        hash = 0;
        if (!TryWorld(out var world)) return false;
        try
        {
            var em = world.EntityManager;
            var e = new Entity { Index = index, Version = version };
            if (!em.Exists(e)) return false;
            int length = em.GetBufferLength(e, _slotsType);
            if (length < 0 || length > MaxRows) return false;
            if (length == 0)
            {
                hash = InventoryStamps.RowHash(ReadOnlySpan<byte>.Empty, 0);
                return true;
            }

            byte* raw = (byte*)em.GetBufferRawRO(e, _slotsType);
            if (raw == null) return false;
            hash = InventoryStamps.RowHash(new ReadOnlySpan<byte>(raw, length * Layout.InventoryBufferStride), length);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWorld(out World world)
    {
        world = Worlds.Server!;
        return Plugin.IsServerProcess && world != null && world.IsCreated;
    }
}
