#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Dev;
using Progenitor.Networking;
using Progenitor.WorldRestore;

namespace Progenitor.Install;

/// <summary>
/// AUTOUPDATE3, server: the live seams of <see cref="ServerAutoUpdate"/>: the <c>[AutoUpdate]</c> section of the server's
/// <c>progenitor.cfg</c>, its own install catalog (built like the remote installer's, owner <c>auto-update</c> in the
/// Thunderstore traffic lines), the version store over the server's folder, the players online, the world-loaded and
/// player-connected events, the pre-update backup (<see cref="ServerBackupsJob"/>), the save-and-quit stop path
/// (<see cref="ServerStopCommand"/>), the admin notices (<see cref="JoinAdminServer.SendUpdateNotice"/>) and the Server tab's
/// actions. A tick once a second runs the cadence, the held updates and Restart when empty. Server only; context-owned: every
/// seam is dropped at uninstall.
/// </summary>
internal static class ServerAutoUpdateLive
{
    private static readonly object Owner = new();
    private static bool _installed;
    private static int _generation;
    private static IDisposable? _worldLoaded;
    private static IDisposable? _connected;
    private static ModVersionService? _store;
    private static InstallCatalogService? _catalog;
    private static string _catalogUrl = "";
    private static readonly object CatalogGate = new();

    private static ConfigEntry<string>? _mods;
    private static ConfigEntry<string>? _majors;
    private static ConfigEntry<int>? _checkHours;
    private static ConfigEntry<bool>? _restartWhenEmpty;
    private static ConfigEntry<int>? _emptyMinutes;
    private static ConfigEntry<bool>? _backup;
    private static ConfigEntry<int>? _backupKeep;
    private static ConfigEntry<int>? _skipHours;
    private static ConfigEntry<int>? _warnMinutes;
    private static ConfigEntry<string>? _exclude;
    private static ConfigEntry<string>? _pins;
    private static ConfigEntry<string>? _hostRestarts;

    internal static void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        int gen = ++_generation;
        string root;
        try { root = Paths.BepInExRootPath; }
        catch { return; }

        ServerAutoUpdate.Reset();
        ServerAutoUpdate.SettingsProbe = ReadSettings;
        ServerAutoUpdate.BepInExRootProbe = () => root;
        ServerAutoUpdate.GameRootProbe = () => Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        ServerAutoUpdate.OnlineProbe = () => Progenitor.Players.PlayerDirectory.Online().Count;
        ServerAutoUpdate.CacheStampProbe = () =>
        {
            var cache = new IndexDiskCache(root, RemoteInstallEvents.CurrentSettings.IndexUrl);
            return cache.TryReadStamp(out var stamp) ? stamp : null;
        };
        ServerAutoUpdate.StoreProbe = Store;
        ServerAutoUpdate.LoadFactsProbe = LoadFactsOf;
        ServerAutoUpdate.ClientsNeedProbe = ClientsNeed;
        ServerAutoUpdate.RunOnMain = work => MainThread.Run(work, Owner);
        ServerAutoUpdate.Schedule = (seconds, action) => MainThread.PostDelayed(seconds, action, Owner);
        ServerAutoUpdate.RunOff = work => Task.Run(work);
        ServerAutoUpdate.Log = line => Plugin.Logger?.LogInfo(line);
        ServerAutoUpdate.AuditSink = line => AuditLogFile.Append(root, ServerUpdateAudit.FileName, line);
        ServerAutoUpdate.Pipeline = Pipeline(root);
        ServerAutoUpdate.StartBackup = StartBackup;
        ServerAutoUpdate.BackupEntriesProbe = BackupEntries;
        ServerAutoUpdate.NotifyAdmins = notice => MainThread.Post(() => JoinAdminServer.SendUpdateNotice(notice), Owner);
        ServerAutoUpdate.Broadcast = text => MainThread.Post(() =>
        {
            try { Progenitor.Chat.Chat.Broadcast(text); }
            catch (Exception e) { Plugin.Logger?.LogWarning("AutoUpdate: broadcast threw: " + e.Message); }
        }, Owner);
        ServerAutoUpdate.StopServer = (reply, refused) =>
        {
            string last = "";
            ServerStopCommand.Execute(true, line =>
            {
                last = line;
                reply(line);
                Plugin.Logger?.LogInfo("AutoUpdate: stop: " + line);
            }, reason => refused(reason + (last.Length > 0 ? ": " + last : "")));
        };
        ServerAutoUpdate.OtherBusyProbe = () =>
        {
            if (ServerBackupsJob.Busy) return true;
            try { return (ServerBackupsServer.CountdownProbe?.Invoke() ?? -1) >= 0; }
            catch { return false; }
        };
        JoinAdminServer.UpdateAction = Action;
        JoinAdminServer.UpdatesState = State;

