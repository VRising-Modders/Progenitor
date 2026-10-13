using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Bootstrap;

/// <summary>
/// One plugin inside a suspended reload-folder DLL: identity only, never the instance, its type or
/// its assembly (a retained reference would pin the collectible ALC the suspend just released).
/// </summary>
internal sealed class SuspendedPlugin
{
    internal SuspendedPlugin(string guid, string name, string version, IReadOnlyList<string>? rows = null,
        IReadOnlyList<string>? hardRows = null)
    {
        Guid = guid ?? "";
        Name = string.IsNullOrEmpty(name) ? Guid : name;
        Version = version ?? "";
        Rows = rows ?? Array.Empty<string>();
        HardRows = hardRows ?? Array.Empty<string>();
    }

    /// <summary>
    /// The subset of <see cref="Rows"/> declared <c>ServerMustHave</c>: the rows that make the plugin
    /// fail a join on a server that does not run it. Lets Core keep a still-resuming plugin paused
    /// on such a server, even after a Core swap.
    /// </summary>
    internal IReadOnlyList<string> HardRows { get; set; }

    internal string Guid { get; }
    internal string Name { get; }
    internal string Version { get; }

    /// <summary>
    /// The handshake row ids this plugin provided when it was paused, recorded by Core at the
    /// suspend. Kept here, not in Core, so a Core swap can still resume the plugin by any of them.
    /// </summary>
    internal IReadOnlyList<string> Rows { get; set; }

    /// <summary>
    /// <c>guid\tname\tversion\tstate\trow,row\thardrow,hardrow</c>: the row shape
    /// <c>ProcessHost.SuspendedPlugins</c> hands Core. <c>state</c> is <c>paused</c> or
    /// <c>resuming</c> (released, not loaded yet).
    /// </summary>
    internal string ToRow(bool resuming) =>
        Clean(Guid) + "\t" + Clean(Name) + "\t" + Clean(Version) + "\t"
        + (resuming ? PluginSuspendPolicy.StateResuming : PluginSuspendPolicy.StatePaused) + "\t"
        + Clean(string.Join(",", Rows)) + "\t" + Clean(string.Join(",", HardRows));

    private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}

/// <summary>
/// One suspended reload-folder DLL (per-server mod pause). The DLL is the unit: one file may hold
/// several plugins and they are suspended and resumed together.
/// </summary>
internal sealed class SuspendedUnit
{
    internal SuspendedUnit(string path, string stem, string imageSha, DateTime lastWrite,
        IReadOnlyList<SuspendedPlugin> plugins, string[]? dependencies = null)
    {
        Dependencies = dependencies ?? Array.Empty<string>();
        Path = path ?? "";
        Stem = stem ?? "";
        ImageSha = imageSha ?? "";
        LastWrite = lastWrite;
        Plugins = plugins ?? Array.Empty<SuspendedPlugin>();
    }

    internal string Path { get; }
    internal string Stem { get; }

    /// <summary>SHA-256 of the image that was loaded when the unit was suspended ("" for a rewritten image).</summary>
    internal string ImageSha { get; }

    /// <summary>The newest write stamp seen for the file. A redeploy while suspended updates it and does not load.</summary>
    internal DateTime LastWrite { get; set; }

    /// <summary>True once the file changed on disk after the suspend; the resume then loads the new bytes.</summary>
    internal bool ChangedWhileSuspended { get; set; }

    internal IReadOnlyList<SuspendedPlugin> Plugins { get; }

    /// <summary>
    /// Released from the pause but not loaded yet (its previous plugin ALC has not collected, or
    /// the file is still being written). Every scan retries it; it is never marked Failed for that.
    /// </summary>
    internal bool Resuming { get; set; }

    /// <summary>Collect pulses already spent on this resume (bounded by <see cref="PluginSuspendPolicy.MaxCollectPulses"/>).</summary>
    internal int CollectPulses { get; set; }

    /// <summary>The last pending-resume line logged for this unit, so a waiting unit logs once per change, not every scan.</summary>
    internal string LastNote { get; set; } = "";

    /// <summary>The union of its plugins' <c>[BepInDependency]</c> GUIDs, kept so a resume brings dependents back too.</summary>
    internal string[] Dependencies { get; }

