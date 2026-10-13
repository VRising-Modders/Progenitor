#if !PROGENITOR_TESTS
using System;
using ProjectM.UI;
using UnityEngine;

namespace Progenitor.Ui;

/// <summary>
/// MAPTP1: the game's world map (the full-screen menu's Map tab) for client plugins: is it open, where
/// on the map is the pointer in world X/Z, and show the whole map. Client only, main thread; every member
/// is safe to call anywhere (false / no-op on the server, the title screen or with the map closed).
///
/// <para><b>Pointer to world</b> (<see cref="TryReadPointer"/>) follows the game's own map click
/// (<c>MapMenuMapper.OnMapClicked</c> / <c>TeleportToMapPosition</c>): the pointer's screen position on
/// the map texture's rect (<c>MapMenu.MapTexture</c>, field 0x58) with a null camera, then
/// the inverse of <c>MapMenuMapper._WorldToAnchoredSpace</c> (field 0xD0). Both reads are interop field
/// accessors; the arithmetic is <see cref="MapTransformMath"/>, game-free and tested. The pointer counts as
/// on the map only inside the texture's rect and, when the menu has one, inside its <c>MapNode</c> viewport.
/// <b>Not live-verified.</b></para>
///
/// <para><b>Reveal</b> (<see cref="RevealAll"/>) sets the client's <c>RevealMapPresentationSystem.RevealFullMap</c>
/// (the game's debug "Reveal Map" switch), reference-counted by <see cref="MapRevealLeases"/>, and puts its
/// prior value back when the last lease goes — also with no client world (a disconnect, an unload at the
/// title screen), because the flag is a process-wide static. The player's exploration data is never written.
/// <b>Not live-verified.</b></para>
/// </summary>
public static class WorldMap
{
    private static readonly MapRevealLeases Leases = new();
    private static bool _warnedReveal;

    /// <summary>
    /// Set once this process has written the flag: the class's static storage exists from then on (it is
    /// process-wide and outlives client worlds), so a restore may be written with no client world — a
    /// disconnect, or an unload at the title screen. Before that, a write waits for the system to exist.
    /// </summary>
    private static bool _flagStorageReady;

    /// <summary>The full-screen menu is open on its Map tab.</summary>
    public static bool IsOpen => GameMenuHistory.SafeMapOpen();

    /// <summary>How many reveal leases are held.</summary>
    public static int RevealLeaseCount => Leases.Count;

    /// <summary>The game's reveal flag now, or null when it cannot be read (no client world).</summary>
    public static bool? RevealFlag => ReadFlag();

    /// <summary>
    /// Show the whole map until the returned handle is disposed (track it on the plugin context so unload
    /// disposes it). Several holders may hold leases; the flag's prior value is restored when the last
    /// one goes. Returns an inert handle when there is no client world.
    /// </summary>
    public static IDisposable RevealAll(object? owner = null)
    {
        var holder = new Lease(owner);
        if (ReadFlag() == null)
        {
            // No RevealMapPresentationSystem (server, title screen): its static is not ours to touch yet.
            holder.Inert = true;
            return holder;
        }

        if (Leases.Acquire(holder, ReadFlag)) WriteFlag(true);
        else if (ReadFlag() == false) WriteFlag(true); // re-assert: something switched it off under a lease
        return holder;
    }

    /// <summary>
    /// The pointer over the open map: its screen point, its point in the map texture's local space (the
    /// parent <see cref="HoldRing.ShowAt"/> takes) and, when the map has data, world X/Z. False when the map
    /// is closed or unreadable; <paramref name="pointer"/> then says why.
    /// </summary>
    public static bool TryReadPointer(out MapPointer pointer)
    {
        pointer = MapPointer.Off("map closed");
        if (!IsOpen) return false;
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) { pointer = MapPointer.Off("no client world"); return false; }
            var mapper = world.GetExistingSystemManaged<MapMenuMapper>();
            if (mapper == null || mapper.Pointer == IntPtr.Zero) { pointer = MapPointer.Off("no map system"); return false; }

            var menu = mapper._MapMenu;
            if (!Alive(menu)) menu = mapper.GetMapMenu();
            if (!Alive(menu)) { pointer = MapPointer.Off("no map menu"); return false; }
            var texture = menu!.MapTexture;
            if (!Alive(texture) || !texture.gameObject.activeInHierarchy) { pointer = MapPointer.Off("no map texture"); return false; }
            var rect = texture.rectTransform;
            if (!Alive(rect)) { pointer = MapPointer.Off("no map rect"); return false; }

            if (!TryPointerScreen(out float sx, out float sy)) { pointer = MapPointer.Off("no pointer"); return false; }
            if (!TryLocal(rect, sx, sy, out float lx, out float ly)) { pointer = MapPointer.Off("pointer not on the map plane"); return false; }

            bool inside = rect.rect.Contains(new Vector2(lx, ly));
            var node = menu.MapNode;
            if (inside && Alive(node) && node.gameObject.activeInHierarchy
                && TryLocal(node, sx, sy, out float nx, out float ny))
                inside = node.rect.Contains(new Vector2(nx, ny));

