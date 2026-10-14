#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Plugins;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Items;

/// <summary>
/// <see cref="PluginContext.Items"/>. <see cref="Grant"/> and <see cref="Equip"/> are server only
/// (the client throws); the item catalog (<see cref="Catalog"/>) works on both sides.
/// </summary>
public sealed class PluginItems
{
    private readonly PluginContext _ctx;

    internal PluginItems(PluginContext ctx) =>
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    /// <summary>
    /// Add <paramref name="prefabGuid"/> to <paramref name="character"/>'s inventory.
    /// <paramref name="options"/> null stacks, accepts a partial add, does not equip, and
    /// leaves durability alone.
    /// </summary>
    public ItemGrantResult Grant(Entity character, int prefabGuid, int amount = 1, ItemGrantOptions? options = null)
    {
        _ctx.EnsureAttached();
        var world = _ctx.World ?? throw new InvalidOperationException("no world");
        return Items.Grant(world, character, new PrefabGUID(prefabGuid), amount, options);
    }

    /// <summary>Wear the stack in bag <paramref name="slot"/>. Does not create an item.</summary>
    public ItemGrantResult Equip(Entity character, int slot)
    {
        _ctx.EnsureAttached();
        var world = _ctx.World ?? throw new InvalidOperationException("no world");
        return Items.Equip(world, character, slot);
    }

    /// <summary>
    /// Every item prefab of the current world (vanilla and custom), sorted by prefab name. Works on
    /// both sides: the server reads the server world, the client its client world. Built on the
    /// first call of a world load and rebuilt when the world-load epoch changes, a custom item is
    /// applied or removed, or an item's <c>ItemData</c> is edited through Progenitor; later calls
    /// return the same list. Empty when there is no world yet. An empty build, or a client build
    /// made before the local character exists, is served but not cached and is retried at most once
    /// per frame. <see cref="ItemCatalogEntry.DisplayName"/> is the localized name on the client and
    /// null for vanilla items on the server. Main thread (throws otherwise).
    /// </summary>
    public IReadOnlyList<ItemCatalogEntry> Catalog()
    {
        RequireCatalogThread(nameof(Catalog));
        return UnityItemCatalogSource.Cache.Entries();
    }

    /// <summary>The catalog entry for <paramref name="prefabGuid"/>, or null when it is not an item in the current world. Builds the catalog when needed.</summary>
    public ItemCatalogEntry? CatalogEntry(int prefabGuid)
    {
        RequireCatalogThread(nameof(CatalogEntry));
        return UnityItemCatalogSource.Cache.Find(prefabGuid);
    }

    /// <summary>Count, build time, side and name coverage of the last build. Does not build.</summary>
    public ItemCatalogInfo CatalogInfo()
    {
        _ctx.EnsureAttached();
        return UnityItemCatalogSource.Cache.Info;
    }

    /// <summary>
    /// Drop the cached catalog so the next call rebuilds. Rarely needed: world loads, custom items
    /// and every <c>ItemData</c> edit made through Progenitor (plugin edits, the server's edit layer,
    /// keep rules) rebuild on their own. Use it after an item write Progenitor did not make.
    /// </summary>
    public void InvalidateCatalog()
    {
        RequireCatalogThread(nameof(InvalidateCatalog));
        UnityItemCatalogSource.Cache.Invalidate();
    }

    private void RequireCatalogThread(string op)
    {
        _ctx.EnsureAttached();
        if (!Dev.MainThread.IsMainThread)
            throw new InvalidOperationException("ctx.Items." + op + " must run on the game main thread.");
    }
}
#endif
