#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using BepInEx;
using ProjectM;
using ProjectM.UI;
using Progenitor.Install;
using Stunlock.Network;

namespace Progenitor.Networking;

/// <summary>
/// JOINFLOW2: the live half of the join screens' Thunderstore installs, Join now and restart-and-rejoin. Wires the
/// game-free <see cref="JoinFlowClient"/> to the Install tab's catalog machinery, Bootstrap's connect record, the
/// game's own connect (<c>GameHelper.LaunchGame</c>, as the dev bridge's <c>connect</c>), the production Leave, and
/// the relauncher. Installed with the join screens (<see cref="JoinFlowPanel.Install"/>); torn down with them.
/// </summary>
internal static class JoinFlowLive
{
    private static readonly object Owner = new();
    private static readonly object DialogOwner = new();
    private static SharedCatalogHttp.Lease? _http;

    internal static void Install()
    {
        try
        {
            string root = Paths.BepInExRootPath;
            // TSTRAFFIC1: the shared client, and a conditional, compressed index check over this client's own
            // progenitor-cache copy (the one the Install tab keeps), so a join past 10 minutes costs a 304.
            _http ??= SharedCatalogHttp.Acquire();
            var transport = new ProgressCatalogTransport(_http);
            var options = CatalogFetchOptions.Default;
            var fetcher = new CatalogFetcher(transport, options);
            var check = ConditionalIndexFetch.Over(fetcher, options.IndexUrl, new IndexDiskCache(root, options.IndexUrl), "join");
            var index = new JoinPackageIndex(
                ct => check.FetchAsync(ct),
                () => ReadCachedIndex(root),
                () => LocalIndexConfig.Resolve());
            // AUTOUPDATE1: a fresh read here is also an update check (no request of its own).
            index.OnFetched = (json, stamp) => Ui.ModsMenu.AdoptJoinIndex(json, stamp);
            JoinFlowClient.PackageIndex = index;
            // LOCALJOIN1 (debug): the join window's local test source, read at each build (empty: none).
            JoinFlowClient.LocalTestFolderProbe = () => LocalIndexConfig.Resolve();
            JoinFlowClient.InstallerFactory = requests =>
                new JoinCatalogInstaller(() => BuildCatalog(root, transport), index, requests, Rules(root));
            JoinFlowClient.TargetProbe = Target;
            // LOCALREJOIN1: this PC's own server is reconnected through the loopback address.
            JoinFlowClient.LocalRejoinFactsProbe = LocalRejoinFacts;
            // F22: a server restarting for Progenitor is offered a rejoin when the connection ends. F37: the player's own
            // Save, stop and start again names that restart and rejoins without asking (the Rejoin page keeps Cancel).
            ServerStopRequest.Accepted = restartsAgain => JoinFlowClient.ArmRejoinWhenBack(
                restartsAgain ? Ui.InstallModsCopy.StopAndStartAgain : Ui.InstallModsCopy.StopServerToApply, autoRejoin: restartsAgain);
            ServerStopRequest.Abandoned = JoinFlowClient.DisarmRejoinWhenBack;
            // Not wired: a server's pending-restart rows alone cannot tell a lost connection from the player's own Leave.
            // Review 2026-10-07: the folder facts are computed off the main thread only.
            LocalFolderFactsCache.Compute = ComputeFolderFacts;
            JoinFlowClient.PasswordProbe = ModManifest.ReadLiveConnectUsedPassword;
            JoinFlowClient.LiveHostProbe = ModManifest.ReadLiveConnectHost;
            JoinFlowClient.Relauncher = new LiveRelauncher();
            JoinFlowClient.ConnectHost = new LiveConnectHost();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("JoinFlow: Thunderstore installs and rejoin are not available: " + e.Message);
        }

        // INSTALLRESTART1: the Mods window's pending-restart bar (its pending rows, the after-restart check, the dialog kit).
        JoinFlowClient.PendingRestartProbe = Ui.ModsMenu.ClientPendingRestart;
        JoinFlowClient.RunsNowProbe = Ui.ModsMenu.RunsNowAfterRestart;
        JoinFlowClient.RestartDialogFactory = title => Ui.Ui.Panel(title, DialogOwner, JoinFlowPanel.PluginId);
        // The restart never starts mid-join: connected or connecting, the handshake not answered yet (a server without
        // Progenitor never answers; an install window holds the join on purpose). The held flag can outlive a world that
        // went without passing through Idle, so at the title view with no world it does not count.
        JoinFlowClient.JoinConnectingProbe = () =>
        {
            if (!Handshake.ClientConnectionHeld) return false;
            if (string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal)) return false;
            if (JoinFlowClient.JoinedWithoutProgenitor || ClientInstallWindow.IsArmed) return false;
            var host = JoinFlowClient.ConnectHost;
            return host == null || host.WorldExists || !host.MainMenuReady;
        };

