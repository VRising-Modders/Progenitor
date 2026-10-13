using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Progenitor.PluginActions;
using Progenitor.Plugins;

namespace Progenitor.Ui;

/// <summary>What the switch can do for one mod.</summary>
internal enum ModSwitchMode
{
    /// <summary>No switch: nothing on this client to switch (a server-only row).</summary>
    Hidden = 0,

    /// <summary>A hot-loadable Progenitor mod: off unloads it now (reason Suspend), on loads it again.</summary>
    Live = 1,

    /// <summary>The change is recorded now and takes effect at the next start.</summary>
    Restart = 2,

    /// <summary>A switch is drawn but cannot act; <see cref="ModSwitchView.Reason"/> says why.</summary>
    Unavailable = 3,

    /// <summary>Progenitor itself or the loader: turning it off is refused.</summary>
    Refused = 4,
}

/// <summary>Who owns the mod's files, as the switch needs to know it.</summary>
internal enum ModOwner
{
    /// <summary>A Progenitor plugin (<c>ProgenitorPlugin&lt;T&gt;</c>): its load can be skipped.</summary>
    Progenitor = 0,

    /// <summary>An ordinary BepInEx plugin: only the preloader can keep it off (by renaming its DLLs).</summary>
    Plain = 1,

    /// <summary>A part of Progenitor itself (<see cref="ModEnableState.RequiredIds"/>).</summary>
    Required = 2,

    /// <summary>BepInEx, the loader.</summary>
    Loader = 3,

    /// <summary>Only the joined server listed it.</summary>
    ServerOnly = 4,

    /// <summary>Nothing says what it is.</summary>
    Unknown = 5,
}

/// <summary>A change asked for that happens at the next start.</summary>
internal enum ModSwitchPending
{
    None = 0,
    TurnsOff = 1,
    TurnsOn = 2,
    Removed = 3,
}

/// <summary>Everything the switch decision reads. Plain data, so every case has a test.</summary>
internal sealed class ModSwitchFacts
{
    internal string Id { get; init; } = "";
    internal string Name { get; init; } = "";
    internal ModOwner Owner { get; init; }

    /// <summary>Bootstrap's reloader can pause plugins in this process.</summary>
    internal bool BackendSupported { get; init; }

    /// <summary>Loaded from <c>progenitor-reload/</c> and not <c>ctx.UnsafeHarmony</c>: it can be unloaded now.</summary>
    internal bool Hot { get; init; }

    /// <summary>Unloaded and held by Bootstrap's pause table (by the switch or a server pause).</summary>
    internal bool Suspended { get; init; }

    /// <summary>The player turned it off (<see cref="ModEnableState"/>).</summary>
    internal bool DisabledIntent { get; init; }

    /// <summary>This Core generation saw its <c>Load</c> (<see cref="ModRegistry"/>).</summary>
    internal bool Known { get; init; }

    /// <summary>Its features run now (<see cref="ModRegistryEntry.Running"/>: loaded; a failed <c>OnLoad</c> is not running).</summary>
    internal bool RunningNow { get; init; }

    /// <summary>What this start's load did (null when this generation never saw it).</summary>
    internal ModLoadOutcome? Outcome { get; init; }

    /// <summary>Skipped because this hard dependency is off (its display name); empty otherwise.</summary>
    internal string OffBecause { get; init; } = "";

    /// <summary>A plain plugin: why its files cannot be named with certainty; empty when they can.</summary>
    internal string PlainWhy { get; init; } = "";

    /// <summary>A plain plugin: the next-start request waiting for it, if any.</summary>
    internal PluginFileAction? PlainRequest { get; init; }

    /// <summary>A plain plugin: its DLLs are renamed off on disk (the preloader did it at an earlier start).</summary>
    internal bool PlainDisabledOnDisk { get; init; }
}

/// <summary>What the switch shows and whether it can be pressed.</summary>
internal sealed class ModSwitchView
{
    internal ModSwitchMode Mode { get; init; }

    /// <summary>Why the switch cannot act (Unavailable / Refused / Hidden); empty otherwise.</summary>
    internal string Reason { get; init; } = "";

    /// <summary>The player's choice: the switch's position.</summary>
    internal bool On { get; init; } = true;

    internal ModSwitchPending Pending { get; init; }

    /// <summary>Off by the player's choice and not running now: the status is <c>Disabled</c>.</summary>
    internal bool Disabled { get; init; }

    /// <summary>Off because this dependency (its name) is off; the status says so instead of <c>Disabled</c>.</summary>
    internal string OffBecause { get; init; } = "";

    internal bool Interactive => Mode == ModSwitchMode.Live || Mode == ModSwitchMode.Restart;

    /// <summary>The status word of a next-start change, or empty.</summary>
    internal string PendingWord => ModSwitch.WordOf(Pending);

    internal static string NameOf(ModSwitchMode mode) => mode switch
    {
        ModSwitchMode.Live => "live",
        ModSwitchMode.Restart => "restart",
        ModSwitchMode.Unavailable => "unavailable",
        ModSwitchMode.Refused => "refused",
        _ => "hidden",
    };

    internal static string NameOf(ModSwitchPending pending) => pending switch
    {
        ModSwitchPending.TurnsOff => "turns-off",
        ModSwitchPending.TurnsOn => "turns-on",
        ModSwitchPending.Removed => "removed",
        _ => "",
    };
}

/// <summary>The result of pressing the switch (or Retry, Uninstall, Cancel).</summary>
internal sealed class ModSwitchOutcome
{
    internal bool Ok { get; init; }

    /// <summary>One plain sentence for the page.</summary>
    internal string Message { get; init; } = "";