        SharedConfig.BindWhenAvailable("[" + ServerUpdateSettings.Section + "] (server)", Bind);
        try { _worldLoaded = Events.EventBus.On<WorldState.WorldLoaded>(_ => ServerAutoUpdate.OnWorldLoaded(), priority: 400); }
        catch (Exception e) { Plugin.Logger?.LogWarning("AutoUpdate: subscribing to the world load threw: " + e.Message); }
        try { _connected = Events.EventBus.On<Events.PlayerConnected>(_ => ServerAutoUpdate.OnPlayerConnected(), priority: 400); }
        catch (Exception e) { Plugin.Logger?.LogWarning("AutoUpdate: subscribing to player connects threw: " + e.Message); }

        // A Core installed into a running world (a hot swap): the world is already loaded. At a server start Core installs
        // before the server world exists and the WorldLoaded event marks it. (Live 2026-10-09: ModSave's save folder is not
        // known yet at this point of a swap, so it cannot be the test.)
        try
        {
            var world = Worlds.Server;
            if (world != null && world.IsCreated) ServerAutoUpdate.OnWorldLoaded();
        }
        catch { /* the WorldLoaded event marks it */ }

        void Tick()
        {
            if (gen != _generation) return;
            try { ServerAutoUpdate.Tick(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("AutoUpdate: the tick threw: " + e.Message); }
            MainThread.PostDelayed(1.0, Tick, Owner);
        }

        MainThread.PostDelayed(1.0, Tick, Owner);
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        _generation++;
        try { _worldLoaded?.Dispose(); } catch { /* teardown */ }
        try { _connected?.Dispose(); } catch { /* teardown */ }
        _worldLoaded = null;
        _connected = null;
        JoinAdminServer.UpdateAction = null;
        JoinAdminServer.UpdatesState = null;
        // A Restart now countdown ends with this generation (review 2026-10-09): say so to the players and the admins now,
        // directly (the main-thread posts of this owner are dropped below), instead of leaving them waiting for a restart.
        string? cancelled = null;
        try { cancelled = ServerAutoUpdate.OnUnloading(); }
        catch { /* teardown */ }
        if (cancelled != null)
        {
            try { Progenitor.Chat.Chat.Broadcast(cancelled); }
            catch (Exception e) { Plugin.Logger?.LogWarning("AutoUpdate: broadcast threw: " + e.Message); }
            try
            {
                JoinAdminServer.SendUpdateNotice(new AdminUpdateNotice
                {
                    Kind = "failed", Text = cancelled, OfferRestart = true, Signature = "restart-unloaded|" + DateTime.UtcNow.Ticks,
                });
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("AutoUpdate: the notice threw: " + e.Message); }
        }

        ServerAutoUpdate.Reset();
        lock (CatalogGate)
        {
            try { _catalog?.DiscardDownloads(); } catch { /* teardown */ }
            _catalog = null;
            _catalogUrl = "";
        }

        _store = null;
        _mods = _majors = _exclude = _pins = _hostRestarts = null;
        _checkHours = _emptyMinutes = _backupKeep = _skipHours = _warnMinutes = null;
        _restartWhenEmpty = _backup = null;
        try { MainThread.DropOwnedBy(Owner, typeof(ServerAutoUpdateLive).Assembly); }
        catch { /* teardown */ }
    }

