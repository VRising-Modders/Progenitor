#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using Progenitor.Dev;
using Progenitor.Networking;
using ProjectM;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Install;

/// <summary>
/// REMOTEINST1: the production wiring of the admin remote install channel (<c>'PrRi'</c>,
/// <see cref="CustomEventPolicy.RemoteInstallEventId"/>), modeled on <see cref="StopServerEvents"/>.
///
/// <para><b>Server.</b> Every request re-reads <c>User</c> off the sender entity the dispatcher
/// resolved from the connection and takes <c>IsAdmin</c>, <c>PlatformId</c> (the SteamID) and
/// <c>CharacterName</c> from that read — the same component reads the handshake and Stop server
/// already make. Nothing about identity or privilege travels in the request. The work runs in
/// <see cref="RemoteInstallService"/>: network and inspection on a worker, the one write step on
/// the main thread, every reply marshaled back to the main thread and sent only if the same user
/// is still connected.</para>
///
/// <para><b>Client.</b> The reply handler feeds <see cref="RemoteInstallClient.Shared"/>, which the
/// Install tab reads. The client declares <see cref="Handshake.Capability.RemoteInstall"/>; the
/// server pushes a status only to a client that declared it, and the client sends requests only
/// after a status arrived — so neither side ever sends this id to a peer that has no handler.</para>
///
/// <para>No native call is added: custom events, the <c>User</c> component read and the main-thread
/// queue are the paths <see cref="StopServerEvents"/>, <see cref="Handshake"/> and
/// <see cref="ServerSettings"/> already use.</para>
/// </summary>
internal static class RemoteInstallEvents
{
    public const int EventId = CustomEventPolicy.RemoteInstallEventId;

    private static readonly object Owner = new();
    private static bool _installed;
    private static RemoteInstallService? _service;
    // Off until [RemoteInstall] is bound: see RemoteInstallSettings.Unbound. The snapshot follows the bound entries (a cfg
    // edit the file watcher reloads applies on the next read; live 2026-10-10 it needed a Core reload).
    private static RemoteInstallSettings _settings = RemoteInstallSettings.Unbound;
    private static readonly RemoteInstallSettingsCache SettingsCache = new();
    private static BepInEx.Configuration.ConfigEntry<bool>? _enabled;
    private static BepInEx.Configuration.ConfigEntry<string>? _allowed;
    private static BepInEx.Configuration.ConfigEntry<string>? _baseUrl;
    private static BepInEx.Configuration.ConfigEntry<int>? _connect;
    private static BepInEx.Configuration.ConfigEntry<int>? _stall;
    private static BepInEx.Configuration.ConfigEntry<int>? _total;
    private static LazyHttpTransport? _http;
    private static ServerAdminList? _adminList;

    // Settings organization stage 5: [RemoteInstall] AllowedSteamIdsInGame (cfg-only); null until bound (not locked).
    private static BepInEx.Configuration.ConfigEntry<bool>? _allowedInGame;

    /// <summary>
    /// True while the operator keeps the install list in the cfg (<c>[RemoteInstall] AllowedSteamIdsInGame = false</c>).
    /// Read on every request, so a cfg edit the file watcher reloads takes effect at once.
    /// </summary>
    internal static bool AllowedSteamIdsLocked()
    {
        try { return _allowedInGame != null && !_allowedInGame.Value; }
        catch { return false; }
    }

    /// <summary>
    /// Read [RemoteInstall] again after an admin changed Enabled or AllowedSteamIds from Progenitor's page
    /// (<c>ServerOptionsLive.Set</c>), so the change applies to the next request.
    /// </summary>
    internal static void RebindSettings()
    {
        var file = Config.SharedConfig.TryGet();
        if (file != null && Plugin.IsServerProcess) BindSettings(file, probe: false);
    }

    internal static RemoteInstallService? Service => _service;

    internal static unsafe void Install()
    {
        if (_installed) return;
        if (Plugin.IsServerProcess) InstallServer();
        else InstallClient();
        _installed = true;
    }