    /// <summary>Every plugin id the action took with it (a live switch-off: siblings and dependants).</summary>
    internal IReadOnlyList<string> Taken { get; init; } = Array.Empty<string>();

    internal static ModSwitchOutcome Fail(string message) => new() { Ok = false, Message = message ?? "" };

    internal static ModSwitchOutcome Done(string message, IReadOnlyList<string>? taken = null) =>
        new() { Ok = true, Message = message ?? "", Taken = taken ?? Array.Empty<string>() };
}

/// <summary>
/// SETTINGS1 stage 3: the mod page's on/off switch (docs/mod-settings.md, "Stage 3").
///
/// <list type="bullet">
/// <item><b>Hot-loadable Progenitor mods</b> switch live, through the machinery per-server mod pause
/// uses: off is Bootstrap's <c>SuspendPlugins</c> (unload reason <c>Suspend</c>; DLL siblings and
/// dependants go with it), on is <c>ResumePlugins</c>.</item>
/// <item><b>Restart-only Progenitor mods</b> (<c>BepInEx/plugins</c>, <c>ctx.UnsafeHarmony</c>)
/// record the choice; at the next start <c>ProgenitorPlugin.Load</c> skips their <c>OnLoad</c> (and
/// the <c>OnLoad</c> of every plugin that hard-depends on one), so they stay off.</item>
/// <item><b>Ordinary BepInEx plugins</b> whose files can be named with certainty get a next-start
/// request the preloader patcher carries out (<see cref="PluginFileActions"/>): their DLLs are
/// renamed to <c>*.dll.old</c>.</item>
/// <item><b>Progenitor itself and the loader</b> are refused.</item>
/// </list>
///
/// <para>The choice is remembered in <c>progenitor-mod-state.json</c> (<see cref="ModEnableState"/>).
/// A mod the player turned off is <i>held</i>: a server resume never brings it back
/// (<see cref="IsHeld"/>, read by <see cref="ServerModPause"/>), and should anything load it anyway
/// its <c>Load</c> skips <c>OnLoad</c>.</para>
/// </summary>
internal static class ModSwitch
{
    /// <summary>Tests only: the pause backend instead of Bootstrap's.</summary>
    internal static Func<IServerModPauseBackend>? BackendOverride { get; set; }

    /// <summary>The BepInEx root the plain-plugin requests are written under. Production: <c>Paths.BepInExRootPath</c>.</summary>
    internal static Func<string>? BepInExRoot { get; set; }

    /// <summary>Tests only: the clock.</summary>
    internal static Func<DateTime>? ClockUtc { get; set; }

    /// <summary>
    /// Review of 5a6e40f8, finding 6: the ordinary BepInEx plugins (not Progenitor's) that hard-depend
    /// on a plugin id, as (id, name). Production asks the chainloader; null answers none.
    /// </summary>
    internal static Func<string, IReadOnlyList<(string Id, string Name)>>? PlainDependantsProbe { get; set; }

    internal static IReadOnlyList<(string Id, string Name)> PlainDependantsOf(string id)
    {
        try { return PlainDependantsProbe?.Invoke(id) ?? Array.Empty<(string, string)>(); }
        catch { return Array.Empty<(string, string)>(); }
    }

    /// <summary>Raised after the switch changed something (the menu rebuilds the list and the page).</summary>
    internal static event Action<string>? Changed;

    private static readonly HashSet<string> SessionHeld = new(StringComparer.OrdinalIgnoreCase);

    // CLIENTPOLICY3: plugins this Core generation's switch unloaded itself (not a server pause it found).
    private static readonly HashSet<string> SwitchTook = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    // ---- words -------------------------------------------------------------------------------

    internal const string TurnedOff = "Turned off.";
    internal const string TurnedOn = "Turned on.";
    internal const string TurnedOnLoading = "Turned on. It loads as soon as its previous copy is cleaned up.";
    internal const string TurnedOnPaused = "Turned on. It stays paused for this server until you leave.";
    internal const string TurnedOnNotAllowed = "Turned on, but this server doesn't allow it, so it stays paused for this server until you leave.";
    internal const string NothingChangesAtStart = "Nothing changes at the next start.";
    internal const string RequestCanceled = "The request for the next start is canceled.";
    internal const string RetryStarted = "Loading it again.";
    internal const string RetryRestartOnly = "It was loaded when the game started. Restart the game to try again.";
    internal const string UnknownOwnerReason = "Progenitor cannot tell what installed this mod, so it cannot switch it.";

    /// <summary>A few words for the Install tab's "Managed elsewhere" row: why the preloader cannot act.</summary>
    internal static string ShortWhy(string? why)
    {
        string w = (why ?? "").Trim();
        if (w.Length == 0) return "";
        if (w.StartsWith("Its folder also holds", StringComparison.Ordinal)) return "Shares its folder with another mod";
        if (w.EndsWith("needs it to load.", StringComparison.Ordinal)) return "Another mod depends on it";
        if (w == PluginFileActions.WhyLink) return "Its folder contains a link";
        if (w == PluginFileActions.WhyProgenitor) return "Part of Progenitor or BepInEx";
        if (w == PluginFileActions.WhyNoLocation) return "Its file is not known";
        return w.TrimEnd('.');
    }

    /// <summary>
    /// Re-review of 9a27bf01, finding 4: the main thread waits at most this long for the next-start
    /// file's lock (another process, or the preloader, may hold it); on a timeout the press says
    /// "busy, try again" instead of freezing the frame.
    /// </summary>
    internal static readonly TimeSpan LockWait = TimeSpan.FromMilliseconds(250);

