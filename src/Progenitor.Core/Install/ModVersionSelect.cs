using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Progenitor.Plugins;

namespace Progenitor.Install;

/// <summary>
/// JOINFLOW4: which copy a server needs. The verdict is the handshake's own: <see cref="Handshake.Compare.Evaluate(IReadOnlyList{Handshake.ModRecord}, IReadOnlyList{Handshake.ModRecord}, out List{string})"/>
/// on the server's request against this client's answer with the candidate version in place of the active one, so a
/// copy is picked exactly when the join check would accept it (<c>Exact</c>, <c>Minor</c> or <c>None</c>, whichever the
/// server's row says).
/// </summary>
internal static class ModVersionSelect
{
    /// <summary>The join check accepts this client's answer for <paramref name="id"/>.</summary>
    public static bool Passes(string id, IReadOnlyList<Handshake.ModRecord> asked, IReadOnlyList<Handshake.ModRecord> provided)
    {
        Handshake.Compare.Evaluate(asked, provided, out var failed);
        foreach (var f in failed) if (string.Equals(f, id, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// This client's answer with <paramref name="id"/> at <paramref name="version"/>: the active row's level, rule and
    /// declared flag are kept (a copy declares what the mod declares); with no active row, the parked copy's own
    /// level and rule when it recorded them, else the server row's, undeclared.
    /// </summary>
    public static List<Handshake.ModRecord> WithVersion(
        IReadOnlyList<Handshake.ModRecord> provided, string id, string version, IReadOnlyList<Handshake.ModRecord> asked, ParkedVersion? parked = null)
    {
        var list = new List<Handshake.ModRecord>(provided.Count + 1);
        bool replaced = false;
        foreach (var r in provided)
        {
            if (string.Equals(r.Guid, id, StringComparison.OrdinalIgnoreCase))
            {
                if (!replaced) list.Add(new Handshake.ModRecord(r.Guid, version, r.Level, r.VersionRule, r.Declared));
                replaced = true;
                continue;
            }

            list.Add(r);
        }

        if (!replaced)
        {
            Handshake.ModRecord? serverRow = null;
            foreach (var a in asked) if (string.Equals(a.Guid, id, StringComparison.OrdinalIgnoreCase)) serverRow = a;
            var level = serverRow?.Level ?? NetworkCompatibilityLevel.ServerMustHave;
            var rule = serverRow?.VersionRule ?? NetworkVersionRule.Exact;
            bool declared = false;
            if (parked != null && Enum.TryParse<NetworkCompatibilityLevel>(parked.Level, out var l) && Enum.TryParse<NetworkVersionRule>(parked.Rule, out var ru))
            {
                level = l;
                rule = ru;
                declared = true;
            }

            list.Add(new Handshake.ModRecord(id, version, level, rule, declared));
        }

        return list;
    }

    /// <summary>
    /// The kept copy the server needs, when the active copy fails its check and one of the kept ones passes: the
    /// player's preferred version first, then the newest. Null when the active copy passes or nothing kept does.
    /// Mod compatibility C3: with <paramref name="ranges"/>, a copy's version is judged as the server would judge it
    /// (<see cref="ServerVersionCheck"/>: the server's match, else the newer side's range, else the request rule), so a
    /// newer copy whose own range includes the server's version is not swapped out. Without it, today's check.
    /// </summary>
    public static ParkedVersion? PickFor(
        string id, IReadOnlyList<Handshake.ModRecord> asked, IReadOnlyList<Handshake.ModRecord> provided, ModVersionEntry? entry,
        KeptCopyRanges? ranges = null)
    {
        if (entry == null || entry.Parked.Count == 0) return null;
        Handshake.ModRecord? serverRow = null;
        foreach (var a in asked)
            if (string.Equals(a.Guid, id, StringComparison.OrdinalIgnoreCase)) { serverRow = a; break; }
        if (serverRow == null) return null;
        var req = ranges?.RequirementFor(serverRow.Value);
        string? active = null;
        foreach (var r in provided)
            if (string.Equals(r.Guid, id, StringComparison.OrdinalIgnoreCase)) { active = r.Version; break; }

        bool activePasses = req != null && active != null
            ? ServerVersionCheck.Accepts(req, active, ranges!.RangeOf(id, active, null))
            : Passes(id, asked, provided);
        if (activePasses) return null;
        foreach (var p in Candidates(entry))
        {
            bool ok = req != null
                ? ServerVersionCheck.Accepts(req, p.Version, ranges!.RangeOf(id, p.Version, p.SourceDetail))
                : Passes(id, asked, WithVersion(provided, id, p.Version, asked, p));
            if (ok) return p;
        }

        return null;
    }

    /// <summary>C3: the live range input for <see cref="PickFor"/> (set by the pre-join gate on a client); null in tests and on a server.</summary>
    internal static Func<KeptCopyRanges?>? RangesSource { get; set; }

    /// <summary>C3: <see cref="RangesSource"/> read safely; null keeps today's check.</summary>
    internal static KeptCopyRanges? CurrentRanges()
    {
        try { return RangesSource?.Invoke(); }
        catch { return null; }
    }

    /// <summary>Kept copies in the order a pick tries them: the preferred version, then newest first.</summary>
    public static List<ParkedVersion> Candidates(ModVersionEntry entry)
    {
        // 2026-10-05: copies a server sent are never candidates (servers no longer distribute mod files).
        var list = entry.Parked.FindAll(p => !p.ServerSent);
        string preferred = PreferredOf(entry);
        list.Sort((a, b) =>
        {
            bool pa = string.Equals(a.Version, preferred, StringComparison.OrdinalIgnoreCase);
            bool pb = string.Equals(b.Version, preferred, StringComparison.OrdinalIgnoreCase);
            if (pa != pb) return pa ? -1 : 1;
            int c = InstalledState.CompareVersions(b.Version, a.Version);
            return c != 0 ? c : b.LastUsedUtc.CompareTo(a.LastUsedUtc);
        });
        return list;
    }

    /// <summary>
    /// The player's own version: the one they last chose (<see cref="ModVersionEntry.Preferred"/>), else the newest
    /// copy they installed themselves, else empty (nothing to restore).
    /// </summary>
    public static string PreferredOf(ModVersionEntry? entry)
    {
        if (entry == null) return "";
        if (entry.Preferred.Length > 0) return entry.Preferred;
        string best = "";
        foreach (var p in entry.Parked)
            if (p.UserInstalled && (best.Length == 0 || InstalledState.CompareVersions(p.Version, best) > 0)) best = p.Version;
        return best;
    }

    /// <summary>
    /// After leaving a server: the kept copy to swap back in, or null. Only a mod whose active copy was placed for a
    /// server (<see cref="ActiveVersion.ForServer"/>) is restored, and only when the preferred copy is kept and is not
    /// the active one.
    /// </summary>
    public static ParkedVersion? RestoreTarget(ModVersionEntry? entry, string? activeVersion)
    {
        if (entry?.Active == null || entry.Active.ForServer.Length == 0 || entry.Pending != null) return null;
        string preferred = PreferredOf(entry);
        if (preferred.Length == 0) return null;
        if (string.Equals(preferred, activeVersion ?? entry.Active.Version, StringComparison.OrdinalIgnoreCase)) return null;
        return entry.FindUsable(preferred);
    }
}

/// <summary>
/// Mod compatibility C3: what the kept-copy choice knows besides the request rows: the server's Version matching (its
/// request bits), its remembered ranges and match per mod, and each copy's own effective range. Plain values and
/// callbacks; the live client builds it, the tests build it by hand.
/// </summary>
internal sealed class KeptCopyRanges
{
    /// <summary>The server-wide Version matching the request announced.</summary>
    public VersionMatchingMode Mode { get; set; } = VersionMatchingMode.Range;

    /// <summary>The server's remembered facts for a mod id (its catalog's peers and match), or null.</summary>
    public Func<string, ServerRequirement?>? Server { get; set; }

    /// <summary>
    /// A copy's own effective peer range: (mod id, version, the parked copy's source detail or null for the active
    /// copy) to the range, or null when not known.
    /// </summary>
    public Func<string, string, string?, string?>? CopyRange { get; set; }

    /// <summary>The server's requirement for <paramref name="row"/>: its version and rule, the mode, and the remembered peers and match for that version.</summary>
    public ServerRequirement RequirementFor(Handshake.ModRecord row)
    {
        var req = new ServerRequirement
        {
            ModId = row.Guid,
            Version = row.Version ?? "",
            Rule = row.VersionRule,
            Matching = ServerRequirements.MatchingWord(Mode),
        };
        ServerRequirement? known = null;
        try { known = Server?.Invoke(row.Guid); }
        catch { known = null; }
        if (known != null && string.Equals(known.Version, req.Version, StringComparison.OrdinalIgnoreCase))
        {
            req.Peers = known.Peers ?? "";
            // Review fix: a remembered match only while this request judges the mod the same way (rule byte and mode);
            // otherwise an admin changed it since, and the fresh request decides.
            if (known.Rule == req.Rule && string.Equals(ServerVersionCheck.NormalizeMatch(known.Matching), req.Matching, StringComparison.Ordinal))
                req.Match = ServerVersionCheck.NormalizeMatch(known.Match);
        }

        return req;
    }

    /// <summary>A copy's range through <see cref="CopyRange"/>; null when not known.</summary>
    public string? RangeOf(string id, string version, string? sourceDetail)
    {
        try { return CopyRange?.Invoke(id, version, sourceDetail); }
        catch { return null; }
    }

    /// <summary>
    /// A Thunderstore copy's range from its verified inspection: <paramref name="sourceDetail"/> is the store's
    /// <c>Owner-Name@1.8.2</c>. Null for any other source or when that version was never inspected.
    /// </summary>
    public static string? FromVerified(VerifiedManifestCache? cache, string? sourceDetail, string version)
    {
        if (cache == null) return null;
        string detail = (sourceDetail ?? "").Trim();
        int at = detail.LastIndexOf('@');
        if (at <= 0) return null;
        string package = detail.Substring(0, at);
        string v = detail.Substring(at + 1);
        if (v.Length == 0) v = version;
        return cache.EffectivePeersOf(package, v);
    }
}

/// <summary>JOINFLOW4: how a swap may happen.</summary>
internal enum ModVersionRoute
{
    /// <summary>The reloader swaps it now (no world, or a reload-folder plugin it can unload).</summary>
    Now = 0,

    /// <summary>Queued in <c>progenitor-pending</c> for the next start.</summary>
    Restart = 1,

    /// <summary>Not swapped; <see cref="ModVersionDecision.Reason"/> says why.</summary>
    Refused = 2,
}

/// <summary>JOINFLOW4: the route and its reason.</summary>
internal readonly struct ModVersionDecision
{
    public ModVersionDecision(ModVersionRoute route, string reason, ModVersionCode code = ModVersionCode.Ok)
    {
        Route = route;
        Reason = reason ?? "";
        Code = code;
    }

    public ModVersionRoute Route { get; }
    public string Reason { get; }
    public ModVersionCode Code { get; }

    public string RouteName => Route switch
    {
        ModVersionRoute.Now => "now",
        ModVersionRoute.Restart => "restart",
        _ => "refused",
    };
}

/// <summary>JOINFLOW4: what decides the route (plain values from the live probes).</summary>
internal sealed class ModVersionRouteFacts
{
    /// <summary>The mod manager that installed the active copy (MANAGED1: "Managed elsewhere"), or null.</summary>
    public string? ManagedBy { get; set; }

    /// <summary>The active copy is loaded from <c>BepInEx/plugins</c> (the chainloader), not the reload folder.</summary>
    public bool ActiveInPlugins { get; set; }

    /// <summary>Other copies of the same plugin GUID beside the active one (reload or pending).</summary>
    public int OtherCopies { get; set; }

    public string OtherCopyNames { get; set; } = "";

    /// <summary>Copies waiting in <c>progenitor-pending</c> for the next start (an install that promotes then).</summary>
    public int PendingCopies { get; set; }

    public string PendingCopyNames { get; set; } = "";

    /// <summary>
    /// The reloader cannot unload the active plugin now (ctx.UnsafeHarmony, a retiring generation, an older
    /// Bootstrap): set through <see cref="UnsafeReason"/>, which carries the concrete cause.
    /// </summary>
    public bool UnsafeHarmony
    {
        get => UnsafeReason.Length > 0;
        set => UnsafeReason = value ? (UnsafeReason.Length > 0 ? UnsafeReason : ModVersionRouteText.UnsafeHarmony) : "";
    }

    /// <summary>Why the reloader cannot unload it now, or "".</summary>
    public string UnsafeReason { get; set; } = "";

    /// <summary>Other reload plugins depend on it (unloading it takes them down too).</summary>
    public int Dependents { get; set; }

    /// <summary>Their names, or "".</summary>
    public string DependentNames { get; set; } = "";

    /// <summary>A Core swap or plugin reload is in flight.</summary>
    public bool LoaderBusy { get; set; }

    public bool JournalBusy { get; set; }

    /// <summary>A swap of this mod is already queued for the next start.</summary>
    public string QueuedVersion { get; set; } = "";

    /// <summary>A join is in progress (between connect and the handshake answer) and this is not the handshake's own swap.</summary>
    public bool MidJoin { get; set; }

    /// <summary>The caller asked for the restart route.</summary>
    public bool ForceRestart { get; set; }
}

/// <summary>
/// JOINFLOW4: the swap route. Refused for another tool's copy, a copy loaded from <c>BepInEx/plugins</c>, two copies of
/// one plugin GUID, a swap already queued, and mid-join; the restart route for a plugin the reloader cannot unload
/// safely (<c>ctx.UnsafeHarmony</c>, dependents, or when asked); otherwise now. The hash and the allowlist are checked by
/// the engine on the bytes it is about to place.
/// </summary>
internal static class ModVersionRoutes
{
    public static ModVersionDecision Decide(ModVersionRouteFacts f)
    {
        if (f == null) return new ModVersionDecision(ModVersionRoute.Refused, "no facts", ModVersionCode.Failed);
        if (!string.IsNullOrEmpty(f.ManagedBy))
            return new ModVersionDecision(ModVersionRoute.Refused, ModVersionRouteText.ManagedElsewhere(f.ManagedBy!), ModVersionCode.ManagedElsewhere);
        if (f.ActiveInPlugins)
            return new ModVersionDecision(ModVersionRoute.Refused, ModVersionRouteText.InPlugins, ModVersionCode.ManagedElsewhere);
        if (f.JournalBusy)
            return new ModVersionDecision(ModVersionRoute.Refused, "Another version swap is still being finished.", ModVersionCode.Busy);
        if (f.QueuedVersion.Length > 0)
            return new ModVersionDecision(ModVersionRoute.Refused, f.QueuedVersion + " is already queued for the next start; restart V Rising first.", ModVersionCode.Queued);
        if (f.OtherCopies > 0)
            return new ModVersionDecision(ModVersionRoute.Refused, ModVersionRouteText.TwoCopies(f.OtherCopyNames), ModVersionCode.TwoCopies);
        if (f.PendingCopies > 0)
            return new ModVersionDecision(ModVersionRoute.Refused, ModVersionRouteText.PendingCopy(f.PendingCopyNames), ModVersionCode.Queued);
        if (f.MidJoin)
            return new ModVersionDecision(ModVersionRoute.Refused, "A join is in progress; versions are swapped when the server asks, or after you leave.", ModVersionCode.Busy);
        if (f.LoaderBusy)
            return new ModVersionDecision(ModVersionRoute.Refused, "The loader is busy (a Core swap); try again in a moment.", ModVersionCode.Busy);
        if (f.ForceRestart) return new ModVersionDecision(ModVersionRoute.Restart, "asked for the restart route");
        if (f.UnsafeReason.Length > 0) return new ModVersionDecision(ModVersionRoute.Restart, f.UnsafeReason);
        if (f.Dependents > 0)
            return new ModVersionDecision(ModVersionRoute.Restart,
                "other mods depend on it" + (f.DependentNames.Length > 0 ? " (" + f.DependentNames + ")" : "") + ", and unloading it would unload them too");
        return new ModVersionDecision(ModVersionRoute.Now, "");
    }
}

/// <summary>JOINFLOW4: route sentences. US English.</summary>
internal static class ModVersionRouteText
{
    public const string UnsafeHarmony = "it uses ctx.UnsafeHarmony, which the reloader cannot unload safely";

    /// <summary>FINJ1: the plugin registered its own IL2CPP types; the reloader keeps it until the game restarts.</summary>
    public const string ProcessLifetime = "it registers its own IL2CPP types, which last until the game restarts, so the reloader cannot unload it";

    public const string InPlugins = "It is loaded from BepInEx/plugins (managed elsewhere), so Progenitor does not keep or swap its versions.";

    public static string ManagedElsewhere(string manager) =>
        "Managed elsewhere: installed by " + manager + ", so Progenitor does not keep or swap its versions.";

    public static string PendingCopy(string names) =>
        "A copy of it waits for the next start of V Rising (" + names + "); its versions are swapped after that start.";

    public static string TwoCopies(string names) =>
        "Two copies of it are on disk (" + names + "); remove one first. Progenitor never loads two versions of one mod.";
}
