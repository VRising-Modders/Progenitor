using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace Progenitor.Bootstrap;

/// <summary>
/// Plugin ALC host for <c>BepInEx/progenitor-reload/</c>. Lives in Bootstrap because the host
/// that <c>Unload</c>s contexts cannot later live in a collectible Core. Sweep of Core
/// registrations (EventBus, commands, prefabs, …) is a callback Core registers; this file
/// does not name those types. Plugin ALC <c>Resolving</c> returns the Core generation that plugin was loaded against.
/// </summary>
internal static class ReloaderHost
{
    private sealed class Loaded
    {
        public string Path = "";
        public DateTime LastWrite;
        public AssemblyLoadContext? Context;
        public List<BasePlugin> Plugins = new();
        public Assembly? Assembly;
        /// <summary>
        /// Core generation this plugin ALC was loaded against. Resolving must return this,
        /// not <see cref="CoreLoader.Assembly"/>, so an uncollected old plugin cannot mix
        /// gen N types with gen N+1. Nulled in <see cref="Unload"/> so a leftover
        /// <c>Loaded</c> cannot pin Core across a swap.
        /// </summary>
        public Assembly? BoundCore;
        /// <summary>Stored so <see cref="Unload"/> can unsubscribe and drop the <c>Loaded</c> capture.</summary>
        public Func<AssemblyLoadContext, AssemblyName, Assembly?>? Resolving;
        /// <summary>
        /// SHA-256 of the image the allowlist admitted for this file (job FENC2). A
        /// <c>.delta</c> may only be applied when its header names this exact hash: the delta is
        /// IL emitted against one specific baseline, and applying it to any other image is at
        /// best a corrupt method table.
        /// </summary>
        public string ImageSha = "";
        /// <summary>
        /// Job FENC3 (R38 finding 1): the compatibility rewriter returned different bytes than it
        /// was given, so the loaded metadata is Cecil's row layout and not the on-disk image's.
        /// <see cref="ImageSha"/> is cleared when this is set — a rewritten plugin has no baseline
        /// a delta can name — and this flag is what makes the refusal say why.
        /// </summary>
        public bool Rewritten;
        /// <summary>Simple assembly name, so a <c>&lt;plugin&gt;.&lt;n&gt;.delta</c> finds its plugin.</summary>
        public string AssemblyName = "";
        /// <summary>
        /// The plugin DLL's file stem, and the ONE key the <c>.delta-state</c> status file is
        /// named by — every writer and the deleter (job FENC6, R41 finding 5). Set before the
        /// image is loaded, so unlike <see cref="AssemblyName"/> it is always available: a load
        /// that throws inside <c>LoadFromStream</c> is precisely the case FENC5's refusal file
        /// exists for, and it is the case where no assembly name can be read. See
        /// <see cref="DeltaApply.StateFileName"/>.
        /// </summary>
        public string Stem = "";
        /// <summary>Deltas apply in generation order; this is the only sequence that will be taken.</summary>
        public int NextDeltaSequence = DeltaApply.FirstSequence;
        /// <summary>
        /// Job FENC4 (R39 finding 1): which load this is. <see cref="NextDeltaSequence"/> goes
        /// back to 1 on every load and the status file therefore says <c>applied=0</c> for both a
        /// process that has applied nothing and one that applied four deltas and restarted. This
        /// value is fresh per <c>Loaded</c>, so the watcher can tell those apart and rebaseline
        /// from the DEPLOYED image's sources rather than from a snapshot of a process that is gone.
        /// </summary>
        public string LoadNonce = DeltaApply.NextLoadNonce();
        /// <summary>
        /// Job FINJ1: the write stamp of the image this <c>Loaded</c> actually runs. <see cref="LastWrite"/>
        /// is adopted on a refused swap so the refusal is logged once; this one never moves, so a
        /// process-lifetime plugin can tell "a new build is waiting" from "back to the running build".
        /// </summary>
        public DateTime LoadedWrite;
        /// <summary>Job FINJ1: the injection call sites and Core reference read from the original image before it loaded.</summary>
        public Il2CppInjectionScan Injection = Il2CppInjectionScan.None;
        /// <summary>Job FINJ1: names in ClassInjector's injected set this plugin's assembly defines. Sticky: injection is never undone.</summary>
        public List<string> RuntimeInjected = new();
        /// <summary>Job FINJ1: <see cref="Il2CppInjectionPolicy.PendingChanged"/> / <c>PendingRemoved</c> while a restart is owed, else "".</summary>
        public string Pending = Il2CppInjectionPolicy.PendingNone;
        /// <summary>
        /// Job FORD1: GUIDs, <c>[BepInDependency]</c> list, assembly name and assembly references read from the
        /// original image. Drives the load order, the dependents a replacement takes down with it, and which
        /// sibling assemblies this plugin's context may resolve.
        /// </summary>
        public PluginImageInfo Info = PluginImageInfo.Empty("", "");
    }

    /// <summary>Job FORD1: one DLL the scan is about to load, with the replacement verdict when there was one.</summary>
    private sealed class LoadCandidate
    {
        public string File = "";
        public DateTime Write;
        public byte[]? Bytes;
        public string? Hash;
        public AllowDecision Allow;
        public bool HaveVerdict;
        public bool UnloadedForReplacement;
        public PluginImageInfo Info = PluginImageInfo.Empty("", "");
    }

    /// <summary>Job FORD1: one loaded DLL leaving this scan (replaced or removed), with its replacement candidate.</summary>
    private sealed class Leaving
    {
        public Loaded Loaded = null!;
        public int Reason;
        public LoadCandidate? Replacement;
    }

    /// <summary>
    /// Job FORD1: DLLs not loaded because a dependency is missing or did not load, path → (stamp, waiting
    /// for, sentence). Never <see cref="Failed"/>: every scan plans them again, so they load by themselves
    /// once the dependency arrives. Logged once per change.
    /// </summary>
    private static readonly Dictionary<string, (DateTime Write, string WaitingFor, string Reason, string[] Guids)> WaitingByPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Job FORD1: metadata of a waiting DLL by path and stamp, so a wait does not re-read the file every second.</summary>
    private static readonly Dictionary<string, (DateTime Write, PluginImageInfo Info)> InfoCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Job FORD1: the last order/cycle/held lines logged, so a repeating state is one line, not one a second.</summary>
    private static readonly HashSet<string> NotedOnce = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, Loaded> ByPath = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Path → last write stamp that threw. Never stores an <see cref="Assembly"/> or
    /// ALC: a Failed entry that outlived Unload would pin the collectible context.
    /// </summary>
    private static readonly Dictionary<string, DateTime> Failed = new(StringComparer.OrdinalIgnoreCase);
    private static string? _folder;
    private static double _nextScan;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private const double ScanIntervalSeconds = 1.0;
    private const double SettleSeconds = 1.0;
    private static readonly object LatchGate = new();
    private static readonly List<WeakReference> LatchedPluginAlcs = new();
    private static readonly List<string> LatchedPluginNames = new();
    private static int _loggedEnumFail;
    /// <summary>The EnC-unavailable sentence is logged once per process, not once per delta.</summary>
    private static int _loggedEncDisabled;
    private static bool _forcePluginLatch;
    private static int _latchPollFrames;
    private static readonly HashSet<string> DetourPinnedAlcs = new(StringComparer.Ordinal);
    private static bool _anyDetourPinned;
    /// <summary>
    /// Cascade <see cref="ReloadAll"/> after a wait miss sets this so <see cref="Load"/>
    /// may mint plugin ALCs even while weaks are still latched. Idle Scan never sets it.
    /// </summary>
    private static bool _loadWhileLatched;

    /// <summary>
    /// Job FREL2 (R61 finding 1, the Bootstrap half of FREL1): how this reloader reads
    /// <c>[BepInPlugin]</c>, at discovery and in the identity fallback alike. <b>False, because
    /// that is what BepInEx uses</b> (<c>MetadataHelper.GetMetadata</c> reads the attribute with
    /// <c>inherit: false</c> and returns null when it finds none). Discovery used to take the
    /// generic overload's default, <c>inherit: true</c>, which admitted plugins BepInEx could not
    /// describe — the single trigger behind every null-metadata dereference in this file. A type
    /// that inherits the attribute is refused at discovery with a line saying why.
    ///
    /// <para>This is the reloader that runs in production (Core's <c>PluginReloader</c> only runs
    /// when <c>BootstrapLink.OwnsLifetime</c> is false), so this is the copy of the fix that
    /// matters — the same relationship <see cref="TryRefuseUnsafeSwap"/> documents for FHARM8.</para>
    /// </summary>
    private const bool DiscoveryInherit = false;

    /// <summary>
    /// The identity of one plugin instance, resolved once and never null (job FREL2,
    /// R61 finding 1): BepInEx's metadata, then the same attribute read without BepInEx, then the
    /// type's full name. Every consumer in this file goes through here, so none of them can NRE on
    /// a plugin BepInEx cannot describe and all of them agree about who the plugin is.
    ///
    /// <para>Job FREL3 (R62 finding 1): the first two are <b>one question asked twice</b>, not two
    /// sources — <c>MetadataHelper.GetMetadata</c> is itself
    /// <c>GetCustomAttributes(typeof(BepInPlugin), inherit: false)</c> — and the second exists for
    /// a metadata read that <em>throws</em> and for an attribute from a second
    /// <c>BepInEx.Core</c> image, which is why it matches by name. The type name is the fallback.
    /// <see cref="PluginIdentity"/> owns both projections and the attribute lookup; the one line
    /// BepInEx has to own is the <c>GetMetadata</c> call below.</para>
    ///
    /// <para><see cref="PluginIdentity"/> is a byte-for-byte mirror of
    /// <c>Progenitor.Core/Reload/PluginIdentity.cs</c> — Bootstrap may not reference Core — and
    /// <c>ReloadHardeningTests.The_two_plugin_identity_copies_are_the_same_code</c> pins that,
    /// the same way <c>ReloadAllowPolicy</c> and the promote copies are pinned.</para>
    /// </summary>
    private static PluginIdentity IdentityOf(BasePlugin plugin)
    {
        var type = plugin?.GetType();
        // Job FREL4 (R63 finding 4): one sink for both the reader that threw and the decoy line
        // the attribute reader emits when a same-named non-BepInEx [BepInPlugin] is present.
        Action<string> note = message => ProcessHost.Log?.LogWarning("Reload: " + message);
        return PluginIdentity.Resolve(
            type?.FullName,
            new Func<PluginIdentity?>?[]
            {
                PluginIdentity.MetadataReader(() => plugin == null ? null : MetadataHelper.GetMetadata(plugin)),
                PluginIdentity.AttributeReader(type, DiscoveryInherit, note),
            },
            note);
    }

    public static string Folder => _folder ??= Path.Combine(Paths.BepInExRootPath, "progenitor-reload");

    public static bool TryGetSourcePath(Assembly asm, out string path)
    {
        path = "";
        if (asm == null) return false;
        foreach (var loaded in ByPath.Values)
        {
            if (loaded.Assembly == null || !ReferenceEquals(loaded.Assembly, asm))
                continue;
            path = loaded.Path ?? "";
            return path.Length > 0;
        }
        return false;
    }

    /// <summary>
    /// Job FHARM8: true when one of <paramref name="loaded"/>'s plugins took
    /// <c>ctx.UnsafeHarmony</c>, in which case the in-place swap is refused and
    /// <see cref="UnsafeHarmonySlot.FormatSwapRefused"/> says why and asks for a restart.
    ///
    /// <para>This is the mirror of <c>Progenitor.Reload.PluginReloader.TryRefuseUnsafeSwap</c>,
    /// and it is the one that matters: Core's reloader only runs when
    /// <c>BootstrapLink.OwnsLifetime</c> is false, so before FHARM8 the production configuration
    /// was the one still swapping a permanently pinned plugin. Both reloaders now read the same
    /// table (<see cref="UnsafeHarmonySlot"/>), which Core fills through
    /// <c>Progenitor.Patches.UnsafeHarmonySlotLink</c>, so they cannot disagree.</para>
    ///
    /// <para>The mark is never cleared for the life of the process: unloading does not unpin the
    /// ALC, so a later load of the same GUID is still living beside a pinned generation.</para>
    /// </summary>
    private static bool TryRefuseUnsafeSwap(string file, Loaded loaded)
    {
        try
        {
            foreach (var plugin in loaded.Plugins)
            {
                if (plugin == null) continue;
                string guid = IdentityOf(plugin).Guid;
                if (!UnsafeHarmonySlot.RefusesSwap(guid)) continue;
                Note(UnsafeHarmonySlot.FormatSwapRefused(Path.GetFileName(file), guid));
                return true;
            }
        }
        catch (Exception e)
        {
            // Never let the mark lookup stop the reloader; an un-refused swap is the old
            // behavior, and the once-per-plugin ctx.UnsafeHarmony warning already went to the log.
            ProcessHost.Log?.LogWarning("Reload: unsafe-Harmony check for "
                + Path.GetFileName(file) + ": " + e.Message);
        }
        return false;
    }

