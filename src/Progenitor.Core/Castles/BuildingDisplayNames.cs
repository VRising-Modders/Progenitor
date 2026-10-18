using System;
using System.Collections.Generic;

namespace Progenitor.Castles;

/// <summary>What one lookup of a building's display name found.</summary>
internal enum BuildingNameLookup
{
    /// <summary>The game's data is not up yet, or the prefab has no blueprint data now: ask again later (not cached).</summary>
    NotReady = 0,

    /// <summary>The localized name the game shows.</summary>
    Found = 1,

    /// <summary>The blueprint names no text, or the game's string table has none for its key: no name (cached).</summary>
    NoName = 2,
}

/// <summary>
/// The cache behind <c>ctx.Castles.BuildingDisplayName</c>: localized building names by prefab id, for one client
/// world. A world change empties it; an answer that the game's data was not up yet is not kept, so the next call asks
/// again. Game-free; the Unity side supplies the lookup. Main thread.
/// </summary>
internal static class BuildingDisplayNames
{
    private static readonly Dictionary<int, string?> Known = new();
    private static object? _world;

    /// <summary>How many prefabs have a kept answer (a name or a known absence).</summary>
    internal static int Count => Known.Count;

    /// <summary>
    /// The display name of <paramref name="prefab"/> in <paramref name="world"/>, from the cache or from one call to
    /// <paramref name="lookup"/>. Null for prefab 0, no world, no name, or data not up yet.
    /// </summary>
    internal static string? Get(int prefab, object? world, Func<int, (BuildingNameLookup Result, string? Text)> lookup)
    {
        if (prefab == 0 || world == null) return null;
        if (!ReferenceEquals(world, _world))
        {
            Known.Clear();
            _world = world;
        }

        if (Known.TryGetValue(prefab, out var kept)) return kept;
        (BuildingNameLookup Result, string? Text) answer;
        try { answer = lookup(prefab); }
        catch { return null; }

        switch (answer.Result)
        {
            case BuildingNameLookup.Found when !string.IsNullOrWhiteSpace(answer.Text):
                string text = answer.Text!.Trim();
                Known[prefab] = text;
                return text;
            case BuildingNameLookup.Found:
            case BuildingNameLookup.NoName:
                Known[prefab] = null;
                return null;
            default:
                return null;
        }
    }

    /// <summary>A Core unload: forget every name and the world.</summary>
    internal static void Clear()
    {
        Known.Clear();
        _world = null;
    }
}
