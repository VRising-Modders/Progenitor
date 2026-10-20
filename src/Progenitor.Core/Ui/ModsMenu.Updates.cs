using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>
/// AUTOUPDATE1 (M6.1): the Mods window's half of client update detection. Wires <see cref="ClientUpdates"/> to the
/// Install tab's catalog (the index this client already read), runs a detection pass whenever that index changes, and
/// feeds a join screens' fresh index read into the same catalog, so no read is made for updates alone.
/// </summary>
public static partial class ModsMenu
{
    private static bool _updatesWired;
#if PROGENITOR_TESTS
    internal static bool UpdatesDrivenForTests { get; set; }
#endif
    private static string _updatesRebuildTried = "";

    /// <summary>The <c>[AutoUpdate] Mods</c> entry (null before the shared config is bound).</summary>
    internal static UpdateMode UpdatesModeSetting
    {
        get
        {
            try
            {
                EnsureBindings();
                return UpdateSettings.Parse(_bindings?.AutoUpdateMods?.Value);
            }
            catch { return UpdateSettings.Parse(UpdateSettings.DefaultMode); }
        }
    }

    internal static bool UpdatesPromptSetting
    {
        get
        {
            try
            {
                EnsureBindings();
                return _bindings?.AutoUpdatePromptBeforeJoin?.Value ?? UpdateSettings.DefaultPrompt;
            }
            catch { return UpdateSettings.DefaultPrompt; }
        }
    }

    /// <summary>AUTOUPDATE2: <c>[AutoUpdate] MajorVersions</c> is Automatic.</summary>
    internal static bool UpdatesMajorAutomatic
    {
        get
        {
            try
            {
                EnsureBindings();
                return UpdateSettings.MajorAutomatic(_bindings?.AutoUpdateMajorVersions?.Value ?? UpdateSettings.DefaultMajor);
            }
            catch { return false; }
        }
    }

    /// <summary>Run a detection pass on the index on show (no request). Wires the probes the first time.</summary>
    internal static void RecheckUpdates(string why)
    {
#if PROGENITOR_TESTS
        // The update state is process-wide; the Mods menu suites must not drive it under the update suite.
        if (!UpdatesDrivenForTests) return;
#endif
        try
        {
            WireClientUpdates();
            ClientUpdates.Recheck(why);
        }
        catch (Exception e)
        {
            try { Plugin.Logger?.LogWarning(ClientUpdates.LogPrefix + "recheck failed: " + e.Message); }
            catch { /* */ }
        }
    }

    private static void WireClientUpdates()
    {
        if (_updatesWired) return;
        _updatesWired = true;
        ClientUpdates.ModeProbe = () => UpdatesModeSetting;
        ClientUpdates.PromptProbe = () => UpdatesPromptSetting;
        ClientUpdates.MajorProbe = () => UpdatesMajorAutomatic;
        ClientUpdates.NsfwProbe = () => ShowNsfwContent;
        ClientUpdates.EntriesProbe = () =>
        {
            var catalog = _catalog;
            return catalog == null ? Array.Empty<CatalogEntry>() : catalog.Search(null);
        };
        ClientUpdates.LocalKeyProbe = key => _catalog is InstallCatalogService s && s.IsLocalKey(key);
        ClientUpdates.DependencyInstalledProbe = DependencyInstalledNow;
#if !PROGENITOR_TESTS
        ClientUpdates.GameRootProbe = () => BepInEx.Paths.GameRootPath;
        ClientUpdates.BepInExRootProbe = () => BepInEx.Paths.BepInExRootPath;
        ClientUpdates.PostMain = a => Progenitor.Dev.MainThread.Post(a, Owner);
        ClientUpdates.Log = line => Plugin.Logger?.LogInfo(line);
        ClientUpdates.Pipeline = new UpdatePipeline
        {
            Inspect = (key, ct) => Catalog() is InstallCatalogService s ? s.DownloadUpdateAndInspectAsync(key, ct) : Catalog().DownloadAndInspectAsync(key, ct), // INDEX1: from the installed copy's own source
            Plan = (key, ct) => _catalog is InstallCatalogService s
                ? s.PlanWithDependenciesAsync(key, InstallTarget.Client, ct)
                : Task.FromResult(InstallPlanSet.Refuse("the install catalog cannot plan dependencies")),
            Execute = set =>
            {
                if (_catalog is not InstallCatalogService s) return ExecuteSetResult.Refuse("no install catalog");
                var result = s.ExecuteWithDependencies(set);
                AfterUpdateWrite();
                return result;
            },
            Release = key => (_catalog as InstallCatalogService)?.ReleaseDownload(key),
            ZipPath = key => (_catalog as InstallCatalogService)?.ZipPathOf(key),
            RunOnMain = work => Progenitor.Dev.MainThread.Run(work, Owner),
            Post = a => Progenitor.Dev.MainThread.Post(a, Owner),
            Installed = key => DependencyInstalledNow(key),
            // AUTOUPDATE2: the version store applies the update when it holds the active copy.
            ApplyStore = (p, set) => Progenitor.Dev.MainThread.Run(() => ApplyThroughStore(p, set), Owner),
            WaitLoaded = WaitImageLoaded,
            RollBackFailed = (p, why) => Progenitor.Dev.MainThread.Run(() => ClientAutoUpdate.RollBackFailed(p, why), Owner),
            NoteRestart = ClientAutoUpdate.NoteRestart,
            // Review H1: the window as the running pass reads it, right before each write.
            WindowClosed = () => ClientAutoUpdate.WindowNow(ignoreRunner: true),
        };
        ClientAutoUpdate.StoreProbe = () => Networking.ModVersionJoin.Service;
        ClientAutoUpdate.LoadFactsProbe = LoadFactsOf;
        ClientAutoUpdate.WindowProbe = ApplyWindowNow;
        ClientUpdates.OpenConfirm = OpenUpdateConfirm;
        ClientUpdates.ShowResult = ShowUpdateResult;
        // Review item 5: the connected server's live handshake rows (none outside a world).
        ClientUpdates.ConnectedProbe = () =>
        {
            if (!Networking.Handshake.ServerRequestSeen) return null;
            var world = Progenitor.Worlds.Client;
            if (world == null || !world.IsCreated) return null;
            return ClientUpdates.ConnectedRequirements(Networking.Handshake.CopyServerAsked());
        };
#endif
    }

