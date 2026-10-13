using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Ai;

/// <summary>
/// Game-free bookkeeping behind <see cref="PluginAi.Variants"/>: which variant names a plugin
/// defined, the marker buff each one is tagged with, which units carry which variant in the current
/// world epoch, and — after a world load — which marked units still need the variant re-applied.
/// Unit keys are <c>epoch/index:version</c>, so an id from an earlier world can never match.
/// </summary>
internal sealed class VariantBook
{
    internal sealed class Entry
    {
        internal Entry(string name, int bossGuid, int markerGuid, string markerId)
        {
            Name = name;
            BossGuid = bossGuid;
            MarkerGuid = markerGuid;
            MarkerId = markerId;
        }

        internal string Name { get; }
        internal int BossGuid { get; set; }
        internal int MarkerGuid { get; }
        internal string MarkerId { get; }
    }

    /// <summary>What a world scan saw on one unit.</summary>
    internal readonly struct ScannedUnit
    {
        internal ScannedUnit(int index, int version, int prefabGuid, IReadOnlyList<int> buffGuids)
        {
            Index = index;
            Version = version;
            PrefabGuid = prefabGuid;
            BuffGuids = buffGuids ?? Array.Empty<int>();
        }

        internal int Index { get; }
        internal int Version { get; }
        internal int PrefabGuid { get; }
        internal IReadOnlyList<int> BuffGuids { get; }
    }

    private readonly Dictionary<string, Entry> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Entry> _byMarker = new();
    private readonly Dictionary<string, string> _applied = new(StringComparer.Ordinal);

    internal int Count => _byName.Count;

    internal IEnumerable<Entry> Entries => _byName.Values;

    /// <summary>
    /// Buff clone id for a variant marker: <c>&lt;pluginGuid&gt;.ai.&lt;name&gt;</c>, ASCII, at most 63
    /// bytes (the DefineBuff id rule). Throws when it cannot be formed.
    /// </summary>
    internal static string MarkerId(string pluginGuid, string variantName)
    {
        if (string.IsNullOrWhiteSpace(pluginGuid)) throw new ArgumentException("plugin id is required");
        ValidateName(variantName);
        string id = pluginGuid.Trim() + ".ai." + variantName;
        if (Encoding.UTF8.GetByteCount(id) > 63)
            throw new ArgumentException("variant marker id '" + id + "' is longer than 63 bytes; use a shorter variant name");
        foreach (char c in id)
            if (c > 0x7e || c < 0x21) throw new ArgumentException("variant marker id '" + id + "' must be printable ASCII without spaces");
        return id;
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Variant name is required.");
        foreach (char c in name)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
            if (!ok) throw new ArgumentException("Variant names use letters, digits, '_' and '-' only: '" + name + "'.");
        }
    }

    internal Entry Define(string name, int bossGuid, int markerGuid, string markerId)
    {
        ValidateName(name);
        if (_byName.ContainsKey(name)) throw new InvalidOperationException("variant '" + name + "' is already defined");
        if (_byMarker.ContainsKey(markerGuid)) throw new InvalidOperationException("marker guid " + markerGuid + " is already used by variant '" + _byMarker[markerGuid].Name + "'");
        var entry = new Entry(name, bossGuid, markerGuid, markerId);
        _byName[name] = entry;
        _byMarker[markerGuid] = entry;
        return entry;
    }

    internal void Remove(string name)
    {
        if (!_byName.TryGetValue(name, out var entry)) return;
        _byName.Remove(name);
        _byMarker.Remove(entry.MarkerGuid);
        var stale = new List<string>();
        foreach (var pair in _applied)
            if (pair.Value == name) stale.Add(pair.Key);
        foreach (var key in stale) _applied.Remove(key);
    }

    internal bool TryGet(string name, out Entry entry) => _byName.TryGetValue(name, out entry!);

    internal bool TryByMarker(int markerGuid, out Entry entry) => _byMarker.TryGetValue(markerGuid, out entry!);

    internal static string Key(int epoch, int index, int version) => epoch + "/" + index + ":" + version;

    internal void Record(string unitKey, string variant) => _applied[unitKey] = variant;

    internal bool Forget(string unitKey) => _applied.Remove(unitKey);

    internal string? VariantOf(string unitKey) => _applied.TryGetValue(unitKey, out var v) ? v : null;

    /// <summary>Units recorded in <paramref name="epoch"/> (keys from older epochs are ignored).</summary>
    internal List<KeyValuePair<string, string>> AppliedIn(int epoch)
    {
        string prefix = epoch + "/";
        var list = new List<KeyValuePair<string, string>>();
        foreach (var pair in _applied)
            if (pair.Key.StartsWith(prefix, StringComparison.Ordinal)) list.Add(pair);
        return list;
    }

    /// <summary>Drop every record from an epoch other than <paramref name="epoch"/>.</summary>
    internal int DropStale(int epoch)
    {
        string prefix = epoch + "/";
        var stale = new List<string>();
        foreach (var key in _applied.Keys)
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) stale.Add(key);
        foreach (var key in stale) _applied.Remove(key);
        return stale.Count;
    }

    /// <summary>
    /// After a world load: every scanned unit that carries one of this book's marker buffs, is the
    /// variant's boss prefab, and is not already recorded in <paramref name="epoch"/>. A marker on a
    /// unit of another prefab is reported in <paramref name="mismatched"/> and not re-applied.
    /// </summary>
    internal List<(ScannedUnit Unit, Entry Variant)> PlanReapply(IEnumerable<ScannedUnit> scan, int epoch, List<string> mismatched)
    {
        var plan = new List<(ScannedUnit, Entry)>();
        foreach (var unit in scan)
        {
            Entry? hit = null;
            foreach (int guid in unit.BuffGuids)
            {
                if (!_byMarker.TryGetValue(guid, out var entry)) continue;
                hit = entry;
                break;
            }
            if (hit == null) continue;
            if (_applied.ContainsKey(Key(epoch, unit.Index, unit.Version))) continue;
            if (hit.BossGuid != unit.PrefabGuid)
            {
                mismatched?.Add(unit.Index + ":" + unit.Version + " carries variant '" + hit.Name + "' but is prefab " + unit.PrefabGuid);
                continue;
            }
            plan.Add((unit, hit));
        }
        return plan;
    }
}
