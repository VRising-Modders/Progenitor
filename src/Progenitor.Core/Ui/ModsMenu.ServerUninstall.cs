using System;
using System.Collections.Generic;
using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>
/// SRVUNINST1: the Mods page's Uninstall per side. Live (2026-10-07, ExampleCommandFramework on the
/// connected test server, client copy absent): the page offered Uninstall, its confirmation promised
/// removal from "your local server", and Yes ran only the client's ledger seam, which refused the
/// plugin as not ours in a one-line note. The page now decides each side before it asks
/// (<see cref="ModUninstallPlan"/>): the client copy by its own remover, the server copy through the
/// Install tab's route — the connected server's admin uninstall (<see cref="RemoteInstallClient"/>)
/// or the configured local folder. Nothing removable means a disabled button and the reason, never a
/// confirmation; the server's answer becomes the page's note when it arrives.
/// </summary>
public static partial class ModsMenu
{
    /// <summary>Tests: the route the page decides the server copy by. Null: the Install tab's route.</summary>
    internal static Func<ServerRoute>? PageRouteProbe { get; set; }

    /// <summary>Tests: the server a side's words name (true: the connected one). Null: the Install tab's.</summary>
    internal static Func<bool, ServerIdentity>? PageServerIdentityProbe { get; set; }

    // The page whose remote uninstall is in flight: its plugin, the package it was sent as, the
    // client half's sentence to keep in front of the server's, and the server named in the answer.
    private static string _modsRemotePlugin = "";
    private static string _modsRemotePackage = "";
    private static string _modsRemoteLead = "";
    private static ServerIdentity? _modsRemoteServer;
    private static bool _modsRemoteFinished;

    // The Uninstall plan the open box shows (Yes sends exactly this one, or nothing).
    private static ModUninstallPlan? _modPromptPlan;

    /// <summary>The Mods page's remote uninstall being followed (readback), or empty.</summary>
    internal static string ModsRemotePackage => _modsRemotePackage;

    private static ServerRoute PageRoute()
    {
        var probe = PageRouteProbe;
        if (probe != null)
        {
            try { return probe(); }
            catch { /* the plain answer below */ }
        }
#if PROGENITOR_TESTS
        return new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NoServer);
#else
        try { return CurrentServerRoute(); }
        catch { return new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NoServer); }
