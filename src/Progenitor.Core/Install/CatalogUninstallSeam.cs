using System;
using System.Collections.Generic;
using Progenitor.Ui;

namespace Progenitor.Install;

/// <summary>
/// INSTALL2: the implementation of MODSPAGE1's <see cref="IModUninstallSeam"/>. The mod page knows a
/// plugin id; the ledger, the markers and the dependency check are all keyed by a Thunderstore
/// package. This maps one to the other through the catalog index — the manifest id first, then the
/// plugin GUID a prior inspection cached — and then calls the same service methods the Install tab
/// calls, so both tabs agree about what is installed and what is pending.
///
/// <para>Nothing here claims a state it did not reach: a plugin the index does not carry answers
/// "not ours" (<see cref="ServerInstallResult.Refuse"/> with <see cref="NotOurs"/>), because the only
/// files Progenitor may delete are the ones its own ledger names.</para>
/// </summary>
internal sealed class CatalogUninstallSeam : IModUninstallSeam
{
    /// <summary>The refusal for a plugin no catalog listing accounts for.</summary>
    public const string NotOurs = "Progenitor did not install this mod, so it cannot remove it.";

    private readonly InstallCatalogService _service;

    public CatalogUninstallSeam(InstallCatalogService service) =>
        _service = service ?? throw new ArgumentNullException(nameof(service));

    /// <summary>SRVUNINST1: the catalog resolves this plugin to a listing, so the ledger calls below can act on it.</summary>
    public bool Owns(string pluginId) => Resolve(pluginId) != null || LedgerRows(pluginId).Count > 0;

    /// <summary>
    /// F27 (live 2026-10-07): the install-ledger rows (both roots) that name a plugin (live: the Mods menu's ledger read).
    /// A plugin whose listing left the index is still acted on from these alone. Null: none.
    /// </summary>
    internal Func<string, IReadOnlyList<InstalledLedgerRow>>? LedgerRowsProbe { get; set; }

    private IReadOnlyList<InstalledLedgerRow> LedgerRows(string pluginId)
    {
        try { return LedgerRowsProbe?.Invoke(pluginId) ?? Array.Empty<InstalledLedgerRow>(); }
        catch { return Array.Empty<InstalledLedgerRow>(); }
    }

    private static IReadOnlyList<InstalledLedgerRow> OnRoot(IReadOnlyList<InstalledLedgerRow> rows, bool client, bool server)
    {
        var list = new List<InstalledLedgerRow>();
        foreach (var r in rows)
        {
            bool isServer = string.Equals(r.Root, InstalledState.ServerRoot, StringComparison.OrdinalIgnoreCase);
            if (isServer ? server : client) list.Add(r);
        }

        return list;
    }

    public bool IsUninstallPending(string pluginId)
    {
        var entry = Resolve(pluginId);
        if (entry == null)
        {
            try { return _service.LedgerRowsPending(LedgerRows(pluginId)); }
            catch { return false; }
        }
        try { return _service.IsUninstallPending(entry.Key); }
        catch { return false; }
    }

    /// <summary>
    /// A copy <em>this process</em> hot loaded is unloaded now — that is today's verified behavior and
    /// it owes no restart — so it takes the plain ledger-scoped removal. Everything else is loaded and
    /// cannot be dropped in process, so it is marked instead and stays live until the next start.
    ///
    /// <para>INSTALL2 defect A: <paramref name="hot"/> is the page's own reading and is only ever
    /// <em>narrowed</em> here, never trusted on its own. The page derives it from the reloader's loaded
    /// list, and the reloader loads every DLL in <c>progenitor-reload/</c> — including a copy Bootstrap
    /// promoted at start, which this process did not hot load and cannot hot unload. The authority is
    /// the <c>hot</c> ledger flag the executor writes and the next start clears, so an ordinary
    /// promoted mod is marked here even when the caller said hot.</para>
    /// </summary>
    public ServerInstallResult MarkUninstall(string pluginId, bool hot)
    {
        var entry = Resolve(pluginId);
        if (entry == null) return FromLedger(pluginId, LedgerAct.Uninstall, client: true, server: true);
        return UnloadsNow(entry, hot) ? _service.RemovePackage(entry.Key) : _service.MarkUninstall(entry.Key);
    }

