using System;
using System.Collections.Generic;

namespace Progenitor.Inventory;

/// <summary>How a preview takes its copy of the user on an entity it should hold one on.</summary>
internal enum SyncEntryAction
{
    /// <summary>Append one copy of the user to the list.</summary>
    Append = 0,
    /// <summary>The game already copied the user onto this entering item (its item sync follows the inventory's list): take that copy over.</summary>
    Adopt = 1,
}

/// <summary>
/// The container preview's inventory sync, game-free (research note "remote container preview E1"). A chest's inventory
/// entity is sent only to the users listed in its <c>SyncToUserBuffer</c>; the game adds a user there when an
/// interaction with the chest starts and removes one copy when it ends. A preview does the same for the length of its
/// lease, on the inventory entities and on every slot item entity that has the buffer, and never grants an interaction.
///
/// <para>The buffer is a multiset (the game adds without checking for duplicates and removes the last match), so the
/// preview tracks exactly which entities it holds one copy on, takes one on an entity's entry, removes one on its exit
/// and at the end, marks an entity released only after its removal read back, and never clears a buffer.</para>
/// </summary>
internal sealed class ContainerPreviewSyncLedger
{
    private readonly HashSet<long> _held = new();

    /// <summary>The entities this preview holds one copy of the user on.</summary>
    internal IReadOnlyCollection<long> Held => _held;

    internal int Count => _held.Count;

    internal bool Holds(long entity) => _held.Contains(entity);

    /// <summary>
    /// What to change so the preview holds a copy exactly on <paramref name="wanted"/> (the entities that should be synced
    /// now and carry the buffer): take a copy on each wanted entity it does not hold, release each held entity no longer
    /// wanted (an item that left the chest). The caller applies each change and calls <see cref="MarkHeld"/> /
    /// <see cref="MarkReleased"/> only for the ones that succeeded; a failed one shows up in the next plan again.
    /// </summary>
    internal (List<long> Take, List<long> Release) Plan(IEnumerable<long> wanted)
    {
        var want = new HashSet<long>(wanted ?? Array.Empty<long>());
        var take = new List<long>();
        var release = new List<long>();
        foreach (long e in want) if (!_held.Contains(e)) take.Add(e);
        foreach (long e in _held) if (!want.Contains(e)) release.Add(e);
        take.Sort();
        release.Sort();
        return (take, release);
    }

    internal void MarkHeld(long entity) => _held.Add(entity);

    internal void MarkReleased(long entity) => _held.Remove(entity);

    /// <summary>Every held entity, sorted (for the end of the lease or a save); the caller releases each that succeeds.</summary>
    internal List<long> Snapshot()
    {
        var all = new List<long>(_held);
        all.Sort();
        return all;
    }

    /// <summary>
    /// How to take the copy on an entity: on the first refresh (the lease start, or after a save put the entries back)
    /// always append; afterwards, an entering item that already holds a copy of the user got it from the game's item
    /// sync (it copies the inventory's list onto changed items), so that copy is adopted rather than doubled.
    /// </summary>
    internal static SyncEntryAction ActionFor(bool firstRefresh, int copiesOfUserNow) =>
        !firstRefresh && copiesOfUserNow > 0 ? SyncEntryAction.Adopt : SyncEntryAction.Append;

    /// <summary>An entity as one key: index in the high word, version in the low word.</summary>
    internal static long Key(int index, int version) => ((long)index << 32) | (uint)version;

    internal static (int Index, int Version) Unkey(long key) => ((int)(key >> 32), (int)(key & 0xFFFFFFFF));
}

/// <summary>
/// Edits of one <c>SyncToUserBuffer</c>, as a list of user keys, game-free. They mirror the game's own
/// <c>InventorySyncUtilities</c>: add appends without a duplicate check; remove takes out the <b>last</b> copy of the user
/// and nothing else. Every write is read back against <see cref="Same"/>.
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

    /// <summary>Same entries in the same order: the read-back check after every write.</summary>
    internal static bool Same(IReadOnlyList<long> a, IReadOnlyList<long> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>
    /// After an append that threw: did it land anyway? Then a retry (with the unflagged type index) would add a second
    /// copy. True when the list is exactly the old one plus the user at the end.
    /// </summary>
    internal static bool AppendLanded(IReadOnlyList<long> before, IReadOnlyList<long> after, long user) =>
        Same(AddOne(before, user), after);
}

