#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM.Network;
using Progenitor.Ecs;
using Progenitor.Persistence;
using Unity.Entities;
using Unity.Mathematics;

namespace Progenitor.Cameras;

/// <summary>
/// Server: keeps a streaming-origin lease (<see cref="SharedObservation"/>) from outliving its run. The game saves
/// <c>User.ProxySyncPosition</c> with the world and does not reset it on reconnect (live 2026-10-17: after a crash
/// during a preview the user's origin stayed at the chest, and every later lease was refused as "another view").
/// <list type="bullet">
/// <item><b>Save guard.</b> On the shared <see cref="WorldSaveGuard"/> suspension every lease puts the user's original
/// origin back and stops writing its point until the save file is written; a lease that starts during it starts
/// suspended.</item>
/// <item><b>Records.</b> While a lease runs, its user (platform id), point and original origin are kept in Core's mod
/// save (<c>progenitor.observation</c>), dropped when it closes. Records written before the store is ready are merged
/// into the previous run's.</item>
/// <item><b>Startup sweep.</b> After a load, a record whose user's origin still sits at the recorded point, with no lease of
/// this run for that user, gets the recorded original back (logged). A lease start that finds the origin at a recorded
/// point does the same instead of refusing.</item>
/// </list>
/// The write is the lease's own path (<see cref="SharedObservation.WriteProxyRaw"/>). Main thread.
/// </summary>
internal static class ObservationGuard
{
    /// <summary>How long after the load the sweep waits for a recorded user to exist.</summary>
    internal const double SweepWaitSeconds = 120;

    private static bool _installed;
    private static IModStore? _store;
    private static IDisposable? _ready;
    private static bool _loaded;
    private static ObservationLeaseRecords _records = new();
    private static readonly List<ObservationLeaseRecord> Pending = new();
    private static double _loadedAt = double.NaN;
    private static double _nextTick;

    internal static int RecordCount => _records.Records.Count;

    internal static int SweepPending => Pending.Count;

    internal static void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        WorldSaveGuard.Suspend += OnSuspend;
        WorldSaveGuard.Resume += OnResume;
        Plugin.Frame += Tick;
        try
        {
            _store = ModSave.Store(ObservationLeaseRecords.StoreId);
            _ready = _store.WhenReady(store =>
            {
                if (!_installed) return;
                var loaded = store.Get<ObservationLeaseRecords>(ObservationLeaseRecords.Key) ?? new ObservationLeaseRecords();
                loaded.Records ??= new List<ObservationLeaseRecord>();
                loaded.Records.RemoveAll(r => r == null);
                Pending.Clear();
                Pending.AddRange(loaded.Records);
                foreach (var r in _records.Records) loaded.Put(r);   // this run's leases, if any started first
                _records = loaded;
                _loaded = true;
                Save();
                _loadedAt = WorldSaveGuard.Now;
                if (Pending.Count > 0) Plugin.Logger?.LogInfo("Camera: " + Pending.Count + " streaming-origin record(s) from an earlier run to check.");
            });
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Camera: the streaming-origin record store threw: " + e.Message); }
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        WorldSaveGuard.Suspend -= OnSuspend;
        WorldSaveGuard.Resume -= OnResume;
        Plugin.Frame -= Tick;
        Save();
        _ready?.Dispose();
        _ready = null;
        _store = null;
        _loaded = false;
        Pending.Clear();
        _records = new ObservationLeaseRecords();
        _loadedAt = double.NaN;
    }

    // ---- records --------------------------------------------------------------------------------

    internal static void Put(ulong platform, float3 point, float3 original)
    {
        _records.Put(new ObservationLeaseRecord
        {
            Platform = platform, X = point.x, Y = point.y, Z = point.z, OriginalX = original.x, OriginalY = original.y, OriginalZ = original.z,
        });
        Save();
    }

    internal static void Remove(ulong platform)
    {
        if (_records.Remove(platform)) Save();
    }