    internal const string ModUpdatesLabel = "Updates";

    /// <summary>
    /// The per-mod choice that follows Preferences › Updates › <b>Update mods</b>: <c>Default (Automatic)</c> and so on
    /// (user report 2026-10-09: "Global" did not say where the value lives).
    /// </summary>
    internal const string ModUpdatesDefaultWord = "Default";

    /// <summary>The help line under the mod page's <b>Updates</b> row.</summary>
    internal const string ModUpdatesHelp = "How this mod updates. Default follows Preferences › Updates.";

    /// <summary>The link row under it that opens Preferences at its Updates group (one place in the history).</summary>
    internal const string ModUpdatesLinkKey = "link-preferences-updates";

    internal const string ModUpdatesLinkCaption = "Preferences » Updates";

    /// <summary>The per-mod choice's captions, in <see cref="ClientAutoUpdate.ModeChoices"/> order. Game-free.</summary>
    internal static string[] ModUpdateChoiceLabels(string defaultWord, string installedVersion) => new[]
    {
        ModUpdatesDefaultWord + " (" + (string.IsNullOrWhiteSpace(defaultWord) ? UpdateSettings.DefaultMode : defaultWord) + ")",
        "Automatic",
        "Notify",
        "Off",
        string.IsNullOrWhiteSpace(installedVersion) ? "Keep this version" : "Keep " + installedVersion.Trim(),
    };

    /// <summary>
    /// Each choice's short explanation, in the same order (decided 2026-10-09), shown where the kit shows a hovered
    /// option's description (OPTIONHOVER1). Game-free.
    /// </summary>
    internal static string[] ModUpdateChoiceDescriptions(string defaultWord, string installedVersion)
    {
        string word = string.IsNullOrWhiteSpace(defaultWord) ? UpdateSettings.DefaultMode : defaultWord.Trim();
        string version = string.IsNullOrWhiteSpace(installedVersion) ? "this version" : installedVersion.Trim();
        return new[]
        {
            "Follow Preferences › Updates (currently " + word + ").",
            "Install updates for this mod by itself, at the title screen.",
            "Show updates for this mod; install only when you choose.",
            "Never check this mod for updates. It won't appear in the update list.",
            "Stay on " + version + ". Newer versions are still listed in the Install tab but never installed. Unpin there or pick another option to resume.",
        };
    }

    /// <summary>The mod page link's click (and <c>widget_click label=link-preferences-updates/open</c>).</summary>
    internal static void OpenPreferencesUpdatesFromLink()
    {
        if (!IsOpen) return;
        if (!JumpToGroup(ModsTab.Preferences, ConfigurationPane.UpdatesGroup)) OpenPreferencesFromLink();
    }

