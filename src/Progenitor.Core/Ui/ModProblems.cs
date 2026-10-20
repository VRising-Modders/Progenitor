using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Ui;

/// <summary>How bad a mod problem is. Ordered: a higher value is worse.</summary>
internal enum ModProblemSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>
/// The kinds of problem the framework reports today. A string, not an enum, so a later source (the
/// M4.7 health check) adds its own kinds without changing this file.
/// </summary>
internal static class ModProblemKinds
{
    /// <summary><c>OnLoad</c> threw; the registrations were undone. Critical.</summary>
    internal const string LoadFailed = "load-failed";

    /// <summary>The localization files next to the DLL failed to load. Warning.</summary>
    internal const string LocalizationFailed = "localization-failed";

    /// <summary>The JSON content files next to the DLL failed to load. Warning.</summary>
    internal const string ContentFailed = "content-failed";

    /// <summary>KEYS1: a mod key lost its chord to the game or to a mod installed first. Warning, a conflict.</summary>
    internal const string KeybindConflict = "keybind-conflict";

    /// <summary>KEYS2: a mod key on a chord Windows or Steam takes first (refused, or an ordinary mod's that cannot be stopped). Warning.</summary>
    internal const string KeybindReserved = "keybind-reserved";

    /// <summary>Two plugins registered the same chat command; the short form is disabled. Warning, a conflict.</summary>
    internal const string CommandConflict = "command-conflict";

    /// <summary>An ordinary plugin hard-depends on a mod the player turned off. Warning.</summary>
    internal const string DependencyOff = "dependency-off";

    /// <summary>The preloader could not carry out a next-start request (turn off, turn on, uninstall). Warning.</summary>
    internal const string StartActionFailed = "start-action-failed";

    /// <summary>FINJ1: the mod registers its own IL2CPP types and is kept until restart; Warning when a change waits for it, else Info.</summary>
    internal const string RestartRequired = "restart-required";

    /// <summary>FORD1: a reload-folder mod is not loaded because a dependency is missing or did not load. Warning.</summary>
    internal const string DependencyMissing = "dependency-missing";

    internal static bool IsConflict(string? kind) =>
        string.Equals(kind, KeybindConflict, StringComparison.Ordinal)
        || string.Equals(kind, CommandConflict, StringComparison.Ordinal);
}

/// <summary>
/// SETTINGS1 stage 3: one mod problem. Every source — a failed load, a file that did not load, a
/// clash with another mod, and later the M4.7 health check — produces this one record, so one UI
/// draws them all. Plain strings only.
/// </summary>
internal sealed class ModProblem
{
    internal ModProblem(
        ModProblemSeverity severity, string kind, string modId, string sentence, string whatToDo,
        string details = "", string otherMod = "", string key = "", string listLine = "", bool dismissible = false)
    {
        Dismissible = dismissible;
        Severity = severity;
        Kind = kind ?? "";
        ModId = (modId ?? "").Trim();
        Sentence = (sentence ?? "").Trim();
        WhatToDo = (whatToDo ?? "").Trim();
        Details = details ?? "";
        OtherMod = otherMod ?? "";
        Key = key ?? "";
        ListLine = (listLine ?? "").Trim();
    }

    internal ModProblemSeverity Severity { get; }

    /// <summary>One of <see cref="ModProblemKinds"/> (or a later source's own).</summary>
    internal string Kind { get; }

    internal string ModId { get; }

    /// <summary>What happened, as one plain sentence.</summary>
    internal string Sentence { get; }

    /// <summary>What the player can do, as one plain sentence.</summary>
    internal string WhatToDo { get; }

    /// <summary>For the mod author: the exception, the file, the log line. Not drawn on the page.</summary>
    internal string Details { get; }

    /// <summary>The other mod involved (its name), or empty.</summary>
    internal string OtherMod { get; }

    /// <summary>What the problem is about within its kind — the key chord, the command — so one mod's two conflicts are two records.</summary>
    internal string Key { get; }

    /// <summary>The short form the Mods list's second line shows; the sentence when empty.</summary>
    internal string ListLine { get; }

    internal bool IsConflict => ModProblemKinds.IsConflict(Kind);

