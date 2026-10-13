using System;
using System.Reflection;
using System.Runtime.Loader;

namespace Progenitor.Bootstrap;

/// <summary>
/// Collectible Core ALC. Microsoft's unloadability guidance: the runtime strong-handles
/// an <see cref="AssemblyLoadContext"/> subclass during <c>Unload</c>, so every instance
/// field must be nulled first or the generation cannot collect. Resolving is stored here
/// (not as a capturing lambda on the event) so <see cref="PrepareUnload"/> can drop it.
/// </summary>
public sealed class CoreLoadContext : AssemblyLoadContext
{
    private Func<AssemblyLoadContext, AssemblyName, Assembly?>? _resolving;

    public CoreLoadContext(string name)
        : base(name, isCollectible: true)
    {
    }

    /// <summary>True while a Resolving handler is stored on this instance (not after <see cref="PrepareUnload"/>).</summary>
    public bool HasResolvingHandler => _resolving != null;

    /// <summary>Subscribe <paramref name="handler"/> and keep the delegate on this instance.</summary>
    public void AttachResolving(Func<AssemblyLoadContext, AssemblyName, Assembly?> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        _resolving = handler;
        Resolving += OnResolving;
    }

    /// <summary>
    /// Drop event handlers and instance fields. Must run immediately before
    /// <see cref="AssemblyLoadContext.Unload"/>. Idempotent.
    /// </summary>
    public void PrepareUnload()
    {
        Resolving -= OnResolving;
        _resolving = null;
    }

    private Assembly? OnResolving(AssemblyLoadContext context, AssemblyName name)
    {
        var handler = _resolving;
        return handler == null ? null : handler(context, name);
    }
}
