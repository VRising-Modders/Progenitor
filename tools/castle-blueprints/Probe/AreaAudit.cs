using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ProjectM;
using Progenitor.Castles;
using Progenitor.Ecs;
using Progenitor.Terrain;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.CastleBlueprintProbe;

internal static class AreaAudit
{
    internal static void Run(World world, string requestPath, string output)
    {
        using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
        var r = request.RootElement;
        int x = r.GetProperty("x").GetInt32(), y = r.GetProperty("y").GetInt32(),
            width = r.GetProperty("width").GetInt32(), depth = r.GetProperty("depth").GetInt32();
        if (width < 1 || width > 4 || depth < 1 || depth > 4 ||
            x < 0 || y < 0 || x > 1280 || y > 1280) throw new ArgumentException("Bounded 1–4 block audit required.");
        using var ground = GroundProbe.TryOpen(world, out var why);
        if (ground == null) throw new InvalidOperationException(why);
        var samples = new List<object>();
        // Half-meter collision tiles over the complete footprint, plus the one-tile edge.
        for (int ty = y * 10 - 1; ty <= (y + depth) * 10; ty++)
        for (int tx = x * 10 - 1; tx <= (x + width) * 10; tx++)
        {
            var wx = CastleGeometry.TileToWorld(tx) + .25f;
            var wz = CastleGeometry.TileToWorld(ty) + .25f;
            bool read = ground.TryGetHeight(wx, -51, wz, out float h);
            samples.Add(new { x = tx, y = ty, height = read ? (float?)h : null });
        }
        var overlaps = new List<object>();
        var em = world.EntityManager;
        foreach (var entity in Query.Entities(em, true, false, ComponentIndex.Of<TileBounds>(ComponentType.AccessMode.ReadOnly)))
        {
            if (!entity.TryRead(em, out TileBounds b)) continue;
            if (b.Value.Max.x <= x * 10 || b.Value.Min.x >= (x + width) * 10 ||
                b.Value.Max.y <= y * 10 || b.Value.Min.y >= (y + depth) * 10) continue;
            string name = "";
            if (entity.TryRead(em, out PrefabGUID guid)) PrefabLookup.TryGetName(world, guid, out name);
            overlaps.Add(new { prefab = name, entity = entity.ToString(),
                minX = b.Value.Min.x, minY = b.Value.Min.y, maxX = b.Value.Max.x, maxY = b.Value.Max.y });
        }
        File.WriteAllText(Path.Combine(output, "area-audit.json"),
            JsonSerializer.Serialize(new { x, y, width, depth, capturedUtc = DateTime.UtcNow,
                terrainSource = ground.TerrainSource, samples, overlaps,
                qualification = "Read-only terrain heights and model bounds; walkability, end-to-end navigation and native placement are not checked." },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