    // ---- settings --------------------------------------------------------------------------------------------

    private static void Bind(ConfigFile file)
    {
        try
        {
            string s = ServerUpdateSettings.Section;
            _mods = ConfigHelper.Bind(file, s, ServerUpdateSettings.ModsKey, ServerUpdateSettings.DefaultMode,
                "Server: Automatic, Notify or Off. Automatic applies updates of the Thunderstore mods Progenitor installed on this server "
                + "(a server-only hot-loadable mod at once, one players also need once the server is empty, one that needs a restart at "
                + "the next start); Notify tells admins; Off checks nothing. On a client this key is the player's own setting (Preferences).");
            _majors = ConfigHelper.Bind(file, s, ServerUpdateSettings.MajorKey, UpdateSettings.DefaultMajor,
                "Server: Automatic or Notify. Automatic also applies a new major version of a mod. Mods only: Progenitor itself "
                + "uses date versions and follows [AutoUpdate] Progenitor.");
            _checkHours = ConfigHelper.Bind(file, s, ServerUpdateSettings.CheckHoursKey, ServerUpdateSettings.DefaultCheckHours,
                "Server: how often (hours, 1 to 168) the server makes one conditional request for the package index when no other part "
                + "of it read the index in that time. A read by the install window or a remote install counts as the check.");
            _restartWhenEmpty = ConfigHelper.Bind(file, s, ServerUpdateSettings.RestartWhenEmptyKey, false,
                "Server: with updates waiting for a restart, save and quit the first time nobody is online for EmptyMinutes. Needs "
                + "HostRestarts (Progenitor never starts the server again).");
            _emptyMinutes = ConfigHelper.Bind(file, s, ServerUpdateSettings.EmptyMinutesKey, ServerUpdateSettings.DefaultEmptyMinutes,
                "Server: the quiet period (minutes, 1 to 120) the server must be empty before Restart when empty acts and before an "
                + "update players also need is applied.");
            _backup = ConfigHelper.Bind(file, s, ServerUpdateSettings.BackupKey, false,
                "Server: make one world backup (kind pre-update) before each batch of updates is applied.");
            _backupKeep = ConfigHelper.Bind(file, s, ServerUpdateSettings.BackupKeepKey, WorldBackupRetention.PreUpdateDefaultKeep,
                "Server: how many pre-update backups are kept (1 to 20), counted only among them: manual and scheduled backups are "
                + "never deleted for them.");
            _skipHours = ConfigHelper.Bind(file, s, ServerUpdateSettings.BackupSkipHoursKey, ServerUpdateSettings.DefaultSkipHours,
                "Server: no pre-update backup is made when a manual, scheduled or pre-update backup is newer than this many hours "
                + "(0 to 168; 0 always makes one).");
            _warnMinutes = ConfigHelper.Bind(file, s, ServerUpdateSettings.WarnMinutesKey, ServerUpdateSettings.DefaultWarnMinutes,
                "Server: how long Restart now warns the players online first (minutes, 1 to 60).");
            _exclude = ConfigHelper.Bind(file, s, ServerUpdateSettings.ExcludeKey, "",
                "Server: Thunderstore packages (Namespace-Name, comma separated) never updated automatically.");
            _pins = ConfigHelper.Bind(file, s, ServerUpdateSettings.PinsKey, "",
                "Server: packages held at a version (Namespace-Name@1.2.0, comma separated).");
            _hostRestarts = ConfigHelper.Bind(file, s, ServerUpdateSettings.HostRestartsKey, "",
                "Server, operator: what starts this server again after it quits (a Windows service, a hosting panel, a script). "
                + "Progenitor never starts a server; Restart when empty stays off while this is empty.");
            var now = ReadSettings();
            Plugin.Logger?.LogInfo("AutoUpdate: server mods " + UpdateSettings.Word(now.Effective) + ", major versions "
                + (now.MajorsAutomatic ? "Automatic" : "Notify") + ", check every " + now.CheckHours + " h; Restart when empty "
                + (now.RestartWhenEmpty ? "on" : "off") + (now.ComesBack ? " (restarted by: " + now.HostRestarts + ")" : " (nothing restarts the server)")
                + "; pre-update backup " + (now.BackupBeforeUpdate ? "on" : "off") + "; audit log BepInEx/" + ServerUpdateAudit.FileName);
        }
        catch (Exception e)
        {
            _mods = null;
            Plugin.Logger?.LogWarning("AutoUpdate: binding [" + ServerUpdateSettings.Section + "] threw (" + e.Message + "); server updates stay Off.");
        }
    }

