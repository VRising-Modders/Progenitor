using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Progenitor.Commands;
using Progenitor.Ui;

namespace Progenitor.Support;

/// <summary>
/// SUPPORT1: the game-bound half of the support bundle. Reads the game's facts on the main thread (versions, world,
/// plugins, mod problems, the hosted server's folder), and the OS calls (Explorer). Everything it hands on is plain data
/// for <see cref="SupportBundle"/>, which runs off the main thread.
/// </summary>
internal static class SupportLive
{
    /// <summary>The Unity player log folder of the client, under the profile's <c>AppData\LocalLow</c>.</summary>
    internal const string ClientUnityFolder = "Stunlock Studios\\VRising";

    /// <summary>The Unity player log folder of a server started without <c>-logFile</c>.</summary>
    internal const string ServerUnityFolder = "Stunlock Studios\\VRisingServer";

    internal static string? SupportFolder()
    {
        try { return Path.Combine(BepInEx.Paths.BepInExRootPath, SupportBundle.FolderName); }
        catch { return null; }
    }

    private static BepInEx.Configuration.ConfigEntry<string>? _outputFolder;

    /// <summary>
    /// Client: binds <c>[Support] OutputFolder</c> in Core's ui cfg (drawn under Get help on Progenitor's page). Empty, the
    /// default, saves bundles on the Desktop. A Core swap re-binds the same entry.
    /// </summary>
    internal static void Install()
    {
        if (Plugin.IsServerProcess) return;
        var file = UiKeybindHost.CoreConfig;
        if (file == null) return;
        _outputFolder = file.Bind(SupportActions.SettingsSection, SupportActions.OutputFolderKey, "",
            "The folder Get help > Collect logs saves its .zip to. Empty: your Desktop. When the folder cannot be written, the "
            + "bundle goes to BepInEx/progenitor-support (which keeps its five newest bundles; no other folder is cleaned up).");
        _includeServerLogs = file.Bind(SupportActions.SettingsSection, SupportActions.IncludeServerLogsKey, true,
            "When you are an admin of the Progenitor server you are on, Collect logs also fetches that server's own redacted "
            + "bundle (up to 20 MB) and puts it under server/ in the zip.");
        _askToOpenDiscord = file.Bind(SupportActions.SettingsSection, SupportActions.AskToOpenDiscordKey, true,
            "After Collect logs saves the zip, offer to open the V Rising Mod Community Discord (or its #technical-support "
            + "channel) in a dialog. Off: only the status line says where the zip is.");
    }

    private static BepInEx.Configuration.ConfigEntry<bool>? _includeServerLogs;
    private static BepInEx.Configuration.ConfigEntry<bool>? _askToOpenDiscord;

    /// <summary><c>[Support] AskToOpenDiscord</c>: offer the Discord in a dialog after a save (default on).</summary>
    internal static bool AskToOpenDiscord() => !Plugin.IsServerProcess && (_askToOpenDiscord?.Value ?? true);

    /// <summary>
    /// Main thread: why the Logs saved dialog may not open now, or null. Never on the server; never while a world is
    /// loading or joining (a client world whose state is neither the title's Idle nor Running in game: connecting,
    /// pre-warming shaders, loading, reconnecting, the disconnect screen), so a collect that finishes mid-transition shows
    /// only its status line.
    /// </summary>
    internal static string? DialogBlockedReason()
    {
        if (Plugin.IsServerProcess) return "on the server";
        try
        {
            var client = Worlds.Client;
            if (client == null || !client.IsCreated) return null;
            var bootstrap = client.GetExistingSystemManaged<ProjectM.ClientBootstrapSystem>();
            if (bootstrap == null) return null;
            var state = bootstrap.ClientState;
            return SupportActions.DialogBlockedFor(state.ToString());
        }
        catch (Exception e) { return "the game's state could not be read (" + e.Message + ")"; }
    }

