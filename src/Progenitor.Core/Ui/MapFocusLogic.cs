using System;

namespace Progenitor.Ui;

/// <summary>What <see cref="MapFocusLogic.DecideStart"/> does first for a "show this spot on the map" request.</summary>
internal enum MapFocusStart
{
    /// <summary>Nothing is opened; the reason says why.</summary>
    Refuse = 0,

    /// <summary>Raise the game's full-screen menu, then pick its Map tab.</summary>
    Raise = 1,

    /// <summary>The full-screen menu is up on another tab: pick its Map tab.</summary>
    SelectMap = 2,

    /// <summary>The map is already shown: only center it.</summary>
    Center = 3,
}

/// <summary>What the opening phase does on one frame (<see cref="MapFocusLogic.OpenTick"/>).</summary>
internal enum MapFocusOpenStep
{
    /// <summary>Wait for the next frame.</summary>
    Wait = 0,

    /// <summary>Pick the menu's Map tab now (the tab's own click handler).</summary>
    SelectMap = 1,

    /// <summary>The map has been shown for <see cref="MapFocusLogic.StableFrames"/> frames with its data: center it.</summary>
    Center = 2,

    /// <summary>Stop; the reason says why (the map stays as it is).</summary>
    GiveUp = 3,
}

/// <summary>What the centering phase does on one frame (<see cref="MapFocusLogic.CenterTick"/>).</summary>
internal enum MapFocusCenterStep
{
    /// <summary>Wait for the next frame (a pan is pending or settling).</summary>
    Wait = 0,

    /// <summary>Ask the map to pan by the amount <see cref="MapFocusLogic.TryPan"/> gives.</summary>
    Pan = 1,

    /// <summary>Done: the spot is centered, or as near as the map's own pan limit allows.</summary>
    Done = 2,

    /// <summary>Stop; the reason says why (the map stays open).</summary>
    GiveUp = 3,
}

/// <summary>One read of the game's menus for <see cref="MapFocusLogic"/>.</summary>
internal readonly struct MapFocusState
{
    internal MapFocusState(bool inWorld, bool probeInstalled, string hudMenu, bool menuOpen, bool mapOpen,
        bool mapTabAvailable, bool workspaceOpen, string view, bool chatFocused)
    {
        InWorld = inWorld;
        ProbeInstalled = probeInstalled;
        HudMenu = hudMenu ?? "";
        MenuOpen = menuOpen;
        MapOpen = mapOpen;
        MapTabAvailable = mapTabAvailable;
        WorkspaceOpen = workspaceOpen;
        View = view ?? "";
        ChatFocused = chatFocused;
    }

    /// <summary>A client world with a HUD (in a game, not the title screen or loading).</summary>
    internal bool InWorld { get; }

    /// <summary>Core's game-menu probe is installed, so the menu and its tab can be read back.</summary>
    internal bool ProbeInstalled { get; }

    /// <summary>The HUD's open menu (<c>OpenHUDMenuSystem.CurrentMenuType</c>); <c>None</c> in play.</summary>
    internal string HudMenu { get; }

    /// <summary>The game's full-screen menu is up (any tab, or the Mods workspace).</summary>
    internal bool MenuOpen { get; }

    /// <summary>The full-screen menu is on its Map tab (<c>WorldMap.IsOpen</c>).</summary>
    internal bool MapOpen { get; }

    /// <summary>The menu's Map tab entry is there and active (it can be clicked).</summary>
    internal bool MapTabAvailable { get; }

    /// <summary>Progenitor's Mods workspace is open.</summary>
    internal bool WorkspaceOpen { get; }

    /// <summary>The game's full-screen view (<c>ViewManager.GetCurrentView()</c>); <c>None</c> in normal play.</summary>
    internal string View { get; }

    /// <summary>The chat input has the keyboard (the player is typing a line).</summary>
    internal bool ChatFocused { get; }

    internal bool ViewBlocks => View.Length > 0 && View != "None";

    internal bool OtherHudMenu => HudMenu.Length > 0 && HudMenu != "None" && !MenuOpen;
}

