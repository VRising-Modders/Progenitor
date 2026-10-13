using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Networking;

namespace Progenitor.Install;

/// <summary>JOINFLOW4: one file on disk that declares a plugin GUID.</summary>
internal sealed class ModCopy
{
    public string Path { get; init; } = "";

    /// <summary><c>reload</c>, <c>pending</c> or <c>plugins</c>.</summary>
    public string Folder { get; init; } = "";

    /// <summary>The <c>[BepInPlugin]</c> version read from the file.</summary>
    public string Version { get; init; } = "";

    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>JOINFLOW4: what the version store asks of the running game (tests supply a fake).</summary>
internal interface IModVersionRuntime
{
    /// <summary>Every DLL declaring <paramref name="modId"/>: <c>progenitor-reload</c> (flat), <c>progenitor-pending</c>, and the chainloader's copy.</summary>
    IReadOnlyList<ModCopy> CopiesOf(string modId);

    /// <summary>MANAGED1: the mod manager that installed a <c>BepInEx/plugins</c> copy, else null.</summary>
    string? ManagedBy(string modId);

    bool UnsafeHarmony(string modId);

    /// <summary>
    /// Why the reloader cannot unload <paramref name="modId"/> now, as a sentence ("" when it can). The live runtime
    /// names the concrete cause (ctx.UnsafeHarmony, a retiring generation, an older Bootstrap); the default derives it
    /// from <see cref="UnsafeHarmony"/>.
    /// </summary>
    string UnloadBlocker(string modId) => UnsafeHarmony(modId) ? ModVersionRouteText.UnsafeHarmony : "";

    /// <summary>Loaded reload plugins that would be unloaded with it (its dependents).</summary>
    int Dependents(string modId);

    /// <summary>The dependents' names for the route reason, or "".</summary>
    string DependentNames(string modId) => "";

    bool LoaderBusy { get; }

    /// <summary>The version this client declares for it now (its handshake row), or null when it is not loaded.</summary>
    string? LoadedVersion(string modId);

    /// <summary>
    /// The reloader's own answer: true while it holds a loaded plugin with this GUID, false when it does not, null when
    /// it cannot say (an older Bootstrap). A swap under another file name waits on this before it writes.
    /// </summary>
    bool? ReloaderHolds(string modId);

    /// <summary>The level and version rule it declares now (names), or empty.</summary>
    (string Level, string Rule) Declared(string modId);

    /// <summary>The server-mods record names this file (a server's copy): its host key, else null.</summary>
    string? ServerRecordHost(string fileName);
}

/// <summary>JOINFLOW4: one swap the join plans: the mod, the kept version the server needs, and its route.</summary>
internal sealed class JoinSwapPlan
{
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public ModVersionDecision Decision { get; init; }
}

/// <summary>
/// JOINFLOW4: the version store as the client uses it — find the active copy, decide the route, park, swap, restore.
/// Game-free: the running game is behind <see cref="IModVersionRuntime"/>.
/// </summary>
internal sealed class ModVersionService
{
    public ModVersionService(ModVersionEngine engine, IModVersionRuntime runtime)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public ModVersionEngine Engine { get; }
    public IModVersionRuntime Runtime { get; }

    public ModVersionManifest Manifest() => Engine.Read();

    /// <summary>The active reload copy, or null; <paramref name="refusal"/> when the copies on disk rule a park or swap out.</summary>
    public ModCopy? ActiveCopy(string modId, out ModVersionDecision? refusal, out List<ModCopy> others)
    {
        refusal = null;
        others = new List<ModCopy>();
        var copies = Runtime.CopiesOf(modId) ?? Array.Empty<ModCopy>();
        ModCopy? active = null;
        foreach (var c in copies)
        {
            if (c.Folder == "plugins")
            {
                string? manager = null;
                try { manager = Runtime.ManagedBy(modId); }
                catch { manager = null; }
                refusal = ModVersionRoutes.Decide(new ModVersionRouteFacts { ManagedBy = manager, ActiveInPlugins = true });
                return null;
            }

            if (c.Folder == "reload")
            {
                if (active == null) active = c;
                else others.Add(c);
            }
            else others.Add(c);
        }

        // A reload copy marked for removal at the next start is already being replaced (the restart route).
        if (active != null && File.Exists(UninstallMarkers.MarkerFor(active.Path)))
            refusal = new ModVersionDecision(ModVersionRoute.Refused,
                active.Name + " is removed at the next start of V Rising (a swap waits for it); restart first.", ModVersionCode.Queued);
        return active;
    }

