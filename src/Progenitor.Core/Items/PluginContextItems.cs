#if !PROGENITOR_TESTS
using System;
using Progenitor.Plugins;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Items;

/// <summary><see cref="PluginContext.Items"/>. Server only. The client throws on use.</summary>
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
}
#endif
