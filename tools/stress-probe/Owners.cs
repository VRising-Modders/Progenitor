#if STRESS_OWNERS
using System;
using System.Collections.Generic;
using System.Linq;
using ProjectM;
using ProjectM.Network;
using Progenitor.Dev.Ops;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.StressProbe;

public sealed partial class Plugin
{
    private readonly Queue<(int Plot, Entity Owner)> _claims = new();
    private readonly Dictionary<int, Entity> _owners = new();
    private int _claimsDone;
    private const int Seed = 18;

    private void BeginClaims(World world, bool single)
    {
        if (_batchActive) throw new InvalidOperationException("Batch already running.");
        _owners.Clear();
        _claims.Clear();
        _claimsDone = 0;
        _batchWorld = world;
        _protected.Clear();
        foreach (var p in Progenitor.Castles.CastleReader.Plots(world))
            if (Progenitor.Dev.Ops.StressProtectedPlots.Contains(p.TerritoryIndex)) _protected[p.TerritoryIndex] = (p.HeartEntity, p.OwnerUserEntity);
        if (_protected.Count != 7) throw new InvalidOperationException("Expected seven protected castles.");
        int count = single ? 1 : 40;
        var em = world.EntityManager;
        var users = Query.Entities(em, true, false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        var used = users.Select(e => em.GetComponentData<User>(e).Index).ToArray();
        var names = new HashSet<string>(users.Select(e => em.GetComponentData<User>(e).CharacterName.ToString()), StringComparer.OrdinalIgnoreCase);
        for (int n = 1; n <= count; n++)
        {
            ulong platform = StressIdentity.PlatformId(Seed, n);
            var matches = users.Where(e => em.GetComponentData<User>(e).PlatformId == platform).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Duplicate stress identity " + platform);
            if (matches.Length == 1) _owners[n] = matches[0];
        }
        if (!StressIndexPlan.TryAllocate(used, count - _owners.Count, out var indices, out var reason))
            throw new InvalidOperationException(reason);
        int ix = 0;
        for (int n = 1; n <= count; n++)
        {
            bool created = false;
            if (!_owners.TryGetValue(n, out var user))
            {
                if (names.Contains(StressIdentity.OwnerName(n))) throw new InvalidOperationException("Stress name already used by another identity.");
                int index = indices[ix++];
                var current = Query.Entities(em, true, false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
                if (current.Any(e => { var u = em.GetComponentData<User>(e); return u.Index == index || u.Index > 111; }))
                    throw new InvalidOperationException("Owner index changed since planning.");
                user = GhostOps.CreateOfflineUser(em, index, StressIdentity.PlatformId(Seed, n), StressIdentity.OwnerName(n), out var read);
                created = true;
            }
            var owner = em.GetComponentData<User>(user);
            if (owner.IsConnected || user.Has<ConnectedUser>(em) || owner.LocalCharacter._Entity != Entity.Null)
                throw new InvalidOperationException("Stress owner is not offline.");
            var team = GhostOps.EnsureOfflineUserTeam(world, user);
            if (!team.Ok)
            {
                if (created) GhostOps.TryDestroyGhostEntity(em, user);
                throw new InvalidOperationException("Owner team failed: " + team.Code + " " + team.Detail);
            }
            _owners[n] = user;
            Log.LogInfo("stress-owners: ready name=" + StressIdentity.OwnerName(n) + " user=" + user + " index=" + owner.Index + " team=" + team.Team);
        }
        var plots = Progenitor.Castles.CastleReader.Plots(world).Where(p => !Progenitor.Dev.Ops.StressProtectedPlots.Contains(p.TerritoryIndex)).OrderBy(p => p.TerritoryIndex).ToArray();
        if (single) plots = plots.Where(p => p.TerritoryIndex == 0).ToArray();
        if (plots.Length == 0) throw new InvalidOperationException("No generated castles.");
        for (int i = 0; i < plots.Length; i++) _claims.Enqueue((plots[i].TerritoryIndex, _owners[(i % count) + 1]));
        _batchActive = true;
        Log.LogInfo("stress-owners: beginning claims=" + _claims.Count + " owners=" + _owners.Count);
        // Allow the instantiated team prefabs to finish their spawn steps.
        Progenitor.Dev.MainThread.PostDelayed(3, NextClaim, this);
    }

    private void NextClaim()
    {
        if (!_batchActive) return;
        try
        {
            var world = CheckBatch();
            if (_claims.Count == 0)
            {
                _batchActive = false;
                Log.LogInfo("stress-owners: COMPLETE claimed=" + _claimsDone + " owners=" + _owners.Count);
                return;
            }
            var target = _claims.Dequeue();
            var p = Progenitor.Castles.CastleReader.Plots(world).Single(p => p.TerritoryIndex == target.Plot);
            if (p.OwnerUserEntity != Entity.Null && p.OwnerUserEntity != target.Owner)
                throw new InvalidOperationException("Refuse replacing owner plot=" + target.Plot);
            var c = GhostOps.ObserveClaim(world.EntityManager, target.Owner, p.HeartEntity, out var castleTeam, out var oldAllies);
            var refusal = OfflineUserTeamRules.ClaimCastleRefusal(c);
            if (refusal != null) throw new InvalidOperationException(refusal);
            if (p.OwnerUserEntity == Entity.Null)
            {
                if (oldAllies.Count != 0) throw new InvalidOperationException("Unowned heart has unexpected allies.");
                TeamUtility.ClaimCastle(world.EntityManager, target.Owner, p.HeartEntity, CastleHeartLimitType.User);
            }
            if (world.EntityManager.GetComponentData<ProjectM.CastleBuilding.CastleHeart>(p.HeartEntity).FuelQuantity <= 0)
            {
                var fuel = StressPlace.Fuel(world, p.HeartEntity, 100);
                if (!fuel.Ok || fuel.Added != 100) throw new InvalidOperationException("Heart refuel failed plot=" + target.Plot);
                Log.LogInfo("stress-owners: refueled plot=" + target.Plot);
            }
            Progenitor.Dev.MainThread.PostDelayed(2, () => VerifyClaim(target.Plot, target.Owner), this);
        }
        catch (Exception ex) { _batchActive = false; Log.LogError("stress-owners: STOP " + ex); }
    }

    private void VerifyClaim(int plot, Entity owner)
    {
        if (!_batchActive) return;
        try
        {
            var world = CheckBatch();
            var em = world.EntityManager;
            var p = Progenitor.Castles.CastleReader.Plots(world).Single(p => p.TerritoryIndex == plot);
            if (p.OwnerUserEntity != owner) throw new InvalidOperationException("Owner readback mismatch plot=" + plot);
            GhostOps.ObserveOfflineUserTeam(em, owner, out var userTeam, out var userAllies);
            GhostOps.ObserveClaim(em, owner, p.HeartEntity, out var castleTeam, out var castleAllies);
            if (!userAllies.Contains(castleTeam) || castleAllies.Count != 1 || castleAllies[0] != userTeam)
                throw new InvalidOperationException("Reciprocal team links missing plot=" + plot);
            _claimsDone++;
            Log.LogInfo("stress-owners: verified plot=" + plot + " owner=" + owner + " ownerTeam=" + userTeam + " castleTeam=" + castleTeam + " claimed=" + _claimsDone + " remaining=" + _claims.Count);
            Progenitor.Dev.MainThread.PostDelayed(0.1, NextClaim, this);
        }
        catch (Exception ex) { _batchActive = false; Log.LogError("stress-owners: STOP plot=" + plot + " " + ex); }
    }
}
#endif