    /// <summary>
    /// KEYS2: the player may dismiss it (the page draws <b>Dismiss</b> on its line). Only a problem that
    /// reports something already handled is dismissible: a reserved key that was replaced and rewritten.
    /// </summary>
    internal bool Dismissible { get; }

    /// <summary>The page line: the sentence, then what to do.</summary>
    internal string Line => WhatToDo.Length == 0 ? Sentence : Sentence + " " + WhatToDo;

    internal string ShortLine => ListLine.Length > 0 ? ListLine : Sentence;

    internal Dictionary<string, object?> Readback() => new(StringComparer.Ordinal)
    {
        ["severity"] = ModProblems.NameOf(Severity),
        ["kind"] = Kind,
        ["mod"] = ModId,
        ["sentence"] = Sentence,
        ["whatToDo"] = WhatToDo,
        ["otherMod"] = OtherMod,
        ["key"] = Key,
        ["listLine"] = ShortLine,
        ["dismissible"] = Dismissible,
    };
}

/// <summary>
/// A source of problems read when asked (conflicts are computed from the live keybind and command
/// tables, so they clear by themselves once the cause is gone). The M4.7 health check registers one.
/// </summary>
internal interface IModProblemSource
{
    string Name { get; }

    IEnumerable<ModProblem> Current();
}

/// <summary>
/// SETTINGS1 stage 3: every mod problem the framework knows. Two kinds of source feed it:
/// <b>reported</b> problems (a load that failed, a file that did not load), recorded when they happen
/// and cleared when their cause goes — the next successful load of that mod, or its unload; and
/// <b>live</b> sources (<see cref="IModProblemSource"/>: keybind and command conflicts) read on every
/// call. <b>Report only:</b> nothing here disables, pauses or unloads anything.
/// </summary>
internal static class ModProblems
{
    private static readonly object Gate = new();
    private static readonly List<ModProblem> Reported = new();
    private static readonly List<IModProblemSource> Sources = new();

    /// <summary>KEYS2: problems the player dismissed this process (mod|kind|key); a live source's copy is skipped.</summary>
    private static readonly HashSet<string> Dismissed = new(StringComparer.OrdinalIgnoreCase);
    private static bool _builtIns;
    private static int _version;

    /// <summary>At most this many problem lines on a page; the rest are behind <c>Show all</c>.</summary>
    internal const int PageLineCap = 3;

    internal const string ShowAllCaption = "Show all";
    internal const string NoticeTitlePrefix = "Problems with ";
    internal const string ReportHeading = "Report for the mod author";
    internal const string CopiedNote = "The report is copied to the clipboard.";
    internal const string NotCopiedNote = "Copying to the clipboard is not available here; the report is shown below.";

    // ---- the words ---------------------------------------------------------------------------

    internal const string LoadFailedSentence = "It failed to load, so none of its features are running.";
    internal const string LoadFailedRetry = "Press Retry to load it again, or tell its author.";
    internal const string LoadFailedRestart = "Restart the game to try again, or tell its author.";
    internal const string LoadFailedListLine = "Failed to load";
    internal const string LocalizationSentence = "Its translation files did not load, so some text may be missing.";
    internal const string ContentSentence = "Its content files did not load, so some of its items or recipes may be missing.";
    internal const string FilesWhatToDo = "Tell its author; the report below has the details.";

    /// <summary>
    /// KEYS1: a key that lost its chord. <paramref name="holder"/> is who keeps it with why
    /// (<c>Hunts, installed first</c>, <c>the game (Interact), which always comes first</c>).
    /// </summary>
    internal static string KeySentence(string chord, string holder) =>
        "Not active: " + chord + " is used by " + holder + ".";

    /// <summary>KEYS1: an ordinary mod's key that lost its chord: reported, never stopped.</summary>
    internal static string CfgKeySentence(string chord, string holder) =>
        chord + " is also used by " + holder + ". " + KeybindArbiter.BothFire;

    internal const string KeyWhatToDo = "Pick another key in its Keys section or in Mods > Keybinds.";

    internal const string CfgKeyWhatToDo = "Change it in Mods > Keybinds.";

    internal static string KeyListLine(string chord, string other) => "Key " + chord + " not active: used by " + other;

    internal static string CfgKeyListLine(string chord, string other) => "Key " + chord + " also used by " + other;

    internal static string KeyMarker(string other) => "Also used by " + other;