    /// <summary>
    /// Bridge: write one <c>[AutoUpdate]</c> entry as the operator would in the cfg (no lock, no admin check: the bridge is
    /// the developer's own). Null when stored, else why not.
    /// </summary>
    internal static string? SetForBridge(string key, string value)
    {
        ConfigEntryBase? entry = key switch
        {
            ServerUpdateSettings.ModsKey => _mods,
            ServerUpdateSettings.MajorKey => _majors,
            ServerUpdateSettings.CheckHoursKey => _checkHours,
            ServerUpdateSettings.RestartWhenEmptyKey => _restartWhenEmpty,
            ServerUpdateSettings.EmptyMinutesKey => _emptyMinutes,
            ServerUpdateSettings.BackupKey => _backup,
            ServerUpdateSettings.BackupKeepKey => _backupKeep,
            ServerUpdateSettings.BackupSkipHoursKey => _skipHours,
            ServerUpdateSettings.WarnMinutesKey => _warnMinutes,
            ServerUpdateSettings.ExcludeKey => _exclude,
            ServerUpdateSettings.PinsKey => _pins,
            ServerUpdateSettings.HostRestartsKey => _hostRestarts,
            _ => null,
        };
        if (entry == null) return "unknown or unbound [AutoUpdate] key '" + key + "'";
        try
        {
            object parsed;
            if (entry.SettingType == typeof(int))
            {
                if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n))
                    return "not a whole number";
                parsed = n;
            }
            else if (entry.SettingType == typeof(bool)) parsed = value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            else parsed = value;
            // Only this key's line is written (review 2026-10-09): a whole-file save put the running Core's stale values over
            // another session's hand edit of the same server's cfg (live 2026-10-09, [Install] DevThunderstoreUrl).
            if (!ServerOptionsLive.StoreOneLine(entry, parsed, out string error)) return error;
            Plugin.Logger?.LogInfo("AutoUpdate: [" + ServerUpdateSettings.Section + "] " + key + " = " + value + " (dev bridge)");
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>The settings now (Off until bound: fail closed).</summary>
    internal static ServerUpdateSettings ReadSettings()
    {
        try
        {
            if (_mods == null) return ServerUpdateSettings.Unbound;
            return new ServerUpdateSettings
            {
                Bound = true,
                Mode = ServerUpdateSettings.ParseMode(_mods.Value),
                MajorsAutomatic = UpdateSettings.MajorAutomatic(_majors?.Value),
                CheckHours = ServerUpdateSettings.ClampCheckHours(_checkHours?.Value ?? ServerUpdateSettings.DefaultCheckHours),
                RestartWhenEmpty = _restartWhenEmpty?.Value ?? false,
                EmptyMinutes = ServerUpdateSettings.ClampEmptyMinutes(_emptyMinutes?.Value ?? ServerUpdateSettings.DefaultEmptyMinutes),
                BackupBeforeUpdate = _backup?.Value ?? false,
                PreUpdateBackupKeep = WorldBackupRetention.ClampPreUpdateKeep(_backupKeep?.Value ?? WorldBackupRetention.PreUpdateDefaultKeep),
                PreUpdateBackupSkipHours = ServerUpdateSettings.ClampSkipHours(_skipHours?.Value ?? ServerUpdateSettings.DefaultSkipHours),
                WarnMinutes = ServerUpdateSettings.ClampWarnMinutes(_warnMinutes?.Value ?? ServerUpdateSettings.DefaultWarnMinutes),
                Exclude = ServerUpdateSettings.ParseExclude(_exclude?.Value),
                Pins = ServerUpdateSettings.ParsePins(_pins?.Value),
                HostRestarts = (_hostRestarts?.Value ?? "").Trim(),
                // Automatic writes mods as a remote install does: off there means Notify here (unbound reads off).
                RemoteInstallEnabled = RemoteInstallEvents.CurrentSettings.Enabled,
            };
        }
        catch { return ServerUpdateSettings.Unbound; }
    }

