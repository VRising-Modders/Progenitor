using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Progenitor.Perf;

/// <summary>
/// One frame's time allowance for deferrable work. Game-free: the clock is injectable for tests.
/// A budget never stops the first step of a run (<see cref="BudgetedQueue{T}.Run"/>), so work always
/// moves forward, and a step that is itself slower than the budget is the only way a frame goes over.
/// </summary>
internal sealed class FrameBudget
{
    private readonly Func<long> _clock;
    private readonly long _start;
    private readonly long _limit;

    /// <param name="ms">The allowance in milliseconds.</param>
    /// <param name="clock">Stopwatch-style timestamps; <see cref="Stopwatch.GetTimestamp"/> when null.</param>
    /// <param name="frequency">Ticks per second of <paramref name="clock"/>; <see cref="Stopwatch.Frequency"/> when 0.</param>
    internal FrameBudget(double ms, Func<long>? clock = null, long frequency = 0)
    {
        _clock = clock ?? Stopwatch.GetTimestamp;
        Frequency = frequency > 0 ? frequency : Stopwatch.Frequency;
        _start = _clock();
        _limit = (long)(Math.Max(0, ms) * Frequency / 1000.0);
    }

    internal long Frequency { get; }

    /// <summary>Ticks since the budget started.</summary>
    internal long ElapsedTicks => _clock() - _start;

    internal double ElapsedMs => ElapsedTicks * 1000.0 / Frequency;

    /// <summary>The allowance is used up.</summary>
    internal bool Exhausted => ElapsedTicks >= _limit;
}

/// <summary>What one <see cref="BudgetedQueue{T}.Run"/> did.</summary>
internal readonly struct BudgetRun
{
    internal BudgetRun(int steps, int completed, int remaining, long ticks)
    {
        Steps = steps;
        Completed = completed;
        Remaining = remaining;
        Ticks = ticks;
    }

    /// <summary>Steps taken this run.</summary>
    internal int Steps { get; }

    /// <summary>Items finished this run.</summary>
    internal int Completed { get; }

    /// <summary>Items carried over to a later run.</summary>
    internal int Remaining { get; }

    /// <summary>Stopwatch ticks the run took.</summary>
    internal long Ticks { get; }
}

/// <summary>
/// A FIFO of work items processed a step at a time under a <see cref="FrameBudget"/>: each run takes
/// steps until the budget is used up (always at least one), and what is left carries over to the next
/// run. A step returns true when its item is finished. Game-free; main thread only.
/// </summary>
internal sealed class BudgetedQueue<T>
{
    private readonly LinkedList<T> _items = new();
    private readonly Func<T, bool> _step;
    private readonly Action<string>? _log;
    private readonly string _name;
    private bool _throwLogged;

    /// <param name="log">Where the first throwing step of this queue is reported (once); none when null.</param>
    internal BudgetedQueue(Func<T, bool> step, Action<string>? log = null, string name = "work")
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _log = log;
        _name = name ?? "work";
    }

    internal int Count => _items.Count;

    /// <summary>Steps that threw (their items were dropped), since construction.</summary>
    internal int Faults { get; private set; }

    internal void Enqueue(T item) => _items.AddLast(item);

    /// <summary>Put <paramref name="item"/> ahead of everything queued, after the item a step is working on.</summary>
    internal void EnqueueAfterCurrent(T item)
    {
        if (_items.First == null) _items.AddFirst(item);
        else _items.AddAfter(_items.First, item);
    }

    /// <summary>Put <paramref name="item"/> ahead of everything queued, the item in progress included.</summary>
    internal void EnqueueFront(T item) => _items.AddFirst(item);

    internal void Clear() => _items.Clear();

    /// <summary>The item the next step works on.</summary>
    internal bool TryPeek(out T item)
    {
        item = _items.First != null ? _items.First.Value : default!;
        return _items.First != null;
    }

    /// <summary>Take steps until <paramref name="budget"/> is used up or nothing is left (at least one step when anything is queued).</summary>
    internal BudgetRun Run(FrameBudget budget)
    {
        long start = budget.ElapsedTicks;
        int steps = 0, completed = 0;
        while (_items.Count > 0)
        {
            if (steps > 0 && budget.Exhausted) break;
            var node = _items.First!;
            steps++;
            bool done;
            try { done = _step(node.Value); }
            catch (Exception e)
            {
                // A throwing step drops its item: it would throw again every frame. Reported once per queue.
                done = true;
                Faults++;
                if (!_throwLogged)
                {
                    _throwLogged = true;
                    try { _log?.Invoke(_name + ": a queued step threw and its item was dropped (later ones are counted, not logged): " + e.Message); }
                    catch { }
                }
            }
            if (!done) continue;
            if (node.List != null) _items.Remove(node);
            completed++;
        }
        return new BudgetRun(steps, completed, _items.Count, budget.ElapsedTicks - start);
    }
}

