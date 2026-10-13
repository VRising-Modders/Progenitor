using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// MAPTP1: who has asked for the whole world map to be shown, and what to put back when nobody does.
/// Game-free; <see cref="WorldMap.RevealAll"/> drives it against the game's flag.
///
/// <para>The flag is the client's <c>RevealMapPresentationSystem.RevealFullMap</c> (a public static
/// bool) — the switch behind the game's own debug-menu "Reveal Map" toggle. It changes
/// what the map draws and nothing the server stores: the player's exploration
/// (<c>UserMapZonePackedRevealElement</c> on the server, <c>RevealedMap_Client</c> on the client) is
/// never written. So "restore" here is the flag's prior value, captured when the first lease is taken
/// and written back when the last one is released.</para>
/// </summary>
public sealed class MapRevealLeases
{
    private readonly List<object> _holders = new();

    /// <summary>The flag's value before the first lease, or null when no lease is held.</summary>
    public bool? Prior { get; private set; }

    /// <summary>How many leases are held.</summary>
    public int Count => _holders.Count;

    /// <summary>
    /// Take a lease for <paramref name="holder"/>. On the first lease <paramref name="readFlag"/> is read
    /// (an unreadable flag reads as off) and the method returns true: the caller sets the flag on. A
    /// holder that already holds one is not counted twice.
    /// </summary>
    public bool Acquire(object holder, Func<bool?> readFlag)
    {
        if (holder == null) throw new ArgumentNullException(nameof(holder));
        foreach (var h in _holders)
            if (ReferenceEquals(h, holder)) return false;
        bool first = _holders.Count == 0;
        _holders.Add(holder);
        if (first)
        {
            bool? read = null;
            try { read = readFlag?.Invoke(); }
            catch { read = null; }
            Prior = read ?? false;
        }

        return first;
    }

    /// <summary>
    /// Release <paramref name="holder"/>'s lease. Returns the value to write back when it was the last one,
    /// otherwise null (others still want the map shown, or the holder held nothing).
    /// </summary>
    public bool? Release(object holder)
    {
        int at = -1;
        for (int i = 0; i < _holders.Count; i++)
        {
            if (!ReferenceEquals(_holders[i], holder)) continue;
            at = i;
            break;
        }

        if (at < 0) return null;
        _holders.RemoveAt(at);
        if (_holders.Count > 0) return null;
        var restore = Prior ?? false;
        Prior = null;
        return restore;
    }

    /// <summary>Drop every lease (Core unload). Returns the value to write back, or null when none was held.</summary>
    public bool? ReleaseAll()
    {
        if (_holders.Count == 0) return null;
        _holders.Clear();
        var restore = Prior ?? false;
        Prior = null;
        return restore;
    }
}
