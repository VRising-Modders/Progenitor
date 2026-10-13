#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Behaviours;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using Progenitor.Ecs;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Units;

/// <summary>
/// Live half of <c>ctx.Units.ClearArea</c> and of the summon sweep that follows every unit despawn.
/// Reads facts off entities and leaves every decision to <see cref="ClearAreaPolicy"/>.
/// <para>
/// Surveys use <see cref="Query.Entities(EntityManager, bool, bool, ComponentType[])"/> with disabled
/// entities included (downed bosses, and units out of player range, are disabled). Component reads
/// are presence checks through <see cref="EntityExtensions.Has{T}"/>, the proven
/// <c>TryRead&lt;EntityOwner&gt;</c> / <c>TryRead&lt;Translation&gt;</c> copies, and a one-byte raw
/// read of <c>IsMinion.Value</c> (metadata: <c>bool Value</c> @0x0, the struct's only field). No new
/// generic interop instantiation over a game value type.
/// </para>
/// <para>
/// Removal is always <see cref="UnityUnitRuntime.DespawnEntity"/> (player refusal, DIAG5 buff
/// sweep, then <see cref="BuffSweep.DestroyUnitReplicated"/>) or, for this plugin's own units,
/// <see cref="UnitHandle.Dispose"/>, which takes the same path. Never a hand-added <c>DestroyTag</c>.
/// </para>
/// </summary>
internal static unsafe class UnityClearArea
{
    /// <summary>Start a clear. Pass 1 runs now; later passes are posted with <paramref name="owner"/>.</summary>
    internal static void Start(UnityUnitRuntime runtime, object owner, float3 center, float radius, ClearAreaOptions options, int maxPasses, ClearAreaResult result)
    {
        var run = new ClearRun(runtime, owner, center, radius, options, maxPasses, result);
        run.Pass();
    }

    /// <summary>One <c>ClearArea</c> call, alive until its last pass.</summary>
    private sealed class ClearRun
    {
        private readonly UnityUnitRuntime _runtime;
        private readonly object _owner;
        private readonly float3 _center;
        private readonly float _radius;
        private readonly ClearAreaOptions _options;
        private readonly int _maxPasses;
        private readonly ClearAreaResult _result;
        private readonly int _epoch;
        private readonly HashSet<Entity> _scheduled = new();
        private readonly HashSet<Entity> _started = new();
        private readonly HashSet<Entity> _ours = new();

        internal ClearRun(UnityUnitRuntime runtime, object owner, float3 center, float radius, ClearAreaOptions options, int maxPasses, ClearAreaResult result)
        {
            _runtime = runtime;
            _owner = owner;
            _center = center;
            _radius = radius;
            _options = options;
            _maxPasses = maxPasses;
            _result = result;
            _epoch = Progenitor.WorldState.WorldEpoch.Current;
        }

