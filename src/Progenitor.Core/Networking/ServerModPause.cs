using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Progenitor.Events;
using Progenitor.Plugins;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// Read-only view of the per-server mod pause: which of this client's hot-loaded plugins are
/// paused, and for which server. Returned by <see cref="ServerModPause.Current"/>.
/// </summary>
public sealed class ServerModPauseState
{
    internal static readonly ServerModPauseState Empty = new(Array.Empty<string>(), Array.Empty<string>(), "", false);

    internal ServerModPauseState(IReadOnlyList<string> pausedIds, IReadOnlyList<string> pausedNames, string host, bool serverWithoutProgenitor,
        IReadOnlyList<string>? resumingIds = null)
    {
        ResumingIds = resumingIds ?? Array.Empty<string>();
        PausedIds = pausedIds ?? Array.Empty<string>();
        PausedNames = pausedNames ?? Array.Empty<string>();
        Host = host ?? "";
        ServerWithoutProgenitor = serverWithoutProgenitor;
    }

    /// <summary>
    /// True when the pause was taken because the server sent no Progenitor handshake request
    /// within the grace period (a BepInEx-only or vanilla server).
    /// </summary>
    public bool ServerWithoutProgenitor { get; }

    /// <summary>
    /// Plugins released from the pause that have not loaded yet (their previous plugin context has
    /// not been collected). Not in <see cref="PausedIds"/>.
    /// </summary>
    public IReadOnlyList<string> ResumingIds { get; }

    /// <summary>BepInPlugin GUIDs of the paused plugins. Empty when nothing is paused.</summary>
    public IReadOnlyList<string> PausedIds { get; }

    /// <summary>Display names, index-aligned with <see cref="PausedIds"/>.</summary>
    public IReadOnlyList<string> PausedNames { get; }

    /// <summary>The address this client dialed when the pause was taken, or empty when it could not be read.</summary>
    public string Host { get; }

    /// <summary>True while at least one plugin is paused.</summary>
    public bool Any => PausedIds.Count > 0;
}

/// <summary>
/// Published on <see cref="EventBus"/> (client only) after this client paused hot-loaded plugins
/// because the server it is joining does not run them. Subscribe through <c>ctx.Events</c> so the
/// subscription is dropped with your plugin.
/// </summary>
public sealed class ModsPaused
{
    /// <summary>Construct. Core publishes this; tests construct it directly.</summary>
    public ModsPaused(IReadOnlyList<string> ids, string host, bool serverWithoutProgenitor = false)
    {
        Ids = ids ?? Array.Empty<string>();
        Host = host ?? "";
        ServerWithoutProgenitor = serverWithoutProgenitor;
    }

    /// <summary>True when the server sent no Progenitor handshake at all (see <see cref="ServerModPauseState.ServerWithoutProgenitor"/>).</summary>
    public bool ServerWithoutProgenitor { get; }

    /// <summary>The plugin GUIDs this pause took (DLL siblings and dependents included).</summary>
    public IReadOnlyList<string> Ids { get; }

    /// <summary>The address this client dialed, or empty.</summary>
    public string Host { get; }
}

/// <summary>
/// Published on <see cref="EventBus"/> (client only) after paused plugins were loaded again:
/// the client left the server, or a later handshake request showed the server runs them.
/// </summary>
public sealed class ModsResumed
{
    /// <summary>Construct. Core publishes this; tests construct it directly.</summary>
    public ModsResumed(IReadOnlyList<string> ids, string reason)
    {
        Ids = ids ?? Array.Empty<string>();
        Reason = reason ?? "";
    }

    /// <summary>The plugin GUIDs released from the pause.</summary>
    public IReadOnlyList<string> Ids { get; }

    /// <summary><c>disconnect</c> or <c>server-runs-it</c>.</summary>
    public string Reason { get; }
}

/// <summary>One paused plugin, as Bootstrap reports it.</summary>
internal readonly struct PausedPlugin
{
    internal PausedPlugin(string guid, string name, string version, bool resuming = false, IReadOnlyList<string>? rows = null,
        IReadOnlyList<string>? hardRows = null)
    {
        HardRows = hardRows ?? Array.Empty<string>();
        Guid = guid ?? "";
        Name = string.IsNullOrEmpty(name) ? Guid : name;
        Version = version ?? "";
        Resuming = resuming;
        Rows = rows ?? Array.Empty<string>();
    }

    internal string Guid { get; }
    internal string Name { get; }
    internal string Version { get; }

    /// <summary>Released from the pause, waiting to load (its old plugin ALC has not collected yet).</summary>
    internal bool Resuming { get; }

    /// <summary>The handshake row ids it provided when it was paused (kept by Bootstrap across Core swaps).</summary>
    internal IReadOnlyList<string> Rows { get; }

    /// <summary>The subset of <see cref="Rows"/> declared ServerMustHave.</summary>
    internal IReadOnlyList<string> HardRows { get; }

    private static List<string> List(string[] parts, int i)
    {
        var list = new List<string>();
        if (parts.Length > i)
            foreach (var r in parts[i].Split(','))
                if (r.Trim().Length > 0) list.Add(r.Trim());
        return list;
    }

    /// <summary>Parse Bootstrap's <c>guid\tname\tversion\tstate\trow,row\thardrow,hardrow</c> row.</summary>
    internal static bool TryParse(string? row, out PausedPlugin plugin)
    {
        plugin = default;
        if (string.IsNullOrEmpty(row)) return false;
        var parts = row!.Split('\t');
        if (parts.Length == 0 || parts[0].Length == 0) return false;
        plugin = new PausedPlugin(parts[0], parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : "",
            parts.Length > 3 && string.Equals(parts[3], "resuming", StringComparison.Ordinal),
            List(parts, 4), List(parts, 5));
        return true;
    }
}

/// <summary>
/// The Bootstrap half of the pause, as Core sees it. Production binds
/// <c>Progenitor.Bootstrap.ProcessHost</c> by reflection (<see cref="ReflectedPauseBackend"/>); the
/// tests supply a fake.
/// </summary>
internal interface IServerModPauseBackend
{
    /// <summary>False when this Bootstrap cannot pause (absent, or older than the feature).</summary>
    bool Supported { get; }

    /// <summary>Why <see cref="Supported"/> is false, for the log. Empty when supported.</summary>
    string UnsupportedReason { get; }

    IReadOnlyList<string> Suspendable();

    /// <summary>Paused and resuming plugins (not loaded).</summary>
    IReadOnlyList<PausedPlugin> Suspended();
    IReadOnlyList<string> Closure(IReadOnlyList<string> guids, out string refusal);

    /// <summary>Pause; <paramref name="rowOwners"/> entries are <c>plugin\trow</c>. Returns the GUIDs now paused.</summary>
    IReadOnlyList<string> Suspend(IReadOnlyList<string> guids, IReadOnlyList<string> rowOwners, out string report);

    /// <summary>Release; returns only the GUIDs loaded now. The rest stay resuming until a scan loads them.</summary>
    IReadOnlyList<string> Resume(IReadOnlyList<string>? guids, out string report);

    /// <summary>
    /// True/false when Bootstrap knows whether this client process started (hosts) the server it
    /// plays on (a private game); null when that cannot be read.
    /// </summary>
    bool? HostsPrivateGame();

    /// <summary>A deferred handshake request carried across a Core swap ("" when none).</summary>
    string PendingRequest { get; set; }

    /// <summary>Resumed plugins not reported yet, carried across a Core swap ("" when none).</summary>
    string ResumeReports { get; set; }

    /// <summary>Keep still-resuming plugins paused. Returns the GUIDs kept.</summary>
    IReadOnlyList<string> KeepPaused(IReadOnlyList<string> guids);

    /// <summary>Identity of the connection the client is on (address and join stamp); "" when unknown.</summary>
    string ConnectionIdentity();
}

/// <summary>
/// Capability-checked reflection onto Bootstrap's pause entry points. Every required member must
/// exist with the expected shape; an older Bootstrap that lacks them yields an unsupported backend
/// and today's plain refusal, never a half-bound one.
/// </summary>
internal sealed class ReflectedPauseBackend : IServerModPauseBackend
{
    internal const string SuspendName = "SuspendPlugins";
    internal const string ResumeName = "ResumePlugins";
    internal const string SuspendedName = "SuspendedPlugins";
    internal const string SuspendableName = "SuspendablePluginGuids";
    internal const string ClosureName = "PluginSuspendClosure";
    internal const string PendingName = "PendingHandshakeRequest";
    internal const string HostingName = "IsHostingPrivateGame";
    internal const string KeepName = "KeepPluginsPaused";
    internal const string ReportsName = "PendingResumeReports";
    internal const string ConnectionName = "ConnectionIdentity";

    private readonly MethodInfo? _suspend;
    private readonly MethodInfo? _resume;
    private readonly MethodInfo? _suspended;
    private readonly MethodInfo? _suspendable;
    private readonly MethodInfo? _closure;
    private readonly PropertyInfo? _pending;
    private readonly PropertyInfo? _hosting;
    private readonly MethodInfo? _keep;
    private readonly PropertyInfo? _reports;
    private readonly PropertyInfo? _connection;

    private ReflectedPauseBackend(Type host, string reason)
    {
        UnsupportedReason = reason;
        if (reason.Length > 0) return;
        _suspend = Find(host, SuspendName);
        _resume = Find(host, ResumeName);
        _suspended = Find(host, SuspendedName);
        _suspendable = Find(host, SuspendableName);
        _closure = Find(host, ClosureName);
        _pending = Prop(host, PendingName, typeof(string));
        _hosting = Prop(host, HostingName, typeof(bool));
        _keep = Find(host, KeepName);
        _reports = Prop(host, ReportsName, typeof(string));
        _connection = Prop(host, ConnectionName, typeof(string));
    }

    public bool Supported => UnsupportedReason.Length == 0;

    public string UnsupportedReason { get; }

    /// <summary>True when there was no Bootstrap to bind (not cached by the caller: it may link later).</summary>
    internal bool NoBootstrap { get; private set; }

    /// <summary>
    /// Bind to <paramref name="host"/> (Bootstrap's <c>ProcessHost</c>), or report why not. Null
    /// means Bootstrap is absent (or not linked yet) and Core's own reloader loads plugins; it has no pause.
    /// </summary>
    internal static IServerModPauseBackend Create(Type? host)
    {
        if (host == null)
            return new ReflectedPauseBackend(typeof(object), ServerModPauseText.NoBootstrap) { NoBootstrap = true };
        var arr = typeof(string[]);
        var byRefString = typeof(string).MakeByRefType();
        string? missing = null;
        if (!Has(host, SuspendName, arr, arr, arr, byRefString)) missing = SuspendName;
        else if (!Has(host, ResumeName, arr, arr, byRefString)) missing = ResumeName;
        else if (!Has(host, SuspendedName, arr)) missing = SuspendedName;
        else if (!Has(host, SuspendableName, arr)) missing = SuspendableName;
        else if (!Has(host, ClosureName, arr, arr, byRefString)) missing = ClosureName;
        else if (Prop(host, PendingName, typeof(string)) is not { CanRead: true, CanWrite: true }) missing = PendingName;
        else if (Prop(host, ReportsName, typeof(string)) is not { CanRead: true, CanWrite: true }) missing = ReportsName;
        else if (!Has(host, KeepName, arr, arr)) missing = KeepName;
        return new ReflectedPauseBackend(host, missing == null ? "" : ServerModPauseText.OldBootstrap(host.FullName + "." + missing));
    }

    public IReadOnlyList<string> Suspendable() => Strings(Call(_suspendable, Array.Empty<object?>()));

    public IReadOnlyList<PausedPlugin> Suspended()
    {
        var list = new List<PausedPlugin>();
        foreach (var row in Strings(Call(_suspended, Array.Empty<object?>())))
            if (PausedPlugin.TryParse(row, out var p)) list.Add(p);
        return list;
    }

    public IReadOnlyList<string> Closure(IReadOnlyList<string> guids, out string refusal)
    {
        var args = new object?[] { ToArray(guids), null };
        var result = Strings(Call(_closure, args));
        refusal = args[1] as string ?? "";
        return result;
    }

    public IReadOnlyList<string> Suspend(IReadOnlyList<string> guids, IReadOnlyList<string> rowOwners, out string report)
    {
        var args = new object?[] { ToArray(guids), ToArray(rowOwners ?? Array.Empty<string>()), null };
        var result = Strings(Call(_suspend, args));
        report = args[2] as string ?? "";
        return result;
    }

    public IReadOnlyList<string> Resume(IReadOnlyList<string>? guids, out string report)
    {
        var args = new object?[] { guids == null ? null : ToArray(guids), null };
        var result = Strings(Call(_resume, args));
        report = args[1] as string ?? "";
        return result;
    }

    public bool? HostsPrivateGame()
    {
        if (!Supported || _hosting == null) return null;
        try { return _hosting.GetValue(null) is bool b ? b : null; }
        catch { return null; }
    }

    public IReadOnlyList<string> KeepPaused(IReadOnlyList<string> guids) =>
        Strings(Call(_keep, new object?[] { ToArray(guids) }));

    public string ConnectionIdentity()
    {
        if (!Supported || _connection == null) return "";
        try { return _connection.GetValue(null) as string ?? ""; }
        catch { return ""; }
    }

    public string ResumeReports
    {
        get
        {
            if (!Supported || _reports == null) return "";
            try { return _reports.GetValue(null) as string ?? ""; }
            catch { return ""; }
        }
        set
        {
            if (!Supported || _reports == null) return;
            try { _reports.SetValue(null, value ?? ""); }
            catch { /* only costs a late ModsResumed */ }
        }
    }

    public string PendingRequest
    {
        get
        {
            if (!Supported || _pending == null) return "";
            try { return _pending.GetValue(null) as string ?? ""; }
            catch { return ""; }
        }
        set
        {
            if (!Supported || _pending == null) return;
            try { _pending.SetValue(null, value ?? ""); }
            catch { /* a lost carry-over only costs the swap-time reply */ }
        }
    }

    private object? Call(MethodInfo? method, object?[] args)
    {
        if (!Supported || method == null) return null;
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
    }

    private static MethodInfo? Find(Type host, string name)
    {
        try { return host.GetMethod(name, BindingFlags.Public | BindingFlags.Static); }
        catch { return null; }
    }