            var m = mapper._WorldToAnchoredSpace;
            var values = new[]
            {
                m.m00, m.m10, m.m20, m.m30,
                m.m01, m.m11, m.m21, m.m31,
                m.m02, m.m12, m.m22, m.m32,
                m.m03, m.m13, m.m23, m.m33,
            };
            bool hasWorld = MapTransformMath.TryAnchoredToWorld(values, lx, ly, out float wx, out float wz);
            pointer = new MapPointer(inside, sx, sy, lx, ly, hasWorld, wx, wz, rect, inside ? "" : "pointer off the map");
            return true;
        }
        catch (Exception e)
        {
            pointer = MapPointer.Off("map read threw: " + e.Message);
            return false;
        }
    }

    /// <summary>Core unload: put the flag back if any lease is still held.</summary>
    internal static void ReleaseAll()
    {
        var restore = Leases.ReleaseAll();
        if (restore.HasValue) WriteFlag(restore.Value);
    }

    private static void Release(Lease lease)
    {
        if (lease.Inert) return;
        var restore = Leases.Release(lease);
        if (restore.HasValue) WriteFlag(restore.Value);
    }

    private static bool? ReadFlag()
    {
        try
        {
            // The static lives in class storage IL2CPP sets up with the class; only read or write it once
            // the system exists in the client world, which is also the only time it means anything.
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return null;
            var system = world.GetExistingSystemManaged<RevealMapPresentationSystem>();
            if (system == null || system.Pointer == IntPtr.Zero) return null;
            return RevealMapPresentationSystem.RevealFullMap;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteFlag(bool value)
    {
        try
        {
            if (!_flagStorageReady && ReadFlag() == null) return;
            RevealMapPresentationSystem.RevealFullMap = value;
            _flagStorageReady = true;
            Plugin.Logger?.LogInfo("WorldMap: reveal full map " + (value ? "on" : "off") + " (leases " + Leases.Count + ").");
        }
        catch (Exception e)
        {
            if (_warnedReveal) return;
            _warnedReveal = true;
            Plugin.Logger?.LogWarning("WorldMap: could not set RevealFullMap: " + e.Message);
        }
    }

    private static bool TryPointerScreen(out float x, out float y) => UnityUiRuntime.TrySamplePointerScreen(out x, out y);

    /// <summary><c>ScreenPointToLocalPointInRectangle(rect, screen, null)</c>, as the game's map click calls it.</summary>
    private static bool TryLocal(RectTransform rect, float sx, float sy, out float lx, out float ly)
    {
        lx = ly = 0f;
        var q = rect.rotation;
        var p = rect.position;
        if (!MapTransformMath.TryScreenRayOnRectPlane(q.x, q.y, q.z, q.w, p.x, p.y, p.z, sx, sy, out float hx, out float hy, out float hz))
            return false;
        var local = rect.InverseTransformPoint(new Vector3(hx, hy, hz));
        lx = local.x;
        ly = local.y;
        return true;
    }

    private static bool Alive(UnityEngine.Object? o) => UnityAlive.Of(o);

    private sealed class Lease : IDisposable
    {
        private bool _disposed;

        internal Lease(object? owner) => Owner = owner;

        internal object? Owner { get; }

        internal bool Inert { get; set; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Release(this);
        }
    }
}

/// <summary>Where the pointer is on the open world map (<see cref="WorldMap.TryReadPointer"/>).</summary>
public readonly struct MapPointer
{
    internal MapPointer(bool onMap, float screenX, float screenY, float localX, float localY, bool hasWorld,
        float worldX, float worldZ, RectTransform? mapRect, string why)
    {
        OnMap = onMap;
        ScreenX = screenX;
        ScreenY = screenY;
        LocalX = localX;
        LocalY = localY;
        HasWorld = hasWorld;
        WorldX = worldX;
        WorldZ = worldZ;
        MapRect = mapRect;
        Why = why ?? "";
    }

    internal static MapPointer Off(string why) => new(false, 0f, 0f, 0f, 0f, false, 0f, 0f, null, why);

    /// <summary>The pointer is over the map picture (inside the texture and its viewport).</summary>
    public bool OnMap { get; }

    /// <summary>Screen position, pixels.</summary>
    public float ScreenX { get; }

    /// <summary>Screen position, pixels.</summary>
    public float ScreenY { get; }

    /// <summary>Position in <see cref="MapRect"/>'s local space (pivot-relative): where to put a ring.</summary>
    public float LocalX { get; }

    /// <summary>Position in <see cref="MapRect"/>'s local space (pivot-relative).</summary>
    public float LocalY { get; }

    /// <summary>The map has data and the point converted to world coordinates.</summary>
    public bool HasWorld { get; }

    /// <summary>World X under the pointer (valid when <see cref="HasWorld"/>).</summary>
    public float WorldX { get; }

    /// <summary>World Z under the pointer (valid when <see cref="HasWorld"/>).</summary>
    public float WorldZ { get; }

    /// <summary>The map texture's rect (the game's object; parent a ring to it, never destroy it).</summary>
    public RectTransform? MapRect { get; }

    /// <summary>Why the pointer is not usable, or empty.</summary>
    public string Why { get; }
}
#endif