        internal void Pass()
        {
            try
            {
                if (!TryWorld(out var em, out string? why))
                {
                    Finish(why);
                    return;
                }

                // Only removals that were started count as in flight; a refused one would keep
                // the loop going for nothing.
                int alive = 0;
                foreach (var e in _started)
                    if (SafeExists(em, e)) alive++;

                var handles = new Dictionary<Entity, UnitHandle>();
                foreach (var handle in Units.LiveOwnedBy(_owner))
                {
                    var e = handle.Entity.ToEntity();
                    handles[e] = handle;
                    _ours.Add(e);
                }

                int found = 0;
                foreach (var e in Survey(em))
                {
                    if (_scheduled.Contains(e)) continue;
                    var c = Read(em, e, handles, _ours, _center, _radius);
                    var kind = ClearAreaPolicy.Classify(c, _options);
                    if (kind == ClearTargetKind.None) continue;

                    bool started;
                    if (handles.TryGetValue(e, out var mine))
                    {
                        try { mine.Dispose(); started = true; }
                        catch (Exception ex)
                        {
                            Plugin.Logger?.LogWarning("Units: ClearArea dispose threw on " + Fmt(e) + ": " + ex.Message);
                            started = false;
                        }
                    }
                    else
                    {
                        started = _runtime.DespawnEntity(em, e);
                    }

                    _scheduled.Add(e);
                    if (!started) continue;
                    _started.Add(e);
                    // A summon of a summon is ours too.
                    if (kind != ClearTargetKind.Drop) _ours.Add(e);
                    _result.Count(kind);
                    found++;
                }

                _result.Passes++;
                Plugin.Logger?.LogInfo("Units: ClearArea pass " + _result.Passes + "/" + _maxPasses
                    + " scope=" + _options.Scope + " new=" + found + " stillAlive=" + alive + " " + _result);

                switch (ClearAreaPolicy.After(_result.Passes, _maxPasses, found, alive))
                {
                    case ClearAreaStep.Done:
                        _result.Remaining = 0;
                        Finish(null);
                        return;
                    case ClearAreaStep.Verify:
                        Post(ClearAreaPolicy.VerifyDelaySeconds, Verify);
                        return;
                    default:
                        Post(ClearAreaPolicy.PassIntervalSeconds, Pass);
                        return;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Units: ClearArea pass threw: " + e.Message);
                Finish("pass threw: " + e.Message);
            }
        }

        private void Verify()
        {
            try
            {
                if (!TryWorld(out var em, out string? why))
                {
                    Finish(why);
                    return;
                }

                var handles = new Dictionary<Entity, UnitHandle>();
                foreach (var handle in Units.LiveOwnedBy(_owner))
                {
                    var e = handle.Entity.ToEntity();
                    handles[e] = handle;
                    _ours.Add(e);
                }

                int left = 0;
                foreach (var e in Survey(em))
                {
                    var c = Read(em, e, handles, _ours, _center, _radius);
                    // Count what is still there, including removals still in flight.
                    c.AlreadyScheduled = false;
                    c.Dying = false;
                    if (ClearAreaPolicy.Classify(c, _options) != ClearTargetKind.None) left++;
                }

                _result.Remaining = left;
                Finish(null);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Units: ClearArea verify threw: " + e.Message);
                Finish("verify threw: " + e.Message);
            }
        }

        private IEnumerable<Entity> Survey(EntityManager em)
        {
            var seen = new HashSet<Entity>();
            if (_options.IncludeUnits || _options.IncludeSummons)
            {
                foreach (var e in Query.Entities(em, includeDisabled: true, includeSystems: false,
                             ComponentIndex.Of<BehaviourTreeInstance>(ComponentType.AccessMode.ReadOnly)))
                    if (seen.Add(e)) yield return e;
            }
            if (_options.IncludeDrops)
            {
                foreach (var e in Query.Entities(em, includeDisabled: true, includeSystems: false,
                             ComponentIndex.Of<ItemPickup>(ComponentType.AccessMode.ReadOnly)))
                    if (seen.Add(e)) yield return e;
            }
        }

        private bool TryWorld(out EntityManager em, out string? why)
        {
            em = default;
            why = null;
            if (Progenitor.WorldState.WorldEpoch.IsStale(_epoch))
            {
                why = "the world reloaded during the clear";
                return false;
            }
            var world = Worlds.Server;
            if (world == null || !world.IsCreated)
            {
                why = "no Server world";
                return false;
            }
            em = world.EntityManager;
            return true;
        }

        private void Post(double seconds, Action next)
        {
            try { Dev.MainThread.PostDelayed(seconds, next, _owner); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Units: ClearArea defer threw: " + e.Message);
                Finish("defer threw: " + e.Message);
            }
        }

        private void Finish(string? refusal)
        {
            if (_result.IsComplete) return;
            if (refusal != null) _result.Refusal = refusal;
            _result.IsComplete = true;
            Plugin.Logger?.LogInfo("Units: ClearArea done " + _result);
            var done = _options.OnComplete;
            if (done == null) return;
            try { done(_result); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Units: ClearArea OnComplete threw: " + e.Message); }
        }
    }

    /// <summary>
    /// Removes the live summons of units that were despawned (handle Despawn, <c>RemoveAll</c>,
    /// plugin unload, ClearArea). A boss keeps summoning while it is being removed, so this runs up
    /// to <see cref="ClearAreaOptions.DefaultMaxPasses"/> passes a second apart after the last
    /// despawn, restarting the count when another unit is retired. One instance per runtime; work is
    /// posted with the runtime as owner so it outlives the plugin that spawned the unit.
    /// </summary>
    internal sealed class SummonSweep
    {
        private static readonly ClearAreaOptions SummonsOnly = new()
        {
            Scope = ClearAreaScope.Owned,
            IncludeUnits = false,
            IncludeSummons = true,
            IncludeDrops = false,
        };

        private readonly UnityUnitRuntime _runtime;
        private readonly HashSet<Entity> _owners = new();
        private readonly HashSet<Entity> _scheduled = new();
        private readonly HashSet<Entity> _started = new();
        private int _epoch = int.MinValue;
        private int _passes;
        private int _removed;
        private bool _posted;

        internal SummonSweep(UnityUnitRuntime runtime) => _runtime = runtime;

        internal void Retire(Entity unit)
        {
            int epoch = Progenitor.WorldState.WorldEpoch.Current;
            if (epoch != _epoch)
            {
                Reset();
                _epoch = epoch;
            }
            _owners.Add(unit);
            _passes = 0;
            if (_posted) return;
            _posted = true;
            // Next frame: RemoveAll retires many units in one call; one survey covers them all.
            Post(0);
        }

        private void Post(double seconds)
        {
            try { Dev.MainThread.PostDelayed(seconds, Pass, _runtime); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Units: summon sweep defer threw: " + e.Message);
                Reset();
            }
        }

        private void Reset()
        {
            _owners.Clear();
            _scheduled.Clear();
            _started.Clear();
            _passes = 0;
            _removed = 0;
            _posted = false;
        }

        private void Pass()
        {
            try
            {
                var world = Worlds.Server;
                if (Progenitor.WorldState.WorldEpoch.IsStale(_epoch) || world == null || !world.IsCreated)
                {
                    Reset();
                    return;
                }
                var em = world.EntityManager;

                int alive = 0;
                foreach (var e in _started)
                    if (SafeExists(em, e)) alive++;

                int found = 0;
                var none = new Dictionary<Entity, UnitHandle>();
                foreach (var e in Query.Entities(em, includeDisabled: true, includeSystems: false,
                             ComponentIndex.Of<BehaviourTreeInstance>(ComponentType.AccessMode.ReadOnly),
                             ComponentIndex.Of<EntityOwner>(ComponentType.AccessMode.ReadOnly)))
                {
                    if (_scheduled.Contains(e)) continue;
                    var c = Read(em, e, none, _owners, default, 0f);
                    c.InArea = true;
                    if (ClearAreaPolicy.Classify(c, SummonsOnly) != ClearTargetKind.Summon) continue;
                    _scheduled.Add(e);
                    if (!_runtime.DespawnEntity(em, e)) continue;
                    _started.Add(e);
                    _owners.Add(e);
                    found++;
                }

                _removed += found;
                _passes++;
                switch (ClearAreaPolicy.After(_passes, ClearAreaOptions.DefaultMaxPasses, found, alive))
                {
                    case ClearAreaStep.AnotherPass:
                        Post(ClearAreaPolicy.PassIntervalSeconds);
                        return;
                    case ClearAreaStep.Verify:
                        Plugin.Logger?.LogInfo("Units: summon sweep removed " + _removed + " summon(s) of despawned unit(s); "
                            + (found + alive) + " still pending after " + _passes + " pass(es).");
                        Reset();
                        return;
                    default:
                        if (_removed > 0)
                            Plugin.Logger?.LogInfo("Units: summon sweep removed " + _removed + " summon(s) of despawned unit(s) in " + _passes + " pass(es).");
                        Reset();
                        return;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Units: summon sweep threw: " + e.Message);
                Reset();
            }
        }
    }

    /// <summary>Read the facts <see cref="ClearAreaPolicy.Classify"/> needs off one entity.</summary>
    private static ClearCandidate Read(
        EntityManager em,
        Entity e,
        Dictionary<Entity, UnitHandle> ourHandles,
        HashSet<Entity> ours,
        float3 center,
        float radius)
    {
        var c = new ClearCandidate();
        if (!SafeExists(em, e)) return c;
        if (radius > 0f)
        {
            if (!TryPosition(em, e, out var p)) return c;
            c.InArea = ClearAreaPolicy.InArea(center.x, center.z, radius, p.x, p.z);
            if (!c.InArea) return c;
        }

        c.Dying = e.Has<DestroyTag>(em);
        c.IsPlayerCharacter = e.Has<PlayerCharacter>(em);
        c.IsUser = e.Has<User>(em);
        // Not TileModel: NPC units carry it too (live: CHAR_Militia_BishopOfDunley_VBlood has
        // TileModel and TileModelSpatialData), so it would protect every boss. Candidates are already
        // limited to units with a behavior tree and ItemPickup drops, so plain castle pieces never
        // reach this point; a castle-bound unit is recognized by its heart connection.
        c.IsCastleStructure = e.Has<CastleHeartConnection>(em);
        // Imprisoned links a prisoner to its cell (PrisonCellEntity), not through EntityOwner, so
        // a player’s prisoner is protected here rather than by the owner walk.
        c.IsServant = e.Has<ServantConnectedCoffin>(em) || e.Has<ServantTypeData>(em) || e.Has<Imprisoned>(em);
        c.IsMountable = e.Has<Mountable>(em);
        c.IsUnit = e.Has<BehaviourTreeInstance>(em);
        c.IsDrop = e.Has<ItemPickup>(em);
        c.IsMinion = e.Has<Minion>(em) || ReadIsMinion(em, e);
        // A handle this clear already disposed is no longer live but is still ours.
        c.IsOurUnit = ourHandles.ContainsKey(e) || ours.Contains(e);

        var chain = ClearAreaPolicy.WalkOwners(
            UnitId.From(e),
            id => OwnerOf(em, id),
            id => IsPlayer(em, id),
            id => ours.Contains(id.ToEntity()));
        c.PlayerOwned = chain.PlayerOwned;
        c.OwnerUnknown = chain.Unknown;
        c.OwnedByOurs = chain.OursOwned;
        return c;
    }

    private static UnitId OwnerOf(EntityManager em, UnitId id)
    {
        try
        {
            var e = id.ToEntity();
            if (!em.Exists(e)) return UnitId.None;
            if (!e.TryRead<EntityOwner>(em, out var owner)) return UnitId.None;
            return owner.Owner == Entity.Null ? UnitId.None : UnitId.From(owner.Owner);
        }
        catch { return UnitId.None; }
    }

    /// <summary>
    /// A removed owner no longer exists and so is not a player; an owner id that still names a
    /// player character or user is.
    /// </summary>
    private static bool IsPlayer(EntityManager em, UnitId id)
    {
        var e = id.ToEntity();
        return e.Has<PlayerCharacter>(em) || e.Has<User>(em);
    }

    /// <summary>
    /// <c>IsMinion.Value</c>: a one-byte bool at offset 0 (<c>ProjectM.IsMinion</c> in the server metadata). Read raw
    /// so no bool-bearing wrapper goes through a generic copy.
    /// </summary>
    private static bool ReadIsMinion(EntityManager em, Entity e)
    {
        try
        {
            if (!e.Has<IsMinion>(em)) return false;
            void* p = em.GetComponentDataRawRO(e, ComponentIndex.IndexOf<IsMinion>());
            return p != null && *(byte*)p != 0;
        }
        catch { return false; }
    }

    private static bool TryPosition(EntityManager em, Entity e, out float3 pos)
    {
        pos = default;
        try
        {
            if (e.Has<Translation>(em) && e.TryRead<Translation>(em, out var t))
            {
                pos = t.Value;
                return true;
            }
            if (e.Has<LocalToWorld>(em) && e.TryRead<LocalToWorld>(em, out var l))
            {
                pos = l.Position;
                return true;
            }
        }
        catch { /* unreadable position: not in the area */ }
        return false;
    }

    private static bool SafeExists(EntityManager em, Entity e)
    {
        try { return em.Exists(e); }
        catch { return false; }
    }

    private static string Fmt(Entity e) => e.Index + ":" + e.Version;
}
#endif
