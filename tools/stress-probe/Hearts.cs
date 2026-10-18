using System;
using System.Collections.Generic;
using System.Linq;
using ProjectM.CastleBuilding;
using ProjectM.Gameplay.Systems;
using ProjectM.Network;
using Unity.Entities;
using Progenitor.Dev.Ops;

namespace Progenitor.StressProbe;

public sealed partial class Plugin
{
    private readonly Queue<int> _hearts = new();
    private int _heartsDone;

    private void BeginHearts(World world, bool single)
    {
        if (_batchActive) throw new InvalidOperationException("Batch already running.");
        _batchWorld = world;
        _protected.Clear();
        var plots = Progenitor.Castles.CastleReader.Plots(world);
        foreach (var p in plots) _protected[p.TerritoryIndex] = (p.HeartEntity, p.OwnerUserEntity);
        foreach (var p in plots.OrderBy(p => p.TerritoryIndex))
            if (!single || p.TerritoryIndex == 0) _hearts.Enqueue(p.TerritoryIndex);
        _batchActive = true;
        NextHeart();
    }

    private void NextHeart()
    {
        if (!_batchActive) return;
        try
        {
            var world = CheckBatch();
            if (_hearts.Count == 0)
            {
                _batchActive = false;
                Log.LogInfo("stress-hearts: COMPLETE rank5-full=" + _heartsDone);
                return;
            }
            int plot = _hearts.Dequeue();
            var entity = _protected[plot].Heart;
            var system = world.GetExistingSystemManaged<CastleHeartEventSystem>()
                ?? throw new InvalidOperationException("Heart upgrade system unavailable.");
            var heart = world.EntityManager.GetComponentData<CastleHeart>(entity);
            if (heart.Level < 1 || heart.Level > 5) throw new InvalidOperationException("Unexpected heart level.");
            if (heart.Level == 5)
            {
                // A resumed batch does not repeat upgrades or wait for an already-settled
                // heart. FillHeart still proves capacity and VerifyHeart checks ownership.
                FillHeart(plot);
                return;
            }
            // Verified runtime-invoke wrapper. This routine uses FromCharacter only for the
            // response notification; a null User skips that response in EventHelperServer.
            // It updates the level, upgrade sequence and inventory slots together.
            var source = new FromCharacter { User = Entity.Null, Character = Entity.Null };
            while (heart.Level < 5)
            {
                int before = heart.Level;
                system.UpgradeCastleHeart(entity, ref source, heart);
                heart = world.EntityManager.GetComponentData<CastleHeart>(entity);
                if (heart.Level != before + 1) throw new InvalidOperationException("Heart upgrade readback failed.");
            }
            Progenitor.Dev.MainThread.PostDelayed(0.25, () => FillHeart(plot), this);
        }
        catch (Exception ex) { _batchActive = false; Log.LogError("stress-hearts: STOP " + ex); }
    }

    private void FillHeart(int plot)
    {
        if (!_batchActive) return;
        try
        {
            var world = CheckBatch();
            var entity = _protected[plot].Heart;
            bool full = false;
            int added = 0;
            for (int i = 0; i < 100; i++)
            {
                var grant = StressPlace.Fuel(world, entity, 1000);
                added += grant.Added;
                if (grant.Result == "Failed_InventoryFull") { full = true; break; }
                if (!grant.Ok) throw new InvalidOperationException("Fuel failed: " + grant.Reason);
                if (grant.DidNotFit > 0) { full = true; break; }
            }
            if (!full) throw new InvalidOperationException("Fuel fill bound exceeded.");
            Log.LogInfo("stress-hearts: filled plot=" + plot + " added=" + added);
            if (added == 0) VerifyHeart(plot);
            else Progenitor.Dev.MainThread.PostDelayed(0.25, () => VerifyHeart(plot), this);
        }
        catch (Exception ex) { _batchActive = false; Log.LogError("stress-hearts: STOP plot=" + plot + " " + ex); }
    }

    private void VerifyHeart(int plot)
    {
        if (!_batchActive) return;
        try
        {
            var world = CheckBatch();
            var entity = _protected[plot].Heart;
            // Top off any essence consumed during the settling frame, then prove the next
            // single item cannot fit. This does not write fuel counters.
            var first = StressPlace.Fuel(world, entity, 1);
            if (first.Added == 1) first = StressPlace.Fuel(world, entity, 1);
            if (first.Result != "Failed_InventoryFull") throw new InvalidOperationException("Heart inventory is not full.");
            var heart = world.EntityManager.GetComponentData<CastleHeart>(entity);
            var fuel = Progenitor.Castles.Castles.ForTerritory(plot).HeartFuel();
            if (heart.Level != 5 || heart.FuelQuantity <= 0 || !fuel.Ok || fuel.Level != 5 || fuel.UnlockedSlots < 1)
                throw new InvalidOperationException("Rank/fuel readback failed.");
            _heartsDone++;
            Log.LogInfo("stress-hearts: verified plot=" + plot + " level=" + heart.Level + " fuel=" + heart.FuelQuantity
                + " slots=" + fuel.UnlockedSlots + " full=True completed=" + _heartsDone + " remaining=" + _hearts.Count);
            Progenitor.Dev.MainThread.PostDelayed(0.1, NextHeart, this);
        }
        catch (Exception ex) { _batchActive = false; Log.LogError("stress-hearts: STOP plot=" + plot + " " + ex); }
    }
}
