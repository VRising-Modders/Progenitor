using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// FINJ1: one reload-folder plugin Bootstrap keeps for the life of the process because it registered
/// its own IL2CPP types. Parsed from <c>Progenitor.Bootstrap.Il2CppInjectionPolicy.FormatRow</c>
/// (<c>file \t guid|guid \t name|name \t 0/1 \t pending \t site|site</c>); Core cannot reference
/// Bootstrap, so this is a mirror and <c>Il2CppInjectionPolicyTests</c> pins the two together.
/// </summary>
internal sealed record ProcessLifetimeRow(
    string File, string[] Guids, string[] Names, bool CoreBound, string Pending, string[] Sites)
{
    internal const string PendingChanged = "changed";
    internal const string PendingRemoved = "removed";

    internal static bool TryParse(string? text, out ProcessLifetimeRow? row)
    {
        row = null;
        if (string.IsNullOrEmpty(text)) return false;
        var parts = text!.Split('\t');
        if (parts.Length < 6 || parts[0].Length == 0) return false;
        row = new ProcessLifetimeRow(parts[0], Split(parts[1]), Split(parts[2]), parts[3] == "1", parts[4], Split(parts[5]));
        return true;
    }

    private static string[] Split(string s) =>
        s.Length == 0 ? Array.Empty<string>() : s.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// FINJ1: the Mods tab's status for plugins that register their own IL2CPP types. Such a plugin is
/// never unloaded or reloaded while the game runs, so a new build (or a removal) waits for a restart.
/// A pending change is a warning ("Restart required"); with nothing pending the row only says that
/// updates need a restart. Read live from Bootstrap on every call, so it clears after the restart.
/// </summary>
internal sealed class ProcessLifetimeSource : IModProblemSource
{
    internal const string ListLineRestart = "Restart required";
    internal const string ListLineNote = "Updates need a restart";
    internal const string ChangedSentence =
        "A new version is installed, but this mod adds its own game types, which cannot be replaced while the game runs.";
    internal const string RemovedSentence =
        "It was removed, but this mod adds its own game types, which cannot be removed while the game runs; it keeps running.";
    internal const string NoteSentence =
        "This mod adds its own game types, so it is not reloaded while the game runs; updates take effect after a restart.";
    internal const string RestartWhatToDo = "Restart the game to finish.";

    private readonly Func<string[]> _rows;

    internal ProcessLifetimeSource(Func<string[]>? rows = null)
    {
#if PROGENITOR_TESTS
        _rows = rows ?? (() => Array.Empty<string>());
#else
        _rows = rows ?? BootstrapLink.ProcessLifetimePluginRows;
#endif
    }

    public string Name => "process-lifetime";

    public IEnumerable<ModProblem> Current()
    {
        string[] raw;
        try { raw = _rows() ?? Array.Empty<string>(); }
        catch { raw = Array.Empty<string>(); }
        foreach (var text in raw)
        {
            if (!ProcessLifetimeRow.TryParse(text, out var row) || row == null) continue;
            bool changed = row.Pending == ProcessLifetimeRow.PendingChanged;
            bool removed = row.Pending == ProcessLifetimeRow.PendingRemoved;
            string details = "Reload: " + row.File + " registers its own IL2CPP types ("
                + string.Join(", ", row.Sites) + ")" + (row.CoreBound ? "; it references Progenitor, so Core swaps are refused" : "")
                + (row.Pending.Length > 0 ? "; pending: " + row.Pending : "");
            foreach (var guid in row.Guids)
            {
                if (string.IsNullOrWhiteSpace(guid)) continue;
                yield return changed || removed
                    ? new ModProblem(ModProblemSeverity.Warning, ModProblemKinds.RestartRequired, guid,
                        changed ? ChangedSentence : RemovedSentence, RestartWhatToDo, details, key: row.File, listLine: ListLineRestart)
                    : new ModProblem(ModProblemSeverity.Info, ModProblemKinds.RestartRequired, guid,
                        NoteSentence, "", details, key: row.File, listLine: ListLineNote);
            }
        }
    }
}

/// <summary>
/// FORD1: a reload-folder DLL Bootstrap did not load because a dependency is missing or did not load.
/// Parsed from <c>Progenitor.Bootstrap.PluginLoadOrder.FormatWaitingRow</c> (<c>file \t guid|guid \t
/// waitingFor \t reason</c>); <c>PluginLoadOrderTests</c> pins the two together.
/// </summary>
internal sealed record WaitingPluginRow(string File, string[] Guids, string WaitingFor, string Reason)
{
    internal static bool TryParse(string? text, out WaitingPluginRow? row)
    {
        row = null;
        if (string.IsNullOrEmpty(text)) return false;
        var parts = text!.Split('\t');
        if (parts.Length < 4 || parts[0].Length == 0) return false;
        var guids = parts[1].Length == 0 ? Array.Empty<string>() : parts[1].Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        row = new WaitingPluginRow(parts[0], guids, parts[2], parts[3]);
        return true;
    }
}

/// <summary>
/// FORD1: the Mods tab's status for a reload-folder mod that is waiting for a dependency. A warning: the
/// mod is installed but none of it is running. It clears by itself once the dependency loads.
/// </summary>
internal sealed class WaitingPluginSource : IModProblemSource
{
    internal const string ListLine = "Waiting for a dependency";
    internal const string WhatToDo = "Install the mod it needs; it loads by itself once that mod is there.";

    internal static string Sentence(string waitingFor) =>
        "It is not running: it needs " + waitingFor + ", which is not installed or did not load.";

    private readonly Func<string[]> _rows;

    internal WaitingPluginSource(Func<string[]>? rows = null)
    {
#if PROGENITOR_TESTS
        _rows = rows ?? (() => Array.Empty<string>());
#else
        _rows = rows ?? BootstrapLink.WaitingPluginRows;
#endif
    }

    public string Name => "waiting-for-dependency";

    public IEnumerable<ModProblem> Current()
    {
        string[] raw;
        try { raw = _rows() ?? Array.Empty<string>(); }
        catch { raw = Array.Empty<string>(); }
        foreach (var text in raw)
        {
            if (!WaitingPluginRow.TryParse(text, out var row) || row == null) continue;
            foreach (var guid in row.Guids)
            {
                if (string.IsNullOrWhiteSpace(guid)) continue;
                yield return new ModProblem(ModProblemSeverity.Warning, ModProblemKinds.DependencyMissing, guid,
                    Sentence(row.WaitingFor), WhatToDo, row.Reason, row.WaitingFor, key: row.File, listLine: ListLine);
            }
        }
    }
}
