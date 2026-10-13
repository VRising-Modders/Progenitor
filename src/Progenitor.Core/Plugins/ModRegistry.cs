using System;
using System.Collections.Generic;

namespace Progenitor.Plugins;

/// <summary>What the last load of a Progenitor plugin did.</summary>
internal enum ModLoadOutcome
{
    /// <summary><c>Load</c> is running (between the side check and the end of <c>OnLoad</c>).</summary>
    Loading = 0,

    /// <summary><c>OnLoad</c> returned.</summary>
    Loaded = 1,

    /// <summary><c>OnLoad</c> threw and its registrations were undone.</summary>
    Failed = 2,

    /// <summary>The player turned it off (or something it needs is off), so <c>OnLoad</c> was not run.</summary>
    SkippedOff = 3,

    /// <summary>Unloaded (any reason).</summary>
    Unloaded = 4,

    /// <summary>Dormant on this side (<c>[ProgenitorSide]</c>).</summary>
    Dormant = 5,
}

/// <summary>One Progenitor plugin as its last <c>Load</c> saw it. Plain strings only.</summary>
internal sealed class ModRegistryEntry
{
    internal string Id { get; init; } = "";
    internal string Name { get; init; } = "";
    internal string Version { get; init; } = "";

    /// <summary>The GUIDs of its hard <c>[BepInDependency]</c>s.</summary>
    internal IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();

    internal CounterpartDeclaration? Counterpart { get; init; }

    /// <summary>
    /// Mod compatibility C3: what its <c>[NetworkCompatibility]</c> says about peer versions of itself, or null when it
    /// has no such attribute. <see cref="CompatDeclaration.EffectiveRange"/> is the range the join evaluation uses.
    /// </summary>
    internal CompatDeclaration? Compat { get; init; }

    internal ModLoadOutcome Outcome { get; set; }

    /// <summary>Why it was skipped (<see cref="ModLoadOutcome.SkippedOff"/>); empty otherwise.</summary>
    internal string OffReason { get; set; } = "";

    /// <summary>
    /// Skipped because this hard dependency is off (not because the player turned this one off);
    /// empty otherwise. The switch then says so and cannot turn it on by itself.
    /// </summary>
    internal string OffDependency { get; set; } = "";

    /// <summary>
    /// True while its features run: loaded, or loading. A failed <c>OnLoad</c> undid every
    /// registration, so a failed plugin is not running (its code is in memory, nothing of it acts).
    /// The one rule the switch, the dependants list and the list's diamonds all read.
    /// </summary>
    internal bool Running => Outcome == ModLoadOutcome.Loaded || Outcome == ModLoadOutcome.Loading;

    /// <summary>Filled from what was left in memory after a Core swap, not from a <c>Load</c> this generation saw.</summary>
    internal bool Rehydrated { get; init; }
}