/// <summary>
/// Taking a preview's sync entries out for a world save and putting them back once the save is written, game-free.
/// Whether the game saves <c>SyncToUserBuffer</c> is unknown; an accepted save is serialized on a later frame, and
/// the only completion signal is the save file's write stamp (<c>WorldSaveTrigger.NewestSaveWriteUtc</c>), so the entries
/// go back when that stamp moved past the one read at the trigger and has stood for <see cref="SettleSeconds"/>, or after
/// <see cref="TimeoutSeconds"/> when it never moves. A second save during the suspension restarts the wait from its own
/// trigger.
/// </summary>
internal sealed class SyncSaveSuspension
{
    /// <summary>The most the entries stay out when the save file's stamp never moves.</summary>
    internal const double TimeoutSeconds = 60;

    /// <summary>How long the advanced stamp must stand still (a compressed save is written over a moment).</summary>
    internal const double SettleSeconds = 1;

    private DateTime _before;
    private DateTime _lastSeen;
    private double _since = double.NaN;
    private double _settleFrom = double.NaN;

    internal bool Active => !double.IsNaN(_since);

    /// <summary>True when the last <see cref="ShouldResume"/> that returned true did so on the timeout.</summary>
    internal bool TimedOut { get; private set; }

    /// <summary>A world save was triggered (first, or again during a suspension: the wait restarts from this one).</summary>
    internal void Begin(double now, DateTime stampAtTrigger)
    {
        _before = stampAtTrigger;
        _lastSeen = stampAtTrigger;
        _since = now;
        _settleFrom = double.NaN;
        TimedOut = false;
    }

    /// <summary>Whether the entries go back now; true ends the suspension.</summary>
    internal bool ShouldResume(double now, DateTime stampNow)
    {
        if (!Active) return false;
        if (stampNow != DateTime.MinValue && stampNow > _before)
        {
            if (stampNow != _lastSeen || double.IsNaN(_settleFrom)) { _lastSeen = stampNow; _settleFrom = now; }
            if (now - _settleFrom >= SettleSeconds) { End(); return true; }
            return false;
        }
        if (now - _since >= TimeoutSeconds) { End(); TimedOut = true; return true; }
        return false;
    }

    internal void End()
    {
        _since = double.NaN;
        _settleFrom = double.NaN;
    }
}

/// <summary>One preview the server may not have cleaned up (persisted in Core's mod save): the chest and the user.</summary>
internal sealed class ContainerPreviewSyncRecord
{
    public string Container { get; set; } = "";
    public ulong Platform { get; set; }
}

/// <summary>The persisted document of <see cref="ContainerPreviewSyncRecord"/>s.</summary>
internal sealed class ContainerPreviewSyncRecords
{
    internal const string StoreId = "progenitor.containerpreview";
    internal const string Key = "syncRecords";

    public List<ContainerPreviewSyncRecord> Records { get; set; } = new();

    internal bool Add(string container, ulong platform)
    {
        if (Contains(container, platform)) return false;
        Records.Add(new ContainerPreviewSyncRecord { Container = container, Platform = platform });
        return true;
    }

    internal bool Remove(string container, ulong platform) =>
        Records.RemoveAll(r => r.Platform == platform && string.Equals(r.Container, container, StringComparison.Ordinal)) > 0;

    internal bool Contains(string container, ulong platform) =>
        Records.Exists(r => r.Platform == platform && string.Equals(r.Container, container, StringComparison.Ordinal));
}

/// <summary>What the startup sweep does with one record.</summary>
internal enum SyncSweepStep
{
    /// <summary>The chest is bound: remove one copy of the user from its inventory entities and items, then drop the record.</summary>
    Sweep = 0,
    /// <summary>The chest's id is not bound to an entity yet after the load: try again on a later tick.</summary>
    Wait = 1,
    /// <summary>The chest is gone (or unknown), or the wait ran out: drop the record.</summary>
    Drop = 2,
}

/// <summary>
/// The startup sweep, game-free: records left by a run that did not end cleanly (a crash during a preview). It is safe
/// whether or not the game saves <c>SyncToUserBuffer</c>: a list without a copy of the user is left as it is.
/// </summary>
internal static class ContainerPreviewSyncSweep
{
    /// <summary>How long after the load the sweep waits for a chest's id to bind.</summary>
    internal const double WaitSeconds = 120;

    internal static SyncSweepStep Decide(ContainerIdStatus status, double secondsSinceLoad) => status switch
    {
        ContainerIdStatus.Live => SyncSweepStep.Sweep,
        ContainerIdStatus.Unbound => secondsSinceLoad < WaitSeconds ? SyncSweepStep.Wait : SyncSweepStep.Drop,
        _ => SyncSweepStep.Drop,
    };
}