    internal static unsafe void Uninstall()
    {
        try { CustomEvents.RemoveClientHandler(EventId, OnRequest); } catch { /* teardown */ }
        try { CustomEvents.RemoveHandler(EventId, OnReply); } catch { /* teardown */ }
        try { _service?.CancelRunning(); } catch { /* teardown */ }
        _service = null;
        if (ReferenceEquals(ServerAdminList.Shared, _adminList)) ServerAdminList.Shared = null;
        _adminList = null;
        _settings = RemoteInstallSettings.Unbound;
        SettingsCache.Clear();
        _enabled = null;
        _allowed = null;
        _baseUrl = null;
        _connect = null;
        _stall = null;
        _total = null;
        _allowedInGame = null;
        // The handler's connection pool would otherwise outlive this Core generation.
        try { _http?.Dispose(); } catch { /* teardown */ }
        _http = null;
        RemoteInstallBridgeOps.IsServer = null;
        RemoteInstallBridgeOps.ServerService = null;
        RemoteInstallBridgeOps.ServerSettings = null;
        RemoteInstallBridgeOps.ClientRoute = null;
        var client = RemoteInstallClient.Shared;
        client.Send = null;
        client.Changed = null;
        client.SteamIdFile = null;
        AdminListFile.LocalSteamIdProbe = null;
        AdminAuthFlow.Shared.Executor = null;
        AdminAuthFlow.Shared.RequestStatus = null;
        AdminAuthFlow.Shared.Enabled = false;
        if (!Plugin.IsServerProcess) client.Reset();
        try { MainThread.DropOwnedBy(Owner, typeof(RemoteInstallEvents).Assembly); } catch { /* teardown */ }
        IdentityCache.Clear();
        _installed = false;
    }

    // ---- server ----------------------------------------------------------------------------

    private static unsafe void InstallServer()
    {
        string root;
        try { root = Paths.BepInExRootPath; }
        catch { return; }

        _http = new LazyHttpTransport(Settings);
        string installRoot = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "";
        _adminList = new ServerAdminList(PersistentDataPath, installRoot)
        {
            LogOnce = line => Plugin.Logger?.LogWarning(line),
        };
        // ServerSettings' administrator edits authorize through the same reader (settings-scopes.md).
        ServerAdminList.Shared = _adminList;
        var service = new RemoteInstallService(
            root,
            Settings,
            _http,
            (transport, settings) => CreateServerCatalog(root, transport, settings))
        {
            PluginLoaded = id => LoadedPluginGuids().Contains(id),
            // SRVUNINST1: the server's loaded plugins and how sure it is of each one's files.
            PluginFacts = () => ServerPluginFacts(root, cached: true),
            // The uninstall itself identifies the files again, never from the status cache.
            PluginFactsFresh = () => ServerPluginFacts(root, cached: false),
            // MainThread.Run faults (rather than never finishing) when the work is dropped because
            // this Core generation is unloading, so the operation's cleanup still runs.
            RunOnMain = work => MainThread.Run(work, Owner),
            Audit = line =>
            {
                Plugin.Logger?.LogInfo(line);
                RemoteInstallAuditLog.Append(root, line);
            },
        };
        _service = service;
        RemoteInstallBridgeOps.IsServer = () => true;
        RemoteInstallBridgeOps.ServerService = () => _service;
        RemoteInstallBridgeOps.ServerSettings = Settings;
        CustomEvents.OnReceiveFromClient(EventId, OnRequest);

        Config.SharedConfig.BindWhenAvailable("[" + RemoteInstallSettings.Section + "]", file => BindSettings(file, probe: true));
    }

    /// <summary>
    /// The server's install catalog over its own BepInEx folder (the remote installer's, and since AUTOUPDATE3 the
    /// auto-updater's own instance): the local-server target is this process, so admission decides hot-load or restart for it.
    /// </summary>
    internal static InstallCatalogService CreateServerCatalog(string root, ICatalogTransport transport, RemoteInstallSettings settings)
    {
        var identities = Admission.CompatibilityIdentities.Current();
        var admission = new Admission.AdmissionPipeline(
            new Admission.BootstrapAdmissionSnapshotSource(identities),
            identities: identities,
            policyProvider: () => Admission.QualificationPolicyCache.Current(root));
        var catalog = new InstallCatalogService(
            root,
            transport,
            new CatalogFetchOptions { IndexUrl = settings.IndexUrl, Timeout = settings.TotalTimeout },
            localServerConfigured: true,
            admission: admission,
            serverBepInExRoot: root,
            thisProcessIsServer: true);
        // Planning runs on a worker: it reads the copy the main thread last took.
        catalog.LoadedPluginGuids = _ => _loadedCache;
        // INDEX1: the server installs and updates from the Progenitor Index too (null in tests).
        catalog.IndexSource = ProgenitorIndexSource.ForRoot(root);
        return catalog;
    }

