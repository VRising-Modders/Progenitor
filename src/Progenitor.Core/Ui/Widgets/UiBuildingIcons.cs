using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// HUB1: castle building icons (<see cref="UiTableRow.BuildingPrefab"/>, <c>ctx.Castles.BuildingIcon</c>), as the game's
/// build menu draws them. The game names a blueprint's icon by asset id and streams it in only for its build menu, so the
/// icons are taken from the build menu's own cells: every cell the game has drawn this session (a building and its
/// variants) gives its prefab and the sprite it shows. A building the game has not drawn yet has no icon, and a row keeps
/// an empty, aligned icon place until it has. The cache belongs to one client world.
///
/// <para>Budget: a scan of the cells runs only on a lookup that misses (a row or a <c>BuildingIcon</c> call waiting
/// for an icon), at most once per <see cref="ScanInterval"/> frames; a scan that learns nothing doubles the interval (to
/// <see cref="MaxScanEveryFrames"/>), one that learns an icon puts it back to <see cref="ScanEveryFrames"/>.
/// <see cref="Version"/> moves whenever an icon is learnt, so a table waiting for one draws it then.</para>
///
/// <para>Game-free: the Unity runtime feeds <see cref="Note"/> from the cells and asks <see cref="Get"/>.</para>
/// </summary>
internal static class UiBuildingIcons
{
    private static readonly Dictionary<int, object> Icons = new();
    private static int _lastScanFrame = int.MinValue;
    private static int _interval = ScanEveryFrames;

    /// <summary>The shortest interval between two scans, in frames (after a scan that learnt an icon).</summary>
    internal const int ScanEveryFrames = 30;

    /// <summary>The longest interval the back-off reaches, in frames (about 30 s at 60 frames a second).</summary>
    internal const int MaxScanEveryFrames = 1800;

    /// <summary>Frames until the next scan may run after the last one.</summary>
    internal static int ScanInterval => _interval;

    /// <summary>Moves each time an icon is learnt or replaced (a waiting row redraws then).</summary>
    internal static int Version { get; private set; }

    /// <summary>The icons known now.</summary>
    internal static int Count
    {
        get { lock (Icons) return Icons.Count; }
    }

    /// <summary>A build-menu cell showed <paramref name="sprite"/> for <paramref name="prefab"/> (0 or a null sprite is ignored).</summary>
    internal static void Note(int prefab, object? sprite)
    {
        if (prefab == 0 || sprite == null) return;
        lock (Icons)
        {
            if (Icons.TryGetValue(prefab, out var known) && ReferenceEquals(known, sprite)) return;
            Icons[prefab] = sprite;
            Version++;
        }
    }

    /// <summary>
    /// The icon known for <paramref name="prefab"/>, or null. A sprite that <paramref name="alive"/> says was unloaded is
    /// dropped (the next scan may find it again).
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

    /// <summary>Whether a miss on <paramref name="frame"/> may scan the cells now (the back-off's interval has passed).</summary>
    internal static bool MayScan(int frame)
    {
        if (_lastScanFrame != int.MinValue && frame >= _lastScanFrame && frame - _lastScanFrame < _interval) return false;
        _lastScanFrame = frame;
        return true;
    }

    /// <summary>A scan finished: one that learnt an icon resets the interval, one that learnt nothing doubles it.</summary>
    internal static void ScanDone(bool learnt) =>
        _interval = learnt ? ScanEveryFrames : Math.Min(MaxScanEveryFrames, _interval * 2);

    /// <summary>A world change or Core unload: forget every icon and the back-off.</summary>
    internal static void Clear()
    {
        lock (Icons) Icons.Clear();
        _lastScanFrame = int.MinValue;
        _interval = ScanEveryFrames;
        Version++;
    }
}
