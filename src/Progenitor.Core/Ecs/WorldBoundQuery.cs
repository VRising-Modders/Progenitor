using System;
using Unity.Entities;

namespace Progenitor.Ecs;

/// <summary>
/// An <see cref="EntityQuery"/> cached across frames together with the world it was built in.
/// Use this for every query kept in a field. <see cref="Release"/> disposes the query only while its
/// own world is still created; after that world is destroyed it forgets the handle, because the
/// world already released the native query and disposing it again faults inside GameAssembly
/// (an access violation .NET cannot catch). See <see cref="WorldQueryPolicy"/>.
/// Main thread only, like every <see cref="EntityManager"/> call.
/// </summary>
internal sealed class WorldBoundQuery
{
    private readonly string _name;
    private EntityQuery _query;
    private World? _world;
    private ulong? _seq;
    private bool _built;

    internal WorldBoundQuery(string name) => _name = name;

    internal bool IsBuilt => _built;

    /// <summary>
    /// The cached query when it was built in <paramref name="world"/>. Otherwise releases the old one
    /// (safely, see <see cref="Release"/>) and returns false; the caller builds a query and calls
    /// <see cref="Store"/>.
    /// </summary>
    internal bool TryGet(World world, out EntityQuery query)
    {
        if (WorldQueryPolicy.CanReuse(_built, _seq, ReadSeq(world)))
        {
            query = _query;
            return true;
        }
        Release();
        query = default;
        return false;
    }

    /// <summary>Cache <paramref name="query"/>, built from <paramref name="world"/>'s EntityManager.</summary>
    internal void Store(World world, EntityQuery query)
    {
        Release();
        _query = query;
        _world = world;
        _seq = ReadSeq(world);
        _built = true;
    }

    /// <summary>Dispose while the owning world lives, otherwise forget. Never throws.</summary>
    internal CachedQueryRelease Release()
    {
        var owner = _world;
        bool alive = IsAlive(owner);
        var decision = WorldQueryPolicy.Release(_built, _seq, alive, alive ? ReadSeq(owner!) : null);
        if (decision == CachedQueryRelease.Dispose)
        {
            try { _query.Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning(_name + ": cached query dispose threw: " + e.Message); }
        }
        _query = default;
        _world = null;
        _seq = null;
        _built = false;
        return decision;
    }

    private static bool IsAlive(World? world)
    {
        try { return world != null && world.IsCreated; }
        catch { return false; }
    }

    private static ulong? ReadSeq(World world)
    {
        try { return world.SequenceNumber; }
        catch { return null; }
    }
}