    /// <summary>
    /// SRVUNINST1 (live 2026-10-07: a join-installed HotDemo, hot loaded, read "Unloads now" and was marked for the next
    /// start): the exact decision <see cref="MarkUninstall"/> makes, for the confirmation's words. Unloaded now when the
    /// reloader has this copy (<paramref name="hot"/>, the page's reading) and either the ledger's <c>hot</c> flag says this
    /// process hot loaded it, or every ledgered copy in this client's <c>progenitor-reload</c> is the trusted hotLoadable
    /// image an inspection verified (HOTDEMO1, the Install tab's and the server's rule): a join-window install sets no flag,
    /// and a Core swap reloads the copy the same way. A copy Bootstrap promoted at start that is not such an image is
    /// still marked.
    /// </summary>
    public bool UnloadsNow(string pluginId, bool hot)
    {
        var entry = Resolve(pluginId);
        return entry != null && UnloadsNow(entry, hot);
    }

    private bool UnloadsNow(CatalogEntry entry, bool hot)
    {
        if (!hot) return false;
        if (IsHotOnDisk(entry)) return true;
        try
        {
            var files = _service.ClientReloadFilesOf(entry);
            if (files.Count == 0) return false;
            foreach (string f in files)
                if (!_service.TrustsHotUnload(entry, f, out _)) return false;
            return true;
        }
        catch { return false; }
    }