        // JOINTURNON: installed-but-off mods are turned on through the Mods list's own switch.
        JoinFlowClient.OffCopyProbe = Ui.ModsMenu.JoinOffCopyOf;
        JoinFlowClient.SwitchProbe = Ui.ModsMenu.SwitchForJoin;
        // 2026-10-05: the answer declares the asked rows this client can fix by itself (Turn on, Use kept), so a server
        // holds the join open for them.
        Handshake.LocalFixProbe = JoinFlowClient.CanFixLocally;

        // CLIENTPOLICY2: this client's mods as its report lists them, the per-server pause and the switch, for the
        // required client rows and the "Not allowed on this server" rows.
        JoinFlowClient.ClientModsProbe = ClientModReportClient.CurrentEntries;
        JoinFlowClient.PausedHereProbe = PausedHere;
        JoinFlowClient.PausableProbe = Pausable;
        JoinFlowClient.TurnsOffAtRestartProbe = Ui.ModsMenu.JoinTurnsOffAtNextStart;
        JoinFlowClient.PolicyPauseProbe = ServerModPause.PauseForPolicy;

        // CLIENTPOLICY3 (live 2026-10-05): a kick in the world leaves the game's own disconnect banner up; the in-world
        // screens must not cover it.
        Handshake.ClientConnectionEnded -= OnConnectionEnded;
        Handshake.ClientConnectionEnded += OnConnectionEnded;