    internal static string CommandSentence(string command, string others) =>
        "The command " + command + " is also registered by " + others + ", so no mod gets the short form.";

    internal static string CommandWhatToDo(string qualified) =>
        qualified.Length == 0 ? "Use the long form of the command." : "Use " + qualified + " instead.";

    internal static string CommandListLine(string command, string others) => "Command " + command + " also used by " + others;

    internal const string StartActionWhatToDo = "Try again from its page, or change the files by hand.";

    // ---- reported problems ------------------------------------------------------------------

    /// <summary>Record a problem. A record with the same mod, kind and key replaces the earlier one.</summary>
    internal static void Report(ModProblem problem)
    {
        if (problem == null || problem.ModId.Length == 0) return;
        lock (Gate)
        {
            // HOSTS1 round 6: the same problem reported again (the start results are re-read on every
            // workspace open) changes nothing, so it does not move the version the kept Mods list is keyed on.
            if (Reported.Exists(p => Identical(p, problem))) return;
            Reported.RemoveAll(p => Same(p, problem));
            Reported.Add(problem);
            _version++;
        }

        Raise();
    }

    /// <summary>Clear one kind of <paramref name="modId"/>'s reported problems (all kinds when null).</summary>
    internal static int Clear(string? modId, string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(modId)) return 0;
        int n;
        lock (Gate)
        {
            n = Reported.RemoveAll(p =>
                string.Equals(p.ModId, modId!.Trim(), StringComparison.OrdinalIgnoreCase)
                && (kind == null || string.Equals(p.Kind, kind, StringComparison.Ordinal)));
            if (n > 0) _version++;
        }