    /// <summary>The route facts for a swap of <paramref name="modId"/>.</summary>
    public ModVersionRouteFacts Facts(string modId, IReadOnlyList<ModCopy> others, bool forceRestart, bool midJoin, string? ignorePending = null)
    {
        var entry = Engine.Read().Find(modId);
        var names = new List<string>();
        var pending = new List<string>();
        foreach (var o in others)
        {
            // The store's own queued file is not a second copy; the swap that queued it is refused for being queued.
            if (ignorePending != null && string.Equals(o.Path, ignorePending, StringComparison.OrdinalIgnoreCase)) continue;
            if (o.Folder == "pending") pending.Add("progenitor-pending/" + o.Name);
            else names.Add(o.Folder + "/" + o.Name);
        }

        return new ModVersionRouteFacts
        {
            OtherCopies = names.Count,
            OtherCopyNames = string.Join(", ", names),
            PendingCopies = pending.Count,
            PendingCopyNames = string.Join(", ", pending),
            UnsafeReason = UnloadBlockerOf(modId),
            Dependents = SafeInt(() => Runtime.Dependents(modId)),
            DependentNames = SafeText(() => Runtime.DependentNames(modId)),
            LoaderBusy = Safe(() => Runtime.LoaderBusy),
            JournalBusy = Engine.JournalPending,
            QueuedVersion = entry?.Pending?.Version ?? "",
            MidJoin = midJoin,
            ForceRestart = forceRestart,
        };
    }

    private string UnloadBlockerOf(string modId)
    {
        try { return Runtime.UnloadBlocker(modId) ?? ""; }
        catch (Exception e) { return "whether it can be unloaded could not be read (" + e.Message + ")"; }
    }