    private static readonly List<string> Events = new();

    /// <summary>
    /// Per-server mod pause: the reload-folder DLLs this client suspended for the server it joined.
    /// Bootstrap-owned so a Core swap keeps them paused; Core rediscovers the set through
    /// <see cref="SuspendedPluginRows"/>.
    /// </summary>
    private static readonly PluginSuspendTable Paused = new();

    /// <summary>
    /// The loaded reload-folder DLLs as closure nodes: GUIDs, <c>[BepInDependency]</c> union, and
    /// whether any plugin took <c>ctx.UnsafeHarmony</c>. Index-aligned with <paramref name="loaded"/>.
    /// </summary>
    private static List<SuspendUnitNode> DescribeUnits(out List<Loaded> loaded)
    {
        loaded = new List<Loaded>(ByPath.Values);
        var nodes = new List<SuspendUnitNode>(loaded.Count);
        foreach (var item in loaded)
        {
            var guids = new List<string>();
            bool unsafeHarmony = false;
            foreach (var plugin in item.Plugins)
            {
                if (plugin == null) continue;
                string guid;
                try { guid = IdentityOf(plugin).Guid; }
                catch { continue; }
                if (guid.Length == 0) continue;
                guids.Add(guid);
                try { if (UnsafeHarmonySlot.RefusesSwap(guid)) unsafeHarmony = true; }
                catch { unsafeHarmony = true; } // fail closed: an unknown mark is not pausable
            }
            var node = DescribeNode(item, 0);
            var deps = new List<string>(node.Dependencies);
            bool injects = Il2CppInjectionPolicy.Decide(LifetimeEvent.Suspend, IsProcessLifetime(item), item.Injection.CoreBound)
                == LifetimeAction.Refuse;
            nodes.Add(new SuspendUnitNode(item.Path, guids.ToArray(), deps.ToArray(), unsafeHarmony, injects));
        }
        return nodes;
    }

    /// <summary>
    /// The GUIDs a suspend of <paramref name="guids"/> would actually take (DLL siblings and
    /// dependents included), without doing it. Empty with <paramref name="refusal"/> set when the
    /// request would be refused. Core's planner simulates the join with exactly this set.
    /// </summary>
    public static string[] SuspendClosure(string[]? guids, out string refusal)
    {
        var nodes = DescribeUnits(out _);
        if (!PluginSuspendPolicy.TryClosure(nodes, guids, out var selected, out refusal))
            return Array.Empty<string>();
        return PluginSuspendPolicy.GuidsOf(nodes, selected).ToArray();
    }

    /// <summary>GUIDs of loaded reload-folder plugins that could be paused (not <c>ctx.UnsafeHarmony</c>).</summary>
    public static string[] SuspendableGuids()
    {
        var result = new List<string>();
        foreach (var node in DescribeUnits(out _))
        {
            if (node.UnsafeHarmony || node.InjectsIl2CppTypes) continue;
            result.AddRange(node.Guids);
        }
        return result.ToArray();
    }

    /// <summary>
    /// Suspended plugins as <c>guid\tname\tversion\tstate\trows</c> rows; <c>state</c> is
    /// <c>paused</c> or <c>resuming</c> (released, waiting to load).
    /// </summary>
    public static string[] SuspendedPluginRows() => Paused.Rows().ToArray();

    /// <summary>
    /// Per-server mod pause: unload the DLLs holding <paramref name="guids"/>, their DLL siblings and
    /// every reload plugin that depends on them, with reason Suspend (state kept, icons kept), and
    /// keep them unloaded until <see cref="Resume"/>. All-or-nothing: a GUID that is not a
    /// reload-folder plugin, or a DLL that took <c>ctx.UnsafeHarmony</c>, refuses the whole request.
    /// <paramref name="rowOwners"/> (<c>plugin\trow</c>) records which handshake rows each plugin
    /// provided, so a later Core generation can resume it by any of them. A GUID whose DLL is only
    /// resuming (not loaded yet) simply stays paused. Returns every GUID now paused by this call.
    /// Main thread only.
    /// </summary>
    public static string[] Suspend(string[]? guids, string[]? rowOwners, out string report)
    {
        var done = new List<string>();
        var lines = new List<string>();
        var rest = new List<string>();
        var stayed = Paused.CancelResume(guids);
        if (stayed.Count > 0)
        {
            done.AddRange(stayed);
            lines.Add("Reload: " + string.Join(", ", stayed) + " stays paused (it had not loaded again yet).");
        }
        if (guids != null)
            foreach (var g in guids)
                if (!stayed.Exists(x => string.Equals(x, g, StringComparison.OrdinalIgnoreCase))) rest.Add(g);

        if (rest.Count > 0)
        {
            var nodes = DescribeUnits(out var loaded);
            if (!PluginSuspendPolicy.TryClosure(nodes, rest, out var selected, out string refusal))
            {
                string line = PluginSuspendPolicy.FormatRefused(refusal);
                Note(line);
                lines.Add(line);
                report = string.Join(" ", lines);
                return done.ToArray();
            }

            var rowsByPlugin = ParseRowOwners(rowOwners, out var hardByPlugin);
            // Dependents first, like a Core swap: a plugin's OnUnload may still talk to what it depends on.
            var orderNodes = new PluginUnloadOrder.Node[selected.Count];
            for (int i = 0; i < selected.Count; i++)
                orderNodes[i] = DescribeNode(loaded[selected[i]], i);
            int[] order = PluginUnloadOrder.ReverseTopological(orderNodes);

            for (int o = 0; o < order.Length; o++)
            {
                var item = loaded[selected[order[o]]];
                var node = nodes[selected[order[o]]];
                var plugins = new List<SuspendedPlugin>();
                foreach (var plugin in item.Plugins)
                {
                    if (plugin == null) continue;
                    try
                    {
                        var identity = IdentityOf(plugin);
                        rowsByPlugin.TryGetValue(identity.Guid, out var rows);
                        hardByPlugin.TryGetValue(identity.Guid, out var hard);
                        plugins.Add(new SuspendedPlugin(identity.Guid, identity.Name, identity.Version, rows, hard));
                    }
                    catch { /* identity never throws; a plugin that did is still unloaded below */ }
                }
                string fileName = Path.GetFileName(item.Path);
                var unit = new SuspendedUnit(item.Path, item.Stem, item.ImageSha, item.LastWrite, plugins, node.Dependencies);
                string line = PluginSuspendPolicy.FormatSuspended(fileName, node.Guids);
                Note(line);
                lines.Add(line);
                Unload(item, PluginUnloadSlot.Suspend, releaseIcons: false);
                ByPath.Remove(item.Path);
                Failed.Remove(item.Path);
                Paused.Add(unit);
                done.AddRange(node.Guids);
            }
        }
        report = string.Join(" ", lines);
        return done.ToArray();
    }