        if (n > 0) Raise();
        return n;
    }

    private static string DismissKey(ModProblem p) => p.ModId + "|" + p.Kind + "|" + p.Key;

    /// <summary>
    /// KEYS2: the player dismissed <paramref name="problem"/> (its page line's <b>Dismiss</b>). Refused for
    /// a problem that is not <see cref="ModProblem.Dismissible"/>. A reserved Progenitor key's note is
    /// cleared too, so its Keybinds row stops repeating it.
    /// </summary>
    internal static bool Dismiss(ModProblem? problem)
    {
        if (problem == null || !problem.Dismissible || problem.ModId.Length == 0) return false;
        lock (Gate)
        {
            Dismissed.Add(DismissKey(problem));
            Reported.RemoveAll(p => Same(p, problem));
            _version++;
        }

        if (string.Equals(problem.Kind, ModProblemKinds.KeybindReserved, StringComparison.Ordinal))
        {
            int bar = problem.Key.IndexOf('|');
            if (bar > 0) Keybinds.DismissReserved(problem.ModId, problem.Key.Substring(0, bar));
        }

        try { Plugin.Logger?.LogInfo("Mods: problem dismissed: " + problem.ModId + " " + problem.Kind + " " + problem.Key); }
        catch { /* */ }
        Raise();
        return true;
    }

    /// <summary>A plugin's load is starting again (Retry, a redeploy): its load-time problems are no longer evidence.</summary>
    internal static void ClearLoadProblems(string? modId)
    {
        Clear(modId, ModProblemKinds.LoadFailed);
        Clear(modId, ModProblemKinds.LocalizationFailed);
        Clear(modId, ModProblemKinds.ContentFailed);
    }

    // ---- live sources -----------------------------------------------------------------------

    /// <summary>Add a live source (the M4.7 health check). Disposing removes it.</summary>
    internal static IDisposable AddSource(IModProblemSource source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        lock (Gate)
        {
            Sources.Add(source);
            _version++;
        }

        return new Remover(source);
    }

    private sealed class Remover : IDisposable
    {
        private IModProblemSource? _source;

        internal Remover(IModProblemSource source) => _source = source;

        public void Dispose()
        {
            var s = _source;
            _source = null;
            if (s == null) return;
            lock (Gate)
            {
                Sources.Remove(s);
                _version++;
            }
        }
    }

    /// <summary>Bumped on every reported change, for a cheap "did anything change" check.</summary>
    internal static int Version
    {
        get { lock (Gate) return _version; }
    }

    /// <summary>Raised after a reported problem was added or cleared (never from a live source).</summary>
    internal static event Action? Changed;

    private static void Raise()
    {
        try { Changed?.Invoke(); }
        catch { /* a listener must never cost a report */ }
    }

    private static void EnsureBuiltIns()
    {
        lock (Gate)
        {
            if (_builtIns) return;
            _builtIns = true;
            Sources.Add(new KeybindConflictSource());
            Sources.Add(new ReservedKeySource());
            Sources.Add(new CommandConflictSource());
            Sources.Add(new DependencyOffSource());
            Sources.Add(new ProcessLifetimeSource());
            Sources.Add(new WaitingPluginSource());
            // INDEX1: installed copies of a version the Progenitor Index took down or warns about.
            Sources.Add(new Progenitor.Install.IndexRevocationSource());
        }
    }

    // ---- reading ------------------------------------------------------------------------------

    // HOSTS1 round 9: a Mods list build asks for every row's problems; each ask evaluated every live source
    // (the keybind and command conflict checks among them). Inside a build scope the list is computed once.
    private static int _scope;
    private static List<ModProblem>? _scoped;

    /// <summary>Round 9: from here until <see cref="LeaveScope"/>, <see cref="All"/> is computed once and reused.</summary>
    internal static void EnterScope()
    {
        lock (Gate) _scope++;
    }

    internal static void LeaveScope()
    {
        lock (Gate)
        {
            if (_scope > 0) _scope--;
            if (_scope == 0) _scoped = null;
        }
    }

    /// <summary>Every problem, reported and live, worst first, then by mod and sentence.</summary>
    internal static List<ModProblem> All()
    {
        lock (Gate)
        {
            if (_scope > 0 && _scoped != null) return new List<ModProblem>(_scoped);
        }

        var all = AllNow();
        lock (Gate)
        {
            if (_scope > 0) _scoped = new List<ModProblem>(all);
        }

        return all;
    }

    private static List<ModProblem> AllNow()
    {
        EnsureBuiltIns();
        List<ModProblem> list;
        List<IModProblemSource> sources;
        lock (Gate)
        {
            list = new List<ModProblem>(Reported);
            sources = new List<IModProblemSource>(Sources);
            list.RemoveAll(p => Dismissed.Contains(DismissKey(p)));
        }

        foreach (var source in sources)
        {
            try
            {
                foreach (var p in source.Current())
                {
                    if (p == null || p.ModId.Length == 0 || list.Exists(x => Same(x, p))) continue;
                    bool dismissed;
                    lock (Gate) dismissed = Dismissed.Contains(DismissKey(p));
                    if (!dismissed) list.Add(p);
                }
            }
            catch (Exception e)
            {
                try { Plugin.Logger?.LogWarning("Mods: problem source " + source.Name + " threw: " + e.Message); }
                catch { /* */ }
            }
        }

        Sort(list);
        return list;
    }

    /// <summary><paramref name="modId"/>'s problems, worst first.</summary>
    internal static List<ModProblem> For(string? modId)
    {
        var mine = new List<ModProblem>();
        if (string.IsNullOrWhiteSpace(modId)) return mine;
        foreach (var p in All())
            if (string.Equals(p.ModId, modId!.Trim(), StringComparison.OrdinalIgnoreCase)) mine.Add(p);
        return mine;
    }

    /// <summary>The worst severity among <paramref name="problems"/>, or null when there are none.</summary>
    internal static ModProblemSeverity? Worst(IReadOnlyList<ModProblem>? problems)
    {
        ModProblemSeverity? worst = null;
        if (problems == null) return null;
        foreach (var p in problems)
            if (worst == null || p.Severity > worst) worst = p.Severity;
        return worst;
    }

    /// <summary>The worst problem that is not a conflict (a conflict never changes the status word).</summary>
    internal static ModProblem? WorstNonConflict(IReadOnlyList<ModProblem>? problems)
    {
        ModProblem? worst = null;
        if (problems == null) return null;
        foreach (var p in problems)
        {
            if (p.IsConflict || p.Severity == ModProblemSeverity.Info) continue;
            if (worst == null || p.Severity > worst.Severity) worst = p;
        }

        return worst;
    }

    internal static int ConflictCount(IReadOnlyList<ModProblem>? problems)
    {
        int n = 0;
        if (problems == null) return 0;
        foreach (var p in problems) if (p.IsConflict) n++;
        return n;
    }

    /// <summary>A mod needs attention when it has a warning or a critical problem (conflicts included).</summary>
    internal static bool NeedsAttention(IReadOnlyList<ModProblem>? problems)
    {
        if (problems == null) return false;
        foreach (var p in problems) if (p.Severity >= ModProblemSeverity.Warning) return true;
        return false;
    }

    /// <summary>The chip on the status bar: <c>1 conflict</c>, <c>2 conflicts</c>; empty for none.</summary>
    internal static string ConflictChip(int count) =>
        count <= 0 ? "" : count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " conflict" : " conflicts");

    /// <summary>
    /// The problems line under the status bar: at most <see cref="PageLineCap"/> lines, worst first,
    /// and whether a <c>Show all</c> is owed (more than the cap, or any details to copy).
    /// </summary>
    internal static List<string> PageLines(IReadOnlyList<ModProblem>? problems, out bool showAll)
    {
        var lines = new List<string>();
        showAll = false;
        if (problems == null || problems.Count == 0) return lines;
        for (int i = 0; i < problems.Count && lines.Count < PageLineCap; i++) lines.Add(problems[i].Line);
        // Live 2026-09-29: Show all only when there is more to see - a problem past the cap, or
        // details for the mod author (an exception). A conflict's details restate its sentence.
        showAll = problems.Count > lines.Count;
        foreach (var p in problems)
            if (!p.IsConflict && p.Details.Trim().Length > 0) showAll = true;
        return lines;
    }

    /// <summary>The key-row marker for a keybind in conflict: the other owners' names, or empty.</summary>
    internal static string KeyConflictOwner(string? pluginId, string? bindId)
    {
        var v = KeyVerdict(pluginId, bindId);
        return v == null || !v.InConflict ? "" : KeybindArbiter.OthersNames(v.Others);
    }

    /// <summary>
    /// KEYS1: the Keys row's sentence for a keybind in conflict — the loser's <c>Not active: …</c> or the
    /// winner's <c>Also used by …</c> — from the same winner rule the Keybinds tab and the pump use.
    /// </summary>
    internal static string KeyConflictLine(string? pluginId, string? bindId)
    {
        var v = KeyVerdict(pluginId, bindId);
        return v == null || !v.InConflict ? "" : v.Reason;
    }

    /// <summary>KEYS1: the winner rule's verdict on one Progenitor bind, or null.</summary>
    internal static KeybindVerdict? KeyVerdict(string? pluginId, string? bindId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(bindId)) return null;
        try { return KeybindArbiter.Current().FindProgenitor(pluginId, bindId); }
        catch { return null; /* evidence, not a gate */ }
    }

    /// <summary>
    /// The copyable report for the mod author: the mod, its version, then every problem with its
    /// details. Plain text, one problem per block.
    /// </summary>
    internal static string ReportText(string modId, string modName, string version, IReadOnlyList<ModProblem>? problems)
    {
        var sb = new StringBuilder();
        sb.Append(modName.Length > 0 ? modName : modId);
        if (version.Length > 0) sb.Append(' ').Append(version);
        sb.Append(" (").Append(modId).Append(")\n");
        sb.Append("Progenitor ").Append(FrameworkVersionText()).Append('\n');
        if (problems != null)
            foreach (var p in problems)
            {
                sb.Append('\n').Append('[').Append(NameOf(p.Severity)).Append("] ").Append(p.Kind).Append(": ").Append(p.Sentence).Append('\n');
                if (p.OtherMod.Length > 0) sb.Append("Other mod: ").Append(p.OtherMod).Append('\n');
                if (p.Details.Length > 0) sb.Append(p.Details.TrimEnd()).Append('\n');
            }

        return sb.ToString();
    }

    private static string FrameworkVersionText()
    {
        try { return typeof(ModProblems).Assembly.GetName().Version?.ToString() ?? ""; }
        catch { return ""; }
    }

    internal static string NameOf(ModProblemSeverity severity) => severity switch
    {
        ModProblemSeverity.Critical => "critical",
        ModProblemSeverity.Warning => "warning",
        _ => "info",
    };

    private static bool Identical(ModProblem a, ModProblem b) =>
        Same(a, b)
        && a.Severity == b.Severity
        && a.Dismissible == b.Dismissible
        && string.Equals(a.Sentence, b.Sentence, StringComparison.Ordinal)
        && string.Equals(a.WhatToDo, b.WhatToDo, StringComparison.Ordinal)
        && string.Equals(a.Details, b.Details, StringComparison.Ordinal)
        && string.Equals(a.OtherMod, b.OtherMod, StringComparison.Ordinal)
        && string.Equals(a.ListLine, b.ListLine, StringComparison.Ordinal);

    private static bool Same(ModProblem a, ModProblem b) =>
        string.Equals(a.ModId, b.ModId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Kind, b.Kind, StringComparison.Ordinal)
        && string.Equals(a.Key, b.Key, StringComparison.Ordinal);

    private static void Sort(List<ModProblem> list) =>
        list.Sort((a, b) =>
        {
            int s = b.Severity.CompareTo(a.Severity);
            if (s != 0) return s;
            int m = StringComparer.OrdinalIgnoreCase.Compare(a.ModId, b.ModId);
            return m != 0 ? m : StringComparer.Ordinal.Compare(a.Sentence, b.Sentence);
        });

    /// <summary>Tests only: forget everything reported and every added source.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Reported.Clear();
            Sources.Clear();
            Dismissed.Clear();
            _builtIns = false;
            _version++;
        }

        Changed = null;
        KeybindConflictSource.NameOf = null;
        KeybindConflictSource.LoadedNameOf = null;
        CommandConflictSource.Probe = null;
        ReservedKeySource.ResetForTests();
    }

    // ---- the load-time reports (ProgenitorPlugin) -------------------------------------------

    /// <summary><c>OnLoad</c> threw. <paramref name="retry"/>: the mod is hot-loadable, so the page offers Retry.</summary>
    internal static ModProblem LoadFailed(string modId, string details, bool retry) =>
        new(ModProblemSeverity.Critical, ModProblemKinds.LoadFailed, modId, LoadFailedSentence,
            retry ? LoadFailedRetry : LoadFailedRestart, details, listLine: LoadFailedListLine);

    /// <summary>
    /// A load failure's what-to-do depends on whether the mod can be loaded again now, which is only
    /// known after the reloader has registered it (not while its <c>OnLoad</c> is throwing): the page
    /// asks <see cref="Progenitor.Ui.ModSwitch.CanRetry"/> when it is drawn and rewrites the sentence.
    /// </summary>
    internal static List<ModProblem> WithRetry(IReadOnlyList<ModProblem>? problems, bool canRetry)
    {
        var list = new List<ModProblem>();
        if (problems == null) return list;
        foreach (var p in problems)
        {
            if (p.Kind != ModProblemKinds.LoadFailed) { list.Add(p); continue; }
            list.Add(new ModProblem(p.Severity, p.Kind, p.ModId, p.Sentence,
                canRetry ? LoadFailedRetry : LoadFailedRestart, p.Details, p.OtherMod, p.Key, p.ListLine, p.Dismissible));
        }

        return list;
    }

    internal static ModProblem LocalizationFailed(string modId, string details) =>
        new(ModProblemSeverity.Warning, ModProblemKinds.LocalizationFailed, modId, LocalizationSentence, FilesWhatToDo, details);

    internal static ModProblem ContentFailed(string modId, string details) =>
        new(ModProblemSeverity.Warning, ModProblemKinds.ContentFailed, modId, ContentSentence, FilesWhatToDo, details);
}

