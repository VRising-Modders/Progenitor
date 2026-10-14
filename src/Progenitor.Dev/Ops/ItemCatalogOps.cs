using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Items;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>item_catalog</c>: the <c>ctx.Items.Catalog()</c> of the bridge's side (server
/// on 27342, client on 27341). Args: <c>filter</c> (substring of the prefab or display name, or a
/// GUID), <c>limit</c> (rows returned, default 20, at most 500), <c>rebuild</c> (drop the cache first
/// so <c>buildMs</c> times a fresh build). Read-only. Main thread.
/// </summary>
internal static class ItemCatalogOps
{
    internal static object Run(JsonElement args)
    {
        string? filter = null;
        int limit = 20;
        bool rebuild = false;
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("filter", out var f) && f.ValueKind == JsonValueKind.String) filter = f.GetString();
            if (args.TryGetProperty("filter", out f) && f.ValueKind == JsonValueKind.Number) filter = f.GetRawText();
            if (args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var n)) limit = n;
            if (args.TryGetProperty("rebuild", out var r) && (r.ValueKind == JsonValueKind.True || r.ValueKind == JsonValueKind.False)) rebuild = r.GetBoolean();
        }
        limit = Math.Clamp(limit, 0, 500);

        if (rebuild) ItemCatalogBridge.Invalidate();
        var entries = ItemCatalogBridge.Entries();
        var info = ItemCatalogBridge.Info();
        var matches = ItemCatalogBridge.Filter(entries, filter);
        var rows = new List<object>(Math.Min(limit, matches.Count));
        for (int i = 0; i < matches.Count && i < limit; i++)
        {
            var e = matches[i];
            rows.Add(new
            {
                guid = e.PrefabGuid,
                prefab = e.PrefabName,
                display = e.DisplayName,
                maxAmount = e.MaxAmount,
                category = e.ItemCategory,
                type = e.ItemType,
                custom = e.IsCustom,
            });
        }

        return new
        {
            side = info.Side,
            built = info.Built,
            count = entries.Count,
            withDisplayName = info.WithDisplayName,
            custom = info.Custom,
            buildMs = Math.Round(info.BuildMilliseconds, 2),
            epoch = info.Epoch,
            builds = info.Builds,
            filter,
            matches = matches.Count,
            entries = rows,
        };
    }
}