/// <summary>
/// The game-free rules of opening the world map on a spot (<c>MapFocus</c>): the chat location link's
/// <b>Set marker</b> places the marker, then the map opens centered on it.
///
/// <para><b>When it opens.</b> Like the game's M key: only in a game, with no other game menu or full-screen view
/// up (the key does nothing there), and not over the Mods workspace. It does not open while the player types in chat:
/// the line is the player's, and ending its edit would send it, so with the chat input active the map stays closed. Already on another tab of the game menu, it switches to the Map tab; already on the map, it only
/// centers.</para>
///
/// <para><b>Centering.</b> The map's picture is the map texture's rect; its anchored position is the pan (the game's
/// per-zone offset). The game's own center-on-player key sets that offset to minus the player's point in the
/// rect (<c>_WorldToAnchoredSpace</c> applied to world X/Z), so a spot is centered at offset <c>-p</c>. The game moves
/// the offset only through its pan input (<c>_PanAdjust</c>, added once and cleared each map update, then clamped
/// to the map's edges), so the pan asked for is <c>-p</c> minus the offset now. A spot outside this map (another
/// zone's map) is not panned to.</para>
/// </summary>
internal static class MapFocusLogic
{
    /// <summary>Frames the map must read as shown, with its data, before it is centered (the menu settles its tab).</summary>
    internal const int StableFrames = 4;

    /// <summary>Most frames the map gets to open before the request is dropped.</summary>
    internal const int OpenFrames = 180;

    /// <summary>Frames after a tab pick before it is picked again when the menu is not on the map yet.</summary>
    internal const int SelectRetryFrames = 10;

    /// <summary>Most Map tab picks one request makes.</summary>
    internal const int MaxSelects = 3;

    /// <summary>Frames a pan gets to show in the map's offset before it is read back.</summary>
    internal const int PanSettleFrames = 3;

    /// <summary>Most pans one request asks for (a pan the game drops, such as a controller's stick update, is asked again).</summary>
    internal const int MaxPans = 3;

    /// <summary>Most frames the map gets to have its data once shown.</summary>
    internal const int ReadyFrames = 120;

    /// <summary>How close (map units) the offset must come to the target to count as centered or as moved.</summary>
    internal const float Tolerance = 1f;

    internal const string WhyNoWorld = "no game world (title screen or loading)";
    internal const string WhyNoProbe = "the game menu cannot be read";
    internal const string WhyWorkspace = "the Mods workspace is open";
    internal const string WhyChat = "the chat input is active";
    internal const string WhyMapClosed = "the map was closed";
    internal const string WhyNotOpened = "the map did not open";
    internal const string WhyNoData = "the map has no data";
    internal const string WhyOffMap = "the spot is not on this map";
    internal const string WhyNoPan = "the map did not move";

    internal static string WhyView(string view) => "the game shows its '" + view + "' view";

    internal static string WhyOtherMenu(string menu) => "the game's '" + menu + "' menu is open";

    /// <summary>The first step for a request, from one read of the game's menus.</summary>
    internal static (MapFocusStart Step, string Why) DecideStart(MapFocusState now)
    {
        if (!now.InWorld) return (MapFocusStart.Refuse, WhyNoWorld);
        if (!now.ProbeInstalled) return (MapFocusStart.Refuse, WhyNoProbe);
        if (now.MapOpen) return (MapFocusStart.Center, "");
        if (now.WorkspaceOpen) return (MapFocusStart.Refuse, WhyWorkspace);
        if (now.ChatFocused) return (MapFocusStart.Refuse, WhyChat);
        if (now.ViewBlocks) return (MapFocusStart.Refuse, WhyView(now.View));
        if (now.MenuOpen) return (MapFocusStart.SelectMap, "");
        if (now.OtherHudMenu) return (MapFocusStart.Refuse, WhyOtherMenu(now.HudMenu));
        return (MapFocusStart.Raise, "");
    }

    /// <summary>
    /// One frame of the opening phase. <paramref name="frames"/> counts frames since the request,
    /// <paramref name="selects"/> the tab picks so far, <paramref name="sinceSelect"/> frames since the last pick,
    /// <paramref name="stable"/> consecutive frames the map has read as shown with data (the caller keeps it; see
    /// <see cref="NextStable"/>), <paramref name="sawMenu"/> whether the menu has been seen up since the request.
    /// </summary>
    internal static (MapFocusOpenStep Step, string Why) OpenTick(MapFocusState now, bool mapReady, int frames, int selects,
        int sinceSelect, int stable, bool sawMenu)
    {
        if (!now.InWorld) return (MapFocusOpenStep.GiveUp, WhyNoWorld);
        if (now.MapOpen && mapReady && stable >= StableFrames) return (MapFocusOpenStep.Center, "");
        if (sawMenu && !now.MenuOpen) return (MapFocusOpenStep.GiveUp, WhyMapClosed);
        if (now.WorkspaceOpen) return (MapFocusOpenStep.GiveUp, WhyWorkspace);
        if (frames >= OpenFrames) return (MapFocusOpenStep.GiveUp, now.MapOpen ? WhyNoData : WhyNotOpened);
        if (ShouldSelect(now, selects, sinceSelect)) return (MapFocusOpenStep.SelectMap, "");
        return (MapFocusOpenStep.Wait, "");
    }

