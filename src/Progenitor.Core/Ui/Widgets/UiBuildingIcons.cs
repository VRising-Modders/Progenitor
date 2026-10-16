using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// HUB1: castle building icons (<see cref="UiTableRow.BuildingPrefab"/>, <c>ctx.Castles.BuildingIcon</c>), as the game's
/// build menu draws them. The game streams a building's icon in only for its build menu (a blueprint names its icon by
/// asset id, loaded on demand), so the icons are taken from the build menu's own cells: every cell the game has drawn
/// this session (a building and its variants) gives its prefab and the sprite it shows. A building the game has not
/// drawn yet has no icon, and a row keeps an empty, aligned icon place. The cache belongs to one client world.
/// Game-free: the Unity runtime feeds <see cref="Note"/> from the cells and asks <see cref="Get"/>.
/// </summary>
internal static class UiBuildingIcons
{
    private static readonly Dictionary<int, object> Icons = new();
    private static int _lastScanFrame = int.MinValue;

    /// <summary>Frames between two scans of the build menu's cells after a miss.</summary>
    internal const int ScanEveryFrames = 30;

    /// <summary>The icons known now.</summary>
    internal static int Count
    {
        get { lock (Icons) return Icons.Count; }
    }

    /// <summary>A build-menu cell showed <paramref name="sprite"/> for <paramref name="prefab"/> (0 or a null sprite is ignored).</summary>
    internal static void Note(int prefab, object? sprite)
    {
        if (prefab == 0 || sprite == null) return;
        lock (Icons) Icons[prefab] = sprite;
    }

    /// <summary>
    /// The icon known for <paramref name="prefab"/>, or null. A sprite that <paramref name="alive"/> says was unloaded is
    /// forgotten (the next scan may find it again).
    /// </summary>
    internal static object? Get(int prefab, Func<object, bool>? alive = null)
    {
        if (prefab == 0) return null;
        lock (Icons)
        {
            if (!Icons.TryGetValue(prefab, out var sprite)) return null;
            bool ok;
            try { ok = alive == null || alive(sprite); }
            catch { ok = false; }
            if (ok) return sprite;
            Icons.Remove(prefab);
            return null;
        }
    }

    /// <summary>Whether a miss on <paramref name="frame"/> may scan the cells again (at most once per <see cref="ScanEveryFrames"/>).</summary>
    internal static bool MayScan(int frame)
    {
        if (_lastScanFrame != int.MinValue && frame >= _lastScanFrame && frame - _lastScanFrame < ScanEveryFrames) return false;
        _lastScanFrame = frame;
        return true;
    }

    /// <summary>A world change or Core unload: forget every icon.</summary>
    internal static void Clear()
    {
        lock (Icons) Icons.Clear();
        _lastScanFrame = int.MinValue;
    }
}