#endif
    }

    private static ServerIdentity? PageServerIdentity(bool remote)
    {
        var probe = PageServerIdentityProbe;
        if (probe != null)
        {
            try { return probe(remote); }
            catch { return null; }
        }
#if PROGENITOR_TESTS
        return null;
#else
        try { return CurrentServerIdentity(remote); }
        catch { return null; }
#endif
    }

    /// <summary>
    /// The plan for one page, from the same evidence the page and the Install tab already read: the
    /// row's ledger and provenance, the uninstall seam, the route, and the connected server's rows
    /// (its ledger row for the listing, then its row for this plugin GUID).
    /// </summary>
    private static ModUninstallPlan BuildUninstallPlan(
        string pluginId, ModsSidebarRow? row, CatalogEntry? evidence, PackageSide side,
        bool clientHalf, bool serverHalf, bool hot)
    {
        var seam = UninstallSeam;
        bool clientLedger = false, serverLedger = false;
        if (row != null)
        {
            for (int i = 0; i < row.Ledger.Count; i++)
            {
                if (string.Equals(row.Ledger[i].Root, InstalledState.ServerRoot, StringComparison.OrdinalIgnoreCase)) serverLedger = true;
                else clientLedger = true;
            }
        }

        bool owns;
        if (seam == null) owns = clientLedger || serverLedger;
        else
        {
            try { owns = seam.Owns(pluginId); }
            catch { owns = false; }
        }

        var client = ClientRemover.None;
        if (row?.Provenance == ModProvenance.Reload) client = ClientRemover.Reload;
        else if (owns && clientLedger) client = ClientRemover.Seam;
        // F1: never "not ours" for a mod its own install record names; say what is missing instead.
        bool ledgeredUnresolved = !owns && clientLedger && client == ClientRemover.None;

        var route = PageRoute();
        RemoteServerRow? ledgerRow = null, pluginRow = null;
        if (route.Kind == ServerRouteKind.Remote)
        {
            var remote = RemoteInstallClient.Shared;
            try
            {
                if (evidence != null) ledgerRow = remote.RowFor(evidence, (_catalog as InstallCatalogService)?.IdentityProbe);
                pluginRow = remote.PluginRowFor(pluginId);
            }
            catch { /* no row: the plan says the server did not report one */ }
        }

        bool serverPresent = serverHalf || ledgerRow != null || (pluginRow != null && pluginRow.State != RemoteRowState.None)
            || row?.Provenance == ModProvenance.Server;
        return ModUninstallPlan.Decide(new ModUninstallFacts
        {
            Name = pluginId,
            ClientPresent = clientHalf || row?.Provenance == ModProvenance.Reload,
            Client = client,
            ClientLedgeredUnresolved = ledgeredUnresolved,
            // SRVUNINST1: the seam's own decision, so the box promises exactly what Yes does.
            ClientHot = SeamUnloadsNow(seam, pluginId, hot),
            ReloadFile = row?.Provenance == ModProvenance.Reload ? row.ReloadFile : "",
            AlsoRemoves = row?.Provenance == ModProvenance.Reload
                ? ReloadFolderPlugins.AlsoDeclaredBy(row.ReloadFile, row.Id, ReloadGuidsProbe)
                : (IReadOnlyList<string>)Array.Empty<string>(),
            ServerPresent = serverPresent,
            Route = route,
            LedgerRow = ledgerRow,
            PluginRow = pluginRow,
            LocalServerOurs = owns && serverLedger,
            // F10: the local server's copy goes now only by that server's own state, never this client's reloader.
            LocalServerHot = owns && serverLedger && SeamServerUnloadsNow(seam, pluginId),
            Server = route.Kind == ServerRouteKind.None ? null : PageServerIdentity(route.Kind == ServerRouteKind.Remote),
            ServerOnlyMod = side == PackageSide.Server,
            LastRefusal = ledgerRow != null || pluginRow != null
                ? RemoteInstallClient.Shared.LatestFor((ledgerRow ?? pluginRow)!.Package)
                : null,
        });
    }

    /// <summary>
    /// Carry out a confirmed plan: the client copy by its remover (the seam also takes the local
    /// folder's copy, as it always has), then the server copy through the remote request. Returns the
    /// page's sentence; the server's answer replaces its half when it arrives.
    /// </summary>
    private static string RunUninstallPlan(string pluginId, ModsSidebarRow row, ModPageModel model, ModUninstallPlan plan)
    {
        if (!plan.CanAct) return plan.WhyNot;
        var seam = UninstallSeam;
        string clientNote = "";
        if (plan.Client == ClientRemover.Reload)
        {
            clientNote = RemoveReloadNow(row);
        }
        else if (plan.Client == ClientRemover.Seam || plan.Server == ServerRemover.LocalSeam)
        {
            // F4/F10: exactly the sides the box named, each by its own state.
            bool clientSide = plan.Client == ClientRemover.Seam, serverSide = plan.Server == ServerRemover.LocalSeam;
            bool now = clientSide ? plan.ClientUnloadsNow && (!serverSide || plan.ServerUnloadsNow) : plan.ServerUnloadsNow;
            clientNote = seam != null
                ? NoteOf(Seam(() => seam.UninstallSides(pluginId, model.Hot, clientSide, serverSide)),
                    // The note says what happened: removed now, or removed at the next restart.
                    now ? InstallModsCopy.RemovedPending : ModPageCopy.RemovesAtRestartNote)
                : NoteForRemoval(row, RemoveInstalledMod(row));
        }

        if (plan.Server != ServerRemover.Remote) return clientNote;
        return Join(clientNote, SendServerUninstall(pluginId, plan, clientNote));
    }

    /// <summary>The Install tab's remote uninstall request for the server copy; the sentence for its first step.</summary>
    private static string SendServerUninstall(string pluginId, ModUninstallPlan plan, string clientNote)
    {
        var remote = RemoteInstallClient.Shared;
        remote.ClearFinished(plan.RemotePackage);
        int nonce = remote.Begin(RemoteInstallOp.Uninstall, plan.RemotePackage, "", force: plan.RemoteForce, out string? refusal);
        if (nonce == 0)
            return ModUninstallPlan.ResultNote(
                new RemoteInstallProgress(0, RemoteInstallOp.Uninstall, RemoteInstallStage.Refused, RemoteInstallReason.Unavailable,
                    plan.RemotePackage, "", refusal ?? ServerRouteCopy.RemoteOff),
                plan.Identity);

        _modsRemotePlugin = pluginId;
        _modsRemotePackage = plan.RemotePackage;
        _modsRemoteLead = clientNote;
        _modsRemoteServer = plan.Identity;
        _modsRemoteFinished = false;
        return ModUninstallPlan.ResultNote(remote.LatestFor(plan.RemotePackage), plan.Identity);
    }

    /// <summary>
    /// The remote channel changed: when it is the page's uninstall, its step or answer becomes the
    /// page's note and the open page is rebuilt (the server's side cell and plan follow its rows). After
    /// a finished step the status is asked again (<see cref="RemoteInstallClient.Accept(RemoteInstallProgress)"/>);
    /// the page is rebuilt once more when it arrives, then the uninstall is no longer followed.
    /// </summary>
    private static void NoteModPageRemoteChanged()
    {
        string plugin = _modsRemotePlugin;
        if (plugin.Length == 0) return;
        var progress = RemoteInstallClient.Shared.LatestFor(_modsRemotePackage);
        bool wasFinished = _modsRemoteFinished;
        if (progress != null)
        {
            if (string.Equals(_modsActionPlugin, plugin, StringComparison.OrdinalIgnoreCase))
                _modsActionNote = Join(_modsRemoteLead, ModUninstallPlan.ResultNote(progress, _modsRemoteServer));
            if (progress.IsTerminal) _modsRemoteFinished = true;
        }

        // A refusal asks for no new status, so nothing more is coming for it.
        bool done = (wasFinished && progress != null && progress.IsTerminal)
            || (progress != null && progress.Stage == RemoteInstallStage.Refused)
            || progress == null;
        if (done)
        {
            _modsRemotePlugin = "";
            _modsRemotePackage = "";
            _modsRemoteLead = "";
            _modsRemoteServer = null;
            _modsRemoteFinished = false;
        }

        if (IsOpen && _modPage != null && string.Equals(_modPage.Model.PluginId, plugin, StringComparison.OrdinalIgnoreCase))
            OpenMod(plugin, ReasonInventoryRefresh);
    }

    // ---- the Install tab's client copy that no ledger lists ------------------------------------

    /// <summary>The Mods list row of a reload-folder plugin no ledger lists (FWALLOW1), or null.</summary>
    private static ModsSidebarRow? ReloadRowOf(string pluginId)
    {
        try
        {
            var row = CurrentSidebarModel(applySearch: false).Find(pluginId);
            return row is { Provenance: ModProvenance.Reload, IsFramework: false } && row.ReloadFile.Length > 0 ? row : null;
        }
        catch { return null; }
    }

    /// <summary>The dialog tag of the Install tab's reload-folder uninstall.</summary>
    internal const string ReloadUninstallDialogTag = "modReloadUninstall";

    /// <summary>
    /// SRVUNINST1: the Install tab's Uninstall of a reload-folder copy — the Mods page's own FWALLOW1
    /// confirmation and removal (the file is deleted now; the reloader unloads it).
    /// </summary>
    private static void OpenReloadUninstallDialog(ModsSidebarRow row, string name)
    {
        string pluginId = row.Id;
        string body = ModPageCopy.UninstallBodyReload(
            System.IO.Path.GetFileName(row.ReloadFile), ReloadFolderPlugins.AlsoDeclaredBy(row.ReloadFile, row.Id, ReloadGuidsProbe));
        EnsureModPageDialogFactory();
        var factory = ModPagePane.DialogFactory;
        if (factory == null)
        {
            NoteAndReopen(pluginId, RemoveReloadNow(row));
            return;
        }

        CloseSwitchDialog();
        _switchDialogPlugin = pluginId;
        var dialog = UiDialog.Open(
            factory, ReloadUninstallDialogTag, "uninstall", pluginId,
            ModPageCopy.UninstallHeading(name, ""), body, ModPageCopy.UninstallYes, LabelTone.Danger, ModPageCopy.UninstallNo,
            danger: true);
        dialog.OnYes(() =>
        {
            if (!string.Equals(_switchDialogPlugin, pluginId, StringComparison.OrdinalIgnoreCase)) return;
            _switchDialogPlugin = "";
            var current = ReloadRowOf(pluginId);
            NoteAndReopen(pluginId, current == null ? ReloadFolderPlugins.WhyNoFile : RemoveReloadNow(current));
        });
        dialog.OnNo(() => _switchDialogPlugin = "");
    }

    /// <summary>FWALLOW1's removal as one sentence, with the inventory told when it worked.</summary>
    private static string RemoveReloadNow(ModsSidebarRow row)
    {
        var removal = RemoveReloadMod(row);
        LastReloadRemoval = removal;
        if (removal.Ok)
        {
            // Review: a turned-off mod that is uninstalled is no longer "turned off": its switch entry goes with it.
            if (Plugins.ModEnableState.IsDisabled(row.Id))
            {
                try { Plugins.ModEnableState.Set(row.Id, row.Name, enabled: true, heldWith: null, DateTime.UtcNow, out _); }
                catch { /* the entry is evidence for the switch; the removal stands */ }
            }

            NoteInventoryChanged(ModMenuInventory.RefreshReasonHotUnloaded);
            // The Install tab's rows read a scan taken before the delete; they read a fresh one now and after the unload.
            try { _installPane?.NoteClientCopyRemoved(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: install rows refresh after a reload removal threw: " + e.Message); }
        }

        return removal.Ok
            ? InstallModsCopy.RemovedPending
            : (removal.Message.Length > 0 ? removal.Message : InstallModsCopy.RemoveFailed);
    }

    /// <summary>
    /// <see cref="InstallModsPane.PlainUninstallNow"/>: a whole-row Uninstall's client copy that no ledger
    /// lists, removed by its own remover without a second box.
    /// </summary>
    private static string PlainUninstallNow(string pluginId, string name)
    {
        if (ReloadRowOf(pluginId) is { } row) return RemoveReloadNow(row);
        return ModSwitch.RequestRemove(pluginId, name, PlainIdentityOf(pluginId)).Message;
    }

    /// <summary>
    /// <see cref="InstallModsPane.PlainUninstallSentence"/>: what <see cref="PlainUninstallNow"/> would do
    /// to this copy, in the words its own box uses (the reload-folder file and every plugin it holds, or
    /// the preloader's move); empty when nothing here can remove it.
    /// </summary>
    private static string PlainUninstallSentence(string pluginId)
    {
        if (ReloadRowOf(pluginId) is { } row)
            return ModPageCopy.UninstallBodyReload(
                System.IO.Path.GetFileName(row.ReloadFile), ReloadFolderPlugins.AlsoDeclaredBy(row.ReloadFile, row.Id, ReloadGuidsProbe));
        return PlainIdentityOf(pluginId) is { Certain: true } ? PlainUninstallBody : "";
    }

    /// <summary>
    /// SRVUNINST1 (live 2026-10-07: an uninstalled HotDemo read "turn it back on in the Mods list"): what this
    /// client has of a required mod the server no longer sees. Turned off (the switch, or the preloader's off copy);
    /// else installed in some form (loaded, or a reload-folder file); else not installed. A key that is a package id
    /// rather than a plugin GUID is not judged (the server notice keeps its own advice).
    /// </summary>
    internal static Networking.RecheckLocalState RecheckLocalStateOf(string key)
    {
        string id = (key ?? "").Trim();
        if (id.Length == 0 || RemoteInstallWire.IsValidPackage(id)) return Networking.RecheckLocalState.Unknown;
        try
        {
            // A copy first (review: an enable-state entry outlives an uninstall, and so may a registry entry): loaded
            // here, a reload-folder file, or an ordinary plugin the preloader turned off on disk.
            bool copy = CurrentReloadFiles().ContainsKey(id) || ModSwitch.PlainOffOnDisk(id);
            if (!copy)
                foreach (var row in ModMenuInventory.Snapshot().Rows)
                    if (string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase) && ModMenuInventory.IsLocalLocation(row.Location)) { copy = true; break; }
            if (!copy) return Networking.RecheckLocalState.NotInstalled;
            // Then the join's own view of a turned-off copy (its guards: not paused here, not already turning on).
            return JoinOffCopyOf(id) is { PausedHere: false, TurnsOnAtNextStart: false }
                ? Networking.RecheckLocalState.TurnedOff
                : Networking.RecheckLocalState.Unknown;
        }
        catch { return Networking.RecheckLocalState.Unknown; }
    }

    /// <summary>F1: the package keys the install-ledger rows name for <paramref name="pluginId"/> (any root).</summary>
    internal static IReadOnlyList<string> LedgerPackageKeysOf(string pluginId)
    {
        var keys = new List<string>();
        try
        {
            // The ledger itself (not the list model, which asks the seam while it builds).
            foreach (var ledger in ModMenuInventory.LedgerRowsForPlugin(CurrentLedger(), pluginId))
                if (InstalledState.TrySplitPackageSource(ledger.Source, out string package, out _) && package.Length > 0
                    && !keys.Exists(k => string.Equals(k, package, StringComparison.OrdinalIgnoreCase)))
                    keys.Add(package);
        }
        catch { /* none */ }

        return keys;
    }

    private static long _reconciledAt;

    /// <summary>
    /// F26: when <paramref name="row"/> has a pending ledger row, fold the promotions the roots recorded (Bootstrap promotes a
    /// pending copy at that process's start) into the ledgers, at most every two seconds. True when a ledger changed (the
    /// inventory is refreshed then).
    /// </summary>
    internal static bool ReconcileIfPending(ModsSidebarRow row)
    {
        bool pending = false;
        foreach (var l in row.Ledger)
            if (string.Equals(l.Category, ServerInstallLedger.CategoryPending, StringComparison.OrdinalIgnoreCase)) { pending = true; break; }
        if (!pending) return false;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _reconciledAt < 2 * System.Diagnostics.Stopwatch.Frequency) return false;
        _reconciledAt = now;
        if (_catalog is not InstallCatalogService service) return false;
        LedgerReconcileResult result;
        try { result = service.ReconcileLedgers(); }
        catch { return false; }
        if (!result.Changed) return false;
        Plugin.Logger?.LogInfo("Ui: folded promoted installs into the ledgers before drawing " + row.Id + " (" + result.Rewritten.Count + " rewritten, " + result.Dropped.Count + " dropped)");
        RefreshAfterPageAction();
        return true;
    }

    private static bool _ledgerLookupServed;

    /// <summary>
    /// F1 (live 2026-10-07): before the Mods page decides about a ledgered mod, the catalog that maps its plugin id to its
    /// ledger key is there: the cached index served (as an open of the workspace serves it) and the local index merged.
    /// Once per Core generation; cheap afterwards.
    /// </summary>
    internal static void EnsureCatalogForLedger()
    {
        if (_ledgerLookupServed) return;
        // Review (L8): only once there is a catalog to serve into; until then the next page asks again.
        if (_catalog is not InstallCatalogService service) return;
        _ledgerLookupServed = true;
        try { ServeCachedIndexNow(); }
        catch { /* the cached index is optional */ }
        try { service.EnsureLookupReady(); }
        catch { /* the local index is optional */ }
    }

    /// <summary>NOTICEOPEN1: the Install Mods catalog lists <paramref name="key"/> (a plugin GUID). Never throws.</summary>
    internal static bool InstallListedOf(string key)
    {
        try { return !string.IsNullOrWhiteSpace(key) && ResolveInstallLink(key.Trim()) != null; }
        catch { return false; }
    }

    /// <summary>
    /// NOTICEOPEN1 (standing rule 2026-10-07): the in-game notice's open button. <c>install</c>: the Mods workspace on the
    /// Install Mods tab with the mod's listing selected (or, when no listing is known, the tab searched for its name);
    /// <c>mods</c>: the Mods list at the mod's page (its switch turns it back on). Opens the workspace first, as a mod link does.
    /// </summary>
    internal static void OpenForPolicyNotice(string kind, string key, string name)
    {
        string id = (key ?? "").Trim();
        ModLinks.OpenWorkspaceThen(() =>
        {
            if (string.Equals(kind, Networking.ClientPolicyNoticeText.OpenKindMods, StringComparison.Ordinal))
            {
                OpenModLinkInstalled(id);
                return;
            }

            EnsureTabState();
            string listing = ResolveInstallLink(id)?.Key ?? "";
            _tabState!.InstallMods.Search = listing.Length > 0 ? "" : (name ?? "").Trim();
            OpenInstallFromInstalled(id, listing);
        });
    }

    private static bool SeamServerUnloadsNow(IModUninstallSeam? seam, string pluginId)
    {
        if (seam == null) return false;
        try { return seam.ServerUnloadsNow(pluginId); }
        catch { return false; }
    }

    private static bool SeamUnloadsNow(IModUninstallSeam? seam, string pluginId, bool hot)
    {
        if (seam == null) return hot;
        try { return seam.UnloadsNow(pluginId, hot); }
        catch { return false; }
    }

    private static string Join(string a, string b)
    {
        a = (a ?? "").Trim();
        b = (b ?? "").Trim();
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        return a + " " + b;
    }

    private static void ResetServerUninstall()
    {
        _modsRemotePlugin = "";
        _modsRemotePackage = "";
        _modsRemoteLead = "";
        _modsRemoteServer = null;
        _modsRemoteFinished = false;
        _modPromptPlan = null;
        PageRouteProbe = null;
        PageServerIdentityProbe = null;
    }
}