    /// <summary>Main thread: the setting is on and this player is an admin of the joined Progenitor server.</summary>
    internal static bool ServerLogsWanted()
    {
        if (Plugin.IsServerProcess) return false;
        if (!(_includeServerLogs?.Value ?? true)) return false;
        try { if (Worlds.Client == null) return false; }
        catch { return false; }
        return SupportActions.ServerLogsRowShown();
    }

    /// <summary>The user's Desktop folder, or null.</summary>
    internal static string? Desktop()
    {
        try
        {
            string d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return string.IsNullOrWhiteSpace(d) ? null : d;
        }
        catch { return null; }
    }

    /// <summary>Where the client's next bundle goes (<see cref="SupportBundle.ChooseOutput"/>), for the Last bundle line and Open folder.</summary>
    internal static string? OutputFolderNow()
    {
        try
        {
            return SupportBundle.ChooseOutput(_outputFolder?.Value, Desktop(), SupportFolder() ?? "", out _, out _, out _);
        }
        catch { return SupportFolder(); }
    }

    /// <summary>Main thread: the client's request (the Collect logs button).</summary>
    internal static SupportBundleRequest? GatherClient()
    {
        var request = Gather(server: false);
        string folder = SupportBundle.ChooseOutput(_outputFolder?.Value, Desktop(), SupportBundle.SupportFolderOf(request),
            out string label, out bool create, out string problem);
        request.OutputFolder = folder;
        request.OutputLabel = label;
        request.CreateOutputFolder = create;
        request.OutputProblem = problem.Length > 0 ? problem : null;

        // Dedicated servers on this PC: whether this client is in a world on one is read here (main thread); the
        // processes are enumerated on the worker thread by Prepare.
        bool inWorld = false;
        int joinPort = 0;
        string joinAddress = "";
        try { inWorld = Worlds.Client != null; } catch { /* unknown reads as not in a world */ }
        try { joinPort = BootstrapLink.LastConnectPort; } catch { /* unknown */ }
        try { joinAddress = BootstrapLink.LastConnectAddress; } catch { /* unknown */ }
        request.Prepare = r => FindLocalServers(r, inWorld, joinAddress, joinPort);
        return request;
    }

    /// <summary>
    /// Worker thread: every running <c>VRisingServer.exe</c> other than this process, by full image path (an unreadable
    /// one is counted in a note), its <c>-logFile</c> and game port from its command line when that can be read, and
    /// whether this client is connected to it. Reads only; nothing in those folders is written.
    /// </summary>
    private static void FindLocalServers(SupportBundleRequest r, bool inWorld, string joinAddress, int joinPort)
    {
        int self = Environment.ProcessId;
        int unreadable = 0;
        IReadOnlyList<string>? own = null;
        Process[] processes;
        try { processes = Process.GetProcessesByName("VRisingServer"); }
        catch (Exception e)
        {
            r.Notes.Add("The running servers could not be listed (" + e.GetType().Name + ").");
            return;
        }

        foreach (var proc in processes)
        {
            try
            {
                int pid;
                try { pid = proc.Id; }
                catch { continue; }
                if (pid == self || r.LocalServers.Count >= SupportBundle.MaxLocalServers * 2) continue;

                string exe = Progenitor.Install.ServerInstall.ExecutablePath(proc);
                if (string.IsNullOrWhiteSpace(exe)) { unreadable++; continue; }
                string? dir = Path.GetDirectoryName(exe);
                if (string.IsNullOrWhiteSpace(dir)) { unreadable++; continue; }

                var source = new LocalServerSource { InstallFolder = dir! };
                IReadOnlyList<string> args = Array.Empty<string>();
                string workingDirectory = dir!;
                if (Progenitor.Install.ServerInstall.TryReadProcessCommandLine(pid, out string commandLine, out string wd))
                {
                    args = Networking.ServerHostSettingsFiles.SplitCommandLine(commandLine);
                    if (!string.IsNullOrWhiteSpace(wd)) workingDirectory = wd;
                    var list = new List<string>(args);
                    source.LogFile = SupportBundle.UnityLogFileArgument(list.ToArray(), workingDirectory);
                }

                int port = ServerPort(dir!, args, workingDirectory);
                if (inWorld && own == null) own = OwnAddresses();
                source.Connected = SupportBundle.IsConnectedTo(inWorld, joinAddress, joinPort, port, own);
                r.LocalServers.Add(source);
            }
            catch { unreadable++; }
            finally
            {
                try { proc.Dispose(); } catch { /* */ }
            }
        }

        if (unreadable > 0)
            r.Notes.Add(unreadable + " running VRisingServer process" + (unreadable == 1 ? "" : "es") + " could not be read (access denied or exiting).");
    }

