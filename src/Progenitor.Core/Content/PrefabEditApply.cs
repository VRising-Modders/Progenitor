using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Content;

/// <summary>
/// One editor's result after a run: who wrote, at what priority, which component type names.
/// No delegate — safe to keep across a simulated Core swap when the table was rebuilt from
/// <see cref="PrefabEditIntent"/>.
/// </summary>
internal readonly struct PrefabEditRecord
{
    public PrefabEditRecord(in PrefabEditSpec spec, string[] components)
    {
        GuidHash = spec.GuidHash;
        PluginId = spec.PluginId;
        PluginName = spec.PluginName;
        Priority = spec.Priority;
        Components = components ?? Array.Empty<string>();
    }

    public int GuidHash { get; }
    public string PluginId { get; }
    public string PluginName { get; }
    public int Priority { get; }
    public string[] Components { get; }
}

/// <summary>
/// Why the registrar is considering an ordered edit pass. Evidence 73 logged the same
/// conflict three times because every apply seam re-ran the pass: registration, sending
/// a definition / key-table placeholder to a joining client, and rehydrate after a Core
/// reload. Only a <see cref="NewClone"/> (entity values were just written from the
/// definition) and an editor-set / generation change legitimately need a re-run.
/// Sending <c>PrfD</c>/<c>PrfK</c> does not create a clone and must not re-run.
/// </summary>
internal enum PrefabEditTrigger
{
    /// <summary>
    /// A new clone was just inserted, or <c>UpdateExisting</c> just reset component
    /// values to the definition (clone-time <c>MaxAmount</c>, name, etc.). Editors must
    /// run again so last-writer values land on that entity.
    /// </summary>
    NewClone,

    /// <summary>
    /// An editor was added (or the ordered set for this GUID otherwise changed). Run
    /// even if the entity was already edited, so the new set is what the game sees.
    /// </summary>
    EditorsChanged,

    /// <summary>
    /// Core generation rehydrate after a swap. The table was rebuilt from plugin
    /// <c>OnLoad</c>; surviving clones still have the previous generation's values but
    /// this generation's delegates are new and must run when ApplyPending did not.
    /// </summary>
    Rehydrate,

    /// <summary>
    /// World / collection became ready (WorldReady, load prefix, ApplyPending's vanilla
    /// sweep). Skip when this GUID already ran with the current editor fingerprint.
    /// </summary>
    CollectionReady,

    /// <summary>
    /// Captured editor inputs changed (<c>Invalidate</c> / <c>InvalidateEdits</c>). The
    /// ordered set is the same; values are not. Re-runs when the invalidation generation
    /// (version counter in the fingerprint) moved, not unconditionally, so a nested
    /// Invalidate during a pass and a cfg Reload that fires many SettingChanged events
    /// coalesce to one extra pass per affected prefab.
    /// </summary>
    InputsChanged,

    /// <summary>
    /// Sending <c>PrfD</c> / <c>PrfK</c> to a joining client does not create or reset a
    /// clone. Do not re-run; the last pass is still on the entity.
    /// </summary>
    PlaceholderSend,
}

/// <summary>
/// Distinct-conflict set for one Core generation. Key is prefab guid + component + the
/// two plugin ids (loser, winner). Re-log only after <see cref="Clear"/> (Core reload /
/// table drop) or <see cref="InvalidateGuid"/> (the set of editors for that prefab changed).
/// </summary>
internal sealed class PrefabEditConflictLog
{
    private readonly object _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>
    /// Stable dedupe key. Plugin ids compare case-insensitively, matching
    /// <see cref="PrefabEditOrder"/> before/after identity.
    /// </summary>
    public static string Key(int guidHash, string? component, string? loserId, string? winnerId)
    {
        return guidHash.ToString()
            + "\n" + (component ?? "")
            + "\n" + (loserId ?? "").ToLowerInvariant()
            + "\n" + (winnerId ?? "").ToLowerInvariant();
    }

    public static string Key(in PrefabEditConflict conflict) =>
        Key(conflict.GuidHash, conflict.Component, conflict.LoserId, conflict.WinnerId);

    /// <summary>True the first time this key is seen this generation; false on repeats.</summary>
    public bool ShouldLog(in PrefabEditConflict conflict)
    {
        string key = Key(in conflict);
        lock (_gate) return _seen.Add(key);
    }

