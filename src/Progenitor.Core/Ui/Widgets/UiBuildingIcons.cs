using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>Where a castle building's icon came from (<see cref="UiBuildingIcons.Source"/>).</summary>
internal enum BuildingIconSource
{
    /// <summary>No icon is known for the building.</summary>
    None,

    /// <summary>The building's own blueprint icon (its icon asset id, read from the sprites the game has loaded).</summary>
    Blueprint,
}

/// <summary>
/// HUB1: castle building icons (<see cref="UiTableRow.BuildingPrefab"/>, <c>ctx.Castles.BuildingIcon</c>), as the game's
/// build menu draws them.
///
/// <para><b>Source.</b> A blueprint names its icon by asset id; the game streams the sprite in for its build menu and
/// keeps it among the loaded UI assets. The Unity runtime resolves a building by that id only
/// (<see cref="BuildingIconSource.Blueprint"/>): the sprite the build menu itself would draw, found without the build
/// menu being open, but only while the game has it loaded. The build menu's cells are not read: a cell whose sprite has
/// not arrived keeps the sprite it showed before, so its building and its sprite can disagree. A building whose
/// blueprint names no icon asset has no icon (<see cref="BuildingIconSource.None"/>).</para>
///
/// <para><b>Budget.</b> A lookup that misses puts the building on the waiting list (<see cref="Want"/>); a building
/// nobody has asked for in <see cref="WaitingExpiryFrames"/> frames leaves it. A scan runs only on a miss, at most once
/// per <see cref="ScanInterval"/> frames, and does a bounded amount of work (<see cref="MaxBuildingsPerScan"/> buildings,
/// <see cref="MaxLookupsPerScan"/> lookups, resuming where the last one stopped); a full pass over the loaded assets that
/// learns nothing doubles the interval (to <see cref="MaxScanEveryFrames"/>), one that learns an icon puts it back to
/// <see cref="ScanEveryFrames"/>. <see cref="Version"/> moves whenever an icon is learned or replaced, so a table waiting
/// for one draws it then. The cache belongs to one client world.</para>
///
/// <para>Game-free: the Unity runtime feeds <see cref="Note"/> and asks <see cref="Get"/>.</para>
/// </summary>
internal static class UiBuildingIcons
{
    private static readonly Dictionary<int, object> Icons = new();
    private static readonly Dictionary<int, int> Waiting = new();
    private static int _lastScanFrame = int.MinValue;
    private static int _interval = ScanEveryFrames;

    /// <summary>The shortest interval between two scans, in frames (after a scan that learned an icon).</summary>
    internal const int ScanEveryFrames = 30;

    /// <summary>The longest interval the back-off reaches, in frames (about 30 s at 60 frames a second).</summary>
    internal const int MaxScanEveryFrames = 1800;

    /// <summary>A waiting building nobody asked for in this many frames leaves the list (about 2 min at 60 frames a second).</summary>
    internal const int WaitingExpiryFrames = 7200;

    /// <summary>The most buildings on the waiting list.</summary>
    internal const int MaxWaiting = 256;

    /// <summary>The most waiting buildings one scan looks for (the most recently asked first).</summary>
    internal const int MaxBuildingsPerScan = 16;

    /// <summary>The most game lookups one scan makes; the next scan resumes where it stopped.</summary>
    internal const int MaxLookupsPerScan = 512;

    /// <summary>Frames until the next scan may run after the last one.</summary>
    internal static int ScanInterval => _interval;

    /// <summary>Moves each time an icon is learned or replaced (a waiting row redraws then).</summary>
    internal static int Version { get; private set; }

    /// <summary>Where the next scan resumes in the game's list of loaded asset sections.</summary>
    internal static int ScanCursor { get; set; }

    /// <summary>The icons known now.</summary>
    internal static int Count
    {
        get { lock (Icons) return Icons.Count; }
    }