/// <summary>
/// SETTINGS1 stage 3: every Progenitor plugin this Core generation has seen load, with what the
/// load did. Filled by <c>ProgenitorPlugin.Load</c> and <c>Unload</c>; read by the Mods menu (the
/// switch, dependants, counterparts). Holds strings only — never a plugin instance, type or
/// assembly — so it pins no collectible generation. A Core swap starts it empty and the plugins'
/// reload fills it again.
/// </summary>
internal static class ModRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ModRegistryEntry> ById = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised (outside the lock) after any entry changed. The Mods menu refreshes on it.</summary>
    internal static event Action<string>? Changed;

    internal static void NoteLoading(
        string id, string name, string version, IReadOnlyList<string>? dependencies, CounterpartDeclaration? counterpart,
        CompatDeclaration? compat = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var entry = new ModRegistryEntry
        {
            Id = id.Trim(),
            Name = string.IsNullOrWhiteSpace(name) ? id.Trim() : name.Trim(),
            Version = (version ?? "").Trim(),
            Dependencies = Copy(dependencies),
            Counterpart = counterpart,
            Compat = compat,
            Outcome = ModLoadOutcome.Loading,
        };
        lock (Gate) ById[entry.Id] = entry;
        Raise(entry.Id);
    }

    internal static void NoteLoaded(string id) => Set(id, ModLoadOutcome.Loaded, "");

    internal static void NoteFailed(string id) => Set(id, ModLoadOutcome.Failed, "");

    internal static void NoteSkipped(string id, string reason, string dependency = "")
    {
        Set(id, ModLoadOutcome.SkippedOff, reason ?? "");
        lock (Gate)
            if (ById.TryGetValue((id ?? "").Trim(), out var entry)) entry.OffDependency = (dependency ?? "").Trim();
    }

    /// <summary>
    /// After a Core swap: a Progenitor plugin the chainloader loaded at game start does not run
    /// <c>Load</c> again, so this generation never saw it. The Mods menu records what the swap left in
    /// memory (<paramref name="outcome"/> read from the old instance), once, if nothing else did.
    /// </summary>
    internal static bool NoteRehydrated(
        string id, string name, string version, IReadOnlyList<string>? dependencies, CounterpartDeclaration? counterpart,
        ModLoadOutcome outcome, string offDependency = "", CompatDeclaration? compat = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (Gate)
        {
            if (ById.ContainsKey(id.Trim())) return false;
            ById[id.Trim()] = new ModRegistryEntry
            {
                Id = id.Trim(),
                Name = string.IsNullOrWhiteSpace(name) ? id.Trim() : name.Trim(),
                Version = (version ?? "").Trim(),
                Dependencies = Copy(dependencies),
                Counterpart = counterpart,
                Compat = compat,
                Outcome = outcome,
                OffDependency = outcome == ModLoadOutcome.SkippedOff ? (offDependency ?? "").Trim() : "",
                Rehydrated = true,
            };
        }

        Raise(id.Trim());
        return true;
    }

    internal static void NoteDormant(string id) => Set(id, ModLoadOutcome.Dormant, "");

    internal static void NoteUnloaded(string id) => Set(id, ModLoadOutcome.Unloaded, "");

    internal static bool TryGet(string? id, out ModRegistryEntry entry)
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (Gate)
        {
            if (!ById.TryGetValue(id!.Trim(), out var found)) return false;
            entry = found;
            return true;
        }
    }

    /// <summary>True when this process skipped the plugin's <c>OnLoad</c> because it is turned off.</summary>
    internal static bool IsSkipped(string? id) => TryGet(id, out var e) && e.Outcome == ModLoadOutcome.SkippedOff;

    /// <summary>A snapshot of every entry.</summary>
    internal static List<ModRegistryEntry> All()
    {
        lock (Gate) return new List<ModRegistryEntry>(ById.Values);
    }

    internal static CounterpartDeclaration? CounterpartOf(string? id) => TryGet(id, out var e) ? e.Counterpart : null;

    /// <summary>
    /// The plugins that depend on <paramref name="id"/> (hard <c>[BepInDependency]</c>),
    /// transitively, that are running now. <paramref name="id"/> itself is never in the result.
    /// Order: nearest first, then by name.
    /// </summary>
    internal static List<ModRegistryEntry> RunningDependants(string? id)
    {
        var result = new List<ModRegistryEntry>();
        if (string.IsNullOrWhiteSpace(id)) return result;
        List<ModRegistryEntry> all;
        lock (Gate) all = new List<ModRegistryEntry>(ById.Values);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id!.Trim() };
        var frontier = new List<string> { id!.Trim() };
        while (frontier.Count > 0)
        {
            var next = new List<string>();
            var layer = new List<ModRegistryEntry>();
            foreach (var entry in all)
            {
                if (seen.Contains(entry.Id) || !entry.Running) continue;
                foreach (var dep in entry.Dependencies)
                {
                    if (!frontier.Exists(f => string.Equals(f, dep, StringComparison.OrdinalIgnoreCase))) continue;
                    layer.Add(entry);
                    break;
                }
            }

            layer.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            foreach (var entry in layer)
            {
                if (!seen.Add(entry.Id)) continue;
                result.Add(entry);
                next.Add(entry.Id);
            }

            frontier = next;
        }

        return result;
    }

    private static void Set(string id, ModLoadOutcome outcome, string reason)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (Gate)
        {
            if (!ById.TryGetValue(id.Trim(), out var entry)) return;
            entry.Outcome = outcome;
            entry.OffReason = reason;
        }

        Raise(id.Trim());
    }

    private static void Raise(string id)
    {
        try { Changed?.Invoke(id); }
        catch { /* a listener must never cost a plugin load */ }
    }

    private static IReadOnlyList<string> Copy(IReadOnlyList<string>? list)
    {
        var copy = new List<string>();
        if (list == null) return copy;
        foreach (var s in list)
            if (!string.IsNullOrWhiteSpace(s)) copy.Add(s.Trim());
        return copy;
    }

    internal static void ResetForTests()
    {
        lock (Gate) ById.Clear();
        Changed = null;
    }
}