/// <summary>
/// Review of 5a6e40f8, finding 6: an ordinary BepInEx plugin that hard-depends on a Progenitor mod the
/// player turned off keeps loading (the chainloader still finds the dependency, which is loaded but
/// inert), so it is told on its own row: <c>It depends on X, which is turned off.</c> Read live from the
/// state file, so it clears when X is turned on or the dependant is turned off or removed.
/// </summary>
internal sealed class DependencyOffSource : IModProblemSource
{
    public string Name => "dependencies";

    internal static string Sentence(string mod) => "It depends on " + mod + ", which is turned off.";

    internal static string WhatToDo(string mod) => "Turn " + mod + " on, or turn this mod off too.";

    internal static string ListLine(string mod) => "Needs " + mod + ", which is off";

    public IEnumerable<ModProblem> Current()
    {
        List<Progenitor.Plugins.ModEnableEntry> off;
        try { off = Progenitor.Plugins.ModEnableState.Disabled(); }
        catch { yield break; }
        foreach (var e in off)
        {
            string name = e.Name.Length > 0 ? e.Name : e.Id;
            foreach (var d in ModSwitch.PlainDependantsOf(e.Id))
            {
                if (string.IsNullOrWhiteSpace(d.Id)) continue;
                yield return new ModProblem(ModProblemSeverity.Warning, ModProblemKinds.DependencyOff, d.Id,
                    Sentence(name), WhatToDo(name), "", name, key: e.Id, listLine: ListLine(name));
            }
        }
    }
}

