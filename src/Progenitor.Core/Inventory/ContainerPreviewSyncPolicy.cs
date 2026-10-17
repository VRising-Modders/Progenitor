using System;
using System.Collections.Generic;

namespace Progenitor.Inventory;

/// <summary>
/// The container preview's inventory sync, game-free (research note "remote container preview E1"). A chest's inventory
/// entity is sent only to the users listed in its <c>SyncToUserBuffer</c>; the game adds a user there when an
/// interaction with the chest starts and removes one copy when it ends. A preview does the same for the length of its
/// lease, on the inventory entities and on every slot item entity that has the buffer, and never grants an interaction.
///
/// <para>The buffer is a multiset (the game adds without checking for duplicates and removes the last match), so the
/// preview tracks exactly which entities it added a copy to, adds one copy on an entity's entry, removes one copy on
/// its exit and at the end, and never clears a buffer.</para>
/// </summary>
internal sealed class ContainerPreviewSyncLedger
{
    private readonly HashSet<long> _added = new();

    /// <summary>The entities this preview holds one copy of the user on.</summary>
    internal IReadOnlyCollection<long> Added => _added;

    internal int Count => _added.Count;

    internal bool Holds(long entity) => _added.Contains(entity);

    /// <summary>
    /// What to change so the preview holds a copy exactly on <paramref name="wanted"/> (the entities that should be synced
    /// now and carry the buffer): add on each wanted entity it does not hold yet, remove from each held entity no longer
    /// wanted (an item that left the chest). The caller applies each change and then calls <see cref="MarkAdded"/> or
    /// <see cref="MarkRemoved"/> for the ones that succeeded.
    /// </summary>
    internal (List<long> Add, List<long> Remove) Plan(IEnumerable<long> wanted)
    {
        var want = new HashSet<long>(wanted ?? Array.Empty<long>());
        var add = new List<long>();
        var remove = new List<long>();
        foreach (long e in want) if (!_added.Contains(e)) add.Add(e);
        foreach (long e in _added) if (!want.Contains(e)) remove.Add(e);
        add.Sort();
        remove.Sort();
        return (add, remove);
    }

    internal void MarkAdded(long entity) => _added.Add(entity);

    internal void MarkRemoved(long entity) => _added.Remove(entity);

    /// <summary>Every held entity (for the end of the lease or a save), emptying the ledger.</summary>
    internal List<long> TakeAll()
    {
        var all = new List<long>(_added);
        all.Sort();
        _added.Clear();
        return all;
    }

    /// <summary>An entity as one key: index in the high word, version in the low word.</summary>
    internal static long Key(int index, int version) => ((long)index << 32) | (uint)version;

    internal static (int Index, int Version) Unkey(long key) => ((int)(key >> 32), (int)(key & 0xFFFFFFFF));
}

/// <summary>
/// Edits of one <c>SyncToUserBuffer</c>, as a list of user keys, game-free. They mirror the game's own
/// <c>InventorySyncUtilities</c>: add appends without a duplicate check; remove takes out the <b>last</b> copy of the user
/// and nothing else.
/// </summary>
internal static class SyncToUserBufferEdit
{
    /// <summary>The buffer with one more copy of <paramref name="user"/> at the end.</summary>
    internal static long[] AddOne(IReadOnlyList<long> users, long user)
    {
        var next = new long[users.Count + 1];
        for (int i = 0; i < users.Count; i++) next[i] = users[i];
        next[users.Count] = user;
        return next;
    }

    /// <summary>The index of the last copy of <paramref name="user"/>, or -1.</summary>
    internal static int LastIndexOf(IReadOnlyList<long> users, long user)
    {
        for (int i = users.Count - 1; i >= 0; i--) if (users[i] == user) return i;
        return -1;
    }

    /// <summary>
    /// The buffer with the last copy of <paramref name="user"/> removed, or null when it holds none (nothing to write;
    /// another path already removed it). Every other entry, other users' and the same user's other copies, stays in order.
    /// </summary>
    internal static long[]? RemoveOne(IReadOnlyList<long> users, long user)
    {
        int at = LastIndexOf(users, user);
        if (at < 0) return null;
        var next = new long[users.Count - 1];
        for (int i = 0, j = 0; i < users.Count; i++) if (i != at) next[j++] = users[i];
        return next;
    }

    /// <summary>How many copies of <paramref name="user"/> the buffer holds.</summary>
    internal static int CountOf(IReadOnlyList<long> users, long user)
    {
        int n = 0;
        for (int i = 0; i < users.Count; i++) if (users[i] == user) n++;
        return n;
    }
}

/// <summary>When a preview's sync entries are taken out for a world save and put back, game-free.</summary>
internal static class ContainerPreviewSyncSave
{
    /// <summary>
    /// Seconds after a save trigger before the entries go back. Whether the game saves <c>SyncToUserBuffer</c> at all is
    /// unknown, and the save runs after its trigger; the entries stay out long enough for it to finish. The previewing
    /// client may see the window stop updating for that time.
    /// </summary>
    internal const double ResumeAfterSeconds = 5;

    /// <summary>Whether a suspended sync should be put back now.</summary>
    internal static bool ShouldResume(double suspendedAt, double now) =>
        !double.IsNaN(suspendedAt) && now - suspendedAt >= ResumeAfterSeconds;
}