    /// <summary>The stable-frame count after this frame's read.</summary>
    internal static int NextStable(int stable, bool mapOpen, bool mapReady) => mapOpen && mapReady ? stable + 1 : 0;

    /// <summary>
    /// Pick the Map tab on this frame: the menu is up with the tab there, not on the map yet, fewer than
    /// <see cref="MaxSelects"/> picks were made and the last one has had <see cref="SelectRetryFrames"/> frames.
    /// </summary>
    internal static bool ShouldSelect(MapFocusState now, int selects, int sinceSelect)
    {
        if (!now.MenuOpen || now.MapOpen || !now.MapTabAvailable || now.WorkspaceOpen) return false;
        if (selects >= MaxSelects) return false;
        return selects == 0 || sinceSelect >= SelectRetryFrames;
    }

    /// <summary>
    /// The pan that centers world (<paramref name="worldX"/>, <paramref name="worldZ"/>): the target offset (minus the
    /// spot's point in the map texture's rect) less the offset now. <paramref name="worldToAnchored"/> is the map's
    /// <c>_WorldToAnchoredSpace</c> (column-major); the rect is the map texture's (local, pivot-relative, as Unity's
    /// <c>Rect</c>). False when the matrix gives no point or the spot is outside the rect (<paramref name="onMap"/>
    /// tells the two apart).
    /// </summary>
    internal static bool TryPan(float[] worldToAnchored, float worldX, float worldZ,
        float rectX, float rectY, float rectWidth, float rectHeight, float offsetX, float offsetY,
        out float panX, out float panY, out float targetX, out float targetY, out bool onMap)
    {
        panX = panY = targetX = targetY = 0f;
        onMap = false;
        if (worldToAnchored == null || worldToAnchored.Length != 16) return false;
        if (!float.IsFinite(worldX) || !float.IsFinite(worldZ) || !float.IsFinite(offsetX) || !float.IsFinite(offsetY)) return false;
        if (!MapTransformMath.TryWorldToAnchored(worldToAnchored, worldX, worldZ, out float px, out float py)) return false;
        if (!float.IsFinite(px) || !float.IsFinite(py)) return false;
        onMap = rectWidth > 0f && rectHeight > 0f
            && px >= rectX && px <= rectX + rectWidth && py >= rectY && py <= rectY + rectHeight;
        if (!onMap) return false;
        targetX = -px;
        targetY = -py;
        panX = targetX - offsetX;
        panY = targetY - offsetY;
        return true;
    }

    /// <summary>
    /// One frame of the centering phase. <paramref name="pans"/> counts pans asked for; <paramref name="sincePan"/>
    /// frames since the last; <paramref name="before"/> is the offset when it was asked, <paramref name="offset"/> the
    /// offset now and <paramref name="target"/> the centered offset. A pan that landed is done; one that moved the map
    /// but stopped short hit the game's edge clamp and is done too; one that changed nothing is asked again.
    /// </summary>
    internal static (MapFocusCenterStep Step, string Why) CenterTick(bool mapOpen, bool mapReady, bool canPan, bool onMap,
        int frames, int pans, int sincePan, (float X, float Y) before, (float X, float Y) offset, (float X, float Y) target)
    {
        if (!mapOpen) return (MapFocusCenterStep.GiveUp, WhyMapClosed);
        if (pans == 0)
        {
            if (mapReady && canPan) return (MapFocusCenterStep.Pan, "");
            if (mapReady && !onMap) return (MapFocusCenterStep.GiveUp, WhyOffMap);
            return frames >= ReadyFrames ? (MapFocusCenterStep.GiveUp, WhyNoData) : (MapFocusCenterStep.Wait, "");
        }

        if (sincePan < PanSettleFrames) return (MapFocusCenterStep.Wait, "");
        if (Near(offset, target)) return (MapFocusCenterStep.Done, "centered");
        if (!Near(offset, before)) return (MapFocusCenterStep.Done, "at the map's edge");
        if (pans >= MaxPans) return (MapFocusCenterStep.GiveUp, WhyNoPan);
        return canPan ? (MapFocusCenterStep.Pan, "") : (MapFocusCenterStep.GiveUp, onMap ? WhyNoData : WhyOffMap);
    }

    internal static bool Near((float X, float Y) a, (float X, float Y) b) =>
        Math.Abs(a.X - b.X) <= Tolerance && Math.Abs(a.Y - b.Y) <= Tolerance;
}