    /// <summary>
    /// Drop keys for <paramref name="guidHash"/> so the next pass of that prefab re-logs
    /// after an add/remove of editors.
    /// </summary>
    public void InvalidateGuid(int guidHash)
    {
        string prefix = guidHash.ToString() + "\n";
        lock (_gate)
        {
            if (_seen.Count == 0) return;
            List<string>? drop = null;
            foreach (var k in _seen)
            {
                if (!k.StartsWith(prefix, StringComparison.Ordinal)) continue;
                drop ??= new List<string>();
                drop.Add(k);
            }
            if (drop == null) return;
            for (int i = 0; i < drop.Count; i++)
                _seen.Remove(drop[i]);
        }
    }

    /// <summary>New Core generation or a full table drop. Every distinct conflict may log again.</summary>
    public void Clear()
    {
        lock (_gate) _seen.Clear();
    }
}

/// <summary>
/// Once-per-generation string set. Used for thrown-editor errors (plugin id + guid) and the
/// vanilla-GUID local-only Warn. Unlike <see cref="PrefabEditConflictLog"/> this does not
/// re-arm when the editor set changes: "once per generation" is the contract.
/// </summary>
internal sealed class PrefabEditOnceLog
{
    private readonly object _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>Stable key: guid then lowercased plugin id, matching conflict identity.</summary>
    public static string Key(int guidHash, string? pluginId) =>
        guidHash.ToString() + "\n" + (pluginId ?? "").ToLowerInvariant();

    /// <summary>True the first time this key is seen this generation; false on repeats.</summary>
    public bool ShouldLog(int guidHash, string? pluginId) => ShouldLog(Key(guidHash, pluginId));

    public bool ShouldLog(string key)
    {
        lock (_gate) return _seen.Add(key ?? "");
    }

    /// <summary>New Core generation or a full table drop.</summary>
    public void Clear()
    {
        lock (_gate) _seen.Clear();
    }
}

/// <summary>
/// Runs an already-ordered editor list, records per-prefab touches, and emits conflict Warn
/// lines. Shared by the live registry (ECS handle) and the unit tests (fake component bag)
/// so a conflict string cannot drift.
/// </summary>
internal static class PrefabEditApply
{
    /// <summary>
    /// Invoke each editor in <paramref name="ordered"/> via <paramref name="invoke"/>, which
    /// returns the component type names that editor wrote. Identity types are ignored by
    /// <see cref="PrefabEditOrder.Conflicts"/>. A thrown editor is skipped (empty touches)
    /// after <paramref name="logError"/>; remaining editors still run.
    /// When <paramref name="oncePerGeneration"/> is set, each distinct conflict (guid +
    /// component + the two plugin ids) is forwarded to <paramref name="logWarn"/> once
    /// until that log is cleared or the prefab's editor set changes.
    /// </summary>
    public static PrefabEditRecord[] Run(
        PrefabEditRegistration[] ordered,
        Func<PrefabEditRegistration, IReadOnlyList<string>> invoke,
        Action<string>? logWarn,
        Action<string, Exception>? logError,
        PrefabEditConflictLog? oncePerGeneration = null)
    {
        return Run(ordered, invoke, logWarn, logError, oncePerGeneration, throwOnce: null, out _);
    }