    private bool IsHotOnDisk(CatalogEntry entry)
    {
        try
        {
            foreach (string package in _service.HotLoadedPackages())
            {
                if (string.Equals(package, entry.Key, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }
        catch { return false; }
    }

    /// <summary>F10: the local server folder's copy goes now when that server's reloader holds a trusted hot-unloadable image.</summary>
    public bool ServerUnloadsNow(string pluginId)
    {
        var entry = Resolve(pluginId);
        if (entry == null) return false;
        try { return _service.ServerUnloadsNow(entry, out _); }
        catch { return false; }
    }

    /// <summary>
    /// F4/F10: each named side by its own state: the client copy now (hot) or at the next start, the local server folder's
    /// copy now (its reloader holds it) or at that server's next restart. A side not named is not touched.
    /// </summary>
    public ServerInstallResult UninstallSides(string pluginId, bool hot, bool client, bool server)
    {
        var entry = Resolve(pluginId);
        if (entry == null) return FromLedger(pluginId, LedgerAct.Uninstall, client, server);
        if (!client && !server) return new ServerInstallResult(true, "nothing to remove", Array.Empty<string>(), Array.Empty<string>(), noOp: true);
        var results = new System.Collections.Generic.List<ServerInstallResult>(2);
        if (client)
            results.Add(UnloadsNow(entry, hot)
                ? _service.RemovePackage(entry.Key, force: false, InstallTarget.Client)
                : _service.MarkUninstall(entry.Key, force: false, InstallTarget.Client));
        if (server && (results.Count == 0 || results[0].Ok))
        {
            bool now;
            try { now = _service.ServerUnloadsNow(entry, out _); }
            catch { now = false; }
            results.Add(now
                ? _service.RemovePackage(entry.Key, force: false, InstallTarget.LocalServer)
                : _service.MarkUninstall(entry.Key, force: false, InstallTarget.LocalServer));
        }

        return Combine(results);
    }

    private static ServerInstallResult Combine(System.Collections.Generic.List<ServerInstallResult> results)
    {
        if (results.Count == 1) return results[0];
        foreach (var r in results)
            if (!r.Ok) return r;
        var removed = new System.Collections.Generic.List<string>();
        var skipped = new System.Collections.Generic.List<string>();
        bool noOp = true, hot = false;
        var messages = new System.Collections.Generic.List<string>();
        foreach (var r in results)
        {
            removed.AddRange(r.Removed);
            skipped.AddRange(r.Skipped);
            noOp &= r.NoOp;
            hot |= r.HotUnloaded;
            if (r.Message.Length > 0) messages.Add(r.Message);
        }

        return new ServerInstallResult(true, string.Join(" ", messages), removed, skipped, noOp, hot);
    }

    public ServerInstallResult CancelUninstall(string pluginId)
    {
        var entry = Resolve(pluginId);
        if (entry == null) return FromLedger(pluginId, LedgerAct.Unmark, client: true, server: true);
        return _service.CancelUninstall(entry.Key);
    }

    /// <summary>
    /// Cancel install: delete the written-but-unloaded copy. F27 (live 2026-10-07: Yes did nothing, and the file had
    /// already been promoted and loaded at the server's restart): the ledgers are folded with what was promoted first, and
    /// a copy that already loaded is never deleted by a Cancel: the answer says it loaded and to uninstall it instead.
    /// </summary>
    public ServerInstallResult CancelPendingInstall(string pluginId)
    {
        try { _service.ReconcileLedgers(); }
        catch { /* the ledgers are read as they are */ }
        var rows = LedgerRows(pluginId);
        bool anyPending = false;
        foreach (var r in rows)
            if (string.Equals(r.Category, ServerInstallLedger.CategoryPending, StringComparison.OrdinalIgnoreCase)) { anyPending = true; break; }
        if (rows.Count > 0 && !anyPending)
        {
            var loaded = new ServerInstallResult(false, AlreadyLoaded, Array.Empty<string>(), Array.Empty<string>());
            InstallLog.Info("Install: Cancel install of " + pluginId + ": " + AlreadyLoaded);
            return loaded;
        }

        var entry = Resolve(pluginId);
        if (entry == null || rows.Count > 0) return FromLedger(pluginId, LedgerAct.CancelInstall, client: true, server: true);
        return _service.RemovePackage(entry.Key);
    }

    /// <summary>F27: a Cancel install whose copy already loaded at its restart.</summary>
    public const string AlreadyLoaded = "It already loaded at its restart, so there is no download to cancel; uninstall it instead.";

    private ServerInstallResult FromLedger(string pluginId, LedgerAct act, bool client, bool server)
    {
        var rows = OnRoot(LedgerRows(pluginId), client, server);
        if (rows.Count == 0) return ServerInstallResult.Refuse(NotOurs);
        return _service.ActOnLedgerRows(rows, act);
    }

    /// <summary>
    /// F1 (live 2026-10-07), F36: the package keys this client's install ledgers name for a plugin (live: the Mods list
    /// row's ledger rows, by their <c>package:</c> source); <see cref="Resolve"/> reads them first. Null: none known.
    /// </summary>
    internal Func<string, IReadOnlyList<string>>? LedgerKeysProbe { get; set; }

    private CatalogEntry? Resolve(string pluginId)
    {
        // F36 (live 2026-10-08: two local listings, Progenitor-FilmRestart and Progenitor-FilmExample, name progenitor.film;
        // the index answered FilmExample, which nothing installed, so the mark found no copy and the page still said
        // "Removes at the next restart"): the package the install record names comes first. Its own listing is the copy
        // on disk; the index match is only a fallback for a plugin no ledger row names. Two packages named for one plugin
        // resolve to none here, so the rows themselves are acted on (FromLedger), never a guess.
        IReadOnlyList<string>? keys = null;
        try { keys = LedgerKeysProbe?.Invoke(pluginId); }
        catch { keys = null; }
        if (keys != null && keys.Count > 0)
        {
            if (keys.Count > 1) return null;
            try { if (_service.TryGetEntry(keys[0], out var byKey) && byKey != null) return byKey; }
            catch { /* the rows themselves follow */ }
            return null;
        }

        try { return _service.FindEntryForPlugin(pluginId); }
        catch { return null; }
    }
}
