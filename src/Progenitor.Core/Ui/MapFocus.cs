#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Hooks;
using ProjectM.UI;
using UnityEngine;

namespace Progenitor.Ui;

/// <summary>
/// Open the game's world map centered on a world spot (client, Core): what a chat location link's <b>Set marker</b>
/// does after placing the marker. Rules: <see cref="MapFocusLogic"/>. One request at a time; a new one replaces it.
///
/// <para><b>Opening.</b> The calls the Mods route and the dev bridge's <c>mods_ui menu op=open tab=map</c> already
/// make: <c>HUDMenuManager.GoToMenu(World, FullscreenMenu)</c> through Core's game-menu probe, then the Map tab's own
/// click handler (<c>FullscreenMenu_OuterTabEntry.OnPointerClick</c>, <see cref="FullscreenMenuProbe.SelectTab"/>),
/// retried a few times while the menu settles. No generic call, no new native signature.</para>
///
/// <para><b>Centering.</b> The map's pan input field <c>MapMenuMapper._PanAdjust</c> (a plain <c>Vector2</c> field,
/// written through its interop field accessor) is set just before the mapper's update
/// (<c>SystemHooks.Before&lt;MapMenuMapper&gt;</c>), so the mapper's own <c>AdjustPan</c> adds it to the zone's offset,
/// clamps it to the map's edges and clears it, as it does for a mouse drag. The amount comes from the map's
/// <c>_WorldToAnchoredSpace</c> (the same read as <see cref="WorldMap.TryReadPointerAt"/>) and the map texture
/// rect's <c>anchoredPosition</c>; the result is read back from that position. Zoom is left as the player had it.
/// <b>Not live-verified.</b></para>
/// </summary>
internal static class MapFocus
{
    private enum Phase
    {
        Idle,
        Opening,
        Centering,
    }

    private static Phase _phase;
    private static float _x, _z;
    private static int _frames, _selects, _sinceSelect, _stable, _pans, _sincePan;
    private static bool _sawMenu, _hooked, _panPending;

    /// <summary>A pan was written to the map's pan input and its result has not been confirmed yet.</summary>
    private static bool _panWritten;
    private static (float X, float Y) _before, _target;
    private static IDisposable? _mapperHook;
    private static string _last = "";

    /// <summary>
    /// Open the map on world (<paramref name="x"/>, <paramref name="z"/>). True when a request started (the map opens and
    /// centers over the next frames); false with <paramref name="why"/> when the game's state does not allow it.
    /// Client only, main thread.
    /// </summary>
    internal static bool OpenAt(float x, float z, out string why)
    {
        why = "";
        if (Plugin.IsServerProcess) { why = "client only"; return false; }
        if (!float.IsFinite(x) || !float.IsFinite(z)) { why = "the spot is not a number"; return false; }
        Stop(null);
        var now = Read();
        var (step, refusal) = MapFocusLogic.DecideStart(now);
        if (step == MapFocusStart.Refuse)
        {
            why = refusal;
            Finish("map not opened (" + why + ")");
            return false;
        }

        _x = x;
        _z = z;
        _frames = _selects = _sinceSelect = _stable = _pans = _sincePan = 0;
        _sawMenu = now.MenuOpen;
        _panPending = false;
        switch (step)
        {
            case MapFocusStart.Raise:
                if (!GameMenuHistory.Probe.OpenMenu())
                {
                    why = "the game menu could not be raised";
                    Finish("map not opened (" + why + ")");
                    return false;
                }

                _phase = Phase.Opening;
                break;
            case MapFocusStart.SelectMap:
                _phase = Phase.Opening;
                break;
            default:
                StartCentering();
                break;
        }

        Hook();
        Plugin.Logger?.LogInfo("Map focus: opening the map on [" + Math.Round(x) + ", " + Math.Round(z) + "] (" + step + ").");
        return true;
    }

    /// <summary>Core unload: drop any request and its hooks.</summary>
    internal static void Uninstall() => Stop(null);