    /// <summary>
    /// Same as <see cref="Run(PrefabEditRegistration[], Func{PrefabEditRegistration, IReadOnlyList{string}}, Action{string}, Action{string, Exception}, PrefabEditConflictLog)"/>
    /// plus throw tracking. <paramref name="anyThrew"/> is true when at least one editor
    /// threw: the caller must not fingerprint the pass (<see cref="PrefabEditTable.RememberRun"/>)
    /// so the next trigger retries. When <paramref name="throwOnce"/> is set, each throwing
    /// plugin id + guid is forwarded to <paramref name="logError"/> once per generation.
    /// </summary>
    public static PrefabEditRecord[] Run(
        PrefabEditRegistration[] ordered,
        Func<PrefabEditRegistration, IReadOnlyList<string>> invoke,
        Action<string>? logWarn,
        Action<string, Exception>? logError,
        PrefabEditConflictLog? oncePerGeneration,
        PrefabEditOnceLog? throwOnce,
        out bool anyThrew)
    {
        anyThrew = false;
        if (ordered == null || ordered.Length == 0)
            return Array.Empty<PrefabEditRecord>();
        if (invoke == null)
            throw new ArgumentNullException(nameof(invoke));

        var records = new PrefabEditRecord[ordered.Length];
        var touches = new IReadOnlyList<string>[ordered.Length];
        var specs = new PrefabEditSpec[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            specs[i] = ordered[i].Spec;
            IReadOnlyList<string> written;
            try
            {
                written = invoke(ordered[i]) ?? Array.Empty<string>();
            }
            catch (Exception e)
            {
                anyThrew = true;
                bool report = throwOnce == null || throwOnce.ShouldLog(ordered[i].Spec.GuidHash, ordered[i].Spec.PluginId);
                if (report)
                    logError?.Invoke(ordered[i].Spec.PluginId, e);
                written = Array.Empty<string>();
            }

            var copy = new string[written.Count];
            for (int t = 0; t < written.Count; t++)
                copy[t] = written[t] ?? "";
            touches[i] = copy;
            records[i] = new PrefabEditRecord(ordered[i].Spec, copy);
        }

        var conflicts = PrefabEditOrder.Conflicts(specs, touches);
        for (int i = 0; i < conflicts.Count; i++)
        {
            if (oncePerGeneration != null && !oncePerGeneration.ShouldLog(conflicts[i]))
                continue;
            logWarn?.Invoke(conflicts[i].Line);
        }
        return records;
    }

    /// <summary>
    /// Whether this trigger should invoke editors given the fingerprint of the last successful
    /// pass and the current ordered set. <see cref="PrefabEditTrigger.PlaceholderSend"/> never
    /// runs. <see cref="PrefabEditTrigger.NewClone"/>, <see cref="PrefabEditTrigger.EditorsChanged"/>,
    /// and <see cref="PrefabEditTrigger.Rehydrate"/> always run (entity values were reset or
    /// the generation / editor set is new). <see cref="PrefabEditTrigger.InputsChanged"/> and
    /// <see cref="PrefabEditTrigger.CollectionReady"/> compare the fingerprint, which includes
    /// the invalidation generation: a nested Invalidate during a pass, or a cfg Reload that
    /// fires many SettingChanged events, only re-runs when that counter actually moved.
    /// </summary>
    public static bool ShouldRun(
        PrefabEditTrigger trigger,
        string? appliedFingerprint,
        string? currentFingerprint)
    {
        switch (trigger)
        {
            case PrefabEditTrigger.PlaceholderSend:
                return false;
            case PrefabEditTrigger.NewClone:
            case PrefabEditTrigger.EditorsChanged:
            case PrefabEditTrigger.Rehydrate:
                return true;
            case PrefabEditTrigger.InputsChanged:
            case PrefabEditTrigger.CollectionReady:
                if (string.IsNullOrEmpty(currentFingerprint)) return false;
                return !string.Equals(appliedFingerprint, currentFingerprint, StringComparison.Ordinal);
            default:
                return false;
        }
    }

    /// <summary>
    /// Stable id of an ordered editor set for one prefab: plugin id, priority, registration
    /// index, and optional <see cref="PrefabEditSpec.Version"/> per row, plus an invalidation
    /// generation. Used to skip a <see cref="PrefabEditTrigger.CollectionReady"/> pass that
    /// would write the same values again. Plugin id + priority + index alone does not see a
    /// closure whose captured values changed — callers must <c>Invalidate</c> (bumps
    /// <paramref name="generation"/>) or pass a new version token on <c>Edit</c>.
    /// </summary>
    public static string Fingerprint(PrefabEditRegistration[]? ordered, int generation = 0)
    {
        if (ordered == null || ordered.Length == 0)
            return generation == 0 ? "" : "#" + generation.ToString();
        var sb = new StringBuilder();
        for (int i = 0; i < ordered.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            var spec = ordered[i].Spec;
            sb.Append(spec.PluginId).Append('\0').Append(spec.Priority).Append('\0').Append(spec.Index);
            if (!string.IsNullOrEmpty(spec.Version))
                sb.Append('\0').Append(spec.Version);
        }
        if (generation != 0)
            sb.Append("\n#").Append(generation);
        return sb.ToString();
    }
}