    /// <summary>The package whose ledger rows name <paramref name="pluginId"/>, or empty.</summary>
    internal static string PackageOfPlugin(string pluginId)
    {
#if PROGENITOR_TESTS
        return "";
#else
        foreach (var m in ClientUpdateInputs.ReadManaged(BepInEx.Paths.GameRootPath))
            foreach (var id in m.PluginIds)
                if (string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase)) return m.Key;
        return "";
#endif
    }

    /// <summary>The package key a Progenitor-installed mod's ledger rows name, or empty.</summary>
    internal static string PackageOfRow(ModsSidebarRow row)
    {
        foreach (var l in row.Ledger)
            if (InstalledState.TrySplitPackageSource(l.Source, out string package, out _) && package.Length > 0) return package;
        return "";
    }

    /// <summary>
    /// AUTOUPDATE2: on a Progenitor-installed mod's page, in its Details grid: <b>Updates</b> (Default (Automatic) / Automatic /
    /// Notify / Off / Keep 1.2.0, the game's dropdown bar) with the <b>Preferences » Updates</b> link under it, <b>Roll back</b>
    /// when an earlier version is kept, and the versions that failed to load with <b>Clear failed versions</b>. MODPAGE2: the
    /// row and each option describe themselves in the description column.
    /// </summary>
    internal static void DrawModUpdateControls(IUiPanel panel, string pluginId, ModsSidebarRow row, ModPagePane? page = null)
    {
        string package = PackageOfRow(row);
        if (package.Length == 0) return;
        string installed = LedgerVersionOf(row);
        string global = UpdateSettings.Word(UpdatesModeSetting);
        var values = ClientAutoUpdate.ModeChoices;
        var labels = ModUpdateChoiceLabels(global, installed);
        string choice = ClientAutoUpdate.ModeChoiceOf(package);
        // The game's own dropdown bar (the settings grid's skin, as on Preferences), not the off-grid selector.
        panel.SettingsGrid(true);
        IUiDropdown dropdown;
        try
        {
            dropdown = panel.Dropdown(ModUpdatesLabel, values, choice, labels, ModUpdateChoiceDescriptions(global, installed));
            dropdown.OnChanged(v =>
            {
                ClientAutoUpdate.SetModeChoice(package, v, installed, out string message);
                NoteAndReopen(pluginId, "Updates: " + message + ".");
            });
        }
        finally { panel.SettingsGrid(false); }

        var details = page?.Details;
        details?.Describe(dropdown, ModUpdatesLabel, ModUpdatesHelp);
        details?.DescribeOptions(dropdown, ModUpdatesLabel, ModUpdatesHelp);

        var linkSpec = SettingsLinks.TextLink(ModUpdatesLinkKey, ModUpdatesLinkCaption);
        // The link's caption starts at the page margin: its button cell pads the caption by half a button pad.
        var linkCells = new List<CellSpec> { ModPageSpacing.Lead() };
        linkCells[0].Width = CellWidth.Px(Math.Max(1f, linkCells[0].Width.Value - ModUpdatesLinkCaptionPad));
        linkCells[0].MinWidth = linkCells[0].Width.Value;
        linkCells.AddRange(linkSpec.Cells);
        panel.SettingsGrid(true);
        IUiCellRow link;
        try { link = panel.CellRow(new CellRowSpec(linkSpec.Key, linkCells, height: linkSpec.ExplicitHeight, plain: true)); }
        finally { panel.SettingsGrid(false); }
        link.OnClick(SettingsLinks.CellOpen, OpenPreferencesUpdatesFromLink);

        string back = ClientAutoUpdate.RollbackVersionOf(pluginId);
        if (back.Length > 0)
        {
            string version = back;
            var cells = new[] { ModPagePane.FactButton(CellRollBack, "Roll back to " + version) };
            var rb = page != null
                ? page.AddFact(panel, "rollback", "Previous version", cells, RollBackHelp, textValue: false)
                : panel.CellRow(ModPagePane.FactRow("rollback", "Previous version", cells, ModPageSpacing.FactsWidth(MenuPanelLayout.DetailRowWidth()), false));
            rb.OnClick(CellRollBack, () => NoteAndReopen(pluginId, ClientAutoUpdate.RollBack(pluginId, package)));
        }

        var failed = ClientAutoUpdate.FailedOf(package);
        if (failed.Count > 0)
        {
            var parts = new List<string>();
            foreach (var f in failed) parts.Add(f.Version + (f.Reason.Length > 0 ? " (" + f.Reason + ")" : ""));
            string text = string.Join(" · ", parts);
            var cells = new[]
            {
                ModPagePane.FactText(ModPagePane.CellFactValue, text, CellTone.Warn),
                ModPagePane.FactButton(CellClearFailed, "Clear failed versions"),
            };
            string help = "Failed to load, not tried again automatically: " + text;
            var fr = page != null
                ? page.AddFact(panel, "failed", "Failed versions", cells, help)
                : panel.CellRow(ModPagePane.FactRow("failed", "Failed versions", cells, ModPageSpacing.FactsWidth(MenuPanelLayout.DetailRowWidth())));
            fr.OnClick(CellClearFailed, () =>
            {
                int n = ClientAutoUpdate.ClearFailed(package);
                NoteAndReopen(pluginId, n == 1 ? "Cleared 1 failed version." : "Cleared " + n + " failed versions.");
            });
        }
    }

    internal const string CellRollBack = "rollback";
    internal const string CellClearFailed = "clear-failed";
    internal const string RollBackHelp = "Put the previous version back now (or at the next start), and keep this mod at it.";

    /// <summary>A text link's caption sits this far inside its button cell (measured live 2026-10-09: 6 px right of the margin).</summary>
    internal const float ModUpdatesLinkCaptionPad = 6f;

    /// <summary>A package the plan needs is installed when the Install tab's own state says so (any tool's copy counts).</summary>
    private static bool DependencyInstalledNow(string key)
    {
        try
        {
            var catalog = _catalog;
            var pane = _installPane;
            if (catalog == null || pane == null) return false;
            if (!catalog.TryGetEntry(key, out var entry) || entry == null) return false;
            return pane.DetectClient(entry).State != InstalledKind.NotInstalled;
        }
        catch { return false; }
    }

