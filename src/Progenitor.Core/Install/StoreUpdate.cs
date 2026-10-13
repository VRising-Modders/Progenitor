using System;
using System.Collections.Generic;
using System.IO;

namespace Progenitor.Install;

/// <summary>AUTOUPDATE2: one update the version store applies (the verified bytes of the new plugin image).</summary>
internal sealed class StoreUpdateRequest
{
    /// <summary>The listing key (<c>Owner-Name</c>).</summary>
    public string Package { get; init; } = "";

    /// <summary>The listing version written (the store keeps the copy under this version).</summary>
    public string Version { get; init; } = "";

    public string PluginId { get; init; } = "";

    public byte[] Bytes { get; init; } = Array.Empty<byte>();

    /// <summary>The image is admitted for a hot load apart from the old copy being loaded (the store's route then decides).</summary>
    public bool HotImage { get; init; }

    /// <summary>Why the image is not hot (the admission's reason), for the result line.</summary>
    public string NotHotReason { get; init; } = "";

    /// <summary>The ledger row the copy carries while it is active (path and sha are set when it is placed).</summary>
    public ParkedLedgerRow? Ledger { get; init; }
}

/// <summary>AUTOUPDATE2: what the store did with an update.</summary>
internal sealed class StoreUpdateResult
{
    /// <summary>The store does not hold this mod's active copy (another route writes it): nothing was done.</summary>
    public bool NotApplicable { get; init; }

    public bool Ok { get; init; }

    /// <summary><c>now</c> (the reloader swaps it within seconds) or <c>restart</c> (the next start).</summary>
    public string Route { get; init; } = "";

    /// <summary>The file placed (or queued), absolute.</summary>
    public string Path { get; init; } = "";

    public string Sha256 { get; init; } = "";

    /// <summary>The version it replaced, kept in the store (empty when none was active).</summary>
    public string Previous { get; init; } = "";

    public string Message { get; init; } = "";

    /// <summary>What was superseded on the way (a pending copy), or empty.</summary>
    public string Superseded { get; init; } = "";

    /// <summary>A swap under another file name waits for the old copy to unload (the frame's pump places it).</summary>
    public bool AwaitingPlace { get; init; }

    /// <summary>Review H1: not written now (the apply window closed, or the copy is placed for a server): tried later, not a failure.</summary>
    public bool Deferred { get; init; }

    public static StoreUpdateResult Defer(string why) => new() { Deferred = true, Message = why };

    public static StoreUpdateResult Skip(string why) => new() { NotApplicable = true, Message = why };

    public static StoreUpdateResult Fail(string why) => new() { Ok = false, Message = why };
}

/// <summary>
/// AUTOUPDATE2: updates through the version store (JOINFLOW4). The new image is kept as a copy, then swapped in like any
/// kept copy: the active copy is parked first (journal, read back), then the new one is written under the same file name,
/// so the reloader unloads and loads it in one scan (now), or it is queued in <c>progenitor-pending</c> with the old copy
/// marked (restart). A queued update is superseded by a newer one (phase 1 open issue 1); a copy another install left in
/// <c>progenitor-pending</c> for this package is superseded when the install ledger shows Progenitor wrote it.
/// Game-free: the store is behind <see cref="ModVersionService"/>.
/// </summary>
internal static class StoreUpdate
{
    /// <summary>
    /// Removes a pending copy another install wrote for this package (absolute path): true with what it did, false with
    /// why it may not (not Progenitor's, other bytes, another package). Live: <see cref="PendingSupersede.TryRemove"/>.
    /// </summary>
    internal delegate bool RemovePending(string package, string path, out string message);