    /// <summary>
    /// <c>plugin\trow</c> or <c>plugin\trow\t1</c> (the <c>1</c> marks a declared ServerMustHave row)
    /// into rows per plugin and hard rows per plugin.
    /// </summary>
    internal static Dictionary<string, List<string>> ParseRowOwners(string[]? rowOwners, out Dictionary<string, List<string>> hard)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        hard = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (rowOwners == null) return map;
        foreach (var entry in rowOwners)
        {
            if (string.IsNullOrEmpty(entry)) continue;
            var f = entry.Split('\t');
            if (f.Length < 2 || f[0].Length == 0 || f[1].Length == 0) continue;
            if (!map.TryGetValue(f[0], out var list)) map[f[0]] = list = new List<string>();
            if (!list.Contains(f[1])) list.Add(f[1]);
            if (f.Length > 2 && f[2] == "1")
            {
                if (!hard.TryGetValue(f[0], out var h)) hard[f[0]] = h = new List<string>();
                if (!h.Contains(f[1])) h.Add(f[1]);
            }
        }
        return map;
    }

    /// <summary>
    /// Keep the given plugins paused when their DLL is only resuming (not loaded yet). Returns the
    /// GUIDs kept. Core calls this when a resume released on a leave would otherwise finish loading
    /// on a server that does not run the plugin.
    /// </summary>
    public static string[] KeepPaused(string[]? guids)
    {
        var kept = Paused.CancelResume(guids);
        if (kept.Count > 0) Note("Reload: " + string.Join(", ", kept) + " stays paused (the server joined does not run it).");
        return kept.ToArray();
    }

    /// <summary>
    /// End the pause of the DLLs holding <paramref name="guids"/> (null: every paused DLL), plus the
    /// paused DLLs they depend on and the paused DLLs that depend on them, in dependency order. Each
    /// is marked resuming and stepped once now (<see cref="StepResume"/>); one that cannot load yet
    /// (its previous plugin ALC has not collected, or the file is inside the settle window) stays
    /// resuming and every scan steps it again until it loads. It is never marked Failed for that.
    /// Returns only the GUIDs that are loaded when this returns. Main thread only.
    /// </summary>
    public static string[] Resume(string[]? guids, out string report)
    {
        var loaded = new List<string>();
        var lines = new List<string>();
        foreach (var unit in Paused.BeginResume(guids))
            if (StepResume(unit, lines)) foreach (var p in unit.Plugins) loaded.Add(p.Guid);
        foreach (var line in lines) Note(line);
        report = string.Join(" ", lines);
        return loaded.ToArray();
    }

    /// <summary>
    /// One attempt to load a resuming unit. True when it is loaded now. The collect pulse is
    /// bounded per unit (<see cref="PluginSuspendPolicy.MaxCollectPulses"/>) and at most one per call,
    /// so a scan never blocks for more than one pulse per unit.
    /// </summary>
    private static bool StepResume(SuspendedUnit unit, List<string> lines)
    {
        string fileName = Path.GetFileName(unit.Path);
        string stem = unit.Stem.Length > 0 ? unit.Stem : Path.GetFileNameWithoutExtension(unit.Path);
        bool exists = File.Exists(unit.Path);
        DateTime write = unit.LastWrite;
        if (exists)
        {
            try { write = File.GetLastWriteTimeUtc(unit.Path); }
            catch { exists = false; }
        }
        bool settled = (DateTime.UtcNow - write).TotalSeconds >= SettleSeconds;
        bool Blocked() => LatchedPluginAlcsAlive() || ShouldRefuseNewPluginAlc(stem);

        bool dependencyPending = Paused.WaitsOnDependency(unit);
        var step = PluginSuspendPolicy.DecideResume(exists, settled,
            exists && settled && !dependencyPending && Blocked(), unit.CollectPulses, dependencyPending);
        if (step == PluginSuspendPolicy.ResumeStep.PulseThenRetry)
        {
            unit.CollectPulses++;
            try { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            catch { /* the next scan retries */ }
            step = Blocked() ? PluginSuspendPolicy.ResumeStep.Wait : PluginSuspendPolicy.ResumeStep.Load;
        }

        switch (step)
        {
            case PluginSuspendPolicy.ResumeStep.Forget:
                Paused.Remove(unit.Path);
                lines.Add(PluginSuspendPolicy.FormatDeletedWhilePaused(fileName));
                return false;
            case PluginSuspendPolicy.ResumeStep.Wait:
                string note = dependencyPending
                    ? PluginSuspendPolicy.FormatResumeWaitingOnDependency(fileName)
                    : PluginSuspendPolicy.FormatResumePending(fileName, settled, unit.CollectPulses);
                if (!string.Equals(note, unit.LastNote, StringComparison.Ordinal))
                {
                    unit.LastNote = note;
                    lines.Add(note);
                }
                return false;
        }

        Paused.Remove(unit.Path);
        lines.Add(PluginSuspendPolicy.FormatResumed(fileName, unit.ChangedWhileSuspended));
        var fresh = Load(unit.Path, write);
        if (fresh != null)
        {
            ByPath[unit.Path] = fresh;
            Failed.Remove(unit.Path);
            return true;
        }
        // A real load failure (refused image, exception): the pause is over and the file is handled
        // like any other failed load, retried when it changes.
        Failed[unit.Path] = write;
        return false;
    }

    private static void Note(string text)
    {
        ProcessHost.Log?.LogInfo(text);
        lock (Events) Events.Add(text);
    }

    public static List<string> RescanNow()
    {
        Scan();
        lock (Events)
        {
            var copy = new List<string>(Events);
            Events.Clear();
            return copy;
        }
    }

    public static List<string> LoadedPluginNames()
    {
        var names = new List<string>();
        foreach (var loaded in ByPath.Values)
            foreach (var plugin in loaded.Plugins)
                // Job FREL2 (R61 finding 1): this was a bare .Name on a nullable metadata read —
                // one plugin BepInEx cannot describe threw an NRE out of the whole listing, so the
                // bridge's status answer was an error instead of the other plugins' names.
                names.Add(IdentityOf(plugin).Name);
        return names;
    }

    /// <summary>
    /// Job HOT2: describe the loaded plugin whose BepInEx GUID is <paramref name="guid"/> —
    /// additive, read-only, and the one thing <see cref="AdmissionSnapshot"/> cannot get from the
    /// existing surface (<see cref="LoadedPluginNames"/> answers names, not GUIDs).
    ///
    /// <para>Returns plain strings only: the caller is a snapshot a collectible Core generation
    /// reads, and handing it the <c>BasePlugin</c>, its <c>Type</c> or its <c>Assembly</c> would
    /// pin the very ALC the snapshot exists to report on. <paramref name="imageSha256"/> is the
    /// baseline hash the allowlist decided on, which is "" for a compatibility-rewritten image
    /// exactly as <c>Loaded.ImageSha</c> records it.</para>
    /// </summary>
    public static bool TryDescribeLoadedPlugin(
        string? guid,
        out string name,
        out string version,
        out string imageSha256,
        out string file)
    {
        name = "";
        version = "";
        imageSha256 = "";
        file = "";
        string want = (guid ?? "").Trim();
        if (want.Length == 0) return false;
        foreach (var loaded in ByPath.Values)
        {
            if (loaded == null) continue;
            foreach (var plugin in loaded.Plugins)
            {
                if (plugin == null) continue;
                PluginIdentity identity;
                // One plugin BepInEx cannot describe must not abort the lookup for the rest
                // (the FREL2 lesson: IdentityOf never throws, but the enumeration can).
                try { identity = IdentityOf(plugin); }
                catch { continue; }
                if (!string.Equals(identity.Guid, want, StringComparison.OrdinalIgnoreCase)) continue;
                name = identity.Name;
                version = identity.Version;
                imageSha256 = loaded.ImageSha ?? "";
                file = loaded.Path ?? "";
                return true;
            }
        }

        return false;
    }

    public static void Tick()
    {
        // Latch poll lives on ProcessHost.Tick only (R17 finding 5): this Scan
        // must not walk AssemblyLoadContext.All every frame.
        // Job FBUFF4 (R30 finding 18) armed the process-exit signal here; R31 finding 18 moved
        // that up to ProcessHost.Tick, above the CoreAttached gate, because a Core-less process
        // never reaches this method and so never armed it at all.
        double now = Clock.Elapsed.TotalSeconds;
        if (now < _nextScan) return;
        _nextScan = now + ScanIntervalSeconds;
        try
        {
            Scan();
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogError($"Reload scan failed: {e}");
        }
    }

    private static void Scan()
    {
        // Dev/Sample resolve Progenitor by simple name against the loaded Core
        // generation. A Core-less process cannot bind those plugins; CoreWatch
        // still Installs a matching image, then CoreAttached becomes true.
        if (!ProcessHost.CoreAttached && !CoreReloadPolicy.MayScanPluginsWithoutCore) return;
        Directory.CreateDirectory(Folder);
        // Released pauses that could not load yet, dependencies first (not file-name order), and
        // never through the Failed path. A unit loaded here is in ByPath with its current stamp, so
        // the file loop below leaves it alone.
        foreach (var resumingUnit in Paused.ResumingInOrder())
        {
            var resumeLines = new List<string>();
            StepResume(resumingUnit, resumeLines);
            foreach (var line in resumeLines) Note(line);
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Job FORD1: three phases instead of load-as-you-walk. (1) Decide what changed, what left and
        // what is new, loading nothing. (2) Unload what leaves together with every loaded plugin that
        // needs it, dependents first. (3) Load everything not loaded in dependency order. A folder
        // without dependencies loads in file-name order exactly as before.
        var candidates = new List<LoadCandidate>();
        var leaving = new List<Leaving>();
        foreach (var file in Directory.GetFiles(Folder, "*.dll"))
        {
            seen.Add(file);
            var write = File.GetLastWriteTimeUtc(file);
            // Per-server mod pause: a suspended DLL stays unloaded until Resume, including after a
            // redeploy (the new stamp is kept so the resume loads the new build).
            if (Paused.SkipInScan(file, write, out bool changedWhilePaused))
            {
                if (changedWhilePaused) Note(PluginSuspendPolicy.FormatSkipped(Path.GetFileName(file)));
                continue;
            }
            // Still resuming after the step above: leave it to the next scan.
            if (Paused.TryGet(file, out var resuming) && resuming.Resuming) continue;
            if ((DateTime.UtcNow - write).TotalSeconds < SettleSeconds) continue;

            if (ByPath.TryGetValue(file, out var loaded))
            {
                // Job FINJ1: a process-lifetime plugin whose file came back (or went back to the
                // running build) owes no restart any more.
                if (loaded.Pending.Length > 0) SetPending(loaded, Il2CppInjectionPolicy.PendingFor(true, loaded.LoadedWrite, write));
                if (loaded.LastWrite == write) continue;
                // Job FHARM8 (the mirror FHARM7 owed). A plugin that took ctx.UnsafeHarmony
                // pinned its own ALC and the Core generation that applied its patches for the
                // life of the process; swapping it in place would load the new build beside the
                // pinned old one and do it again on the next save. Say so and keep the running
                // build. The write stamp is adopted so the refusal is one line per change rather
                // than one a second; deleting the file still unloads, and a restart is what
                // actually picks the new build up.
                if (TryRefuseUnsafeSwap(file, loaded))
                {
                    loaded.LastWrite = write;
                    continue;
                }
                // Job FINJ1. A plugin that registered its own IL2CPP types cannot be reloaded in
                // place: the next generation's RegisterTypeInIl2Cpp throws "already injected" and
                // whatever it built on the type never comes back. Keep the running build, adopt the
                // stamp (one line per change), and owe a restart.
                if (Il2CppInjectionPolicy.Decide(LifetimeEvent.FileChanged, IsProcessLifetime(loaded), loaded.Injection.CoreBound)
                    == LifetimeAction.KeepRunning)
                {
                    SetPending(loaded, Il2CppInjectionPolicy.PendingFor(true, loaded.LoadedWrite, write));
                    loaded.LastWrite = write;
                    continue;
                }
                // Job FBUFF4 (R30 findings 3 and 15): the replacement image is read, hashed and put
                // through the allow store exactly once. That single verdict picks the unload reason
                // *and* is handed to the Load that follows, so the reason a plugin was told can never
                // disagree with what the loader then did — and a file that simply cannot be read this
                // tick (the deploy tool still has it open) is not a verdict at all: nothing unloads
                // and the next scan tries again.
                var verdict = CheckReplacement(file, out var bytes, out var hash, out var allow, out string readError);
                if (!PluginReplacementPolicy.MayUnloadForReplacement(verdict))
                {
                    Note(PluginReplacementPolicy.FormatUnreadable(Path.GetFileName(file), readError));
                    continue;
                }
                int reason = PluginReplacementPolicy.ReasonFor(verdict);
                leaving.Add(new Leaving
                {
                    Loaded = loaded,
                    Reason = reason,
                    Replacement = new LoadCandidate
                    {
                        File = file,
                        Write = write,
                        Bytes = bytes,
                        Hash = hash,
                        Allow = allow,
                        HaveVerdict = bytes != null,
                        // R31 finding 24: the plugin was told Swap and kept its live state; if the load
                        // that follows fails, say that its state is not swept.
                        UnloadedForReplacement = reason == PluginUnloadSlot.Swap,
                    },
                });
                continue;
            }
            if (Failed.TryGetValue(file, out var failedWrite) && failedWrite == write) continue;
            candidates.Add(new LoadCandidate { File = file, Write = write });
        }

        foreach (var path in new List<string>(ByPath.Keys))
        {
            if (seen.Contains(path)) continue;
            Failed.Remove(path);
            // Job FINJ1: unloading a process-lifetime plugin releases nothing and leaves its injected
            // class behind; it stays loaded until the game restarts.
            var gone = ByPath[path];
            if (Il2CppInjectionPolicy.Decide(LifetimeEvent.FileRemoved, IsProcessLifetime(gone), gone.Injection.CoreBound)
                == LifetimeAction.KeepRunning)
            {
                SetPending(gone, Il2CppInjectionPolicy.PendingRemoved);
                continue;
            }
            leaving.Add(new Leaving { Loaded = gone, Reason = PluginUnloadSlot.Removal });
        }

        UnloadLeaving(leaving, candidates, seen);
        LoadInOrder(candidates, seen);

        // Deleting a paused mod ends its pause: there is nothing left to resume.
        foreach (var gone in Paused.DropMissing(seen))
            Note(PluginSuspendPolicy.FormatDeletedWhilePaused(Path.GetFileName(gone.Path)));

        ScanDeltas();
    }

    /// <summary>
    /// Job FENC2 (roadmap M4 item 5): the method-body inner loop. <c>tools/enc --watch</c> drops a
    /// <c>&lt;plugin&gt;.&lt;n&gt;.delta</c> here and this applies it with
    /// <c>MetadataUpdater.ApplyUpdate</c>, which replaces the bodies in place — no unload, no
    /// re-<c>Load()</c>, nothing owner-keyed touched. Four things must hold, and the file is
    /// deleted either way so a refusal is logged once rather than once a second:
    ///
    /// <list type="number">
    /// <item>the runtime allows metadata updates at all (the launcher set
    /// <c>DOTNET_MODIFIABLE_ASSEMBLIES=debug</c>; nothing this process does can turn it on);</item>
    /// <item>the header's baseline sha256 is the sha256 of the image the allowlist admitted for
    /// that plugin, so a delta from a stale build cannot reach a swapped plugin;</item>
    /// <item>the sequence is the next one expected, so a missed generation is a refusal rather
    /// than a silently skipped edit;</item>
    /// <item>the delta itself passes the allowlist. It is arbitrary IL — the eighth automatic
    /// code-loading path, gated exactly like the other seven.</item>
    /// </list>
    ///
    /// No settle window: the watcher writes through a temp plus a move, so a delta is either
    /// there whole or not there. A delta whose <c>ApplyUpdate</c> throws (a rude edit the
    /// watcher's conservative classifier let through) is logged and dropped and <em>nothing else
    /// happens</em>: the developer's normal rebuild-and-deploy is the generation swap, and the
    /// watcher has already told them to do it.
    ///
    /// Job FENC3 (R38 finding 3) adds the order. The names are <c>&lt;plugin&gt;.&lt;n&gt;.delta</c>
    /// and an ordinal sort of them stops being generation order at ten, so
    /// <see cref="DeltaApply.NextInScanOrder"/> parses the sequence out of the name and this takes
    /// exactly ONE delta per tick, the lowest. The scan runs once a second; nothing waits long,
    /// and the second delta of a pair is never judged against a sequence the first just moved.
    /// </summary>
    private static void ScanDeltas()
    {
        string[] files;
        try { files = Directory.GetFiles(Folder, "*.delta"); }
        catch (Exception e) { ProcessHost.Log?.LogWarning("Reload: delta scan: " + e.Message); return; }
        if (files.Length == 0) return;

        string? file = DeltaApply.NextInScanOrder(files);
        if (file == null) return;
        {
            string fileName = Path.GetFileName(file);
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch { return; } // still landing; the next scan sees it
            ApplyDelta(file, fileName, bytes);
            try { File.Delete(file); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"Reload: could not delete {fileName}: {e.Message}"); }
        }
    }

    private static void ApplyDelta(string file, string fileName, byte[] bytes)
    {
        if (!DeltaApply.IsSupported && System.Threading.Interlocked.Exchange(ref _loggedEncDisabled, 1) == 0)
            Note("Reload: EnC unavailable — " + DeltaApply.DisabledMessage);

        DeltaApply.TryParse(bytes, out var header, out string parseError);
        Loaded? target = null;
        if (header != null && header.Plugin.Length > 0)
        {
            foreach (var loaded in ByPath.Values)
            {
                // Job FENC7 (R42 finding 4): matched on the FILE STEM, the one identity in this
                // pipeline, through the shared test. The watcher never loads the image, so the
                // stem is the only name it can put in the header; matching on the assembly name
                // refused every delta for the life of the session wherever the two differed,
                // while FENC6's status file went on reading loaded=1 with the right sha.
                if (!DeltaApply.NamesPlugin(loaded.Stem, header.Plugin)) continue;
                target = loaded;
                break;
            }
        }

        var allow = ReloadAllowStore.Decide(fileName, ReloadAllowPolicy.Sha256Hex(bytes));
        var verdict = parseError.Length > 0
            ? DeltaVerdict.Malformed
            : DeltaApply.Decide(
                header, DeltaApply.IsSupported, target?.ImageSha,
                target?.NextDeltaSequence ?? DeltaApply.FirstSequence, allow,
                target?.Rewritten ?? false);

        if (verdict != DeltaVerdict.Apply)
        {
            string why = verdict == DeltaVerdict.Malformed && parseError.Length > 0
                ? parseError
                : DeltaApply.RefusalReason(verdict, header, target?.ImageSha,
                    target?.NextDeltaSequence ?? DeltaApply.FirstSequence);
            Note(DeltaApply.FormatRefused(fileName, why));
            // The watcher rebaselines off the status file, so it must be readable after a refusal
            // too: a refusal is precisely when the two sequences are out of step (job FENC3).
            WriteDeltaState(target);
            return;
        }

        if (!DeltaApply.TryApply(target!.Assembly, header!, bytes, out string applyError))
        {
            Note(DeltaApply.FormatRude(fileName, applyError));
            WriteDeltaState(target);
            return;
        }
        target.NextDeltaSequence = header!.Sequence + 1;
        Note(DeltaApply.FormatApplied(header));
        WriteDeltaState(target);
    }

    /// <summary>
    /// Job FENC3 (R38 finding 2): publish this plugin's delta position for <c>tools/enc</c>.
    ///
    /// The reloader's sequence and the watcher's were two counters that could not see each other,
    /// so a server restart, a run without <c>DOTNET_MODIFIABLE_ASSEMBLIES=debug</c>, or one rude
    /// apply desynchronized them permanently: the watcher counted up on every emit, the reloader
    /// stayed where it was, and every later delta was refused out-of-order and deleted until the
    /// developer rebuilt. The reloader's own counter is still <b>memory only</b> — it is per
    /// loaded image and dies with the process, which is the whole point — and this file is the
    /// one-way status channel the watcher reads to rebaseline from it.
    ///
    /// It is written NEXT TO the reload folder, never in it, so no scan can pick it up. Best
    /// effort by construction: the reload folder may be read-only in a locked-down install and a
    /// missing status file simply leaves the watcher where it was.
    /// </summary>
    private static void WriteDeltaState(Loaded? loaded)
    {
        // Job FENC6 (R41 finding 5): keyed on the FILE STEM, like every other touch of this file,
        // so a successful load OVERWRITES the loaded=0 refusal a previous failed load of the same
        // DLL left behind — including the ordinary case where that load threw before its assembly
        // name could be read. The assembly name still goes inside the file.
        if (loaded == null) return;
        DeltaApply.WriteStateFile(
            Folder, loaded.Stem, loaded.AssemblyName, loaded.ImageSha,
            loaded.NextDeltaSequence - DeltaApply.FirstSequence, loaded.Rewritten,
            loaded.LoadNonce);
    }

    /// <summary>
    /// The plugin is gone; so is its position. Pairs with <see cref="DropDeltasFor"/>. Keyed on
    /// the file stem (job FENC6), so it also clears a refusal written for an unreadable assembly.
    /// </summary>
    private static void DropDeltaStateFor(string? stem) => DeltaApply.DeleteStateFile(Folder, stem);

    /// <summary>
    /// Job FENC5 (R40 finding 9): a load that threw leaves a REFUSAL in the channel, not silence.
    ///
    /// The failed-load path routes through <see cref="Unload"/>, which drops the status file —
    /// correctly, because the position that file published belongs to an image that is gone.
    /// Stopping there left the watcher with no file at all, and a missing file used to read as
    /// "carry on": it kept emitting deltas for a plugin nothing held, every one refused for want
    /// of a target, and a refusal with no target writes no status file, so the channel never
    /// reopened. Writing <c>loaded=0</c> and the reason keeps it open, and the watcher blocks on
    /// it and prints the reason. An ordinary unload still deletes the file: nothing is loading,
    /// nothing failed, and the watcher's own missing-file rule says so.
    ///
    /// Job FENC6 (R41 finding 5): named by the FILE STEM, not by a best guess at the assembly
    /// name. FENC5 wrote the refusal under the stem only when the name was unreadable — which is
    /// the usual case here — while the success write and the unload delete keyed on the assembly
    /// name, so where the two differed nothing could ever clear this file and the watcher blocked
    /// on it for the rest of time. One key, every writer, every deleter.
    /// </summary>
    private static void WriteFailedDeltaState(string? stem, string? plugin, string? reason) =>
        DeltaApply.WriteFailedStateFile(Folder, stem, plugin, reason);

    /// <summary>
    /// A generation swap invalidates every delta: they were emitted against the image that is
    /// going away. Deleting them here is what stops the next scan from feeding a delta for
    /// baseline N to the freshly loaded image N+1 (it would be refused on the baseline check
    /// anyway; this keeps the log honest and the folder clean).
    ///
    /// Job FENC7 (R42 finding 4): by FILE STEM, because that is what the watcher names the file
    /// on disk and what the header inside it carries. Keyed on the assembly name this swept
    /// nothing wherever the two names differed.
    /// </summary>
    private static void DropDeltasFor(string? stem)
    {
        if (string.IsNullOrEmpty(stem)) return;
        try
        {
            foreach (var file in Directory.GetFiles(Folder, stem + ".*.delta"))
            {
                try { File.Delete(file); }
                catch { /* the next scan refuses it on the baseline check */ }
            }
        }
        catch { /* folder gone; nothing to drop */ }
    }

    /// <summary>
    /// One evaluation of a replacement image while the previous version is still live: read the
    /// bytes, hash them, ask the allow store. <see cref="ReplacementVerdict.Unreadable"/> is a
    /// non-answer, not a refusal — R30 finding 15: a transient read failure used to be reported
    /// as "refused", which the caller turned into a Removal and a sweep of live player state.
    /// The bytes and the decision are handed back so <see cref="Load"/> does not read the file a
    /// second time (the TOCTOU where a file allowed at T1 is refused at T2 and the plugin never
    /// comes back).
    /// </summary>
    private static ReplacementVerdict CheckReplacement(
        string file, out byte[]? bytes, out string? hash, out AllowDecision allow, out string readError)
    {
        bytes = null;
        hash = null;
        allow = default;
        readError = "";
        try { bytes = File.ReadAllBytes(file); }
        catch (Exception e)
        {
            readError = e.Message;
            return ReplacementVerdict.Unreadable;
        }
        hash = ReloadAllowPolicy.Sha256Hex(bytes);
        allow = ReloadAllowStore.Decide(Path.GetFileName(file), hash);
        return ReloadAllowPolicy.MayLoad(allow) ? ReplacementVerdict.Allowed : ReplacementVerdict.Refused;
    }

    /// <summary>
    /// First sight of a file: read and decide here. A replacement whose predecessor was just
    /// unloaded comes through the overload below with the decision the unload reason was taken
    /// from, so the two can never disagree (R30 finding 15).
    /// </summary>
    private static Loaded? Load(string file, DateTime write)
    {
        string fileName = Path.GetFileName(file);

        // Identity first (job FRLD): the bytes are read, hashed and checked against the
        // optional operator allowlist *before* a collectible ALC is minted, so a refused
        // file never reaches LoadFromStream and never leaves an ALC behind. Scan stamps
        // the write time into Failed when this returns null, so the refusal is logged once
        // per version of the file rather than once a second.
        byte[] bytes;
        try { bytes = File.ReadAllBytes(file); }
        catch (Exception e)
        {
            Note($"Reload: reading {fileName} failed: {e.Message}");
            return null;
        }

        string hash = ReloadAllowPolicy.Sha256Hex(bytes);
        return Load(file, write, bytes, hash, ReloadAllowStore.Decide(fileName, hash));
    }

    private static Loaded? Load(string file, DateTime write, byte[] bytes, string hash, AllowDecision allow)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        string fileName = Path.GetFileName(file);

        long size = bytes.LongLength;
        Note(ReloadAllowPolicy.FormatPluginLoad(fileName, hash, size, allow));
        if (!ReloadAllowPolicy.MayLoad(allow))
        {
            Note(ReloadAllowPolicy.FormatRefusedPlugin(fileName, hash, size, allow));
            return null;
        }

        // Job FCOMPAT2 (M4 item 2, docs/modules.md "Compatibility rewriter"): the mirrored half
        // of PluginReloader.Load's hunk. This is the reloader that runs in production, so the
        // rewriter has to exist here too (and does, as the DriftMap.cs / PluginRewriter.cs
        // mirrors next to ReloadAllowPolicy.cs). Same three properties: the allowlist verdict
        // above was taken on the ORIGINAL bytes and is never re-taken, a refused file never
        // reaches Cecil, and the rewritten image is never hashed for a gate nor written to disk.
        // [Compat] RewriteMode defaults to DryRun.
        //
        // Job FCOMPAT3 adds two things to the same hunk, mirroring PluginReloader.Load. The
        // allowlist is handed over as a delegate so the mirrored rewriter can gate the drift map
        // on the operator's list without naming a type whose namespace differs on each side; and
        // the pass walks the plugin's own folder for local dependency DLLs and rewrites them
        // leaf-first, each through this same gate order.
        CompatRewriteHost.AllowTokenReader ??= static (n, h) => ReloadAllowPolicy.Token(ReloadAllowStore.Decide(n, h));
        var original = bytes;
        var rewrite = CompatRewriteHost.RewriteForLoad(Paths.BepInExRootPath, file, bytes, Note);
        bytes = rewrite.Image;
        // Job FENC3 (R38 finding 1). RewriteBundle.Image is documented as "the input array unless
        // applied", so this reference comparison is the only place the rewrite verdict exists as a
        // fact rather than as a hope: after this line nothing can tell a rewritten load from an
        // unrewritten one, because ImageSha is the hash of the ORIGINAL bytes in both cases and
        // that is exactly the number tools/enc computes from the same file on disk. A preloaded
        // dependency counts too — the plugin binds a rewritten helper, which is the same mismatch
        // one level down.
        bool rewritten = !ReferenceEquals(rewrite.Image, original) || rewrite.Dependencies.Count > 0;
        // Job FINJ1: read the ORIGINAL image for IL2CPP type injection before anything of it loads.
        var injection = Il2CppInjectionPolicy.Scan(original);
        // A second copy of a kept process-lifetime plugin (the Install tab's version swap under another
        // file name, a renamed redeploy) would register the same type names again and fail exactly as an
        // in-place reload does. Refused; Scan stamps Failed, so this is one line per version of the file.
        if (ProcessLifetimeHolderOf(file, injection.AssemblyName) is { } holder)
        {
            Note(Il2CppInjectionPolicy.FormatSecondCopyRefused(fileName, holder));
            return null;
        }

        // evenIfLatched allows *other* plugin names after a wait miss; the
        // per-name prefix check always applies so we never mint a second
        // progenitor-reload:{name}:* against the still-installed Core
        // (R17 finding 4). Idle Scan still refuses everything while latched.
        if (!CoreReloadPolicy.MayCreatePluginAlc(LatchedPluginAlcsAlive(), _loadWhileLatched)
            || ShouldRefuseNewPluginAlc(name))
        {
            Note($"Reload: skipped {Path.GetFileName(file)}; previous plugin ALC still alive; restart the process.");
            return null;
        }
        var context = new AssemblyLoadContext($"progenitor-reload:{name}:{write.Ticks}", isCollectible: true);
        // Leaf-first, into the plugin's own collectible context and BEFORE the plugin: a name
        // already loaded in an ALC wins over the file on disk, which is how the rewritten
        // dependency becomes the one the plugin binds. Empty in every mode but Apply.
        rewrite.PreloadInto(
            (_, image) =>
            {
                using var dependency = new MemoryStream(image, writable: false);
                context.LoadFromStream(dependency);
            },
            Note);
        // ImageSha is the hash the allowlist decided on — the ORIGINAL bytes, before the compat
        // rewriter touched them — because that is the identity a delta names. When the rewriter
        // actually changed something there is no such identity: the baseline is CLEARED and every
        // delta for this plugin is refused with DeltaApply.RewrittenMessage. Until job FENC3 the
        // comment here claimed the baseline check enforced that, which it could not: the two
        // hashes are hashes of the same bytes, so they were always equal (R38 finding 1).
        var loaded = new Loaded
        {
            Path = file,
            Stem = name, // job FENC6: the status file's one key, known before anything loads
            LastWrite = write,
            LoadedWrite = write,
            Injection = injection,
            Info = PluginLoadOrder.Read(fileName, original),
            Context = context,
            BoundCore = CoreLoader.Assembly,
            ImageSha = rewritten ? "" : hash,
            Rewritten = rewritten,
        };
        loaded.Resolving = (_, an) => OnPluginResolving(loaded, an);
        context.Resolving += loaded.Resolving;
        try
        {
            Assembly assembly;
            using (var stream = new MemoryStream(bytes, writable: false))
                assembly = context.LoadFromStream(stream);

            loaded.Assembly = assembly;
            string asmName;
            try { asmName = assembly.GetName().Name ?? ""; }
            catch { asmName = ""; }
            loaded.AssemblyName = asmName;
            Note(ReloadAllowPolicy.FormatPluginLoadVerbose(fileName, hash, size, asmName));
            if (rewritten)
                Note("Reload: " + asmName + " takes no method-body deltas — " + DeltaApply.RewrittenMessage);
            // Job FENC3: publish the starting position (last applied 0, so the next expected is 1)
            // so a watcher that was already running resynchronizes to this load instead of
            // counting up against a sequence that just went back to the beginning.
            WriteDeltaState(loaded);
            WarnIfCoreVersionSkew(assembly);

            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(BasePlugin).IsAssignableFrom(type)) continue;
                // Job FREL2 (R61 finding 1): DiscoveryInherit, and it is false, because that is
                // what BepInEx's MetadataHelper uses. Discovery used to inherit (the default of
                // the generic overload) while BepInEx did not, so a type that took [BepInPlugin]
                // from a base class was admitted here and then described by nobody: every
                // GetMetadata read in this file returned null and NRE'd. Agreeing with BepInEx
                // removes the trigger at the root rather than guarding its symptoms.
                if (type.GetCustomAttribute<BepInPlugin>(DiscoveryInherit) == null)
                {
                    // Say so once rather than dropping it silently: a plugin whose attribute is
                    // inherited is not a BepInEx plugin — BepInEx's own chainloader would not
                    // load it either — and "has no BepInPlugin types" below would be misleading.
                    if (type.GetCustomAttribute<BepInPlugin>(inherit: true) != null)
                        ProcessHost.Log?.LogWarning(
                            $"Reload: {type.FullName} inherits [BepInPlugin] from a base type; BepInEx reads "
                            + "the attribute with inherit:false and cannot describe it, so it is not loaded. "
                            + "Put [BepInPlugin] on the plugin class itself.");
                    continue;
                }

                var plugin = (BasePlugin)Activator.CreateInstance(type)!;
                var identity = IdentityOf(plugin);
                plugin.Load();
                loaded.Plugins.Add(plugin);
                Note($"Reload: loaded {identity.Name} {identity.Version} from {Path.GetFileName(file)}.");
            }
            if (loaded.Plugins.Count == 0)
                ProcessHost.Log?.LogWarning($"Reload: {Path.GetFileName(file)} has no BepInPlugin types.");
            if (injection.Injects)
                ProcessHost.Log?.LogWarning(Il2CppInjectionPolicy.FormatDetected(fileName, injection.Sites, injection.CoreBound));
            return loaded;
        }
        catch (Exception e)
        {
            Note($"Reload: loading {Path.GetFileName(file)} failed: {e}");
            // Whatever of this image did load is going away for good.
            Unload(loaded, PluginUnloadSlot.Removal, releaseIcons: false);
            // Job FENC5 (R40 finding 9): Unload just dropped the status file; reopen it as a
            // refusal so the watcher blocks with this reason instead of emitting deltas into a
            // process that holds no such plugin. Job FENC6 (R41 finding 5): the FILE STEM is the
            // key the watcher looks this file up by and the key every writer and the deleter use,
            // so the refusal lands where the next successful load of this DLL will overwrite it.
            // The assembly name, if the load got far enough to read one, goes inside the file.
            WriteFailedDeltaState(loaded.Stem, loaded.AssemblyName, e.GetType().Name + ": " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// Unload one file's plugins. <paramref name="reason"/> is a <see cref="PluginUnloadSlot"/>
    /// code published for the duration of each <c>plugin.Unload()</c>, so a
    /// <c>ProgenitorPlugin</c> in the collectible Core sees it in
    /// <c>OnUnload(ctx, PluginUnloadReason)</c> (job FBUFF4, R30 finding 3). This is the
    /// production reloader: before FBUFF4 it published nothing and Core inferred Removal for an
    /// ordinary plugin redeploy, sweeping the live state job FBUFF2 exists to keep. A process
    /// exit overrides whatever the caller asked for — that unload is a Shutdown whatever else
    /// was in flight (R30 finding 18).
    /// </summary>
    private static void Unload(Loaded loaded, int reason, bool releaseIcons = false)
    {
        if (PluginUnloadSlot.ProcessExiting) reason = PluginUnloadSlot.Shutdown;
        var context = loaded.Context;
        var asm = loaded.Assembly;
        var resolving = loaded.Resolving;
        string? assemblyName = null;
        try { assemblyName = loaded.Assembly?.GetName().Name; }
        catch { assemblyName = null; }
        loaded.Resolving = null;
        loaded.Assembly = null;
        loaded.BoundCore = null;
        loaded.Context = null;
        Failed.Remove(loaded.Path);
        var pluginIds = new List<string>();

        // Drop the Resolving capture of `loaded` (and BoundCore) before Unload so
        // the ALC is not kept alive by its own handler after bookkeeping is gone.
        if (context != null && resolving != null)
        {
            try { context.Resolving -= resolving; }
            catch { /* already unloading */ }
        }

        string? contextName = null;
        try { contextName = context?.Name; }
        catch { contextName = null; }

        // Job FCOMPAT4 (R34 finding 4), mirroring PluginReloader.Unload: since FCOMPAT3 a plugin
        // owns more than one assembly — the rewriter preloads each rewritten plugin-local
        // dependency into this same collectible context — and every sweep below is keyed on an
        // assembly. Sweeping only the plugin leaves a helper DLL's handler holding the
        // LoaderAllocator, and the ALC never collects. Core's PluginOwnedAssemblies is the same
        // rule; Bootstrap may not compile-reference Core, so the derivation is inline here (and
        // the EventBus half runs on Core's side, through Sweep -> ProcessHost.SweepPlugin).
        var owned = new List<Assembly>();
        if (asm != null) owned.Add(asm);
        try
        {
            if (context != null && context.IsCollectible)
            {
                foreach (var other in context.Assemblies)
                    if (other != null && !owned.Contains(other)) owned.Add(other);
            }
        }
        catch { /* a context mid-unload can throw on enumeration; the plugin assembly still gets swept */ }

        // ---- unload steps: Unload (job FREL3, R62 finding 3): each one guarded on its own ----
        // The marker is not decoration: ReloadHardeningTests.Every_unload_step_is_inside_a_try
        // enumerates every statement between it and the end marker and fails on a bare call. The
        // bare one it was written for was WarnIfDetourCachePins, a diagnostic sitting in front of
        // context.Unload(); the handful of statements that are deliberately outside a try are
        // named, with reasons, in that test's exemption list.
        foreach (var plugin in loaded.Plugins)
        {
            // Job FREL2 (R61 finding 1): resolved once, before anything, and never null. The
            // metadata read used to sit INSIDE the try below and AHEAD of plugin.Unload(), with
            // the icon-owner derivation between them, so anything either of those threw skipped
            // the plugin's own teardown entirely — the NRE landed in the catch and the log line
            // blamed "Unload threw". The identity is also what the icon release and the unload
            // order use, so all of them agree and none can throw for want of metadata.
            var identity = IdentityOf(plugin);

            try
            {
                string id = IconCachePolicy.OwnerId(identity.Guid, assemblyName);
                if (!pluginIds.Contains(id))
                    pluginIds.Add(id);
            }
            catch (Exception e)
            {
                // A missed icon id leaks one cached Sprite for the life of the process; it must
                // not cost the unload, which is what pins the collectible ALC.
                ProcessHost.Log?.LogWarning($"Reload: IconCachePolicy.OwnerId for {identity.Name}: {e.Message}");
            }

            try
            {
                bool ok;
                using (PluginUnloadSlot.Push(reason))
                    ok = plugin.Unload();
                Note($"Reload: unloaded {identity.Name}: {(ok ? "clean" : "plugin reported it cannot unload; continuing anyway")}");
            }
            catch (Exception e)
            {
                ProcessHost.Log?.LogError($"Reload: Unload threw for {identity.Name}: {e}");
            }

            // Always sweep even when Unload threw, and guard each step on its own so a throw in
            // one cannot skip the next: the Core-side sweep, then every owned assembly's queued
            // work. Job FREL2, matching PluginReloader.Unload.
            try { Sweep(plugin); }
            catch (Exception e) { ProcessHost.Log?.LogError($"Reload: SweepPlugin for {identity.Name}: {e}"); }
            for (int i = 0; i < owned.Count; i++)
            {
                try { WorkQueue.DropOwnedBy(plugin, owned[i], i == 0 ? contextName : null); }
                catch (Exception e) { ProcessHost.Log?.LogWarning($"Reload: MainThread.DropOwnedBy: {e.Message}"); }
            }
        }
        loaded.Plugins.Clear();

        // R18 finding 4: a plugin slot delegate is a Bootstrap static root. Dev
        // Unload may throw before its own ClearPlugin; Core Uninstall's ClearAll
        // runs after the plugin-ALC wait. Null every plugin-bound slot for this
        // ALC now so the wait is not measuring our own pin.
        for (int i = 0; i < owned.Count; i++)
        {
            try { HarmonySlots.ClearPluginContext(owned[i], i == 0 ? contextName : null); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"Reload: HarmonySlots.ClearPluginContext: {e.Message}"); }

            try { WorkQueue.DropOwnedBy(plugin: null, owned[i], i == 0 ? contextName : null); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"Reload: MainThread.DropOwnedBy(assembly): {e.Message}"); }
        }

        // Job FREL3 (R62 finding 3): guarded, and this is the one whose loss costs most. It is a
        // DIAGNOSTIC sitting immediately in front of context.Unload(): WarnIfDetourCachePins
        // guards its own scan but not the Note loop, the FormatPluginAlcPinned calls or the latch
        // publish below it, so anything thrown from there escaped Unload and the collectible
        // plugin ALC was never even asked to unload — pinned for the life of the process, after
        // which CoreLoader's PluginAlcsAlive refusal blocks every later Core swap. Exactly the
        // shape FREL2 fixed one loop up (a metadata read in front of plugin.Unload()).
        try { WarnIfDetourCachePins(asm, contextName); }
        catch (Exception e) { ProcessHost.Log?.LogWarning($"Reload: WarnIfDetourCachePins: {e.Message}"); }
        // Job FENC3: the position the status file publishes belongs to the image that is
        // unloading, so it goes with the deltas. Job FENC7 (R42 finding 4): both by STEM. The
        // deltas and the status file are two halves of one channel and they now share one key --
        // the plugin DLL's file stem, the only name available before anything loads and the only
        // name the watcher can produce.
        DropDeltasFor(loaded.Stem);
        DropDeltaStateFor(loaded.Stem);

        try { context?.Unload(); } catch (Exception e) { ProcessHost.Log?.LogWarning($"Reload: context unload: {e.Message}"); }

        // Core-swap Unload keeps the Sprite/Texture2D pair so the next generation can
        // apply-from-cache before the plugin file is back in ByPath. File-removed is
        // the only Unload that destroys the pair (process exit is ClearAll).
        if (releaseIcons)
        {
            for (int i = 0; i < pluginIds.Count; i++)
            {
                try { IconCache.ReleasePlugin(pluginIds[i]); }
                catch (Exception e) { ProcessHost.Log?.LogWarning("IconCache.ReleasePlugin: " + e.Message); }
            }
        }
        // ---- end unload steps: Unload ----
    }

    /// <summary>
    /// True when a recently unloaded plugin's methods are still in Harmony IL2CPP's
    /// append-only <c>DelegateCache</c>. That ALC will never collect; the Core swap
    /// must not wait <see cref="CoreReloadPolicy.PluginAlcWaitFrames"/>.
    /// </summary>
    public static bool AnyDetourPinned()
    {
        lock (LatchGate) return _anyDetourPinned;
    }

    private static void WarnIfDetourCachePins(Assembly? assembly, string? alcName)
    {
        List<string> methods;
        try { methods = CorePinDiagnostics.DetourCachePinsFor(assembly, alcName); }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("plugin-alc detour-cache scan: " + e.Message);
            return;
        }
        if (methods.Count == 0)
        {
            // Missing cache field: skip-wait stays off (fail closed, wait the
            // normal frames). CorePinDiagnostics already logged once.
            return;
        }
        string name = string.IsNullOrEmpty(alcName) ? CoreReloadPolicy.UnnamedAlcName : alcName;
        for (int i = 0; i < methods.Count; i++)
            Note(DetourCachePolicy.FormatPluginAlcPinned(name, methods[i]));
        lock (LatchGate)
        {
            DetourPinnedAlcs.Add(name);
            _anyDetourPinned = true;
        }
    }

    /// <summary>
    /// Progenitor and Bootstrap first (<see cref="PluginAssemblyResolver"/>). Then, job FORD1, an assembly
    /// another loaded reload-folder plugin provides (AutoLatchGates → ExampleCommandFramework): each plugin
    /// has its own context, and without this the reference fell through to the default context, which does
    /// not have it. The edge is a runtime dependency: the scan unloads this plugin before that provider and
    /// reloads it after (<see cref="PluginLoadOrder.NeedsAtRuntime"/>).
    /// </summary>
    private static Assembly? OnPluginResolving(Loaded loaded, AssemblyName name)
    {
        var own = PluginAssemblyResolver.Resolve(name.Name, loaded.BoundCore, typeof(ProcessHost).Assembly);
        if (own != null || string.IsNullOrEmpty(name.Name)) return own;
        foreach (var other in ByPath.Values)
        {
            if (other == null || ReferenceEquals(other, loaded) || other.Assembly == null) continue;
            if (string.Equals(other.AssemblyName, name.Name, StringComparison.OrdinalIgnoreCase))
                return other.Assembly;
        }
        return null;
    }

    /// <summary>
    /// Unload every reloadable plugin in reverse <c>[BepInDependency]</c> order (dependents
    /// first), talking to the <em>old</em> Core (sweep still registered). Independent
    /// plugins use last-loaded-first as the tie-break. Clears Failed so a post-swap reload
    /// of the same write stamp is not skipped. Used by a collectible Core swap; files stay
    /// on disk.
    /// </summary>
    public static int UnloadAllReverse() => UnloadAll(PluginUnloadSlot.Swap, "Core swap");

    /// <summary>
    /// The exit-time sweep (R31 finding 18). Nothing unloaded reload-folder plugins at process
    /// exit, so <c>reason=Shutdown</c> - one of the four shapes <c>docs/modules.md</c> tells an
    /// operator to expect - could never reach a plugin: the signal was live and no unload read
    /// it. <c>SelfUpdate.SweepPluginsAtExit</c> calls this from whichever exit handler fires
    /// first, after setting <see cref="PluginUnloadSlot.NoteProcessExiting"/>, so the reason is
    /// Shutdown whether it is passed here or read off the slot inside <see cref="Unload"/>.
    /// Same reverse dependency order as a Core swap; the files stay on disk.
    /// </summary>
    public static int UnloadAllForShutdown() => UnloadAll(PluginUnloadSlot.Shutdown, "process exit");

    private static int UnloadAll(int reason, string what)
    {
        var loaded = new List<Loaded>(ByPath.Count);
        foreach (var kv in ByPath)
            loaded.Add(kv.Value);

        var nodes = new PluginUnloadOrder.Node[loaded.Count];
        for (int i = 0; i < loaded.Count; i++)
            nodes[i] = DescribeNode(loaded[i], i);

        int[] order = PluginUnloadOrder.ReverseTopological(nodes);
        var keep = reason == PluginUnloadSlot.Swap ? KeptAcrossCoreSwap() : new HashSet<Loaded>();
        int n = 0;
        for (int o = 0; o < order.Length; o++)
        {
            var item = loaded[order[o]];
            // Job FINJ1: a process-lifetime plugin that does not reference Core survives the swap the
            // way a BepInEx/plugins plugin does. It never resolves Core, so its BoundCore is dropped
            // to keep the outgoing generation unpinned. One that references Core was refused before
            // the swap began (CoreLoader.RequestReload, CoreReloadOrchestrator.TryBegin); if it is
            // here anyway it is unloaded as before, because keeping it would pin the outgoing Core.
            // Job FORD1: so do the reload-folder plugins it needs at runtime (KeptAcrossCoreSwap).
            if (keep.Contains(item))
            {
                item.BoundCore = null;
                Note(IsProcessLifetime(item)
                    ? Il2CppInjectionPolicy.FormatKeptAcrossCoreSwap(Path.GetFileName(item.Path))
                    : PluginLoadOrder.FormatKeptForDependent(Path.GetFileName(item.Path)));
                continue;
            }
            Note($"Reload: {what} unloading {Path.GetFileName(item.Path)} "
                + $"(unload reason={PluginUnloadSlot.Name(reason)}).");
            Unload(item, reason, releaseIcons: false);
            ByPath.Remove(item.Path);
            Failed.Remove(item.Path);
            n++;
        }
        Failed.Clear();
        return n;
    }

    // ---- job FORD1: dependency-aware load order -------------------------------------------------

    /// <summary>Every loaded reload-folder plugin <paramref name="loaded"/> needs at runtime, transitively.</summary>
    private static List<Loaded> RuntimeProviders(Loaded loaded)
    {
        var result = new List<Loaded>();
        var queue = new List<Loaded> { loaded };
        for (int k = 0; k < queue.Count; k++)
        {
            foreach (var other in ByPath.Values)
            {
                if (other == null || queue.Contains(other)) continue;
                if (!PluginLoadOrder.NeedsAtRuntime(queue[k].Info, other.Info)) continue;
                queue.Add(other);
                result.Add(other);
            }
        }
        return result;
    }

    /// <summary>
    /// The plugins a Core swap leaves loaded: process-lifetime plugins that do not reference Core (job FINJ1)
    /// and, job FORD1, every reload-folder plugin they need at runtime, which would otherwise be swapped
    /// under them. <see cref="CoreBoundProcessLifetimeFiles"/> refuses the swap when one of those references Core.
    /// </summary>
    private static HashSet<Loaded> KeptAcrossCoreSwap()
    {
        var keep = new HashSet<Loaded>();
        foreach (var item in ByPath.Values)
        {
            if (item == null) continue;
            if (Il2CppInjectionPolicy.Decide(LifetimeEvent.CoreSwap, IsProcessLifetime(item), item.Injection.CoreBound)
                != LifetimeAction.KeepRunning) continue;
            keep.Add(item);
            foreach (var provider in RuntimeProviders(item)) keep.Add(provider);
        }
        return keep;
    }

    /// <summary>A loaded plugin that can only go away with a restart (<c>ctx.UnsafeHarmony</c> or process-lifetime).</summary>
    private static bool CannotUnloadInPlace(Loaded loaded)
    {
        if (IsProcessLifetime(loaded)) return true;
        foreach (var plugin in loaded.Plugins)
        {
            if (plugin == null) continue;
            try { if (UnsafeHarmonySlot.RefusesSwap(IdentityOf(plugin).Guid)) return true; }
            catch { return true; }
        }
        return false;
    }

    /// <summary>
    /// Phase 2 of <see cref="Scan"/>: unload what is being replaced or removed together with every loaded
    /// plugin that needs it at runtime (a hard <c>[BepInDependency]</c> or a resolved assembly reference),
    /// dependents first. A dependent comes back in phase 3, after its provider; a dependent of a removed
    /// provider waits for it. A change whose dependents include one that cannot be unloaded in place is
    /// held as a whole: the provider keeps running and a restart is owed, so nothing is left bound to a
    /// provider that is gone.
    /// </summary>
    private static void UnloadLeaving(List<Leaving> leaving, List<LoadCandidate> candidates, HashSet<string> seen)
    {
        if (leaving.Count == 0) return;
        var all = new List<Loaded>(ByPath.Values);
        var infos = all.ConvertAll(l => l.Info);

        // Hold any change whose dependents include a plugin that cannot be unloaded in place.
        for (int k = leaving.Count - 1; k >= 0; k--)
        {
            int seed = all.IndexOf(leaving[k].Loaded);
            if (seed < 0) continue;
            foreach (int i in PluginLoadOrder.DependentClosure(infos, new[] { seed }))
            {
                if (i == seed || !CannotUnloadInPlace(all[i])) continue;
                var held = leaving[k];
                string line = PluginLoadOrder.FormatHeldByPinnedDependent(Path.GetFileName(held.Loaded.Path), Path.GetFileName(all[i].Path));
                if (held.Replacement != null) held.Loaded.LastWrite = held.Replacement.Write;
                if (NotedOnce.Add(line + "|" + (held.Replacement?.Write.Ticks ?? -1))) Note(line);
                leaving.RemoveAt(k);
                break;
            }
        }
        if (leaving.Count == 0) return;

        var seeds = new List<int>();
        foreach (var l in leaving)
        {
            int i = all.IndexOf(l.Loaded);
            if (i >= 0) seeds.Add(i);
        }
        var closure = PluginLoadOrder.DependentClosure(infos, seeds);
        var units = new List<Loaded>();
        foreach (int i in closure) units.Add(all[i]);
        var nodes = new PluginUnloadOrder.Node[units.Count];
        for (int i = 0; i < units.Count; i++)
            nodes[i] = DescribeNode(units[i], all.IndexOf(units[i]));

        foreach (int o in PluginUnloadOrder.ReverseTopological(nodes))
        {
            var unit = units[o];
            string file = Path.GetFileName(unit.Path);
            var leave = leaving.Find(l => ReferenceEquals(l.Loaded, unit));
            if (leave == null)
            {
                // A dependent: it unloads with Swap (it comes back) and is loaded again after its provider.
                var provider = leaving.Find(l => PluginLoadOrder.NeedsAtRuntime(unit.Info, l.Loaded.Info))
                    ?? leaving[0];
                Note(PluginLoadOrder.FormatDependentFirst(file, Path.GetFileName(provider.Loaded.Path)));
                var write = unit.LastWrite;
                Unload(unit, PluginUnloadSlot.Swap, releaseIcons: false);
                ByPath.Remove(unit.Path);
                Failed.Remove(unit.Path);
                if (seen.Contains(unit.Path) && !candidates.Exists(c => string.Equals(c.File, unit.Path, StringComparison.OrdinalIgnoreCase)))
                    candidates.Add(new LoadCandidate { File = unit.Path, Write = write });
                continue;
            }
            if (leave.Replacement != null)
            {
                Note($"Reload: {file} changed, reloading (unload reason={PluginUnloadSlot.Name(leave.Reason)}).");
                Unload(unit, leave.Reason, releaseIcons: false);
                ByPath.Remove(unit.Path);
                candidates.Add(leave.Replacement);
            }
            else
            {
                Note($"Reload: {file} removed, unloading (unload reason={PluginUnloadSlot.Name(PluginUnloadSlot.Removal)}).");
                Unload(unit, PluginUnloadSlot.Removal, releaseIcons: true);
                ByPath.Remove(unit.Path);
            }
        }
    }

    /// <summary>
    /// Phase 3 of <see cref="Scan"/>: load <paramref name="candidates"/> in <see cref="PluginLoadOrder.Plan"/>
    /// order. A DLL whose hard dependency is missing, or whose provider did not load this pass, waits:
    /// logged once, reported, and planned again on the next scan, never stamped as failed.
    /// </summary>
    private static void LoadInOrder(List<LoadCandidate> candidates, HashSet<string> seen)
    {
        // Waits for files that are gone or loaded end here.
        foreach (var path in new List<string>(WaitingByPath.Keys))
        {
            if (!seen.Contains(path) || ByPath.ContainsKey(path)) WaitingByPath.Remove(path);
        }
        foreach (var path in new List<string>(InfoCache.Keys))
            if (!seen.Contains(path)) InfoCache.Remove(path);
        if (candidates.Count == 0) return;

        var ready = new List<LoadCandidate>();
        var infos = new List<PluginImageInfo>();
        foreach (var c in candidates)
        {
            string fileName = Path.GetFileName(c.File);
            if (c.Bytes == null && InfoCache.TryGetValue(c.File, out var cached) && cached.Write == c.Write)
            {
                c.Info = cached.Info;
            }
            else
            {
                if (c.Bytes == null)
                {
                    try { c.Bytes = File.ReadAllBytes(c.File); }
                    catch (Exception e)
                    {
                        Note($"Reload: reading {fileName} failed: {e.Message}");
                        Failed[c.File] = c.Write;
                        continue;
                    }
                }
                c.Info = PluginLoadOrder.Read(fileName, c.Bytes);
                InfoCache[c.File] = (c.Write, c.Info);
            }
            ready.Add(c);
            infos.Add(c.Info);
        }

        bool externalKnown = TryLoadedGuids(out var guids);
        var plan = PluginLoadOrder.Plan(infos, guids, LoadedAssemblyNames(), externalKnown);

        var loadOrder = new List<string>();
        var cycle = new List<string>();
        foreach (var step in plan)
        {
            if (step.Waiting) continue;
            loadOrder.Add(infos[step.Index].File);
            if (step.InCycle) cycle.Add(infos[step.Index].File);
        }
        if (cycle.Count > 0)
        {
            string line = PluginLoadOrder.FormatCycle(cycle);
            if (NotedOnce.Add(line)) Note(line);
        }
        if (!IsNameOrder(loadOrder))
            Note(PluginLoadOrder.FormatOrder(loadOrder));

        var loadedOk = new bool[ready.Count];
        foreach (var step in plan)
        {
            var c = ready[step.Index];
            string fileName = Path.GetFileName(c.File);
            if (step.Waiting)
            {
                SetWaiting(c, step.WaitingFor, step.Reason);
                continue;
            }
            string missing = "";
            foreach (int p in step.HardProviders)
                if (!loadedOk[p]) { missing = infos[p].File; break; }
            if (missing.Length > 0)
            {
                SetWaiting(c, missing, PluginLoadOrder.FormatProviderDidNotLoad(fileName, missing));
                continue;
            }

            if (c.Bytes == null)
            {
                try { c.Bytes = File.ReadAllBytes(c.File); }
                catch (Exception e)
                {
                    Note($"Reload: reading {fileName} failed: {e.Message}");
                    Failed[c.File] = c.Write;
                    continue;
                }
            }
            if (!c.HaveVerdict)
            {
                c.Hash = ReloadAllowPolicy.Sha256Hex(c.Bytes);
                c.Allow = ReloadAllowStore.Decide(fileName, c.Hash);
            }
            var fresh = Load(c.File, c.Write, c.Bytes, c.Hash!, c.Allow);
            if (fresh != null)
            {
                ByPath[c.File] = fresh;
                Failed.Remove(c.File);
                WaitingByPath.Remove(c.File);
                InfoCache.Remove(c.File);
                loadedOk[step.Index] = true;
                continue;
            }
            Failed[c.File] = c.Write;
            WaitingByPath.Remove(c.File);
            if (c.UnloadedForReplacement)
            {
                // Honest limitation, mirroring PluginReloader: the old instance is gone, so
                // nothing can re-run its OnUnload with Removal. Say so rather than leaving
                // the operator with only "skipped ...; previous plugin ALC still alive."
                Note($"Reload: {fileName} failed to load after a Swap unload; "
                    + "live state the previous version kept is not swept. Delete the file to unload it for good.");
            }
        }
    }

    private static void SetWaiting(LoadCandidate c, string waitingFor, string reason)
    {
        if (WaitingByPath.TryGetValue(c.File, out var prior)
            && prior.Write == c.Write
            && string.Equals(prior.WaitingFor, waitingFor, StringComparison.Ordinal))
            return;
        WaitingByPath[c.File] = (c.Write, waitingFor, reason, c.Info.Guids);
        Note(reason);
    }

    private static bool IsNameOrder(List<string> files)
    {
        for (int i = 1; i < files.Count; i++)
            if (string.Compare(files[i - 1], files[i], StringComparison.OrdinalIgnoreCase) > 0) return false;
        return true;
    }

    /// <summary>
    /// GUIDs already running: reload-folder plugins, Bootstrap's own, and the chainloader's
    /// (<c>BepInEx/plugins</c>). False when the chainloader's list could not be read; then the plan never
    /// waits on a dependency outside the folder.
    /// </summary>
    private static bool TryLoadedGuids(out List<string> guids)
    {
        guids = new List<string> { MyPluginInfo.PLUGIN_GUID };
        foreach (var loaded in ByPath.Values)
        {
            if (loaded == null) continue;
            foreach (var g in loaded.Info.Guids) guids.Add(g);
            foreach (var plugin in loaded.Plugins)
            {
                if (plugin == null) continue;
                try { var id = IdentityOf(plugin); if (id.HasGuid) guids.Add(id.Guid); }
                catch { /* next */ }
            }
        }
        try
        {
            var chain = IL2CPPChainloader.Instance;
            if (chain?.Plugins == null) return false;
            foreach (var key in chain.Plugins.Keys) guids.Add(key);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> LoadedAssemblyNames()
    {
        var names = new List<string>();
        foreach (var loaded in ByPath.Values)
            if (loaded != null && loaded.AssemblyName.Length > 0) names.Add(loaded.AssemblyName);
        return names;
    }

    /// <summary>
    /// Reload-folder DLLs waiting for a dependency, as <see cref="PluginLoadOrder.FormatWaitingRow"/> lines,
    /// for <c>reload_status</c> and Core's Mods tab (through <c>ProcessHost.WaitingPlugins</c>).
    /// </summary>
    public static string[] WaitingRows()
    {
        var rows = new List<string>();
        foreach (var kv in WaitingByPath)
            rows.Add(PluginLoadOrder.FormatWaitingRow(Path.GetFileName(kv.Key), kv.Value.Guids, kv.Value.WaitingFor, kv.Value.Reason));
        return rows.ToArray();
    }

    // ---- job FINJ1: process-lifetime plugins (IL2CPP type injection) ------------------------------

    /// <summary>
    /// True when <paramref name="loaded"/> registers its own IL2CPP types: its image references an
    /// injection call site, or ClassInjector's injected set already names a type its assembly
    /// defines (a helper did it). The runtime answer is sticky once found.
    /// </summary>
    private static bool IsProcessLifetime(Loaded loaded)
    {
        if (loaded.Injection.Injects || loaded.RuntimeInjected.Count > 0) return true;
        var asm = loaded.Assembly;
        if (asm == null) return false;
        try
        {
            var hits = Il2CppInjectionPolicy.MatchInjected(
                InjectedTypeNames(), name => asm.GetType(name, throwOnError: false) != null);
            if (hits.Count == 0) return false;
            loaded.RuntimeInjected.AddRange(hits);
            ProcessHost.Log?.LogWarning(Il2CppInjectionPolicy.FormatDetected(
                Path.GetFileName(loaded.Path), hits, loaded.Injection.CoreBound, runtime: true));
            return true;
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("Reload: IL2CPP injection check for " + Path.GetFileName(loaded.Path) + ": " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// <c>ClassInjector</c>'s private injected-name set (the same read <c>CorePinDiagnostics.LogClassInjector</c>
    /// makes). Empty when Il2CppInterop is not loaded or the field moved.
    /// </summary>
    private static List<string> InjectedTypeNames()
    {
        var names = new List<string>();
        var type = Type.GetType(Il2CppInjectionPolicy.ClassInjectorType + ", Il2CppInterop.Runtime");
        var field = type?.GetField("InjectedTypes", BindingFlags.NonPublic | BindingFlags.Static);
        if (field?.GetValue(null) is System.Collections.IEnumerable set)
        {
            foreach (var item in set)
                if (item is string name) names.Add(name);
        }
        return names;
    }

    private static void SetPending(Loaded loaded, string pending)
    {
        if (string.Equals(loaded.Pending, pending, StringComparison.Ordinal)) return;
        loaded.Pending = pending;
        string file = Path.GetFileName(loaded.Path);
        Note(pending == Il2CppInjectionPolicy.PendingChanged ? Il2CppInjectionPolicy.FormatKeptOnChange(file)
            : pending == Il2CppInjectionPolicy.PendingRemoved ? Il2CppInjectionPolicy.FormatKeptOnRemoval(file)
            : Il2CppInjectionPolicy.FormatBackToLoaded(file));
    }

    /// <summary>
    /// A kept process-lifetime plugin (other than the one at <paramref name="file"/>) whose assembly has
    /// the name <paramref name="assemblyName"/>: a second copy under another file name would inject the
    /// same type names again and fail exactly as an in-place reload does.
    /// </summary>
    private static string? ProcessLifetimeHolderOf(string file, string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName)) return null;
        foreach (var loaded in ByPath.Values)
        {
            if (loaded == null || string.Equals(loaded.Path, file, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(loaded.AssemblyName, assemblyName, StringComparison.Ordinal)) continue;
            if (IsProcessLifetime(loaded)) return Path.GetFileName(loaded.Path);
        }
        return null;
    }

    /// <summary>True when a loaded reload-folder plugin with this GUID is process-lifetime (AdmissionSnapshot).</summary>
    public static bool IsProcessLifetimePlugin(string? guid)
    {
        string want = (guid ?? "").Trim();
        if (want.Length == 0) return false;
        foreach (var loaded in ByPath.Values)
        {
            if (loaded == null) continue;
            foreach (var plugin in loaded.Plugins)
            {
                if (plugin == null) continue;
                PluginIdentity identity;
                try { identity = IdentityOf(plugin); }
                catch { continue; }
                if (string.Equals(identity.Guid, want, StringComparison.OrdinalIgnoreCase) && IsProcessLifetime(loaded))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The process-lifetime plugins that reference Core. While one is loaded a Core swap cannot
    /// unload it without pinning the outgoing generation, so the swap is refused up front.
    /// </summary>
    public static List<string> CoreBoundProcessLifetimeFiles()
    {
        var files = new List<string>();
        foreach (var loaded in ByPath.Values)
        {
            if (loaded == null) continue;
            if (!IsProcessLifetime(loaded)) continue;
            // Job FORD1: it is kept with every reload-folder plugin it needs at runtime, so one of those
            // that references Core binds the swap exactly as the plugin itself would.
            bool bound = loaded.Injection.CoreBound;
            foreach (var provider in RuntimeProviders(loaded)) bound |= provider.Injection.CoreBound;
            if (Il2CppInjectionPolicy.Decide(LifetimeEvent.CoreSwap, true, bound) == LifetimeAction.Refuse)
                files.Add(Path.GetFileName(loaded.Path));
        }
        return files;
    }

    /// <summary>
    /// Every loaded process-lifetime plugin as <see cref="Il2CppInjectionPolicy.FormatRow"/> lines, for
    /// Core's Mods tab (through <c>ProcessHost.ProcessLifetimePlugins</c>) and <c>reload_status</c>.
    /// </summary>
    public static string[] ProcessLifetimeRows()
    {
        var rows = new List<string>();
        foreach (var loaded in ByPath.Values)
        {
            if (loaded == null || !IsProcessLifetime(loaded)) continue;
            var guids = new List<string>();
            var names = new List<string>();
            foreach (var plugin in loaded.Plugins)
            {
                if (plugin == null) continue;
                try
                {
                    var identity = IdentityOf(plugin);
                    if (identity.HasGuid) guids.Add(identity.Guid);
                    names.Add(identity.Name);
                }
                catch { /* next plugin */ }
            }
            var sites = new List<string>(loaded.Injection.Sites);
            sites.AddRange(loaded.RuntimeInjected);
            rows.Add(Il2CppInjectionPolicy.FormatRow(new Il2CppInjectionPolicy.Row(
                Path.GetFileName(loaded.Path), guids.ToArray(), names.ToArray(),
                loaded.Injection.CoreBound, loaded.Pending, sites.ToArray())));
        }
        return rows.ToArray();
    }

    /// <summary>
    /// Context names of plugins still loaded. After a Core swap's <see cref="UnloadAllReverse"/> these
    /// are exactly the kept process-lifetime plugins, which are not waited on and never latched.
    /// </summary>
    private static HashSet<string> LiveContextNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var loaded in ByPath.Values)
        {
            string? name = null;
            try { name = loaded?.Context?.Name; }
            catch { name = null; }
            if (!string.IsNullOrEmpty(name)) names.Add(name!);
        }
        return names;
    }

    /// <summary>
    /// Collectible plugin ALCs still in <c>AssemblyLoadContext.All</c>. A Core swap must
    /// not Unload Core while one of these is alive: it pins both ALCs. Enumeration
    /// failure is fail-closed (count &gt; 0).
    /// </summary>
    public static int AliveCollectibleCount() => ListCollectiblePluginAlcNames().Count;

    /// <summary>
    /// Names of waited collectible plugin ALCs still in <c>AssemblyLoadContext.All</c>
    /// (<c>progenitor-reload:*</c> only). Third-party and unnamed collectibles are
    /// pin-diag only (R17 finding 1). Fail closed: an exception in enumeration
    /// counts as still-alive and is logged once.
    /// </summary>
    public static List<string> ListCollectiblePluginAlcNames()
    {
        var names = new List<string>();
        if (!TryListWaitedCollectibleAlcNames(names, out var error))
        {
            if (System.Threading.Interlocked.Exchange(ref _loggedEnumFail, 1) == 0)
                ProcessHost.Log?.LogWarning(
                    "plugin-alc listing failed: " + (error?.Message ?? "") + "; treating as still alive");
            return CoreReloadPolicy.ClosePluginAlcListing(names, enumerationFailed: true);
        }
        return names;
    }

    /// <summary>
    /// Enumerate waited collectibles without the fail-closed token. Returns false
    /// when <c>AssemblyLoadContext.All</c> throws so a latch poll can leave
    /// <c>_forcePluginLatch</c> set.
    /// </summary>
    internal static bool TryListWaitedCollectibleAlcNames(List<string> names, out Exception? error)
    {
        error = null;
        try
        {
            var live = LiveContextNames();
            foreach (var alc in AssemblyLoadContext.All)
            {
                if (alc == null || !alc.IsCollectible) continue;
                if (!CoreReloadPolicy.IsWaitedCollectibleAlc(alc.Name)) continue;
                if (alc.Name != null && live.Contains(alc.Name)) continue; // job FINJ1: kept, not retiring
                names.Add(alc.Name ?? "");
            }
            return true;
        }
        catch (Exception e)
        {
            error = e;
            return false;
        }
    }

    /// <summary>
    /// Snapshot weaks of still-listed plugin ALCs so a later <c>core_reload</c> refuses
    /// until they die (same latch as a Core generation that did not collect).
    /// </summary>
    public static void LatchAlivePluginAlcs()
    {
        lock (LatchGate)
        {
            LatchedPluginAlcs.Clear();
            LatchedPluginNames.Clear();
            _forcePluginLatch = false;
            _latchPollFrames = 0;
            try
            {
                var live = LiveContextNames();
                foreach (var alc in AssemblyLoadContext.All)
                {
                    if (alc == null || !alc.IsCollectible) continue;
                    if (!CoreReloadPolicy.IsWaitedCollectibleAlc(alc.Name)) continue;
                    if (alc.Name != null && live.Contains(alc.Name)) continue; // job FINJ1: kept, not retiring
                    LatchedPluginAlcs.Add(new WeakReference(alc));
                    LatchedPluginNames.Add(alc.Name ?? CoreReloadPolicy.UnnamedAlcName);
                }
            }
            catch (Exception e)
            {
                if (System.Threading.Interlocked.Exchange(ref _loggedEnumFail, 1) == 0)
                    ProcessHost.Log?.LogWarning(
                        "plugin-alc latch listing failed: " + e.Message + "; treating as still alive");
                LatchedPluginNames.Add(CoreReloadPolicy.EnumerationFailedAlcName);
                _forcePluginLatch = true;
            }
        }
    }

    public static bool LatchedPluginAlcsAlive()
    {
        lock (LatchGate)
        {
            if (_forcePluginLatch) return true;
            return AnyLatchedWeakAlive();
        }
    }

    /// <summary>
    /// Plugin-ALC latch poll. Cheap <c>WeakReference.IsAlive</c> compact runs every
    /// call while latched. <c>AssemblyLoadContext.All</c> is walked at most every
    /// <see cref="CoreReloadPolicy.PluginLatchPollEveryFrames"/> ticks, and only
    /// while <c>_forcePluginLatch</c> is set. Stops entirely once the latch is
    /// empty (R17 finding 5). Called from ProcessHost.Tick and RequestReload;
    /// ReloaderHost.Tick does not poll.
    /// </summary>
    public static void PollPluginLatch()
    {
        lock (LatchGate)
        {
            if (!_forcePluginLatch && LatchedPluginAlcs.Count == 0 && LatchedPluginNames.Count == 0)
            {
                _latchPollFrames = 0;
                return;
            }
            CompactDeadWeaks();
            if (!_forcePluginLatch && !AnyLatchedWeakAlive())
            {
                LatchedPluginAlcs.Clear();
                LatchedPluginNames.Clear();
                _latchPollFrames = 0;
                return;
            }
            _latchPollFrames++;
            if (!_forcePluginLatch) return;
            if (!CoreReloadPolicy.ShouldPollPluginLatch(
                    _latchPollFrames - 1, CoreReloadPolicy.PluginLatchPollEveryFrames))
                return;
            var listed = new List<string>();
            if (TryListWaitedCollectibleAlcNames(listed, out _)
                && CoreReloadPolicy.ShouldClearForcePluginLatch(true, listed.Count))
            {
                _forcePluginLatch = false;
                if (!AnyLatchedWeakAlive())
                {
                    LatchedPluginAlcs.Clear();
                    LatchedPluginNames.Clear();
                    _latchPollFrames = 0;
                }
            }
        }
    }

    private static bool AnyLatchedWeakAlive()
    {
        for (int i = 0; i < LatchedPluginAlcs.Count; i++)
        {
            if (LatchedPluginAlcs[i] is { IsAlive: true })
                return true;
        }
        return false;
    }

    private static void CompactDeadWeaks()
    {
        int w = 0;
        for (int i = 0; i < LatchedPluginAlcs.Count; i++)
        {
            if (LatchedPluginAlcs[i] is not { IsAlive: true }) continue;
            if (w != i) LatchedPluginAlcs[w] = LatchedPluginAlcs[i];
            w++;
        }
        if (w < LatchedPluginAlcs.Count)
            LatchedPluginAlcs.RemoveRange(w, LatchedPluginAlcs.Count - w);
    }

    public static IReadOnlyList<string> LatchedPluginAlcNamesLive()
    {
        lock (LatchGate)
        {
            if (LatchedPluginNames.Count == 0)
                return Array.Empty<string>();
            return LatchedPluginNames.ToArray();
        }
    }

    /// <summary>
    /// Do not create a new <c>progenitor-reload:{name}:*</c> context while an old
    /// one is still listed. Global latch refuse is <see cref="CoreReloadPolicy.MayCreatePluginAlc"/>
    /// (idle Scan); this is the per-name check so wait-miss <c>ReloadAll</c> can
    /// restore other plugins without stacking a second Sample ALC (R17 finding 4).
    /// </summary>
    public static bool ShouldRefuseNewPluginAlc(string? pluginName)
    {
        if (string.IsNullOrEmpty(pluginName)) return false;
        try
        {
            foreach (var alc in AssemblyLoadContext.All)
            {
                if (alc == null || !alc.IsCollectible) continue;
                if (CoreReloadPolicy.PluginAlcNameMatches(alc.Name, pluginName))
                    return true;
            }
        }
        catch
        {
            return true;
        }
        return false;
    }

    private static PluginUnloadOrder.Node DescribeNode(Loaded loaded, int loadIndex)
    {
        string guid = "";
        var deps = new List<string>();
        try
        {
            foreach (var plugin in loaded.Plugins)
            {
                try
                {
                    // Job FREL2 (R61 finding 1): a bare meta.GUID here NRE'd into the catch below
                    // and took this plugin's [BepInDependency] list with it, so the unload ORDER
                    // silently degraded to last-loaded-first for a plugin others depend on.
                    var identity = IdentityOf(plugin);
                    if (string.IsNullOrEmpty(guid) && identity.HasGuid)
                        guid = identity.Guid;
                    var attrs = plugin.GetType().GetCustomAttributes(typeof(BepInDependency), inherit: true);
                    for (int i = 0; i < attrs.Length; i++)
                    {
                        if (attrs[i] is BepInDependency dep && !string.IsNullOrEmpty(dep.DependencyGUID))
                            deps.Add(dep.DependencyGUID);
                    }
                }
                catch
                {
                    // One plugin's metadata must not abort the order for the rest.
                }
            }
        }
        catch
        {
            // fall through with empty guid/deps → last-loaded-first
        }
        // Job FORD1: an assembly reference to another loaded reload-folder DLL is a dependency too (its
        // context resolved that assembly from the other one), and a library DLL with no [BepInPlugin] is
        // keyed by its assembly name so the edge survives.
        if (string.IsNullOrEmpty(guid) && loaded.AssemblyName.Length > 0)
            guid = AssemblyNodeKey(loaded.AssemblyName);
        foreach (var other in ByPath.Values)
        {
            if (other == null || ReferenceEquals(other, loaded)) continue;
            if (!PluginLoadOrder.NeedsAtRuntime(loaded.Info, other.Info)) continue;
            string key = other.Info.Guids.Length > 0 ? other.Info.Guids[0] : AssemblyNodeKey(other.AssemblyName);
            if (key.Length > 0 && !deps.Contains(key)) deps.Add(key);
        }
        return new PluginUnloadOrder.Node(guid, deps.ToArray(), loadIndex);
    }

    private static string AssemblyNodeKey(string? assemblyName) =>
        string.IsNullOrEmpty(assemblyName) ? "" : "asm:" + assemblyName;

    /// <summary>
    /// Load every settled DLL in the reload folder as if first seen. Call after the new Core
    /// generation has Install'd and SweepPlugin is registered. One plugin failing does not
    /// abort the rest (same as a regular Scan).
    /// </summary>
    public static int ReloadAll() => ReloadAll(evenIfLatched: false);

    /// <summary>
    /// Load every settled DLL in the reload folder as if first seen. Call after the new Core
    /// generation has Install'd and SweepPlugin is registered. One plugin failing does not
    /// abort the rest (same as a regular Scan). <paramref name="evenIfLatched"/> is the
    /// wait-miss restore path: plugins come back so the process is never plugin-less
    /// while the latch still refuses a later <c>core_reload</c>.
    /// </summary>
    public static int ReloadAll(bool evenIfLatched)
    {
        if (!CoreReloadPolicy.MayCreatePluginAlc(LatchedPluginAlcsAlive(), evenIfLatched))
        {
            Note("Reload: skipped creating plugin ALCs; latched plugin ALC still alive; restart the process.");
            return ByPath.Count;
        }
        Failed.Clear();
        // Scan no-ops when CoreAttached is false (Freeze). Abort-before-Uninstall
        // unfreezes first; this latch is the belt if a caller forgets. Do not
        // force-attach after NullRuntime (CoreTick is gone).
        bool attached = ProcessHost.CoreAttached;
        bool forced = false;
        if (!attached && ProcessHost.CoreTick != null)
        {
            ProcessHost.CoreAttached = true;
            forced = true;
        }
        _loadWhileLatched = evenIfLatched;
        try { Scan(); }
        finally
        {
            _loadWhileLatched = false;
            if (forced) ProcessHost.CoreAttached = false;
        }
        return ByPath.Count;
    }

    private static void WarnIfCoreVersionSkew(Assembly assembly)
    {
        try
        {
            var coreVer = CoreLoader.Assembly?.GetName().Version;
            if (coreVer == null) return;
            foreach (var name in assembly.GetReferencedAssemblies())
            {
                if (!string.Equals(name.Name, PluginAssemblyResolver.CoreSimpleName, StringComparison.Ordinal))
                    continue;
                var v = name.Version;
                if (v == null) continue;
                if (v.Major == coreVer.Major && v.Minor == coreVer.Minor) continue;
                ProcessHost.Log?.LogWarning(
                    $"Reload: {assembly.GetName().Name} compiled against Progenitor {v} but this Core is {coreVer}; Load may throw MissingMethodException.");
            }
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning($"Reload: Core version compare: {e.Message}");
        }
    }

    private static void Sweep(BasePlugin plugin)
    {
        var sweep = ProcessHost.SweepPlugin;
        if (sweep == null)
        {
            ProcessHost.Log?.LogWarning("Reload: SweepPlugin is not registered; Core has not attached.");
            return;
        }
        try { sweep(plugin); }
        catch (Exception e) { ProcessHost.Log?.LogWarning($"Reload: SweepPlugin: {e.Message}"); }
    }
}
