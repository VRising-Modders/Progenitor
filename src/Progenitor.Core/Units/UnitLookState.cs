using System;
using System.Collections.Generic;

namespace Progenitor.Units;

/// <summary>
/// Server-side record of the looks <c>ctx.Units.SetLook</c> asked for, keyed by unit entity. Holds no
/// handle and no plugin object — only ids and plain values — so a plugin unload is never pinned by it.
/// Game-free; <c>UnitLooks</c> drives it on the main thread.
/// </summary>
internal sealed class UnitLookServerTable
{
    internal sealed class Entry
    {
        public UnitId Unit;
        public UnitLook Look = UnitLook.Vanilla;
        public string PluginId = "";
        public int Epoch;

        /// <summary>The unit's network id once the game has assigned one; default until then.</summary>
        public UnitNetKey Key = default;

        /// <summary>True when clients have not been sent the current <see cref="Look"/> yet.</summary>
        public bool Dirty = true;

        /// <summary>True once any Set for this unit reached the transport, so a removal must send Clear.</summary>
        public bool Sent = false;

        /// <summary>
        /// For a player character (<c>ctx.Units.SetLook(Entity, ...)</c>): its user entity, whose
        /// disconnect clears the look. <see cref="UnitId.None"/> for every other entity.
        /// </summary>
        public UnitId CharacterUser = UnitId.None;
    }

    private readonly Dictionary<UnitId, Entry> _entries = new();

    public int Count => _entries.Count;

    /// <summary>
    /// Record <paramref name="look"/> (copied) for <paramref name="unit"/>. Returns the entry, marked
    /// dirty unless the stored look was already the same.
    /// </summary>
    public Entry Set(UnitId unit, UnitLook look, string pluginId, int epoch) =>
        Set(unit, look, pluginId, epoch, UnitId.None);

    /// <summary>
    /// <see cref="Set(UnitId, UnitLook, string, int)"/> for an entity that belongs to a player: the look
    /// is also removed by <see cref="RemoveByUser"/> for <paramref name="characterUser"/>.
    /// </summary>
    public Entry Set(UnitId unit, UnitLook look, string pluginId, int epoch, UnitId characterUser)
    {
        if (look == null) throw new ArgumentNullException(nameof(look));
        if (!_entries.TryGetValue(unit, out var entry) || entry.Epoch != epoch)
        {
            entry = new Entry { Unit = unit, Epoch = epoch };
            _entries[unit] = entry;
        }
        if (!UnitLookMath.Same(entry.Look, look) || !entry.Sent) entry.Dirty = true;
        entry.Look = look.Copy();
        entry.PluginId = pluginId ?? "";
        entry.CharacterUser = characterUser;
        return entry;
    }

    /// <summary>A copy of the look recorded for <paramref name="unit"/>, or null.</summary>
    public UnitLook? Get(UnitId unit) => _entries.TryGetValue(unit, out var entry) ? entry.Look.Copy() : null;

    /// <summary>Remove and return the entry for <paramref name="unit"/>, or null.</summary>
    public Entry? Remove(UnitId unit)
    {
        if (!_entries.TryGetValue(unit, out var entry)) return null;
        _entries.Remove(unit);
        return entry;
    }

    /// <summary>Remove and return every entry recorded by <paramref name="pluginId"/>.</summary>
    public List<Entry> RemoveOwnedBy(string pluginId)
    {
        var removed = new List<Entry>();
        foreach (var entry in _entries.Values)
            if (string.Equals(entry.PluginId, pluginId, StringComparison.Ordinal)) removed.Add(entry);
        foreach (var entry in removed) _entries.Remove(entry.Unit);
        return removed;
    }

    /// <summary>
    /// Remove and return every entry whose <see cref="Entry.CharacterUser"/> is <paramref name="user"/>
    /// (the player disconnected). <see cref="UnitId.None"/> removes nothing.
    /// </summary>
    public List<Entry> RemoveByUser(UnitId user)
    {
        var removed = new List<Entry>();
        if (user.IsNone) return removed;
        foreach (var entry in _entries.Values)
            if (entry.CharacterUser == user) removed.Add(entry);
        foreach (var entry in removed) _entries.Remove(entry.Unit);
        return removed;
    }

    /// <summary>Remove and return every entry.</summary>
    public List<Entry> RemoveAll()
    {
        var removed = new List<Entry>(_entries.Values);
        _entries.Clear();
        return removed;
    }

    /// <summary>Snapshot of the entries, safe to mutate the table while walking it.</summary>
    public Entry[] Snapshot()
    {
        if (_entries.Count == 0) return Array.Empty<Entry>();
        var copy = new Entry[_entries.Count];
        _entries.Values.CopyTo(copy, 0);
        return copy;
    }
}

/// <summary>
/// The natural <c>localScale</c> of each model instance, recorded the first time the applier sees it
/// and before it writes anything. Visual scale is relative to this (Frostmaw's model is 2, Bishop's
/// 1.2, Dracula's 1), and restoring a model writes it back. Keyed by the model's instance id.
/// </summary>
internal sealed class NaturalScaleBook
{
    private readonly Dictionary<int, UnitVec3> _natural = new();