    // ---- the store, the catalog and the pipeline ---------------------------------------------------------------

    private static ModVersionService? Store()
    {
        if (_store != null) return _store;
        _store = ModVersionLive.CreateServerService(l => Plugin.Logger?.LogInfo("AutoUpdate: " + l), l => Plugin.Logger?.LogWarning("AutoUpdate: " + l));
        return _store;
    }

    /// <summary>The auto-updater's own catalog (rebuilt when the remote installer's Thunderstore base URL changes).</summary>
    private static InstallCatalogService? Catalog(string root, out RemoteInstallSettings settings)
    {
        settings = RemoteInstallEvents.CurrentSettings;
        var transport = RemoteInstallEvents.SharedTransport;
        if (transport == null) return null;
        lock (CatalogGate)
        {
            if (_catalog == null || !string.Equals(_catalogUrl, settings.IndexUrl, StringComparison.Ordinal))
            {
                try { _catalog?.DiscardDownloads(); } catch { /* replaced */ }
                _catalog = RemoteInstallEvents.CreateServerCatalog(root, transport, settings);
                _catalog.IndexCheckOwner = "auto-update";
                _catalogUrl = settings.IndexUrl;
            }

            return _catalog;
        }
    }

    private static ServerUpdatePipeline Pipeline(string root) => new()
    {
        ReadIndex = async (request, ct) =>
        {
            var catalog = Catalog(root, out var settings);
            if (catalog == null) return new ServerIndexRead { Ok = false, Error = "the server's Thunderstore transport is not ready" };
            if (!settings.BaseUrlValid) return new ServerIndexRead { Ok = false, Error = "[RemoteInstall] ThunderstoreBaseUrl is not an https URL" };
            if (request)
            {
                var load = await catalog.LoadIndexAsync(ct, force: false).ConfigureAwait(false);
                if (!load.Ok) return new ServerIndexRead { Ok = false, Error = load.Error ?? "the index could not be read", Requested = true };
                return new ServerIndexRead { Ok = true, Requested = true, Entries = catalog.Search(null), Stamp = load.IndexStamp ?? "", IsLocal = catalog.IsLocalKey };
            }

            if (!catalog.IndexCheck.Cache.TryRead(CatalogFetcher.IndexMaxBytes, out string body, out _))
                return new ServerIndexRead { Ok = false, Error = "no cached index yet" };
            var fromCache = catalog.LoadIndexFromJson(body, "cache");
            if (!fromCache.Ok) return new ServerIndexRead { Ok = false, Error = fromCache.Error ?? "the cached index could not be read" };
            return new ServerIndexRead { Ok = true, Entries = catalog.Search(null), Stamp = "cache", IsLocal = catalog.IsLocalKey };
        },
        Inspect = async (key, ct) =>
        {
            var catalog = Catalog(root, out var settings);
            if (catalog == null) return new InspectCallResult(false, "no install catalog", null, null);
            // The remote installer's checks: a V Rising listing, a download on the configured Thunderstore (the operator's
            // local index excepted).
            // INDEX1: a Progenitor Index entry is checked against its signed pin by the download itself.
            if (catalog.TryGetEntry(key, out var entry) && entry != null && !catalog.IsLocalKey(key) && !entry.FromIndex)
            {
                if (!string.IsNullOrEmpty(entry.ListingUrl) && entry.ListingUrl!.IndexOf("/c/v-rising/", StringComparison.OrdinalIgnoreCase) < 0)
                    return new InspectCallResult(false, "the listing is not in the V Rising community", entry, null);
                if (!settings.IsThunderstoreUrl(entry.DownloadUrl))
                    return new InspectCallResult(false, "the download link is not on " + settings.ThunderstoreBaseUrl, entry, null);
            }

            return await catalog.DownloadUpdateAndInspectAsync(key, ct).ConfigureAwait(false);
        },
        Plan = async (key, ct) =>
        {
            await MainThread.Run(() => { RemoteInstallEvents.RefreshLoadedCache(); return true; }, Owner).ConfigureAwait(false);
            var catalog = Catalog(root, out _);
            return catalog == null ? InstallPlanSet.Refuse("no install catalog") : await catalog.PlanWithDependenciesAsync(key, InstallTarget.LocalServer, ct).ConfigureAwait(false);
        },
        Approve = (set, version) => Approve(root, set, version),
        Release = key =>
        {
            lock (CatalogGate) _catalog?.ReleaseDownload(key);
        },
        Installed = key => DependencyInstalled(root, key),
    };