/// <summary>
/// KEYS1: keybind conflicts, from the winner rule (<see cref="KeybindArbiter"/>) over the game's keys,
/// every Progenitor bind and ordinary mods' key settings, read on every call — so a conflict disappears
/// the moment either side is remapped or unloaded. <b>Only the side that lost gets a problem</b>: a
/// Progenitor bind that is not active (<c>Not active: F8 is used by X, installed first.</c>), or an
/// ordinary mod's key that clashes with an earlier one (<c>… Both fire; Progenitor can't stop this
/// mod.</c>). The winner's own Keys row still says <c>Also used by …</c>.
/// </summary>
internal sealed class KeybindConflictSource : IModProblemSource
{
    public string Name => "keybinds";

    /// <summary>Plugin id → display name. The menu sets it; the id is used when it is null or answers nothing.</summary>
    internal static Func<string, string?>? NameOf { get; set; }

    internal readonly struct Conflict
    {
        internal Conflict(
            string pluginId, string bindId, string chord, string otherName, string otherId, bool active, string reason,
            KeybindSource source, string winnerPhrase)
        {
            PluginId = pluginId;
            BindId = bindId;
            Chord = chord;
            OtherName = otherName;
            OtherId = otherId;
            Active = active;
            Reason = reason;
            Source = source;
            WinnerPhrase = winnerPhrase;
        }

        internal string PluginId { get; }
        internal string BindId { get; }
        internal string Chord { get; }
        internal string OtherName { get; }
        internal string OtherId { get; }

        /// <summary>False: this side lost the chord.</summary>
        internal bool Active { get; }

        internal string Reason { get; }
        internal KeybindSource Source { get; }

        /// <summary>The winner as the sentence names it, with why (<c>Hunts, installed first</c>); empty for a winner.</summary>
        internal string WinnerPhrase { get; }
    }