    internal bool Contains(string? guid)
    {
        if (string.IsNullOrEmpty(guid)) return false;
        for (int i = 0; i < Plugins.Count; i++)
            if (string.Equals(Plugins[i].Guid, guid, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>
/// Game-free description of one loaded reload-folder DLL, for the suspend closure. Built by
/// <c>ReloaderHost</c> from its <c>Loaded</c> entries; the tests build it directly.
/// </summary>
internal readonly struct SuspendUnitNode
{
    internal SuspendUnitNode(string path, string[]? guids, string[]? dependencies, bool unsafeHarmony, bool injectsIl2CppTypes = false)
    {
        Path = path ?? "";
        Guids = guids ?? Array.Empty<string>();
        Dependencies = dependencies ?? Array.Empty<string>();
        UnsafeHarmony = unsafeHarmony;
        InjectsIl2CppTypes = injectsIl2CppTypes;
    }

    internal string Path { get; }

    /// <summary>The BepInPlugin GUIDs of every plugin in this DLL.</summary>
    internal string[] Guids { get; }

    /// <summary>The union of every plugin's <c>[BepInDependency]</c> GUIDs.</summary>
    internal string[] Dependencies { get; }

    /// <summary>A plugin in this DLL took <c>ctx.UnsafeHarmony</c>; it can only go away with a restart.</summary>
    internal bool UnsafeHarmony { get; }

    /// <summary>Job FINJ1: a plugin in this DLL registers its own IL2CPP types; it can only go away with a restart.</summary>
    internal bool InjectsIl2CppTypes { get; }
}

/// <summary>
/// The pure half of per-server mod pause in Bootstrap: which DLLs a suspend request covers, and the
/// log sentences. <c>ReloaderHost</c> owns the side effects.
/// </summary>
internal static class PluginSuspendPolicy
{
    internal const string StatePaused = "paused";
    internal const string StateResuming = "resuming";

    /// <summary>At most this many blocking collect pulses per resume, one per scan at most.</summary>
    internal const int MaxCollectPulses = 5;

    /// <summary>One step of a pending resume, decided from facts only.</summary>
    internal enum ResumeStep
    {
        /// <summary>The file is gone: forget the pause.</summary>
        Forget = 0,
        /// <summary>Not yet: the file is inside the settle window, or the old ALC is alive and the pulses are spent.</summary>
        Wait = 1,
        /// <summary>The old ALC is alive: run one collect pulse, then look again.</summary>
        PulseThenRetry = 2,
        /// <summary>Load it now.</summary>
        Load = 3,
    }

    internal static ResumeStep DecideResume(bool fileExists, bool settled, bool alcBlocked, int pulsesUsed) =>
        DecideResume(fileExists, settled, alcBlocked, pulsesUsed, dependencyPending: false);

    /// <summary>Same, and a dependency that has not loaded yet makes the unit wait (no pulse spent).</summary>
    internal static ResumeStep DecideResume(bool fileExists, bool settled, bool alcBlocked, int pulsesUsed, bool dependencyPending)
    {
        if (!fileExists) return ResumeStep.Forget;
        if (dependencyPending) return ResumeStep.Wait;
        if (!settled) return ResumeStep.Wait;
        if (!alcBlocked) return ResumeStep.Load;
        return pulsesUsed < MaxCollectPulses ? ResumeStep.PulseThenRetry : ResumeStep.Wait;
    }

    /// <summary>
    /// Load order for units being resumed: what a unit depends on first, dependents after. Ties and
    /// cycles keep the given order.
    /// </summary>
    internal static List<SuspendedUnit> DependenciesFirst(IReadOnlyList<SuspendedUnit> units)
    {
        var left = new List<SuspendedUnit>(units ?? Array.Empty<SuspendedUnit>());
        var ordered = new List<SuspendedUnit>(left.Count);
        while (left.Count > 0)
        {
            int pick = -1;
            for (int i = 0; i < left.Count && pick < 0; i++)
            {
                bool ready = true;
                foreach (var dep in left[i].Dependencies)
                {
                    for (int j = 0; j < left.Count && ready; j++)
                        if (j != i && left[j].Contains(dep)) ready = false;
                    if (!ready) break;
                }
                if (ready) pick = i;
            }
            if (pick < 0) pick = 0; // cycle: keep the given order
            ordered.Add(left[pick]);
            left.RemoveAt(pick);
        }
        return ordered;
    }
    /// <summary>
    /// The DLLs to suspend for <paramref name="requested"/>: every DLL holding a requested GUID,
    /// then, to a fixpoint, every DLL that depends on a GUID already selected (a dependent cannot
    /// keep running once what it depends on is gone). All-or-nothing: a requested GUID no reload
    /// DLL holds (a plain BepInEx plugin, loaded from <c>BepInEx/plugins</c>) or a selected DLL that
    /// took <c>ctx.UnsafeHarmony</c> refuses the whole request, because a partial pause is not the
    /// set the caller predicted a clean join from.
    /// </summary>
    internal static bool TryClosure(
        IReadOnlyList<SuspendUnitNode>? units,
        IEnumerable<string>? requested,
        out List<int> selected,
        out string refusal)
    {
        selected = new List<int>();
        refusal = "";
        var list = units ?? Array.Empty<SuspendUnitNode>();
        var chosen = new bool[list.Count];
        var want = new List<string>();
        if (requested != null)
            foreach (var g in requested)
                if (!string.IsNullOrWhiteSpace(g) && !want.Exists(w => string.Equals(w, g, StringComparison.OrdinalIgnoreCase)))
                    want.Add(g.Trim());
        if (want.Count == 0)
        {
            refusal = "nothing was asked for";
            return false;
        }

        foreach (var g in want)
        {
            int found = -1;
            for (int i = 0; i < list.Count && found < 0; i++)
                if (Holds(list[i], g)) found = i;
            if (found < 0)
            {
                refusal = g + " is not a hot-loaded plugin (it was loaded at game start); only a restart without it removes it";
                return false;
            }
            chosen[found] = true;
        }

        bool grew = true;
        while (grew)
        {
            grew = false;
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < list.Count; i++)
                if (chosen[i]) foreach (var g in list[i].Guids) if (!string.IsNullOrEmpty(g)) guids.Add(g);
            for (int i = 0; i < list.Count; i++)
            {
                if (chosen[i]) continue;
                foreach (var dep in list[i].Dependencies)
                {
                    if (string.IsNullOrEmpty(dep) || !guids.Contains(dep)) continue;
                    chosen[i] = true;
                    grew = true;
                    break;
                }
            }
        }

        for (int i = 0; i < list.Count; i++)
        {
            if (!chosen[i]) continue;
            if (list[i].UnsafeHarmony)
            {
                refusal = FirstGuid(list[i]) + " uses ctx.UnsafeHarmony and cannot be paused without a restart";
                selected.Clear();
                return false;
            }
            if (list[i].InjectsIl2CppTypes)
            {
                refusal = Il2CppInjectionPolicy.FormatSuspendRefused(FirstGuid(list[i]));
                selected.Clear();
                return false;
            }
            selected.Add(i);
        }
        return true;
    }

    /// <summary>Every GUID of the selected DLLs, in selection order.</summary>
    internal static List<string> GuidsOf(IReadOnlyList<SuspendUnitNode> units, IReadOnlyList<int> selected)
    {
        var result = new List<string>();
        foreach (int i in selected)
            foreach (var g in units[i].Guids)
                if (!string.IsNullOrEmpty(g) && !result.Exists(r => string.Equals(r, g, StringComparison.OrdinalIgnoreCase)))
                    result.Add(g);
        return result;
    }

    /// <summary>
    /// <c>Reload: suspended Foo.dll (example.foo) (unload reason=Suspend).</c> F7 (live 2026-10-07): the same suspend serves
    /// the per-server pause and the Mods list's switch, so the line names no context (Core logs the caller's).
    /// </summary>
    internal static string FormatSuspended(string fileName, IEnumerable<string> guids) =>
        "Reload: suspended " + fileName + " (" + Join(guids) + ") (unload reason="
        + PluginUnloadSlot.Name(PluginUnloadSlot.Suspend) + ").";

    internal static string FormatRefused(string refusal) =>
        "Reload: suspend refused; nothing was paused: " + refusal + ".";

    internal static string FormatResumed(string fileName, bool changed) =>
        "Reload: resuming " + fileName + (changed ? " (the file changed while paused; loading the new build)." : ".");

    internal static string FormatResumePending(string fileName) => FormatResumePending(fileName, settled: true, pulses: 0);

    /// <summary>The pending-resume line; its text changes only with the state, so it is logged once per change.</summary>
    internal static string FormatResumeWaitingOnDependency(string fileName) =>
        "Reload: " + fileName + " is released from pause but waits for a mod it depends on to load first.";

    internal static string FormatResumePending(string fileName, bool settled, int pulses) =>
        !settled
            ? "Reload: " + fileName + " is released from pause and is still being written; it loads once it settles."
            : pulses >= MaxCollectPulses
                ? "Reload: " + fileName + " is released from pause but its previous plugin ALC is still alive after "
                  + MaxCollectPulses + " collect pulses; every scan keeps trying without pulsing."
                : "Reload: " + fileName + " is released from pause but its previous plugin ALC has not collected yet; "
                  + "the next scan loads it.";

    internal static string FormatSkipped(string fileName) =>
        "Reload: " + fileName + " changed while paused; it loads when the pause ends.";

    internal static string FormatDeletedWhilePaused(string fileName) =>
        "Reload: " + fileName + " was removed while paused; forgetting the pause.";

    private static bool Holds(SuspendUnitNode unit, string guid)
    {
        foreach (var g in unit.Guids)
            if (string.Equals(g, guid, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string FirstGuid(SuspendUnitNode unit) =>
        unit.Guids.Length > 0 ? unit.Guids[0] : System.IO.Path.GetFileName(unit.Path);

    private static string Join(IEnumerable<string> items)
    {
        var sb = new StringBuilder();
        foreach (var s in items)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(s);
        }
        return sb.ToString();
    }
}

/// <summary>
/// The suspended DLLs, keyed by full path. Lives in Bootstrap so a Core swap cannot forget it: the
/// next Core generation rediscovers the set through <c>ProcessHost.SuspendedPlugins</c>. Game-free.
/// Main thread only, like the rest of the reloader.
/// </summary>
internal sealed class PluginSuspendTable
{
    private readonly Dictionary<string, SuspendedUnit> _byPath = new(StringComparer.OrdinalIgnoreCase);

    internal int Count => _byPath.Count;

    internal bool IsSuspended(string? path) => !string.IsNullOrEmpty(path) && _byPath.ContainsKey(path!);

    internal bool TryGet(string path, out SuspendedUnit unit) => _byPath.TryGetValue(path, out unit!);

    internal void Add(SuspendedUnit unit)
    {
        if (unit == null || unit.Path.Length == 0) return;
        _byPath[unit.Path] = unit;
    }

    internal void Remove(string path) => _byPath.Remove(path);

    /// <summary>
    /// The scan's question for a file it found on disk. True means "leave it alone": the file is
    /// paused, whether or not it changed. A changed stamp is recorded (and reported once through
    /// <paramref name="changedNow"/>) so the resume loads the new build. A unit that is resuming is
    /// not skipped here: the scan steps its resume instead.
    /// </summary>
    internal bool SkipInScan(string path, DateTime write, out bool changedNow)
    {
        changedNow = false;
        if (!_byPath.TryGetValue(path, out var unit) || unit.Resuming) return false;
        if (unit.LastWrite != write)
        {
            unit.LastWrite = write;
            unit.ChangedWhileSuspended = true;
            changedNow = true;
        }
        return true;
    }

    /// <summary>Forget every paused or resuming file that is no longer on disk. Deleting a paused mod ends its pause.</summary>
    internal List<SuspendedUnit> DropMissing(ISet<string> seen)
    {
        var gone = new List<SuspendedUnit>();
        foreach (var kv in _byPath)
            if (seen == null || !seen.Contains(kv.Key)) gone.Add(kv.Value);
        foreach (var unit in gone) _byPath.Remove(unit.Path);
        return gone;
    }

    /// <summary>
    /// Mark for resume every unit holding one of <paramref name="guids"/> (null: every unit), then,
    /// to a fixpoint, every paused unit those depend on (a plugin cannot load without its
    /// dependency) and every paused unit that depends on them (it was paused only because they
    /// were). The units stay in the table as <see cref="SuspendedUnit.Resuming"/> until they
    /// actually load. Returned in load order: dependencies first.
    /// </summary>
    internal List<SuspendedUnit> BeginResume(IEnumerable<string>? guids)
    {
        var taken = new List<SuspendedUnit>();
        if (guids == null) taken.AddRange(_byPath.Values);
        else
        {
            var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in guids) if (!string.IsNullOrEmpty(g)) want.Add(g);
            foreach (var unit in _byPath.Values)
                foreach (var p in unit.Plugins)
                    if (want.Contains(p.Guid)) { taken.Add(unit); break; }
        }

        bool grew = taken.Count > 0;
        while (grew)
        {
            grew = false;
            var back = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var needs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in taken)
            {
                foreach (var p in unit.Plugins) back.Add(p.Guid);
                foreach (var d in unit.Dependencies) needs.Add(d);
            }
            foreach (var unit in _byPath.Values)
            {
                if (taken.Contains(unit)) continue;
                bool add = false;
                foreach (var p in unit.Plugins) if (needs.Contains(p.Guid)) { add = true; break; }
                if (!add) foreach (var dep in unit.Dependencies) if (back.Contains(dep)) { add = true; break; }
                if (!add) continue;
                taken.Add(unit);
                grew = true;
            }
        }

        foreach (var unit in taken) unit.Resuming = true;
        return PluginSuspendPolicy.DependenciesFirst(taken);
    }

    /// <summary>
    /// A new pause for a GUID whose unit is only resuming (not loaded yet): it simply stays paused.
    /// Returns the GUIDs of every unit flipped back.
    /// </summary>
    internal List<string> CancelResume(IEnumerable<string>? guids)
    {
        var back = new List<string>();
        if (guids == null) return back;
        var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in guids) if (!string.IsNullOrEmpty(g)) want.Add(g);
        foreach (var unit in _byPath.Values)
        {
            if (!unit.Resuming) continue;
            bool hit = false;
            foreach (var p in unit.Plugins) if (want.Contains(p.Guid)) { hit = true; break; }
            if (!hit) continue;
            KeepPaused(unit, back);
        }

        // Dependent closure: a resuming unit that depends on anything still paused (the units just
        // kept, or any other paused unit) cannot load either, whatever its own rows declare.
        bool grew = back.Count > 0;
        while (grew)
        {
            grew = false;
            var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in _byPath.Values)
                if (!unit.Resuming) foreach (var p in unit.Plugins) held.Add(p.Guid);
            foreach (var unit in _byPath.Values)
            {
                if (!unit.Resuming) continue;
                foreach (var dep in unit.Dependencies)
                {
                    if (!held.Contains(dep)) continue;
                    KeepPaused(unit, back);
                    grew = true;
                    break;
                }
            }
        }
        return back;
    }

    private static void KeepPaused(SuspendedUnit unit, List<string> back)
    {
        unit.Resuming = false;
        unit.CollectPulses = 0;
        unit.LastNote = "";
        foreach (var p in unit.Plugins) back.Add(p.Guid);
    }

    /// <summary>
    /// True while a unit this one depends on is still in the table (paused, or resuming and not
    /// loaded yet). The dependent waits: ordering dependencies first is not enough when a dependency
    /// is blocked on its old plugin ALC.
    /// </summary>
    internal bool WaitsOnDependency(SuspendedUnit unit)
    {
        if (unit == null) return false;
        foreach (var dep in unit.Dependencies)
            foreach (var other in _byPath.Values)
                if (!ReferenceEquals(other, unit) && other.Contains(dep)) return true;
        return false;
    }

    /// <summary>Resuming units in load order (dependencies first), for the scan.</summary>
    internal List<SuspendedUnit> ResumingInOrder()
    {
        var list = new List<SuspendedUnit>();
        foreach (var unit in _byPath.Values) if (unit.Resuming) list.Add(unit);
        return PluginSuspendPolicy.DependenciesFirst(list);
    }

    /// <summary>Every plugin row for Core: <c>guid\tname\tversion\tstate\trows\thardrows</c>.</summary>
    internal List<string> Rows()
    {
        var rows = new List<string>();
        foreach (var unit in _byPath.Values)
            foreach (var p in unit.Plugins) rows.Add(p.ToRow(unit.Resuming));
        return rows;
    }

    /// <summary>Every suspended plugin, in no particular order.</summary>
    internal List<SuspendedPlugin> Plugins()
    {
        var list = new List<SuspendedPlugin>();
        foreach (var unit in _byPath.Values) list.AddRange(unit.Plugins);
        return list;
    }

    internal List<SuspendedUnit> Units() => new(_byPath.Values);

    internal void Clear() => _byPath.Clear();
}
