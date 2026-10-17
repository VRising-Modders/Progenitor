using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>Where a castle building's icon came from (<see cref="UiBuildingIcons.Source"/>).</summary>
internal enum BuildingIconSource
{
    /// <summary>No icon is known for the building yet.</summary>
    None,

    /// <summary>The building's own blueprint icon (its icon asset id, read from the sprites the game has loaded).</summary>
    Blueprint,

    /// <summary>A build-menu cell, for a building whose blueprint names no icon asset.</summary>
    BuildMenu,
}

/// <summary>
/// HUB1: castle building icons (<see cref="UiTableRow.BuildingPrefab"/>, <c>ctx.Castles.BuildingIcon</c>), as the game's
/// build menu draws them.
///
/// <para><b>Sources.</b> A blueprint names its icon by asset id; the game streams the sprite in for its build menu and
/// keeps it among the loaded UI assets. The Unity runtime resolves a building first by that id (<see
/// cref="BuildingIconSource.Blueprint"/>): the sprite the build menu itself would draw, found without the build menu
/// being open, but only while the game has it loaded. A building whose blueprint names no icon asset falls back to the
/// build menu's own cells (<see cref="BuildingIconSource.BuildMenu"/>). A cell is never trusted for a blueprint that
/// names an icon asset: a cell whose sprite is still streaming keeps the sprite it showed before (another building's,
/// or its template's), so the cell's building and its sprite disagree until the sprite arrives
/// (<see cref="AcceptsCell"/>). A blueprint icon replaces a cell icon; a cell icon never replaces a blueprint icon.</para>
///
/// <para>Budget: a scan runs only on a lookup that misses (a row or a <c>BuildingIcon</c> call waiting for an icon), at
/// most once per <see cref="ScanInterval"/> frames; a scan that learns nothing doubles the interval (to
/// <see cref="MaxScanEveryFrames"/>), one that learns an icon puts it back to <see cref="ScanEveryFrames"/>.
/// <see cref="Version"/> moves whenever an icon is learnt, replaced or withdrawn, so a table waiting for one draws it
/// then. The cache belongs to one client world.</para>
///
/// <para>Game-free: the Unity runtime feeds <see cref="Note(int, object?, BuildingIconSource)"/> and
/// <see cref="NoteKey"/> and asks <see cref="Get"/>.</para>
/// </summary>
internal static class UiBuildingIcons
{
    private readonly struct Known
    {
        public Known(object sprite, BuildingIconSource source)
        {
            Sprite = sprite;
            Source = source;
        }

        public object Sprite { get; }
        public BuildingIconSource Source { get; }
    }

    private static readonly Dictionary<int, Known> Icons = new();
    private static readonly Dictionary<int, bool> Keys = new();
    private static readonly HashSet<int> Waiting = new();
    private static int _lastScanFrame = int.MinValue;
    private static int _interval = ScanEveryFrames;

    /// <summary>The shortest interval between two scans, in frames (after a scan that learnt an icon).</summary>
    internal const int ScanEveryFrames = 30;

    /// <summary>The longest interval the back-off reaches, in frames (about 30 s at 60 frames a second).</summary>
    internal const int MaxScanEveryFrames = 1800;

    /// <summary>The most buildings a scan resolves for at once (the oldest waiting ones are kept).</summary>
    internal const int MaxWaiting = 256;

    /// <summary>Frames until the next scan may run after the last one.</summary>
    internal static int ScanInterval => _interval;

    /// <summary>Moves each time an icon is learnt, replaced or withdrawn (a waiting row redraws then).</summary>
    internal static int Version { get; private set; }

    /// <summary>The icons known now.</summary>
    internal static int Count
    {
        get { lock (Icons) return Icons.Count; }
    }

    /// <summary>A build-menu cell showed <paramref name="sprite"/> for <paramref name="prefab"/> (0 or a null sprite is ignored).</summary>
    internal static void Note(int prefab, object? sprite) => Note(prefab, sprite, BuildingIconSource.BuildMenu);

    /// <summary>
    /// <paramref name="sprite"/> is <paramref name="prefab"/>'s icon, from <paramref name="source"/> (0, a null sprite or
    /// <see cref="BuildingIconSource.None"/> is ignored). A cell icon does not replace a blueprint icon.
    /// </summary>
    internal static void Note(int prefab, object? sprite, BuildingIconSource source)
    {
        if (prefab == 0 || sprite == null || source == BuildingIconSource.None) return;
        lock (Icons)
        {
            if (Icons.TryGetValue(prefab, out var known))
            {
                if (ReferenceEquals(known.Sprite, sprite) && known.Source == source) return;
                if (known.Source == BuildingIconSource.Blueprint && source == BuildingIconSource.BuildMenu) return;
            }

            Icons[prefab] = new Known(sprite, source);
            Waiting.Remove(prefab);
            Version++;
        }
    }

