using System;

namespace Progenitor.Systems;

/// <summary>
/// Nesting-safe thread-static world/delta pair used while a virtual-system key is dispatching.
/// Game-free so the test project can prove save/restore without Unity: a nested
/// <see cref="Push"/> must not wipe the outer pair, or later slots of the outer snapshot skip
/// because the wrapper sees a null world. No allocation on the hot path (struct snapshot,
/// not <see cref="IDisposable"/>).
/// </summary>
internal static class DispatchAmbient
{
    [ThreadStatic]
    private static object? t_world;

    [ThreadStatic]
    private static float t_delta;

    /// <summary>World token of the innermost live <see cref="Push"/>, or null outside any dispatch.</summary>
    internal static object? World => t_world;

    /// <summary>Delta of the innermost live <see cref="Push"/>, or 0 outside any dispatch.</summary>
    internal static float Delta => t_delta;

    /// <summary>
    /// Previous ambient captured by <see cref="Push"/>, handed to <see cref="Restore"/> in a
    /// finally so nested dispatch cannot leave the outer key with a null world.
    /// </summary>
    internal readonly struct Snapshot
    {
        internal readonly object? World;
        internal readonly float Delta;

        internal Snapshot(object? world, float delta)
        {
            World = world;
            Delta = delta;
        }
    }

    /// <summary>
    /// Install <paramref name="world"/> / <paramref name="delta"/> for this dispatch and return
    /// the previous pair. Caller must <see cref="Restore"/> that snapshot in a finally, even when
    /// the inner dispatch throws.
    /// </summary>
    internal static Snapshot Push(object world, float delta)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        var previous = new Snapshot(t_world, t_delta);
        t_world = world;
        t_delta = delta;
        return previous;
    }

    /// <summary>Put back the pair captured by the matching <see cref="Push"/>.</summary>
    internal static void Restore(in Snapshot previous)
    {
        t_world = previous.World;
        t_delta = previous.Delta;
    }
}