    public static StoreUpdateResult Apply(ModVersionService? svc, StoreUpdateRequest req, RemovePending? removePending)
    {
        if (svc == null) return StoreUpdateResult.Skip("the version store is not available");
        if (req == null || req.PluginId.Length == 0) return StoreUpdateResult.Skip("the plugin id is not known");
        if (req.Bytes.Length == 0) return StoreUpdateResult.Fail("no bytes to write");
        string id = req.PluginId;
        if (svc.Engine.JournalPending || svc.Engine.AwaitingMod.Length > 0)
            return StoreUpdateResult.Fail("another version swap is still being finished");

        var entry = svc.Manifest().Find(id);
        var active = svc.ActiveCopy(id, out var refusal, out var others);
        bool queued = entry?.Pending != null;
        if (refusal is { } r)
        {
            if (r.Code == ModVersionCode.ManagedElsewhere) return StoreUpdateResult.Skip(r.Reason);
            if (!(r.Code == ModVersionCode.Queued && queued)) return StoreUpdateResult.Fail(r.Reason);
        }

        if (active == null && !queued) return StoreUpdateResult.Skip("no copy of it is in progenitor-reload");

        // A copy placed for a server is the server's version until the player's own comes back after leaving: never updated
        // while placed (design, "Connected to a server that needs another version").
        if (entry?.Active is { ForServer.Length: > 0 } placed)
            return StoreUpdateResult.Defer("the copy in use was placed for " + (placed.ForServerName.Length > 0 ? placed.ForServerName : placed.ForServer)
                + "; it is updated after your own version comes back");

        // Phase 1 open issue 1: a copy another install queued for this package is superseded, never refused.
        string queuedPath = queued ? Path.GetFullPath(svc.Engine.Env.Paths.Absolute(entry!.Pending!.PendingFile)) : "";
        var notes = new List<string>();
        foreach (var o in others)
        {
            if (o.Folder != "pending") continue;
            if (queued && string.Equals(Path.GetFullPath(o.Path), queuedPath, StringComparison.OrdinalIgnoreCase)) continue;
            string message = "";
            bool removed = false;
            try { removed = removePending != null && removePending(req.Package, o.Path, out message); }
            catch (Exception e) { message = e.Message; }
            if (!removed)
                return StoreUpdateResult.Fail(ModVersionRouteText.PendingCopy("progenitor-pending/" + o.Name)
                    + (message.Length > 0 ? " (" + message + ")" : ""));
            notes.Add(message.Length > 0 ? message : "superseded progenitor-pending/" + o.Name);
        }

        string fileName = active?.Name ?? entry!.Pending!.File;
        bool existed = entry?.FindUsable(req.Version) != null;
        var added = svc.Engine.AddCopy(id, req.Version, fileName, req.Bytes, req.Ledger, ModVersionSource.Thunderstore, req.Package + "@" + req.Version);
        if (!added.Ok) return StoreUpdateResult.Fail(added.Message);
        string sha = ModVersionFiles.Sha256Hex(req.Bytes);

        ModVersionResult result;
        string previous;
        if (queued)
        {
            // The queued swap is superseded: the newer copy loads at the next start instead.
            previous = entry!.Active?.Version ?? "";
            notes.Add("superseded the queued " + entry.Pending!.Version);
            result = svc.Engine.Requeue(id, req.Version);
        }
        else
        {
            previous = PreviousOf(svc, id, active!);
            result = svc.Swap(id, req.Version, "", "", forceRestart: !req.HotImage);
        }

        if (!result.Ok)
        {
            if (!existed) svc.Engine.Forget(id, req.Version, force: true);
            return StoreUpdateResult.Fail(result.Message);
        }

        var paths = svc.Engine.Env.Paths;
        string folder = result.Route == "restart" ? paths.Pending : paths.Reload;
        return new StoreUpdateResult
        {
            Ok = true,
            Route = result.Route == "restart" ? "restart" : "now",
            Path = Path.Combine(folder, fileName),
            Sha256 = sha,
            Previous = previous,
            Message = result.Message + (result.Route == "restart" && !req.HotImage && req.NotHotReason.Length > 0 ? " (" + req.NotHotReason + ")" : ""),
            Superseded = string.Join("; ", notes),
            AwaitingPlace = result.AwaitingPlace,
        };
    }

    private static string PreviousOf(ModVersionService svc, string id, ModCopy active)
    {
        string? loaded = null;
        try { loaded = svc.Runtime.LoadedVersion(id); }
        catch { loaded = null; }
        string v = !string.IsNullOrEmpty(loaded) && loaded != "?" ? loaded! : active.Version;
        return string.IsNullOrEmpty(v) ? "unknown" : v;
    }

    /// <summary>
    /// The copy <b>Roll back</b> returns to: the most recently kept copy of the player's own (not one a server sent) that is
    /// older than the active version and not the queued one. Null when there is none.
    /// </summary>
    public static ParkedVersion? RollbackTarget(ModVersionEntry? entry)
    {
        if (entry == null) return null;
        string active = entry.Active?.Version ?? "";
        string pending = entry.Pending?.Version ?? "";
        ParkedVersion? best = null;
        foreach (var p in entry.Parked)
        {
            if (p.ServerSent) continue;
            if (string.Equals(p.Version, active, StringComparison.OrdinalIgnoreCase)) continue;
            if (pending.Length > 0 && string.Equals(p.Version, pending, StringComparison.OrdinalIgnoreCase)) continue;
            // Back means older: a newer kept copy (after a roll back) is the update, offered by Updates, not by Roll back.
            if (active.Length > 0 && UpdateVersions.Compare(p.Version, active) > 0) continue;
            if (best == null || p.ParkedUtc > best.ParkedUtc) best = p;
        }

        return best;
    }

