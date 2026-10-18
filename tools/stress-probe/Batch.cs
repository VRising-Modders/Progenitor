using System;
using System.Collections.Generic;
using System.Linq;
using ProjectM.CastleBuilding;
using Unity.Entities;
using Progenitor.Ecs;

namespace Progenitor.StressProbe;

public sealed partial class Plugin
{
    private bool _batchActive;
    private World? _batchWorld;
    private readonly Queue<(int Plot, Entity Territory)> _pending = new();
    private readonly Dictionary<int, (Entity Heart, Entity Owner)> _protected = new();
    private readonly List<int> _completed = new();

    private void BeginGeneration(World world, bool single = false)
    {
        if (_batchActive) throw new InvalidOperationException("Batch already running.");
        _batchWorld = world;
        foreach (var p in Progenitor.Castles.CastleReader.Plots(world))
            _protected[p.TerritoryIndex] = (p.HeartEntity, p.OwnerUserEntity);
        var targets = new List<(int Plot, Entity Territory)>();
        foreach (var e in Query.Entities(world.EntityManager, true, false, ComponentIndex.Of<CastleTerritory>(ComponentType.AccessMode.ReadOnly)))
        {
            var t = world.EntityManager.GetComponentData<CastleTerritory>(e);
            if (!Progenitor.Dev.Ops.StressProtectedPlots.Contains(t.CastleTerritoryIndex) && !world.EntityManager.Exists(t.CastleHeart))
                targets.Add((t.CastleTerritoryIndex, e));
        }
        foreach (var t in targets.OrderBy(t => t.Plot).Take(single ? 1 : int.MaxValue)) _pending.Enqueue(t);
        _batchActive = true;
        Log.LogInfo("stress-batch: begin emptyPlots=" + _pending.Count + " protected=" + _protected.Count);
        NextGeneration();
    }

    private World CheckBatch()
    {
        var w = _batchWorld;
        if (!_batchActive || w == null || !w.IsCreated || !ReferenceEquals(w, Worlds.Server))
            throw new InvalidOperationException("Batch world changed or batch stopped.");
        var checkTimer = System.Diagnostics.Stopwatch.StartNew();
        var plots = Progenitor.Castles.CastleReader.Plots(w);
        foreach (var expected in _protected)
        {
            var p = plots.FirstOrDefault(p => p.TerritoryIndex == expected.Key);
            if (p == null || p.HeartEntity != expected.Value.Heart || p.OwnerUserEntity != expected.Value.Owner)
                throw new InvalidOperationException("Protected plot changed: " + expected.Key);
        }
        Log.LogInfo("stress-timing: protectedCheckMs=" + checkTimer.ElapsedMilliseconds);
        return w;
    }

    private void NextGeneration()
    {
        if (!_batchActive) return;
        try
        {
            var w = CheckBatch();
            if (_pending.Count == 0)
            {
                _batchActive = false;
                Log.LogInfo("stress-batch: complete generated=" + _completed.Count + " plots=" + string.Join(",", _completed));
                return;
            }
            var target = _pending.Dequeue();
            var createTimer = System.Diagnostics.Stopwatch.StartNew();
            string result = GenerateOnce(w, target.Territory);
            Log.LogInfo("stress-timing: generateWithGhostMs=" + createTimer.ElapsedMilliseconds);
            if (result != "submitted-awaiting-initialization") throw new InvalidOperationException("plot=" + target.Plot + " " + result);
            Progenitor.Dev.MainThread.PostDelayed(2, () => VerifyGeneration(target.Plot), this);
        }
        catch (Exception ex) { _batchActive = false; Log.LogError("stress-batch: STOP " + ex); }
    }

    private void VerifyGeneration(int plot)
    {
        if (!_batchActive) return;
        try
        {
            var w = CheckBatch();
            var verifyTimer = System.Diagnostics.Stopwatch.StartNew();
            var p = Progenitor.Castles.CastleReader.Plots(w).FirstOrDefault(p => p.TerritoryIndex == plot);
            Log.LogInfo("stress-timing: readPlotsMs=" + verifyTimer.ElapsedMilliseconds);
            if (p == null || !w.EntityManager.Exists(p.HeartEntity) || p.FloorCount < 1)
                throw new InvalidOperationException("Missing generated castle plot=" + plot);
            var h = Progenitor.Castles.Castles.ForTerritory(plot);
            Log.LogInfo("stress-timing: handleMs=" + verifyTimer.ElapsedMilliseconds);
            var fuel = Progenitor.Dev.Ops.StressPlace.Fuel(w, p.HeartEntity, 100);
            if (!fuel.Ok || fuel.Added != 100) throw new InvalidOperationException("Fuel insertion failed plot=" + plot);
            _completed.Add(plot);
            Log.LogInfo("stress-timing: fuelMs=" + verifyTimer.ElapsedMilliseconds + " beginContainerStationRead");
            Log.LogInfo("stress-batch: verified plot=" + plot + " heart=" + p.HeartEntity + " floors=" + p.FloorCount + " containers=" + h.Containers().Count + " stations=" + h.Stations().Count + " completed=" + _completed.Count + " remaining=" + _pending.Count);
            Log.LogInfo("stress-timing: verifyTotalMs=" + verifyTimer.ElapsedMilliseconds);
            System.IO.File.WriteAllText(System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "stress-generated-plots.json"), System.Text.Json.JsonSerializer.Serialize(_completed));
            Progenitor.Dev.MainThread.PostDelayed(1, NextGeneration, this);
        }
        catch (Exception ex) { _batchActive = false; Log.LogError("stress-batch: STOP plot=" + plot + " " + ex); }
    }
}
