using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>Which pointer event reached a watched game UI object (<see cref="Ui.WatchPointer"/>).</summary>
public enum UiPointerEventKind
{
    /// <summary>A pointer button went down over the object (Unity <c>PointerDown</c>).</summary>
    Down = 1,

    /// <summary>The button that went down over the object came up, wherever the pointer is now (<c>PointerUp</c>).</summary>
    Up = 2,

    /// <summary>The pointer left the object (<c>PointerExit</c>).</summary>
    Exit = 3,
}

/// <summary>
/// One pointer event on a watched object. <see cref="PrimaryHeld"/> is the framework input layer's
/// sample of the primary (left) mouse button at dispatch time: true for a left press on
/// <see cref="UiPointerEventKind.Down"/>, false for a right or middle press. The event carries no
/// position; a handler that needs one reads its own object.
/// </summary>
public readonly struct UiPointerEvent
{
    /// <summary>Make an event (tests and Core only; plugins receive them).</summary>
    public UiPointerEvent(UiPointerEventKind kind, bool primaryHeld)
    {
        Kind = kind;
        PrimaryHeld = primaryHeld;
    }

    /// <summary>Down, Up or Exit.</summary>
    public UiPointerEventKind Kind { get; }

    /// <summary>Whether the primary mouse button was held when the event was dispatched.</summary>
    public bool PrimaryHeld { get; }

    /// <inheritdoc />
    public override string ToString() => Kind + (PrimaryHeld ? " (primary)" : "");
}

/// <summary>
/// Owner-tagged list of live pointer watches. Game-free: Core puts every
/// <see cref="Ui.WatchPointer"/> handle here so <see cref="Ui.RemoveAll"/> and Core Uninstall can
/// dispose watches a plugin forgot. A watch lives on a game-owned object outside the plugin's
/// <c>progenitor-ui-&lt;pluginId&gt;</c> root, so no name sweep can reach it.
/// </summary>
internal static class UiPointerWatchRegistry
{
    private static readonly object Gate = new();
    private static readonly List<(object? Owner, IDisposable Watch)> Live = new();

    /// <summary>How many watches are registered.</summary>
    internal static int Count
    {
        get { lock (Gate) return Live.Count; }
    }

    /// <summary>Register a live watch.</summary>
    internal static void Add(object? owner, IDisposable watch)
    {
        if (watch == null) throw new ArgumentNullException(nameof(watch));
        lock (Gate) Live.Add((owner, watch));
    }

    /// <summary>Forget a watch that disposed itself. Unknown watches are ignored.</summary>
    internal static void Forget(IDisposable watch)
    {
        lock (Gate)
        {
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(Live[i].Watch, watch))
                    Live.RemoveAt(i);
            }
        }
    }

    /// <summary>Dispose every watch registered for <paramref name="owner"/>. Returns how many.</summary>
    internal static int RemoveAll(object owner)
    {
        if (owner == null) return 0;
        var mine = new List<IDisposable>();
        lock (Gate)
        {
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(Live[i].Owner, owner)) continue;
                mine.Add(Live[i].Watch);
                Live.RemoveAt(i);
            }
        }

        return DisposeAll(mine);
    }

    /// <summary>Dispose every registered watch (Core Uninstall). Returns how many.</summary>
    internal static int Clear()
    {
        List<IDisposable> all;
        lock (Gate)
        {
            all = new List<IDisposable>(Live.Count);
            for (int i = 0; i < Live.Count; i++) all.Add(Live[i].Watch);
            Live.Clear();
        }

        return DisposeAll(all);
    }

    private static int DisposeAll(List<IDisposable> watches)
    {
        for (int i = 0; i < watches.Count; i++)
        {
            try { watches[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: pointer watch dispose threw: " + e.Message); }
        }

        return watches.Count;
    }
}