    internal const string BusyTryAgain = "Busy: another game process is changing the Mods menu's requests. Try again in a moment.";

    internal static string BusyOr(string error) =>
        string.Equals(error, PluginFileActions.WhyBusy, StringComparison.Ordinal) ? BusyTryAgain : error;

    internal static string ManagedElsewhereReason(string why) =>
        "Managed elsewhere: " + (string.IsNullOrWhiteSpace(why) ? "its files cannot be told apart." : why.Trim());

    internal static string WordOf(ModSwitchPending pending) => pending switch
    {
        ModSwitchPending.TurnsOff => ModSwitchWords.TurnsOffAtNextStart,
        ModSwitchPending.TurnsOn => ModSwitchWords.TurnsOnAtNextStart,
        ModSwitchPending.Removed => ModSwitchWords.RemovedAtNextStart,
        _ => "",
    };

    // ---- the decision (pure) -----------------------------------------------------------------

    internal static ModSwitchView Decide(ModSwitchFacts f)
    {
        f ??= new ModSwitchFacts();
        switch (f.Owner)
        {
            case ModOwner.Loader:
                return new ModSwitchView { Mode = ModSwitchMode.Refused, Reason = ModSwitchWords.LoaderRefusal };
            case ModOwner.Required:
                return new ModSwitchView { Mode = ModSwitchMode.Refused, Reason = ModSwitchWords.RequiredRefusal };
            case ModOwner.ServerOnly:
                return new ModSwitchView { Mode = ModSwitchMode.Hidden, Reason = ModSwitchWords.NotOnClientReason };
            case ModOwner.Unknown:
                return new ModSwitchView { Mode = ModSwitchMode.Unavailable, Reason = UnknownOwnerReason };
            case ModOwner.Plain:
                return DecidePlain(f);
        }

        bool on = !f.DisabledIntent;
        // Review finding 4: a plugin that does not run on this side has nothing to switch here.
        if (f.Outcome == ModLoadOutcome.Dormant)
            return new ModSwitchView { Mode = ModSwitchMode.Unavailable, Reason = ModSwitchWords.DormantReason, On = on };
        // Off only because something it needs is off: its own switch cannot bring it back.
        if (on && f.OffBecause.Length > 0)
            return new ModSwitchView
            {
                Mode = ModSwitchMode.Unavailable,
                Reason = ModSwitchWords.OffBecauseReason(f.OffBecause),
                On = false,
                Disabled = true,
                OffBecause = f.OffBecause,
            };
        if (f.BackendSupported && (f.Hot || f.Suspended))
        {
            // Live: the change is immediate, so nothing waits for a restart.
            return new ModSwitchView
            {
                Mode = ModSwitchMode.Live,
                On = on,
                Disabled = !on && (f.Suspended || !f.RunningNow),
            };
        }

        // Review finding 4: "turns on" only for a load this start skipped because the player had it
        // off; "turns off" only while its features run. A failed or dormant load is neither.
        var pending = ModSwitchPending.None;
        if (f.Known)
        {
            if (on && f.Outcome == ModLoadOutcome.SkippedOff) pending = ModSwitchPending.TurnsOn;
            else if (!on && f.RunningNow) pending = ModSwitchPending.TurnsOff;
        }

        return new ModSwitchView
        {
            Mode = ModSwitchMode.Restart,
            On = on,
            Pending = pending,
            Disabled = !on && !f.RunningNow,
        };
    }

    private static ModSwitchView DecidePlain(ModSwitchFacts f)
    {
        bool waiting = f.PlainRequest != null;
        if (f.PlainWhy.Length > 0 && !f.PlainDisabledOnDisk && !waiting)
            return new ModSwitchView { Mode = ModSwitchMode.Unavailable, Reason = ManagedElsewhereReason(f.PlainWhy) };

        bool on = f.PlainRequest switch
        {
            PluginFileAction.Disable => false,
            PluginFileAction.Enable => true,
            _ => !f.PlainDisabledOnDisk,
        };
        var pending = f.PlainRequest switch
        {
            PluginFileAction.Disable => ModSwitchPending.TurnsOff,
            PluginFileAction.Enable => ModSwitchPending.TurnsOn,
            PluginFileAction.Remove => ModSwitchPending.Removed,
            _ => ModSwitchPending.None,
        };
        return new ModSwitchView
        {
            Mode = ModSwitchMode.Restart,
            On = on,
            Pending = pending,
            Disabled = f.PlainDisabledOnDisk && f.PlainRequest != PluginFileAction.Enable,
        };
    }

    // ---- facts from the process --------------------------------------------------------------

    internal static IServerModPauseBackend Backend()
    {
        var o = BackendOverride;
        if (o != null) return o();
        return ServerModPause.PauseBackend();
    }

    private static DateTime Now()
    {
        try { return ClockUtc?.Invoke() ?? DateTime.UtcNow; }
        catch { return DateTime.UtcNow; }
    }