    private static void Save()
    {
        if (!_loaded) return;   // the previous run's records are not loaded yet: never write over them
        try
        {
            _store?.Set(ObservationLeaseRecords.Key, _records);
            // Write the small store file now, not at the next ModSave.SaveAll: a crash before that would lose the record
            // of a lease whose point the world save may already hold.
            (_store as ModStore)?.SaveToDisk();
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Camera: storing the streaming-origin records threw: " + e.Message); }
    }

    // ---- the save guard ---------------------------------------------------------------------------

    private static void OnSuspend()
    {
        foreach (var lease in SharedObservation.All())
        {
            try { lease.SuspendProxy(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Camera: restoring a streaming origin for the save threw: " + e.Message); }
        }
    }

    private static void OnResume()
    {
        foreach (var lease in SharedObservation.All()) lease.ResumeProxy();
    }

    // ---- repairs ----------------------------------------------------------------------------------

    /// <summary>
    /// A lease start found the user's origin taken with no lease of this run: when it sits at this user's recorded lease
    /// point, put the recorded original back and return true (the start then proceeds).
    /// </summary>
    internal static bool TryRepair(World world, Entity user)
    {
        var em = world.EntityManager;
        var row = em.GetComponentData<User>(user);
        var record = _records.For(row.PlatformId);
        if (record == null) return false;
        var proxy = row.ProxySyncPosition.Value;
        bool at = ObservationProxyRules.AtPoint(proxy.x, proxy.y, proxy.z, record);
        if (ObservationProxyRules.Decide(SharedObservation.For(user) != null, at) != ObservationProxyRepair.Restore) return false;
        Restore(world, user, record, "a new lease found it left over");
        return true;
    }

    private static void Restore(World world, Entity user, ObservationLeaseRecord record, string why)
    {
        var original = new float3(record.OriginalX, record.OriginalY, record.OriginalZ);
        SharedObservation.WriteProxyRaw(world, user, original);
        Remove(record.Platform);
        Plugin.Logger?.LogWarning("Camera: user " + record.Platform + "'s streaming origin was still at a lease point of an earlier run ("
            + record.X + ", " + record.Y + ", " + record.Z + "); restored to (" + original.x + ", " + original.y + ", " + original.z + ") (" + why + ").");
    }

    private static void Tick()
    {
        if (Pending.Count == 0 || double.IsNaN(_loadedAt)) return;
        double now = WorldSaveGuard.Now;
        if (now < _nextTick) return;
        _nextTick = now + 1;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;
        for (int i = Pending.Count - 1; i >= 0; i--)
        {
            var r = Pending[i];
            try
            {
                var user = UserByPlatform(em, r.Platform);
                if (user == Entity.Null)
                {
                    if (now - _loadedAt < SweepWaitSeconds) continue;
                    Pending.RemoveAt(i);
                    if (SharedObservationFor(r.Platform) == null) Remove(r.Platform);
                    continue;
                }
                Pending.RemoveAt(i);
                if (SharedObservation.For(user) != null) continue;   // a lease of this run holds the record now
                var proxy = em.GetComponentData<User>(user).ProxySyncPosition.Value;
                bool at = ObservationProxyRules.AtPoint(proxy.x, proxy.y, proxy.z, r);
                if (ObservationProxyRules.Decide(false, at) == ObservationProxyRepair.Restore) Restore(world, user, r, "startup sweep");
                else
                {
                    Remove(r.Platform);
                    Plugin.Logger?.LogInfo("Camera: startup sweep: user " + r.Platform + "'s streaming origin is not at the recorded point; left as is.");
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Camera: startup sweep of user " + r.Platform + " threw: " + e.Message);
                Pending.RemoveAt(i);
            }
        }
    }

    private static SharedObservation? SharedObservationFor(ulong platform)
    {
        foreach (var lease in SharedObservation.All()) if (lease.Platform == platform) return lease;
        return null;
    }

    internal static Entity UserByPlatform(EntityManager em, ulong platform)
    {
        foreach (var e in Query.Entities(em, includeDisabled: true, includeSystems: false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly)))
        {
            try { if (em.GetComponentData<User>(e).PlatformId == platform) return e; }
            catch { /* unreadable: skip */ }
        }
        return Entity.Null;
    }

    /// <summary>
    /// Dev bridge <c>proxy_reset</c>: put a user's streaming origin back to zero (a one-off repair for an origin left over
    /// from before the records existed). Refused while a lease of this run holds that user.
    /// </summary>
    internal static Dictionary<string, object?> ResetToZero(ulong platform)
    {
        var world = Worlds.Server ?? throw new InvalidOperationException("no server world");
        var em = world.EntityManager;
        var user = UserByPlatform(em, platform);
        if (user == Entity.Null) throw new ArgumentException("no user with platform id " + platform);
        var before = em.GetComponentData<User>(user).ProxySyncPosition.Value;
        if (SharedObservation.For(user) != null)
            return new Dictionary<string, object?> { ["ok"] = false, ["note"] = "a streaming-origin lease of this run holds that user; end it first", ["before"] = Vec(before) };
        SharedObservation.WriteProxyRaw(world, user, float3.zero);
        Remove(platform);
        var after = em.GetComponentData<User>(user).ProxySyncPosition.Value;
        Plugin.Logger?.LogWarning("Camera: user " + platform + "'s streaming origin reset to zero by the Dev bridge (was " + before.x + ", " + before.y + ", " + before.z + ").");
        return new Dictionary<string, object?>
        {
            ["ok"] = ObservationProxyRules.IsZero(after.x, after.y, after.z),
            ["user"] = user.Index + ":" + user.Version,
            ["before"] = Vec(before),
            ["after"] = Vec(after),
        };
    }

    internal static float[] Vec(float3 v) => new[] { v.x, v.y, v.z };
}
#endif
