#if !PROGENITOR_TESTS
using System;
using Progenitor.Ui;

namespace Progenitor.Plugins;

/// <summary>HUB1: castle building icons and display names. Client only, main thread.</summary>
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

    /// <summary>
    /// The name the game shows for the castle building <paramref name="prefabGuid"/>, in the player's language: the
    /// build menu's and the station window's title (<c>TM_SalvageStation_Table</c> is "The Devourer"). Read from the
    /// building's blueprint data (<c>ManagedBlueprintData.Name</c>) and the game's string table. Kept per prefab for the
    /// client world (a world change forgets them). Null when the building has no such name, on prefab 0, while the
    /// game's data is not up yet (not kept: ask again), and on the server, which has no string table. Client, main thread.
    /// </summary>
    public string? BuildingDisplayName(int prefabGuid)
    {
        if (prefabGuid == 0 || Plugin.IsServerProcess) return null;
        try
        {
            var world = Progenitor.Worlds.Client;
            if (world == null || !world.IsCreated) return null;
            return Progenitor.Castles.BuildingDisplayNames.Get(prefabGuid, world, prefab => LookUpBuildingName(world, prefab));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ctx.Castles.BuildingDisplayName: " + e.Message);
            return null;
        }
    }

    private static (Progenitor.Castles.BuildingNameLookup Result, string? Text) LookUpBuildingName(Unity.Entities.World world, int prefab)
    {
        var registry = world.GetExistingSystemManaged<ProjectM.GameDataSystem>()?.ManagedDataRegistry;
        if (registry == null) return (Progenitor.Castles.BuildingNameLookup.NotReady, null);
        if (!registry.TryGet<ProjectM.ManagedBlueprintData>(new Stunlock.Core.PrefabGUID(prefab), out var data) || data == null)
            return (Progenitor.Castles.BuildingNameLookup.NotReady, null);
        var key = data.Name;
        if (Progenitor.Localization.Localization.TryGetGameString(key, out var text) && !string.IsNullOrWhiteSpace(text))
            return (Progenitor.Castles.BuildingNameLookup.Found, text);
        return (Progenitor.Castles.BuildingNameLookup.NoName, null);
    }
}
#endif