    private static string Root()
    {
        try { return BepInExRoot?.Invoke() ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// An ordinary plugin the preloader turned off is not loaded, so nothing reports its version:
    /// read it from the renamed DLL's own <c>[BepInPlugin]</c> metadata (<c>Foo.dll.old</c>). Empty
    /// when the mod is not off on disk or no DLL declares it.
    /// </summary>
    internal static string DisabledVersionOf(string id)
    {
        string root = Root();
        if (root.Length == 0 || string.IsNullOrWhiteSpace(id)) return "";
        try
        {
            foreach (var r in DisabledOnDiskNow(root))
            {
                if (!string.Equals(r.PluginId, id, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var f in r.Files)
                {
                    if (!PluginFileActions.IsDll(f.Path)) continue;
                    string v = PluginFileActions.PluginVersionIn(
                        System.IO.Path.Combine(root, f.Path) + PluginFileActions.DisabledSuffix, id);
                    if (v.Length > 0) return v;
                }

                return "";
            }
        }
        catch { /* unreadable: the version stays unknown */ }

        return "";
    }

    /// <summary>True while the preloader holds this ordinary plugin off on disk.</summary>
    internal static bool PlainOffOnDisk(string id)
    {
        string root = Root();
        if (root.Length == 0 || string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            foreach (var r in PluginFileActions.DisabledOnDisk(root, PlainState()))
                if (string.Equals(r.PluginId, id, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { /* unreadable: not known to be off */ }

        return false;
    }

    /// <summary>The next-start state file, or an empty one when it cannot be read.</summary>
    internal static PluginFileState PlainState()
    {
        if (_scope > 0 && _scopedPlainState != null) return _scopedPlainState;
        string root = Root();
        PluginFileState state;
        if (root.Length == 0) state = new PluginFileState();
        else state = PluginFileActions.TryRead(root, out var read, out _) ? read : new PluginFileState();
        if (_scope > 0) _scopedPlainState = state;
        return state;
    }

    // HOSTS1 round 9: a Mods list build reads each row's switch facts. Per row, those read the preloader's
    // state file (JSON), stat the disabled DLLs and asked the pause backend twice. Inside a build scope
    // (main thread only) each is read once.
    private static int _scope;
    private static PluginFileState? _scopedPlainState;
    private static List<PluginFileResult>? _scopedDisabledOnDisk;
    private static IReadOnlyList<string>? _scopedSuspendable;
    private static IReadOnlyList<PausedPlugin>? _scopedSuspended;
    private static bool? _scopedSupported;

    internal static void EnterScope() => _scope++;

    internal static void LeaveScope()
    {
        if (_scope > 0) _scope--;
        if (_scope > 0) return;
        _scopedPlainState = null;
        _scopedDisabledOnDisk = null;
        _scopedSuspendable = null;
        _scopedSuspended = null;
        _scopedSupported = null;
    }

    private static List<PluginFileResult> DisabledOnDiskNow(string root)
    {
        if (_scope > 0 && _scopedDisabledOnDisk != null) return _scopedDisabledOnDisk;
        var list = PluginFileActions.DisabledOnDisk(root, PlainState());
        if (_scope > 0) _scopedDisabledOnDisk = list;
        return list;
    }

    private static void ReadBackend(out bool supported, out IReadOnlyList<string> suspendable, out IReadOnlyList<PausedPlugin> suspended)
    {
        if (_scope > 0 && _scopedSupported.HasValue && _scopedSuspendable != null && _scopedSuspended != null)
        {
            supported = _scopedSupported.Value;
            suspendable = _scopedSuspendable;
            suspended = _scopedSuspended;
            return;
        }

        var backend = Backend();
        supported = backend.Supported;
        suspendable = supported ? backend.Suspendable() : Array.Empty<string>();
        suspended = supported ? backend.Suspended() : Array.Empty<PausedPlugin>();
        if (_scope > 0)
        {
            _scopedSupported = supported;
            _scopedSuspendable = suspendable;
            _scopedSuspended = suspended;
        }
    }

    /// <summary>
    /// The facts for <paramref name="id"/>, read now. <paramref name="plain"/> is the identification
    /// of an ordinary plugin's files (null for everything else).
    /// </summary>
    internal static ModSwitchFacts FactsFor(string id, string name, ModOwner owner, PluginIdentification? plain = null)
    {
        bool supported = false, hot = false, suspended = false;
        if (owner == ModOwner.Progenitor)
        {
            try
            {
                ReadBackend(out supported, out var suspendable, out var pausedNow);
                if (supported)
                {
                    hot = Contains(suspendable, id);
                    foreach (var p in pausedNow)
                        if (string.Equals(p.Guid, id, StringComparison.OrdinalIgnoreCase)) { suspended = true; break; }
                }
            }
            catch { supported = false; }
        }

        bool known = ModRegistry.TryGet(id, out var entry);
        bool running = known && entry.Running;
        string offBecause = "";
        if (known && entry.Outcome == ModLoadOutcome.SkippedOff && entry.OffDependency.Length > 0)
            offBecause = ModRegistry.TryGet(entry.OffDependency, out var dep) ? dep.Name : entry.OffDependency;
        PluginFileAction? request = null;
        bool offOnDisk = false;
        if (owner == ModOwner.Plain)
        {
            var state = PlainState();
            request = PluginFileActions.PendingFor(state, id)?.Action;
            string root = Root();
            if (root.Length > 0)
                foreach (var r in DisabledOnDiskNow(root))
                    if (string.Equals(r.PluginId, id, StringComparison.OrdinalIgnoreCase)) { offOnDisk = true; break; }
        }

        return new ModSwitchFacts
        {
            Id = id,
            Name = name,
            Owner = owner,
            BackendSupported = supported,
            Hot = hot,
            Suspended = suspended,
            DisabledIntent = owner == ModOwner.Progenitor && ModEnableState.IsDisabled(id),
            Known = known,
            Outcome = known ? entry.Outcome : null,
            OffBecause = offBecause,
            RunningNow = owner == ModOwner.Plain ? plain != null && !offOnDisk : running,
            PlainWhy = owner == ModOwner.Plain ? (plain == null ? PluginFileActions.WhyNoLocation : plain.Why) : "",
            PlainRequest = request,
            PlainDisabledOnDisk = offOnDisk,
        };
    }

    internal static ModSwitchView Describe(string id, string name, ModOwner owner, PluginIdentification? plain = null) =>
        Decide(FactsFor(id, name, owner, plain));

    /// <summary>
    /// A plugin the switch holds off: the player turned it off, or it went with one the player turned
    /// off. A server resume must never bring it back. Read from the state file, so it survives a Core swap.
    /// </summary>
    internal static bool IsHeld(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (Gate) if (SessionHeld.Contains(id!)) return true;
        if (ModEnableState.IsDisabled(id)) return true;
        // Review finding 1: a plugin that went with a turned-off mod is held only while both are
        // still in Bootstrap's pause table. After a restart it loads and runs like any other, and a
        // server pause of it later is the server's, which a leave must end.
        HashSet<string>? suspended = null;
        foreach (var e in ModEnableState.Disabled())
            foreach (var h in ModEnableState.HeldWithNow(e))
            {
                if (!string.Equals(h, id, StringComparison.OrdinalIgnoreCase)) continue;
                suspended ??= SuspendedIds();
                if (suspended.Contains(h) && suspended.Contains(e.Id)) return true;
            }

        return false;
    }

    private static HashSet<string> SuspendedIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var backend = Backend();
            if (backend.Supported)
                foreach (var p in backend.Suspended()) set.Add(p.Guid);
        }
        catch { /* nothing known to be suspended: nothing is held by a sibling */ }
        return set;
    }

    /// <summary>
    /// Once per Core generation: drop the <c>heldWith</c> lists of turned-off mods Bootstrap no longer
    /// holds (a process start empties its table; a Core swap keeps it).
    /// </summary>
    internal static int PruneStaleHolds()
    {
        int n = ModEnableState.PruneOtherSessions();
        var suspended = SuspendedIds();
        return n + ModEnableState.PruneHeldWith(id => suspended.Contains(id));
    }

    /// <summary>
    /// Review finding 2: the plugins paused for the joined server right now — in Bootstrap's table
    /// and not held by the switch. Bootstrap's resume also brings back what a released plugin depends
    /// on and what depends on it, so no switch action may resume while this is not empty.
    /// </summary>
    internal static List<string> ServerPausedNow()
    {
        var list = new List<string>();
        try
        {
            var backend = Backend();
            if (!backend.Supported) return list;
            foreach (var p in backend.Suspended())
                if (!IsHeld(p.Guid) && !list.Contains(p.Guid)) list.Add(p.Guid);
        }
        catch { /* none known */ }
        return list;
    }

    /// <summary>Resume <paramref name="release"/> without the server-paused plugins, and keep those paused.</summary>
    private static IReadOnlyList<string> ResumeKeepingServerPauses(IServerModPauseBackend backend, List<string> release, List<string> serverPaused)
    {
        release.RemoveAll(r => serverPaused.Exists(s => string.Equals(s, r, StringComparison.OrdinalIgnoreCase)));
        if (release.Count == 0) return Array.Empty<string>();
        var loaded = backend.Resume(release, out _);
        if (serverPaused.Count > 0)
        {
            try { backend.KeepPaused(serverPaused); }
            catch { /* best effort, as ServerModPause does */ }
        }

        return loaded;
    }

    /// <summary>
    /// What else turning <paramref name="id"/> off takes with it, by name: a live switch, Bootstrap's
    /// closure (DLL siblings and dependants); a restart switch, the running plugins that hard-depend
    /// on it (their loads are skipped at the next start). Empty when nothing else goes.
    /// </summary>
    internal static List<string> AlsoTurnsOff(string id, ModSwitchView view)
    {
        var names = new List<string>();
        if (view == null) return names;
        if (view.Mode == ModSwitchMode.Live)
        {
            try
            {
                var closure = Backend().Closure(new[] { id }, out _);
                foreach (var g in closure)
                    if (!string.Equals(g, id, StringComparison.OrdinalIgnoreCase)) names.Add(NameOf(g));
            }
            catch { /* the confirm then lists nothing, and the switch still goes through Bootstrap's closure */ }
        }
        else if (view.Mode == ModSwitchMode.Restart)
        {
            foreach (var e in ModRegistry.RunningDependants(id)) names.Add(e.Name);
            // Review finding 6: ordinary plugins that need it are named too; they keep loading at the
            // next start and would find it off.
            foreach (var p in PlainDependantsOf(id))
                if (!names.Contains(p.Name)) names.Add(p.Name);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    private static string NameOf(string id) => ModRegistry.TryGet(id, out var e) ? e.Name : id;

    // ---- actions -----------------------------------------------------------------------------

    /// <summary>Press the switch. <paramref name="plain"/>: an ordinary plugin's identified files.</summary>
    internal static ModSwitchOutcome Set(string id, string name, ModOwner owner, bool on, PluginIdentification? plain = null)
    {
        var view = Describe(id, name, owner, plain);
        if (!view.Interactive) return ModSwitchOutcome.Fail(view.Reason);
        // Re-review of 9a27bf01, finding 1: read before the entry goes (turning on removes it).
        var cascade = on && owner == ModOwner.Progenitor
            ? new List<string>(ModEnableState.Get(id)?.PlainOff ?? new List<string>())
            : null;
        ModSwitchOutcome outcome;
        try
        {
            if (owner == ModOwner.Plain) outcome = SetPlain(id, name, on, plain);
            else if (view.Mode == ModSwitchMode.Live) outcome = on ? LiveOn(id) : LiveOff(id, name);
            else outcome = RestartSet(id, name, on);
        }
        catch (Exception e)
        {
            outcome = ModSwitchOutcome.Fail("The switch failed: " + e.Message);
        }

        Log(id, (on ? "on" : "off") + " -> " + (outcome.Ok ? "ok" : "refused") + ": " + outcome.Message);
        if (outcome.Ok && cascade != null && cascade.Count > 0) RestorePlainDependants(cascade);
        if (outcome.Ok) Raise(id);
        return outcome;
    }

    /// <summary>
    /// After a restart-only Progenitor mod is turned off: turn off at the next start the ordinary
    /// plugins that hard-depend on it, where the preloader can name their files, and record that this
    /// mod caused it (<see cref="ModEnableEntry.PlainOff"/>). Returns their names.
    /// </summary>
    internal static List<string> TurnOffPlainDependants(string id, Func<string, PluginIdentification?> identify)
    {
        var names = new List<string>();
        var ids = new List<string>();
        foreach (var d in PlainDependantsOf(id))
        {
            PluginIdentification? ident;
            try { ident = identify?.Invoke(d.Id); }
            catch { ident = null; }
            if (ident is not { Certain: true }) continue;
            // One the player already has off (or has asked to) is not this mod's doing.
            var before = Describe(d.Id, d.Name, ModOwner.Plain, ident);
            if (!before.On) continue;
            if (!Set(d.Id, d.Name, ModOwner.Plain, on: false, ident).Ok) continue;
            names.Add(d.Name);
            ids.Add(d.Id);
        }

        if (ids.Count > 0) ModEnableState.SetPlainOff(id, ids);
        return names;
    }

    /// <summary>
    /// Re-review of 9a27bf01, finding 1: the mod that caused them is on again, so each ordinary plugin
    /// it turned off comes back: a request still waiting is canceled, one already carried out gets a
    /// turn-on request. A plugin the player turned off separately was never recorded here.
    /// </summary>
    internal static List<string> RestorePlainDependants(IReadOnlyList<string> plainIds)
    {
        var restored = new List<string>();
        string root = Root();
        if (root.Length == 0) return restored;
        foreach (var d in plainIds)
        {
            var state = PlainState();
            var waiting = PluginFileActions.PendingFor(state, d);
            if (waiting != null && waiting.Action == PluginFileAction.Disable)
            {
                if (PluginFileActions.Cancel(root, d, out _, LockWait)) restored.Add(d);
                continue;
            }

            foreach (var r in PluginFileActions.DisabledOnDisk(root, state))
            {
                if (!string.Equals(r.PluginId, d, StringComparison.OrdinalIgnoreCase)) continue;
                if (SetPlain(d, r.Name, on: true, null).Ok) restored.Add(d);
                break;
            }
        }

        if (restored.Count > 0) Log(string.Join(",", plainIds), "restored with the mod they need: " + string.Join(", ", restored));
        return restored;
    }

    private static ModSwitchOutcome LiveOff(string id, string name)
    {
        var backend = Backend();
        if (!backend.Supported) return ModSwitchOutcome.Fail(ModSwitchWords.NoBackendReason);
        bool alreadyOut = false;
        foreach (var p in backend.Suspended())
            if (string.Equals(p.Guid, id, StringComparison.OrdinalIgnoreCase)) { alreadyOut = true; break; }

        // The choice is written first, so whatever happens next the mod stays off at the next start.
        if (!ModEnableState.Set(id, name, enabled: false, heldWith: null, Now(), out string error))
            return ModSwitchOutcome.Fail(error);
        if (alreadyOut)
        {
            lock (Gate) SessionHeld.Add(id);
            Invalidate();
            return ModSwitchOutcome.Done(TurnedOff, new[] { id });
        }

        var taken = backend.Suspend(new[] { id }, Array.Empty<string>(), out string report);
        if (taken.Count == 0)
        {
            ModEnableState.Set(id, name, enabled: true, heldWith: null, Now(), out _);
            return ModSwitchOutcome.Fail(report.Length > 0 ? report : "It could not be unloaded.");
        }

        ModEnableState.Set(id, name, enabled: false, heldWith: taken, Now(), out _);
        lock (Gate)
            foreach (var t in taken)
            {
                SessionHeld.Add(t);
                // CLIENTPOLICY3: this unload is the player's, not a server pause (turning it on undoes it).
                SwitchTook.Add(t);
            }
        foreach (var t in taken) ModProblems.Clear(t);
        Invalidate();
        return ModSwitchOutcome.Done(TurnedOff, taken);
    }

    private static ModSwitchOutcome LiveOn(string id)
    {
        var backend = Backend();
        if (!backend.Supported) return ModSwitchOutcome.Fail(ModSwitchWords.NoBackendReason);
        var entry = ModEnableState.Get(id);
        var release = new List<string> { id };
        if (entry != null)
            foreach (var h in ModEnableState.HeldWithNow(entry))
                if (!ModEnableState.IsDisabled(h) && !release.Contains(h)) release.Add(h);
        // Review finding 2: read before the hold is released (released plugins are not held any more).
        var serverPaused = ServerPausedNow();
        if (!ModEnableState.Set(id, entry?.Name, enabled: true, heldWith: null, Now(), out string error))
            return ModSwitchOutcome.Fail(error);
        bool switchTookIt;
        lock (Gate)
        {
            switchTookIt = SwitchTook.Contains(id);
            foreach (var r in release)
            {
                SessionHeld.Remove(r);
                SwitchTook.Remove(r);
            }
        }

        bool suspended = false;
        foreach (var p in backend.Suspended())
            if (string.Equals(p.Guid, id, StringComparison.OrdinalIgnoreCase)) { suspended = true; break; }

        if (!suspended)
        {
            // Loaded but inert: its OnLoad was skipped at this start because it was off. Unload it
            // and load it again so OnLoad runs now.
            if (!ModRegistry.IsSkipped(id)) { Invalidate(); return ModSwitchOutcome.Done(TurnedOn); }
            var taken = backend.Suspend(new[] { id }, Array.Empty<string>(), out string report);
            if (taken.Count == 0) return ModSwitchOutcome.Fail(report.Length > 0 ? report : "It could not be reloaded.");
            release = new List<string>(taken);
        }
        else if (ServerModPause.PolicyStanding(id) == ServerModPause.StandingNotAllowed)
        {
            // CLIENTPOLICY3: the joined server's client mod policy does not allow it: it stays paused, and the
            // result says why.
            Invalidate();
            return ModSwitchOutcome.Done(TurnedOnNotAllowed);
        }
        else if (!switchTookIt && ServerModPause.PausedForCurrentServer(id))
        {
            // The server the player is on still does not run it: that pause is the server's, not the
            // player's, and it ends when they leave. (A pause the switch itself made is the player's: turning the
            // mod on undoes it, live 2026-10-05.)
            Invalidate();
            return ModSwitchOutcome.Done(TurnedOnPaused);
        }

        // Review finding 2: while the joined server has mods paused, a resume could bring them back
        // with this one (Bootstrap resumes what it depends on and what depends on it). It stays
        // unloaded, no longer held, so the leave's resume loads it.
        // CLIENTPOLICY3: a mod the joined server requires loads now anyway (waiting for the leave would cost the player
        // the join under Disconnect the player); the server's pauses are kept around the resume.
        if (serverPaused.Count > 0 && ServerModPause.PolicyStanding(id) != ServerModPause.StandingRequired)
        {
            Invalidate();
            return ModSwitchOutcome.Done(ModSwitchWords.TurnedOnAfterLeave, release);
        }

        var loaded = ResumeKeepingServerPauses(backend, release, serverPaused);
        Invalidate();
        return ModSwitchOutcome.Done(Contains(loaded, id) ? TurnedOn : TurnedOnLoading, release);
    }

    private static ModSwitchOutcome RestartSet(string id, string name, bool on)
    {
        if (!ModEnableState.Set(id, name, on, heldWith: null, Now(), out string error)) return ModSwitchOutcome.Fail(error);
        var after = Describe(id, name, ModOwner.Progenitor);
        Invalidate();
        return ModSwitchOutcome.Done(after.Pending == ModSwitchPending.None ? NothingChangesAtStart : after.PendingWord + ".");
    }

    private static ModSwitchOutcome SetPlain(string id, string name, bool on, PluginIdentification? plain)
    {
        string root = Root();
        if (root.Length == 0) return ModSwitchOutcome.Fail(ModSwitchWords.NoBackendReason);
        var state = PlainState();
        var waiting = PluginFileActions.PendingFor(state, id);
        bool offOnDisk = false;
        PluginFileResult? disabledResult = null;
        foreach (var r in PluginFileActions.DisabledOnDisk(root, state))
            if (string.Equals(r.PluginId, id, StringComparison.OrdinalIgnoreCase)) { offOnDisk = true; disabledResult = r; break; }

        // Pressing the switch back before the restart cancels the waiting request.
        if (waiting != null && ((on && waiting.Action == PluginFileAction.Disable) || (!on && waiting.Action == PluginFileAction.Enable)))
        {
            if (!PluginFileActions.Cancel(root, id, out string cancelError, LockWait) && cancelError.Length > 0)
                return ModSwitchOutcome.Fail(BusyOr(cancelError));
            return ModSwitchOutcome.Done(RequestCanceled + " " + NothingChangesAtStart);
        }

        PluginFileRequest request;
        if (!on)
        {
            if (plain == null || !plain.Certain) return ModSwitchOutcome.Fail(ManagedElsewhereReason(plain?.Why ?? ""));
            request = new PluginFileRequest { PluginId = id, Name = name, Action = PluginFileAction.Disable, Folder = plain.Folder, Files = plain.Files, Loose = plain.Loose };
        }
        else
        {
            if (!offOnDisk || disabledResult == null) return ModSwitchOutcome.Done(NothingChangesAtStart);
            var files = new List<PluginFileEntry>();
            foreach (var f in disabledResult.Files)
            {
                string disabled = System.IO.Path.Combine(root, f.Path) + PluginFileActions.DisabledSuffix;
                files.Add(new PluginFileEntry(f.Path, PluginFileActions.IsDll(f.Path) ? PluginFileActions.Sha256Of(disabled) : ""));
            }

            request = new PluginFileRequest { PluginId = id, Name = name, Action = PluginFileAction.Enable, Folder = disabledResult.Folder, Files = files, Loose = disabledResult.Loose };
        }

        request.RequestedUtc = Now().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        if (!PluginFileActions.Request(root, request, out string error, LockWait)) return ModSwitchOutcome.Fail(BusyOr(error));
        ModProblems.Clear(id, ModProblemKinds.StartActionFailed);
        Invalidate();
        return ModSwitchOutcome.Done(WordOf(on ? ModSwitchPending.TurnsOn : ModSwitchPending.TurnsOff) + ".");
    }

    /// <summary>Uninstall an ordinary plugin at the next start: its folder is moved to <c>BepInEx/progenitor-removed</c>.</summary>
    internal static ModSwitchOutcome RequestRemove(string id, string name, PluginIdentification? plain)
    {
        string root = Root();
        if (root.Length == 0) return ModSwitchOutcome.Fail(ModSwitchWords.NoBackendReason);
        var state = PlainState();
        PluginFileRequest? request = null;
        if (plain != null && plain.Certain)
            request = new PluginFileRequest { PluginId = id, Name = name, Action = PluginFileAction.Remove, Folder = plain.Folder, Files = plain.Files, Loose = plain.Loose };
        else
        {
            foreach (var r in PluginFileActions.DisabledOnDisk(root, state))
            {
                if (!string.Equals(r.PluginId, id, StringComparison.OrdinalIgnoreCase)) continue;
                var files = new List<PluginFileEntry>();
                foreach (var f in r.Files)
                {
                    string full = System.IO.Path.Combine(root, f.Path);
                    string actual = System.IO.File.Exists(full) ? full : full + PluginFileActions.DisabledSuffix;
                    files.Add(new PluginFileEntry(f.Path, PluginFileActions.IsDll(f.Path) ? PluginFileActions.Sha256Of(actual) : ""));
                }

                request = new PluginFileRequest { PluginId = id, Name = name, Action = PluginFileAction.Remove, Folder = r.Folder, Files = files, Loose = r.Loose };
                break;
            }
        }

        if (request == null) return ModSwitchOutcome.Fail(ManagedElsewhereReason(plain?.Why ?? ""));
        request.RequestedUtc = Now().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        if (!PluginFileActions.Request(root, request, out string error, LockWait)) return ModSwitchOutcome.Fail(BusyOr(error));
        ModProblems.Clear(id, ModProblemKinds.StartActionFailed);
        Log(id, "uninstall requested for the next start");
        Invalidate();
        Raise(id);
        return ModSwitchOutcome.Done(ModSwitchWords.RemovedAtNextStart + ".");
    }

    /// <summary>Cancel an ordinary plugin's waiting next-start request.</summary>
    internal static ModSwitchOutcome CancelRequest(string id)
    {
        string root = Root();
        if (root.Length == 0) return ModSwitchOutcome.Fail(ModSwitchWords.NoBackendReason);
        if (!PluginFileActions.Cancel(root, id, out string error, LockWait))
            return error.Length > 0 ? ModSwitchOutcome.Fail(BusyOr(error)) : ModSwitchOutcome.Done(NothingChangesAtStart);
        Log(id, "next-start request canceled");
        Invalidate();
        Raise(id);
        return ModSwitchOutcome.Done(RequestCanceled + " " + NothingChangesAtStart);
    }

    /// <summary>True when a failed hot-loadable mod can be loaded again now.</summary>
    internal static bool CanRetry(string id)
    {
        if (!ModRegistry.TryGet(id, out var e) || e.Outcome != ModLoadOutcome.Failed) return false;
        try
        {
            ReadBackend(out bool supported, out var suspendable, out _);
            return supported && Contains(suspendable, id);
        }
        catch { return false; }
    }

    /// <summary>Load a mod whose <c>OnLoad</c> threw again: unload it (reason Suspend) and load it.</summary>
    internal static ModSwitchOutcome Retry(string id)
    {
        if (!CanRetry(id)) return ModSwitchOutcome.Fail(RetryRestartOnly);
        var backend = Backend();
        // Review finding 2: never while the joined server has mods paused (the resume could load them).
        var serverPaused = ServerPausedNow();
        if (serverPaused.Count > 0) return ModSwitchOutcome.Fail(ModSwitchWords.RetryAfterLeave);
        var taken = backend.Suspend(new[] { id }, Array.Empty<string>(), out string report);
        if (taken.Count == 0) return ModSwitchOutcome.Fail(report.Length > 0 ? report : "It could not be unloaded.");
        ResumeKeepingServerPauses(backend, new List<string>(taken), serverPaused);
        Log(id, "retry: reloaded " + string.Join(", ", taken));
        Invalidate();
        Raise(id);
        return ModSwitchOutcome.Done(RetryStarted, taken);
    }

    // ---- next-start results -------------------------------------------------------------------

    /// <summary>
    /// Turn the preloader's failed results into problems (a warning on the mod, saying plainly what
    /// could not be done), so the row and the page say so instead of pretending. Called when the menu
    /// opens. A later request for the same plugin replaces the result, and clears the problem.
    /// </summary>
    internal static int ReconcileStartResults()
    {
        var state = PlainState();
        int n = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = state.Results.Count - 1; i >= 0; i--)
        {
            var r = state.Results[i];
            if (!seen.Add(r.PluginId)) continue;
            if (r.Done)
            {
                ModProblems.Clear(r.PluginId, ModProblemKinds.StartActionFailed);
                continue;
            }

            string what = r.Action switch
            {
                PluginFileAction.Enable => "turned on",
                PluginFileAction.Remove => "removed",
                _ => "turned off",
            };
            ModProblems.Report(new ModProblem(
                ModProblemSeverity.Warning, ModProblemKinds.StartActionFailed, r.PluginId,
                "It could not be " + what + " at the last start: " + r.Reason, ModProblems.StartActionWhatToDo,
                PluginFileActions.Describe(r), listLine: "Could not be " + what + " at start"));
            n++;
        }

        return n;
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static void Invalidate()
    {
        try { ModMenuInventory.Invalidate("mod-switch"); }
        catch { /* evidence, not a gate */ }
    }

    private static void Raise(string id)
    {
        try { Changed?.Invoke(id); }
        catch { /* a listener must not cost the switch */ }
    }

    private static void Log(string id, string line)
    {
        try { Plugin.Logger?.LogInfo("Mods: switch " + id + ": " + line); }
        catch { /* */ }
    }

    private static bool Contains(IReadOnlyList<string>? list, string id)
    {
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            SessionHeld.Clear();
            SwitchTook.Clear();
        }

        BackendOverride = null;
        BepInExRoot = null;
        ClockUtc = null;
        PlainDependantsProbe = null;
        Changed = null;
    }
}