    private static void OnFrame()
    {
        try
        {
            switch (_phase)
            {
                case Phase.Opening: TickOpening(); break;
                case Phase.Centering: TickCentering(); break;
                default: Unhook(); break;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Map focus: " + e.Message);
            Stop("stopped: " + e.Message);
        }
    }

    private static void TickOpening()
    {
        _frames++;
        _sinceSelect++;
        var now = Read();
        if (now.MenuOpen) _sawMenu = true;
        bool ready = now.MapOpen && TryReadMap(out _, out _, out _, out bool hasData) && hasData;
        _stable = MapFocusLogic.NextStable(_stable, now.MapOpen, ready);
        var (step, why) = MapFocusLogic.OpenTick(now, ready, _frames, _selects, _sinceSelect, _stable, _sawMenu);
        switch (step)
        {
            case MapFocusOpenStep.SelectMap:
                _selects++;
                _sinceSelect = 0;
                _stable = 0;
                if (!GameMenuHistory.Probe.SelectTab(GameMenuHistory.MapTab))
                    Plugin.Logger?.LogDiag("Map focus: the Map tab pick did not run.");
                return;
            case MapFocusOpenStep.Center:
                StartCentering();
                return;
            case MapFocusOpenStep.GiveUp:
                Stop("map not opened (" + why + ")");
                return;
        }
    }

    private static void StartCentering()
    {
        _phase = Phase.Centering;
        _frames = 0;
        _pans = 0;
        _sincePan = 0;
        try
        {
            _mapperHook ??= SystemHooks.Before<MapMenuMapper>(BeforeMapper);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Map focus: the map hook could not be installed: " + e.Message);
        }
    }

    private static void TickCentering()
    {
        _frames++;
        _sincePan++;
        bool open = WorldMap.IsOpen;
        bool read = TryReadMap(out var matrix, out var rect, out var offset, out bool hasData) && open;
        bool canPan = false, onMap = false;
        (float X, float Y) target = _target;
        if (read && hasData)
        {
            canPan = MapFocusLogic.TryPan(matrix!, _x, _z, rect.x, rect.y, rect.width, rect.height, offset.x, offset.y,
                out _, out _, out float tx, out float ty, out onMap);
            if (canPan) target = (tx, ty);
        }

        if (!open)
        {
            Stop("map opened, not centered (" + MapFocusLogic.WhyMapClosed + ")");
            return;
        }

        if (_panPending)
        {
            // The map's update has not run since the pan was asked for.
            if (_sincePan >= MapFocusLogic.ReadyFrames) Stop("map opened, not centered (the map did not update)");
            return;
        }

        var (step, why) = MapFocusLogic.CenterTick(open, read && hasData, canPan, onMap, _frames, _pans, _sincePan,
            _before, read ? (offset.x, offset.y) : _before, target);
        switch (step)
        {
            case MapFocusCenterStep.Pan:
                if (_mapperHook == null) { Stop("map opened, not centered (no map hook)"); return; }
                // Counted here, so a pan the hook cannot make still ends after MaxPans; the hook refreshes both points.
                _panPending = true;
                _pans++;
                _sincePan = 0;
                _before = read ? (offset.x, offset.y) : _before;
                _target = target;
                return;
            case MapFocusCenterStep.Done:
                _panWritten = false; // confirmed: the map's update consumed it
                Stop("map opened on [" + Math.Round(_x) + ", " + Math.Round(_z) + "]: " + why);
                return;
            case MapFocusCenterStep.GiveUp:
                Stop("map opened, not centered (" + why + ")");
                return;
        }
    }

    /// <summary>Just before the map's update: put the pending pan in its pan input, where its own update applies it.</summary>
    private static void BeforeMapper(SystemHookContext ctx)
    {
        if (!_panPending) return;
        try
        {
            var world = ctx?.World;
            if (world == null || !world.IsCreated) return;
            _panPending = false;
            if (!TryReadMap(world, out var matrix, out var rect, out var offset, out bool hasData) || !hasData) return;
            if (!MapFocusLogic.TryPan(matrix!, _x, _z, rect.x, rect.y, rect.width, rect.height, offset.x, offset.y,
                    out float px, out float py, out float tx, out float ty, out _))
                return;
            var mapper = world.GetExistingSystemManaged<MapMenuMapper>();
            if (mapper == null || mapper.Pointer == IntPtr.Zero) return;
            mapper._PanAdjust = new Vector2(px, py);
            _panWritten = true;
            _before = (offset.x, offset.y);
            _target = (tx, ty);
            _sincePan = 0;
        }
        catch (Exception e)
        {
            _panPending = false;
            Plugin.Logger?.LogWarning("Map focus: the pan threw: " + e.Message);
        }
    }

    /// <summary>
    /// The open map's world-to-map matrix (column-major), its texture rect (local) and that rect's anchored position
    /// (the pan). <paramref name="hasData"/>: the map shows a zone (the mapper's <c>_LastHasMapData</c>, an invertible
    /// matrix). False when the map cannot be read.
    /// </summary>
    private static bool TryReadMap(out float[]? matrix, out Rect rect, out Vector2 offset, out bool hasData) =>
        TryReadMap(Worlds.Client, out matrix, out rect, out offset, out hasData);

    private static bool TryReadMap(Unity.Entities.World? world, out float[]? matrix, out Rect rect, out Vector2 offset, out bool hasData)
    {
        matrix = null;
        rect = default;
        offset = default;
        hasData = false;
        try
        {
            if (world == null || !world.IsCreated) return false;
            var mapper = world.GetExistingSystemManaged<MapMenuMapper>();
            if (mapper == null || mapper.Pointer == IntPtr.Zero) return false;
            var menu = mapper._MapMenu;
            if (!UnityAlive.Of(menu)) menu = mapper.GetMapMenu();
            if (!UnityAlive.Of(menu)) return false;
            var texture = menu!.MapTexture;
            if (!UnityAlive.Of(texture) || !texture.gameObject.activeInHierarchy) return false;
            var rt = texture.rectTransform;
            if (!UnityAlive.Of(rt)) return false;
            rect = rt.rect;
            offset = rt.anchoredPosition;
            var m = mapper._WorldToAnchoredSpace;
            matrix = new[]
            {
                m.m00, m.m10, m.m20, m.m30,
                m.m01, m.m11, m.m21, m.m31,
                m.m02, m.m12, m.m22, m.m32,
                m.m03, m.m13, m.m23, m.m33,
            };
            bool lastHasData;
            try { lastHasData = mapper._LastHasMapData; }
            catch { lastHasData = true; }
            hasData = lastHasData && MapTransformMath.TryInvert(matrix, out _);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>One read of the game's menus.</summary>
    private static MapFocusState Read()
    {
        bool inWorld = false, menuOpen = false, chatFocused = false, mapTab = false;
        string hudMenu = "", view = "";
        var probe = GameMenuHistory.Probe;
        try
        {
            var world = Worlds.Client;
            var hud = world != null && world.IsCreated ? OpenHUDMenuSystem.Get(world) : null;
            inWorld = hud != null;
            if (hud != null)
            {
                hudMenu = hud.CurrentMenuType.ToString();
                menuOpen = hud.CurrentMenuType == HUDMenuType.FullscreenMenu;
                try
                {
                    var window = world!.GetExistingSystemManaged<ClientChatSystem>()?._ChatWindow;
                    chatFocused = window != null && window.Pointer != IntPtr.Zero && window.IsChatFocused;
                }
                catch { chatFocused = false; }
            }

            try { view = ViewManager.GetCurrentView().ToString(); } catch { view = ""; }
        }
        catch { /* reads as no world */ }

        bool probeMenu = GameMenuHistory.SafeMenuOpen();
        try { mapTab = (menuOpen || probeMenu) && probe.IsTabAvailable(GameMenuHistory.MapTab); } catch { mapTab = false; }
        return new MapFocusState(inWorld, probe is not NullGameMenuProbe, hudMenu, menuOpen || probeMenu, WorldMap.IsOpen,
            mapTab, ModsMenu.IsOpen, view, chatFocused);
    }

    private static void Hook()
    {
        if (_hooked) return;
        Plugin.Frame += OnFrame;
        _hooked = true;
    }

    private static void Unhook()
    {
        if (_hooked)
        {
            Plugin.Frame -= OnFrame;
            _hooked = false;
        }

        try { _mapperHook?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Map focus: unhook threw: " + e.Message); }
        _mapperHook = null;
    }

    private static void Stop(string? result)
    {
        _phase = Phase.Idle;
        _panPending = false;
        ClearWrittenPan();
        Unhook();
        if (result != null) Finish(result);
    }

    /// <summary>
    /// A pan written but not confirmed (the map closed, an unload, a new request) would stay in the map's pan input and
    /// move a later open: put it back to zero when the map system exists.
    /// </summary>
    private static void ClearWrittenPan()
    {
        if (!_panWritten) return;
        _panWritten = false;
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return;
            var mapper = world.GetExistingSystemManaged<MapMenuMapper>();
            if (mapper != null && mapper.Pointer != IntPtr.Zero) mapper._PanAdjust = Vector2.zero;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Map focus: clearing the pan threw: " + e.Message); }
    }

    private static void Finish(string result)
    {
        _last = result;
        Plugin.Logger?.LogInfo("Map focus: " + result + ".");
    }

    /// <summary>The dev bridge's readback (<see cref="MapFocusTestBridge"/>).</summary>
    internal static Dictionary<string, object?> Describe() => new(StringComparer.Ordinal)
    {
        ["phase"] = _phase.ToString(),
        ["spot"] = new[] { _x, _z },
        ["selects"] = _selects,
        ["pans"] = _pans,
        ["target"] = new[] { _target.X, _target.Y },
        ["mapOpen"] = WorldMap.IsOpen,
        ["last"] = _last,
    };
}

/// <summary>
/// The dev bridge's <c>map focus</c> drives the map focus through this by reflection (Core grants Dev no
/// <c>InternalsVisibleTo</c>); it is not a plugin API and no network path reaches it. Main thread.
/// </summary>
internal static class MapFocusTestBridge
{
    /// <summary>The request's phase, picks, pans and last result.</summary>
    public static Dictionary<string, object?> Status() => MapFocus.Describe();

    /// <summary>Open the map on world (<paramref name="x"/>, <paramref name="z"/>), as the chat link's Set marker does after the marker.</summary>
    public static Dictionary<string, object?> Focus(float x, float z)
    {
        bool ok = MapFocus.OpenAt(x, z, out string why);
        var r = MapFocus.Describe();
        r["ok"] = ok;
        if (!ok) r["why"] = why;
        return r;
    }
}
#endif