        // JOINFLOW4: keep both versions when the player is ahead of a server.
        ModVersionLive.Install();
    }

    private static void OnConnectionEnded()
    {
        try { JoinFlowClient.OnConnectionLost(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: closing the screens on the connection's end threw: " + e.Message); }
    }

    internal static void Uninstall()
    {
        Handshake.ClientConnectionEnded -= OnConnectionEnded;
        Handshake.LocalFixProbe = null;
        ServerStopRequest.Accepted = null;
        ServerStopRequest.Abandoned = null;
        LocalFolderFactsCache.Compute = null;
        // LOCALDETECT1 review: the auto-detected folder goes with this Core generation (a read in flight is discarded).
        Progenitor.Install.LocalServerDetectCache.Clear();
        ModVersionLive.Uninstall();
        try { _http?.Dispose(); }
        catch { /* teardown */ }
        _http = null;
    }

    /// <summary>CLIENTPOLICY2: the mods the per-server pause holds now (paused, not released; the player's own switch-offs excluded).</summary>
    private static IReadOnlyList<ClientModEntry> PausedHere()
    {
        var list = new List<ClientModEntry>();
        try
        {
            var backend = ServerModPause.PauseBackend();
            if (!backend.Supported) return list;
            var current = ServerModPause.Current;
            foreach (var p in backend.Suspended())
            {
                if (p.Resuming) continue;
                bool listed = false;
                foreach (var id in current.PausedIds)
                    if (string.Equals(id, p.Guid, StringComparison.OrdinalIgnoreCase)) { listed = true; break; }
                if (listed) list.Add(new ClientModEntry(p.Guid, p.Name, p.Version, ClientModSources.Progenitor));
            }
        }
        catch { /* nothing is shown as paused */ }

        return list;
    }

    /// <summary>CLIENTPOLICY2: the plugins Bootstrap's reloader can pause now (hot-loaded, no ctx.UnsafeHarmony).</summary>
    private static IReadOnlyCollection<string> Pausable()
    {
        try
        {
            var backend = ServerModPause.PauseBackend();
            return backend.Supported ? new List<string>(backend.Suspendable()) : Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>A catalog service for one batch, built like the Install tab's (client root only, the same admission).</summary>
    private static InstallCatalogService BuildCatalog(string root, ICatalogTransport transport)
    {
        var identities = Progenitor.Install.Admission.CompatibilityIdentities.Current();
        var admission = new Progenitor.Install.Admission.AdmissionPipeline(
            new Progenitor.Install.Admission.BootstrapAdmissionSnapshotSource(identities),
            identities: identities,
            policyProvider: () => Progenitor.Install.Admission.QualificationPolicyCache.Current(root));
        var service = new InstallCatalogService(root, transport, admission: admission, thisProcessIsServer: false, registerForBridge: false);
        service.LoadedPluginGuids = _ => LoadedGuidsOnMain();
        return service;
    }

    private static JoinInstallRules Rules(string root) => new()
    {
        ThunderstoreEnabled = () => OnMain(() => Ui.ModsMenu.ThunderstoreEnabled, true),
        ShowNsfw = () => OnMain(() => Ui.ModsMenu.ShowNsfwContent, false),
        ManagedElsewhere = entry => OnMain(() => ManagedElsewhereOf(root, entry), null),
        RunOnMain = work => Dev.MainThread.Run(work, Owner),
        PluginLoaded = id => OnMain(() => IsLoaded(id), false),
        // JOINFLOW4: "Use version X" keeps the newer copy (parked) before the server's version is written.
        KeepBeforeReplace = KeepOnMain,
    };

    /// <summary>
    /// JOINFLOW4: the park on the main thread. A worker that stops waiting must not leave the park to run later and
    /// strand a copy the install already refused (review finding 9): the queued work checks the flag first, and once it
    /// has started the worker waits for its real answer.
    /// </summary>
    private static (bool Ok, string Note, bool Moved) KeepOnMain(JoinInstallRequest request)
    {
        var gate = new object();
        bool abandoned = false;
        try
        {
            var task = Dev.MainThread.Run(() =>
            {
                lock (gate) if (abandoned) return (false, "the main thread did not answer in time", false);
                return ModVersionHooks.KeepForThunderstore(request);
            }, Owner);
            if (task.Wait(TimeSpan.FromSeconds(10))) return task.Result;
            lock (gate) abandoned = true;
            // Started before the flag: its answer is the real one.
            return task.Wait(TimeSpan.FromSeconds(30)) ? task.Result : (false, "the main thread did not answer in time", false);
        }
        catch (Exception e) { return (false, e.Message, false); }
    }

    /// <summary>Run <paramref name="work"/> on the main thread from a worker and wait for it (the answer on failure).</summary>
    private static T OnMain<T>(Func<T> work, T fallback)
    {
        try
        {
            var task = Dev.MainThread.Run(work, Owner);
            return task.Wait(TimeSpan.FromSeconds(10)) ? task.Result : fallback;
        }
        catch { return fallback; }
    }

    private static IReadOnlyList<string> LoadedGuidsOnMain() => OnMain<IReadOnlyList<string>>(() =>
    {
        var list = new List<string>();
        foreach (var mod in ModManifest.EnumerateLoaded()) if (!string.IsNullOrEmpty(mod.Id)) list.Add(mod.Id);
        return list;
    }, Array.Empty<string>());

    private static bool IsLoaded(string id)
    {
        foreach (var mod in ModManifest.EnumerateLoaded())
            if (string.Equals(mod.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>MANAGED1's verdict for the loaded copy of this listing: the tool that installed it, or null.</summary>
    private static string? ManagedElsewhereOf(string root, CatalogEntry entry)
    {
        var inventory = new List<InstalledInventoryRow>();
        foreach (var row in Ui.ModMenuInventory.Snapshot().Rows)
            if (!string.IsNullOrEmpty(row.Id)) inventory.Add(new InstalledInventoryRow(row.Id, row.Version, row.Location, null));
        var query = InstalledState.Scan(root, null, inventory).ForRoot(InstalledState.ClientRoot);
        var state = InstalledState.Detect(entry, query);
        if (state.State != InstalledKind.ManagedElsewhere) return null;
        return state.Manager.Length > 0 ? state.Manager : "another tool";
    }

    /// <summary>
    /// Server (2026-10-05): the read of the Thunderstore index behind the install window's Thunderstore fix — the join
    /// screens' own index (the local index first, the fetched one reused for 30 minutes, the cached copy on a failed
    /// fetch), so the server and the client agree on which version is served. Built once per Core generation.
    /// TSTRAFFIC1: the check is conditional and compressed over the server's own <c>progenitor-cache</c> copy.
    /// </summary>
    internal static ServerPackageAvailability CreateServerPackages()
    {
        string root = Paths.BepInExRootPath;
        // Owned by the reader and disposed with it on ModManifest.Uninstall (one per Core generation).
        var http = SharedCatalogHttp.Acquire();
        var options = CatalogFetchOptions.Default;
        var fetcher = new CatalogFetcher(http, options);
        var check = ConditionalIndexFetch.Over(fetcher, options.IndexUrl, new IndexDiskCache(root, options.IndexUrl), "server availability");
        var index = new JoinPackageIndex(
            ct => check.FetchAsync(ct),
            () => ReadCachedIndex(root),
            () => LocalIndexConfig.Resolve(),
            maxAge: JoinPackageIndex.ServerMaxAge);
        return new ServerPackageAvailability((wanted, ct) => index.LoadAsync(wanted, ct), null, http);
    }

    private static string? ReadCachedIndex(string root)
    {
        try
        {
            string path = Path.Combine(root, Ui.InstallIndexRefresher.CacheFolderName, Ui.InstallIndexRefresher.IndexFileName);
            if (!File.Exists(path) || new FileInfo(path).Length > ThunderstoreAdapter.DefaultMaxJsonBytes) return null;
            return File.ReadAllText(path);
        }
        catch { return null; }
    }

    /// <summary>The joined server from Bootstrap's connect record (the address, port and transport of the last LaunchGame).</summary>
    private static JoinTarget Target() => new()
    {
        Address = BootstrapLink.LastConnectAddress ?? "",
        Port = BootstrapLink.LastConnectPort,
        Protocol = BootstrapLink.LastConnectProtocol ?? "",
        HostedByThisClient = BootstrapLink.IsHostingPrivateGame,
    };

    /// <summary>The relauncher: inbox PowerShell, detached and hidden, through Steam.</summary>
    private sealed class LiveRelauncher : IJoinRelauncher
    {
        /// <summary>The restart log, in the BepInEx folder: the helper's steps and the game's broker failures.</summary>
        private const string RestartLogName = "progenitor-restart.log";

        public RelaunchPlan Plan()
        {
            var plan = new RelaunchPlan();
            string ps = RelaunchCommand.PowerShellPath(Environment.SystemDirectory);
            bool psExists = File.Exists(ps);
            bool handler = SteamUrlHandler();
            bool running = SteamRunning();
            plan.Mode = RelaunchCommand.Decide(handler, running, psExists);
            var why = new List<string>(3);
            if (!psExists) why.Add("Windows PowerShell not found at " + ps);
            if (!handler) why.Add("no steam:// handler is registered");
            if (!running) why.Add("Steam is not running");
            plan.Why = string.Join("; ", why);
            using var self = Process.GetCurrentProcess();
            string exe = Environment.ProcessPath ?? "";
            plan.Script = RelaunchCommand.Script(self.Id, self.StartTime.ToUniversalTime().Ticks, exe,
                logPath: Path.Combine(Paths.BepInExRootPath, RestartLogName));
            plan.FileName = ps;
            plan.Arguments = RelaunchCommand.Arguments(plan.Script);
            return plan;
        }

        public void Start(RelaunchPlan plan, Action<bool, string> done)
        {
            // The broker can take seconds to answer: wait for it on a background thread so no frame blocks, and
            // hand the answer to the main thread (the caller quits from there).
            var thread = new System.Threading.Thread(() =>
            {
                bool started;
                string error;
                try { started = RunBroker(plan, out error); }
                catch (Exception e)
                {
                    started = false;
                    error = e.Message;
                }

                Dev.MainThread.Post(() => done(started, error), Owner);
            })
            {
                IsBackground = true,
                Name = "Progenitor restart broker",
            };
            thread.Start();
        }

        // Runs off the main thread. Logs through BepInEx's logger (thread-safe) and the restart log.
        private static bool RunBroker(RelaunchPlan plan, out string error)
        {
            error = "";
            // Only this short-lived broker is a child of the game. Windows owns the waiting helper, so Steam
            // cannot retain the old session through inherited handles. The broker's command travels on its
            // command line (Unicode), never through a console code page, so non-ASCII install paths survive.
            var utf8 = new System.Text.UTF8Encoding(false);
            var info = new ProcessStartInfo(plan.FileName, RelaunchCommand.BrokerArguments(plan.FileName, plan.Arguments))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = utf8,
                StandardErrorEncoding = utf8,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.SystemDirectory,
            };
            using var p = Process.Start(info);
            if (p == null)
            {
                error = "the relauncher did not start";
                return false;
            }

            // Read both pipes while waiting, so a full pipe can never stall the broker.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(RelaunchCommand.BrokerTimeoutSeconds * 1000))
            {
                // Kill it so it cannot create the helper after the game has told the player to start manually.
                // A helper it created just before this still waits for Steam to report the game idle and for no
                // game process to be running, so it never launches a second copy beside a manual start.
                string killed;
                try
                {
                    p.Kill(entireProcessTree: true);
                    killed = "killed";
                }
                catch (Exception e) { killed = "could not be killed (" + e.Message + ")"; }

                error = "the Windows restart broker did not answer within " + RelaunchCommand.BrokerTimeoutSeconds + " s and was " + killed;
                Plugin.Logger?.LogWarning("JoinFlow: " + error);
                NoteRestartLog("game: " + error);
                return false;
            }

            p.WaitForExit(); // drains the redirected pipes
            string result = Wait(stdout).Trim().TrimStart('﻿').Trim();
            string detail = Wait(stderr).Trim();
            if (p.ExitCode != 0 || !int.TryParse(result, out int helperPid) || helperPid <= 0)
            {
                error = "the Windows restart broker failed (exit " + p.ExitCode + "): " + detail;
                NoteRestartLog("game: " + error);
                return false;
            }

            Plugin.Logger?.LogInfo("JoinFlow: independent relauncher started (pid " + helperPid + "); Windows owns the helper; broker exited");
            return true;
        }

        private static string Wait(Task<string> read)
        {
            try { return read.Wait(2000) ? read.Result : ""; }
            catch { return ""; }
        }

        private static void NoteRestartLog(string message)
        {
            try
            {
                File.AppendAllText(Path.Combine(Paths.BepInExRootPath, RestartLogName),
                    DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine, new System.Text.UTF8Encoding(false));
            }
            catch { /* the log is best effort */ }
        }

        public void Quit(double delaySeconds)
        {
            Plugin.Logger?.LogInfo("JoinFlow: quitting V Rising for the restart in " + delaySeconds.ToString("0.#") + " s");
            // Called from a button (the main thread): with no delay, Quit now, so no Core swap can drop it.
            // TITLEVIDEO1: every quit retires the title video first (a playback left for engine cleanup can hang the exit
            // the relauncher waits for), then quits after at most a couple of frames. A Core unload mid-wait quits at once.
            if (delaySeconds <= 0)
            {
                Progenitor.Ui.TitleVideoQuit.Request("restart", () => UnityEngine.Application.Quit());
                return;
            }

            Dev.MainThread.PostDelayed(delaySeconds, () => Progenitor.Ui.TitleVideoQuit.Request("restart", () => UnityEngine.Application.Quit()), Owner);
        }

        private static bool SteamUrlHandler() =>
            OperatingSystem.IsWindows() && ReadKeyExists(classesRoot: true, @"steam\shell\open\command");

        private static bool SteamRunning()
        {
            if (OperatingSystem.IsWindows())
            {
                int? pid = ReadDword(@"Software\Valve\Steam\ActiveProcess", "pid");
                if (pid is > 0) return true;
            }

            // A game Steam started carries its app id in the environment.
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamAppId"));
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static bool ReadKeyExists(bool classesRoot, string key)
        {
            try
            {
                var hive = classesRoot ? Microsoft.Win32.Registry.ClassesRoot : Microsoft.Win32.Registry.CurrentUser;
                using var k = hive.OpenSubKey(key, writable: false);
                return k != null;
            }
            catch { return false; }
        }

        private static int? ReadDword(string key, string value)
        {
            if (!OperatingSystem.IsWindows()) return null;
            return ReadDwordWindows(key, value);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static int? ReadDwordWindows(string key, string value)
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key, writable: false);
                return k?.GetValue(value) is int i ? i : null;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// LOCALREJOIN1: the facts <see cref="JoinLocalRoute"/> decides from, read the way the Install tab's route reads
    /// them: the configured folder (<c>ModsMenu.CurrentRouteDecision</c>), its port, whether its server process runs
    /// here, its name, and whether the target address is this machine.
    /// </summary>
    internal static JoinLocalFacts? LocalRejoinFacts(JoinTarget target)
    {
        string root = "";
        try
        {
            var decision = Ui.ModsMenu.CurrentRouteDecision();
            if (decision.Facts is { PathValid: true } f) root = f.CanonicalPath ?? "";
        }
        catch { root = ""; }

        return LocalServerFacts(target, root);
    }

    /// <summary>
    /// LOCALROUTE1: the same facts for a validated server folder <paramref name="root"/> (empty: none), shared by the loopback
    /// rejoin and the Install tab's route (<c>ModsMenu.CurrentRouteDecision</c>).
    /// </summary>
    internal static JoinLocalFacts? LocalServerFacts(JoinTarget target, string root)
    {
        try
        {
            root ??= "";
            string address = (target?.Address ?? "").Trim();
            bool loopback = string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase)
                || (System.Net.IPAddress.TryParse(address, out var ip) && System.Net.IPAddress.IsLoopback(ip));
            // Review 2026-10-07: the folder's facts (a process scan, a command-line read, file reads) come from the
            // background cache; never computed here, on the main thread. None yet reads as not this PC's server.
            var folder = root.Length > 0 ? LocalFolderFactsCache.Get(root) : null;
            int port = folder?.Port ?? 0;
            bool running = folder?.Running ?? false;
            string name = folder?.Name ?? "";
            string operatorKey = folder?.OperatorKey ?? "";

            return new JoinLocalFacts
            {
                AddressIsThisMachine = LocalAddress.IsThisMachine(address),
                AddressIsLoopback = loopback,
                FolderValid = root.Length > 0,
                FolderPort = port,
                FolderServerRunning = running,
                FolderServerName = name,
                FolderOperatorFingerprint = operatorKey,
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Review 2026-10-07: one folder's facts, computed on the thread pool by <see cref="LocalFolderFactsCache"/>: one
    /// process scan (off the main thread <c>ServerProcessControl.Running</c> scans), the port and name from the settings
    /// and the command line, the operator key file. Never called on the main thread.
    /// </summary>
    internal static LocalFolderFactsCache.FolderFacts? ComputeFolderFacts(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        ServerProcessInfo? live = null;
        try { live = ServerProcessControl.RunningNow(root); } catch { live = null; }
        int port = 0;
        string name = "";
        try { port = ServerProcessControl.HostPort(root); } catch { port = 0; }
        try { name = ServerIdentity.HostName(root); } catch { name = ""; }
        return new LocalFolderFactsCache.FolderFacts
        {
            Port = port,
            Running = live != null,
            Name = name,
            OperatorKey = JoinLocalRoute.ReadOperatorFingerprint(root),
        };
    }

    /// <summary>The game side of the reconnect: the world, the title view, Leave, and the connect itself.</summary>
    private sealed class LiveConnectHost : IJoinConnectHost
    {
        private double _menuAt = -10;
        private bool _menu;
        private Unity.Entities.World? _world;

        /// <summary>
        /// Read every frame (the last-server tick). The client world found last is kept while it stays created, so a
        /// frame in the world is one field read instead of a scan that reads (and allocates) every world's name.
        /// </summary>
        public bool WorldExists
        {
            get
            {
                var held = _world;
                if (held != null)
                {
                    try
                    {
                        if (held.IsCreated) return true;
                    }
                    catch { /* a world that cannot be read is gone */ }

                    _world = null;
                }

                var w = Worlds.Client;
                if (w == null || !w.IsCreated) return false;
                _world = w;
                return true;
            }
        }

        public bool MainMenuReady
        {
            get
            {
                if (WorldExists) return false;
                // FindObjectOfType is not free: the answer is reused for half a second.
                double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                if (now - _menuAt < 0.5) return _menu;
                _menuAt = now;
                try
                {
                    var menu = UnityEngine.Object.FindObjectOfType<MainMenuNewView>();
                    _menu = ViewManager.GetCurrentView() == ViewLocation.MainMenu && menu != null && menu.gameObject.activeInHierarchy;
                }
                catch { _menu = false; }
                return _menu;
            }
        }

        public bool JoinedOk => WorldExists && string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal);

        /// <summary>
        /// Live 2026-10-05: the game's own connect failure — <c>ClientBootstrapSystem.ClientState</c> is
        /// <c>ClientState.Error</c> (what the loading screen's "Error: Connection timed out" draws from), with
        /// <c>DisconnectChangeReason</c> (a plain enum field on the system) as the reason. Plain field reads through the
        /// interop's own getters; nothing is called on the game.
        /// </summary>
        public bool TryConnectFailure(out string reasonName, out bool kicked)
        {
            reasonName = "";
            kicked = false;
            try
            {
                var w = Worlds.Client;
                if (w == null || !w.IsCreated) return false;
                var sys = w.GetExistingSystemManaged<ClientBootstrapSystem>();
                if (sys == null || sys.ClientState != ClientState.Error) return false;
                var why = sys.DisconnectChangeReason;
                kicked = why == ConnectionStatusChangeReason.Kicked;
                reasonName = why.ToString();
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// INSTALLRESTART1: <c>MainMenuNewView._ContinueText</c> (a private string field read through the interop's
        /// getter): <c>UpdateContinueData</c> sets it to the newest hosted save's <c>HostSettings.Name</c>, or to the newest
        /// server's name when that one is newer (research install-restart-resume-2026-10-06.md).
        /// </summary>
        public string? ContinueText
        {
            get
            {
                if (WorldExists) return null;
                try
                {
                    var menu = UnityEngine.Object.FindObjectOfType<MainMenuNewView>();
                    if (menu == null || !menu.gameObject.activeInHierarchy) return null;
                    return menu._ContinueText ?? "";
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// INSTALLRESTART1: the title screen's Continue, the production path (<c>MainMenuNewView.ContinueButton_OnClick</c>,
        /// public, no arguments): with no popup open it hosts <c>_ContinueData.SaveFile</c> through
        /// <c>HostGame</c> (the save's own host settings) when no newer server is the Continue target. Observed live
        /// 2026-10-06: Continue resumes a private hosted game.
        /// </summary>
        public bool PressContinue(out string error)
        {
            error = "";
            try
            {
                var menu = UnityEngine.Object.FindObjectOfType<MainMenuNewView>();
                if (menu == null || !menu.gameObject.activeInHierarchy)
                {
                    error = "the title screen is not on show";
                    return false;
                }

                GcBeforeLaunch.BeforeOwnLaunch("resume");
                menu.ContinueButton_OnClick();
                Plugin.Logger?.LogInfo("JoinFlow: resume: MainMenuNewView.ContinueButton_OnClick (" + (menu._ContinueText ?? "") + ")");
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        public void RequestLeave()
        {
            // The production Leave (the dev bridge's disconnect): ExitGame, which returns to the main menu.
            ViewManager.GoToView(ViewLocation.ExitGame);
            Plugin.Logger?.LogInfo("JoinFlow: reconnect: leaving the server (ExitGame view)");
        }

        public void RequestMainMenu()
        {
            ViewManager.GoToView(ViewLocation.MainMenu);
            Plugin.Logger?.LogInfo("JoinFlow: reconnect: back to the main menu");
        }

        public bool Launch(JoinTarget target, out string error)
        {
            error = "";
            try
            {
                string protocol = JoinTarget.CanonicalProtocol(target.Protocol);
                if (protocol.Length == 0)
                {
                    error = "the transport " + target.Protocol + " cannot be reconnected";
                    return false;
                }

                // The dev bridge's connect, call for call: ConnectAddress.CreateSteamIPv4 / CreateLidgrenIPv4 and
                // GameHelper.LaunchGame(GameConnect) by value, no password (it is never stored).
                var addr = protocol == JoinTarget.ProtocolLidgren
                    ? ConnectAddress.CreateLidgrenIPv4(target.Address.Trim(), (ushort)target.Port)
                    : ConnectAddress.CreateSteamIPv4(target.Address.Trim(), (ushort)target.Port);
                var connect = new GameConnect
                {
                    ConnectAddress = addr,
                    CreateClient = true,
                    ShouldHost = false,
                };
                // No-op when Core's LaunchGame slot is armed (the slot collects inside this call).
                GcBeforeLaunch.BeforeOwnLaunch("rejoin");
                GameHelper.LaunchGame(connect);
                Plugin.Logger?.LogInfo("JoinFlow: reconnect: LaunchGame " + protocol + "://" + target.Endpoint);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>
        /// SERVEREMPTY1: the game's own password prompt for a server joined with a password. <c>GameHelper.LaunchPasswordProtectedGame</c>
        /// takes <c>PasswordGameConnect</c> by value through <c>il2cpp_runtime_invoke</c>, exactly as <c>LaunchGame</c> takes
        /// <c>GameConnect</c>; it opens <c>Join_PasswordView</c>, whose Connect launches the join with the typed password
        /// (research: client-connect.md, hosted-game-launch-op.md). The connect data carries no password.
        /// </summary>
        public bool LaunchPasswordPrompt(JoinTarget target, out string error)
        {
            error = "";
            try
            {
                string protocol = JoinTarget.CanonicalProtocol(target.Protocol);
                if (protocol.Length == 0)
                {
                    error = "the transport " + target.Protocol + " cannot be reconnected";
                    return false;
                }

                var addr = protocol == JoinTarget.ProtocolLidgren
                    ? ConnectAddress.CreateLidgrenIPv4(target.Address.Trim(), (ushort)target.Port)
                    : ConnectAddress.CreateSteamIPv4(target.Address.Trim(), (ushort)target.Port);
                var launch = new PasswordGameConnect
                {
                    ConnectData = new GameConnect
                    {
                        ConnectAddress = addr,
                        CreateClient = true,
                        ShouldHost = false,
                    },
                    FromServerBrowser = false,
                };
                GameHelper.LaunchPasswordProtectedGame(launch);
                Plugin.Logger?.LogInfo("JoinFlow: reconnect: the game's password prompt for " + protocol + "://" + target.Endpoint);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }
    }
}
#endif