/// <summary>Trimming long-lived lookup caches (game-free).</summary>
internal static class CacheTrim
{
    /// <summary>Remove every entry <paramref name="drop"/> selects; returns how many went.</summary>
    internal static int RemoveWhere<TKey, TValue>(Dictionary<TKey, TValue> cache, Func<TKey, TValue, bool> drop) where TKey : notnull
    {
        List<TKey>? gone = null;
        foreach (var kv in cache)
        {
            bool remove;
            try { remove = drop(kv.Key, kv.Value); }
            catch { remove = true; }
            if (remove) (gone ??= new List<TKey>()).Add(kv.Key);
        }
        if (gone == null) return 0;
        foreach (var key in gone) cache.Remove(key);
        return gone.Count;
    }
}

/// <summary>
/// Per-subsystem counters of budgeted, deferrable work, folded into the hitch summary
/// (<c>work=[name frames:… ms:… max:… carried:…]</c>): how many frames ran work, the total and the
/// largest single-frame cost, and the most work items carried over to a later frame. Allocation-free
/// per note after the first note of a name. Main thread only.
/// </summary>
internal static class FrameWork
{
    internal const int MaxNames = 12;

    private static readonly string[] Names = new string[MaxNames];
    private static readonly int[] Frames = new int[MaxNames];
    private static readonly long[] TotalTicks = new long[MaxNames];
    private static readonly long[] MaxTicks = new long[MaxNames];
    private static readonly int[] MaxCarried = new int[MaxNames];
    private static readonly long[] Skipped = new long[MaxNames];
    private static int _count;

    /// <summary>One frame of <paramref name="name"/>'s work: what it cost and how many items it carried over.</summary>
    internal static void Note(string name, long ticks, int carried)
    {
        int i = IndexOf(name);
        if (i < 0) return;
        Frames[i]++;
        TotalTicks[i] += Math.Max(0, ticks);
        if (ticks > MaxTicks[i]) MaxTicks[i] = ticks;
        if (carried > MaxCarried[i]) MaxCarried[i] = carried;
    }

    /// <summary>A pass of <paramref name="name"/> found nothing changed and did no work (a cheap skip).</summary>
    internal static void NoteSkip(string name)
    {
        int i = IndexOf(name);
        if (i >= 0) Skipped[i]++;
    }

    private static int IndexOf(string name)
    {
        for (int i = 0; i < _count; i++)
            if (ReferenceEquals(Names[i], name) || string.Equals(Names[i], name, StringComparison.Ordinal)) return i;
        if (_count >= MaxNames) return -1;
        Names[_count] = name;
        return _count++;
    }

    /// <summary>The window's segment (empty when nothing was noted), e.g. <c> work=[materials frames:30 ms:41 max:3 carried:12 skips:0]</c>.</summary>
    internal static string Format()
    {
        if (_count == 0) return "";
        var sb = new StringBuilder(" work=[");
        bool any = false;
        for (int i = 0; i < _count; i++)
        {
            if (Frames[i] == 0 && Skipped[i] == 0) continue;
            if (any) sb.Append(", ");
            any = true;
            sb.Append(Names[i])
                .Append(" frames:").Append(Frames[i].ToString(CultureInfo.InvariantCulture))
                .Append(" ms:").Append(Ms(TotalTicks[i]))
                .Append(" max:").Append(Ms(MaxTicks[i]))
                .Append(" carried:").Append(MaxCarried[i].ToString(CultureInfo.InvariantCulture))
                .Append(" skips:").Append(Skipped[i].ToString(CultureInfo.InvariantCulture));
        }
        return any ? sb.Append(']').ToString() : "";
    }

    /// <summary>Start a new window (names are kept, so their strings are not reallocated).</summary>
    internal static void ResetWindow()
    {
        for (int i = 0; i < _count; i++)
        {
            Frames[i] = 0;
            TotalTicks[i] = 0;
            MaxTicks[i] = 0;
            MaxCarried[i] = 0;
            Skipped[i] = 0;
        }
    }

    internal static void ResetForTests()
    {
        ResetWindow();
        for (int i = 0; i < _count; i++) Names[i] = "";
        _count = 0;
    }

    private static string Ms(long ticks) =>
        (Math.Max(0, ticks) * 1000.0 / Stopwatch.Frequency).ToString("0.#", CultureInfo.InvariantCulture);
}
