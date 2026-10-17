#if !PROGENITOR_TESTS
using System;
using Progenitor.Ui;

namespace Progenitor.Plugins;

/// <summary>HUB1: castle building icons. Client only, main thread.</summary>
public sealed partial class PluginCastles
{
    /// <summary>
    /// The icon the game's build menu draws for the castle building <paramref name="prefabGuid"/> (a chest such as
    /// <c>TM_Stash_Chest_Wood_General_Big</c>, a station such as <c>TM_CraftingStation_Smithy</c>, a prison cell), a
    /// <c>UnityEngine.Sprite</c> to give a row's or a picker's <c>Icon</c>; null when there is none yet. It is the
    /// building's own blueprint icon, found among the sprites the game has loaded (the build menu need not be open); the
    /// game streams a blueprint's icon in for its build menu, so a building whose icon the game has not loaded has none
    /// yet; ask again later. A table row can name the building instead and draw the icon when it is known:
    /// <see cref="UiTableRow.BuildingPrefab"/>. Null on the server. <see cref="BuildingIconSource"/> says where it came from.
    /// </summary>
    public object? BuildingIcon(int prefabGuid)
    {
        if (prefabGuid == 0) return null;
        try { return WidgetRuntime.Current?.BuildingIcon(prefabGuid); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ctx.Castles.BuildingIcon: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// Where the icon <see cref="BuildingIcon"/> returns for <paramref name="prefabGuid"/> came from: <c>"blueprint"</c>
    /// (the building's own blueprint icon) or <c>"none"</c> (no icon yet, or a blueprint that names no icon asset).
    /// Reads the cache only: call <see cref="BuildingIcon"/> first.
    /// </summary>
    public string BuildingIconSource(int prefabGuid)
    {
        try { return UiBuildingIcons.SourceName(prefabGuid); }
        catch { return "none"; }
    }
}
#endif