    private static string SafeText(Func<string> read)
    {
        try { return read() ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Keep the active copy of <paramref name="modId"/> before something else replaces it (the server's copy, a
    /// Thunderstore version, the bridge's force-park). The route decides how: removed now when the reloader can
    /// unload it, else kept loaded and marked for removal at the next start.
    /// </summary>
    public ModVersionResult KeepActive(string modId, ActiveVersion? replacement, bool forceRestart = false, bool requireMove = false,
        bool midJoin = false)
    {
        if (Engine.JournalPending) return ModVersionResult.Refuse(ModVersionCode.Busy, "Another version swap is still being finished.");
        var active = ActiveCopy(modId, out var refusal, out var others);
        if (refusal is { } r) return ModVersionResult.Refuse(r.Code, r.Reason);
        if (active == null) return ModVersionResult.Refuse(ModVersionCode.NotFound, "No copy of " + modId + " is in progenitor-reload.");
        // A copy waiting in pending is what is about to replace this one (the caller's own install): only a second
        // reload copy rules a park out.
        var reloadOthers = others.FindAll(o => o.Folder == "reload");
        var facts = Facts(modId, reloadOthers, forceRestart, midJoin);
        var decision = ModVersionRoutes.Decide(facts);
        if (decision.Route == ModVersionRoute.Refused) return ModVersionResult.Refuse(decision.Code, decision.Reason);
        if (requireMove && decision.Route != ModVersionRoute.Now)
            return ModVersionResult.Refuse(ModVersionCode.Busy, modId + " cannot be unloaded now (" + decision.Reason + ").");
        var who = Provenance(modId, active);
        string version = VersionOf(modId, active);
        var (level, rule) = Runtime.Declared(modId);
        return Engine.Park(new ParkRequest
        {
            ModId = modId,
            ActivePath = active.Path,
            Version = version,
            Source = who.Source,
            SourceDetail = who.Detail,
            UserInstalled = who.User,
            Level = level ?? "",
            Rule = rule ?? "",
            Mode = decision.Route == ModVersionRoute.Now ? ParkMode.Move : ParkMode.CopyAndMark,
            Replacement = replacement,
        });
    }

    /// <summary>
    /// Swap kept <paramref name="version"/> in. <paramref name="forServer"/> empty: the player's own choice (it becomes
    /// preferred). <paramref name="midJoin"/>: called outside the handshake while a join is under way (refused).
    /// </summary>
    public ModVersionResult Swap(string modId, string version, string forServer, string forServerName, bool forceRestart = false, bool midJoin = false,
        bool nowOnly = false)
    {
        if (Engine.JournalPending) return ModVersionResult.Refuse(ModVersionCode.Busy, "Another version swap is still being finished.");
        var entry = Engine.Read().Find(modId);
        if (entry?.FindUsable(version) == null)
            return ModVersionResult.Refuse(ModVersionCode.NotFound, entry?.FindParked(version) != null
                ? "The kept copy of " + modId + " " + version + " came from a server; servers no longer send mod files, so it is not used."
                : "No kept copy of " + modId + " " + version + ".");
        if (entry.Pending != null)
            return ModVersionResult.Refuse(ModVersionCode.Queued, modId + " " + entry.Pending.Version + " is already queued for the next start; restart first.");
        var active = ActiveCopy(modId, out var refusal, out var others);
        if (refusal is { } r) return ModVersionResult.Refuse(r.Code, r.Reason);
        var facts = Facts(modId, others, forceRestart, midJoin);
        var decision = ModVersionRoutes.Decide(facts);
        if (decision.Route == ModVersionRoute.Refused) return ModVersionResult.Refuse(decision.Code, decision.Reason);
        if (nowOnly && decision.Route != ModVersionRoute.Now)
            return ModVersionResult.Refuse(ModVersionCode.Busy, modId + " " + version + " can only be swapped in at the next start (" + decision.Reason + ").");
        SwapActive? displaced = null;
        if (active != null)
        {
            var who = Provenance(modId, active);
            var (level, rule) = Runtime.Declared(modId);
            displaced = new SwapActive
            {
                Path = active.Path,
                Version = VersionOf(modId, active),
                Source = who.Source,
                SourceDetail = who.Detail,
                UserInstalled = who.User,
                Level = level ?? "",
                Rule = rule ?? "",
            };
        }

        var result = Engine.SwapIn(new SwapRequest
        {
            ModId = modId,
            Version = version,
            Restart = decision.Route == ModVersionRoute.Restart,
            Active = displaced,
            ForServer = forServer ?? "",
            ForServerName = forServerName ?? "",
        });

        // Under another file name the kept copy is written once the displaced plugin has unloaded; when it is not
        // loaded at all, that is now.
        if (result.Ok && result.AwaitingPlace) return TryCompletePlace(force: false) ?? result;
        return result;
    }

    /// <summary>
    /// The second half of a swap under another file name: write the kept copy once the displaced plugin is no longer
    /// loaded (or <paramref name="force"/>, after the caller's deadline). Null while it still waits; the
    /// <c>nothing-to-do</c> answer when no swap waits.
    /// </summary>
    public ModVersionResult? TryCompletePlace(bool force)
    {
        string id = Engine.AwaitingMod;
        if (id.Length == 0) return ModVersionResult.Refuse(ModVersionCode.NothingToDo, "No swap is waiting.");

        // Another copy appeared meanwhile (another install wrote one): stop, keep the displaced copy kept, write nothing.
        try
        {
            foreach (var c in Runtime.CopiesOf(id))
                if (c.Folder is "reload" or "pending" or "plugins")
                    return ModVersionResult.Refuse(ModVersionCode.TwoCopies,
                        "Another copy of " + id + " appeared (" + c.Folder + "/" + c.Name + "), so the kept one was not written (" + Engine.AbortPlace("another copy appeared") + ").");
        }
        catch { /* the engine still refuses a taken target name */ }

        // The reloader's own answer decides when it has one: while it holds the plugin nothing is written, whatever the
        // deadline (review: a forced write could land in a scan that still has the old copy). Only without it does the
        // handshake table, then the deadline, decide.
        bool? holds;
        try { holds = Runtime.ReloaderHolds(id); }
        catch { holds = true; }
        if (holds == true) return null;
        if (holds == null && !force)
        {
            string? loaded;
            try { loaded = Runtime.LoadedVersion(id); }
            catch { loaded = "?"; }
            if (loaded != null) return null;
        }

        return Engine.CompletePlace();
    }

    /// <summary>Give up a swap whose displaced plugin never unloaded: the displaced copy goes back.</summary>
    public string AbortWaiting(string why) => Engine.AbortWaiting(why);

    /// <summary>Swap the player's preferred version back in (the bridge's and the mod page's Restore).</summary>
    public ModVersionResult RestorePreferred(string modId, string? version = null, bool midJoin = false)
    {
        var entry = Engine.Read().Find(modId);
        string want = string.IsNullOrEmpty(version) ? ModVersionSelect.PreferredOf(entry) : version!;
        if (want.Length == 0) return ModVersionResult.Refuse(ModVersionCode.NothingToDo, "No preferred version of " + modId + " is kept.");
        if (entry?.FindUsable(want) == null)
        {
            if (entry?.Active != null && string.Equals(entry.Active.Version, want, StringComparison.OrdinalIgnoreCase) && entry.Active.ForServer.Length > 0)
            {
                // Already active: it is the player's own now.
                var a = entry.Active.Clone();
                a.ForServer = "";
                a.ForServerName = "";
                Engine.NoteActive(modId, a);
            }

            return ModVersionResult.Refuse(ModVersionCode.NothingToDo, modId + " " + want + " is not kept (it may be the active copy).");
        }

        return Swap(modId, want, "", "", midJoin: midJoin);
    }

    /// <summary>
    /// At Core start (2026-10-05, server distribution removed): delete every kept copy a server sent
    /// (<see cref="ModVersionSource.Server"/>, not the player's own), so "Use kept" can never swap server-sent bytes
    /// back in. Copies from Thunderstore and the player's own are untouched. One line per copy deleted or left.
    /// </summary>
    public List<string> ForgetServerCopies()
    {
        var lines = new List<string>();
        if (Engine.JournalPending || Engine.AwaitingMod.Length > 0)
        {
            // A swap is still being finished: try again once it settles (ForgetServerCopiesIfDue, before each join's
            // swap plan). Until then every pick skips these copies anyway (ParkedVersion.ServerSent).
            ForgetServerCopiesDue = true;
            return lines;
        }

        ForgetServerCopiesDue = false;
        var targets = new List<(string Id, string Version)>();
        foreach (var e in Engine.Read().Mods)
            foreach (var p in e.Parked)
                if (p.ServerSent) targets.Add((e.Id, p.Version));
        foreach (var (id, version) in targets)
        {
            var r = Engine.Forget(id, version, force: false);
            lines.Add(id + " " + version + ": " + (r.Ok ? "the kept copy a server sent was deleted" : "the kept copy a server sent was left (" + r.Message + ")"));
        }

        return lines;
    }

    /// <summary>True when <see cref="ForgetServerCopies"/> was skipped because a swap was still being finished.</summary>
    public bool ForgetServerCopiesDue { get; private set; }

    /// <summary>Run <see cref="ForgetServerCopies"/> again when an earlier pass had to wait; empty otherwise.</summary>
    public List<string> ForgetServerCopiesIfDue() => ForgetServerCopiesDue ? ForgetServerCopies() : new List<string>();

    /// <summary>
    /// At Core start, after <see cref="ModVersionEngine.Reconcile"/>: a mod whose server copy was recorded as active
    /// but which has no copy on disk at all (the start removed the player's marked copy, then refused or lost the
    /// server's pending one) gets the player's preferred kept copy back, so the player is never left without the mod.
    /// </summary>
    public List<string> RepairMissingActive()
    {
        var lines = new List<string>();
        if (Engine.JournalPending || Engine.AwaitingMod.Length > 0) return lines;
        var m = Engine.Read();
        foreach (var e in m.Mods)
        {
            if (e.Active == null || e.Active.ForServer.Length == 0 || e.Pending != null) continue;
            IReadOnlyList<ModCopy> copies;
            try { copies = Runtime.CopiesOf(e.Id) ?? Array.Empty<ModCopy>(); }
            catch { continue; }
            if (copies.Count > 0) continue;
            string want = ModVersionSelect.PreferredOf(e);
            if (want.Length == 0 || e.FindUsable(want) == null)
            {
                lines.Add(e.Id + ": the server's copy is not installed and no preferred version is kept");
                continue;
            }

            var r = Swap(e.Id, want, "", "", nowOnly: false);
            lines.Add(e.Id + ": the server's copy is not installed; " + (r.Ok ? "your " + want + " was put back" : "your " + want + " could not be put back (" + r.Message + ")"));
            if (Engine.AwaitingMod.Length > 0) break;
        }

        return lines;
    }

    /// <summary>
    /// After leaving a server (no world): every mod whose active copy was placed for a server gets the player's
    /// preferred version back — now when the reloader can swap it, else queued for the next start. A mod with nothing
    /// to restore is simply marked as the player's.
    /// </summary>
    public List<string> RestoreAfterLeave() => RestoreAfterLeave(out _);

    /// <summary>
    /// The same, one swap at a time: <paramref name="more"/> when a swap waits for its displaced plugin to unload (the
    /// caller runs this again once it is placed).
    /// </summary>
    public List<string> RestoreAfterLeave(out bool more)
    {
        more = false;
        var lines = new List<string>();
        if (Engine.AwaitingMod.Length > 0)
        {
            more = true;
            return lines;
        }

        var m = Engine.Read();
        foreach (var e in m.Mods)
        {
            if (e.Active == null || e.Active.ForServer.Length == 0) continue;
            if (e.Pending != null) continue;
            // The copy placed for the server is not active yet (its install, or the marked copy it replaces, waits
            // for the next start): restore after that start, never by forgetting it was placed for a server
            // (review finding 4).
            ActiveCopy(e.Id, out var refusal, out _);
            if (refusal is { Code: ModVersionCode.Queued })
            {
                lines.Add(e.Id + ": waits for the next start");
                continue;
            }

            if (HasPendingCopy(e.Id))
            {
                lines.Add(e.Id + ": waits for the next start (a copy is in progenitor-pending)");
                continue;
            }

            string? loaded = null;
            try { loaded = Runtime.LoadedVersion(e.Id); }
            catch { loaded = null; }
            var target = ModVersionSelect.RestoreTarget(e, loaded ?? e.Active.Version);
            if (target == null)
            {
                var a = e.Active.Clone();
                a.ForServer = "";
                a.ForServerName = "";
                Engine.NoteActive(e.Id, a);
                lines.Add(e.Id + ": nothing to restore");
                continue;
            }

            var r = Swap(e.Id, target.Version, "", "");
            lines.Add(e.Id + " " + target.Version + ": " + (r.Ok ? (r.AwaitingPlace ? "now (placing)" : r.Route) : ModVersionResult.CodeName(r.Code))
                + " (" + r.Message + ")");
            if (r.Ok && r.AwaitingPlace)
            {
                more = true;
                break;
            }
        }

        return lines;
    }

    private bool HasPendingCopy(string modId)
    {
        try
        {
            foreach (var c in Runtime.CopiesOf(modId)) if (c.Folder == "pending") return true;
        }
        catch { /* none known */ }

        return false;
    }

    /// <summary>The swaps a join needs: for every kept mod the server asked about, the kept version its check accepts.</summary>
    public List<JoinSwapPlan> PlanForJoin(IReadOnlyList<Handshake.ModRecord> asked, IReadOnlyList<Handshake.ModRecord> provided,
        Func<string, bool>? paused = null)
    {
        var plans = new List<JoinSwapPlan>();
        var m = Engine.Read();
        foreach (var e in m.Mods)
        {
            // A mod the per-server pause holds is not in this client's answer; the pause resumes it for a server that
            // asks for it, so it is not "failing" here (review finding 6).
            bool held;
            try { held = paused?.Invoke(e.Id) ?? false; }
            catch { held = false; }
            if (held) continue;
            var pick = ModVersionSelect.PickFor(e.Id, asked, provided, e, ModVersionSelect.CurrentRanges());
            if (pick == null) continue;
            var active = ActiveCopy(e.Id, out var refusal, out var others);
            ModVersionDecision d = refusal ?? ModVersionRoutes.Decide(Facts(e.Id, others, forceRestart: false, midJoin: false));
            if (d.Route != ModVersionRoute.Refused)
            {
                string? why = KeptCopyProblem(e.Id, pick);
                if (why != null) d = new ModVersionDecision(ModVersionRoute.Refused, why, ModVersionCode.HashMismatch);
            }

            plans.Add(new JoinSwapPlan { Id = e.Id, Version = pick.Version, Decision = d });
        }

        return plans;
    }

    /// <summary>Why a kept copy cannot be swapped in as it is on disk (its hash or the allowlist), or null.</summary>
    public string? KeptCopyProblem(string modId, ParkedVersion p)
    {
        try
        {
            string file = Engine.Env.Paths.StoredFile(modId, p);
            string? sha = ModVersionFiles.ShaOf(file);
            if (sha == null || sha.Length == 0) return "The kept copy of " + modId + " " + p.Version + " is gone from " + ModVersionPaths.FolderName + ".";
            if (!string.Equals(sha, p.Sha256, StringComparison.OrdinalIgnoreCase)) return ModVersionText.HashMismatch(modId, p.Version, p.Sha256, sha);
            string? allow = Engine.Env.Allow(p.File, sha);
            return allow == null ? null : ModVersionText.Allowlist(modId, p.Version, p.File, allow);
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>Where the active copy came from, and whether it is the player's own.</summary>
    private (ModVersionSource Source, string Detail, bool User) Provenance(string modId, ModCopy active)
    {
        var entry = Engine.Read().Find(modId);
        if (entry?.Active != null && entry.Active.ForServer.Length > 0
            && (entry.Active.File.Length == 0 || string.Equals(entry.Active.File, active.Name, StringComparison.OrdinalIgnoreCase)))
            return (entry.Active.Source, entry.Active.ForServer, false);
        string? host = null;
        try { host = Runtime.ServerRecordHost(active.Name); }
        catch { host = null; }
        if (!string.IsNullOrEmpty(host)) return (ModVersionSource.Server, host!, false);
        return (ModVersionSource.User, "", true);
    }

    private string VersionOf(string modId, ModCopy active)
    {
        string? loaded = null;
        try { loaded = Runtime.LoadedVersion(modId); }
        catch { loaded = null; }
        string v = !string.IsNullOrEmpty(loaded) && loaded != "?" ? loaded! : active.Version;
        return string.IsNullOrEmpty(v) ? "unknown" : v;
    }

    private static bool Safe(Func<bool> f)
    {
        try { return f(); }
        catch { return true; }
    }

    private static int SafeInt(Func<int> f)
    {
        try { return f(); }
        catch { return 1; }
    }
}