    public int Count => _natural.Count;

    /// <summary>
    /// The natural scale of <paramref name="modelId"/>: the one recorded earlier, or
    /// <paramref name="current"/> when this is the first sight (which is then recorded).
    /// </summary>
    public UnitVec3 Observe(int modelId, UnitVec3 current)
    {
        if (_natural.TryGetValue(modelId, out var natural)) return natural;
        _natural[modelId] = current;
        return current;
    }

    public bool TryGet(int modelId, out UnitVec3 natural) => _natural.TryGetValue(modelId, out natural);

    /// <summary>Drop <paramref name="modelId"/> after its model was restored (or is gone).</summary>
    public bool Forget(int modelId) => _natural.Remove(modelId);

    public void Clear() => _natural.Clear();
}

/// <summary>
/// Client-side record of the looks the server sent, keyed by network id, plus what the applier last
/// wrote for each (which model instance, which look). The live applier keeps the model object itself
/// beside it; this half is game-free.
/// </summary>
internal sealed class UnitLookClientTable
{
    internal sealed class Entry
    {
        public UnitNetKey Key = default;
        public UnitLook Wanted = UnitLook.Vanilla;

        /// <summary>Instance id of the model the look was last applied to; 0 for none yet.</summary>
        public int ModelId;

        /// <summary>The look last applied to <see cref="ModelId"/>; null when nothing was applied yet.</summary>
        public UnitLook? Applied;

        /// <summary>The live model (a <c>UnityEngine.Transform</c>); set and read only by the live applier.</summary>
        public object? Model = null;
    }

    private readonly Dictionary<UnitNetKey, Entry> _entries = new();

    public int Count => _entries.Count;

    /// <summary>Record the wanted look for <paramref name="key"/> (copied). Keeps what was applied.</summary>
    public Entry Set(UnitNetKey key, UnitLook look)
    {
        if (look == null) throw new ArgumentNullException(nameof(look));
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry { Key = key };
            _entries[key] = entry;
        }
        entry.Wanted = look.Copy();
        return entry;
    }

    public Entry? Get(UnitNetKey key) => _entries.TryGetValue(key, out var entry) ? entry : null;

    public Entry? Remove(UnitNetKey key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return null;
        _entries.Remove(key);
        return entry;
    }

    public List<Entry> RemoveAll()
    {
        var removed = new List<Entry>(_entries.Values);
        _entries.Clear();
        return removed;
    }

    public Entry[] Snapshot()
    {
        if (_entries.Count == 0) return Array.Empty<Entry>();
        var copy = new Entry[_entries.Count];
        _entries.Values.CopyTo(copy, 0);
        return copy;
    }
}

/// <summary>
/// One applier step for one tracked unit whose model is <see cref="UnitLookClientTable.Entry"/>'s
/// current model: whether the model instance changed (the old one is restored first), the natural
/// and target scale, whether the scale must be written, and the color transition.
/// </summary>
internal readonly record struct UnitLookModelStep(
    bool ModelChanged,
    int PreviousModelId,
    UnitVec3 Natural,
    UnitVec3 TargetScale,
    bool WriteScale,
    UnitLookTransition Colors)
{
    /// <summary>
    /// Plan the step for <paramref name="entry"/> now that its model is <paramref name="modelId"/> at
    /// <paramref name="currentScale"/>. Records the natural scale in <paramref name="book"/> on first
    /// sight. Does not change <paramref name="entry"/>; call <see cref="Commit"/> after the writes.
    /// </summary>
    public static UnitLookModelStep Plan(UnitLookClientTable.Entry entry, int modelId, UnitVec3 currentScale, NaturalScaleBook book)
    {
        bool changed = entry.ModelId != modelId;
        var natural = book.Observe(modelId, currentScale);
        var target = UnitLookMath.ScaledFrom(natural, entry.Wanted.VisualScale);
        // A new model instance starts vanilla: nothing was applied to it yet.
        var colors = UnitLookTransition.Plan(changed ? null : entry.Applied, entry.Wanted);
        return new UnitLookModelStep(
            ModelChanged: changed,
            PreviousModelId: changed ? entry.ModelId : 0,
            Natural: natural,
            TargetScale: target,
            WriteScale: UnitLookMath.ScaleDrifted(currentScale, target),
            Colors: colors);
    }

    /// <summary>Record that the step was applied.</summary>
    public static void Commit(UnitLookClientTable.Entry entry, int modelId)
    {
        entry.ModelId = modelId;
        entry.Applied = entry.Wanted.Copy();
    }

    /// <summary>
    /// The restore for a model that loses its look (look cleared, model replaced, Core unload): the
    /// natural scale if one was recorded, and the color transition from what was applied to nothing.
    /// </summary>
    public static (bool HasNatural, UnitVec3 Natural, UnitLookTransition Colors) Restore(int modelId, UnitLook? applied, NaturalScaleBook book)
    {
        bool has = book.TryGet(modelId, out var natural);
        return (has, natural, UnitLookTransition.Plan(applied, null));
    }
}
