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
    /// <c>UnityEngine.Sprite</c> to give a row's or a picker's <c>Icon</c>; null when there is none yet. The game streams a
    /// building's icon in for its build menu only, so a building the player has not seen in the build menu this session
    /// (since the world was joined) has no icon; ask again later. A table row can name the building instead and draw the
    /// icon when it is known: <see cref="UiTableRow.BuildingPrefab"/>. Null on the server.
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
}
#endif