    /// <summary>The server's game port: its command line, then its ServerHostSettings layers (read only), else the game's 9876.</summary>
    private static int ServerPort(string installFolder, IReadOnlyList<string> args, string workingDirectory)
    {
        try
        {
            var inputs = new Networking.ServerHostSettingsInputs { Args = args, Env = _ => null, InstallRoot = installFolder };
            if (Networking.ServerHostSettingsFiles.TryArg(args, "persistentDataPath", out string data) && !string.IsNullOrWhiteSpace(data))
            {
                string d = data.Trim().Trim('"');
                inputs.PersistentDataPath = Path.IsPathRooted(d) ? d : Path.GetFullPath(Path.Combine(workingDirectory, d));
            }
            int? port = Networking.ServerHostSettingsFiles.Resolve(inputs).EffectiveInt("Port");
            return port is > 0 ? port.Value : DefaultGamePort;
        }
        catch { return DefaultGamePort; }
    }

    /// <summary>The game's default server port (ServerHostSettings.json <c>Port</c>).</summary>
    internal const int DefaultGamePort = 9876;

    private static IReadOnlyList<string> OwnAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var a in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                list.Add(a.ToString());
        }
        catch { /* only loopback counts */ }
        return list;
    }

    /// <summary>Main thread: this process's request. A server process also takes its <c>-logFile</c>.</summary>
    internal static SupportBundleRequest Gather(bool server)
    {
        var facts = Facts(server);
        var request = new SupportBundleRequest
        {
            Side = server ? "server" : "client",
            BepInExRoot = BepInEx.Paths.BepInExRootPath,
            NowUtc = DateTime.UtcNow,
            NowLocal = DateTime.Now,
            UserProfile = SafeRead(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            UserName = SafeRead(() => Environment.UserName),
            MachineName = SafeRead(() => Environment.MachineName),
        };

        string? localLow = LocalLow();
        if (localLow != null) request.UnityLogFolder = Path.Combine(localLow, server ? ServerUnityFolder : ClientUnityFolder);

        if (server)
        {
            string? logFile = SupportBundle.UnityLogFileArgument(Environment.GetCommandLineArgs(), SafeRead(() => Environment.CurrentDirectory));
            if (logFile != null) request.ExtraLogFiles.Add(logFile);
        }
        else
        {
            request.HostedBepInExRoot = HostedBepInEx(out string hostedNote);
            if (hostedNote.Length > 0) facts.Notes.Add(hostedNote);
        }

        request.GameSummary = facts.Format();
        request.KnownNames.AddRange(UserNames(server ? Worlds.Server : Worlds.Client));
        AddDiagnosisInputs(request);
        return request;
    }

    /// <summary>
    /// Main thread: what the diagnosis matches log lines against (each loaded BepInEx plugin's GUID, name, assembly names
    /// and its types' namespaces; Core's namespaces count for Progenitor's own entry) and Progenitor's current mod problems.
    /// Never throws.
    /// </summary>
    private static void AddDiagnosisInputs(SupportBundleRequest request)
    {
        try
        {
            var plugins = BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance?.Plugins;
            if (plugins != null)
                foreach (var pair in plugins)
                {
                    var info = pair.Value;
                    var p = new DiagnosisPlugin(pair.Key ?? info?.Metadata?.GUID ?? "", info?.Metadata?.Name ?? "");
                    System.Reflection.Assembly? asm = null;
                    try { asm = info?.Instance?.GetType().Assembly; } catch { asm = null; }
                    if (asm != null) AddAssembly(p, asm);
                    try
                    {
                        string? location = info?.Location;
                        if (!string.IsNullOrWhiteSpace(location)) p.Assemblies.Add(Path.GetFileNameWithoutExtension(location));
                    }
                    catch { /* no location */ }
                    // Progenitor's own entry (Bootstrap's plugin) also stands for Core, which is not a chainloader plugin.
                    if (string.Equals(p.Guid, FrameworkVersions.CorePluginId, StringComparison.OrdinalIgnoreCase))
                    {
                        AddAssembly(p, typeof(SupportLive).Assembly);
                        // Its BepInEx name is "Progenitor Bootstrap"; players know the framework as Progenitor.
                        p.Display = FrameworkVersions.CoreName;
                    }
                    request.DiagnosisPlugins.Add(p);
                }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Support: the plugin list for the diagnosis could not be read (" + e.GetType().Name + ").");
        }

        // Plugins loaded from BepInEx/progenitor-reload (each in its own collectible load context, so the chainloader does
        // not list them): every [BepInPlugin] type in a collectible assembly other than Core's own. Without them, an
        // exception in such a mod falls through to the framework frame that hosts it.
        try
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in request.DiagnosisPlugins) known.Add(p.Guid);
            var core = typeof(SupportLive).Assembly;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                bool collectible;
                try { collectible = asm.IsCollectible; }
                catch { continue; }
                if (!collectible || ReferenceEquals(asm, core) || asm.IsDynamic) continue;
                Type?[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }
                foreach (var type in types)
                {
                    if (type == null) continue;
                    BepInEx.BepInPlugin? meta = null;
                    try
                    {
                        foreach (var attribute in type.GetCustomAttributes(typeof(BepInEx.BepInPlugin), inherit: false))
                            meta = attribute as BepInEx.BepInPlugin;
                    }
                    catch { meta = null; }
                    if (meta == null || string.IsNullOrWhiteSpace(meta.GUID) || !known.Add(meta.GUID)) continue;
                    var p = new DiagnosisPlugin(meta.GUID, meta.Name ?? "");
                    AddAssembly(p, asm);
                    request.DiagnosisPlugins.Add(p);
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Support: the reload-folder plugins for the diagnosis could not be read (" + e.GetType().Name + ").");
        }

        try
        {
            foreach (var problem in ModProblems.All())
                request.ModProblems.Add(new KeyValuePair<string, string>(problem.ModId, problem.Sentence));
        }
        catch { /* no problems listed */ }
    }

    private static void AddAssembly(DiagnosisPlugin p, System.Reflection.Assembly asm)
    {
        try
        {
            string? name = asm.GetName().Name;
            if (!string.IsNullOrWhiteSpace(name) && !p.Assemblies.Contains(name!)) p.Assemblies.Add(name!);
        }
        catch { /* unnamed */ }

        Type?[] types;
        try { types = asm.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
        catch { types = Array.Empty<Type>(); }
        foreach (var type in types)
        {
            string? ns = null;
            try { ns = type?.Namespace; } catch { /* unreadable */ }
            if (string.IsNullOrWhiteSpace(ns) || p.Namespaces.Contains(ns!)) continue;
            p.Namespaces.Add(ns!);
            if (p.Namespaces.Count >= 200) break;
        }
    }

    /// <summary>
    /// Main thread: the character names of every <c>User</c> in <paramref name="world"/>, disabled (offline) users
    /// included (CLAUDE.md: queries omit disabled entities unless asked). On a client that is this player and the
    /// players the game replicated; on a server, everyone who ever joined this world. Never throws.
    /// </summary>
    internal static List<string> UserNames(Unity.Entities.World? world)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (world == null || !world.IsCreated) return names;
            var em = world.EntityManager;
            foreach (var e in Ecs.Query.Entities(em, includeDisabled: true, includeSystems: false,
                         Unity.Entities.ComponentType.ReadOnly<ProjectM.Network.User>()))
            {
                try
                {
                    string name = em.GetComponentData<ProjectM.Network.User>(e).CharacterName.ToString() ?? "";
                    if (name.Trim().Length >= 3 && seen.Add(name.Trim())) names.Add(name.Trim());
                }
                catch { /* one unreadable user */ }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning("Support: player names could not be read (" + ex.GetType().Name + "); only the names the logs write are removed.");
        }
        return names;
    }

    private static string? LocalLow()
    {
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) return null;
            string? appData = Path.GetDirectoryName(local.TrimEnd('\\', '/'));
            return appData == null ? null : Path.Combine(appData, "LocalLow");
        }
        catch { return null; }
    }

    /// <summary>
    /// The private game's BepInEx folder: the one being hosted now, else the game's bundled server when its log was
    /// written in the last day (the private game that just crashed).
    /// </summary>
    private static string? HostedBepInEx(out string note)
    {
        note = "";
        try
        {
            string? hosted = Progenitor.Install.HostedServerFolder.Current();
            if (hosted != null)
            {
                note = "This client is hosting a private game; its logs are under hosted-server/.";
                return Path.Combine(hosted, "BepInEx");
            }

            string bundled = Path.Combine(BepInEx.Paths.GameRootPath, "VRising_Server", "BepInEx");
            string log = Path.Combine(bundled, "LogOutput.log");
            if (File.Exists(log) && DateTime.UtcNow - File.GetLastWriteTimeUtc(log) <= SupportBundle.RecentWindow)
            {
                note = "A private game ran on this PC in the last day; its logs are under hosted-server/.";
                return bundled;
            }
        }
        catch { /* no hosted logs */ }
        return null;
    }

    private static SupportFacts Facts(bool server)
    {
        var facts = new SupportFacts
        {
            Side = server ? "server" : "client",
            CoreVersion = SafeRead(() => FrameworkVersions.Core) ?? "",
            BootstrapVersion = SafeRead(() => FrameworkVersions.Bootstrap),
            DevVersion = SafeRead(() => FrameworkVersions.Dev),
            GameBuild = SafeRead(() => Progenitor.Install.Admission.CompatibilityIdentities.GameBuild()) ?? "",
            BepInExVersion = SafeRead(() => FrameworkVersions.Of(typeof(BepInEx.Paths).Assembly)) ?? "",
        };

        try
        {
            if (Worlds.Server != null) facts.World = server ? "server world running" : "hosting the server world in this process";
            else if (Worlds.Client != null) facts.World = "in a world (connected to a server)";
            else facts.World = server ? "no server world yet" : "not in a world (title screen or loading)";
        }
        catch (Exception e) { facts.World = "unknown (" + e.GetType().Name + ")"; }

        if (!server)
        {
            try { facts.HostingPrivateGame = BootstrapLink.IsHostingPrivateGame; }
            catch { /* unknown reads as no */ }
        }

        try
        {
            var plugins = BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance?.Plugins;
            if (plugins != null)
                foreach (var pair in plugins)
                {
                    var meta = pair.Value?.Metadata;
                    facts.Plugins.Add(new SupportPlugin(pair.Key ?? meta?.GUID ?? "", meta?.Name ?? "", meta?.Version?.ToString() ?? ""));
                }
        }
        catch (Exception e) { facts.Notes.Add("The BepInEx plugin list could not be read: " + e.GetType().Name + "."); }

        try { facts.Reloadable.AddRange(Reload.PluginReloader.LoadedPluginNames()); }
        catch (Exception e) { facts.Notes.Add("The reloadable plugin list could not be read: " + e.GetType().Name + "."); }

        try
        {
            foreach (var mod in Networking.ModManifest.EnumerateLoaded())
                facts.NetworkMods.Add(new SupportPlugin(mod.Id, "", mod.Version));
        }
        catch (Exception e) { facts.Notes.Add("The handshake mod list could not be read: " + e.GetType().Name + "."); }

        try
        {
            foreach (var p in ModProblems.All())
                facts.Problems.Add(ModProblems.NameOf(p.Severity) + " · " + (p.ModId.Length > 0 ? p.ModId : "?") + " · " + p.Sentence
                    + (string.IsNullOrWhiteSpace(p.Details) ? "" : " [" + p.Details.Replace('\r', ' ').Replace('\n', ' ') + "]"));
        }
        catch (Exception e) { facts.Notes.Add("The mod problems could not be read: " + e.GetType().Name + "."); }

        return facts;
    }

    private static string? SafeRead(Func<string?> read)
    {
        try { return read(); }
        catch { return null; }
    }

    /// <summary>Explorer with the file selected. Windows only; false when it could not start.</summary>
    internal static bool RevealInExplorer(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Support: could not open Explorer: " + e.Message);
            return false;
        }
    }

    /// <summary>Open a web link in the player's browser (Unity's <c>Application.OpenURL</c>). False when it could not.</summary>
    internal static bool OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            UnityEngine.Application.OpenURL(url);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Support: could not open the link: " + e.Message);
            return false;
        }
    }

    /// <summary>Open a folder in the shell. False when it could not.</summary>
    internal static bool OpenFolderInExplorer(string folder)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Support: could not open the folder: " + e.Message);
            return false;
        }
    }

    // ---- the server's command ---------------------------------------------------------------------------------------

    private static readonly object ServerGate = new();
    private static bool _serverRunning;

    /// <summary>
    /// <c>.progenitor logs</c>: build this server's bundle off the main thread and reply with its name and its folder
    /// relative to the server's folder (never an absolute path). The caller has already been authorized.
    /// </summary>
    internal static void CollectForCommand(ICommandContext ctx)
    {
        lock (ServerGate)
        {
            if (_serverRunning)
            {
                ctx.Reply("Already collecting logs.");
                return;
            }
            _serverRunning = true;
        }

        SupportBundleRequest request;
        try { request = Gather(server: Plugin.IsServerProcess); }
        catch (Exception e)
        {
            lock (ServerGate) _serverRunning = false;
            Plugin.Logger?.LogWarning("Support: could not read the server's state: " + SupportRedactor.Scrub(e.ToString()));
            ctx.Reply("Could not collect logs: " + SupportRedactor.Scrub(e.Message));
            return;
        }

        ctx.Reply("Collecting this server's logs...");
        var world = ctx.World;
        Task.Run(() =>
        {
            SupportBundleResult result;
            try { result = SupportBundle.Build(request); }
            catch (Exception e) { result = new SupportBundleResult { Ok = false, Error = SupportRedactor.Scrub(e.Message) }; }
            Dev.MainThread.Post(() =>
            {
                lock (ServerGate) _serverRunning = false;
                string line = result.Ok
                    ? "Saved " + result.FileName + " (" + SupportBundle.FormatBytes(result.Bytes) + ") in " + RelativeFolder(result.Path)
                        + " on the server. Its personal details are removed; review it before posting."
                        + (result.LikelyInvolved.Length > 0 ? " " + result.LikelyInvolved : "")
                    : "Could not collect logs: " + SupportRedactor.Scrub(result.Error);
                Plugin.Logger?.LogInfo("Support: " + (result.Ok ? "saved " + result.FileName + " (" + result.Bytes + " bytes, "
                    + result.Entries.Count + " files, " + result.Seconds.ToString("0.00") + " s)" : "failed: " + SupportRedactor.Scrub(result.Error)));
                try
                {
                    if (world != null && world.IsCreated) ctx.Reply(line);
                }
                catch { /* the caller left */ }
            });
        });
    }

    /// <summary>The bundle's folder relative to the game folder: <c>BepInEx/progenitor-support</c>.</summary>
    private static string RelativeFolder(string zipPath)
    {
        try
        {
            string folder = Path.GetDirectoryName(zipPath) ?? "";
            string game = Path.GetFullPath(BepInEx.Paths.GameRootPath).TrimEnd('\\', '/');
            if (folder.StartsWith(game + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return folder.Substring(game.Length + 1).Replace('\\', '/');
        }
        catch { /* fall through */ }
        return "BepInEx/" + SupportBundle.FolderName;
    }
}
