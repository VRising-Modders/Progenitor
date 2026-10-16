using System;
using System.Collections.Generic;
using System.Runtime.Loader;

namespace Progenitor.Bootstrap;

/// <summary>
/// Unload removes a context from AssemblyLoadContext.All before its allocator is collected.
/// Keep resurrection-tracking weak references so dependent plugins finish retiring before Core.
/// </summary>
public sealed class RetiringContexts
{
    private readonly object _gate = new();
    private readonly List<(string Name, WeakReference Reference)> _entries = new();

    public void Track(AssemblyLoadContext context)
    {
        if (!context.IsCollectible) return;
        lock (_gate)
            _entries.Add((context.Name ?? "unnamed", new WeakReference(context, trackResurrection: true)));
    }

    public List<(string Name, WeakReference Reference)> Alive()
    {
        lock (_gate)
        {
            _entries.RemoveAll(entry => !entry.Reference.IsAlive);
            return new List<(string Name, WeakReference Reference)>(_entries);
        }
    }
}
