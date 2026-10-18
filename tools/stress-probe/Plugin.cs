using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using Progenitor.Ecs;
using Unity.Entities;
using System.IO;

namespace Progenitor.StressProbe;

[BepInPlugin("progenitor.stress-probe", "Progenitor Stress Probe", "0.1.0")]
[BepInDependency("progenitor")]
public sealed partial class Plugin : BasePlugin
{
    public override void Load()
    {
#if STRESS_OWNERS
        Progenitor.Dev.Ops.Plugin.Logger = Log;
#endif
        Progenitor.Dev.MainThread.Post(Inspect);
    }
    public override bool Unload() { _batchActive = false; Log.LogInfo("stress-probe: unloaded"); return true; }

    private void Inspect()
    {
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) { Log.LogInfo("stress-probe: no server world"); return; }
            var generator = world.GetExistingSystemManaged<GenerateCastleSystem>();
            var collection = world.GetExistingSystemManaged<GenerateCastlePrefabsCollectionSystem>();
            Log.LogInfo("stress-probe: generator=" + (generator == null ? "absent" : "present enabled=" + generator.Enabled)
                + " collection=" + (collection == null ? "absent" : "present enabled=" + collection.Enabled));
            InspectResult();
            foreach (var e in Query.Entities(world.EntityManager, true, false, ComponentIndex.Of<CastleTerritory>(ComponentType.AccessMode.ReadWrite)))
            {
                var t = world.EntityManager.GetComponentData<CastleTerritory>(e);
                if (t.CastleTerritoryIndex < 5)
                    Log.LogInfo("stress-probe: territory=" + t.CastleTerritoryIndex + " entity=" + e + " heart=" + t.CastleHeart);
            }
            foreach (var e in Query.Entities(world.EntityManager, true, false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadWrite)))
            {
                var u = world.EntityManager.GetComponentData<User>(e);
                if (u.IsAdmin) Log.LogInfo("stress-probe: admin=" + e + " connected=" + u.IsConnected);
            }
            // A one-use local marker makes ordinary plugin reloads read-only.
            string marker = Path.Combine(BepInEx.Paths.ConfigPath, "stress-probe-once.txt");
            if (!File.Exists(marker)) return;
            var action = File.ReadAllText(marker).Trim();
            File.Delete(marker);
            const int plot = 0;
            if (!string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""),
                @"C:\VRisingMods\_testserver\server\VRisingServer.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Server executable mismatch.");
            var argv = Environment.GetCommandLineArgs();
            int saveArg = Array.FindIndex(argv, a => string.Equals(a, "-persistentDataPath", StringComparison.OrdinalIgnoreCase));
            if (saveArg < 0 || saveArg + 1 >= argv.Length || !string.Equals(Path.GetFullPath(argv[saveArg + 1]).TrimEnd('\\'),
                @"C:\VRisingMods\_testserver\moo-world-copy-20260909", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Copied test save mismatch.");
            Entity target = Entity.Null;
            foreach (var e in Query.Entities(world.EntityManager, true, false, ComponentIndex.Of<CastleTerritory>(ComponentType.AccessMode.ReadOnly)))
                if (world.EntityManager.GetComponentData<CastleTerritory>(e).CastleTerritoryIndex == plot) target = e;
            if (action == "generate-rest") BeginGeneration(world);
            else if (action == "generate-next") BeginGeneration(world, true);
            else if (action == "rank5:0") BeginHearts(world, true);
            else if (action == "rank5-all") BeginHearts(world, false);
#if STRESS_OWNERS
            else if (action == "claim:0") BeginClaims(world, single: true);
            else if (action == "claim-rest") BeginClaims(world, single: false);
#endif
            else if (action == "generate:0") RunOne(world, target);
            else if (action == "fuel:0")
            {
                var heart = world.EntityManager.GetComponentData<CastleTerritory>(target).CastleHeart;
                var result = Progenitor.Dev.Ops.StressPlace.Fuel(world, heart, 100);
                Log.LogInfo("stress-probe: fuel added=" + result.Added + " remaining=" + result.DidNotFit + " ok=" + result.Ok);
                Progenitor.Dev.MainThread.PostDelayed(3, InspectResult, this);
            }
            else throw new InvalidOperationException("Unknown probe action.");
        }
        catch (Exception ex) { Log.LogError("stress-probe: " + ex); }
    }

    private void RunOne(World world, Entity territory)
    {
        Log.LogInfo("stress-probe: generate=" + GenerateOnce(world, territory));
        Progenitor.Dev.MainThread.PostDelayed(3, InspectResult, this);
    }

    private string GenerateOnce(World world, Entity territory)
    {
        var em = world.EntityManager;
        var before = new System.Collections.Generic.HashSet<Entity>(Query.Entities(em, true, false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly)));
        try
        {
            // Reuse the existing Dev ghost creator; never leave the user across a frame.
            using var args = System.Text.Json.JsonDocument.Parse("{\"action\":\"create\",\"count\":1,\"namePrefix\":\"CastleProbe\",\"admin\":true,\"connectedUser\":false}");
            Progenitor.Dev.Ops.GhostOps.Run(args.RootElement);
            Entity temporary = Entity.Null;
            foreach (var e in Query.Entities(em, true, false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly)))
                if (!before.Contains(e)) temporary = e;
            if (temporary == Entity.Null) throw new InvalidOperationException("Temporary admin creation failed.");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            string result = Progenitor.Dev.Ops.StressPlace.Generate(world, territory, temporary);
            Log.LogInfo("stress-probe: generation elapsedMs=" + timer.Elapsed.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            return result;
        }
        finally
        {
            foreach (var e in Query.Entities(em, true, false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly)))
                if (!before.Contains(e) && em.Exists(e)) em.DestroyEntity(e);
            Log.LogInfo("stress-probe: temporary-user cleanup count=" + Query.Entities(em, true, false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly)).Count + " expected=" + before.Count);
        }
    }

    private void InspectResult()
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        foreach (var p in Progenitor.Castles.CastleReader.Plots(world))
        {
            if (p.TerritoryIndex == 45 || p.TerritoryIndex == 67 || p.TerritoryIndex == 86 || p.TerritoryIndex == 92 || p.TerritoryIndex == 125 || p.TerritoryIndex == 129 || p.TerritoryIndex == 136)
                Log.LogInfo("stress-probe: protected plot=" + p.TerritoryIndex + " heart=" + p.HeartEntity + " owner=" + p.OwnerUserEntity);
            if (p.TerritoryIndex == 0)
            {
                var handle = Progenitor.Castles.Castles.ForTerritory(0);
                var heart = world.EntityManager.GetComponentData<CastleHeart>(p.HeartEntity);
                Log.LogInfo("stress-probe: result plot=0 heart=" + p.HeartEntity + " floors=" + p.FloorCount
                    + " buildings=" + Progenitor.Castles.CastleReader.Buildings(world, p).Count + " owner=" + p.OwnerUserEntity
                    + " containers=" + handle.Containers().Count + " stations=" + handle.Stations().Count
                    + " revision=" + handle.StructureRevision + " fuel=" + heart.FuelQuantity + " fuelEnd=" + heart.FuelEndTime);
            }
        }
    }
}