#if !PROGENITOR_TESTS
    /// <summary>AUTOUPDATE2: one update through the version store (main thread); null when the install route writes it.</summary>
    private static StoreUpdateResult? ApplyThroughStore(PreparedUpdate p, InstallPlanSet set)
    {
        // Review H1: an automatic pass writes only while the apply window is open, read in this same main-thread step.
        if (p.RequireWindow && ClientAutoUpdate.WindowNow(ignoreRunner: true) is { Length: > 0 } closed) return StoreUpdateResult.Defer(closed);
        if (_catalog is not InstallCatalogService s) return null;
        var svc = Networking.ModVersionJoin.Service;
        if (svc == null) return null;
        var bytes = s.ApprovedImage(set, out string sha, out bool hot, out string notHot, out string error);
        if (bytes == null && error.Length == 0 && set.Root?.PluginId is { Length: > 0 } id && svc.Manifest().Find(id)?.Pending != null)
            // Review M3: the install route would write a second pending copy beside the store's queued one.
            return StoreUpdateResult.Fail("a version of it is already queued for the next start; restart V Rising first");
        if (bytes == null) return error.Length > 0 ? StoreUpdateResult.Fail(error) : null;
        var root = set.Root!;
        string version = string.IsNullOrWhiteSpace(root.Entry.Manifest.Version) ? p.To : root.Entry.Manifest.Version!;
        var ledger = new ParkedLedgerRow
        {
            Sha256 = sha,
            Size = bytes.LongLength,
            Category = ServerInstallLedger.CategoryMod,
            Source = LedgerSpelling.SourceFor(root.Entry, version),
            WrittenUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            PluginId = root.PluginId ?? "",
        };
        string game = BepInEx.Paths.GameRootPath;
        var result = StoreUpdate.Apply(svc, new StoreUpdateRequest
        {
            Package = p.Package,
            Version = version,
            PluginId = root.PluginId ?? "",
            Bytes = bytes,
            HotImage = hot,
            NotHotReason = notHot,
            Ledger = ledger,
        }, (string package, string path, out string message) => PendingSupersede.TryRemove(game, package, path, out message));
        if (result.NotApplicable)
            Plugin.Logger?.LogInfo(ClientUpdates.LogPrefix + p.Package + " takes the install route: " + result.Message);
        AfterUpdateWrite();
        return result;
    }

    /// <summary>
    /// AUTOUPDATE2 review M2: what can be read about a plugin's load: Bootstrap's admission snapshot (null on an older
    /// Bootstrap), Core's registry outcome (Progenitor plugins only) and the reloader's record of the file (any plugin it
    /// loads; a failed load records its exception). <paramref name="stem"/> empty: the store's active copy names it.
    /// </summary>
    private static LoadFacts LoadFactsOf(string pluginId, string stem)
    {
        (bool, string)? snapshot = null;
        bool busy = false;
        if (BootstrapLink.AdmissionSnapshotCapability >= 1
            && BootstrapLink.TryCaptureAdmissionSnapshot(pluginId, null, null, out var v) && v != null)
        {
            snapshot = (v.PluginLoaded, v.PluginImageSha256 ?? "");
            busy = v.LoaderBusy;
        }

        string? registry = Progenitor.Plugins.ModRegistry.TryGet(pluginId, out var reg) ? reg.Outcome.ToString() : null;
        if (string.IsNullOrEmpty(stem))
        {
            try
            {
                var active = Networking.ModVersionJoin.Service?.ActiveCopy(pluginId, out _, out _);
                stem = active == null ? "" : System.IO.Path.GetFileNameWithoutExtension(active.Name);
            }
            catch { stem = ""; }
        }

        ReloadRecord? record = null;
        try { record = ReloadRecord.Read(BepInEx.Paths.BepInExRootPath, stem); }
        catch { record = null; }
        return new LoadFacts { Snapshot = snapshot, Registry = registry, Record = record, LoaderBusy = busy };
    }

    /// <summary>
    /// AUTOUPDATE2: wait until the image is loaded, failed or the deadline passed (<see cref="LoadCheck"/>: 15 s, pushed back
    /// while the loader is busy, at most 120 s).
    /// </summary>
    private static Task<(LoadVerdict, string)> WaitImageLoaded(string pluginId, string sha)
    {
        var done = new TaskCompletionSource<(LoadVerdict, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = DateTime.UtcNow;
        double busySeconds = 0;
        DateTime last = started;
        void Poll()
        {
            try
            {
                var now = DateTime.UtcNow;
                var facts = LoadFactsOf(pluginId, "");
                if (facts.LoaderBusy) busySeconds += (now - last).TotalSeconds;
                last = now;
                var (verdict, detail) = LoadCheck.Judge(facts, sha, started);
                if (verdict is LoadVerdict.Loaded or LoadVerdict.Failed || (verdict == LoadVerdict.Unknown && (now - started).TotalSeconds >= 3))
                {
                    done.TrySetResult((verdict, detail));
                    return;
                }

                if (LoadCheck.PastDeadline((now - started).TotalSeconds, busySeconds))
                {
                    done.TrySetResult((LoadVerdict.Waiting, detail + " after " + (int)(now - started).TotalSeconds + " s"));
                    return;
                }

                Progenitor.Dev.MainThread.PostDelayed(0.25, Poll, Owner);
            }
            catch (Exception e) { done.TrySetResult((LoadVerdict.Unknown, "the load could not be checked: " + e.Message)); }
        }

        Progenitor.Dev.MainThread.Post(Poll, Owner);
        return done.Task;
    }

    /// <summary>AUTOUPDATE2: the apply window's facts now (main thread).</summary>
    private static ApplyWindowFacts ApplyWindowNow()
    {
        var svc = Networking.ModVersionJoin.Service;
        bool loaderBusy = false;
        try { loaderBusy = BootstrapLink.TryCaptureAdmissionSnapshot("", null, null, out var v) && v != null && v.LoaderBusy; }
        catch { loaderBusy = true; }
        var world = Progenitor.Worlds.Client;
        if (world != null && !world.IsCreated) world = null;
        return new ApplyWindowFacts
        {
            AtTitle = NativeModsEntry.AtTitleScreen,
            InWorld = world != null,
            // The held-connection flag is only meaningful while a client world exists (it is not cleared when the world goes
            // away without passing through Idle, live 2026-10-09 after a bridge disconnect).
            Joining = Networking.JoinFlowClient.BusyForVersions || (Networking.Handshake.ClientConnectionHeld && world != null),
            PreJoinHolding = Networking.PreJoinGate.Holding,
            Hosting = BootstrapLink.IsHostingPrivateGame,
            LoaderBusy = loaderBusy,
            StoreMissing = svc == null,
            StoreBusy = svc != null && (svc.Engine.JournalPending || svc.Engine.AwaitingMod.Length > 0),
            RestoreDue = Networking.ModVersionJoin.RestoreDue,
            RunnerBusy = ClientUpdates.Busy,
        };
    }

    private static void AfterUpdateWrite()
    {
        try
        {
            ForgetLedgerRemovals();
            if (IsOpen && _tabState != null && _tabState.Selected == ModsTab.InstallMods) BuildInstallDetail();
        }
        catch { /* the next build reads the ledger */ }
    }

    private static bool OpenUpdateConfirm(string title, string body, IReadOnlyList<(string Label, string Value)> facts,
        string yesCaption, Action onYes, Action onNo)
    {
        var list = new List<DialogFact>();
        foreach (var (label, value) in facts) list.Add(new DialogFact(label, value));
        var dialog = UiDialog.Open(
            t => Ui.Panel(t, Owner, SettingsPanel.PluginId),
            "updates", "update-confirm", "updates",
            title, UiRichText.Literal(body), yesCaption, LabelTone.Ok, "Cancel", facts: list);
        // A confirmation another dialog replaces counts as Cancel: the runner must never wait for it forever.
        dialog.OnYes(onYes).OnNo(onNo).OnDropped(onNo);
        return dialog.IsOpen;
    }

    private static void ShowUpdateResult(string title, string body)
    {
        try
        {
            UiDialog.Open(
                t => Ui.Panel(t, Owner, SettingsPanel.PluginId),
                "updates", "update-result", "updates",
                title, UiRichText.Literal(body), "OK", LabelTone.Normal, "");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(ClientUpdates.LogPrefix + "result dialog failed: " + e.Message);
        }
    }

    /// <summary>
    /// A join screens' read brought a fresh index body (JoinPackageIndex). Feed it to the Install tab's catalog (off the
    /// main thread) so detection sees it, without a request of its own.
    /// </summary>
    internal static void AdoptJoinIndex(string json, string stamp)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return;
        Progenitor.Dev.MainThread.Post(() =>
        {
            try
            {
                if (!ThunderstoreEnabled) return;
                EnsureBindings();
                EnsureCatalog();
                if (_catalog is not InstallCatalogService service) return;
                Task.Run<CatalogLoadResult>(() =>
                {
                    try { return service.LoadIndexFromJson(json, stamp); }
                    catch (Exception e) { return new CatalogLoadResult(false, e.Message, 0, null); }
                }).ContinueWith(t =>
                {
                    bool done = t.Status == TaskStatus.RanToCompletion;
                    bool ok = done && t.Result.Ok;
                    string error = done ? (t.Result.Error ?? "") : "failed";
                    Progenitor.Dev.MainThread.Post(() =>
                    {
                        if (ok) NoteIndexContent("join-index");
                        else Plugin.Logger?.LogInfo(ClientUpdates.LogPrefix + "the join read's index was not adopted: " + error);
                    }, Owner);
                }, TaskScheduler.Default);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning(ClientUpdates.LogPrefix + "join index adoption failed: " + e.Message);
            }
        }, Owner);
    }

    /// <summary>
    /// The title screen: serve the cached index (no request) so the badge is right before any open, and run a pass.
    /// Called once per Core generation from the Mods entry's tick.
    /// </summary>
    internal static void PrimeUpdates()
    {
        try
        {
            EnsureBindings();
            EnsureCatalog();
            ServeCachedIndexNow();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(ClientUpdates.LogPrefix + "could not serve the cached index: " + e.Message);
        }

        RecheckUpdates("start");
    }