    /// <summary>
    /// Put <paramref name="version"/> (a kept copy) back: now when the reloader can, else at the next start. While an update
    /// waits for the next start, that queued copy is superseded by this one. <paramref name="forget"/>: delete the kept
    /// copy of that version afterward (a version that failed to load).
    /// </summary>
    public static ModVersionResult RollBack(ModVersionService? svc, string pluginId, string version, string forget = "")
    {
        if (svc == null) return ModVersionResult.Refuse(ModVersionCode.NotFound, "Keeping versions is not available here.");
        var entry = svc.Manifest().Find(pluginId);
        if (entry?.FindUsable(version) == null) return ModVersionResult.Refuse(ModVersionCode.NotFound, "No kept copy of " + pluginId + " " + version + ".");
        ModVersionResult r = entry.Pending != null ? svc.Engine.Requeue(pluginId, version) : svc.Swap(pluginId, version, "", "");
        if (r.Ok && forget.Length > 0 && !string.Equals(forget, version, StringComparison.OrdinalIgnoreCase) && !r.AwaitingPlace)
            svc.Engine.Forget(pluginId, forget, force: true);
        return r;
    }
}

/// <summary>
/// AUTOUPDATE2 (phase 1 open issue 1): a copy waiting in <c>progenitor-pending</c> that Progenitor's install ledger shows it
/// wrote for the same package (a pending row with this path and these bytes) may be replaced by a newer update. Anything
/// else in the way is refused, as before.
/// </summary>
internal static class PendingSupersede
{
    /// <summary>The ledger row of <paramref name="path"/> when it is this package's pending copy with the ledgered bytes, else null with why.</summary>
    public static ServerInstallLedgerEntry? OwnRow(string gameRoot, string package, string path, out string why)
    {
        why = "";
        ServerInstallLedger? ledger;
        try { ledger = ServerInstallLedger.TryRead(gameRoot); }
        catch { ledger = null; }
        if (ledger == null) { why = "no install ledger"; return null; }
        string rel;
        try { rel = ServerInstallLedger.Relative(gameRoot, Path.GetFullPath(path)); }
        catch { why = "the path is not under the game folder"; return null; }
        foreach (var row in ledger.Files)
        {
            if (!string.Equals(row.Path, rel, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(row.Category, ServerInstallLedger.CategoryPending, StringComparison.Ordinal)) { why = "it is not a pending install"; return null; }
            if (!InstalledState.TrySplitPackageSource(row.Source, out string p, out _) || !string.Equals(p, package, StringComparison.OrdinalIgnoreCase))
            {
                why = "it was installed for another package";
                return null;
            }

            string? sha = ClientUpdateInputs.Sha256Of(path);
            if (sha == null || !string.Equals(sha, row.Sha256, StringComparison.OrdinalIgnoreCase)) { why = "its bytes changed after Progenitor wrote it"; return null; }
            return row;
        }

        why = "Progenitor's install ledger does not name it";
        return null;
    }

    /// <summary>Delete this package's pending copy and its ledger row (see <see cref="OwnRow"/>).</summary>
    public static bool TryRemove(string gameRoot, string package, string path, out string message)
    {
        var row = OwnRow(gameRoot, package, path, out string why);
        if (row == null) { message = why; return false; }
        try
        {
            File.Delete(path);
            if (File.Exists(path)) { message = "it could not be deleted"; return false; }
            var ledger = ServerInstallLedger.TryRead(gameRoot);
            if (ledger != null)
            {
                var keep = new List<ServerInstallLedgerEntry>();
                foreach (var f in ledger.Files) if (!string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase)) keep.Add(f);
                new ServerInstallLedger(ledger.Version, ledger.BootstrapSha256, ledger.CoreSha256, ledger.InstalledUtc, keep).Write(gameRoot);
            }

            message = "superseded the pending " + row.Source.Replace("package:", "") + " (" + Path.GetFileName(path) + ")";
            return true;
        }
        catch (Exception e)
        {
            message = e.Message;
            return false;
        }
    }
}