    /// <summary>AUTOUPDATE3: the remote installer's settings now (Unbound until <c>[RemoteInstall]</c> is bound).</summary>
    internal static RemoteInstallSettings CurrentSettings => Settings();

    /// <summary>
    /// The settings now: the snapshot is rebuilt when the bound entries' values changed since it was built (a cfg edit, an
    /// admin's change), and kept otherwise. Unbound (off) until <c>[RemoteInstall]</c> is bound.
    /// </summary>
    private static RemoteInstallSettings Settings()
    {
        var enabled = _enabled;
        if (enabled == null) return _settings;
        try
        {
            var raw = new RemoteInstallRaw(enabled.Value, _allowed?.Value ?? "", _baseUrl?.Value ?? RemoteInstallSettings.DefaultBaseUrl,
                _connect?.Value ?? RemoteInstallSettings.DefaultConnectTimeoutSeconds,
                _stall?.Value ?? RemoteInstallSettings.DefaultStallTimeoutSeconds,
                _total?.Value ?? RemoteInstallSettings.DefaultTotalTimeoutSeconds);
            var settings = SettingsCache.Read(raw, out bool rebuilt, out var rejected);
            if (rebuilt)
            {
                bool first = ReferenceEquals(_settings, RemoteInstallSettings.Unbound);
                _settings = settings;
                if (rejected.Count > 0)
                    Plugin.Logger?.LogWarning("RemoteInstall: ignoring AllowedSteamIds entries that are not SteamID64s: " + string.Join(", ", rejected));
                if (!settings.BaseUrlValid)
                    Plugin.Logger?.LogWarning("RemoteInstall: ThunderstoreBaseUrl '" + settings.ThunderstoreBaseUrl
                                              + "' is not an https URL; remote installs will answer thunderstore-unreachable (config).");
                Plugin.Logger?.LogInfo("RemoteInstall: " + (first ? "" : "settings changed: ") + (settings.Enabled ? "enabled" : "disabled")
                                       + (settings.AllowedSteamIds.Count > 0 ? " for " + settings.AllowedSteamIds.Count + " listed admin(s)" : " for any in-game admin")
                                       + "; Thunderstore " + settings.ThunderstoreBaseUrl + "; audit log BepInEx/" + RemoteInstallAuditLog.FileName);
            }

            return settings;
        }
        catch { return RemoteInstallSettings.Unbound; }
    }

    /// <summary>AUTOUPDATE3: the server's shared HTTP transport (the remote installer's), or null before install.</summary>
    internal static ICatalogTransport? SharedTransport => _http;

    /// <summary>AUTOUPDATE3 (main thread): refresh the loaded-plugin copy the worker-thread planner reads.</summary>
    internal static void RefreshLoadedCache() => LoadedPluginGuids();