#endif

#if !PROGENITOR_TESTS
    private static double _sourceNextCheck;

    /// <summary>
    /// AUTOUPDATE2 (phase 1 open issue 3): <c>[Install] DevThunderstoreUrl</c> takes effect without a Core reload. Every
    /// two seconds (the Mods entry's tick) the index URL the catalog was built for is compared with the one the setting
    /// gives now (the loopback and developer-tree rule of <see cref="ThunderstoreMirror"/> still decides); on a change the
    /// catalog, its refresher and the Install tab are rebuilt, the new URL's cached index is served and one check is made
    /// (a dev-only setting change, not a timer). Nothing changes while an update or install runs; the next tick retries.
    /// INSTALLERR1 (live 2026-10-14: a URL cleared in a world kept the stand-in's list and checked the old http base): it
    /// runs in a world too, every Install-tab build forces it (<paramref name="force"/>), and a shown Install tab is redrawn
    /// for the new source (<paramref name="rebuildPanes"/>). It also watches for the network coming back (<see cref="WatchNetworkForRetry"/>).
    /// </summary>
    internal static void FollowIndexSource() => FollowIndexSource(force: false, rebuildPanes: true);

    private static bool _followingSource;

    internal static void FollowIndexSource(bool force, bool rebuildPanes)
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (!force && now < _sourceNextCheck) return;
        _sourceNextCheck = now + 2.0;
        try { WatchNetworkForRetry(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Install: network watch threw: " + e.Message); }
        if (_catalog is not InstallCatalogService) return;
        string want;
        try { want = CatalogFetchOptions.Default.IndexUrl; }
        catch { return; }
        // INSTALLERR1: never mid-update, mid-download or mid-confirm: the switch waits until the flow ends (next tick).
        var decision = IndexSourceFollow.Decide(want, _catalogIndexUrl, ClientUpdates.Busy, _installPane?.FlowActive == true);
        if (decision == IndexSourceDecision.Same) return;
        if (decision == IndexSourceDecision.Defer)
        {
            if (!string.Equals(_sourceDeferredFor, want, StringComparison.Ordinal))
            {
                _sourceDeferredFor = want;
                Plugin.Logger?.LogInfo("Install: the index source changed to " + want + "; switching once the running install or update ends.");
            }
            return;
        }

        _sourceDeferredFor = "";
        string was = _catalogIndexUrl;
        Plugin.Logger?.LogInfo("Install: the index source changed (" + was + " -> " + want + "); the catalog is rebuilt for it.");
        _followingSource = true;
        try
        {
            _catalog = null;
            _installPane = null;
            try
            {
                EnsureBindings();
                EnsureCatalog();
                ServeCachedIndexOffThreadThenCheck();
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Install: rebuilding the catalog for the new index source failed: " + e.Message); }
            RecheckUpdates("index-source");
            if (rebuildPanes && InstallTabShown)
            {
                try { BuildInstallPanes(); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Install: redrawing the Install tab for the new index source failed: " + e.Message); }
            }
        }
        finally { _followingSource = false; }
    }

    private static string _sourceDeferredFor = "";

    private static bool InstallTabShown => IsOpen && _tabState != null && _tabState.Selected == ModsTab.InstallMods;

    /// <summary>
    /// INSTALLERR1: after a source change, the new source's cached index (several MB of JSON) is read and parsed off the
    /// main thread, as the refresh's own body is; then, on the main thread, it is handed to the pane, the one check is
    /// queued (after the cache, so an older copy never lands over a fresher answer) and a shown Install tab is redrawn.
    /// The pane reads as loading meanwhile.
    /// </summary>
    private static void ServeCachedIndexOffThreadThenCheck()
    {
        var refresher = _indexRefresher;
        if (refresher == null || !ThunderstoreEnabled) return;
        var pane = EnsureInstallPane();
        pane.NoteLoadStarted();
        System.Threading.Tasks.Task.Run(() =>
        {
            if (refresher.CacheServed) return (CatalogLoadResult?)null;
            return refresher.TryServeCache(out var cached) ? cached : (CatalogLoadResult?)null;
        }).ContinueWith(t =>
        {
            CatalogLoadResult? served = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : null;
            Progenitor.Dev.MainThread.Post(() =>
            {
                if (!ReferenceEquals(refresher, _indexRefresher)) return; // another source change replaced it
                if (served is { } cached && ReferenceEquals(pane, _installPane))
                {
                    pane.NoteLoadResult(cached, refresher.SourceUrl, fromCache: true);
                    NoteIndexContent("index-cache-served");
                }

                if (ThunderstoreEnabled) QueueInstallIndexLoad();
                if (InstallTabShown && ReferenceEquals(pane, _installPane))
                {
                    try { BuildInstallPanes(); }
                    catch (Exception e) { Plugin.Logger?.LogWarning("Install: redrawing the Install tab after the cache was served failed: " + e.Message); }
                }
            }, Owner);
        }, System.Threading.Tasks.TaskScheduler.Default);
    }

    private static bool? _networkWasUp;

    /// <summary>
    /// INSTALLERR1: the network came back (no usable interface, then one): retry a failed check at once instead of waiting
    /// for the ladder. Read every two seconds, only while the Install tab is shown with a failed check; no event handler is
    /// attached, so nothing is left behind on unload.
    /// </summary>
    private static void WatchNetworkForRetry()
    {
        var pane = _installPane;
        if (pane == null || pane.IndexProblemShown == null || !InstallTabShown)
        {
            _networkWasUp = null;
            return;
        }

        bool up;
        try { up = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(); }
        catch { return; }
        bool cameBack = _networkWasUp == false && up;
        _networkWasUp = up;
        if (!cameBack) return;
        Plugin.Logger?.LogInfo("Install: the network is back; retrying the mod list now (" + pane.RetryNowIfFailing() + ").");
    }

    /// <summary>The index URL the catalog was built for (bridge readback).</summary>
    internal static string CatalogIndexUrl => _catalogIndexUrl;
#endif

    /// <summary>
    /// Bridge (<c>auto_update</c>): the update state, the badge as drawn, the Install tab list and the pre-join prompt.
    /// </summary>
    internal static Dictionary<string, object?> DescribeUpdates()
    {
        WireClientUpdates();
        var d = ClientUpdates.Describe();
        try { d["auto"] = ClientAutoUpdate.Describe(); }
        catch (Exception e) { d["auto"] = new Dictionary<string, object?> { ["error"] = e.Message }; }
        try { d["self"] = SelfUpdates.Describe(); }
        catch (Exception e) { d["self"] = new Dictionary<string, object?> { ["error"] = e.Message }; }
        d["installList"] = _installPane == null ? null : new Dictionary<string, object?>
        {
            ["builtFor"] = _installPane.UpdatesBuiltFor,
            ["current"] = InstallModsPane.UpdatesStamp(),
        };
#if !PROGENITOR_TESTS
        try { d["badgeDrawn"] = NativeModsEntry.DescribeBadge(); }
        catch (Exception e) { d["badgeDrawn"] = new Dictionary<string, object?> { ["error"] = e.Message }; }
        try { d["preJoin"] = Networking.PreJoinGate.Describe(); }
        catch (Exception e) { d["preJoin"] = new Dictionary<string, object?> { ["error"] = e.Message }; }
        d["indexSource"] = new Dictionary<string, object?> { ["catalogBuiltFor"] = _catalogIndexUrl, ["settingNow"] = CatalogFetchOptions.Default.IndexUrl, ["mirror"] = ThunderstoreMirror.Note };
#endif
        return d;
    }

    /// <summary>
    /// Bridge actions: <c>recheck</c>, <c>pin</c>, <c>unpin</c>, <c>update</c>, <c>update_all</c>; AUTOUPDATE2: <c>auto_run</c>,
    /// <c>rollback</c> (<c>plugin</c>), <c>mode</c> (<c>package</c>, <c>choice</c>), <c>clear_failed</c>, <c>forget_attempts</c>,
    /// <c>start_check</c>, <c>subtitle_dot</c> (<c>choice</c> on/off).
    /// </summary>
    internal static Dictionary<string, object?> UpdatesAction(string action, string? package, string? host = null, int port = 0,
        string? plugin = null, string? choice = null)
    {
        WireClientUpdates();
        string message;
        bool ok;
        switch (action)
        {
            case "recheck":
                RecheckUpdates("bridge");
                ok = true;
                message = "detection queued (no request)";
                break;
            case "pin":
            case "unpin":
                ok = ClientUpdates.SetPinned(package ?? "", action == "pin", out message);
                break;
            case "update":
                ok = ClientUpdates.BeginUpdate(new[] { package ?? "" }, "bridge-update", confirm: true,
                    ClientUpdates.ShowOutcome,
                    out message);
                break;
            case "update_all":
                if (_installPane != null)
                {
                    message = _installPane.PressUpdateAll();
                    ok = ClientUpdates.Busy;
                }
                else
                {
                    var all = new List<string>();
                    foreach (var i in ClientUpdates.Report.Items) if (!i.Pinned) all.Add(i.Package);
                    ok = ClientUpdates.BeginUpdate(all, "bridge-update-all", confirm: true,
                        ClientUpdates.ShowOutcome,
                        out message);
                }
                break;
#if !PROGENITOR_TESTS
            case "prejoin_preview":
            {
                var preview = Networking.PreJoinGate.Preview(host ?? "", port);
                var r = DescribeUpdates();
                r["ok"] = true;
                r["message"] = "what the pre-join prompt would offer for " + host + ":" + port + " (nothing launched)";
                r["preview"] = preview;
                return r;
            }
            case "prejoin_forget":
                message = "forgot " + Networking.PreJoinGate.ForgetDismissed() + " Join anyway answer(s) of this session";
                ok = true;
                break;
            case "check":
            {
                // One conditional index request now (the stand-in or Thunderstore), as the Install tab's own check.
                EnsureBindings();
                EnsureCatalog();
                if (!ThunderstoreEnabled) { ok = false; message = "Use Thunderstore is off"; break; }
                QueueInstallIndexLoad(force: true);
                ok = true;
                message = "index check queued (" + _catalogIndexUrl + ")";
                break;
            }
            case "auto_run":
            {
                string closed = ClientAutoUpdate.WindowNow();
                var due = ClientAutoUpdate.Due();
                if (closed.Length > 0) { ok = false; message = "the apply window is closed: " + closed; }
                else if (due.Count == 0) { ok = false; message = "nothing is due for an automatic update"; }
                else { message = ClientAutoUpdate.RunPass("auto-bridge", due); ok = message.StartsWith("started", StringComparison.Ordinal); }
                break;
            }
            case "rollback":
            {
                string id = (plugin ?? "").Trim();
                string pkg = (package ?? "").Trim();
                if (pkg.Length == 0) pkg = PackageOfPlugin(id);
                message = id.Length == 0 ? "plugin is required" : ClientAutoUpdate.RollBack(id, pkg);
                ok = message.StartsWith("Rolled back", StringComparison.Ordinal);
                break;
            }
            case "mode":
            {
                string pkg = package ?? "";
                string installed = "";
                foreach (var m in ClientUpdateInputs.ReadManaged(BepInEx.Paths.GameRootPath))
                    if (string.Equals(m.Key, pkg, StringComparison.OrdinalIgnoreCase)) installed = m.Version;
                ok = ClientAutoUpdate.SetModeChoice(pkg, choice ?? "Global", installed, out message);
                break;
            }
            case "clear_failed":
                message = "cleared " + ClientAutoUpdate.ClearFailed(package ?? "") + " failed version(s)";
                ok = true;
                break;
            case "forget_attempts":
                message = "forgot " + ClientAutoUpdate.ForgetAttempts() + " attempt(s) of this session";
                ok = true;
                break;
            case "start_check":
                message = string.Join("; ", ClientAutoUpdate.CheckAfterStart());
                ok = true;
                break;
            case "screenshot":
            {
                // The game's own frame, written at the end of this frame (no window focus, no input).
                string path = (choice ?? "").Trim();
                if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !System.IO.Path.IsPathRooted(path))
                {
                    ok = false;
                    message = "choice must be an absolute .png path";
                    break;
                }

                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                UnityEngine.ScreenCapture.CaptureScreenshot(path);
                ok = true;
                message = "capture queued: " + path;
                break;
            }
            case "subtitle_dot":
                NativeModsEntry.SubtitleDot = string.Equals(choice, "on", StringComparison.OrdinalIgnoreCase);
                message = "subtitle dot " + (NativeModsEntry.SubtitleDot ? "on" : "off");
                ok = true;
                break;
#endif
            default:
                ok = false;
                message = "unknown action " + action;
                break;
        }

        var result = DescribeUpdates();
        result["ok"] = ok;
        result["message"] = message;
        return result;
    }
}
