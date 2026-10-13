using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;

namespace Progenitor.Compat;

/// <summary>
/// Which assemblies a plugin <b>owns</b>, for every unload sweep that is keyed on an assembly
/// rather than on an owner object.
///
/// R34 finding 4. Before job FCOMPAT3 a plugin's helper DLL resolved through default probing
/// into the non-collectible default context; the rewriter now preloads the rewritten image into
/// the plugin's own collectible context instead, which is the right place for it — but every
/// assembly-keyed sweep still took <c>plugin.GetType().Assembly</c> and only that. A handler
/// declared in <c>Shared.dll</c> was therefore never swept: <c>EventBus.RemoveDeclaredIn</c>
/// filters on the declaring assembly, and <c>Shared.dll</c> is not the plugin. The delegate then
/// keeps the <c>LoaderAllocator</c> alive, the ALC never collects, <c>CoreLoader</c>'s
/// <c>PluginAlcsAlive</c> refusal blocks Core swaps, and a stale handler fires into unloading
/// code in the meantime.
///
/// The owner id is the plugin's <b>collectible load context</b>: the reloader creates exactly one
/// per plugin file (<c>progenitor-reload:{name}:{ticks}</c>) and the rewriter's dependencies are
/// loaded into it at load time, so "in this plugin's ALC" is precisely "tagged with this plugin".
/// Deriving the set from the context rather than from a side table also catches a dependency the
/// context's <c>Resolving</c> handler loaded, which no registry written at rewrite time would.
///
/// Lives beside the rewriter because the rewriter is what put the dependencies there, and it is
/// game-free for the same reason the rest of this folder is — <c>Progenitor.Tests</c> compiles it
/// and drives it against synthetic assemblies in a real collectible context.
/// </summary>
public static class PluginOwnedAssemblies
{
    /// <summary>
    /// The plugin's own assembly first, then every other assembly in its collectible context.
    /// Never throws and never returns null; an assembly with no context (or a context that is
    /// already collected) yields just the assembly it was given.
    /// </summary>
    public static IReadOnlyList<Assembly> Of(Assembly? pluginAssembly, AssemblyLoadContext? context = null)
    {
        var owned = new List<Assembly>();
        if (pluginAssembly != null) owned.Add(pluginAssembly);
        try
        {
            var alc = context ?? (pluginAssembly == null ? null : AssemblyLoadContext.GetLoadContext(pluginAssembly));
            // The default context is every assembly in the process; sweeping "declared in" over
            // that would drop Core's own registrations. Only a collectible plugin ALC is owned.
            if (alc == null || !alc.IsCollectible) return owned;
            foreach (var assembly in alc.Assemblies)
            {
                if (assembly == null) continue;
                bool seen = false;
                for (int i = 0; i < owned.Count; i++)
                    if (ReferenceEquals(owned[i], assembly)) { seen = true; break; }
                if (!seen) owned.Add(assembly);
            }
        }
        catch
        {
            // A context mid-unload can throw on enumeration. The plugin assembly alone is then
            // the same sweep this code did before, which is never worse than doing nothing.
        }
        return owned;
    }

    /// <summary>
    /// Drop every <c>EventBus</c> subscription whose handler or owner was declared in any
    /// assembly this plugin owns. Returns how many went. This is the sweep R34 finding 4 is
    /// about: <c>RemoveAll(owner)</c> already covers a registration that named an owner, and
    /// this covers the ones that did not — including the ones declared in a rewritten dependency.
    /// </summary>
    public static int RemoveEventHandlers(IReadOnlyList<Assembly>? owned)
    {
        if (owned == null) return 0;
        int removed = 0;
        for (int i = 0; i < owned.Count; i++)
        {
            if (owned[i] == null) continue;
            try { removed += Progenitor.Events.EventBus.RemoveDeclaredIn(owned[i]); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Reload: EventBus.RemoveDeclaredIn: " + e.Message); }
        }
        return removed;
    }
}