    internal static string DisplayName(string id)
    {
        // KEYS4: Progenitor's own plugins by name, never by id.
        if (KeybindArbiter.FrameworkName(id) is { } framework) return framework;
        try
        {
            string? n = NameOf?.Invoke(id);
            if (!string.IsNullOrWhiteSpace(n)) return n!.Trim();
        }
        catch { /* the id is a fine fallback */ }
        // A mod with no menu registration (or before the workspace has opened since a Core swap):
        // its BepInEx name, as the Keybinds tab's section titles show it, never the raw GUID.
        try
        {
            string? loaded = LoadedNameOf?.Invoke(id);
            if (!string.IsNullOrWhiteSpace(loaded)) return loaded!.Trim();
        }
        catch { /* the id is a fine fallback */ }
        return id;
    }

    /// <summary>Plugin id → the loaded plugin's BepInEx name (production: the chainloader); null when unknown. A seam for tests.</summary>
    internal static Func<string, string?>? LoadedNameOf { get; set; } = DefaultLoadedName;

    private static string? DefaultLoadedName(string id)
    {
#if PROGENITOR_TESTS
        return null;
#else
        var plugins = BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance?.Plugins;
        if (plugins == null || string.IsNullOrWhiteSpace(id)) return null;
        return plugins.TryGetValue(id.Trim(), out var info) ? info?.Metadata?.Name : null;
#endif
    }