    private static ServerApprovedImage Approve(string root, InstallPlanSet set, string version)
    {
        var catalog = Catalog(root, out _);
        if (catalog == null) return new ServerApprovedImage { Error = "no install catalog" };
        var bytes = catalog.ApprovedImage(set, out string sha, out bool hot, out string notHot, out string error);
        if (bytes == null) return new ServerApprovedImage { Error = error.Length > 0 ? error : "the update has more than one file (update it from the Install tab)" };
        var plan = set.Root!;
        string v = string.IsNullOrWhiteSpace(plan.Entry.Manifest.Version) ? version : plan.Entry.Manifest.Version!;
        return new ServerApprovedImage
        {
            Bytes = bytes,
            Sha256 = sha,
            Version = v,
            PluginId = plan.PluginId ?? "",
            Hot = hot,
            NotHotReason = notHot,
            Ledger = new ParkedLedgerRow
            {
                Sha256 = sha,
                Size = bytes.LongLength,
                Category = ServerInstallLedger.CategoryMod,
                Source = LedgerSpelling.SourceFor(plan.Entry, v),
                WrittenUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                PluginId = plan.PluginId ?? "",
                Route = ServerInstallLedger.RouteRemote,
            },
        };
    }

    /// <summary>A dependency is installed when the ledger names it or the server's installed state finds a copy of it.</summary>
    private static bool DependencyInstalled(string root, string key)
    {
        try
        {
            string game = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "";
            foreach (var m in ClientUpdateInputs.ReadManaged(game))
                if (string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)) return true;
            InstallCatalogService? catalog;
            lock (CatalogGate) catalog = _catalog;
            if (catalog == null || !catalog.TryGetEntry(key, out var entry) || entry == null) return false;
            var inventory = new List<InstalledInventoryRow>();
            var plugins = BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance?.Plugins;
            if (plugins != null)
                foreach (var pair in plugins)
                {
                    var info = pair.Value;
                    if (info?.Metadata == null) continue;
                    inventory.Add(new InstalledInventoryRow(info.Metadata.GUID ?? "", info.Metadata.Version?.ToString() ?? "", info.Location ?? "", null));
                }

            var query = InstalledState.Scan(root, null, inventory).ForRoot(InstalledState.ClientRoot);
            return InstalledState.Detect(entry, query).State != InstalledKind.NotInstalled;
        }
        catch { return false; }
    }

    /// <summary>Do clients need any of these plugins: the server asks for it at the handshake (any level but client-only), or the policy requires it.</summary>
    private static bool ClientsNeed(IReadOnlyList<string> pluginIds)
    {
        if (pluginIds == null || pluginIds.Count == 0) return true;
        try
        {
            foreach (var row in Handshake.CopyRequired())
                foreach (var id in pluginIds)
                    if (string.Equals(row.Guid, id, StringComparison.OrdinalIgnoreCase) && row.Level != Progenitor.Plugins.NetworkCompatibilityLevel.ClientOnly)
                        return true;
            var policy = JoinAdminServer.CurrentPolicy;
            foreach (var id in pluginIds)
                if (policy.IndexOfRequired(id) >= 0) return true;
            return false;
        }
        catch { return true; }
    }

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
                var active = Store()?.ActiveCopy(pluginId, out _, out _);
                stem = active == null ? "" : Path.GetFileNameWithoutExtension(active.Name);
            }
            catch { stem = ""; }
        }

        ReloadRecord? record = null;
        try { record = ReloadRecord.Read(Paths.BepInExRootPath, stem); }
        catch { record = null; }
        return new LoadFacts { Snapshot = snapshot, Registry = registry, Record = record, LoaderBusy = busy };
    }

    // ---- backups -------------------------------------------------------------------------------------------

    private static string? StartBackup(string label, int keep, Action<BackupOutcome> done)
    {
        if (!ServerBackupsServer.TryLayout(out var layout, out _, out string refusal)) return refusal;
        string? root = ServerBackupsServer.BepInExRootProbe?.Invoke();
        if (string.IsNullOrEmpty(root)) return "the BepInEx folder of this server is not known";
        return ServerBackupsJob.Start(WorldRestorePaths.KindPreUpdate, WorldRestorePaths.TriggerUpdate, "auto-update", layout, root!,
            ServerBackupsServer.BackupSettings().Policy, done, keep, label);
    }

    private static IReadOnlyList<WorldBackupEntry> BackupEntries()
    {
        if (!ServerBackupsServer.TryLayout(out var layout, out _, out _)) return Array.Empty<WorldBackupEntry>();
        return WorldBackupCatalog.List(ServerBackupsServer.Fs, layout, ServerBackupsServer.ZoneNow());
    }

    // ---- the Server tab -----------------------------------------------------------------------------------

    private static (bool Ok, string Error, string Note) Action(string op, RemoteInstallActor actor)
    {
        string by = (actor.CharacterName ?? "").Length > 0 ? actor.CharacterName! : "an admin";
        string? refusal;
        switch (op)
        {
            case JoinAdminOps.UpdateCheck:
                refusal = ServerAutoUpdate.CheckNow(by);
                return refusal == null ? (true, "", "Checking for updates now.") : (false, refusal, "");
            case JoinAdminOps.UpdateRestart:
                refusal = ServerAutoUpdate.RestartNow(by);
                if (refusal != null) return (false, refusal, "");
                int left = ServerAutoUpdate.CountdownSeconds;
                return (true, "", left >= 0 ? "The server restarts in " + (left >= 120 ? left / 60 + " minutes" : left + " seconds") + "; players are warned. You can cancel it."
                    : "Nobody is online: the server saves and quits now.");
            case JoinAdminOps.UpdateCancelRestart:
                refusal = ServerAutoUpdate.CancelRestart(by);
                return refusal == null ? (true, "", "The restart is cancelled; the updates wait for the next start.") : (false, refusal, "");
            default:
                return (false, JoinAdminPolicy.UnknownOpError, "");
        }
    }

    private static JoinAdminUpdates State()
    {
        var s = ReadSettings();
        var u = new JoinAdminUpdates
        {
            Line = ServerAutoUpdate.StatusLine(),
            Countdown = ServerAutoUpdate.CountdownSeconds,
            ComesBack = s.ComesBack,
            HostRestarts = s.HostRestarts,
        };
        u.RestartPending.AddRange(ServerAutoUpdate.RestartPending());
        var report = ServerAutoUpdate.Report;
        foreach (var i in report.Items)
        {
            if (i.Pinned || (i.Auto && s.Effective == UpdateMode.Automatic)) continue;
            u.Waiting.Add(i.Name + " " + i.Installed + " → " + i.Latest + (i.NotAutoReason.Length > 0 ? " · " + i.NotAutoReason : ""));
        }

        var desc = ServerAutoUpdate.Describe();
        if (desc.TryGetValue("held", out var held) && held is Dictionary<string, string> h)
            foreach (var kv in h) u.Waiting.Add(kv.Key + " · held: " + kv.Value);
        u.Recent.AddRange(ServerAutoUpdate.RecentLines());
        return u;
    }
}
#endif
