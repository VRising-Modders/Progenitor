using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Items;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
#endif

namespace Progenitor.Sample;

/// <summary>
/// <c>.items catalog [filter]</c>: <c>ctx.Items.Catalog()</c> (<c>docs/api.md</c> Items). Prints the
/// catalog size and build cost, then the first few entries whose prefab name or display name
/// contains the filter (or whose GUID equals it). Commands run on the server, where vanilla items
/// have no display name; the Dev bridge op <c>item_catalog</c> shows the client's names.
/// Read-only. Reply lines are game-free so the test project compiles this file.
/// </summary>
internal static class SampleItemCatalog
{
    internal const string Area = "items";

    /// <summary>Entries printed per call.</summary>
    internal const int MaxRows = 8;

    internal static bool Handles(string? verb) =>
        string.Equals((verb ?? "").Trim(), "catalog", StringComparison.OrdinalIgnoreCase);

    /// <summary>The reply: one summary line, then up to <see cref="MaxRows"/> entries.</summary>
    internal static List<string> Lines(IReadOnlyList<ItemCatalogEntry> entries, ItemCatalogInfo info, string? filter)
    {
        var lines = new List<string>();
        if (entries.Count == 0)
        {
            lines.Add("Item catalog is empty: the world's item prefabs are not loaded yet.");
            return lines;
        }

        var matches = new List<ItemCatalogEntry>();
        var f = (filter ?? "").Trim();
        foreach (var e in entries)
        {
            if (e.Matches(f))
                matches.Add(e);
        }

        lines.Add(string.Format(CultureInfo.InvariantCulture,
            "Item catalog ({0}): {1} items, {2} with display names, {3} custom, built in {4:0.#} ms. {5} match{6}.",
            info.Side, entries.Count, info.WithDisplayName, info.Custom, info.BuildMilliseconds,
            matches.Count, f.Length == 0 ? "" : " \"" + f + "\""));
        for (int i = 0; i < matches.Count && i < MaxRows; i++)
            lines.Add(Row(matches[i]));
        if (matches.Count > MaxRows)
            lines.Add("... " + (matches.Count - MaxRows).ToString(CultureInfo.InvariantCulture) + " more; narrow the filter.");
        return lines;
    }

    /// <summary><c>1332980397 Item_Ingredient_Mineral_IronOre "Iron Ore" stack N type T cat 0xC</c> (the quoted name only where the side has one).</summary>
    internal static string Row(ItemCatalogEntry e) =>
        e.PrefabGuid.ToString(CultureInfo.InvariantCulture) + " " + (e.PrefabName.Length == 0 ? "(no name)" : e.PrefabName)
        + (e.DisplayName == null ? "" : " \"" + e.DisplayName + "\"")
        + " stack " + e.MaxAmount.ToString(CultureInfo.InvariantCulture)
        + " type " + e.ItemType.ToString(CultureInfo.InvariantCulture)
        + " cat 0x" + e.ItemCategory.ToString("X", CultureInfo.InvariantCulture)
        + (e.IsCustom ? " custom" : "");

#if !PROGENITOR_TESTS
    internal static void HandleCommand(ICommandContext ctx, string? filter)
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        var entries = plugin.Items.Catalog();
        var info = plugin.Items.CatalogInfo();
        foreach (var line in Lines(entries, info, filter))
            ctx.Reply(line);
        SampleLog.Info(Area, "catalog side=" + info.Side + " count=" + entries.Count + " named=" + info.WithDisplayName
            + " custom=" + info.Custom + " buildMs=" + info.BuildMilliseconds.ToString("0.#", CultureInfo.InvariantCulture)
            + " builds=" + info.Builds + " filter=" + (filter ?? ""));
    }
#endif
}