    private static void BindSettings(BepInEx.Configuration.ConfigFile file, bool probe)
    {
        try
        {
            var enabled = Config.ConfigHelper.Bind(file, RemoteInstallSettings.Section, RemoteInstallSettings.EnabledKey, true,
                "Server. Lets a connected player who is an in-game admin here (adminlist.txt plus adminauth) install, update and "
                + "uninstall Thunderstore mods on this server from their Install mods tab. No password is asked or stored. Off: such "
                + "requests are answered 'disabled'.");
            var allowed = Config.ConfigHelper.Bind(file, RemoteInstallSettings.Section, RemoteInstallSettings.AllowedSteamIdsKey, "",
                "Server. Optional SteamID64 list (comma or space separated) of the admins who may install remotely. Empty: any in-game admin.");
            _allowedInGame = Config.ConfigHelper.Bind(file, RemoteInstallSettings.Section, RemoteInstallSettings.AllowedSteamIdsInGameKey, true,
                "Server. Deprecated: use [ServerSettings] LockedInGame = progenitor/JoinAdmin/allowed-steam-ids. "
                + "False still locks AllowedSteamIds in the game (Progenitor's page shows it read-only and the server refuses an edit).");
            var baseUrl = Config.ConfigHelper.Bind(file, RemoteInstallSettings.Section, RemoteInstallSettings.ThunderstoreBaseUrlKey,
                RemoteInstallSettings.DefaultBaseUrl,
                "Server, test/diagnostic. The Thunderstore site this server downloads from (https only). Leave it at "
                + RemoteInstallSettings.DefaultBaseUrl + "; point it at an unreachable https address only to test how remote installs "
                + "report an outbound-blocked host.");
            var connect = Config.ConfigHelper.Bind(file, RemoteInstallSettings.Section, RemoteInstallSettings.ConnectTimeoutKey,
                RemoteInstallSettings.DefaultConnectTimeoutSeconds, "Server. Seconds to wait for a connection to Thunderstore.");
            var stall = Config.ConfigHelper.Bind(file, RemoteInstallSettings.Section, RemoteInstallSettings.StallTimeoutKey,
                RemoteInstallSettings.DefaultStallTimeoutSeconds, "Server. Seconds a download may go without receiving a byte before it fails.");
            var total = Config.ConfigHelper.Bind(file, RemoteInstallSettings.Section, RemoteInstallSettings.TotalTimeoutKey,
                RemoteInstallSettings.DefaultTotalTimeoutSeconds, "Server. Seconds one fetch (the index, or one package) may take in all.");

            _allowed = allowed;
            _baseUrl = baseUrl;
            _connect = connect;
            _stall = stall;
            _total = total;
            _enabled = enabled;
            Settings();
        }
        catch (Exception e)
        {
            _enabled = null;
            _settings = RemoteInstallSettings.Unbound;
            Plugin.Logger?.LogWarning("RemoteInstall: binding [RemoteInstall] threw (" + e.Message + "); remote installs stay off.");
        }

        // Startup reachability probe, off the main thread; its result rides in every status packet.
        var service = _service;
        if (probe && service != null && Settings().Enabled)
        {
            _ = service.ProbeAsync().ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                    Plugin.Logger?.LogInfo("RemoteInstall: Thunderstore " + (t.Result.State == RemoteReachability.Reachable
                        ? "reachable"
                        : "unreachable (" + t.Result.Detail + ")"));
            }, TaskScheduler.Default);
        }
    }

    /// <summary>The loaded plugin GUIDs as the main thread last read them (for worker-thread planning).</summary>
    private static volatile IReadOnlyList<string> _loadedCache = Array.Empty<string>();

    /// <summary>Main thread only: read the inventory and refresh <see cref="_loadedCache"/>.</summary>
    private static List<string> LoadedPluginGuids()
    {
        var list = ReadLoadedPluginGuids();
        _loadedCache = list;
        return list;
    }

    /// <summary>
    /// SRVUNINST1, main thread: every plugin the chainloader loaded on this server, with the same
    /// ordinary-plugin identification the client's Mods page makes for its own plugins
    /// (<c>ModsMenu.PlainIdentityOf</c>): Progenitor's reload-owned plugins count as neighbors, never
    /// as files to move. The reload folder and the ledger are read by the service itself.
    /// </summary>
    private static IReadOnlyList<ServerPluginFact> ServerPluginFacts(string root, bool cached)
    {
        var facts = new List<ServerPluginFact>();
        try
        {
            var plugins = BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance?.Plugins;
            if (plugins == null) return facts;
            var loaded = new List<PluginActions.LoadedPluginFact>();
            var found = new List<(string Id, string Name, string Version, string Location, List<string> Deps)>();
            foreach (var pair in plugins)
            {
                var info = pair.Value;
                if (info?.Metadata == null) continue;
                var deps = new List<string>();
                try
                {
                    foreach (var d in info.Dependencies)
                        if (d != null && (d.Flags & BepInDependency.DependencyFlags.HardDependency) != 0) deps.Add(d.DependencyGUID);
                }
                catch { /* judged on the rest */ }
                string where = "";
                try { where = info.Location ?? ""; }
                catch { where = ""; }
                string id = info.Metadata.GUID ?? "";
                loaded.Add(new PluginActions.LoadedPluginFact(id, info.Metadata.Name ?? id, where, deps));
                found.Add((id, info.Metadata.Name ?? id, info.Metadata.Version?.ToString() ?? "", where, deps));
            }

            foreach (var e in Plugins.ModRegistry.All())
                if (!loaded.Exists(f => string.Equals(f.Id, e.Id, StringComparison.OrdinalIgnoreCase)))
                    loaded.Add(new PluginActions.LoadedPluginFact(e.Id, e.Name, "", e.Dependencies));

            // SRVUNINST1 review: identification hashes the mod's DLLs, and an admin's Install tab asks for the
            // status every few seconds; it is redone only when the file, its folder or the loaded set changed.
            var ids = new List<string>(loaded.Count);
            foreach (var l in loaded) ids.Add(l.Id);
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            string loadedStamp = string.Join(",", ids);
            foreach (var (id, name, version, where, deps) in found)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                PluginActions.PluginIdentification? plain = cached
                    ? IdentifyCached(root, id, where, loaded, loadedStamp)
                    : IdentifyNow(root, id, where, loaded);
                facts.Add(new ServerPluginFact
                {
                    Id = id,
                    Name = name,
                    Version = version,
                    Location = where,
                    Framework = Ui.ModMenuInventory.IsFrameworkId(id),
                    Plain = plain,
                    HardDependencies = deps,
                });
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("RemoteInstall: reading this server's plugins threw: " + e.Message); }
        return facts;
    }

    private static readonly Dictionary<string, (string Stamp, PluginActions.PluginIdentification? Ident)> IdentityCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <see cref="PluginActions.PluginFileActions.Identify"/> once per plugin until its DLL (size, write
    /// time), its folder's write time or the set of loaded plugins changes. Main thread only.
    /// </summary>
    private static PluginActions.PluginIdentification? IdentifyCached(
        string root, string id, string location, List<PluginActions.LoadedPluginFact> loaded, string loadedStamp)
    {
        string stamp;
        try
        {
            var file = new FileInfo(location);
            string dir = file.DirectoryName ?? "";
            stamp = location + "|" + (file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "-")
                + "|" + (dir.Length > 0 && Directory.Exists(dir) ? Directory.GetLastWriteTimeUtc(dir).Ticks.ToString() : "-")
                + "|" + loadedStamp;
        }
        catch { stamp = ""; }

        if (stamp.Length > 0 && IdentityCache.TryGetValue(id, out var hit) && string.Equals(hit.Stamp, stamp, StringComparison.Ordinal))
            return hit.Ident;
        PluginActions.PluginIdentification? ident;
        try { ident = PluginActions.PluginFileActions.Identify(root, id, location, loaded); }
        catch { ident = null; }
        if (stamp.Length > 0)
        {
            if (IdentityCache.Count > 512) IdentityCache.Clear();
            IdentityCache[id] = (stamp, ident);
        }

        return ident;
    }

    private static PluginActions.PluginIdentification? IdentifyNow(
        string root, string id, string location, List<PluginActions.LoadedPluginFact> loaded)
    {
        try { return PluginActions.PluginFileActions.Identify(root, id, location, loaded); }
        catch { return null; }
    }

    private static List<string> ReadLoadedPluginGuids()
    {
        var list = new List<string>();
        try
        {
            foreach (var row in Ui.ModMenuInventory.Snapshot().Rows)
            {
                if (string.IsNullOrEmpty(row.Id)) continue;
                if (string.Equals(row.Location, "Server list", StringComparison.Ordinal)) continue;
                if (!list.Contains(row.Id)) list.Add(row.Id);
            }
        }
        catch { /* inventory is optional evidence */ }
        return list;
    }

    private static unsafe void OnRequest(int _, Entity userEntity, User user, byte* payload, int length)
    {
        var service = _service;
        if (payload == null || length <= 0 || service == null) return;
        if (!RemoteInstallWire.TryDecodeRequest(new ReadOnlySpan<byte>(payload, length), out var request)) return;
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity)) return;
            // Re-read rather than trust the resolved copy, as Stop server and ServerSettings do.
            var current = world.EntityManager.GetComponentData<User>(userEntity);
            if (!current.IsConnected) return;
            var actor = ActorOf(current);
            int index = current.Index;
            ulong platform = current.PlatformId;

            // Main thread: take the inventory the worker's dependency check will read.
            if (request.Op is RemoteInstallOp.Install or RemoteInstallOp.Update) LoadedPluginGuids();

            if (request.Op == RemoteInstallOp.List)
            {
                Send(world, userEntity, index, platform, RemoteInstallWire.EncodeStatus(service.StatusFor(request.Nonce, actor)));
                // Only someone who could install makes this server reach out to Thunderstore.
                if (RemoteInstallPolicy.Allowed(Settings(), actor)) RefreshProbe(service, world, userEntity, index, platform, actor);
                return;
            }

            service.Handle(request, actor, progress =>
            {
                byte[] bytes;
                try { bytes = RemoteInstallWire.EncodeProgress(progress); }
                catch { return; }
                MainThread.Post(() =>
                {
                    Send(world, userEntity, index, platform, bytes);
                    if (!progress.IsTerminal) return;
                    bool changed = progress.Stage is RemoteInstallStage.HotLoaded or RemoteInstallStage.PendingRestart or RemoteInstallStage.Removed;
                    if (!changed) return;
                    // The new ledger rows. The handshake is NOT re-asked: a hot-loaded mod that adds a
                    // client requirement would make the existing re-evaluation kick the admin (the
                    // join's install grace is spent). Every client sees the change at its next join.
                    Send(world, userEntity, index, platform, RemoteInstallWire.EncodeStatus(service.StatusFor(0, actor)));
                }, Owner);
            });
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("RemoteInstallEvents request: " + e.Message);
        }
    }

    /// <summary>A list request is also the client's "check again": re-probe (at most once a minute) and push a changed answer.</summary>
    private static void RefreshProbe(RemoteInstallService service, World world, Entity userEntity, int index, ulong platform, RemoteInstallActor actor)
    {
        var before = service.LastProbe;
        _ = service.ProbeAsync(force: true).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            if (t.Result.State == before.State && t.Result.Detail == before.Detail) return;
            MainThread.Post(() => Send(world, userEntity, index, platform,
                RemoteInstallWire.EncodeStatus(service.StatusFor(0, actor))), Owner);
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Push the status to a client that just completed the handshake, if it declared the
    /// capability. Called from <c>Handshake.OnResponse</c>'s post-ok effects.
    /// </summary>
    internal static void OnHandshakeOk(World? world, Entity userEntity, Handshake.Capability clientCaps)
    {
        var service = _service;
        if (service == null || world == null || (clientCaps & Handshake.Capability.RemoteInstall) == 0) return;
        try
        {
            if (!world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity)) return;
            var current = world.EntityManager.GetComponentData<User>(userEntity);
            if (!current.IsConnected) return;
            var actor = ActorOf(current);
            Send(world, userEntity, current.Index, current.PlatformId, RemoteInstallWire.EncodeStatus(service.StatusFor(0, actor)));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("RemoteInstallEvents status push: " + e.Message);
        }
    }

    /// <summary>
    /// The requester as this server sees them. Authorized when their SteamID (the server's own
    /// <c>User.PlatformId</c> read) is in the admin list file the game loads — re-read when it changes —
    /// or when <c>User.IsAdmin</c> is set (adminauth), or when they host this private game
    /// (<see cref="ServerAdminList.IsHost"/>). An unreadable list leaves IsAdmin and the host.
    /// </summary>
    private static RemoteInstallActor ActorOf(User current)
    {
        var membership = AdminListRead.Unreadable;
        try { membership = _adminList?.Check(current.PlatformId) ?? AdminListRead.Unreadable; }
        catch { membership = AdminListRead.Unreadable; }
        return new RemoteInstallActor(current.CharacterName.ToString(), current.PlatformId, current.IsAdmin,
            ServerAdminList.Authorize(membership, current.IsAdmin, current.PlatformId));
    }

    /// <summary>This server's persistentDataPath: the <c>-persistentDataPath</c> argument, else the game's own value (as ModSave reads it).</summary>
    private static string? PersistentDataPath()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], "-persistentDataPath", StringComparison.OrdinalIgnoreCase) && args[i + 1].Length > 0)
                    return Path.GetFullPath(args[i + 1]);
            }
        }
        catch { /* fall through to the game's value */ }

        try
        {
            string path = StunApplication.persistentDataPath;
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Send to one user, only if that same user is still connected in the same world.</summary>
    private static void Send(World world, Entity userEntity, int index, ulong platform, byte[] bytes)
    {
        try
        {
            if (!world.IsCreated || Worlds.Server?.Pointer != world.Pointer) return;
            if (!world.EntityManager.HasComponent<User>(userEntity)) return;
            var user = world.EntityManager.GetComponentData<User>(userEntity);
            if (!user.IsConnected || user.Index != index || user.PlatformId != platform) return;
            var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap == null) return;
            CustomEvents.SendToUser(bootstrap, index, EventId, new ReadOnlySpan<byte>(bytes));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("RemoteInstallEvents send: " + e.Message);
        }
    }

    // ---- client ----------------------------------------------------------------------------

    private static unsafe void InstallClient()
    {
        CustomEvents.OnReceive(EventId, OnReply);
        RemoteInstallBridgeOps.IsServer = () => false;
        RemoteInstallBridgeOps.ClientRoute = Ui.ModsMenu.CurrentRouteDecision;
        // "Authenticate as admin": the game's own adminauth, the call the game's debug menu makes
        // (StunConsole.Commands.TryCommand). Not yet checked live, so behind [Ui] AdminAuthButton.
        var auth = AdminAuthFlow.Shared;
        auth.Reset();
        auth.Executor = RunGameAdminAuth;
        auth.RequestStatus = () =>
        {
            // The answer is the server's next status; ask now and again once the server has handled it.
            RemoteInstallClient.Shared.RequestStatus();
            MainThread.PostDelayed(2, () => RemoteInstallClient.Shared.RequestStatus(), Owner);
            MainThread.PostDelayed(5, () => RemoteInstallClient.Shared.RequestStatus(), Owner);
            MainThread.PostDelayed(AdminAuthFlow.TimeoutSeconds + 0.5, () =>
            {
                AdminAuthFlow.Shared.Observe(RemoteInstallClient.Shared.Status);
                Ui.ModsMenu.NoteRemoteInstallChanged();
            }, Owner);
            return true;
        };
        Config.SharedConfig.BindWhenAvailable("[" + AdminAuthFlow.ConfigSection + "] " + AdminAuthFlow.ConfigKey, file =>
        {
            try
            {
                var entry = file.Bind(AdminAuthFlow.ConfigSection, AdminAuthFlow.ConfigKey, true, AdminAuthFlow.ConfigDescription);
                AdminAuthFlow.Shared.Enabled = entry.Value;
                // The file is Bootstrap's process-lifetime config: subscribe through ConfigHelper so the
                // handler is removed when this Core unloads. A direct entry.SettingChanged += pinned the
                // generation (live 2026-10-01: every client Core swap refused from startup).
                Config.ConfigHelper.AddSettingChanged(file, (_, e) =>
                {
                    if (!ReferenceEquals(e.ChangedSetting, entry)) return;
                    try { AdminAuthFlow.Shared.Enabled = entry.Value; }
                    catch { /* tearing down */ }
                });
            }
            catch (Exception e)
            {
                AdminAuthFlow.Shared.Enabled = false;
                Plugin.Logger?.LogWarning("Ui: [Ui] " + AdminAuthFlow.ConfigKey + " could not be bound (" + e.Message + "); the button stays off.");
            }
        });
        var client = RemoteInstallClient.Shared;
        client.Send = bytes => CustomEvents.SendToServer(EventId, new ReadOnlySpan<byte>(bytes));
        client.Changed = () => MainThread.Post(Ui.ModsMenu.NoteRemoteInstallChanged, Owner);
        try { client.SteamIdFile = Path.Combine(Paths.BepInExRootPath, "progenitor-cache", "steamid.txt"); }
        catch { client.SteamIdFile = null; }
        // The admin-list helper's SteamID: what a Progenitor server resolved for this connection
        // (its own User.PlatformId read), kept between sessions. No client-side read is added.
        AdminListFile.LocalSteamIdProbe = () => RemoteInstallClient.Shared.LastKnownSteamId;
    }

    /// <summary>
    /// The game's own <c>adminauth</c>: <c>Engine.Console.StunConsole.Commands.TryCommand("adminauth", false)</c>
    /// (client native code: <c>StunConsole.get_Commands</c>, <c>ConsoleCommandHandler.TryCommand</c>;
    /// the game's <c>DebugMenuMapper.ResetTab_General</c> makes the same call). The
    /// command's handler (<c>ClientConsoleCommandSystem.AdminAuth</c>) sends
    /// <c>AdminAuthEvent</c>; the server's <c>AdminAuthSystem</c> checks its list and sets IsAdmin.
    /// Not checked live: only reached with <c>[Ui] AdminAuthButton</c> on.
    /// </summary>
    private static bool RunGameAdminAuth()
    {
        if (!AdminAuthFlow.Shared.Enabled || Plugin.IsServerProcess) return false;
        var handler = Engine.Console.StunConsole.Commands;
        if (handler == null) return false;
        handler.TryCommand("adminauth", false);
        Plugin.Logger?.LogInfo("RemoteInstall: ran the game's adminauth command for the Authenticate as admin button.");
        return true;
    }

    private static unsafe void OnReply(int _, byte* payload, int length)
    {
        if (payload == null || length <= 0) return;
        try { RemoteInstallClient.Shared.Accept(new ReadOnlySpan<byte>(payload, length)); }
        catch (Exception e) { Plugin.Logger?.LogWarning("RemoteInstallEvents reply: " + e.Message); }
    }

    /// <summary>
    /// True while the client is in a game (joining or running) — the same <c>ClientState</c> read the
    /// handshake refresh makes. The disconnect screen keeps <c>Client_0</c> alive, so a world alone is not a connection.
    /// </summary>
    /// <summary>
    /// LOCALDETECT1 review (live 2026-10-10): the client is joining or in a game: a client world exists and is not at the
    /// main menu (<c>Idle</c>) or the disconnect screen (<c>Error</c>). Wider than <see cref="ClientConnected"/>, which
    /// misses the join's other states (shader pre-warm, reconnecting, connecting); the install route reads this, so a
    /// joining client never takes the "not connected" branch where the configured folder is the route.
    /// </summary>
    internal static bool ClientJoiningOrConnected()
    {
        try
        {
            var client = Worlds.Client;
            if (client == null || !client.IsCreated) return false;
            var bootstrap = client.GetExistingSystemManaged<ClientBootstrapSystem>();
            if (bootstrap == null) return false;
            var state = bootstrap.ClientState;
            return state != ClientState.Idle && state != ClientState.Error;
        }
        catch
        {
            return false;
        }
    }

    internal static bool ClientConnected()
    {
        try
        {
            var client = Worlds.Client;
            if (client == null || !client.IsCreated) return false;
            var bootstrap = client.GetExistingSystemManaged<ClientBootstrapSystem>();
            var state = bootstrap == null ? ClientState.Idle : bootstrap.ClientState;
            return state == ClientState.Running || state == ClientState.Loading;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The server's HTTP transport, built on first use from the then-current settings so the
    /// connect timeout follows the bound <c>[RemoteInstall] ConnectTimeoutSeconds</c>. Redirects are
    /// not followed by the handler: <see cref="CatalogFetcher"/> follows them itself, HTTPS only.
    /// </summary>
    private sealed class LazyHttpTransport : ICatalogTransport, IDisposable
    {
        private readonly Func<RemoteInstallSettings> _settings;
        private HttpCatalogTransport? _inner;
        private SocketsHttpHandler? _handler;
        private readonly object _gate = new();

        public LazyHttpTransport(Func<RemoteInstallSettings> settings) => _settings = settings;

        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken)
        {
            HttpCatalogTransport inner;
            lock (_gate)
            {
                if (_inner == null)
                {
                    var s = _settings();
                    var handler = new SocketsHttpHandler
                    {
                        ConnectTimeout = s.ConnectTimeout,
                        AllowAutoRedirect = false,
                    };
                    _handler = handler;
                    _inner = new HttpCatalogTransport(handler, CatalogFetchOptions.Default);
                }

                inner = _inner;
            }

            return inner.SendAsync(request, cancellationToken);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _inner?.Dispose();
                _inner = null;
                // HttpCatalogTransport does not own its handler.
                _handler?.Dispose();
                _handler = null;
            }
        }
    }
}
#endif