    /// <summary>
    /// Whether <paramref name="prefab"/>'s blueprint names an icon asset. One that does drops any icon a cell gave it
    /// (that sprite may have been the cell's previous one) and is never taken from a cell again.
    /// </summary>
    internal static void NoteKey(int prefab, bool hasBlueprintKey)
    {
        if (prefab == 0) return;
        lock (Icons)
        {
            Keys[prefab] = hasBlueprintKey;
            if (hasBlueprintKey && Icons.TryGetValue(prefab, out var known) && known.Source == BuildingIconSource.BuildMenu)
            {
                Icons.Remove(prefab);
                Version++;
            }
        }
    }

    /// <summary>Whether <see cref="NoteKey"/> has answered for <paramref name="prefab"/> in this world.</summary>
    internal static bool KeyKnown(int prefab)
    {
        lock (Icons) return Keys.ContainsKey(prefab);
    }

    /// <summary>
    /// Whether a build-menu cell may give <paramref name="prefab"/> its icon: a cell for a building, whose sprite has
    /// arrived (<paramref name="cellLoading"/> false), for a blueprint known to name no icon asset of its own.
    /// </summary>
    internal static bool AcceptsCell(int prefab, bool cellLoading)
    {
        if (prefab == 0 || cellLoading) return false;
        lock (Icons) return Keys.TryGetValue(prefab, out bool hasKey) && !hasKey;
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
            if (!Icons.TryGetValue(prefab, out var known)) return null;
            bool ok;
            try { ok = alive == null || alive(known.Sprite); }
            catch { ok = false; }
            if (ok) return known.Sprite;
            Icons.Remove(prefab);
            return null;
        }
    }

    /// <summary>Where <paramref name="prefab"/>'s known icon came from (<see cref="BuildingIconSource.None"/> without one).</summary>
    internal static BuildingIconSource Source(int prefab)
    {
        lock (Icons) return Icons.TryGetValue(prefab, out var known) ? known.Source : BuildingIconSource.None;
    }

    /// <summary>The readback name of <paramref name="source"/>: <c>blueprint</c>, <c>build-menu</c> or <c>none</c>.</summary>
    internal static string SourceName(BuildingIconSource source) => source switch
    {
        BuildingIconSource.Blueprint => "blueprint",
        BuildingIconSource.BuildMenu => "build-menu",
        _ => "none",
    };

    /// <summary>The readback name of where <paramref name="prefab"/>'s icon came from (<see cref="SourceName(BuildingIconSource)"/>).</summary>
    internal static string SourceName(int prefab) => SourceName(Source(prefab));

    /// <summary>A lookup for <paramref name="prefab"/> missed: the next scan resolves it with the others waiting.</summary>
    internal static void Want(int prefab)
    {
        if (prefab == 0) return;
        lock (Icons)
        {
            if (Icons.ContainsKey(prefab) || Waiting.Count >= MaxWaiting) return;
            Waiting.Add(prefab);
        }
    }

    /// <summary>The buildings waiting for an icon now.</summary>
    internal static int[] WaitingNow()
    {
        lock (Icons)
        {
            var all = new int[Waiting.Count];
            Waiting.CopyTo(all);
            return all;
        }
    }

    /// <summary>Whether a miss on <paramref name="frame"/> may scan now (the back-off's interval has passed).</summary>
    internal static bool MayScan(int frame)
    {
        if (_lastScanFrame != int.MinValue && frame >= _lastScanFrame && frame - _lastScanFrame < _interval) return false;
        _lastScanFrame = frame;
        return true;
    }

    /// <summary>A scan finished: one that learnt an icon resets the interval, one that learnt nothing doubles it.</summary>
    internal static void ScanDone(bool learnt) =>
        _interval = learnt ? ScanEveryFrames : Math.Min(MaxScanEveryFrames, _interval * 2);

    /// <summary>A world change or Core unload: forget every icon, every blueprint key answer and the back-off.</summary>
    internal static void Clear()
    {
        lock (Icons)
        {
            Icons.Clear();
            Keys.Clear();
            Waiting.Clear();
        }

        _lastScanFrame = int.MinValue;
        _interval = ScanEveryFrames;
        Version++;
    }
}
