using System;
using System.Collections.Generic;

namespace Progenitor.Content;

internal enum DefinitionPriority
{
    InventoryOrEquipment,
    ActiveContainerOrVisible,
    Nearby,
    Background
}

/// <summary>
/// One client's bounded definition stream. Call only on the owning thread. Delivered identities
/// remain suppressed until Forget (a replacement) or Clear (a new connection).
/// </summary>
internal sealed class DefinitionPriorityQueue
{
    private sealed class Entry
    {
        internal int Guid;
        internal DefinitionPriority Priority;
        internal double Distance;
        internal long Sequence;
        internal LinkedListNode<Entry> AgeNode = null!;
    }

    private readonly int _capacity;
    private readonly int _foregroundBurst;
    private readonly Dictionary<int, Entry> _pending = new();
    private readonly HashSet<int> _delivered = new();
    private readonly HashSet<Entry> _promoted = new();
    private readonly LinkedList<Entry> _age = new();
    private readonly SortedSet<Entry> _priority = new(Comparer<Entry>.Create((a, b) =>
    {
        int comparison = a.Priority.CompareTo(b.Priority);
        if (comparison == 0 && a.Priority == DefinitionPriority.Nearby)
            comparison = a.Distance.CompareTo(b.Distance);
        return comparison != 0 ? comparison : a.Sequence.CompareTo(b.Sequence);
    }));
    private long _sequence;
    private int _foregroundSent;

    internal DefinitionPriorityQueue(int capacity, int foregroundBurst = 8)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (foregroundBurst <= 0) throw new ArgumentOutOfRangeException(nameof(foregroundBurst));
        _capacity = capacity;
        _foregroundBurst = foregroundBurst;
    }

    internal int PendingCount => _pending.Count;
    internal bool ContainsPending(int guid) => _pending.ContainsKey(guid);
    internal bool IsDelivered(int guid) => _delivered.Contains(guid);

    /// <summary>Adds a unique identity or promotes pending work; never demotes it.</summary>
    internal bool Enqueue(int guid, DefinitionPriority priority = DefinitionPriority.Background,
        double distanceSquared = double.PositiveInfinity)
    {
        if (priority < DefinitionPriority.InventoryOrEquipment || priority > DefinitionPriority.Background)
            throw new ArgumentOutOfRangeException(nameof(priority));
        if (double.IsNaN(distanceSquared) || distanceSquared < 0)
            throw new ArgumentOutOfRangeException(nameof(distanceSquared));
        if (_delivered.Contains(guid)) return false;
        if (_pending.TryGetValue(guid, out var existing))
        {
            if (priority < existing.Priority ||
                (priority == DefinitionPriority.Nearby && existing.Priority == priority && distanceSquared < existing.Distance))
            {
                _priority.Remove(existing);
                existing.Priority = priority;
                existing.Distance = distanceSquared;
                _priority.Add(existing);
                _promoted.Add(existing);
            }
            return true;
        }
        if (_pending.Count + _delivered.Count >= _capacity) return false;
        var entry = new Entry { Guid = guid, Priority = priority, Distance = distanceSquared, Sequence = _sequence++ };
        entry.AgeNode = _age.AddLast(entry);
        _pending.Add(guid, entry);
        _priority.Add(entry);
        if (priority != DefinitionPriority.Background) _promoted.Add(entry);
        return true;
    }

    /// <summary>Expires relevance hints in O(k log n), where k is the promoted pending count.
    /// Original age, delivery suppression and the fairness cycle survive movement or teleport.</summary>
    internal void ResetPriorities()
    {
        foreach (var entry in _promoted)
        {
            _priority.Remove(entry);
            entry.Priority = DefinitionPriority.Background;
            entry.Distance = double.PositiveInfinity;
            _priority.Add(entry);
        }
        _promoted.Clear();
    }

    /// <summary>
    /// Claims the next delivery. Every foregroundBurst + 1 claims serve oldest pending work,
    /// so an identity with N older pending entries is claimed within (N + 1) such cycles,
    /// even when new foreground work keeps arriving. The caller owns transmission after claim.
    /// </summary>
    internal bool TryDequeue(out int guid)
    {
        if (_pending.Count == 0) { guid = default; return false; }
        Entry entry;
        if (_foregroundSent >= _foregroundBurst)
        {
            entry = _age.First!.Value;
            _foregroundSent = 0;
        }
        else
        {
            entry = _priority.Min!;
            _foregroundSent++;
        }
        RemovePending(entry);
        _delivered.Add(entry.Guid);
        guid = entry.Guid;
        return true;
    }

    internal bool Forget(int guid)
    {
        if (_pending.TryGetValue(guid, out var entry))
        {
            RemovePending(entry);
            return true;
        }
        return _delivered.Remove(guid);
    }

    internal void Clear()
    {
        _pending.Clear();
        _delivered.Clear();
        _promoted.Clear();
        _age.Clear();
        _priority.Clear();
        _sequence = 0;
        _foregroundSent = 0;
    }

    private void RemovePending(Entry entry)
    {
        _priority.Remove(entry);
        _promoted.Remove(entry);
        _age.Remove(entry.AgeNode);
        _pending.Remove(entry.Guid);
    }
}
