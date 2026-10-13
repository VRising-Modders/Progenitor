using System;
using System.Collections.Generic;

namespace Progenitor.Content;

/// <summary>
/// Vanilla values of every component an ordered editor overwrote, captured lazily at the
/// first tracked write of that component in this Core generation. Removing an editor
/// (handle <c>Dispose</c>, plugin unload, handshake Unrequire sweep) then restores the
/// prefab entity instead of leaving the removed editor's last values on it forever.
///
/// Game-free by construction: an entry holds the entity identity as two ints
/// (<c>Entity.Index</c> / <c>Entity.Version</c>) and a restore callback the write site
/// built while the component type was still known, so nothing here needs ECS generics or
/// reflection. The callback is invoked with an opaque context (the live <c>World</c>) on
/// the main thread from the registry's edit pass only.
///
/// Because every pass except <see cref="PrefabEditTrigger.NewClone"/> restores before it
/// re-runs the editors, an edit pass is idempotent by construction: an editor that does
/// read-modify-write sees vanilla again on every re-run and can no longer accumulate.
///
/// Only <c>PrefabEditHandle.Write&lt;T&gt;</c> and <c>SetComponent&lt;T&gt;</c> are
/// tracked. Structural changes made through <c>handle.EntityManager</c> (AddComponent,
/// RemoveComponent, buffer edits) are not snapshotted and are not restored; the handle
/// logs <c>unrestorable structural edit</c> once per editor at that property.
/// </summary>
internal sealed class PrefabEditBaselines
{
    private readonly object _gate = new();
    private readonly Dictionary<int, List<Baseline>> _byGuid = new();

    /// <summary>Number of prefabs with at least one captured baseline.</summary>
    public int PrefabCount
    {
        get { lock (_gate) return _byGuid.Count; }
    }

    /// <summary>
    /// Cheap pre-check for the write site: false when this (guid, component) already has a
    /// baseline, so the first writer in the life of the entity defines vanilla and a second
    /// write does not move it. Reads never capture.
    /// </summary>
    public bool NeedsCapture(int guidHash, string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        lock (_gate)
        {
            if (!_byGuid.TryGetValue(guidHash, out var list)) return true;
            return IndexOfNoLock(list, typeName!) < 0;
        }
    }

    /// <summary>
    /// Remember the pre-write value of <paramref name="typeName"/> on the entity identified
    /// by <paramref name="entityIndex"/> / <paramref name="entityVersion"/>. False when a
    /// baseline already exists (the earlier one is the vanilla value and is kept).
    /// </summary>
    public bool Capture(
        int guidHash,
        string? typeName,
        int entityIndex,
        int entityVersion,
        Action<object> restore)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        if (restore == null) throw new ArgumentNullException(nameof(restore));
        lock (_gate)
        {
            if (!_byGuid.TryGetValue(guidHash, out var list))
            {
                list = new List<Baseline>();
                _byGuid[guidHash] = list;
            }
            if (IndexOfNoLock(list, typeName!) >= 0) return false;
            list.Add(new Baseline(typeName!, entityIndex, entityVersion, restore));
            return true;
        }
    }

    /// <summary>Captured component count for one prefab (0 when nothing was written).</summary>
    public int Count(int guidHash)
    {
        lock (_gate) return _byGuid.TryGetValue(guidHash, out var list) ? list.Count : 0;
    }

    /// <summary>Component type names captured for one prefab, in capture order.</summary>
    public string[] Components(int guidHash)
    {
        lock (_gate)
        {
            if (!_byGuid.TryGetValue(guidHash, out var list) || list.Count == 0)
                return Array.Empty<string>();
            var names = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
                names[i] = list[i].TypeName;
            return names;
        }
    }

    /// <summary>Prefab guid hashes with at least one baseline (order undefined).</summary>
    public int[] Guids()
    {
        lock (_gate)
        {
            if (_byGuid.Count == 0) return Array.Empty<int>();
            var arr = new int[_byGuid.Count];
            _byGuid.Keys.CopyTo(arr, 0);
            return arr;
        }
    }

    /// <summary>
    /// Write every baseline of <paramref name="guidHash"/> back onto the bound entity and
    /// return the component names actually restored, in capture order. Baselines whose
    /// stored identity does not match <paramref name="entityIndex"/> /
    /// <paramref name="entityVersion"/> are stale (a new clone, or a new client world) and
    /// are dropped without being written. Matching baselines are kept: they are still the
    /// vanilla values for the next pass. A throwing callback is reported through
    /// <paramref name="onError"/> and skipped; the remaining ones still run.
    /// </summary>
    public string[] Restore(
        int guidHash,
        int entityIndex,
        int entityVersion,
        object context,
        Action<string, Exception>? onError = null)
    {
        Baseline[] run;
        lock (_gate)
        {
            if (!_byGuid.TryGetValue(guidHash, out var list) || list.Count == 0)
                return Array.Empty<string>();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].EntityIndex == entityIndex && list[i].EntityVersion == entityVersion) continue;
                list.RemoveAt(i);
            }
            if (list.Count == 0)
            {
                _byGuid.Remove(guidHash);
                return Array.Empty<string>();
            }
            run = list.ToArray();
        }

        var restored = new List<string>(run.Length);
        for (int i = 0; i < run.Length; i++)
        {
            try
            {
                run[i].Restore(context);
                restored.Add(run[i].TypeName);
            }
            catch (Exception e)
            {
                onError?.Invoke(run[i].TypeName, e);
            }
        }
        return restored.ToArray();
    }

    /// <summary>
    /// Forget this prefab's baselines without restoring. Used after a
    /// <see cref="PrefabEditTrigger.NewClone"/> pass (the applicator just wrote definition
    /// state onto a fresh or reset entity, so the old values are not vanilla any more) and
    /// after a restore-only pass (the entity is vanilla now; nothing left to remember).
    /// </summary>
    public void Drop(int guidHash)
    {
        lock (_gate) _byGuid.Remove(guidHash);
    }

    /// <summary>Forget everything (Core uninstall, new client world).</summary>
    public void Clear()
    {
        lock (_gate) _byGuid.Clear();
    }

    private static int IndexOfNoLock(List<Baseline> list, string typeName)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].TypeName, typeName, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private sealed class Baseline
    {
        public Baseline(string typeName, int entityIndex, int entityVersion, Action<object> restore)
        {
            TypeName = typeName;
            EntityIndex = entityIndex;
            EntityVersion = entityVersion;
            Restore = restore;
        }

        public string TypeName { get; }
        public int EntityIndex { get; }
        public int EntityVersion { get; }
        public Action<object> Restore { get; }
    }
}