    /// <summary>Every mod key (Progenitor or cfg) whose chord something else also uses, with the verdict.</summary>
    internal static List<Conflict> Conflicts()
    {
        var result = new List<Conflict>();
        KeybindPicture picture;
        try { picture = KeybindArbiter.Current(); }
        catch { return result; }
        foreach (var v in picture.Verdicts)
        {
            var c = v.Claim;
            if (c.Source == KeybindSource.Game || !v.InConflict || string.IsNullOrWhiteSpace(c.OwnerId)) continue;
            var first = v.Active ? v.Others[0] : v.Winner!;
            string otherId = first.Source == KeybindSource.Game ? KeybindArbiter.GameOwnerId : first.OwnerId;
            string winnerPhrase = v.Active ? "" : KeybindArbiter.HolderPhrase(v.Winner!) + KeybindArbiter.WhyPhrase(v.Winner!, v.Rule);
            string otherName = v.Active ? KeybindArbiter.OthersNames(v.Others) : KeybindArbiter.HolderPhrase(v.Winner!);
            result.Add(new Conflict(c.OwnerId, c.ActionId, c.ChordText, otherName, otherId, v.Active, v.Reason, c.Source, winnerPhrase));
        }

        return result;
    }

    internal static string JoinNames(List<string> names)
    {
        if (names.Count <= 1) return names.Count == 1 ? names[0] : "";
        if (names.Count == 2) return names[0] + " and " + names[1];
        return string.Join(", ", names.GetRange(0, names.Count - 1)) + " and " + names[names.Count - 1];
    }

    public IEnumerable<ModProblem> Current()
    {
        foreach (var c in Conflicts())
        {
            if (c.Active) continue;
            bool cfg = c.Source == KeybindSource.Cfg;
            yield return new ModProblem(
                ModProblemSeverity.Warning, ModProblemKinds.KeybindConflict, c.PluginId,
                cfg ? ModProblems.CfgKeySentence(c.Chord, c.WinnerPhrase) : ModProblems.KeySentence(c.Chord, c.WinnerPhrase),
                cfg ? ModProblems.CfgKeyWhatToDo : ModProblems.KeyWhatToDo,
                cfg ? "" : Keybinds.NotActiveLog(c.PluginId + "/" + c.BindId, c.Reason),
                c.OtherName, key: c.BindId + "|" + c.Chord,
                listLine: cfg ? ModProblems.CfgKeyListLine(c.Chord, c.OtherName) : ModProblems.KeyListLine(c.Chord, c.OtherName));
        }
    }
}

/// <summary>
/// Chat-command conflicts, read from the command registry on every call: a route two owners
/// registered, whose short form is therefore disabled for all of them (<c>CommandRegistry</c>'s
/// rule: no owner wins). One record per owner, naming the others and the long form that still works.
/// </summary>
internal sealed class CommandConflictSource : IModProblemSource
{
    public string Name => "commands";

    /// <summary>Tests only: the conflicts instead of the registry's (route, owner ids, qualified paths).</summary>
    internal static Func<IReadOnlyList<(string Route, IReadOnlyList<string> Owners, IReadOnlyList<string> Qualified)>>? Probe { get; set; }

    public IEnumerable<ModProblem> Current()
    {
        IReadOnlyList<(string Route, IReadOnlyList<string> Owners, IReadOnlyList<string> Qualified)> conflicts;
        try { conflicts = Probe?.Invoke() ?? Progenitor.Commands.CommandRegistry.Conflicts(); }
        catch { yield break; }
        foreach (var c in conflicts)
        {
            if (c.Owners == null || c.Owners.Count < 2) continue;
            string command = Progenitor.Commands.CommandRegistry.Prefix + c.Route;
            for (int i = 0; i < c.Owners.Count; i++)
            {
                string owner = c.Owners[i];
                if (string.IsNullOrWhiteSpace(owner)) continue;
                var others = new List<string>();
                for (int j = 0; j < c.Owners.Count; j++)
                    if (j != i && !string.IsNullOrWhiteSpace(c.Owners[j])) others.Add(KeybindConflictSource.DisplayName(c.Owners[j]));
                if (others.Count == 0) continue;
                string otherNames = KeybindConflictSource.JoinNames(others);
                string qualified = c.Qualified == null ? "" : string.Join(" or ", c.Qualified);
                yield return new ModProblem(
                    ModProblemSeverity.Warning, ModProblemKinds.CommandConflict, owner,
                    ModProblems.CommandSentence(command, otherNames), ModProblems.CommandWhatToDo(qualified),
                    "Ambiguous command '" + command + "'; owners: " + string.Join(", ", c.Owners) + ".",
                    otherNames, key: c.Route, listLine: ModProblems.CommandListLine(command, otherNames));
            }
        }
    }
}