    private static PropertyInfo? Prop(Type host, string name, Type type)
    {
        try
        {
            var p = host.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            return p != null && p.PropertyType == type ? p : null;
        }
        catch { return null; }
    }

    private static bool Has(Type host, string name, Type returns, params Type[] parameters)
    {
        var m = Find(host, name);
        if (m == null || m.ReturnType != returns) return false;
        var ps = m.GetParameters();
        if (ps.Length != parameters.Length) return false;
        for (int i = 0; i < ps.Length; i++)
            if (ps[i].ParameterType != parameters[i]) return false;
        return true;
    }

    private static string[] ToArray(IReadOnlyList<string> list)
    {
        var a = new string[list.Count];
        for (int i = 0; i < a.Length; i++) a[i] = list[i];
        return a;
    }

    private static IReadOnlyList<string> Strings(object? raw)
    {
        var list = new List<string>();
        if (raw is string[] rows)
            foreach (var s in rows) if (!string.IsNullOrEmpty(s)) list.Add(s);
        return list;
    }
}

/// <summary>What one handshake request should do to the pause.</summary>
internal sealed class ServerModPausePlan
{
    internal static readonly ServerModPausePlan Nothing = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "");

    internal ServerModPausePlan(IReadOnlyList<string> resume, IReadOnlyList<string> pause, IReadOnlyList<string> restartNeeded, string why)
    {
        Resume = resume;
        Pause = pause;
        RestartNeeded = restartNeeded;
        Why = why ?? "";
    }

    /// <summary>Paused plugins the server asks about: load them again before the reply.</summary>
    public IReadOnlyList<string> Resume { get; }

    /// <summary>Plugins to pause before the reply (the full closure: DLL siblings and dependents).</summary>
    public IReadOnlyList<string> Pause { get; }

    /// <summary>Failing rows owned by plugins that cannot be paused (loaded at game start).</summary>
    public IReadOnlyList<string> RestartNeeded { get; }

    /// <summary>Why nothing is paused although the join is predicted to fail; empty otherwise.</summary>
    public string Why { get; }

    /// <summary>
    /// Mod compatibility C3: the plugins of <see cref="Pause"/> paused because the server requires exactly its version
    /// (or its minor) of a both-sides optional mod, for the notice's reason. Empty otherwise.
    /// </summary>
    public IReadOnlyList<string> StrictMatch { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The pure decisions of per-server mod pause. Nothing here touches Bootstrap or the game.
/// </summary>
internal static class ServerModPausePolicy
{
    internal const string WhyTruncated = "the server's request filled every slot, so it may have more requirements than it sent";
    internal const string WhyAutoPauseOff = "automatic pause is off for this server";
    internal const string WhyAsked = "the server asks about a plugin the pause would take";
    internal const string WhyNotClean = "pausing the plugins this client can pause would not make the join succeed";
    internal const string WhyRestartOnly = "a failing mod was loaded at game start and cannot be paused";
    internal const string WhyNotServerMustHave = "a failing mod does not declare ServerMustHave, so it is never paused";
    internal const string WhyProtected = "the pause would also take a mod that declares it works without the server (NoNeedForSync or ClientOnly)";

    /// <summary>
    /// Would the server accept this client's reply? The same prediction the server makes:
    /// <see cref="Handshake.Compare.Evaluate(IReadOnlyList{Handshake.ModRecord}, IReadOnlyList{Handshake.ModRecord}, out List{string})"/>
    /// of the asked rows against exactly the rows <see cref="Handshake.Compare.PackReply"/> would
    /// send, plus the fail-closed overflow a dropped declared row causes.
    /// </summary>
    internal static bool PredictAccepted(
        IReadOnlyList<Handshake.ModRecord> asked,
        IReadOnlyList<Handshake.ModRecord> provided,
        out List<string> failedIds)
    {
        var packed = Handshake.Compare.PackReply(asked, provided);
        Handshake.Compare.Evaluate(asked, packed.Rows, out failedIds);
        return failedIds.Count == 0 && !packed.DeclaredDropped;
    }

    /// <summary>
    /// The paused plugins this request asks about (by plugin GUID, or by any row the plugin
    /// provided before it was paused). They are resumed before the reply so the server that runs
    /// them gets their rows. Order follows <paramref name="suspended"/>.
    /// </summary>
    internal static List<string> ResumeFor(
        IReadOnlyList<Handshake.ModRecord>? asked,
        IReadOnlyList<string>? suspended,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? rowsByPlugin)
    {
        var result = new List<string>();
        if (asked == null || suspended == null || suspended.Count == 0) return result;
        var askedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in asked) if (!string.IsNullOrEmpty(rec.Guid)) askedIds.Add(rec.Guid);
        foreach (var plugin in suspended)
        {
            if (string.IsNullOrEmpty(plugin)) continue;
            bool hit = askedIds.Contains(plugin);
            if (!hit && rowsByPlugin != null && rowsByPlugin.TryGetValue(plugin, out var rows) && rows != null)
                foreach (var row in rows) if (askedIds.Contains(row)) { hit = true; break; }
            if (hit && !result.Contains(plugin)) result.Add(plugin);
        }
        return result;
    }

    /// <summary>
    /// The plugins to pause so this join succeeds, or nothing. Pauses only when all of these hold:
    /// the request was not full (<paramref name="truncated"/> false), auto-pause is on, every
    /// failing row the client provides is owned by a pausable plugin, the closure Bootstrap would
    /// take asks for none of the rows the server asked about, and the join predicted without the
    /// closure's rows is clean. Otherwise the plan pauses nothing and today's refusal happens.
    /// </summary>
    internal static ServerModPausePlan PlanPause(
        IReadOnlyList<Handshake.ModRecord> asked,
        bool truncated,
        IReadOnlyList<Handshake.ModRecord> provided,
        IReadOnlyDictionary<string, string>? owners,
        IReadOnlyCollection<string>? suspendable,
        Func<IReadOnlyList<string>, (IReadOnlyList<string> Closure, string Refusal)> closure,
        bool autoPause,
        bool pauseOptional = false)
    {
        asked ??= Array.Empty<Handshake.ModRecord>();
        provided ??= Array.Empty<Handshake.ModRecord>();
        if (PredictAccepted(asked, provided, out var failed)) return ServerModPausePlan.Nothing;

        var askedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in asked) if (!string.IsNullOrEmpty(rec.Guid)) askedIds.Add(rec.Guid);
        var providedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in provided) if (!string.IsNullOrEmpty(rec.Guid)) providedIds.Add(rec.Guid);
        var canPause = new HashSet<string>(suspendable ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        var owned = new List<string>();
        var restart = new List<string>();
        var providedBy = new Dictionary<string, Handshake.ModRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in provided) if (!string.IsNullOrEmpty(rec.Guid)) providedBy[rec.Guid] = rec;
        bool otherLevel = false;
        // Mod compatibility C3 (decision 4): under Exact or Same minor for all, a both-sides optional mod at another
        // version is paused for this server like a ServerMustHave one. These are its failing rows and their owners.
        var optionalRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var optionalOwners = new List<string>();
        foreach (var id in failed)
        {
            if (pauseOptional && providedIds.Contains(id) && askedIds.Contains(id) && IsOptionalRow(providedBy[id]))
            {
                string optOwner = OwnerOf(id, owners, canPause);
                if (optOwner.Length == 0 || !canPause.Contains(optOwner)) { restart.Add(id); continue; }
                optionalRows.Add(id);
                if (!optionalOwners.Contains(optOwner, StringComparer.OrdinalIgnoreCase)) optionalOwners.Add(optOwner);
                if (!owned.Contains(optOwner, StringComparer.OrdinalIgnoreCase)) owned.Add(optOwner);
                continue;
            }

            // Only this client's own rows that the server did not ask about can be withdrawn.
            if (!providedIds.Contains(id) || askedIds.Contains(id)) continue;
            // Keyed on the declared level: only a row that says it cannot work without its server
            // half (ServerMustHave) is ever paused. A NoNeedForSync or ClientOnly row that fails
            // (a version rule) is left to today's refusal.
            // Review of 5a6e40f8, finding 4: a row a [Counterpart(Limited/Optional)] lowered to
            // NoNeedForSync still pauses on a version mismatch, as its ServerMustHave row did.
            if (!IsServerMustHave(providedBy[id]) && !LoweredByCounterpart(RowOwner(id, owners))) { otherLevel = true; continue; }
            string owner = OwnerOf(id, owners, canPause);
            if (owner.Length == 0 || !canPause.Contains(owner)) { restart.Add(id); continue; }
            if (!owned.Contains(owner, StringComparer.OrdinalIgnoreCase)) owned.Add(owner);
        }

        if (truncated) return new ServerModPausePlan(Array.Empty<string>(), Array.Empty<string>(), restart, WhyTruncated);
        if (restart.Count > 0) return new ServerModPausePlan(Array.Empty<string>(), Array.Empty<string>(), restart, WhyRestartOnly);
        if (otherLevel) return new ServerModPausePlan(Array.Empty<string>(), Array.Empty<string>(), restart, WhyNotServerMustHave);
        if (owned.Count == 0) return new ServerModPausePlan(Array.Empty<string>(), Array.Empty<string>(), restart, WhyNotClean);
        if (!autoPause) return new ServerModPausePlan(Array.Empty<string>(), Array.Empty<string>(), restart, WhyAutoPauseOff);

        var (set, refusal) = closure(owned);
        if (set == null || set.Count == 0)
            return new ServerModPausePlan(Array.Empty<string>(), Array.Empty<string>(), restart,
                string.IsNullOrEmpty(refusal) ? WhyNotClean : refusal);
        var taking = new HashSet<string>(set, StringComparer.OrdinalIgnoreCase);
        var optional = new HashSet<string>(optionalOwners, StringComparer.OrdinalIgnoreCase);
        // The optional mods paused for strict matching declare that they work alone by definition; the check still
        // protects every other plugin the closure would take.
        foreach (var plugin in taking)
            if (!optional.Contains(plugin) && DeclaresItWorksAlone(plugin, provided, owners))
                return new ServerModPausePlan(Array.Empty<string>(), Array.Empty<string>(), restart, WhyProtected);

        // A plugin the server asks about (by its own GUID or by any row it provides) is never paused, except the
        // optional mods paused for strict matching (the server accepts a client without them; checked below).
        var remaining = new List<Handshake.ModRecord>(provided.Count);
        foreach (var rec in provided)
        {
            string owner = OwnerOf(rec.Guid, owners, canPause);
            bool goes = taking.Contains(rec.Guid) || (owner.Length > 0 && taking.Contains(owner));
            bool optionalGoes = optional.Contains(rec.Guid) || (owner.Length > 0 && optional.Contains(owner));
            if (goes && askedIds.Contains(rec.Guid) && !optionalGoes)
                return new ServerModPausePlan(Array.Empty<string>(), Array.Empty<string>(), restart, WhyAsked);
            if (!goes) remaining.Add(rec);
        }
        foreach (var plugin in taking)
            if (askedIds.Contains(plugin) && !optional.Contains(plugin))
                return new ServerModPausePlan(Array.Empty<string>(), Array.Empty<string>(), restart, WhyAsked);

        if (!PredictAccepted(asked, remaining, out _))
            return new ServerModPausePlan(Array.Empty<string>(), Array.Empty<string>(), restart, WhyNotClean);
        var strict = new List<string>();
        foreach (var plugin in set)
            if (optional.Contains(plugin)) strict.Add(plugin);
        return new ServerModPausePlan(Array.Empty<string>(), new List<string>(set), restart, "") { StrictMatch = strict };
    }

    /// <summary>
    /// C3: a present row this client declared <see cref="NetworkCompatibilityLevel.NoNeedForSync"/> (a both-sides
    /// optional mod): the one kind Exact or Same minor for all pauses besides ServerMustHave.
    /// </summary>
    internal static bool IsOptionalRow(Handshake.ModRecord rec) =>
        rec.Declared && rec.Level == NetworkCompatibilityLevel.NoNeedForSync && Handshake.Compare.IsPresentRecord(rec);

    /// <summary>
    /// C3: the paused plugins that must stay paused although the server asks about them: under strict matching
    /// (<paramref name="pauseOptional"/>), a plugin the server asks about by its own GUID, whose kept rows declare no
    /// ServerMustHave row, and whose version the server's (strict) row refuses. Resuming it would only pause it again.
    /// </summary>
    internal static List<string> KeepOptionalPaused(IReadOnlyList<Handshake.ModRecord>? asked, IReadOnlyList<PausedPlugin>? suspended,
        bool pauseOptional)
    {
        var keep = new List<string>();
        if (!pauseOptional || asked == null || suspended == null) return keep;
        foreach (var p in suspended)
        {
            if (p.HardRows.Count > 0 || p.Version.Length == 0) continue;
            foreach (var row in asked)
            {
                if (!string.Equals(row.Guid, p.Guid, StringComparison.OrdinalIgnoreCase)) continue;
                if (row.Level == NetworkCompatibilityLevel.NoNeedForSync && !Handshake.Compare.VersionsMatch(row.Version, p.Version, row.VersionRule))
                    keep.Add(p.Guid);
                break;
            }
        }

        return keep;
    }

    /// <summary>
    /// The combined plan for one request: resume what the server asks about, and pause (computed
    /// on the current rows) what would fail. The coordinator runs the pause half again after a
    /// resume has put the resumed plugins' rows back.
    /// </summary>
    internal static ServerModPausePlan Plan(
        IReadOnlyList<Handshake.ModRecord> asked,
        bool truncated,
        IReadOnlyList<Handshake.ModRecord> provided,
        IReadOnlyDictionary<string, string>? owners,
        IReadOnlyCollection<string>? suspendable,
        IReadOnlyList<string>? suspendedNow,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? rowsByPlugin,
        Func<IReadOnlyList<string>, (IReadOnlyList<string> Closure, string Refusal)> closure,
        bool autoPause,
        bool pauseOptional = false)
    {
        var resume = ResumeFor(asked, suspendedNow, rowsByPlugin);
        var pause = PlanPause(asked, truncated, provided, owners, suspendable, closure, autoPause, pauseOptional);
        return new ServerModPausePlan(resume, pause.Pause, pause.RestartNeeded, pause.Why) { StrictMatch = pause.StrictMatch };
    }

    /// <summary>
    /// The pause for a server that runs no Progenitor at all (no handshake request within the
    /// grace period): every pausable plugin that declared one of its rows
    /// <see cref="NetworkCompatibilityLevel.ServerMustHave"/> (it has no server half there), plus
    /// what Bootstrap's closure adds. Declared ServerMustHave rows whose owner cannot be paused are
    /// returned in <see cref="ServerModPausePlan.RestartNeeded"/> and stay loaded.
    /// </summary>
    internal static ServerModPausePlan PlanNoProgenitor(
        IReadOnlyList<Handshake.ModRecord>? provided,
        IReadOnlyDictionary<string, string>? owners,
        IReadOnlyCollection<string>? suspendable,
        Func<IReadOnlyList<string>, (IReadOnlyList<string> Closure, string Refusal)> closure,
        bool autoPause,
        IReadOnlyList<Handshake.ModRecord>? asked = null)
    {
        var canPause = new HashSet<string>(suspendable ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var askedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in asked ?? Array.Empty<Handshake.ModRecord>()) if (!string.IsNullOrEmpty(rec.Guid)) askedIds.Add(rec.Guid);
        var owned = new List<string>();
        var restart = new List<string>();
        foreach (var rec in provided ?? Array.Empty<Handshake.ModRecord>())
        {
            if (!IsServerMustHave(rec)) continue;
            // A row the recorded server request asked about is one that server runs: never paused,
            // and neither is the plugin that provides any asked row.
            if (askedIds.Contains(rec.Guid)) continue;
            string owner = OwnerOf(rec.Guid, owners, canPause);
            if (owner.Length > 0 && OwnsAsked(owner, askedIds, provided!, owners)) continue;
            if (owner.Length == 0 || !canPause.Contains(owner))
            {
                if (!restart.Contains(rec.Guid, StringComparer.OrdinalIgnoreCase)) restart.Add(rec.Guid);
                continue;
            }
            if (!owned.Contains(owner, StringComparer.OrdinalIgnoreCase)) owned.Add(owner);
        }
        if (owned.Count == 0)
            return new ServerModPausePlan(Array.Empty<string>(), Array.Empty<string>(), restart, restart.Count > 0 ? WhyRestartOnly : "");
        if (!autoPause) return new ServerModPausePlan(Array.Empty<string>(), Array.Empty<string>(), restart, WhyAutoPauseOff);
        var (set, refusal) = closure(owned);
        if (set == null || set.Count == 0)
            return new ServerModPausePlan(Array.Empty<string>(), Array.Empty<string>(), restart,
                string.IsNullOrEmpty(refusal) ? WhyNotClean : refusal);
        foreach (var plugin in set)
        {
            if (DeclaresItWorksAlone(plugin, provided ?? Array.Empty<Handshake.ModRecord>(), owners))
                return new ServerModPausePlan(Array.Empty<string>(), Array.Empty<string>(), restart, WhyProtected);
            if (askedIds.Contains(plugin)) return new ServerModPausePlan(Array.Empty<string>(), Array.Empty<string>(), restart, WhyAsked);
            foreach (var rec in provided ?? Array.Empty<Handshake.ModRecord>())
                if (askedIds.Contains(rec.Guid) && owners != null && owners.TryGetValue(rec.Guid, out var o)
                    && string.Equals(o, plugin, StringComparison.OrdinalIgnoreCase))
                    return new ServerModPausePlan(Array.Empty<string>(), Array.Empty<string>(), restart, WhyAsked);
        }
        return new ServerModPausePlan(Array.Empty<string>(), new List<string>(set), restart, "");
    }

    /// <summary>
    /// Plugins released on a leave that have not loaded yet and would fail on the server now being
    /// joined: resuming, not asked about by <paramref name="asked"/> (by GUID or any row they
    /// provided), with a ServerMustHave row the request does not ask about. They are kept paused
    /// instead of loading in the middle of this join. <paramref name="asked"/> null or empty with
    /// <paramref name="noProgenitor"/> means a server without Progenitor: every hard row fails.
    /// </summary>
    internal static List<string> KeepPausedWhileResuming(IReadOnlyList<PausedPlugin>? suspended, IReadOnlyList<Handshake.ModRecord>? asked)
    {
        var keep = new List<string>();
        var askedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in asked ?? Array.Empty<Handshake.ModRecord>()) if (!string.IsNullOrEmpty(rec.Guid)) askedIds.Add(rec.Guid);
        foreach (var p in suspended ?? Array.Empty<PausedPlugin>())
        {
            if (!p.Resuming || p.HardRows.Count == 0) continue;
            if (askedIds.Contains(p.Guid)) continue;
            bool askedRow = false;
            foreach (var r in p.Rows) if (askedIds.Contains(r)) { askedRow = true; break; }
            if (askedRow) continue;
            foreach (var r in p.HardRows)
                if (!askedIds.Contains(r)) { keep.Add(p.Guid); break; }
        }
        return keep;
    }

    /// <summary>
    /// Fail-safe gate for the no-Progenitor detection. It may fire only when Bootstrap positively
    /// says this client is not hosting a private game (a private game's server never asks the
    /// hosting player, so silence proves nothing there) and the connect address has a recognized
    /// remote form. An unreadable hosting flag, an empty or unrecognised address, or a loopback
    /// address never fires.
    /// </summary>
    internal static bool MayDetectNoProgenitor(bool? hostsPrivateGame, string? host) =>
        hostsPrivateGame == false && IsRecognisedRemoteHost(host);

    /// <summary>
    /// <c>a.b.c.d:port</c>, <c>[v6]:port</c> or <c>name.domain:port</c> with a port in 1..65535, not
    /// naming this machine. Everything else (P2P session ids, a bare address, an empty read) is
    /// not recognized. These forms come from <c>ModManifest.NormalizeConnectHost</c>; which forms
    /// real Steam, Lidgren and P2P joins produce still needs live verification.
    /// </summary>
    internal static bool IsRecognisedRemoteHost(string? host)
    {
        string h = (host ?? "").Trim();
        if (h.Length == 0 || IsLocalHost(h)) return false;
        string addr, port;
        if (h.StartsWith("[", StringComparison.Ordinal))
        {
            int close = h.IndexOf(']');
            if (close <= 1 || close + 1 >= h.Length || h[close + 1] != ':') return false;
            addr = h.Substring(1, close - 1);
            port = h.Substring(close + 2);
            if (!System.Net.IPAddress.TryParse(addr, out var v6) || v6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
        }
        else
        {
            int colon = h.LastIndexOf(':');
            if (colon <= 0 || h.IndexOf(':') != colon) return false;
            addr = h.Substring(0, colon);
            port = h.Substring(colon + 1);
            bool dotted = true;
            foreach (char c in addr) if (!(char.IsDigit(c) || c == '.')) { dotted = false; break; }
            if (dotted)
            {
                if (!System.Net.IPAddress.TryParse(addr, out var v4) || v4.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                    || addr.Split('.').Length != 4) return false;
            }
            else
            {
                if (addr.IndexOf('.') < 0) return false;
                foreach (char c in addr)
                    if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-')) return false;
            }
        }
        return int.TryParse(port, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p > 0 && p <= 65535;
    }

    private static bool OwnsAsked(string plugin, HashSet<string> askedIds, IReadOnlyList<Handshake.ModRecord> provided, IReadOnlyDictionary<string, string>? owners)
    {
        if (askedIds.Contains(plugin)) return true;
        if (owners == null) return false;
        foreach (var rec in provided)
            if (askedIds.Contains(rec.Guid) && owners.TryGetValue(rec.Guid, out var o) && string.Equals(o, plugin, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// A plugin whose <c>[Counterpart]</c> lowered its client row (it runs without its server part).
    /// Tests replace it; production asks <see cref="ModRegistry"/>.
    /// </summary>
    internal static Func<string, bool>? LoweredProbe { get; set; }

    /// <summary>
    /// The plugin that provides handshake row <paramref name="rowId"/>: the recorded owner, else the
    /// row itself (a plugin's own GUID row). Re-review of 9a27bf01, finding 5: a row id is never read
    /// as a plugin id without this.
    /// </summary>
    internal static string RowOwner(string rowId, IReadOnlyDictionary<string, string>? owners) =>
        owners != null && owners.TryGetValue(rowId ?? "", out var o) && !string.IsNullOrEmpty(o) ? o : rowId ?? "";

    internal static bool LoweredByCounterpart(string? plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin)) return false;
        try
        {
            if (LoweredProbe != null) return LoweredProbe(plugin!);
            return CounterpartPolicy.RunsWithout(ModRegistry.CounterpartOf(plugin)?.Need);
        }
        catch { return false; }
    }

    /// <summary>A present row declared <see cref="NetworkCompatibilityLevel.ServerMustHave"/>: the only level this feature pauses for.</summary>
    internal static bool IsServerMustHave(Handshake.ModRecord rec) =>
        rec.Declared && rec.Level == NetworkCompatibilityLevel.ServerMustHave && Handshake.Compare.IsPresentRecord(rec);

    /// <summary>
    /// True when <paramref name="plugin"/> declares that it works without its server half
    /// (<see cref="NetworkCompatibilityLevel.NoNeedForSync"/> or
    /// <see cref="NetworkCompatibilityLevel.ClientOnly"/>). Such a plugin is never paused, not even
    /// as a DLL sibling or dependent of one that is, so a closure that would take it pauses
    /// nothing. Its own-GUID row decides when it has one; otherwise any declared row it owns at
    /// one of those levels, with no ServerMustHave row beside it. A plugin that declares nothing
    /// (a library) may be taken with what it belongs to.
    /// </summary>
    internal static bool DeclaresItWorksAlone(
        string plugin, IReadOnlyList<Handshake.ModRecord> provided, IReadOnlyDictionary<string, string>? owners)
    {
        // Review of 5a6e40f8, finding 4: a counterpart declaration keeps that plugin from being paused
        // for a missing server part (its row is NoNeedForSync), but it does not make the DLL it shares
        // (or what it depends on) unpausable: it may be taken with them.
        if (LoweredByCounterpart(plugin)) return false;
        bool anySoft = false, anyHard = false;
        foreach (var rec in provided)
        {
            if (!rec.Declared) continue;
            bool mine = string.Equals(rec.Guid, plugin, StringComparison.OrdinalIgnoreCase);
            if (mine) return rec.Level != NetworkCompatibilityLevel.ServerMustHave;
            if (owners == null || !owners.TryGetValue(rec.Guid, out var o) || !string.Equals(o, plugin, StringComparison.OrdinalIgnoreCase)) continue;
            if (rec.Level == NetworkCompatibilityLevel.ServerMustHave) anyHard = true;
            else anySoft = true;
        }
        return anySoft && !anyHard;
    }

    /// <summary>
    /// A connect address that names this machine (a private game's hosted child, or a local test
    /// server). The no-Progenitor detection never fires for it: a private game's child server is
    /// started by this client's Progenitor with Bootstrap staged into it.
    /// </summary>
    internal static bool IsLocalHost(string? host)
    {
        string h = (host ?? "").Trim();
        if (h.Length == 0) return false;
        int scheme = h.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) h = h.Substring(scheme + 3);
        if (h.StartsWith("[", StringComparison.Ordinal))
        {
            int close = h.IndexOf(']');
            h = close > 0 ? h.Substring(1, close - 1) : h.Trim('[', ']');
        }
        else
        {
            int colon = h.LastIndexOf(':');
            if (colon > 0 && h.IndexOf(':') == colon) h = h.Substring(0, colon);
        }
        return string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(h, "::1", StringComparison.Ordinal)
            || h.StartsWith("127.", StringComparison.Ordinal);
    }

    /// <summary>The plugin that provides row <paramref name="rowGuid"/>: the recorded owner, else the row itself when it is a pausable plugin's own GUID.</summary>
    internal static string OwnerOf(string rowGuid, IReadOnlyDictionary<string, string>? owners, ICollection<string> pausable)
    {
        if (string.IsNullOrEmpty(rowGuid)) return "";
        if (owners != null && owners.TryGetValue(rowGuid, out var owner) && !string.IsNullOrEmpty(owner)) return owner;
        return pausable.Contains(rowGuid) ? rowGuid : "";
    }

    private static bool Contains(this List<string> list, string value, StringComparer comparer)
    {
        foreach (var s in list) if (comparer.Equals(s, value)) return true;
        return false;
    }
}

/// <summary>
/// What the per-server pause's notice draws (a game-styled popup, <see cref="ServerModPauseNotice"/>): the title, the
/// body, one line per paused mod (its readable name and why), and a note. <see cref="Text"/> is the same as one plain
/// sentence, for the log and <see cref="ServerModPause.NoticeRequested"/>.
/// </summary>
internal sealed class PauseNoticeContent
{
    public string Title { get; init; } = ServerModPauseText.NoticeTitle;
    public string Body { get; init; } = "";
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
    public string Note { get; init; } = "";

    /// <summary>The paused mods with their reasons (empty for a notice that paused nothing).</summary>
    public IReadOnlyList<(string Name, string Reason)> Mods { get; init; } = Array.Empty<(string, string)>();

    public string Text { get; init; } = "";

    /// <summary>NOTICEOPEN1: the open button: <c>install</c> (the Install Mods tab), <c>mods</c> (the Mods list) or empty for none.</summary>
    public string OpenKind { get; init; } = "";

    /// <summary>NOTICEOPEN1: the plugin the open button shows.</summary>
    public string OpenKey { get; init; } = "";

    /// <summary>NOTICEOPEN1: its name (the Install tab's search when no listing is known).</summary>
    public string OpenName { get; init; } = "";

    /// <summary>NOTICEOPEN1: the button's caption (<c>Open Install Mods</c>, <c>Open Mods</c>), or empty.</summary>
    public string OpenCaption { get; init; } = "";
}

/// <summary>Every sentence per-server mod pause shows or logs. Game-free so the tests read them.</summary>
internal static class ServerModPauseText
{
    /// <summary>Why a mod is paused: the server lacks its server half (the existing per-server pause).</summary>
    internal const string ReasonServerLacks = "This server doesn't run it";

    /// <summary>CLIENTPOLICY2: the server's client mod policy (Only approved) does not allow it.</summary>
    internal const string ReasonNotAllowed = "Not allowed on this server";

    /// <summary>A server without Progenitor: the mod needs its server half, which only a Progenitor server can run.</summary>
    internal const string ReasonNoProgenitor = "This server doesn't run Progenitor";

    /// <summary>C3: a both-sides optional mod at another version than the server's, under Exact version for all.</summary>
    internal const string ReasonExactMatch = "Another version than this server's (exact match required)";

    /// <summary>C3: the same, under Same minor for all.</summary>
    internal const string ReasonMinorMatch = "Another version than this server's (same minor required)";

    /// <summary>C3: the notice reason for an optional mod paused under <paramref name="mode"/>; empty under declared ranges.</summary>
    internal static string ReasonStrictMatch(VersionMatchingMode mode) => mode switch
    {
        VersionMatchingMode.Exact => ReasonExactMatch,
        VersionMatchingMode.Minor => ReasonMinorMatch,
        _ => "",
    };

    /// <summary>The list shows at most this many mods, then <c>and N more</c>.</summary>
    internal const int MaxListed = 8;

    /// <summary><c>1 mod</c>, <c>3 mods</c>.</summary>
    internal static string Mods(int n) => n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " mod" : " mods");

    /// <summary>
    /// The pause notice (requested 2026-10-05: one line per mod with its name and why, correct plurals): title
    /// <c>Mods paused for this server</c>; body <c>Paused 2 mods for this server. They're off while you're on Night
    /// Garden and come back when you leave:</c>; lines <c>RotorCamera · Not allowed on this server</c>, at most
    /// <see cref="MaxListed"/> then <c>and N more</c>; the note names restart-only mods still running.
    /// </summary>
    internal static PauseNoticeContent Paused(IReadOnlyList<(string Name, string Reason)> mods, string? serverName,
        IReadOnlyList<string>? restartOnly = null)
    {
        var list = new List<(string Name, string Reason)>();
        foreach (var m in mods ?? Array.Empty<(string, string)>())
            if (!string.IsNullOrWhiteSpace(m.Name)) list.Add((m.Name.Trim(), m.Reason ?? ""));
        int n = list.Count;
        string server = string.IsNullOrWhiteSpace(serverName) ? "this server" : serverName!.Trim();
        string body = "Paused " + Mods(n) + " for this server. "
            + (n == 1 ? "It's off while you're on " + server + " and comes back when you leave:"
                : "They're off while you're on " + server + " and come back when you leave:");
        var lines = new List<string>(Math.Min(n, MaxListed) + 1);
        for (int i = 0; i < n && i < MaxListed; i++)
            lines.Add(list[i].Reason.Length > 0 ? list[i].Name + " · " + list[i].Reason : list[i].Name);
        if (n > MaxListed) lines.Add("and " + (n - MaxListed).ToString(CultureInfo.InvariantCulture) + " more");
        string note = restartOnly != null && restartOnly.Count > 0 ? RestartOnlyStillRunning(restartOnly) : "";
        var parts = new List<string>(n);
        foreach (var m in list) parts.Add(m.Reason.Length > 0 ? m.Name + " (" + m.Reason + ")" : m.Name);
        string text = "Paused " + Mods(n) + " for this server: " + string.Join(", ", parts) + ". They come back when you leave."
            + (note.Length > 0 ? " " + note : "");
        return new PauseNoticeContent { Body = body, Lines = lines, Note = note, Mods = list, Text = text };
    }

    /// <summary>A notice that paused nothing (the restart-only case, an older Bootstrap): one paragraph.</summary>
    internal static PauseNoticeContent Plain(string title, string text) =>
        new() { Title = title ?? NoticeTitle, Body = text ?? "", Text = text ?? "" };

    internal const string NoticeTitle = "Mods paused for this server";
    internal const string CloseButton = "OK";
    internal const string UnsupportedTitle = "Mods not paused";

    /// <summary>How long the notice stays up if the player does not close it.</summary>
    internal const double NoticeSeconds = 20;

    internal const string NoBootstrap =
        "Progenitor.Bootstrap is not loaded, so hot-loaded plugins are managed by Core's own reloader, which cannot pause them";

    internal static string OldBootstrap(string member) =>
        "this Progenitor.Bootstrap is older than per-server mod pause (" + member + " is missing)";

    /// <summary><c>Paused 2 mod(s) this server doesn't use: A, B. They come back when you leave.</c></summary>
    internal static string Notice(IReadOnlyList<string> names)
    {
        int n = names?.Count ?? 0;
        return "Paused " + n.ToString(CultureInfo.InvariantCulture) + " mod(s) this server doesn't use: "
            + Join(names) + ". They come back when you leave.";
    }

    /// <summary><c>This server doesn't run Progenitor. Paused 2 mod(s) that need it: A, B. They come back when you leave.</c></summary>
    internal static string NoProgenitorNotice(IReadOnlyList<string> names, IReadOnlyList<string>? restartOnly)
    {
        int n = names?.Count ?? 0;
        string text = "This server doesn't run Progenitor. Paused " + n.ToString(CultureInfo.InvariantCulture)
            + " mod(s) that need it: " + Join(names) + ". They come back when you leave.";
        if (restartOnly != null && restartOnly.Count > 0) text += " " + RestartOnlyStillRunning(restartOnly);
        return text;
    }

    /// <summary>The notice when nothing could be paused but restart-only mods need the server too.</summary>
    internal static string NoProgenitorRestartOnlyNotice(IReadOnlyList<string> restartOnly) =>
        "This server doesn't run Progenitor. " + RestartOnlyStillRunning(restartOnly);

    internal static string RestartOnlyStillRunning(IReadOnlyList<string> restartOnly) =>
        "Still running because they were loaded at game start: " + Join(restartOnly)
        + "; restart V Rising without them to turn them off.";

    internal static string LogNoProgenitor(double grace, string host) =>
        "ServerModPause: no Progenitor handshake request within " + grace.ToString("0.#", CultureInfo.InvariantCulture)
        + " s of joining " + (string.IsNullOrEmpty(host) ? "this server" : host) + "; treating it as a server without Progenitor.";

    internal static string LogNoRequestNotActed(string host, bool? hosting) =>
        "ServerModPause: no handshake request after the grace period, but not pausing: "
        + (hosting == true ? "this client is hosting a private game (its server never asks the host)"
            : hosting == null ? "whether this client hosts the server could not be read"
            : "the connect address '" + host + "' is empty, local or of a form not recognized")
        + ".";

    internal const string LogCarriedDiscarded =
        "ServerModPause: discarded the handshake request a previous Core generation deferred: the client is no longer on that connection.";

    internal const string LogAbandoned =
        "ServerModPause: the connection ended before the deferred handshake reply; nothing paused, nothing sent.";

    internal static string LogResumePending(string id) =>
        "ServerModPause: " + id + " is released but not loaded yet (its previous plugin context has not been collected); it is reported when it loads.";

    internal static string LogResumeFailed(string id) =>
        "ServerModPause: " + id + " left the pause but did not load (see the Reload lines above).";

    /// <summary>The notice when a pause would have helped but this Bootstrap cannot do it.</summary>
    internal static string UnsupportedNotice(string reason) =>
        "Some of your mods need this server to run them and it does not, but they could not be paused: "
        + reason + ". Update Progenitor and restart the game to have them paused automatically.";

    internal static string LogPaused(IReadOnlyList<string> ids, string host) =>
        "ServerModPause: paused " + Join(ids) + " for " + (string.IsNullOrEmpty(host) ? "this server" : host) + ".";

    internal static string LogResumed(IReadOnlyList<string> ids, string reason) =>
        "ServerModPause: resumed " + Join(ids) + " (" + reason + ").";

    internal static string LogNotPaused(string why, IReadOnlyList<string> restart) =>
        "ServerModPause: the join is predicted to fail and nothing was paused: " + why
        + (restart != null && restart.Count > 0 ? " (restart without: " + Join(restart) + ")" : "") + ".";

    internal static string LogUnsupported(string reason) =>
        "ServerModPause: cannot pause mods: " + reason + ". Update Progenitor (Bootstrap needs a game restart).";

    internal static string LogRowsLate(IReadOnlyList<string> ids, double seconds) =>
        "ServerModPause: resumed " + Join(ids) + " but their handshake rows were not back after "
        + seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s; replying without waiting longer.";

    internal static string Join(IReadOnlyList<string>? items)
    {
        if (items == null || items.Count == 0) return "(none)";
        var sb = new StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(items[i]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Per-server mod pause, client side. When a Progenitor server's handshake request arrives, this
/// decides whether any of this client's own hot-loaded plugins would fail the join only because
/// the server does not run them; if so they are paused (unloaded with
/// <c>PluginUnloadReason.Suspend</c>) before the reply goes out, and a small notice says so. They
/// come back on a real disconnect, or as soon as a later request shows the server runs them.
/// Plugins loaded at game start (BepInEx/plugins, <c>ctx.UnsafeHarmony</c>) are never touched.
/// The paused set lives in Bootstrap, so a Core swap keeps it.
/// </summary>
public static class ServerModPause
{
    /// <summary>Resume reason carried by <see cref="ModsResumed"/> after a disconnect.</summary>
    public const string ReasonDisconnect = "disconnect";

    /// <summary>Resume reason carried by <see cref="ModsResumed"/> when the server asked about the plugin.</summary>
    public const string ReasonServerRunsIt = "server-runs-it";

    /// <summary>How long a reply waits for resumed plugins' rows to come back (well inside the server's 20 s answer deadline).</summary>
    internal const double ResumeRowsWaitSeconds = 8;

    internal const double ResumeRowsPollSeconds = 0.1;

    /// <summary>How often <see cref="Tick"/> looks for resuming plugins that have loaded since.</summary>
    internal const double AwaitPollSeconds = 0.5;

    private static readonly object Gate = new();
    private static readonly object Owner = new();
    private static IServerModPauseBackend? _backend;
    private static string _host = "";
    private static bool _warnedUnsupported;

    /// <summary>
    /// Advanced by every real connection end. A deferred reply captures it with its request and is
    /// abandoned (no pause, no reply, no profile write) if it changed before the reply went out.
    /// </summary>
    private static int _connectionGen;
    private static long _requestSeq;

    /// <summary>
    /// Plugins released from the pause that Bootstrap had not loaded yet (their previous plugin ALC
    /// had not collected). <see cref="Tick"/> reports each one when it loads. Kept in Bootstrap
    /// (<see cref="IServerModPauseBackend.ResumeReports"/>) so a Core swap in between loses neither
    /// the <see cref="ModsResumed"/> nor the re-answer.
    /// </summary>
    private static double _nextAwaitPoll;

    internal sealed class Awaited
    {
        internal string Reason = "";
        internal bool ReAnswer;
    }

    internal static Dictionary<string, Awaited> ParseAwaiting(string? text)
    {
        var map = new Dictionary<string, Awaited>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (text ?? "").Split('\n'))
        {
            var f = line.Split('\t');
            if (f.Length < 3 || f[0].Length == 0) continue;
            map[f[0]] = new Awaited { Reason = f[1], ReAnswer = f[2] == "1" };
        }
        return map;
    }

    internal static string SerializeAwaiting(Dictionary<string, Awaited> map)
    {
        var sb = new StringBuilder();
        foreach (var kv in map)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(kv.Key).Append('\t').Append(kv.Value.Reason).Append('\t').Append(kv.Value.ReAnswer ? '1' : '0');
        }
        return sb.ToString();
    }

    private static void EditAwaiting(IServerModPauseBackend backend, Action<Dictionary<string, Awaited>> change)
    {
        lock (Gate)
        {
            var map = ParseAwaiting(backend.ResumeReports);
            change(map);
            backend.ResumeReports = SerializeAwaiting(map);
        }
    }

    /// <summary>One deferred reply: the request, its connection, and how to send it.</summary>
    private sealed class PendingReply
    {
        internal List<Handshake.ModRecord> Asked = new();
        internal bool Truncated;
        internal Func<bool> Send = () => false;
        internal IServerModPauseBackend Backend = null!;
        internal int ConnectionGen;
        internal long Seq;
        /// <summary>Asked plugins that were still resuming when the reply started waiting.</summary>
        internal List<string> StillResuming = new();

        /// <summary>C3: the request announced Exact or Same minor for all: optional mods at another version are paused too.</summary>
        internal bool PauseOptional;

        /// <summary>C3: the Version matching the request announced, for the notice's reason.</summary>
        internal VersionMatchingMode Mode;
    }

    /// <summary>
    /// C3: the Version matching of the server this request came from, read from its request bits (noted by the handshake
    /// before the reply path runs). Declared ranges when unreadable, and for a request carried across a Core swap.
    /// </summary>
    internal static VersionMatchingMode RequestMode()
    {
        try { return VersionMatchJudge.ModeOf(ModCompatCaps.ServerCaps); }
        catch { return VersionMatchingMode.Range; }
    }

    /// <summary>C3: <see cref="ResumeFor"/> without the optional mods strict matching keeps paused.</summary>
    private static List<string> ResumeForRequest(IReadOnlyList<Handshake.ModRecord> asked, IReadOnlyList<PausedPlugin> suspended, bool pauseOptional)
    {
        var resume = ServerModPausePolicy.ResumeFor(asked, IdsOf(suspended, skipHeld: true), RowsOf(suspended));
        var keep = ServerModPausePolicy.KeepOptionalPaused(asked, suspended, pauseOptional);
        if (keep.Count > 0) resume.RemoveAll(id => Contains(keep, id));
        return resume;
    }

    /// <summary>Raised with the notice as one sentence (title, text): the log's form, and what the tests read.</summary>
    internal static event Action<string, string>? NoticeRequested;

    /// <summary>Raised to show the notice (the game-styled popup's parts). The notice panel subscribes; nothing else may.</summary>
    internal static event Action<PauseNoticeContent>? PauseNoticeRequested;

    private static void RaiseNotice(PauseNoticeContent content)
    {
        try { PauseNoticeRequested?.Invoke(content); }
        catch (Exception e) { Log(warn: true, "ServerModPause: notice threw: " + e.Message); }
        try { NoticeRequested?.Invoke(content.Title, content.Text); }
        catch (Exception e) { Log(warn: true, "ServerModPause: notice threw: " + e.Message); }
    }

    /// <summary>The joined server's name as its profile remembers it (from its signed catalog), or empty.</summary>
    private static string ServerNameOf(string host)
    {
        try { return ServerProfiles.Get(host).ServerName ?? ""; }
        catch { return ""; }
    }

    /// <summary>Raised when the notice should go away (the client left).</summary>
    internal static event Action? NoticeDismissed;

    /// <summary>
    /// Default for <c>[Networking] NoProgenitorGraceSeconds</c>. A Progenitor server asks from its
    /// <c>PlayerConnected</c> handler, which runs in <c>ServerBootstrapSystem.OnUserConnected</c>
    /// (Events/EventHooks.cs); client logs show that request answered in the same join as the
    /// prefab definition packet that postfix sends. The watch only starts once the client reaches
    /// <c>ClientState.Running</c>, so 10 s after Running is a wide margin (inferred, not timed).
    /// </summary>
    internal const int DefaultNoProgenitorGraceSeconds = 10;

    /// <summary>Reads the bound config value; null in tests and before the config binds.</summary>
    internal static Func<int>? GraceReader;

    private static readonly NoProgenitorWatch Watch = new();
    private static bool _serverWithoutProgenitor;

    /// <summary>The grace period in seconds; 0 or less turns the no-Progenitor detection off.</summary>
    internal static double GraceSeconds
    {
        get
        {
            try { return GraceReader?.Invoke() ?? DefaultNoProgenitorGraceSeconds; }
            catch { return DefaultNoProgenitorGraceSeconds; }
        }
    }

    /// <summary>
    /// Test seams, one per async flow so parallel test classes that also reach this type (every
    /// <c>Handshake.NotifyClientConnectionEnded</c> caller) see production behavior: no pause-capable
    /// backend, so nothing happens. Null in production.
    /// </summary>
#pragma warning disable CS0649 // assigned by the test project only
    internal sealed class TestSeams
    {
        internal IServerModPauseBackend? Backend;
        internal Action<Action>? Post;
        internal Action<double, Action>? PostDelayed;
        internal Func<string>? Host;
        internal Func<double>? Clock;
        internal Func<Handshake.ModRecord[]>? Provided;
        internal Func<Dictionary<string, string>>? Owners;
        internal Func<Handshake.ModRecord[]>? Asked;
        internal Func<bool>? ReAnswer;
        internal NoProgenitorWatch? Watch;

        // CLIENTPOLICY2: this client's mods, whether the server asked for the report, and the remembered policy text.
        internal Func<IReadOnlyList<ClientModEntry>>? ClientMods;
        internal Func<bool>? ServerAsksReport;
        internal Func<string, string>? RememberedPolicy;
    }
#pragma warning restore CS0649

    private static readonly System.Threading.AsyncLocal<TestSeams?> Seams = new();

    /// <summary>Tests only: install seams for the calling flow (null clears them).</summary>
    internal static TestSeams? Test
    {
        get => Seams.Value;
        set => Seams.Value = value;
    }

    private static NoProgenitorWatch W => Seams.Value?.Watch ?? Watch;

    private static Handshake.ModRecord[] ProvidedRows() =>
        Seams.Value?.Provided?.Invoke() ?? Handshake.CopyProvided();

    private static Dictionary<string, string> ProvidedOwners() =>
        Seams.Value?.Owners?.Invoke() ?? Handshake.CopyProvidedOwners();

    private static Handshake.ModRecord[] RecordedRequest() =>
        Seams.Value?.Asked?.Invoke() ?? Handshake.CopyServerAsked();

    /// <summary>What is paused right now (paused or released and waiting to load), and for which host. Never null.</summary>
    public static ServerModPauseState Current
    {
        get
        {
            try
            {
                var backend = Backend();
                if (!backend.Supported) return ServerModPauseState.Empty;
                var paused = backend.Suspended();
                if (paused.Count == 0) return ServerModPauseState.Empty;
                var ids = new List<string>(paused.Count);
                var names = new List<string>(paused.Count);
                var resuming = new List<string>();
                foreach (var p in paused)
                {
                    // SETTINGS1 stage 3: a plugin the player turned off is not paused for this server.
                    if (Held(p.Guid)) continue;
                    if (p.Resuming) { resuming.Add(p.Guid); continue; }
                    ids.Add(p.Guid);
                    names.Add(p.Name);
                }
                if (ids.Count == 0 && resuming.Count == 0) return ServerModPauseState.Empty;
                string host;
                bool noProgenitor;
                lock (Gate) { host = _host; noProgenitor = _serverWithoutProgenitor; }
                return new ServerModPauseState(ids, names, host, noProgenitor, resuming);
            }
            catch
            {
                return ServerModPauseState.Empty;
            }
        }
    }

    /// <summary>True when <paramref name="pluginId"/> is paused for the current server.</summary>
    public static bool IsPaused(string? pluginId)
    {
        if (string.IsNullOrEmpty(pluginId)) return false;
        foreach (var id in Current.PausedIds)
            if (string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The deny panel's question for a <c>YouRequire</c> row: is the plugin behind this row one
    /// that only a restart can remove (loaded at game start or <c>ctx.UnsafeHarmony</c>)? False when
    /// the answer is not known (no owner recorded, or no pause-capable Bootstrap).
    /// </summary>
    internal static bool IsRestartOnly(string? rowId)
    {
        if (string.IsNullOrEmpty(rowId)) return false;
        try
        {
            var backend = Backend();
            if (!backend.Supported) return false;
            var owners = ProvidedOwners();
            var pausable = new HashSet<string>(backend.Suspendable(), StringComparer.OrdinalIgnoreCase);
            string owner = ServerModPausePolicy.OwnerOf(rowId!, owners, pausable);
            if (owner.Length > 0 && pausable.Contains(owner)) return false;
            // An owner we know about that the reloader does not hold: loaded at game start.
            if (owner.Length > 0) return true;
            // No owner recorded: a row provided outside a plugin context (plain BepInEx plugin).
            return Array.Exists(ProvidedRows(), r => string.Equals(r.Guid, rowId, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// <c>Handshake.OnRequest</c>'s reply path. Sends at once when there is nothing to resume and the
    /// join is predicted to succeed (today's behavior, unchanged); otherwise reconciles one frame
    /// later on the main thread — resume, wait for rows, pause — and then sends. The deferred
    /// request is also parked in Bootstrap, so a Core swap before the reply cannot lose it.
    /// </summary>
    internal static void OnRequest(List<Handshake.ModRecord> askedRows, bool truncated, Func<bool> sendReply)
    {
        if (sendReply == null) return;
        askedRows ??= new List<Handshake.ModRecord>();
        W.NoteRequest();
        IServerModPauseBackend backend;
        IReadOnlyList<PausedPlugin> suspended;
        try
        {
            backend = Backend();
            suspended = backend.Supported ? backend.Suspended() : Array.Empty<PausedPlugin>();
        }
        catch (Exception e)
        {
            Log(warn: true, "ServerModPause: backend read threw: " + e.Message);
            sendReply();
            return;
        }

        lock (Gate) { _host = ReadHost(); _serverWithoutProgenitor = false; }
        var mode = RequestMode();
        bool pauseOptional = mode != VersionMatchingMode.Range;
        var resume = ResumeForRequest(askedRows, suspended, pauseOptional);
        bool clean = ServerModPausePolicy.PredictAccepted(askedRows, ProvidedRows(), out _);
        // A mod released on a leave that is still loading and would fail here must be kept paused
        // before the reply, which needs the deferred path too.
        bool keep = ServerModPausePolicy.KeepPausedWhileResuming(suspended, askedRows).Count > 0;
        // CLIENTPOLICY2: a server whose remembered policy does not allow a loaded mod that can be paused: the automatic
        // pause runs before the reply (the report follows the reply), so it takes the deferred path too.
        bool policy = false;
        try { policy = backend.Supported && PolicyPlanFor(askedRows, backend, withClosure: false).Pause.Count > 0; }
        catch (Exception e) { Log(warn: true, "ServerModPause: the client mod policy check threw: " + e.Message); }
        if (resume.Count == 0 && clean && !keep && !policy)
        {
            sendReply();
            return;
        }

        if (!backend.Supported)
        {
            ReportUnsupported(backend.UnsupportedReason, askedRows);
            sendReply();
            return;
        }

        var pending = new PendingReply
        {
            Asked = new List<Handshake.ModRecord>(askedRows),
            Truncated = truncated,
            Send = sendReply,
            Backend = backend,
            PauseOptional = pauseOptional,
            Mode = mode,
        };
        lock (Gate)
        {
            pending.ConnectionGen = _connectionGen;
            pending.Seq = ++_requestSeq;
        }
        string carriedHost;
        lock (Gate) carriedHost = _host;
        try { backend.PendingRequest = CarriedRequest.Serialize(pending.Seq, truncated, pending.Asked, carriedHost, backend.ConnectionIdentity()); }
        catch { /* the carry-over only matters across a Core swap */ }
        Post(() => Reconcile(pending));
    }

    /// <summary>
    /// The request a previous Core generation deferred and never answered (its queued work was
    /// dropped with it). Taken once; the caller answers it through <see cref="OnRequest"/>.
    /// </summary>
    internal static bool TakeCarriedRequest(bool connectionLive, out List<Handshake.ModRecord> asked, out bool truncated)
    {
        asked = new List<Handshake.ModRecord>();
        truncated = false;
        try
        {
            var backend = Backend();
            if (!backend.Supported) return false;
            string payload = backend.PendingRequest;
            if (string.IsNullOrEmpty(payload)) return false;
            backend.PendingRequest = "";
            if (!CarriedRequest.TryParse(payload, out _, out truncated, out asked, out string host, out string connection))
                return false;
            if (CarriedRequest.MayAnswer(host, connection, ReadHost(), backend.ConnectionIdentity(), connectionLive)) return true;
            Log(warn: false, ServerModPauseText.LogCarriedDiscarded);
            asked.Clear();
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The client left the server for real (kick, walk to the menu, world ended). Every paused
    /// plugin comes back, any deferred reply is abandoned, and the no-Progenitor watch starts over.
    /// Not called on a new join's epoch change or a Core-only refresh.
    /// </summary>
    internal static void OnConnectionEnded()
    {
        W.Reset();
        lock (Gate)
        {
            _serverWithoutProgenitor = false;
            _connectionGen++;
            PolicyPaused.Clear();
        }
        try { NoticeDismissed?.Invoke(); }
        catch (Exception e) { Log(warn: true, "ServerModPause: notice dismiss threw: " + e.Message); }
        IServerModPauseBackend backend;
        try { backend = Backend(); }
        catch { return; }
        if (!backend.Supported) return;
        try { backend.PendingRequest = ""; }
        catch { /* nothing to carry */ }
        try { EditAwaiting(backend, map => { foreach (var a in map.Values) a.ReAnswer = false; }); }
        catch { /* only costs a stale re-answer attempt, which needs a recorded request anyway */ }
        IReadOnlyList<PausedPlugin> paused;
        try { paused = backend.Suspended(); }
        catch { return; }
        if (IdsOf(paused, skipHeld: true).Count == 0) return;
        Post(() => ResumeAll(backend, ReasonDisconnect));
    }

    /// <summary>
    /// Core going down: drop queued work and the notice. The paused set and any deferred request
    /// stay in Bootstrap; the next generation answers that request after its plugins reload.
    /// </summary>
    internal static void Uninstall()
    {
        try { NoticeDismissed?.Invoke(); }
        catch { /* teardown */ }
#if !PROGENITOR_TESTS
        try { Dev.MainThread.DropOwnedBy(Owner, typeof(ServerModPause).Assembly); }
        catch { /* teardown */ }
#endif
        lock (Gate)
        {
            _backend = null;
            _warnedUnsupported = false;
        }
    }

#if PROGENITOR_TESTS
    internal static void ResetForTests()
    {
        Seams.Value = null;
        GraceReader = null;
        Watch.ResetForTests();
        NoticeRequested = null;
        PauseNoticeRequested = null;
        NoticeDismissed = null;
        lock (Gate)
        {
            _backend = null;
            _host = "";
            _warnedUnsupported = false;
            _serverWithoutProgenitor = false;
            _connectionGen = 0;
            _requestSeq = 0;
            _nextAwaitPoll = 0;
            PolicyPaused.Clear();
        }
    }
#endif

    private static bool Stale(PendingReply p)
    {
        lock (Gate) return p.ConnectionGen != _connectionGen;
    }

    private static void Abandon(PendingReply p)
    {
        Log(warn: false, ServerModPauseText.LogAbandoned);
        ClearCarried(p);
    }

    private static void ClearCarried(PendingReply p)
    {
        try
        {
            string payload = p.Backend.PendingRequest;
            if (CarriedRequest.TryParse(payload, out long seq, out _, out _) && seq == p.Seq)
                p.Backend.PendingRequest = "";
        }
        catch { /* best effort */ }
    }

    private static void Reconcile(PendingReply p)
    {
        if (Stale(p)) { Abandon(p); return; }
        var waitFor = new List<string>();
        try
        {
            var suspended = p.Backend.Suspended();
            var resume = ResumeForRequest(p.Asked, suspended, p.PauseOptional);
            if (resume.Count > 0)
            {
                var loaded = p.Backend.Resume(resume, out string report);
                if (report.Length > 0) Log(warn: false, "ServerModPause: " + report);
                if (loaded.Count > 0) AfterResume(loaded, ReasonServerRunsIt);
                // Wait only for asked plugins that are loaded now; a dependent brought back with
                // them may provide no row at all, and one still waiting on its old ALC has no rows
                // to wait for. That one is reported when it loads, and the server is re-answered.
                // Wait for every asked plugin, loaded now or still resuming: Bootstrap's scan keeps
                // loading the latter while the reply waits (within the deadline), so the reply can
                // carry its rows and the server does not refuse the join before a re-answer.
                var stillResuming = new HashSet<string>(RemainingResuming(p.Backend, loaded), StringComparer.OrdinalIgnoreCase);
                foreach (var id in resume)
                {
                    if (Contains(loaded, id)) { waitFor.Add(id); continue; }
                    if (!stillResuming.Contains(id)) continue; // gone or failed to load
                    waitFor.Add(id);
                    p.StillResuming.Add(id);
                    Await(p.Backend, id, ReasonServerRunsIt, reAnswer: true);
                }
                foreach (var id in stillResuming)
                    if (!Contains(resume, id)) Await(p.Backend, id, ReasonServerRunsIt, reAnswer: false);
            }
        }
        catch (Exception e)
        {
            Log(warn: true, "ServerModPause: resume threw: " + e.Message);
        }

        if (waitFor.Count == 0)
        {
            PauseThenSend(p);
            return;
        }
        WaitForRows(p, waitFor, Clock() + ResumeRowsWaitSeconds);
    }

    private static void WaitForRows(PendingReply p, List<string> resumed, double deadline)
    {
        if (Stale(p)) { Abandon(p); return; }
        if (RowsBack(resumed))
        {
            // The reply now carries their rows: no re-answer is owed for them.
            if (p.StillResuming.Count > 0)
                EditAwaiting(p.Backend, map => { foreach (var id in p.StillResuming) if (map.TryGetValue(id, out var a)) a.ReAnswer = false; });
            PauseThenSend(p);
            return;
        }
        if (Clock() >= deadline)
        {
            Log(warn: true, ServerModPauseText.LogRowsLate(resumed, ResumeRowsWaitSeconds));
            PauseThenSend(p);
            return;
        }
        PostDelayed(ResumeRowsPollSeconds, () => WaitForRows(p, resumed, deadline));
    }

    /// <summary>Every resumed plugin has at least one handshake row again (its own GUID, or any row it owns).</summary>
    internal static bool RowsBack(IReadOnlyList<string> resumed)
    {
        var provided = ProvidedRows();
        var owners = ProvidedOwners();
        foreach (var plugin in resumed)
        {
            bool back = false;
            foreach (var rec in provided)
            {
                if (string.Equals(rec.Guid, plugin, StringComparison.OrdinalIgnoreCase)) { back = true; break; }
                if (owners.TryGetValue(rec.Guid, out var o) && string.Equals(o, plugin, StringComparison.OrdinalIgnoreCase)) { back = true; break; }
            }
            if (!back) return false;
        }
        return true;
    }

    private static void PauseThenSend(PendingReply p)
    {
        if (Stale(p)) { Abandon(p); return; }
        try
        {
            string host;
            lock (Gate) host = _host;
            bool autoPause = ServerProfiles.AutoPause(host);
            var provided = ProvidedRows();
            var owners = ProvidedOwners();
            var backend = p.Backend;
            if (autoPause) KeepPausedIfUnrun(backend, p.Asked, host, noProgenitor: false);
            var plan = ServerModPausePolicy.PlanPause(
                p.Asked, p.Truncated, provided, owners, backend.Suspendable(),
                guids =>
                {
                    var set = backend.Closure(guids, out string refusal);
                    return (set, refusal);
                },
                autoPause,
                p.PauseOptional);

            var paused = new List<(string Name, string Reason)>();
            if (plan.Pause.Count > 0)
            {
                var taken = backend.Suspend(plan.Pause, RowOwners(provided, owners, plan.Pause), out string report);
                string strictReason = ServerModPauseText.ReasonStrictMatch(p.Mode);
                if (taken.Count > 0) AfterPause(taken, host, backend, noProgenitor: false, plan.RestartNeeded, paused,
                    reasonOf: id => Contains(plan.StrictMatch, id) && strictReason.Length > 0 ? strictReason : null);
                else Log(warn: true, "ServerModPause: nothing was paused: " + report);
            }
            else if (plan.Why.Length > 0)
            {
                Log(warn: false, ServerModPauseText.LogNotPaused(plan.Why, plan.RestartNeeded));
            }

            // CLIENTPOLICY2: the automatic pause of the mods this server's policy does not allow, before the reply and
            // so before the report that follows it. One notice for both pauses, one line per mod with its reason.
            PausePolicyMods(p.Asked, host, backend, paused);
            if (paused.Count > 0) RaiseNotice(ServerModPauseText.Paused(paused, ServerNameOf(host)));
        }
        catch (Exception e)
        {
            Log(warn: true, "ServerModPause: pause threw; replying without it: " + e.Message);
        }

        try { p.Send(); }
        catch (Exception e) { Log(warn: true, "ServerModPause: reply threw: " + e.Message); }
        ClearCarried(p);
    }

    /// <summary>
    /// Client frame hook: feeds the no-Progenitor watch whether the client is in
    /// <c>ClientState.Running</c>. When the grace period after entering Running passes with no
    /// handshake request, the server may be treated as one without Progenitor.
    /// </summary>
    internal static void OnClientFrame(bool running)
    {
        double grace = GraceSeconds;
        if (!W.Observe(running, Clock(), grace)) return;
        Post(() => OnNoProgenitorDetected(grace));
    }

    /// <summary>
    /// Client frame hook (every frame, before any world check): reports resuming plugins that have
    /// loaded since, and re-answers the server when one it asked for has.
    /// </summary>
    internal static void Tick()
    {
        double now = Clock();
        lock (Gate)
        {
            if (now < _nextAwaitPoll) return;
            _nextAwaitPoll = now + AwaitPollSeconds;
        }

        IServerModPauseBackend backend;
        IReadOnlyList<PausedPlugin> suspended;
        IReadOnlyList<string> loadedNow;
        try
        {
            backend = Backend();
            if (!backend.Supported) return;
            suspended = backend.Suspended();
            loadedNow = backend.Suspendable();
        }
        catch { return; }

        var loaded = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        bool reAnswer = false;
        var failed = new List<string>();
        var asked = RecordedRequest();
        try
        {
            EditAwaiting(backend, map =>
            {
                // Rediscover resuming plugins nobody recorded (a Core swap before the record).
                foreach (var p in suspended)
                {
                    if (!p.Resuming || map.ContainsKey(p.Guid) || Held(p.Guid)) continue;
                    bool askedFor = ServerModPausePolicy.ResumeFor(asked, new[] { p.Guid },
                        new Dictionary<string, IReadOnlyList<string>> { [p.Guid] = p.Rows }).Count > 0;
                    map[p.Guid] = new Awaited { Reason = askedFor ? ReasonServerRunsIt : ReasonDisconnect, ReAnswer = askedFor };
                }
                foreach (var id in new List<string>(map.Keys))
                {
                    bool stillOut = false;
                    foreach (var p in suspended) if (string.Equals(p.Guid, id, StringComparison.OrdinalIgnoreCase)) { stillOut = true; break; }
                    if (stillOut) continue;
                    var a = map[id];
                    map.Remove(id);
                    if (!Contains(loadedNow, id)) { failed.Add(id); continue; }
                    if (!loaded.TryGetValue(a.Reason, out var list)) loaded[a.Reason] = list = new List<string>();
                    list.Add(id);
                    if (a.ReAnswer) reAnswer = true;
                }
            });
        }
        catch { return; }

        foreach (var id in failed) Log(warn: true, ServerModPauseText.LogResumeFailed(id));
        foreach (var kv in loaded) AfterResume(kv.Value, kv.Key);
        if (reAnswer && asked.Length > 0)
        {
            bool sent = false;
            try { sent = ReAnswer(); }
            catch (Exception e) { Log(warn: true, "ServerModPause: re-answer threw: " + e.Message); }
            Log(warn: false, "ServerModPause: re-answered the handshake after a resumed mod loaded; sent=" + (sent ? "true" : "false"));
        }
    }

    /// <summary>
    /// Plugins released on a leave that are still resuming and would fail on this server stay
    /// paused instead of loading mid-join (NEW 2). They are announced like a fresh pause.
    /// </summary>
    private static void KeepPausedIfUnrun(IServerModPauseBackend backend, IReadOnlyList<Handshake.ModRecord> asked, string host, bool noProgenitor)
    {
        try
        {
            var keep = ServerModPausePolicy.KeepPausedWhileResuming(backend.Suspended(), asked);
            if (keep.Count == 0) return;
            var kept = backend.KeepPaused(keep);
            if (kept.Count == 0) return;
            EditAwaiting(backend, map => { foreach (var id in kept) map.Remove(id); });
            AfterPause(kept, host, backend, noProgenitor, Array.Empty<string>());
        }
        catch (Exception e)
        {
            Log(warn: true, "ServerModPause: keeping resuming mods paused threw: " + e.Message);
        }
    }

    /// <summary>
    /// No handshake request arrived: pause every pausable plugin that declared a ServerMustHave
    /// row. Fail-safe: only when Bootstrap positively says this client is not hosting a private
    /// game and the connect address has a recognized remote form; never for an older Bootstrap;
    /// never when the profile turns auto-pause off or the profiles file cannot be read.
    /// </summary>
    internal static void OnNoProgenitorDetected(double grace)
    {
        if (W.RequestSeen) return; // a slow request landed between the watch and this frame
        // SERVEREMPTY1: a joined server without Progenitor counts as a successful join for the last-server record.
        JoinFlowClient.NoteJoinedWithoutProgenitor();
        string host = ReadHost();
        IServerModPauseBackend backend;
        try { backend = Backend(); }
        catch (Exception e) { Log(warn: true, "ServerModPause: backend read threw: " + e.Message); return; }
        if (!backend.Supported) return;
        bool? hosting = backend.HostsPrivateGame();
        if (!ServerModPausePolicy.MayDetectNoProgenitor(hosting, host))
        {
            Log(warn: false, ServerModPauseText.LogNoRequestNotActed(host, hosting));
            return;
        }
        lock (Gate) _host = host;
        Log(warn: false, ServerModPauseText.LogNoProgenitor(grace, host));
        try { ServerProfiles.NoteProgenitor(host, runsProgenitor: false, DateTime.UtcNow); }
        catch (Exception e) { Log(warn: true, "ServerModPause: profile write threw: " + e.Message); }

        var provided = ProvidedRows();
        var owners = ProvidedOwners();
        bool auto = ServerProfiles.AutoPause(host);
        if (auto)
        {
            lock (Gate) _serverWithoutProgenitor = true;
            KeepPausedIfUnrun(backend, RecordedRequest(), host, noProgenitor: true);
        }
        try
        {
            var plan = ServerModPausePolicy.PlanNoProgenitor(
                provided, owners, backend.Suspendable(),
                guids =>
                {
                    var set = backend.Closure(guids, out string refusal);
                    return (set, refusal);
                },
                auto,
                RecordedRequest());
            if (plan.RestartNeeded.Count > 0)
                Log(warn: true, ServerModPauseText.LogNotPaused(ServerModPausePolicy.WhyRestartOnly, plan.RestartNeeded));
            if (plan.Pause.Count == 0)
            {
                if (plan.Why.Length > 0 && plan.Why != ServerModPausePolicy.WhyRestartOnly)
                    Log(warn: false, ServerModPauseText.LogNotPaused(plan.Why, plan.RestartNeeded));
                if (plan.RestartNeeded.Count > 0 && plan.Why != ServerModPausePolicy.WhyAutoPauseOff)
                {
                    RaiseNotice(ServerModPauseText.Plain(ServerModPauseText.NoticeTitle, ServerModPauseText.NoProgenitorRestartOnlyNotice(plan.RestartNeeded)));
                }
                return;
            }

            var taken = backend.Suspend(plan.Pause, RowOwners(provided, owners, plan.Pause), out string report);
            if (taken.Count == 0)
            {
                Log(warn: true, "ServerModPause: nothing was paused: " + report);
                return;
            }
            lock (Gate) _serverWithoutProgenitor = true;
            AfterPause(taken, host, backend, noProgenitor: true, plan.RestartNeeded);
        }
        catch (Exception e)
        {
            Log(warn: true, "ServerModPause: no-Progenitor pause threw: " + e.Message);
        }
    }

    /// <summary><c>plugin\trow</c> for every provided row a plugin about to be paused owns.</summary>
    private static List<string> RowOwners(
        IReadOnlyList<Handshake.ModRecord> provided, IReadOnlyDictionary<string, string> owners, IReadOnlyList<string> plugins)
    {
        var set = new HashSet<string>(plugins, StringComparer.OrdinalIgnoreCase);
        var rows = new List<string>();
        foreach (var rec in provided)
        {
            string owner = ServerModPausePolicy.OwnerOf(rec.Guid, owners, set);
            if (owner.Length == 0 || !set.Contains(owner)) continue;
            rows.Add(owner + "\t" + rec.Guid + (ServerModPausePolicy.IsServerMustHave(rec) ? "\t1" : ""));
        }
        return rows;
    }

    private static void ResumeAll(IServerModPauseBackend backend, string reason)
    {
        try
        {
            // SETTINGS1 stage 3: every paused plugin except the ones the player turned off. The list
            // is explicit (never null, which Bootstrap reads as "every paused DLL").
            var ids = IdsOf(backend.Suspended(), skipHeld: true);
            if (ids.Count == 0) return;
            var loaded = backend.Resume(ids, out string report);
            if (report.Length > 0) Log(warn: false, "ServerModPause: " + report);
            if (loaded.Count > 0) AfterResume(loaded, reason);
            foreach (var id in RemainingResuming(backend, loaded)) Await(backend, id, reason, reAnswer: false);
        }
        catch (Exception e)
        {
            Log(warn: true, "ServerModPause: resume on leave threw: " + e.Message);
        }
    }

    private static IEnumerable<string> RemainingResuming(IServerModPauseBackend backend, IReadOnlyList<string> loaded)
    {
        foreach (var p in backend.Suspended())
            if (p.Resuming && !Contains(loaded, p.Guid)) yield return p.Guid;
    }

    private static void Await(IServerModPauseBackend backend, string id, string reason, bool reAnswer)
    {
        bool fresh = false;
        EditAwaiting(backend, map =>
        {
            if (map.TryGetValue(id, out var existing)) { existing.ReAnswer |= reAnswer; return; }
            map[id] = new Awaited { Reason = reason, ReAnswer = reAnswer };
            fresh = true;
        });
        if (fresh) Log(warn: false, ServerModPauseText.LogResumePending(id));
    }

    /// <summary>
    /// CLIENTPOLICY2: the automatic pause's plan for this request: the policy remembered for this server
    /// (<see cref="ServerProfiles.PolicyOf"/>), this client's mods as its report lists them, and Bootstrap's pausable set.
    /// Nothing when the server did not ask for the report or no policy is remembered. Without <paramref name="withClosure"/>
    /// the candidates stand for the closure (the cheap check whether the reply must wait).
    /// </summary>
    private static ClientPolicyPausePlan PolicyPlanFor(IReadOnlyList<Handshake.ModRecord> asked, IServerModPauseBackend backend, bool withClosure)
    {
        if (!ServerAsksReport()) return ClientPolicyPausePlan.Nothing;
        string host;
        lock (Gate) host = _host;
        var section = ClientPolicyView.FromRemembered(RememberedPolicy(host));
        if (section == null) return ClientPolicyPausePlan.Nothing;
        return ClientPolicyView.PlanPause(section, asked, ClientMods(), backend.Suspendable(),
            withClosure
                ? guids =>
                {
                    var set = backend.Closure(guids, out string refusal);
                    return (set, refusal);
                }
                : guids => (guids, ""),
            ProvidedRows(), ProvidedOwners(), ServerProfiles.AutoPause(host));
    }

    // CLIENTPOLICY2: the last automatic pause, for the bridge's readback.
    private static Dictionary<string, object?>? _lastPolicyPause;

    private static void PausePolicyMods(IReadOnlyList<Handshake.ModRecord> asked, string host, IServerModPauseBackend backend,
        List<(string Name, string Reason)> notice)
    {
        try
        {
            var plan = PolicyPlanFor(asked, backend, withClosure: true);
            if (plan.NotAllowed.Count == 0) return;
            var taken = plan.Pause.Count > 0
                ? backend.Suspend(plan.Pause, RowOwners(ProvidedRows(), ProvidedOwners(), plan.Pause), out string report)
                : Array.Empty<string>();
            lock (Gate)
                _lastPolicyPause = new Dictionary<string, object?>
                {
                    ["at"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["host"] = host,
                    ["notAllowed"] = new List<string>(plan.NotAllowed),
                    ["planned"] = new List<string>(plan.Pause),
                    ["paused"] = new List<string>(taken),
                    ["restartOnly"] = new List<string>(plan.RestartOnly),
                    ["why"] = plan.Why,
                };
            if (taken.Count > 0) AfterPause(taken, host, backend, noProgenitor: false, Array.Empty<string>(), notice, policy: true);
            else if (plan.Pause.Count > 0) Log(warn: true, "ClientPolicy: the automatic pause took nothing (Bootstrap refused it)");
            else if (plan.Why.Length > 0) Log(warn: false, "ClientPolicy: not paused before the report: " + plan.Why
                + (plan.RestartOnly.Count > 0 ? " (restart without: " + ServerModPauseText.Join(plan.RestartOnly) + ")" : ""));
        }
        catch (Exception e)
        {
            Log(warn: true, "ClientPolicy: the automatic pause threw; replying without it: " + e.Message);
        }
    }

    internal const string StandingRequired = "required";
    internal const string StandingApproved = "approved";
    internal const string StandingNotAllowed = "not-allowed";

    /// <summary>
    /// SELFFIX1 review: the required client mods of the policy remembered for the server being joined, or null when none is
    /// remembered (a first join). The mod report's fix claims are limited to these.
    /// </summary>
    internal static IReadOnlyList<ClientModRow>? RememberedRequiredRows()
    {
        try
        {
            var section = ClientPolicyView.FromRemembered(RememberedPolicy(CurrentHost()));
            return ClientPolicyView.PolicyOf(section)?.Required;
        }
        catch { return null; }
    }

    /// <summary>
    /// CLIENTPOLICY3: where <paramref name="id"/> stands under the joined server's client mod policy, as this client
    /// remembers it (signed): <see cref="StandingRequired"/>, <see cref="StandingApproved"/> (an approved row names it),
    /// <see cref="StandingNotAllowed"/> (Only approved, and nothing allows it), or empty (no policy known, or it says
    /// nothing about this mod).
    /// </summary>
    internal static string PolicyStanding(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        try
        {
            if (!ServerAsksReport()) return "";
            var section = ClientPolicyView.FromRemembered(RememberedPolicy(CurrentHost()));
            var policy = ClientPolicyView.PolicyOf(section);
            if (policy == null) return "";
            if (policy.IndexOfRequired(id) >= 0) return StandingRequired;
            if (policy.IndexOfApproved(id) >= 0) return StandingApproved;
            if (policy.Mode != ClientPolicyMode.Approved) return "";
            string name = id, version = "";
            try
            {
                foreach (var p in Backend().Suspended())
                    if (string.Equals(p.Guid, id, StringComparison.OrdinalIgnoreCase)) { name = p.Name; version = p.Version; break; }
            }
            catch { /* judged by its id */ }

            var entry = new ClientModEntry(id, name, version, ClientModSources.Progenitor);
            return ClientModJudge.Allowed(policy, ClientPolicyView.FactsOf(section, RecordedRequest()), entry) ? "" : StandingNotAllowed;
        }
        catch { return ""; }
    }

    /// <summary>CLIENTPOLICY3: the joined server's connect address as the pause knows it (the server notice's probes).</summary>
    internal static string CurrentHost()
    {
        lock (Gate)
        {
            if (_host.Length == 0) _host = ReadHost();
            return _host;
        }
    }

    /// <summary>CLIENTPOLICY3: the joined server's name from its profile, or empty.</summary>
    internal static string CurrentServerName() => ServerNameOf(CurrentHost());

    /// <summary>
    /// CLIENTPOLICY3 (docs/client-mod-policy.md, "Stage 3 as built"): the automatic pause before a mid-session report.
    /// When the loaded set changed during the session (a hot load, a Turn on, an install, a reload) or a re-signed catalog
    /// brought a new policy, every loaded mod the remembered policy does not allow that the per-server pause can take is
    /// paused before the report goes out, exactly as at join (<see cref="PausePolicyMods"/>, the same plan and the same
    /// refusals), and the existing paused-mods notice names it. Returns how many were paused.
    /// </summary>
    // CLIENTPOLICY3: plugins this connection paused because the joined server's client mod policy did not allow them.
    private static readonly HashSet<string> PolicyPaused = new(StringComparer.OrdinalIgnoreCase);

    internal const string ReasonPolicyAllows = "policy-allows";
    internal const string BackOnTitle = "Mods back on";

    /// <summary>
    /// CLIENTPOLICY3 (live 2026-10-05): a signed policy update arrived mid-session. Every mod this connection paused
    /// because the policy did not allow it, that the policy now requires, approves or otherwise allows (or that no
    /// policy covers any more), is resumed now; a mod the player switched off stays off. The other mods paused for this
    /// server stay paused around the resume. A popup names what came back. Returns how many were released.
    /// </summary>
    internal static int ResumePolicyPausedNowAllowed()
    {
        List<string> candidates;
        lock (Gate) candidates = new List<string>(PolicyPaused);
        if (candidates.Count == 0) return 0;
        IServerModPauseBackend backend;
        try { backend = Backend(); }
        catch { return 0; }
        if (!backend.Supported) return 0;
        try
        {
            var suspended = backend.Suspended();
            var release = new List<string>();
            var names = new List<string>();
            foreach (string id in candidates)
            {
                PausedPlugin? found = null;
                foreach (var p in suspended)
                    if (string.Equals(p.Guid, id, StringComparison.OrdinalIgnoreCase)) { found = p; break; }
                if (found == null)
                {
                    lock (Gate) PolicyPaused.Remove(id); // already back (a leave, a resume)
                    continue;
                }

                if (found.Value.Resuming || Held(id) || PolicyStanding(id) == StandingNotAllowed) continue;
                release.Add(id);
                names.Add(found.Value.Name);
            }

            if (release.Count == 0) return 0;
            var keep = new List<string>();
            foreach (var p in suspended)
                if (!p.Resuming && !Held(p.Guid) && !Contains(release, p.Guid)) keep.Add(p.Guid);
            var loaded = backend.Resume(release, out string report);
            if (report.Length > 0) Log(warn: false, "ServerModPause: " + report);
            if (keep.Count > 0)
            {
                try { backend.KeepPaused(keep); }
                catch { /* best effort, as the switch does */ }
            }

            lock (Gate) foreach (var id in release) PolicyPaused.Remove(id);
            if (loaded.Count > 0) AfterResume(loaded, ReasonPolicyAllows);
            foreach (var id in RemainingResuming(backend, loaded))
                if (Contains(release, id)) Await(backend, id, ReasonPolicyAllows, reAnswer: false);
            InvalidateInventory();
            string server = ServerNameOf(CurrentHost());
            string where = string.IsNullOrWhiteSpace(server) ? "This server" : server.Trim();
            RaiseNotice(new PauseNoticeContent
            {
                Title = BackOnTitle,
                Body = where + (names.Count == 1 ? " allows this mod now, so it's back on:" : " allows these mods now, so they're back on:"),
                Lines = names,
                Text = BackOnTitle + ": " + string.Join(", ", names) + ".",
            });
            Log(warn: false, "ClientPolicy: resumed " + string.Join(", ", release) + ": the server's policy allows them now");
            return release.Count;
        }
        catch (Exception e)
        {
            Log(warn: true, "ClientPolicy: resuming mods the policy allows now threw: " + e.Message);
            return 0;
        }
    }

    /// <summary>CLIENTPOLICY3: this connection paused <paramref name="id"/> because the server's policy did not allow it.</summary>
    internal static bool PausedForPolicy(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        bool ours;
        lock (Gate) ours = PolicyPaused.Contains(id);
        return ours && IsPaused(id);
    }

    internal static int PausePolicyModsMidSession()
    {
        IServerModPauseBackend backend;
        try { backend = Backend(); }
        catch { return 0; }
        if (!backend.Supported) return 0;
        string host = CurrentHost();

        var paused = new List<(string Name, string Reason)>();
        PausePolicyMods(RecordedRequest(), host, backend, paused);
        if (paused.Count > 0) RaiseNotice(ServerModPauseText.Paused(paused, ServerNameOf(host)));
        return paused.Count;
    }

    /// <summary>
    /// CLIENTPOLICY2: <b>Pause for this server</b> on the join screens: pause <paramref name="ids"/> (with Bootstrap's
    /// closure) for the joined server, as the automatic pause would. Refused for a plugin that cannot be paused, for a
    /// closure that takes a mod the server asked about, or by Bootstrap.
    /// </summary>
    internal static JoinPauseResult PauseForPolicy(IReadOnlyList<string> ids)
    {
        if (ids == null || ids.Count == 0) return JoinPauseResult.Fail("nothing to pause");
        IServerModPauseBackend backend;
        try { backend = Backend(); }
        catch (Exception e) { return JoinPauseResult.Fail(e.Message); }
        if (!backend.Supported) return JoinPauseResult.Fail(backend.UnsupportedReason);
        try
        {
            var pausable = new HashSet<string>(backend.Suspendable(), StringComparer.OrdinalIgnoreCase);
            foreach (string id in ids)
                if (!pausable.Contains(id))
                    return JoinPauseResult.Fail(id + " was loaded when the game started (or uses ctx.UnsafeHarmony), so it cannot be paused");
            var set = backend.Closure(ids, out string refusal);
            if (set == null || set.Count == 0) return JoinPauseResult.Fail(refusal.Length > 0 ? refusal : ServerModPausePolicy.WhyNotClean);
            var asked = RecordedRequest();
            var askedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in asked) if (!string.IsNullOrEmpty(r.Guid)) askedIds.Add(r.Guid);
            foreach (string plugin in set)
                if (askedIds.Contains(plugin)) return JoinPauseResult.Fail(ClientPolicyView.WhyAsked);
            string host;
            lock (Gate)
            {
                if (_host.Length == 0) _host = ReadHost();
                host = _host;
            }

            var taken = backend.Suspend(set, RowOwners(ProvidedRows(), ProvidedOwners(), set), out string report);
            if (taken.Count == 0) return JoinPauseResult.Fail(report.Length > 0 ? report : "nothing was paused");
            var names = new List<string>();
            foreach (var p in backend.Suspended())
                foreach (var id in taken)
                    if (string.Equals(p.Guid, id, StringComparison.OrdinalIgnoreCase)) { names.Add(p.Name.Length > 0 ? p.Name : p.Guid); break; }
            // The join screens say what was paused; no popup on top of them.
            AfterPause(taken, host, backend, noProgenitor: false, Array.Empty<string>(), new List<(string, string)>(), policy: true);
            return new JoinPauseResult { Ok = true, Taken = new List<string>(taken), Names = names };
        }
        catch (Exception e)
        {
            return JoinPauseResult.Fail(e.Message);
        }
    }

    /// <summary>CLIENTPOLICY2: the bridge's <c>join_ui policy</c> readback: the remembered policy, the plan now, the last pause.</summary>
    internal static Dictionary<string, object?> DescribePolicyPause()
    {
        string host;
        Dictionary<string, object?>? last;
        lock (Gate)
        {
            host = _host;
            last = _lastPolicyPause;
        }

        if (host.Length == 0) host = ReadHost();
        string text = RememberedPolicy(host);
        var section = ClientPolicyView.FromRemembered(text);
        var d = new Dictionary<string, object?>
        {
            ["host"] = host,
            ["serverAsks"] = ServerAsksReport(),
            ["remembered"] = section != null,
            ["mode"] = section == null ? "" : ClientModRules.ModeWord(section.PolicyMode),
            ["approved"] = section?.Approved.Count ?? 0,
            ["requiredClient"] = section?.RequiredClient.Count ?? 0,
            ["serverRuns"] = section?.ServerRuns.Count ?? 0,
            ["autoPause"] = ServerProfiles.AutoPause(host),
            ["lastPause"] = last,
        };
        try
        {
            var backend = Backend();
            if (backend.Supported && section != null)
            {
                var plan = ClientPolicyView.PlanPause(section, RecordedRequest(), ClientMods(), backend.Suspendable(),
                    guids => (guids, ""), ProvidedRows(), ProvidedOwners(), ServerProfiles.AutoPause(host));
                d["wouldPause"] = new List<string>(plan.Pause);
                d["notAllowed"] = new List<string>(plan.NotAllowed);
                d["restartOnly"] = new List<string>(plan.RestartOnly);
                d["why"] = plan.Why;
            }
        }
        catch (Exception e) { d["planError"] = e.Message; }

        return d;
    }

    private static bool ServerAsksReport()
    {
        if (Seams.Value?.ServerAsksReport is { } fake) return fake();
        return ClientModReportClient.ServerAsks;
    }

    private static string RememberedPolicy(string host)
    {
        if (Seams.Value?.RememberedPolicy is { } fake) return fake(host) ?? "";
        return ServerProfiles.PolicyOf(host);
    }

    private static IReadOnlyList<ClientModEntry> ClientMods()
    {
        if (Seams.Value?.ClientMods is { } fake) return fake() ?? Array.Empty<ClientModEntry>();
        return ClientModReportClient.CurrentEntries();
    }

    private static void AfterPause(IReadOnlyList<string> taken, string host, IServerModPauseBackend backend,
        bool noProgenitor, IReadOnlyList<string> restartOnly, List<(string Name, string Reason)>? noticeSink = null, bool policy = false,
        Func<string, string?>? reasonOf = null)
    {
        Log(warn: false, ServerModPauseText.LogPaused(taken, host));
        // CLIENTPOLICY3: what the client mod policy paused can come back when a mid-session policy update allows it.
        if (policy) lock (Gate) foreach (var id in taken) PolicyPaused.Add(id);
        try { EditAwaiting(backend, map => { foreach (var id in taken) map.Remove(id); }); }
        catch { /* best effort */ }
        // C3: each paused mod with its own reason when the caller gives one (strict matching), else the batch's.
        var names = new List<(string Name, string? Why)>();
        foreach (var p in backend.Suspended())
            foreach (var id in taken)
                if (string.Equals(p.Guid, id, StringComparison.OrdinalIgnoreCase)) { names.Add((p.Name, ReasonFor(reasonOf, id))); break; }
        if (names.Count == 0)
            foreach (var id in taken) names.Add((id, ReasonFor(reasonOf, id)));
        try { ServerProfiles.NotePaused(host, taken, DateTime.UtcNow); }
        catch (Exception e) { Log(warn: true, "ServerModPause: profile write threw: " + e.Message); }
        InvalidateInventory();
        try { EventBus.Publish(new ModsPaused(taken, host, noProgenitor)); }
        catch (Exception e) { Log(warn: true, "ServerModPause: ModsPaused publish threw: " + e.Message); }
        string reason = policy ? ServerModPauseText.ReasonNotAllowed
            : noProgenitor ? ServerModPauseText.ReasonNoProgenitor
            : ServerModPauseText.ReasonServerLacks;
        var entries = new List<(string Name, string Reason)>(names.Count);
        foreach (var n in names) entries.Add((n.Name, n.Why ?? reason));
        if (noticeSink != null)
        {
            noticeSink.AddRange(entries);
            return;
        }

        RaiseNotice(ServerModPauseText.Paused(entries, ServerNameOf(host), noProgenitor ? restartOnly : null));
    }

    private static string? ReasonFor(Func<string, string?>? reasonOf, string id)
    {
        try { return reasonOf?.Invoke(id); }
        catch { return null; }
    }

    private static void AfterResume(IReadOnlyList<string> loaded, string reason)
    {
        Log(warn: false, ServerModPauseText.LogResumed(loaded, reason));
        InvalidateInventory();
        try { EventBus.Publish(new ModsResumed(loaded, reason)); }
        catch (Exception e) { Log(warn: true, "ServerModPause: ModsResumed publish threw: " + e.Message); }
    }

    private static void ReportUnsupported(string reason, List<Handshake.ModRecord> asked)
    {
        // Only worth saying when this client's own declared rows are what fails.
        ServerModPausePolicy.PredictAccepted(asked, ProvidedRows(), out var failed);
        bool ours = false;
        var askedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in asked) askedIds.Add(r.Guid);
        foreach (var id in failed) if (!askedIds.Contains(id)) { ours = true; break; }
        if (!ours) return;
        bool first;
        lock (Gate) { first = !_warnedUnsupported; _warnedUnsupported = true; }
        if (first) Log(warn: true, ServerModPauseText.LogUnsupported(reason));
        RaiseNotice(ServerModPauseText.Plain(ServerModPauseText.UnsupportedTitle, ServerModPauseText.UnsupportedNotice(reason)));
    }

    private static void InvalidateInventory()
    {
        try { ModMenuInventory.Invalidate("server-mod-pause"); }
        catch { /* evidence, not a gate */ }
    }

    /// <summary>The rows each paused plugin provided, as Bootstrap recorded them (they survive a Core swap).</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> RowsOf(IReadOnlyList<PausedPlugin> plugins)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in plugins) map[p.Guid] = p.Rows;
        return map;
    }

    private static List<string> IdsOf(IReadOnlyList<PausedPlugin> plugins, bool skipHeld = false)
    {
        var ids = new List<string>(plugins.Count);
        foreach (var p in plugins)
            if (!skipHeld || !Held(p.Guid)) ids.Add(p.Guid);
        return ids;
    }

    /// <summary>
    /// SETTINGS1 stage 3: is <paramref name="id"/> held off by the player's switch
    /// (<see cref="Progenitor.Ui.ModSwitch.IsHeld"/>)? Such a plugin is never resumed by a leave or a
    /// server request, and is not reported as paused for this server. Never throws.
    /// </summary>
    internal static Func<string, bool>? HeldByPlayer { get; set; }

    private static bool Held(string id)
    {
        try { return (HeldByPlayer ?? Progenitor.Ui.ModSwitch.IsHeld)(id); }
        catch { return false; }
    }

    /// <summary>The pause backend (Bootstrap's reloader), for the Mods menu's on/off switch.</summary>
    internal static IServerModPauseBackend PauseBackend() => Backend();

    /// <summary>
    /// SETTINGS1 stage 3: did this join's pause take <paramref name="id"/> — is the client on a server
    /// (a recorded handshake request, or one detected without Progenitor) whose profile's last pause
    /// named it? The switch keeps such a plugin paused when the player turns it back on.
    /// </summary>
    internal static bool PausedForCurrentServer(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        // CLIENTPOLICY3: the joined server's client mod policy decides first. A mod it requires or approves is not
        // paused for it (live 2026-10-05: a required mod switched off and on stayed paused and the player was
        // disconnected); one it does not allow is.
        string standing = PolicyStanding(id);
        if (standing == StandingNotAllowed) return true;
        if (standing == StandingRequired || standing == StandingApproved) return false;
        string host;
        bool noProgenitor;
        lock (Gate) { host = _host; noProgenitor = _serverWithoutProgenitor; }
        if (host.Length == 0) return false;
        try
        {
            // Review of 5a6e40f8, finding 5: the current join's own evidence, never the host's list
            // from an earlier visit. A server without Progenitor runs none of it; a Progenitor server
            // runs it when this join's request names it (or a row it provided).
            if (noProgenitor) return true;
            var asked = RecordedRequest();
            if (asked.Length == 0) return false;
            IReadOnlyList<string> rows = Array.Empty<string>();
            try
            {
                foreach (var p in Backend().Suspended())
                    if (string.Equals(p.Guid, id, StringComparison.OrdinalIgnoreCase)) { rows = p.Rows; break; }
            }
            catch { /* the plugin's own id is still compared */ }
            var runsIt = ServerModPausePolicy.ResumeFor(asked, new[] { id },
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { [id] = rows });
            return runsIt.Count == 0;
        }
        catch { /* not known: the switch resumes it */ }
        return false;
    }

    private static bool Contains(IReadOnlyList<string> list, string id)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The backend. A bound Bootstrap is cached; "no Bootstrap" is not, because the link may be made
    /// later in this process.
    /// </summary>
    private static IServerModPauseBackend Backend()
    {
        if (Seams.Value?.Backend is { } fake) return fake;
        lock (Gate)
        {
            if (_backend != null) return _backend;
#if PROGENITOR_TESTS
            var created = ReflectedPauseBackend.Create(null);
#else
            var created = ReflectedPauseBackend.Create(BootstrapLink.OwnsLifetime ? BootstrapLink.HostType : null);
#endif
            if (created is ReflectedPauseBackend { NoBootstrap: true }) return created;
            _backend = created;
            return created;
        }
    }

    private static bool ReAnswer()
    {
        if (Seams.Value?.ReAnswer is { } fake) return fake();
#if PROGENITOR_TESTS
        return false;
#else
        return Handshake.ReDeclareSelf();
#endif
    }

    private static string ReadHost()
    {
        if (Seams.Value?.Host is { } host) return host() ?? "";
        try { return ModManifest.ReadLiveConnectHost(); }
        catch { return ""; }
    }

    private static double Clock()
    {
        if (Seams.Value?.Clock is { } clock) return clock();
        return System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    }

    private static void Post(Action action)
    {
        if (Seams.Value?.Post is { } post) { post(action); return; }
        Dev.MainThread.Post(action, Owner);
    }

    private static void PostDelayed(double seconds, Action action)
    {
        if (Seams.Value?.PostDelayed is { } later) { later(seconds, action); return; }
        Dev.MainThread.PostDelayed(seconds, action, Owner);
    }

    private static void Log(bool warn, string line)
    {
        try
        {
            if (warn) Plugin.Logger?.LogWarning(line);
            else Plugin.Logger?.LogInfo(line);
        }
        catch { /* a logger must not cost the join */ }
    }
}

/// <summary>
/// A deferred handshake request, as text for Bootstrap's carry-over slot:
/// <c>v1|seq|truncated</c>, then one <c>guid\tversion\tlevel\trule\tdeclared</c> line per row.
/// </summary>
internal static class CarriedRequest
{
    internal static string Serialize(long seq, bool truncated, IReadOnlyList<Handshake.ModRecord> asked) =>
        Serialize(seq, truncated, asked, "", "");

    /// <summary>
    /// Also records the connection the request came on: <paramref name="host"/> (the connect address)
    /// and <paramref name="connection"/> (Bootstrap's join identity), so a later generation answers
    /// it only on that same connection.
    /// </summary>
    internal static string Serialize(long seq, bool truncated, IReadOnlyList<Handshake.ModRecord> asked, string host, string connection)
    {
        var sb = new StringBuilder();
        sb.Append("v2|").Append(seq.ToString(CultureInfo.InvariantCulture)).Append('|').Append(truncated ? '1' : '0')
          .Append('|').Append(Clean(host)).Append('|').Append(Clean(connection));
        foreach (var r in asked)
        {
            sb.Append('\n').Append(r.Guid).Append('\t').Append(r.Version).Append('\t')
              .Append(((int)r.Level).ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(((int)r.VersionRule).ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(r.Declared ? '1' : '0');
        }
        return sb.ToString();
    }

    internal static bool TryParse(string? text, out long seq, out bool truncated, out List<Handshake.ModRecord> asked) =>
        TryParse(text, out seq, out truncated, out asked, out _, out _);

    internal static bool TryParse(string? text, out long seq, out bool truncated, out List<Handshake.ModRecord> asked,
        out string host, out string connection)
    {
        seq = 0;
        truncated = false;
        host = "";
        connection = "";
        asked = new List<Handshake.ModRecord>();
        if (string.IsNullOrEmpty(text)) return false;
        var lines = text!.Split('\n');
        var head = lines[0].Split('|');
        if (head.Length != 5 || head[0] != "v2" || !long.TryParse(head[1], NumberStyles.None, CultureInfo.InvariantCulture, out seq)) return false;
        truncated = head[2] == "1";
        host = head[3];
        connection = head[4];
        for (int i = 1; i < lines.Length; i++)
        {
            var f = lines[i].Split('\t');
            if (f.Length != 5 || f[0].Length == 0) return false;
            if (!int.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out int level)
                || !int.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out int rule)) return false;
            asked.Add(new Handshake.ModRecord(f[0], f[1], (NetworkCompatibilityLevel)level, (NetworkVersionRule)rule, f[4] == "1"));
        }
        return true;
    }

    /// <summary>
    /// Answer a carried request only on the connection it came on: the client is loading or in the
    /// world (not on the disconnect screen, which keeps <c>Client_0</c> alive), the connect address
    /// is the recorded one and non-empty, and Bootstrap's join identity is unchanged. Anything else
    /// discards it.
    /// </summary>
    internal static bool MayAnswer(string carriedHost, string carriedConnection, string currentHost, string currentConnection, bool connectionLive) =>
        connectionLive
        && !string.IsNullOrEmpty(carriedHost)
        && string.Equals(carriedHost, currentHost, StringComparison.OrdinalIgnoreCase)
        && string.Equals(carriedConnection ?? "", currentConnection ?? "", StringComparison.Ordinal);

    private static string Clean(string? s) => (s ?? "").Replace('|', ' ').Replace('\n', ' ');
}

/// <summary>
/// Decides when a join has gone on long enough without a Progenitor handshake request to call the
/// server one without Progenitor. Game-free: the client frame hook feeds it
/// <c>ClientState.Running</c> and a clock.
///
/// <para>It arms only on a transition <em>into</em> Running that this Core generation watched
/// happen (it saw a non-Running state first), so a Core that installs mid-game, or the
/// re-handshake after a Core swap, never arms it. A request at any time before the deadline
/// disarms it, and once a request was seen it stays quiet until the connection ends
/// (<see cref="Reset"/>).</para>
/// </summary>
internal sealed class NoProgenitorWatch
{
    private bool _sawNotRunning;
    private bool _running;
    private bool _armed;
    private double _due;

    /// <summary>True once a handshake request arrived during the current join.</summary>
    internal bool RequestSeen { get; private set; }

    internal bool Armed => _armed;

    /// <summary>One frame. True exactly once: the grace period passed with no request.</summary>
    internal bool Observe(bool running, double now, double graceSeconds)
    {
        if (!running)
        {
            // A Running -> Loading -> Running flicker is the same connection: a request already
            // seen keeps the watch quiet until the connection really ends (Reset).
            _sawNotRunning = true;
            _running = false;
            _armed = false;
            return false;
        }

        if (!_running)
        {
            _running = true;
            if (_sawNotRunning && !RequestSeen && graceSeconds > 0 && !double.IsNaN(now))
            {
                _armed = true;
                _due = now + graceSeconds;
            }
        }

        if (_armed && now >= _due)
        {
            _armed = false;
            return !RequestSeen;
        }
        return false;
    }

    internal void NoteRequest()
    {
        RequestSeen = true;
        _armed = false;
    }

    /// <summary>A real connection end. The next Loading/Running cycle is a new join.</summary>
    internal void Reset()
    {
        RequestSeen = false;
        _armed = false;
    }

    internal void ResetForTests()
    {
        _sawNotRunning = false;
        _running = false;
        _armed = false;
        _due = 0;
        RequestSeen = false;
    }
}
