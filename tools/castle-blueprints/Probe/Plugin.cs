using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using ProjectM;
using ProjectM.CastleBuilding;
using Progenitor;
using Progenitor.Castles;
using Progenitor.Ecs;
using Progenitor.Terrain;
using Unity.Entities;
using Unity.Transforms;

namespace Progenitor.CastleBlueprintProbe;

/// <summary>One-shot, read-only snapshots. No generation, clearing, or placement.</summary>
[BepInPlugin("progenitor.castle-blueprint-probe", "Castle Blueprint Probe", "0.1.0")]
[BepInDependency("progenitor")]
public sealed class Plugin : BasePlugin
{
    private bool _active;
    private World? _world;
    private List<CastlePlot> _plots = new();
    private int _next;
    private string _output = "";

    public override void Load()
    {
        _active = true;
        Progenitor.Dev.MainThread.Post(Start);
    }
    public override bool Unload()
    {
        _active = false;
        _plots.Clear();
        _world = null;
        Log.LogInfo("blueprint-probe: unloaded");
        return true;
    }
    private void Start()
    {
        if (!_active) return;
        try
        {
            _world = Worlds.Server;
            if (_world == null || !_world.IsCreated) throw new InvalidOperationException("Server world required.");
            var request = Path.Combine(Paths.ConfigPath, "castle-blueprint-read.json");
            var audit = Path.Combine(Paths.ConfigPath, "castle-blueprint-audit.json");
            if (File.Exists(audit))
            {
                _output = Path.Combine(Paths.BepInExRootPath, "castle-blueprint-read");
                Directory.CreateDirectory(_output);
                AreaAudit.Run(_world, audit, _output);
                Log.LogInfo("blueprint-probe: area audit complete");
                return;
            }
            if (!File.Exists(request)) { Log.LogInfo("blueprint-probe: no read request"); return; }
            var ids = JsonSerializer.Deserialize<int[]>(File.ReadAllText(request))
                ?? throw new InvalidOperationException("Expected an array of territory indices.");
            if (ids.Length == 0 || ids.Length > 16) throw new InvalidOperationException("Select 1–16 plots per batch.");
            _plots = CastleReader.Plots(_world).Where(p => ids.Contains(p.TerritoryIndex)).ToList();
            if (_plots.Count != ids.Distinct().Count()) throw new InvalidOperationException("A requested heart-bearing plot was not found.");
            _output = Path.Combine(Paths.BepInExRootPath, "castle-blueprint-read");
            Directory.CreateDirectory(_output);
            Next();
        }
        catch (Exception e) { Log.LogError("blueprint-probe: " + e); }
    }
    private void Next()
    {
        if (!_active || _world == null || !_world.IsCreated || _world != Worlds.Server) return;
        try
        {
            var p = _plots[_next++];
            var em = _world.EntityManager;
            var settings = _world.GetExistingSystemManaged<ServerGameSettingsSystem>()?.Settings;
            var limits = settings?.CastleStatModifiers_Global?.HeartLimits;
            var row = p.Level switch { 1 => limits?.Level1, 2 => limits?.Level2, 3 => limits?.Level3,
                4 => limits?.Level4, 5 => limits?.Level5, _ => null };
            using var ground = GroundProbe.TryOpen(_world, out var groundError);
            if (ground == null || p.BlocksSkipped) throw new InvalidOperationException("Missing ground or plot blocks: " + groundError);
            var floors = CastleReader.Tiles(_world, p);
            var buildings = CastleReader.Buildings(_world, p);
            if (!p.HeartEntity.TryRead(em, out Translation heartPosition))
                throw new InvalidOperationException("Heart position is unreadable.");
            var heartBlockX = (int)Math.Floor((heartPosition.Value.x + 3200) / 5);
            var heartBlockY = (int)Math.Floor((heartPosition.Value.z + 3200) / 5);
            var blocks = p.BlockCoordinates.Select(b =>
            {
                var heights = new List<float?>();
                // Centre and inset corners: screening only, not complete collision validation.
                foreach (var offset in new[] { (5,5), (1,1), (9,1), (1,9), (9,9) })
                    heights.Add(ground.TryGetHeight(CastleGeometry.TileToWorld(b.X * 10 + offset.Item1), -51,
                        CastleGeometry.TileToWorld(b.Y * 10 + offset.Item2), out var h) ? h : null);
                return new { x = b.X, y = b.Y, heights };
            }).ToArray();
            var result = new
            {
                schema = 1, capturedUtc = DateTime.UtcNow, plot = p.TerritoryIndex,
                heartLevel = p.Level, floorCount = p.FloorCount,
                heart = new { x = heartBlockX, y = heartBlockY },
                floorLimit = (int?)row?.FloorLimit, heightLimit = (int?)row?.HeightLimit,
                blocks,
                rooms = floors.Select(f => f.RoomEntity).Where(e => e != Entity.Null).Distinct()
                    .Where(e => e.Has<CastleRoom>(em)).Select(e =>
                    {
                        var r = em.GetComponentData<CastleRoom>(e);
                        return new { id = e.Index, missingWalls = r.IsMissingWalls, hasRoof = r.HasRoof };
                    }),
                floors = floors.Select(f => new { x = f.X, y = f.Y, height = f.Height,
                    room = f.RoomId, walls = f.WallFlags, doors = f.DoorFlags,
                    impassable = f.Impassable, type = f.FloorTypeName,
                    roof = f.FloorEntity.Has<CastleFloorRoof>(em) }),
                buildings = buildings.Select(b => new { prefab = b.PrefabName, x = b.OriginX,
                    y = b.OriginY, height = b.OriginHeight, rotation = b.Rotation,
                    minX = b.FootprintMinX, minY = b.FootprintMinY,
                    maxX = b.FootprintMaxX, maxY = b.FootprintMaxY, kind = b.Kind.ToString() }),
                notChecked = new[] { "outside approach accessibility", "complete terrain collision",
                    "player unlocks", "material payment", "native placement validation",
                    "roof component contents (presence alone is not a roof proof)" }
            };
            File.WriteAllText(Path.Combine(_output, "plot-" + p.TerritoryIndex + ".json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Log.LogInfo("blueprint-probe: read plot=" + p.TerritoryIndex + " blocks=" + blocks.Length);
            if (_next < _plots.Count) Progenitor.Dev.MainThread.PostDelayed(.5, Next, this);
            else Log.LogInfo("blueprint-probe: complete plots=" + _plots.Count);
        }
        catch (Exception e) { Log.LogError("blueprint-probe: " + e); }
    }
}