    /// <summary><paramref name="sprite"/> is <paramref name="prefab"/>'s blueprint icon (0 or a null sprite is ignored).</summary>
    internal static void Note(int prefab, object? sprite)
    {
        if (prefab == 0 || sprite == null) return;
        lock (Icons)
        {
            Waiting.Remove(prefab);
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

    /// <summary>Where <paramref name="prefab"/>'s known icon came from (<see cref="BuildingIconSource.None"/> without one).</summary>
    internal static BuildingIconSource Source(int prefab)
    {
        lock (Icons) return Icons.ContainsKey(prefab) ? BuildingIconSource.Blueprint : BuildingIconSource.None;
    }

    /// <summary>The readback name of <paramref name="source"/>: <c>blueprint</c> or <c>none</c>.</summary>
    internal static string SourceName(BuildingIconSource source) =>
        source == BuildingIconSource.Blueprint ? "blueprint" : "none";

    /// <summary>The readback name of where <paramref name="prefab"/>'s icon came from (<see cref="SourceName(BuildingIconSource)"/>).</summary>
    internal static string SourceName(int prefab) => SourceName(Source(prefab));

    /// <summary>A lookup for <paramref name="prefab"/> missed on <paramref name="frame"/>: a later scan looks for it.</summary>
    internal static void Want(int prefab, int frame)
    {
        if (prefab == 0) return;
        lock (Icons)
        {
            if (Icons.ContainsKey(prefab)) return;
            if (!Waiting.ContainsKey(prefab) && Waiting.Count >= MaxWaiting) Expire(frame);
            if (!Waiting.ContainsKey(prefab) && Waiting.Count >= MaxWaiting) return;
            Waiting[prefab] = frame;
        }
    }

    /// <summary>
    /// The buildings a scan on <paramref name="frame"/> looks for: those asked for in the last
    /// <see cref="WaitingExpiryFrames"/> frames (older ones leave the list), the most recently asked first, at most
    /// <see cref="MaxBuildingsPerScan"/>.
    /// </summary>
    internal static int[] WaitingNow(int frame)
    {
        lock (Icons)
        {
            Expire(frame);
            var all = new List<KeyValuePair<int, int>>(Waiting);
            all.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : a.Key.CompareTo(b.Key));
            int n = Math.Min(all.Count, MaxBuildingsPerScan);
            var result = new int[n];
            for (int i = 0; i < n; i++) result[i] = all[i].Key;
            return result;
        }
    }

    /// <summary>The buildings on the waiting list now (tests and readback).</summary>
    internal static int WaitingCount
    {
        get { lock (Icons) return Waiting.Count; }
    }

    private static void Expire(int frame)
    {
        List<int>? gone = null;
        foreach (var pair in Waiting)
            if (frame < pair.Value || frame - pair.Value > WaitingExpiryFrames) (gone ??= new List<int>()).Add(pair.Key);
        if (gone != null) foreach (int prefab in gone) Waiting.Remove(prefab);
    }

    /// <summary>Whether a miss on <paramref name="frame"/> may scan now (the back-off's interval has passed).</summary>
    internal static bool MayScan(int frame)
    {
        if (_lastScanFrame != int.MinValue && frame >= _lastScanFrame && frame - _lastScanFrame < _interval) return false;
        _lastScanFrame = frame;
        return true;
    }

    /// <summary>
    /// A scan finished: one that learned an icon resets the interval, one that finished a full pass and learned nothing
    /// doubles it, and one that stopped part way (its lookup budget spent) keeps it.
    /// </summary>
    internal static void ScanDone(bool learned, bool fullPass = true)
    {
        if (learned) _interval = ScanEveryFrames;
        else if (fullPass) _interval = Math.Min(MaxScanEveryFrames, _interval * 2);
    }

    /// <summary>A world change or Core unload: forget every icon, the waiting list, the scan cursor and the back-off.</summary>
    internal static void Clear()
    {
        lock (Icons)
        {
            Icons.Clear();
            Waiting.Clear();
        }

        ScanCursor = 0;
        _lastScanFrame = int.MinValue;
        _interval = ScanEveryFrames;
        Version++;
    }
}

/// <summary>
/// HUB1: the building icons a table or checklist has drawn, checked every <see cref="CheckEveryFrames"/> frames so a
/// row whose sprite the game unloaded (or whose icon changed) draws again instead of keeping a dead sprite. Game-free.
/// </summary>
internal sealed class UiBuildingIconWatch
{
    /// <summary>Frames between two checks of the drawn icons.</summary>
    internal const int CheckEveryFrames = 60;

    private readonly int[] _prefab;
    private readonly object?[] _icon;
    private int _frames;

    internal UiBuildingIconWatch(int slots)
    {
        _prefab = new int[slots];
        _icon = new object?[slots];
    }

    /// <summary>Whether a drawn row shows a building's icon now.</summary>
    internal bool Any { get; private set; }

    /// <summary>A render starts: no drawn building icon yet.</summary>
    internal void BeginRender()
    {
        Array.Clear(_prefab, 0, _prefab.Length);
        Array.Clear(_icon, 0, _icon.Length);
        Any = false;
    }

    /// <summary>Slot <paramref name="slot"/> drew <paramref name="icon"/> for building <paramref name="prefab"/>.</summary>
    internal void Drawn(int slot, int prefab, object icon)
    {
        if (slot < 0 || slot >= _prefab.Length || prefab == 0) return;
        _prefab[slot] = prefab;
        _icon[slot] = icon;
        Any = true;
    }

    /// <summary>One frame: whether a check is due now (one every <see cref="CheckEveryFrames"/> frames while <see cref="Any"/>).</summary>
    internal bool Due()
    {
        if (!Any) return false;
        if (++_frames < CheckEveryFrames) return false;
        _frames = 0;
        return true;
    }

    /// <summary>Whether a drawn building icon no longer resolves to the sprite drawn (<paramref name="resolve"/> asks the cache).</summary>
    internal bool Stale(Func<int, object?> resolve)
    {
        for (int i = 0; i < _prefab.Length; i++)
        {
            if (_prefab[i] == 0) continue;
            object? now;
            try { now = resolve(_prefab[i]); }
            catch { now = null; }
            if (!ReferenceEquals(now, _icon[i])) return true;
        }

        return false;
    }
}
