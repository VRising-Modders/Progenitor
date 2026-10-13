using System;
using Progenitor.Plugins;
using Stunlock.Core;

namespace Progenitor.Content;

/// <summary>
/// Invalidation helpers on <see cref="PluginContent"/>. These live in Content so a captured-value
/// change can re-run the ordered pass without a side write on the clone (R20 findings 4 and 7)
/// and without editing <c>PluginContext.cs</c> (another job owns that file).
/// </summary>
public static class PluginContentEdits
{
    /// <summary>
    /// Mark editors of <paramref name="prefabGuid"/> as stale and queue one coalesced
    /// re-run of the ordered pass. Call when captured editor inputs change (a synced
    /// <c>ConfigEntry</c>, a computed stat). CollectionReady will not notice a closure
    /// change on its own.
    /// </summary>
    public static void Invalidate(this PluginContent content, PrefabGUID prefabGuid)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        PrefabRegistry.Invalidate(prefabGuid);
    }

    /// <summary>
    /// <see cref="Invalidate(PluginContent, PrefabGUID)"/> for every prefab this plugin
    /// registered an editor on. Pass <see cref="PluginContext.Owner"/> — <see cref="PluginContent"/>
    /// does not expose the owner (that lives on the context).
    /// </summary>
    public static void InvalidateEdits(this PluginContent content, object owner)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        PrefabRegistry.InvalidateEdits(owner);
    }
}
